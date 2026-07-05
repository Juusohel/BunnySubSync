using System;
using System.Collections.Generic;
using System.Linq;

namespace BunnySubSync.Game;

/// <summary>
/// Dev-gated second producer of the seam events (plan §9 G2: "simulation
/// mode, in scope, required"). Emits exactly the same SubDispatched /
/// VoyageCompleted records as the real capture layer, so every line of code
/// below the seam — journal, assembler, later the outbox and pushes — runs
/// unchanged. The only thing it can't test is whether the capture adapter
/// reads game memory correctly; that stays the job of the real-voyage gates.
/// </summary>
public sealed class VoyageSimulator
{
    // Fabricated identity used when no real snapshot is picked. Hex-obvious
    // in logs and external_voyage_ids so sim rows are unmistakable.
    public const ulong FabricatedFcId = 0x51D_FC;
    public const uint FabricatedRegisterTime = 424242;

    private readonly VoyageEvents events;

    public VoyageSimulator(VoyageEvents events)
    {
        this.events = events;
    }

    /// <summary>Identity of the last simulated dispatch, so "Simulate collection
    /// (for last dispatch)" exercises the dispatch→complete lifecycle.</summary>
    public SubDispatched? LastDispatch { get; private set; }

    /// <returns>An error string, or null on success.</returns>
    public string? SimulateDispatch(
        ulong fcId, uint registerTime, string subName, uint mapStartSector, string routeLetters,
        int durationMinutes, DateTime? dispatchedUtcOverride)
    {
        uint[] sectors;
        try
        {
            sectors = SectorMath.RouteToSectors(mapStartSector, routeLetters);
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }

        if (sectors.Length is 0 or > 5)
            return "Route must be 1–5 letters.";
        if (durationMinutes <= 0)
            return "Duration must be positive.";

        var dispatchedUtc = dispatchedUtcOverride ?? DateTime.UtcNow;
        var scheduledReturn = (uint)new DateTimeOffset(dispatchedUtc, TimeSpan.Zero)
                              .AddMinutes(durationMinutes).ToUnixTimeSeconds();

        var ev = new SubDispatched(
            fcId, registerTime, subName, dispatchedUtc, scheduledReturn, sectors, Simulated: true);
        LastDispatch = ev;
        events.RaiseDispatched(ev);
        return null;
    }

    /// <param name="forLastDispatch">Reuse the last simulated dispatch's identity
    /// and route so the pair forms one voyage. Otherwise the given identity is
    /// used as-is (e.g. to exercise the missed-dispatch path).</param>
    /// <returns>An error string, or null on success.</returns>
    public string? SimulateCollection(
        bool forLastDispatch, ulong fcId, uint registerTime, string subName,
        uint mapStartSector, string routeLetters, List<LootLine> loot,
        DateTime? collectedUtcOverride)
    {
        uint[] sectors;
        if (forLastDispatch)
        {
            if (LastDispatch == null)
                return "No simulated dispatch yet this session.";

            fcId = LastDispatch.FcId;
            registerTime = LastDispatch.SubRegisterTime;
            subName = LastDispatch.SubName;
            sectors = LastDispatch.Sectors;
        }
        else
        {
            try
            {
                sectors = SectorMath.RouteToSectors(mapStartSector, routeLetters);
            }
            catch (ArgumentException ex)
            {
                return ex.Message;
            }
        }

        if (sectors.Length is 0 or > 5)
            return "Route must be 1–5 letters.";

        var lootLines = loot.Where(l => l.FfxivItemId != 0 && l.Quantity > 0).ToList();
        if (lootLines.Count == 0)
            return "Add at least one loot line (item id + quantity).";

        events.RaiseCompleted(new VoyageCompleted(
            fcId,
            registerTime,
            subName,
            collectedUtcOverride ?? DateTime.UtcNow,
            lootLines,
            sectors,
            Simulated: true));
        return null;
    }
}
