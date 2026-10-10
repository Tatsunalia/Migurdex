using Migurdex.Cli.Services;
using Migurdex.Cli.Services.Downloads;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Models;
using Spectre.Console;

namespace Migurdex.Cli.Tui.Views;

public class AnimeDetailsView : BaseView
{
    /// <summary>
    ///     Bölüm listesindeki "Toplu indir" satırının arama anahtarı.
    /// </summary>
    private const string TopluIndirSearchable = "Toplu indir";

    private readonly IApiClientService _apiClient;
    private readonly IHistoryService   _historyService;
    private readonly IServiceProvider  _serviceProvider;
    private          string?           _animeId;
    private          string?           _animeTitle;
    private          AnimeDetails?     _cachedDetails;
    private          string?           _initialPosterUrl;
    private          string?           _lastSelectedSearchable;
    private          string?           _lastSelectedSeasonSearchable;
    private          string?           _provider;
    private          int?              _selectedSeason;

    public AnimeDetailsView(IApiClientService apiClient,
        IHistoryService                       historyService,
        IServiceProvider                      serviceProvider)
    {
        _apiClient       = apiClient;
        _historyService  = historyService;
        _serviceProvider = serviceProvider;
    }

    public void SetTarget(string provider, string animeId, string? initialPosterUrl = null, string? animeTitle = null)
    {
        if (_provider != provider || _animeId != animeId)
        {
            _cachedDetails                = null;
            _animeTitle                   = animeTitle;
            _selectedSeason               = null;
            _lastSelectedSeasonSearchable = null;
            _lastSelectedSearchable       = null;
        }

        _provider         = provider;
        _animeId          = animeId;
        _initialPosterUrl = initialPosterUrl;
    }

    public override string GetRpcState()
    {
        var title = _cachedDetails?.Title ?? _animeTitle;
        return string.IsNullOrWhiteSpace(title) ? "Detaylar" : title;
    }

