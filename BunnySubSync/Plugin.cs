using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using BunnySubSync.Game;
using BunnySubSync.Windows;

namespace BunnySubSync;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInteropProvider { get; private set; } = null!;

    private const string CommandName = "/bunnysync";

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("Bunny Sub Sync");
    private MainWindow MainWindow { get; init; }

    // G2 capture layer. Producers (snapshot service, result hook, simulator)
    // emit onto VoyageEvents; the assembler + journal live below the seam.
    private readonly VoyageEvents voyageEvents;
    private readonly VoyageJournal voyageJournal;
    private readonly SubSnapshotService subSnapshots;
    private readonly VoyageResultHook voyageResultHook;
    private readonly VoyageAssembler voyageAssembler;
    private readonly VoyageSimulator voyageSimulator;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        voyageEvents = new VoyageEvents();
        voyageJournal = new VoyageJournal(PluginInterface.GetPluginConfigDirectory());
        subSnapshots = new SubSnapshotService(voyageEvents);
        voyageAssembler = new VoyageAssembler(voyageEvents, voyageJournal, subSnapshots);
        voyageResultHook = new VoyageResultHook(voyageEvents);
        voyageSimulator = new VoyageSimulator(voyageEvents);

        MainWindow = new MainWindow(this, voyageJournal, voyageAssembler, voyageSimulator);
        WindowSystem.AddWindow(MainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Opens the Bunny Sub Sync window. Dev: /bunnysync sim dispatch|collect|collect-new"
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        WindowSystem.RemoveAllWindows();
        MainWindow.Dispose();

        // Producers first (stop emitting), then the consumer.
        voyageResultHook.Dispose();
        subSnapshots.Dispose();
        voyageAssembler.Dispose();

        CommandManager.RemoveHandler(CommandName);
    }

    private void OnCommand(string command, string args)
    {
        var trimmed = args.Trim();
        if (trimmed.Length == 0)
        {
            MainWindow.Toggle();
            return;
        }

        // /bunnysync sim … — quick re-runs of the Simulator tab's current
        // inputs (dev-gated exactly like the tab itself).
        var parts = trimmed.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        if (parts[0] == "sim" && parts.Length > 1)
        {
            if (!Configuration.DevMode)
            {
                ChatGui.PrintError("[BunnySubSync] Simulator requires DevMode (config JSON).");
                return;
            }

            var error = parts[1] switch
            {
                "dispatch" => MainWindow.RunSimDispatch(),
                "collect" => MainWindow.RunSimCollect(forLastDispatch: true),
                "collect-new" => MainWindow.RunSimCollect(forLastDispatch: false),
                _ => $"Unknown sim action '{parts[1]}' (dispatch|collect|collect-new).",
            };

            ChatGui.Print(error == null
                ? "[BunnySubSync] Simulated — see the Log tab."
                : $"[BunnySubSync] {error}");
            return;
        }

        MainWindow.Toggle();
    }

    public void ToggleMainUi() => MainWindow.Toggle();
}
