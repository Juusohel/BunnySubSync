using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BunnySubSync.Api;
using Lumina.Excel.Sheets;

namespace BunnySubSync.Game;

/// <summary>
/// Below-the-seam consumer: turns SubDispatched/VoyageCompleted events into
/// journal entries and wire-ready PushRows. Knows nothing about game memory —
/// only events, the journal, static sheet data, and the snapshot *cache*.
/// Capture is log-only: rows end up Queued in the journal; the outbox worker
/// will drain them (resolving the platform submarine_id from the mapping at
/// drain time — that's why BuiltRow.SubmarineId stays 0 here).
/// </summary>
public sealed class VoyageAssembler : IDisposable
{
    private readonly VoyageEvents events;
    private readonly VoyageJournal journal;
    private readonly SubSnapshotService snapshots;
    private readonly Configuration configuration;

    public VoyageAssembler(
        VoyageEvents events, VoyageJournal journal, SubSnapshotService snapshots, Configuration configuration)
    {
        this.events = events;
        this.journal = journal;
        this.snapshots = snapshots;
        this.configuration = configuration;
        events.Dispatched += OnDispatched;
        events.Completed += OnCompleted;
    }

    public void Dispose()
    {
        events.Dispatched -= OnDispatched;
        events.Completed -= OnCompleted;
    }

    private void OnDispatched(SubDispatched ev)
    {
        var entry = journal.AppendDispatch(ev);
        var durationMinutes = RouteDurationMinutes(ev.ScheduledReturnUnix, ev.DispatchedUtc);

        if (configuration.PushOnDispatch)
        {
            // Dispatch stage: an incomplete row (collected_at null, no loot)
            // with the SAME external_voyage_id the collection row will carry —
            // the server completes it in place. The outbox drains it once the
            // mapping resolves; if it never goes out, the collection push
            // alone creates the full row (no coupling).
            var dispatchRow = BuildRow(
                ev.FcId, ev.SubRegisterTime, ev.SubName,
                deployedAtUtc: ev.DispatchedUtc,
                collectedAtUtc: null,
                durationMinutes: durationMinutes,
                sectors: ev.Sectors,
                loot: [],
                notes: ev.Simulated ? "[SIM] simulated voyage" : null);
            journal.Update(entry, e => e.DispatchRow = dispatchRow);
        }

        Plugin.Log.Information(
            $"Dispatch detected{SimTag(ev.Simulated)}: '{ev.SubName}' "
            + $"route {SectorMath.SectorsToRoute(ev.Sectors)}, "
            + $"scheduled return {DateTimeOffset.FromUnixTimeSeconds(ev.ScheduledReturnUnix).UtcDateTime:u}, "
            + $"duration {durationMinutes} min — journal entry {entry.Id}");
    }