    public override async Task RenderAsync(ITuiNavigator navigator)
    {
        if (string.IsNullOrEmpty(_provider) || string.IsNullOrEmpty(_animeId))
        {
            AnsiConsole.MarkupLine("[red]Hata: Sağlayıcı veya anime belirtilmemiş.[/]");
            if (!TuiConsole.WaitForKey())
            {
                return;
            }

            navigator.Pop();
            return;
        }

        var     details   = _cachedDetails;
        string? loadError = null;
        if (details == null)
        {
            AnsiConsole.Clear();
            Theme.WriteHeader(_animeTitle ?? "Detaylar");
            AnsiConsole.MarkupLine(TuiHelpers.ProviderLine(_provider!));
            AnsiConsole.WriteLine();

            await AnsiConsole.Status()
                             .Spinner(Spinner.Known.Dots)
                             .StartAsync("Yükleniyor...",
                                         async ctx =>
                                         {
                                             try
                                             {
                                                 var result =
                                                     await _apiClient.GetAnimeDetailsAsync(_provider, _animeId);

                                                 details   = result.Data;
                                                 loadError = result.Error;
                                             }
                                             catch (Exception ex)
                                             {
                                                 loadError = ex.Message;
                                                 AnsiConsole.WriteException(ex);
                                             }
                                         });

            if (details == null)
            {
                AnsiConsole.MarkupLine("[red]Veri yok.[/]");
                if (loadError is not null)
                {
                    AnsiConsole.MarkupLine($"[grey]{Markup.Escape(loadError)}[/]");
                }

                if (!TuiConsole.WaitForKey())
                {
                    return;
                }

                navigator.Pop();
                return;
            }

            if (string.IsNullOrWhiteSpace(details.PosterUrl) && !string.IsNullOrWhiteSpace(_initialPosterUrl))
            {
                details.PosterUrl = _initialPosterUrl;
            }

            details.Normalize();

            _cachedDetails = details;
            AnsiConsole.Clear();
        }

        var isFav = _historyService.IsFavorite(_animeId, _provider);

        var metaParts = new List<string>
        {
            TuiHelpers.ProviderLine(_provider!),
            $"[grey]Format:[/] {details.Format}",
            $"[grey]{details.Episodes.Count} bölüm[/]"
        };

        if (isFav)
        {
            metaParts.Add("[yellow]♥ Favorilerde[/]");
        }

        var headerLines = new List<string>
        {
            string.Join("  [grey]•[/]  ", metaParts)
        };

        if (!string.IsNullOrWhiteSpace(details.Summary) && details.Summary != "-")
        {
            foreach (var line in Theme.WrapText(details.Summary))
            {
                headerLines.Add($"[grey]{Markup.Escape(line)}[/]");
            }
        }

        var groupedEpisodes = details.Episodes
                                     .GroupBy(e => e.Season ?? 1)
                                     .OrderBy(g => g.Key)
                                     .ToList();

        var isMultiSeason = groupedEpisodes.Count > 1;

        if (isMultiSeason && _selectedSeason == null)
        {
            var seasonChoices = new List<FuzzyChoice>
            {
                new()
                {
                    Display       = isFav ? "[yellow]♥ Favorilerden çıkar[/]" : "[yellow]♡ Favorilere ekle[/]",
                    DisplayActive = isFav ? "[bold white]♥ Favorilerden çıkar[/]" : "[bold white]♡ Favorilere ekle[/]",
                    Searchable    = isFav ? "Favorilerden Çıkar" : "Favorilere Ekle",
                    IsAction      = true
                }
            };

            foreach (var group in groupedEpisodes)
            {
                var seasonNum = group.Key;
                var epCount   = group.Count();
                var label     = $"{seasonNum}. Sezon";
                seasonChoices.Add(new FuzzyChoice
                {
                    Display       = $"{seasonNum}. Sezon [grey]({epCount})[/]",
                    DisplayActive = $"[bold white]{seasonNum}. Sezon ({epCount})[/]",
                    Searchable    = label
                });
            }

            seasonChoices.Add(TuiHelpers.Back());

            var seasonChoice = FuzzyPrompt.Show(details.Title,
                                                seasonChoices,
                                                initialSelection: _lastSelectedSeasonSearchable,
                                                headerLines: headerLines);

            if (seasonChoice == null || seasonChoice.Searchable == "Geri")
            {
                navigator.Pop();
                return;
            }

            _lastSelectedSeasonSearchable = seasonChoice.Searchable;

            if (seasonChoice.Searchable is "Favorilere Ekle" or "Favorilerden Çıkar")
            {
                _historyService.ToggleFavorite(new FavoriteEntry
                {
                    AnimeId      = _animeId,
                    AnimeTitle   = details.Title,
                    ProviderName = _provider
                });
                return;
            }

            var matchedGroup = groupedEpisodes.FirstOrDefault(g => $"{g.Key}. Sezon" == seasonChoice.Searchable);
            if (matchedGroup != null)
            {
                _selectedSeason         = matchedGroup.Key;
                _lastSelectedSearchable = null;
            }

            return;
        }

        var activeSeason = _selectedSeason ?? (groupedEpisodes.Count > 0 ? groupedEpisodes[0].Key : 1);
        var currentSeasonGroup = groupedEpisodes.FirstOrDefault(g => g.Key == activeSeason)
                                 ?? groupedEpisodes.FirstOrDefault();

        if (isMultiSeason)
        {
            headerLines.Add($"[grey]Sezon:[/] {activeSeason}. Sezon");
        }

        var choices = new List<FuzzyChoice>();

        var isSingleContent = details.Episodes.Count == 1;
        var episodeMap      = new Dictionary<string, Episode>();

        var resumeEpisodeId = _historyService.GetWatchHistory()
                                             .Where(h => h.ProviderName == _provider
                                                         && h.AnimeId == _animeId
                                                         && h is { IsCompleted: false, LastPositionSeconds: > 0 })
                                             .OrderByDescending(h => h.LastWatchedAt)
                                             .FirstOrDefault()
                                             ?.EpisodeId;

        if (isSingleContent)
        {
            var singleEp = details.Episodes[0];
            var isMovie = details.Format == ContentFormat.Movie
                          || AnimeDetails.IsMovieTitle(details.Title)
                          || AnimeDetails.IsMovieSummary(details.Summary);

            var playSearchable = isMovie ? "Filmi Oynat" : "Bölümü Oynat";
            var playDisplay    = isMovie ? "[bold green]▶ Filmi oynat[/]" : "[bold green]▶ Bölümü oynat[/]";
            var playActiveDisplay =
                isMovie
                    ? "[bold white on darkgreen] ▶ Filmi oynat [/]"
                    : "[bold white on darkgreen] ▶ Bölümü oynat [/]";

            choices.Add(new FuzzyChoice
            {
                Display       = playDisplay,
                DisplayActive = playActiveDisplay,
                Searchable    = playSearchable
            });

            episodeMap[playSearchable] = singleEp;

            var downloadSearchable = isMovie ? "Filmi İndir" : "Bölümü İndir";
            var downloadDisplay    = isMovie ? "[cyan]⤓ Filmi indir[/]" : "[cyan]⤓ Bölümü indir[/]";
            var downloadActiveDisplay =
                isMovie
                    ? "[bold white on grey23] ⤓ Filmi indir [/]"
                    : "[bold white on grey23] ⤓ Bölümü indir [/]";

            choices.Add(new FuzzyChoice
            {
                Display       = downloadDisplay,
                DisplayActive = downloadActiveDisplay,
                Searchable    = downloadSearchable
            });

            episodeMap[downloadSearchable] = singleEp;

            choices.Add(new FuzzyChoice
            {
                Display       = isFav ? "[yellow]♥ Favorilerden çıkar[/]" : "[yellow]♡ Favorilere ekle[/]",
                DisplayActive = isFav ? "[bold white]♥ Favorilerden çıkar[/]" : "[bold white]♡ Favorilere ekle[/]",
                Searchable    = isFav ? "Favorilerden Çıkar" : "Favorilere Ekle",
                IsAction      = true
            });
        }
        else
        {
            if (!isMultiSeason)
            {
                choices.Add(new FuzzyChoice
                {
                    Display       = isFav ? "[yellow]♥ Favorilerden çıkar[/]" : "[yellow]♡ Favorilere ekle[/]",
                    DisplayActive = isFav ? "[bold white]♥ Favorilerden çıkar[/]" : "[bold white]♡ Favorilere ekle[/]",
                    Searchable    = isFav ? "Favorilerden Çıkar" : "Favorilere Ekle",
                    IsAction      = true
                });
            }

            // ⭐ Toplu indirme listenin YERİNE geçmez, liste içinde ayrı bir
            //   satırdır. Böylece bölüme Enter ile basıldığında oynatma,
            //   tekli indirme ve favori ekleme yolları bozulmaz.
            var topluIndirilecekBolum = currentSeasonGroup?.Count() ?? 0;
            if (topluIndirilecekBolum > 1)
            {
                choices.Add(new FuzzyChoice
                {
                    Display       = $"[cyan]⤓ Toplu indir[/] [grey]({topluIndirilecekBolum} bölüm)[/]",
                    DisplayActive = $"[bold white on grey23] ⤓ Toplu indir ({topluIndirilecekBolum} bölüm) [/]",
                    Searchable    = TopluIndirSearchable,
                    IsAction      = true
                });
            }

            if (currentSeasonGroup != null)
            {
                var isMovie    = details.Format == ContentFormat.Movie;
                var unitPrefix = isMovie ? "Film" : "Bölüm";

                foreach (var ep in currentSeasonGroup.OrderBy(e => e.Number))
                {
                    var titleTrimmed = ep.Title?.Trim() ?? "";
                    var isGenericTitle = string.IsNullOrWhiteSpace(titleTrimmed)
                                         || titleTrimmed.Equals($"{ep.Number}", StringComparison.OrdinalIgnoreCase)
                                         || titleTrimmed.Equals($"{ep.Number}.", StringComparison.OrdinalIgnoreCase)
                                         || titleTrimmed.Equals($"Bölüm {ep.Number}",
                                                                StringComparison.OrdinalIgnoreCase)
                                         || titleTrimmed.Equals($"{ep.Number}. Bölüm",
                                                                StringComparison.OrdinalIgnoreCase)
                                         || titleTrimmed.Equals($"{ep.Number}.Bölüm",
                                                                StringComparison.OrdinalIgnoreCase)
                                         || titleTrimmed.Equals($"Film {ep.Number}", StringComparison.OrdinalIgnoreCase)
                                         || titleTrimmed.Equals($"{ep.Number}. Film",
                                                                StringComparison.OrdinalIgnoreCase)
                                         || titleTrimmed.Equals($"{ep.Number}.Film", StringComparison.OrdinalIgnoreCase)
                                         || titleTrimmed.Equals("Film", StringComparison.OrdinalIgnoreCase)
                                         || titleTrimmed.Equals("Bölüm", StringComparison.OrdinalIgnoreCase);

                    var hasCustomTitle = !isGenericTitle;

                    var shortTitle       = hasCustomTitle ? Theme.Ellipsize(ep.Title!, 44) : string.Empty;
                    var isResume         = ep.Id == resumeEpisodeId;
                    var resumeBadge      = isResume ? $" {Theme.Badge("devam")}" : "";
                    var resumeSearchable = isResume ? " devam" : "";
                    var display = hasCustomTitle
                                      ? $"[grey]{ep.Number:00}[/] {unitPrefix} [grey]│ {Markup.Escape(shortTitle)}[/]{resumeBadge}"
                                      : $"[grey]{ep.Number:00}[/] {unitPrefix}{resumeBadge}";

                    var displayActive = hasCustomTitle
                                            ? $"[bold white]{ep.Number:00} {unitPrefix} │ {Markup.Escape(shortTitle)}[/]{resumeBadge}"
                                            : $"[bold white]{ep.Number:00} {unitPrefix}[/]{resumeBadge}";

                    var searchable = (hasCustomTitle
                                          ? $"{ep.Number}. {unitPrefix} {ep.Title}"
                                          : $"{ep.Number}. {unitPrefix}")
                                     + resumeSearchable;

                    choices.Add(new FuzzyChoice
                    {
                        Display       = display,
                        DisplayActive = displayActive,
                        Searchable    = searchable
                    });

                    episodeMap[searchable] = ep;
                }
            }
        }

        choices.Add(TuiHelpers.Back());

        var choice = FuzzyPrompt.Show(details.Title,
                                      choices,
                                      initialSelection: _lastSelectedSearchable,
                                      headerLines: headerLines);

        if (choice == null || choice.Searchable == "Geri")
        {
            if (isMultiSeason)
            {
                _selectedSeason         = null;
                _lastSelectedSearchable = null;
                return;
            }

            navigator.Pop();
            return;
        }

        _lastSelectedSearchable = choice.Searchable;

        if (choice.Searchable is "Favorilere Ekle" or "Favorilerden Çıkar")
        {
            _historyService.ToggleFavorite(new FavoriteEntry
            {
                AnimeId      = _animeId,
                AnimeTitle   = details.Title,
                ProviderName = _provider
            });
            return;
        }

        // ⭐ "Toplu indir" satırı. Kullanıcı Esc'ye basarsa veya hiçbir bölüm
        //   işaretlemezse bölüm listesine döner; hiçbir indirme başlamaz.
        if (choice.Searchable == TopluIndirSearchable)
        {
            var secilenler = SelectEpisodesForBulkDownload(details,
                                                          headerLines,
                                                          currentSeasonGroup);

            if (secilenler is { Count: > 0 })
            {
                await StartBulkDownloadAsync(navigator, details, secilenler);
            }

            return;
        }

        if (episodeMap.TryGetValue(choice.Searchable, out var selectedEp))
        {
            if (isSingleContent)
            {
                var singleMode = choice.Searchable.Contains("İndir", StringComparison.OrdinalIgnoreCase)
                                     ? SourceViewMode.Download
                                     : SourceViewMode.Play;
                var singleSourcesView = (EpisodeSourcesView) _serviceProvider.GetService(typeof(EpisodeSourcesView))!;
                singleSourcesView.SetTarget(_provider, _animeId, details.Title, selectedEp, details.Episodes, details.PosterUrl, mode: singleMode);
                navigator.Push(singleSourcesView);
                return;
            }

            var actions = new List<FuzzyChoice>
            {
                Theme.PlayChoice("Oynat"),
                Theme.ActionChoice("İndir", Theme.Primary),
                TuiHelpers.Back()
            };
            var action = FuzzyPrompt.Show(details.Title,
                                          actions,
                                          searchable: false,
                                          headerLines:
                                          [$"[grey]{Markup.Escape(TuiHelpers.EllipsizedTitle(details.Title))} › {Markup.Escape(TuiHelpers.FormatEpisodeRef(selectedEp))}[/]"],
                                          footerHelp: "↑↓ gez • Enter seç • Esc geri");
            if (action is null || action.Searchable == "Geri")
            {
                return;
            }

            var mode = action.Searchable == "İndir" ? SourceViewMode.Download : SourceViewMode.Play;
            var sourcesView = (EpisodeSourcesView) _serviceProvider.GetService(typeof(EpisodeSourcesView))!;
            sourcesView.SetTarget(_provider, _animeId, details.Title, selectedEp, details.Episodes, details.PosterUrl, mode: mode);
            navigator.Push(sourcesView);
        }
    }

