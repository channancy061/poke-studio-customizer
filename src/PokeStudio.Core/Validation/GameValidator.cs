using PokeStudio.Core.Changes;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;

namespace PokeStudio.Core.Validation;

public enum IssueSeverity
{
    /// <summary>Must be fixed before saving: the game would not build or would misbehave.</summary>
    Error,

    /// <summary>Worth a look; saving is allowed.</summary>
    Warning,
}

/// <summary>A problem explained in plain words, optionally with a one-click fix (applied through undo).</summary>
public sealed record ValidationIssue(IssueSeverity Severity, string Area, string Subject, string Message, string? Suggestion = null)
{
    /// <summary>Creates the change that fixes the problem; null when there is no automatic fix.</summary>
    public Func<GameData, IChange>? Fix { get; init; }

    /// <summary>Key of the species/learnset the issue belongs to, so the UI can jump there.</summary>
    public string? TargetKey { get; init; }
}

/// <summary>Checks every module gets for free. Modules add their own via <see cref="IGameModule.Validate"/>.</summary>
public static class GameValidator
{
    public static IReadOnlyList<ValidationIssue> Run(GameData data, IGameModule module)
    {
        var issues = new List<ValidationIssue>();
        var limits = module.Limits;
        var speciesKeys = new HashSet<string>(data.Species.Select(s => s.Key), StringComparer.Ordinal);
        var moveKeys = new HashSet<string>(data.Catalog.Moves.Select(m => m.Key), StringComparer.Ordinal);

        for (var i = 0; i < data.Species.Count; i++)
        {
            CheckSpecies(data, i, limits, issues);
        }

        for (var i = 0; i < data.Learnsets.Count; i++)
        {
            CheckLearnset(data, i, limits, moveKeys, issues);
        }

        foreach (var set in data.Evolutions.Where(e => e.LockedReason is null))
        {
            CheckEvolutions(data, set, limits, speciesKeys, issues);
        }

        foreach (var move in data.Moves.Where(m => !m.LockedFields.ContainsKey("*")))
        {
            CheckMove(move, limits, issues);
        }

        if (data.Trainers.Count > 0)
        {
            var species = data.Species.ToDictionary(s => s.Key, StringComparer.Ordinal);
            var items = new HashSet<string>(data.Catalog.Items.Select(i => i.Key), StringComparer.Ordinal);
            foreach (var t in data.Trainers.Where(t => t.LockedReason is null))
            {
                CheckTrainer(data, t, limits, species, moveKeys, items, issues);
            }
        }

        CheckPhase6(data, module, limits, speciesKeys, issues);
        issues.AddRange(module.Validate(data));
        return issues;
    }

    private static void CheckSpecies(GameData data, int index, GameLimits limits, List<ValidationIssue> issues)
    {
        var s = data.Species[index];
        if (!s.IsEnabled)
        {
            return;
        }

        var who = s.DisplayName;
        void Add(IssueSeverity sev, string message, string? suggestion = null, Func<GameData, IChange>? fix = null) =>
            issues.Add(new ValidationIssue(sev, "Pokémon", who, message, suggestion) { Fix = fix, TargetKey = s.Key });

        if (string.IsNullOrWhiteSpace(s.Name))
        {
            Add(IssueSeverity.Error, "has no name.", "Type a name.");
        }
        else if (s.Name.Length > limits.SpeciesNameLength)
        {
            Add(IssueSeverity.Error,
                $"name “{s.Name}” is {s.Name.Length} letters; this game allows {limits.SpeciesNameLength}.",
                $"Shorten it to “{s.Name[..limits.SpeciesNameLength]}”?",
                d => SpeciesChange(d, s.Key, "Name", x => x with { Name = x.Name[..limits.SpeciesNameLength] }));
        }

        var stats = new[] { ("HP", s.Stats.Hp), ("Attack", s.Stats.Attack), ("Defense", s.Stats.Defense), ("Sp. Atk", s.Stats.SpAttack), ("Sp. Def", s.Stats.SpDefense), ("Speed", s.Stats.Speed) };
        foreach (var (label, value) in stats.Where(x => x.Item2 is < 1 or > BaseStats.Max))
        {
            Add(IssueSeverity.Error, $"{label} is {value}; it must be between 1 and {BaseStats.Max}.");
        }

        if (s.CatchRate is < 0 or > 255)
        {
            Add(IssueSeverity.Error, $"catch rate is {s.CatchRate}; it must be between 0 and 255.");
        }

        if (s.Types.Count == 2 && s.Types[0] == s.Types[1])
        {
            Add(IssueSeverity.Warning, $"has {data.Catalog.NameOf(s.Types[0])} as both types.", "Make it a single-type Pokémon?",
                d => SpeciesChange(d, s.Key, "Types", x => x with { Types = [x.Types[0]] }));
        }

        if (s.Abilities.Count > 0 && s.Abilities[0] == "ABILITY_NONE")
        {
            Add(IssueSeverity.Warning, "has no first ability, so it may end up with none.", "Pick a first ability.");
        }

        var lines = s.DexEntry.Split('\n');
        if (lines.Length > limits.DexEntryMaxLines)
        {
            Add(IssueSeverity.Warning, $"Pokédex entry has {lines.Length} lines; only {limits.DexEntryMaxLines} fit on screen.");
        }

        var longLine = lines.FirstOrDefault(l => l.Length > limits.DexEntryLineLength);
        if (longLine is not null)
        {
            Add(IssueSeverity.Warning, $"a Pokédex line is {longLine.Length} characters and may run off the screen (about {limits.DexEntryLineLength} fit).", "Press Enter to break the line.");
        }
    }

