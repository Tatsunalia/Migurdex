using Migurdex.Cli.Tui;
using Xunit;

namespace Migurdex.Tests;

/// <summary>
///     Çoklu seçim ekranının tuş mantığı.
/// </summary>
/// <remarks>
/// ⭐ Bu dosya bir regresyon kilididir. Ekran <c>Esc geri</c> yazıyordu ama
///     işaretli bölümler yine de seçim sayılıp indirme başlatılıyordu.
///     <c>cancelled</c> bayrağı bunu engeller; testler de engellediğini
///     doğrular.
/// </remarks>
public sealed class FuzzyPromptMultiSelectTests
{
    /// <summary>
    ///     <c>HandleMultiKey</c> birden çok <c>ref</c> parametre alıyor;
    ///     test okunurluğu için durumu tek yerde tutar.
    /// </summary>
    private sealed class State
    {
        public string         Query      = string.Empty;
        public int            Cursor;
        public int            TextCursor;
        public FuzzyChoice?   Action;
        public bool           Cancelled;
        public bool           Running = true;
    }

    private static void Press(ConsoleKeyInfo key, List<FuzzyChoice> choices, State state)
        => FuzzyPrompt.HandleMultiKey(key,
                                      choices,
                                      ref state.Query,
                                      ref state.Cursor,
                                      ref state.TextCursor,
                                      searchable:   true,
                                      ref state.Action,
                                      ref state.Cancelled,
                                      ref state.Running);

    private static ConsoleKeyInfo Key(ConsoleKey key)
        => new('\0', key, false, false, false);

    private static ConsoleKeyInfo CtrlA()
        => new('a', ConsoleKey.A, false, false, true);

    private static FuzzyChoice Episode(int number)
        => new()
        {
            Display       = $"Bölüm {number:00}",
            DisplayActive = $"Bölüm {number:00}",
            Searchable    = $"ep{number:0000}",
            CanBeChecked  = true
        };

    private static FuzzyChoice Shortcut(string label)
        => new()
        {
            Display       = label,
            DisplayActive = label,
            Searchable    = label,
            IsAction      = true
        };

    // ---------- iptal ----------

    [Fact]
    public void HandleMultiKey_Escape_IsaretliSatirlarVarOlsun_CancelledYerlestirir()
    {
        var choices          = new List<FuzzyChoice> { Episode(1), Episode(2) };
        choices[0].IsChecked = true;
        var state = new State();

        Press(Key(ConsoleKey.Escape), choices, state);

        Assert.True(state.Cancelled);
        Assert.False(state.Running);
        Assert.Null(state.Action);
    }

    [Fact]
    public void HandleMultiKey_Escape_HicbiriIsaretliDegilken_Da_CancelledYerlestirir()
    {
        var choices = new List<FuzzyChoice> { Episode(1) };
        var state   = new State();

        Press(Key(ConsoleKey.Escape), choices, state);

        Assert.True(state.Cancelled);
    }

    // ---------- işaretleme ----------

    [Fact]
    public void HandleMultiKey_Space_IsaretlemeyiDegistirir()
    {
        var choices = new List<FuzzyChoice> { Episode(1), Episode(2) };
        var state   = new State { Cursor = 1 };

        Press(Key(ConsoleKey.Spacebar), choices, state);

        Assert.True(choices[1].IsChecked);
        Assert.False(choices[0].IsChecked);
        Assert.True(state.Running);
        Assert.False(state.Cancelled);
    }

    [Fact]
    public void HandleMultiKey_Space_IkinciKezIsaretiKaldirir()
    {
        var choices = new List<FuzzyChoice> { Episode(1) };
        var state   = new State();

        Press(Key(ConsoleKey.Spacebar), choices, state);
        Assert.True(choices[0].IsChecked);

        Press(Key(ConsoleKey.Spacebar), choices, state);
        Assert.False(choices[0].IsChecked);
    }

    [Fact]
    public void HandleMultiKey_Space_KisayolSatirinda_HicbirSeyYapmaz()
    {
        var choices = new List<FuzzyChoice> { Shortcut("Tüm bölümleri işaretle") };
        var state   = new State();

        Press(Key(ConsoleKey.Spacebar), choices, state);

        Assert.False(choices[0].IsChecked);
        Assert.True(state.Running);
    }

    // ---------- Ctrl+A ----------

    [Fact]
    public void HandleMultiKey_CtrlA_YalnizcaIsaretlenebilirSatirlariIsaretler()
    {
        var choices = new List<FuzzyChoice>
        {
            Episode(1),
            Shortcut("Tüm bölümleri işaretle"),
            Episode(2)
        };
        var state = new State();

        Press(CtrlA(), choices, state);

        Assert.True(choices[0].IsChecked);
        Assert.True(choices[2].IsChecked);
        Assert.False(choices[1].IsChecked);
        Assert.True(state.Running);
    }

    [Fact]
    public void HandleMultiKey_CtrlA_EkrandaGorunmeyenSatiraDokunmaz()
    {
        var gorunen = new List<FuzzyChoice> { Episode(1) };
        var gizli   = Episode(2);   // filtre dışında kaldı
        var state   = new State();

        Press(CtrlA(), gorunen, state);

        Assert.True(gorunen[0].IsChecked);
        Assert.False(gizli.IsChecked);
    }

    // ---------- Enter ----------

    [Fact]
    public void HandleMultiKey_Enter_ImlectekiSatiriSecer()
    {
        var choices = new List<FuzzyChoice> { Episode(1), Episode(2) };
        var state   = new State { Cursor = 1 };

        Press(Key(ConsoleKey.Enter), choices, state);

        Assert.NotNull(state.Action);
        Assert.Equal("ep0002", state.Action!.Searchable);
        Assert.False(state.Running);
        Assert.False(state.Cancelled);
    }

    [Fact]
    public void HandleMultiKey_Enter_SatirYoksa_Durdurmaz()
    {
        var choices = new List<FuzzyChoice>();
        var state   = new State();

        Press(Key(ConsoleKey.Enter), choices, state);

        Assert.Null(state.Action);
        Assert.True(state.Running);
    }

    // ---------- gezinme ----------

    [Fact]
    public void HandleMultiKey_Oklar_ListedeSarmalar()
    {
        var choices = new List<FuzzyChoice> { Episode(1), Episode(2) };
        var state   = new State();

        Press(Key(ConsoleKey.UpArrow), choices, state);
        Assert.Equal(1, state.Cursor);

        Press(Key(ConsoleKey.DownArrow), choices, state);
        Assert.Equal(0, state.Cursor);
    }

    [Fact]
    public void HandleMultiKey_Yazarak_SorguDegisir()
    {
        var choices = new List<FuzzyChoice> { Episode(1), Episode(2) };
        var state   = new State();

        Press(new ConsoleKeyInfo('5', ConsoleKey.D5, false, false, false), choices, state);

        Assert.Equal("5", state.Query);
        Assert.Equal(0, state.Cursor);
    }
}