    /// <summary>
    ///     Çoklu bölüm seçim ekranını açar ve seçilen bölümleri döndürür.
    /// </summary>
    /// <returns>
    ///     Kullanıcının işaretlediği bölümler. <c>Esc</c>'ye basılır veya hiçbir
    ///     şey işaretlenmezse <c>null</c> döner — bu durumda hiçbir indirme
    ///     başlamaz.
    /// </returns>
    /// <remarks>
    /// ⭐ Diskte zaten indirilmiş bölümler yeşil gösterilir. Dizin bir kez
    ///     taranır, her bölüm için O(1) sorgu yapılır.
    /// </remarks>
    private List<Episode>? SelectEpisodesForBulkDownload(
        AnimeDetails                        details,
        IReadOnlyList<string>               headerLines,
        IGrouping<int, Episode>?            currentSeasonGroup)
    {
        var sezonBolumleri = currentSeasonGroup?.OrderBy(e => e.Number).ToList() ?? [];
        if (sezonBolumleri.Count == 0)
        {
            return null;
        }

        var pathBuilder = _serviceProvider.GetService(typeof(IDownloadPathBuilder)) as IDownloadPathBuilder;
        var configService = _serviceProvider.GetService(typeof(IConfigurationService)) as IConfigurationService;
        var downloadRoot  = configService?.Config.DownloadDirectory;

        var diskteOlanlar = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (pathBuilder is not null && !string.IsNullOrWhiteSpace(downloadRoot))
        {
            try
            {
                // ⭐ Dizin, indirmenin kullandığı Build ile soruluyor.
                //   BuildAnimeDirectory farklı bir bütçe kullanıyor (ölçüldü:
                //   110 bayt vs 42 bayt) ve uzun anime adlarında yanlış
                //   klasörü gösteriyordu; yeşil liste boş çıkıyordu.
                //   Aynı hatanın iki yerde olmaması için tek kaynak kullanıldı.
                var dizin = pathBuilder.Build(downloadRoot, details.Title, "", 1, 1).AnimeDirectory;
                diskteOlanlar = DownloadPresence.LoadExistingStems(dizin);
            }
            catch (ArgumentException)
            {
                // Çıktı dizini geçersizse yalnızca renksiz gösterilir.
            }

            if (diskteOlanlar.Count > 0)
            {
                headerLines = [..headerLines, "[green]Yeşil[/] = diskte zaten indirilmiş"];
            }
        }

        const string tumunuIsaretle = "Tüm bölümleri işaretle";

        var secenekler = new List<FuzzyChoice>
        {
            new()
            {
                Display       = "[cyan]Tüm bölümleri işaretle[/]",
                DisplayActive = "[bold white on grey23] Tüm bölümleri işaretle [/]",
                Searchable    = tumunuIsaretle,
                IsAction      = true
            }
        };

        var secenekBolum = new Dictionary<string, Episode>(StringComparer.Ordinal);
        var isMovie       = details.Format == ContentFormat.Movie;
        var birim         = isMovie ? "Film" : "Bölüm";

        foreach (var bolum in sezonBolumleri)
        {
            var aramaAnahtari = $"ep{bolum.Number:0000}";
            var kisaBaslik    = bolum.Title?.Trim() ?? "";
            var ozelBaslikVar = !string.IsNullOrWhiteSpace(kisaBaslik)
                                 && !kisaBaslik.Equals($"{bolum.Number}", StringComparison.OrdinalIgnoreCase)
                                 && !kisaBaslik.Equals($"Bölüm {bolum.Number}", StringComparison.OrdinalIgnoreCase);

            var etiket = ozelBaslikVar
                            ? $"{birim} {bolum.Number:00} · {Theme.Ellipsize(bolum.Title!, 44)}"
                            : $"{birim} {bolum.Number:00}";

            var stem   = pathBuilder is not null && !string.IsNullOrWhiteSpace(downloadRoot)
                             ? pathBuilder.BuildFileStem(bolum.Season ?? 1, bolum.Number, bolum.Title ?? "")
                             : string.Empty;
            var diskte = stem.Length > 0 && diskteOlanlar.Contains(stem);

            secenekler.Add(new FuzzyChoice
            {
                Display       = diskte
                                    ? $"[green]{Markup.Escape(etiket)}[/]"
                                    : $"[grey]{Markup.Escape(etiket)}[/]",
                DisplayActive = $"[bold white]{Markup.Escape(etiket)}[/]",
                Searchable    = aramaAnahtari,
                CanBeChecked  = true
            });

            secenekBolum[aramaAnahtari] = bolum;
        }

        secenekler.Add(TuiHelpers.Back());

        var secilenler = FuzzyPrompt.ShowMulti(details.Title,
                                               secenekler,
                                               initialSelection: tumunuIsaretle,
                                               headerLines:      headerLines,
                                               footerHelp: "↑↓ gez • Space işaretle • Ctrl+A tümünü işaretle "
                                                           + "• Enter onayla • Esc geri");

        if (secilenler.Count == 0)
        {
            return null;
        }

        // Hiçbiri işaretlenmemişse imleçteki satır seçilmiş sayılır; bu,
        // "Tüm bölümleri işaretle" kısayolunu çalışır.
        if (secilenler.Count == 1 && secilenler[0].Searchable == tumunuIsaretle)
        {
            return sezonBolumleri;
        }

        var sonuc = secilenler.Where(c => secenekBolum.ContainsKey(c.Searchable))
                              .Select(c => secenekBolum[c.Searchable])
                              .ToList();

        return sonuc.Count > 0 ? sonuc : null;
    }

