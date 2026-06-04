using Dalamud.Plugin.Ipc;
using LuminaAction = Lumina.Excel.Sheets.Action;

namespace GBRMonsterHunter.IPC;

internal sealed class RotationSolverRebornIpc : IpcAdapterBase, IDisposable
{
    private const byte SpecialEnd = 0;
    private const byte SpecialNoCasting = 13;
    private const byte StateManual = 3;

    private readonly ICallGateSubscriber<byte, object> changeOperatingMode;
    private readonly ICallGateSubscriber<byte, object> triggerSpecial;
    private readonly ICallGateSubscriber<byte, float, object> triggerSpecialDuration;
    private readonly ICallGateSubscriber<uint, object> nextGcdActionChanged;
    private readonly ICallGateSubscriber<uint, object> nextActionChanged;
    private bool eventsSubscribed;
    private DateTime nextEventSubscribeAttempt = DateTime.MinValue;

    public RotationSolverRebornIpc(PluginServices services, ThrottledLogger logger)
        : base(services, logger)
    {
        var pi = services.PluginInterface;
        changeOperatingMode = pi.GetIpcSubscriber<byte, object>("RotationSolverReborn.ChangeOperatingMode");
        triggerSpecial = pi.GetIpcSubscriber<byte, object>("RotationSolverReborn.TriggerSpecialState");
        triggerSpecialDuration = pi.GetIpcSubscriber<byte, float, object>("RotationSolverReborn.TriggerSpecialStateWithDuration");
        nextGcdActionChanged = pi.GetIpcSubscriber<uint, object>("RotationSolverReborn.ActionUpdater.NextGCDActionChanged");
        nextActionChanged = pi.GetIpcSubscriber<uint, object>("RotationSolverReborn.ActionUpdater.NextActionChanged");
    }

    public bool NextActionEventsAvailable { get; private set; }
    public string? EventLastError { get; private set; }
    public uint LatestNextGcdActionId { get; private set; }
    public uint LatestNextActionId { get; private set; }
    public string LatestNextGcdActionName => GetActionName(LatestNextGcdActionId);
    public string LatestNextActionName => GetActionName(LatestNextActionId);

    public override void RefreshAvailability()
    {
        SetAvailability(
            "RotationSolverReborn coordination IPC providers not found",
            () => changeOperatingMode.HasAction,
            () => triggerSpecial.HasAction || triggerSpecialDuration.HasAction);

        TrySubscribeEvents();
        NextActionEventsAvailable = nextGcdActionChanged.HasAction || nextActionChanged.HasAction || eventsSubscribed;
    }

    public bool PauseCombat(float durationSeconds) =>
        triggerSpecialDuration.HasAction
            ? TryAction(nameof(PauseCombat), () => triggerSpecialDuration.InvokeAction(SpecialNoCasting, durationSeconds))
            : TryAction(nameof(PauseCombat), () => triggerSpecial.InvokeAction(SpecialNoCasting));

    public bool ResumeCombat() => TryAction(nameof(ResumeCombat), () => triggerSpecial.InvokeAction(SpecialEnd));

    public bool SetManualMode() => TryAction(nameof(SetManualMode), () => changeOperatingMode.InvokeAction(StateManual));

    public void Dispose()
    {
        if (!eventsSubscribed)
            return;

        TryAction("UnsubscribeNextGCDActionChanged", () => nextGcdActionChanged.Unsubscribe(OnNextGcdActionChanged));
        TryAction("UnsubscribeNextActionChanged", () => nextActionChanged.Unsubscribe(OnNextActionChanged));
        eventsSubscribed = false;
    }

    private void TrySubscribeEvents()
    {
        if (eventsSubscribed || DateTime.UtcNow < nextEventSubscribeAttempt)
            return;

        try
        {
            nextGcdActionChanged.Subscribe(OnNextGcdActionChanged);
            nextActionChanged.Subscribe(OnNextActionChanged);
            eventsSubscribed = true;
            NextActionEventsAvailable = true;
            EventLastError = null;
        }
        catch (Exception ex)
        {
            EventLastError = ex.Message;
            NextActionEventsAvailable = false;
            nextEventSubscribeAttempt = DateTime.UtcNow.AddSeconds(5);
            Logger.Warning($"{nameof(RotationSolverRebornIpc)}:{nameof(TrySubscribeEvents)}", $"RSR event subscription failed: {ex.Message}");
        }
    }

    private void OnNextGcdActionChanged(uint actionId) => LatestNextGcdActionId = actionId;

    private void OnNextActionChanged(uint actionId) => LatestNextActionId = actionId;

    private string GetActionName(uint actionId)
    {
        if (actionId == 0)
            return "none";

        try
        {
            var row = Services.Data.GetExcelSheet<LuminaAction>().GetRowOrDefault(actionId);
            return row?.Name.ToString() ?? $"#{actionId}";
        }
        catch
        {
            return $"#{actionId}";
        }
    }
}
