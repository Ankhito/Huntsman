using System.Numerics;
using System.Text.Json;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using LuminaSupplemental.Excel.Model;
using LuminaSupplemental.Excel.Services;

namespace Huntsman.Planning;

internal sealed class DropLocationProvider(PluginServices services)
{
    private const float ClusterMergeDistance = 0.7f;
    private const float ClusterMergeDistanceSquared = ClusterMergeDistance * ClusterMergeDistance;
    private const string UnknownZoneName = "Unknown zone";

    private Dictionary<uint, DropItemInfo>? dropsByItemId;
    private HashSet<uint>? knownDropItemIds;
    private IReadOnlyList<DroppableItemOption>? droppableItemOptions;
    private string? lastDroppableIndexError;
    private bool localBuildSucceeded;
    private string? lastLocalBuildError;

    private sealed record MobDropOverrides
    {
        public List<MobDropOverrideDrop> AddedDrops { get; init; } = [];
        public List<MobDropOverrideDrop> RemovedDrops { get; init; } = [];
        public List<MobDropOverrideSpawn> Spawns { get; init; } = [];
    }

    private sealed record MobDropOverrideDrop
    {
        public uint ItemId { get; init; }
        public uint BNpcNameId { get; init; }
    }

    private sealed record MobDropOverrideSpawn
    {
        public uint BNpcNameId { get; init; }
        public uint TerritoryTypeId { get; init; }
        public float MapX { get; init; }
        public float MapY { get; init; }
    }

    private readonly record struct MobDropLinkKey(uint ItemId, uint BNpcNameId);
    private readonly record struct MobDropSpawnPoint(uint TerritoryTypeId, Vector3 Position);

    public bool LocalDataAvailable
    {
        get
        {
            EnsureBuilt();
            return localBuildSucceeded;
        }
    }

    public int KnownDropItemCount
    {
        get
        {
            EnsureBuilt();
            return knownDropItemIds?.Count ?? 0;
        }
    }

    public string? LastLocalBuildError
    {
        get
        {
            EnsureBuilt();
            return lastLocalBuildError;
        }
    }

    public int SearchableDropItemCount => GetDroppableItems().Count;
    public string? LastDroppableIndexError => lastDroppableIndexError;

