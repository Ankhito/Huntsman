using Dalamud.Bindings.ImGui;
using GBRMonsterHunter.Automation;
using GBRMonsterHunter.IPC;
using GBRMonsterHunter.Planning;
using System.Numerics;

namespace GBRMonsterHunter.UI;

internal sealed class MainWindow
{
    private readonly Configuration config;
    private readonly GatherBuddyRebornIpc gbr;
    private readonly LifestreamIpc lifestream;
    private readonly VnavmeshIpc vnavmesh;
    private readonly RotationDriverService rotationDriver;
    private readonly MonsterNavigator monsterNavigator;
    private readonly DropHuntListManager dropHuntList;
    private readonly VulcanDropAutomation automation;
    private readonly CombatJobService combatJobs;

    public MainWindow(
        Configuration config,
        GatherBuddyRebornIpc gbr,
        LifestreamIpc lifestream,
        VnavmeshIpc vnavmesh,
        RotationDriverService rotationDriver,
        MonsterNavigator monsterNavigator,
        DropHuntListManager dropHuntList,
        VulcanDropAutomation automation,
        CombatJobService combatJobs)
    {
        this.config = config;
        this.gbr = gbr;
        this.lifestream = lifestream;
        this.vnavmesh = vnavmesh;
        this.rotationDriver = rotationDriver;
        this.monsterNavigator = monsterNavigator;
        this.dropHuntList = dropHuntList;
        this.automation = automation;
        this.combatJobs = combatJobs;
    }

    public bool IsOpen { get; set; }

    public void Dispose()
    {
        Shutdown();
        rotationDriver.Dispose();
    }

    public void Shutdown() => automation.Stop();

    public void Update()
    {
        monsterNavigator.Update();
        automation.Update(this);
    }

    public void StopAutomation()
    {
        automation.Stop();
        gbr.SetAutoGatherEnabled(false);
    }

    public void RefreshDependencies()
    {
        gbr.RefreshAvailability();
        lifestream.RefreshAvailability();
        vnavmesh.RefreshAvailability();
        rotationDriver.RefreshAvailability();
    }

    public string BuildStatusLine() =>
        $"GBRMonsterHunter: Vulcan={automation.CurrentPlanName} ({automation.StatusText}), GBR={gbr.Available} ({gbr.LastError ?? gbr.GetStatus()}), Lifestream={lifestream.Available} ({lifestream.LastError ?? "ok"}), vnavmesh={vnavmesh.Available} ({vnavmesh.LastError ?? "ok"}), Rotation={rotationDriver.Available} ({rotationDriver.StatusDetail}), MonsterNav={monsterNavigator.State} ({monsterNavigator.StatusText})";

    public void RouteActiveDropTarget() => automation.RouteActive();

    public void AdvanceDropTarget() => automation.Advance();

    public void ResumeVulcan() => automation.ResumeVulcan();

    public void Draw()
    {
        if (!IsOpen)
            return;

        ImGui.SetNextWindowSize(new Vector2(560, 380), ImGuiCond.FirstUseEver);
        var isOpen = IsOpen;
        if (!ImGui.Begin("GBR Monster Hunter", ref isOpen))
        {
            IsOpen = isOpen;
            ImGui.End();
            return;
        }

        IsOpen = isOpen;
        DrawStatus();
        ImGui.Separator();
        DrawActions();
        ImGui.Separator();
        DrawActiveTarget();
        ImGui.End();
    }

    private void DrawStatus()
    {
        RefreshDependencies();
        ImGui.TextUnformatted($"Vulcan plan: {automation.CurrentPlanName}");
        ImGui.TextUnformatted($"Vulcan queue: {automation.QueueState}");
        ImGui.TextUnformatted($"Automation: {automation.StatusText}");
        DrawCombatJobSelector();
        DrawRotationDriverSelector();
        DrawNavigationTuning();
        ImGui.TextUnformatted($"Combat job: {automation.CombatJobStatus}");
        DrawStatusLine("GBR", gbr.Available, gbr.Available ? $"IPC v{gbr.GetVersion()}: {gbr.GetStatus()}" : gbr.LastError);
        DrawStatusLine("Lifestream", lifestream.Available, lifestream.Available ? $"busy={lifestream.IsBusy()}" : lifestream.LastError);
        DrawStatusLine("vnavmesh", vnavmesh.Available, vnavmesh.Available ? $"ready={vnavmesh.IsReady()}, moving={vnavmesh.IsNavigating()}" : vnavmesh.LastError);
        DrawStatusLine("Rotation", rotationDriver.Available, rotationDriver.StatusDetail);
        DrawStatusLine("Monster nav", monsterNavigator.State != MonsterNavigationState.Failed, monsterNavigator.StatusText);
    }

