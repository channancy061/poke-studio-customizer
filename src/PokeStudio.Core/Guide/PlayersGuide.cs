using System.Globalization;
using System.Text;
using PokeStudio.Core.ChampionLab;
using PokeStudio.Core.GameOptions;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;
using PokeStudio.Core.Progression;

namespace PokeStudio.Core.Guide;

public abstract record GuideBlock;

public sealed record GuideHeading(int Level, string Text, string? Anchor = null) : GuideBlock;

public sealed record GuideText(string Text) : GuideBlock;

/// <summary>A highlighted tip for the player.</summary>
public sealed record GuideTip(string Text) : GuideBlock;

public sealed record GuideList(IReadOnlyList<string> Items) : GuideBlock;

public sealed record GuideTable(IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows) : GuideBlock;

public sealed record GuideDocument(string Title, IReadOnlyList<(string Title, string Anchor)> Contents, IReadOnlyList<GuideBlock> Blocks);

/// <summary>What goes into the guide.</summary>
public sealed record GuideOptions
{
    public string Title { get; init; } = "";
    public bool StartOptions { get; init; } = true;
    public bool LevelCaps { get; init; } = true;
    public bool Bosses { get; init; } = true;
    public bool OtherTrainers { get; init; }
    public bool Wild { get; init; } = true;
    public bool Shops { get; init; } = true;
    public bool Evolutions { get; init; } = true;
}

