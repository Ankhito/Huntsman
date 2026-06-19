using FFXIVClientStructs.FFXIV.Client.Game;

namespace Huntsman.Planning;

internal static class InventoryCounter
{
    public static unsafe int Count(uint itemId, bool includeHq = true)
    {
        try
        {
            var inventory = InventoryManager.Instance();
            if (inventory == null)
                return 0;

            var nq = (int)inventory->GetInventoryItemCount(itemId, false, false, false);
            if (!includeHq)
                return nq;

            return nq + (int)inventory->GetInventoryItemCount(itemId, true, false, false);
        }
        catch
        {
            return 0;
        }
    }
}
