using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;

namespace Migurdex.Core.Utils;

public static class LoggerBuilder
{
    public static void ConfigureLogging(IServiceCollection services, IConfiguration configuration)
    {
        Configure(services, writeToConsole: true);
    }

    /// <summary>
    ///     CLI için log kurar: <b>yalnızca dosyaya</b> yazar.
    /// </summary>
    /// <param name="writeToConsole">
    ///     <c>false</c> ise konsola hiçbir şey yazılmaz. TUI ekranı kullanıyor;
    ///     konsola yazılan her satır indirme çubuğunun üstüne biner ve
    ///     ekranı bozar. Bu yüzden CLI için kapatılmıştır.
    /// </param>
    /// <remarks>
    /// ⭐ Serilog kurulumu API ile <b>aynı</b>: aynı sınıf, aynı paketler, aynı
    ///     günlük dosya biçimi. Burada yeni bir log sistemi kurulmaz; sadece
    ///     konsol sink'i kapatılır. Böylece "Serilog işimize yaramaz" yerine
    ///     depoda zaten olan altyapı kullanılır.
    /// </remarks>
    public static void Configure(IServiceCollection services, bool writeToConsole)
    {
        var baseDir = AppContext.BaseDirectory;
        if (string.IsNullOrEmpty(baseDir) || !Directory.Exists(baseDir))
        {
            var exeDir = Environment.ProcessPath is { Length: > 0 } p ? Path.GetDirectoryName(p) : null;
            baseDir = !string.IsNullOrEmpty(exeDir) && Directory.Exists(exeDir)
                          ? exeDir!
                          : Directory.GetCurrentDirectory();
        }

        var     logsDir = Path.Combine(baseDir, "logs");
        string? logFile = null;
        try
        {
            if (!Directory.Exists(logsDir))
            {
                Directory.CreateDirectory(logsDir);
            }

            logFile = Path.Combine(logsDir, "Migurdex-.log");
        }
        catch
        {
            logFile = null;
        }

        var loggerConfig = new LoggerConfiguration()
#if DEBUG
                           .MinimumLevel.Debug()
#else
                               .MinimumLevel.Information()
#endif
                           .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                           .MinimumLevel.Override("System", LogEventLevel.Warning)
                           .Enrich.FromLogContext();

        // ⭐ Konsol sink'i yalniz acikca istenirse eklenir. TUI ekrani
        //   kullandigi icin CLI'da kapatiliyor - bkz. Configure aciklamasi.
        if (writeToConsole)
        {
            loggerConfig.WriteTo.Async(a => a.Console(
                                               theme: AnsiConsoleTheme.Code,
                                               outputTemplate:
                                               "[{Timestamp:MM/dd HH:mm:ss}] | {Level} | {SourceContext} {NewLine}{Message:lj}{NewLine}{Exception}{NewLine}"));
        }

        if (logFile is not null)
        {
            loggerConfig.WriteTo.Async(a => a.File(
                                           logFile,
                                           rollingInterval: RollingInterval.Day,
                                           outputTemplate:
                                           "[{Timestamp:yyyy/MM/dd HH:mm:ss.fff}] | {Level} | {SourceContext} {NewLine}{Message:lj}{NewLine}{Exception}{NewLine}"));
        }

        var logger = loggerConfig.CreateLogger();

        services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddSerilog(logger, true);
        });

        Log.Logger = logger;
    }
}
