using System.Globalization;

namespace PokeStudio.Core.Models;

public sealed record CatalogEntry(string Key, string Name, string? Description = null);

/// <summary>Names and descriptions for everything a game refers to by key (moves, items, types…).</summary>
public sealed class GameCatalog
{
    public List<CatalogEntry> Types { get; } = [];
    public List<CatalogEntry> Abilities { get; } = [];
    public List<CatalogEntry> Items { get; } = [];
    public List<CatalogEntry> Moves { get; } = [];
    public List<CatalogEntry> GrowthRates { get; } = [];
    public List<CatalogEntry> EggGroups { get; } = [];
    public List<CatalogEntry> EvolutionMethods { get; } = [];
    public List<CatalogEntry> EvolutionConditions { get; } = [];
    public List<CatalogEntry> MoveEffects { get; } = [];
    public List<CatalogEntry> MoveTargets { get; } = [];
    public List<CatalogEntry> TrainerClasses { get; } = [];
    public List<CatalogEntry> TrainerPics { get; } = [];
    public List<CatalogEntry> TrainerMusic { get; } = [];
    public List<CatalogEntry> Natures { get; } = [];

    /// <summary>Enemy AI flags with plain-English descriptions; combined presets have Description starting with "Preset:".</summary>
    public List<CatalogEntry> AiFlags { get; } = [];

    /// <summary>Moves taught by TM/HM, in machine order (label like "TM01").</summary>
    public List<(string Label, string Move)> Machines { get; } = [];

    private Dictionary<string, string>? _names;

    /// <summary>Display name for any key, falling back to a tidied version of the key.</summary>
    public string NameOf(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return "None";
        }

        _names ??= Types.Concat(Abilities).Concat(Items).Concat(Moves).Concat(GrowthRates)
            .Concat(EggGroups).Concat(EvolutionMethods).Concat(EvolutionConditions).Concat(MoveEffects).Concat(MoveTargets)
            .Concat(TrainerClasses).Concat(TrainerPics).Concat(TrainerMusic).Concat(Natures).Concat(AiFlags)
            .GroupBy(e => e.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);
        return _names.TryGetValue(key, out var name) ? name : Prettify(key);
    }

    /// <summary>Call after changing entries so names are looked up again.</summary>
    public void Invalidate() => _names = null;

    /// <summary>Keeps pickers and lists in step after a move is renamed.</summary>
    public void RenameMove(string key, string name)
    {
        var i = Moves.FindIndex(m => m.Key == key);
        if (i >= 0 && Moves[i].Name != name)
        {
            Moves[i] = Moves[i] with { Name = name };
            Invalidate();
        }
    }

    private static readonly string[] KnownPrefixes =
        ["SPECIES_", "MOVE_", "EFFECT_", "TARGET_", "TRAINER_CLASS_", "TRAINER_PIC_", "TRAINER_ENCOUNTER_MUSIC_", "AI_FLAG_", "NATURE_", "TRAINER_", "ITEM_", "ABILITY_", "TYPE_", "GROWTH_", "EGG_GROUP_", "EVO_", "IF_", "TIME_", "MAPSEC_", "NATURE_", "WEATHER_", "REGION_"];

    /// <summary>"MOVE_SOLAR_BEAM" → "Solar Beam".</summary>
    public static string Prettify(string key)
    {
        var s = key;
        foreach (var p in KnownPrefixes)
        {
            if (s.StartsWith(p, StringComparison.Ordinal))
            {
                s = s[p.Length..];
                break;
            }
        }

        var words = s.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(w.ToLowerInvariant()));
        return string.Join(' ', words);
    }
}
