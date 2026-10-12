using PokeStudio.Core.Changes;
using PokeStudio.Core.Modules;

namespace PokeStudio.Core.Story;

/// <summary>
/// A legendary or mythical Pokémon standing on a map, fought (and caught) by talking to it. With <see cref="RevealedBy"/>
/// it stays hidden until that story battle is won: the trainer then tells the player where to look (the side story).
/// If it isn't caught it goes away and comes back the next time the player enters the map.
/// </summary>
public sealed record StoryLegend
{
    public string Species { get; init; } = "";
    public int Level { get; init; } = 50;
    public string Map { get; init; } = "";

    /// <summary>The place as players know it ("Rock Tunnel"), for the clue and the guide.</summary>
    public string Place { get; init; } = "";

    public int X { get; init; }
    public int Y { get; init; }
    public int Elevation { get; init; }

    /// <summary>The story battle (trainer key) whose first win reveals it, or null when it's there from the start.</summary>
    public string? RevealedBy { get; init; }

    /// <summary>What the trainer says after that win.</summary>
    public string Clue { get; init; } = "";

    /// <summary>A stable id for its flag and script, from the species ("REGIROCK").</summary>
    public string Id => Species.StartsWith("SPECIES_", StringComparison.Ordinal) ? Species["SPECIES_".Length..] : Species;
}

/// <summary>One legend of a trail: who reveals it (a pack trainer's name), which Pokémon and where.</summary>
public sealed record LegendEntry(string Revealer, string Species, string Map, string Place);

/// <summary>Legend trails: after each crossover challenger, a legendary of their region appears somewhere in this one.</summary>
public static class Legends
{
    public const string FlagPrefix = "FLAG_PS_LEGEND_";

    public static string FlagOf(StoryLegend legend) => FlagPrefix + legend.Id;

    /// <summary>FireRed with the Hoenn challengers: Hoenn's legendaries and Jirachi in Kanto.</summary>
    public static IReadOnlyList<LegendEntry> HoennTrail { get; } =
    [
        new("Roxanne", "SPECIES_REGIROCK", "RockTunnel_B1F", "Rock Tunnel"),
        new("Brawly", "SPECIES_REGICE", "SeafoamIslands_B3F", "the Seafoam Islands"),
        new("Wattson", "SPECIES_REGISTEEL", "PowerPlant", "the Power Plant"),
        new("Flannery", "SPECIES_GROUDON", "MtEmber_RubyPath_B5F", "the depths of Mt. Ember"),
        new("Norman", "SPECIES_LATIAS", "ViridianForest", "Viridian Forest"),
        new("Winona", "SPECIES_LATIOS", "Route25", "Route 25, near Bill's house"),
        new("Tate&Liza", "SPECIES_JIRACHI", "MtMoon_B2F", "the bottom of Mt. Moon"),
        new("Juan", "SPECIES_KYOGRE", "SeafoamIslands_B4F", "the bottom of the Seafoam Islands"),
        new("Drake", "SPECIES_RAYQUAZA", "VictoryRoad_3F", "the top of Victory Road"),
    ];

    /// <summary>Emerald with the Kanto challengers: Kanto's and Johto's legendaries, Mew and Celebi in Hoenn.</summary>
    public static IReadOnlyList<LegendEntry> KantoTrail { get; } =
    [
        new("Misty", "SPECIES_SUICUNE", "ShoalCave_LowTideInnerRoom", "Shoal Cave"),
        new("Lt. Surge", "SPECIES_ZAPDOS", "NewMauville_Inside", "New Mauville"),
        new("Lorelei", "SPECIES_ARTICUNO", "ShoalCave_LowTideIceRoom", "the ice room of Shoal Cave"),
        new("Erika", "SPECIES_CELEBI", "PetalburgWoods", "Petalburg Woods"),
        new("Koga", "SPECIES_RAIKOU", "RusturfTunnel", "Rusturf Tunnel"),
        new("Bruno", "SPECIES_ENTEI", "MtChimney", "the top of Mt. Chimney"),
        new("Sabrina", "SPECIES_MEW", "MeteorFalls_B1F_2R", "the depths of Meteor Falls"),
        new("Blaine", "SPECIES_MOLTRES", "FieryPath", "the Fiery Path"),
        new("Giovanni", "SPECIES_MEWTWO", "VictoryRoad_B2F", "the bottom of Victory Road"),
    ];