    private static void CheckLearnset(GameData data, int index, GameLimits limits, HashSet<string> moveKeys, List<ValidationIssue> issues)
    {
        var l = data.Learnsets[index];
        var who = l.UsedBy.Count > 0 ? string.Join(", ", l.UsedBy.Take(2).Select(k => data.Species.FirstOrDefault(s => s.Key == k)?.DisplayName ?? k)) : l.Key;
        void Add(IssueSeverity sev, string message, string? suggestion = null, Func<GameData, IChange>? fix = null) =>
            issues.Add(new ValidationIssue(sev, "Learnset", who, message, suggestion) { Fix = fix, TargetKey = l.Key });

        var levelOne = l.LevelUp.Where(m => m.Level == 1).ToList();
        if (levelOne.Count > limits.MovesKnown)
        {
            var extra = levelOne.Count - limits.MovesKnown;
            Add(IssueSeverity.Warning,
                $"learns {levelOne.Count} moves at level 1 but can only hold {limits.MovesKnown}.",
                $"Remove the first {extra} so the last {limits.MovesKnown} stay?",
                d => LearnsetChange(d, l.Key, $"Removed {extra} extra level-1 move(s)", x =>
                {
                    var drop = x.LevelUp.Where(m => m.Level == 1).Take(extra).ToHashSet();
                    return x with { LevelUp = x.LevelUp.Where(m => !drop.Contains(m)).ToList() };
                }));
        }

        foreach (var bad in l.LevelUp.Where(m => m.Level < 0 || m.Level > limits.MaxLevel))
        {
            Add(IssueSeverity.Error, $"learns {data.Catalog.NameOf(bad.Move)} at level {bad.Level}; levels go from 1 to {limits.MaxLevel}.");
        }

        var dupes = l.LevelUp.GroupBy(m => m).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        foreach (var d in dupes)
        {
            Add(IssueSeverity.Warning, $"learns {data.Catalog.NameOf(d.Move)} twice at level {d.Level}.", "Remove the duplicate?",
                g => LearnsetChange(g, l.Key, $"Removed duplicate {g.Catalog.NameOf(d.Move)}", x =>
                {
                    var seen = new HashSet<LevelUpMove>();
                    return x with { LevelUp = x.LevelUp.Where(seen.Add).ToList() };
                }));
        }

        if (moveKeys.Count > 0)
        {
            foreach (var unknown in l.LevelUp.Select(m => m.Move).Concat(l.EggMoves).Where(m => !moveKeys.Contains(m)).Distinct())
            {
                Add(IssueSeverity.Error, $"uses “{unknown}”, which isn't a move in this game.", "Pick a different move.");
            }
        }
    }

