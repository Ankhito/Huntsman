using Dalamud.Game.Command;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace Huntsman;

internal sealed class PluginServices(
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
    public IDalamudPluginInterface PluginInterface { get; } = pluginInterface;
    public ICommandManager Commands { get; } = commands;
    public IClientState ClientState { get; } = clientState;
    public IObjectTable Objects { get; } = objects;
    public ITargetManager Targets { get; } = targets;
    public IDataManager Data { get; } = data;
    public ICondition Condition { get; } = condition;
    public IFramework Framework { get; } = framework;
    public IChatGui Chat { get; } = chat;
    public IPluginLog Log { get; } = log;
}
