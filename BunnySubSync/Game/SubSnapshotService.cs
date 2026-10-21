using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;

namespace BunnySubSync.Game;

/// <summary>A game FC we've seen a workshop for — identity context the
/// Mapping tab needs: content id + tag + world + character.</summary>
public sealed record SeenFc(ulong FcId, string Tag, string World, string CharacterName);

/// <summary>One sub's state as last seen in the workshop (also feeds duration
/// estimates). TakenUtc is when this exact state was first read: an unchanged
/// read keeps the earlier snapshot.</summary>
public sealed record SubSnapshot(
    uint RegisterTime,
    string Name,
    ushort RankId,
    uint ReturnTime,
    uint[] Sectors,
    ushort HullId,
    ushort SternId,
    ushort BowId,
    ushort BridgeId,
    DateTime TakenUtc)
{
    public SubBuild Build => new(RankId, HullId, SternId, BowId, BridgeId);
}

/// <summary>A workshop's unlocked and explored sectors (SubmarineExploration
/// row ids), and when that state was first read.</summary>
public sealed record SectorUnlocks(uint[] Unlocked, uint[] Explored, DateTime TakenUtc);

/// <summary>
/// Above-the-seam producer #1: polls workshop submarine data on the framework
/// tick (technique from SubmarineTracker Plugin.cs FrameworkUpdate) and emits
/// SubDispatched on an observed ReturnTime transition.
///
/// Invariant: the first snapshot after login/plugin-load is a
/// baseline, not a dispatch — detection fires only on an observed *change* of
/// ReturnTime for a sub we already had a snapshot of.
/// </summary>
public sealed unsafe class SubSnapshotService : IDisposable
{
    private const uint IslandSanctuaryTerritoryUse = 49;
    private const int UnlockReadIntervalSeconds = 10;

    private readonly VoyageEvents events;

    // fcId → (registerTime → last snapshot). In-memory only: cleared on
    // plugin reload, which correctly re-arms the baseline rule.
    private readonly Dictionary<ulong, Dictionary<uint, SubSnapshot>> known = [];

    // fcId → (registerTime → the last voyage seen under way: its *non-zero*
    // ReturnTime, plus the build from its first sighting). Collection zeroes
    // the live ReturnTime on the next tick — *before* the voyage-result
    // packet lands — so the live cache cannot answer "which voyage was just
    // collected?". This one can: it survives the T→0 transition and always
    // holds the schedule of the voyage being (or about to be) collected.
    // Memory-only is fine: ReturnTime stays set until the collect click, so
    // the baseline poll on entering the workshop repopulates it first.
    private readonly Dictionary<ulong, Dictionary<uint, VoyageSighting>> lastVoyage = [];

    private readonly Dictionary<ulong, SeenFc> seenFcs = [];

    // fcId → the workshop's sector unlocks, re-read every few seconds while
    // inside it. Never holds an empty read.
    private readonly Dictionary<ulong, SectorUnlocks> unlocks = [];
    private DateTime nextUnlockReadUtc = DateTime.MinValue;
    private bool unlockReadFailed;

    private sealed record VoyageSighting(uint ReturnTime, SubBuild Build);

    public SubSnapshotService(VoyageEvents events)
    {
        this.events = events;
        Plugin.Framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnFrameworkUpdate;
    }

    /// <summary>
    /// Last snapshot taken of a sub, if any — used by the assembler to
    /// recover the scheduled return time and build for journal-missed
    /// voyages. Reads the cache, never game memory.
    /// </summary>
    public SubSnapshot? TryGetLastSnapshot(ulong fcId, uint registerTime) =>
        known.TryGetValue(fcId, out var subs) && subs.TryGetValue(registerTime, out var snap)
            ? snap
            : null;

    /// <summary>
    /// The last *non-zero* scheduled return observed for a sub (0 = never
    /// seen mid-voyage this session). Unlike the live snapshot's ReturnTime —
    /// already zeroed by the collection click when the result packet lands —
    /// this identifies the voyage that was just collected. Used by the
    /// assembler's stale-join guard and missed-dispatch estimate anchor.
    /// </summary>
    public uint LastVoyageReturnUnix(ulong fcId, uint registerTime) =>
        lastVoyage.TryGetValue(fcId, out var subs) && subs.TryGetValue(registerTime, out var v)
            ? v.ReturnTime
            : 0;

