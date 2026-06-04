using System.Numerics;
using Lumina.Excel.Sheets;

namespace GBRMonsterHunter.Planning;

internal sealed class MonsterRoutePlanner(PluginServices services)
{
    public AetheryteRoute? ResolveRoute(MonsterLocation location)
    {
        if (!location.HasMapCoordinates)
            return null;

        var territory = services.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(location.TerritoryTypeId);
        var map = services.Data.GetExcelSheet<Map>().GetRowOrDefault(location.MapRowId)
            ?? territory?.Map.ValueNullable;
        if (territory == null || map == null)
            return null;

        var destination = location.WorldPosition ?? ConvertMapToWorld(location.MapX, location.MapY, map.Value);
        var aetherytes = ResolveAetherytes(location.TerritoryTypeId, map.Value);
        var nearest = aetherytes
            .OrderBy(aetheryte => SquaredDistance(aetheryte.MapX, aetheryte.MapY, location.MapX * 100f, location.MapY * 100f))
            .ThenBy(aetheryte => aetheryte.Id)
            .FirstOrDefault();

        return nearest == null
            ? null
            : new AetheryteRoute(
                location.TerritoryTypeId,
                nearest.Id,
                nearest.Name,
                destination,
                SquaredDistance(nearest.MapX, nearest.MapY, location.MapX * 100f, location.MapY * 100f));
    }

    private List<AetheryteMarker> ResolveAetherytes(uint territoryTypeId, Map map)
    {
        var markers = services.Data.GetSubrowExcelSheet<MapMarker>()
            .SelectMany(row => row)
            .Where(marker => marker.DataType == 3)
            .GroupBy(marker => marker.DataKey.RowId)
            .ToDictionary(group => group.Key, group => group.First());

        var scale = Math.Max(0.01f, map.SizeFactor / 100f);
        return services.Data.GetExcelSheet<Aetheryte>()
            .Where(aetheryte => aetheryte.IsAetheryte && aetheryte.Territory.RowId == territoryTypeId)
            .Select(aetheryte =>
            {
                if (!markers.TryGetValue(aetheryte.RowId, out var marker))
                    return null;

                return new AetheryteMarker(
                    aetheryte.RowId,
                    aetheryte.PlaceName.ValueNullable?.Name.ToString() ?? $"Aetheryte {aetheryte.RowId}",
                    MarkerToMap(marker.X, scale),
                    MarkerToMap(marker.Y, scale));
            })
            .Where(marker => marker != null)
            .Cast<AetheryteMarker>()
            .ToList();
    }

    private static Vector3 ConvertMapToWorld(float mapX, float mapY, Map map)
    {
        var worldX = ConvertMapCoordToWorldCoord(mapX, map.SizeFactor, map.OffsetX);
        var worldZ = ConvertMapCoordToWorldCoord(mapY, map.SizeFactor, map.OffsetY);
        return new Vector3(worldX, 0, worldZ);
    }

    private static float ConvertMapCoordToWorldCoord(float mapCoord, uint sizeFactor, int offset)
    {
        const double factor = 0.019999999552965164d;
        return sizeFactor == 0
            ? 0f
            : (float)((mapCoord - 1.0d - (2048.0d / sizeFactor) - (factor * offset)) / factor);
    }

    private static int MarkerToMap(double coord, double scale) => (int)(2 * coord / scale + 100.9);

    private static float SquaredDistance(float ax, float ay, float bx, float by)
    {
        var dx = ax - bx;
        var dy = ay - by;
        return (dx * dx) + (dy * dy);
    }

    private sealed record AetheryteMarker(uint Id, string Name, int MapX, int MapY);
}
