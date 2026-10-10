using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;

namespace PokeStudio.Modules.Nds;

/// <summary>Gen 4 / 5 numbering of types, evolution methods and the like.</summary>
internal static class NdsConst
{
    public static readonly string[] Gen4Types =
        ["NORMAL", "FIGHTING", "FLYING", "POISON", "GROUND", "ROCK", "BUG", "GHOST", "STEEL", "MYSTERY", "FIRE", "WATER", "GRASS", "ELECTRIC", "PSYCHIC", "ICE", "DRAGON", "DARK"];

    public static readonly string[] Gen5Types =
        ["NORMAL", "FIGHTING", "FLYING", "POISON", "GROUND", "ROCK", "BUG", "GHOST", "STEEL", "FIRE", "WATER", "GRASS", "ELECTRIC", "PSYCHIC", "ICE", "DRAGON", "DARK"];

    public static readonly string[] Growth = ["MEDIUM_FAST", "ERRATIC", "FLUCTUATING", "MEDIUM_SLOW", "FAST", "SLOW"];

    public static readonly string[] EggGroups =
        ["NONE", "MONSTER", "WATER_1", "BUG", "FLYING", "FIELD", "FAIRY", "GRASS", "HUMAN_LIKE", "WATER_3", "MINERAL", "AMORPHOUS", "WATER_2", "DITTO", "DRAGON", "UNDISCOVERED"];

    public static string[] Types(int gen) => gen == 4 ? Gen4Types : Gen5Types;

    public static SpeciesTags TagsFor(int dex) => dex switch
    {
        150 or 249 or 250 or 382 or 383 or 384 or 483 or 484 or 487 or 643 or 644 or 646 => SpeciesTags.RestrictedLegendary,
        151 or 251 or 385 or 386 or 489 or 490 or 491 or 492 or 493 or 494 or 647 or 648 or 649 => SpeciesTags.Mythical,
        144 or 145 or 146 or 243 or 244 or 245 or 377 or 378 or 379 or 380 or 381 or 480 or 481 or 482 or 485 or 486 or 488
            or 638 or 639 or 640 or 641 or 642 or 645 => SpeciesTags.SubLegendary,
        _ => SpeciesTags.None,
    };

    public static string Constant(string name)
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

    /// <summary>Gen 4 (1–26) / Gen 5 (1–27) evolution method → the app's shared form.</summary>
    public static Evolution ToEvolution(int gen, int method, int param, string target, Func<int, string> item, Func<int, string> move, Func<int, string> species)
    {
        if (gen == 5)
        {
            // Gen 5 inserted "trade for a species" at 7; everything after moves up by one
            if (method == 7)
            {
                return E(target, "EVO_TRADE", "0", ("IF_TRADE_PARTNER_SPECIES", species(param)));
            }

            if (method > 7)
            {
                method--;
            }
        }

        var lv = param.ToString(CultureInfo.InvariantCulture);
        return method switch
        {
            1 => E(target, "EVO_LEVEL", "0", ("IF_MIN_FRIENDSHIP", "220")),
            2 => E(target, "EVO_LEVEL", "0", ("IF_MIN_FRIENDSHIP", "220"), ("IF_TIME", "TIME_DAY")),
            3 => E(target, "EVO_LEVEL", "0", ("IF_MIN_FRIENDSHIP", "220"), ("IF_TIME", "TIME_NIGHT")),
            4 => E(target, "EVO_LEVEL", lv),
            5 => E(target, "EVO_TRADE", "0"),
            6 => E(target, "EVO_TRADE", "0", ("IF_HOLD_ITEM", item(param))),
            7 => E(target, "EVO_ITEM", item(param)),
            8 => E(target, "EVO_LEVEL", lv, ("IF_ATK_GT_DEF", "")),
            9 => E(target, "EVO_LEVEL", lv, ("IF_ATK_EQ_DEF", "")),
            10 => E(target, "EVO_LEVEL", lv, ("IF_ATK_LT_DEF", "")),
            11 => E(target, "EVO_LEVEL", lv, ("IF_SILCOON", "")),
            12 => E(target, "EVO_LEVEL", lv, ("IF_CASCOON", "")),
            13 => E(target, "EVO_LEVEL_NINJASK", lv),
            14 => E(target, "EVO_LEVEL_SHEDINJA", lv),
            15 => E(target, "EVO_LEVEL", "0", ("IF_MIN_BEAUTY", lv)),
            16 => E(target, "EVO_ITEM", item(param), ("IF_GENDER", "MON_MALE")),
            17 => E(target, "EVO_ITEM", item(param), ("IF_GENDER", "MON_FEMALE")),
            18 => E(target, "EVO_LEVEL", "0", ("IF_HOLD_ITEM", item(param)), ("IF_TIME", "TIME_DAY")),
            19 => E(target, "EVO_LEVEL", "0", ("IF_HOLD_ITEM", item(param)), ("IF_TIME", "TIME_NIGHT")),
            20 => E(target, "EVO_LEVEL", "0", ("IF_KNOWS_MOVE", move(param))),
            21 => E(target, "EVO_LEVEL", "0", ("IF_SPECIES_IN_PARTY", species(param))),
            22 => E(target, "EVO_LEVEL", lv, ("IF_GENDER", "MON_MALE")),
            23 => E(target, "EVO_LEVEL", lv, ("IF_GENDER", "MON_FEMALE")),
            24 => E(target, "EVO_LEVEL", "0", ("IF_IN_MAPSEC", "LOCATION_MAGNETIC_FIELD")),
            25 => E(target, "EVO_LEVEL", "0", ("IF_IN_MAPSEC", "LOCATION_MOSS_ROCK")),
            26 => E(target, "EVO_LEVEL", "0", ("IF_IN_MAPSEC", "LOCATION_ICE_ROCK")),
            _ => E(target, $"EVO_METHOD_{method}", lv),
        };
    }