    private void DrawCombatJobSelector()
    {
        var selectedLabel = combatJobs.GetSelectedJobLabel();
        if (!ImGui.BeginCombo("Combat job", selectedLabel))
            return;

        if (ImGui.Selectable("None", config.CombatClassJobId == 0))
        {
            config.CombatClassJobId = 0;
            config.Save();
        }

        foreach (var job in combatJobs.GetCombatJobs())
        {
            if (!ImGui.Selectable(job.Label, config.CombatClassJobId == job.ClassJobId))
                continue;

            config.CombatClassJobId = job.ClassJobId;
            config.Save();
        }

        ImGui.EndCombo();
    }

    private void DrawRotationDriverSelector()
    {
        var selectedLabel = GetRotationDriverLabel(config.RotationDriver);
        if (!ImGui.BeginCombo("Rotation driver", selectedLabel))
            return;

        foreach (var driver in Enum.GetValues<RotationDriverKind>())
        {
            if (!ImGui.Selectable(GetRotationDriverLabel(driver), config.RotationDriver == driver))
                continue;

            config.RotationDriver = driver;
            config.Save();
            rotationDriver.RefreshAvailability();
        }

        ImGui.EndCombo();
    }

    private void DrawNavigationTuning()
    {
        if (!ImGui.CollapsingHeader("Navigation tuning"))
            return;

        var arrivalDistance = config.ArrivalDistance;
        if (ImGui.InputFloat("Arrival distance", ref arrivalDistance))
        {
            config.ArrivalDistance = Math.Clamp(arrivalDistance, 2f, 50f);
            config.Save();
        }

        var targetSearchRadius = config.TargetSearchRadius;
        if (ImGui.InputFloat("Target search radius", ref targetSearchRadius))
        {
            config.TargetSearchRadius = Math.Clamp(targetSearchRadius, 5f, 100f);
            config.Save();
        }

        var navigationTimeout = (float)config.NavigationTimeoutSeconds;
        if (ImGui.InputFloat("Navigation timeout (s)", ref navigationTimeout))
        {
            config.NavigationTimeoutSeconds = Math.Clamp(navigationTimeout, 30f, 900f);
            config.Save();
        }

        var targetSearchTimeout = (float)config.TargetSearchTimeoutSeconds;
        if (ImGui.InputFloat("Target search timeout (s)", ref targetSearchTimeout))
        {
            config.TargetSearchTimeoutSeconds = Math.Clamp(targetSearchTimeout, 5f, 120f);
            config.Save();
        }
    }

    private void DrawActions()
    {
        if (ImGui.Button("Stop"))
            StopAutomation();
        ImGui.SameLine();
        if (ImGui.Button("Route Active"))
            RouteActiveDropTarget();
        ImGui.SameLine();
        if (ImGui.Button("Next"))
            AdvanceDropTarget();
        ImGui.SameLine();
        if (ImGui.Button("Resume Vulcan"))
            ResumeVulcan();
    }

    private void DrawActiveTarget()
    {
        ImGui.TextUnformatted(dropHuntList.Name);
        if (dropHuntList.Items.Count == 0)
        {
            ImGui.TextDisabled("No active drop hunt list.");
            return;
        }

        var active = dropHuntList.ActiveItem;
        if (active == null)
        {
            ImGui.TextDisabled(dropHuntList.StatusText);
            return;
        }

        var location = active.GetBestLocation();
        ImGui.TextUnformatted($"Target item: {active.ItemName}");
        ImGui.TextUnformatted($"Need: {active.Missing} remaining ({active.Owned}/{active.Needed})");
        ImGui.TextUnformatted(location == null
            ? "Route: no known route data"
            : $"Route: {location.MobName} in territory {location.TerritoryTypeId} ({location.MapX:F1}, {location.MapY:F1})");
    }

    private static void DrawStatusLine(string name, bool available, string? detail)
    {
        ImGui.TextUnformatted($"{name}: {(available ? "ready" : "missing")} ({detail ?? "ok"})");
    }

    private static string GetRotationDriverLabel(RotationDriverKind driver) => driver switch
    {
        RotationDriverKind.WrathCombo => "WrathCombo",
        _ => "RotationSolverReborn",
    };
}
