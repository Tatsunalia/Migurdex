using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Infrastructure;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Migurdex.Core.Extractors;

public partial class AbyssExtractor : IExtractor
{
    private readonly HttpClient              _httpClient;
    private readonly ILogger<AbyssExtractor> _logger;

    public AbyssExtractor(ISharedBridge bridge)
    {
        _httpClient = bridge.CreateHttpClient(x =>
        {
            x.Emulation = BrowserEmulation.Chrome147;
        });

        _logger = bridge.CreateLogger<AbyssExtractor>();
    }

    public string Name => "Abyss";

    public bool CanExtract(string url)
    {
        return url.Contains("abysscdn.com", StringComparison.OrdinalIgnoreCase)
               || url.Contains("abyss.to", StringComparison.OrdinalIgnoreCase)
               || url.Contains("abyssplayer.com", StringComparison.OrdinalIgnoreCase)
               || url.Contains("short.icu", StringComparison.OrdinalIgnoreCase)
               || url.Contains("iamcdn.net", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<List<VideoSource>> ExtractAsync(string url,
        IDictionary<string, string>?                         headers           = null,
        CancellationToken                                    cancellationToken = default)
    {
        var sources = new List<VideoSource>();

        try
        {
            var referer = headers.GetReferer();
            if (string.IsNullOrEmpty(referer))
            {
                referer = "https://anizm.net/";
            }

            var normalizedUrl = url;

            var vMatch = VParamRegex().Match(url);
            if (vMatch.Success)
            {
                normalizedUrl = $"https://abysscdn.com/?v={vMatch.Groups[1].Value}";
            }
            else
            {
                var pathMatch = PathEmbedRegex().Match(url);
                if (pathMatch.Success)
                {
                    normalizedUrl = $"https://abysscdn.com/?v={pathMatch.Groups[1].Value}";
                }
                else
                {
                    try
                    {
                        var uri         = new Uri(url);
                        var lastSegment = uri.AbsolutePath.Trim('/');
                        if (!string.IsNullOrEmpty(lastSegment)
                            && !lastSegment.Contains('/')
                            && SegmentRegex().IsMatch(lastSegment))
                        {
                            normalizedUrl = $"https://abysscdn.com/?v={lastSegment}";
                        }
                    }
                    catch
                    {
                        // ignored
                    }
                }
            }

            _logger.LogDebug("fetching Abyss embed page: {Url} (Normalized: {NormalizedUrl}) {Referer}",
                             url,
                             normalizedUrl,
                             referer);

            var request = new HttpRequestMessage(HttpMethod.Get, normalizedUrl);

            if (!string.IsNullOrEmpty(referer))
            {
                request.Headers.Add("Referer", referer);
            }

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("embed page failed for: {Url}", normalizedUrl);

                return sources;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrEmpty(html))
            {
                _logger.LogWarning("embed page empty response for: {Url}", normalizedUrl);

                return sources;
            }

            var datasMatch = DatasVarRegex().Match(html);
            if (!datasMatch.Success)
            {
                _logger.LogWarning("could not find 'datas' variable in Abyss HTML");

                return sources;
            }

            var datasB64    = datasMatch.Groups[1].Value;
            var jsonBytes   = Convert.FromBase64String(datasB64);
            var decodedJson = Encoding.Latin1.GetString(jsonBytes);

            using var doc  = JsonDocument.Parse(decodedJson);
            var       root = doc.RootElement;

            if (!root.TryGetProperty("slug", out var slugProp)
                || !root.TryGetProperty("user_id", out var userIdProp)
                || !root.TryGetProperty("md5_id", out var md5IdProp)
                || !root.TryGetProperty("media", out var mediaProp))
            {
                _logger.LogWarning("decoded 'datas' JSON is missing expected properties");

                return sources;
            }

            var slug     = slugProp.GetString() ?? "";
            var mediaStr = mediaProp.GetString() ?? "";

            var userId = userIdProp.ValueKind == JsonValueKind.Number
                             ? userIdProp.GetInt64().ToString()
                             : userIdProp.GetString() ?? "";

            var md5Id = md5IdProp.ValueKind == JsonValueKind.Number
                            ? md5IdProp.GetInt64().ToString()
                            : md5IdProp.GetString() ?? "";

            if (string.IsNullOrEmpty(slug) || string.IsNullOrEmpty(mediaStr))
            {
                _logger.LogWarning("invalid slug or media values in datas JSON");

                return sources;
            }

            var encryptedBytes = Encoding.Latin1.GetBytes(mediaStr);

            var     decryptedStr      = string.Empty;
            var     decryptionSuccess = false;
            byte[]? decryptedBytes    = null;

            var highPriorityKeys = new List<string>(10);
            AddKeyIfValid(highPriorityKeys, userId, slug, md5Id);
            AddKeyIfValid(highPriorityKeys, userId, slug, "undefined");
            AddKeyIfValid(highPriorityKeys, "undefined", slug, md5Id);
            AddKeyIfValid(highPriorityKeys, "undefined", slug, "undefined");
            AddKeyIfValid(highPriorityKeys, userId, slug, "");
            AddKeyIfValid(highPriorityKeys, userId, slug, "null");
            AddKeyIfValid(highPriorityKeys, userId, slug, "0");

            using var aes = Aes.Create();
            aes.Mode    = CipherMode.ECB;
            aes.Padding = PaddingMode.None;

            var aesKeyBytes = new byte[32];

            foreach (var keyString in highPriorityKeys)
            {
                GetMd5HexBytes(keyString, aesKeyBytes);

                if (TryDecryptKey(aes, encryptedBytes, aesKeyBytes, out decryptedBytes))
                {
                    decryptedStr      = Encoding.UTF8.GetString(decryptedBytes!);
                    decryptionSuccess = true;
                    _logger.LogInformation("decryption succeeded using high-priority key string: {KeyString}",
                                           keyString);

                    break;
                }
            }

            if (!decryptionSuccess)
            {
                var fallbackUserIds = new[] { userId, "undefined" };

                for (var i = 1; i <= 500; i++)
                {
                    var idStr = i.ToString();
                    foreach (var possibleUserId in fallbackUserIds)
                    {
                        var keyString = $"{possibleUserId}:{slug}:{idStr}";
                        GetMd5HexBytes(keyString, aesKeyBytes);

                        if (TryDecryptKey(aes, encryptedBytes, aesKeyBytes, out decryptedBytes))
                        {
                            decryptedStr      = Encoding.UTF8.GetString(decryptedBytes!);
                            decryptionSuccess = true;
                            _logger.LogInformation("decryption succeeded using fallback key string: {KeyString}",
                                                   keyString);

                            break;
                        }
                    }

                    if (decryptionSuccess)
                    {
                        break;
                    }
                }
            }

            if (!decryptionSuccess || string.IsNullOrEmpty(decryptedStr))
            {
                _logger.LogWarning("aES-CTR decryption failed for all possible local database IDs");

                return sources;
            }

            _logger.LogDebug("decrypted media JSON: {DecryptedStr}", decryptedStr);

            using var mediaDoc  = JsonDocument.Parse(decryptedStr);
            var       mediaRoot = mediaDoc.RootElement;

            if (mediaRoot.TryGetProperty("mp4", out var mp4Prop) && mp4Prop.ValueKind == JsonValueKind.Object)
            {
                List<string>? domains = null;
                if (mp4Prop.TryGetProperty("domains", out var domainsProp)
                    && domainsProp.ValueKind == JsonValueKind.Array)
                {
                    domains = new List<string>();
                    foreach (var domainProp in domainsProp.EnumerateArray())
                    {
                        var domain = domainProp.ValueKind == JsonValueKind.String
                                         ? domainProp.GetString()
                                         : null;

                        if (!string.IsNullOrEmpty(domain))
                        {
                            domains.Add(domain);
                        }
                    }
                }

                if (mp4Prop.TryGetProperty("sources", out var sourcesProp)
                    && sourcesProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var source in sourcesProp.EnumerateArray())
                    {
                        string? label = null;
                        if (source.TryGetProperty("label", out var lProp))
                        {
                            label = lProp.GetString();
                        }

                        if (source.TryGetProperty("codec", out var codecProp)
                            && string.Equals(codecProp.GetString(),
                                             "av1",
                                             StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string? resId = null;
                        if (source.TryGetProperty("res_id", out var resIdProp))
                        {
                            resId = resIdProp.ValueKind == JsonValueKind.Number
                                        ? resIdProp.GetInt64().ToString()
                                        : resIdProp.GetString();
                        }

                        string? directBaseUrl = null;
                        if (source.TryGetProperty("url", out var uProp))
                        {
                            directBaseUrl = uProp.GetString();
                        }

                        string? directPath = null;
                        if (source.TryGetProperty("path", out var pathProp))
                        {
                            directPath = pathProp.GetString();
                        }

                        string? sub = null;
                        if (source.TryGetProperty("sub", out var subProp))
                        {
                            sub = subProp.GetString();
                        }

                        if (!source.TryGetProperty("size", out var sizeProp)
                            || sizeProp.ValueKind is not (JsonValueKind.Number or JsonValueKind.String))
                        {
                            continue;
                        }

                        var sizeStr = sizeProp.ValueKind == JsonValueKind.Number
                                          ? sizeProp.GetInt64().ToString()
                                          : sizeProp.GetString() ?? "";

                        if (string.IsNullOrEmpty(sizeStr) || sizeStr == "0")
                        {
                            continue;
                        }

                        long? sizeBytes = null;
                        if (long.TryParse(sizeStr, out var parsedSize))
                        {
                            sizeBytes = parsedSize;
                        }

                        string? soraUrl = null;
                        if (!string.IsNullOrEmpty(resId)
                            && !string.IsNullOrEmpty(sub)
                            && domains is { Count: > 0 })
                        {
                            var domain = domains.FirstOrDefault(x => x.Contains(sub,
                                                                    StringComparison.OrdinalIgnoreCase));

                            if (!string.IsNullOrEmpty(domain))
                            {
                                var soraPath  = $"/mp4/{md5Id}/{resId}/{sizeStr}?v={slug}";
                                var soraToken = BuildSoraToken(aes, soraPath, sizeStr);

                                if (!string.IsNullOrEmpty(soraToken))
                                {
                                    soraUrl = $"https://{domain.TrimEnd('/')}/sora/{sizeStr}/{soraToken}";
                                }
                            }
                        }

                        if (!string.IsNullOrEmpty(soraUrl))
                        {
                            sources.Add(new VideoSource
                            {
                                Url       = soraUrl,
                                Quality   = $"{label ?? "Auto"}",
                                Type      = VideoType.Mp4,
                                SizeBytes = sizeBytes,
                                Headers = new Dictionary<string, string>
                                {
                                    { "Referer", "https://player.abyssplayer.com/" }
                                }
                            });

                            continue;
                        }

                        // AES-CTR encrypted bytes, needs decrypt on download
                        /*
                        if (string.IsNullOrEmpty(directBaseUrl) || string.IsNullOrEmpty(directPath))
                        {
                            continue;
                        }

                        var directCombinedUrl = $"{directBaseUrl.TrimEnd('/')}/{directPath.TrimStart('/')}";
                        sources.Add(new VideoSource
                        {
                            Url       = directCombinedUrl,
                            Quality   = $"{label ?? "Auto"}",
                            Type      = VideoType.Mp4,
                            SizeBytes = sizeBytes,
                            Headers = new Dictionary<string, string>
                            {
                                { "Referer", "https://abysscdn.com/" }
                            }
                        });
                        */
                    }

                    sources.Sort((a, b) => (b.SizeBytes ?? 0).CompareTo(a.SizeBytes ?? 0));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to extract Abyss video sources for: {Url}", url);
        }

        return sources;
    }

    [GeneratedRegex(@"[?&]v=([a-zA-Z0-9_-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex VParamRegex();

    [GeneratedRegex(@"/(?:video/embed|video|embed)/([a-zA-Z0-9_-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex PathEmbedRegex();

    [GeneratedRegex(@"^[a-zA-Z0-9_-]+$", RegexOptions.IgnoreCase)]
    private static partial Regex SegmentRegex();

    [GeneratedRegex(@"const\s+datas\s*=\s*""([^""]+)""")]
    private static partial Regex DatasVarRegex();

    private static void AddKeyIfValid(List<string> list, string userId, string slug, string id)
    {
        var key = $"{userId}:{slug}:{id}";
        if (!list.Contains(key))
        {
            list.Add(key);
        }
    }

    private static void GetMd5HexBytes(string input, Span<byte> destination)
    {
        var maxByteCount = Encoding.UTF8.GetByteCount(input);
        var inputBytes   = maxByteCount <= 256 ? stackalloc byte[256] : new byte[maxByteCount];
        var bytesWritten = Encoding.UTF8.GetBytes(input, inputBytes);

        Span<byte> hashBytes = stackalloc byte[16];
        MD5.HashData(inputBytes[..bytesWritten], hashBytes);

        WriteHexLower(hashBytes, destination);
    }

    private static byte GetHexCharByte(int val)
    {
        return (byte) (val < 10 ? '0' + val : 'a' + (val - 10));
    }

    private static string? BuildSoraToken(Aes aes, string path, string size)
    {
        try
        {
            Span<byte> keyBytes = stackalloc byte[32];
            GetSoraKeyBytes(size, keyBytes);

            var keyArray  = keyBytes.ToArray();
            var plainText = Encoding.UTF8.GetBytes(path);
            var cipher    = AesCtrCrypt(aes, keyArray, plainText);

            var first  = Convert.ToBase64String(cipher).Replace("=", string.Empty);
            var second = Convert.ToBase64String(Encoding.ASCII.GetBytes(first)).Replace("=", string.Empty);

            return second;
        }
        catch
        {
            return null;
        }
    }

    private static void GetSoraKeyBytes(string size, Span<byte> destination)
    {
        Span<byte> seed = stackalloc byte[size.Length];
        for (var i = 0; i < size.Length; i++)
        {
            var c = size[i];
            seed[i] = c is >= '0' and <= '9' ? (byte) (c - '0') : (byte) c;
        }

        Span<byte> hashBytes = stackalloc byte[16];
        MD5.HashData(seed, hashBytes);

        WriteHexLower(hashBytes, destination);
    }

    private static void WriteHexLower(ReadOnlySpan<byte> hash, Span<byte> destination)
    {
        for (var i = 0; i < hash.Length; i++)
        {
            var b = hash[i];
            destination[i * 2]       = GetHexCharByte(b >> 4);
            destination[(i * 2) + 1] = GetHexCharByte(b & 0x0F);
        }
    }

    private static byte[] AesCtrCrypt(Aes aes, byte[] key, byte[] input)
    {
        aes.Key = key;

        using var encryptor = aes.CreateEncryptor();

        var counter   = new byte[16];
        var keyStream = new byte[16];
        Buffer.BlockCopy(key, 0, counter, 0, 16);

        var output     = new byte[input.Length];
        var blockCount = (input.Length + 15) / 16;

        for (var i = 0; i < blockCount; i++)
        {
            encryptor.TransformBlock(counter, 0, 16, keyStream, 0);

            var offset         = i * 16;
            var bytesToProcess = Math.Min(16, input.Length - offset);

            for (var j = 0; j < bytesToProcess; j++)
            {
                output[offset + j] = (byte) (input[offset + j] ^ keyStream[j]);
            }

            for (var k = 15; k >= 0; k--)
            {
                if (++counter[k] != 0)
                {
                    break;
                }
            }
        }

        return output;
    }

    private static bool TryDecryptKey(Aes aes, byte[] ciphertext, byte[] key, out byte[]? decryptedBytes)
    {
        decryptedBytes = AesCtrCrypt(aes, key, ciphertext);

        for (var i = 0; i < decryptedBytes.Length; i++)
        {
            var b = decryptedBytes[i];

            if (b is 0x20 or 0x09 or 0x0D or 0x0A)
            {
                continue;
            }

            if (b is not 0x7B and not 0x5B)
            {
                decryptedBytes = null;
            }

            return b is 0x7B or 0x5B;
        }

        decryptedBytes = null;

        return false;
    }
}
