using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;
using PokeStudio.Core.Validation;

namespace PokeStudio.Modules.N3ds;

/// <summary>Reads and writes Gen 6 (X / Y / Omega Ruby / Alpha Sapphire) data.</summary>
internal static class N3dsData
{
    public const int SpeciesCount = 722;

    public static readonly string[] Types =
        ["NORMAL", "FIGHTING", "FLYING", "POISON", "GROUND", "ROCK", "BUG", "GHOST", "STEEL", "FIRE", "WATER", "GRASS", "ELECTRIC", "PSYCHIC", "ICE", "DRAGON", "DARK", "FAIRY"];

    private static readonly string[] Growth = ["MEDIUM_FAST", "ERRATIC", "FLUCTUATING", "MEDIUM_SLOW", "FAST", "SLOW"];

    private static readonly string[] EggGroups =
        ["NONE", "MONSTER", "WATER_1", "BUG", "FLYING", "FIELD", "FAIRY", "GRASS", "HUMAN_LIKE", "WATER_3", "MINERAL", "AMORPHOUS", "WATER_2", "DITTO", "DRAGON", "UNDISCOVERED"];

    private static SpeciesTags TagsFor(int dex) => dex switch
    {
        150 or 249 or 250 or 382 or 383 or 384 or 483 or 484 or 487 or 643 or 644 or 646 or 716 or 717 or 718 => SpeciesTags.RestrictedLegendary,
        151 or 251 or 385 or 386 or 489 or 490 or 491 or 492 or 493 or 494 or 647 or 648 or 649 or 719 or 720 or 721 => SpeciesTags.Mythical,
        144 or 145 or 146 or 243 or 244 or 245 or 377 or 378 or 379 or 380 or 381 or 480 or 481 or 482 or 485 or 486 or 488
            or 638 or 639 or 640 or 641 or 642 or 645 => SpeciesTags.SubLegendary,
        _ => SpeciesTags.None,
    };

