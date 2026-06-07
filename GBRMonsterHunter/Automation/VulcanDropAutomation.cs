using GBRMonsterHunter.IPC;
using GBRMonsterHunter.Planning;
using GBRMonsterHunter.UI;

namespace GBRMonsterHunter.Automation;

internal sealed class VulcanDropAutomation(
    PluginServices services,
    GatherBuddyRebornIpc gbr,
    VulcanReflectionAdapter vulcan,
    MaterialPlanner planner,
    DropHuntListManager dropHuntList,
    CombatJobService combatJobs,
    MonsterNavigator monsterNavigator)
{
    private const string PauseReason = "GBRMonsterHunter: hunting drop-only materials";

    private string? activePlanSignature;
    private uint? routedItemId;
    private bool pausedVulcan;

    public string StatusText { get; private set; } = "Waiting for Vulcan.";
    public string CurrentPlanName { get; private set; } = "None";
    public string QueueState { get; private set; } = "None";
    public string CombatJobStatus => combatJobs.StatusText;
    public bool HasActiveDropWork => dropHuntList.Enabled && dropHuntList.Items.Count > 0 && !dropHuntList.IsComplete;
    public bool VulcanPaused => pausedVulcan;
    public string? VulcanListenerError => vulcan.LastError;

    public void Update(MainWindow window)
    {
        var snapshot = vulcan.GetActiveExecutionPlan();
        if (snapshot == null)
        {
            ResumeIfNeeded();
            CurrentPlanName = "None";
            StatusText = vulcan.Available ? "Waiting for Vulcan." : $"Vulcan listener unavailable: {vulcan.LastError}";
            return;
        }

        CurrentPlanName = snapshot.ListName;
        var queue = vulcan.GetQueueSnapshot();
        if (queue == null)
        {
            StatusText = $"Vulcan listener unavailable: {vulcan.LastError}";
            return;
        }

        QueueState = queue.State;
        if (activePlanSignature != snapshot.Signature)
        {
            activePlanSignature = snapshot.Signature;
            routedItemId = null;
            BuildDropList(snapshot, window);
        }

        dropHuntList.Refresh();

        if (!dropHuntList.Enabled || dropHuntList.Items.Count == 0)
        {
            ResumeIfNeeded();
            StatusText = $"Vulcan plan '{snapshot.ListName}' has no missing drop materials.";
            return;
        }

        if (!IsGbrGatheringComplete(queue))
        {
            StatusText = $"Pending GBR gathering. {dropHuntList.StatusText}";
            return;
        }

        if (dropHuntList.IsComplete)
        {
            ResumeIfNeeded();
            StatusText = $"Drop hunt complete for '{snapshot.ListName}'. Vulcan resumed.";
            return;
        }

        if (!PauseVulcanForDrops())
            return;

        RouteActiveIfNeeded();
    }

    public void Stop()
    {
        routedItemId = null;
        dropHuntList.Stop();
        monsterNavigator.Stop();
        ResumeIfNeeded();
        StatusText = "Stopped.";
    }

    public void RouteActive()
    {
        routedItemId = null;
        RouteActiveIfNeeded();
    }

    public void Advance()
    {
        dropHuntList.Advance();
        routedItemId = null;
        RouteActiveIfNeeded();
    }

    public void ResumeVulcan()
    {
        ResumeIfNeeded();
        StatusText = "Vulcan resume requested.";
    }

    private void BuildDropList(VulcanExecutionPlanSnapshot snapshot, MainWindow window)
    {
        var requirements = planner.PlanMaterialCounts(snapshot.Materials);
        dropHuntList.Generate(requirements, $"Vulcan Drop Hunt: {snapshot.ListName}");

        if (dropHuntList.Items.Count == 0)
        {
            StatusText = $"Vulcan plan '{snapshot.ListName}' has no missing drop materials.";
            return;
        }

        window.IsOpen = true;
        StatusText = $"Detected {dropHuntList.Items.Count} drop target(s) for '{snapshot.ListName}'.";
    }

    private bool PauseVulcanForDrops()
    {
        var queue = vulcan.GetQueueSnapshot();
        if (queue?.Paused == true)
        {
            if (!string.Equals(queue.PauseReason, PauseReason, StringComparison.Ordinal))
            {
                pausedVulcan = false;
                StatusText = $"Vulcan is already paused for another reason ({queue.PauseReason}); waiting before drop hunt.";
                return false;
            }

            pausedVulcan = true;
            StatusText = $"Paused Vulcan for drops. {dropHuntList.StatusText}";
            return true;
        }

        if (queue?.Paused == false)
            pausedVulcan = false;

        if (!pausedVulcan)
            pausedVulcan = vulcan.PauseQueue(PauseReason);

        StatusText = pausedVulcan
            ? $"Paused Vulcan for drops. {dropHuntList.StatusText}"
            : $"Drop hunt active; failed to pause Vulcan: {vulcan.LastError ?? "unknown error"}";
        return pausedVulcan;
    }

    private bool IsGbrGatheringComplete(VulcanQueueSnapshot queue)
    {
        gbr.RefreshAvailability();
        if (gbr.Available && gbr.IsAutoGatherEnabled())
            return false;

        if (queue.IsWaitingForGather)
            return false;

        if (!queue.IsGatheringComplete)
            return false;

        return queue.IsPostGather || !queue.WaitingForGatherComplete || queue.Paused && queue.PauseReason == PauseReason;
    }

    private void ResumeIfNeeded()
    {
        if (!pausedVulcan)
            return;

        var queue = vulcan.GetQueueSnapshot();
        if (queue?.Paused == true && !string.Equals(queue.PauseReason, PauseReason, StringComparison.Ordinal))
        {
            pausedVulcan = false;
            return;
        }

        if (queue?.Paused == false)
        {
            pausedVulcan = false;
            return;
        }

        if (vulcan.ResumeQueue())
            pausedVulcan = false;
    }

    private void RouteActiveIfNeeded()
    {
        var active = dropHuntList.ActiveItem;
        if (active == null)
            return;

        if (routedItemId == active.ItemId)
            return;

        var locations = active.GetCandidateLocations(services.ClientState.TerritoryType);
        if (locations.Count == 0)
        {
            StatusText = $"No route data for {active.ItemName}; Vulcan remains paused.";
            return;
        }

        if (!combatJobs.EnsureReadyForDropHunt())
        {
            StatusText = combatJobs.StatusText;
            return;
        }

        if (monsterNavigator.Start(locations))
        {
            routedItemId = active.ItemId;
            StatusText = $"Patrolling {locations.Count} cluster(s) for {active.ItemName}: {locations[0].MobName}.";
        }
        else
        {
            StatusText = $"Failed to route to {active.ItemName}: {monsterNavigator.StatusText}";
        }
    }
}
