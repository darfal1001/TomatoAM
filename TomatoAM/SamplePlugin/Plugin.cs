using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace SamplePlugin;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] public static IPluginLog Log { get; private set; } = null!;

    private const string CommandName     = "/p3";
    private const string CommandNameTest = "/kefkatest";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly KefkaTF logic;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IObjectTable objectTable,
        IPluginLog log,
        IFramework framework,
        IDataManager dataManager)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager  = commandManager;

        logic = new KefkaTF(log, objectTable, framework, dataManager);

        commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Manually trigger mechanic detection"
        });
        commandManager.AddHandler(CommandNameTest, new CommandInfo(OnTestCommand)
        {
            HelpMessage = "Reset pull dedup counter"
        });
    }

    private void OnCommand(string command, string args)     => Log.Info("[KefkaTF] Listening...");
    private void OnTestCommand(string command, string args) => logic.ResetPull();

    public void Dispose()
    {
        commandManager.RemoveHandler(CommandName);
        commandManager.RemoveHandler(CommandNameTest);
        logic.Dispose();
    }
}