    private void OnCompleted(VoyageCompleted ev)
    {
        var entry = journal.FindPendingCollection(ev.FcId, ev.SubRegisterTime);

        // Stale-join guard: if the plugin was off across a collect+redispatch,
        // the journal still holds voyage 1's pending entry while this result
        // belongs to voyage 2 (whose dispatch we never saw — baseline rule).
        // Joining would stamp voyage 2's loot with voyage 1's times. The
        // last snapshot's ReturnTime is voyage 2's schedule — a mismatch with
        // the entry's schedule exposes the staleness.
        if (entry != null && !ev.Simulated)
        {
            var lastSnap = snapshots.TryGetLastSnapshot(ev.FcId, ev.SubRegisterTime);
            if (lastSnap is { ReturnTime: not 0 } && lastSnap.ReturnTime != entry.ScheduledReturnUnix)
            {
                Plugin.Log.Warning(
                    $"Pending journal entry for '{entry.SubName}' is from an earlier voyage "
                    + $"(scheduled return {entry.ScheduledReturnUnix} vs observed {lastSnap.ReturnTime}) "
                    + "— its collection was never observed; treating this result as a missed dispatch");
                journal.Update(entry, e =>
                {
                    e.State = OutboxState.Failed;
                    e.FailureMessage = "collection never observed — voyage result lost";
                });
                entry = null;
            }
        }

        if (entry == null)
        {
            OnCompletedWithoutDispatch(ev);
            return;
        }

        if (!entry.Sectors.AsSpan().SequenceEqual(ev.Sectors))
        {
            // The result packet is ground truth; the dispatch-time read
            // losing a race is conceivable, a wrong packet isn't.
            Plugin.Log.Warning(
                $"Route mismatch for '{entry.SubName}': dispatch saw "
                + $"{SectorMath.SectorsToRoute(entry.Sectors)}, result says "
                + $"{SectorMath.SectorsToRoute(ev.Sectors)} — using the result");
        }

        var row = BuildRow(
            ev.FcId,
            ev.SubRegisterTime,
            entry.SubName,
            deployedAtUtc: entry.DispatchedUtc,
            collectedAtUtc: ev.CollectedUtc,
            durationMinutes: RouteDurationMinutes(entry.ScheduledReturnUnix, entry.DispatchedUtc),
            sectors: ev.Sectors,
            loot: ev.Loot,
            notes: ev.Simulated ? "[SIM] simulated voyage" : null);

        journal.Update(entry, e =>
        {
            e.State = OutboxState.Queued;
            e.CollectedUtc = ev.CollectedUtc;
            e.BuiltRow = row;
        });

        Plugin.Log.Information(
            $"Voyage assembled{SimTag(ev.Simulated)} (queued for push): "
            + JsonSerializer.Serialize(row));
    }

    private void OnCompletedWithoutDispatch(VoyageCompleted ev)
    {
        // Plugin installed mid-voyage, or the journal was lost. Never
        // silently assemble with guessed times — record it Failed and let the
        // user explicitly Estimate & queue from the Log tab (a wrong 3-day
        // voyage skews gil/hour more than a missing one).
        var lastSnap = snapshots.TryGetLastSnapshot(ev.FcId, ev.SubRegisterTime);
        var entry = journal.AppendMissedDispatch(
            ev,
            lastKnownReturnUnix: lastSnap?.ReturnTime ?? 0,
            subName: ev.SubName);

        Plugin.Log.Warning(
            $"Voyage completed with no journaled dispatch{SimTag(ev.Simulated)}: "
            + $"'{ev.SubName}' route {SectorMath.SectorsToRoute(ev.Sectors)} — "
            + $"flagged in the journal ({entry.Id}); use 'Estimate & queue' in the Log tab to push it");
    }

