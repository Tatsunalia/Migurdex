namespace Migurdex.Shared.Models;

/// <summary>CLI'nin güvenli kullanıcı tanılamasına çevirebildiği kararlı sağlayıcı hata kodları.</summary>
public static class ProviderSourceProblem
{
    public const string CodeProperty = "code";

    public const string SourcesUnavailable = "provider_sources_unavailable";

    public const string TrAnimeIzleUpstreamInvalidResponse = "tranimeizle_upstream_invalid_response";

    public static bool TryGetSafeMessage(string? code, out string message)
    {
        message = code switch
        {
            SourcesUnavailable => "API sağlayıcıdan kaynak listesi alamadı.",
            TrAnimeIzleUpstreamInvalidResponse =>
                "TrAnimeIzle geçersiz bir kaynak yanıtı döndürdü; bu bölüm için kaynaklar alınamadı.",
            _ => string.Empty
        };

        return message.Length > 0;
    }
}
