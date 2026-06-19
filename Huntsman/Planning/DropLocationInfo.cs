namespace Huntsman.Planning;

internal sealed record DropClusterInfo(
    uint TerritoryTypeId,
    uint MapRowId,
    float MapX,
    float MapY,
    int SpawnPointCount)
{
    public bool HasCoordinates => TerritoryTypeId != 0 && MapRowId != 0 && MapX > 0f && MapY > 0f;
}

internal sealed record DropZoneInfo(string ZoneName, uint TerritoryTypeId, IReadOnlyList<DropClusterInfo> Clusters);

internal sealed record DropMobInfo(uint BNpcNameId, string MobName, IReadOnlyList<DropZoneInfo> Zones);

internal sealed record DropItemInfo(IReadOnlyList<DropMobInfo> Mobs)
{
    public static DropItemInfo Empty { get; } = new([]);
    public bool HasData => Mobs.Count > 0;
}

internal sealed record DroppableItemOption(
    uint ItemId,
    string Name,
    int MobCount,
    int ZoneCount,
    int ClusterCount,
    bool HasRouteData);
