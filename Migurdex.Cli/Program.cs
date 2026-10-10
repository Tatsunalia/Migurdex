using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Migurdex.Cli.Services;
using Serilog;
using Migurdex.Cli.Services.Downloads;
using Migurdex.Cli.Tui;
using Migurdex.Cli.Tui.Views;
using Migurdex.Core.Database;
using Migurdex.Core.Extensions;
using Migurdex.Core.Services;
using Migurdex.Core.Services.Turnstile;
using Migurdex.Core.Utils;
using Migurdex.Shared.Models;
using Migurdex.Shared.Update;
using Spectre.Console;

namespace Migurdex.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch
        {
            // ignored
        }

        try
        {
            Console.InputEncoding = System.Text.Encoding.UTF8;
        }
        catch
        {
            // ignored
        }

        if (Environment.GetEnvironmentVariable("NO_COLOR") is not null)
        {
            AnsiConsole.Profile.Capabilities.ColorSystem = ColorSystem.NoColors;
        }

        CleanStaleBackup();

        if (args.Any(a => a.Equals("--version", StringComparison.OrdinalIgnoreCase)
                          || a.Equals("-v", StringComparison.OrdinalIgnoreCase))
            || (args.Length > 0 && args[0].Equals("version", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine($"migurdex {AppInfo.GetDisplayVersion()}");
            return 0;
        }

        if (HelpCommand.IsTopLevelRequest(args))
        {
            // TTY yokken TUI'yi açmak Console.ReadKey ile çöktürür; yardım yalnızca yazma yapar.
            return HelpCommand.Run();
        }

        if (args.Length > 0 && args[0].Equals("update", StringComparison.OrdinalIgnoreCase))
        {
            var updateServices = new ServiceCollection();
            ConfigureServices(updateServices);
            using var updateProvider = updateServices.BuildServiceProvider();
            return await UpdateCommand.RunAsync(args[1..], updateProvider);
        }

        if (args.Length > 0 && args[0].Equals("auth", StringComparison.OrdinalIgnoreCase))
        {
            var authServices = new ServiceCollection();
            ConfigureServices(authServices);
            using var authProvider = authServices.BuildServiceProvider();
            return await AuthCommand.RunAsync(args[1..],
                                              authProvider.GetRequiredService<OAuthTokenStore>(),
                                              authProvider.GetRequiredService<AniListOAuthClient>(),
                                              authProvider.GetRequiredService<MalOAuthClient>());
        }

        if (args.Length > 0 && NonInteractiveCommand.IsCommand(args[0]))
        {
            var nonInteractiveServices = new ServiceCollection();
            ConfigureServices(nonInteractiveServices);
            using var nonInteractiveProvider = nonInteractiveServices.BuildServiceProvider();
            return await NonInteractiveCommand.RunAsync(args, nonInteractiveProvider);
        }

        AppDomain.CurrentDomain.ProcessExit += (s, e) => RestoreCursor();
        TuiApplicationCancellation.Reset();
        ITuiNavigator? activeNavigator   = null;
        var            cancelSignalCount = 0;
        ConsoleCancelEventHandler tuiCancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            if (TuiApplicationCancellation.IsModalActive && TuiApplicationCancellation.CancelModal())
            {
                RestoreCursor();
                return;
            }

            if (Interlocked.Increment(ref cancelSignalCount) == 1)
            {
                TuiApplicationCancellation.RequestCancellation();
                activeNavigator?.Exit();
                RestoreCursor();
            }
            else
            {
                eventArgs.Cancel = false;
            }
        };
        Console.CancelKeyPress += tuiCancelHandler;

        try
        {
            Console.Title = "Migurdex Terminal Client";

            var services = new ServiceCollection();
            ConfigureServices(services);

            using var serviceProvider = services.BuildServiceProvider();

            // ⭐ Başlangıç kaydı: log sisteminin gerçekten çalıştığının
            //   kanıtıdır. Serilog dosyayı ilk Log çağrısında açar; bu satır
            //   olmadan `logs/` klasörü boş kalır ve kullanıcı "log yok"
            //   diye düşünür.
            serviceProvider.GetService<ILoggerFactory>()
                           ?.CreateLogger("Migurdex")
                           .LogInformation("Migurdex baslatildi (surum {Surum})", AppInfo.GetVersion());

            AnsiConsole.Clear();
            AnsiConsole.MarkupLine("[bold cyan]Migurdex[/] [grey]başlatılıyor...[/]");
            AnsiConsole.WriteLine();

            var apiService = serviceProvider.GetRequiredService<IApiClientService>();
            var tuiToken   = TuiApplicationCancellation.Token;

            var isOnline = await AnsiConsole.Status()
                                            .Spinner(Spinner.Known.Dots)
                                            .StartAsync("API kontrol ediliyor...",
                                                        async ctx =>
                                                        {
                                                            if (await apiService.IsApiOnlineAsync(tuiToken))
                                                            {
                                                                tuiToken.ThrowIfCancellationRequested();
                                                                return true;
                                                            }

                                                            ctx.Status(
                                                                "API başlatılıyor...");
                                                            var started =
                                                                await apiService.TryStartApiDaemonAsync(tuiToken);
                                                            tuiToken.ThrowIfCancellationRequested();
                                                            return started;
                                                        });

            if (!isOnline)
            {
                AnsiConsole.MarkupLine("[yellow][[!]] API bağlantısı kurulamadı.[/]");
                AnsiConsole.MarkupLine(
                    "[grey]İstemciyi açıp ayarlardan API adresini değiştirebilirsiniz.[/]");
                AnsiConsole.MarkupLine("[grey]Devam etmek için bir tuşa basın...[/]");
                if (!Console.IsInputRedirected)
                {
                    try { Console.ReadKey(true); }
                    catch
                    {
                        // ignored
                    }
                }
            }

            var navigator = serviceProvider.GetRequiredService<ITuiNavigator>();
            activeNavigator = navigator;
            var mainMenu = serviceProvider.GetRequiredService<MainMenuView>();

            _ = Task.Run(() => serviceProvider.GetRequiredService<WatchSyncService>().FlushQueueAsync());

            var noUpdateCheck = args.Any(a => a.Equals("--no-update-check", StringComparison.OrdinalIgnoreCase));
            await MaybePromptForUpdateAsync(serviceProvider, noUpdateCheck);
            tuiToken.ThrowIfCancellationRequested();

            await navigator.StartAsync(mainMenu);

            AnsiConsole.Clear();
            return 0;
        }
        catch (OperationCanceledException) when (TuiApplicationCancellation.Token.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception ex)
        {
            // ⭐ Yakalanmamış istisna ÖNCEDE ekrana düşüyor ve uygulamayı
            //   öldürüyordu; log dosyasına hiçbir şey yazılmıyordu. Bu
            //   katman olmadan "çöktü" dediğinde sebebi öğrenmek imkânsızdı.
            //   Kullanıcı log dosyasını paylaşarak hatayı bildirebilsin.
            Log.Fatal(ex, "Migurdex beklenmeyen bir hatayla durdu");

            AnsiConsole.Clear();
            AnsiConsole.MarkupLine("[red]Beklenmeyen bir hata oluştu.[/]");
            AnsiConsole.MarkupLine("[grey]Ayrıntılar log dosyasına yazıldı:[/] " + LogFileLocation());
            AnsiConsole.WriteLine();

            if (!Console.IsInputRedirected)
            {
                try { Console.ReadKey(true); }
                catch
                {
                    // ignored
                }
            }

            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= tuiCancelHandler;
            RestoreCursor();
            TuiApplicationCancellation.Reset();
        }
    }

    /// <summary>Log dosyasının yolunu kullanıcıya gösterir.</summary>
    /// <remarks>
    /// ⭐ Serilog dosyayı <c>logs/Migurdex-&lt;tarih&gt;.log</c> adıyla
    ///     açar. Kullanıcı hatayı bildirmek istediğinde bu dosyanın
    ///     yerini bilmesi gerekir; yoksa log sistemi işe yaramaz.
    /// </remarks>
    private static string LogFileLocation()
    {
        var kok = AppContext.BaseDirectory;
        if (string.IsNullOrEmpty(kok) || !Directory.Exists(kok))
        {
            var exeDizin = Environment.ProcessPath is { Length: > 0 } yol
                               ? Path.GetDirectoryName(yol)
                               : null;
            kok = !string.IsNullOrEmpty(exeDizin) && Directory.Exists(exeDizin)
                      ? exeDizin!
                      : Directory.GetCurrentDirectory();
        }

        return Path.Combine(kok, "logs");
    }

    private static async Task MaybePromptForUpdateAsync(IServiceProvider services, bool noUpdateCheck)
    {
        if (noUpdateCheck || Console.IsInputRedirected)
        {
            return;
        }

        try
        {
            var updateService = services.GetRequiredService<IUpdateService>();
            var configService = services.GetRequiredService<IConfigurationService>();

            var result = await updateService.CheckForUpdatesAsync();
            if (result is null || !result.IsUpdateAvailable)
            {
                return;
            }

            if (result.LatestVersion.Equals(configService.Config.SkippedVersion,
                                            StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title($"[cyan]Yeni sürüm mevcut:[/] v{result.CurrentVersion} → v{result.LatestVersion}"
                           + (result.IsPrerelease ? " [grey](pre-release)[/]" : ""))
                    .HighlightStyle(new Style(Color.Cyan1, decoration: Decoration.Bold))
                    .AddChoices("Evet, güncelle", "Hayır", "Bu sürümü atla"));

            if (choice == "Evet, güncelle")
            {
                var code = await UpdateCommand.RunAsync(["--yes"], services);
                if (code == 0)
                {
                    Environment.Exit(0);
                }

                AnsiConsole.MarkupLine("[grey]Mevcut sürümle devam etmek için bir tuşa basın...[/]");
                if (!Console.IsInputRedirected)
                {
                    Console.ReadKey(true);
                }
            }
            else if (choice == "Bu sürümü atla")
            {
                configService.Config.SkippedVersion = result.LatestVersion;
                try { configService.Save(); }
                catch
                {
                    // ignored
                }
            }
        }
        catch
        {
            // ignored
        }
    }

    private static void CleanStaleBackup()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe))
            {
                return;
            }

            var backup = exe + ".old";
            if (File.Exists(backup))
            {
                File.Delete(backup);
            }
        }
        catch
        {
            // ignored
        }
    }

    private static void RestoreCursor()
    {
        Console.Write("\x1b[?25h");
        AnsiConsole.Cursor.Show();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // Keep Turnstile warnings visible on stderr while Serilog writes the
        // normal CLI diagnostics to rolling files only.
        var stderrProvider = new StderrLoggerProvider();
        services.AddSingleton(stderrProvider);
        // ⭐ CLI loglama: Serilog yalnız DOSYAYA yazar, konsola değil.
        //   TUI ekranı kullanıyor; konsola yazılan her satır indirme
        //   çubuğunun üstüne biner. API tarafı LoggerBuilder.ConfigureLogging
        //   ile konsol+a dosya kullanıyor, CLI ise aynı kurulumun dosya-yalnız
        //   hâlidir (LoggerBuilder.Configure).
        LoggerBuilder.Configure(services, writeToConsole: false);
        services.AddLogging(builder => builder.AddProvider(stderrProvider));
        services.AddSingleton<IConfigurationService, ConfigurationService>();
        services.AddSingleton<MigurdexDatabase>(sp =>
                                                    new MigurdexDatabase(
                                                        sp.GetRequiredService<IConfigurationService>()
                                                          .ConfigDirectory));
        services.AddTurnstileServices(TurnstilePaths.DataRoot(),
                                      TurnstilePaths.BrowserCacheRoot(),
                                      sp => sp.GetRequiredService<IConfigurationService>()
                                              .Config.EnableIncognitoMode);
        services.AddSingleton<IHistoryService>(sp =>
                                                   new HistoryService(sp.GetRequiredService<IConfigurationService>(),
                                                                      sp.GetRequiredService<MigurdexDatabase>()));
        services.AddDownloadServices(provider =>
        {
            var config = provider.GetRequiredService<IConfigurationService>().Config;
            return new HlsDownloadOptions
            {
                Executable  = config.YtDlpPath,
                MaxAttempts = 3,
                RetryDelay  = TimeSpan.FromSeconds(1)
            };
        });
        services.AddSingleton<IDiscordRpcService, DiscordRpcService>();
        services.AddSingleton<IMpvPlayerService, MpvPlayerService>();
        services.AddSingleton<PlaybackOrchestrator>();

        services.AddSingleton<HttpClient>();
        services.AddSingleton<IApiClientService, ApiClientService>();
        services.AddSingleton<IUpdateService, UpdateService>();

        services.AddSingleton<OAuthTokenStore>(sp =>
                                                   new OAuthTokenStore(sp.GetRequiredService<MigurdexDatabase>()));

        // ⭐ Tek bir CliBridge kaydı: logger fabrikasını DI'den alır. Önceden
        //   her yerde `new CliBridge()` deniyordu ve logger NullLoggerFactory
        //   idi, yani CLI'daki tüm log mesajları çöpe gidiyordu.
        services.AddSingleton<CliBridge>(sp => new CliBridge(sp.GetService<ILoggerFactory>()));

        services.AddSingleton<AniListOAuthClient>(sp => new AniListOAuthClient(
                                                                              sp.GetRequiredService<CliBridge>(),
                                                                              AniListAppCredentials.ClientId,
                                                                              AniListAppCredentials
                                                                                  .ClientSecret));
        services.AddSingleton<MalOAuthClient>(sp => new MalOAuthClient(
                                                                    sp.GetRequiredService<CliBridge>(),
                                                                    MalAppCredentials.ClientId));
        services.AddSingleton<WatchSyncService>(sp =>
        {
            var api      = sp.GetRequiredService<IApiClientService>();
            var store    = sp.GetRequiredService<OAuthTokenStore>();
            var db       = sp.GetRequiredService<MigurdexDatabase>();
            var aniOauth = sp.GetRequiredService<AniListOAuthClient>();
            var malOauth = sp.GetRequiredService<MalOAuthClient>();
            var bridge   = sp.GetRequiredService<CliBridge>();
            var aniList  = new AniListListClient(bridge, aniOauth, store);
            var mal      = new MalListClient(bridge, malOauth, store);
            return new WatchSyncService(aniList,
                                        store,
                                        (provider, animeId, season, episode, title, ct) =>
                                            MapEpisodeAsync(api, provider, animeId, season, episode, title, ct),
                                        db,
                                        malClient: mal);
        });

        services.AddSingleton<ITuiNavigator, TuiNavigator>();

        services.AddTransient<MainMenuView>();
        services.AddTransient<SearchView>();
        services.AddTransient<SearchResultsView>();
        services.AddTransient<AnimeDetailsView>();
        services.AddTransient<BatchDownloadView>();
        services.AddTransient<EpisodeSourcesView>();
        services.AddTransient<PlaybackMenuView>();
        services.AddTransient<FavoritesView>();
        services.AddTransient<WatchHistoryView>();
        services.AddTransient<SettingsView>();
    }

    private static async Task<EpisodeMappingResult> MapEpisodeAsync(IApiClientService api,
        string                                                                        provider,
        string                                                                        animeId,
        int                                                                           season,
        double                                                                        episode,
        string?                                                                       title,
        CancellationToken                                                             cancellationToken)
    {
        try
        {
            var result = await api.MapTrackerEpisodeAsync(provider, animeId, season, episode, cancellationToken);
            if (result.IsSuccess && result.Data is not null)
            {
                return new EpisodeMappingResult
                {
                    Mapping = result.Data
                };
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                return new EpisodeMappingResult();
            }

            var resolved = await api.ResolveTrackerIdAsync(provider,
                                                           animeId,
                                                           [title],
                                                           cancellationToken: cancellationToken);
            if (resolved.IsSuccess && resolved.Data is not null && resolved.Data.Ambiguous)
            {
                return new EpisodeMappingResult
                {
                    Ambiguous  = true,
                    Candidates = resolved.Data.Candidates
                };
            }

            return new EpisodeMappingResult();
        }
        catch
        {
            return new EpisodeMappingResult();
        }
    }
}
