using PokeStudio.Core.Story;
using PokeStudio.Modules.Expansion;

namespace PokeStudio.Core.Tests;

/// <summary>Crossover packs: Emerald's numbered trainer list, sprite imports and whole packs on the user's real projects.</summary>
public class CrossoverTests
{
    [Fact]
    public void Numbered_trainer_lists_get_numbers_after_the_last_and_more_room_when_needed()
    {
        const string text = "#define TRAINER_NONE 0\n#define TRAINER_MAY_PLACEHOLDER 854\n\n#define TRAINERS_COUNT_EMERALD     855\n#define MAX_TRAINERS_COUNT_EMERALD 864\n\n#define TRAINERS_COUNT TRAINERS_COUNT_EMERALD\n";
        var two = StoryWriter.Opponent(text, ["TRAINER_PS_BROCK", "TRAINER_PS_MISTY"]);
        Assert.Contains("#define TRAINERS_COUNT_EMERALD     855\n#define TRAINER_PS_BROCK (855) // Poke Studio story battle\n#define TRAINER_PS_MISTY (856) // Poke Studio story battle\n#undef TRAINERS_COUNT_EMERALD // Poke Studio story battle\n#define TRAINERS_COUNT_EMERALD (857) // Poke Studio story battle\n", two, StringComparison.Ordinal);
        Assert.DoesNotContain("#undef MAX_TRAINERS_COUNT_EMERALD", two, StringComparison.Ordinal); // still room

        var keys = Enumerable.Range(0, 13).Select(i => $"TRAINER_PS_T{i}").ToList();
        var thirteen = StoryWriter.Opponent(text, keys);
        Assert.Contains("#define MAX_TRAINERS_COUNT_EMERALD 864\n#undef MAX_TRAINERS_COUNT_EMERALD // Poke Studio story battle\n#define MAX_TRAINERS_COUNT_EMERALD (872) // Poke Studio story battle\n", thirteen, StringComparison.Ordinal);

        // saving again gives the same file; with none left the original comes back
        Assert.Equal(thirteen, StoryWriter.Opponent(thirteen, keys));
        Assert.Equal(text, StoryWriter.Opponent(thirteen, []));
    }

    [Fact]
    public void Starting_status_is_written_the_way_trainerproc_reads_it_and_reads_back()
    {
        var c = new Models.GameCatalog();
        c.StartingStatuses.Add(new Models.CatalogEntry("STARTING_STATUS_WEATHER_SUN", "Harsh sun", "weatherSun"));
        c.StartingStatuses.Add(new Models.CatalogEntry("STARTING_STATUS_SPIKES_PLAYER_L1", "Spikes", "spikesPlayerL1"));
        var t = new Models.Trainer { Id = 1, Key = "TRAINER_PS_FLANNERY", Name = "FLANNERY", Pic = "TRAINER_PIC_LEADER_FLANNERY",
            StartingStatus = ["STARTING_STATUS_WEATHER_SUN", "STARTING_STATUS_SPIKES_PLAYER_L1"],
            Party = [new Models.TrainerPokemon { Species = "SPECIES_TORKOAL", Level = 28 }] };
        var text = TrainerParty.FormatTrainer(t, c, new Dictionary<string, string>(), "\n");
        Assert.Contains("Starting Status: Weather Sun / Spikes Player L1\n", text, StringComparison.Ordinal);
        var (_, back) = TrainerParty.Parse(text, TrainerParty.SpeciesConstant).Single();
        Assert.Equal(t.StartingStatus, back.StartingStatus);
    }

