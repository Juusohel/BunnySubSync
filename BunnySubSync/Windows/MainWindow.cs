using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using BunnySubSync.Api;
using BunnySubSync.Game;

namespace BunnySubSync.Windows;

public class MainWindow : Window, IDisposable
{
    private const string ProdServerUrl = "https://subs.bnuuy.gg";

    private static readonly Vector4 ErrorColor = new(1f, 0.4f, 0.4f, 1f);
    private static readonly Vector4 SuccessColor = new(0.4f, 1f, 0.4f, 1f);
    private static readonly Vector4 WarnColor = new(1f, 0.85f, 0.4f, 1f);
    private static readonly Vector4 MutedColor = new(0.65f, 0.65f, 0.65f, 1f);

    private readonly Plugin plugin;
    private readonly VoyageJournal journal;
    private readonly VoyageAssembler assembler;
    private readonly VoyageSimulator simulator;

    private string serverUrlInput;
    private string apiTokenInput;
    private bool revealToken;

    private bool isTesting;
    private string? statusMessage;
    private bool statusIsError;

    // --- Simulator tab state -------------------------------------------------
    private sealed class SimLootLine
    {
        public int ItemId;
        public int Quantity = 1;
        public bool Hq;
    }

    private string simFcIdHex = VoyageSimulator.FabricatedFcId.ToString("x");
    private int simRegisterTime = (int)VoyageSimulator.FabricatedRegisterTime;
    private string simSubName = "Sim Sub";
    private int simMapIndex;
    private string simRoute = "OJ";
    private int simDurationMinutes = 120;
    private readonly List<SimLootLine> simLoot = [new SimLootLine { ItemId = 22500, Quantity = 4 }];
    private string? simMessage;
    private bool simMessageIsError;
    private List<(uint StartSector, string MapName)>? mapStarts;

