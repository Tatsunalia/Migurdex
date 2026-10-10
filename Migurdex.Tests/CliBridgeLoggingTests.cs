using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Migurdex.Cli.Services;
using Xunit;

namespace Migurdex.Tests;

/// <summary>
///     CLI'nin log köprüsü.
/// </summary>
/// <remarks>
/// ⭐ Regresyon kilidi: <c>CliBridge</c> logger olarak
///     <see cref="NullLoggerFactory"/> döndürüyordu, yani CLI'daki tüm log
///     mesajları çöpe gidiyordu. Bu testler gerçek bir fabrikaya bağlandığında
///     mesajın gerçekten ulaştığını doğrular.
/// </remarks>
public sealed class CliBridgeLoggingTests
{
    /// <summary>Mesajları toplayan sahte fabrika.</summary>
    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        public List<string> Mesajlar { get; } = [];

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName)
            => new CapturingLogger(Mesajlar, categoryName);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(List<string> toplam, string kategori) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state)
                where TState : notnull
                => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel  logLevel,
                                    EventId   eventId,
                                    TState    state,
                                    Exception? exception,
                                    Func<TState, Exception?, string> formatter)
            {
                toplam.Add($"{kategori}: {formatter(state, exception)}");
            }

            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new();

                public void Dispose()
                {
                }
            }
        }
    }

    [Fact]
    public void LoggerFactory_VarsayilanKurulumda_NullLogger()
    {
        // Parametre verilmemişse önceki davranış korunur: testler ve
        // tekil kullanım yerinde çalışmaya devam eder.
        var bridge = new CliBridge();

        Assert.Same(NullLoggerFactory.Instance, bridge.LoggerFactory);
    }

    [Fact]
    public void CreateLogger_VerilenFabrikayiKullanir()
    {
        var fabrika = new CapturingLoggerFactory();
        var bridge  = new CliBridge(fabrika);

        var logger = bridge.CreateLogger<TestSinifi>();

        logger.LogInformation("merhaba {Sayi}", 42);

        Assert.Single(fabrika.Mesajlar);
        Assert.Contains("merhaba 42", fabrika.Mesajlar[0]);
    }

    [Fact]
    public void CreateLogger_KategoriyiTasiyor()
    {
        var fabrika = new CapturingLoggerFactory();
        var bridge  = new CliBridge(fabrika);

        bridge.CreateLogger<TestSinifi>().LogError("hata");

        Assert.Contains("TestSinifi", fabrika.Mesajlar[0]);
    }

    [Fact]
    public void LoggerFactory_Alan_VerilenFabrikaylaAyni()
    {
        var fabrika = new CapturingLoggerFactory();
        var bridge  = new CliBridge(fabrika);

        Assert.Same(fabrika, bridge.LoggerFactory);
    }

    private sealed class TestSinifi;
}