    private static Evolution E(string target, string method, string param, params (string Kind, string Value)[] conditions) =>
        new(method, param, target) { Conditions = conditions.Select(c => new EvolutionCondition(c.Kind, c.Value)).ToList() };
}

/// <summary>Reads a Gen 4 / Gen 5 ROM into the app's model.</summary>
internal static class NdsReader
{
    public static (GameData Data, NdsState State) Read(NdsRom rom, NdsGame game, NdsTables tables)
    {
        var state = new NdsState { Rom = rom, Game = game, Tables = tables };
        var data = new GameData { HeldItemLevelEvolutions = false };
        var c = data.Catalog;
        var gen = game.Generation;
        var types = NdsConst.Types(gen);
        var text = new Narc(rom.Read(game.Text));
        List<string> Text(int member) => member < 0 ? [] : gen == 4 ? Gen4Text.ReadFile(text.Members[member], out _) : Gen5Text.ReadFile(text.Members[member]);

        foreach (var t in types)
        {
            c.Types.Add(new CatalogEntry("TYPE_" + t, t == "MYSTERY" ? "???" : GameCatalog.Prettify(t)));
        }

        foreach (var g in NdsConst.Growth)
        {
            c.GrowthRates.Add(new CatalogEntry("GROWTH_" + g, GameCatalog.Prettify(g)));
        }

        foreach (var g in NdsConst.EggGroups)
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

        foreach (var (key, name) in new[] { ("IF_MIN_FRIENDSHIP", "High friendship"), ("IF_TIME", "Time of day"), ("IF_HOLD_ITEM", "Holding an item"), ("IF_GENDER", "Gender"),
                     ("IF_KNOWS_MOVE", "Knows a move"), ("IF_SPECIES_IN_PARTY", "With a Pokémon in the party"), ("IF_IN_MAPSEC", "At a special place"), ("IF_TRADE_PARTNER_SPECIES", "Traded for a Pokémon"),
                     ("IF_ATK_GT_DEF", "Attack higher than Defense"), ("IF_ATK_EQ_DEF", "Attack equal to Defense"), ("IF_ATK_LT_DEF", "Attack lower than Defense"),
                     ("IF_SILCOON", "Personality: Silcoon"), ("IF_CASCOON", "Personality: Cascoon"), ("IF_MIN_BEAUTY", "Beauty at least") })
        {
            c.EvolutionConditions.Add(new CatalogEntry(key, name));
        }

        string Unique(string key, HashSet<string> used, int i) => used.Add(key) ? key : $"{key}_{i}";

        // ---- names ----
        var used = new HashSet<string>(StringComparer.Ordinal);
        var abilityNames = Text(tables.AbilityNames);
        var abilityKeys = new List<string>();
        for (var i = 0; i < abilityNames.Count; i++)
        {
            var key = i == 0 ? "ABILITY_NONE" : Unique("ABILITY_" + NdsConst.Constant(abilityNames[i]), used, i);
            abilityKeys.Add(key);
            state.AbilityIndex[key] = i;
            c.Abilities.Add(new CatalogEntry(key, i == 0 ? "None" : abilityNames[i]));
        }

        used.Clear();
        var itemNames = Text(tables.ItemNames);
        var itemData = new Narc(rom.Read(game.Items)).Members;
        var itemKeys = new List<string>();
        for (var i = 0; i < itemNames.Count; i++)
        {
            var name = itemNames[i];
            var key = i == 0 ? "ITEM_NONE" : Unique(name.Length == 0 || name.All(ch => ch is '?' or '-') ? $"ITEM_UNUSED_{i}" : "ITEM_" + NdsConst.Constant(name), used, i);
            itemKeys.Add(key);
            state.ItemIndex[key] = i;
            c.Items.Add(new CatalogEntry(key, i == 0 ? "None" : name));
            if (i == 0)
            {
                continue;
            }

            var hasData = i < itemData.Count && itemData[i].Length >= 2;
            var item = new Item
            {
                Key = key,
                Name = name,
                Price = hasData ? BinaryPrimitives.ReadUInt16LittleEndian(itemData[i]) * (gen == 5 ? 10 : 1) : 0,
                LockedFields = hasData
                    ? new Dictionary<string, string> { ["Description"] = "Item descriptions can't change in DS ROM mode yet." }
                    : new Dictionary<string, string> { ["Price"] = "This item has no price data in the game.", ["Description"] = "Item descriptions can't change in DS ROM mode yet." },
            };
            data.Items.Add(item);
            state.Items[key] = item;
        }

        used.Clear();
        var moveNames = Text(tables.MoveNames);
        var moveData = new Narc(rom.Read(game.Moves)).Members;
        var moveKeys = new List<string>();
        for (var i = 0; i < moveNames.Count; i++)
        {
            var key = i == 0 ? "MOVE_NONE" : Unique("MOVE_" + NdsConst.Constant(moveNames[i]), used, i);
            moveKeys.Add(key);
            state.MoveIndex[key] = i;
            c.Moves.Add(new CatalogEntry(key, i == 0 ? "None" : moveNames[i]));
        }

        var effects = new SortedSet<int>();
        for (var i = 1; i < Math.Min(moveNames.Count, moveData.Count); i++)
        {
            var d = moveData[i];
            int type, power, accuracy, pp, priority, category, effect;
            bool contact;
            if (gen == 4)
            {
                effect = BinaryPrimitives.ReadUInt16LittleEndian(d);
                category = d[2];
                power = d[3];
                type = d[4];
                accuracy = d[5];
                pp = d[6];
                priority = (sbyte)d[10];
                contact = (d[11] & 1) != 0;
            }
            else
            {
                type = d[0];
                category = d[2] switch { 1 => 0, 2 => 1, _ => 2 };
                power = d[3];
                accuracy = d[4];
                pp = d[5];
                priority = (sbyte)d[6];
                effect = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(26));
                contact = (BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(32)) & 1) != 0;
            }

