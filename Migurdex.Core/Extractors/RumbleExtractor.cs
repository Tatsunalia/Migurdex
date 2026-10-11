using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Infrastructure;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Text.RegularExpressions;

namespace Migurdex.Core.Extractors;

public partial class RumbleExtractor : IExtractor
{
    private readonly HttpClient               _httpClient;
    private readonly ILogger<RumbleExtractor> _logger;
    private readonly M3U8PlaylistExtractor    _m3U8Extractor;

    public RumbleExtractor(M3U8PlaylistExtractor m3U8Extractor, ISharedBridge bridge)
    {
        _httpClient = bridge.CreateHttpClient(x =>
        {
            x.Emulation = BrowserEmulation.Firefox147;
        });

        _m3U8Extractor = m3U8Extractor;
        _logger        = bridge.CreateLogger<RumbleExtractor>();
    }

    public string Name => "Rumble";

    public bool CanExtract(string url)
    {
        return url.Contains("rumble.com", StringComparison.OrdinalIgnoreCase);
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
                referer = "https://rumble.com/";
            }

            _logger.LogDebug("fetching Rumble embed page: {Url}", url);

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent",
                                "Mozilla/5.0 (X11; Linux x86_64; rv:157.0) Gecko/20100101 Firefox/157.0");
            request.Headers.Add("Accept",
                                "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
            request.Headers.Add("Accept-Language", "en-US,en;q=0.5");
            request.Headers.Add("Referer", referer);

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("embed page failed: {StatusCode} for URL: {Url}",
                                   response.StatusCode,
                                   url);

                return sources;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrEmpty(html))
            {
                _logger.LogWarning("embed page empty response for: {Url}", url);

                return sources;
            }

            var     unescapedHtml = html.Replace(@"\/", "/");
            string? m3U8Url       = null;
            var     hlsMatch      = HlsUrlRegex().Match(unescapedHtml);
            if (hlsMatch.Success)
            {
                m3U8Url = hlsMatch.Groups[1].Value;
            }

            if (!string.IsNullOrEmpty(m3U8Url))
            {
                _logger.LogInformation("extracted master M3U8 playlist: {M3u8Url}", m3U8Url);

                var extractedSources = await _m3U8Extractor.ExtractAsync(m3U8Url,
                                                                         new Dictionary<string, string>
                                                                         {
                                                                             { "Referer", "https://rumble.com/" },
                                                                             {
                                                                                 "User-Agent",
                                                                                 "Mozilla/5.0 (X11; Linux x86_64; rv:157.0) Gecko/20100101 Firefox/157.0"
                                                                             }
                                                                         },
                                                                         cancellationToken,
                                                                         client: _httpClient);
                sources.AddRange(extractedSources);

                if (sources.Count == 0)
                {
                    sources.Add(new VideoSource
                    {
                        Url     = m3U8Url,
                        Quality = "Auto",
                        Type    = VideoType.M3U8
                    });
                }
            }

            var mp4Matches  = Mp4UrlRegex().Matches(html);
            var seenMp4Urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match match in mp4Matches)
            {
                var mp4Url  = match.Groups[1].Value.Replace(@"\/", "/").Replace(@"\", "");
                var height  = match.Groups[2].Value;
                var quality = $"{height}p";

                if (!seenMp4Urls.Add(mp4Url))
                {
                    continue;
                }

                sources.Add(new VideoSource
                {
                    Url     = mp4Url,
                    Quality = quality,
                    Type    = VideoType.Mp4
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "extraction failed for URL: {Url}", url);
        }

        return sources;
    }

    [GeneratedRegex(@"""hls"":\s*\{[^}]*?""url"":\s*""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex HlsUrlRegex();

    [GeneratedRegex(@"""url"":""(https?:\\?/\\?/[^""]+\.mp4)"".*?""h"":(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex Mp4UrlRegex();
}
