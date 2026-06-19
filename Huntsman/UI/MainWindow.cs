using System.Numerics;
using Dalamud.Bindings.ImGui;
using Huntsman.Automation;
using Huntsman.IPC;
using Huntsman.Planning;

namespace Huntsman.UI;

internal sealed class MainWindow
{
    private const int MaxManualQuantity = 9999;
    private const int MaxSearchResults = 50;

    private readonly Configuration config;
    private readonly LifestreamIpc lifestream;
    private readonly VnavmeshIpc vnavmesh;
    private readonly RotationDriverService rotationDriver;
    private readonly MonsterNavigator monsterNavigator;
    private readonly DropLocationProvider dropLocations;
    private readonly MaterialPlanner planner;
    private readonly DropHuntListManager dropHuntList;
    private readonly HuntController huntController;
    private readonly CombatJobService combatJobs;
    private readonly List<ManualHuntSelection> manualSelections = [];

    private Page currentPage = Page.Home;
    private IReadOnlyList<DroppableItemOption> manualSearchResults = [];
    private string lastManualSearch = "\0";
    private string manualSearch = string.Empty;
    private uint selectedManualItemId;
    private int manualQuantity = 1;
    private string manualRequestInput = string.Empty;
    private string manualRequestStatus = "Search local drop data and add items to a manual hunt.";

    public MainWindow(
        Configuration config,
        LifestreamIpc lifestream,
        VnavmeshIpc vnavmesh,
        RotationDriverService rotationDriver,
        MonsterNavigator monsterNavigator,
        DropLocationProvider dropLocations,
        MaterialPlanner planner,
        DropHuntListManager dropHuntList,
        HuntController huntController,
        CombatJobService combatJobs)
    {
        this.config = config;
        this.lifestream = lifestream;
        this.vnavmesh = vnavmesh;
        this.rotationDriver = rotationDriver;
        this.monsterNavigator = monsterNavigator;
        this.dropLocations = dropLocations;
        this.planner = planner;
        this.dropHuntList = dropHuntList;
        this.huntController = huntController;
        this.combatJobs = combatJobs;
    }

    public bool IsOpen { get; set; }

    public void Dispose()
    {
        StopHunt();
        rotationDriver.Dispose();
    }

    public void Update()
    {
        monsterNavigator.Update();
        huntController.Update();
    }

    public void StopHunt() => huntController.Stop();

    public void RefreshDependencies()
    {
        lifestream.RefreshAvailability();
        vnavmesh.RefreshAvailability();
        rotationDriver.RefreshAvailability();
    }

    public string BuildStatusLine() =>
        $"Huntsman: LocalDrops={dropLocations.LocalDataAvailable} ({dropLocations.KnownDropItemCount} known), Hunt={huntController.StatusText}, Lifestream={lifestream.Available} ({lifestream.LastError ?? "ok"}), vnavmesh={vnavmesh.Available} ({vnavmesh.LastError ?? "ok"}), Rotation={rotationDriver.Available} ({rotationDriver.StatusDetail}), Mount={monsterNavigator.IsMounted} ({monsterNavigator.LastMountStatus}), Navigation={monsterNavigator.State} ({monsterNavigator.StatusText})";

    public void Draw()
    {
        if (!IsOpen)
            return;

        var open = IsOpen;
        ImGui.SetNextWindowSize(new Vector2(920f, 680f), ImGuiCond.FirstUseEver);
        HuntsmanTheme.Push();
        if (!ImGui.Begin("Huntsman###HuntsmanMain", ref open))
        {
            IsOpen = open;
            ImGui.End();
            HuntsmanTheme.Pop();
            return;
        }

        IsOpen = open;
        RefreshDependencies();
        DrawHeader();
        ImGui.Spacing();
        DrawShell();

        ImGui.End();
        HuntsmanTheme.Pop();
    }

