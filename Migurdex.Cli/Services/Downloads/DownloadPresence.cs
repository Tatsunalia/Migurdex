using Migurdex.Cli.Configuration;

namespace Migurdex.Cli.Services.Downloads;

/// <summary>
///     "Bu bölüm diskte zaten var mı?" sorusunun tek yeridir.
/// </summary>
/// <remarks>
/// ⭐ İki ekran aynı soruyu sorar: seçim ekranı yeşil renkle, toplu indirme
///     ekranı atlamak için. Soru iki yerde ayrı ayrı sorulursa iki ayrı
///     yorum birbirini tutmaz; bu yüzden tek yerde toplandı.
/// </remarks>
public static class DownloadPresence
{
    /// <summary>Gerçek video kapları. Bunlar "indirilmiş" sayılır.</summary>
    /// <remarks>
    /// <c>.part</c> yarım indirme, <c>.vtt</c> altyazıdır — indirilmiş bir
    /// video değildir. Uzantısız dosyalar da kabul edilir: HLS için
    /// uzantıyı yt-dlp belirler ve dosya uzantısız kalabilir.
    /// </remarks>
    private static readonly HashSet<string> VideoExtensions
        = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".mkv", ".webm", ".mov", ".avi", ".m4v", ".ts", ".flv", ".wmv"
        };

    /// <summary>
    ///     Bir dizindeki indirilmiş videoların <b>gövde adlarını</b> döndürür.
    /// </summary>
    /// <param name="animeDirectory">Anime klasörü; yoksa boş küme döner.</param>
    /// <remarks>
    /// ⭐ Seçim ekranında 1166 bölüm gösteriliyor. Her bölüm için dizini
    ///     taramak 0,1 sn eklediği için dizin <b>bir kez</b> taranır ve
    ///     her bölüm için O(1) sorgu yapılır.
    /// </remarks>
    public static HashSet<string> LoadExistingStems(string? animeDirectory)
    {
        var stems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(animeDirectory)
            || !Directory.Exists(animeDirectory))
        {
            return stems;
        }

        foreach (var path in Directory.EnumerateFiles(animeDirectory))
        {
            var name = Path.GetFileName(path);

            // ".part" ve ".vtt" birer video değil; yeniden indirilmeli.
            if (name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var uzanti = Path.GetExtension(name);
            if (uzanti.Length > 0 && !VideoExtensions.Contains(uzanti))
            {
                continue;
            }

            stems.Add(Path.GetFileNameWithoutExtension(name));
        }

        return stems;
    }

    /// <summary>
    ///     Verilen bölüm için diskte indirilmiş bir video var mı?
    /// </summary>
    /// <param name="overwrite">
    ///     <c>true</c> ise mevcut dosya üzerine yazılacağı için "zaten var"
    ///     sayılmaz.
    /// </param>
    public static bool IsAlreadyDownloaded(IDownloadPathBuilder pathBuilder,
                                           CliConfig            config,
                                           string               animeTitle,
                                           string?              episodeTitle,
                                           int                  season,
                                           double               episodeNumber,
                                           bool                 overwrite = false)
    {
        if (overwrite || pathBuilder is null)
        {
            return false;
        }

        // ⭐ Dizin <b>BuildAnimeDirectory ile değil</b>, indirmenin kullandığı
        //   Build ile soruluyor. İkisi aynı bütçeyi kullanmıyor:
        //   BuildAnimeDirectory 110 bayta, Build 42 bayta izin veriyor
        //   (ölçüldü, bu makinede). Uzun anime adlarında iki metot FARKLI
        //   klasor üretir; kontrol burada yanlış klasörü tarar ve bölüm
        //   "yok" der, yeniden indirilir.
        //   DownloadService.cs:67 de aynı Build'i çağırıyor — yani artık
        //   kontrol ve yazma aynı yolu soruyor.
        var stem = pathBuilder.BuildFileStem(season, episodeNumber, episodeTitle ?? "");
        if (stem.Length == 0)
        {
            return false;
        }

        var dizin = pathBuilder.Build(config.DownloadDirectory,
                                      animeTitle,
                                      episodeTitle ?? "",
                                      season,
                                      episodeNumber).AnimeDirectory;

        return LoadExistingStems(dizin).Contains(stem);
    }
}