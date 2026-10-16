using System.Text.RegularExpressions;
using PokeStudio.Core.Modules;
using PokeStudio.Core.Story;

namespace PokeStudio.Core.Progression;

/// <summary>
/// The order a playthrough reaches the wild areas of FireRed and Emerald, with each badge where it's won and the
/// League after Victory Road. Areas not listed come after the League (the post-game). Major battles are placed in it:
/// a badge at its own place, a story battle in front of the place it guards (or just before the battle it comes before),
/// and everything else at the League. It decides which major battle each area leads up to, so a Rock gym's counters are
/// planned on the routes before that gym, not on the route after it.
/// </summary>
public static partial class StoryOrder
{
    public const string League = "@LEAGUE";

    private static string Badge(int n) => $"FLAG_BADGE0{n}_GET";

    /// <summary>FireRed: Kanto, then the Sevii Islands One to Three before the last badge.</summary>
    public static IReadOnlyList<string> FireRed { get; } =
    [
        "MAP_PALLET_TOWN", "MAP_ROUTE1", "MAP_VIRIDIAN_CITY", "MAP_ROUTE22", "MAP_ROUTE2", "MAP_VIRIDIAN_FOREST",
        Badge(1),
        "MAP_ROUTE3", "MAP_ROUTE4", "MAP_MT_MOON_1F", "MAP_MT_MOON_B1F", "MAP_MT_MOON_B2F", "MAP_CERULEAN_CITY", "MAP_ROUTE24", "MAP_ROUTE25",
        Badge(2),
        "MAP_ROUTE5", "MAP_ROUTE6", "MAP_VERMILION_CITY", "MAP_SSANNE_EXTERIOR", "MAP_ROUTE11", "MAP_DIGLETTS_CAVE_B1F",
        Badge(3),
        "MAP_ROUTE9", "MAP_ROUTE10", "MAP_ROCK_TUNNEL_1F", "MAP_ROCK_TUNNEL_B1F", "MAP_ROUTE8", "MAP_ROUTE7", "MAP_CELADON_CITY",
        Badge(4),
        "MAP_POKEMON_TOWER_3F", "MAP_POKEMON_TOWER_4F", "MAP_POKEMON_TOWER_5F", "MAP_POKEMON_TOWER_6F", "MAP_POKEMON_TOWER_7F",
        "MAP_ROUTE12", "MAP_ROUTE13", "MAP_ROUTE14", "MAP_ROUTE15", "MAP_ROUTE16", "MAP_ROUTE17", "MAP_ROUTE18", "MAP_FUCHSIA_CITY",
        "MAP_SAFARI_ZONE_CENTER", "MAP_SAFARI_ZONE_EAST", "MAP_SAFARI_ZONE_NORTH", "MAP_SAFARI_ZONE_WEST",
        Badge(5),
        Badge(6),
        "MAP_POWER_PLANT", "MAP_ROUTE19", "MAP_ROUTE20", "MAP_SEAFOAM_ISLANDS_1F", "MAP_SEAFOAM_ISLANDS_B1F", "MAP_SEAFOAM_ISLANDS_B2F",
        "MAP_SEAFOAM_ISLANDS_B3F", "MAP_SEAFOAM_ISLANDS_B4F", "MAP_ROUTE21_NORTH", "MAP_ROUTE21_SOUTH", "MAP_CINNABAR_ISLAND",
        "MAP_POKEMON_MANSION_1F", "MAP_POKEMON_MANSION_2F", "MAP_POKEMON_MANSION_3F", "MAP_POKEMON_MANSION_B1F",
        Badge(7),
        "MAP_ONE_ISLAND", "MAP_ONE_ISLAND_KINDLE_ROAD", "MAP_ONE_ISLAND_TREASURE_BEACH", "MAP_MT_EMBER_EXTERIOR",
        "MAP_MT_EMBER_SUMMIT_PATH_1F", "MAP_MT_EMBER_SUMMIT_PATH_2F", "MAP_MT_EMBER_SUMMIT_PATH_3F", "MAP_MT_EMBER_RUBY_PATH_1F",
        "MAP_MT_EMBER_RUBY_PATH_B1F", "MAP_MT_EMBER_RUBY_PATH_B1F_STAIRS", "MAP_MT_EMBER_RUBY_PATH_B2F", "MAP_MT_EMBER_RUBY_PATH_B2F_STAIRS",
        "MAP_MT_EMBER_RUBY_PATH_B3F", "MAP_TWO_ISLAND_CAPE_BRINK", "MAP_THREE_ISLAND_PORT", "MAP_THREE_ISLAND_BOND_BRIDGE", "MAP_THREE_ISLAND_BERRY_FOREST",
        Badge(8),
        "MAP_ROUTE23", "MAP_VICTORY_ROAD_1F", "MAP_VICTORY_ROAD_2F", "MAP_VICTORY_ROAD_3F",
        League,
    ];

