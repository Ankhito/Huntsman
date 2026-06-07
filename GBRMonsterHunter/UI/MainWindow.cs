using Dalamud.Bindings.ImGui;
using GBRMonsterHunter.Automation;
using GBRMonsterHunter.IPC;
using GBRMonsterHunter.Planning;
using System.Numerics;

namespace GBRMonsterHunter.UI;

internal sealed class MainWindow
{
    private static readonly Vector4 Bg = new(0.08f, 0.09f, 0.11f, 1f);
    private static readonly Vector4 CardBg = new(0.12f, 0.13f, 0.16f, 1f);
    private static readonly Vector4 CardBgSoft = new(0.16f, 0.17f, 0.20f, 1f);
    private static readonly Vector4 Accent = new(0.25f, 0.72f, 0.68f, 1f);
    private static readonly Vector4 AccentSoft = new(0.31f, 0.55f, 0.86f, 1f);
    private static readonly Vector4 Warn = new(0.95f, 0.66f, 0.30f, 1f);
    private static readonly Vector4 Error = new(0.92f, 0.32f, 0.34f, 1f);
    private static readonly Vector4 TextDim = new(0.62f, 0.66f, 0.72f, 1f);

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

        var open = IsOpen;
        ImGui.SetNextWindowSize(new Vector2(740, 560), ImGuiCond.FirstUseEver);
        PushWindowStyle();
        if (!ImGui.Begin("GBR Monster Hunter", ref open))
        {
            IsOpen = open;
            ImGui.End();
            PopWindowStyle();
            return;
        }

        IsOpen = open;
        DrawHeader();
        if (ImGui.BeginTabBar("GBRMonsterHunterTabs"))
        {
            DrawTab("Dashboard", DrawDashboard);
            DrawTab("Settings", DrawSettings);
            DrawTab("Diagnostics", DrawDiagnostics);
            ImGui.EndTabBar();
        }

