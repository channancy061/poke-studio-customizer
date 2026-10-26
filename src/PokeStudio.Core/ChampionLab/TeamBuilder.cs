using PokeStudio.Core.Changes;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;

namespace PokeStudio.Core.ChampionLab;

/// <summary>One suggested team member and why it was picked.</summary>
public sealed record Suggestion(TrainerPokemon Pokemon, string Role, IReadOnlyList<string> Reasons);

public sealed record TeamSuggestion(IReadOnlyList<Suggestion> Members, IReadOnlyList<string> Notes);

/// <summary>
/// Rule-based team builder (no AI model): picks Pokémon that fit the theme, the legendary rules and the level, patch the
/// team's weaknesses against what the player can have, then chooses moves, nature, ability, item and EVs by role.
/// Every choice is explained.
/// </summary>
public static class TeamBuilder
{
    private static readonly string[] GoodAbilities =
    [
        "ABILITY_HUGE_POWER", "ABILITY_PURE_POWER", "ABILITY_SPEED_BOOST", "ABILITY_ADAPTABILITY", "ABILITY_INTIMIDATE", "ABILITY_LEVITATE",
        "ABILITY_DROUGHT", "ABILITY_DRIZZLE", "ABILITY_SAND_STREAM", "ABILITY_SNOW_WARNING", "ABILITY_SWIFT_SWIM", "ABILITY_CHLOROPHYLL",
        "ABILITY_SAND_RUSH", "ABILITY_SLUSH_RUSH", "ABILITY_REGENERATOR", "ABILITY_MULTISCALE", "ABILITY_TECHNICIAN", "ABILITY_GUTS",
        "ABILITY_MAGIC_GUARD", "ABILITY_SHEER_FORCE", "ABILITY_TOUGH_CLAWS", "ABILITY_STURDY", "ABILITY_THICK_FAT", "ABILITY_WATER_ABSORB",
        "ABILITY_VOLT_ABSORB", "ABILITY_FLASH_FIRE", "ABILITY_STORM_DRAIN", "ABILITY_LIGHTNING_ROD", "ABILITY_MOXIE", "ABILITY_PROTEAN",
    ];

    private static readonly string[] PhysicalSetup = ["MOVE_SWORDS_DANCE", "MOVE_DRAGON_DANCE", "MOVE_BULK_UP", "MOVE_SHIFT_GEAR", "MOVE_SHELL_SMASH", "MOVE_HONE_CLAWS"];

    private static readonly string[] SpecialSetup = ["MOVE_NASTY_PLOT", "MOVE_CALM_MIND", "MOVE_QUIVER_DANCE", "MOVE_SHELL_SMASH", "MOVE_TAIL_GLOW", "MOVE_GROWTH"];

    private static readonly string[] UtilityMoves =
    [
        "MOVE_PROTECT", "MOVE_RECOVER", "MOVE_ROOST", "MOVE_SOFT_BOILED", "MOVE_SLACK_OFF", "MOVE_WILL_O_WISP", "MOVE_THUNDER_WAVE",
        "MOVE_TOXIC", "MOVE_STEALTH_ROCK", "MOVE_U_TURN", "MOVE_VOLT_SWITCH", "MOVE_KNOCK_OFF", "MOVE_TAILWIND", "MOVE_TRICK_ROOM",
    ];

    /// <summary>The last official National Dex number (Pecharunt, Gen 9).</summary>
    public const int OfficialDexMax = 1025;

    /// <param name="Avoid">Species other bosses already use: picked only when nothing else fits as well (variety between gyms).</param>
    public sealed record Options(Difficulty Difficulty = Difficulty.Normal, int? TeamSize = null, int? Level = null, int Variant = 0, IReadOnlySet<string>? Avoid = null);