/// <summary>
/// The Player's Guide: a plain-language walkthrough of the hack built from its own data, for players who want help —
/// boss teams (with weaknesses and what to bring), level caps, where to catch Pokémon, shops, special evolutions and the
/// new-game options. Adaptive boss slots are shown as "?" with their candidates.
/// </summary>
public static class PlayersGuide
{
    public static GuideDocument Build(GameData data, LabSettings lab, IReadOnlyDictionary<string, string> trainerPlaces, GuideOptions o)
    {
        var blocks = new List<GuideBlock>();
        var contents = new List<(string, string)>();
        void Section(string title, string anchor)
        {
            contents.Add((title, anchor));
            blocks.Add(new GuideHeading(2, title, anchor));
        }

        string Species(string key) => data.Species.FirstOrDefault(s => s.Key == key)?.DisplayName ?? GameCatalog.Prettify(key.Replace("SPECIES_", "", StringComparison.Ordinal));
        string Name(string key) => data.Catalog.NameOf(key);
        string Item(string key) => data.Items.FirstOrDefault(i => i.Key == key)?.Name ?? Name(key);
        string Types(string species) => string.Join("/", (data.Species.FirstOrDefault(s => s.Key == species)?.Types ?? []).Distinct().Select(TypeName));

        var title = o.Title.Length > 0 ? o.Title : "Player's Guide";
        blocks.Add(new GuideText("This guide shows what's waiting for you: every important battle, where to catch Pokémon, what the shops sell and how special Pokémon evolve. Use as much or as little of it as you like."));

        // ---- starting a new game ----
        if (o.StartOptions && data.GameOptions is { AnyUsed: true } options)
        {
            Section("Starting a new game", "start");
            blocks.AddRange(StartBlocks(options));
        }

        // ---- level caps ----
        var caps = data.Rules?.LevelCaps ?? [];
        if (o.LevelCaps && caps.Count > 0)
        {
            Section("Level caps", "caps");
            blocks.Add(new GuideText(data.Rules!.ExpCap == ExpCapKind.Hard || data.GameOptions?.LevelCapCodeUsed == true
                ? "Your Pokémon stop gaining experience at the cap for the next important battle (when the level cap is on). Here are the caps:"
                : "The highest level of each important battle, so you know what to aim for:"));
            // "Gym 1" names its leader ("Gym 1 (Brock)")
            var leaders = EncounterPlanner.Check(data, lab).ToDictionary(r => r.Index);
            blocks.Add(new GuideTable(["Before", "Level"], caps.Select((c, i) => (IReadOnlyList<string>)[
                leaders.TryGetValue(i, out var r) && r.Bosses.Count > 0 && !r.Bosses.Contains(c.Label, StringComparer.OrdinalIgnoreCase) ? $"{c.Label} ({string.Join(", ", r.Bosses)})" : c.Label,
                c.Level.ToString(CultureInfo.InvariantCulture)]).ToList()));
        }

        // ---- bosses ----
        if (o.Bosses)
        {
            Section("Important battles", "bosses");
            blocks.Add(new GuideText("In story order. Each team shows the Pokémon, their levels, abilities, held items, natures and moves. \"Bring\" lists the types that hit most of the team hard."));

            // the adaptive slot is only real when the game has the option (decomp projects with it switched on)
            var adaptiveOn = data.AdaptiveSlotsAlwaysOn || data.GameOptions?.AdaptiveSlot is OptionMode.PlayerChooses or OptionMode.AlwaysOn;
            if (adaptiveOn && data.Trainers.Any(t => t.HasAdaptiveSlot && !t.NeverFought))
            {
                blocks.Add(new GuideTip("Adaptive bosses: gym leaders, your rival and the Elite Four keep one Pokémon secret (marked ?). When the battle starts it becomes "
                    + "whichever of its candidates best counters your current team, so every run can be different. The candidates are listed under each team."
                    + (!data.AdaptiveSlotsAlwaysOn && data.GameOptions?.AdaptiveSlot == OptionMode.PlayerChooses ? " This only happens if you said yes to adaptive bosses when starting the game." : "")));
            }

            // story order: by milestone when the game has level caps, otherwise by the strongest Pokémon's level
            var bosses = data.Trainers
                .Where(t => Lab.DifficultyOf(t) == Difficulty.Normal && t.Party.Count > 0 && !t.NeverFought)
                .Select(t => (Trainer: t, Group: Lab.GroupOf(data, lab, t)))
                .Where(x => x.Group != TrainerGroup.Regular || o.OtherTrainers)
                .OrderBy(x => Lab.MilestoneOf(data, x.Trainer))
                .ThenBy(x => x.Trainer.Party.Max(p => p.Level))
                .ThenBy(x => x.Group)
                .ThenBy(x => x.Trainer.Id)
                .ToList();

            // one fight with a team per starter (the rival): same trainer, place, size and top level, side by side
            string PlaceOf(Trainer t) => trainerPlaces.TryGetValue(Lab.BaseKey(t), out var pl) ? pl : "";
            bool SameFight(Trainer a, Trainer b) => a.Name == b.Name && a.TrainerClass == b.TrainerClass && PlaceOf(a) == PlaceOf(b)
                && a.Party.Count == b.Party.Count && a.Party.Max(p => p.Level) == b.Party.Max(p => p.Level);
            var fights = new List<List<(Trainer Trainer, TrainerGroup Group)>>();
            foreach (var x in bosses)
            {
                if (fights.Count > 0 && fights[^1].Count < 3 && SameFight(fights[^1][0].Trainer, x.Trainer))
                {
                    fights[^1].Add(x);
                }
                else
                {
                    fights.Add([x]);
                }
            }

            foreach (var fight in fights)
            {
                var (first, group) = fight[0];
                // the GBA games' small "PKMN" letters read as Pokémon in the guide
                var label = $"{Name(first.TrainerClass).Replace("ᴾᴷᴹᴺ", "Pokémon", StringComparison.Ordinal)} {GameCatalog.Prettify(first.Name)}".Trim()
                    + (Lab.IsRematch(data, first) ? " (rematch)" : "");
                blocks.Add(new GuideHeading(3, label, Anchor(first.Key)));
                var where = PlaceOf(first) is { Length: > 0 } place ? $"Where: {place}. " : "";
                var kind = first.Battle == BattleType.Double ? "Double battle. " : "";
                var theme = lab.Themes.TryGetValue(Lab.BaseKey(first), out var th) ? $"Type: {TypeName(th.Type)}. " : "";
                var variants = fight.Count > 1 ? $"Their team depends on the starter you picked: they take the one that's strong against yours ({fight.Count} versions below). " : "";
                // story battles: the field effect that lasts the whole fight, and what winning gives
                var field = first.StartingStatus.Count > 0 ? $"The whole battle is fought with: {string.Join(", ", first.StartingStatus.Select(Story.StoryEffects.Label))}. " : "";
                var reward = data.Story?.Battles.FirstOrDefault(b => b.TrainerKey == Lab.BaseKey(first))?.Reward is { } item
                    ? $"Win to get the {Name(item.Replace("ITEM_TM_", "MOVE_", StringComparison.Ordinal))} TM. " : "";
                var legends = data.Story?.Legends.Where(l => l.RevealedBy == Lab.BaseKey(first))
                    .Select(l => $"{data.Species.FirstOrDefault(sp => sp.Key == l.Species)?.Name ?? l.Species} (level {l.Level}) in {l.Place}").ToList() ?? [];
                var reveals = legends.Count > 0 ? $"Win and they tell you where to find {string.Join(" and ", legends)}. " : "";
                blocks.Add(new GuideText($"{Lab.Label(group)}. {where}{kind}{theme}{field}{reward}{reveals}{variants}".Trim()));
                foreach (var (t, _) in fight)
                {
                    if (fight.Count > 1)
                    {
                        // name each version by the Pokémon only it has (the rival's starter line)
                        var own = t.Party.Select(p => p.Species)
                            .Where(sp => fight.Where(o2 => !ReferenceEquals(o2.Trainer, t)).All(o2 => o2.Trainer.Party.All(p => p.Species != sp)))
                            .Distinct().Select(Species).ToList();
                        blocks.Add(new GuideHeading(4, own.Count > 0 ? $"With {string.Join(", ", own)}" : $"Version {fight.IndexOf((t, group)) + 1}"));
                    }

                    blocks.Add(TeamTable(data, t, Species, Name, Item, Types, adaptiveOn));
                    if (adaptiveOn && t.HasAdaptiveSlot)
                    {
                        var choices = string.Join(", ", new[] { t.Party[t.FlexIndex!.Value] }.Concat(t.FlexCandidates).Select(c => Species(c.Species)));
                        blocks.Add(new GuideTip($"? — one Pokémon (shown as \"{Species(t.Party[t.FlexIndex.Value].Species)} ?\") counters your team: one of {choices}."));
                    }

                    blocks.Add(new GuideTip(Advice(data, t)));
                    foreach (var version in data.Trainers.Where(v => Lab.BaseKey(v) == t.Key && v.Key != t.Key).OrderBy(Lab.DifficultyOf))
                    {
                        blocks.Add(new GuideHeading(4, $"{label} — {Lab.Label(Lab.DifficultyOf(version))}"));
                        blocks.Add(TeamTable(data, version, Species, Name, Item, Types, adaptiveOn));
                    }
                }
            }
        }

        // ---- the final chapter ----
        if (o.Bosses && data.Story?.Finale is { } finale && data.Trainers.Any(t => t.Key == finale.Guide))
        {
            Section("After the Champion", "finale");
            var nuzlocke = data.GameOptions is { } go && go.NuzRuleUsed(go.NuzFaintedGone);
            blocks.Add(new GuideText($"Becoming Champion isn't the end. {finale.Regions} are still collapsing into each other, and the credits wait until that's stopped."));
            blocks.Add(new GuideList(
            [
                $"After the Hall of Fame, {finale.GuideName} is waiting at home and asks you to meet at {finale.MeetName}.",
                $"There {finale.GuideName} battles you first (see the team above), above the Champion's level. The level cap goes up to match.",
                $"Win and {finale.GuideName} takes you to {finale.IslandName}. You can heal and change your team before you say you're ready.",
                $"Then {finale.BossName}'s {finale.Forms.Count} forms attack one after another, at level {finale.FormLevel}, with no healing in between. Their order and moves are chosen against your team, so every form brings moves that hit you hard. Bring Pokémon that cover each other's weaknesses.",
                $"After the last form, {finale.BossName} (level {finale.CatchLevel}) has to be caught to stop the collapse. Bring plenty of Poké Balls. If it gets away, talk to {finale.GuideName} to try again.",
                nuzlocke
                    ? $"If your team falls to {finale.BossName}: without the Nuzlocke, try again from {finale.GuideName}; in a Nuzlocke, the run is over."
                    : $"If your team falls to {finale.BossName}, try again from {finale.GuideName}.",
                "Once it's caught, the regions separate, the visiting challengers go home, and the credits roll.",
            ]));
        }

        // ---- where to catch: in playthrough order, chapter by chapter up to each major battle ----
        if (o.Wild && data.Encounters.Count > 0)
        {
            Section("Where to catch Pokémon", "wild");
            var next = EncounterPlanner.NextBattles(data);
            var areas = EncounterPlanner.Areas(data);
            var ordered = StoryOrder.For(data) is not null;
            blocks.Add(new GuideText(ordered
                ? "In the order you reach each place, chapter by chapter up to each important battle. Each chapter starts with good catches for that battle. A way of meeting Pokémon marked \"later\" opens up further on (a fishing rod, Surf or Rock Smash)."
                : "Every place with wild Pokémon, how you meet them and how often (the chance of each Pokémon when you meet one)."));
            var readiness = caps.Count > 0 ? EncounterPlanner.Check(data, lab).ToDictionary(r => r.Index) : [];
            var nights = data.GameOptions?.DayAndNight == true ? EncounterPlanner.NightTables(data, lab).ToDictionary(t => t.Key, StringComparer.Ordinal) : [];
            if (nights.Count > 0)
            {
                blocks.Add(new GuideText("At night about a third of the grass and cave Pokémon change: those rows say \"at night\"."));
            }

            // the chapter of each place: the first important battle after it (from the grass, before any rod or Surf);
            // a chapter leads up to every battle until the next chapter's places (Roxanne and Brock both in Pewter City)
            int ChapterOf(IReadOnlyList<EncounterTable> t, string m) =>
                caps.Count > 0 ? next(m, t.SelectMany(x => x.Methods).Where(x => x.Slots.Count > 0).Min(x => x.Slots.Min(y => y.MinLevel)), "land", 0) : -1;
            var chapters = areas.Select(a => ChapterOf(a.Tables, a.Map)).ToList();
            var chapter = -2;
            for (var ai = 0; ai < areas.Count; ai++)
            {
                var (map, tables, level, _) = areas[ai];
                var here = chapters[ai];
                if (here != chapter && caps.Count > 0)
                {
                    chapter = here;
                    var until = chapters.Skip(ai).FirstOrDefault(c => c > here, caps.Count);
                    var battles = Enumerable.Range(here, Math.Max(1, until - here)).Select(i =>
                        readiness.TryGetValue(i, out var b) && b.Bosses.Count > 0 && caps[i].Label is var l && !b.Bosses.Contains(l, StringComparer.OrdinalIgnoreCase)
                            ? $"{l} ({string.Join(", ", b.Bosses)})" : caps[i].Label).ToList();
                    blocks.Add(new GuideHeading(3, "Before " + (battles.Count == 1 ? battles[0] : string.Join(", ", battles[..^1]) + " and " + battles[^1])));
                    if (readiness.TryGetValue(here, out var r) && r.Counters.Count > 0)
                    {
                        blocks.Add(new GuideTip($"Good catches for {caps[here].Label} ({string.Join("/", r.MainTypes.Select(TypeName))}): " + string.Join(", ", r.Counters.Take(8).Select(c => $"{Species(c.Species)} ({c.Area})")) + "."));
                    }
                }

                var table = tables.OrderByDescending(t => t.Methods.Sum(m => m.Slots.Count)).First();
                blocks.Add(new GuideHeading(caps.Count > 0 ? 4 : 3, StoryOrder.For(data) is null ? Validation.GameValidator.PlaceName(table) : StoryOrder.AreaName(map)));
                var rows = new List<IReadOnlyList<string>>();
                foreach (var method in table.Methods.Where(m => m.Slots.Count > 0))
                {
                    var rates = data.EncounterChances.FirstOrDefault(c => c.Type == method.Type);
                    var chances = rates?.Percent ?? [];
                    var slots = method.Slots.Select((s, i) => (s, i, chance: i < chances.Count ? chances[i] : 0)).ToList();
                    // fishing has a group of slots per rod, each adding up to 100% on its own
                    var groups = rates?.Groups is { Count: > 0 } g0
                        ? g0.Select(kv => (Name: GameCatalog.Prettify(kv.Key.ToUpperInvariant()), Slots: slots.Where(x => kv.Value.Contains(x.i)).ToList())).ToList()
                        : [(Name: MethodName(method.Type), Slots: slots)];
                    foreach (var group in groups)
                    {
                        var bySpecies = group.Slots
                            .GroupBy(x => x.s.Species)
                            .Select(g => (Species: g.Key, Min: g.Min(x => x.s.MinLevel), Max: g.Max(x => x.s.MaxLevel), Chance: g.Sum(x => x.chance),
                                Later: caps.Count > 0 && g.Min(x => next(map, x.s.MinLevel, method.Type, x.i)) > here))
                            .OrderByDescending(x => x.Chance).ThenBy(x => x.Species, StringComparer.Ordinal);
                        foreach (var x in bySpecies)
                        {
                            rows.Add([group.Name + (x.Later ? " (later)" : ""), Species(x.Species), Types(x.Species), x.Min == x.Max ? $"{x.Min}" : $"{x.Min}–{x.Max}", x.Chance > 0 ? $"{x.Chance}%" : ""]);
                        }
                    }
                }

                // the night Pokémon (the slots they take over keep their chances)
                if (nights.TryGetValue(table.Key + "_Night", out var night) && night.Methods.FirstOrDefault() is { } nightLand)
                {
                    var landChances = data.EncounterChances.FirstOrDefault(c => c.Type == nightLand.Type)?.Percent ?? [];
                    var daySpecies = table.Methods.Where(m => m.Type == nightLand.Type).SelectMany(m => m.Slots).Select(x => x.Species).ToHashSet(StringComparer.Ordinal);
                    foreach (var g in nightLand.Slots.Select((x, i) => (x, chance: i < landChances.Count ? landChances[i] : 0)).Where(y => !daySpecies.Contains(y.x.Species)).GroupBy(y => y.x.Species))
                    {
                        var min = g.Min(y => y.x.MinLevel);
                        var max = g.Max(y => y.x.MaxLevel);
                        rows.Add([MethodName(nightLand.Type) + " at night", Species(g.Key), Types(g.Key), min == max ? $"{min}" : $"{min}–{max}", $"{g.Sum(y => y.chance)}%"]);
                    }
                }

                blocks.Add(new GuideTable(["How", "Pokémon", "Type", "Level", "Chance"], rows));
            }
        }

        // ---- shops ----
        if (o.Shops && data.Marts.Count > 0)
        {
            Section("Shops", "shops");
            foreach (var mart in data.Marts.OrderBy(m => m.Place, StringComparer.Ordinal))
            {
                blocks.Add(new GuideHeading(3, mart.Place.Length > 0 ? mart.Place : mart.Key));
                var rows = mart.Items.Select(i => (IReadOnlyList<string>)[Item(i), Price(data, i), "From the start"]).ToList();
                foreach (var (badge, items) in mart.BadgeTiers.OrderBy(kv => kv.Key))
                {
                    rows.AddRange(items.Select(i => (IReadOnlyList<string>)[Item(i), Price(data, i), $"After badge {badge}"]));
                }

                blocks.Add(new GuideTable(["Item", "Price", "Available"], rows));
            }
        }

        // ---- special evolutions ----
        if (o.Evolutions)
        {
            var special = data.Evolutions
                .SelectMany(set => set.Evolutions.Select(e => (set.FromSpecies, Evolution: e)))
                .Where(x => !(x.Evolution.Method == "EVO_LEVEL" && x.Evolution.Parameter != "0" && x.Evolution.Conditions.Count == 0))
                .Where(x => data.Species.Any(s => s.Key == x.FromSpecies && s.IsEnabled) && data.Species.Any(s => s.Key == x.Evolution.TargetSpecies && s.IsEnabled))
                .OrderBy(x => Species(x.FromSpecies), StringComparer.Ordinal)
                .ToList();
            if (special.Count > 0)
            {
                Section("How special Pokémon evolve", "evolutions");
                blocks.Add(new GuideText("Pokémon that don't simply evolve at a level:"));
                blocks.Add(new GuideTable(["Pokémon", "Evolves into", "How"],
                    special.Select(x => (IReadOnlyList<string>)[Species(x.FromSpecies), Species(x.Evolution.TargetSpecies), EvolutionFixer.Describe(data, x.Evolution)]).ToList()));
            }
        }

        return new GuideDocument(title, contents, blocks);
    }

