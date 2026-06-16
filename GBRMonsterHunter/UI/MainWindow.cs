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
    private const int MaxManualQuantity = 9999;
    private const int MaxSearchResults = 50;

    private readonly Configuration config;
    private readonly GatherBuddyRebornIpc gbr;
    private readonly LifestreamIpc lifestream;
    private readonly VnavmeshIpc vnavmesh;
    private readonly RotationDriverService rotationDriver;
    private readonly MonsterNavigator monsterNavigator;
    private readonly DropLocationProvider dropLocations;
    private readonly MaterialPlanner planner;
    private readonly DropHuntListManager dropHuntList;
    private readonly VulcanDropAutomation automation;
    private readonly CombatJobService combatJobs;
    private readonly List<ManualHuntSelection> manualSelections = [];
    private IReadOnlyList<DroppableItemOption> manualSearchResults = [];
    private string lastManualSearch = "\0";
    private string manualSearch = string.Empty;
    private uint selectedManualItemId;
    private int manualQuantity = 1;
    private string manualRequestInput = string.Empty;
    private string manualRequestStatus = "Search local drop data and add items to a manual hunt.";

    public MainWindow(
        Configuration config,
        GatherBuddyRebornIpc gbr,
        LifestreamIpc lifestream,
        VnavmeshIpc vnavmesh,
        RotationDriverService rotationDriver,
        MonsterNavigator monsterNavigator,
        DropLocationProvider dropLocations,
        MaterialPlanner planner,
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
        this.dropLocations = dropLocations;
        this.planner = planner;
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
        $"GBRMonsterHunter: LocalDrops={dropLocations.LocalDataAvailable} ({dropLocations.KnownDropItemCount} known), Vulcan={automation.CurrentPlanName} ({automation.StatusText}), OptionalGBR={gbr.Available} ({gbr.LastError ?? gbr.GetStatus()}), Lifestream={lifestream.Available} ({lifestream.LastError ?? "ok"}), vnavmesh={vnavmesh.Available} ({vnavmesh.LastError ?? "ok"}), Rotation={rotationDriver.Available} ({rotationDriver.StatusDetail}), MonsterNav={monsterNavigator.State} ({monsterNavigator.StatusText})";

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
            ImGui.TextColored(TextDim, "Standalone drop-material routing using local game data.");

            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 6);
            DrawPill(automation.HasActiveDropWork ? "Drop Hunt Active" : "No Active Hunt", active ? Accent : TextDim);
            ImGui.SameLine();
            DrawPill(dropLocations.LocalDataAvailable ? "Local Drops Ready" : "Local Drops Missing", dropLocations.LocalDataAvailable ? Accent : Error);
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
            DrawMetric("Plan Source", dropHuntList.Enabled ? dropHuntList.Name : automation.CurrentPlanName, automation.QueueState);
        ImGui.SameLine();
        using (BeginCard("##target-card", new Vector2(cardW, 112), active?.HasRoute == true ? Accent : Warn))
            DrawMetric("Drop Target", active?.ItemName ?? "None", active == null ? dropHuntList.StatusText : $"{active.Missing} remaining");
        ImGui.SameLine();
        using (BeginCard("##nav-card", new Vector2(cardW, 112), monsterNavigator.State == MonsterNavigationState.Failed ? Error : AccentSoft))
            DrawMetric("Navigation", monsterNavigator.State.ToString(), monsterNavigator.StatusText);

        DrawManualHuntCard();

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
            DrawPill(gbr.Available ? "Optional GBR Available" : "Optional GBR Missing", gbr.Available ? Accent : Warn);
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
            DrawKeyValue("Route source", "Local LuminaSupplemental mob-drop data first");
            DrawKeyValue("Optional integrations", "Vulcan, GatherBuddy Reborn");
            DrawKeyValue("Selection", "same territory, highest spawn count, stable ordering");
        }

        using (BeginCard("##navigation-settings", new Vector2(-1, 292), Accent))
        {
            DrawSectionTitle("Navigation Tuning");
            var autoMountEnabled = config.AutoMountEnabled;
            if (ImGui.Checkbox("Auto-mount between route points", ref autoMountEnabled))
            {
                config.AutoMountEnabled = autoMountEnabled;
                config.Save();
            }

            var autoDismountBeforeCombat = config.AutoDismountBeforeCombat;
            if (ImGui.Checkbox("Auto-dismount before combat", ref autoDismountBeforeCombat))
            {
                config.AutoDismountBeforeCombat = autoDismountBeforeCombat;
                config.Save();
            }

            var autoMountMinDistance = config.AutoMountMinDistance;
            if (DrawFloatSetting("Minimum mount distance", "Minimum route distance before attempting Mount Roulette.", ref autoMountMinDistance, 1f, 5f, 200f))
            {
                config.AutoMountMinDistance = autoMountMinDistance;
                config.Save();
            }

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

        using (BeginCard("##drop-data-diagnostics", new Vector2(-1, 132), dropLocations.LocalDataAvailable ? Accent : Error))
        {
            DrawSectionTitle("Drop Data Source");
            DrawKeyValue("Core local data", dropLocations.LocalDataAvailable ? "available" : "unavailable");
            DrawKeyValue("Known local drop items", dropLocations.KnownDropItemCount.ToString());
            DrawKeyValue("Searchable drop items", dropLocations.SearchableDropItemCount.ToString());
            DrawKeyValue("Local build error", dropLocations.LastLocalBuildError ?? "none");
            DrawKeyValue("Drop index error", dropLocations.LastDroppableIndexError ?? "none");
            DrawKeyValue("Optional GatherBuddy fallback", dropLocations.GatherBuddyFallbackAvailable ? "available" : dropLocations.LastGatherBuddyError ?? "unavailable");
        }

        using (BeginCard("##dependency-diagnostics", new Vector2(-1, 174), DependenciesReady() ? AccentSoft : Warn))
        {
            DrawSectionTitle("Optional Integration Status");
            DrawKeyValue("GatherBuddy Reborn IPC", gbr.Available ? $"IPC v{gbr.GetVersion()}: {gbr.GetStatus()}" : gbr.LastError ?? "missing");
            DrawKeyValue("Vulcan", automation.VulcanListenerError ?? "ok");
            DrawKeyValue("Lifestream", lifestream.Available ? $"busy={lifestream.IsBusy()}" : lifestream.LastError ?? "missing");
            DrawKeyValue("vnavmesh", vnavmesh.Available ? $"ready={vnavmesh.IsReady()}, moving={vnavmesh.IsNavigating()}" : vnavmesh.LastError ?? "missing");
            DrawKeyValue("Rotation", rotationDriver.StatusDetail);
        }

        using (BeginCard("##navigation-diagnostics", new Vector2(-1, 344), monsterNavigator.State == MonsterNavigationState.Failed ? Error : Accent))
        {
            var activeItem = dropHuntList.ActiveItem;
            var activeLocation = monsterNavigator.ActiveLocation ?? activeItem?.GetBestLocation(monsterNavigator.CurrentTerritoryTypeId);
            var activeRoute = monsterNavigator.ActiveRoute;
            DrawSectionTitle("Navigation State");
            DrawKeyValue("State", monsterNavigator.State.ToString());
            DrawKeyValue("Status", monsterNavigator.StatusText);
            DrawKeyValue("Active item", activeItem == null ? "none" : $"{activeItem.ItemName} ({activeItem.ItemId})");
            DrawKeyValue("Selected mob", activeLocation?.MobName ?? "none");
            DrawKeyValue("BNpcName ID", activeLocation?.BNpcNameId?.ToString() ?? "none");
            DrawKeyValue("Territory ID", activeLocation?.TerritoryTypeId.ToString() ?? "none");
            DrawKeyValue("Map ID", activeLocation?.MapRowId.ToString() ?? "none");
            DrawKeyValue("Map X/Y", activeLocation == null ? "none" : $"{activeLocation.MapX:F1}, {activeLocation.MapY:F1}");
            DrawKeyValue("Current territory", monsterNavigator.CurrentTerritoryTypeId.ToString());
            DrawKeyValue("Route destination", activeRoute == null ? "none" : $"{activeRoute.Destination.X:F1}, {activeRoute.Destination.Y:F1}, {activeRoute.Destination.Z:F1}");
            DrawKeyValue("Last route error", monsterNavigator.LastRouteStartError ?? "none");
            DrawKeyValue("Last vnavmesh error", monsterNavigator.LastVnavmeshError ?? "none");
            DrawKeyValue("Mounted", monsterNavigator.IsMounted.ToString());
            DrawKeyValue("Auto-mount", config.AutoMountEnabled.ToString());
            DrawKeyValue("Auto-dismount", config.AutoDismountBeforeCombat.ToString());
            DrawKeyValue("Last mount", monsterNavigator.LastMountStatus);
            DrawKeyValue("Last dismount", monsterNavigator.LastDismountStatus);
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
        DrawPill("Local data", dropLocations.LocalDataAvailable ? Accent : Error);
        ImGui.SameLine();
        DrawPill("Vulcan optional", string.IsNullOrWhiteSpace(automation.VulcanListenerError) ? Accent : Warn);
        ImGui.SameLine();
        DrawPill("GBR optional", gbr.Available ? Accent : Warn);
        ImGui.SameLine();
        DrawPill("Lifestream", lifestream.Available ? Accent : Warn);
        ImGui.SameLine();
        DrawPill("vnavmesh", vnavmesh.Available ? Accent : Error);
        ImGui.SameLine();
        DrawPill(rotationDriver.DriverName, rotationDriver.Available ? Accent : Warn);
    }

    private void DrawManualHuntCard()
    {
        using (BeginCard("##manual", new Vector2(-1, 302), Accent))
        {
            DrawSectionTitle("Manual Hunt");
            if (!dropLocations.LocalDataAvailable)
                ImGui.TextColored(Error, Fit("Drop data unavailable; check Diagnostics.", ImGui.GetContentRegionAvail().X));

            ImGui.SetNextItemWidth(Math.Max(180f, ImGui.GetContentRegionAvail().X * 0.55f));
            if (ImGui.InputText("Search drops", ref manualSearch, 128))
                RefreshManualSearchResults(force: true);

            ImGui.SameLine();
            ImGui.SetNextItemWidth(96f);
            if (ImGui.InputInt("Qty", ref manualQuantity))
                manualQuantity = Math.Clamp(manualQuantity, 1, MaxManualQuantity);
            manualQuantity = Math.Clamp(manualQuantity, 1, MaxManualQuantity);

            RefreshManualSearchResults(force: false);
            DrawManualSearchResults();

            if (ImGui.Button("Add Item"))
                AddSelectedManualItem();
            ImGui.SameLine();
            if (ImGui.Button("Start Hunt"))
                StartManualHunt();
            ImGui.SameLine();
            if (ImGui.Button("Clear"))
                ClearManualSelections();

            DrawManualSelectionList();
            ImGui.TextColored(TextDim, Fit(manualRequestStatus, ImGui.GetContentRegionAvail().X));

            if (ImGui.CollapsingHeader("Text input fallback"))
            {
                ImGui.InputTextMultiline("##manual-request-input", ref manualRequestInput, 2048, new Vector2(-1, 44));
                if (ImGui.Button("Generate From Text"))
                    GenerateManualDropHunt();
            }
        }
    }

    private void DrawManualSearchResults()
    {
        var selected = FindDroppableOption(selectedManualItemId);
        var preview = selected == null
            ? "No droppable item selected"
            : FormatDroppableOption(selected);

        if (!ImGui.BeginCombo("Drop item", preview))
            return;

        if (manualSearchResults.Count == 0)
        {
            ImGui.TextColored(TextDim, string.IsNullOrWhiteSpace(manualSearch) ? "Type to search local drop data." : "No matching local drop items.");
        }
        else
        {
            foreach (var option in manualSearchResults)
            {
                if (!ImGui.Selectable(FormatDroppableOption(option), selectedManualItemId == option.ItemId))
                    continue;

                selectedManualItemId = option.ItemId;
                manualRequestStatus = option.HasRouteData
                    ? $"Selected {option.Name}."
                    : $"Selected {option.Name}; no route data.";
            }
        }

        ImGui.EndCombo();
    }

    private void DrawManualSelectionList()
    {
        ImGui.BeginChild("##manual-selected-items", new Vector2(-1, 82), true);
        if (manualSelections.Count == 0)
        {
            ImGui.TextColored(TextDim, "No manual hunt items selected.");
            ImGui.EndChild();
            return;
        }

        for (var i = 0; i < manualSelections.Count; i++)
        {
            var selection = manualSelections[i];
            ImGui.PushID((int)selection.ItemId);
            if (ImGui.SmallButton("Remove"))
            {
                manualSelections.RemoveAt(i);
                manualRequestStatus = $"Removed {selection.Name}.";
                ImGui.PopID();
                break;
            }

            ImGui.SameLine();
            var routeText = selection.HasRouteData ? "route data" : "no route data";
            ImGui.TextUnformatted(Fit($"{selection.Name} x{selection.Quantity} - {routeText}", ImGui.GetContentRegionAvail().X));
            ImGui.PopID();
        }

        ImGui.EndChild();
    }

    private void RefreshManualSearchResults(bool force)
    {
        var search = manualSearch.Trim();
        if (!force && string.Equals(search, lastManualSearch, StringComparison.Ordinal))
            return;

        lastManualSearch = search;
        var options = dropLocations.GetDroppableItems();
        manualSearchResults = string.IsNullOrWhiteSpace(search)
            ? options.Take(MaxSearchResults).ToList()
            : options
                .Where(option => option.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
                .Take(MaxSearchResults)
                .ToList();

        if (selectedManualItemId != 0 && manualSearchResults.All(option => option.ItemId != selectedManualItemId))
            selectedManualItemId = 0;
    }

    private void AddSelectedManualItem()
    {
        if (!dropLocations.LocalDataAvailable)
        {
            manualRequestStatus = "Drop data unavailable; check Diagnostics.";
            return;
        }

        var option = FindDroppableOption(selectedManualItemId);
        if (option == null)
        {
            manualRequestStatus = "No droppable item selected.";
            return;
        }

        manualQuantity = Math.Clamp(manualQuantity, 1, MaxManualQuantity);
        var index = manualSelections.FindIndex(selection => selection.ItemId == option.ItemId);
        if (index >= 0)
        {
            var existing = manualSelections[index];
            var quantity = Math.Clamp(existing.Quantity + manualQuantity, 1, MaxManualQuantity);
            manualSelections[index] = existing with { Quantity = quantity };
            manualRequestStatus = $"Updated {option.Name} to x{quantity}.";
            return;
        }

        manualSelections.Add(new ManualHuntSelection(option.ItemId, option.Name, manualQuantity, option.HasRouteData));
        manualRequestStatus = option.HasRouteData
            ? $"Added {manualQuantity}x {option.Name}."
            : $"Added {manualQuantity}x {option.Name}; no route data.";
    }

    private void StartManualHunt()
    {
        if (manualSelections.Count == 0)
        {
            manualRequestStatus = "Add at least one droppable item before starting a hunt.";
            return;
        }

        var materialCounts = manualSelections.ToDictionary(selection => selection.ItemId, selection => selection.Quantity);
        var requirements = planner.PlanMaterialCounts(materialCounts);
        dropHuntList.Generate(requirements, "Manual Drop Hunt");

        if (dropHuntList.Items.Count == 0)
        {
            manualRequestStatus = "No missing droppable materials found.";
            return;
        }

        var noRouteCount = dropHuntList.Items.Count(item => !item.HasRoute);
        manualRequestStatus = noRouteCount == 0
            ? $"Generated {dropHuntList.Items.Count} drop target(s)."
            : $"Generated {dropHuntList.Items.Count} drop target(s); {noRouteCount} without route data.";
    }

    private void ClearManualSelections()
    {
        manualSelections.Clear();
        selectedManualItemId = 0;
        manualRequestStatus = "Cleared manual hunt selections.";
    }

    private void DrawTab(string label, Action draw)
    {
        if (!ImGui.BeginTabItem(label))
            return;

        draw();
        ImGui.EndTabItem();
    }

    private void GenerateManualDropHunt()
    {
        var requirements = planner.Plan(manualRequestInput);
        dropHuntList.Generate(requirements, "Manual Drop Hunt");
        manualRequestStatus = dropHuntList.Items.Count == 0
            ? "No missing droppable materials found."
            : $"Generated {dropHuntList.Items.Count} drop target(s).";
    }

    private DroppableItemOption? FindDroppableOption(uint itemId) =>
        itemId == 0 ? null : dropLocations.GetDroppableItems().FirstOrDefault(option => option.ItemId == itemId);

    private static string FormatDroppableOption(DroppableItemOption option) =>
        option.HasRouteData
            ? $"{option.Name} - {option.MobCount} mob(s), {option.ZoneCount} zone(s)"
            : $"{option.Name} - no route data";

    private bool DependenciesReady() => dropLocations.LocalDataAvailable && vnavmesh.Available;

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

    private sealed record ManualHuntSelection(uint ItemId, string Name, int Quantity, bool HasRouteData);
}