    private static string? Dir(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    [Fact]
    public void Hoenn_sprites_can_be_planned_into_firered_when_available()
    {
        if (Dir("POKESTUDIO_FIRERED_DIR") is not { } fr || Dir("POKESTUDIO_EXPANSION_DIR") is not { } em)
        {
            return;
        }

        string? Read(string rel) => File.Exists(Path.Combine(fr, rel)) ? File.ReadAllText(Path.Combine(fr, rel)) : null;
        var plan = new ProjectImport.Plan(Read, rel => File.Exists(Path.Combine(fr, rel)));
        Assert.False(ProjectImport.HasPic(Read, "TRAINER_PIC_LEADER_JUAN"));
        ProjectImport.AddPic(plan, em, "TRAINER_PIC_LEADER_JUAN");
        ProjectImport.AddPic(plan, em, "TRAINER_PIC_CHAMPION_WALLACE");
        ProjectImport.AddLook(plan, em, "OBJ_EVENT_GFX_ROXANNE");
        ProjectImport.AddLook(plan, em, "OBJ_EVENT_GFX_WALLACE");

        // FireRed's own format: a front picture and a palette file, and a build rule per overworld sprite
        Assert.Contains("graphics/trainers/front_pics/leader_juan_front_pic.png", plan.Binary.Keys);
        Assert.Contains("graphics/trainers/palettes/leader_juan.pal", plan.NewText.Keys);
        Assert.Contains("graphics/object_events/pics/people/roxanne.png", plan.Binary.Keys);
        Assert.Contains("graphics/object_events/pics/people/wallace.png", plan.Binary.Keys);
        var changed = plan.Changed;
        Assert.Contains("[TRAINER_PIC_LEADER_JUAN] =", changed[ProjectImport.TrainerGraphics], StringComparison.Ordinal);
        Assert.Contains("leader_juan_front_pic.4bpp.smol", changed[ProjectImport.TrainerGraphics], StringComparison.Ordinal);
        Assert.Contains("TRAINER_PIC_LEADER_JUAN, // Poke Studio import", changed[ProjectImport.TrainerConstants], StringComparison.Ordinal);
        Assert.Contains("OBJ_EVENT_GFX_ROXANNE, // Poke Studio import", changed[ProjectImport.ObjectConstants], StringComparison.Ordinal);
        Assert.Contains("[OBJ_EVENT_GFX_ROXANNE] = &gObjectEventGraphicsInfo_Roxanne", changed[ProjectImport.ObjectPointers], StringComparison.Ordinal);
        Assert.Contains(".paletteTag = OBJ_EVENT_PAL_TAG_NPC_1,", changed[ProjectImport.ObjectInfo], StringComparison.Ordinal);
        Assert.Contains("$(OBJEVENTGFXDIR)/people/roxanne.4bpp", changed[ProjectImport.SpriteRules], StringComparison.Ordinal);
        Assert.Contains("gObjectEventPic_Roxanne", changed[ProjectImport.ObjectGraphics], StringComparison.Ordinal);
        Assert.Contains("sPicTable_Roxanne", changed[ProjectImport.ObjectPics], StringComparison.Ordinal);

        // two imports in a row: each entry is whole and marked once
        var table = changed[ProjectImport.TrainerGraphics];
        foreach (var pic in new[] { "LeaderJuan", "ChampionWallace" })
        {
            Assert.Matches($@"\[TRAINER_PIC_\w+\] =\s*\{{\s*\.frontPic = TRAINER_FRONT_PIC\(gTrainerFrontPic_{pic}, gTrainerPalette_{pic}, TRAINER_PIC_SIZE\),\s*\}},", table);
        }

        Assert.DoesNotContain("Poke Studio import // Poke Studio import", table, StringComparison.Ordinal);
        Assert.Equal(table.Count(c => c == '{'), table.Count(c => c == '}'));

        // the same sprite twice is refused instead of written twice
        Assert.Throws<InvalidOperationException>(() => ProjectImport.AddLook(plan, em, "OBJ_EVENT_GFX_ROXANNE"));
    }

    private static async Task WholePack(string? root, string? other, CrossoverPack pack, int expectedImports)
    {
        if (root is null || other is null)
        {
            return;
        }

        using var tmp = new TempDir();
        var module = new ExpansionModule();
        var data = await module.LoadDataAsync(root, tmp.Combine("data"));
        var (pics, looks) = module.MissingSprites(data, pack.Pics, pack.Looks);
        Assert.Equal(expectedImports, pics.Count + looks.Count);
        Assert.Null(module.CheckImportSource(other, pics, looks));
        var imports = pics.Select(p => new StoryImport(StoryImport.Pic, p, other)).Concat(looks.Select(l => new StoryImport(StoryImport.Look, l, other))).ToList();

        var (change, notes, added) = CrossoverPacks.Build(data, pack, map => module.StorySpots(data, map), imports, module.Limits.TrainerNameLength);
        Assert.Empty(notes);
        Assert.Equal(pack.Entries.Count, added);
        Assert.Null(data.Story); // Build leaves the data as it was; the change does the rest
        change!.Apply();
        Assert.Equal(pack.Entries.Count, data.Story!.Battles.Count);

        // every battle is a major battle, in story order, and levels never go down along the way
        var caps = data.Rules!.LevelCaps;
        var story = caps.Where(c => c.Milestone.StartsWith("FLAG_PS_BEAT_", StringComparison.Ordinal)).ToList();
        Assert.Equal(pack.Entries.Count, story.Count);
        Assert.True(caps.Zip(caps.Skip(1)).All(p => p.Second.Level >= p.First.Level - 1), string.Join(", ", caps.Select(c => $"{c.Label} {c.Level}")));
        Assert.All(data.Story.Battles, b => Assert.NotEmpty(data.Trainers.Single(t => t.Key == b.TrainerKey).Party));
        Assert.All(data.Trainers.Where(t => t.Key.StartsWith(StoryBattles.TrainerPrefix, StringComparison.Ordinal)), t => Assert.True(t.Name.Length <= module.Limits.TrainerNameLength, t.Name));
        Assert.DoesNotContain(module.Validate(data), i => i.Severity == Validation.IssueSeverity.Error);

        // the challengers fight on their own terms and give a TM of their type, never the same one twice
        var trainers = data.Story.Battles.Select(b => (Battle: b, Trainer: data.Trainers.Single(t => t.Key == b.TrainerKey))).ToList();
        Assert.True(trainers.Count(x => x.Trainer.StartingStatus.Count > 0) >= 10, "most challengers get a field effect");
        Assert.All(trainers.SelectMany(x => x.Trainer.StartingStatus), s => Assert.Contains(data.Catalog.StartingStatuses, c => c.Key == s));
        var rewards = trainers.Select(x => x.Battle.Reward).OfType<string>().ToList();
        Assert.Equal(pack.Entries.Count(e => e.Role != PackRole.Champion), rewards.Count);
        Assert.Equal(rewards.Count, rewards.Distinct().Count());

        // the Champion is above everyone: at least one legendary and one mythical
        var champion = trainers.Single(x => pack.Entries.Single(e => e.Role == PackRole.Champion).Name.Equals(x.Trainer.Name, StringComparison.OrdinalIgnoreCase)).Trainer;
        var tags = champion.Party.Select(p => data.Species.Single(s => s.Key == p.Species).Tags).ToList();
        Assert.Contains(tags, t => (t & (Models.SpeciesTags.RestrictedLegendary | Models.SpeciesTags.SubLegendary)) != 0);
        Assert.Contains(tags, t => t.HasFlag(Models.SpeciesTags.Mythical));

        // and the Player's Guide warns about the effect and says what winning gives
        var guide = Guide.PlayersGuide.ToMarkdown(Guide.PlayersGuide.Build(data, data.Lab!, module.TrainerPlaces(data), new Guide.GuideOptions { Title = "T" }));
        Assert.Contains("The whole battle is fought with:", guide, StringComparison.Ordinal);
        Assert.Contains("Win to get the", guide, StringComparison.Ordinal);

        change.Revert();
        Assert.True(data.Story is null || data.Story.Battles.Count == 0);
    }

    [Fact]
    public Task Real_firered_takes_the_whole_hoenn_pack_when_available() =>
        WholePack(Dir("POKESTUDIO_FIRERED_DIR"), Dir("POKESTUDIO_EXPANSION_DIR"), CrossoverPacks.HoennInKanto, 15);

    [Fact]
    public Task Real_emerald_takes_the_whole_kanto_pack_when_available() =>
        WholePack(Dir("POKESTUDIO_EXPANSION_DIR"), Dir("POKESTUDIO_FIRERED_DIR"), CrossoverPacks.KantoInHoenn, 0);
}
