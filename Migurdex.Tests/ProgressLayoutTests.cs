using System.Text;
using Migurdex.Cli.Tui;
using Spectre.Console;
using Xunit;

namespace Migurdex.Tests;

/// <summary>
///     İndirme çubuğunun terminal genişliğine uyum sağlaması.
/// </summary>
/// <remarks>
/// ⭐ Kullanıcı "terminali uzatıp kısaltmamıza ayak uydurarak çalışsa"
///     dedi. Bu ölçüm, farklı genişliklerde <b>gerçek</b> çıktının neye
///     benzediğini gösterir. Ölçmeden "olur" demek yanlış olur.
///     <para>
///     Ölçüm aracı: gerçek <c>AnsiConsole.Progress</c> çağrılır ve çıktı
///     <c>StringWriter</c>'a yazılır. Canlı ekranı beklemeden son kare
///     alınabildiği için "canlı çizim test içinde üretilemiyor" itirazı
///     burada çözülmüştür.
///     </para>
/// </remarks>
public sealed class ProgressLayoutTests
{
    private readonly List<string> _ciktiler = [];
    private const string UzunAciklama =
        "BOCCHI THE ROCK! S1E05 (5/76) - Video indiriliyor - 244.67 MiB / 388.17 MiB - 89.32 MiB/s - ETA 00:01 - Esc: iptal";

    [Theory]
    [InlineData(60)]
    [InlineData(80)]
    [InlineData(100)]
    [InlineData(140)]
    [InlineData(200)]
    public void Cubuk_GenisliklereGoreCiz(int genislik)
    {
        var cikti = Izle(genislik, UzunAciklama);

        _ciktiler.Add($"### genislik = {genislik}");
        foreach (var satir in cikti.Split('\n').Where(l => l.Trim().Length > 0))
        {
            _ciktiler.Add("|" + satir.TrimEnd() + "|");
        }

        // ⭐ ASIL OLÇUM: cıktı terminale SIĞMIYORSA bu bir hatadır.
        //   Spectre taşma olursa satırları kaydırır veya keser.
        var tasan = cikti.Split('\n').Where(l => l.Trim().Length > genislik).ToList();
        Assert.Empty(tasan);
    }

    [Fact]
    public void DarTerminalde_Aciklama_Kirpiliyor_Mu()
    {
        var dar = Izle(60, UzunAciklama);

        _ciktiler.Add("### dar (60) tam cikti:");
        foreach (var l in dar.Split('\n').Where(x => x.Trim().Length > 0))
        {
            _ciktiler.Add("|" + l.TrimEnd() + "|");
        }

        // Dar terminalde ya kırpılmalı ya alt satıra sarılmalı — ikisi de
        // kabul. Ama asıl şey çubuğun görünür kalması.
        Assert.Contains("%", dar);
    }

    [Fact]
    public void GenisTerminalde_ButunAciklama_Gorunuyor_Mu()
    {
        var genis = Izle(200, UzunAciklama);

        _ciktiler.Add("### genis (200) tam cikti:");
        foreach (var l in genis.Split('\n').Where(x => x.Trim().Length > 0))
        {
            _ciktiler.Add("|" + l.TrimEnd() + "|");
        }

        Assert.Contains("Esc: iptal", genis);
    }

    /// <summary>
    ///     Spectre'ın canlı ekranını koda üretir.
    /// </summary>
    /// <remarks>
    /// ⭐ Ölçüm aracı: gerçek <c>AnsiConsole.Progress</c> çağrılır, çıktı
    ///     hafızaya alınır. Bu, "canlı çizim test içinde üretilemiyor"
    ///     itirazını doğrudan çözer — canlı ekranı beklemeden son kareyi
    ///     yazdırıyoruz.
    /// </remarks>
    private static string Izle(int genislik, string aciklama)
    {
        var sb      = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
                                         {
                                             Ansi = AnsiSupport.No,
                                             Out  = new AnsiConsoleOutput(sb)
                                         });
        console.Profile.Width = genislik;

        console.Progress()
               .AutoClear(false)
               .HideCompleted(false)
               .Columns(new PercentageColumn(),
                        new ProgressBarColumn(),
                        new TaskDescriptionColumn(),
                        new SpinnerColumn())
               .Start(ctx =>
               {
                   var t = ctx.AddTask("hazir", maxValue: 100);
                   t.Value        = 63;
                   t.Description = aciklama;
                   ctx.Refresh();
               });

        return sb.ToString();
    }

    [Fact]
    public void TumGenisliklerinCiktisini_DosyayaYaz()
    {
        // ⭐ Olcum ciktisi konsolda kaybolur; dosyaya yazilir.
        //   Sonra dosya OKUNUR - "canli cizim uretilemiyor" sorusu boyle
        //   cozulur.
        var yol = Path.Combine(Path.GetTempPath(), "progress-layout-olcum.txt");
        var sb   = new StringBuilder();

        foreach (var g in new[] { 60, 80, 100, 120, 140, 180, 220 })
        {
            sb.AppendLine("### genislik = " + g);
            foreach (var satir in Izle(g, UzunAciklama).Split('\n').Where(l => l.Trim().Length > 0))
            {
                sb.AppendLine("|" + satir.TrimEnd() + "|");
            }

            sb.AppendLine();
        }

        File.WriteAllText(yol, sb.ToString());
        Assert.True(File.Exists(yol));
    }
}