    private static string Price(GameData data, string item) =>
        data.Items.FirstOrDefault(i => i.Key == item) is { Price: > 0 } i ? "¥" + i.Price.ToString("N0", CultureInfo.InvariantCulture) : "";

    private static string TypeName(string type) => GameCatalog.Prettify(type.Replace("TYPE_", "", StringComparison.Ordinal));

    private static string MethodName(string type) => type switch
    {
        "land_mons" => "Grass / cave",
        "water_mons" => "Surfing",
        "rock_smash_mons" => "Rock Smash",
        "fishing_mons" => "Fishing",
        "hidden_mons" => "Hidden",
        _ => GameCatalog.Prettify(type.Replace("_mons", "", StringComparison.Ordinal).ToUpperInvariant()),
    };

    private static string Anchor(string key) => key.ToLowerInvariant().Replace('_', '-').Replace("@", "-", StringComparison.Ordinal);

    /// <summary>The moves a Pokémon knows at a level when none are set: the last four it learned by levelling up.</summary>
    private static IReadOnlyList<string> DefaultMoves(GameData data, Species species, int level) =>
        data.LearnsetFor(species) is not { } learnset ? []
        : learnset.LevelUp.Where(m => m.Level <= level && m.Move is not ("" or "MOVE_NONE")).Select(m => m.Move).Reverse().Distinct().Take(4).Reverse().ToList();