    private static void CheckEvolutions(GameData data, EvolutionSet set, GameLimits limits, HashSet<string> speciesKeys, List<ValidationIssue> issues)
    {
        var who = data.Species.FirstOrDefault(s => s.Key == set.FromSpecies)?.DisplayName ?? set.FromSpecies;
        foreach (var e in set.Evolutions)
        {
            if (!speciesKeys.Contains(e.TargetSpecies))
            {
                issues.Add(new ValidationIssue(IssueSeverity.Error, "Evolution", who, $"evolves into “{e.TargetSpecies}”, which doesn't exist in this game.", "Pick another Pokémon.") { TargetKey = set.FromSpecies });
            }

            if (e.Method == "EVO_LEVEL" && int.TryParse(e.Parameter, out var lvl) && (lvl < 0 || lvl > limits.MaxLevel))
            {
                issues.Add(new ValidationIssue(IssueSeverity.Error, "Evolution", who, $"evolves at level {lvl}; levels go from 1 to {limits.MaxLevel}.") { TargetKey = set.FromSpecies });
            }
        }

        if (set.Evolutions.Count > limits.MaxEvolutionsPerSpecies)
        {
            issues.Add(new ValidationIssue(IssueSeverity.Warning, "Evolution", who, $"has {set.Evolutions.Count} evolutions; the game handles up to {limits.MaxEvolutionsPerSpecies}.") { TargetKey = set.FromSpecies });
        }
    }

    private static void CheckMove(Move m, GameLimits limits, List<ValidationIssue> issues)
    {
        void Add(IssueSeverity sev, string message, string? suggestion = null, Func<GameData, IChange>? fix = null) =>
            issues.Add(new ValidationIssue(sev, "Move", m.Name.Length > 0 ? m.Name : m.Key, message, suggestion) { Fix = fix, TargetKey = m.Key });

        if (string.IsNullOrWhiteSpace(m.Name))
        {
            Add(IssueSeverity.Error, "has no name.", "Type a name.");
        }
        else if (m.Name.Length > limits.MoveNameLength)
        {
            var shorter = m.Name[..limits.MoveNameLength].TrimEnd();
            Add(IssueSeverity.Error, $"name is {m.Name.Length} letters; this game allows {limits.MoveNameLength}.", $"Shorten it to “{shorter}”?",
                d => MoveChange(d, m.Key, "name", x => x with { Name = shorter }));
        }

        if (m.Power < 0 || m.Power > limits.MaxMovePower)
        {
            Add(IssueSeverity.Error, $"has power {m.Power}; it must be 0 to {limits.MaxMovePower}.");
        }

        if (m.Category == MoveCategory.Status && m.Power > 0)
        {
            Add(IssueSeverity.Warning, $"is a status move but has power {m.Power}. Status moves don't deal damage, so the power is ignored.",
                "Set the power to 0, or make it Physical or Special.", d => MoveChange(d, m.Key, "power", x => x with { Power = 0 }));
        }
        else if (m.Category != MoveCategory.Status && m.Power == 0 && m.Effect == "EFFECT_HIT")
        {
            Add(IssueSeverity.Warning, "is a damaging move with 0 power, so it won't do any damage.", "Give it some power, or make it a status move.");
        }

        if (m.Accuracy is < 0 or > 100)
        {
            Add(IssueSeverity.Error, $"has {m.Accuracy}% accuracy; it must be 0 to 100 (0 = never misses).");
        }

        if (m.Pp < 1 || m.Pp > limits.MaxMovePp)
        {
            Add(IssueSeverity.Error, $"has {m.Pp} PP; it must be 1 to {limits.MaxMovePp}.");
        }
        else if (m.Pp > 40)
        {
            Add(IssueSeverity.Warning, $"has {m.Pp} PP. No official move has more than 40, and PP Ups raise it further.");
        }

        if (m.Priority < limits.MinMovePriority || m.Priority > limits.MaxMovePriority)
        {
            Add(IssueSeverity.Error, $"has priority {m.Priority}; it must be {limits.MinMovePriority} to +{limits.MaxMovePriority}.");
        }

        var lines = m.Description.Split('\n').Length;
        if (m.Description.Length > 0 && lines > limits.MoveDescriptionMaxLines)
        {
            Add(IssueSeverity.Warning, $"description has {lines} lines; the summary screen shows {limits.MoveDescriptionMaxLines}.");
        }
    }

