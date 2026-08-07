using System.Text.Json;

namespace ResourceMonitor.Updates;

public sealed record UpdateCheckResult(bool IsUpdateAvailable, string? LatestVersion, string? ReleaseUrl);

// Verificação de versão nova no GitHub — mesmo espírito de HardwareInfoReader (lê estado
// externo, não é regra de negócio do monitoramento). Silenciosa em qualquer erro (rede
// indisponível, timeout, rate limit, JSON inesperado): nunca deve atrapalhar o app, só
// deixar de mostrar o aviso dessa vez.
public static class UpdateChecker
{
    private const string ReleasesApiUrl = "https://api.github.com/repos/antonio-ferrer/ResourceMonitor/releases/latest";
    private static readonly UpdateCheckResult NoUpdate = new(false, null, null);

    public static async Task<UpdateCheckResult> CheckAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ResourceMonitor-UpdateCheck");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            using var response = await client.GetAsync(ReleasesApiUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return NoUpdate;
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            var tagName = document.RootElement.GetProperty("tag_name").GetString();
            var releaseUrl = document.RootElement.TryGetProperty("html_url", out var urlProperty)
                ? urlProperty.GetString()
                : null;

            if (tagName is null || !TryParseVersion(tagName, out var latest) || !TryParseVersion(currentVersion, out var current))
            {
                return NoUpdate;
            }

            return latest > current ? new UpdateCheckResult(true, tagName, releaseUrl) : NoUpdate;
        }
        catch
        {
            return NoUpdate;
        }
    }

    private static bool TryParseVersion(string text, out Version version) =>
        Version.TryParse(text.TrimStart('v', 'V'), out version!);
}