    public MainWindow(Plugin plugin, VoyageJournal journal, VoyageAssembler assembler, VoyageSimulator simulator)
        : base("Bunny Sub Sync##BunnySubSyncMainWindow")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 320),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        this.plugin = plugin;
        this.journal = journal;
        this.assembler = assembler;
        this.simulator = simulator;
        serverUrlInput = plugin.Configuration.ServerUrl;
        apiTokenInput = plugin.Configuration.ApiToken;
    }

    public void Dispose() { }

    public override void Draw()
    {
        using var tabBar = ImRaii.TabBar("BunnySubSyncTabs");
        if (!tabBar.Success)
            return;

        using (var tab = ImRaii.TabItem("Status"))
        {
            if (tab.Success)
                DrawStatusTab();
        }

        using (var tab = ImRaii.TabItem("Mapping"))
        {
            if (tab.Success)
                DrawMappingTab();
        }

        using (var tab = ImRaii.TabItem("Log"))
        {
            if (tab.Success)
                DrawLogTab();
        }

        if (plugin.Configuration.DevMode)
        {
            using var tab = ImRaii.TabItem("Simulator");
            if (tab.Success)
                DrawSimulatorTab();
        }
    }

    private void DrawStatusTab()
    {
        ImGui.Spacing();

        ImGui.SetNextItemWidth(300);
        if (ImGui.InputText("Server URL", ref serverUrlInput, 256))
        {
            plugin.Configuration.ServerUrl = serverUrlInput;
            plugin.Configuration.Save();
        }

        ImGui.SetNextItemWidth(300);
        var flags = revealToken ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.Password;
        if (ImGui.InputText("Plugin token", ref apiTokenInput, 256, flags))
        {
            plugin.Configuration.ApiToken = apiTokenInput;
            plugin.Configuration.Save();
        }

        ImGui.SameLine();
        ImGui.Checkbox("Show", ref revealToken);

        ImGui.Spacing();

        using (ImRaii.Disabled(isTesting))
        {
            if (ImGui.Button("Test connection"))
                TestConnection();
        }

        if (isTesting)
        {
            ImGui.SameLine();
            ImGui.Text("Testing...");
        }

        if (statusMessage != null)
        {
            ImGui.Spacing();
            using var color = ImRaii.PushColor(ImGuiCol.Text, statusIsError ? ErrorColor : SuccessColor);
            ImGui.TextWrapped(statusMessage);
        }
    }

    private static void DrawMappingTab()
    {
        ImGui.Spacing();
        ImGui.TextWrapped("FC and submarine mapping will live here (Phase G3).");
    }

    private void DrawLogTab()
    {
        ImGui.Spacing();

        var entries = journal.SnapshotNewestFirst();
        var queued = entries.Count(e => e.State == OutboxState.Queued);
        var failed = entries.Count(e => e.State == OutboxState.Failed);
        var pending = entries.Count(e => e.State == OutboxState.PendingCollection);

        ImGui.TextWrapped(
            $"Voyage journal: {entries.Count} entries — {pending} at sea, {queued} queued "
            + $"(pushes arrive in Phase G3), {failed} need attention.");
        ImGui.Spacing();

        if (entries.Count == 0)
        {
            using var color = ImRaii.PushColor(ImGuiCol.Text, MutedColor);
            ImGui.TextWrapped("No voyages observed yet. Visit your workshop so submarines get a baseline snapshot; dispatches and collections are recorded from there.");
            return;
        }

        using var table = ImRaii.Table("JournalTable", 5,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY);
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("Time (UTC)", ImGuiTableColumnFlags.WidthFixed, 130);
        ImGui.TableSetupColumn("Sub", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Route", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.WidthFixed, 110);
        ImGui.TableSetupColumn("Detail", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        foreach (var entry in entries)
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            var shownTime = entry.CollectedUtc ?? entry.DispatchedUtc;
            ImGui.Text($"{shownTime:MM-dd HH:mm}");

            ImGui.TableNextColumn();
            ImGui.Text(entry.SubName + (entry.Simulated ? " [SIM]" : ""));

            ImGui.TableNextColumn();
            ImGui.Text(SectorMath.SectorsToRoute(entry.Sectors));

            ImGui.TableNextColumn();
            var (stateText, stateColor) = entry.State switch
            {
                OutboxState.PendingCollection => ("at sea", MutedColor),
                OutboxState.Queued => ("queued", SuccessColor),
                OutboxState.Sent => ("sent", SuccessColor),
                OutboxState.Failed => ("needs attention", ErrorColor),
                _ => (entry.State.ToString(), MutedColor),
            };
            using (ImRaii.PushColor(ImGuiCol.Text, stateColor))
                ImGui.Text(stateText);

            ImGui.TableNextColumn();
            if (entry.State == OutboxState.Failed && entry.PendingLoot != null)
            {
                ImGui.Text(entry.FailureMessage ?? "");
                ImGui.SameLine();
                using var id = ImRaii.PushId(entry.Id.ToString());
                if (ImGui.Button("Estimate & queue"))
                {
                    var error = assembler.EstimateAndQueue(entry);
                    if (error != null)
                        Plugin.Log.Warning($"Estimate & queue: {error}");
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Builds the row with an estimated dispatch time (marked [EST]). Nothing is estimated without this explicit click.");
            }
            else if (entry.FailureMessage != null)
            {
                ImGui.Text(entry.FailureMessage);
            }
            else if (entry.BuiltRow != null)
            {
                var gilLines = entry.BuiltRow.Loot.Count;
                ImGui.Text($"{gilLines} loot line(s), {entry.BuiltRow.CeruleumTanks} tanks");
            }
            else
            {
                ImGui.Text($"returns {DateTimeOffset.FromUnixTimeSeconds(entry.ScheduledReturnUnix).UtcDateTime:MM-dd HH:mm}");
            }
        }
    }

    private void DrawSimulatorTab()
    {
        ImGui.Spacing();

        if (plugin.Configuration.ServerUrl.TrimEnd('/') == ProdServerUrl)
        {
            using var color = ImRaii.PushColor(ImGuiCol.Text, WarnColor);
            ImGui.TextWrapped(
                "⚠ Server URL is the production site. Simulated voyages queued here will be "
                + "pushed to PRODUCTION once G3 lands — point at a local server, or plan on "
                + "deleting [SIM] rows from the website.");
            ImGui.Spacing();
        }

        ImGui.TextWrapped("Emits the same internal events as the real capture layer — everything downstream (journal, assembly, later pushes) is exercised for real.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.SetNextItemWidth(160);
        ImGui.InputText("FC id (hex)", ref simFcIdHex, 16, ImGuiInputTextFlags.CharsHexadecimal);
        ImGui.SetNextItemWidth(160);
        ImGui.InputInt("Register time", ref simRegisterTime, 0);
        ImGui.SetNextItemWidth(160);
        ImGui.InputText("Sub name", ref simSubName, 64);

        mapStarts ??= SectorMath.MapStarts();
        var mapNames = mapStarts.Select(m => m.MapName).ToArray();
        ImGui.SetNextItemWidth(220);
        ImGui.Combo("Map", ref simMapIndex, mapNames, mapNames.Length);

        ImGui.SetNextItemWidth(160);
        if (ImGui.InputText("Route letters", ref simRoute, 8))
            simRoute = simRoute.ToUpperInvariant();

        ImGui.SetNextItemWidth(160);
        ImGui.InputInt("Duration (min)", ref simDurationMinutes, 0);

        ImGui.Spacing();
        ImGui.Text("Loot (ffxiv item ids — 22500–22507 are the salvage pieces):");
        int? removeIndex = null;
        for (var i = 0; i < simLoot.Count; i++)
        {
            using var id = ImRaii.PushId(i);
            ImGui.SetNextItemWidth(110);
            ImGui.InputInt("##item", ref simLoot[i].ItemId, 0);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(70);
            ImGui.InputInt("##qty", ref simLoot[i].Quantity, 0);
            ImGui.SameLine();
            ImGui.Checkbox("HQ", ref simLoot[i].Hq);
            ImGui.SameLine();
            if (ImGui.Button("×"))
                removeIndex = i;
        }
        if (removeIndex is { } idx)
            simLoot.RemoveAt(idx);
        if (ImGui.Button("+ loot line"))
            simLoot.Add(new SimLootLine());

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("Simulate dispatch"))
            SetSimMessage(RunSimDispatch());

        ImGui.SameLine();
        using (ImRaii.Disabled(simulator.LastDispatch == null))
        {
            if (ImGui.Button("Simulate collection (for last dispatch)"))
                SetSimMessage(RunSimCollect(forLastDispatch: true));
        }

        ImGui.SameLine();
        if (ImGui.Button("Simulate collection (standalone)"))
            SetSimMessage(RunSimCollect(forLastDispatch: false));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Completes a pending dispatch with the same FC id + register if one exists; otherwise exercises the missed-dispatch path (use a fresh register value for that).");

        if (simMessage != null)
        {
            ImGui.Spacing();
            using var color = ImRaii.PushColor(ImGuiCol.Text, simMessageIsError ? ErrorColor : SuccessColor);
            ImGui.TextWrapped(simMessage);
        }
    }

    private void SetSimMessage(string? error)
    {
        simMessageIsError = error != null;
        simMessage = error ?? "Done — see the Log tab and /xllog.";
    }

    // Shared with the /bunnysync sim … command (uses the tab's current inputs).
    public string? RunSimDispatch()
    {
        if (!TryParseSimIdentity(out var fcId, out var error))
            return error;

        return simulator.SimulateDispatch(
            fcId, (uint)simRegisterTime, simSubName,
            CurrentMapStart(), simRoute, simDurationMinutes, dispatchedUtcOverride: null);
    }

    public string? RunSimCollect(bool forLastDispatch)
    {
        if (!TryParseSimIdentity(out var fcId, out var error))
            return error;

        var loot = simLoot
                   .Where(l => l.ItemId > 0 && l.Quantity > 0)
                   .Select(l => new LootLine((uint)l.ItemId, l.Quantity, l.Hq))
                   .ToList();

        return simulator.SimulateCollection(
            forLastDispatch, fcId, (uint)simRegisterTime, simSubName,
            CurrentMapStart(), simRoute, loot, collectedUtcOverride: null);
    }

    private uint CurrentMapStart()
    {
        mapStarts ??= SectorMath.MapStarts();
        return mapStarts.Count == 0
            ? 0
            : mapStarts[Math.Clamp(simMapIndex, 0, mapStarts.Count - 1)].StartSector;
    }

    private bool TryParseSimIdentity(out ulong fcId, out string? error)
    {
        if (!ulong.TryParse(simFcIdHex, System.Globalization.NumberStyles.HexNumber, null, out fcId))
        {
            error = "FC id must be hex.";
            return false;
        }

        error = null;
        return true;
    }

    private async void TestConnection()
    {
        isTesting = true;
        statusMessage = null;

        try
        {
            using var client = new ApiClient(serverUrlInput, apiTokenInput);
            var sync = await client.SyncAsync().ConfigureAwait(false);

            statusIsError = false;
            statusMessage =
                $"Linked as {sync.User.Name} — {sync.FreeCompanies.Count} FC(s), " +
                $"{sync.Submarines.Count} submarine(s), {sync.SalvageItems.Count} catalog item(s).";
            Plugin.Log.Information(statusMessage);
        }
        catch (UnauthorizedException ex)
        {
            statusIsError = true;
            statusMessage = "Token rejected by the server. Check the token and try again.";
            Plugin.Log.Warning(ex, "Test connection: token rejected");
        }
        catch (Exception ex)
        {
            statusIsError = true;
            statusMessage = $"Connection failed: {ex.Message}";
            Plugin.Log.Error(ex, "Test connection failed");
        }
        finally
        {
            isTesting = false;
        }
    }
}