    private void DrawHeader()
    {
        using (HuntsmanWidgets.Card("##top", new Vector2(-1f, 88f), HeaderAccent()))
        {
            ImGui.SetWindowFontScale(1.55f);
            ImGui.TextColored(HuntsmanTheme.Gold, "Huntsman");
            ImGui.SetWindowFontScale(1f);
            ImGui.TextColored(HuntsmanTheme.Dimmed, "Standalone monster-drop hunting for FFXIV.");
            ImGui.Spacing();
            HuntsmanWidgets.Pill(dropLocations.LocalDataAvailable ? "Local drops ready" : "Local drops missing", dropLocations.LocalDataAvailable ? HuntsmanTheme.Green : HuntsmanTheme.Red);
            ImGui.SameLine();
            HuntsmanWidgets.Pill(vnavmesh.Available ? "vnavmesh ready" : "vnavmesh missing", vnavmesh.Available ? HuntsmanTheme.Green : HuntsmanTheme.Red);
            ImGui.SameLine();
            HuntsmanWidgets.Pill(rotationDriver.Available ? $"{rotationDriver.DriverName} ready" : "Combat driver pending", rotationDriver.Available ? HuntsmanTheme.Green : HuntsmanTheme.Warn);
            ImGui.SameLine();
            HuntsmanWidgets.Pill(monsterNavigator.State.ToString(), monsterNavigator.State == MonsterNavigationState.Failed ? HuntsmanTheme.Red : HuntsmanTheme.GoldSoft);
        }
    }

    private void DrawShell()
    {
        using (HuntsmanWidgets.Card("##nav", new Vector2(170f, 0f), HuntsmanTheme.GoldSoft))
        {
            DrawNavItem(Page.Home, "Home");
            DrawNavItem(Page.Hunt, "Hunt");
            DrawNavItem(Page.Route, "Active Route");
            DrawNavItem(Page.Settings, "Settings");
            DrawNavItem(Page.Diagnostics, "Diagnostics");
        }

        ImGui.SameLine();
        using (HuntsmanWidgets.Card("##content", new Vector2(0f, 0f), HuntsmanTheme.Gold))
        {
            switch (currentPage)
            {
                case Page.Home:
                    DrawHome();
                    break;
                case Page.Hunt:
                    DrawHunt();
                    break;
                case Page.Route:
                    DrawRoute();
                    break;
                case Page.Settings:
                    DrawSettings();
                    break;
                case Page.Diagnostics:
                    DrawDiagnostics();
                    break;
            }
        }
    }

    private void DrawNavItem(Page page, string label)
    {
        if (HuntsmanWidgets.NavItem(label, currentPage == page))
            currentPage = page;
    }

    private void DrawHome()
    {
        CenterText("Huntsman", 1.8f, HuntsmanTheme.Gold);
        CenterText("Standalone monster-drop hunting for FFXIV", 1f, HuntsmanTheme.Dimmed);
        ImGui.Spacing();
        ImGui.TextWrapped("Search droppable items, choose quantities, route to monsters, and hand off combat using local drop data with vnavmesh and mount support.");
        ImGui.TextColored(HuntsmanTheme.Dimmed, "No external crafting or gathering plugin dependency.");
        ImGui.Spacing();

        var width = ImGui.GetContentRegionAvail().X;
        var gap = ImGui.GetStyle().ItemSpacing.X;
        var cardW = (width - gap) / 2f;
        DrawStatusCard("Local drop data", dropLocations.LocalDataAvailable, $"{dropLocations.KnownDropItemCount} known items");
        ImGui.SameLine();
        DrawStatusCard("vnavmesh", vnavmesh.Available, vnavmesh.LastError ?? "ready", cardW);
        DrawStatusCard("Combat driver", rotationDriver.Available, rotationDriver.StatusDetail, cardW);
        ImGui.SameLine();
        DrawStatusCard("Mount support", config.AutoMountEnabled, monsterNavigator.LastMountStatus, cardW);

        ImGui.Spacing();
        if (HuntsmanWidgets.GoldButton("Start Hunting", new Vector2(132f, 0f)))
            currentPage = Page.Hunt;
        ImGui.SameLine();
        if (ImGui.Button("Settings", new Vector2(112f, 0f)))
            currentPage = Page.Settings;
        ImGui.SameLine();
        if (ImGui.Button("Diagnostics", new Vector2(126f, 0f)))
            currentPage = Page.Diagnostics;

        ImGui.Spacing();
        using (HuntsmanWidgets.Card("##support", new Vector2(-1f, 112f), HuntsmanTheme.GoldSoft))
        {
            HuntsmanWidgets.Section("Support Huntsman");
            ImGui.TextColored(HuntsmanTheme.Dimmed, "Donation links are not available yet.");
            ImGui.BeginDisabled();
            ImGui.Button("Ko-fi (coming soon)");
            ImGui.SameLine();
            ImGui.Button("GitHub Sponsors (coming soon)");
            ImGui.SameLine();
            ImGui.Button("Patreon (coming soon)");
            ImGui.EndDisabled();
        }
    }