    private static GuideTable TeamTable(GameData data, Trainer t, Func<string, string> species, Func<string, string> name, Func<string, string> item, Func<string, string> types,
        bool adaptiveOn = true)
    {
        var rows = new List<IReadOnlyList<string>>();
        for (var i = 0; i < t.Party.Count; i++)
        {
            var p = t.Party[i];
            var flex = adaptiveOn && t.HasAdaptiveSlot && i == t.FlexIndex;
            // what the game uses when the team doesn't say: one of the Pokémon's abilities, its last four level-up
            // moves by that level, a random nature, no item
            var sp = data.Species.FirstOrDefault(s => s.Key == p.Species);
            var abilities = sp?.Abilities.Where(x => x is not ("" or "ABILITY_NONE")).Distinct().Select(name).ToList() ?? [];
            var moves = p.Moves.Count > 0 ? p.Moves : sp is null ? [] : DefaultMoves(data, sp, p.Level);
            rows.Add([
                species(p.Species) + (flex ? " ?" : ""),
                types(p.Species),
                p.Level.ToString(CultureInfo.InvariantCulture),
                p.Ability is { } a ? name(a) : abilities.Count > 0 ? string.Join(" or ", abilities) : "",
                p.HeldItem is { } h && h != "ITEM_NONE" ? item(h) : "None",
                p.Nature is { } n ? name(n) : "Random",
                string.Join(", ", moves.Select(name)),
            ]);
        }

        return new GuideTable(["Pokémon", "Type", "Lv", "Ability", "Item", "Nature", "Moves"], rows);
    }