    /// <summary>Emerald: Hoenn in the usual badge order, Sky Pillar before the last gym.</summary>
    public static IReadOnlyList<string> Emerald { get; } =
    [
        "MAP_ROUTE101", "MAP_ROUTE103", "MAP_ROUTE102", "MAP_PETALBURG_CITY", "MAP_ROUTE104", "MAP_PETALBURG_WOODS", "MAP_ROUTE116",
        Badge(1),
        "MAP_RUSTURF_TUNNEL", "MAP_DEWFORD_TOWN", "MAP_ROUTE106", "MAP_GRANITE_CAVE_1F", "MAP_GRANITE_CAVE_B1F", "MAP_GRANITE_CAVE_B2F",
        "MAP_GRANITE_CAVE_STEVENS_ROOM",
        Badge(2),
        "MAP_ROUTE109", "MAP_SLATEPORT_CITY", "MAP_ROUTE110", "MAP_ROUTE117", "MAP_ROUTE111",
        Badge(3),
        "MAP_ROUTE112", "MAP_FIERY_PATH", "MAP_ROUTE113", "MAP_ROUTE114", "MAP_METEOR_FALLS_1F_1R", "MAP_METEOR_FALLS_1F_2R", "MAP_ROUTE115",
        "MAP_JAGGED_PASS",
        Badge(4),
        "MAP_MIRAGE_TOWER_1F", "MAP_MIRAGE_TOWER_2F", "MAP_MIRAGE_TOWER_3F", "MAP_MIRAGE_TOWER_4F",
        Badge(5),
        "MAP_ROUTE105", "MAP_ROUTE107", "MAP_ROUTE108", "MAP_ABANDONED_SHIP_ROOMS_B1F", "MAP_NEW_MAUVILLE_ENTRANCE", "MAP_NEW_MAUVILLE_INSIDE",
        "MAP_ROUTE118", "MAP_ROUTE119", "MAP_ROUTE120",
        Badge(6),
        "MAP_ROUTE121", "MAP_SAFARI_ZONE_SOUTH", "MAP_SAFARI_ZONE_SOUTHWEST", "MAP_SAFARI_ZONE_NORTH", "MAP_SAFARI_ZONE_NORTHWEST",
        "MAP_LILYCOVE_CITY", "MAP_ROUTE122", "MAP_MT_PYRE_1F", "MAP_MT_PYRE_2F", "MAP_MT_PYRE_3F", "MAP_MT_PYRE_4F", "MAP_MT_PYRE_5F",
        "MAP_MT_PYRE_6F", "MAP_MT_PYRE_EXTERIOR", "MAP_MT_PYRE_SUMMIT", "MAP_ROUTE123", "MAP_MAGMA_HIDEOUT_1F", "MAP_MAGMA_HIDEOUT_2F_1R",
        "MAP_MAGMA_HIDEOUT_2F_2R", "MAP_MAGMA_HIDEOUT_2F_3R", "MAP_MAGMA_HIDEOUT_3F_1R", "MAP_MAGMA_HIDEOUT_3F_2R", "MAP_MAGMA_HIDEOUT_3F_3R",
        "MAP_MAGMA_HIDEOUT_4F", "MAP_ROUTE124", "MAP_UNDERWATER_ROUTE124", "MAP_MOSSDEEP_CITY", "MAP_ROUTE125",
        "MAP_SHOAL_CAVE_LOW_TIDE_ENTRANCE_ROOM", "MAP_SHOAL_CAVE_LOW_TIDE_INNER_ROOM", "MAP_SHOAL_CAVE_LOW_TIDE_STAIRS_ROOM",
        "MAP_SHOAL_CAVE_LOW_TIDE_LOWER_ROOM", "MAP_SHOAL_CAVE_LOW_TIDE_ICE_ROOM", "MAP_ROUTE127", "MAP_ROUTE128",
        Badge(7),
        "MAP_SEAFLOOR_CAVERN_ENTRANCE", "MAP_SEAFLOOR_CAVERN_ROOM1", "MAP_SEAFLOOR_CAVERN_ROOM2", "MAP_SEAFLOOR_CAVERN_ROOM3",
        "MAP_SEAFLOOR_CAVERN_ROOM4", "MAP_SEAFLOOR_CAVERN_ROOM5", "MAP_SEAFLOOR_CAVERN_ROOM6", "MAP_SEAFLOOR_CAVERN_ROOM7",
        "MAP_SEAFLOOR_CAVERN_ROOM8", "MAP_ROUTE126", "MAP_UNDERWATER_ROUTE126", "MAP_SOOTOPOLIS_CITY", "MAP_CAVE_OF_ORIGIN_ENTRANCE",
        "MAP_CAVE_OF_ORIGIN_1F", "MAP_ROUTE129", "MAP_ROUTE130", "MAP_ROUTE131", "MAP_PACIFIDLOG_TOWN", "MAP_ROUTE132", "MAP_ROUTE133",
        "MAP_ROUTE134", "MAP_SKY_PILLAR_1F", "MAP_SKY_PILLAR_3F", "MAP_SKY_PILLAR_5F",
        Badge(8),
        "MAP_EVER_GRANDE_CITY", "MAP_VICTORY_ROAD_1F", "MAP_VICTORY_ROAD_B1F", "MAP_VICTORY_ROAD_B2F",
        League,
    ];

