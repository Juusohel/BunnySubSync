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
        // A sub sent out while this player was away from the workshop (by an
        // FC mate, or from another computer) is first seen late, and the time
        // it was seen would make its route too short. Such a dispatch is timed
        // from the game's schedule instead, like a missed one.
        var estimatedUtc = EstimateLateDispatch(ev.ScheduledReturnUnix, ev.DispatchedUtc, ev.Sectors, ev.Build);
        var entry = journal.AppendDispatch(ev, estimatedUtc);
        var durationMinutes = RouteDurationMinutes(entry.ScheduledReturnUnix, entry.DispatchedUtc);

        if (configuration.PushOnDispatch)
        {
            // Dispatch stage: an incomplete row (collected_at null, no loot)
            // with the SAME external_voyage_id the collection row will carry —
            // the server completes it in place. The outbox drains it once the
            // mapping resolves; if it never goes out, the collection push
            // alone creates the full row (no coupling).
            var dispatchRow = BuildRow(
                ev.FcId, ev.SubRegisterTime, ev.SubName,
                deployedAtUtc: entry.DispatchedUtc,
                collectedAtUtc: null,
                durationMinutes: durationMinutes,
                sectors: ev.Sectors,
                build: ev.Build,
                loot: [],
                notes: RowNotes(ev.Simulated, entry.DeployedAtEstimated),
                deployedAtEstimated: entry.DeployedAtEstimated);
            journal.Update(entry, e => e.DispatchRow = dispatchRow);
        }

        if (estimatedUtc != null)
        {
            Plugin.Log.Information(
                $"Dispatch of '{ev.SubName}' seen late (at {ev.DispatchedUtc:u}) — "
                + $"dispatch time computed back from the scheduled return: {estimatedUtc:u}");
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

        // Stale-join guard: if this machine never saw this voyage's dispatch
        // (plugin off across a collect+redispatch, or the voyage was
        // dispatched from another device — multi-PC / shared-FC flows), the
        // journal may still hold an *earlier* voyage's pending entry. Joining
        // would stamp this voyage's loot with the old entry's times and
        // external_voyage_id (the server would then call it a duplicate and
        // drop the loot). Compare against the last *non-zero* observed
        // ReturnTime: the live snapshot is useless here — collection zeroes
        // it a tick before the result packet lands.
        if (entry != null && !ev.Simulated)
        {
            var lastReturn = snapshots.LastVoyageReturnUnix(ev.FcId, ev.SubRegisterTime);
            if (lastReturn != 0 && lastReturn != entry.ScheduledReturnUnix)
            {
                Plugin.Log.Warning(
                    $"Pending journal entry for '{entry.SubName}' is from an earlier voyage "
                    + $"(scheduled return {entry.ScheduledReturnUnix} vs observed {lastReturn}) "
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

        // An entry an older version journaled can still hold the time a late
        // sighting was seen at. Time it from the schedule here instead, unless
        // its dispatch row already reached the server: the server holds the
        // voyage under that row's id, and a new id would split it in two.
        if (!entry.DeployedAtEstimated
            && !entry.DispatchPushed
            && EstimateLateDispatch(entry.ScheduledReturnUnix, entry.DispatchedUtc, ev.Sectors, entry.Build)
                is { } estimatedUtc)
        {
            Plugin.Log.Information(
                $"Dispatch of '{entry.SubName}' was seen late (at {entry.DispatchedUtc:u}) — "
                + $"dispatch time computed back from the scheduled return: {estimatedUtc:u}");
            journal.Update(entry, e =>
            {
                e.DispatchedUtc = estimatedUtc;
                e.DeployedAtEstimated = true;
            });
        }

        var row = BuildRow(
            ev.FcId,
            ev.SubRegisterTime,
            entry.SubName,
            deployedAtUtc: entry.DispatchedUtc,
            collectedAtUtc: ev.CollectedUtc,
            durationMinutes: RouteDurationMinutes(entry.ScheduledReturnUnix, entry.DispatchedUtc),
            sectors: ev.Sectors,
            build: entry.Build,
            loot: ev.Loot,
            notes: RowNotes(ev.Simulated, entry.DeployedAtEstimated),
            deployedAtEstimated: entry.DeployedAtEstimated,
            sectorResults: ev.SectorResults);

        journal.Update(entry, e =>
        {
            e.State = OutboxState.Queued;
            e.CollectedUtc = ev.CollectedUtc;
            e.SectorResults = ev.SectorResults;
            e.BuiltRow = row;
        });

        Plugin.Log.Information(
            $"Voyage assembled{SimTag(ev.Simulated)} (queued for push): "
            + JsonSerializer.Serialize(row));
    }

    private void OnCompletedWithoutDispatch(VoyageCompleted ev)
    {
        // Dispatched from another device (multi-PC / shared-FC), plugin
        // installed mid-voyage, or the journal was lost. Anchor on the last
        // non-zero observed ReturnTime — the live snapshot's is already 0 by
        // the time the result packet lands.
        // The same sighting also holds the build this voyage was dispatched
        // with (simulated voyages don't borrow a real sub's).
        var lastReturn = snapshots.LastVoyageReturnUnix(ev.FcId, ev.SubRegisterTime);
        var entry = journal.AppendMissedDispatch(
            ev,
            lastKnownReturnUnix: lastReturn,
            lastKnownBuild: ev.Simulated ? null : snapshots.LastVoyageBuild(ev.FcId, ev.SubRegisterTime),
            subName: ev.SubName);

        // Schedule-anchored auto-recovery: with the voyage's actual scheduled
        // return in hand, deployed_at = return − estimated duration is derived
        // from game truth, not guessed — safe to queue without user action
        // (this is what makes dispatch-on-one-device, collect-on-another work
        // unattended). The row still goes out flagged deployed_at_estimated,
        // so when another device pushed the real dispatch, the server keeps
        // that row's own times (ingest enrich rule). Without the anchor we
        // keep the old behavior: fail loudly, never push guessed times
        // silently (a wrong 3-day voyage skews gil/hour more than a missing
        // one).
        if (lastReturn != 0 && !ev.Simulated)
        {
            var error = EstimateAndQueue(entry);
            if (error == null)
            {
                Plugin.Log.Information(
                    $"Voyage completed with no journaled dispatch: '{ev.SubName}' "
                    + $"route {SectorMath.SectorsToRoute(ev.Sectors)} — auto-queued from the "
                    + $"observed schedule (journal entry {entry.Id})");
                return;
            }

            Plugin.Log.Warning($"Auto-estimate failed for '{ev.SubName}': {error}");
        }

        Plugin.Log.Warning(
            $"Voyage completed with no journaled dispatch{SimTag(ev.Simulated)}: "
            + $"'{ev.SubName}' route {SectorMath.SectorsToRoute(ev.Sectors)} — "
            + $"flagged in the journal ({entry.Id}); use 'Estimate & queue' in the Log tab to push it");
    }

    /// <summary>
    /// Queue a journal-missed voyage: estimate deployed_at from the scheduled
    /// return (or, failing that, the collection time) minus the route duration
    /// computed from the sub's last-seen build. Called automatically by
    /// OnCompletedWithoutDispatch when the observed schedule anchors the
    /// estimate, and explicitly via the Log tab's "Estimate & queue" button
    /// otherwise. Simulated entries have no build to estimate from, so they
    /// may pass a fallback duration (the Simulator tab's input) instead —
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
            build: entry.Build,
            loot: entry.PendingLoot,
            notes: notes,
            deployedAtEstimated: true,
            sectorResults: entry.SectorResults);

        journal.Update(entry, e =>
        {
            e.State = OutboxState.Queued;
            e.DispatchedUtc = deployedAtUtc;
            e.DeployedAtEstimated = true;
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
        SubBuild? build,
        List<LootLine> loot,
        string? notes,
        bool deployedAtEstimated = false,
        List<SectorResult>? sectorResults = null)
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
            // The ids the route letters above are derived from, and the
            // dispatch build: the site can't work either out from the route.
            SectorIds = sectors.Length > 0 ? sectors : null,
            Build = build is { } b
                ? new PushBuild
                {
                    Rank = b.Rank,
                    HullPartId = b.Hull,
                    SternPartId = b.Stern,
                    BowPartId = b.Bow,
                    BridgePartId = b.Bridge,
                }
                : null,
            // Only a collection has a result.
            SectorResults = collectedAtUtc != null && sectorResults is { Count: > 0 }
                ? [.. sectorResults.Select(ToWire)]
                : null,
        };
    }

    private static PushSectorResult ToWire(SectorResult r) => new()
    {
        SectorId = r.SectorId,
        UnlockedSectorId = r.UnlockedSectorId,
        FavorLine = r.FavorLine,
        ExpGained = r.ExpGained,
        Primary = ToWire(r.Primary),
        Additional = r.Additional is { } additional ? ToWire(additional) : null,
        Raw = r.Raw is { } raw ? ToWire(raw) : null,
    };

    private static PushSectorRaw ToWire(SectorRaw r) => new()
    {
        PointRating = r.PointRating,
        FirstExploration = r.FirstExploration,
        AdditionalSubmarineUnlocked = r.AdditionalSubmarineUnlocked,
        DoubleDip = r.DoubleDip,
        DiscoveredLinePrimary = r.DiscoveredLinePrimary,
        DiscoveredLineAdditional = r.DiscoveredLineAdditional,
        SurveyLineAdditional = r.SurveyLineAdditional,
        YieldLineAdditional = r.YieldLineAdditional,
    };

    private static PushSectorItem ToWire(SectorLoot l) => new()
    {
        ItemId = l.ItemId,
        Quantity = l.Quantity,
        Hq = l.Hq,
        SurveyLine = l.SurveyLine,
        YieldLine = l.YieldLine,
    };

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

    /// <summary>
    /// The dispatch time to record for a voyage seen leaving at
    /// <paramref name="observedUtc"/>, when that sighting was clearly late:
    /// the scheduled return minus the route's duration for the voyage's own
    /// build (parts are locked and rank holds while a sub is out). Null keeps
    /// the observed time: the sighting was on time, or there's no build or
    /// route to compute a duration from.
    /// </summary>
    private static DateTime? EstimateLateDispatch(
        uint scheduledReturnUnix, DateTime observedUtc, uint[] sectors, SubBuild? build)
    {
        if (scheduledReturnUnix == 0 || build is not { } b)
            return null;

        uint durationSeconds;
        try
        {
            durationSeconds = SectorMath.EstimateDurationSeconds(
                sectors, b.Rank, b.Hull, b.Stern, b.Bow, b.Bridge);
        }
        catch (Exception ex)
        {
            // A failed estimate must never cost the voyage its journal entry.
            Plugin.Log.Warning(ex, "Couldn't compute this route's duration — keeping the observed dispatch time");
            return null;
        }

        if (durationSeconds == 0)
            return null;

        var returnUtc = DateTimeOffset.FromUnixTimeSeconds(scheduledReturnUnix).UtcDateTime;
        if ((returnUtc - observedUtc).TotalSeconds >= durationSeconds - LateSightingToleranceSeconds)
            return null;

        return returnUtc.AddSeconds(-durationSeconds);
    }

    // A dispatch seen as it happens reads within seconds of the duration
    // computed for its route and build. One that reads more than this much
    // shorter was seen late. (A PC clock running fast reads the same way, and
    // the game's schedule is the better source then too.)
    private const int LateSightingToleranceSeconds = 10 * 60;

    private static string? RowNotes(bool simulated, bool lateSighting)
    {
        if (!lateSighting)
            return simulated ? "[SIM] simulated voyage" : null;

        const string note = "[EST] deployed_at estimated — dispatch seen late";
        return simulated ? "[SIM] " + note : note;
    }

    private static int RouteDurationMinutes(uint scheduledReturnUnix, DateTime dispatchedUtc)
    {
        var dispatchedUnix = new DateTimeOffset(dispatchedUtc, TimeSpan.Zero).ToUnixTimeSeconds();
        return (int)Math.Max(0, (scheduledReturnUnix - dispatchedUnix) / 60);
    }

    private static string SimTag(bool simulated) => simulated ? " [SIM]" : "";
}