    private void DrawHunt()
    {
        HuntsmanWidgets.Section("Droppable Item Search");
        if (!dropLocations.LocalDataAvailable)
            ImGui.TextColored(HuntsmanTheme.Red, "Drop data unavailable; check Diagnostics.");

        ImGui.SetNextItemWidth(Math.Max(220f, ImGui.GetContentRegionAvail().X * 0.58f));
        if (ImGui.InputTextWithHint("##search", "Search local drop data", ref manualSearch, 128))
            RefreshManualSearchResults(force: true);

        ImGui.SameLine();
        ImGui.SetNextItemWidth(94f);
        if (ImGui.InputInt("Qty", ref manualQuantity))
            manualQuantity = Math.Clamp(manualQuantity, 1, MaxManualQuantity);
        manualQuantity = Math.Clamp(manualQuantity, 1, MaxManualQuantity);

        RefreshManualSearchResults(force: false);
        DrawManualSearchResults();

        if (HuntsmanWidgets.GoldButton("Add Item"))
            AddSelectedManualItem();
        ImGui.SameLine();
        if (HuntsmanWidgets.GoldButton("Start Hunt"))
            StartManualHunt(route: true);
        ImGui.SameLine();
        if (ImGui.Button("Clear"))
            ClearManualSelections();

        ImGui.Spacing();
        DrawManualSelectionList();
        ImGui.TextColored(HuntsmanTheme.Dimmed, manualRequestStatus);

        if (ImGui.CollapsingHeader("Text input fallback"))
        {
            ImGui.InputTextMultiline("##manual-request-input", ref manualRequestInput, 2048, new Vector2(-1f, 52f));
            if (HuntsmanWidgets.GoldButton("Generate From Text"))
                GenerateManualDropHunt();
        }

        ImGui.Spacing();
        using (HuntsmanWidgets.Card("##hunt-list", new Vector2(-1f, 150f), huntController.HasActiveDropWork ? HuntsmanTheme.Gold : HuntsmanTheme.Dimmed))
        {
            HuntsmanWidgets.Section("Current Hunt List");
            HuntsmanWidgets.KeyValue("Status", dropHuntList.StatusText);
            HuntsmanWidgets.KeyValue("Targets", dropHuntList.Items.Count.ToString());
            HuntsmanWidgets.KeyValue("Active", dropHuntList.ActiveItem?.ItemName ?? "none");
        }
    }

