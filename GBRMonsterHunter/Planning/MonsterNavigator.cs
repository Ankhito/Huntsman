using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using GBRMonsterHunter.IPC;

namespace GBRMonsterHunter.Planning;

internal enum MonsterNavigationState
{
    Idle,
    Teleporting,
    WaitingForZoneLoad,
    Navigating,
    ArrivedSearching,
    MovingToTarget,
    Arrived,
    Failed,
}

internal sealed class MonsterNavigator(
    PluginServices services,
    Configuration config,
    LifestreamIpc lifestream,
    VnavmeshIpc vnavmesh,
    RotationDriverService rotationDriver,
    CommandBridge commands,
    MonsterRoutePlanner planner)
{
    private const double TeleportCooldownSeconds = 3.0;
    private const double ZoneLoadWaitSeconds = 1.5;
    private const double TargetSearchRetrySeconds = 2.0;
    private const double TargetRepathSeconds = 2.0;

    private readonly List<MonsterLocation> patrolLocations = [];

    private MonsterLocation? target;
    private AetheryteRoute? route;
    private IBattleNpc? acquiredTarget;
    private Vector3 lastTargetMovePosition;
    private DateTime stateStartedAt = DateTime.MinValue;
    private DateTime nextTargetSearchAt = DateTime.MinValue;
    private DateTime nextTargetRepathAt = DateTime.MinValue;
    private bool teleportAttempted;
    private int targetSearchAttempts;
    private int patrolIndex;

    public MonsterNavigationState State { get; private set; }
    public string StatusText { get; private set; } = "Idle";

    private float ArrivalDistance => Math.Clamp(config.ArrivalDistance, 2f, 50f);
    private float TargetSearchRadius => Math.Clamp(config.TargetSearchRadius, 5f, 100f);
    private double NavigationTimeoutSeconds => Math.Clamp(config.NavigationTimeoutSeconds, 30.0, 900.0);
    private double TargetSearchTimeoutSeconds => Math.Clamp(config.TargetSearchTimeoutSeconds, 5.0, 120.0);

    public bool Start(MonsterLocation location) => Start([location]);

    public bool Start(IReadOnlyList<MonsterLocation> locations)
    {
        Stop();
        patrolLocations.Clear();
        patrolLocations.AddRange(OrderPatrolLocations(locations));
        patrolIndex = 0;

        if (patrolLocations.Count == 0)
        {
            State = MonsterNavigationState.Failed;
            StatusText = "No usable monster cluster locations were provided.";
            return false;
        }

        return StartCurrentPatrolLocation();
    }

    public void Stop()
    {
        lifestream.Abort();
        vnavmesh.Stop();
        rotationDriver.ResumeCombat();
        target = null;
        route = null;
        acquiredTarget = null;
        patrolLocations.Clear();
        patrolIndex = 0;
        teleportAttempted = false;
        targetSearchAttempts = 0;
        nextTargetSearchAt = DateTime.MinValue;
        nextTargetRepathAt = DateTime.MinValue;
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
            case MonsterNavigationState.ArrivedSearching:
                UpdateArrivedSearching();
                break;
            case MonsterNavigationState.MovingToTarget:
                UpdateMovingToTarget();
                break;
        }
    }

    private IReadOnlyList<MonsterLocation> OrderPatrolLocations(IReadOnlyList<MonsterLocation> locations)
    {
        var uniqueLocations = locations
            .Where(location => location.HasMapCoordinates)
            .DistinctBy(location => new
            {
                location.MobName,
                location.TerritoryTypeId,
                location.MapRowId,
                location.MapX,
                location.MapY,
                location.BNpcNameId,
            })
            .ToList();

        var player = services.Objects.LocalPlayer;
        if (player == null)
            return uniqueLocations;

        var currentTerritory = services.ClientState.TerritoryType;
        var sameTerritory = uniqueLocations
            .Where(location => location.TerritoryTypeId == currentTerritory)
            .Select(location => (Location: location, Route: planner.ResolveRoute(location)))
            .Where(entry => entry.Route != null)
            .OrderBy(entry => Vector3.DistanceSquared(player.Position, entry.Route!.Destination))
            .Select(entry => entry.Location)
            .ToList();

        var otherTerritories = uniqueLocations
            .Where(location => location.TerritoryTypeId != currentTerritory)
            .ToList();

        return sameTerritory.Count == 0
            ? uniqueLocations
            : sameTerritory.Concat(otherTerritories).ToList();
    }

    private bool StartCurrentPatrolLocation()
    {
        for (var attempts = 0; attempts < patrolLocations.Count; attempts++)
        {
            if (patrolIndex >= patrolLocations.Count)
                patrolIndex = 0;

            target = patrolLocations[patrolIndex];
            route = planner.ResolveRoute(target);
            if (route == null)
            {
                patrolIndex++;
                continue;
            }

            acquiredTarget = null;
            teleportAttempted = false;
            targetSearchAttempts = 0;
            nextTargetSearchAt = DateTime.MinValue;
            nextTargetRepathAt = DateTime.MinValue;
            State = services.ClientState.TerritoryType == route.TerritoryTypeId
                ? MonsterNavigationState.WaitingForZoneLoad
                : MonsterNavigationState.Teleporting;
            stateStartedAt = DateTime.UtcNow;
            StatusText = State == MonsterNavigationState.Teleporting
                ? $"Teleporting to {route.AetheryteName} for cluster {patrolIndex + 1}/{patrolLocations.Count}"
                : $"Preparing cluster {patrolIndex + 1}/{patrolLocations.Count}";
            return true;
        }

        target = null;
        route = null;
        State = MonsterNavigationState.Failed;
        StatusText = "No usable route/aetheryte found for any monster cluster.";
        return false;
    }

    private void AdvanceToNextPatrolLocation(string reason)
    {
        patrolIndex++;
        if (patrolIndex >= patrolLocations.Count)
            patrolIndex = 0;

        StatusText = $"{reason}; moving to cluster {patrolIndex + 1}/{patrolLocations.Count}.";
        StartCurrentPatrolLocation();
    }

    private void UpdateTeleporting()
    {
        if (IsBetweenAreas() || lifestream.IsBusy())
            return;

        if (!teleportAttempted)
        {
            teleportAttempted = true;
            stateStartedAt = DateTime.UtcNow;
            StatusText = $"Teleporting to {route!.AetheryteName} for cluster {patrolIndex + 1}/{patrolLocations.Count}";

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

        if (TryAcquireVisibleTarget(false))
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
            AdvanceToNextPatrolLocation($"Failed to start movement to cluster {patrolIndex + 1}/{patrolLocations.Count}: {vnavmesh.LastError ?? "unknown error"}");
            return;
        }

        State = MonsterNavigationState.Navigating;
        stateStartedAt = DateTime.UtcNow;
        StatusText = $"Moving to {target!.MobName} cluster {patrolIndex + 1}/{patrolLocations.Count}";
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

        if (TryAcquireVisibleTarget(false))
            return;

        var player = services.Objects.LocalPlayer;
        if (player == null)
            return;

        var distance = Vector3.Distance(player.Position, route.Destination);
        if (distance <= ArrivalDistance)
        {
            HandleArrival();
            return;
        }

        if ((DateTime.UtcNow - stateStartedAt).TotalSeconds > NavigationTimeoutSeconds)
        {
            vnavmesh.Stop();
            AdvanceToNextPatrolLocation($"Navigation timeout at cluster {patrolIndex + 1}/{patrolLocations.Count}; still {distance:F1} yalms away");
            return;
        }

        if (!vnavmesh.IsNavigating())
        {
            StatusText = $"Movement stopped; restarting cluster {patrolIndex + 1}/{patrolLocations.Count} ({distance:F1} yalms remaining)";
            StartVnavmeshNavigation();
        }
    }

    private void HandleArrival()
    {
        vnavmesh.Stop();
        if (TryAcquireVisibleTarget(true))
            return;

        State = MonsterNavigationState.ArrivedSearching;
        stateStartedAt = DateTime.UtcNow;
        nextTargetSearchAt = DateTime.UtcNow;
        targetSearchAttempts = 0;
        StatusText = $"Arrived at cluster {patrolIndex + 1}/{patrolLocations.Count}, searching for {target!.MobName}.";
    }

    private void UpdateArrivedSearching()
    {
        if (services.ClientState.TerritoryType != route!.TerritoryTypeId)
        {
            State = MonsterNavigationState.Teleporting;
            teleportAttempted = false;
            stateStartedAt = DateTime.UtcNow;
            StatusText = "Territory changed; restarting route";
            return;
        }

        if (IsBetweenAreas() || lifestream.IsBusy())
            return;

        if (DateTime.UtcNow < nextTargetSearchAt)
            return;

        targetSearchAttempts++;
        if (TryAcquireVisibleTarget(true))
            return;

        var elapsed = (DateTime.UtcNow - stateStartedAt).TotalSeconds;
        if (elapsed > TargetSearchTimeoutSeconds)
        {
            AdvanceToNextPatrolLocation($"Could not find {target!.MobName} at cluster {patrolIndex + 1}/{patrolLocations.Count} after {elapsed:F0}s");
            return;
        }

        nextTargetSearchAt = DateTime.UtcNow.AddSeconds(TargetSearchRetrySeconds);
        StatusText = $"Arrived at cluster {patrolIndex + 1}/{patrolLocations.Count}, searching for {target!.MobName} ({targetSearchAttempts} attempt(s)).";
    }

    private void UpdateMovingToTarget()
    {
        if (acquiredTarget == null || acquiredTarget.IsDead || !acquiredTarget.IsTargetable)
        {
            acquiredTarget = null;
            services.Targets.Target = null;
            State = MonsterNavigationState.ArrivedSearching;
            stateStartedAt = DateTime.UtcNow;
            nextTargetSearchAt = DateTime.UtcNow;
            StatusText = $"Lost target; searching cluster {patrolIndex + 1}/{patrolLocations.Count}.";
            return;
        }

        var player = services.Objects.LocalPlayer;
        if (player == null)
            return;

        var stopDistance = GetCombatStopDistance(acquiredTarget);
        var distance = Math.Max(0f, Vector3.Distance(player.Position, acquiredTarget.Position) - acquiredTarget.HitboxRadius);
        if (distance <= stopDistance)
        {
            vnavmesh.Stop();
            services.Targets.Target = acquiredTarget;
            CompleteArrivalWithTarget();
            return;
        }

        if ((DateTime.UtcNow - stateStartedAt).TotalSeconds > NavigationTimeoutSeconds)
        {
            vnavmesh.Stop();
            State = MonsterNavigationState.Failed;
            StatusText = $"Timed out moving to {acquiredTarget.Name} ({distance:F1} yalms away).";
            return;
        }

        if (DateTime.UtcNow < nextTargetRepathAt && vnavmesh.IsNavigating())
            return;

        var moved = Vector3.DistanceSquared(lastTargetMovePosition, acquiredTarget.Position) > MathF.Max(1f, acquiredTarget.HitboxRadius * acquiredTarget.HitboxRadius);
        if (!vnavmesh.IsNavigating() || moved)
            StartTargetMovement();
    }

    private bool TryAcquireVisibleTarget(bool restrictToCurrentCluster)
    {
        var match = FindVisibleHuntedTarget(restrictToCurrentCluster);
        if (match == null)
            return false;

        acquiredTarget = match;
        services.Targets.Target = match;
        vnavmesh.Stop();
        State = MonsterNavigationState.MovingToTarget;
        stateStartedAt = DateTime.UtcNow;
        nextTargetRepathAt = DateTime.MinValue;
        lastTargetMovePosition = match.Position;
        StatusText = $"Acquired {match.Name}; moving to attack range.";
        StartTargetMovement();
        return true;
    }

    private void StartTargetMovement()
    {
        if (acquiredTarget == null)
            return;

        vnavmesh.RefreshAvailability();
        if (!vnavmesh.Available || !vnavmesh.IsReady())
        {
            State = MonsterNavigationState.Failed;
            StatusText = $"vnavmesh unavailable while moving to target: {vnavmesh.LastError ?? "navmesh not ready"}";
            return;
        }

        var stopDistance = GetCombatStopDistance(acquiredTarget);
        var destination = vnavmesh.NearestPoint(acquiredTarget.Position, 6f, 12f) ?? acquiredTarget.Position;
        if (!vnavmesh.PathfindAndMoveCloseTo(destination, stopDistance))
        {
            State = MonsterNavigationState.Failed;
            StatusText = $"Failed to move to {acquiredTarget.Name}: {vnavmesh.LastError ?? "unknown error"}";
            return;
        }

        lastTargetMovePosition = acquiredTarget.Position;
        nextTargetRepathAt = DateTime.UtcNow.AddSeconds(TargetRepathSeconds);
        StatusText = $"Moving to {acquiredTarget.Name} for combat.";
    }

    private void CompleteArrivalWithTarget()
    {
        var driverReady = rotationDriver.PrepareForCombat();
        State = MonsterNavigationState.Arrived;
        StatusText = driverReady
            ? $"Targeted {target!.MobName}; {rotationDriver.DriverName} combat driver ready."
            : $"Targeted {target!.MobName}; combat driver unavailable ({rotationDriver.LastError ?? rotationDriver.StatusDetail}).";
    }

    private IBattleNpc? FindVisibleHuntedTarget(bool restrictToCurrentCluster)
    {
        if (target == null || route == null)
            return null;

        var player = services.Objects.LocalPlayer;
        var playerPosition = player?.Position ?? route.Destination;
        var searchRadius = TargetSearchRadius;

        return services.Objects
            .OfType<IBattleNpc>()
            .Where(npc => npc.IsTargetable && !npc.IsDead)
            .Select(npc =>
            {
                var nameIdMatches = target.BNpcNameId is > 0 && npc.NameId == target.BNpcNameId.Value;
                var baseIdMatches = target.BNpcNameId is > 0 && npc.BaseId == target.BNpcNameId.Value;
                var nameMatches = string.Equals(npc.Name.ToString(), target.MobName, StringComparison.OrdinalIgnoreCase);
                var priority = nameIdMatches ? 3 : baseIdMatches ? 2 : nameMatches ? 1 : 0;
                return new
                {
                    Object = npc,
                    Priority = priority,
                    DistanceToPlayer = Vector3.DistanceSquared(npc.Position, playerPosition),
                    DistanceToCluster = Vector3.Distance(npc.Position, route.Destination),
                };
            })
            .Where(candidate => candidate.Priority > 0)
            .Where(candidate => !restrictToCurrentCluster || candidate.DistanceToCluster <= searchRadius)
            .OrderByDescending(candidate => candidate.Priority)
            .ThenBy(candidate => candidate.DistanceToPlayer)
            .Select(candidate => candidate.Object)
            .FirstOrDefault();
    }

    private static float GetCombatStopDistance(IBattleNpc target) => Math.Clamp(target.HitboxRadius + 2.9f, 3f, 12f);

    private bool IsBetweenAreas() =>
        services.Condition[ConditionFlag.BetweenAreas] || services.Condition[ConditionFlag.BetweenAreas51];
}