    private static void CheckTrainer(GameData data, Trainer t, GameLimits limits, Dictionary<string, Species> species,
        HashSet<string> moveKeys, HashSet<string> items, List<ValidationIssue> issues)
    {
        var who = t.Name.Length > 0 ? $"{data.Catalog.NameOf(t.TrainerClass)} {t.Name}" : t.Key;
        void Add(IssueSeverity sev, string message, string? suggestion = null, Func<GameData, IChange>? fix = null) =>
            issues.Add(new ValidationIssue(sev, "Trainer", who, message, suggestion) { Fix = fix, TargetKey = t.Key });

        if (t.Key == "TRAINER_NONE")
        {
            return;
        }

        if (t.Name.Length > limits.TrainerNameLength)
        {
            Add(IssueSeverity.Error, $"name “{t.Name}” is {t.Name.Length} letters; this game allows {limits.TrainerNameLength}.", null,
                d => TrainerChange(d, t.Key, "name", x => x with { Name = x.Name[..limits.TrainerNameLength] }));
        }

        if (t.Party.Count == 0)
        {
            Add(IssueSeverity.Error, "has no Pokémon.", "Add at least one Pokémon to the team.");
        }
        else if (t.Party.Count > limits.MaxPartySize)
        {
            Add(IssueSeverity.Error, $"has {t.Party.Count} Pokémon; a team holds {limits.MaxPartySize}.");
        }

        if (t.Battle == BattleType.Double && t.Party.Count == 1)
        {
            Add(IssueSeverity.Error, "is set to a double battle but has only one Pokémon.", "Add a second Pokémon, or make it a single battle.",
                d => TrainerChange(d, t.Key, "battle type", x => x with { Battle = BattleType.Single }));
        }

        if (t.Items.Count > limits.MaxTrainerItems)
        {
            Add(IssueSeverity.Error, $"carries {t.Items.Count} items; trainers can carry {limits.MaxTrainerItems}.");
        }

        foreach (var item in t.Items.Where(i => items.Count > 0 && !items.Contains(i)))
        {
            Add(IssueSeverity.Error, $"carries “{item}”, which isn't an item in this game.");
        }

        for (var i = 0; i < t.Party.Count; i++)
        {
            var p = t.Party[i];
            var slot = i;
            var name = species.TryGetValue(p.Species, out var sp) ? sp.DisplayName : p.Species;
            if (sp is null)
            {
                Add(IssueSeverity.Error, $"has “{p.Species}”, which isn't a Pokémon in this game.");
                continue;
            }

            if (!sp.IsEnabled)
            {
                Add(IssueSeverity.Error, $"uses {name}, which is switched off in your game's settings.");
            }

            if (p.Level < 1 || p.Level > limits.MaxLevel)
            {
                var clamped = Math.Clamp(p.Level, 1, limits.MaxLevel);
                Add(IssueSeverity.Error, $"has {name} at level {p.Level}; levels go from 1 to {limits.MaxLevel}.", $"Set it to {clamped}?",
                    d => TrainerChange(d, t.Key, "level", x => x with { Party = ReplaceAt(x.Party, slot, x.Party[slot] with { Level = clamped }) }));
            }

            if (p.Moves.Count > limits.MovesKnown)
            {
                Add(IssueSeverity.Error, $"gives {name} {p.Moves.Count} moves; a Pokémon knows {limits.MovesKnown}.");
            }

            foreach (var m in p.Moves.Where(m => moveKeys.Count > 0 && !moveKeys.Contains(m)))
            {
                Add(IssueSeverity.Error, $"gives {name} the move “{m}”, which this game doesn't have.");
            }

            if (p.HeldItem is { } held && items.Count > 0 && !items.Contains(held))
            {
                Add(IssueSeverity.Error, $"gives {name} “{held}” to hold, which isn't an item in this game.");
            }

            if (p.Ability is { } ability && sp.Abilities.Count > 0 && !sp.Abilities.Contains(ability))
            {
                Add(IssueSeverity.Warning, $"gives {name} the ability {data.Catalog.NameOf(ability)}, which it can't normally have.",
                    "That's allowed, but players may find it surprising.");
            }

            if (p.Nickname is { Length: > 0 } nick && nick.Length > limits.NicknameLength)
            {
                Add(IssueSeverity.Warning, $"nickname “{nick}” is longer than {limits.NicknameLength} letters and will be cut short.");
            }

            if (p.Ivs is { } iv && new[] { iv.Hp, iv.Attack, iv.Defense, iv.SpAttack, iv.SpDefense, iv.Speed }.Any(v => v is < 0 or > 31))
            {
                Add(IssueSeverity.Error, $"gives {name} IVs outside 0–31.");
            }

            if (p.Evs is { } ev)
            {
                var all = new[] { ev.Hp, ev.Attack, ev.Defense, ev.SpAttack, ev.SpDefense, ev.Speed };
                if (all.Any(v => v is < 0 or > 255))
                {
                    Add(IssueSeverity.Error, $"gives {name} EVs outside 0–255.");
                }
                else if (all.Sum() > 510)
                {
                    Add(IssueSeverity.Warning, $"gives {name} {all.Sum()} EVs in total; a player's Pokémon can have 510.");
                }
            }

            if (p.Moves.Count > 0 && data.LearnsetFor(sp) is { } learnset && learnset.LevelUp.Count > 0)
            {
                var knows = learnset.LevelUp.Select(x => x.Move).Concat(learnset.EggMoves).Concat(learnset.Teachable).ToHashSet(StringComparer.Ordinal);
                var odd = p.Moves.Where(m => !knows.Contains(m)).ToList();
                if (odd.Count > 0 && !learnset.LockedParts.ContainsKey("teachable"))
                {
                    Add(IssueSeverity.Warning, $"gives {name} {string.Join(", ", odd.Select(data.Catalog.NameOf))}, which it can't learn normally.",
                        "Fine for a challenge hack; just make sure it's on purpose.");
                }
            }
        }
    }

