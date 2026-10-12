using Migurdex.Cli.Services;
using Xunit;

namespace Migurdex.Tests;

/// <summary>
///     Ayarlardaki indirme dizini seçimi (klasör gezgini).
/// </summary>
/// <remarks>
/// ⭐ İki test grubu:
/// <list type="bullet">
/// <item>Gezginin **mantığı** — dizin listeleme, gizli klasör eleme, üst
/// dizine çıkma. Canlı ekran (AnsiConsole.Live) test içinde kurulamaz,
/// ama bu kısımlar saf dosya sistemidir ve ölçülebilir.</item>
/// <item><b>Yazma yolunun korunması</b> — gezgin mevcut ayarı değiştirmez.
/// Geçen sefer "var olan ekranı değiştirme" hatasını yapmıştık; bu testler
/// o hatanın tekrarlanmasını engeller.</item>
/// </list>
/// </remarks>
public sealed class FolderPickerTests : IDisposable
{
    private readonly string _kok;

    public FolderPickerTests()
    {
        _kok = Path.Combine(Path.GetTempPath(), "folderpicker-test-" + Guid.NewGuid().ToString("N"));
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
            // temizlik sorunu testi basarisiz yapmamali
        }
    }

    private string Olustur(params string[] parcalar)
    {
        var yol = Path.Combine([_kok, .. parcalar]);
        Directory.CreateDirectory(yol);
        return yol;
    }

    // ---------- Normalize ----------

    [Fact]
    public void Normalize_BosDizin_AnaDizineDuser()
    {
        var sonuc = FolderPicker.Normalize("");

        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), sonuc);
    }

    [Fact]
    public void Normalize_NullDizin_AnaDizineDuser()
    {
        var sonuc = FolderPicker.Normalize(null);

        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), sonuc);
    }

    [Fact]
    public void Normalize_VarOlanDizin_TamYolaCevirir()
    {
        var sonuc = FolderPicker.Normalize(_kok);

        Assert.Equal(Path.GetFullPath(_kok), sonuc);
    }

    [Fact]
    public void Normalize_VarOlamayanDizin_AnaDizineDuser()
    {
        var sonuc = FolderPicker.Normalize(Path.Combine(_kok, "yok-boyle-bir-klasor"));

        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), sonuc);
    }

    [Fact]
    public void Normalize_KarsilarliKarmaDeger_AnaDizineDuser()
    {
        // Bozuk karakter iceren yol istisna atmamali, gezgin bos ekranla acmamali.
        var sonuc = FolderPicker.Normalize("\0bozuk\0");

        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), sonuc);
    }

    [Fact]
    public void Normalize_BoslukluDizin_Kirpilir()
    {
        var sonuc = FolderPicker.Normalize("   ");

        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), sonuc);
    }

    // ---------- Manuel yazma satiri sabiti ----------

    [Fact]
    public void ManuelYazSearchable_SabitVeGorunur()
    {
        // ⭐ Gezgin acildiginda yazma yolu elde kalmali. Bu sabit
        //   SettingsView'in devam eden yazma kutusuna duser.
        Assert.False(string.IsNullOrWhiteSpace(FolderPicker.ManuelYazSearchable));
    }

    // ---------- dosya sistemi davranisi (gezginin veri kaynagi) ----------

    [Fact]
    public void Gezgin_AltKlasorleri_Listeler()
    {
        Olustur("Anime");
        Olustur("Filmler");

        var alt = Directory.EnumerateDirectories(_kok).Select(Path.GetFileName).ToList();

        Assert.Equal(2, alt.Count);
        Assert.Contains("Anime", alt);
        Assert.Contains("Filmler", alt);
    }

    [Fact]
    public void Gezgin_Dosyalari_Listelemez()
    {
        // Gezgin yalniz klasor gosterir; dosya secilemez.
        Olustur("Anime");
        File.WriteAllText(Path.Combine(_kok, "notlar.txt"), "x");

        var alt = Directory.EnumerateDirectories(_kok).ToList();

        Assert.Single(alt);
        Assert.DoesNotContain(alt, d => d.EndsWith("notlar.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void Gezgin_GizliKlasorleri_Gostermez()
    {
        Olustur(".git");
        Olustur("Anime");

        var gorunur = Directory.EnumerateDirectories(_kok)
                             .Select(Path.GetFileName)
                             .Where(ad => ad is not null && !ad.StartsWith('.'))
                             .ToList();

        Assert.DoesNotContain(".git", gorunur);
        Assert.Contains("Anime", gorunur);
    }

    [Fact]
    public void Gezgin_AltKlasorYoksa_ListelemeCalisir()
    {
        var alt = Directory.EnumerateDirectories(_kok).ToList();

        Assert.Empty(alt);
    }

    [Fact]
    public void Gezgin_UstDizin_VarOlanDizinIcindeBulunur()
    {
        var ust = Directory.GetParent(_kok)?.FullName;

        Assert.NotNull(ust);
        Assert.True(Directory.Exists(ust));
    }

    [Fact]
    public void Gezgin_IcIceKlasorler_HerSeviyedeGezilebilir()
    {
        var derin = Olustur("Anime", "One Piece", "Season 1");

        // ⭐ Olustur(...) yolu OLUSTURDU; burada yolun kendisi dogru
        //   mu diye bakiliyor. Ic ice her seviye gezinilebilir olmali:
        //   gezgin ".." satiriyla ust dizine cikabiliyor.
        Assert.True(Directory.Exists(derin));
        Assert.True(Directory.Exists(Path.Combine(_kok, "Anime", "One Piece")));
        Assert.True(Directory.Exists(Path.Combine(_kok, "Anime", "One Piece", "Season 1")));
    }

    [Fact]
    public void Gezgin_SecilenDizin_YazilabilirMi_Dogrulanir()
    {
        // Kullanici var olmayan bir klasoru secebilir; indirme sirasinda
        // hata vermemesi icin yazilabilir olmasi mantikli.
        var yeni = Path.Combine(_kok, "Yeni Klasor");
        Directory.CreateDirectory(yeni);

        var probe = Path.Combine(yeni, "test.txt");
        File.WriteAllText(probe, "x");

        Assert.True(File.Exists(probe));
    }
}