using System.Text.RegularExpressions;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;
using PokeStudio.Core.Validation;

namespace PokeStudio.Core.ChampionLab;

public enum TrainerGroup
{
    Regular,
    EarlyGym,
    LateGym,
    Rival,
    EliteFour,
    Champion,
    PostGame,
}

public enum Difficulty
{
    Easy,
    Normal,
    Hard,
    ExtraHard,
}

/// <summary>Limits for one trainer group at one difficulty. -1 = no limit.</summary>
public sealed record GroupRules
{
    public required TrainerGroup Group { get; init; }
    public required Difficulty Difficulty { get; init; }
    public int MaxRestricted { get; init; }
    public int MaxSubLegendary { get; init; }
    public int MaxMythical { get; init; }
    public int MaxUltraBeast { get; init; }
    public int MaxParadox { get; init; }

    /// <summary>"1 restricted OR 2 sub-legendaries": a restricted legendary uses up the sub-legendary allowance.</summary>
    public bool RestrictedOrSub { get; init; }

    public int MaxMega { get; init; } = 1;
    public bool SpeciesClause { get; init; } = true;
    public bool ItemClause { get; init; }
}

/// <summary>"Mostly" lets a gym bring a few off-type Pokémon to cover its weaknesses.</summary>
public sealed record TypeTheme(string Type, int OffTypeAllowed = 0);

/// <summary>Champion Lab's settings, saved with the Poke Studio project (never in the game files).</summary>
public sealed record LabSettings
{
    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyDictionary<string, TrainerGroup> GroupOverrides { get; init; } = new Dictionary<string, TrainerGroup>();
    public IReadOnlyDictionary<string, TypeTheme> Themes { get; init; } = new Dictionary<string, TypeTheme>();
    public IReadOnlyList<GroupRules> Rules { get; init; } = Defaults.Rules();

    /// <summary>Species that mustn't appear on trainers before a milestone (index into the level caps).</summary>
    public IReadOnlyDictionary<string, int> StoryLocks { get; init; } = new Dictionary<string, int>();

    public bool RivalKeepsStarter { get; init; } = true;

    /// <summary>The team builder only uses official Pokémon from the main series (National Dex 1–1025, normal forms).</summary>
    public bool OfficialOnly { get; init; } = true;

    /// <summary>Moves left out of the game (e.g. Dragon Rage): no learnset has them and no trainer gets them.</summary>
    public IReadOnlyList<string> LeftOutMoves { get; init; } = [];
    public bool FairPlay { get; init; } = true;
    public bool UniqueTypeCombos { get; init; } = true;

    /// <summary>The Champion's team has at least one legendary and one mythical: they're above everyone before them.</summary>
    public bool ChampionLegends { get; init; } = true;

    public GroupRules RulesFor(TrainerGroup group, Difficulty difficulty) =>
        Rules.FirstOrDefault(r => r.Group == group && r.Difficulty == difficulty)
        ?? Rules.FirstOrDefault(r => r.Group == group && r.Difficulty == Difficulty.Normal)
        ?? new GroupRules { Group = group, Difficulty = difficulty, MaxRestricted = -1, MaxSubLegendary = -1, MaxMythical = -1, MaxUltraBeast = -1, MaxParadox = -1, MaxMega = -1, SpeciesClause = false };
}

public static class Defaults
{
    /// <summary>The defaults agreed for the app (all editable).</summary>
    public static IReadOnlyList<GroupRules> Rules()
    {
        var list = new List<GroupRules>();
        foreach (var d in Enum.GetValues<Difficulty>())
        {
            var hard = d >= Difficulty.Hard;
            var extra = d == Difficulty.ExtraHard;
            GroupRules R(TrainerGroup g, int restricted = 0, int sub = 0, int mythical = 0, bool orSub = false, int ub = 0, int paradox = 0) => new()
            {
                Group = g,
                Difficulty = d,
                MaxRestricted = restricted,
                MaxSubLegendary = sub,
                MaxMythical = mythical,
                RestrictedOrSub = orSub,
                MaxUltraBeast = ub,
                MaxParadox = paradox,
                ItemClause = hard,
            };

            list.Add(R(TrainerGroup.Regular));
            list.Add(R(TrainerGroup.EarlyGym));
            list.Add(R(TrainerGroup.LateGym, sub: extra ? 1 : 0));
            list.Add(R(TrainerGroup.Rival, sub: hard ? 1 : 0));
            list.Add(R(TrainerGroup.EliteFour, sub: hard ? 1 : 0, mythical: extra ? 1 : 0, ub: extra ? 1 : 0, paradox: extra ? 1 : 0));
            list.Add(R(TrainerGroup.Champion,
                restricted: extra ? 2 : hard ? 1 : 0,
                sub: hard ? 2 : 1,
                orSub: d == Difficulty.Hard,
                ub: hard ? 1 : 0,
                paradox: hard ? 1 : 0));
            list.Add(R(TrainerGroup.PostGame, restricted: hard ? 2 : 1, sub: 2, mythical: hard ? 1 : 0, ub: 2, paradox: 2));
        }

        return list;
    }
}

