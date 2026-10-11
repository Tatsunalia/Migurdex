using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Text.RegularExpressions;

namespace Migurdex.Core.Extractors;

public partial class UpboltExtractor : IExtractor
{
    private const string ChromeUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/147.0.0.0 Safari/537.36";

    private readonly HttpClient               _httpClient;
    private readonly ILogger<UpboltExtractor> _logger;
    private readonly M3U8PlaylistExtractor    _m3U8Extractor;

    public UpboltExtractor(M3U8PlaylistExtractor m3U8Extractor, ISharedBridge bridge)
    {
        _httpClient = bridge.CreateHttpClient(o =>
        {
            o.AllowAutoRedirect  = true;
            o.UseCookies         = true;
            o.Emulation          = BrowserEmulation.Chrome147;
            o.SkipDefaultHeaders = true;
        });

        _logger        = bridge.CreateLogger<UpboltExtractor>();
        _m3U8Extractor = m3U8Extractor;
    }

    public string Name => "Upbolt";

    public bool CanExtract(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return false;
        }

        return url.Contains("upbolt.", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<List<VideoSource>> ExtractAsync(string url,
        IDictionary<string, string>?                         headers           = null,
        CancellationToken                                    cancellationToken = default)
    {
        var sources = new List<VideoSource>();

        try
        {
            var fileCode = ExtractFileCode(url);
            if (string.IsNullOrWhiteSpace(fileCode))
            {
                _logger.LogWarning("could not extract file_code from: {Url}", url);
                return sources;
            }

            var embedUrl = $"https://upbolt.to/e/{fileCode}";
            var embUrl   = $"https://upbolt.to/emb-{fileCode}";

            using var getReq = new HttpRequestMessage(HttpMethod.Get, embedUrl);
            getReq.Headers.TryAddWithoutValidation("User-Agent", ChromeUa);
            getReq.Headers.TryAddWithoutValidation("Accept", "*/*");
            using (var getResp = await _httpClient.SendAsync(getReq, cancellationToken))
            {
                if (!getResp.IsSuccessStatusCode)
                {
                    _logger.LogDebug("embed GET {Status} ({Url}), POST deneniyor", (int) getResp.StatusCode, embedUrl);
                }
            }

            using var postReq = new HttpRequestMessage(HttpMethod.Post, "https://upbolt.to/dl")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["op"]        = "embed",
                    ["file_code"] = fileCode,
                    ["auto"]      = "1",
                    ["referer"]   = ""
                })
            };
            postReq.Headers.TryAddWithoutValidation("User-Agent", ChromeUa);
            postReq.Headers.TryAddWithoutValidation("Accept", "*/*");
            postReq.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
            postReq.Headers.TryAddWithoutValidation("Referer", embedUrl);

            using var postResp = await _httpClient.SendAsync(postReq, cancellationToken);
            if (!postResp.IsSuccessStatusCode)
            {
                _logger.LogWarning("dl POST failed: {StatusCode} ({Code})", (int) postResp.StatusCode, fileCode);
                return sources;
            }

            var html = await postResp.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(html))
            {
                _logger.LogWarning("dl POST empty response ({Code})", fileCode);
                return sources;
            }

            var fileMatch = UpboltFileRegex().Match(html);
            if (!fileMatch.Success)
            {
                _logger.LogWarning("could not find urlset master in dl response ({Code})", fileCode);
                return sources;
            }

            var masterUrl = fileMatch.Groups[1].Value;
            _logger.LogInformation("resolved Upbolt master: {Url}", masterUrl);

            var playbackHeaders = new Dictionary<string, string>
            {
                { "Referer", embUrl },
                { "User-Agent", ChromeUa }
            };

            var extracted = await _m3U8Extractor.ExtractAsync(masterUrl,
                                                              new Dictionary<string, string>(playbackHeaders),
                                                              cancellationToken,
                                                              _httpClient);
            foreach (var src in extracted)
            {
                src.Hoster  ??= "Upbolt";
                src.Headers =   new Dictionary<string, string>(playbackHeaders);
                sources.Add(src);
            }

            if (sources.Count == 0)
            {
                sources.Add(new VideoSource
                {
                    Url     = masterUrl,
                    Quality = "Auto",
                    Type    = VideoType.M3U8,
                    Hoster  = "Upbolt",
                    Headers = playbackHeaders
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to extract Upbolt video sources for: {Url}", url);
        }

        return sources;
    }

    private static string? ExtractFileCode(string url)
    {
        var m = FileCodeRegex().Match(url);
        return m.Success ? m.Groups[1].Value : null;
    }

    [GeneratedRegex(@"upbolt\.to/(?:e/|emb-|embed-)?([A-Za-z0-9]+)", RegexOptions.IgnoreCase)]
    private static partial Regex FileCodeRegex();

    [GeneratedRegex(@"file\s*:\s*[""'](https?://[^""']+urlset/master\.m3u8[^""']*)[""']")]
    private static partial Regex UpboltFileRegex();
}