    public static TeamSuggestion Suggest(GameData data, LabSettings lab, Trainer trainer, Options options)
    {
        var notes = new List<string>();
        var group = Lab.GroupOf(data, lab, trainer);
        var rules = lab.RulesFor(group, options.Difficulty);
        var theme = lab.Themes.GetValueOrDefault(Lab.BaseKey(trainer));
        var milestone = Lab.MilestoneOf(data, trainer);
        var caps = data.Rules?.LevelCaps ?? [];
        var level = options.Level ?? (trainer.Party.Count > 0 ? trainer.Party.Max(p => p.Level) : caps.ElementAtOrDefault(Math.Max(0, milestone))?.Level ?? 20);
        var size = options.TeamSize ?? Math.Max(trainer.Party.Count, options.Difficulty >= Difficulty.Hard && group != TrainerGroup.Regular ? 6 : 1);
        if (FirstRivalBattle(group, trainer))
        {
            size = trainer.Party.Count; // the player has only their starter: the rival has only theirs, on every difficulty
        }

        var threats = ThreatTypes(data, milestone);
        var hard = options.Difficulty >= Difficulty.Hard;
        var extra = options.Difficulty == Difficulty.ExtraHard;

        var pool = Candidates(data, lab, level, theme, milestone).ToList();
        if (pool.Count == 0)
        {
            return new TeamSuggestion([], ["No Pokémon fit these rules at this level. Loosen the theme or the legendary limits."]);
        }

        var picked = new List<Species>();
        var rivalStarterIncluded = false;
        var rng = new Random(HashCode.Combine(trainer.Key, options.Variant));

        // the rival's starter comes first and stays
        if (group == TrainerGroup.Rival && data.Starters.Count == 3 && RivalStarter(data, lab, trainer) is { } starterLine)
        {
            var evolved = Progression.EvolutionPaths.EvolvedForm(data, starterLine, level, 36);
            if (data.Species.FirstOrDefault(s => s.Key == evolved) is { } st)
            {
                picked.Add(st);
                rivalStarterIncluded = true;
                notes.Add($"{st.DisplayName} is the rival's starter and stays on every team.");
            }
        }

        // the Champion is above everyone before them: at least one legendary and one mythical, the strongest that fit
        // (their own type first), with the limits raised just enough for them
        if (group == TrainerGroup.Champion && lab.ChampionLegends)
        {
            foreach (var (tags, label) in new[] { (SpeciesTags.RestrictedLegendary | SpeciesTags.SubLegendary, "legendary"), (SpeciesTags.Mythical, "mythical") })
            {
                if (picked.Count >= size || picked.Any(p => (p.Tags & tags) != 0))
                {
                    continue;
                }

                var best = pool.Where(s => (s.Tags & tags) != 0 && picked.All(p => p.Key != s.Key))
                    .OrderByDescending(s => theme is not null && s.Types.Contains(theme.Type))
                    .ThenByDescending(s => Score(s, picked, threats))
                    .FirstOrDefault();
                if (best is null)
                {
                    notes.Add($"No {label} Pokémon is available at level {level} for the Champion (check the story locks).");
                    continue;
                }

                picked.Add(best);
                rules = rules with
                {
                    MaxRestricted = Raise(rules.MaxRestricted, best.Tags.HasFlag(SpeciesTags.RestrictedLegendary)),
                    MaxSubLegendary = Raise(rules.MaxSubLegendary, best.Tags.HasFlag(SpeciesTags.SubLegendary)),
                    MaxMythical = Raise(rules.MaxMythical, best.Tags.HasFlag(SpeciesTags.Mythical)),
                    RestrictedOrSub = false,
                };
                notes.Add($"{best.DisplayName} is the Champion's {label}.");
            }

            static int Raise(int max, bool needed) => !needed || max < 0 ? max : Math.Max(max, 1);
        }

        // early bosses get Pokémon a player at that level can face (no Aerodactyl at level 13); the limit loosens a step at a
        // time when the theme has nothing under it
        var strongest = MaxStatTotal(level, options.Difficulty);
        while (picked.Count < size)
        {
            var offTypeLeft = theme is null ? int.MaxValue : theme.OffTypeAllowed - picked.Count(p => !p.Types.Contains(theme.Type));
            var scored = pool
                .Where(s => s.Stats.Total <= strongest)
                .Where(s => picked.All(p => p.Key != s.Key) && (!rules.SpeciesClause || picked.All(p => Root(data, p.Key) != Root(data, s.Key) && p.Name != s.Name)))
                .Where(s => theme is null || s.Types.Contains(theme.Type) || offTypeLeft > 0)
                .Where(s => Allowed(s, rules, picked))
                .Where(s => !lab.UniqueTypeCombos || theme is null || picked.All(p => !p.Types.OrderBy(x => x).SequenceEqual(s.Types.OrderBy(x => x))))
                .Select(s => (Species: s, Score: Score(s, picked, threats) + (rng.NextDouble() * 25 * options.Variant) - (options.Avoid?.Contains(Root(data, s.Key)) == true ? 600 : 0)))
                .OrderByDescending(x => x.Score)
                .ToList();
            if (scored.Count == 0 && strongest < 800)
            {
                strongest += 40;
                continue;
            }

            if (scored.Count == 0)
            {
                notes.Add($"Only {picked.Count} Pokémon fit every rule; the team is shorter than {size}.");
                break;
            }

            picked.Add(scored[0].Species);
        }

        // strongest last: the ace
        var ordered = picked.Skip(rivalStarterIncluded ? 1 : 0).OrderBy(s => s.Stats.Total).ToList();
        if (rivalStarterIncluded)
        {
            ordered.Add(picked[0]); // the rival's starter is the ace
        }

        var usedItems = new HashSet<string>(StringComparer.Ordinal);
        var members = new List<Suggestion>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var s = ordered[i];
            var isAce = i == ordered.Count - 1;
            members.Add(Build(data, s, level + (isAce && hard ? 2 : 0), options.Difficulty, threats, picked, rules.ItemClause ? usedItems : null, isAce));
        }

