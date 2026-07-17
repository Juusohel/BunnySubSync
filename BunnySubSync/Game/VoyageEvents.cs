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

public sealed record SubDispatched(
    ulong FcId,
    uint SubRegisterTime,
    string SubName,
    DateTime DispatchedUtc,
    uint ScheduledReturnUnix,
    uint[] Sectors,
    bool Simulated = false);

public sealed record VoyageCompleted(
    ulong FcId,
    uint SubRegisterTime,
    string SubName,
    DateTime CollectedUtc,
    List<LootLine> Loot,
    uint[] Sectors,
    bool Simulated = false);

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
