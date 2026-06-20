using Dalamud.Game.ClientState.Party;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace SamplePlugin;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] public static IPluginLog Log { get; private set; } = null!;


    private const string CommandNameTest       = "/kefkatest";
    private const string CommandNamePhase4     = "/p4";
    private const string CommandNamePhase4Test = "/kefkatest4";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly KefkaTF logic;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IObjectTable objectTable,
        IPluginLog log,
        IFramework framework,
        IDataManager dataManager,
        IChatGui chatGui,
        IPartyList partyList,
        ISigScanner sigScanner)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager  = commandManager;

        logic = new KefkaTF(log, objectTable, framework, dataManager, chatGui, partyList, sigScanner);

        commandManager.AddHandler(CommandNameTest, new CommandInfo(OnTestCommand)
        {
            HelpMessage = "Reset pull dedup counter"
        });
        commandManager.AddHandler(CommandNamePhase4, new CommandInfo(OnPhase4Command)
        {
            HelpMessage = "Scan party for Phase 4 (Kefka Says) debuffs and apply head markers"
        });
        commandManager.AddHandler(CommandNamePhase4Test, new CommandInfo(OnPhase4TestCommand)
        {
            HelpMessage = "Run a fake Phase 4 test with randomised debuffs (no real party needed)"
        });
    }

    private void OnCommand(string command, string args)         => Log.Info("[KefkaTF] Listening...");
    private void OnTestCommand(string command, string args)     => logic.ResetPull();
    private void OnPhase4Command(string command, string args)   => logic.TriggerPhase4Scan();
    private void OnPhase4TestCommand(string command, string args) => logic.RunPhase4Test();

    public void Dispose()
    {
        commandManager.RemoveHandler(CommandNameTest);
        commandManager.RemoveHandler(CommandNamePhase4);
        commandManager.RemoveHandler(CommandNamePhase4Test);
        logic.Dispose();
    }
}