using Migurdex.Shared;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace Migurdex.Cli.Services.Downloads;

public sealed class DownloadHttpClientFactory : IDownloadHttpClientFactory
{
    private static readonly SocketsHttpHandler _sharedHandler = new()
    {
        AllowAutoRedirect = false,
        UseCookies         = false,
        UseProxy           = true
    };

    public HttpClient CreateClient()
    {
        return new HttpClient(_sharedHandler, false)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }
}

public sealed class HttpMessageHandlerDownloadClientFactory : IDownloadHttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    public HttpMessageHandlerDownloadClientFactory(HttpMessageHandler handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    public HttpClient CreateClient()
    {
        return new HttpClient(_handler, false)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }
}

internal static class DownloadHttp
{
    public const int MaxRedirects = 5;

    private static readonly HashSet<string> _crossOriginSafeHeaders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Accept",
            "Accept-Language",
            "Origin",
            "Range",
            "Referer",
            "User-Agent"
        };

    private static readonly HashSet<string> _externalProcessSafeHeaders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Accept",
            "Accept-Language",
            "Origin",
            "Referer",
            "User-Agent"
        };

    private static readonly HashSet<string> _subtitleFallbackSafeHeaders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Accept",
            "Accept-Language",
            "Origin",
            "Referer",
            "User-Agent"
        };

    public static async Task<HttpResponseMessage> SendWithRedirectsAsync(
        HttpClient                    client,
        Uri                           initialUri,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken             cancellationToken,
        DownloadStallOptions?         stallOptions = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        await ValidateHttpUriAsync(initialUri, cancellationToken).ConfigureAwait(false);

        var currentUri     = initialUri;
        var requestHeaders = CopyHeaders(headers);
        var options        = stallOptions ?? DownloadStallOptions.Default;

        for (var redirectCount = 0;;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var request = CreateRequest(currentUri, requestHeaders);
            var response = await SendForHeadersAsync(client,
                                                     request,
                                                     options.ResponseHeaderTimeout,
                                                     cancellationToken)
                                   .ConfigureAwait(false);

            if (!IsRedirect(response.StatusCode))
            {
                return response;
            }

            var location = response.Headers.Location;
            response.Dispose();

            if (redirectCount >= MaxRedirects)
            {
                throw new DownloadException("Yönlendirme sınırı aşıldı.");
            }

            if (location is null)
            {
                throw new DownloadException("Yönlendirme adresi eksik.");
            }

            var nextUri = location.IsAbsoluteUri
                              ? location
                              : new Uri(currentUri, location);
            await ValidateHttpUriAsync(nextUri, cancellationToken).ConfigureAwait(false);

            if (!IsSameOrigin(currentUri, nextUri))
            {
                // Redirect zinciri boyunca yalnız açıkça güvenli başlıklar korunur.
                // Böylece özel API-key/token başlıkları varsayılan olarak yeni origin'e taşınmaz.
                requestHeaders = FilterCrossOriginHeaders(requestHeaders);
            }

            currentUri    = nextUri;
            redirectCount++;
        }
    }

    public static Dictionary<string, string> CopyHeaders(
        IEnumerable<KeyValuePair<string, string>>? headers)
    {
        var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (headers is null)
        {
            return copy;
        }

        foreach (var header in headers)
        {
            var key = header.Key?.Trim();
            if (string.IsNullOrWhiteSpace(key)
                || header.Value is null
                || key.Contains('\r')
                || key.Contains('\n')
                || header.Value.Contains('\r')
                || header.Value.Contains('\n'))
            {
                continue;
            }

            copy[key] = header.Value;
        }

        return copy;
    }

    public static Dictionary<string, string> CopyExternalProcessHeaders(
        IEnumerable<KeyValuePair<string, string>>? headers)
    {
        return FilterAllowedHeaders(headers, _externalProcessSafeHeaders);
    }

    public static Dictionary<string, string> CopySubtitleFallbackHeaders(
        IEnumerable<KeyValuePair<string, string>>? headers,
        Uri?                              sourceUri,
        Uri                               subtitleUri)
    {
        var filtered = FilterAllowedHeaders(headers, _subtitleFallbackSafeHeaders);
        var result   = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value) in filtered)
        {
            if (name.Equals("Referer", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Origin", StringComparison.OrdinalIgnoreCase))
            {
                if (sourceUri is not null)
                {
                    if (!IsSameOrigin(sourceUri, subtitleUri))
                    {
                        continue;
                    }
                }
                else if (!Uri.TryCreate(value, UriKind.Absolute, out var headerUri)
                         || !IsSameOrigin(headerUri, subtitleUri))
                {
                    continue;
                }
            }

            result[name] = value;
        }

        return result;
    }

    public static string CreateSourceFingerprint(
        string?                                      sourceUrl,
        IEnumerable<KeyValuePair<string, string>>? headers)
    {
        var normalizedHeaders = CopyHeaders(headers);
        normalizedHeaders.Remove("Range");

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendHashField(hash, sourceUrl?.Trim() ?? string.Empty);
        foreach (var (name, value) in normalizedHeaders.OrderBy(item => item.Key,
                                                                    StringComparer.OrdinalIgnoreCase))
        {
            AppendHashField(hash, name.Trim());
            AppendHashField(hash, value);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()[..24];
    }

    public static string CreateOpaqueDigest(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
                          .ToLowerInvariant();
    }

    public static bool IsHtml(HttpResponseMessage response)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        return string.Equals(mediaType, "text/html", StringComparison.OrdinalIgnoreCase)
               || string.Equals(mediaType, "application/xhtml+xml", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsNonMediaContent(HttpResponseMessage response)
    {
        // Medya gövdesi asla metin/JSON/XML/görsel olmamalıdır. Sunucu hata sayfasını
        // 200 ile dönerse (CDN engelleri, hata proxy'leri) dosya bozuk video olarak kaydedilirdi.
        // Content-Type yoksa gerçek medya sunucularına güvenilir; bilinen medya türleri serbest bırakılır.
        var mediaType = response.Content.Headers.ContentType?.MediaType
                            ?.Split(';', StringSplitOptions.TrimEntries)[0]
                            .Trim()
                            .ToLowerInvariant();
        if (string.IsNullOrEmpty(mediaType)
            || mediaType is "application/octet-stream" or "application/binary")
        {
            return false;
        }

        if (mediaType is "application/xhtml+xml" or "application/x-www-form-urlencoded"
            || mediaType.EndsWith("+json", StringComparison.Ordinal)
            || mediaType.EndsWith("+xml", StringComparison.Ordinal))
        {
            return true;
        }

        return mediaType.StartsWith("text/", StringComparison.Ordinal)
               || mediaType.StartsWith("image/", StringComparison.Ordinal)
               || mediaType.StartsWith("font/", StringComparison.Ordinal);
    }

    public static bool IsSameOrigin(Uri left, Uri right)
    {
        return left.Scheme.Equals(right.Scheme, StringComparison.OrdinalIgnoreCase)
               && left.Host.Equals(right.Host, StringComparison.OrdinalIgnoreCase)
               && left.Port == right.Port;
    }

    public static void ValidateHttpUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri
            || (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new DownloadException("Yalnız HTTP ve HTTPS kaynakları desteklenir.");
        }

        if (IPAddress.TryParse(uri.Host, out var literal) && NetworkGuard.IsBlockedAddress(literal)
            || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            throw new DownloadException("Bu host'a istek gönderilemez.");
        }
    }

    public static async Task ValidateHttpUriAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ValidateHttpUri(uri);
        if (await NetworkGuard.ResolvesToBlockedAddressAsync(uri.Host, cancellationToken).ConfigureAwait(false))
        {
            throw new DownloadException("Bu host'a istek gönderilemez.");
        }
    }

    private static async Task<HttpResponseMessage> SendForHeadersAsync(
        HttpClient client,
        HttpRequestMessage request,
        TimeSpan headerTimeout,
        CancellationToken cancellationToken)
    {
        var sendTask = client.SendAsync(request,
                                        HttpCompletionOption.ResponseHeadersRead,
                                        cancellationToken);
        if (headerTimeout == Timeout.InfiniteTimeSpan || sendTask.IsCompleted)
        {
            return await sendTask.ConfigureAwait(false);
        }

        var watchdog = Task.Delay(headerTimeout, cancellationToken);
        if (await Task.WhenAny(sendTask, watchdog).ConfigureAwait(false) == sendTask)
        {
            return await sendTask.ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        DisposeWhenCompleted(sendTask);
        throw new DownloadException($"Sunucu {Math.Max(1, (int)Math.Round(headerTimeout.TotalSeconds, MidpointRounding.AwayFromZero))} saniye boyunca yanıt vermedi.");
    }

    private static void DisposeWhenCompleted(Task<HttpResponseMessage> sendTask)
    {
        _ = sendTask.ContinueWith(static task =>
                                  {
                                      if (task.Status == TaskStatus.RanToCompletion)
                                      {
                                          task.Result.Dispose();
                                          return;
                                      }

                                      _ = task.Exception;
                                  },
                                  CancellationToken.None,
                                  TaskContinuationOptions.ExecuteSynchronously,
                                  TaskScheduler.Default);
    }

    internal static int ToWholeSeconds(TimeSpan value)
    {
        return Math.Max(1, (int)Math.Round(value.TotalSeconds, MidpointRounding.AwayFromZero));
    }

    private static HttpRequestMessage CreateRequest(
        Uri                                    uri,
        IReadOnlyDictionary<string, string>    headers)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        foreach (var header in headers)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return request;
    }

    private static Dictionary<string, string> FilterCrossOriginHeaders(
        IReadOnlyDictionary<string, string> headers)
    {
        return FilterAllowedHeaders(headers, _crossOriginSafeHeaders);
    }

    private static Dictionary<string, string> FilterAllowedHeaders(
        IEnumerable<KeyValuePair<string, string>>? headers,
        HashSet<string>                            allowedNames)
    {
        var filtered = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (headers is null)
        {
            return filtered;
        }

        foreach (var (name, value) in CopyHeaders(headers))
        {
            if (allowedNames.Contains(name))
            {
                filtered[name] = value;
            }
        }

        return filtered;
    }

    private static void AppendHashField(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static bool IsRedirect(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;
    }
}

/// <summary>
/// İndirme akışının asılı kalmasını (stall) yakalamak için kullanılan eşikler.
///
/// <para><b>Neden <c>HttpClient.Timeout</c> değil?</b> <c>HttpClient.Timeout</c>
/// duvar-saati (wall-clock) sınırıdır: istek başlangıcından itibaren geçen süreyi
/// ölçer. 272 MB'lık bir dosya 1 Mbps bağlantıda ~36 dakika sürer; makul görünen
/// bir 5 dakikalık sınır sağlıklı indirmeleri keserdi — yani mevcut sızıntı yerine
/// yeni bir hata üretirdi. Buradaki eşikler ise <em>son bayttan bu yana geçen
/// süre</em>yi ölçer. Yavaş ama canlı bir indirmede her blok yeniden sayar; yalnız
/// hiç bayt gelmediği süre hesaba katılır.</para>
///
/// <para>Her eşik <c>Timeout.InfiniteTimeSpan</c> ile kapatılabilir; kapatıldığında
/// davranış bu düzeltmeden önceki hâline döner.</para>
/// </summary>
public sealed class DownloadStallOptions
{
    public static DownloadStallOptions Default { get; } = new();

    /// <summary>
    /// DNS + TCP + TLS + ilk bayta kadar süren yanıt başlığı beklemesi. 90 saniye,
    /// en kötü ama hâlâ sağlıklı bir kurulumun (DNS yeniden denemeleri, TCP SYN
    /// yeniden gönderimleri, uzak TLS el sıkışması) 2-3 katıdır. Bu pencere gövde
    /// eşiklerinden uzun tutulur çünkü sunucu isteği henüz kabul etmemiştir.
    /// </summary>
    public TimeSpan ResponseHeaderTimeout { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Başlıklar geldikten sonra ilk gövde baytı için beklenecek en fazla süre.
    /// CDN'in origin'den çekmeye başlaması, soğuk önbellek ve sunucu tarafı
    /// hazırlık bu arada gerçekleşebildiği için boşta bekleme eşiğinden uzun.
    /// </summary>
    public TimeSpan FirstByteTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// İlk bayttan sonra ardışık baytlar arasında beklenecek en fazla süre.
    /// Kurulmuş bir medya bağlantısında 1 Mbps bile 128 KB'lık bir bloğu ~1 saniyede
    /// taşır; 20 saniyelik sıfır ilerleme artık "yavaş bağlantı" değil, ölü sokettir.
    /// </summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(20);
}

/// <summary>
/// "Son N saniyede <em>hiç bayt gelmedi</em>" koşulunu denetleyen bekçi.
///
/// <para>Neden asenkron okuma? Bir ağ akışında <c>ReadAsync</c> ilk bayt gelir
/// gelmez tamamlanır; tamponu doldurmaya çalışmaz. Bekleme çözünürlüğü bu yüzden
/// blok boyutundan bağımsızdır ve mevcut 128 KB'lık tamponlar küçültülmeden
/// kullanılabilir. Senkron <c>Read</c> iptal edilemezdi ve takılan bağlantıda
/// sonsuza dek bloklardı; hiç kullanılmaz.</para>
///
/// <para>Okuma <c>Task.WhenAny(okuma, gözcü)</c> ile yarışır. Gözcü <em>caller</em>
/// token'ıyla çalışır: iptal edildiğinde bekleyiş <c>OperationCanceledException</c>
/// ile sonlanır, sonsuza dek süren bir döngü oluşmaz. Zaman aşımında asılı kalan
/// okuma, çağıranın gövde akışını yok edeceği için önceden iptal edilir.</para>
///
/// <para>Ömrü tek bir gövde okuma döngüsüne karşılık gelir. <see cref="Dispose"/>
/// iç <see cref="CancellationTokenSource"/>'ı yok ettiği için <c>using</c>
/// bildirimi gövde akışından <em>önce</em> yapılmalıdır (bildirimler ters sırada
/// yok edilir).</para>
/// </summary>
internal sealed class DownloadStallGuard : IDisposable
{
    private readonly TimeSpan                _firstByteTimeout;
    private readonly TimeSpan                _idleTimeout;
    private readonly CancellationToken       _callerToken;
    private readonly CancellationTokenSource _readCts;
    private          long                    _lastActivity;

    public DownloadStallGuard(DownloadStallOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!IsUsableThreshold(options.FirstByteTimeout))
        {
            throw new ArgumentOutOfRangeException(nameof(options),
                                                "İlk bayt eşiği sıfırdan büyük ya da InfiniteTimeSpan olmalı.");
        }

        if (!IsUsableThreshold(options.IdleTimeout))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Boşta bekleme eşiği sıfırdan büyük ya da InfiniteTimeSpan olmalı.");
        }

        _firstByteTimeout = options.FirstByteTimeout;
        _idleTimeout     = options.IdleTimeout;
        _callerToken     = cancellationToken;
        _readCts         = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _lastActivity    = Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// Eşik ya pozitif bir süre ya da bekçiyi kapatan
    /// <see cref="Timeout.InfiniteTimeSpan"/> olmalıdır.
    /// </summary>
    private static bool IsUsableThreshold(TimeSpan value)
    {
        return value > TimeSpan.Zero || value == Timeout.InfiniteTimeSpan;
    }

    /// <summary>
    /// Bu akıştan en az bir bayt okundu mu? İlk bayt eşiği yalnızca bu false
    /// iken uygulanır.
    /// </summary>
    public bool ReceivedAnyByte { get; private set; }

    public async Task<int> ReadAsync(Stream stream, Memory<byte> buffer)
    {
        var budget   = ReceivedAnyByte ? _idleTimeout : _firstByteTimeout;
        var readTask = stream.ReadAsync(buffer, _readCts.Token).AsTask();
        if (budget == Timeout.InfiniteTimeSpan)
        {
            return await CompleteReadAsync(readTask).ConfigureAwait(false);
        }

        if (readTask.IsCompleted)
        {
            return await CompleteReadAsync(readTask).ConfigureAwait(false);
        }

        var remaining = budget - Stopwatch.GetElapsedTime(_lastActivity);
        if (remaining <= TimeSpan.Zero)
        {
            return ThrowStalled(readTask, budget);
        }

        var watchdog = Task.Delay(remaining, _callerToken);
        if (await Task.WhenAny(readTask, watchdog).ConfigureAwait(false) == readTask)
        {
            return await CompleteReadAsync(readTask).ConfigureAwait(false);
        }

        // Gözcü bitti: ya kullanıcı iptal etti ya da eşik doldu. İptal önce gelir;
        // Ctrl+C bir hata değil, kullanıcının vazgeçmesidir.
        _callerToken.ThrowIfCancellationRequested();
        return ThrowStalled(readTask, budget);
    }

    private async Task<int> CompleteReadAsync(Task<int> readTask)
    {
        var read = await readTask.ConfigureAwait(false);
        if (read > 0)
        {
            // Her alınan bayt "son hareket"i tazeler: yavaş ama canlı bir indirme
            // hiçbir zaman eşiği doldurmaz.
            ReceivedAnyByte = true;
            _lastActivity   = Stopwatch.GetTimestamp();
        }

        return read;
    }

    private int ThrowStalled(Task<int> readTask, TimeSpan budget)
    {
        try
        {
            // Asılı kalan okuma iptal edilir; aksi hâlde çağıranın gövde akışını
            // yok etmesi yarım kalmış bir okumayla yarışır.
            _readCts.Cancel();
        }
        catch (Exception)
        {
            // İptal geri çağrılarından biri hata verse de asıl hata maskelenmez.
        }

        Observe(readTask);
        throw new DownloadException(
            $"Sunucu {DownloadHttp.ToWholeSeconds(budget)} saniye boyunca veri göndermedi; indirme takıldı.");
    }

    private static void Observe(Task task)
    {
        // Bırakılan okuma "unobserved task exception" olarak sayılmasın.
        _ = task.ContinueWith(static faulted => { _ = faulted.Exception; },
                              CancellationToken.None,
                              TaskContinuationOptions.OnlyOnFaulted
                              | TaskContinuationOptions.ExecuteSynchronously,
                              TaskScheduler.Default);
    }

    public void Dispose()
    {
        _readCts.Dispose();
    }
}
