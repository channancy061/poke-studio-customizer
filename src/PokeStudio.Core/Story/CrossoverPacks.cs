using PokeStudio.Core.ChampionLab;
using PokeStudio.Core.Changes;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;

namespace PokeStudio.Core.Story;

public enum PackRole
{
    Leader,
    EliteFour,
    Champion,
}

/// <summary>
/// One battle of a crossover pack: who, where (a map and the entrance it guards: the map the entrance leads to), and
/// which battle it comes before (null: the last level-cap step, the League).
/// </summary>
public sealed record PackEntry(
    string Name,
    string Title,
    PackRole Role,
    string Pic,
    string Look,
    string Map,
    string Dest,
    string? Before,
    string Type,
    int TeamSize,
    bool Double = false,
    bool Northmost = false);

/// <summary>Another region's gym leaders, Elite Four and Champion as story battles in this game.</summary>
public sealed record CrossoverPack(string Id, string Title, string Description, string NeedsMap, string SourceProject, IReadOnlyList<PackEntry> Entries)
{
    public IEnumerable<string> Pics => Entries.Select(e => e.Pic).Distinct(StringComparer.Ordinal);
    public IEnumerable<string> Looks => Entries.Select(e => e.Look).Distinct(StringComparer.Ordinal);
}

/// <summary>A sprite to bring from another project on the next save.</summary>
public sealed record StoryImport(string Kind, string Key, string Source)
{
    public const string Pic = "pic";
    public const string Look = "look";
}

public static class CrossoverPacks
{
    private const string Badge = "FLAG_BADGE0";

    /// <summary>FireRed: each Hoenn leader before the matching Kanto gym, the Elite Four along the way, Wallace after Victory Road.</summary>
    public static readonly CrossoverPack HoennInKanto = new(
        "hoenn-in-kanto",
        "Hoenn challengers in Kanto",
        "Each Hoenn gym leader guards the matching Kanto gym, the Hoenn Elite Four guard story places along the way, and Wallace waits just outside Victory Road.",
        "PewterCity",
        "pokeemerald-expansion",
        [
            new("Roxanne", "Gym Leader of Rustboro City", PackRole.Leader, "TRAINER_PIC_LEADER_ROXANNE", "OBJ_EVENT_GFX_ROXANNE", "PewterCity", "MAP_PEWTER_CITY_GYM", Badge + "1_GET", "TYPE_ROCK", 3),
            new("Sidney", "of the Hoenn Elite Four", PackRole.EliteFour, "TRAINER_PIC_ELITE_FOUR_SIDNEY", "OBJ_EVENT_GFX_SIDNEY", "Route4", "MAP_MT_MOON_1F", Badge + "2_GET", "TYPE_DARK", 3),
            new("Brawly", "Gym Leader of Dewford Town", PackRole.Leader, "TRAINER_PIC_LEADER_BRAWLY", "OBJ_EVENT_GFX_BRAWLY", "CeruleanCity", "MAP_CERULEAN_CITY_GYM", Badge + "2_GET", "TYPE_FIGHTING", 3),
            new("Wattson", "Gym Leader of Mauville City", PackRole.Leader, "TRAINER_PIC_LEADER_WATTSON", "OBJ_EVENT_GFX_WATTSON", "VermilionCity", "MAP_VERMILION_CITY_GYM", Badge + "3_GET", "TYPE_ELECTRIC", 4),
            new("Flannery", "Gym Leader of Lavaridge Town", PackRole.Leader, "TRAINER_PIC_LEADER_FLANNERY", "OBJ_EVENT_GFX_FLANNERY", "CeladonCity", "MAP_CELADON_CITY_GYM", Badge + "4_GET", "TYPE_FIRE", 4),
            new("Phoebe", "of the Hoenn Elite Four", PackRole.EliteFour, "TRAINER_PIC_ELITE_FOUR_PHOEBE", "OBJ_EVENT_GFX_PHOEBE", "LavenderTown", "MAP_POKEMON_TOWER_1F", Badge + "5_GET", "TYPE_GHOST", 4),
            new("Norman", "Gym Leader of Petalburg City", PackRole.Leader, "TRAINER_PIC_LEADER_NORMAN", "OBJ_EVENT_GFX_NORMAN", "FuchsiaCity", "MAP_FUCHSIA_CITY_GYM", Badge + "5_GET", "TYPE_NORMAL", 4),
            new("Drake", "of the Hoenn Elite Four", PackRole.EliteFour, "TRAINER_PIC_ELITE_FOUR_DRAKE", "OBJ_EVENT_GFX_DRAKE", "SaffronCity", "MAP_SILPH_CO_1F", Badge + "6_GET", "TYPE_DRAGON", 5),
            new("Winona", "Gym Leader of Fortree City", PackRole.Leader, "TRAINER_PIC_LEADER_WINONA", "OBJ_EVENT_GFX_WINONA", "SaffronCity", "MAP_SAFFRON_CITY_GYM", Badge + "6_GET", "TYPE_FLYING", 5),
            new("Glacia", "of the Hoenn Elite Four", PackRole.EliteFour, "TRAINER_PIC_ELITE_FOUR_GLACIA", "OBJ_EVENT_GFX_GLACIA", "CinnabarIsland", "MAP_POKEMON_MANSION_1F", Badge + "7_GET", "TYPE_ICE", 5),
            new("Tate&Liza", "Gym Leaders of Mossdeep City", PackRole.Leader, "TRAINER_PIC_LEADER_TATE_AND_LIZA", "OBJ_EVENT_GFX_TATE", "CinnabarIsland", "MAP_CINNABAR_ISLAND_GYM", Badge + "7_GET", "TYPE_PSYCHIC", 4, Double: true),
            new("Juan", "Gym Leader of Sootopolis City", PackRole.Leader, "TRAINER_PIC_LEADER_JUAN", "OBJ_EVENT_GFX_JUAN", "ViridianCity", "MAP_VIRIDIAN_CITY_GYM", Badge + "8_GET", "TYPE_WATER", 5),
            new("Wallace", "Champion of Hoenn", PackRole.Champion, "TRAINER_PIC_CHAMPION_WALLACE", "OBJ_EVENT_GFX_WALLACE", "Route23", "MAP_VICTORY_ROAD_2F", null, "TYPE_WATER", 6),
        ]);

