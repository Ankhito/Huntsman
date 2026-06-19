using Dalamud.Plugin.Ipc;

namespace Huntsman.IPC;

internal sealed class WrathComboIpc : IpcAdapterBase, IRotationDriver
{
    private readonly ICallGateSubscriber<bool> ipcReady;
    private readonly ICallGateSubscriber<bool> autoRotationState;
    private readonly ICallGateSubscriber<bool> currentJobAutoRotationReady;

    public WrathComboIpc(PluginServices services, ThrottledLogger logger)
        : base(services, logger)
    {
        var pi = services.PluginInterface;
        ipcReady = pi.GetIpcSubscriber<bool>("WrathCombo.IPCReady");
        autoRotationState = pi.GetIpcSubscriber<bool>("WrathCombo.GetAutoRotationState");
        currentJobAutoRotationReady = pi.GetIpcSubscriber<bool>("WrathCombo.IsCurrentJobAutoRotationReady");
    }

    public string DriverName => "WrathCombo";
    public bool? AutoRotationEnabled { get; private set; }
    public bool? CurrentJobReady { get; private set; }

    public string StatusDetail
    {
        get
        {
            if (!Available)
                return LastError ?? "missing";

            var enabled = AutoRotationEnabled.HasValue ? AutoRotationEnabled.Value.ToString() : "unknown";
            var ready = CurrentJobReady.HasValue ? CurrentJobReady.Value.ToString() : "unknown";
            return $"ready (enabled={enabled}, jobReady={ready})";
        }
    }

    public override void RefreshAvailability()
    {
        SetAvailability(
            "WrathCombo IPC providers not found",
            () => ipcReady.HasFunction || autoRotationState.HasFunction || currentJobAutoRotationReady.HasFunction);

        AutoRotationEnabled = autoRotationState.HasFunction && TryCall(nameof(GetAutoRotationState), () => autoRotationState.InvokeFunc(), out var enabled)
            ? enabled
            : null;
        CurrentJobReady = currentJobAutoRotationReady.HasFunction && TryCall(nameof(IsCurrentJobAutoRotationReady), () => currentJobAutoRotationReady.InvokeFunc(), out var ready)
            ? ready
            : null;
    }

    public bool PrepareForCombat()
    {
        RefreshAvailability();
        return Available && CurrentJobReady != false;
    }

    public bool ResumeCombat() => true;

    private bool GetAutoRotationState() => autoRotationState.InvokeFunc();

    private bool IsCurrentJobAutoRotationReady() => currentJobAutoRotationReady.InvokeFunc();
}
