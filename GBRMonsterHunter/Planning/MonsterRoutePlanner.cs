using System.Numerics;
using Lumina.Excel.Sheets;

namespace GBRMonsterHunter.Planning;

internal sealed class MonsterRoutePlanner(PluginServices services)
{
    public AetheryteRoute? ResolveRoute(MonsterLocation location)
    {
        return TryResolveRoute(location, out var route, out _) ? route : null;
    }

    public bool TryResolveRoute(MonsterLocation location, out AetheryteRoute? route, out string error)
    {
        if (!location.HasMapCoordinates)
        {
            route = null;
            error = $"invalid route coordinates: territory={location.TerritoryTypeId}, map={location.MapRowId}, X={location.MapX:F1}, Y={location.MapY:F1}";
            return false;
        }

        if (!IsLikelyMapCoordinate(location.MapX) || !IsLikelyMapCoordinate(location.MapY))
        {
            route = null;
            error = $"{location.MobName} has invalid map coordinates: X={location.MapX:F1}, Y={location.MapY:F1}.";
            return false;
        }

        var territory = services.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(location.TerritoryTypeId);
        var map = services.Data.GetExcelSheet<Map>().GetRowOrDefault(location.MapRowId)
            ?? territory?.Map.ValueNullable;
        if (territory == null || map == null)
        {
            route = null;
            error = territory == null
                ? $"{location.MobName} has unknown territory {location.TerritoryTypeId}."
                : $"{location.MobName} has unknown map {location.MapRowId}.";
            return false;
        }

        var destination = location.WorldPosition ?? ConvertMapToWorld(location.MapX, location.MapY, map.Value);
        if (!IsFiniteDestination(destination))
        {
            route = null;
            error = $"failed to convert map coordinates for {location.MobName}: X={location.MapX:F1}, Y={location.MapY:F1}.";
            return false;
        }

        var aetherytes = ResolveAetherytes(location.TerritoryTypeId, map.Value);
        var nearest = aetherytes
            .OrderBy(aetheryte => SquaredDistance(aetheryte.MapX, aetheryte.MapY, location.MapX * 100f, location.MapY * 100f))
            .ThenBy(aetheryte => aetheryte.Id)
            .FirstOrDefault();

        if (nearest == null)
        {
            route = null;
            error = $"No aetheryte route found for {location.MobName} in territory {location.TerritoryTypeId}.";
            return false;
        }

        route = new AetheryteRoute(
            location.TerritoryTypeId,
            nearest.Id,
            nearest.Name,
            destination,
            SquaredDistance(nearest.MapX, nearest.MapY, location.MapX * 100f, location.MapY * 100f));
        error = string.Empty;
        return true;
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

    private static bool IsLikelyMapCoordinate(float value) => value is > 0f and < 50f;

    private static bool IsFiniteDestination(Vector3 destination) =>
        float.IsFinite(destination.X) && float.IsFinite(destination.Y) && float.IsFinite(destination.Z);

    private static float SquaredDistance(float ax, float ay, float bx, float by)
    {
        var dx = ax - bx;
        var dy = ay - by;
        return (dx * dx) + (dy * dy);
    }

    private sealed record AetheryteMarker(uint Id, string Name, int MapX, int MapY);
}