    /// <summary>Emerald: each Kanto leader before the matching Hoenn gym, the Elite Four along the way, Blue after Victory Road.</summary>
    public static readonly CrossoverPack KantoInHoenn = new(
        "kanto-in-hoenn",
        "Kanto challengers in Hoenn",
        "Each Kanto gym leader guards the matching Hoenn gym, the Kanto Elite Four guard story places along the way, and Blue waits just outside Victory Road.",
        "RustboroCity",
        "pokefirered-expansion",
        [
            new("Brock", "Gym Leader of Pewter City", PackRole.Leader, "TRAINER_PIC_LEADER_BROCK_FRLG", "OBJ_EVENT_GFX_BROCK", "RustboroCity", "MAP_RUSTBORO_CITY_GYM", Badge + "1_GET", "TYPE_ROCK", 3),
            new("Misty", "Gym Leader of Cerulean City", PackRole.Leader, "TRAINER_PIC_LEADER_MISTY_FRLG", "OBJ_EVENT_GFX_MISTY", "DewfordTown", "MAP_DEWFORD_TOWN_GYM", Badge + "2_GET", "TYPE_WATER", 3),
            new("Lt. Surge", "Gym Leader of Vermilion City", PackRole.Leader, "TRAINER_PIC_LEADER_LT_SURGE_FRLG", "OBJ_EVENT_GFX_LT_SURGE", "MauvilleCity", "MAP_MAUVILLE_CITY_GYM", Badge + "3_GET", "TYPE_ELECTRIC", 4),
            new("Lorelei", "of the Kanto Elite Four", PackRole.EliteFour, "TRAINER_PIC_ELITE_FOUR_LORELEI_FRLG", "OBJ_EVENT_GFX_LORELEI", "Route112", "MAP_ROUTE112_CABLE_CAR_STATION", Badge + "4_GET", "TYPE_ICE", 4),
            new("Erika", "Gym Leader of Celadon City", PackRole.Leader, "TRAINER_PIC_LEADER_ERIKA_FRLG", "OBJ_EVENT_GFX_ERIKA", "LavaridgeTown", "MAP_LAVARIDGE_TOWN_GYM_1F", Badge + "4_GET", "TYPE_GRASS", 4),
            new("Koga", "Gym Leader of Fuchsia City", PackRole.Leader, "TRAINER_PIC_LEADER_KOGA_FRLG", "OBJ_EVENT_GFX_KOGA", "PetalburgCity", "MAP_PETALBURG_CITY_GYM", Badge + "5_GET", "TYPE_POISON", 4),
            new("Bruno", "of the Kanto Elite Four", PackRole.EliteFour, "TRAINER_PIC_ELITE_FOUR_BRUNO_FRLG", "OBJ_EVENT_GFX_BRUNO", "Route119", "MAP_ROUTE119_WEATHER_INSTITUTE_1F", Badge + "6_GET", "TYPE_FIGHTING", 5),
            new("Sabrina", "Gym Leader of Saffron City", PackRole.Leader, "TRAINER_PIC_LEADER_SABRINA_FRLG", "OBJ_EVENT_GFX_SABRINA", "FortreeCity", "MAP_FORTREE_CITY_GYM", Badge + "6_GET", "TYPE_PSYCHIC", 5),
            new("Agatha", "of the Kanto Elite Four", PackRole.EliteFour, "TRAINER_PIC_ELITE_FOUR_AGATHA_FRLG", "OBJ_EVENT_GFX_AGATHA", "Route122", "MAP_MT_PYRE_1F", Badge + "7_GET", "TYPE_GHOST", 5),
            new("Blaine", "Gym Leader of Cinnabar Island", PackRole.Leader, "TRAINER_PIC_LEADER_BLAINE_FRLG", "OBJ_EVENT_GFX_BLAINE", "MossdeepCity", "MAP_MOSSDEEP_CITY_GYM", Badge + "7_GET", "TYPE_FIRE", 5),
            new("Lance", "of the Kanto Elite Four", PackRole.EliteFour, "TRAINER_PIC_ELITE_FOUR_LANCE_FRLG", "OBJ_EVENT_GFX_LANCE", "SkyPillar_Outside", "MAP_SKY_PILLAR_1F", Badge + "8_GET", "TYPE_DRAGON", 5),
            new("Giovanni", "Gym Leader of Viridian City", PackRole.Leader, "TRAINER_PIC_LEADER_GIOVANNI_FRLG", "OBJ_EVENT_GFX_GIOVANNI", "SootopolisCity", "MAP_SOOTOPOLIS_CITY_GYM_1F", Badge + "8_GET", "TYPE_GROUND", 5),
            new("Blue", "Champion of Kanto", PackRole.Champion, "TRAINER_PIC_CHAMPION_RIVAL_FRLG", "OBJ_EVENT_GFX_BLUE", "EverGrandeCity", "MAP_VICTORY_ROAD_1F", null, "", 6, Northmost: true),
        ]);

