using System.Reflection;

namespace GBRMonsterHunter.IPC;

internal sealed record VulcanExecutionPlanSnapshot(
    int ListId,
    string ListName,
    int Version,
    IReadOnlyDictionary<uint, int> Materials)
{
    public string Signature
    {
        get
        {
            var materialHash = string.Join(';', Materials.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value}"));
            return $"{ListId}:{Version}:{materialHash}";
        }
    }
}

internal sealed record VulcanQueueSnapshot(
    string State,
    bool WaitingForGatherComplete,
    bool IsGatheringComplete,
    bool Paused,
    string PauseReason)
{
    public bool IsWaitingForGather => string.Equals(State, "WaitingForGather", StringComparison.Ordinal);
    public bool IsPostGather => string.Equals(State, "WaitingForJobSwitch", StringComparison.Ordinal)
        || string.Equals(State, "ReadyForCraft", StringComparison.Ordinal)
        || string.Equals(State, "Crafting", StringComparison.Ordinal);
}

internal sealed class VulcanReflectionAdapter(PluginServices services)
{
    private Type? bridgeType;
    private MethodInfo? getActiveExecutionPlan;
    private MethodInfo? isGatheringComplete;
    private PropertyInfo? waitingForGatherComplete;
    private FieldInfo? queueProcessorField;
    private string? lastError;

    public bool Available { get; private set; }
    public string? LastError => lastError;

    public VulcanExecutionPlanSnapshot? GetActiveExecutionPlan()
    {
        if (!EnsureBindings())
            return null;

        try
        {
            var plan = getActiveExecutionPlan!.Invoke(null, null);
            return plan == null ? null : BuildSnapshot(plan);
        }
        catch (Exception ex)
        {
            Available = false;
            lastError = $"Failed to read Vulcan execution plan: {ex.GetBaseException().Message}";
            services.Log.Warning(ex, "Failed to read Vulcan execution plan through reflection.");
            return null;
        }
    }

    public bool PauseQueue(string reason)
    {
        var queueProcessor = GetQueueProcessor();
        if (queueProcessor == null)
            return false;

        try
        {
            queueProcessor.GetType().GetMethod("Pause", [typeof(string)])?.Invoke(queueProcessor, [reason]);
            return true;
        }
        catch (Exception ex)
        {
            lastError = $"Failed to pause Vulcan queue: {ex.GetBaseException().Message}";
            services.Log.Warning(ex, "Failed to pause Vulcan queue through reflection.");
            return false;
        }
    }

    public bool ResumeQueue()
    {
        var queueProcessor = GetQueueProcessor();
        if (queueProcessor == null)
            return false;

        try
        {
            queueProcessor.GetType().GetMethod("Resume", Type.EmptyTypes)?.Invoke(queueProcessor, null);
            return true;
        }
        catch (Exception ex)
        {
            lastError = $"Failed to resume Vulcan queue: {ex.GetBaseException().Message}";
            services.Log.Warning(ex, "Failed to resume Vulcan queue through reflection.");
            return false;
        }
    }

    public VulcanQueueSnapshot? GetQueueSnapshot()
    {
        if (!EnsureBindings())
            return null;

        try
        {
            var queueProcessor = queueProcessorField!.GetValue(null);
            var state = queueProcessor?.GetType().GetProperty("CurrentState")?.GetValue(queueProcessor)?.ToString() ?? "None";
            var paused = queueProcessor?.GetType().GetProperty("Paused")?.GetValue(queueProcessor) as bool? ?? false;
            var pauseReason = queueProcessor?.GetType().GetProperty("PauseReason")?.GetValue(queueProcessor) as string ?? string.Empty;
            var waiting = waitingForGatherComplete?.GetValue(null) as bool? ?? false;
            var gatheringComplete = isGatheringComplete?.Invoke(null, null) as bool? ?? false;
            return new VulcanQueueSnapshot(state, waiting, gatheringComplete, paused, pauseReason);
        }
        catch (Exception ex)
        {
            lastError = $"Failed to read Vulcan queue state: {ex.GetBaseException().Message}";
            services.Log.Warning(ex, "Failed to read Vulcan queue state through reflection.");
            return null;
        }
    }

    private object? GetQueueProcessor()
    {
        if (!EnsureBindings())
            return null;

        try
        {
            return queueProcessorField!.GetValue(null);
        }
        catch (Exception ex)
        {
            lastError = $"Failed to access Vulcan queue processor: {ex.GetBaseException().Message}";
            services.Log.Warning(ex, "Failed to access Vulcan queue processor through reflection.");
            return null;
        }
    }

    private bool EnsureBindings()
    {
        if (bridgeType != null)
            return true;

        var assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(assembly => string.Equals(assembly.GetName().Name, "GatherBuddyReborn", StringComparison.OrdinalIgnoreCase))
            ?? AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly => assembly.GetType("GatherBuddy.Crafting.CraftingGatherBridge") != null);
        if (assembly == null)
        {
            Available = false;
            lastError = "GatherBuddyReborn assembly not loaded.";
            return false;
        }

