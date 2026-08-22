using Il2Cpp;

namespace SmartStrategy;

internal static class MaterialReservePolicy
{
    internal static double MinimumReserve(Drop material)
    {
        if (material == null || Plugin.Config?.RareMaterialReservesEnabled.Value != true) return 0;
        var key = CraftableCatalog.Normalize(material.name);
        if (string.IsNullOrEmpty(key)) key = CraftableCatalog.Normalize(material.localizedName);
        return key switch
        {
            var value when value.Contains("spiritboost", StringComparison.Ordinal) =>
                Math.Max(0, Plugin.Config.SpiritBoostMaterialReserve.Value),
            var value when value.Contains("simurghfeather", StringComparison.Ordinal) =>
                Math.Max(0, Plugin.Config.SimurghFeatherMaterialReserve.Value),
            var value when value.Contains("dragonscale", StringComparison.Ordinal) =>
                Math.Max(0, Plugin.Config.DragonScaleMaterialReserve.Value),
            var value when value.Contains("reptileeye", StringComparison.Ordinal) =>
                Math.Max(0, Plugin.Config.ReptileEyeMaterialReserve.Value),
            var value when value.Contains("scrap", StringComparison.Ordinal) =>
                Math.Max(0, Plugin.Config.ScrapMaterialReserve.Value),
            _ => 0
        };
    }

    internal static double RequiredStock(Drop material, double recipeStock, double plannedConsumption) =>
        Math.Max(Math.Max(0, recipeStock), MinimumReserve(material) + Math.Max(0, plannedConsumption));

    internal static bool CanCraftOnce(TemporaryCraftableItem item, out string reason)
    {
        reason = null;
        if (item == null) return false;
        try
        {
            var requirements = item.GetRequirements();
            if (requirements == null) return true;
            for (var i = 0; i < requirements.Length; i++)
            {
                var requirement = requirements[i];
                var material = requirement?.material;
                if (material == null || requirement.divinityPoints || requirement.amount <= 0) continue;
                var reserve = MinimumReserve(material);
                if (reserve <= 0) continue;
                var after = material.amount - requirement.amount;
                if (after + 0.0001 >= reserve) continue;
                reason = $"Crafting would leave rare material {DisplayName(material)} at {Math.Max(0, after):0}, below safety stock {reserve:0}";
                return false;
            }
            return true;
        }
        catch
        {
            reason = "Unable to read rare-material reserve state";
            return false;
        }
    }

    private static string DisplayName(Drop material) => string.IsNullOrWhiteSpace(material?.localizedName)
        ? material?.name ?? "Unknown Material" : material.localizedName;
}
