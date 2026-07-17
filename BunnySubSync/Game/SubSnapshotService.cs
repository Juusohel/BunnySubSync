using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;

namespace BunnySubSync.Game;

/// <summary>A game FC we've seen a workshop for — identity context the
/// Mapping tab needs: content id + tag + world + character.</summary>
public sealed record SeenFc(ulong FcId, string Tag, string World, string CharacterName);

/// <summary>One sub's state as last seen in the workshop (also feeds duration estimates).</summary>
public sealed record SubSnapshot(
    uint RegisterTime,
    string Name,
    ushort RankId,
    uint ReturnTime,
    uint[] Sectors,
    ushort HullId,
    ushort SternId,
    ushort BowId,
    ushort BridgeId);

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

    private readonly VoyageEvents events;

    // fcId → (registerTime → last snapshot). In-memory only: cleared on
    // plugin reload, which correctly re-arms the baseline rule.
    private readonly Dictionary<ulong, Dictionary<uint, SubSnapshot>> known = [];

    private readonly Dictionary<ulong, SeenFc> seenFcs = [];

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

    /// <summary>Game FCs whose workshop we've visited this session (Mapping tab).</summary>
    public IReadOnlyCollection<SeenFc> SeenFcs => seenFcs.Values;

    /// <summary>All subs last seen for a game FC, for the Mapping tab's sub rows.</summary>
    public IReadOnlyCollection<SubSnapshot> SubsOf(ulong fcId) =>
        known.TryGetValue(fcId, out var subs) ? subs.Values : [];

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

        foreach (var sub in housing->WorkshopTerritory->Submersible.Data)
        {
            if (sub.RankId == 0)
                continue;

            var current = TakeSnapshot(sub);

            // Throttle: nothing changed for this sub → nothing to do.
            // (Explicit comparison — record equality would compare the
            // Sectors array by reference and never match.)
            fcSubs.TryGetValue(current.RegisterTime, out var prev);
            if (prev != null && SnapshotsEqual(prev, current))
                continue;

            fcSubs[current.RegisterTime] = current;

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
                    DateTime.UtcNow,
                    current.ReturnTime,
                    current.Sectors));
            }
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

    private static SubSnapshot TakeSnapshot(HousingWorkshopSubmersibleSubData sub)
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
            sub.BridgeId);
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