    private void DrawRoute()
    {
        var active = dropHuntList.ActiveItem;
        var location = monsterNavigator.ActiveLocation ?? active?.GetBestLocation(monsterNavigator.CurrentTerritoryTypeId);
        var route = monsterNavigator.ActiveRoute;

        HuntsmanWidgets.Section("Active Route");
        HuntsmanWidgets.KeyValue("Active item", active == null ? "none" : $"{active.ItemName} ({active.Missing} remaining)");
        HuntsmanWidgets.KeyValue("Selected mob", location?.MobName ?? "none");
        HuntsmanWidgets.KeyValue("Route destination", route == null ? "none" : $"{route.Destination.X:F1}, {route.Destination.Y:F1}, {route.Destination.Z:F1}");
        HuntsmanWidgets.KeyValue("Navigation", $"{monsterNavigator.State}: {monsterNavigator.StatusText}");
        HuntsmanWidgets.KeyValue("Mount", $"{(monsterNavigator.IsMounted ? "mounted" : "not mounted")} - {monsterNavigator.LastMountStatus}");
        HuntsmanWidgets.KeyValue("Combat", rotationDriver.StatusDetail);

        ImGui.Spacing();
        if (HuntsmanWidgets.GoldButton("Route Active"))
            huntController.RouteActive();
        ImGui.SameLine();
        if (ImGui.Button("Next"))
            huntController.Advance();
        ImGui.SameLine();
        if (ImGui.Button("Stop"))
            StopHunt();
    }

    private void DrawSettings()
    {
        HuntsmanWidgets.Section("Combat Handoff");
        DrawCombatJobSelector();
        DrawRotationDriverSelector();
        HuntsmanWidgets.KeyValue("Combat job", huntController.CombatJobStatus);
        HuntsmanWidgets.KeyValue("Driver", rotationDriver.StatusDetail);

        ImGui.Spacing();
        HuntsmanWidgets.Section("Navigation Tuning");
        DrawToggleSetting("Auto-mount between route points", "Attempts Mount Roulette before longer routes.", config.AutoMountEnabled, v => config.AutoMountEnabled = v);
        DrawToggleSetting("Auto-dismount before combat", "Dismounts before target search and combat handoff.", config.AutoDismountBeforeCombat, v => config.AutoDismountBeforeCombat = v);
        DrawFloatSetting("Minimum mount distance", "Minimum route distance before attempting Mount Roulette.", config.AutoMountMinDistance, 1f, 5f, 200f, v => config.AutoMountMinDistance = v);
        DrawFloatSetting("Arrival distance", "Distance in yalms considered close enough to start target search.", config.ArrivalDistance, 0.1f, 2f, 50f, v => config.ArrivalDistance = v);
        DrawFloatSetting("Target search radius", "Radius around the destination used to find matching battle NPCs.", config.TargetSearchRadius, 0.5f, 5f, 100f, v => config.TargetSearchRadius = v);
        DrawDoubleSetting("Navigation timeout (s)", "Maximum route movement time before failure.", config.NavigationTimeoutSeconds, 1f, 30.0, 900.0, v => config.NavigationTimeoutSeconds = v);
        DrawDoubleSetting("Target search timeout (s)", "How long to wait at arrival before retrying the route.", config.TargetSearchTimeoutSeconds, 1f, 5.0, 120.0, v => config.TargetSearchTimeoutSeconds = v);
    }

