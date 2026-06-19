using System.Numerics;
using Dalamud.Plugin.Ipc;

namespace Huntsman.IPC;

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
        TryNearestPoint(position, horizontalRange, verticalRange, out var point) ? point : null;

    public bool PathfindAndMoveTo(Vector3 destination)
    {
        if (!moveTo.HasFunction)
        {
            LastError = "vnavmesh PathfindAndMoveTo IPC provider is unavailable.";
            return false;
        }

        if (!TryCall(nameof(PathfindAndMoveTo), () => moveTo.InvokeFunc(destination, false), out var started))
            return false;

        if (started)
            return true;

        LastError = $"vnavmesh PathfindAndMoveTo returned false for destination {FormatVector(destination)}.";
        return false;
    }

    public bool PathfindAndMoveCloseTo(Vector3 destination, float tolerance)
    {
        if (moveCloseTo.HasFunction)
        {
            if (!TryCall(nameof(PathfindAndMoveCloseTo), () => moveCloseTo.InvokeFunc(destination, false, tolerance), out var started))
                return false;

            if (started)
                return true;

            LastError = $"vnavmesh PathfindAndMoveCloseTo returned false for destination {FormatVector(destination)} with tolerance {tolerance:F1}.";
            return false;
        }

        return PathfindAndMoveTo(destination);
    }

    public void Stop() => TryAction(nameof(Stop), () => stop.InvokeAction());

    private bool TryNearestPoint(Vector3 position, float horizontalRange, float verticalRange, out Vector3? point)
    {
        point = null;
        if (!nearestPoint.HasFunction)
        {
            LastError = "vnavmesh NearestPoint IPC provider is unavailable.";
            return false;
        }

        if (!TryCall(nameof(NearestPoint), () => nearestPoint.InvokeFunc(position, horizontalRange, verticalRange), out point))
            return false;

        if (point != null)
            return true;

        LastError = $"vnavmesh could not find a mesh point near {FormatVector(position)}.";
        return false;
    }

    private static string FormatVector(Vector3 value) => $"X={value.X:F1}, Y={value.Y:F1}, Z={value.Z:F1}";
}