    /// <summary>
    /// The build of the voyage <see cref="LastVoyageReturnUnix"/> identifies,
    /// as first seen while it was under way — its dispatch build, even when
    /// the dispatch itself was missed (null = never seen mid-voyage this
    /// session).
    /// </summary>
    public SubBuild? LastVoyageBuild(ulong fcId, uint registerTime) =>
        lastVoyage.TryGetValue(fcId, out var subs) && subs.TryGetValue(registerTime, out var v)
            ? v.Build
            : null;

    /// <summary>Game FCs whose workshop we've visited this session (Mapping tab).</summary>
    public IReadOnlyCollection<SeenFc> SeenFcs => seenFcs.Values;

    /// <summary>All subs last seen for a game FC, for the Mapping tab's sub rows.</summary>
    public IReadOnlyCollection<SubSnapshot> SubsOf(ulong fcId) =>
        known.TryGetValue(fcId, out var subs) ? subs.Values : [];

    /// <summary>A game FC's sector unlocks as last read in its workshop, if any.</summary>
    public SectorUnlocks? TryGetUnlocks(ulong fcId) =>
        unlocks.TryGetValue(fcId, out var u) ? u : null;

    private void OnFrameworkUpdate(IFramework _)
    {
        try
        {
            PollWorkshop();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Error while polling workshop submarine data");
        }
    }

    private void PollWorkshop()
    {
        if (Plugin.ObjectTable.LocalPlayer == null)
            return;

        var housing = HousingManager.Instance();
        if (housing == null || housing->WorkshopTerritory == null)
            return;

        // Island Sanctuary also populates WorkshopTerritory (6.4) — skip it,
        // exactly like SubmarineTracker Plugin.cs:262.
        var territory = Plugin.DataManager.GetExcelSheet<TerritoryType>()
                              .GetRow(Plugin.ClientState.TerritoryType);
        if (territory.TerritoryIntendedUse.RowId == IslandSanctuaryTerritoryUse)
            return;

        var fcId = InfoProxyFreeCompany.Instance()->Id;
        if (fcId == 0)
            return;

        if (!seenFcs.ContainsKey(fcId))
        {
            var local = Plugin.ObjectTable.LocalPlayer;
            seenFcs[fcId] = new SeenFc(
                fcId,
                local?.CompanyTag.TextValue ?? "",
                local?.HomeWorld.Value.Name.ExtractText() ?? "",
                local?.Name.TextValue ?? "");
            Plugin.Log.Information($"Workshop FC seen: {fcId:x} tag '{seenFcs[fcId].Tag}'");
        }

        var fcSubs = known.TryGetValue(fcId, out var existing)
            ? existing
            : known[fcId] = [];

        var nowUtc = DateTime.UtcNow;
        var sawSub = false;

        foreach (var sub in housing->WorkshopTerritory->Submersible.Data)
        {
            if (sub.RankId == 0)
                continue;

            sawSub = true;
            var current = TakeSnapshot(sub, nowUtc);

            // Throttle: nothing changed for this sub → nothing to do.
            // (Explicit comparison — record equality would compare the
            // Sectors array by reference and never match.)
            fcSubs.TryGetValue(current.RegisterTime, out var prev);
            if (prev != null && SnapshotsEqual(prev, current))
                continue;

            fcSubs[current.RegisterTime] = current;

            // Remember the schedule across the T→0 collection transition
            // (see lastVoyage) — recorded on baseline and on dispatch. The
            // build is kept from the voyage's first sighting, so even a
            // same-voyage re-read can't replace its dispatch build.
            if (current.ReturnTime != 0)
            {
                var fcVoyages = lastVoyage.TryGetValue(fcId, out var r)
                    ? r
                    : lastVoyage[fcId] = [];
                if (!fcVoyages.TryGetValue(current.RegisterTime, out var seen)
                    || seen.ReturnTime != current.ReturnTime)
                    fcVoyages[current.RegisterTime] = new VoyageSighting(current.ReturnTime, current.Build);
            }

            if (prev == null)
            {
                // Baseline, not a dispatch: record state, fire nothing.
                Plugin.Log.Information(
                    $"Sub snapshot (baseline): '{current.Name}' rank {current.RankId}, "
                    + $"return {FormatReturn(current.ReturnTime)}, "
                    + $"route {SectorMath.SectorsToRoute(current.Sectors)}");
                continue;
            }

            Plugin.Log.Information(
                $"Sub snapshot (changed): '{current.Name}' return "
                + $"{FormatReturn(prev.ReturnTime)} → {FormatReturn(current.ReturnTime)}");

            // Dispatch = observed ReturnTime transition to a new non-zero
            // value: 0→T (dispatch after idle) or T1→T2 (redispatch where the
            // collect frame was missed). T→0 is a collection — the result
            // hook owns that side.
            if (current.ReturnTime != 0 && prev.ReturnTime != current.ReturnTime)
            {
                events.RaiseDispatched(new SubDispatched(
                    fcId,
                    current.RegisterTime,
                    current.Name,
                    nowUtc,
                    current.ReturnTime,
                    current.Sectors,
                    current.Build));
            }
        }

        // Only once the workshop has its subs loaded, like SubmarineTracker,
        // which reads these in the same place (Plugin.cs, its FC upsert).
        if (sawSub && nowUtc >= nextUnlockReadUtc)
        {
            nextUnlockReadUtc = nowUtc.AddSeconds(UnlockReadIntervalSeconds);
            ReadUnlocks(fcId, nowUtc);
        }
    }

