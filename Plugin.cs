using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using LootHunter.Automation;
using LootHunter.Data;
using LootHunter.IPC;
using LootHunter.Services;
using LootHunter.Windows;

namespace LootHunter;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/loothunter";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static IAetheryteList AetheryteList { get; private set; } = null!;
    [PluginService] internal static IDutyState DutyState { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IGameInventory GameInventory { get; private set; } = null!;

    public Configuration Configuration { get; }
    public LootListService LootLists { get; }
    public MobDropDatabase MobDatabase { get; }
    public FarmController FarmController { get; }
    public InventoryService Inventory { get; }

    private readonly WindowSystem windowSystem = new("LootHunter");
    private readonly MainWindow mainWindow;
    private readonly ConfigWindow configWindow;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        LootLists = new LootListService(Configuration);
        MobDatabase = new MobDropDatabase(DataManager, AetheryteList, Log);
        _ = MobDatabase.InitializeAsync(Framework);

        Inventory = new InventoryService(GameInventory);
        var planner = new RoutePlanner(MobDatabase);
        var safety = new LevelSafetyService(PlayerState, Configuration);
        var travel = new TravelService(PluginInterface, ClientState, Configuration);
        var navigation = new NavigationService(PluginInterface, ObjectTable, Configuration);
        var targetService = new TargetService(ObjectTable, TargetManager);
        var mount = new MountService(ObjectTable, Log);
        var combat = new CombatProvider(PluginInterface, TargetManager, Configuration);

        FarmController = new FarmController(
            Configuration,
            Inventory,
            MobDatabase,
            planner,
            safety,
            travel,
            navigation,
            mount,
            targetService,
            combat,
            ClientState,
            ObjectTable,
            DutyState,
            Framework,
            Log);

        mainWindow = new MainWindow(this);
        configWindow = new ConfigWindow(this);
        windowSystem.AddWindow(mainWindow);
        windowSystem.AddWindow(configWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open LootHunter.",
        });

        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;

        Log.Information("LootHunter initialized.");
    }

    public void Dispose()
    {
        FarmController.Stop();
        MobDatabase.Dispose();
        Inventory.Dispose();
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        CommandManager.RemoveHandler(CommandName);
        windowSystem.RemoveAllWindows();
    }

    private void OnCommand(string command, string args) => ToggleMainUi();
    public void ToggleMainUi() => mainWindow.Toggle();
    public void ToggleConfigUi() => configWindow.Toggle();
}