    /// <summary>Which types hit most of the team super-effectively, and what the team hits hard.</summary>
    private static string Advice(GameData data, Trainer t)
    {
        var team = t.Party.Select(p => data.Species.FirstOrDefault(s => s.Key == p.Species)).Where(s => s is not null).Select(s => s!.Types).ToList();
        if (team.Count == 0)
        {
            return "";
        }

        var bring = TypeChart.Types
            .Select(type => (type, weak: team.Count(ts => TypeChart.Multiplier(type, ts) > 1), resist: team.Count(ts => TypeChart.Multiplier(type, ts) < 1)))
            .Where(x => x.weak > 0)
            .OrderByDescending(x => x.weak - x.resist).ThenByDescending(x => x.weak)
            .Take(3)
            .Select(x => TypeName(x.type))
            .ToList();
        var theirs = team.SelectMany(ts => ts).GroupBy(x => x).OrderByDescending(g => g.Count()).Take(3).Select(g => TypeName(g.Key)).ToList();
        return $"Bring: {(bring.Count > 0 ? string.Join(", ", bring) : "anything strong")}. Watch out for their {string.Join(", ", theirs)} moves.";
    }

    private static IEnumerable<GuideBlock> StartBlocks(GameOptionsSettings o)
    {
        yield return new GuideTip("When you start a new game you're asked a few questions. Read each one — don't keep pressing A! Yes is picked first; press B to answer No. You can't change your answers later in that save.");
        var items = new List<string>();
        void Add(OptionMode mode, string what, string asked, string always, string? off = null)
        {
            switch (mode)
            {
                case OptionMode.PlayerChooses: items.Add($"{what}: you choose. {asked}"); break;
                case OptionMode.AlwaysOn: items.Add($"{what}: always on. {always}"); break;
                case OptionMode.AlwaysOff when off is not null: items.Add($"{what}: {off}"); break;
            }
        }

        if (o.Difficulty == OptionMode.PlayerChooses)
        {
            items.Add($"Difficulty: you choose Easy, Normal, Hard{(o.OfferExtraHard ? $" or {o.HardestName}" : "")}. It changes the important trainers' teams (see each battle below).");
        }
        else if (o.Difficulty == OptionMode.AlwaysOn)
        {
            items.Add($"Difficulty: always {(o.FixedDifficulty == DifficultyChoice.ExtraHard ? o.HardestName : o.FixedDifficulty.ToString())}.");
        }

        if (o.TrainerRulesByDifficulty && o.Difficulty != OptionMode.NotUsed)
        {
            items.Add("    • Easy: only important battles stop you. Other trainers battle only if you talk to them.");
            items.Add("    • Normal: trainers behave as in the original game.");
            items.Add("    • Hard: every trainer spots you from far away and in every direction, so you can't sneak past. A Gym Leader only battles you once every trainer in their Gym is beaten.");
            if (o.OfferExtraHard)
            {
                items.Add($"    • {o.HardestName}: as Hard, and every trainer has six Pokémon at the level cap, fully trained with held items, covering each other's weaknesses, and battling with the smartest AI.");
            }
        }

        Add(o.TrainingOff, "Remove EVs and IVs", "Say yes and every Pokémon (yours, wild ones and every trainer's) counts as having perfect IVs and no EVs, so there's nothing to train and the Pokémon Center helper skips IVs and EVs. Say no for the usual rules: EVs from battles, vitamins and the helper all count. (Hidden Power's type always follows a Pokémon's own IVs.)",
            "Every Pokémon (yours, wild ones and every trainer's) counts as having perfect IVs and no EVs, so there's nothing to train. (Hidden Power's type follows a Pokémon's own IVs.)");
        Add(o.LevelCap, "Hard level cap", "Say yes and your Pokémon stop at the cap for the next important battle.", "Your Pokémon stop at the cap for the next important battle.", "off - no level cap.");
        Add(o.NoItemsInBattle, "No items in battle", o.NoItemsAlsoWild ? "Say yes and you can't use your bag in any battle." : "Say yes and you can't use your bag in trainer battles.",
            o.NoItemsAlsoWild ? "You can't use your bag in any battle." : "You can't use your bag in trainer battles.");
        Add(o.InfiniteCandy, "Infinite Candy", "Say yes to get a Rare Candy that never runs out (it stops at the level cap).", "You get a Rare Candy that never runs out (it stops at the level cap).");
        Add(o.AdaptiveSlot, "Adaptive bosses", "Say yes and gym leaders, your rival and the Elite Four swap one Pokémon to counter your team (marked ? below).",
            "Gym leaders, your rival and the Elite Four swap one Pokémon to counter your team (marked ? below).");
        if (o.Nuzlocke is OptionMode.PlayerChooses or OptionMode.AlwaysOn)
        {
            items.Add(o.Nuzlocke == OptionMode.PlayerChooses ? "Nuzlocke: you choose. If you say yes, these rules follow:" : "Nuzlocke: always on, with these rules:");
            void Rule(OptionMode mode, string text)
            {
                if (mode is OptionMode.AlwaysOn or OptionMode.PlayerChooses)
                {
                    items.Add($"    • {text}{(mode == OptionMode.PlayerChooses ? " (you choose)" : "")}");
                }
            }

            Rule(o.NuzFirstEncounter, "Only the first wild Pokémon you meet in each area can be caught, counting from when you have Poké Balls. In a wild double battle, knock one out and catch the other.");
            Rule(o.NuzFaintedGone, "A Pokémon that faints is gone for good - no revives.");
            if (o.NuzFaintedGone is OptionMode.AlwaysOn or OptionMode.PlayerChooses)
            {
                items.Add("    • If your whole team faints, you choose at the start: go on with healthy Pokémon from your PC, or the run is over. No battle starts while nobody in your party can fight.");
            }

            Rule(o.NuzDupes, "Duplicates clause: a Pokémon you already caught (or its evolution family) doesn't use up the area.");
            Rule(o.NuzEeveeException, "Eevee clause: Eevee never counts as a duplicate.");
            Rule(o.NuzShiny, "Shiny clause: shiny Pokémon can always be caught.");
            Rule(o.NuzStatic, "Static encounters count: legendaries and Pokémon you walk up to use up the area too. Saying yes asks you again, because a legendary could be lost.");
            Rule(o.NuzNicknames, "Every Pokémon you catch needs a nickname (not its species name).");
            Rule(o.NuzLevelCap, "The level cap is on.");
        }

        if (o.CenterHelper.AnyService)
        {
            var services = new List<string>();
            if (o.CenterHelper.Nature) { services.Add("change a Pokémon's nature" + Cost(o.CenterHelper.NaturePrice)); }
            if (o.CenterHelper.PerfectIvs) { services.Add("give perfect IVs" + Cost(o.CenterHelper.IvPrice)); }
            if (o.CenterHelper.EvSpreads) { services.Add("set its EVs" + Cost(o.CenterHelper.EvPrice)); }
            items.Add("In every Pokémon Center, the helper can " + string.Join(", ", services) + ".");
        }

        yield return new GuideList(items);
    }

