using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;

namespace Migurdex.Cli.Services;

public sealed class CliBridge : ISharedBridge
{
    private readonly ILoggerFactory _loggerFactory;

    /// <param name="loggerFactory">
    ///     Core'daki <c>ILogger</c> kullanan her sınıf bu köprüden logger alır
    ///     (50+ dosya: extractor'lar, plugin yükleyici, izleme servisleri).
    ///     <c>null</c> verilirse önceki davranış korunur: log yok.
    /// </param>
    /// <remarks>
    /// ⭐ Önceden burası <see cref="NullLoggerFactory.Instance"/> döndürüyordu,
    ///     yani CLI'daki tüm log mesajları **tamamen çöpe atılıyordu**.
    ///     Program.cs artık Serilog'u buraya veriyor; böylece bir indirme
    ///     hatası ekranda kaybolmak yerine <c>logs/</c> klasörüne yazılıyor.
    /// </remarks>
    public CliBridge(ILoggerFactory? loggerFactory = null)
    {
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    public IMp4MetadataReader MetadataReader => throw new NotSupportedException();

    /// <inheritdoc />
    public ILoggerFactory LoggerFactory => _loggerFactory;

    public HttpClient CreateHttpClient(HttpClientOptions? options = null)
    {
        return new HttpClient(new SocketsHttpHandler(), true);
    }

    public HttpClient CreateHttpClient(Action<HttpClientOptions> configure)
    {
        return CreateHttpClient();
    }

    public ILogger<T> CreateLogger<T>()
    {
        return _loggerFactory.CreateLogger<T>();
    }
}