using Migurdex.Cli.Configuration;
using Migurdex.Cli.Services.Downloads;
using Migurdex.Shared.Models;
using Xunit;

namespace Migurdex.Tests;

/// <summary>
///     "Bu bölüm diskte zaten var mı?" kontrolü.
/// </summary>
/// <remarks>
/// ⭐ Regresyon kilidi. Kontrol <c>BuildAnimeDirectory</c> ile dizin soruyordu,
///     indirme ise <c>Build</c> ile yazıyordu. İkisi <b>ayrı bütçe</b>
///     kullandığı için uzun anime adlarında iki farklı klasör üretiyordu:
///     kontrol yanlış klasörü tarıyor, bölüm "yok" sayılıyor ve yeniden
///     indiriliyordu. Ölçüldü: 110 bayt vs 42 bayt.
/// </remarks>
public sealed class DownloadPresenceTests : IDisposable
{
    private readonly string _kok;

    public DownloadPresenceTests()
    {
        _kok = Path.Combine(Path.GetTempPath(), "dlpresence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_kok);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_kok, true);
        }
        catch (IOException)
        {
            // temizlik basarisizligi testi bozmamali
        }
    }

    private static CliConfig Config(string kok) => new() { DownloadDirectory = kok };

    /// <summary>
    ///     İndirmenin gerçekten yazdığı yolu üretir.
    /// </summary>
    /// <remarks>
    /// ⭐ <see cref="DownloadService"/> da <c>pathBuilder.Build</c> çağırıyor;
    ///     test de aynısını kullanır. Test "gerçek davranışı" taklit eder,
    ///     kontrollü bir şeyi değil.
    /// </remarks>
    /// <summary>
    ///     İndirmenin gerçekten yazdığı yolu üretir.
    /// </summary>
    /// <remarks>
    /// ⭐ <see cref="DownloadService"/> da <c>pathBuilder.Build</c> çağırıyor;
    ///     test de aynısını kullanır. Test "gerçek davranışı" taklit eder,
    ///     kontrollü bir şeyi değil.
    /// <para>
    ///     ⭐ <c>Build</c> anime klasörünü <b>oluşturmaz</b> (indirme sırasında
    ///     <c>DownloadService</c> oluşturur). Testte yazmadan önce dizin
    ///     oluşturulmalı — yoksa <c>DirectoryNotFoundException</c> gelir.
    /// </para>
    /// </remarks>
    private static DownloadPath GercekYol(DownloadPathBuilder b, CliConfig c, string anime, string? bolumBasligi, int sezon, double bolum)
    {
        var yol = b.Build(c.DownloadDirectory, anime, bolumBasligi ?? "", sezon, bolum);
        Directory.CreateDirectory(yol.AnimeDirectory);
        return yol;
    }

    // ---------- atlamali ----------

    [Fact]
    public void IsAlreadyDownloaded_DosyaDiskteVarsa_Atlanir()
    {
        var b     = new DownloadPathBuilder();
        var c     = Config(_kok);
        var yol   = GercekYol(b, c, "Test Anime", "Bölüm 1", 1, 1);

        File.WriteAllText(yol.MediaPath, "video");

        Assert.True(DownloadPresence.IsAlreadyDownloaded(b, c, "Test Anime", "Bölüm 1", 1, 1));
    }

    [Fact]
    public void IsAlreadyDownloaded_DosyaYoksa_Indirilir()
    {
        var b = new DownloadPathBuilder();
        var c = Config(_kok);

        Assert.False(DownloadPresence.IsAlreadyDownloaded(b, c, "Test Anime", "Bölüm 1", 1, 1));
    }

    [Fact]
    public void IsAlreadyDownloaded_UzunAnimeAdi_DogruKlasoruTarar()
    {
        // ⭐ ASIL REGRESYON. Anime adi 42 baytin uzerinde: iki yol farkli
        //   klasor uretiyordu. Dosya GERCEK yola yazilir; kontrol onu
        //   gormeli.
        var uzunBaslik = new string('a', 60) + " Anime";

        var b   = new DownloadPathBuilder();
        var c   = Config(_kok);
        var yol = GercekYol(b, c, uzunBaslik, "Bölüm 1", 1, 1);

        File.WriteAllText(yol.MediaPath, "video");

        Assert.True(DownloadPresence.IsAlreadyDownloaded(b, c, uzunBaslik, "Bölüm 1", 1, 1),
                    "uzun anime adinda kontrol gercek yolu gormeli");
    }

