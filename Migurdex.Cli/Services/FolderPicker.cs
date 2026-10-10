using Migurdex.Cli.Tui;
using Spectre.Console;

namespace Migurdex.Cli.Services;

/// <summary>
///     Ayarlarda indirme dizini seçmek için TUI içi klasör gezgini.
/// </summary>
/// <remarks>
/// ⭐ <b>Var olan yazma yolu KORUNUR.</b> Gezgin açıldığında listenin en
///     altında <c>Yolu elle yaz</c> satırı bulunur; girilirse eski
///     yazma kutusu aynen açılır. Böylece gezgin yolu yazan kullanıcının
///     işini kesmez — yalnızca kolaylaştırır.
/// </remarks>
public static class FolderPicker
{
    /// <summary>Listede bu satır görünürse yazma kutusuna geçilir.</summary>
    internal const string ManuelYazSearchable = "Yolu elle yaz";

    /// <summary>Bu satır o anki dizini seçer.</summary>
    private const string BuDiziniSecSearchable = "Bu dizini seç";

    /// <summary>Üst dizine çıkar.</summary>
    private const string UstDizinSearchable = ".. (üst dizin)";

    /// <summary>
    ///     Klasör seçtirir.
    /// </summary>
    /// <param name="baslangicDizini">
    ///     Gezginin açılacağı dizin. Ayarlardaki mevcut indirme dizinidir.
    /// </param>
    /// <returns>
    ///     Seçilen tam yol. Kullanıcı vazgeçerse <c>null</c>.
    /// </returns>
    /// <remarks>
    /// ⭐ Dizin listelenemezse (izin yok, ağ yolu kesilmiş) hata mesajı
    ///     basılıp gezgin açılmaz; çağıran yazma kutusuna düşer.
    /// </remarks>
    public static string? Select(string baslangicDizini, string baslik)
    {
        var baslangic = Normalize(baslangicDizini);

        if (!TryListele(baslangic, out var ilkSatirlar, out var hata))
        {
            Toast.Show($"[red]{Markup.Escape(hata)}[/]");
            return null;
        }

        var dizin = baslangic;

        while (true)
        {
            if (!TryListele(dizin, out var satirlar, out hata))
            {
                Toast.Show($"[red]{Markup.Escape(hata)}[/]");
                return null;
            }

            var secenekler = new List<FuzzyChoice>
            {
                new()
                {
                    // ⭐ Sıralamada üstte: gezgin "şu anki klasörü seç" diye
                    //   kullanıcıyı bekletmesin.
                    Display       = "[bold green]Bu dizini seç[/]",
                    DisplayActive = "[bold white on darkgreen] Bu dizini seç [/]",
                    Searchable    = BuDiziniSecSearchable,
                    IsAction      = true
                }
            };

            var ustDizin = UstDizinOlabilecek(dizin);
            if (ustDizin is not null)
            {
                secenekler.Add(new FuzzyChoice
                {
                    Display       = "[grey].. (üst dizin)[/]",
                    DisplayActive = "[bold white] .. (üst dizin) [/]",
                    Searchable    = UstDizinSearchable,
                    IsAction      = true
                });
            }

            foreach (var satir in satirlar)
            {
                secenekler.Add(new FuzzyChoice
                {
                    Display       = $"[grey]{Markup.Escape(satir)}[/]",
                    DisplayActive = $"[bold white]{Markup.Escape(satir)}[/]",
                    Searchable    = satir
                });
            }

            // ⭐ Yazma yolu burada durur, gezgin açıldığında elde kalmaz.
            secenekler.Add(new FuzzyChoice
            {
                Display       = "[cyan]Yolu elle yaz[/]",
                DisplayActive = "[bold white on grey23] Yolu elle yaz [/]",
                Searchable    = ManuelYazSearchable,
                IsAction      = true
            });

            secenekler.Add(TuiHelpers.Back());

            var secim = FuzzyPrompt.Show(
                baslik,
                secenekler,
                headerLines: [$"[grey]{Markup.Escape(dizin)}[/]"],
                searchable: false,
                footerHelp: "↑↓ gez • Enter gir • Esc geri");

            if (secim is null)
            {
                return null;
            }

            switch (secim.Searchable)
            {
                case BuDiziniSecSearchable:
                    return dizin;

                case UstDizinSearchable:
                    if (ustDizin is not null)
                    {
                        dizin = ustDizin;
                    }

                    break;

                case ManuelYazSearchable:
                    return null;   // çağıran yazma kutusuna düşer

                case "Geri":
                    return null;

                default:
                    var alt = Path.Combine(dizin, secim.Searchable);
                    if (Directory.Exists(alt))
                    {
                        dizin = alt;
                    }

                    break;
            }
        }
    }

    /// <summary>
    ///     Dizin içeriğini alt klasörler olarak listeler.
    /// </summary>
    /// <remarks>
    /// ⭐ Gizli dosyalar (<c>.</c> ile başlayan) gizlenir; bunlar kullanıcıyı
    ///     değil, <c>.</c> ve <c>..</c> gürültüsünü ekrana koyar.
    /// </remarks>
    private static bool TryListele(string dizin, out List<string> altKlasorler, out string hata)
    {
        altKlasorler = [];
        hata         = string.Empty;

        try
        {
            // ⭐ Where(... !IsNullOrEmpty) null'i eler ama derleyici bunu
            //   bilemez; Where'nin sonrasi Where(ad => ad!) ile daraltilir.
            altKlasorler = [.. Directory.EnumerateDirectories(dizin)
                                      .Select(Path.GetFileName)
                                      .Where(ad => !string.IsNullOrEmpty(ad))
                                      .Select(ad => ad!)
                                      .Where(ad => !ad.StartsWith('.'))
                                      .OrderBy(ad => ad, StringComparer.CurrentCultureIgnoreCase)];

            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException
                                       or IOException
                                       or ArgumentException)
        {
            hata = $"Bu dizin okunamadı: {ex.Message}";
            return false;
        }
    }

    /// <summary>Sürücü köküne çıkılıp çıkılamayacağını döndürür.</summary>
    private static string? UstDizinOlabilecek(string dizin)
    {
        try
        {
            var ust = Directory.GetParent(dizin)?.FullName;
            return string.IsNullOrEmpty(ust) ? null : ust;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    ///     Başlangıç dizinini güvenli hale getirir.
    /// </summary>
    /// <remarks>
    /// ⭐ Var olmayan ya da okunamayan bir dizin verilirse kullanıcının
    ///     ana dizinine düşülür; gezgin boş ekranla açılmaz.
    /// </remarks>
    internal static string Normalize(string? dizin)
    {
        if (string.IsNullOrWhiteSpace(dizin))
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        try
        {
            var tam = Path.GetFullPath(Environment.ExpandEnvironmentVariables(dizin.Trim()));
            return Directory.Exists(tam)
                       ? tam
                       : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        catch (Exception)
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
    }
}