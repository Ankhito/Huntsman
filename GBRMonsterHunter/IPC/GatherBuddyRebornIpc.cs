using Dalamud.Plugin.Ipc;

namespace GBRMonsterHunter.IPC;

internal sealed class GatherBuddyRebornIpc : IpcAdapterBase
{
    private const int MinimumSupportedVersion = 2;

    private readonly ICallGateSubscriber<int> version;
    private readonly ICallGateSubscriber<string, uint> identify;
    private readonly ICallGateSubscriber<bool> isAutoGatherEnabled;
    private readonly ICallGateSubscriber<string> autoGatherStatus;
    private readonly ICallGateSubscriber<bool, object> setAutoGatherEnabled;
    private readonly ICallGateSubscriber<bool> isAutoGatherWaiting;

    public GatherBuddyRebornIpc(PluginServices services, ThrottledLogger logger)
        : base(services, logger)
    {
        var pi = services.PluginInterface;
        version = pi.GetIpcSubscriber<int>("GatherBuddyReborn.Version");
        identify = pi.GetIpcSubscriber<string, uint>("GatherBuddyReborn.Identify");
        isAutoGatherEnabled = pi.GetIpcSubscriber<bool>("GatherBuddyReborn.IsAutoGatherEnabled");
        autoGatherStatus = pi.GetIpcSubscriber<string>("GatherBuddyReborn.GetAutoGatherStatusText");
        setAutoGatherEnabled = pi.GetIpcSubscriber<bool, object>("GatherBuddyReborn.SetAutoGatherEnabled");
        isAutoGatherWaiting = pi.GetIpcSubscriber<bool>("GatherBuddyReborn.IsAutoGatherWaiting");
    }

    public override void RefreshAvailability()
    {
        SetAvailability(
            "GatherBuddyReborn IPC v2 providers not found",
            () => version.HasFunction,
            () => identify.HasFunction,
            () => isAutoGatherEnabled.HasFunction,
            () => autoGatherStatus.HasFunction,
            () => setAutoGatherEnabled.HasAction,
            () => isAutoGatherWaiting.HasFunction);

        if (Available && TryCall(nameof(GetVersion), () => version.InvokeFunc(), out var ipcVersion) && ipcVersion < MinimumSupportedVersion)
        {
            Available = false;
            LastError = $"GatherBuddyReborn IPC v{ipcVersion} found, v{MinimumSupportedVersion}+ required";
        }
    }

    public int GetVersion() => TryCall(nameof(GetVersion), () => version.InvokeFunc(), out var value) ? value : 0;

    public uint Identify(string itemName) =>
        TryCall(nameof(Identify), () => identify.InvokeFunc(itemName), out var itemId) ? itemId : 0;

    public bool IsAutoGatherEnabled() =>
        TryCall(nameof(IsAutoGatherEnabled), () => isAutoGatherEnabled.InvokeFunc(), out var enabled) && enabled;

    public bool IsAutoGatherWaiting() =>
        TryCall(nameof(IsAutoGatherWaiting), () => isAutoGatherWaiting.InvokeFunc(), out var waiting) && waiting;

    public string GetStatus() =>
        TryCall(nameof(GetStatus), () => autoGatherStatus.InvokeFunc(), out var status) ? status ?? string.Empty : LastError ?? "unavailable";

    public bool SetAutoGatherEnabled(bool enabled) =>
        TryAction(nameof(SetAutoGatherEnabled), () => setAutoGatherEnabled.InvokeAction(enabled));
}
