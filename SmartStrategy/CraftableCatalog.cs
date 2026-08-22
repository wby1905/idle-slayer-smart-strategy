using Il2Cpp;
using UnityEngine;

namespace SmartStrategy;

internal enum SmartItem
{
    RagePill,
    Whetstone,
    ShardsNecklace,
    SoulsCompass,
    CriticalSword,
    SoulsSpyglass,
    RandomStone,
    Specialization,
    KeyManifest,
    RandomBoxStaff,
    NecklaceOfCollectables,
    AlternateDimensionStaff,
    BidimensionalStaff,
    DeathwaveScepter
}

internal sealed class CraftableCatalog
{
    private static readonly Dictionary<SmartItem, string[]> Aliases = new()
    {
        [SmartItem.RagePill] = new[] { "craftableitemragepill", "ragepill" },
        [SmartItem.Whetstone] = new[] { "craftableitemwhetstone", "whetstone" },
        [SmartItem.ShardsNecklace] = new[] { "craftableitemshardsnecklace", "shardsnecklace" },
        [SmartItem.SoulsCompass] = new[] { "craftableitemsoulscompass", "soulscompass" },
        [SmartItem.CriticalSword] = new[] { "craftableitemcriticalsword", "criticalsword" },
        [SmartItem.SoulsSpyglass] = new[] { "craftableitemsoulsspyglass", "soulsspyglass" },
        [SmartItem.RandomStone] = new[] { "craftableitemrandomstone", "randomstone" },
        [SmartItem.Specialization] = new[] { "craftableitemspecialization", "specialization" },
        [SmartItem.KeyManifest] = new[] { "craftableitemkeymanifest", "keymanifest" },
        [SmartItem.RandomBoxStaff] = new[] { "craftableitemrandomboxstaff", "randomboxstaff" },
        [SmartItem.NecklaceOfCollectables] = new[] { "craftableitemnecklaceofcollectables", "necklaceofcollectables", "necklaceofcollectibles" },
        [SmartItem.AlternateDimensionStaff] = new[] { "craftableitemalternatedimensionstaff", "alternatedimensionstaff" },
        [SmartItem.BidimensionalStaff] = new[] { "craftableitembidimensionalstaff", "bidimensionalstaff" },
        [SmartItem.DeathwaveScepter] = new[] { "craftableitemdeathwavescepter", "deathwavescepter" }
    };

    private readonly Dictionary<SmartItem, TemporaryCraftableItem> _items = new();
    private readonly HashSet<SmartItem> _loggedMissing = new();
    private float _nextResolveTime;

    internal TemporaryCraftableItem Get(SmartItem kind)
    {
        if (_items.TryGetValue(kind, out var existing) && existing != null) return existing;
        // Cache unresolved items as negative results. Without the entry, every Get ignored the retry time and
        // rescanned all Unity Resources, reaching one full scan every 0.5 seconds during kill quests.
        if (Time.unscaledTime < _nextResolveTime) return null;
        ResolveAll();
        _items.TryGetValue(kind, out existing);
        return existing;
    }

    internal void ResolveAll(bool forceLog = false)
    {
        // Newly unlocked items do not need subsecond discovery; a one-minute retry balances latency and main-thread cost.
        _nextResolveTime = Time.unscaledTime + 60f;
        var found = Resources.FindObjectsOfTypeAll<TemporaryCraftableItem>();
        foreach (var item in found)
        {
            if (item == null) continue;
            var internalName = Normalize(item.name);
            var localizedName = Normalize(item.localizedName);
            foreach (var entry in Aliases)
            {
                if (_items.TryGetValue(entry.Key, out var current) && current != null) continue;
                if (!Matches(internalName, localizedName, entry.Value)) continue;
                _items[entry.Key] = item;
                _loggedMissing.Remove(entry.Key);
                SmartLog.Debug(SmartLogCategory.Material,
                    $"Resolved craftable {entry.Key}: internalName={item.name}.");
            }
        }

        if (!forceLog) return;
        foreach (var kind in Aliases.Keys)
        {
            if (_items.TryGetValue(kind, out var item) && item != null) continue;
            if (_loggedMissing.Add(kind))
                SmartLog.Debug(SmartLogCategory.Material, $"Craftable is unresolved or still locked: {kind}.");
        }
    }

    internal static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var chars = value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray();
        return new string(chars);
    }

    private static bool Matches(string internalName, string localizedName, IEnumerable<string> aliases)
    {
        foreach (var alias in aliases)
        {
            if (internalName.Contains(alias, StringComparison.Ordinal) || localizedName.Contains(alias, StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
