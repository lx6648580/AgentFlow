using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace AgentFlow.Editor.POC;

internal static class HostApi
{
    public static Uri? BaseAddress(string? catalogUrl) =>
        Uri.TryCreate(catalogUrl, UriKind.Absolute, out var catalog) ? new(catalog, "../") : null;

    public static bool IsNetworkError(Exception error) =>
        error is HttpRequestException or OperationCanceledException or TimeoutException or JsonException;

    public static async Task<(string Code, string? Node)> ReadErrorAsync(
        HttpResponseMessage response, CancellationToken token, string fallback, string notFound)
    {
        if (response.StatusCode == HttpStatusCode.NotFound)
            return (notFound, null);
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ApiError>(cancellationToken: token);
            return (UiText.Keys.Contains(error?.Code) ? error!.Code : fallback, error?.NodeId);
        }
        catch (JsonException) { return (fallback, null); }
    }

    private sealed record ApiError(string Code, string? NodeId);
}
