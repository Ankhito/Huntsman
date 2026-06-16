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
    MonsterRoutePlanner planner,
    MountService mountService)
{
    private const double TeleportCooldownSeconds = 3.0;
    private const double ZoneLoadWaitSeconds = 1.5;
    private const double TargetSearchRetrySeconds = 2.0;
    private const double TargetRepathSeconds = 2.0;
    private const double MountCommandCooldownSeconds = 5.0;
    private const double MountWaitSeconds = 2.0;
    private const double DismountCommandCooldownSeconds = 2.0;

    private readonly List<MonsterLocation> patrolLocations = [];

    private MonsterLocation? target;
    private AetheryteRoute? route;
    private IBattleNpc? acquiredTarget;
    private Vector3 lastTargetMovePosition;
    private DateTime stateStartedAt = DateTime.MinValue;
    private DateTime nextTargetSearchAt = DateTime.MinValue;
    private DateTime nextTargetRepathAt = DateTime.MinValue;
    private DateTime lastMountAttemptAt = DateTime.MinValue;
    private DateTime lastDismountAttemptAt = DateTime.MinValue;
    private bool teleportAttempted;
    private bool mountAttemptedForCluster;
    private int targetSearchAttempts;
    private int patrolIndex;

    public MonsterNavigationState State { get; private set; }
    public string StatusText { get; private set; } = "Idle";
    public string? LastRouteStartError { get; private set; }
    public MonsterLocation? ActiveLocation => target;
    public AetheryteRoute? ActiveRoute => route;
    public string? LastVnavmeshError => vnavmesh.LastError;
    public uint CurrentTerritoryTypeId => services.ClientState.TerritoryType;
    public bool IsMounted => mountService.IsMounted;
    public string LastMountStatus => mountService.LastMountStatus;
    public string LastDismountStatus => mountService.LastDismountStatus;

    private float ArrivalDistance => Math.Clamp(config.ArrivalDistance, 2f, 50f);
    private float TargetSearchRadius => Math.Clamp(config.TargetSearchRadius, 5f, 100f);
    private double NavigationTimeoutSeconds => Math.Clamp(config.NavigationTimeoutSeconds, 30.0, 900.0);
    private double TargetSearchTimeoutSeconds => Math.Clamp(config.TargetSearchTimeoutSeconds, 5.0, 120.0);

    public bool Start(MonsterLocation location) => Start([location]);

    public bool Start(IReadOnlyList<MonsterLocation> locations)
    {
        Stop();
        var validLocations = FilterValidRouteCandidates(locations, out var validationSummary);
        patrolLocations.Clear();
        patrolLocations.AddRange(OrderPatrolLocations(validLocations));
        patrolIndex = 0;

        if (patrolLocations.Count == 0)
        {
            State = MonsterNavigationState.Failed;
            LastRouteStartError = validationSummary ?? "No usable monster cluster locations were provided.";
            StatusText = LastRouteStartError;
            services.Log.Warning($"Monster route start failed: {LastRouteStartError}");
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
        lastMountAttemptAt = DateTime.MinValue;
        lastDismountAttemptAt = DateTime.MinValue;
        mountAttemptedForCluster = false;
        State = MonsterNavigationState.Idle;
        StatusText = "Idle";
    }

    private IReadOnlyList<MonsterLocation> FilterValidRouteCandidates(IReadOnlyList<MonsterLocation> locations, out string? validationSummary)
    {
        var validLocations = new List<MonsterLocation>();
        var lastError = "no candidates were provided";
        var checkedCount = 0;

        foreach (var location in locations)
        {
            checkedCount++;
            if (!ValidateLocation(location, out var error))
            {
                lastError = error;
                continue;
            }

            validLocations.Add(location);
        }

        validationSummary = validLocations.Count == 0
            ? $"No valid route candidates. Checked {checkedCount} candidate(s). Last error: {lastError}."
            : null;
        return validLocations;
    }

    private static bool ValidateLocation(MonsterLocation location, out string error)
    {
        if (string.IsNullOrWhiteSpace(location.MobName))
        {
            error = "candidate has no mob name";
            return false;
        }

        if (location.TerritoryTypeId == 0)
        {
            error = $"{location.MobName} has no valid territory.";
            return false;
        }

        if (location.MapRowId == 0)
        {
            error = $"{location.MobName} has no valid map.";
            return false;
        }

        if (location.BNpcNameId is 0)
        {
            error = $"{location.MobName} has invalid BNpcNameId 0.";
            return false;
        }

        if (!IsLikelyMapCoordinate(location.MapX) || !IsLikelyMapCoordinate(location.MapY))
        {
            error = $"{location.MobName} has invalid coordinates: X={location.MapX:F1}, Y={location.MapY:F1}.";
            return false;
        }

        error = string.Empty;
        return true;
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
            if (!planner.TryResolveRoute(target, out route, out var routeError))
            {
                LastRouteStartError = routeError;
                patrolIndex++;
                continue;
            }

            acquiredTarget = null;
            teleportAttempted = false;
            mountAttemptedForCluster = false;
            targetSearchAttempts = 0;
            nextTargetSearchAt = DateTime.MinValue;
            nextTargetRepathAt = DateTime.MinValue;
            State = services.ClientState.TerritoryType == route!.TerritoryTypeId
                ? MonsterNavigationState.WaitingForZoneLoad
                : MonsterNavigationState.Teleporting;
            stateStartedAt = DateTime.UtcNow;
            StatusText = State == MonsterNavigationState.Teleporting
                ? $"Teleporting to {route.AetheryteName} for cluster {patrolIndex + 1}/{patrolLocations.Count}"
                : $"Preparing cluster {patrolIndex + 1}/{patrolLocations.Count}";
            LastRouteStartError = null;
            return true;
        }

        target = null;
        route = null;
        State = MonsterNavigationState.Failed;
        LastRouteStartError = $"No usable route/aetheryte found for any monster cluster. Checked {patrolLocations.Count} candidate(s). Last error: {LastRouteStartError ?? "none"}.";
        StatusText = LastRouteStartError;
        services.Log.Warning($"Monster route start failed: {LastRouteStartError}");
        return false;
    }

    private void AdvanceToNextPatrolLocation(string reason)
    {
        services.Log.Warning($"Monster route attempt skipped: {reason}");
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

        if (!EnsureDismountedBeforeTargeting())
            return;

        if (TryAcquireVisibleTarget(false))
            return;

        StartVnavmeshNavigation();
    }

    private void StartVnavmeshNavigation()
    {
        vnavmesh.RefreshAvailability();
        if (!vnavmesh.Available)
        {
            State = MonsterNavigationState.Failed;
            LastRouteStartError = $"Cannot route to {target!.MobName}: vnavmesh is unavailable ({vnavmesh.LastError ?? "IPC providers missing"}).";
            StatusText = LastRouteStartError;
            services.Log.Warning($"Monster route start failed: {LastRouteStartError}");
            return;
        }

        if (!vnavmesh.IsReady())
        {
            State = MonsterNavigationState.Failed;
            LastRouteStartError = $"Cannot route to {target!.MobName}: vnavmesh is not ready; open navmesh or wait for it to load.";
            StatusText = LastRouteStartError;
            services.Log.Warning($"Monster route start failed: {LastRouteStartError}");
            return;
        }

        if (!PrepareMountForRouteMovement())
            return;

        var destination = vnavmesh.NearestPoint(route!.Destination);
        if (destination == null)
        {
            AdvanceToNextPatrolLocation($"vnavmesh path request failed for {target!.MobName}: {vnavmesh.LastError ?? "could not find nearest mesh point"}");
            return;
        }

        if (!vnavmesh.PathfindAndMoveCloseTo(destination.Value, ArrivalDistance))
        {
            AdvanceToNextPatrolLocation($"vnavmesh move command failed for {target!.MobName}: {vnavmesh.LastError ?? "PathfindAndMoveCloseTo returned no detail"}");
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
        if (!EnsureDismountedBeforeTargeting())
            return;

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
        if (!EnsureDismountedBeforeTargeting())
            return;

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
        if (!EnsureDismountedBeforeTargeting())
            return false;

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

        if (!EnsureDismountedBeforeTargeting())
            return;

        vnavmesh.RefreshAvailability();
        if (!vnavmesh.Available)
        {
            State = MonsterNavigationState.Failed;
            StatusText = $"Cannot route to {acquiredTarget.Name}: vnavmesh is unavailable ({vnavmesh.LastError ?? "IPC providers missing"}).";
            services.Log.Warning($"Monster target movement failed: {StatusText}");
            return;
        }

        if (!vnavmesh.IsReady())
        {
            State = MonsterNavigationState.Failed;
            StatusText = $"Cannot route to {acquiredTarget.Name}: vnavmesh is not ready; open navmesh or wait for it to load.";
            services.Log.Warning($"Monster target movement failed: {StatusText}");
            return;
        }

        var stopDistance = GetCombatStopDistance(acquiredTarget);
        var destination = vnavmesh.NearestPoint(acquiredTarget.Position, 6f, 12f);
        if (destination == null)
        {
            State = MonsterNavigationState.Failed;
            StatusText = $"Cannot route to {acquiredTarget.Name}: {vnavmesh.LastError ?? "failed to find nearest mesh point near target"}.";
            services.Log.Warning($"Monster target movement failed: {StatusText}");
            return;
        }

        if (!vnavmesh.PathfindAndMoveCloseTo(destination.Value, stopDistance))
        {
            State = MonsterNavigationState.Failed;
            StatusText = $"vnavmesh move command failed for {acquiredTarget.Name}: {vnavmesh.LastError ?? "PathfindAndMoveCloseTo returned no detail"}.";
            services.Log.Warning($"Monster target movement failed: {StatusText}");
            return;
        }

        lastTargetMovePosition = acquiredTarget.Position;
        nextTargetRepathAt = DateTime.UtcNow.AddSeconds(TargetRepathSeconds);
        StatusText = $"Moving to {acquiredTarget.Name} for combat.";
    }

    private void CompleteArrivalWithTarget()
    {
        if (mountService.IsMounted)
        {
            State = MonsterNavigationState.Failed;
            StatusText = "Cannot start combat while mounted; dismount before attacking.";
            return;
        }

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

    private static bool IsLikelyMapCoordinate(float value) => value is > 0f and < 50f;

    private bool PrepareMountForRouteMovement()
    {
        var player = services.Objects.LocalPlayer;
        if (player == null || route == null || mountService.IsMounted)
            return true;

        var distance = Vector3.Distance(player.Position, route.Destination);
        if (!mountService.CanAttemptMount(distance, out var reason))
        {
            if (config.AutoMountEnabled && distance >= Math.Clamp(config.AutoMountMinDistance, 1f, 500f) && !mountAttemptedForCluster)
            {
                mountAttemptedForCluster = true;
                mountService.RecordMountSkipped(reason);
                StatusText = $"Mount unavailable: {reason}; continuing on foot.";
            }

            return true;
        }

        if (mountAttemptedForCluster)
        {
            if ((DateTime.UtcNow - lastMountAttemptAt).TotalSeconds < MountWaitSeconds)
            {
                StatusText = $"Waiting for mount before moving to {target!.MobName}.";
                return false;
            }

            return true;
        }

        if ((DateTime.UtcNow - lastMountAttemptAt).TotalSeconds < MountCommandCooldownSeconds)
            return true;

        mountAttemptedForCluster = true;
        lastMountAttemptAt = DateTime.UtcNow;
        var result = mountService.TryMountRoulette(distance);
        StatusText = result.Success
            ? $"Mounting with Mount Roulette before moving to {target!.MobName}."
            : $"{result.Status} Continuing on foot.";
        return !result.Success;
    }

    private bool EnsureDismountedBeforeTargeting()
    {
        if (!mountService.IsMounted)
            return true;

        if ((DateTime.UtcNow - lastDismountAttemptAt).TotalSeconds < DismountCommandCooldownSeconds)
        {
            StatusText = "Cannot start combat while mounted; waiting to dismount.";
            return false;
        }

        lastDismountAttemptAt = DateTime.UtcNow;
        var result = mountService.TryDismount();
        StatusText = result.Status;
        if (!result.Success && result.IsFatal)
        {
            State = MonsterNavigationState.Failed;
            services.Log.Warning($"Monster dismount failed: {result.Status}");
        }

        return !mountService.IsMounted && result.Success;
    }
}