    public static IReadOnlyList<CrossoverPack> All { get; } = [HoennInKanto, KantoInHoenn];

    /// <summary>A name the game has room for ("Lt. Surge" stays; longer ones lose their spaces, then letters).</summary>
    private static string Fit(string name, int limit) =>
        name.Length <= limit ? name : name.Replace(" ", "", StringComparison.Ordinal) is var tight && tight.Length <= limit ? tight : tight[..limit];

    /// <summary>The trainer class for a role, from the game's own list.</summary>
    private static string? ClassFor(GameData data, PackRole role)
    {
        var key = role switch { PackRole.Leader => "TRAINER_CLASS_LEADER", PackRole.EliteFour => "TRAINER_CLASS_ELITE_FOUR", _ => "TRAINER_CLASS_CHAMPION" };
        return data.Catalog.TrainerClasses.Any(c => c.Key == key) ? key : null;
    }

    /// <summary>
    /// Builds the whole pack as one undo step: the sprites to import, a type theme per trainer, and every story battle
    /// with a Champion Lab team at the right level. Entries whose map, entrance or milestone the game doesn't have are
    /// left out and listed in the notes.
    /// </summary>
    /// <param name="spots">The spots of a map (from the game module).</param>
    /// <param name="imports">Sprites this game lacks and the source project to take them from.</param>
    public static (IChange? Change, List<string> Notes, int Added) Build(GameData data, CrossoverPack pack, Func<string, IReadOnlyList<StorySpot>> spots, IReadOnlyList<StoryImport> imports, int nameLimit = 10, IReadOnlyCollection<string>? givenTms = null)
    {
        var rewarded = new HashSet<string>(givenTms ?? [], StringComparer.Ordinal);
        foreach (var r in data.Story?.Battles.Select(b => b.Reward).OfType<string>() ?? [])
        {
            rewarded.Add(r);
        }

        var notes = new List<string>();
        var parts = new List<IChange>();
        void Do(IChange change)
        {
            change.Apply();
            parts.Add(change);
        }

        try
        {
            // 1. sprites to bring in on save
            if (imports.Count > 0)
            {
                var story = data.Story ?? new StorySettings();
                var merged = story.Imports.Concat(imports.Where(i => !story.Imports.Any(x => x.Kind == i.Kind && x.Key == i.Key))).ToList();
                Do(new ValueChange<StorySettings?>("Story battles", "Sprites to import", v => data.Story = v, data.Story, story with { Imports = merged }));
            }

            var added = 0;
            var lastLevel = 0;
            var milestones = data.Rules?.LevelCaps.Where(c => !c.Milestone.StartsWith("FLAG_PS_BEAT_", StringComparison.Ordinal)).ToList() ?? [];
            foreach (var e in pack.Entries)
            {
                var spot = spots(e.Map).Where(s => s.Entrance == StoryBattles.PlaceName(e.Dest))
                    .OrderBy(s => e.Northmost ? s.DoorY : 0).FirstOrDefault();
                var before = e.Before ?? milestones.LastOrDefault()?.Milestone;
                var cap = milestones.FindIndex(c => c.Milestone == before);
                var trainerClass = ClassFor(data, e.Role);
                if (spot is null || cap < 0 || trainerClass is null)
                {
                    notes.Add($"{e.Name} left out: {(spot is null ? $"no free spot in front of {StoryBattles.PlaceName(e.Dest)} on {StoryBattles.PlaceName(e.Map)}" : cap < 0 ? "the battle they come before isn't in the level caps" : "the game has no class for them")}.");
                    continue;
                }

                if (data.Story?.Battles.Any(b => b.Map == spot.Map && b.X == spot.X && b.Y == spot.Y) == true)
                {
                    notes.Add($"{e.Name} left out: another story battle already guards {spot.Entrance}.");
                    continue;
                }

                // level: leaders just under the gym they come before, the Elite Four between the two caps around them
                var next = milestones[cap].Level;
                var previous = cap > 0 ? milestones[cap - 1].Level : Math.Max(5, next - 6);
                var level = Math.Max(5, e.Role switch
                {
                    PackRole.Leader => next - 1,
                    PackRole.EliteFour => (previous + next + 1) / 2,
                    _ => next - 2,
                });

                // never lower than the battle before it (Kanto's 5th and 6th gyms share a cap, for example)
                level = Math.Min(Math.Max(level, lastLevel), next);
                lastLevel = level;

                var capitals = data.Trainers.Count(t => t.Name.Length > 1 && !t.Name.Any(char.IsLower)) > data.Trainers.Count / 2;
                var sameClass = data.Trainers.FirstOrDefault(t => t.TrainerClass == trainerClass && t.Party.Count > 0 && !t.Key.StartsWith(StoryBattles.TrainerPrefix, StringComparison.Ordinal));
                var key = StoryBattles.NewTrainerKey(data, e.Name);
                var trainer = new Trainer
                {
                    Id = data.Trainers.Select(t => t.Id).DefaultIfEmpty(0).Max() + 1,
                    Key = key,
                    Name = Fit(capitals ? e.Name.ToUpperInvariant() : e.Name, nameLimit),
                    TrainerClass = trainerClass,
                    Pic = e.Pic,
                    Gender = e.Look.EndsWith("_F", StringComparison.Ordinal) || e.Name is "Roxanne" or "Flannery" or "Winona" or "Phoebe" or "Glacia" or "Misty" or "Erika" or "Sabrina" or "Lorelei" or "Agatha" ? "Female" : "Male",
                    Music = sameClass?.Music ?? "",
                    AiFlags = sameClass?.AiFlags ?? [],
                    Battle = e.Double ? BattleType.Double : BattleType.Single,

                    // the whole battle on their terms: a lasting effect of their type (Flannery's sun, Juan's rain…)
                    StartingStatus = e.Type.Length > 0 && StoryEffects.ForType(data, e.Type) is { } effect ? [effect] : [],
                };

                // Champion Lab team in the trainer's type (the Champion has no single type)
                var lab = data.Lab ?? new LabSettings();
                if (e.Type.Length > 0 && !lab.Themes.ContainsKey(key))
                {
                    var themed = lab with { Themes = new Dictionary<string, TypeTheme>(lab.Themes) { [key] = new TypeTheme(e.Type, e.Role == PackRole.Champion ? 2 : 0) } };
                    Do(new ValueChange<LabSettings?>("Champion Lab", $"{e.Name}: {GameCatalog.Prettify(e.Type.Replace("TYPE_", "", StringComparison.Ordinal))} theme", v => data.Lab = v, data.Lab, themed));
                    lab = themed;
                }

                var size = Math.Max(e.Double ? 2 : 1, e.TeamSize);
                var team = TeamBuilder.Suggest(data, lab, trainer, new TeamBuilder.Options(Difficulty.Normal, size, level));
                var party = team.Members.Select(m => m.Pokemon).ToList();
                if (party.Count < (e.Double ? 2 : 1) && sameClass is not null)
                {
                    party = sameClass.Party.Take(size).Select(p => p with { Level = level }).ToList();
                }

                if (party.Count < (e.Double ? 2 : 1))
                {
                    notes.Add($"{e.Name} left out: no team fits the Champion Lab rules at level {level}.");
                    continue;
                }

                trainer = trainer with { Party = party };
                var battle = new StoryBattle
                {
                    TrainerKey = key,
                    Map = spot.Map,
                    Entrance = spot.Entrance,
                    X = spot.X,
                    Y = spot.Y,
                    Elevation = spot.Elevation,
                    Facing = spot.Facing,
                    Sight = spot.Sight,
                    DoorX = spot.DoorX,
                    DoorY = spot.DoorY,
                    FrontX = spot.FrontX,
                    FrontY = spot.FrontY,
                    Look = e.Look,
                    Intro = StoryBattles.DefaultIntro(e.Name, e.Title),
                    Defeat = StoryBattles.DefaultDefeat(e.Name),
                    After = StoryBattles.DefaultAfter(e.Name),
                    Before = before,

                    // a TM of their type for the player, like a gym leader gives, never one the game or another challenger gives
                    // (old games have few TMs per type: then their type's other TM, or one of their team's other types)
                    Reward = e.Type.Length > 0 && e.Role != PackRole.Champion
                        ? StoryEffects.RewardFor(data, e.Type, level, rewarded,
                            trainer.Party.SelectMany(p => data.Species.FirstOrDefault(x => x.Key == p.Species)?.Types ?? []).ToHashSet(StringComparer.Ordinal))
                        : null,
                };
                if (battle.Reward is { } reward)
                {
                    rewarded.Add(reward);
                }

                Do(StoryBattles.Add(data, battle, trainer, level));
                added++;
            }

            return (parts.Count == 0 ? null : new CompositeChange("Story battles", $"{pack.Title}: {added} battles", parts), notes, added);
        }
        finally
        {
            // the app applies the change itself (one undo step); leave the data as it was
            for (var i = parts.Count - 1; i >= 0; i--)
            {
                parts[i].Revert();
            }
        }
    }
}
