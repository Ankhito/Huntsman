using Lumina.Excel.Sheets;

namespace Huntsman.Planning;

internal sealed class MaterialSourceClassifier(PluginServices services, DropLocationProvider dropLocations)
{
    private HashSet<uint>? gatherables;
    private HashSet<uint>? fish;
    private HashSet<uint>? craftables;
    private HashSet<uint>? vendorItems;
    private HashSet<uint>? fallbackDropItems;

    public MaterialSourceKind Classify(uint itemId)
    {
        EnsureInitialized();

        if (gatherables!.Contains(itemId))
            return MaterialSourceKind.Gatherable;

        if (fish!.Contains(itemId))
            return MaterialSourceKind.Fish;

        if (dropLocations.IsKnownDrop(itemId))
            return MaterialSourceKind.Drop;

        if (fallbackDropItems!.Contains(itemId))
            return MaterialSourceKind.Drop;

        if (craftables!.Contains(itemId))
            return MaterialSourceKind.Craftable;

        if (vendorItems!.Contains(itemId))
            return MaterialSourceKind.Vendor;

        return MaterialSourceKind.Other;
    }

    private void EnsureInitialized()
    {
        if (gatherables != null)
            return;

        gatherables = services.Data.GetExcelSheet<GatheringItem>()
            .Where(row => row.Item.RowId != 0)
            .Select(row => row.Item.RowId)
            .ToHashSet();

        fish = services.Data.GetExcelSheet<FishParameter>()
            .Where(row => row.Item.RowId != 0)
            .Select(row => row.Item.RowId)
            .ToHashSet();

        craftables = services.Data.GetExcelSheet<Recipe>()
            .Where(row => row.RowId != 0 && row.ItemResult.RowId != 0)
            .Select(row => row.ItemResult.RowId)
            .ToHashSet();

        vendorItems = services.Data.GetSubrowExcelSheet<GilShopItem>()
            .SelectMany(row => row)
            .Where(row => row.Item.RowId != 0)
            .Select(row => row.Item.RowId)
            .ToHashSet();

        fallbackDropItems = services.Data.GetExcelSheet<RetainerTaskNormal>()
            .Where(row => row.Item.RowId != 0 && row.GatheringLog.RowId == 0 && row.FishingLog.RowId == 0)
            .Select(row => row.Item.RowId)
            .ToHashSet();
    }
}
