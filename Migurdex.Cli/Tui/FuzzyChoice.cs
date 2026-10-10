namespace Migurdex.Cli.Tui;

public class FuzzyChoice
{
    public string  Display         { get; set; } = string.Empty;
    public string  DisplayActive   { get; set; } = string.Empty;
    public string  Searchable      { get; set; } = string.Empty;
    public object? AssociatedValue { get; set; }

    public bool IsAction { get; set; }

    /// <summary>
    /// Bu satır çoklu seçimde işaretlenebilir mi?
    /// </summary>
    /// <remarks>
    /// ⭐ <c>false</c> ise satırda kutu çizilmez ve <c>Space</c> hiçbir şey
    /// yapmaz; yalnız <c>Enter</c> seçer. Kısayol satırları
    /// ("Tüm bölümlerini işaretle") böyle tanımlanır.
    /// </remarks>
    public bool CanBeChecked { get; set; }

    /// <summary>Çoklu seçimde bu satır işaretli mi?</summary>
    public bool IsChecked { get; set; }
}
