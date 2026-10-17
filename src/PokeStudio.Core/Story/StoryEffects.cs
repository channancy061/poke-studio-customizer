using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;

namespace PokeStudio.Core.Story;

/// <summary>
/// The lasting field effects a battle can start with, and a challenger's reward TM. A type's effect makes the battle
/// harder in its own way (a Fire leader's sun weakens Water moves); the first one the game has is used.
/// </summary>
public static class StoryEffects
{
    private const string P = "STARTING_STATUS_";

    private static readonly Dictionary<string, string[]> ByType = new(StringComparer.Ordinal)
    {
        ["TYPE_FIRE"] = [P + "WEATHER_SUN", P + "SEA_OF_FIRE_PLAYER"],
        ["TYPE_WATER"] = [P + "WEATHER_RAIN", P + "SWAMP_PLAYER"],
        ["TYPE_GRASS"] = [P + "WEATHER_RAIN", P + "GRASSY_TERRAIN"],
        ["TYPE_ROCK"] = [P + "WEATHER_SANDSTORM", P + "STEALTH_ROCK_PLAYER"],
        ["TYPE_GROUND"] = [P + "WEATHER_SANDSTORM", P + "SPIKES_PLAYER_L1"],
        ["TYPE_STEEL"] = [P + "WEATHER_SANDSTORM", P + "SHARP_STEEL_PLAYER"],
        ["TYPE_ICE"] = [P + "WEATHER_SNOW", P + "WEATHER_HAIL", P + "SPIKES_PLAYER_L1"],
        ["TYPE_ELECTRIC"] = [P + "ELECTRIC_TERRAIN"],
        ["TYPE_PSYCHIC"] = [P + "PSYCHIC_TERRAIN"],
        ["TYPE_FAIRY"] = [P + "MISTY_TERRAIN"],
        ["TYPE_FLYING"] = [P + "TAILWIND_OPPONENT"],
        ["TYPE_DRAGON"] = [P + "TAILWIND_OPPONENT"],
        ["TYPE_POISON"] = [P + "TOXIC_SPIKES_PLAYER_L1"],
        ["TYPE_BUG"] = [P + "STICKY_WEB_PLAYER"],
        ["TYPE_GHOST"] = [P + "TRICK_ROOM"],
    };

    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        [P + "WEATHER_SUN"] = "Harsh sun (Fire moves stronger, Water moves weaker)",
        [P + "WEATHER_RAIN"] = "Rain (Water moves stronger, Fire moves weaker)",
        [P + "WEATHER_SANDSTORM"] = "Sandstorm (hurts all but Rock, Ground and Steel types)",
        [P + "WEATHER_HAIL"] = "Hail (hurts all but Ice types)",
        [P + "WEATHER_SNOW"] = "Snow (Ice types get more Defense)",
        [P + "WEATHER_FOG"] = "Fog",
        [P + "ELECTRIC_TERRAIN"] = "Electric Terrain",
        [P + "MISTY_TERRAIN"] = "Misty Terrain",
        [P + "GRASSY_TERRAIN"] = "Grassy Terrain",
        [P + "PSYCHIC_TERRAIN"] = "Psychic Terrain",
        [P + "TRICK_ROOM"] = "Trick Room (slower Pokémon move first)",
        [P + "MAGIC_ROOM"] = "Magic Room (held items do nothing)",
        [P + "WONDER_ROOM"] = "Wonder Room (Defense and Sp. Def swap)",
        [P + "TAILWIND_OPPONENT"] = "Tailwind on their side (their Speed doubled)",
        [P + "TAILWIND_PLAYER"] = "Tailwind on your side",
        [P + "SEA_OF_FIRE_PLAYER"] = "Sea of fire on your side (non-Fire types take damage every turn)",
        [P + "SWAMP_PLAYER"] = "Swamp on your side (your Speed quartered)",
        [P + "RAINBOW_OPPONENT"] = "Rainbow on their side (their extra effects happen twice as often)",
        [P + "STEALTH_ROCK_PLAYER"] = "Stealth Rock on your side",
        [P + "SPIKES_PLAYER_L1"] = "Spikes on your side",
        [P + "SPIKES_PLAYER_L2"] = "Spikes on your side (2 layers)",
        [P + "SPIKES_PLAYER_L3"] = "Spikes on your side (3 layers)",
        [P + "TOXIC_SPIKES_PLAYER_L1"] = "Toxic Spikes on your side",
        [P + "TOXIC_SPIKES_PLAYER_L2"] = "Toxic Spikes on your side (badly poisons)",
        [P + "STICKY_WEB_PLAYER"] = "Sticky Web on your side",
        [P + "SHARP_STEEL_PLAYER"] = "Sharp steel on your side",
    };

    public static string Label(string effect) =>
        Labels.TryGetValue(effect, out var label) ? label : GameCatalog.Prettify(effect.Replace(P, "", StringComparison.Ordinal));

    /// <summary>The effect for a type that this game has, or null.</summary>
    public static string? ForType(GameData data, string type) =>
        ByType.TryGetValue(type, out var options) ? options.FirstOrDefault(o => data.Catalog.StartingStatuses.Any(s => s.Key == o)) : null;

    /// <summary>
    /// A TM of this type for the player: a damaging move about right for this point of the game, not one the
    /// game's scripts or another story battle already give.
    /// </summary>
    public static string? RewardFor(GameData data, string type, int level, IEnumerable<string> taken, IReadOnlyCollection<string>? teamTypes = null)
    {
        // never a TM the game already gives or another challenger gives: their type's attack, else their type's other TM
        // (Sandstorm for Rock), else one of their team's other types, else a strong one for this point of the game
        var used = taken.ToHashSet(StringComparer.Ordinal);
        var maxPower = level < 20 ? 70 : level < 30 ? 80 : level < 40 ? 95 : 250;
        var all = data.Catalog.Machines.Where(m => m.Label.StartsWith("TM", StringComparison.Ordinal))
            .Select(m => (Item: "ITEM_TM_" + m.Move.Replace("MOVE_", "", StringComparison.Ordinal), Move: data.Moves.FirstOrDefault(x => x.Key == m.Move)))
            .Where(m => m.Move is not null && !used.Contains(m.Item))
            .ToList();
        bool Attack(Move m) => m.Category != MoveCategory.Status && m.Power > 0;
        string? Best(IEnumerable<(string Item, Move? Move)> options)
        {
            var list = options.ToList();
            return list.Where(o => o.Move!.Power <= maxPower).OrderByDescending(o => o.Move!.Power).Select(o => o.Item).FirstOrDefault()
                ?? list.OrderBy(o => o.Move!.Power).Select(o => o.Item).FirstOrDefault();
        }

        return Best(all.Where(m => m.Move!.Type == type && Attack(m.Move)))
            ?? all.Where(m => m.Move!.Type == type).Select(m => m.Item).FirstOrDefault()
            ?? Best(all.Where(m => (teamTypes ?? []).Contains(m.Move!.Type) && m.Move.Type != type && Attack(m.Move)))
            ?? Best(all.Where(m => Attack(m.Move!) && m.Move!.Power >= 60));
    }
}
