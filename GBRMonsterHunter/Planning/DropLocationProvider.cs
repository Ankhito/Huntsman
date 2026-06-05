using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using LuminaSupplemental.Excel.Model;
using LuminaSupplemental.Excel.Services;

namespace GBRMonsterHunter.Planning;

internal sealed class DropLocationProvider(PluginServices services)
{
    private const float ClusterMergeDistance = 0.7f;
    private const float ClusterMergeDistanceSquared = ClusterMergeDistance * ClusterMergeDistance;
    private const string UnknownZoneName = "Unknown zone";

    private Dictionary<uint, DropItemInfo>? dropsByItemId;
    private HashSet<uint>? knownDropItemIds;
    private Type? gatherBuddyMobDropInfoCacheType;
    private MethodInfo? gatherBuddyGetDropInfoForItem;
    private MethodInfo? gatherBuddyIsKnownDropItem;
    private MethodInfo? gatherBuddyEnsureInitializeStarted;

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

    public DropItemInfo GetDropInfo(uint itemId)
    {
        if (TryGetGatherBuddyDropInfo(itemId, out var gatherBuddyInfo) && gatherBuddyInfo.Mobs.Count > 0)
            return gatherBuddyInfo;

        EnsureBuilt();
        return dropsByItemId!.TryGetValue(itemId, out var info) ? info : DropItemInfo.Empty;
    }

    public bool IsKnownDrop(uint itemId)
    {
        if (TryIsGatherBuddyKnownDrop(itemId, out var known) && known)
            return true;

        EnsureBuilt();
        return knownDropItemIds!.Contains(itemId);
    }

    private bool TryIsGatherBuddyKnownDrop(uint itemId, out bool known)
    {
        known = false;
        if (!EnsureGatherBuddyMobDropBindings())
            return false;

        try
        {
            gatherBuddyEnsureInitializeStarted?.Invoke(null, null);
            known = gatherBuddyIsKnownDropItem?.Invoke(null, [itemId]) as bool? ?? false;
            return true;
        }
        catch (Exception ex)
        {
            services.Log.Debug($"Failed to read GatherBuddy mob-drop known state: {ex.GetBaseException().Message}");
            return false;
        }
    }

    private bool TryGetGatherBuddyDropInfo(uint itemId, out DropItemInfo info)
    {
        info = DropItemInfo.Empty;
        if (!EnsureGatherBuddyMobDropBindings())
            return false;

        try
        {
            gatherBuddyEnsureInitializeStarted?.Invoke(null, null);
            var dropInfo = gatherBuddyGetDropInfoForItem?.Invoke(null, [itemId]);
            if (dropInfo == null)
                return false;

            info = ConvertGatherBuddyDropInfo(dropInfo);
            return true;
        }
        catch (Exception ex)
        {
            services.Log.Debug($"Failed to read GatherBuddy mob-drop locations: {ex.GetBaseException().Message}");
            return false;
        }
    }