    /// <summary>
    ///     Seçilen bölümleri sırayla indirir ve sonucu özetler.
    /// </summary>
    /// <remarks>
    /// ⭐ Gösterge <b>tekli indirmenin kendi kodu</b>dır
    ///     (<see cref="EpisodeSourcesView.UpdateDownloadTask"/>). Burada yeni
    ///     bir gösterge yazılmadı; iki ekran aynı satırı günceller, gösteri
    ///     ayrışamaz.
    /// <para>
    ///     Bölümler <b>sırayla</b> iner. Bir bölümün hatası kalanları
    ///     durdurmaz; <c>Esc</c> kalanları durdurur ve iptal edilen bölüm
    ///     başarısız sayılmaz.
    /// </para>
    /// </remarks>
    private async Task StartBulkDownloadAsync(ITuiNavigator navigator,
        AnimeDetails                                                    details,
        List<Episode>                                                   secilenler)
    {
        if (_serviceProvider.GetService(typeof(IConfigurationService)) is not IConfigurationService configService
            || _serviceProvider.GetService(typeof(IDownloadService)) is not IDownloadService downloadService
            || _serviceProvider.GetService(typeof(IDownloadPathBuilder)) is not IDownloadPathBuilder pathBuilder)
        {
            AnsiConsole.MarkupLine("[red]Toplu indirme için gerekli servisler bulunamadı.[/]");
            return;
        }

        var config      = configService.Config;
        var userStopped = false;
        var succeeded   = new List<Episode>();
        var skipped     = new List<Episode>();
        var basarisiz   = new List<(Episode Bolum, string Neden)>();
        var iptal       = false;

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TuiApplicationCancellation.Token);