    private static string Cost(int price) => price > 0 ? $" (¥{price.ToString("N0", CultureInfo.InvariantCulture)})" : " (free)";

    // ---------- rendering ----------

    public static string ToHtml(GuideDocument d)
    {
        // only the characters HTML needs escaped: names like "Pokémon" stay readable in the file
        static string E(string s) => s.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal);
        var sb = new StringBuilder();
        sb.Append("<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        sb.Append("<title>").Append(E(d.Title)).Append("</title>\n<style>\n");
        sb.Append("""
            :root { --bg:#ffffff; --fg:#1d1d24; --muted:#5d5d6e; --line:#dcdce6; --tip:#eef3ff; --tipline:#5b6cff; --head:#f4f4f8; }
            @media (prefers-color-scheme: dark) { :root { --bg:#14141b; --fg:#ececf2; --muted:#a0a0b4; --line:#33334a; --tip:#1e2440; --tipline:#8190ff; --head:#1c1c27; } }
            * { box-sizing: border-box; }
            body { margin: 0 auto; max-width: 1100px; padding: 24px 16px 64px; background: var(--bg); color: var(--fg); font: 15px/1.5 system-ui, "Segoe UI", sans-serif; }
            h1 { font-size: 2em; margin: 0 0 8px; } h2 { margin-top: 40px; border-bottom: 2px solid var(--line); padding-bottom: 4px; } h3 { margin: 28px 0 6px; } h4 { margin: 16px 0 6px; color: var(--muted); }
            nav ol { columns: 2; padding-left: 20px; } a { color: var(--tipline); }
            .tip { background: var(--tip); border-left: 4px solid var(--tipline); padding: 8px 12px; border-radius: 6px; margin: 8px 0; }
            .wrap { overflow-x: auto; } table { border-collapse: collapse; width: 100%; margin: 6px 0 10px; font-size: 14px; }
            th, td { border: 1px solid var(--line); padding: 4px 8px; text-align: left; vertical-align: top; } th { background: var(--head); }
            ul { padding-left: 22px; } li.sub { list-style: none; margin-left: 12px; }
            footer { margin-top: 48px; color: var(--muted); font-size: 13px; }
            details > summary { cursor: pointer; list-style: none; } details > summary::-webkit-details-marker { display: none; }
            details > summary > * { display: inline; } details > summary::before { content: "\25B8"; display: inline-block; width: 1.2em; color: var(--muted); transition: transform .15s; }
            details[open] > summary::before { transform: rotate(90deg); }
            details.sec > summary > h2 { display: inline-block; border: 0; margin: 0; } details.sec { margin-top: 40px; border-bottom: 2px solid var(--line); padding-bottom: 8px; }
            details.item { margin: 18px 0 6px; } details.item > summary > h3 { margin: 0; }
            .tools { margin: 12px 0; display: flex; gap: 8px; } .tools button { font: inherit; padding: 4px 12px; border-radius: 6px; border: 1px solid var(--line); background: var(--head); color: var(--fg); cursor: pointer; }
            @media print { body { max-width: none; } details.sec { break-before: page; } table { font-size: 11px; } .tools { display: none; } details > summary::before { display: none; } }

            """);
        sb.Append("</style>\n</head>\n<body>\n<h1>").Append(E(d.Title)).Append("</h1>\n");
        sb.Append("<div class=\"tools\"><button type=\"button\" onclick=\"setAll(true)\">Expand all</button><button type=\"button\" onclick=\"setAll(false)\">Collapse all</button></div>\n");
        if (d.Contents.Count > 0)
        {
            sb.Append("<nav><strong>Contents</strong><ol>\n");
            foreach (var (t, a) in d.Contents)
            {
                sb.Append("<li><a href=\"#").Append(a).Append("\">").Append(E(t)).Append("</a></li>\n");
            }

            sb.Append("</ol></nav>\n");
        }

        // sections (h2) and their entries (h3) fold away; the contents links open what they point to
        var inSection = false;
        var inItem = false;
        void CloseItem()
        {
            if (inItem)
            {
                sb.Append("</details>\n");
                inItem = false;
            }
        }

        foreach (var b in d.Blocks)
        {
            switch (b)
            {
                case GuideHeading { Level: 2 } h:
                    CloseItem();
                    if (inSection)
                    {
                        sb.Append("</details>\n");
                    }

                    sb.Append(CultureInfo.InvariantCulture, $"<details class=\"sec\" open{(h.Anchor is null ? "" : $" id=\"{h.Anchor}\"")}><summary><h2>{E(h.Text)}</h2></summary>\n");
                    inSection = true;
                    break;
                case GuideHeading { Level: 3 } h:
                    CloseItem();
                    sb.Append(CultureInfo.InvariantCulture, $"<details class=\"item\" open{(h.Anchor is null ? "" : $" id=\"{h.Anchor}\"")}><summary><h3>{E(h.Text)}</h3></summary>\n");
                    inItem = true;
                    break;
                case GuideHeading h:
                    sb.Append(CultureInfo.InvariantCulture, $"<h{h.Level}{(h.Anchor is null ? "" : $" id=\"{h.Anchor}\"")}>{E(h.Text)}</h{h.Level}>\n");
                    break;
                case GuideText t when t.Text.Length > 0:
                    sb.Append("<p>").Append(E(t.Text)).Append("</p>\n");
                    break;
                case GuideTip t when t.Text.Length > 0:
                    sb.Append("<div class=\"tip\">").Append(E(t.Text)).Append("</div>\n");
                    break;
                case GuideList l:
                    sb.Append("<ul>\n");
                    foreach (var item in l.Items)
                    {
                        var sub = item.StartsWith("    • ", StringComparison.Ordinal);
                        sb.Append(sub ? "<li class=\"sub\">• " : "<li>").Append(E(sub ? item[6..] : item)).Append("</li>\n");
                    }

