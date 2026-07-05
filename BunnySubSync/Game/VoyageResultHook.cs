using System;
using System.Collections.Generic;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Network;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Text.ReadOnly;

namespace BunnySubSync.Game;

/// <summary>
/// Above-the-seam producer #2: hooks the voyage-result packet and emits
/// VoyageCompleted. Direct port of SubmarineTracker's Manager/HookManager.cs
/// (MIT) — same packet id, same read path, same defensiveness (the entire
/// body is try/caught: a throwing hook can crash the game client).
/// </summary>
public sealed unsafe class VoyageResultHook : IDisposable
{
    /// <summary>EventId of the submarine voyage-result packet (HookManager.cs).</summary>
    private const uint VoyageResultEventId = 721343;

    private readonly VoyageEvents events;
    private readonly Hook<PacketDispatcher.Delegates.HandleEventYieldPacket> hook;

    public VoyageResultHook(VoyageEvents events)
    {
        this.events = events;
        hook = Plugin.GameInteropProvider.HookFromAddress<PacketDispatcher.Delegates.HandleEventYieldPacket>(
            PacketDispatcher.MemberFunctionPointers.HandleEventYieldPacket,
            PacketReceiver);
        hook.Enable();
    }

    public void Dispose()
    {
        hook.Dispose();
    }

    private void PacketReceiver(EventId id, short scene, byte responseId, int* intParams, byte argCount)
    {
        hook.Original(id, scene, responseId, intParams, argCount);

        if (id != VoyageResultEventId)
            return;

        try
        {
            var housing = HousingManager.Instance();
            if (housing == null || housing->WorkshopTerritory == null)
                return;

            // Slot 4 is the sub whose result window just opened (HookManager.cs).
            var current = housing->WorkshopTerritory->Submersible.DataPointers[4];
            if (current.Value == null)
                return;

            var sub = current.Value;
            var fcId = InfoProxyFreeCompany.Instance()->Id;
            var register = sub->RegisterTime;
            var name = SubSnapshotService.ExtractName(sub->Name);

            var data = sub->GatheredData;
            if (data[0].ItemIdPrimary == 0)
                return;

            var sectors = new List<uint>();
            var loot = new List<LootLine>();
            foreach (var entry in data)
            {
                if (entry.Point == 0)
                    continue;

                sectors.Add(entry.Point);
                loot.Add(new LootLine(entry.ItemIdPrimary, entry.ItemCountPrimary, entry.ItemHQPrimary));
                if (entry.ItemIdAdditional > 0)
                    loot.Add(new LootLine(entry.ItemIdAdditional, entry.ItemCountAdditional, entry.ItemHQAdditional));
            }

            if (sectors.Count == 0)
                return;

            Plugin.Log.Information(
                $"Voyage result packet: '{name}' (fc {fcId:x}, register {register}), "
                + $"{sectors.Count} sector(s), {loot.Count} loot line(s)");

            // sub->ReturnTime is already 0 when this packet lands, so
            // "collected now" is the ground truth (HookManager.cs:47).
            events.RaiseCompleted(new VoyageCompleted(
                fcId,
                register,
                name,
                DateTime.UtcNow,
                loot,
                [.. sectors]));
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Error in voyage-result packet receiver");
        }
    }
}
