namespace GBRMonsterHunter.Planning;

internal sealed record DropHuntListItem(
    uint ItemId,
    string ItemName,
    int Needed,
    int Owned,
    DropItemInfo DropInfo,
    uint? RecipeId = null)
{
    public int Missing => Math.Max(0, Needed - Owned);
    public bool Complete => Missing <= 0;
    public bool HasRoute => GetBestLocation() != null;

    public DropHuntListItem Refresh() => this with { Owned = InventoryCounter.Count(ItemId) };

    public MonsterLocation? GetBestLocation()
    {
        foreach (var mob in DropInfo.Mobs)
        foreach (var zone in mob.Zones)
        foreach (var cluster in zone.Clusters)
        {
            if (!cluster.HasCoordinates)
                continue;

            return new MonsterLocation(
                mob.MobName,
                cluster.TerritoryTypeId,
                cluster.MapRowId,
                cluster.MapX,
                cluster.MapY,
                mob.BNpcNameId);
        }

        return null;
    }
}
