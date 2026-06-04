using System.Numerics;
using Dalamud.Plugin.Ipc;

namespace GBRMonsterHunter.IPC;

internal sealed class VnavmeshIpc : IpcAdapterBase
{
    private readonly ICallGateSubscriber<Vector3, bool, bool> moveTo;
    private readonly ICallGateSubscriber<Vector3, bool, float, bool> moveCloseTo;
    private readonly ICallGateSubscriber<object> stop;
    private readonly ICallGateSubscriber<bool> isRunning;
    private readonly ICallGateSubscriber<bool> navReady;
    private readonly ICallGateSubscriber<Vector3, float, float, Vector3?> nearestPoint;

    public VnavmeshIpc(PluginServices services, ThrottledLogger logger)
        : base(services, logger)
    {
        var pi = services.PluginInterface;
        moveTo = pi.GetIpcSubscriber<Vector3, bool, bool>("vnavmesh.SimpleMove.PathfindAndMoveTo");
        moveCloseTo = pi.GetIpcSubscriber<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo");
        stop = pi.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
        isRunning = pi.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        navReady = pi.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        nearestPoint = pi.GetIpcSubscriber<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPoint");
    }

    public override void RefreshAvailability() =>
        SetAvailability(
            "vnavmesh IPC providers not found",
            () => navReady.HasFunction,
            () => moveCloseTo.HasFunction || moveTo.HasFunction,
            () => stop.HasAction);

    public bool IsReady() => TryCall(nameof(IsReady), () => navReady.InvokeFunc(), out var ready) && ready;

    public bool IsNavigating() => TryCall(nameof(IsNavigating), () => isRunning.InvokeFunc(), out var running) && running;

    public Vector3? NearestPoint(Vector3 position, float horizontalRange = 8f, float verticalRange = 32f) =>
        nearestPoint.HasFunction && TryCall(nameof(NearestPoint), () => nearestPoint.InvokeFunc(position, horizontalRange, verticalRange), out var point)
            ? point
            : null;

    public bool PathfindAndMoveTo(Vector3 destination) =>
        TryCall(nameof(PathfindAndMoveTo), () => moveTo.InvokeFunc(destination, false), out var started) && started;

    public bool PathfindAndMoveCloseTo(Vector3 destination, float tolerance)
    {
        if (moveCloseTo.HasFunction)
            return TryCall(nameof(PathfindAndMoveCloseTo), () => moveCloseTo.InvokeFunc(destination, false, tolerance), out var started) && started;

        return PathfindAndMoveTo(destination);
    }

    public void Stop() => TryAction(nameof(Stop), () => stop.InvokeAction());
}
