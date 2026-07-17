using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
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
    private readonly SubSnapshotService snapshots;
    private readonly SyncService sync;
    private readonly Outbox outbox;

    private string serverUrlInput;
    private string apiTokenInput;
    private bool revealToken;

    private bool isTesting;
    private string? statusMessage;
    private bool statusIsError;

    private string? mappingMessage;
    private DateTime? lastAutoMatchAt;

    // --- Stats tab state -----------------------------------------------------
    private int? statsScopeFcId;         // null = the account's default scope
    private StatsResponse? statsResult;
    private string? statsError;
    private bool statsLoading;
    private DateTime? statsLoadedAtUtc;

    // --- Backfill tab state --------------------------------------------------
    private readonly BackfillService backfill;
    private readonly FileDialogManager fileDialog = new();
    private string backfillDbPath = BackfillService.DefaultDbPath();
    private string backfillFrom = "";
    private string backfillTo = "";
    private BackfillService.ScanResult? backfillScan;
    private string? backfillMessage;
    private bool backfillMessageIsError;
    private bool backfillBusy;

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

    public MainWindow(
        Plugin plugin, VoyageJournal journal, VoyageAssembler assembler, VoyageSimulator simulator,
        SubSnapshotService snapshots, SyncService sync, Outbox outbox, BackfillService backfill)
        : base("Bunny Sub Sync##BunnySubSyncMainWindow")
    {
        this.backfill = backfill;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(480, 360),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        this.plugin = plugin;
        this.journal = journal;
        this.assembler = assembler;
        this.simulator = simulator;
        this.snapshots = snapshots;
        this.sync = sync;
        this.outbox = outbox;
        serverUrlInput = plugin.Configuration.ServerUrl;
        apiTokenInput = plugin.Configuration.ApiToken;
    }

    public void Dispose() { }

    public override void Draw()
    {
        fileDialog.Draw();

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

        using (var tab = ImRaii.TabItem("Stats"))
        {
            if (tab.Success)
                DrawStatsTab();
        }

        using (var tab = ImRaii.TabItem("Log"))
        {
            if (tab.Success)
                DrawLogTab();
        }

        using (var tab = ImRaii.TabItem("Backfill"))
        {
            if (tab.Success)
                DrawBackfillTab();
        }

        if (plugin.Configuration.DevMode)
        {
            using var tab = ImRaii.TabItem("Simulator");
            if (tab.Success)
                DrawSimulatorTab();
        }
    }

    // =========================================================================
    // Status
    // =========================================================================

    private void DrawStatusTab()
    {
        ImGui.Spacing();

        if (outbox.Paused)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, ErrorColor))
                ImGui.TextWrapped($"⚠ Pushes paused: {outbox.PausedReason}");
            ImGui.Spacing();
        }

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
            outbox.OnTokenChanged();
        }

        ImGui.SameLine();
        ImGui.Checkbox("Show", ref revealToken);

        ImGui.Spacing();

        var pushOnDispatch = plugin.Configuration.PushOnDispatch;
        if (ImGui.Checkbox("Push at dispatch time (site shows subs at sea)", ref pushOnDispatch))
        {
            plugin.Configuration.PushOnDispatch = pushOnDispatch;
            plugin.Configuration.Save();
        }

        var chatNotifications = plugin.Configuration.ChatNotifications;
        if (ImGui.Checkbox("Chat message on successful push", ref chatNotifications))
        {
            plugin.Configuration.ChatNotifications = chatNotifications;
            plugin.Configuration.Save();
        }

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

    // =========================================================================
    // Mapping — interactive FC/submarine linking
    // =========================================================================

    private void DrawMappingTab()
    {
        ImGui.Spacing();

        using (ImRaii.Disabled(sync.Refreshing))
        {
            if (ImGui.Button("Refresh from server"))
                _ = RefreshSyncAsync();
        }
        ImGui.SameLine();
        using (ImRaii.PushColor(ImGuiCol.Text, MutedColor))
        {
            ImGui.Text(sync.LastAtUtc is { } t
                ? $"inventory from {t:HH:mm:ss} UTC"
                : "no inventory yet — Refresh to load your FCs and subs");
        }

        if (mappingMessage != null)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, WarnColor))
                ImGui.TextWrapped(mappingMessage);
        }

        ImGui.Spacing();
        ImGui.Separator();

        // FCs come from three places: workshop visits this session, existing
        // mappings, and journal entries (covers sim voyages and voyages from
        // sessions before a plugin reload — mappable without re-visiting).
        var seen = snapshots.SeenFcs.Select(f => f.FcId)
                            .Union(plugin.Configuration.FcMappings.Keys)
                            .Union(journal.Snapshot().Select(e => e.FcId))
                            .ToList();
        if (seen.Count == 0)
        {
            ImGui.Spacing();
            using (ImRaii.PushColor(ImGuiCol.Text, MutedColor))
                ImGui.TextWrapped("No game FC seen yet — visit your workshop (or simulate a dispatch) and this tab will populate.");
            return;
        }

        foreach (var fcId in seen)
            DrawFcMapping(fcId);
    }

    private void DrawFcMapping(ulong fcId)
    {
        var config = plugin.Configuration;
        var seenFc = snapshots.SeenFcs.FirstOrDefault(f => f.FcId == fcId);
        var header = seenFc != null
            ? $"FC <{seenFc.Tag}> @ {seenFc.World} — {seenFc.CharacterName}"
            : $"FC {fcId:x} (not seen this session)";

        using var id = ImRaii.PushId(fcId.ToString());
        if (!ImGui.CollapsingHeader(header, ImGuiTreeNodeFlags.DefaultOpen))
            return;

        if (!config.FcMappings.TryGetValue(fcId, out var mapping))
        {
            mapping = new FcMapping();
            config.FcMappings[fcId] = mapping;
        }

        // Skeleton links for every sub seen in the workshop, so rows exist
        // before any linking. Links persist by RegisterTime (renames safe).
        var dirty = false;
        foreach (var snap in snapshots.SubsOf(fcId))
        {
            if (!mapping.Subs.TryGetValue(snap.RegisterTime, out var link))
            {
                mapping.Subs[snap.RegisterTime] = new SubLink { LastKnownName = snap.Name };
                dirty = true;
            }
            else if (link.LastKnownName != snap.Name)
            {
                link.LastKnownName = snap.Name;
                dirty = true;
            }
        }

        // Same for subs known only from the journal (sim voyages, voyages
        // recorded before a reload) — they need rows to be linkable too.
        // Snapshot names win; otherwise the newest journal entry names the row.
        var snapshotRegisters = snapshots.SubsOf(fcId).Select(s => s.RegisterTime).ToHashSet();
        foreach (var entry in journal.Snapshot().Where(e => e.FcId == fcId))
        {
            if (!mapping.Subs.TryGetValue(entry.SubRegisterTime, out var link))
            {
                mapping.Subs[entry.SubRegisterTime] = new SubLink { LastKnownName = entry.SubName };
                dirty = true;
            }
            else if (!snapshotRegisters.Contains(entry.SubRegisterTime)
                     && link.LastKnownName != entry.SubName)
            {
                link.LastKnownName = entry.SubName;
                dirty = true;
            }
        }

        var enabled = mapping.Enabled;
        if (ImGui.Checkbox("Enabled (push this FC's voyages)", ref enabled))
        {
            mapping.Enabled = enabled;
            dirty = true;
        }

        var inventory = sync.Last;
        if (inventory == null)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, MutedColor))
                ImGui.TextWrapped("Refresh from server to choose the platform FC.");
            if (dirty)
                config.Save();
            return;
        }

        AutoMatchOnce(mapping, seenFc, inventory, ref dirty);

        // Platform FC dropdown. Own FCs first, then a "Shared with me" group
        // (each labeled with its owner, since same-named FCs are otherwise
        // indistinguishable). View-only shared FCs are shown but not selectable
        // — the server rejects pushes into them. Zero platform FCs →
        // "(account)" pseudo-target (id 0): sub matching runs against the whole
        // account.
        var sharedFcs = inventory.FreeCompanies.Where(fc => fc.Shared).ToList();

        var selectedFc = mapping.PlatformFcId is > 0
            ? inventory.FreeCompanies.FirstOrDefault(fc => fc.Id == mapping.PlatformFcId)
            : null;
        var currentLabel = mapping.PlatformFcId switch
        {
            null => "— choose —",
            0 => "(account)",
            _ => selectedFc != null ? FcLabel(selectedFc) : $"#{mapping.PlatformFcId} (missing — deleted on site?)",
        };

        ImGui.SetNextItemWidth(260);
        using (var combo = ImRaii.Combo("Platform FC", currentLabel))
        {
            if (combo.Success)
            {
                if (inventory.FreeCompanies.Count == 0)
                {
                    SelectFcOption(0, "(account)", mapping, ref dirty);
                }
                else
                {
                    foreach (var fc in inventory.FreeCompanies.Where(fc => !fc.Shared))
                        SelectFcOption(fc.Id, FcLabel(fc), mapping, ref dirty);

                    if (sharedFcs.Count > 0)
                    {
                        ImGui.Separator();
                        using (ImRaii.PushColor(ImGuiCol.Text, MutedColor))
                            ImGui.Text("Shared with me");

                        foreach (var fc in sharedFcs)
                        {
                            if (fc.ReadOnly)
                            {
                                // View-only membership — never a push target.
                                using (ImRaii.Disabled())
                                using (ImRaii.PushColor(ImGuiCol.Text, MutedColor))
                                    ImGui.Selectable($"{FcLabel(fc)} — view only");
                            }
                            else
                            {
                                SelectFcOption(fc.Id, FcLabel(fc), mapping, ref dirty);
                            }
                        }
                    }
                }
            }
        }

        // A shared FC the user only views (e.g. demoted after mapping it) can't
        // receive pushes — surface it rather than silently dropping voyages.
        if (selectedFc is { ReadOnly: true })
        {
            using (ImRaii.PushColor(ImGuiCol.Text, WarnColor))
                ImGui.TextWrapped("This shared FC is view-only — pushes are rejected by the server. It still shows in the Stats tab.");
        }

        // Mode-visibility warning: pushes would succeed but stay invisible on
        // the website under the current mode. Shared FCs are exempt — the site
        // shows them regardless of the multi-FC toggle.
        if (!inventory.MultiFcMode
            && mapping.PlatformFcId is > 0
            && inventory.FreeCompanies.FirstOrDefault(fc => fc.Id == mapping.PlatformFcId) is { IsPrimary: false, Shared: false })
        {
            using (ImRaii.PushColor(ImGuiCol.Text, WarnColor))
                ImGui.TextWrapped("Pushes will succeed but stay hidden on the website until you enable multi-FC mode or make this FC primary.");
        }

        if (mapping.PlatformFcId != null)
            DrawSubLinks(mapping, inventory, ref dirty);

        if (dirty)
        {
            config.Save();
            // Newly resolvable mappings should push promptly, not in ≤60s.
            outbox.Kick();
        }
    }

    private static string FcLabel(SyncFreeCompany fc)
        => fc.Shared
            ? $"{fc.Name} — {fc.OwnerName ?? "shared"}"
            : $"{fc.Name}{(fc.IsPrimary ? " ★" : "")}";

    /// <summary>Renders one platform-FC choice; on a change, re-points the
    /// mapping and clears the sub links (they point into the old FC's subs).</summary>
    private void SelectFcOption(int pid, string label, FcMapping mapping, ref bool dirty)
    {
        if (!ImGui.Selectable(label, mapping.PlatformFcId == pid) || mapping.PlatformFcId == pid)
            return;

        mapping.PlatformFcId = pid;
        foreach (var link in mapping.Subs.Values)
            link.PlatformSubmarineId = null;
        dirty = true;
    }

    private void DrawSubLinks(FcMapping mapping, SyncResponse inventory, ref bool dirty)
    {
        // Sub matching runs ONLY inside the chosen platform FC (or the whole
        // account for "(account)") — same-named subs in different FCs can never
        // cross-link.
        var scope = mapping.PlatformFcId == 0
            ? inventory.Submarines
            : inventory.Submarines.Where(s => s.FreeCompanyId == mapping.PlatformFcId).ToList();

        ImGui.Spacing();
        foreach (var (register, link) in mapping.Subs.OrderBy(kv => kv.Value.LastKnownName))
        {
            using var subId = ImRaii.PushId((int)register);

            var subEnabled = link.Enabled;
            if (ImGui.Checkbox("##enabled", ref subEnabled))
            {
                link.Enabled = subEnabled;
                dirty = true;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Push this submarine's voyages");

            ImGui.SameLine();
            ImGui.Text(link.LastKnownName);
            ImGui.SameLine(220);

            if (link.PlatformSubmarineId == null)
            {
                // Auto-link: exactly one case-insensitive name match in scope.
                var matches = scope
                              .Where(s => string.Equals(s.Name, link.LastKnownName, StringComparison.OrdinalIgnoreCase))
                              .ToList();
                if (matches.Count == 1)
                {
                    link.PlatformSubmarineId = matches[0].Id;
                    dirty = true;
                    Plugin.Log.Information($"Auto-linked sub '{link.LastKnownName}' → platform #{matches[0].Id}");
                }
            }

            var current = scope.FirstOrDefault(s => s.Id == link.PlatformSubmarineId);
            var preview = link.PlatformSubmarineId == null
                ? "— not linked —"
                : current?.Name ?? $"#{link.PlatformSubmarineId} (missing — re-link)";

            ImGui.SetNextItemWidth(200);
            using (var combo = ImRaii.Combo("##link", preview))
            {
                if (combo.Success)
                {
                    foreach (var s in scope)
                    {
                        if (ImGui.Selectable($"{s.Name}##{s.Id}", link.PlatformSubmarineId == s.Id)
                            && link.PlatformSubmarineId != s.Id)
                        {
                            link.PlatformSubmarineId = s.Id;
                            dirty = true;
                        }
                    }
                }
            }

            if (link.PlatformSubmarineId == null)
            {
                var scopeName = mapping.PlatformFcId == 0
                    ? "your account"
                    : $"FC '{inventory.FreeCompanies.FirstOrDefault(f => f.Id == mapping.PlatformFcId)?.Name}'";
                ImGui.SameLine();
                using (ImRaii.PushColor(ImGuiCol.Text, WarnColor))
                    ImGui.Text("(?)");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"Create a submarine named '{link.LastKnownName}' in {scopeName} on the website, then Refresh. Multiple same-named subs need a manual pick.");
                ImGui.SameLine();
                if (ImGui.SmallButton("copy name"))
                    ImGui.SetClipboardText(link.LastKnownName);
            }
            else if (current is { Shared: false })
            {
                // Collision nudge: this in-game sub is linked to a sub in one of
                // your OWN FCs, but a shared FC offers a same-named sub. The user
                // may want to contribute to the shared FC instead — hint only,
                // never auto-switch. View-only FCs are excluded: they aren't
                // selectable as a push target, so the advice would be impossible.
                var sharedTwin = inventory.Submarines.FirstOrDefault(
                    s => s.Shared && !s.ReadOnly
                         && string.Equals(s.Name, link.LastKnownName, StringComparison.OrdinalIgnoreCase));
                if (sharedTwin != null)
                {
                    var sharedFc = inventory.FreeCompanies.FirstOrDefault(f => f.Id == sharedTwin.FreeCompanyId);
                    ImGui.SameLine();
                    using (ImRaii.PushColor(ImGuiCol.Text, WarnColor))
                        ImGui.Text("(shared?)");
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(
                            $"Already linked to your own FC, but '{link.LastKnownName}' also exists in the shared FC "
                            + $"'{sharedFc?.Name}'{(sharedFc?.OwnerName is { } o ? $" ({o})" : "")}. "
                            + "Switch Platform FC above to re-point it to the shared FC.");
                }
            }
        }
    }

    /// <summary>Best-effort FC preselect, once per sync refresh: single
    /// case-insensitive match of the in-game tag against platform FC names.
    /// Convenience only — the user still has to tick Enabled.</summary>
    private void AutoMatchOnce(FcMapping mapping, SeenFc? seenFc, SyncResponse inventory, ref bool dirty)
    {
        if (mapping.PlatformFcId != null || seenFc == null || lastAutoMatchAt == sync.LastAtUtc)
            return;

        lastAutoMatchAt = sync.LastAtUtc;
        var matches = inventory.FreeCompanies
                               .Where(fc => string.Equals(fc.Name, seenFc.Tag, StringComparison.OrdinalIgnoreCase))
                               .ToList();
        if (matches.Count == 1)
        {
            mapping.PlatformFcId = matches[0].Id;
            dirty = true;
            Plugin.Log.Information($"Auto-matched game FC <{seenFc.Tag}> → platform FC '{matches[0].Name}' (preselect only)");
        }
    }

    private async System.Threading.Tasks.Task RefreshSyncAsync()
    {
        mappingMessage = await sync.RefreshAsync().ConfigureAwait(false);
    }

    // =========================================================================
    // Stats — read-only in-game view of the website's aggregates
    // =========================================================================

    private void DrawStatsTab()
    {
        ImGui.Spacing();
        ImGui.TextWrapped("Your voyage stats from the website, computed server-side (completed voyages only).");
        ImGui.Spacing();

        var inventory = sync.Last;

        // Scope selector: the account default, or a specific FC (own or shared,
        // including view-only ones — stats read even where pushing doesn't).
        var scopeLabel = statsScopeFcId is { } scopeId
            ? inventory?.FreeCompanies.FirstOrDefault(f => f.Id == scopeId) is { } scopeFc ? FcLabel(scopeFc) : $"#{scopeId}"
            : "My default scope";
        ImGui.SetNextItemWidth(260);
        using (var combo = ImRaii.Combo("Scope", scopeLabel))
        {
            if (combo.Success)
            {
                if (ImGui.Selectable("My default scope", statsScopeFcId == null))
                    statsScopeFcId = null;
                if (inventory != null)
                {
                    foreach (var fc in inventory.FreeCompanies)
                    {
                        if (ImGui.Selectable($"{FcLabel(fc)}##{fc.Id}", statsScopeFcId == fc.Id))
                            statsScopeFcId = fc.Id;
                    }
                }
            }
        }
        if (inventory == null)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, MutedColor))
                ImGui.TextWrapped("Refresh the Mapping tab to list your FCs here; the default scope works without it.");
        }

        using (ImRaii.Disabled(statsLoading))
        {
            if (ImGui.Button("Load stats"))
                LoadStats();
        }
        if (statsLoading)
        {
            ImGui.SameLine();
            ImGui.Text("Loading...");
        }
        else if (statsLoadedAtUtc is { } loadedAt)
        {
            ImGui.SameLine();
            using (ImRaii.PushColor(ImGuiCol.Text, MutedColor))
                ImGui.Text($"loaded {loadedAt:HH:mm:ss} UTC");
        }

        if (statsError != null)
        {
            ImGui.Spacing();
            using (ImRaii.PushColor(ImGuiCol.Text, ErrorColor))
                ImGui.TextWrapped(statsError);
            return;
        }

        if (statsResult is not { } stats)
        {
            ImGui.Spacing();
            using (ImRaii.PushColor(ImGuiCol.Text, MutedColor))
                ImGui.TextWrapped("No stats loaded yet — pick a scope and Load.");
            return;
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.Text($"Completed voyages: {stats.Totals.Voyages:N0}  ({stats.Totals.Voyages7d:N0} in the last 7 days)");
        ImGui.Text($"Total gil: {stats.Totals.Gil:N0}  ({stats.Totals.Gil7d:N0} in the last 7 days)");
        ImGui.Spacing();

        if (stats.PerSubmarine.Count > 0)
        {
            ImGui.Text("By submarine");
            using var table = ImRaii.Table("StatsSubTable", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV);
            if (table.Success)
            {
                ImGui.TableSetupColumn("Submarine", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Voyages", ImGuiTableColumnFlags.WidthFixed, 70);
                ImGui.TableSetupColumn("Total gil", ImGuiTableColumnFlags.WidthFixed, 110);
                ImGui.TableSetupColumn("Gil/route hr", ImGuiTableColumnFlags.WidthFixed, 100);
                ImGui.TableHeadersRow();
                foreach (var s in stats.PerSubmarine)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn(); ImGui.Text(s.Name);
                    ImGui.TableNextColumn(); ImGui.Text($"{s.Voyages:N0}");
                    ImGui.TableNextColumn(); ImGui.Text($"{s.TotalGil:N0}");
                    ImGui.TableNextColumn(); ImGui.Text($"{s.GilPerRouteHour:N0}");
                }
            }
            ImGui.Spacing();
        }

        if (stats.PerRoute.Count > 0)
        {
            ImGui.Text("By route");
            using var table = ImRaii.Table("StatsRouteTable", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV);
            if (table.Success)
            {
                ImGui.TableSetupColumn("Route", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Voyages", ImGuiTableColumnFlags.WidthFixed, 70);
                ImGui.TableSetupColumn("Avg gil", ImGuiTableColumnFlags.WidthFixed, 110);
                ImGui.TableSetupColumn("Gil/route hr", ImGuiTableColumnFlags.WidthFixed, 100);
                ImGui.TableHeadersRow();
                foreach (var r in stats.PerRoute)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn(); ImGui.Text(r.Route);
                    ImGui.TableNextColumn(); ImGui.Text($"{r.Voyages:N0}");
                    ImGui.TableNextColumn(); ImGui.Text($"{r.AvgGil:N0}");
                    ImGui.TableNextColumn(); ImGui.Text($"{r.GilPerRouteHour:N0}");
                }
            }
        }

        if (stats.PerSubmarine.Count == 0 && stats.PerRoute.Count == 0)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, MutedColor))
                ImGui.TextWrapped("No completed voyages in this scope yet.");
        }
    }

    private async void LoadStats()
    {
        statsLoading = true;
        statsError = null;
        var scope = statsScopeFcId;

        try
        {
            using var client = new ApiClient(plugin.Configuration.ServerUrl, plugin.Configuration.ApiToken);
            statsResult = await client.StatsAsync(scope).ConfigureAwait(false);
            statsLoadedAtUtc = DateTime.UtcNow;
        }
        catch (UnauthorizedException)
        {
            statsError = "Token rejected by the server. Check the token on the Status tab.";
        }
        catch (Exception ex)
        {
            statsError = $"Failed to load stats: {ex.Message}";
            Plugin.Log.Warning(ex, "Stats load failed");
        }
        finally
        {
            statsLoading = false;
        }
    }

    // =========================================================================
    // Log
    // =========================================================================

    private void DrawLogTab()
    {
        ImGui.Spacing();

        var entries = journal.SnapshotNewestFirst();
        var queued = entries.Count(e => e.State == OutboxState.Queued);
        var failed = entries.Count(e => e.State == OutboxState.Failed);
        var pending = entries.Count(e => e.State == OutboxState.PendingCollection);
        var sent = entries.Count(e => e.State == OutboxState.Sent);

        ImGui.TextWrapped(
            $"Voyage journal: {entries.Count} entries — {pending} at sea, {queued} queued, "
            + $"{sent} sent, {failed} need attention.");
        if (outbox.Paused)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, ErrorColor))
                ImGui.TextWrapped($"⚠ Pushes paused: {outbox.PausedReason}");
        }
        else if (outbox.LastDrainUtc is { } drained)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, MutedColor))
                ImGui.TextWrapped($"Last push {drained:HH:mm:ss} UTC — {outbox.LastDrainSummary}");
        }
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

        ImGui.TableSetupColumn("Time (UTC)", ImGuiTableColumnFlags.WidthFixed, 100);
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
                OutboxState.Queued => ("queued", WarnColor),
                OutboxState.Sent when entry.PushStatus == "enriched" => ("enriched ✦", SuccessColor),
                OutboxState.Sent => ($"sent ({entry.PushStatus})", SuccessColor),
                OutboxState.Failed => ("needs attention", ErrorColor),
                OutboxState.Skipped => ("skipped", MutedColor),
                _ => (entry.State.ToString(), MutedColor),
            };
            using (ImRaii.PushColor(ImGuiCol.Text, stateColor))
                ImGui.Text(stateText);

            ImGui.TableNextColumn();
            DrawEntryDetail(entry);
        }
    }

    private void DrawEntryDetail(JournalEntry entry)
    {
        using var id = ImRaii.PushId(entry.Id.ToString());

        if (entry.State == OutboxState.Failed && entry.BuiltRow == null && entry.PendingLoot != null)
        {
            ImGui.Text(entry.FailureMessage ?? "");
            ImGui.SameLine();
            if (ImGui.SmallButton("Estimate & queue"))
            {
                // Sim entries have no real build to estimate from — they use
                // the Simulator tab's duration; real entries never do.
                var error = assembler.EstimateAndQueue(
                    entry, entry.Simulated ? simDurationMinutes : 0);
                if (error != null)
                    Plugin.Log.Warning($"Estimate & queue: {error}");
                else
                    outbox.Kick();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Builds the row with an estimated dispatch time (marked [EST]). Nothing is estimated without this explicit click.");
            return;
        }

        if (entry.State is OutboxState.Failed or OutboxState.Skipped)
        {
            ImGui.Text(entry.FailureMessage ?? "");
            if (entry.BuiltRow != null)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Retry"))
                    outbox.Retry(entry);
            }
            return;
        }

        if (entry.UnmatchedItems is { Count: > 0 } unmatched)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, WarnColor))
                ImGui.Text($"⚠ unmatched: {string.Join(", ", unmatched)}");
            return;
        }

        if (entry.WaitReason != null)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, MutedColor))
                ImGui.Text(entry.WaitReason);
            return;
        }

        if (entry.BuiltRow != null)
        {
            ImGui.Text($"{entry.BuiltRow.Loot.Count} loot line(s), {entry.BuiltRow.CeruleumTanks} tanks");
            return;
        }

        ImGui.Text($"returns {DateTimeOffset.FromUnixTimeSeconds(entry.ScheduledReturnUnix).UtcDateTime:MM-dd HH:mm}"
                   + (entry.DispatchPushed ? " · on site" : ""));
    }

    // =========================================================================
    // Backfill (SubmarineTracker history → platform CSV)
    // =========================================================================

    private void DrawBackfillTab()
    {
        ImGui.Spacing();
        ImGui.TextWrapped(
            "Exports your SubmarineTracker voyage history as a CSV for the website's Import "
            + "dialog. Voyages are matched through the Mapping tab, so map your FC(s) and subs "
            + "first. Dispatch times are estimated (loot history only stores returns) — rows "
            + "are stamped accordingly.");
        ImGui.Spacing();

        ImGui.SetNextItemWidth(420);
        ImGui.InputText("##dbpath", ref backfillDbPath, 512);
        ImGui.SameLine();
        if (ImGui.Button("Pick DB…"))
        {
            fileDialog.OpenFileDialog("SubmarineTracker database", ".db", (ok, path) =>
            {
                if (ok && !string.IsNullOrEmpty(path))
                    backfillDbPath = path;
            });
        }
        if (!File.Exists(backfillDbPath))
        {
            using (ImRaii.PushColor(ImGuiCol.Text, WarnColor))
                ImGui.TextWrapped("File not found — has SubmarineTracker run on this machine?");
        }

        ImGui.SetNextItemWidth(120);
        ImGui.InputTextWithHint("##from", "from YYYY-MM-DD", ref backfillFrom, 10);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120);
        ImGui.InputTextWithHint("##to", "to YYYY-MM-DD", ref backfillTo, 10);
        ImGui.SameLine();
        using (ImRaii.PushColor(ImGuiCol.Text, MutedColor))
            ImGui.Text("(collection date, UTC; empty = everything)");

        using (ImRaii.PushColor(ImGuiCol.Text, WarnColor))
        {
            ImGui.TextWrapped(
                "Import may duplicate voyages you already entered manually with different "
                + "timestamps (import dedup is exact-match). Pick a cutoff date before your "
                + "platform history starts.");
        }
        ImGui.Spacing();

        using (ImRaii.Disabled(backfillBusy))
        {
            if (ImGui.Button("Scan"))
                RunBackfillScan();
        }

        if (backfillBusy)
        {
            ImGui.SameLine();
            ImGui.Text("Scanning...");
        }

        if (backfillScan is { } scan)
        {
            ImGui.Spacing();
            ImGui.Text($"{scan.TotalVoyages} voyage(s) found"
                       + (scan.OldestUtc is { } o ? $" ({o:yyyy-MM-dd} → {scan.NewestUtc:yyyy-MM-dd})" : "")
                       + $" — {scan.Mappable.Count} mappable, {scan.SkippedUnmapped} skipped (unmapped FC/sub).");

            using (ImRaii.Disabled(scan.Mappable.Count == 0))
            {
                if (ImGui.Button("Export CSV…"))
                {
                    var rows = scan.Mappable;
                    fileDialog.SaveFileDialog(
                        "Save backfill CSV", ".csv", "bunnysub_backfill.csv", ".csv", (ok, path) =>
                        {
                            if (!ok)
                                return;
                            try
                            {
                                File.WriteAllText(path, BackfillService.BuildCsv(rows));
                                backfillMessageIsError = false;
                                backfillMessage = $"Wrote {rows.Count} voyage(s) to {path}. Upload it via the website's "
                                                  + "Import dialog — preview first, and tick the backfill option there.";
                            }
                            catch (Exception ex)
                            {
                                backfillMessageIsError = true;
                                backfillMessage = $"Write failed: {ex.Message}";
                            }
                        });
                }
            }
        }

        if (backfillMessage != null)
        {
            ImGui.Spacing();
            using (ImRaii.PushColor(ImGuiCol.Text, backfillMessageIsError ? ErrorColor : SuccessColor))
                ImGui.TextWrapped(backfillMessage);
        }
    }

    private void RunBackfillScan()
    {
        if (!TryParseBackfillDate(backfillFrom, endOfDay: false, out var fromUtc)
            || !TryParseBackfillDate(backfillTo, endOfDay: true, out var toUtc))
        {
            backfillMessageIsError = true;
            backfillMessage = "Dates must be YYYY-MM-DD (or empty).";
            return;
        }

        backfillBusy = true;
        backfillMessage = null;
        backfillScan = null;
        var dbPath = backfillDbPath;

        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                backfillScan = backfill.Scan(dbPath, fromUtc, toUtc);
                backfillMessageIsError = false;
            }
            catch (Exception ex)
            {
                backfillMessageIsError = true;
                backfillMessage = ex.Message;
                Plugin.Log.Warning(ex, "Backfill scan failed");
            }
            finally
            {
                backfillBusy = false;
            }
        });
    }

    private static bool TryParseBackfillDate(string input, bool endOfDay, out DateTime? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(input))
            return true;

        if (!DateTime.TryParseExact(
                input.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
            return false;

        parsed = endOfDay ? date.AddDays(1).AddSeconds(-1) : date;
        return true;
    }

    // =========================================================================
    // Simulator (DevMode only)
    // =========================================================================

    private void DrawSimulatorTab()
    {
        ImGui.Spacing();

        if (plugin.Configuration.ServerUrl.TrimEnd('/') == ProdServerUrl)
        {
            using var color = ImRaii.PushColor(ImGuiCol.Text, WarnColor);
            ImGui.TextWrapped(
                "⚠ Server URL is the production site. Simulated voyages WILL be pushed to "
                + "PRODUCTION if mapped — point at a local server, or plan on deleting [SIM] "
                + "rows from the website.");
            ImGui.Spacing();
        }

        ImGui.TextWrapped("Emits the same internal events as the real capture layer — everything downstream (journal, assembly, outbox, pushes) is exercised for real.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.SetNextItemWidth(160);
        ImGui.InputText("FC id (hex)", ref simFcIdHex, 16, ImGuiInputTextFlags.CharsHexadecimal);
        ImGui.SetNextItemWidth(160);
        if (ImGui.InputText("Sub name", ref simSubName, 64))
        {
            // A sub's identity is its register time. Deriving it from the
            // name means "different name = different sim sub" — otherwise
            // every sim run collapses into one mapping row named after the
            // first voyage (found the hard way during testing).
            simRegisterTime = StableRegisterFor(simSubName);
        }
        ImGui.SetNextItemWidth(160);
        ImGui.InputInt("Sub id", ref simRegisterTime, 0);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("The sub's stable identity (the game calls it RegisterTime — its registration timestamp; nothing to do with voyage times). Follows the name automatically; override only to deliberately reuse or avoid an identity.");

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

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        if (ImGui.Button("Clear [SIM] journal entries"))
        {
            var removed = journal.RemoveSimulated();
            SetSimMessage(null);
            simMessage = $"Removed {removed} simulated journal entr{(removed == 1 ? "y" : "ies")}.";
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Journal only — rows already pushed to the server stay there (delete [SIM] rows on the website).");
    }

    /// <summary>Deterministic register for a sim sub name (don't use
    /// string.GetHashCode — it's randomized per process).</summary>
    private static int StableRegisterFor(string name)
    {
        var hash = 17u;
        foreach (var c in name.Trim().ToUpperInvariant())
            hash = hash * 31 + c;
        return (int)(hash & 0x3FFFFFFF) | 1;
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
            var syncResponse = await client.SyncAsync().ConfigureAwait(false);

            statusIsError = false;
            statusMessage =
                $"Linked as {syncResponse.User.Name} — {syncResponse.FreeCompanies.Count} FC(s), " +
                $"{syncResponse.Submarines.Count} submarine(s), {syncResponse.SalvageItems.Count} catalog item(s).";
            Plugin.Log.Information(statusMessage);

            // The mapping tab feeds off the same inventory — refresh it too.
            _ = sync.RefreshAsync();
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