    private static void CheckPhase6(GameData data, IGameModule module, GameLimits limits, HashSet<string> speciesKeys, List<ValidationIssue> issues)
    {
        var enabled = data.Species.Where(s => s.IsEnabled).Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var e in data.Encounters.Where(e => e.LockedReason is null))
        {
            var where = PlaceName(e);
            foreach (var m in e.Methods)
            {
                foreach (var s in m.Slots)
                {
                    void Add(IssueSeverity sev, string msg) => issues.Add(new ValidationIssue(sev, "Encounter", where, msg) { TargetKey = e.Key });
                    if (!speciesKeys.Contains(s.Species))
                    {
                        Add(IssueSeverity.Error, $"has “{s.Species}” in the wild, which isn't a Pokémon in this game.");
                    }
                    else if (!enabled.Contains(s.Species))
                    {
                        Add(IssueSeverity.Error, $"has {data.Species.First(x => x.Key == s.Species).DisplayName} in the wild, but it's switched off in your settings.");
                    }

                    if (s.MinLevel < 1 || s.MaxLevel > limits.MaxLevel)
                    {
                        Add(IssueSeverity.Error, $"has a wild level outside 1–{limits.MaxLevel}.");
                    }
                    else if (s.MinLevel > s.MaxLevel)
                    {
                        Add(IssueSeverity.Error, $"has a slot from level {s.MinLevel} to {s.MaxLevel}; the lowest level must come first.");
                    }
                }
            }
        }

