namespace Huntsman.Planning;

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

    public MonsterLocation? GetBestLocation(uint currentTerritoryTypeId = 0) =>
        GetCandidateLocations(currentTerritoryTypeId).FirstOrDefault();

    public IReadOnlyList<MonsterLocation> GetCandidateLocations(uint currentTerritoryTypeId = 0)
    {
        var candidates = new List<(MonsterLocation Location, int SpawnPointCount, bool SameTerritory, string ZoneName)>();

        foreach (var mob in DropInfo.Mobs)
        foreach (var zone in mob.Zones)
        foreach (var cluster in zone.Clusters)
        {
            if (!cluster.HasCoordinates)
                continue;

            var location = new MonsterLocation(
                mob.MobName,
                cluster.TerritoryTypeId,
                cluster.MapRowId,
                cluster.MapX,
                cluster.MapY,
                mob.BNpcNameId);

            candidates.Add((
                location,
                cluster.SpawnPointCount,
                currentTerritoryTypeId != 0 && cluster.TerritoryTypeId == currentTerritoryTypeId,
                zone.ZoneName));
        }

        return candidates
            .OrderByDescending(candidate => candidate.SameTerritory)
            .ThenByDescending(candidate => candidate.SpawnPointCount)
            .ThenBy(candidate => candidate.ZoneName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Location.MobName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Location.MapX)
            .ThenBy(candidate => candidate.Location.MapY)
            .Select(candidate => candidate.Location)
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
    }
}