    [Fact]
    public void KontrolVeYazma_AyniKlasoruIsaretEder()
    {
        // ⭐ Iki yolun AYNI klasoru gostermesi gerekir; farkli gosteriyorsa
        //   kontrol calisir ama yanlis yere bakar.
        var b = new DownloadPathBuilder();
        var c = Config(_kok);

        foreach (var baslik in new[] { "Kısa", new string('b', 60), "BOCCHI THE ROCK! Re: Bocchi" })
        {
            var yol = GercekYol(b, c, baslik, "", 1, 1);
            File.WriteAllText(Path.Combine(yol.AnimeDirectory, yol.FileStem + ".mp4"), "x");

            Assert.True(DownloadPresence.IsAlreadyDownloaded(b, c, baslik, "", 1, 1),
                        $"'{baslik}' icin kontrol yazma yolunu gormeli");
        }
    }

    [Fact]
    public void IsAlreadyDownloaded_OverwriteAcik_DurumaBakmaz()
    {
        var b     = new DownloadPathBuilder();
        var c     = Config(_kok);
        var yol   = GercekYol(b, c, "Test Anime", "Bölüm 1", 1, 1);

        File.WriteAllText(yol.MediaPath, "video");

        Assert.False(DownloadPresence.IsAlreadyDownloaded(b, c, "Test Anime", "Bölüm 1", 1, 1, overwrite: true));
    }

    // ---------- LoadExistingStems ----------

    [Fact]
    public void LoadExistingStems_VideoDosyalariniAlir()
    {
        Directory.CreateDirectory(Path.Combine(_kok, "A"));
        File.WriteAllText(Path.Combine(_kok, "A", "S01E01 - Bölüm.mp4"), "x");

        var stems = DownloadPresence.LoadExistingStems(Path.Combine(_kok, "A"));

        Assert.Contains("S01E01 - Bölüm", stems);
    }

    [Fact]
    public void LoadExistingStems_AltyaziyiVeYarimiYokSayar()
    {
        Directory.CreateDirectory(Path.Combine(_kok, "A"));
        File.WriteAllText(Path.Combine(_kok, "A", "S01E01 - Bölüm.vtt"), "x");
        File.WriteAllText(Path.Combine(_kok, "A", "S01E02 - Bölüm.part"), "x");

        var stems = DownloadPresence.LoadExistingStems(Path.Combine(_kok, "A"));

        Assert.Empty(stems);
    }

    [Fact]
    public void LoadExistingStems_UzantısızDosyayıAlir()
    {
        // HLS'te uzantıyı yt-dlp belirler; dosya uzantısız kalabilir.
        Directory.CreateDirectory(Path.Combine(_kok, "A"));
        File.WriteAllText(Path.Combine(_kok, "A", "S01E03 - Bölüm"), "x");

        var stems = DownloadPresence.LoadExistingStems(Path.Combine(_kok, "A"));

        Assert.Contains("S01E03 - Bölüm", stems);
    }

    [Fact]
    public void LoadExistingStems_VarOlamayanDizin_BosDoner()
    {
        var stems = DownloadPresence.LoadExistingStems(Path.Combine(_kok, "yok"));

        Assert.Empty(stems);
    }

    [Fact]
    public void LoadExistingStems_NullDizin_BosDoner()
    {
        Assert.Empty(DownloadPresence.LoadExistingStems(null));
    }

    [Fact]
    public void IsAlreadyDownloaded_BosKok_Donmez()
    {
        var b = new DownloadPathBuilder();
        var c = new CliConfig { DownloadDirectory = "" };

        // istisna atmamali
        _ = DownloadPresence.IsAlreadyDownloaded(b, c, "X", "Bölüm 1", 1, 1);
    }
}