    /// <summary>
    /// Reads which sectors this workshop has unlocked and explored, with the
    /// same game calls SubmarineTracker uses, for every SubmarineExploration
    /// row. An all-locked read means the data isn't there yet and is dropped.
    /// A failing call (a game-data change) turns the read off for the
    /// session instead of throwing every few seconds.
    /// </summary>
    private void ReadUnlocks(ulong fcId, DateTime nowUtc)
    {
        if (unlockReadFailed)
            return;

        try
        {
            var unlocked = new List<uint>();
            var explored = new List<uint>();
            foreach (var row in Plugin.DataManager.GetExcelSheet<SubmarineExploration>())
            {
                // The game's checks take a byte-sized sector id.
                if (row.RowId > byte.MaxValue)
                    continue;

                var point = (byte)row.RowId;
                if (HousingManager.IsSubmarineExplorationUnlocked(point))
                    unlocked.Add(row.RowId);
                if (HousingManager.IsSubmarineExplorationExplored(point))
                    explored.Add(row.RowId);
            }

            if (unlocked.Count == 0)
                return;

            if (unlocks.TryGetValue(fcId, out var prev)
                && prev.Unlocked.AsSpan().SequenceEqual(CollectionsMarshal.AsSpan(unlocked))
                && prev.Explored.AsSpan().SequenceEqual(CollectionsMarshal.AsSpan(explored)))
                return;

            unlocks[fcId] = new SectorUnlocks([.. unlocked], [.. explored], nowUtc);
            Plugin.Log.Information(
                $"Workshop sectors read: {unlocked.Count} unlocked, {explored.Count} explored");
        }
        catch (Exception ex)
        {
            unlockReadFailed = true;
            Plugin.Log.Error(ex, "Couldn't read the workshop's unlocked sectors; they won't sync this session");
        }
    }

    private static bool SnapshotsEqual(SubSnapshot a, SubSnapshot b) =>
        a.RegisterTime == b.RegisterTime
        && a.Name == b.Name
        && a.RankId == b.RankId
        && a.ReturnTime == b.ReturnTime
        && a.HullId == b.HullId
        && a.SternId == b.SternId
        && a.BowId == b.BowId
        && a.BridgeId == b.BridgeId
        && a.Sectors.AsSpan().SequenceEqual(b.Sectors);

    private static SubSnapshot TakeSnapshot(HousingWorkshopSubmersibleSubData sub, DateTime nowUtc)
    {
        var sectors = new List<uint>();
        foreach (var point in sub.CurrentExplorationPoints)
        {
            if (point > 0)
                sectors.Add(point);
        }

        return new SubSnapshot(
            sub.RegisterTime,
            ExtractName(sub.Name),
            sub.RankId,
            sub.ReturnTime,
            [.. sectors],
            sub.HullId,
            sub.SternId,
            sub.BowId,
            sub.BridgeId,
            nowUtc);
    }

    /// <summary>Sub names live in a fixed NUL-padded byte buffer (SeString bytes).</summary>
    internal static string ExtractName(Span<byte> raw)
    {
        var nul = raw.IndexOf((byte)0);
        var bytes = nul >= 0 ? raw[..nul] : raw;
        return new ReadOnlySeStringSpan(bytes).ExtractText();
    }

    private static string FormatReturn(uint unix) =>
        unix == 0 ? "none" : DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.ToString("u");
}