        try
        {
            using var modal = TuiApplicationCancellation.BeginModal(cancellation);

            await AnsiConsole.Progress()
                             .AutoClear(false)
                             // ⭐ false: tamamlanan bölüm satırı EKRANDA KALIR.
                             //   true iken her bölüm bittiğinde satırı siliniyor,
                             //   ekran boşalıp baştan çiziliyordu; kullanıcı
                             //   "yeni indirme başladı" sanıyordu. Tekli
                             //   indirme de aynı değeri kullanır
                             //   (EpisodeSourcesView:514).
                             .HideCompleted(false)
                             .Columns(new PercentageColumn(),
                                      new ProgressBarColumn(),
                                      new TaskDescriptionColumn(),
                                      new SpinnerColumn())
                             .StartAsync(async progressContext =>
                             {
                                 for (var i = 0; i < secilenler.Count; i++)
                                 {
                                     if (cancellation.IsCancellationRequested)
                                     {
                                         iptal = true;
                                         break;
                                     }

                                     var bolum = secilenler[i];

                                     if (DownloadPresence.IsAlreadyDownloaded(pathBuilder,
                                                                             config,
                                                                             details.Title,
                                                                             bolum.Title,
                                                                             bolum.Season ?? 1,
                                                                             bolum.Number,
                                                                             config.DownloadOverwrite))
                                     {
                                         skipped.Add(bolum);
                                         continue;
                                     }

                                     var tracker = new EpisodeSourcesView.DownloadProgressTracker();

                                     // ⭐ Satırda anime adı + bölüm + sıra yazar.
                                     //   "Bölüm 5/76" tek başına hangi anime olduğunu
                                     //   söylemiyordu. Etiket tekli indirmeyle aynı
                                     //   fonksiyondan gelir.
                                     var hedefEtiketi =
                                         $"{TuiHelpers.FormatHedefEtiketi(details.Title, bolum)} "
                                         + $"({i + 1}/{secilenler.Count})";

                                     var task = progressContext.AddTask($"{hedefEtiketi} • Hazırlanıyor",
                                                                           maxValue: 100);

                                     VideoSource? kaynak = null;
                                     try
                                     {
                                         var adaylar = await DownloadCandidateResolver.ResolveAsync(
                                                                               _apiClient,
                                                                               _provider!,
                                                                               bolum.Id,
                                                                               null,
                                                                               DownloadSourceFormat.Auto,
                                                                               config,
                                                                               config.DownloadAutoSelectTimeoutSeconds,
                                                                               cancellation.Token);

                                         kaynak = adaylar.Candidates.FirstOrDefault();
                                     }
                                     catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                                     {
                                         iptal = true;
                                         task.Description = $"{hedefEtiketi} • iptal edildi";
                                         break;
                                     }
                                     catch (Exception)
                                     {
                                         kaynak = null;
                                     }

                                     if (kaynak is null)
                                     {
                                         task.Description = $"{hedefEtiketi} • kaynak bulunamadı";
                                         basarisiz.Add((bolum, "İndirilebilir kaynak bulunamadı."));
                                         continue;
                                     }

                                     DownloadResult? sonuc      = null;
                                     var               indirme   = downloadService.DownloadAsync(
                                                                 new DownloadRequest
                                                                 {
                                                                     Source            = kaynak,
                                                                     OutputDirectory   = config.DownloadDirectory,
                                                                     AnimeTitle        = details.Title,
                                                                     EpisodeTitle      = bolum.Title,
                                                                     Season            = bolum.Season ?? 1,
                                                                     EpisodeNumber     = bolum.Number,
                                                                     Overwrite         = config.DownloadOverwrite,
                                                                     Resume            = config.DownloadResume,
                                                                     DownloadSubtitles = config.DownloadSubtitles,
                                                                     Progress          = tracker
                                                                 },
                                                                 cancellation.Token);

                                     while (!indirme.IsCompleted)
                                     {
                                         if (Console.KeyAvailable
                                             && Console.ReadKey(true).Key == ConsoleKey.Escape)
                                         {
                                             userStopped = true;
                                             cancellation.Cancel();
                                             break;
                                         }

                                         EpisodeSourcesView.UpdateDownloadTask(task, tracker, hedefEtiketi);
                                         await Task.Delay(100);
                                     }

                                     EpisodeSourcesView.UpdateDownloadTask(task, tracker, hedefEtiketi);
                                     sonuc = await indirme;

                                     if (sonuc is { Success: true })
                                     {
                                         succeeded.Add(bolum);
                                     }
                                     else if (sonuc is { IsCancelled: true })
                                     {
                                         iptal = true;
                                     }
                                     else
                                     {
                                         basarisiz.Add((bolum,
                                                        sonuc?.Error ?? "Bilinmeyen hata."));
                                     }
                                 }
                             });
        }
        catch (OperationCanceledException)
        {
            iptal = true;
        }
        catch (Exception)
        {
            AnsiConsole.MarkupLine("[red]Toplu indirme sırasında beklenmeyen bir hata oluştu.[/]");
            return;
        }