public static partial class Lab
{
    public static string Label(TrainerGroup g) => g switch
    {
        TrainerGroup.EarlyGym => "Gyms 1–5",
        TrainerGroup.LateGym => "Gyms 6–8",
        TrainerGroup.EliteFour => "Elite Four",
        TrainerGroup.PostGame => "Post-game",
        _ => g.ToString(),
    };

    public static string Label(Difficulty d) => d == Difficulty.ExtraHard ? "Extra Hard" : d.ToString();

    public static Difficulty DifficultyOf(Trainer t)
    {
        var at = t.Key.IndexOf('@', StringComparison.Ordinal);
        return at < 0 ? Difficulty.Normal : t.Key[(at + 1)..].Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant() switch
        {
            "EASY" => Difficulty.Easy,
            "HARD" => Difficulty.Hard,
            "EXTRAHARD" or "EXTRA_HARD" => Difficulty.ExtraHard,
            _ => Difficulty.Normal,
        };
    }

    public static string BaseKey(Trainer t) => t.Key.Split('@')[0];

    [GeneratedRegex(@"_(\d+)$")]
    private static partial Regex RematchSuffix();

    /// <summary>Best guess from class, key and level; the user can move any trainer.</summary>
    public static TrainerGroup Guess(GameData data, Trainer t)
    {
        var cls = data.Catalog.NameOf(t.TrainerClass);
        var key = BaseKey(t);
        bool Has(string s, string w) => s.Contains(w, StringComparison.OrdinalIgnoreCase);
        var rematch = RematchSuffix().Match(key) is { Success: true } m && int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) > 1;
        if (Has(cls, "Champion"))
        {
            return rematch ? TrainerGroup.PostGame : TrainerGroup.Champion;
        }

        if (Has(cls, "Elite"))
        {
            return rematch ? TrainerGroup.PostGame : TrainerGroup.EliteFour;
        }

        if (Has(cls, "Leader"))
        {
            if (rematch)
            {
                return TrainerGroup.PostGame;
            }

            return MilestoneOf(data, t) is var i and >= 0 && i >= 5 ? TrainerGroup.LateGym : TrainerGroup.EarlyGym;
        }

        if (Has(cls, "Rival") || Has(key, "RIVAL") || Has(key, "_MAY_") || Has(key, "_BRENDAN_") || Has(key, "TRAINER_BLUE"))
        {
            return TrainerGroup.Rival;
        }

        return TrainerGroup.Regular;
    }

    public static TrainerGroup GroupOf(GameData data, LabSettings lab, Trainer t) =>
        lab.GroupOverrides.TryGetValue(BaseKey(t), out var g) ? g : Guess(data, t);

    /// <summary>Which milestone (index into the level caps) a trainer belongs before, from its strongest Pokémon.</summary>
    public static int MilestoneOf(GameData data, Trainer t)
    {
        var caps = data.Rules?.LevelCaps ?? [];
        if (caps.Count == 0 || t.Party.Count == 0)
        {
            return -1;
        }

        // a story battle has its own step; a gym leader of FireRed or Emerald the step of their badge
        var key = BaseKey(t);
        var own = data.Story?.Battles.FirstOrDefault(b => b.TrainerKey == key)?.BeatFlag ?? Progression.StoryOrder.BadgeOf(key);
        if (own is not null && caps.Select((c, i) => (c, i)).FirstOrDefault(x => x.c.Milestone == own) is { c: not null } found)
        {
            return found.i;
        }

        // a rematch (Emerald's TRAINER_ROXANNE_2…5) is fought after the League
        if (IsRematch(data, t))
        {
            return caps.Count - 1;
        }

        // everyone else by level; the League's own Elite Four and Champion never at a story battle's step (a challenger
        // waiting outside Victory Road comes before them)
        var league = t.TrainerClass.Contains("ELITE_FOUR", StringComparison.Ordinal) || t.TrainerClass.Contains("CHAMPION", StringComparison.Ordinal);
        var storySteps = league ? data.Story?.Battles.Select(b => b.BeatFlag).ToHashSet(StringComparer.Ordinal) ?? [] : [];
        var ace = t.Party.Max(p => p.Level);
        for (var i = 0; i < caps.Count; i++)
        {
            if (ace <= caps[i].Level && !storySteps.Contains(caps[i].Milestone))
            {
                return i;
            }
        }

        return caps.Count - 1;
    }

    /// <summary>A rematch: TRAINER_X_2…9 when TRAINER_X_1 exists (Emerald's gym leaders and trainers fought again later).</summary>
    public static bool IsRematch(GameData data, Trainer t)
    {
        var key = BaseKey(t);
        var cut = key.LastIndexOf('_');
        return cut > 0 && key.Length - cut == 2 && key[^1] is >= '2' and <= '9' && data.Trainers.Any(x => x.Key == key[..cut] + "_1");
    }

    /// <summary>The theme a team mostly follows: a type at least two-thirds of it share (4 of 6, 2 of 2…).</summary>
    public static string? SuggestTheme(GameData data, Trainer t)
    {
        var species = t.Party.Select(p => data.Species.FirstOrDefault(s => s.Key == p.Species)).Where(s => s is not null).ToList();
        if (species.Count < 2)
        {
            return null;
        }

        var best = species.SelectMany(s => s!.Types).GroupBy(x => x).OrderByDescending(g => g.Count()).First();
        return best.Count() * 3 >= species.Count * 2 ? best.Key : null;
    }
}

