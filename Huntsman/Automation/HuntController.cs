using Huntsman.Planning;

namespace Huntsman.Automation;

internal sealed class HuntController(
    PluginServices services,
    DropHuntListManager dropHuntList,
    CombatJobService combatJobs,
    MonsterNavigator monsterNavigator)
{
    private uint? routedItemId;

    public string StatusText { get; private set; } = "Ready for manual drop hunts.";
    public string CombatJobStatus => combatJobs.StatusText;
    public bool HasActiveDropWork => dropHuntList.Enabled && dropHuntList.Items.Count > 0 && !dropHuntList.IsComplete;

    public void Update()
    {
        dropHuntList.Refresh();
        StatusText = HasActiveDropWork
            ? dropHuntList.StatusText
            : dropHuntList.IsComplete
                ? "Drop hunt complete."
                : "Ready for manual drop hunts.";
    }

    public void Stop()
    {
        routedItemId = null;
        dropHuntList.Stop();
        monsterNavigator.Stop();
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

    private void RouteActiveIfNeeded()
    {
        var active = dropHuntList.ActiveItem;
        if (active == null)
        {
            StatusText = dropHuntList.StatusText;
            return;
        }

        if (routedItemId == active.ItemId)
            return;

        var locations = active.GetCandidateLocations(services.ClientState.TerritoryType);
        if (locations.Count == 0)
        {
            StatusText = $"No route data for {active.ItemName}.";
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
