using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BunnySubSync.Api;
using Dalamud.Plugin.Services;

namespace BunnySubSync.Game;

/// <summary>
/// Keeps the site's copy of each linked sub's build (rank + parts) and each
/// linked FC's sector unlocks in step with the game, through POST
/// /api/plugin/v1/build. Below the seam: it reads the snapshot service's
/// cache, never game memory.
///
/// Builds: only changes are sent. The build last sent is remembered per sub
/// in the config, together with the site id it went to, so a plugin reload or
/// a second computer doesn't resend, and a re-link sends again. Each push
/// carries the time its state was read: the site keeps the most recently
/// observed build, so a late retry can't overwrite a newer one.
///
/// Unlocks: sent once per session (then again only if they change). The site
/// keeps the union of everything sent, so a resend is harmless, and it's what
/// refills the site's copy after the FC owner resets it.
///
/// A "not_found" answer (no such sub/FC, or not writable by this account) is
/// remembered for this session only, so it isn't retried every few seconds,
/// and the next session tries again: a viewer promoted to member, or a fixed
/// link, then gets through.
///
/// Never blocks or touches voyage sync: pushes run on the thread pool,
/// failures back off (1, 2, 4 … 30 min), and a rejected token only stops this
/// until the token changes (the outbox reports that to the user).
/// </summary>
public sealed class BuildSync : IDisposable
{
    private const int CheckIntervalSeconds = 5;
    private const int BackoffCapMinutes = 30;

    private readonly SubSnapshotService snapshots;
    private readonly Configuration configuration;

    // Config writes happen on the framework thread, like the Mapping tab's.
    private readonly ConcurrentQueue<Action> onFramework = new();

    // Site sub ids already warned about being linked twice (framework thread).
    private readonly HashSet<int> warnedShared = [];

    // Per session, framework thread: the unlocks each site FC has answered,
    // and the build keys the site answered "not_found".
    private readonly Dictionary<int, string> unlocksAnswered = [];
    private readonly HashSet<string> buildsNotFound = [];

    private int pushing; // 0/1 via Interlocked
    private DateTime nextCheckUtc = DateTime.MinValue;
    private int consecutiveFailures;
    private string? rejectedToken;

    public BuildSync(SubSnapshotService snapshots, Configuration configuration)
    {
        this.snapshots = snapshots;
        this.configuration = configuration;
        Plugin.Framework.Update += OnTick;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnTick;
    }

    /// <summary>One request: everything read at the same moment for one FC.</summary>
    private sealed class Batch(DateTime observedUtc)
    {
        public DateTime ObservedUtc { get; } = observedUtc;
        public List<(SubLink Link, string Key, BuildPushSubmarine Wire)> Subs { get; } = [];
        public (string Key, BuildPushUnlocks Wire)? Unlocks { get; set; }
    }

    private void OnTick(IFramework framework)
    {
        try
        {
            while (onFramework.TryDequeue(out var apply))
                apply();

            var now = DateTime.UtcNow;
            if (now < nextCheckUtc || Volatile.Read(ref pushing) == 1)
                return;
            nextCheckUtc = now.AddSeconds(CheckIntervalSeconds);

            var token = configuration.ApiToken;
            if (string.IsNullOrWhiteSpace(token) || token == rejectedToken)
                return;

            var batches = CollectWork();
            if (batches.Count == 0 || Interlocked.CompareExchange(ref pushing, 1, 0) != 0)
                return;

            var serverUrl = configuration.ServerUrl;
            _ = Task.Run(() => PushAsync(serverUrl, token, batches));
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Build sync check failed");
        }
    }