        var itemKeys = data.Catalog.Items.Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var item in data.Items.Where(i => !i.LockedFields.ContainsKey("*")))
        {
            void Add(IssueSeverity sev, string msg, string? suggestion = null) => issues.Add(new ValidationIssue(sev, "Item", item.Name, msg, suggestion) { TargetKey = item.Key });
            if (item.Name.Length > limits.ItemNameLength)
            {
                Add(IssueSeverity.Error, $"name is {item.Name.Length} letters; this game allows {limits.ItemNameLength}.");
            }

            if (item.Price < 0 || item.Price > limits.MaxPrice)
            {
                Add(IssueSeverity.Error, $"costs {item.Price}; prices go from 0 to {limits.MaxPrice:N0}.");
            }
        }

        foreach (var mart in data.Marts.Where(m => m.LockedReason is null))
        {
            void Add(IssueSeverity sev, string msg, string? suggestion = null) => issues.Add(new ValidationIssue(sev, "Mart", mart.Place, msg, suggestion) { TargetKey = mart.Key });
            if (mart.Items.Count == 0)
            {
                Add(IssueSeverity.Error, "sells nothing. A shop needs at least one item.");
            }

            foreach (var i in mart.Items.Concat(mart.BadgeTiers.Values.SelectMany(v => v)).Where(i => itemKeys.Count > 0 && !itemKeys.Contains(i)).Distinct())
            {
                Add(IssueSeverity.Error, $"sells “{i}”, which isn't an item in this game.");
            }

            foreach (var i in mart.Items.Where(i => data.Items.FirstOrDefault(x => x.Key == i) is { Price: 0 } && i != "ITEM_RARE_CANDY").Distinct())
            {
                Add(IssueSeverity.Warning, $"sells {data.Catalog.NameOf(i)} for nothing (its price is 0).", "Fine if you meant to give it away.");
            }
        }

        if (limits.TextLineWidth is { } width)
        {
            foreach (var t in data.Texts.Where(t => t.LockedReason is null))
            {
                foreach (var line in TextLines(t.Text))
                {
                    if (module.MeasureText(line) is { } px && px > width)
                    {
                        issues.Add(new ValidationIssue(IssueSeverity.Warning, "Text", t.Place.Length > 0 ? t.Place : t.Key,
                            $"line “{Shorten(line)}” is {px} pixels wide; the text box fits {width}.", "Break it into two lines.") { TargetKey = t.Key });
                    }
                }
            }
        }

        if (data.Rules is { } rules)
        {
            for (var i = 1; i < rules.LevelCaps.Count; i++)
            {
                if (rules.LevelCaps[i].Level < rules.LevelCaps[i - 1].Level)
                {
                    issues.Add(new ValidationIssue(IssueSeverity.Warning, "Level caps", rules.LevelCaps[i].Label,
                        $"cap {rules.LevelCaps[i].Level} is lower than the one before it ({rules.LevelCaps[i - 1].Level})."));
                }
            }

            if (rules.LevelCaps.Any(c => c.Level < 1 || c.Level > limits.MaxLevel))
            {
                issues.Add(new ValidationIssue(IssueSeverity.Error, "Level caps", "Level caps", $"must be between 1 and {limits.MaxLevel}."));
            }
        }
    }

    /// <summary>Visible lines of a text: split at the "\n", "\l" and "\p" codes; placeholders like {PLAYER} kept.</summary>
    public static IEnumerable<string> TextLines(string text) =>
        text.Replace("\\l", "\\n", StringComparison.Ordinal).Replace("\\p", "\\n", StringComparison.Ordinal).Split("\\n");

    private static string Shorten(string s) => s.Length <= 40 ? s : s[..37] + "…";

    public static string PlaceName(EncounterTable e) => e.Map.Length > 0 ? GameCatalog.Prettify(e.Map.Replace("MAP_", "", StringComparison.Ordinal)) : e.Key;

    private static IReadOnlyList<TrainerPokemon> ReplaceAt(IReadOnlyList<TrainerPokemon> list, int index, TrainerPokemon value)
    {
        var copy = list.ToList();
        copy[index] = value;
        return copy;
    }

    private static IChange TrainerChange(GameData data, string key, string field, Func<Trainer, Trainer> update)
    {
        var index = data.Trainers.FindIndex(t => t.Key == key);
        var before = data.Trainers[index];
        return new ValueChange<Trainer>("Trainers", $"{before.Name} · fixed {field}", v => data.Trainers[index] = v, before, update(before));
    }

    private static IChange MoveChange(GameData data, string key, string field, Func<Move, Move> update)
    {
        var index = data.Moves.FindIndex(m => m.Key == key);
        var before = data.Moves[index];
        var after = update(before);
        return new ValueChange<Move>("Moves", $"{before.Name} · fixed {field}", v =>
        {
            data.Moves[index] = v;
            data.Catalog.RenameMove(v.Key, v.Name);
        }, before, after);
    }

    private static IChange SpeciesChange(GameData data, string key, string field, Func<Species, Species> update)
    {
        var index = data.Species.FindIndex(s => s.Key == key);
        var before = data.Species[index];
        var after = update(before);
        return new ValueChange<Species>("Pokémon", $"{before.DisplayName} · fixed {field.ToLowerInvariant()}", v => data.Species[index] = v, before, after);
    }

    private static IChange LearnsetChange(GameData data, string key, string description, Func<Learnset, Learnset> update)
    {
        var index = data.Learnsets.FindIndex(l => l.Key == key);
        var before = data.Learnsets[index];
        return new ValueChange<Learnset>("Learnsets", description, v => data.Learnsets[index] = v, before, update(before));
    }
}