    public static IReadOnlyList<LegendEntry> TrailFor(CrossoverPack pack) =>
        pack.Id == CrossoverPacks.HoennInKanto.Id ? HoennTrail : pack.Id == CrossoverPacks.KantoInHoenn.Id ? KantoTrail : [];

    public static string Clue(string speciesName, string place) =>
        $"Before you go… On my way here I felt\\na huge power in {place}.\\pIt felt like {speciesName}.\\nGo and see for yourself!";

    /// <summary>
    /// Adds a trail as one undo step: each legend stands on a free spot of its map (from <paramref name="spotFor"/>),
    /// hidden until its revealer's story battle is won, at a level a little above that trainer's team. Legends whose
    /// revealer, species or map this game doesn't have are left out and listed in the notes.
    /// </summary>
    public static (IChange? Change, List<string> Notes, int Added) AddTrail(GameData data, IReadOnlyList<LegendEntry> trail, Func<string, StorySpot?> spotFor)
    {
        var notes = new List<string>();
        var story = data.Story ?? new StorySettings();
        var legends = story.Legends.ToList();
        foreach (var e in trail)
        {
            var species = data.Species.FirstOrDefault(s => s.Key == e.Species);
            var revealer = data.Trainers.FirstOrDefault(t => story.Battles.Any(b => b.TrainerKey == t.Key) && string.Equals(t.Name, e.Revealer, StringComparison.OrdinalIgnoreCase));
            if (legends.Any(l => l.Species == e.Species))
            {
                continue;
            }

            if (species is null || revealer is null)
            {
                notes.Add($"{e.Species}: {(species is null ? "this game doesn't have it" : $"{e.Revealer} isn't in the story")}.");
                continue;
            }

            if (spotFor(e.Map) is not { } spot)
            {
                notes.Add($"{species.Name}: no free spot in {e.Place}.");
                continue;
            }

            var level = Math.Clamp(revealer.Party.Select(p => p.Level).DefaultIfEmpty(50).Max() + 2, 1, 100);
            legends.Add(new StoryLegend
            {
                Species = e.Species, Level = level, Map = e.Map, Place = e.Place, X = spot.X, Y = spot.Y, Elevation = spot.Elevation,
                RevealedBy = revealer.Key, Clue = Clue(species.Name, e.Place),
            });
        }

        var added = legends.Count - story.Legends.Count;
        if (added == 0)
        {
            return (null, notes, 0);
        }

        var change = new ValueChange<StorySettings?>("Story battles", $"Legend trail: {added} legendary Pokémon", v => data.Story = v, data.Story, story with { Legends = legends });
        return (change, notes, added);
    }

    /// <summary>Takes one legend out (one undo step).</summary>
    public static IChange? Remove(GameData data, string species) =>
        data.Story is { } story && story.Legends.Any(l => l.Species == species)
            ? new ValueChange<StorySettings?>("Story battles", $"Legend removed: {species}", v => data.Story = v, story, story with { Legends = story.Legends.Where(l => l.Species != species).ToList() })
            : null;

    /// <summary>Changes one legend's level (one undo step).</summary>
    public static IChange? SetLevel(GameData data, string species, int level) =>
        data.Story is { } story && story.Legends.FirstOrDefault(l => l.Species == species) is { } old && old.Level != level
            ? new ValueChange<StorySettings?>("Story battles", $"{species} level {level}", v => data.Story = v, story,
                story with { Legends = story.Legends.Select(l => l.Species == species ? l with { Level = Math.Clamp(level, 1, 100) } : l).ToList() })
            : null;
}
