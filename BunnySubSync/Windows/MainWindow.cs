using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using BunnySubSync.Api;

namespace BunnySubSync.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    private string serverUrlInput;
    private string apiTokenInput;
    private bool revealToken;

    private bool isTesting;
    private string? statusMessage;
    private bool statusIsError;

    public MainWindow(Plugin plugin)
        : base("Bunny Sub Sync##BunnySubSyncMainWindow")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 320),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        this.plugin = plugin;
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
            using var color = ImRaii.PushColor(
                ImGuiCol.Text,
                statusIsError ? new Vector4(1f, 0.4f, 0.4f, 1f) : new Vector4(0.4f, 1f, 0.4f, 1f));
            ImGui.TextWrapped(statusMessage);
        }
    }

    private static void DrawMappingTab()
    {
        ImGui.Spacing();
        ImGui.TextWrapped("FC and submarine mapping will live here (Phase G3).");
    }

    private static void DrawLogTab()
    {
        ImGui.Spacing();
        ImGui.TextWrapped("Push history and errors will appear here (Phase G3).");
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