    private List<Batch> CollectWork()
    {
        var batches = new List<Batch>();

        foreach (var fc in snapshots.SeenFcs)
        {
            // Same gating as voyage pushes: linked and enabled only.
            if (!configuration.FcMappings.TryGetValue(fc.FcId, out var mapping)
                || !mapping.Enabled
                || mapping.PlatformFcId is not { } platformFcId)
                continue;

            var byTime = new Dictionary<DateTime, Batch>();
            Batch At(DateTime observedUtc) =>
                byTime.TryGetValue(observedUtc, out var b) ? b : byTime[observedUtc] = new Batch(observedUtc);

            // Two subs in this workshop linked to one site sub (say, auto-linked
            // by a shared name): the site can't tell whose build is right and
            // rejects a push holding both, which would stall every push after
            // it, so neither is sent until one is re-linked.
            var shared = snapshots.SubsOf(fc.FcId)
                                  .Select(s => mapping.Subs.TryGetValue(s.RegisterTime, out var l) && l.Enabled
                                                   ? l.PlatformSubmarineId
                                                   : null)
                                  .Where(id => id.HasValue)
                                  .GroupBy(id => id!.Value)
                                  .Where(g => g.Count() > 1)
                                  .Select(g => g.Key)
                                  .ToHashSet();

            foreach (var snap in snapshots.SubsOf(fc.FcId))
            {
                if (!mapping.Subs.TryGetValue(snap.RegisterTime, out var link)
                    || !link.Enabled
                    || link.PlatformSubmarineId is not { } submarineId)
                    continue;

                if (shared.Contains(submarineId))
                {
                    if (warnedShared.Add(submarineId))
                        Plugin.Log.Warning(
                            $"Build sync: two subs are linked to site submarine #{submarineId}; re-link one in the Mapping tab");
                    continue;
                }

                var key = $"{submarineId}:{snap.RankId}:{snap.HullId}:{snap.SternId}:{snap.BowId}:{snap.BridgeId}";
                if (link.PushedBuild == key || buildsNotFound.Contains(key))
                    continue;

                At(snap.TakenUtc).Subs.Add((link, key, new BuildPushSubmarine
                {
                    SubmarineId = submarineId,
                    Rank = snap.RankId,
                    HullPartId = snap.HullId,
                    SternPartId = snap.SternId,
                    BowPartId = snap.BowId,
                    BridgePartId = snap.BridgeId,
                }));
            }

            if (snapshots.TryGetUnlocks(fc.FcId) is { } unlocks)
            {
                var key = $"{platformFcId}:{string.Join(',', unlocks.Unlocked)}:{string.Join(',', unlocks.Explored)}";
                if (!unlocksAnswered.TryGetValue(platformFcId, out var answered) || answered != key)
                {
                    At(unlocks.TakenUtc).Unlocks = (key, new BuildPushUnlocks
                    {
                        FreeCompanyId = platformFcId,
                        UnlockedSectorIds = unlocks.Unlocked,
                        ExploredSectorIds = unlocks.Explored,
                    });
                }
            }

            batches.AddRange(byTime.Values.OrderBy(b => b.ObservedUtc));
        }

        return batches;
    }

    private async Task PushAsync(string serverUrl, string token, List<Batch> batches)
    {
        try
        {
            using var client = new ApiClient(serverUrl, token);
            foreach (var batch in batches)
            {
                var request = new BuildPushRequest
                {
                    ObservedAt = new DateTimeOffset(batch.ObservedUtc, TimeSpan.Zero),
                    Submarines = [.. batch.Subs.Select(s => s.Wire)],
                    Unlocks = batch.Unlocks?.Wire,
                };

                var response = await client.PushBuildAsync(request).ConfigureAwait(false)
                               ?? throw new InvalidOperationException("the server's answer wasn't recognised (is the site up to date?)");
                onFramework.Enqueue(() => MarkSent(batch, response));
            }

            consecutiveFailures = 0;
        }
        catch (UnauthorizedException)
        {
            rejectedToken = token;
            Plugin.Log.Warning("Build sync stopped: the server rejected the plugin token");
        }
        catch (Exception ex)
        {
            consecutiveFailures++;
            var delayMinutes = Math.Min(1 << Math.Min(consecutiveFailures - 1, 10), BackoffCapMinutes);
            nextCheckUtc = DateTime.UtcNow.AddMinutes(delayMinutes);
            Plugin.Log.Warning(ex, $"Build sync failed (attempt {consecutiveFailures}); retrying in {delayMinutes} min");
        }
        finally
        {
            Volatile.Write(ref pushing, 0);
        }
    }

    /// <summary>Framework thread: remember what the site now has. An answered
    /// sub counts as sent, including "stale" (the site has newer), except
    /// "not_found", which is only skipped for the rest of this session.
    /// Unlocks count as answered for this session whatever the status.</summary>
    private void MarkSent(Batch batch, BuildPushResponse response)
    {
        var answered = response.Submarines!
                               .GroupBy(r => r.SubmarineId)
                               .ToDictionary(g => g.Key, g => g.First().Status);
        var updated = 0;

        foreach (var (link, key, wire) in batch.Subs)
        {
            if (!answered.TryGetValue(wire.SubmarineId, out var status))
                continue;

            if (status == "not_found")
            {
                buildsNotFound.Add(key);
                Plugin.Log.Warning(
                    $"Build sync: site submarine #{wire.SubmarineId} doesn't exist or isn't writable by this account; check the Mapping tab (retried next session)");
                continue;
            }

            link.PushedBuild = key;
            if (status == "updated")
                updated++;
        }

        var unlocksNote = "";
        if (batch.Unlocks is { } unlocks && response.Unlocks?.Status is { } unlockStatus)
        {
            unlocksAnswered[unlocks.Wire.FreeCompanyId] = unlocks.Key;
            unlocksNote = $", sector unlocks {unlockStatus}";
            if (unlockStatus == "not_found")
                Plugin.Log.Warning(
                    $"Build sync: site FC #{unlocks.Wire.FreeCompanyId} doesn't exist or isn't writable by this account; check the Mapping tab (retried next session)");
        }

        configuration.Save();
        Plugin.Log.Information(
            $"Build sync: {updated} of {batch.Subs.Count} sub build(s) updated{unlocksNote}");
    }
}