    private void DrawDiagnostics()
    {
        HuntsmanWidgets.Section("Data Source");
        HuntsmanWidgets.KeyValue("Core local data", dropLocations.LocalDataAvailable ? "available" : "unavailable");
        HuntsmanWidgets.KeyValue("Known local drop items", dropLocations.KnownDropItemCount.ToString());
        HuntsmanWidgets.KeyValue("Searchable drop items", dropLocations.SearchableDropItemCount.ToString());
        HuntsmanWidgets.KeyValue("Local build error", dropLocations.LastLocalBuildError ?? "none");
        HuntsmanWidgets.KeyValue("Drop index error", dropLocations.LastDroppableIndexError ?? "none");

        ImGui.Spacing();
        HuntsmanWidgets.Section("Hunt Details");
        HuntsmanWidgets.KeyValue("Controller", huntController.StatusText);
        HuntsmanWidgets.KeyValue("Drop list", $"{dropHuntList.Items.Count} target(s), complete={dropHuntList.IsComplete}");
        if (ImGui.CollapsingHeader("Route candidates"))
        {
            foreach (var item in dropHuntList.Items)
                HuntsmanWidgets.KeyValue(item.ItemName, $"{item.GetCandidateLocations(monsterNavigator.CurrentTerritoryTypeId).Count} candidate(s), route={item.HasRoute}");
        }

        ImGui.Spacing();
        HuntsmanWidgets.Section("Navigation");
        var activeItem = dropHuntList.ActiveItem;
        var activeLocation = monsterNavigator.ActiveLocation ?? activeItem?.GetBestLocation(monsterNavigator.CurrentTerritoryTypeId);
        var activeRoute = monsterNavigator.ActiveRoute;
        HuntsmanWidgets.KeyValue("State", monsterNavigator.State.ToString());
        HuntsmanWidgets.KeyValue("Status", monsterNavigator.StatusText);
        HuntsmanWidgets.KeyValue("Active item", activeItem == null ? "none" : $"{activeItem.ItemName} ({activeItem.ItemId})");
        HuntsmanWidgets.KeyValue("Selected mob", activeLocation?.MobName ?? "none");
        HuntsmanWidgets.KeyValue("BNpcName ID", activeLocation?.BNpcNameId?.ToString() ?? "none");
        HuntsmanWidgets.KeyValue("Territory ID", activeLocation?.TerritoryTypeId.ToString() ?? "none");
        HuntsmanWidgets.KeyValue("Map X/Y", activeLocation == null ? "none" : $"{activeLocation.MapX:F1}, {activeLocation.MapY:F1}");
        HuntsmanWidgets.KeyValue("Current territory", monsterNavigator.CurrentTerritoryTypeId.ToString());
        HuntsmanWidgets.KeyValue("Route destination", activeRoute == null ? "none" : $"{activeRoute.Destination.X:F1}, {activeRoute.Destination.Y:F1}, {activeRoute.Destination.Z:F1}");
        HuntsmanWidgets.KeyValue("Last route error", monsterNavigator.LastRouteStartError ?? "none");
        HuntsmanWidgets.KeyValue("Last vnavmesh error", monsterNavigator.LastVnavmeshError ?? "none");
        HuntsmanWidgets.KeyValue("Mounted", monsterNavigator.IsMounted.ToString());
        HuntsmanWidgets.KeyValue("Last mount", monsterNavigator.LastMountStatus);
        HuntsmanWidgets.KeyValue("Last dismount", monsterNavigator.LastDismountStatus);

        ImGui.Spacing();
        HuntsmanWidgets.Section("Runtime Services");
        HuntsmanWidgets.KeyValue("Lifestream", lifestream.Available ? $"busy={lifestream.IsBusy()}" : lifestream.LastError ?? "missing");
        HuntsmanWidgets.KeyValue("vnavmesh", vnavmesh.Available ? $"ready={vnavmesh.IsReady()}, moving={vnavmesh.IsNavigating()}" : vnavmesh.LastError ?? "missing");
        HuntsmanWidgets.KeyValue("Combat driver", rotationDriver.StatusDetail);
    }