        if (TuiApplicationCancellation.Token.IsCancellationRequested)
        {
            return;
        }

        AnsiConsole.Clear();
        WriteBulkSummary(secilenler.Count, succeeded, skipped, basarisiz, iptal || userStopped);
    }

    /// <summary>Toplu indirme sonrası özeti basar.</summary>
    /// <remarks>
    /// ⭐ Başarısız bölümlerin <b>nedeni</b> yazılır, sadece numarası değil.
    /// Aksi hâlde 1166 bölümün hepsi başarısız olduğunda ekran "Bölüm 2"
    /// diye 1166 kez aynı şeyi söyler ve gerçek sorun görünmez.
    /// </remarks>
    private static void WriteBulkSummary(
        int                                 toplam,
        List<Episode>                       succeeded,
        List<Episode>                       skipped,
        List<(Episode Bolum, string Neden)> basarisiz,
        bool                                durduruldu)
    {
        AnsiConsole.MarkupLine($"[green]✓[/] {succeeded.Count} bölüm indirildi [grey](hedef: {toplam})[/]");

        if (skipped.Count > 0)
        {
            AnsiConsole.MarkupLine($"[cyan]•[/] {skipped.Count} bölüm diskte zaten var, atlandı");
        }

        if (basarisiz.Count > 0)
        {
            AnsiConsole.MarkupLine($"[red]![/] {basarisiz.Count} bölüm indirilemedi:");

            foreach (var grup in basarisiz.GroupBy(b => b.Neden).OrderByDescending(g => g.Count()))
            {
                var etiketler = string.Join(", ", grup.Select(b => $"Bölüm {b.Bolum.Number:00}").Take(6));
                var kalan     = grup.Count() - 6;

                AnsiConsole.MarkupLine($"  [grey]×{grup.Count()}[/] {grup.Key} [grey]{etiketler}"
                                       + (kalan > 0 ? $" [grey](+{kalan})[/]" : ""));
            }
        }

        if (durduruldu)
        {
            AnsiConsole.MarkupLine("[yellow]Durduruldu. İndirilen bölümler diskte kaldı.[/]");
        }

        AnsiConsole.WriteLine();
    }
}
