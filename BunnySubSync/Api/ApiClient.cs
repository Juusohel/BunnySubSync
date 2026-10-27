using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace BunnySubSync.Api;

public sealed class UnauthorizedException(string message) : Exception(message);

/// <summary>Outcome of a game-data upload: 200, 403 and 409 are all normal
/// answers there, so the status comes back instead of throwing.</summary>
public sealed record GameDataUploadResult(HttpStatusCode StatusCode, GameDataUploadResponse? Body);

public sealed class ApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient client;

    public ApiClient(string serverUrl, string apiToken)
    {
        client = new HttpClient
        {
            BaseAddress = new Uri(serverUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
        client.DefaultRequestHeaders.Add("X-Plugin-Version", Plugin.PluginInterface.Manifest.AssemblyVersion.ToString());
    }

    public async Task<SyncResponse> SyncAsync()
    {
        using var response = await client.GetAsync("api/plugin/v1/sync").ConfigureAwait(false);
        await ThrowIfUnauthorizedAsync(response).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<SyncResponse>(JsonOptions).ConfigureAwait(false);
        return result ?? throw new JsonException("Empty sync response body.");
    }

    /// <param name="freeCompanyId">Scope to a specific FC (own or shared), or
    /// null for the key-holder's own default scope.</param>
    public async Task<StatsResponse> StatsAsync(int? freeCompanyId)
    {
        var path = freeCompanyId is { } id
            ? $"api/plugin/v1/stats?free_company_id={id}"
            : "api/plugin/v1/stats";
        using var response = await client.GetAsync(path).ConfigureAwait(false);
        await ThrowIfUnauthorizedAsync(response).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<StatsResponse>(JsonOptions).ConfigureAwait(false);
        return result ?? throw new JsonException("Empty stats response body.");
    }

    public async Task<PushResponse> PushAsync(List<PushRow> rows)
    {
        var request = new PushRequest { Deployments = rows };
        using var response = await client.PostAsJsonAsync("api/plugin/v1/deployments", request, JsonOptions).ConfigureAwait(false);
        await ThrowIfUnauthorizedAsync(response).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<PushResponse>(JsonOptions).ConfigureAwait(false);
        return result ?? throw new JsonException("Empty push response body.");
    }

    /// <summary>POST the game-data dump. Throws only on transport failure or a
    /// rejected token; every other answer is returned for the caller to read.</summary>
    public async Task<GameDataUploadResult> UploadGameDataAsync(GameDataUpload upload)
    {
        using var response = await client.PostAsJsonAsync("api/plugin/v1/gamedata", upload, JsonOptions).ConfigureAwait(false);
        await ThrowIfUnauthorizedAsync(response).ConfigureAwait(false);

        GameDataUploadResponse? body = null;
        try
        {
            body = await response.Content.ReadFromJsonAsync<GameDataUploadResponse>(JsonOptions).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // Not our JSON (a proxy error page, say) — the status code is what matters.
        }

        return new GameDataUploadResult(response.StatusCode, body);
    }

    /// <summary>POST changed sub builds and/or sector unlocks. Throws on a
    /// rejected token, an error status or a transport failure. Returns null
    /// when a 200 isn't the server's own answer (a site too old to have this
    /// endpoint), so nothing is marked as sent.</summary>
    public async Task<BuildPushResponse?> PushBuildAsync(BuildPushRequest request)
    {
        using var response = await client.PostAsJsonAsync("api/plugin/v1/build", request, JsonOptions).ConfigureAwait(false);
        await ThrowIfUnauthorizedAsync(response).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        try
        {
            var result = await response.Content.ReadFromJsonAsync<BuildPushResponse>(JsonOptions).ConfigureAwait(false);
            return result?.Submarines == null ? null : result;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private static async Task ThrowIfUnauthorizedAsync(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            throw new UnauthorizedException($"Server rejected the api token: {body}");
        }
    }

    public void Dispose() => client.Dispose();
}