/// <summary>The three starters and which one beats which (first entry the player can pick, etc.).</summary>
public sealed record StarterSet(IReadOnlyList<string> Species);

public static class Starters
{
    /// <summary>The starter that has a type advantage over <paramref name="playerStarter"/> (Grass → Fire → Water → Grass).</summary>
    public static string? Counter(GameData data, StarterSet set, string playerStarter)
    {
        Species? S(string k) => data.Species.FirstOrDefault(s => s.Key == k);
        var player = S(playerStarter);
        if (player is null)
        {
            return null;
        }

        static string? Beats(string type) => type switch
        {
            "TYPE_GRASS" => "TYPE_FIRE",
            "TYPE_FIRE" => "TYPE_WATER",
            "TYPE_WATER" => "TYPE_GRASS",
            _ => null,
        };

        var wanted = player.Types.Select(Beats).FirstOrDefault(x => x is not null);
        return set.Species.FirstOrDefault(k => k != playerStarter && S(k) is { } s && s.Types.Contains(wanted ?? "")) ;
    }

    /// <summary>Every species in the evolution family of <paramref name="species"/> (both directions).</summary>
    public static HashSet<string> Family(GameData data, string species)
    {
        var family = new HashSet<string>(StringComparer.Ordinal) { species };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var set in data.Evolutions)
            {
                foreach (var e in set.Evolutions)
                {
                    if (family.Contains(set.FromSpecies) && family.Add(e.TargetSpecies))
                    {
                        changed = true;
                    }

                    if (family.Contains(e.TargetSpecies) && family.Add(set.FromSpecies))
                    {
                        changed = true;
                    }
                }
            }
        }

        return family;
    }
}

/// <summary>What the player can realistically own before each milestone (an estimate from wild levels).</summary>
public sealed record ScoutReport(string Milestone, int Cap, IReadOnlyList<string> Species, IReadOnlyList<string> Types);

public static class Scouting
{
    /// <summary>
    /// Wild areas whose highest level is at most the milestone's cap count as reachable before it; anything caught there
    /// can also be evolved by level up to the cap. Gifts and in-game trades aren't counted yet.
    /// </summary>
    public static IReadOnlyList<ScoutReport> Run(GameData data)
    {
        var caps = data.Rules?.LevelCaps ?? [];
        var reports = new List<ScoutReport>();
        foreach (var cap in caps)
        {
            var caught = data.Encounters
                .Where(e => e.Methods.SelectMany(m => m.Slots).Any() && e.Methods.SelectMany(m => m.Slots).Max(s => s.MaxLevel) <= cap.Level)
                .SelectMany(e => e.Methods.SelectMany(m => m.Slots).Select(s => s.Species))
                .ToHashSet(StringComparer.Ordinal);
            var owned = caught.Select(s => Progression.EvolutionPaths.EvolvedForm(data, s, cap.Level, int.MaxValue)).Concat(caught)
                .Where(k => data.Species.Any(s => s.Key == k && s.IsEnabled))
                .Distinct().ToList();
            var types = owned.SelectMany(k => data.Species.First(s => s.Key == k).Types).Distinct().Order(StringComparer.Ordinal).ToList();
            reports.Add(new ScoutReport(cap.Label, cap.Level, owned, types));
        }

        return reports;
    }
}

