using Dalamud.Plugin.Ipc;

namespace Huntsman.IPC;

internal sealed class LifestreamIpc : IpcAdapterBase
{
    private readonly ICallGateSubscriber<string, object> executeCommand;
    private readonly ICallGateSubscriber<bool> isBusy;
    private readonly ICallGateSubscriber<object> abort;
    private readonly ICallGateSubscriber<string, bool> aethernetTeleport;

    public LifestreamIpc(PluginServices services, ThrottledLogger logger)
        : base(services, logger)
    {
        var pi = services.PluginInterface;
        executeCommand = pi.GetIpcSubscriber<string, object>("Lifestream.ExecuteCommand");
        isBusy = pi.GetIpcSubscriber<bool>("Lifestream.IsBusy");
        abort = pi.GetIpcSubscriber<object>("Lifestream.Abort");
        aethernetTeleport = pi.GetIpcSubscriber<string, bool>("Lifestream.AethernetTeleport");
    }

    public override void RefreshAvailability() =>
        SetAvailability(
            "Lifestream IPC providers not found",
            () => executeCommand.HasAction,
            () => isBusy.HasFunction,
            () => abort.HasAction);

    public bool IsBusy() => TryCall(nameof(IsBusy), () => isBusy.InvokeFunc(), out var busy) && busy;

    public bool ExecuteCommand(string destination) =>
        TryAction(nameof(ExecuteCommand), () => executeCommand.InvokeAction(destination));

    public bool AethernetTeleport(string destination) =>
        aethernetTeleport.HasFunction
            ? TryCall(nameof(AethernetTeleport), () => aethernetTeleport.InvokeFunc(destination), out var ok) && ok
            : ExecuteCommand(destination);

    public void Abort() => TryAction(nameof(Abort), () => abort.InvokeAction());
}