            effects.Add(effect);
            var move = new Move
            {
                Id = i,
                Key = moveKeys[i],
                Name = moveNames[i],
                Type = "TYPE_" + types[Math.Min(type, types.Length - 1)],
                Category = (MoveCategory)Math.Min(category, 2),
                Power = power,
                Accuracy = accuracy,
                Pp = pp,
                Priority = priority,
                Effect = $"EFFECT_{effect}",
                MakesContact = contact,
                LockedFields = new Dictionary<string, string> { ["Description"] = "Move descriptions can't change in DS ROM mode yet." },
            };
            data.Moves.Add(move);
            state.Moves[move.Key] = move;
        }

        foreach (var e in effects)
        {
            c.MoveEffects.Add(new CatalogEntry($"EFFECT_{e}", $"Effect {e}"));
        }

        foreach (var (i, m) in tables.TmMoves.Select((m, i) => (i, m)))
        {
            var key = m < moveKeys.Count ? moveKeys[m] : "MOVE_NONE";
            state.TmMoves.Add(key);
            var hmStart = gen == 4 ? 92 : 92;
            var label = gen == 5 && i >= 98 ? $"TM{i - 6 + 1:00}" : i < hmStart ? $"TM{i + 1:00}" : $"HM{i - hmStart + 1:00}";
            c.Machines.Add((label, key));
        }

        // ---- species ----
        used.Clear();
        var speciesNames = Text(tables.SpeciesNames);
        var speciesKeys = new List<string>();
        for (var i = 0; i < game.SpeciesCount; i++)
        {
            var name = i < speciesNames.Count ? speciesNames[i] : $"#{i}";
            var key = i == 0 ? "SPECIES_NONE" : Unique("SPECIES_" + NdsConst.Constant(name), used, i);
            speciesKeys.Add(key);
            state.SpeciesIndex[key] = i;
        }

        string SpeciesKey(int i) => i >= 0 && i < speciesKeys.Count ? speciesKeys[i] : "SPECIES_NONE";
        string MoveKey(int i) => i >= 0 && i < moveKeys.Count ? moveKeys[i] : "MOVE_NONE";
        string ItemKey(int i) => i >= 0 && i < itemKeys.Count ? itemKeys[i] : "ITEM_NONE";

        var personal = new Narc(rom.Read(game.Personal)).Members;
        var learn = new Narc(rom.Read(game.Learnsets)).Members;
        var evo = new Narc(rom.Read(game.Evolutions)).Members;
        for (var i = 1; i < game.SpeciesCount; i++)
        {
            var p = personal[i];
            var key = speciesKeys[i];
            int Item(int o) => BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(o));
            var genderAt = gen == 4 ? 16 : 18;
            var gender = p[genderAt] switch
            {
                0 => GenderRatio.AlwaysMale,
                254 => GenderRatio.AlwaysFemale,
                255 => GenderRatio.NoGender,
                var g => new GenderRatio(Math.Round(g * 100.0 / 256, 1)),
            };
            var growth = gen == 4 ? p[19] : p[21];
            var eggAt = gen == 4 ? 20 : 22;
            var abilityAt = gen == 4 ? 22 : 24;
            var abilities = Enumerable.Range(0, gen == 4 ? 2 : 3).Select(k => p[abilityAt + k]).Select(a => a < abilityKeys.Count ? abilityKeys[a] : "ABILITY_NONE").ToList();
            var species = new Species
            {
                Id = i,
                Key = key,
                Name = speciesNames[i],
                Stats = new BaseStats(p[0], p[1], p[2], p[4], p[5], p[3]),
                Types = new[] { p[6], p[7] }.Select(x => "TYPE_" + types[Math.Min((int)x, types.Length - 1)]).Distinct().ToList(),
                Abilities = abilities,
                HeldItemCommon = Item(12) is > 0 and var h1 ? ItemKey(h1) : null,
                HeldItemRare = Item(14) is > 0 and var h2 ? ItemKey(h2) : null,
                CatchRate = p[8],
                ExpYield = gen == 4 ? p[9] : BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(34)),
                Gender = gender,
                Growth = "GROWTH_" + NdsConst.Growth[Math.Min((int)growth, NdsConst.Growth.Length - 1)],
                EggGroups = new[] { p[eggAt], p[eggAt + 1] }.Select(x => "EGG_GROUP_" + NdsConst.EggGroups[Math.Min((int)x, NdsConst.EggGroups.Length - 1)]).Distinct().ToList(),
                NationalDexNumber = i,
                NationalDex = i,
                Tags = NdsConst.TagsFor(i),
                LearnsetKey = key,
                LockedFields = new Dictionary<string, string> { ["DexEntry"] = "Pokédex entries can't change in DS ROM mode yet.", ["Category"] = "Pokédex entries can't change in DS ROM mode yet." },
            };
            data.Species.Add(species);
            state.Species[key] = species;

            // learnset
            var l = learn[i];
            var levelUp = new List<LevelUpMove>();
            if (gen == 4)
            {
                for (var o = 0; o + 1 < l.Length; o += 2)
                {
                    var v = BinaryPrimitives.ReadUInt16LittleEndian(l.AsSpan(o));
                    if (v == 0xFFFF)
                    {
                        break;
                    }

                    levelUp.Add(new LevelUpMove(v >> 9, MoveKey(v & 0x1FF)));
                }
            }
            else
            {
                for (var o = 0; o + 3 < l.Length; o += 4)
                {
                    var mv = BinaryPrimitives.ReadUInt16LittleEndian(l.AsSpan(o));
                    var lv = BinaryPrimitives.ReadUInt16LittleEndian(l.AsSpan(o + 2));
                    if (mv == 0xFFFF)
                    {
                        break;
                    }

                    levelUp.Add(new LevelUpMove(lv, MoveKey(mv)));
                }
            }

            var teach = new List<string>();
            for (var b = 0; b < state.TmMoves.Count; b++)
            {
                if ((p[game.TmBitsOffset + (b / 8)] >> (b % 8) & 1) != 0)
                {
                    teach.Add(state.TmMoves[b]);
                }
            }

            var learnset = new Learnset
            {
                Key = key,
                UsedBy = [key],
                LevelUp = levelUp,
                Teachable = teach.Distinct().ToList(),
                LockedParts = state.TmMoves.Count == 0
                    ? new Dictionary<string, string> { ["egg"] = "Egg moves can't change in DS ROM mode yet.", ["teachable"] = "This game's TM list couldn't be found." }
                    : new Dictionary<string, string> { ["egg"] = "Egg moves can't change in DS ROM mode yet." },
            };
            data.Learnsets.Add(learnset);
            state.Learnsets[key] = learnset;

            // evolutions
            var evos = new List<Evolution>();
            var ev = evo[i];
            for (var k = 0; k < game.EvolutionsPerSpecies && (6 * k) + 5 < ev.Length; k++)
            {
                var method = BinaryPrimitives.ReadUInt16LittleEndian(ev.AsSpan(6 * k));
                if (method == 0)
                {
                    continue;
                }

                var param = BinaryPrimitives.ReadUInt16LittleEndian(ev.AsSpan((6 * k) + 2));
                var target = BinaryPrimitives.ReadUInt16LittleEndian(ev.AsSpan((6 * k) + 4));
                evos.Add(NdsConst.ToEvolution(gen, method, param, SpeciesKey(target), ItemKey, MoveKey, SpeciesKey));
            }

            var set = new EvolutionSet { FromSpecies = key, Evolutions = evos };
            data.Evolutions.Add(set);
            state.Evolutions[key] = set;
        }

        // ---- trainers ----
        var classNames = Text(tables.ClassNames);
        var trainerNames = Text(tables.TrainerNames);
        var trData = new Narc(rom.Read(game.TrainerData)).Members;
        var trPoke = new Narc(rom.Read(game.TrainerParties)).Members;
        var maxClass = trData.Max(t => t.Length > 1 ? t[1] : 0);
        used.Clear();
        for (var i = 0; i <= maxClass; i++)
        {
            var name = i < classNames.Count && classNames[i].Length > 0 ? classNames[i] : $"Class {i}";
            var key = Unique("TRAINER_CLASS_" + NdsConst.Constant(name), used, i);
            state.ClassIndex[key] = i;
            c.TrainerClasses.Add(new CatalogEntry(key, name));
        }

        var classKeys = state.ClassIndex.OrderBy(kv => kv.Value).Select(kv => kv.Key).ToList();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 1; i < trData.Count; i++)
        {
            var d = trData[i];
            if (d.Length < 20)
            {
                continue;
            }

            var name = i < trainerNames.Count ? trainerNames[i] : "";
            var baseKey = "TRAINER_" + (name.Length > 0 ? NdsConst.Constant(name) : $"UNNAMED_{i}");
            counts[baseKey] = counts.GetValueOrDefault(baseKey) + 1;
            var battle = gen == 4 ? (BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(16)) != 0 ? BattleType.Double : BattleType.Single)
                : d[2] switch { 1 => BattleType.Double, 2 => BattleType.Triple, 3 => BattleType.Rotation, _ => BattleType.Single };
            var ai = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(12));
            var trainer = new Trainer
            {
                Id = i,
                Key = $"{baseKey}_{counts[baseKey]}",
                Name = name,
                TrainerClass = d[1] < classKeys.Count ? classKeys[d[1]] : classKeys[0],
                Items = Enumerable.Range(0, 4).Select(k => (int)BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(4 + (2 * k)))).Where(x => x > 0).Select(ItemKey).ToList(),
                Battle = battle,
                AiFlags = Enumerable.Range(0, 32).Where(b => (ai >> b & 1) != 0).Select(b => c.AiFlags[b].Key).ToList(),
                Party = ReadParty(game, d[0], d[3], i < trPoke.Count ? trPoke[i] : [], SpeciesKey, MoveKey, ItemKey),
            };
            data.Trainers.Add(trainer);
            state.Trainers[trainer.Key] = (trainer, i);
        }

        // ---- wild Pokémon ----
        ReadEncounters(rom, game, state, data, SpeciesKey);
        c.Invalidate();
        return (data, state);
    }

    internal static int PartyStride(NdsGame game, int flags) =>
        (game.Generation == 5 || game.TrainerMonExtra ? 8 : 6) + ((flags & 2) != 0 ? 2 : 0) + ((flags & 1) != 0 ? 8 : 0);

    private static List<TrainerPokemon> ReadParty(NdsGame game, int flags, int count, byte[] data, Func<int, string> species, Func<int, string> move, Func<int, string> item)
    {
        var gen = game.Generation;
        var stride = PartyStride(game, flags);
        var result = new List<TrainerPokemon>();
        for (var k = 0; k < count && (stride * (k + 1)) <= data.Length; k++)
        {
            var o = stride * k;
            var iv = data[o] * 31 / 255;
            int level, sp;
            var p = o;
            if (gen == 4)
            {
                level = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(o + 2));
                sp = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(o + 4)) & 0x3FF;
                p = o + 6;
            }
            else
            {
                level = data[o + 2];
                sp = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(o + 4));
                p = o + 8;
            }

            string? held = null;
            if ((flags & 2) != 0)
            {
                var it = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p));
                held = it > 0 ? item(it) : null;
                p += 2;
            }

            var moves = new List<string>();
            if ((flags & 1) != 0)
            {
                for (var m = 0; m < 4; m++)
                {
                    var mv = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p + (2 * m)));
                    if (mv > 0)
                    {
                        moves.Add(move(mv));
                    }
                }
            }

            result.Add(new TrainerPokemon { Species = species(sp), Level = level, HeldItem = held, Moves = moves, Ivs = new BaseStats(iv, iv, iv, iv, iv, iv) });
        }

        return result;
    }

    /// <summary>Slot layouts: (method type, slot count, offset, entry size) per format.</summary>
    internal static IEnumerable<(string Type, int Slots, int Offset, int Size, string Format)> Layout(string format) => format switch
    {
        "dppt" => [("land_mons", 12, 4, 8, "lvl32-sp32"), ("water_mons", 5, 208, 8, "max-min-sp32"), ("rock_smash_mons", 5, 252, 8, "max-min-sp32"),
                   ("fishing_mons", 15, 296, 8, "max-min-sp32")],
        "hgss" => [("land_mons", 12, 8, 1, "hgss-land"), ("water_mons", 5, 100, 4, "min-max-sp16"), ("rock_smash_mons", 2, 120, 4, "min-max-sp16"),
                   ("fishing_mons", 15, 128, 4, "min-max-sp16")],
        _ => [("land_mons", 12, 8, 4, "bw"), ("water_mons", 5, 152, 4, "bw"), ("fishing_mons", 5, 192, 4, "bw")],
    };

    private static void ReadEncounters(NdsRom rom, NdsGame game, NdsState state, GameData data, Func<int, string> species)
    {
        var areas = new Narc(rom.Read(game.Encounters)).Members;
        for (var a = 0; a < areas.Count; a++)
        {
            var d = areas[a];
            var methods = new List<EncounterMethodTable>();
            foreach (var (type, slots, offset, size, format) in Layout(game.EncounterFormat))
            {
                var list = new List<EncounterSlot>();
                for (var k = 0; k < slots; k++)
                {
                    var slot = ReadSlot(d, format, offset, size, k, species);
                    if (slot is null)
                    {
                        list.Clear();
                        break;
                    }

                    list.Add(slot);
                }

                // (fishing in DP/Pt sits in three rod tables with a rate before each)
                if (list.Count > 0 && list.Any(s => s.Species != "SPECIES_NONE"))
                {
                    methods.Add(new EncounterMethodTable { Type = type, Rate = Rate(d, game.EncounterFormat, type), Slots = list });
                }
            }

            if (methods.Count == 0)
            {
                continue;
            }

            var table = new EncounterTable { Key = $"AREA_{a}", Map = $"MAP_AREA_{a}", Group = "Wild", Methods = methods };
            data.Encounters.Add(table);
            state.Encounters[table.Key] = (table, a);
        }

        var landChances = new[] { 20, 20, 10, 10, 10, 10, 5, 5, 4, 4, 1, 1 };
        data.EncounterChances.Add(new EncounterSlotChances("land_mons", landChances, new Dictionary<string, IReadOnlyList<int>>()));
        data.EncounterChances.Add(new EncounterSlotChances("water_mons", [60, 30, 5, 4, 1], new Dictionary<string, IReadOnlyList<int>>()));
        data.EncounterChances.Add(new EncounterSlotChances("rock_smash_mons", game.EncounterFormat == "hgss" ? [90, 10] : [60, 30, 5, 4, 1], new Dictionary<string, IReadOnlyList<int>>()));
        data.EncounterChances.Add(new EncounterSlotChances("fishing_mons",
            game.EncounterFormat == "bw" ? [40, 40, 15, 4, 1] : [60, 30, 5, 4, 1, 40, 40, 15, 4, 1, 40, 40, 15, 4, 1],
            game.EncounterFormat == "bw" ? new Dictionary<string, IReadOnlyList<int>>()
                : new Dictionary<string, IReadOnlyList<int>> { ["old_rod"] = [0, 1, 2, 3, 4], ["good_rod"] = [5, 6, 7, 8, 9], ["super_rod"] = [10, 11, 12, 13, 14] }));
    }

    /// <summary>DP/Pt rod tables each start with a 4-byte rate, so slot k of "fishing" skips those.</summary>
    internal static int SlotOffset(string format, int offset, int size, int k) =>
        format == "max-min-sp32" && offset == 296 ? offset + (k / 5 * 44) + (k % 5 * size) : offset + (k * size);

    private static EncounterSlot? ReadSlot(byte[] d, string format, int offset, int size, int k, Func<int, string> species)
    {
        var o = SlotOffset(format, offset, size, k);
        if (format == "hgss-land")
        {
            if (20 + (2 * k) + 1 >= d.Length || o + k >= d.Length)
            {
                return null;
            }

            var level = d[8 + k];
            var sp = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(44 + (2 * k))); // daytime
            return Valid(sp, level, level) ? new EncounterSlot(species(sp), level, level) : null;
        }

        if (o + size > d.Length)
        {
            return null;
        }

        int min, max, s;
        switch (format)
        {
            case "lvl32-sp32":
                min = max = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o));
                s = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o + 4));
                break;
            case "max-min-sp32":
                max = d[o];
                min = d[o + 1];
                s = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o + 4));
                break;
            case "min-max-sp16":
                min = d[o];
                max = d[o + 1];
                s = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o + 2));
                break;
            default: // bw
                s = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o)) & 0x7FF;
                min = d[o + 2];
                max = d[o + 3];
                break;
        }

        return Valid(s, min, max) ? new EncounterSlot(species(s), min, max) : null;
    }

    private static bool Valid(int species, int min, int max) => species == 0 ? min <= 100 && max <= 100 : species < 1000 && min is >= 1 and <= 100 && max is >= 1 and <= 100;

    private static int Rate(byte[] d, string format, string type) => format switch
    {
        "dppt" => type switch { "land_mons" => d[0], "water_mons" => d[204], "rock_smash_mons" => d[248], _ => d[292] },
        "hgss" => type switch { "land_mons" => d[0], "water_mons" => d[1], "rock_smash_mons" => d[2], _ => d[3] },
        _ => type switch { "land_mons" => d[0], "water_mons" => d[3], _ => d[5] },
    };
}