    /// <summary>
    /// Explicit user action from the Log tab for a journal-missed voyage:
    /// estimate deployed_at from the scheduled return (or, failing that, the
    /// collection time) minus the route duration computed from the sub's
    /// last-seen build. Simulated entries have no build to estimate from, so
    /// they may pass a fallback duration (the Simulator tab's input) instead —
    /// real entries never take that path. Returns an error string, or null.
    /// </summary>
    public string? EstimateAndQueue(JournalEntry entry, int simFallbackDurationMinutes = 0)
    {
        if (entry.State != OutboxState.Failed || entry.PendingLoot == null || entry.CollectedUtc == null)
            return "This entry has nothing to estimate.";

        uint durationSeconds;
        var snap = snapshots.TryGetLastSnapshot(entry.FcId, entry.SubRegisterTime);
        if (snap != null)
        {
            durationSeconds = SectorMath.EstimateDurationSeconds(
                entry.Sectors, snap.RankId, snap.HullId, snap.SternId, snap.BowId, snap.BridgeId);
        }
        else if (entry.Simulated && simFallbackDurationMinutes > 0)
        {
            durationSeconds = (uint)simFallbackDurationMinutes * 60;
        }
        else
        {
            return "No snapshot of this submarine yet — visit the workshop once, then retry.";
        }

        if (durationSeconds == 0)
            return "Could not estimate a duration for this route.";

        // Scheduled return is the better anchor when a snapshot caught the
        // sub mid-voyage; otherwise anchor on the observed collection.
        var anchorUtc = entry.ScheduledReturnUnix != 0
            ? DateTimeOffset.FromUnixTimeSeconds(entry.ScheduledReturnUnix).UtcDateTime
            : entry.CollectedUtc.Value;
        var deployedAtUtc = anchorUtc.AddSeconds(-durationSeconds);

        var notes = "[EST] deployed_at estimated — plugin missed the dispatch";
        if (entry.Simulated)
            notes = "[SIM] " + notes;

        var row = BuildRow(
            entry.FcId,
            entry.SubRegisterTime,
            entry.SubName,
            deployedAtUtc,
            entry.CollectedUtc.Value,
            durationMinutes: (int)(durationSeconds / 60),
            sectors: entry.Sectors,
            loot: entry.PendingLoot,
            notes: notes,
            deployedAtEstimated: true);

        journal.Update(entry, e =>
        {
            e.State = OutboxState.Queued;
            e.DispatchedUtc = deployedAtUtc;
            e.FailureMessage = null;
            e.BuiltRow = row;
        });

        Plugin.Log.Information(
            $"Estimated voyage queued for push: {JsonSerializer.Serialize(row)}");
        return null;
    }

    private static PushRow BuildRow(
        ulong fcId,
        uint subRegisterTime,
        string subName,
        DateTime deployedAtUtc,
        DateTime? collectedAtUtc,
        int durationMinutes,
        uint[] sectors,
        List<LootLine> loot,
        string? notes,
        bool deployedAtEstimated = false)
    {
        var deployedUnix = new DateTimeOffset(deployedAtUtc, TimeSpan.Zero).ToUnixTimeSeconds();

        return new PushRow
        {
            // Idempotency key: stable across the dispatch push and the
            // collection push for the same voyage.
            ExternalVoyageId = $"{fcId:x}-{subRegisterTime}-{deployedUnix}",
            SubmarineId = 0, // resolved from the mapping by the outbox
            ClientSubmarineName = subName,
            Route = SectorMath.SectorsToRoute(sectors),
            RouteDurationMinutes = durationMinutes,
            DeployedAt = new DateTimeOffset(deployedAtUtc, TimeSpan.Zero),
            DeployedAtEstimated = deployedAtEstimated,
            CollectedAt = collectedAtUtc is { } c ? new DateTimeOffset(c, TimeSpan.Zero) : null,
            CeruleumTanks = SectorMath.TankCost(sectors),
            Notes = notes,
            Loot = AggregateLoot(loot),
        };
    }

    /// <summary>Aggregate loot lines by (item, hq), summing quantities.</summary>
    private static List<PushLoot> AggregateLoot(List<LootLine> loot)
    {
        var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();
        return loot
               .Where(l => l.FfxivItemId != 0 && l.Quantity > 0)
               .GroupBy(l => (l.FfxivItemId, l.Hq))
               .Select(g => new PushLoot
               {
                   FfxivItemId = g.Key.FfxivItemId,
                   // Name fallback helps the server's unmatched-item report.
                   ItemName = itemSheet.TryGetRow(g.Key.FfxivItemId, out var item)
                       ? item.Name.ExtractText()
                       : null,
                   Quantity = g.Sum(l => l.Quantity),
                   Hq = g.Key.Hq,
               })
               .ToList();
    }

    private static int RouteDurationMinutes(uint scheduledReturnUnix, DateTime dispatchedUtc)
    {
        var dispatchedUnix = new DateTimeOffset(dispatchedUtc, TimeSpan.Zero).ToUnixTimeSeconds();
        return (int)Math.Max(0, (scheduledReturnUnix - dispatchedUnix) / 60);
    }

    private static string SimTag(bool simulated) => simulated ? " [SIM]" : "";
}