        bridgeType = assembly.GetType("GatherBuddy.Crafting.CraftingGatherBridge");
        getActiveExecutionPlan = bridgeType?.GetMethod("GetActiveExecutionPlan", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes);
        isGatheringComplete = bridgeType?.GetMethod("IsGatheringComplete", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes);
        waitingForGatherComplete = bridgeType?.GetProperty("WaitingForGatherComplete", BindingFlags.Public | BindingFlags.Static);
        queueProcessorField = bridgeType?.GetField("_queueProcessor", BindingFlags.NonPublic | BindingFlags.Static);

        Available = bridgeType != null
            && getActiveExecutionPlan != null
            && isGatheringComplete != null
            && waitingForGatherComplete != null
            && queueProcessorField != null;
        lastError = Available ? null : "GatherBuddy Vulcan internals were not found.";
        return Available;
    }

    private static VulcanExecutionPlanSnapshot BuildSnapshot(object plan)
    {
        var type = plan.GetType();
        var listId = (int)(type.GetProperty("ListId")?.GetValue(plan) ?? -1);
        var listName = (string?)type.GetProperty("ListName")?.GetValue(plan) ?? "Vulcan Plan";
        var version = (int)(type.GetProperty("Version")?.GetValue(plan) ?? 0);
        var materials = new Dictionary<uint, int>();

        AddMaterialCounts(type.GetProperty("MaterialsView")?.GetValue(plan), materials);
        AddMaterialCounts(type.GetProperty("IngredientDemandsView")?.GetValue(plan), materials);

        return new VulcanExecutionPlanSnapshot(listId, listName, version, materials);
    }

    private static void AddMaterialCounts(object? materialsObject, IDictionary<uint, int> materials)
    {
        if (materialsObject is not System.Collections.IEnumerable enumerable)
            return;

        foreach (var entry in enumerable)
        {
            if (!TryReadMaterialEntry(entry, out var itemId, out var quantity) || quantity <= 0)
                continue;

            materials[itemId] = Math.Max(materials.TryGetValue(itemId, out var current) ? current : 0, quantity);
        }
    }

    private static bool TryReadMaterialEntry(object entry, out uint itemId, out int quantity)
    {
        itemId = 0;
        quantity = 0;
        var entryType = entry.GetType();
        var key = entryType.GetProperty("Key")?.GetValue(entry);
        var value = entryType.GetProperty("Value")?.GetValue(entry);
        if (TryReadItemId(key, out itemId))
        {
            if (TryReadQuantity(value, out quantity))
                return true;

            quantity = ReadBestQuantityFromObject(value);
            return quantity > 0;
        }

        itemId = ReadBestItemIdFromObject(entry);
        quantity = ReadBestQuantityFromObject(entry);
        return itemId != 0 && quantity > 0;
    }

    private static uint ReadBestItemIdFromObject(object? value)
    {
        if (value == null)
            return 0;

        foreach (var name in new[] { "ItemId", "ItemID", "Item", "Material", "Ingredient" })
        {
            var propertyValue = value.GetType().GetProperty(name)?.GetValue(value);
            if (TryReadItemId(propertyValue, out var itemId))
                return itemId;
        }

        return 0;
    }

    private static int ReadBestQuantityFromObject(object? value)
    {
        if (value == null)
            return 0;

        foreach (var name in new[] { "Missing", "MissingQuantity", "Needed", "Required", "RequiredQuantity", "Total", "TotalNeeded", "Quantity", "Count", "Amount" })
        {
            var propertyValue = value.GetType().GetProperty(name)?.GetValue(value);
            if (TryReadQuantity(propertyValue, out var quantity) && quantity > 0)
                return quantity;
        }

        return 0;
    }

    private static bool TryReadItemId(object? value, out uint itemId)
    {
        itemId = value switch
        {
            uint typed => typed,
            int typed and >= 0 => (uint)typed,
            ushort typed => typed,
            short typed and >= 0 => (uint)typed,
            byte typed => typed,
            _ => 0,
        };
        if (itemId != 0)
            return true;

        var rowId = value?.GetType().GetProperty("RowId")?.GetValue(value);
        itemId = rowId switch
        {
            uint typed => typed,
            int typed and >= 0 => (uint)typed,
            _ => 0,
        };
        return itemId != 0;
    }

    private static bool TryReadQuantity(object? value, out int quantity)
    {
        quantity = value switch
        {
            int typed => typed,
            uint typed and <= int.MaxValue => (int)typed,
            short typed => typed,
            ushort typed => typed,
            byte typed => typed,
            _ => 0,
        };
        return quantity > 0;
    }
}