        var weak = TypeChart.Types.Where(t => picked.Count(p => TypeChart.Multiplier(t, p.Types) > 1) >= 3).ToList();
        if (weak.Count > 0)
        {
            notes.Add("Shared weakness: " + string.Join(", ", weak.Select(data.Catalog.NameOf)) + ". Consider a Pokémon that resists it.");
        }

        if (extra)
        {
            notes.Add("Extra Hard: full training (EVs), best natures and competitive items. Pair it with the Expert or Champion AI.");
        }

        return new TeamSuggestion(members, notes);
    }

    /// <summary>
    /// The strongest Pokémon (base stat total) a boss of this level should have: 330 + 8 per level (434 at level 13,
    /// 498 at 21, no limit worth noticing from about 45), Easy 30 lower, Hard 25 and Extra Hard 50 higher.
    /// </summary>
    public static int MaxStatTotal(int level, Difficulty difficulty) =>
        330 + (8 * level) + difficulty switch { Difficulty.Easy => -30, Difficulty.Hard => 25, Difficulty.ExtraHard => 50, _ => 0 };

    /// <summary>
    /// A boss's team size when it's redesigned: growing with the badge (3 at the first two gyms up to 6 at the last two),
    /// 5 for the Elite Four, 6 for Champions and the post-game, one more for a rival; never smaller than it was.
    /// </summary>
    public static int BossTeamSize(GameData data, LabSettings lab, Trainer t)
    {
        var group = Lab.GroupOf(data, lab, t);
        var badge = Progression.StoryOrder.BadgeOf(t.Key) is { } flag ? flag[^5] - '0' : 0;
        var size = group switch
        {
            TrainerGroup.EarlyGym or TrainerGroup.LateGym => badge > 0 ? 2 + ((badge + 1) / 2) : group == TrainerGroup.EarlyGym ? 4 : 5,
            TrainerGroup.EliteFour => 5,
            TrainerGroup.Champion or TrainerGroup.PostGame => 6,
            TrainerGroup.Rival => FirstRivalBattle(group, t) ? t.Party.Count : t.Party.Count + 1,
            _ => t.Party.Count,
        };
        return Math.Clamp(Math.Max(size, t.Party.Count), 1, 6);
    }

    /// <summary>
    /// Redesigns every boss's normal team at its own level (gym leaders, Elite Four, Champions, rivals; story battles keep
    /// theirs), with a type theme from its current team when it has none, as one undo step.
    /// </summary>
    public static (IChange? Change, int Count) RedesignBosses(GameData data, LabSettings lab)
    {
        var parts = new List<IChange>();
        void Do(IChange change)
        {
            change.Apply();
            parts.Add(change);
        }

        try
        {
            var bosses = data.Trainers.Where(t => Lab.DifficultyOf(t) == Difficulty.Normal && !t.Key.Contains('@', StringComparison.Ordinal)
                    && t.LockedReason is null && t.Party.Count > 0 && !t.NeverFought
                    && !t.Key.StartsWith(Story.StoryBattles.TrainerPrefix, StringComparison.Ordinal)
                    && Lab.GroupOf(data, lab, t) != TrainerGroup.Regular)
                .Select(t => t.Key).ToList();

            // themes first (gym leaders and the Elite Four keep the type they had)
            var themes = new Dictionary<string, TypeTheme>(lab.Themes);
            foreach (var key in bosses)
            {
                var t = data.Trainers.First(x => x.Key == key);
                if (!themes.ContainsKey(key) && Lab.GroupOf(data, lab, t) is TrainerGroup.EarlyGym or TrainerGroup.LateGym or TrainerGroup.EliteFour
                    && Lab.SuggestTheme(data, t) is { } type)
                {
                    themes[key] = new TypeTheme(type);
                }
            }

            var themed = lab with { Themes = themes };
            if (themes.Count != lab.Themes.Count)
            {
                Do(new ValueChange<LabSettings?>("Champion Lab", "Type themes for every boss", v => data.Lab = v, data.Lab, themed));
            }

            // story battles' teams count as used, so a gym leader doesn't repeat the challenger just before them
            var used = data.Trainers.Where(t => t.Key.StartsWith(Story.StoryBattles.TrainerPrefix, StringComparison.Ordinal))
                .SelectMany(t => t.Party).Select(p => Root(data, p.Species)).ToHashSet(StringComparer.Ordinal);
            var count = 0;
            foreach (var key in bosses)
            {
                var t = data.Trainers.First(x => x.Key == key);
                var suggestion = Suggest(data, themed, t, new Options(Difficulty.Normal, BossTeamSize(data, themed, t), t.Party.Max(p => p.Level), Avoid: used));
                if (Lab.GroupOf(data, themed, t) != TrainerGroup.Rival)
                {
                    used.UnionWith(suggestion.Members.Select(m => Root(data, m.Pokemon.Species)));
                }

                if (suggestion.Members.Count == 0)
                {
                    continue;
                }

                Do(Apply(data, t, suggestion, $"{t.Name}: redesigned"));
                count++;
            }

            return (parts.Count == 0 ? null : new CompositeChange("Champion Lab", $"Every boss redesigned ({count})", parts.ToList()), count);
        }
        finally
        {
            for (var i = parts.Count - 1; i >= 0; i--)
            {
                parts[i].Revert();
            }
        }
    }

    /// <summary>Applies a suggestion to a trainer as one undo step.</summary>
    public static IChange Apply(GameData data, Trainer trainer, TeamSuggestion suggestion, string label)
    {
        var index = data.Trainers.FindIndex(t => t.Key == trainer.Key);
        var before = data.Trainers[index];
        return new ValueChange<Trainer>("Trainers", $"{before.Name} · {label}", v => data.Trainers[index] = v, before,
            before with { Party = suggestion.Members.Select(m => m.Pokemon).ToList() });
    }

    // ---------- helpers ----------

    internal static string FamilyRoot(GameData data, string key) => Root(data, key);

    internal static string Root(GameData data, string key)
    {
        var current = key;
        for (var guard = 0; guard < 5; guard++)
        {
            var parent = data.Evolutions.FirstOrDefault(e => e.Evolutions.Any(x => x.TargetSpecies == current));
            if (parent is null)
            {
                break;
            }

            current = parent.FromSpecies;
        }

        return current;
    }

    /// <summary>Types the player's Pokémon can have before this milestone (all types if unknown).</summary>
    internal static IReadOnlyList<string> ThreatTypes(GameData data, int milestone)
    {
        var scout = Scouting.Run(data);
        return milestone >= 0 && milestone < scout.Count && scout[milestone].Types.Count > 0 ? scout[milestone].Types : TypeChart.Types;
    }

    /// <summary>Species reachable by this level: evolutions from level-up up to the level, item/trade ones from level 36.</summary>
    // forms that only exist in battle, need a story event or special item, or aren't meant for trainers
    private static readonly string[] BattleOnly =
    [
        "_GMAX", "_TOTEM", "_PRIMAL", "_MEGA", "_ASH", "_BATTLE_BOND", "_HERO", "_ETERNAMAX", "_CROWNED", "_ZEN", "_BLADE",
        "_SCHOOL", "_COMPLETE", "_BUSTED", "_PIROUETTE", "_ULTRA", "_STELLAR", "_TERASTAL", "_NOICE", "_HANGRY", "_GULPING",
        "_GORGING", "_CROWNED_SHIELD", "_CROWNED_SWORD", "_SUNSHINE", "_RAINY", "_SNOWY", "_SUNNY", "_STARTER", "_PARTNER",
    ];

    private static readonly string[] Novelty = ["_COSPLAY", "_ROCK_STAR", "_BELLE", "_POP_STAR", "_PH_D", "_LIBRE", "_CAP", "_SPIKY_EARED", "_ETERNAL_FLOWER", "_WORLD"];

    /// <summary>Not a Mega or a form that only exists during battle.</summary>
    public static bool IsRegularForm(Species s) => !s.Tags.HasFlag(SpeciesTags.Mega) && !BattleOnly.Any(b => s.Key.Contains(b, StringComparison.Ordinal));

    /// <summary>A Pokémon of the main games (National Dex 1–1025), not a novelty or event form.</summary>
    public static bool IsOfficial(Species s) => s.NationalDex is >= 1 and <= OfficialDexMax && !Novelty.Any(n => s.Key.Contains(n, StringComparison.Ordinal));

    private static IEnumerable<Species> Candidates(GameData data, LabSettings lab, int level, TypeTheme? theme, int milestone)
    {
        // official Pokémon only: needs the game's National Dex numbers; novelty and event forms are left out too
        var officialOnly = lab.OfficialOnly && data.Species.Any(s => s.NationalDex is not null);
        foreach (var s in data.Species.Where(s => s.IsEnabled && !s.LockedFields.ContainsKey("*") && s.Stats.Total > 0 && IsRegularForm(s)))
        {
            if (officialOnly && !IsOfficial(s))
            {
                continue;
            }

            if (lab.StoryLocks.TryGetValue(s.Key, out var unlock) && milestone >= 0 && milestone < unlock)
            {
                continue;
            }

            if (!ReachableAt(data, s.Key, level))
            {
                continue;
            }

            // fully evolved (or not evolving any further by this level)
            if (Progression.EvolutionPaths.EvolvedForm(data, s.Key, level, 36) != s.Key)
            {
                continue;
            }

            yield return s;
        }
    }

    /// <summary>True when every evolution step leading to <paramref name="key"/> can have happened by this level.</summary>
    private static bool ReachableAt(GameData data, string key, int level)
    {
        var current = key;
        var seen = new HashSet<string>(StringComparer.Ordinal) { key };
        while (data.Evolutions.FirstOrDefault(e => e.Evolutions.Any(x => x.TargetSpecies == current)) is { } parent)
        {
            if (!seen.Add(parent.FromSpecies))
            {
                break; // evolution data that loops back on itself: the chain has no real start
            }

            var evo = parent.Evolutions.First(x => x.TargetSpecies == current);
            var needed = evo.Method == "EVO_LEVEL" && int.TryParse(evo.Parameter, out var at) && at > 0 ? at : 36;
            if (level < needed)
            {
                return false;
            }

            current = parent.FromSpecies;
        }

        return true;
    }

    private static bool Allowed(Species s, GroupRules rules, List<Species> picked)
    {
        int Count(SpeciesTags t) => picked.Count(p => p.Tags.HasFlag(t)) + (s.Tags.HasFlag(t) ? 1 : 0);
        var restricted = Count(SpeciesTags.RestrictedLegendary);
        var sub = Count(SpeciesTags.SubLegendary);
        bool Within(int max, int n) => max < 0 || n <= max;
        if (!Within(rules.MaxRestricted, restricted) || !Within(rules.MaxMythical, Count(SpeciesTags.Mythical))
            || !Within(rules.MaxUltraBeast, Count(SpeciesTags.UltraBeast)) || !Within(rules.MaxParadox, Count(SpeciesTags.Paradox)))
        {
            return false;
        }

        var subMax = rules.RestrictedOrSub && restricted > 0 ? 0 : rules.MaxSubLegendary;
        return Within(subMax, sub);
    }

    /// <summary>Strength plus how much it helps the team against the player's likely types.</summary>
    private static double Score(Species s, List<Species> team, IReadOnlyList<string> threats)
    {
        var score = (double)s.Stats.Total;
        foreach (var t in threats)
        {
            var teamWeak = team.Count(p => TypeChart.Multiplier(t, p.Types) > 1);
            var mult = TypeChart.Multiplier(t, s.Types);
            if (teamWeak > 0 && mult < 1)
            {
                score += 25 * teamWeak * (mult == 0 ? 1.5 : 1); // patches a hole the team has
            }

            if (mult > 1)
            {
                score -= 12 * (teamWeak + 1); // stacking a weakness
            }

            if (s.Types.Any(own => TypeChart.Multiplier(own, [t]) > 1))
            {
                score += 4; // its own type hits that threat hard
            }
        }

        return score;
    }

    private static Suggestion Build(GameData data, Species s, int level, Difficulty difficulty, IReadOnlyList<string> threats, List<Species> team, HashSet<string>? usedItems, bool isAce)
    {
        var reasons = new List<string>();
        var c = data.Catalog;
        var physical = s.Stats.Attack >= s.Stats.SpAttack;
        var fast = s.Stats.Speed >= 85;
        var bulky = s.Stats.Hp + s.Stats.Defense + s.Stats.SpDefense > 3 * Math.Max(s.Stats.Attack, s.Stats.SpAttack);
        var role = bulky && !fast ? "Wall" : fast ? (physical ? "Fast physical attacker" : "Fast special attacker") : physical ? "Physical attacker" : "Special attacker";
        if (isAce)
        {
            role = "Ace · " + role;
        }

        var types = string.Join("/", s.Types.Select(c.NameOf));
        var patched = threats.Where(t => TypeChart.Multiplier(t, s.Types) < 1 && team.Any(p => p != s && TypeChart.Multiplier(t, p.Types) > 1)).Take(3).ToList();
        reasons.Add($"{s.DisplayName} ({types}), stat total {s.Stats.Total}." + (patched.Count > 0 ? $" Covers the team against {string.Join(", ", patched.Select(c.NameOf))}." : ""));

        var moves = PickMoves(data, s, level, physical, threats, difficulty, out var moveNote);
        if (moveNote is not null)
        {
            reasons.Add(moveNote);
        }

        // nature by role
        string? nature = null;
        if (difficulty >= Difficulty.Normal && c.Natures.Count > 0)
        {
            nature = bulky && !fast
                ? (s.Stats.Defense >= s.Stats.SpDefense ? Natures.For(2, physical ? 3 : 1) : Natures.For(4, physical ? 3 : 1))
                : fast ? Natures.For(5, physical ? 3 : 1) : Natures.For(physical ? 1 : 3, physical ? 3 : 1);
            if (c.Natures.All(n => n.Key != nature))
            {
                nature = null;
            }
            else
            {
                reasons.Add($"{c.NameOf(nature)} nature: raises {(nature!.Contains("JOLLY") || nature.Contains("TIMID") ? "Speed" : bulky && !fast ? "its defences" : physical ? "Attack" : "Sp. Atk")} for its role.");
            }
        }

        // ability
        var abilities = s.Abilities.Where(a => a != "ABILITY_NONE").ToList();
        var normalAbilities = abilities.Take(Math.Min(2, abilities.Count)).ToList();
        var choices = difficulty >= Difficulty.Hard ? abilities : normalAbilities;
        var ability = choices.FirstOrDefault(a => GoodAbilities.Contains(a)) ?? choices.FirstOrDefault();
        if (ability is not null && GoodAbilities.Contains(ability))
        {
            reasons.Add($"{c.NameOf(ability)} ability.");
        }

        // item
        string? item = null;
        if (difficulty >= Difficulty.Hard || isAce)
        {
            string[] prefs = difficulty switch
            {
                Difficulty.ExtraHard when bulky && !fast => ["ITEM_LEFTOVERS", "ITEM_ROCKY_HELMET", "ITEM_SITRUS_BERRY"],
                Difficulty.ExtraHard when fast => ["ITEM_CHOICE_SCARF", "ITEM_LIFE_ORB", "ITEM_FOCUS_SASH"],
                Difficulty.ExtraHard => [physical ? "ITEM_CHOICE_BAND" : "ITEM_CHOICE_SPECS", "ITEM_LIFE_ORB", "ITEM_ASSAULT_VEST"],
                Difficulty.Hard when bulky => ["ITEM_LEFTOVERS", "ITEM_SITRUS_BERRY"],
                Difficulty.Hard => ["ITEM_LIFE_ORB", "ITEM_SITRUS_BERRY", "ITEM_LEFTOVERS"],
                _ => ["ITEM_SITRUS_BERRY", "ITEM_ORAN_BERRY"],
            };
            item = prefs.FirstOrDefault(i => c.Items.Any(x => x.Key == i) && (usedItems is null || !usedItems.Contains(i)));
            if (item is not null)
            {
                usedItems?.Add(item);
                reasons.Add($"Holds {c.NameOf(item)}.");
            }
        }

        // training
        BaseStats? ivs = difficulty switch
        {
            Difficulty.Easy => new BaseStats(0, 0, 0, 0, 0, 0),
            Difficulty.Normal => null,
            _ => new BaseStats(31, 31, 31, 31, 31, 31),
        };
        BaseStats? evs = null;
        if (difficulty == Difficulty.ExtraHard)
        {
            var physicalWall = s.Stats.Defense >= s.Stats.SpDefense;
            evs = bulky && !fast
                ? new BaseStats(252, 0, physicalWall ? 252 : 4, 0, physicalWall ? 4 : 252, 0) // HP + the stronger defence
                : physical ? new BaseStats(4, 252, 0, 0, 0, 252) : new BaseStats(4, 0, 0, 252, 0, 252);
            reasons.Add("Fully trained (252 / 252 / 4 EVs).");
        }

        var mon = new TrainerPokemon
        {
            Species = s.Key,
            Level = Math.Clamp(level, 1, 100),
            Moves = moves,
            Nature = nature,
            Ability = ability,
            HeldItem = item,
            Ivs = ivs,
            Evs = evs,
        };
        return new Suggestion(mon, role, reasons);
    }

    private static IReadOnlyList<string> PickMoves(GameData data, Species s, int level, bool physical, IReadOnlyList<string> threats, Difficulty difficulty, out string? note)
    {
        note = null;
        var learnset = data.LearnsetFor(s);
        if (learnset is null || data.Moves.Count == 0)
        {
            return []; // the game picks its latest level-up moves
        }

        // moves it learns by levelling up by now are fair; TMs, tutors and egg moves only up to the power its level allows
        // (a Lv 5 rival with Thunderbolt or a Lv 14 leader with Head Smash outclasses the player's team)
        var natural = learnset.LevelUp.Where(m => m.Level <= level).Select(m => m.Move).ToHashSet(StringComparer.Ordinal);
        var known = natural
            .Concat(learnset.Teachable)
            .Concat(difficulty >= Difficulty.Hard ? learnset.EggMoves : [])
            .Distinct().ToHashSet(StringComparer.Ordinal);
        var leftOut = data.Lab?.LeftOutMoves ?? [];
        var moves = data.Moves.Where(m => known.Contains(m.Key) && !m.LockedFields.ContainsKey("*") && MoveRules.Allowed(m, level, difficulty, natural, leftOut)).ToList();
        var category = physical ? MoveCategory.Physical : MoveCategory.Special;
        double Value(Move m) => m.Power * (m.Accuracy == 0 ? 1 : m.Accuracy / 100.0) * (s.Types.Contains(m.Type) ? 1.5 : 1);
        // two-turn, recharge, self-fainting and situational moves make poor picks for a trainer
        string[] awkward = ["TWO_TURN", "SEMI_INVULNERABLE", "RECHARGE", "EXPLOSION", "FUTURE_SIGHT", "SKY_DROP", "FOCUS_PUNCH", "LAST_RESORT", "SNORE", "DREAM_EATER", "BELCH", "SYNCHRONOISE", "FLING", "NATURAL_GIFT", "FINAL_GAMBIT"];
        string[] awkwardMoves = ["MOVE_MISTY_EXPLOSION", "MOVE_EXPLOSION", "MOVE_SELF_DESTRUCT", "MOVE_FINAL_GAMBIT", "MOVE_FLY", "MOVE_BOUNCE", "MOVE_DIG", "MOVE_DIVE", "MOVE_SOLAR_BEAM", "MOVE_SKY_ATTACK", "MOVE_HYPER_BEAM", "MOVE_GIGA_IMPACT"];
        var attacks = moves.Where(m => m.Category == category && m.Power > 0 && !awkwardMoves.Contains(m.Key) && !awkward.Any(a => m.Effect.Contains(a, StringComparison.Ordinal))).ToList();

        var chosen = new List<Move>();
        foreach (var t in s.Types)
        {
            if (attacks.Where(m => m.Type == t).OrderByDescending(Value).FirstOrDefault() is { } stab && !chosen.Contains(stab))
            {
                chosen.Add(stab);
            }
        }

        // coverage: the attack that hits the most likely threat types super effectively
        while (chosen.Count < 3 && attacks.Except(chosen).Where(m => chosen.All(c => c.Type != m.Type))
            .Select(m => (Move: m, Hits: threats.Count(t => TypeChart.Multiplier(m.Type, [t]) > 1)))
            .Where(x => x.Hits > 0)
            .OrderByDescending(x => x.Hits * Value(x.Move))
            .FirstOrDefault() is { Move: not null } cover)
        {
            chosen.Add(cover.Move);
        }

        var setup = physical ? PhysicalSetup : SpecialSetup;
        var extra = setup.Select(k => moves.FirstOrDefault(m => m.Key == k)).FirstOrDefault(m => m is not null)
            ?? UtilityMoves.Select(k => moves.FirstOrDefault(m => m.Key == k)).FirstOrDefault(m => m is not null);
        if (extra is not null && chosen.Count < 4)
        {
            chosen.Add(extra);
        }

        foreach (var m in attacks.Except(chosen).OrderByDescending(Value))
        {
            if (chosen.Count >= 4)
            {
                break;
            }

            chosen.Add(m);
        }

        if (chosen.Count == 0)
        {
            return [];
        }

        note = "Moves: " + string.Join(", ", chosen.Select(m => m.Name)) + ".";
        return chosen.Take(4).Select(m => m.Key).ToList();
    }

    /// <summary>The first rival battle (Oak's Lab, Route 103): a rival whose Pokémon are all Lv 6 or lower.</summary>
    private static bool FirstRivalBattle(TrainerGroup group, Trainer t) =>
        group == TrainerGroup.Rival && t.Party.Count > 0 && t.Party.All(p => p.Level <= 6);

    private static string? RivalStarter(GameData data, LabSettings lab, Trainer t)
    {
        var starters = new StarterSet(data.Starters);
        var suffix = data.Starters.FirstOrDefault(s => Lab.BaseKey(t).EndsWith(s.Replace("SPECIES_", "_", StringComparison.Ordinal), StringComparison.Ordinal));
        if (suffix is null)
        {
            return null;
        }

        // same convention detection as the checks: does the current team hold the suffix's line?
        var family = Starters.Family(data, suffix);
        return t.Party.Any(p => family.Contains(p.Species)) ? suffix : Starters.Counter(data, starters, suffix) ?? suffix;
    }
}
