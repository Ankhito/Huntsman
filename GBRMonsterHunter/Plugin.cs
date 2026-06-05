using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using GBRMonsterHunter.Automation;
using GBRMonsterHunter.IPC;
using GBRMonsterHunter.Planning;
using GBRMonsterHunter.UI;

namespace GBRMonsterHunter;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/gbrmh";

    private readonly PluginServices services;
    private readonly Configuration config;
    private readonly GatherBuddyRebornIpc gbr;
    private readonly MainWindow window;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commands,
        IClientState clientState,
        IObjectTable objects,
        ITargetManager targets,
        IDataManager data,
        ICondition condition,
        IFramework framework,
        IChatGui chat,
        IPluginLog log)
    {
        services = new PluginServices(pluginInterface, commands, clientState, objects, targets, data, condition, framework, chat, log);
        config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        config.Initialize(pluginInterface);

        var logger = new ThrottledLogger(services);
        gbr = new GatherBuddyRebornIpc(services, logger);
        var lifestream = new LifestreamIpc(services, logger);
        var vnavmesh = new VnavmeshIpc(services, logger);
        var rotationSolver = new RotationSolverRebornIpc(services, logger);
        var wrathCombo = new WrathComboIpc(services, logger);
        var rotationDriver = new RotationDriverService(config, rotationSolver, wrathCombo);
        var commandBridge = new CommandBridge(services);
        var vulcan = new VulcanReflectionAdapter(services);
        var dropLocations = new DropLocationProvider(services);
        var planner = new MaterialPlanner(services, dropLocations);
        var dropHuntList = new DropHuntListManager(dropLocations);
        var combatJobs = new CombatJobService(services, config);
        var monsterRoutePlanner = new MonsterRoutePlanner(services);
        var monsterNavigator = new MonsterNavigator(services, config, lifestream, vnavmesh, rotationDriver, commandBridge, monsterRoutePlanner);
        var automation = new VulcanDropAutomation(gbr, vulcan, planner, dropHuntList, combatJobs, monsterNavigator);
        window = new MainWindow(config, gbr, lifestream, vnavmesh, rotationDriver, monsterNavigator, dropHuntList, automation, combatJobs);

        commands.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open GBR Monster Hunter.",
        });

        pluginInterface.UiBuilder.Draw += Draw;
        pluginInterface.UiBuilder.OpenConfigUi += OpenConfig;
        framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        services.PluginInterface.UiBuilder.Draw -= Draw;
        services.PluginInterface.UiBuilder.OpenConfigUi -= OpenConfig;
        services.Framework.Update -= OnFrameworkUpdate;
        services.Commands.RemoveHandler(CommandName);
        window.Dispose();
    }

    private void Draw() => window.Draw();

    private void OnFrameworkUpdate(IFramework framework) => window.Update();

    private void OpenConfig() => window.IsOpen = true;

    private void OnCommand(string command, string args)
    {
        var arg = args.Trim().ToLowerInvariant();
        switch (arg)
        {
            case "":
            case "open":
                window.IsOpen = true;
                break;
            case "gbr on":
                gbr.SetAutoGatherEnabled(true);
                services.Chat.Print("GBRMonsterHunter: GBR auto-gather enabled.");
                break;
            case "gbr off":
                gbr.SetAutoGatherEnabled(false);
                services.Chat.Print("GBRMonsterHunter: GBR auto-gather disabled.");
                break;
            case "status":
                gbr.RefreshAvailability();
                window.RefreshDependencies();
                services.Chat.Print(window.BuildStatusLine());
                break;
            case "stop":
                window.StopAutomation();
                services.Chat.Print("GBRMonsterHunter: stopped navigation and dependency automation.");
                break;
            default:
                services.Chat.Print("Usage: /gbrmh [open|status|stop|gbr on|gbr off]");
                break;
        }
    }
}