    private bool EnsureGatherBuddyMobDropBindings()
    {
        if (gatherBuddyMobDropInfoCacheType != null)
            return gatherBuddyGetDropInfoForItem != null;

        var assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(assembly => string.Equals(assembly.GetName().Name, "GatherBuddyReborn", StringComparison.OrdinalIgnoreCase))
            ?? AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly => assembly.GetType("GatherBuddy.Crafting.MobDropInfoCache") != null);
        gatherBuddyMobDropInfoCacheType = assembly?.GetType("GatherBuddy.Crafting.MobDropInfoCache");
        gatherBuddyGetDropInfoForItem = gatherBuddyMobDropInfoCacheType?.GetMethod("GetDropInfoForItem", BindingFlags.Public | BindingFlags.Static, [typeof(uint)]);
        gatherBuddyIsKnownDropItem = gatherBuddyMobDropInfoCacheType?.GetMethod("IsKnownDropItem", BindingFlags.Public | BindingFlags.Static, [typeof(uint)]);
        gatherBuddyEnsureInitializeStarted = gatherBuddyMobDropInfoCacheType?.GetMethod("EnsureInitializeStarted", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes);
        return gatherBuddyGetDropInfoForItem != null;
    }

    private static DropItemInfo ConvertGatherBuddyDropInfo(object dropInfo)
    {
        var mobsObject = dropInfo.GetType().GetProperty("Mobs")?.GetValue(dropInfo);
        if (mobsObject is not System.Collections.IEnumerable mobsEnumerable)
            return DropItemInfo.Empty;

        var mobs = new List<DropMobInfo>();
        foreach (var mobObject in mobsEnumerable)
        {
            var mobType = mobObject.GetType();
            var bNpcNameId = ReadUInt(mobType.GetProperty("BNpcNameId")?.GetValue(mobObject));
            var mobName = mobType.GetProperty("MobName")?.GetValue(mobObject) as string ?? string.Empty;
            var zonesObject = mobType.GetProperty("Zones")?.GetValue(mobObject);
            if (string.IsNullOrWhiteSpace(mobName) || zonesObject is not System.Collections.IEnumerable zonesEnumerable)
                continue;

            var zones = new List<DropZoneInfo>();
            foreach (var zoneObject in zonesEnumerable)
            {
                var zoneType = zoneObject.GetType();
                var zoneName = zoneType.GetProperty("ZoneName")?.GetValue(zoneObject) as string ?? UnknownZoneName;
                var territoryTypeId = ReadUInt(zoneType.GetProperty("TerritoryTypeId")?.GetValue(zoneObject));
                var clustersObject = zoneType.GetProperty("Clusters")?.GetValue(zoneObject);
                if (clustersObject is not System.Collections.IEnumerable clustersEnumerable)
                    continue;

                var clusters = new List<DropClusterInfo>();
                foreach (var clusterObject in clustersEnumerable)
                {
                    var clusterType = clusterObject.GetType();
                    clusters.Add(new DropClusterInfo(
                        ReadUInt(clusterType.GetProperty("TerritoryTypeId")?.GetValue(clusterObject)),
                        ReadUInt(clusterType.GetProperty("MapRowId")?.GetValue(clusterObject)),
                        ReadFloat(clusterType.GetProperty("MapX")?.GetValue(clusterObject)),
                        ReadFloat(clusterType.GetProperty("MapY")?.GetValue(clusterObject)),
                        ReadInt(clusterType.GetProperty("SpawnPointCount")?.GetValue(clusterObject))));
                }

                if (clusters.Count > 0)
                    zones.Add(new DropZoneInfo(zoneName, territoryTypeId, clusters));
            }

            if (zones.Count > 0)
                mobs.Add(new DropMobInfo(bNpcNameId, mobName, zones));
        }

        return mobs.Count == 0 ? DropItemInfo.Empty : new DropItemInfo(mobs);
    }

    private static uint ReadUInt(object? value) => value switch
    {
        uint typed => typed,
        int typed and >= 0 => (uint)typed,
        ushort typed => typed,
        short typed and >= 0 => (uint)typed,
        byte typed => typed,
        _ => 0,
    };

    private static int ReadInt(object? value) => value switch
    {
        int typed => typed,
        uint typed and <= int.MaxValue => (int)typed,
        ushort typed => typed,
        short typed => typed,
        byte typed => typed,
        _ => 0,
    };

    private static float ReadFloat(object? value) => value switch
    {
        float typed => typed,
        double typed => (float)typed,
        int typed => typed,
        uint typed => typed,
        _ => 0f,
    };

    private void EnsureBuilt()
    {
        if (dropsByItemId != null)
            return;

        try
        {
            dropsByItemId = Build();
        }
        catch (Exception ex)
        {
            services.Log.Warning(ex, "Failed to build drop location cache.");
            dropsByItemId = [];
            knownDropItemIds = [];
        }
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
