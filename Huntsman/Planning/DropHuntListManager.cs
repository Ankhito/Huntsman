namespace Huntsman.Planning;

internal sealed class DropHuntListManager(DropLocationProvider dropLocations)
{
    private readonly List<DropHuntListItem> items = [];
    private int activeIndex;

    public string Name { get; private set; } = "Manual Drop Hunt";
    public bool Enabled { get; private set; }
    public bool IsManualHunt => string.Equals(Name, "Manual Drop Hunt", StringComparison.Ordinal);
    public IReadOnlyList<DropHuntListItem> Items => items;
    public DropHuntListItem? ActiveItem => Enabled && activeIndex >= 0 && activeIndex < items.Count ? items[activeIndex] : null;
    public bool IsComplete => items.Count > 0 && items.All(item => item.Complete);
    public string StatusText { get; private set; } = "No drop hunt list generated.";

    public void Generate(IEnumerable<MaterialRequirement> requirements, string? listName = null)
    {
        items.Clear();
        items.AddRange(requirements
            .Where(req => req.SourceKind == MaterialSourceKind.Drop && req.Missing > 0)
            .Select(req => new DropHuntListItem(req.ItemId, req.Name, req.Needed, req.Owned, dropLocations.GetDropInfo(req.ItemId), req.RecipeId))
            .OrderBy(item => item.ItemName, StringComparer.OrdinalIgnoreCase));

        Name = string.IsNullOrWhiteSpace(listName) ? "Manual Drop Hunt" : listName.Trim();
        activeIndex = 0;
        Enabled = items.Count > 0;
        StatusText = Enabled
            ? $"Generated {items.Count} drop target(s)."
            : "No droppable deficits found for this plan.";
    }

    public void Refresh()
    {
        for (var i = 0; i < items.Count; i++)
            items[i] = items[i].Refresh();

        if (!Enabled)
            return;

        AdvancePastComplete();
        StatusText = IsComplete
            ? "Drop hunt list complete."
            : ActiveItem is { } active
                ? $"Active drop target: {active.ItemName} x{active.Missing}"
                : "No active drop target.";
    }

    public void Stop()
    {
        Enabled = false;
        StatusText = "Drop hunt list stopped.";
    }

    public void SetActive(uint itemId)
    {
        var index = items.FindIndex(item => item.ItemId == itemId);
        if (index < 0)
            return;

        activeIndex = index;
        Enabled = true;
        Refresh();
    }

    public void Advance()
    {
        if (items.Count == 0)
            return;

        activeIndex = Math.Min(items.Count - 1, activeIndex + 1);
        Enabled = true;
        Refresh();
    }

    private void AdvancePastComplete()
    {
        while (activeIndex < items.Count && items[activeIndex].Complete)
            activeIndex++;

        if (activeIndex >= items.Count)
            activeIndex = Math.Max(0, items.Count - 1);
    }
}