    public IReadOnlyList<DroppableItemOption> GetDroppableItems()
    {
        EnsureBuilt();
        if (droppableItemOptions != null)
            return droppableItemOptions;

        try
        {
            var items = services.Data.GetExcelSheet<Item>();
            droppableItemOptions = knownDropItemIds!
                .Where(itemId => itemId != 0 && items.TryGetRow(itemId, out _))
                .Select(itemId =>
                {
                    var item = items.GetRow(itemId);
                    var name = item.Name.ExtractText();
                    return string.IsNullOrWhiteSpace(name)
                        ? null
                        : BuildDroppableItemOption(itemId, name);
                })
                .OfType<DroppableItemOption>()
                .OrderBy(option => option.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(option => option.ItemId)
                .ToList();
            lastDroppableIndexError = null;
        }
        catch (Exception ex)
        {
            lastDroppableIndexError = ex.GetBaseException().Message;
            droppableItemOptions = [];
        }

        return droppableItemOptions;
    }

    public DropItemInfo GetDropInfo(uint itemId)
    {
        EnsureBuilt();
        return dropsByItemId!.TryGetValue(itemId, out var info) && info.Mobs.Count > 0
            ? info
            : DropItemInfo.Empty;
    }

    public bool IsKnownDrop(uint itemId)
    {
        EnsureBuilt();
        return knownDropItemIds!.Contains(itemId);
    }

    private void EnsureBuilt()
    {
        if (dropsByItemId != null)
            return;

        try
        {
            dropsByItemId = Build();
            droppableItemOptions = null;
            localBuildSucceeded = true;
            lastLocalBuildError = null;
        }
        catch (Exception ex)
        {
            lastLocalBuildError = ex.GetBaseException().Message;
            localBuildSucceeded = false;
            services.Log.Warning(ex, "Failed to build local LuminaSupplemental drop location cache.");
            dropsByItemId = [];
            knownDropItemIds = [];
            droppableItemOptions = [];
        }
    }

    private DroppableItemOption BuildDroppableItemOption(uint itemId, string name)
    {
        var info = dropsByItemId!.TryGetValue(itemId, out var foundInfo) ? foundInfo : DropItemInfo.Empty;
        var zoneCount = info.Mobs.Sum(mob => mob.Zones.Count);
        var clusterCount = info.Mobs.Sum(mob => mob.Zones.Sum(zone => zone.Clusters.Count));
        var hasRouteData = info.Mobs
            .SelectMany(mob => mob.Zones)
            .SelectMany(zone => zone.Clusters)
            .Any(cluster => cluster.HasCoordinates);

        return new DroppableItemOption(itemId, name, info.Mobs.Count, zoneCount, clusterCount, hasRouteData);
    }

    private Dictionary<uint, DropItemInfo> Build()
    {
        var gameData = services.Data.GameData;
        var language = gameData.Options.DefaultExcelLanguage;
        var mobDrops = CsvLoader.LoadResource<MobDrop>(CsvLoader.MobDropResourceName, true, out _, out _, gameData, language) ?? [];
        var mobSpawns = CsvLoader.LoadResource<MobSpawnPosition>(CsvLoader.MobSpawnResourceName, true, out _, out _, gameData, language) ?? [];
        var overrides = LoadOverrides();

        var bNpcNames = services.Data.GetExcelSheet<BNpcName>();
        var territories = services.Data.GetExcelSheet<TerritoryType>();
        var maps = services.Data.GetExcelSheet<Map>();
        var spawnsByMob = BuildSpawnIndex(mobSpawns, overrides.Spawns);
        var removedDrops = overrides.RemovedDrops
            .Where(drop => drop.ItemId != 0 && drop.BNpcNameId != 0)
            .Select(drop => new MobDropLinkKey(drop.ItemId, drop.BNpcNameId))
            .ToHashSet();

        var byItemAndMob = mobDrops
            .Where(drop => drop.ItemId != 0 && drop.BNpcNameId != 0)
            .Select(drop => new MobDropLinkKey(drop.ItemId, drop.BNpcNameId))
            .Where(drop => !removedDrops.Contains(drop))
            .Concat(overrides.AddedDrops
                .Where(drop => drop.ItemId != 0 && drop.BNpcNameId != 0)
                .Select(drop => new MobDropLinkKey(drop.ItemId, drop.BNpcNameId)))
            .Distinct();

        var mutable = new Dictionary<uint, List<DropMobInfo>>();
        knownDropItemIds = [];
        foreach (var drop in byItemAndMob)
        {
            knownDropItemIds.Add(drop.ItemId);
            if (!bNpcNames.TryGetRow(drop.BNpcNameId, out var bNpcName))
                continue;

            var mobName = bNpcName.Singular.ExtractText();
            if (string.IsNullOrWhiteSpace(mobName))
                continue;

            spawnsByMob.TryGetValue(drop.BNpcNameId, out var spawns);
            var zones = BuildZones(spawns, territories, maps);
            if (zones.Count == 0)
                continue;

            if (!mutable.TryGetValue(drop.ItemId, out var mobs))
                mutable[drop.ItemId] = mobs = [];
            mobs.Add(new DropMobInfo(drop.BNpcNameId, mobName, zones));
        }

        return mutable.ToDictionary(
            pair => pair.Key,
            pair => new DropItemInfo(pair.Value
                .OrderBy(mob => mob.MobName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(mob => mob.BNpcNameId)
                .ToList()));
    }

    private static Dictionary<uint, List<MobDropSpawnPoint>> BuildSpawnIndex(
        IReadOnlyList<MobSpawnPosition> mobSpawns,
        IReadOnlyList<MobDropOverrideSpawn> overrideSpawns)
    {
        var spawnsByMob = new Dictionary<uint, List<MobDropSpawnPoint>>();
        foreach (var spawn in mobSpawns)
            AddSpawn(spawnsByMob, spawn.BNpcNameId, spawn.TerritoryTypeId, spawn.Position);
        foreach (var spawn in overrideSpawns)
            AddSpawn(spawnsByMob, spawn.BNpcNameId, spawn.TerritoryTypeId, new Vector3(spawn.MapX, spawn.MapY, 0f));
        return spawnsByMob;
    }

    private static void AddSpawn(Dictionary<uint, List<MobDropSpawnPoint>> spawnsByMob, uint bNpcNameId, uint territoryTypeId, Vector3 position)
    {
        if (bNpcNameId == 0 || territoryTypeId == 0)
            return;

        if (!spawnsByMob.TryGetValue(bNpcNameId, out var spawns))
            spawnsByMob[bNpcNameId] = spawns = [];
        spawns.Add(new MobDropSpawnPoint(territoryTypeId, position));
    }

    private static List<DropZoneInfo> BuildZones(
        IReadOnlyList<MobDropSpawnPoint>? spawns,
        ExcelSheet<TerritoryType> territories,
        ExcelSheet<Map> maps)
    {
        if (spawns == null || spawns.Count == 0)
        {
            return
            [
                new DropZoneInfo(UnknownZoneName, 0,
                [
                    new DropClusterInfo(0, 0, 0f, 0f, 0),
                ]),
            ];
        }

        var zones = new List<DropZoneInfo>();
        foreach (var group in spawns.GroupBy(spawn => spawn.TerritoryTypeId))
        {
            if (!territories.TryGetRow(group.Key, out var territory))
                continue;
            if (territory.ContentFinderCondition.RowId != 0 || territory.QuestBattle.RowId != 0)
                continue;

            var map = territory.Map.RowId != 0 && maps.TryGetRow(territory.Map.RowId, out var foundMap)
                ? foundMap
                : (Map?)null;
            var zoneName = territory.PlaceName.ValueNullable?.Name.ExtractText() ?? $"Territory {group.Key}";
            var clusters = BuildClusters(group.Select(spawn => spawn.Position).ToList(), group.Key, map);

            if (clusters.Count > 0)
                zones.Add(new DropZoneInfo(zoneName, group.Key, clusters));
        }

        return zones
            .OrderBy(zone => zone.ZoneName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<DropClusterInfo> BuildClusters(IReadOnlyList<Vector3> positions, uint territoryTypeId, Map? map)
    {
        var normalizedPositions = positions
            .Select(position => NormalizePosition(position, map))
            .Where(position => position.HasValue)
            .Select(position => position!.Value)
            .ToList();
        var clusteredPoints = ClusterPositions(normalizedPositions);
        var clusters = new List<DropClusterInfo>(clusteredPoints.Count);

        foreach (var points in clusteredPoints)
        {
            var representative = GetRepresentativePoint(points);
            clusters.Add(new DropClusterInfo(territoryTypeId, map?.RowId ?? 0, representative.X, representative.Y, points.Count));
        }

        if (clusters.Count == 0)
            clusters.Add(new DropClusterInfo(territoryTypeId, map?.RowId ?? 0, 0f, 0f, positions.Count));
        return clusters;
    }

    private static Vector2? NormalizePosition(Vector3 position, Map? map)
    {
        if (IsMapCoordinate(position.X) && IsMapCoordinate(position.Y))
            return new Vector2(position.X, position.Y);

        if (!map.HasValue)
            return null;

        var mapX = ConvertWorldCoordToMapCoord(position.X, map.Value.SizeFactor, map.Value.OffsetX);
        var mapY = ConvertWorldCoordToMapCoord(position.Z, map.Value.SizeFactor, map.Value.OffsetY);
        return IsMapCoordinate(mapX) && IsMapCoordinate(mapY)
            ? new Vector2(mapX, mapY)
            : null;
    }

    private static List<List<Vector2>> ClusterPositions(IReadOnlyList<Vector2> positions)
    {
        var clusters = new List<List<Vector2>>();
        var centroids = new List<Vector2>();
        foreach (var position in positions.OrderBy(position => position.X).ThenBy(position => position.Y))
        {
            var bestIndex = -1;
            var bestDistanceSquared = float.PositiveInfinity;
            for (var i = 0; i < centroids.Count; i++)
            {
                var distanceSquared = Vector2.DistanceSquared(centroids[i], position);
                if (distanceSquared > ClusterMergeDistanceSquared || distanceSquared >= bestDistanceSquared)
                    continue;

                bestIndex = i;
                bestDistanceSquared = distanceSquared;
            }

            if (bestIndex < 0)
            {
                clusters.Add([position]);
                centroids.Add(position);
                continue;
            }

            clusters[bestIndex].Add(position);
            centroids[bestIndex] = AveragePosition(clusters[bestIndex]);
        }

        return clusters
            .Select((points, index) => (Points: points, Centroid: centroids[index]))
            .OrderByDescending(cluster => cluster.Points.Count)
            .ThenBy(cluster => cluster.Centroid.X)
            .ThenBy(cluster => cluster.Centroid.Y)
            .Select(cluster => cluster.Points)
            .ToList();
    }

    private static Vector2 AveragePosition(IReadOnlyList<Vector2> points)
    {
        if (points.Count == 0)
            return Vector2.Zero;

        var total = Vector2.Zero;
        for (var i = 0; i < points.Count; i++)
            total += points[i];
        return total / points.Count;
    }

    private static Vector2 GetRepresentativePoint(IReadOnlyList<Vector2> points)
    {
        if (points.Count == 0)
            return Vector2.Zero;
        if (points.Count == 1)
            return points[0];

        var bestPoint = points[0];
        var bestScore = float.PositiveInfinity;
        for (var i = 0; i < points.Count; i++)
        {
            var score = 0f;
            for (var j = 0; j < points.Count; j++)
                score += Vector2.DistanceSquared(points[i], points[j]);
            if (score >= bestScore)
                continue;

            bestScore = score;
            bestPoint = points[i];
        }

        return bestPoint;
    }

    private static bool IsMapCoordinate(float value) => value is > 0f and < 50f;

    private static float ConvertWorldCoordToMapCoord(float worldCoord, uint sizeFactor, int offset)
    {
        const double factor = 0.019999999552965164d;
        return sizeFactor == 0
            ? 0f
            : (float)((factor * offset) + (2048.0d / sizeFactor) + (factor * worldCoord) + 1.0d);
    }

    private static MobDropOverrides LoadOverrides()
    {
        const string json = """
        {
          "removedDrops": [
            {
              "itemId": 44072,
              "bNpcNameId": 281
            }
          ],
          "addedDrops": [
            {
              "itemId": 5345,
              "bNpcNameId": 480
            },
            {
              "itemId": 5345,
              "bNpcNameId": 1753
            }
          ],
          "spawns": [
            { "bNpcNameId": 480, "territoryTypeId": 153, "mapX": 26.1, "mapY": 20.1 },
            { "bNpcNameId": 1753, "territoryTypeId": 155, "mapX": 25.621801, "mapY": 25.663221 },
            { "bNpcNameId": 12930, "territoryTypeId": 1188, "mapX": 9.0, "mapY": 10.0 },
            { "bNpcNameId": 12939, "territoryTypeId": 1188, "mapX": 30.1, "mapY": 16.0 },
            { "bNpcNameId": 12943, "territoryTypeId": 1188, "mapX": 11.5, "mapY": 22.9 },
            { "bNpcNameId": 12944, "territoryTypeId": 1188, "mapX": 19.7, "mapY": 29.8 },
            { "bNpcNameId": 12953, "territoryTypeId": 1189, "mapX": 21.5, "mapY": 12.3 },
            { "bNpcNameId": 12956, "territoryTypeId": 1189, "mapX": 13.0, "mapY": 10.0 },
            { "bNpcNameId": 12964, "territoryTypeId": 1189, "mapX": 17.2, "mapY": 25.2 },
            { "bNpcNameId": 12972, "territoryTypeId": 1190, "mapX": 30.6, "mapY": 34.5 },
            { "bNpcNameId": 12973, "territoryTypeId": 1190, "mapX": 23.6, "mapY": 34.2 },
            { "bNpcNameId": 12974, "territoryTypeId": 1190, "mapX": 17.3, "mapY": 9.8 },
            { "bNpcNameId": 12991, "territoryTypeId": 1190, "mapX": 21.8, "mapY": 10.2 },
            { "bNpcNameId": 13079, "territoryTypeId": 1187, "mapX": 32.0, "mapY": 14.9 },
            { "bNpcNameId": 13080, "territoryTypeId": 1187, "mapX": 18.0, "mapY": 12.7 },
            { "bNpcNameId": 13082, "territoryTypeId": 1187, "mapX": 27.5, "mapY": 15.0 },
            { "bNpcNameId": 13101, "territoryTypeId": 1191, "mapX": 32.5, "mapY": 32.2 },
            { "bNpcNameId": 13104, "territoryTypeId": 1191, "mapX": 15.0, "mapY": 15.0 },
            { "bNpcNameId": 13131, "territoryTypeId": 1192, "mapX": 36.4, "mapY": 16.9 },
            { "bNpcNameId": 13239, "territoryTypeId": 1189, "mapX": 17.2, "mapY": 25.2 },
            { "bNpcNameId": 13548, "territoryTypeId": 1190, "mapX": 21.8, "mapY": 10.2 },
            { "bNpcNameId": 13588, "territoryTypeId": 1190, "mapX": 21.8, "mapY": 10.2 }
          ]
        }
        """;

        return JsonSerializer.Deserialize<MobDropOverrides>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new MobDropOverrides();
    }
}