/// <summary>Checks hand-made teams against the Champion Lab rules (warnings: they never block saving).</summary>
public static class LabChecks
{
    public static IEnumerable<ValidationIssue> Run(GameData data, LabSettings lab, StarterSet? starters)
    {
        var species = data.Species.ToDictionary(s => s.Key, StringComparer.Ordinal);
        var scout = lab.FairPlay ? Scouting.Run(data) : [];
        foreach (var t in data.Trainers.Where(t => t.LockedReason is null && t.Party.Count > 0 && t.Key != "TRAINER_NONE"))
        {
            var group = Lab.GroupOf(data, lab, t);
            var difficulty = Lab.DifficultyOf(t);
            var rules = lab.RulesFor(group, difficulty);
            var who = $"{data.Catalog.NameOf(t.TrainerClass)} {t.Name}".Trim();
            ValidationIssue W(string message, string? suggestion = null) =>
                new(IssueSeverity.Warning, "Trainer", who, message, suggestion) { TargetKey = t.Key };
            var context = $"{Lab.Label(group)}, {Lab.Label(difficulty)}";
            var members = t.Party.Select(p => species.GetValueOrDefault(p.Species)).Where(s => s is not null).Select(s => s!).ToList();

            int Count(SpeciesTags tag) => members.Count(s => s.Tags.HasFlag(tag));
            var restricted = Count(SpeciesTags.RestrictedLegendary);
            var sub = Count(SpeciesTags.SubLegendary);
            if (rules.MaxRestricted >= 0 && restricted > rules.MaxRestricted)
            {
                yield return W($"has {restricted} restricted legendary Pokémon; {context} allows {rules.MaxRestricted}.");
            }

            var subAllowed = rules.RestrictedOrSub ? Math.Max(0, rules.MaxSubLegendary - (restricted * rules.MaxSubLegendary)) : rules.MaxSubLegendary;
            if (rules.MaxSubLegendary >= 0 && sub > subAllowed)
            {
                yield return W(rules.RestrictedOrSub && restricted > 0
                    ? $"has a restricted legendary and {sub} sub-legendaries; {context} allows one restricted or {rules.MaxSubLegendary} sub-legendaries."
                    : $"has {sub} sub-legendary Pokémon; {context} allows {subAllowed}.");
            }

            foreach (var (tag, name, max) in new[]
            {
                (SpeciesTags.Mythical, "mythical", rules.MaxMythical), (SpeciesTags.UltraBeast, "Ultra Beast", rules.MaxUltraBeast), (SpeciesTags.Paradox, "Paradox", rules.MaxParadox),
            })
            {
                if (max >= 0 && Count(tag) > max)
                {
                    yield return W($"has {Count(tag)} {name} Pokémon; {context} allows {max}.");
                }
            }

            if (rules.MaxMega >= 0 && Count(SpeciesTags.Mega) > rules.MaxMega)
            {
                yield return W($"has {Count(SpeciesTags.Mega)} Mega/Primal Pokémon; at most {rules.MaxMega} per team.");
            }

            if (rules.SpeciesClause && t.Party.GroupBy(p => p.Species).FirstOrDefault(g => g.Count() > 1) is { } dupe)
            {
                yield return W($"uses {Name(data, dupe.Key)} twice (Species Clause).", "Swap one for a different Pokémon.");
            }

            if (rules.ItemClause && t.Party.Where(p => p.HeldItem is { } i && i != "ITEM_NONE").GroupBy(p => p.HeldItem).FirstOrDefault(g => g.Count() > 1) is { } sameItem)
            {
                yield return W($"gives {data.Catalog.NameOf(sameItem.Key)} to more than one Pokémon (Item Clause, {context}).");
            }

            // type theme
            if (lab.Themes.TryGetValue(Lab.BaseKey(t), out var theme))
            {
                var off = members.Where(s => !s.Types.Contains(theme.Type)).ToList();
                if (off.Count > theme.OffTypeAllowed)
                {
                    yield return W($"is a {data.Catalog.NameOf(theme.Type)}-type specialist, but {string.Join(", ", off.Select(s => s.DisplayName))} {(off.Count == 1 ? "isn't" : "aren't")} {data.Catalog.NameOf(theme.Type)} type.",
                        theme.OffTypeAllowed == 0 ? "Swap them, or allow an off-type Pokémon in the theme settings." : null);
                }

                if (lab.UniqueTypeCombos && members.GroupBy(s => string.Join("/", s.Types)).FirstOrDefault(g => g.Count() > 1) is { } same)
                {
                    yield return W($"has more than one {string.Join("/", same.Key.Split('/').Select(data.Catalog.NameOf))} Pokémon; varied second types cover more weaknesses.");
                }
            }

            // story locks
            var milestone = Lab.MilestoneOf(data, t);
            foreach (var p in t.Party)
            {
                if (lab.StoryLocks.TryGetValue(p.Species, out var unlock) && milestone >= 0 && milestone < unlock)
                {
                    var caps = data.Rules!.LevelCaps;
                    yield return W($"uses {Name(data, p.Species)} before {caps[Math.Min(unlock, caps.Count - 1)].Label}, when the story introduces it.");
                }
            }

            // fair play: no more legendaries than the player could own by then
            if (lab.FairPlay && milestone >= 0 && milestone < scout.Count && restricted + sub > 0)
            {
                var available = scout[milestone].Species.Count(k => species.TryGetValue(k, out var s) && (s.Tags & (SpeciesTags.RestrictedLegendary | SpeciesTags.SubLegendary)) != 0);
                if (restricted + sub > available)
                {
                    yield return W($"has {restricted + sub} legendary Pokémon, but the player can catch only {available} before {scout[milestone].Milestone}.",
                        "Fair play: give bosses legendaries only when the player can have them too.");
                }
            }
        }

        // the rival keeps their starter in every fight
        if (lab.RivalKeepsStarter && starters is { Species.Count: 3 })
        {
            foreach (var issue in RivalChecks(data, lab, starters))
            {
                yield return issue;
            }
        }
    }