    private void DrawStatusCard(string title, bool ready, string detail, float width = 0f)
    {
        var size = new Vector2(width <= 0f ? (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2f : width, 76f);
        using (HuntsmanWidgets.Card($"##status_{title}", size, ready ? HuntsmanTheme.Green : HuntsmanTheme.Warn))
        {
            HuntsmanWidgets.Pill(ready ? "Ready" : "Check", ready ? HuntsmanTheme.Green : HuntsmanTheme.Warn);
            ImGui.SameLine();
            ImGui.TextColored(HuntsmanTheme.Text, title);
            ImGui.TextColored(HuntsmanTheme.Dimmed, detail);
        }
    }

    private void DrawManualSearchResults()
    {
        var selected = FindDroppableOption(selectedManualItemId);
        var preview = selected == null ? "No droppable item selected" : FormatDroppableOption(selected);
        if (!ImGui.BeginCombo("Drop item", preview))
            return;

        if (manualSearchResults.Count == 0)
        {
            ImGui.TextColored(HuntsmanTheme.Dimmed, string.IsNullOrWhiteSpace(manualSearch) ? "Type to search local drop data." : "No matching local drop items.");
        }
        else
        {
            foreach (var option in manualSearchResults)
            {
                if (!ImGui.Selectable(FormatDroppableOption(option), selectedManualItemId == option.ItemId))
                    continue;

                selectedManualItemId = option.ItemId;
                manualRequestStatus = option.HasRouteData ? $"Selected {option.Name}." : $"Selected {option.Name}; no route data.";
            }
        }

        ImGui.EndCombo();
    }

    private void DrawManualSelectionList()
    {
        using (HuntsmanWidgets.Card("##manual-selected-items", new Vector2(-1f, 116f), HuntsmanTheme.GoldSoft))
        {
            if (manualSelections.Count == 0)
            {
                ImGui.TextColored(HuntsmanTheme.Dimmed, "No manual hunt items selected.");
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
                ImGui.TextUnformatted($"{selection.Name} x{selection.Quantity} - {routeText}");
                ImGui.PopID();
            }
        }
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

    private void StartManualHunt(bool route)
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

        if (route)
            huntController.RouteActive();
    }

    private void ClearManualSelections()
    {
        manualSelections.Clear();
        selectedManualItemId = 0;
        manualRequestStatus = "Cleared manual hunt selections.";
    }

    private void GenerateManualDropHunt()
    {
        var requirements = planner.Plan(manualRequestInput);
        dropHuntList.Generate(requirements, "Manual Drop Hunt");
        manualRequestStatus = dropHuntList.Items.Count == 0
            ? "No missing droppable materials found."
            : $"Generated {dropHuntList.Items.Count} drop target(s).";
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

    private void DrawToggleSetting(string label, string tooltip, bool current, Action<bool> set)
    {
        var value = current;
        if (HuntsmanWidgets.Toggle($"##{label}", ref value))
        {
            set(value);
            config.Save();
        }

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(value ? HuntsmanTheme.Text : HuntsmanTheme.Dimmed, label);
        Tooltip(tooltip);
    }

    private void DrawFloatSetting(string label, string tooltip, float current, float speed, float min, float max, Action<float> set)
    {
        var value = current;
        if (ImGui.DragFloat(label, ref value, speed, min, max))
        {
            set(Math.Clamp(value, min, max));
            config.Save();
        }

        Tooltip(tooltip);
    }

    private void DrawDoubleSetting(string label, string tooltip, double current, float speed, double min, double max, Action<double> set)
    {
        var value = (float)current;
        if (ImGui.DragFloat(label, ref value, speed, (float)min, (float)max))
        {
            set(Math.Clamp(value, (float)min, (float)max));
            config.Save();
        }

        Tooltip(tooltip);
    }

    private DroppableItemOption? FindDroppableOption(uint itemId) =>
        itemId == 0 ? null : dropLocations.GetDroppableItems().FirstOrDefault(option => option.ItemId == itemId);

    private static Vector4 HeaderAccent() => HuntsmanTheme.Gold;

    private static string FormatDroppableOption(DroppableItemOption option) =>
        option.HasRouteData
            ? $"{option.Name} - {option.MobCount} mob(s), {option.ZoneCount} zone(s)"
            : $"{option.Name} - no route data";

    private static void Tooltip(string text)
    {
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }

    private static void CenterText(string text, float scale, Vector4 color)
    {
        var width = ImGui.GetContentRegionAvail().X;
        ImGui.SetWindowFontScale(scale);
        var textWidth = ImGui.CalcTextSize(text).X;
        var offset = (width - textWidth) * 0.5f;
        if (offset > 0f)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offset);
        ImGui.TextColored(color, text);
        ImGui.SetWindowFontScale(1f);
    }

    private static string GetRotationDriverLabel(RotationDriverKind driver) => driver switch
    {
        RotationDriverKind.WrathCombo => "WrathCombo",
        _ => "RotationSolverReborn",
    };

    private enum Page
    {
        Home,
        Hunt,
        Route,
        Settings,
        Diagnostics,
    }

    private sealed record ManualHuntSelection(uint ItemId, string Name, int Quantity, bool HasRouteData);
}