                    sb.Append("</ul>\n");
                    break;
                case GuideTable t:
                    sb.Append("<div class=\"wrap\"><table>\n<tr>");
                    foreach (var h in t.Headers)
                    {
                        sb.Append("<th>").Append(E(h)).Append("</th>");
                    }

                    sb.Append("</tr>\n");
                    foreach (var row in t.Rows)
                    {
                        sb.Append("<tr>");
                        foreach (var cell in row)
                        {
                            sb.Append("<td>").Append(E(cell)).Append("</td>");
                        }

                        sb.Append("</tr>\n");
                    }

                    sb.Append("</table></div>\n");
                    break;
            }
        }

        CloseItem();
        if (inSection)
        {
            sb.Append("</details>\n");
        }

        // expand/collapse all; a contents link opens its section; printing shows everything
        sb.Append("""
            <script>
            function setAll(open) { document.querySelectorAll('details').forEach(function (d) { d.open = open; }); }
            function reveal() { var t = location.hash && document.getElementById(location.hash.slice(1)); for (var e = t; e; e = e.parentElement) { if (e.tagName === 'DETAILS') e.open = true; } if (t) t.scrollIntoView(); }
            window.addEventListener('hashchange', reveal); reveal();
            window.addEventListener('beforeprint', function () { setAll(true); });
            </script>

            """);

        sb.Append("<footer>Made with Poke Studio.</footer>\n</body>\n</html>\n");
        return sb.ToString();
    }

    public static string ToMarkdown(GuideDocument d)
    {
        static string Cell(string s) => s.Replace("|", "\\|", StringComparison.Ordinal);
        var sb = new StringBuilder();
        sb.Append("# ").Append(d.Title).Append("\n\n");
        foreach (var (t, a) in d.Contents)
        {
            sb.Append("- [").Append(t).Append("](#").Append(a).Append(")\n");
        }

        sb.Append('\n');
        foreach (var b in d.Blocks)
        {
            switch (b)
            {
                case GuideHeading h:
                    sb.Append(new string('#', h.Level)).Append(' ').Append(h.Text).Append("\n\n");
                    break;
                case GuideText t when t.Text.Length > 0:
                    sb.Append(t.Text).Append("\n\n");
                    break;
                case GuideTip t when t.Text.Length > 0:
                    sb.Append("> ").Append(t.Text).Append("\n\n");
                    break;
                case GuideList l:
                    foreach (var item in l.Items)
                    {
                        sb.Append(item.StartsWith("    • ", StringComparison.Ordinal) ? "  - " + item[6..] : "- " + item).Append('\n');
                    }

                    sb.Append('\n');
                    break;
                case GuideTable t:
                    sb.Append("| ").Append(string.Join(" | ", t.Headers.Select(Cell))).Append(" |\n");
                    sb.Append('|').Append(string.Concat(t.Headers.Select(_ => " --- |"))).Append('\n');
                    foreach (var row in t.Rows)
                    {
                        sb.Append("| ").Append(string.Join(" | ", row.Select(Cell))).Append(" |\n");
                    }

                    sb.Append('\n');
                    break;
            }
        }

        return sb.ToString();
    }
}