    /// <summary>
    /// Rival fights come in one version per starter. Projects differ in what the suffix means (Emerald: the player's
    /// pick, so May_…_MUDKIP has Treecko; FireRed: the rival's own, so RIVAL_…_SQUIRTLE has Squirtle); the convention is
    /// read from the project's own teams, then every rival fight must keep that starter line.
    /// </summary>
    /// <summary>Levels past evolution before warning (the original games often run a level or two late).</summary>
    public const int EvolutionGrace = 3;

    public static IEnumerable<ValidationIssue> RivalChecks(GameData data, LabSettings lab, StarterSet starters)
    {
        var rivals = data.Trainers
            .Where(t => t.LockedReason is null && t.Party.Count > 0 && Lab.GroupOf(data, lab, t) == TrainerGroup.Rival)
            .Select(t => (Trainer: t, Suffix: starters.Species.FirstOrDefault(s => Lab.BaseKey(t).EndsWith(s.Replace("SPECIES_", "_", StringComparison.Ordinal), StringComparison.Ordinal))))
            .Where(x => x.Suffix is not null)
            .ToList();
        if (rivals.Count == 0)
        {
            return [];
        }

        var families = starters.Species.ToDictionary(s => s, s => Starters.Family(data, s), StringComparer.Ordinal);
        bool HasLine(Trainer t, string starter) => t.Party.Any(p => families[starter].Contains(p.Species));
        var ownCount = rivals.Count(x => HasLine(x.Trainer, x.Suffix!));
        var counterCount = rivals.Count(x => Starters.Counter(data, starters, x.Suffix!) is { } c && HasLine(x.Trainer, c));
        var suffixIsRivals = ownCount >= counterCount;

        var issues = new List<ValidationIssue>();
        foreach (var (t, suffix) in rivals)
        {
            var expected = suffixIsRivals ? suffix! : Starters.Counter(data, starters, suffix!);
            if (expected is null)
            {
                continue;
            }

            var who = $"{data.Catalog.NameOf(t.TrainerClass)} {t.Name}".Trim();
            var member = t.Party.FirstOrDefault(p => families[expected].Contains(p.Species));
            if (member is null)
            {
                var why = suffixIsRivals ? "their starter" : $"the starter that beats the player's {Name(data, suffix!)}";
                issues.Add(new ValidationIssue(IssueSeverity.Warning, "Trainer", who, $"doesn't have the {Name(data, expected)} line, {why}.",
                    "Rivals keep their starter from the first fight to the last.") { TargetKey = t.Key });
            }
            else if (Progression.EvolutionPaths.EvolvedForm(data, member.Species, member.Level - EvolutionGrace, int.MaxValue) is var evolved && evolved != member.Species)
            {
                issues.Add(new ValidationIssue(IssueSeverity.Warning, "Trainer", who,
                    $"has a level {member.Level} {Name(data, member.Species)}; by then it would have evolved into {Name(data, evolved)}.") { TargetKey = t.Key });
            }
        }

        return issues;
    }

    private static string Name(GameData data, string key) => data.Species.FirstOrDefault(s => s.Key == key)?.DisplayName ?? data.Catalog.NameOf(key);
}