    /// <summary>
    /// When each way of meeting wild Pokémon opens up: where the fishing rods are given, and the badge (or place) that
    /// lets Surf and Rock Smash be used outside battle. Before that, those Pokémon can't be caught however early the area is.
    /// </summary>
    private static readonly Dictionary<string, string> FireRedGates = new(StringComparer.Ordinal)
    {
        ["old_rod"] = "MAP_VERMILION_CITY", ["good_rod"] = "MAP_FUCHSIA_CITY", ["super_rod"] = "MAP_ROUTE12",
        ["water"] = Badge(5), ["rock"] = "MAP_ONE_ISLAND_KINDLE_ROAD",
    };

    private static readonly Dictionary<string, string> EmeraldGates = new(StringComparer.Ordinal)
    {
        ["old_rod"] = "MAP_DEWFORD_TOWN", ["good_rod"] = "MAP_ROUTE118", ["super_rod"] = "MAP_MOSSDEEP_CITY",
        ["water"] = Badge(5), ["rock"] = Badge(3),
    };

    /// <summary>
    /// The earliest place in the order a wild Pokémon met this way can be caught (fishing slots 0–1 need the Old Rod,
    /// 2–4 the Good Rod, the rest the Super Rod), or −1 when nothing is needed.
    /// </summary>
    public static double Gate(IReadOnlyList<string> order, string method, int slot)
    {
        var gates = ReferenceEquals(order, FireRed) ? FireRedGates : ReferenceEquals(order, Emerald) ? EmeraldGates : null;
        var key = method.Contains("fish", StringComparison.OrdinalIgnoreCase) ? (slot < 2 ? "old_rod" : slot < 5 ? "good_rod" : "super_rod")
            : method.Contains("water", StringComparison.OrdinalIgnoreCase) ? "water"
            : method.Contains("rock", StringComparison.OrdinalIgnoreCase) ? "rock"
            : null;
        return gates is not null && key is not null && gates.TryGetValue(key, out var token) && IndexOf(order, token) is >= 0 and var i ? i : -1;
    }

    /// <summary>Each gym leader's badge in FireRed and Emerald (their first battle; rematches come after the League).</summary>
    private static readonly Dictionary<string, int> LeaderBadges = new(StringComparer.Ordinal)
    {
        ["TRAINER_LEADER_BROCK"] = 1, ["TRAINER_LEADER_MISTY"] = 2, ["TRAINER_LEADER_LT_SURGE"] = 3, ["TRAINER_LEADER_ERIKA"] = 4,
        ["TRAINER_LEADER_KOGA"] = 5, ["TRAINER_LEADER_SABRINA"] = 6, ["TRAINER_LEADER_BLAINE"] = 7, ["TRAINER_LEADER_GIOVANNI"] = 8,
        ["TRAINER_ROXANNE_1"] = 1, ["TRAINER_BRAWLY_1"] = 2, ["TRAINER_WATTSON_1"] = 3, ["TRAINER_FLANNERY_1"] = 4,
        ["TRAINER_NORMAN_1"] = 5, ["TRAINER_WINONA_1"] = 6, ["TRAINER_TATE_AND_LIZA_1"] = 7, ["TRAINER_JUAN_1"] = 8,
    };

    /// <summary>The badge flag a gym leader gives (FLAG_BADGE05_GET for Koga), or null for anyone else.</summary>
    public static string? BadgeOf(string trainerKey) =>
        LeaderBadges.TryGetValue(trainerKey.Split('@')[0], out var n) ? Badge(n) : null;

    /// <summary>The order for this game (FireRed or Emerald by their maps), or null for other games.</summary>
    public static IReadOnlyList<string>? For(GameData data)
    {
        var maps = data.Encounters.Select(t => t.Map).ToHashSet(StringComparer.Ordinal);
        return maps.Contains("MAP_ROUTE101") && maps.Contains("MAP_PETALBURG_WOODS") ? Emerald
            : maps.Contains("MAP_ROUTE1") && maps.Contains("MAP_VIRIDIAN_FOREST") ? FireRed
            : null;
    }

