using System.Numerics;

namespace GBRMonsterHunter.Planning;

internal sealed record AetheryteRoute(
    uint TerritoryTypeId,
    uint AetheryteId,
    string AetheryteName,
    Vector3 Destination,
    float DistanceSquared);