    private static string Constant(string name)
    {
        var s = name.ToUpperInvariant().Replace("♀", "_F", StringComparison.Ordinal).Replace("♂", "_M", StringComparison.Ordinal).Replace("É", "E", StringComparison.Ordinal);
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                sb.Append(c);
            }
            else if ((c is ' ' or '-' or '_') && sb.Length > 0 && sb[^1] != '_')
            {
                sb.Append('_');
            }
        }

        return sb.ToString().Trim('_');
    }

    private static List<string> Text(N3dsModule.State s, int member) =>
        member < 0 ? [] : Gen6Text.Read(new Garc(s.File(s.Tables.Text)).Entries[member].Files[0]);

    public static GameData Read(N3dsModule.State s)
    {
        var t = s.Tables;
        var data = new GameData { HeldItemLevelEvolutions = false };
        var c = data.Catalog;
        foreach (var ty in Types)
        {
            c.Types.Add(new CatalogEntry("TYPE_" + ty, GameCatalog.Prettify(ty)));
        }

        foreach (var g in Growth)
        {
            c.GrowthRates.Add(new CatalogEntry("GROWTH_" + g, GameCatalog.Prettify(g)));
        }

        foreach (var g in EggGroups)
        {
            c.EggGroups.Add(new CatalogEntry("EGG_GROUP_" + g, g == "NONE" ? "None" : GameCatalog.Prettify(g)));
        }

        for (var b = 0; b < 32; b++)
        {
            c.AiFlags.Add(new CatalogEntry($"AI_FLAG_BIT_{b}", b switch { 0 => "Check bad moves", 1 => "Evaluate attacks", 2 => "Expert", 3 => "Setup first turn", _ => $"Behaviour {b}" }));
        }

        foreach (var (key, name) in new[] { ("EVO_LEVEL", "Level up"), ("EVO_ITEM", "Use an item"), ("EVO_TRADE", "Trade"), ("EVO_LEVEL_NINJASK", "Level (Nincada → Ninjask)"), ("EVO_LEVEL_SHEDINJA", "Level (Shedinja appears)") })
        {
            c.EvolutionMethods.Add(new CatalogEntry(key, name));
        }

        string Unique(string key, HashSet<string> used, int i) => used.Add(key) ? key : $"{key}_{i}";
        var used = new HashSet<string>(StringComparer.Ordinal);

        var abilityNames = Text(s, t.AbilityNames);
        var abilityKeys = new List<string>();
        for (var i = 0; i < abilityNames.Count; i++)
        {
            var key = i == 0 ? "ABILITY_NONE" : Unique("ABILITY_" + Constant(abilityNames[i]), used, i);
            abilityKeys.Add(key);
            s.AbilityIndex[key] = i;
            c.Abilities.Add(new CatalogEntry(key, i == 0 ? "None" : abilityNames[i]));
        }

        used.Clear();
        var itemNames = Text(s, t.ItemNames);
        var itemData = t.Items.Length > 0 ? new Garc(s.File(t.Items)) : null;
        var itemKeys = new List<string>();
        for (var i = 0; i < itemNames.Count; i++)
        {
            var name = itemNames[i];
            var key = i == 0 ? "ITEM_NONE" : Unique(name.Length == 0 || name.All(ch => ch is '?' or '-') ? $"ITEM_UNUSED_{i}" : "ITEM_" + Constant(name), used, i);
            itemKeys.Add(key);
            s.ItemIndex[key] = i;
            c.Items.Add(new CatalogEntry(key, i == 0 ? "None" : name));
            if (i == 0)
            {
                continue;
            }

            var hasData = itemData is not null && i < itemData.Count && itemData[i].Length >= 2;
            var item = new Item
            {
                Key = key,
                Name = name,
                Price = hasData ? BinaryPrimitives.ReadUInt16LittleEndian(itemData![i]) * 10 : 0,
                LockedFields = new Dictionary<string, string> { ["Description"] = "Item descriptions can't change for 3DS games yet." },
            };
            data.Items.Add(item);
            s.Items[key] = item;
        }

        used.Clear();
        var moveNames = Text(s, t.MoveNames);
        var moveKeys = new List<string>();
        for (var i = 0; i < moveNames.Count; i++)
        {
            var key = i == 0 ? "MOVE_NONE" : Unique("MOVE_" + Constant(moveNames[i]), used, i);
            moveKeys.Add(key);
            s.MoveIndex[key] = i;
            c.Moves.Add(new CatalogEntry(key, i == 0 ? "None" : moveNames[i]));
        }

        var moves = MiniPack.Read(new Garc(s.File(t.Moves))[0]);
        for (var i = 1; i < Math.Min(moveNames.Count, moves.Count); i++)
        {
            var d = moves[i];
            var move = new Move
            {
                Id = i,
                Key = moveKeys[i],
                Name = moveNames[i],
                Type = "TYPE_" + Types[Math.Min((int)d[0], Types.Length - 1)],
                Category = d[2] switch { 1 => MoveCategory.Physical, 2 => MoveCategory.Special, _ => MoveCategory.Status },
                Power = d[3],
                Accuracy = d[4],
                Pp = d[5],
                Priority = (sbyte)d[6],
                MakesContact = d.Length >= 36 && (BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(32)) & 1) != 0,
                LockedFields = new Dictionary<string, string> { ["Description"] = "Move descriptions can't change for 3DS games yet." },
            };
            data.Moves.Add(move);
            s.Moves[move.Key] = move;
        }

        foreach (var (i, m) in t.TmMoves.Select((m, i) => (i, m)))
        {
            var key = m < moveKeys.Count ? moveKeys[m] : "MOVE_NONE";
            s.TmMoves.Add(key);
            c.Machines.Add((i < 100 ? $"TM{i + 1:00}" : $"HM{i - 99:00}", key));
        }

        used.Clear();
        var speciesNames = Text(s, t.SpeciesNames);
        var speciesKeys = new List<string>();
        for (var i = 0; i < SpeciesCount; i++)
        {
            var key = i == 0 ? "SPECIES_NONE" : Unique("SPECIES_" + Constant(speciesNames[i]), used, i);
            speciesKeys.Add(key);
            s.SpeciesIndex[key] = i;
        }

        string SpeciesKey(int i) => i >= 0 && i < speciesKeys.Count ? speciesKeys[i] : "SPECIES_NONE";
        string MoveKey(int i) => i >= 0 && i < moveKeys.Count ? moveKeys[i] : "MOVE_NONE";
        string ItemKey(int i) => i >= 0 && i < itemKeys.Count ? itemKeys[i] : "ITEM_NONE";

        var personal = new Garc(s.File(t.Personal));
        var learn = new Garc(s.File(t.Learnsets));
        var evo = new Garc(s.File(t.Evolutions));
        for (var i = 1; i < SpeciesCount; i++)
        {
            var p = personal[i];
            var key = speciesKeys[i];
            var species = new Species
            {
                Id = i,
                Key = key,
                Name = speciesNames[i],
                Stats = new BaseStats(p[0], p[1], p[2], p[4], p[5], p[3]),
                Types = new[] { p[6], p[7] }.Select(x => "TYPE_" + Types[Math.Min((int)x, Types.Length - 1)]).Distinct().ToList(),
                Abilities = Enumerable.Range(0, 3).Select(k => p[24 + k]).Select(a => a < abilityKeys.Count ? abilityKeys[a] : "ABILITY_NONE").ToList(),
                HeldItemCommon = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(12)) is > 0 and var h1 ? ItemKey(h1) : null,
                HeldItemRare = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(14)) is > 0 and var h2 ? ItemKey(h2) : null,
                CatchRate = p[8],
                ExpYield = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(34)),
                Gender = p[18] switch { 0 => GenderRatio.AlwaysMale, 254 => GenderRatio.AlwaysFemale, 255 => GenderRatio.NoGender, var g => new GenderRatio(Math.Round(g * 100.0 / 256, 1)) },
                Growth = "GROWTH_" + Growth[Math.Min((int)p[21], Growth.Length - 1)],
                EggGroups = new[] { p[22], p[23] }.Select(x => "EGG_GROUP_" + EggGroups[Math.Min((int)x, EggGroups.Length - 1)]).Distinct().ToList(),
                NationalDexNumber = i,
                NationalDex = i,
                Tags = TagsFor(i),
                LearnsetKey = key,
                LockedFields = new Dictionary<string, string> { ["DexEntry"] = "Pokédex entries can't change for 3DS games yet.", ["Category"] = "Pokédex entries can't change for 3DS games yet." },
            };
            data.Species.Add(species);
            s.Species[key] = species;

            var l = learn[i];
            var levelUp = new List<LevelUpMove>();
            for (var o = 0; o + 3 < l.Length; o += 4)
            {
                var mv = BinaryPrimitives.ReadUInt16LittleEndian(l.AsSpan(o));
                if (mv == 0xFFFF)
                {
                    break;
                }

                levelUp.Add(new LevelUpMove(BinaryPrimitives.ReadUInt16LittleEndian(l.AsSpan(o + 2)), MoveKey(mv)));
            }

            var teach = Enumerable.Range(0, s.TmMoves.Count).Where(b => (p[40 + (b / 8)] >> (b % 8) & 1) != 0).Select(b => s.TmMoves[b]).Distinct().ToList();
            var learnset = new Learnset
            {
                Key = key,
                UsedBy = [key],
                LevelUp = levelUp,
                Teachable = teach,
                LockedParts = new Dictionary<string, string> { ["egg"] = "Egg moves can't change for 3DS games yet." },
            };
            data.Learnsets.Add(learnset);
            s.Learnsets[key] = learnset;

            var evos = new List<Evolution>();
            var ev = evo[i];
            for (var k = 0; k < 8 && (6 * k) + 5 < ev.Length; k++)
            {
                var method = BinaryPrimitives.ReadUInt16LittleEndian(ev.AsSpan(6 * k));
                if (method != 0)
                {
                    evos.Add(ToEvolution(method, BinaryPrimitives.ReadUInt16LittleEndian(ev.AsSpan((6 * k) + 2)), SpeciesKey(BinaryPrimitives.ReadUInt16LittleEndian(ev.AsSpan((6 * k) + 4))), ItemKey, MoveKey, SpeciesKey));
                }
            }

            var set = new EvolutionSet { FromSpecies = key, Evolutions = evos };
            data.Evolutions.Add(set);
            s.Evolutions[key] = set;
        }

        // trainers
        if (t.TrainerData.Length > 0 && t.TrainerParties.Length > 0)
        {
            var td = new Garc(s.File(t.TrainerData));
            var tp = new Garc(s.File(t.TrainerParties));
            var classNames = Text(s, t.ClassNames);
            var trainerNames = Text(s, t.TrainerNames);
            var maxClass = Enumerable.Range(0, td.Count).Max(i => td[i].Length > 3 ? td[i][2] : 0);
            used.Clear();
            for (var i = 0; i <= maxClass; i++)
            {
                var name = i < classNames.Count && classNames[i].Length > 0 ? classNames[i] : $"Class {i}";
                var key = Unique("TRAINER_CLASS_" + Constant(name), used, i);
                s.ClassIndex[key] = i;
                c.TrainerClasses.Add(new CatalogEntry(key, name));
            }

            var classKeys = s.ClassIndex.OrderBy(kv => kv.Value).Select(kv => kv.Key).ToList();
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 1; i < td.Count; i++)
            {
                var d = td[i];
                var name = i < trainerNames.Count ? trainerNames[i] : "";
                var baseKey = "TRAINER_" + (name.Length > 0 ? Constant(name) : $"UNNAMED_{i}");
                counts[baseKey] = counts.GetValueOrDefault(baseKey) + 1;
                var ai = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(16));
                var trainer = new Trainer
                {
                    Id = i,
                    Key = $"{baseKey}_{counts[baseKey]}",
                    Name = name,
                    TrainerClass = d[2] < classKeys.Count ? classKeys[d[2]] : classKeys[0],
                    Items = Enumerable.Range(0, 4).Select(k => (int)BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(8 + (2 * k)))).Where(x => x > 0).Select(ItemKey).ToList(),
                    AiFlags = Enumerable.Range(0, 32).Where(b => (ai >> b & 1) != 0).Select(b => c.AiFlags[b].Key).ToList(),
                    Party = ReadParty(d[0], d[7], tp[i], SpeciesKey, MoveKey, ItemKey),
                };
                data.Trainers.Add(trainer);
                s.Trainers[trainer.Key] = (trainer, i);
            }
        }

        c.Invalidate();
        return data;
    }

    private static int Stride(int flags) => 8 + ((flags & 2) != 0 ? 2 : 0) + ((flags & 1) != 0 ? 8 : 0);

    private static List<TrainerPokemon> ReadParty(int flags, int count, byte[] data, Func<int, string> species, Func<int, string> move, Func<int, string> item)
    {
        var stride = Stride(flags);
        var result = new List<TrainerPokemon>();
        for (var k = 0; k < count && stride * (k + 1) <= data.Length; k++)
        {
            var o = stride * k;
            var iv = data[o] * 31 / 255;
            var p = o + 8;
            string? held = null;
            if ((flags & 2) != 0)
            {
                var it = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p));
                held = it > 0 ? item(it) : null;
                p += 2;
            }

            var moves = (flags & 1) != 0
                ? Enumerable.Range(0, 4).Select(m => (int)BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p + (2 * m)))).Where(m => m > 0).Select(move).ToList()
                : [];
            result.Add(new TrainerPokemon
            {
                Species = species(BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(o + 4))),
                Level = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(o + 2)),
                HeldItem = held,
                Moves = moves,
                Ivs = new BaseStats(iv, iv, iv, iv, iv, iv),
            });
        }

        return result;
    }

    /// <summary>Gen 6 evolution methods (1–27 as in Gen 5, then 28–34 new) → the app's shared form.</summary>
    internal static Evolution ToEvolution(int method, int param, string target, Func<int, string> item, Func<int, string> move, Func<int, string> species)
    {
        Evolution E(string m, string p, params (string Kind, string Value)[] conds) => new(m, p, target) { Conditions = conds.Select(x => new EvolutionCondition(x.Kind, x.Value)).ToList() };
        var lv = param.ToString(CultureInfo.InvariantCulture);
        return method switch
        {
            1 => E("EVO_LEVEL", "0", ("IF_MIN_FRIENDSHIP", "220")),
            2 => E("EVO_LEVEL", "0", ("IF_MIN_FRIENDSHIP", "220"), ("IF_TIME", "TIME_DAY")),
            3 => E("EVO_LEVEL", "0", ("IF_MIN_FRIENDSHIP", "220"), ("IF_TIME", "TIME_NIGHT")),
            4 => E("EVO_LEVEL", lv),
            5 => E("EVO_TRADE", "0"),
            6 => E("EVO_TRADE", "0", ("IF_HOLD_ITEM", item(param))),
            7 => E("EVO_TRADE", "0", ("IF_TRADE_PARTNER_SPECIES", species(param))),
            8 => E("EVO_ITEM", item(param)),
            9 => E("EVO_LEVEL", lv, ("IF_ATK_GT_DEF", "")),
            10 => E("EVO_LEVEL", lv, ("IF_ATK_EQ_DEF", "")),
            11 => E("EVO_LEVEL", lv, ("IF_ATK_LT_DEF", "")),
            12 => E("EVO_LEVEL", lv, ("IF_SILCOON", "")),
            13 => E("EVO_LEVEL", lv, ("IF_CASCOON", "")),
            14 => E("EVO_LEVEL_NINJASK", lv),
            15 => E("EVO_LEVEL_SHEDINJA", lv),
            16 => E("EVO_LEVEL", "0", ("IF_MIN_BEAUTY", lv)),
            17 => E("EVO_ITEM", item(param), ("IF_GENDER", "MON_MALE")),
            18 => E("EVO_ITEM", item(param), ("IF_GENDER", "MON_FEMALE")),
            19 => E("EVO_LEVEL", "0", ("IF_HOLD_ITEM", item(param)), ("IF_TIME", "TIME_DAY")),
            20 => E("EVO_LEVEL", "0", ("IF_HOLD_ITEM", item(param)), ("IF_TIME", "TIME_NIGHT")),
            21 => E("EVO_LEVEL", "0", ("IF_KNOWS_MOVE", move(param))),
            22 => E("EVO_LEVEL", "0", ("IF_SPECIES_IN_PARTY", species(param))),
            23 => E("EVO_LEVEL", lv, ("IF_GENDER", "MON_MALE")),
            24 => E("EVO_LEVEL", lv, ("IF_GENDER", "MON_FEMALE")),
            25 => E("EVO_LEVEL", "0", ("IF_IN_MAPSEC", "LOCATION_MAGNETIC_FIELD")),
            26 => E("EVO_LEVEL", "0", ("IF_IN_MAPSEC", "LOCATION_MOSS_ROCK")),
            27 => E("EVO_LEVEL", "0", ("IF_IN_MAPSEC", "LOCATION_ICE_ROCK")),
            28 => E("EVO_LEVEL", lv, ("IF_UPSIDE_DOWN", "")),
            29 => E("EVO_LEVEL", "0", ("IF_MIN_FRIENDSHIP", "AFFECTION_2"), ("IF_KNOWS_MOVE_TYPE", "TYPE_" + Types[Math.Min(param, Types.Length - 1)])),
            30 => E("EVO_LEVEL", lv, ("IF_TYPE_IN_PARTY", "TYPE_DARK")),
            31 => E("EVO_LEVEL", lv, ("IF_WEATHER", "WEATHER_RAIN")),
            32 => E("EVO_LEVEL", lv, ("IF_TIME", "TIME_DAY")),
            33 => E("EVO_LEVEL", lv, ("IF_TIME", "TIME_NIGHT")),
            34 => E("EVO_LEVEL", lv, ("IF_GENDER", "MON_FEMALE"), ("IF_FORM", "1")),
            _ => E($"EVO_METHOD_{method}", lv),
        };
    }

    internal static (int Method, int Param)? FromEvolution(Evolution e, Func<string, int> item, Func<string, int> move, Func<string, int> species)
    {
        var kinds = string.Join("+", e.Conditions.Select(x => x.Kind).Order(StringComparer.Ordinal));
        string Cond(string kind) => e.Conditions.First(x => x.Kind == kind).Value;
        var time = e.Conditions.FirstOrDefault(x => x.Kind == "IF_TIME")?.Value;
        var female = e.Conditions.FirstOrDefault(x => x.Kind == "IF_GENDER")?.Value.Contains("FEMALE", StringComparison.Ordinal) == true;
        int Level() => int.TryParse(e.Parameter, CultureInfo.InvariantCulture, out var l) ? l : -1;
        return (e.Method, kinds) switch
        {
            ("EVO_LEVEL", "") when Level() is > 0 and <= 100 => (4, Level()),
            ("EVO_LEVEL", "IF_MIN_FRIENDSHIP") => (1, 0),
            ("EVO_LEVEL", "IF_MIN_FRIENDSHIP+IF_TIME") => (time == "TIME_NIGHT" ? 3 : 2, 0),
            ("EVO_TRADE", "") => (5, 0),
            ("EVO_TRADE", "IF_HOLD_ITEM") => (6, item(Cond("IF_HOLD_ITEM"))),
            ("EVO_TRADE", "IF_TRADE_PARTNER_SPECIES") => (7, species(Cond("IF_TRADE_PARTNER_SPECIES"))),
            ("EVO_ITEM", "") => (8, item(e.Parameter)),
            ("EVO_LEVEL", "IF_ATK_GT_DEF") when Level() > 0 => (9, Level()),
            ("EVO_LEVEL", "IF_ATK_EQ_DEF") when Level() > 0 => (10, Level()),
            ("EVO_LEVEL", "IF_ATK_LT_DEF") when Level() > 0 => (11, Level()),
            ("EVO_LEVEL", "IF_SILCOON") when Level() > 0 => (12, Level()),
            ("EVO_LEVEL", "IF_CASCOON") when Level() > 0 => (13, Level()),
            ("EVO_LEVEL_NINJASK", "") when Level() > 0 => (14, Level()),
            ("EVO_LEVEL_SHEDINJA", "") when Level() > 0 => (15, Level()),
            ("EVO_LEVEL", "IF_MIN_BEAUTY") => (16, int.TryParse(Cond("IF_MIN_BEAUTY"), CultureInfo.InvariantCulture, out var b) ? b : 170),
            ("EVO_ITEM", "IF_GENDER") => (female ? 18 : 17, item(e.Parameter)),
            ("EVO_LEVEL", "IF_HOLD_ITEM+IF_TIME") => (time == "TIME_NIGHT" ? 20 : 19, item(Cond("IF_HOLD_ITEM"))),
            ("EVO_LEVEL", "IF_KNOWS_MOVE") => (21, move(Cond("IF_KNOWS_MOVE"))),
            ("EVO_LEVEL", "IF_SPECIES_IN_PARTY") => (22, species(Cond("IF_SPECIES_IN_PARTY"))),
            ("EVO_LEVEL", "IF_GENDER") when Level() > 0 => (female ? 24 : 23, Level()),
            ("EVO_LEVEL", "IF_IN_MAPSEC") => (Cond("IF_IN_MAPSEC") switch { "LOCATION_MAGNETIC_FIELD" => 25, "LOCATION_MOSS_ROCK" => 26, _ => 27 }, 0),
            ("EVO_LEVEL", "IF_UPSIDE_DOWN") when Level() > 0 => (28, Level()),
            ("EVO_LEVEL", "IF_KNOWS_MOVE_TYPE+IF_MIN_FRIENDSHIP") => (29, Math.Max(0, Array.IndexOf(Types, Cond("IF_KNOWS_MOVE_TYPE").Replace("TYPE_", "", StringComparison.Ordinal)))),
            ("EVO_LEVEL", "IF_TYPE_IN_PARTY") when Level() > 0 => (30, Level()),
            ("EVO_LEVEL", "IF_WEATHER") when Level() > 0 => (31, Level()),
            ("EVO_LEVEL", "IF_TIME") when Level() > 0 => (time == "TIME_NIGHT" ? 33 : 32, Level()),
            ("EVO_LEVEL", "IF_FORM+IF_GENDER") when Level() > 0 => (34, Level()),
            _ => null,
        };
    }

    public static List<ValidationIssue> Check(N3dsModule.State s, GameData data, GameLimits limits)
    {
        var issues = new List<ValidationIssue>();
        void Error(string area, string subject, string message, string? key = null) =>
            issues.Add(new ValidationIssue(IssueSeverity.Error, area, subject, message) { TargetKey = key });
        foreach (var set in data.Evolutions)
        {
            if (set.Evolutions.Count > 8)
            {
                Error("Evolutions", set.FromSpecies, "These games allow up to 8 evolutions per Pokémon.", set.FromSpecies);
            }

            foreach (var e in set.Evolutions.Where(e => FromEvolution(e, _ => 0, _ => 0, _ => 0) is null))
            {
                Error("Evolutions", set.FromSpecies, $"\"{e.Method} {e.Parameter}\" with these conditions can't be done in this game.", set.FromSpecies);
            }
        }

        var tm = s.TmMoves.ToHashSet(StringComparer.Ordinal);
        foreach (var l in data.Learnsets.Where(l => tm.Count > 0 && l.Teachable.Any(m => !tm.Contains(m))))
        {
            Error("Learnsets", l.Key, "Only TM and HM moves can be taught in this game.", l.Key);
        }

        foreach (var t in data.Trainers.Where(t => t.Party.Count is 0 or > 6 || t.HasAdaptiveSlot))
        {
            Error("Trainers", t.Name, t.HasAdaptiveSlot ? "Adaptive boss slots need a project (decomp)." : "A trainer needs 1 to 6 Pokémon.", t.Key);
        }

        return issues;
    }

    /// <summary>Applies the changes; returns each archive that changed (path → new bytes).</summary>
    public static Dictionary<string, byte[]> Write(N3dsModule.State s, GameData data)
    {
        var t = s.Tables;
        var changed = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        int Species(string key) => s.SpeciesIndex.GetValueOrDefault(key);
        int Move(string key) => s.MoveIndex.GetValueOrDefault(key);
        int Item(string? key) => key is null ? 0 : s.ItemIndex.GetValueOrDefault(key);
        int Type(string key) => Math.Max(0, Array.IndexOf(Types, key.Replace("TYPE_", "", StringComparison.Ordinal)));
        void W16(byte[] b, int o, int v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o), (ushort)v);

        var personal = new Garc(s.File(t.Personal));
        var learn = new Garc(s.File(t.Learnsets));
        var evo = new Garc(s.File(t.Evolutions));
        bool p1 = false, l1 = false, e1 = false;
        var renamed = new Dictionary<int, string>();
        foreach (var sp in data.Species)
        {
            if (!s.Species.TryGetValue(sp.Key, out var before) || before == sp)
            {
                continue;
            }

            var i = Species(sp.Key);
            var p = personal[i];
            p[0] = (byte)sp.Stats.Hp;
            p[1] = (byte)sp.Stats.Attack;
            p[2] = (byte)sp.Stats.Defense;
            p[3] = (byte)sp.Stats.Speed;
            p[4] = (byte)sp.Stats.SpAttack;
            p[5] = (byte)sp.Stats.SpDefense;
            p[6] = (byte)Type(sp.Types.ElementAtOrDefault(0) ?? "TYPE_NORMAL");
            p[7] = (byte)Type(sp.Types.ElementAtOrDefault(1) ?? sp.Types.ElementAtOrDefault(0) ?? "TYPE_NORMAL");
            p[8] = (byte)sp.CatchRate;
            W16(p, 12, Item(sp.HeldItemCommon));
            W16(p, 14, Item(sp.HeldItemRare));
            p[18] = sp.Gender switch { { Genderless: true } => 255, { PercentFemale: 0 } => 0, { PercentFemale: 100 } => 254, { PercentFemale: { } f } => (byte)Math.Clamp(Math.Round(f * 256 / 100), 1, 253), _ => p[18] };
            p[21] = (byte)Math.Max(0, Array.IndexOf(Growth, sp.Growth.Replace("GROWTH_", "", StringComparison.Ordinal)));
            var eggs = sp.EggGroups.Select(g => Math.Max(0, Array.IndexOf(EggGroups, g.Replace("EGG_GROUP_", "", StringComparison.Ordinal)))).ToList();
            p[22] = (byte)eggs.ElementAtOrDefault(0);
            p[23] = (byte)(eggs.Count > 1 ? eggs[1] : eggs.ElementAtOrDefault(0));
            for (var k = 0; k < 3; k++)
            {
                p[24 + k] = (byte)s.AbilityIndex.GetValueOrDefault(sp.Abilities.ElementAtOrDefault(k) ?? "ABILITY_NONE");
            }

            W16(p, 34, sp.ExpYield);
            p1 = true;
            if (sp.Name != before.Name)
            {
                renamed[i] = sp.Name;
            }
        }

        foreach (var l in data.Learnsets)
        {
            if (!s.Learnsets.TryGetValue(l.Key, out var before) || ReferenceEquals(before, l))
            {
                continue;
            }

            var i = Species(l.Key);
            if (!before.LevelUp.SequenceEqual(l.LevelUp))
            {
                var bytes = new List<byte>();
                foreach (var m in l.LevelUp.OrderBy(m => m.Level))
                {
                    bytes.AddRange(BitConverter.GetBytes((ushort)Move(m.Move)));
                    bytes.AddRange(BitConverter.GetBytes((ushort)m.Level));
                }

                bytes.AddRange([0xFF, 0xFF, 0xFF, 0xFF]);
                learn[i] = [.. bytes];
                l1 = true;
            }

            if (!before.Teachable.SequenceEqual(l.Teachable))
            {
                var p = personal[i];
                for (var b = 0; b < s.TmMoves.Count; b++)
                {
                    var mask = (byte)(1 << (b % 8));
                    p[40 + (b / 8)] = l.Teachable.Contains(s.TmMoves[b]) ? (byte)(p[40 + (b / 8)] | mask) : (byte)(p[40 + (b / 8)] & ~mask);
                }

                p1 = true;
            }
        }

        foreach (var set in data.Evolutions)
        {
            if (!s.Evolutions.TryGetValue(set.FromSpecies, out var before) || ReferenceEquals(before, set))
            {
                continue;
            }

            var e = evo[Species(set.FromSpecies)];
            for (var k = 0; k < 8; k++)
            {
                var ev = k < set.Evolutions.Count ? set.Evolutions[k] : null;
                var native = ev is null ? null : FromEvolution(ev, key => Item(key), Move, Species);
                W16(e, 6 * k, native?.Method ?? 0);
                W16(e, (6 * k) + 2, native?.Param ?? 0);
                W16(e, (6 * k) + 4, native is null ? 0 : Species(ev!.TargetSpecies));
            }

            e1 = true;
        }

        if (p1)
        {
            changed[t.Personal] = personal.Build();
        }

        if (l1)
        {
            changed[t.Learnsets] = learn.Build();
        }

        if (e1)
        {
            changed[t.Evolutions] = evo.Build();
        }

        // moves
        var moveGarc = new Garc(s.File(t.Moves));
        var moves = MiniPack.Read(moveGarc[0]);
        var m1 = false;
        foreach (var mv in data.Moves)
        {
            if (!s.Moves.TryGetValue(mv.Key, out var before) || before == mv)
            {
                continue;
            }

            var d = moves[Move(mv.Key)];
            d[0] = (byte)Type(mv.Type);
            d[2] = mv.Category switch { MoveCategory.Physical => 1, MoveCategory.Special => 2, _ => 0 };
            d[3] = (byte)mv.Power;
            d[4] = (byte)mv.Accuracy;
            d[5] = (byte)mv.Pp;
            d[6] = (byte)(sbyte)mv.Priority;
            if (d.Length >= 36)
            {
                var flags = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(32));
                BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(32), mv.MakesContact ? flags | 1 : flags & ~1u);
            }

            m1 = true;
        }

        if (m1)
        {
            moveGarc[0] = MiniPack.Build(moves, moveGarc[0]);
            changed[t.Moves] = moveGarc.Build();
        }

        // items
        if (t.Items.Length > 0)
        {
            var items = new Garc(s.File(t.Items));
            var i1 = false;
            foreach (var it in data.Items)
            {
                if (s.Items.TryGetValue(it.Key, out var before) && before.Price != it.Price && Item(it.Key) is var i && i < items.Count && items[i].Length >= 2)
                {
                    W16(items[i], 0, it.Price / 10);
                    i1 = true;
                }
            }

            if (i1)
            {
                changed[t.Items] = items.Build();
            }
        }

        // trainers
        if (t.TrainerData.Length > 0 && t.TrainerParties.Length > 0)
        {
            var td = new Garc(s.File(t.TrainerData));
            var tp = new Garc(s.File(t.TrainerParties));
            var t1 = false;
            foreach (var tr in data.Trainers)
            {
                if (!s.Trainers.TryGetValue(tr.Key, out var before) || ReferenceEquals(before.Trainer, tr))
                {
                    continue;
                }

                var i = before.Index;
                var d = td[i];
                d[2] = (byte)s.ClassIndex.GetValueOrDefault(tr.TrainerClass);
                for (var k = 0; k < 4; k++)
                {
                    W16(d, 8 + (2 * k), k < tr.Items.Count ? Item(tr.Items[k]) : 0);
                }

                uint ai = 0;
                foreach (var f in tr.AiFlags)
                {
                    var b = data.Catalog.AiFlags.FindIndex(x => x.Key == f);
                    if (b is >= 0 and < 32)
                    {
                        ai |= 1u << b;
                    }
                }

                BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(16), ai);
                var custom = tr.Party.Any(p => p.Moves.Count > 0);
                var held = tr.Party.Any(p => p.HeldItem is not null && p.HeldItem != "ITEM_NONE");
                var flags = (custom ? 1 : 0) | (held ? 2 : 0);
                var stride = Stride(flags);
                var bytes = new byte[stride * tr.Party.Count];
                for (var k = 0; k < tr.Party.Count; k++)
                {
                    var p = tr.Party[k];
                    var o = stride * k;
                    var iv = p.Ivs is { } ivs ? (int)Math.Round((ivs.Hp + ivs.Attack + ivs.Defense + ivs.SpAttack + ivs.SpDefense + ivs.Speed) / 6.0 * 255 / 31) : 0;
                    bytes[o] = (byte)Math.Clamp(iv, 0, 255);
                    W16(bytes, o + 2, Math.Clamp(p.Level, 1, 100));
                    W16(bytes, o + 4, Species(p.Species));
                    var at = o + 8;
                    if (held)
                    {
                        W16(bytes, at, Item(p.HeldItem));
                        at += 2;
                    }

                    if (custom)
                    {
                        for (var m = 0; m < 4; m++)
                        {
                            W16(bytes, at + (2 * m), m < p.Moves.Count ? Move(p.Moves[m]) : 0);
                        }
                    }
                }

                d[0] = (byte)((d[0] & ~3) | flags);
                d[7] = (byte)tr.Party.Count;
                tp[i] = bytes;
                t1 = true;
            }

            if (t1)
            {
                changed[t.TrainerData] = td.Build();
                changed[t.TrainerParties] = tp.Build();
            }
        }

        // names (English)
        if (renamed.Count > 0)
        {
            var text = new Garc(s.File(t.Text));
            var raw = text.Entries[t.SpeciesNames].Files[0];
            var list = Gen6Text.Read(raw);
            foreach (var (i, name) in renamed)
            {
                list[i] = name;
            }

            text.Entries[t.SpeciesNames].Files[0] = Gen6Text.Write(list, raw);
            changed[t.Text] = text.Build();
        }

        return changed;
    }
}

/// <summary>A "mini pack" (two-letter magic, count, offsets): Gen 6 keeps all moves in one.</summary>
internal static class MiniPack
{
    public static List<byte[]> Read(byte[] data)
    {
        var count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2));
        var offsets = Enumerable.Range(0, count + 1).Select(i => (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4 + (4 * i)))).ToList();
        return Enumerable.Range(0, count).Select(i => data.AsSpan(offsets[i], offsets[i + 1] - offsets[i]).ToArray()).ToList();
    }

    public static byte[] Build(IReadOnlyList<byte[]> items, byte[] original)
    {
        var header = 4 + (4 * (items.Count + 1));
        var start = (header + 3) & ~3;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(original[0]);
        w.Write(original[1]);
        w.Write((ushort)items.Count);
        var pos = start;
        foreach (var it in items)
        {
            w.Write(pos);
            pos += it.Length;
        }

        w.Write(pos);
        while (ms.Position < start)
        {
            w.Write((byte)0);
        }

        foreach (var it in items)
        {
            w.Write(it);
        }

        return ms.ToArray();
    }
}
