using System;
using System.Collections.Generic;

namespace BunnySubSync.Game;

// ---------------------------------------------------------------------------
// The event seam: the two game-facing producers (snapshot
// service, result hook) and the simulator emit ONLY these two events, and
// everything downstream (journal, assembler, later the outbox/UI) consumes
// ONLY these. Game memory is never touched below this seam — that equivalence
// is what makes the simulator a faithful stand-in for real voyages.
// ---------------------------------------------------------------------------

public sealed record LootLine(uint FfxivItemId, int Quantity, bool Hq);

/// <summary>A sub's build as the workshop reports it: rank plus the four raw
/// SubmarinePart row ids. Parts can't be swapped while a sub is out and rank
/// only changes on collection, so a build seen mid-voyage is the build that
/// voyage was dispatched with.</summary>
public sealed record SubBuild(ushort Rank, ushort Hull, ushort Stern, ushort Bow, ushort Bridge);

public sealed record SubDispatched(
    ulong FcId,
    uint SubRegisterTime,
    string SubName,
    DateTime DispatchedUtc,
    uint ScheduledReturnUnix,
    uint[] Sectors,
    SubBuild? Build,
    bool Simulated = false);

/// <summary>One item slot of a sector's result. SurveyLine and YieldLine are
/// the game's own verdicts for it (which surveillance and retrieval outcome
/// it rolled), as the ids of the result window's message lines.</summary>
public sealed record SectorLoot(uint ItemId, int Quantity, bool Hq, uint SurveyLine, uint YieldLine);

/// <summary>The rest of a sector's result entry, kept raw: fields the game
/// reports whose meaning isn't pinned down yet (or that duplicate others),
/// stored unchanged so nothing in the result is lost. The additional slot's
/// lines are kept here even when that slot has no item.</summary>
public sealed record SectorRaw(
    uint PointRating,
    bool FirstExploration,
    bool AdditionalSubmarineUnlocked,
    bool DoubleDip,
    uint DiscoveredLinePrimary,
    uint DiscoveredLineAdditional,
    uint SurveyLineAdditional,
    uint YieldLineAdditional);

/// <summary>What one sector of a voyage returned, exactly as the game reports
/// it: the sector, the sector it unlocked (0 = none), the favor verdict line,
/// the EXP it gave, its primary and optional additional item, and the rest of
/// the entry raw (null from a producer that doesn't read it).</summary>
public sealed record SectorResult(
    uint SectorId,
    uint UnlockedSectorId,
    uint FavorLine,
    uint ExpGained,
    SectorLoot Primary,
    SectorLoot? Additional,
    SectorRaw? Raw = null);

/// <summary>A collected voyage. Loot is the flat list of item lines;
/// SectorResults keeps the same result per sector, in visit order (null from
/// a producer that doesn't read it).</summary>
public sealed record VoyageCompleted(
    ulong FcId,
    uint SubRegisterTime,
    string SubName,
    DateTime CollectedUtc,
    List<LootLine> Loot,
    uint[] Sectors,
    bool Simulated = false,
    List<SectorResult>? SectorResults = null);

public sealed class VoyageEvents
{
    public event Action<SubDispatched>? Dispatched;
    public event Action<VoyageCompleted>? Completed;

    // Producers call these. Subscriber exceptions are contained here so a
    // downstream bug can never propagate back up into the packet hook or the
    // framework tick (a throwing hook can crash the game client).
    public void RaiseDispatched(SubDispatched ev)
    {
        try
        {
            Dispatched?.Invoke(ev);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Unhandled error in SubDispatched subscriber");
        }
    }

    public void RaiseCompleted(VoyageCompleted ev)
    {
        try
        {
            Completed?.Invoke(ev);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Unhandled error in VoyageCompleted subscriber");
        }
    }
}
