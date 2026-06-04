using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using GBRMonsterHunter.IPC;

namespace GBRMonsterHunter.Planning;

internal enum MonsterNavigationState
{
    Idle,
    Teleporting,
    WaitingForZoneLoad,
    Navigating,
    Arrived,
    Failed,
}

internal sealed class MonsterNavigator(
    PluginServices services,
    Configuration config,
    LifestreamIpc lifestream,
    VnavmeshIpc vnavmesh,
    RotationSolverRebornIpc rotationSolver,
    CommandBridge commands,
    MonsterRoutePlanner planner)
{
    private const double TeleportCooldownSeconds = 3.0;
    private const double ZoneLoadWaitSeconds = 1.5;
    private const double NavigationTimeoutSeconds = 180.0;
    private const float ArrivalDistance = 12f;
    private const float TargetSearchRadius = 35f;

    private MonsterLocation? target;
    private AetheryteRoute? route;
    private DateTime stateStartedAt = DateTime.MinValue;
    private bool teleportAttempted;

    public MonsterNavigationState State { get; private set; }
    public string StatusText { get; private set; } = "Idle";

    public bool Start(MonsterLocation location)
    {
        route = planner.ResolveRoute(location);
        if (route == null)
        {
            State = MonsterNavigationState.Failed;
            StatusText = "No usable route/aetheryte found for monster location.";
            return false;
        }

        target = location;
        teleportAttempted = false;
        State = services.ClientState.TerritoryType == route.TerritoryTypeId
            ? MonsterNavigationState.WaitingForZoneLoad
            : MonsterNavigationState.Teleporting;
        stateStartedAt = DateTime.UtcNow;
        StatusText = State == MonsterNavigationState.Teleporting
            ? $"Teleporting to {route.AetheryteName}"
            : "Preparing local navigation";
        return true;
    }

    public void Stop()
    {
        lifestream.Abort();
        vnavmesh.Stop();
        rotationSolver.ResumeCombat();
        target = null;
        route = null;
        teleportAttempted = false;
        State = MonsterNavigationState.Idle;
        StatusText = "Idle";
    }

    public void Update()
    {
        if (target == null || route == null || State is MonsterNavigationState.Idle or MonsterNavigationState.Arrived or MonsterNavigationState.Failed)
            return;

        switch (State)
        {
            case MonsterNavigationState.Teleporting:
                UpdateTeleporting();
                break;
            case MonsterNavigationState.WaitingForZoneLoad:
                UpdateWaitingForZoneLoad();
                break;
            case MonsterNavigationState.Navigating:
                UpdateNavigating();
                break;
        }
    }

    private void UpdateTeleporting()
    {
        if (IsBetweenAreas() || lifestream.IsBusy())
            return;

        if (!teleportAttempted)
        {
            teleportAttempted = true;
            stateStartedAt = DateTime.UtcNow;
            StatusText = $"Teleporting to {route!.AetheryteName}";

            lifestream.RefreshAvailability();
            if (lifestream.Available)
                lifestream.ExecuteCommand(route.AetheryteName);
            else
                commands.TeleporterTeleport(route.AetheryteName, config.TeleporterCommandTemplate);
            return;
        }

        if ((DateTime.UtcNow - stateStartedAt).TotalSeconds < TeleportCooldownSeconds)
            return;

        if (services.ClientState.TerritoryType == route!.TerritoryTypeId)
        {
            State = MonsterNavigationState.WaitingForZoneLoad;
            stateStartedAt = DateTime.UtcNow;
            StatusText = "Waiting for zone load";
        }
        else if ((DateTime.UtcNow - stateStartedAt).TotalSeconds > 30)
        {
            State = MonsterNavigationState.Failed;
            StatusText = $"Teleport timeout; still in territory {services.ClientState.TerritoryType}, expected {route.TerritoryTypeId}.";
        }
    }

    private void UpdateWaitingForZoneLoad()
    {
        if (IsBetweenAreas() || lifestream.IsBusy())
            return;

        if ((DateTime.UtcNow - stateStartedAt).TotalSeconds < ZoneLoadWaitSeconds)
            return;

        StartVnavmeshNavigation();
    }

    private void StartVnavmeshNavigation()
    {
        vnavmesh.RefreshAvailability();
        if (!vnavmesh.Available || !vnavmesh.IsReady())
        {
            State = MonsterNavigationState.Failed;
            StatusText = $"vnavmesh unavailable: {vnavmesh.LastError ?? "navmesh not ready"}";
            return;
        }

        var destination = vnavmesh.NearestPoint(route!.Destination) ?? route.Destination;
        if (!vnavmesh.PathfindAndMoveCloseTo(destination, ArrivalDistance))
        {
            State = MonsterNavigationState.Failed;
            StatusText = $"Failed to start vnavmesh movement: {vnavmesh.LastError ?? "unknown error"}";
            return;
        }

        State = MonsterNavigationState.Navigating;
        stateStartedAt = DateTime.UtcNow;
        StatusText = $"Moving to {target!.MobName}";
    }

    private void UpdateNavigating()
    {
        if (services.ClientState.TerritoryType != route!.TerritoryTypeId)
        {
            State = MonsterNavigationState.Teleporting;
            teleportAttempted = false;
            stateStartedAt = DateTime.UtcNow;
            StatusText = "Territory changed; restarting route";
            return;
        }

        var player = services.Objects.LocalPlayer;
        if (player == null)
            return;

        var distance = System.Numerics.Vector3.Distance(player.Position, route.Destination);
        if (distance <= ArrivalDistance)
        {
            vnavmesh.Stop();
            if (TrySelectHuntedTarget())
            {
                rotationSolver.RefreshAvailability();
                if (rotationSolver.Available)
                    rotationSolver.SetManualMode();

                State = MonsterNavigationState.Arrived;
                StatusText = $"Targeted {target!.MobName}; RSR manual mode requested.";
            }
            else
            {
                StatusText = $"Arrived, searching for {target!.MobName}";
                StartVnavmeshNavigation();
            }
            return;
        }

        if ((DateTime.UtcNow - stateStartedAt).TotalSeconds > NavigationTimeoutSeconds)
        {
            vnavmesh.Stop();
            State = MonsterNavigationState.Failed;
            StatusText = $"Navigation timeout; still {distance:F1} yalms away.";
            return;
        }

        if (!vnavmesh.IsNavigating())
        {
            StatusText = $"Movement stopped; restarting ({distance:F1} yalms remaining)";
            StartVnavmeshNavigation();
        }
    }

    private bool TrySelectHuntedTarget()
    {
        if (target == null || route == null)
            return false;

        var match = services.Objects
            .Where(obj => obj.ObjectKind == ObjectKind.BattleNpc && obj.IsTargetable)
            .Where(obj => string.Equals(obj.Name.ToString(), target.MobName, StringComparison.OrdinalIgnoreCase))
            .Where(obj => target.BNpcNameId == null || obj.BaseId == target.BNpcNameId.Value)
            .Where(obj => System.Numerics.Vector3.Distance(obj.Position, route.Destination) <= TargetSearchRadius)
            .OrderBy(obj => System.Numerics.Vector3.DistanceSquared(obj.Position, route.Destination))
            .FirstOrDefault();

        if (match == null)
            return false;

        services.Targets.Target = match;
        return true;
    }

    private bool IsBetweenAreas() =>
        services.Condition[ConditionFlag.BetweenAreas] || services.Condition[ConditionFlag.BetweenAreas51];
}
