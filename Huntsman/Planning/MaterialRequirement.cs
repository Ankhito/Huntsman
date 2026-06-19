namespace Huntsman.Planning;

internal enum MaterialSourceKind
{
    Unknown,
    Inventory,
    Gatherable,
    Fish,
    Craftable,
    Drop,
    Vendor,
    Other,
}

internal sealed record MaterialRequirement(
    uint ItemId,
    string Name,
    int Needed,
    int Owned,
    MaterialSourceKind SourceKind,
    uint? RecipeId = null)
{
    public int Missing => Math.Max(0, Needed - Owned);
}
