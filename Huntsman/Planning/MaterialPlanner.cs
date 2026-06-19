using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace Huntsman.Planning;

internal sealed class MaterialPlanner(PluginServices services, DropLocationProvider dropLocations)
{
    private readonly MaterialSourceClassifier sourceClassifier = new(services, dropLocations);

    public IReadOnlyList<MaterialRequirement> Plan(string input)
    {
        var requests = ParseRequests(input);
        if (requests.Count == 0)
            return [];

        var items = services.Data.GetExcelSheet<Item>();
        var recipes = services.Data.GetExcelSheet<Recipe>();
        var itemByName = items
            .Where(item => item.RowId != 0 && !item.Name.IsEmpty)
            .GroupBy(item => Normalize(item.Name.ExtractText()))
            .ToDictionary(group => group.Key, group => group.First());

        var recipeByResult = recipes
            .Where(recipe => recipe.RowId != 0 && recipe.ItemResult.RowId != 0)
            .GroupBy(recipe => recipe.ItemResult.RowId)
            .ToDictionary(group => group.Key, group => group.First());

        var materialCounts = new Dictionary<uint, int>();
        foreach (var request in requests)
        {
            if (!itemByName.TryGetValue(Normalize(request.Name), out var item))
                continue;

            AddRecipeMaterials(item.RowId, request.Quantity, recipeByResult, materialCounts, depth: 0);
        }

        return ToRequirements(materialCounts, items, recipeByResult, sourceClassifier);
    }

    public IReadOnlyList<MaterialRequirement> PlanMaterialCounts(IReadOnlyDictionary<uint, int> materialCounts)
    {
        var items = services.Data.GetExcelSheet<Item>();
        var recipeByResult = services.Data.GetExcelSheet<Recipe>()
            .Where(recipe => recipe.RowId != 0 && recipe.ItemResult.RowId != 0)
            .GroupBy(recipe => recipe.ItemResult.RowId)
            .ToDictionary(group => group.Key, group => group.First());

        return ToRequirements(materialCounts, items, recipeByResult, sourceClassifier);
    }

    private static IReadOnlyList<MaterialRequirement> ToRequirements(
        IEnumerable<KeyValuePair<uint, int>> materialCounts,
        Lumina.Excel.ExcelSheet<Item> items,
        IReadOnlyDictionary<uint, Recipe> recipeByResult,
        MaterialSourceClassifier sourceClassifier)
    {
        return materialCounts
            .Select(pair =>
            {
                var item = items.GetRow(pair.Key);
                var name = item.Name.ExtractText();
                var owned = InventoryCounter.Count(pair.Key);
                var source = sourceClassifier.Classify(pair.Key);
                var recipeId = recipeByResult.TryGetValue(pair.Key, out var recipe) ? recipe.RowId : (uint?)null;
                return new MaterialRequirement(pair.Key, name, pair.Value, owned, source, recipeId);
            })
            .OrderByDescending(req => req.SourceKind == MaterialSourceKind.Drop)
            .ThenBy(req => req.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddRecipeMaterials(
        uint itemId,
        int quantity,
        IReadOnlyDictionary<uint, Recipe> recipeByResult,
        IDictionary<uint, int> output,
        int depth)
    {
        if (depth > 6 || !recipeByResult.TryGetValue(itemId, out var recipe))
        {
            output[itemId] = GetExisting(output, itemId) + quantity;
            return;
        }

        var resultAmount = Math.Max(1, (int)recipe.AmountResult);
        var craftsNeeded = (int)Math.Ceiling(quantity / (double)resultAmount);
        var hadIngredient = false;

        var ingredientSlots = Math.Min(recipe.Ingredient.Count, recipe.AmountIngredient.Count);
        for (var i = 0; i < ingredientSlots; i++)
        {
            var ingredient = recipe.Ingredient[i];
            var amount = (int)recipe.AmountIngredient[i];
            if (ingredient.RowId == 0 || amount <= 0)
                continue;

            hadIngredient = true;
            AddRecipeMaterials(ingredient.RowId, amount * craftsNeeded, recipeByResult, output, depth + 1);
        }

        if (!hadIngredient)
            output[itemId] = GetExisting(output, itemId) + quantity;
    }

    private static List<(string Name, int Quantity)> ParseRequests(string input)
    {
        var requests = new List<(string Name, int Quantity)>();
        foreach (var line in input.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('x', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && int.TryParse(parts[1], out var quantity))
                requests.Add((parts[0], Math.Max(1, quantity)));
            else
                requests.Add((line, 1));
        }

        return requests;
    }

    private static string Normalize(string value) => value.Trim().ToLowerInvariant();

    private static int GetExisting(IDictionary<uint, int> values, uint itemId) =>
        values.TryGetValue(itemId, out var current) ? current : 0;
}
