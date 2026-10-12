using Migurdex.Shared.Update;

namespace Migurdex.Cli.Services;

/// <summary>Çalışan API'nin CLI ile aynı sürümde olduğunu denetler; uyarı API kullanımını engellemez.</summary>
public static class ApiVersionCheck
{
    public static bool IsMismatch(string? apiVersion, string? cliVersion)
    {
        var api = ToVersionIdentity(apiVersion);
        var cli = ToVersionIdentity(cliVersion);
        return !string.IsNullOrWhiteSpace(api)
               && !string.IsNullOrWhiteSpace(cli)
               && !api.Equals(cli, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task ReportAsync(IApiClientService api,
        string?                                                    endpoint,
        CancellationToken                                          cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(api);

        ApiHealthInfo? health;
        try
        {
            health = await api.GetApiHealthAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return;
        }

        var cliVersion = AppInfo.GetVersion();
        if (health is null || !IsMismatch(health.Version, cliVersion))
        {
            return;
        }

        var location = Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && !uri.IsDefaultPort
            ? $"Port {uri.Port}'te"
            : string.IsNullOrWhiteSpace(endpoint) ? "API adresinde" : $"'{endpoint}' adresinde";
        await Console.Error.WriteLineAsync(
            $"Uyarı: {location} farklı bir Migurdex sürümü çalışıyor (API: {AppInfo.NormalizeTag(health.Version)}, CLI: {AppInfo.NormalizeTag(cliVersion)}). "
            + "Bu API'yi kapatıp yeniden başlatman önerilir; aksi halde sonuçlar tutarsız olabilir.");
    }

    private static string ToVersionIdentity(string? version)
    {
        return AppInfo.NormalizeTag(version).Split('+')[0].Trim();
    }
}