    /// <summary>
    /// Where each level-cap step sits in the order (rising), or null for games without an order. A story battle sits in
    /// front of the area it guards when that area is in the order, otherwise just before the battle it comes before;
    /// the League's battles at the League, and anything after the League (the final chapter) at the very end.
    /// </summary>
    public static double[]? CapPositions(GameData data)
    {
        if (For(data) is not { } order || data.Rules is not { LevelCaps.Count: > 0 } rules)
        {
            return null;
        }

        var caps = rules.LevelCaps;
        var league = IndexOf(order, League);
        var story = (data.Story?.Battles ?? []).ToDictionary(b => b.BeatFlag, StringComparer.Ordinal);
        var positions = new double[caps.Count];
        double? Fixed(int i) =>
            caps[i].Milestone.StartsWith("FLAG_BADGE0", StringComparison.Ordinal) && IndexOf(order, caps[i].Milestone) is >= 0 and var b ? b
            : story.TryGetValue(caps[i].Milestone, out var s) ? StoryPosition(order, s, league)
            : null;

        // from the end: a story battle without its own place sits just before what follows it
        var next = (double)order.Count;
        for (var i = caps.Count - 1; i >= 0; i--)
        {
            var p = Fixed(i);
            if (p is null && story.TryGetValue(caps[i].Milestone, out var s) && s.Before is not null && s.Before != StoryBattles.AfterLast)
            {
                p = next - 0.5;
            }

            positions[i] = p ?? (story.TryGetValue(caps[i].Milestone, out var after) && after.Before == StoryBattles.AfterLast ? order.Count : league);
            next = positions[i];
        }

        // rising in cap order
        for (var i = 1; i < positions.Length; i++)
        {
            positions[i] = Math.Max(positions[i], positions[i - 1]);
        }

        return positions;
    }

    /// <summary>Where an area is in the order (areas not in it come after the League), or null without an order.</summary>
    public static double? AreaPosition(IReadOnlyList<string> order, string map) =>
        IndexOf(order, map) is >= 0 and var i ? i : IndexOf(order, League) + 0.25;

    /// <summary>The first major battle after an area (an index into the level caps).</summary>
    public static int NextBattle(double[] caps, double areaPosition)
    {
        for (var i = 0; i < caps.Length; i++)
        {
            if (caps[i] > areaPosition)
            {
                return i;
            }
        }

        return caps.Length - 1;
    }

    private static double? StoryPosition(IReadOnlyList<string> order, StoryBattle b, int league)
    {
        // the last step before the League (outside Victory Road's exit), or before a League battle
        if (b.Before is null || (b.Before != StoryBattles.AfterLast && !b.Before.StartsWith("FLAG_BADGE0", StringComparison.Ordinal)))
        {
            return league - 0.5;
        }

        if (b.Before == StoryBattles.AfterLast)
        {
            return order.Count;
        }

        // the area it guards: the same place, or the same place on another floor ("Pokemon Tower 1F" → its 3rd floor)
        var guarded = Floorless(b.Entrance);
        for (var i = 0; i < order.Count; i++)
        {
            if (order[i].StartsWith("MAP_", StringComparison.Ordinal) && Floorless(StoryBattles.PlaceName(order[i])) == guarded)
            {
                return i - 0.5;
            }
        }

        return null;
    }

    /// <summary>"MAP_ROUTE1" → "Route 1", "MAP_SSANNE_EXTERIOR" → "S.S. Anne Exterior", "MAP_MT_MOON_B1F" → "Mt. Moon B1F".</summary>
    public static string AreaName(string map)
    {
        var words = (map.StartsWith("MAP_", StringComparison.Ordinal) ? map[4..] : map).Split('_', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", words.Select(w => w switch
        {
            "SSANNE" => "S.S. Anne",
            "DIGLETTS" => "Diglett's",
            "POKEMON" => "Pokémon",
            "MT" => "Mt.",
            "STEVENS" => "Steven's",
            "WALLYS" => "Wally's",
            _ when FloorWord().IsMatch(w) => w,
            _ when RouteWord().Match(w) is { Success: true } r => $"Route {r.Groups[1].Value}",
            _ => char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant(),
        }));
    }

    [GeneratedRegex(@"^(B?\d+F|\d+R)$")]
    private static partial Regex FloorWord();

    [GeneratedRegex(@"^ROUTE(\d+)$")]
    private static partial Regex RouteWord();

    private static string Floorless(string place) => FloorSuffix().Replace(place.Replace("Pokémon", "Pokemon", StringComparison.Ordinal), "").Trim().ToUpperInvariant();

    private static int IndexOf(IReadOnlyList<string> order, string token)
    {
        for (var i = 0; i < order.Count; i++)
        {
            if (order[i] == token)
            {
                return i;
            }
        }

        return -1;
    }

    [GeneratedRegex(@"\s+(B?\d+F|\d+R)$", RegexOptions.IgnoreCase)]
    private static partial Regex FloorSuffix();
}
