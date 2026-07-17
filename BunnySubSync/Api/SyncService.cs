using System;
using System.Threading.Tasks;

namespace BunnySubSync.Api;

/// <summary>
/// Holds the latest /sync inventory (FCs, subs, catalog, multi_fc_mode) for
/// the Mapping tab and the outbox's stale-link recovery. Refreshed explicitly
/// (Test connection, Mapping-tab Refresh) — never on a timer; the mapping
/// flow is interactive by design.
/// </summary>
public sealed class SyncService(Configuration configuration)
{
    public SyncResponse? Last { get; private set; }
    public DateTime? LastAtUtc { get; private set; }
    public bool Refreshing { get; private set; }

    /// <returns>An error string, or null on success.</returns>
    public async Task<string?> RefreshAsync()
    {
        if (Refreshing)
            return null;

        Refreshing = true;
        try
        {
            using var client = new ApiClient(configuration.ServerUrl, configuration.ApiToken);
            Last = await client.SyncAsync().ConfigureAwait(false);
            LastAtUtc = DateTime.UtcNow;
            Plugin.Log.Information(
                $"Sync refreshed: {Last.FreeCompanies.Count} FC(s), {Last.Submarines.Count} sub(s), "
                + $"multi_fc_mode={Last.MultiFcMode}");
            return null;
        }
        catch (UnauthorizedException)
        {
            return "Token rejected by the server.";
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Sync refresh failed");
            return $"Sync failed: {ex.Message}";
        }
        finally
        {
            Refreshing = false;
        }
    }
}
