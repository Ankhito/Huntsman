using System.Numerics;

namespace Huntsman.Planning;

internal sealed record MonsterLocation(
    string MobName,
    uint TerritoryTypeId,
    uint MapRowId,
    float MapX,
    float MapY,
    uint? BNpcNameId = null,
    Vector3? WorldPosition = null)
{
    public bool HasMapCoordinates => TerritoryTypeId != 0 && MapRowId != 0 && MapX > 0 && MapY > 0;
}
