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
