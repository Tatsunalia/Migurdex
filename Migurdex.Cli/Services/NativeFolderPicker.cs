using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.Versioning;

namespace Migurdex.Cli.Services;

/// <summary>
///     Best-effort native folder dialog on Windows; other platforms use the TUI picker.
/// </summary>
public static class NativeFolderPicker
{
    public static string? PencereIleSec(string baslangicDizini)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return WindowsPencere.Acir(baslangicDizini);
        }
        catch (Exception)
        {
            // WinForms may be unavailable (for example, on a server install).
            // The caller then falls back to the cross-platform TUI picker.
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static class WindowsPencere
    {
        public static string? Acir(string baslangicDizini)
        {
            // Load WinForms dynamically so the CLI has no Windows-only build dependency.
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyName(
                new AssemblyName("System.Windows.Forms"));
            var dialogType = assembly.GetType("System.Windows.Forms.FolderBrowserDialog");
            if (dialogType is null)
            {
                return null;
            }

            var initialPath = Directory.Exists(baslangicDizini)
                                  ? baslangicDizini
                                  : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var dialog = Activator.CreateInstance(dialogType);
            if (dialog is null)
            {
                return null;
            }

            try
            {
                var dialogObjectType = dialog.GetType();
                dialogObjectType.GetProperty("SelectedPath")?.SetValue(dialog, initialPath);
                dialogObjectType.GetProperty("UseDescriptionForTitle")?.SetValue(dialog, true);
                dialogObjectType.GetProperty("Description")?.SetValue(dialog, "İndirme dizinini seç");

                var resultType = assembly.GetType("System.Windows.Forms.DialogResult");
                var showDialog = dialogObjectType.GetMethod("ShowDialog", Type.EmptyTypes);
                if (resultType is null || showDialog is null)
                {
                    return null;
                }

                var result = showDialog.Invoke(dialog, null);
                var selectedPath = dialogObjectType.GetProperty("SelectedPath")?.GetValue(dialog) as string;
                var okValue = Enum.ToObject(resultType, 1); // DialogResult.OK

                return Equals(result, okValue) && !string.IsNullOrWhiteSpace(selectedPath)
                           ? selectedPath
                           : null;
            }
            finally
            {
                (dialog as IDisposable)?.Dispose();
            }
        }
    }
}