        ImGui.End();
        PopWindowStyle();
    }

    private void DrawHeader()
    {
        RefreshDependencies();
        var active = automation.HasActiveDropWork;
        var failed = monsterNavigator.State == MonsterNavigationState.Failed;
        var accent = failed ? Error : active ? Accent : TextDim;

        using (BeginCard("##header", new Vector2(-1, 90), accent))
        {
            ImGui.TextColored(accent, failed ? "Attention" : active ? "Hunting" : "Standing by");
            ImGui.SameLine();
            ImGui.TextUnformatted("GBR Monster Hunter");
            ImGui.TextColored(TextDim, "Drop-material routing from GatherBuddy Reborn / Vulcan plans.");

            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 6);
            DrawPill(automation.HasActiveDropWork ? "Drop Hunt Active" : "No Active Hunt", active ? Accent : TextDim);
            ImGui.SameLine();
            DrawPill(automation.VulcanPaused ? "Vulcan Paused" : automation.QueueState, automation.VulcanPaused ? Warn : AccentSoft);
            ImGui.SameLine();
            DrawPill(monsterNavigator.State.ToString(), failed ? Error : AccentSoft);
            ImGui.SameLine();
            DrawPill(rotationDriver.Available ? $"{rotationDriver.DriverName} Ready" : "Combat Driver Missing", rotationDriver.Available ? Accent : Warn);
        }
    }

    private void DrawDashboard()
    {
        ImGui.Spacing();
        var w = ImGui.GetContentRegionAvail().X;
        var gap = ImGui.GetStyle().ItemSpacing.X;
        var cardW = (w - gap * 2) / 3f;
        var active = dropHuntList.ActiveItem;
        var location = active?.GetBestLocation();

        using (BeginCard("##plan-card", new Vector2(cardW, 112), automation.HasActiveDropWork ? Accent : TextDim))
            DrawMetric("Vulcan Plan", automation.CurrentPlanName, automation.QueueState);
        ImGui.SameLine();
        using (BeginCard("##target-card", new Vector2(cardW, 112), active?.HasRoute == true ? Accent : Warn))
            DrawMetric("Drop Target", active?.ItemName ?? "None", active == null ? dropHuntList.StatusText : $"{active.Missing} remaining");
        ImGui.SameLine();
        using (BeginCard("##nav-card", new Vector2(cardW, 112), monsterNavigator.State == MonsterNavigationState.Failed ? Error : AccentSoft))
            DrawMetric("Navigation", monsterNavigator.State.ToString(), monsterNavigator.StatusText);

        using (BeginCard("##controls", new Vector2(-1, 118), AccentSoft))
        {
            DrawSectionTitle("Controls");
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
            ImGui.SameLine();
            DrawPill(gbr.Available ? "GBR Available" : "GBR Missing", gbr.Available ? Accent : Error);
        }

        using (BeginCard("##active-target", new Vector2(-1, 150), active?.HasRoute == true ? Accent : Warn))
        {
            DrawSectionTitle("Active Drop Target");
            if (active == null)
            {
                ImGui.TextColored(TextDim, dropHuntList.StatusText);
            }
            else
            {
                DrawKeyValue("Item", active.ItemName);
                DrawKeyValue("Need", $"{active.Missing} remaining ({active.Owned}/{active.Needed})");
                DrawKeyValue("Mob", location?.MobName ?? "no known route data");
                DrawKeyValue("Route", location == null ? "none" : $"Territory {location.TerritoryTypeId} ({location.MapX:F1}, {location.MapY:F1})");
            }
        }

        using (BeginCard("##deps", new Vector2(-1, 126), DependenciesReady() ? Accent : Warn))
        {
            DrawSectionTitle("Dependencies");
            DrawDependencyPills();
        }
    }

    private void DrawSettings()
    {
        ImGui.Spacing();
        using (BeginCard("##combat-settings", new Vector2(-1, 152), Accent))
        {
            DrawSectionTitle("Combat Handoff");
            DrawCombatJobSelector();
            DrawRotationDriverSelector();
            DrawKeyValue("Combat job", automation.CombatJobStatus);
            DrawKeyValue("Driver", rotationDriver.StatusDetail);
        }

        using (BeginCard("##route-settings", new Vector2(-1, 112), AccentSoft))
        {
            DrawSectionTitle("Routing");
            DrawKeyValue("Teleporter command", config.TeleporterCommandTemplate);
            DrawKeyValue("Route source", "GatherBuddy mob-drop data, then local fallbacks");
            DrawKeyValue("Selection", "same territory, highest spawn count, stable ordering");
        }

        using (BeginCard("##navigation-settings", new Vector2(-1, 210), Accent))
        {
            DrawSectionTitle("Navigation Tuning");
            var arrivalDistance = config.ArrivalDistance;
            if (DrawFloatSetting("Arrival distance", "Distance in yalms considered close enough to start target search.", ref arrivalDistance, 0.1f, 2f, 50f))
            {
                config.ArrivalDistance = arrivalDistance;
                config.Save();
            }

            var targetSearchRadius = config.TargetSearchRadius;
            if (DrawFloatSetting("Target search radius", "Radius around the destination used to find matching battle NPCs.", ref targetSearchRadius, 0.5f, 5f, 100f))
            {
                config.TargetSearchRadius = targetSearchRadius;
                config.Save();
            }

            var navigationTimeout = config.NavigationTimeoutSeconds;
            if (DrawDoubleSetting("Navigation timeout (s)", "Maximum route movement time before failure.", ref navigationTimeout, 1f, 30.0, 900.0))
            {
                config.NavigationTimeoutSeconds = navigationTimeout;
                config.Save();
            }

            var targetSearchTimeout = config.TargetSearchTimeoutSeconds;
            if (DrawDoubleSetting("Target search timeout (s)", "How long to wait at arrival before retrying the route.", ref targetSearchTimeout, 1f, 5.0, 120.0))
            {
                config.TargetSearchTimeoutSeconds = targetSearchTimeout;
                config.Save();
            }
        }
    }

    private void DrawDiagnostics()
    {
        ImGui.Spacing();
        using (BeginCard("##automation-diagnostics", new Vector2(-1, 158), automation.HasActiveDropWork ? Accent : TextDim))
        {
            DrawSectionTitle("Automation State");
            DrawKeyValue("Automation", automation.StatusText);
            DrawKeyValue("Vulcan plan", automation.CurrentPlanName);
            DrawKeyValue("Vulcan queue", automation.QueueState);
            DrawKeyValue("Vulcan paused", automation.VulcanPaused.ToString());
            DrawKeyValue("Drop list", $"{dropHuntList.Items.Count} target(s), complete={dropHuntList.IsComplete}");
        }

        using (BeginCard("##dependency-diagnostics", new Vector2(-1, 174), DependenciesReady() ? AccentSoft : Warn))
        {
            DrawSectionTitle("Dependency Status");
            DrawKeyValue("GBR", gbr.Available ? $"IPC v{gbr.GetVersion()}: {gbr.GetStatus()}" : gbr.LastError ?? "missing");
            DrawKeyValue("Vulcan", automation.VulcanListenerError ?? "ok");
            DrawKeyValue("Lifestream", lifestream.Available ? $"busy={lifestream.IsBusy()}" : lifestream.LastError ?? "missing");
            DrawKeyValue("vnavmesh", vnavmesh.Available ? $"ready={vnavmesh.IsReady()}, moving={vnavmesh.IsNavigating()}" : vnavmesh.LastError ?? "missing");
            DrawKeyValue("Rotation", rotationDriver.StatusDetail);
        }

        using (BeginCard("##navigation-diagnostics", new Vector2(-1, 132), monsterNavigator.State == MonsterNavigationState.Failed ? Error : Accent))
        {
            DrawSectionTitle("Navigation State");
            DrawKeyValue("State", monsterNavigator.State.ToString());
            DrawKeyValue("Status", monsterNavigator.StatusText);
            DrawKeyValue("Active item", dropHuntList.ActiveItem?.ItemName ?? "none");
        }
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

    private void DrawDependencyPills()
    {
        DrawPill("GBR", gbr.Available ? Accent : Error);
        ImGui.SameLine();
        DrawPill("Vulcan", string.IsNullOrWhiteSpace(automation.VulcanListenerError) ? Accent : Warn);
        ImGui.SameLine();
        DrawPill("Lifestream", lifestream.Available ? Accent : Warn);
        ImGui.SameLine();
        DrawPill("vnavmesh", vnavmesh.Available ? Accent : Error);
        ImGui.SameLine();
        DrawPill(rotationDriver.DriverName, rotationDriver.Available ? Accent : Warn);
    }

    private void DrawTab(string label, Action draw)
    {
        if (!ImGui.BeginTabItem(label))
            return;

        draw();
        ImGui.EndTabItem();
    }

    private bool DependenciesReady() => gbr.Available && vnavmesh.Available;

    private static void DrawMetric(string label, string value, string detail)
    {
        ImGui.TextColored(TextDim, label);
        ImGui.SetWindowFontScale(1.25f);
        ImGui.TextUnformatted(Fit(value, ImGui.GetContentRegionAvail().X));
        ImGui.SetWindowFontScale(1f);
        ImGui.TextColored(TextDim, Fit(detail, ImGui.GetContentRegionAvail().X));
    }

    private static void DrawSectionTitle(string label)
    {
        ImGui.TextColored(TextDim, label.ToUpperInvariant());
        ImGui.Separator();
    }

    private static void DrawKeyValue(string label, string value)
    {
        ImGui.TextColored(TextDim, label);
        ImGui.SameLine(180);
        ImGui.TextWrapped(value);
    }

    private static void DrawPill(string label, Vector4 color)
    {
        var pad = new Vector2(9, 4);
        var pos = ImGui.GetCursorScreenPos();
        var size = ImGui.CalcTextSize(label) + pad * 2;
        ImGui.GetWindowDrawList().AddRectFilled(pos, pos + size, ImGui.GetColorU32(new Vector4(color.X, color.Y, color.Z, 0.18f)), 6f);
        ImGui.GetWindowDrawList().AddRect(pos, pos + size, ImGui.GetColorU32(new Vector4(color.X, color.Y, color.Z, 0.55f)), 6f);
        ImGui.SetCursorScreenPos(pos + pad);
        ImGui.TextColored(color, label);
        ImGui.SetCursorScreenPos(new Vector2(pos.X + size.X, pos.Y));
        ImGui.Dummy(size);
    }

    private static bool DrawFloatSetting(string label, string tooltip, ref float value, float speed, float min, float max)
    {
        if (ImGui.DragFloat(label, ref value, speed, min, max))
        {
            value = Math.Clamp(value, min, max);
            DrawTooltip(tooltip);
            return true;
        }

        DrawTooltip(tooltip);
        return false;
    }

    private static bool DrawDoubleSetting(string label, string tooltip, ref double value, float speed, double min, double max)
    {
        var working = (float)value;
        if (ImGui.DragFloat(label, ref working, speed, (float)min, (float)max))
        {
            value = Math.Clamp(working, (float)min, (float)max);
            DrawTooltip(tooltip);
            return true;
        }

        DrawTooltip(tooltip);
        return false;
    }

    private static void DrawTooltip(string text)
    {
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }

    private static string Fit(string text, float maxWidth)
    {
        if (ImGui.CalcTextSize(text).X <= maxWidth)
            return text;

        while (text.Length > 1 && ImGui.CalcTextSize(text + "...").X > maxWidth)
            text = text[..^1];
        return text + "...";
    }

    private static CardScope BeginCard(string id, Vector2 size, Vector4 accent)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, CardBg);
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(accent.X, accent.Y, accent.Z, 0.45f));
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 8f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 1f);
        ImGui.BeginChild(id, size, true, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, CardBgSoft);
        return new CardScope();
    }

    private static void PushWindowStyle()
    {
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Bg);
        ImGui.PushStyleColor(ImGuiCol.Tab, CardBg);
        ImGui.PushStyleColor(ImGuiCol.TabHovered, CardBgSoft);
        ImGui.PushStyleColor(ImGuiCol.TabActive, CardBgSoft);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, CardBgSoft);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(0.20f, 0.22f, 0.26f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Button, CardBgSoft);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.20f, 0.28f, 0.32f, 1f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 8f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 5f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 8f);
    }

    private static void PopWindowStyle()
    {
        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(8);
    }

    private static string GetRotationDriverLabel(RotationDriverKind driver) => driver switch
    {
        RotationDriverKind.WrathCombo => "WrathCombo",
        _ => "RotationSolverReborn",
    };

    private readonly ref struct CardScope
    {
        public void Dispose()
        {
            ImGui.PopStyleColor();
            ImGui.EndChild();
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor(2);
        }
    }
}
