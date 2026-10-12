using Migurdex.Shared.Models;

namespace Migurdex.Cli.Services.Downloads;

/// <summary>Mevcut video dosyalarını toplu bölüm listesi ve indirme kuyruğu için tanımlar.</summary>
public static class DownloadPresence
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".webm", ".mov", ".avi", ".m4v", ".ts", ".flv", ".wmv"
    };

    /// <summary>Bir anime dizinindeki video dosyalarının uzantısız adlarını bir kez okur.</summary>
    public static HashSet<string> LoadExistingStems(string? animeDirectory)
    {
        var stems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(animeDirectory) || !Directory.Exists(animeDirectory))
        {
            return stems;
        }

        foreach (var path in Directory.EnumerateFiles(animeDirectory))
        {
            var name = Path.GetFileName(path);
            if (name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var extension = Path.GetExtension(name);
            if (extension.Length > 0 && !VideoExtensions.Contains(extension))
            {
                continue;
            }

            stems.Add(Path.GetFileNameWithoutExtension(name));
        }

        return stems;
    }

    /// <summary>
    /// Sıralı bölüm listesindeki yerel videosu bulunan indeksleri döndürür.
    /// Klasörler bir kez taranır; hem MP4 hem HLS hedef yolu bütçesi dikkate alınır.
    /// </summary>
    public static HashSet<int> FindExistingEpisodeIndices(
        IDownloadPathBuilder pathBuilder,
        string? outputDirectory,
        string animeTitle,
        IReadOnlyList<Episode> episodes)
    {
        ArgumentNullException.ThrowIfNull(pathBuilder);
        ArgumentNullException.ThrowIfNull(animeTitle);
        ArgumentNullException.ThrowIfNull(episodes);

        var found = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            return found;
        }

        var pathComparer = OperatingSystem.IsWindows()
                               ? StringComparer.OrdinalIgnoreCase
                               : StringComparer.Ordinal;
        var stemsByDirectory = new Dictionary<string, HashSet<string>>(pathComparer);

        for (var index = 0; index < episodes.Count; index++)
        {
            var episode = episodes[index];
            foreach (var extension in new string?[] { null, ".mp4" })
            {
                var target = pathBuilder.Build(outputDirectory,
                                               animeTitle,
                                               episode.Title ?? string.Empty,
                                               episode.Season ?? 1,
                                               episode.Number,
                                               extension);
                if (!stemsByDirectory.TryGetValue(target.AnimeDirectory, out var stems))
                {
                    stems = LoadExistingStems(target.AnimeDirectory);
                    stemsByDirectory.Add(target.AnimeDirectory, stems);
                }

                if (stems.Contains(target.FileStem))
                {
                    found.Add(index);
                    break;
                }
            }
        }

        return found;
    }
}
