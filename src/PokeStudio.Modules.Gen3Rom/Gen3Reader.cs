using System.Globalization;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;

namespace PokeStudio.Modules.Gen3Rom;

/// <summary>Gen 3 constants (the games' own numbering) and how they read in the app.</summary>
internal static class Gen3
{
    public static readonly string[] Types =
        ["NORMAL", "FIGHTING", "FLYING", "POISON", "GROUND", "ROCK", "BUG", "GHOST", "STEEL", "MYSTERY", "FIRE", "WATER", "GRASS", "ELECTRIC", "PSYCHIC", "ICE", "DRAGON", "DARK"];

    public static readonly string[] Growth = ["MEDIUM_FAST", "ERRATIC", "FLUCTUATING", "MEDIUM_SLOW", "FAST", "SLOW"];

    public static readonly string[] EggGroups =
        ["NONE", "MONSTER", "WATER_1", "BUG", "FLYING", "FIELD", "FAIRY", "GRASS", "HUMAN_LIKE", "WATER_3", "MINERAL", "AMORPHOUS", "WATER_2", "DITTO", "DRAGON", "UNDISCOVERED"];

    public static readonly (int Bit, string Key, string Name)[] Targets =
    [
        (0x00, "MOVE_TARGET_SELECTED", "One Pokémon"), (0x01, "MOVE_TARGET_DEPENDS", "Depends on the move"), (0x02, "MOVE_TARGET_USER_OR_SELECTED", "User or one Pokémon"),
        (0x04, "MOVE_TARGET_RANDOM", "A random foe"), (0x08, "MOVE_TARGET_BOTH", "Both foes"), (0x10, "MOVE_TARGET_USER", "The user"),
        (0x20, "MOVE_TARGET_FOES_AND_ALLY", "Everyone else"), (0x40, "MOVE_TARGET_OPPONENTS_FIELD", "The foes' side"),
    ];

    public static readonly string[] AiFlags =
    [
        "CHECK_BAD_MOVE", "TRY_TO_FAINT", "CHECK_VIABILITY", "SETUP_FIRST_TURN", "RISKY", "PREFER_STRONGEST_MOVE", "PREFER_BATON_PASS", "DOUBLE_BATTLE", "HP_AWARE",
    ];

    public static readonly string[] Pockets = ["", "Items", "Poké Balls", "TMs & HMs", "Berries", "Key Items"];

    /// <summary>National Dex numbers of the legendary and mythical Pokémon of Gens 1–3 (for the Champion Lab rules).</summary>
    public static SpeciesTags TagsFor(int dex) => dex switch
    {
        150 or 249 or 250 or 382 or 383 or 384 => SpeciesTags.RestrictedLegendary,
        151 or 251 or 385 or 386 => SpeciesTags.Mythical,
        144 or 145 or 146 or 243 or 244 or 245 or 377 or 378 or 379 or 380 or 381 => SpeciesTags.SubLegendary,
        _ => SpeciesTags.None,
    };

    // ---- evolution methods (Gen 3 numbering) ↔ the app's shared form (method, parameter, conditions) ----
    public const int Friendship = 1, FriendshipDay = 2, FriendshipNight = 3, Level = 4, Trade = 5, TradeItem = 6, Item = 7,
        AtkGtDef = 8, AtkEqDef = 9, AtkLtDef = 10, Silcoon = 11, Cascoon = 12, Ninjask = 13, Shedinja = 14, Beauty = 15;

    public const string FriendshipValue = "220";
}

/// <summary>Reads a Gen 3 ROM into the app's model.</summary>
internal static class Gen3Reader
{
    public static (GameData Data, Gen3RomModule.State State) Read(RomImage rom, Gen3Tables t)
    {
        var state = new Gen3RomModule.State { Rom = rom, Tables = t };
        var data = new GameData();
        var c = data.Catalog;
        data.HeldItemLevelEvolutions = false;

        string Unique(string key, HashSet<string> used, int index)
        {
            var k = key;
            if (!used.Add(k))
            {
                k = $"{key}_{index}";
                used.Add(k);
            }

            return k;
        }

        // ---- catalogs ----
        for (var i = 0; i < Gen3.Types.Length; i++)
        {
            c.Types.Add(new CatalogEntry("TYPE_" + Gen3.Types[i], i == 9 ? "???" : GameCatalog.Prettify(Gen3.Types[i])));
        }

        foreach (var g in Gen3.Growth)
        {
            c.GrowthRates.Add(new CatalogEntry("GROWTH_" + g, GameCatalog.Prettify(g)));
        }

        foreach (var g in Gen3.EggGroups)
        {
            c.EggGroups.Add(new CatalogEntry("EGG_GROUP_" + g, g == "NONE" ? "None" : GameCatalog.Prettify(g)));
        }

        foreach (var (_, key, name) in Gen3.Targets)
        {
            c.MoveTargets.Add(new CatalogEntry(key, name));
        }

        for (var i = 0; i < 32; i++)
        {
            var name = i < Gen3.AiFlags.Length ? Gen3.AiFlags[i] : $"BIT_{i}";
            c.AiFlags.Add(new CatalogEntry("AI_FLAG_" + name, GameCatalog.Prettify(name)));
        }

        c.EvolutionMethods.Add(new CatalogEntry("EVO_LEVEL", "Level up", "At a level (0 = any level-up, when a condition is met)."));
        c.EvolutionMethods.Add(new CatalogEntry("EVO_ITEM", "Use an item", "When the item is used on it."));
        c.EvolutionMethods.Add(new CatalogEntry("EVO_TRADE", "Trade", "When traded (holding an item, if one is set)."));
        c.EvolutionMethods.Add(new CatalogEntry("EVO_LEVEL_NINJASK", "Level (Nincada → Ninjask)", "Nincada's first evolution."));
        c.EvolutionMethods.Add(new CatalogEntry("EVO_LEVEL_SHEDINJA", "Level (Shedinja appears)", "Shedinja appears in an empty party slot."));
        foreach (var (key, name) in new[] { ("IF_MIN_FRIENDSHIP", "High friendship"), ("IF_TIME", "Time of day"), ("IF_HOLD_ITEM", "Holding an item"),
                     ("IF_ATK_GT_DEF", "Attack higher than Defense"), ("IF_ATK_EQ_DEF", "Attack equal to Defense"), ("IF_ATK_LT_DEF", "Attack lower than Defense"),
                     ("IF_SILCOON", "Personality: Silcoon"), ("IF_CASCOON", "Personality: Cascoon"), ("IF_MIN_BEAUTY", "Beauty at least") })
        {
            c.EvolutionConditions.Add(new CatalogEntry(key, name));
        }

        // ---- abilities, items, moves (names first: other tables refer to them) ----
        var used = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < t.AbilityCount; i++)
        {
            var name = Gen3Text.Read(rom.Bytes.AsSpan(t.AbilityNames + (13 * i), 13));
            var key = i == 0 ? "ABILITY_NONE" : Unique("ABILITY_" + Gen3Text.Constant(name), used, i);
            state.AbilityIndex[key] = i;
            c.Abilities.Add(new CatalogEntry(key, i == 0 ? "None" : Title(name)));
        }

        var abilityKeys = state.AbilityIndex.OrderBy(kv => kv.Value).Select(kv => kv.Key).ToList();
        used.Clear();
        var itemKeys = new List<string>();
        for (var i = 0; i < t.ItemCount; i++)
        {
            var at = t.Items + (Gen3Tables.ItemSize * i);
            var name = Gen3Text.Read(rom.Bytes.AsSpan(at, 14));
            var key = i == 0 ? "ITEM_NONE" : Unique(name.All(ch => ch == '?') ? $"ITEM_UNUSED_{i}" : "ITEM_" + Gen3Text.Constant(name), used, i);
            itemKeys.Add(key);
            state.ItemIndex[key] = i;
            var desc = rom.Offset(at + 20);
            var item = new Item
            {
                Key = key,
                Name = i == 0 ? "None" : name,
                Price = rom.U16(at + 16),
                Description = desc >= 0 ? Gen3Text.Read(rom.Bytes.AsSpan(desc, Math.Min(200, rom.Length - desc))).Replace("\n", " ", StringComparison.Ordinal) : "",
                Pocket = rom.U8(at + 26) < Gen3.Pockets.Length ? Gen3.Pockets[rom.U8(at + 26)] : "",
                LockedFields = new Dictionary<string, string> { ["Description"] = "Item descriptions can't be changed in ROM mode yet." },
            };
            if (i > 0)
            {
                data.Items.Add(item);
                state.Items[key] = item;
            }

            c.Items.Add(new CatalogEntry(key, item.Name));
        }

        used.Clear();
        var moveKeys = new List<string>();
        for (var i = 0; i < t.MoveCount; i++)
        {
            var name = Gen3Text.Read(rom.Bytes.AsSpan(t.MoveNames + (13 * i), 13));
            var key = i == 0 ? "MOVE_NONE" : Unique("MOVE_" + Gen3Text.Constant(name), used, i);
            moveKeys.Add(key);
            state.MoveIndex[key] = i;
            c.Moves.Add(new CatalogEntry(key, i == 0 ? "None" : name));
        }

        var effects = new SortedSet<int>();
        for (var i = 1; i < t.MoveCount; i++)
        {
            var at = t.Moves + (Gen3Tables.MoveSize * i);
            var type = rom.U8(at + 2);
            var typeKey = "TYPE_" + Gen3.Types[Math.Min(type, Gen3.Types.Length - 1)];
            var power = rom.U8(at + 1);
            effects.Add(rom.U8(at));
            var descAt = t.MoveDescriptions >= 0 ? rom.Offset(t.MoveDescriptions + (4 * (i - 1))) : -1;
            var move = new Move
            {
                Id = i,
                Key = moveKeys[i],
                Name = c.Moves[i].Name,
                Type = typeKey,
                Power = power,
                Accuracy = rom.U8(at + 3),
                Pp = rom.U8(at + 4),
                Priority = (sbyte)rom.U8(at + 7),
                Effect = $"EFFECT_{rom.U8(at)}",
                Target = Gen3.Targets.FirstOrDefault(x => x.Bit == rom.U8(at + 6)).Key ?? "MOVE_TARGET_SELECTED",
                MakesContact = (rom.U8(at + 8) & 1) != 0,
                Category = power == 0 ? MoveCategory.Status : GameCatalog.SpecialTypesBeforeSplit.Contains(typeKey) ? MoveCategory.Special : MoveCategory.Physical,
                Description = descAt >= 0 ? Gen3Text.Read(rom.Bytes.AsSpan(descAt, Math.Min(200, rom.Length - descAt))).Replace("\n", " ", StringComparison.Ordinal) : "",
                LockedFields = new Dictionary<string, string>
                {
                    ["Category"] = "In these games the move's type decides physical or special.",
                    ["Description"] = "Move descriptions can't be changed in ROM mode yet.",
                },
            };
            data.Moves.Add(move);
            state.Moves[move.Key] = move;
        }

        foreach (var e in effects)
        {
            c.MoveEffects.Add(new CatalogEntry($"EFFECT_{e}", $"Effect {e}"));
        }

        // ---- TMs and HMs ----
        for (var i = 0; i < 58; i++)
        {
            var move = rom.U16(t.TmMoves + (2 * i));
            var key = move < moveKeys.Count ? moveKeys[move] : "MOVE_NONE";
            state.TmMoves.Add(key);
            c.Machines.Add((i < 50 ? $"TM{i + 1:00}" : $"HM{i - 49:00}", key));
        }

        // ---- species ----
        used.Clear();
        var speciesKeys = new List<string>();
        for (var i = 0; i < Gen3Tables.SpeciesCount; i++)
        {
            var name = Gen3Text.Read(rom.Bytes.AsSpan(t.SpeciesNames + (11 * i), 11));
            var placeholder = i == 0 || name.Trim('?').Length == 0;
            var key = i == 0 ? "SPECIES_NONE" : placeholder ? $"SPECIES_UNUSED_{i}" : Unique("SPECIES_" + Gen3Text.Constant(name), used, i);
            speciesKeys.Add(key);
            state.SpeciesIndex[key] = i;
        }

        for (var i = 1; i < Gen3Tables.SpeciesCount; i++)
        {
            var at = t.BaseStats + (Gen3Tables.BaseStatsSize * i);
            var key = speciesKeys[i];
            var name = Gen3Text.Read(rom.Bytes.AsSpan(t.SpeciesNames + (11 * i), 11));
            var dex = rom.U16(t.NationalDex + (2 * (i - 1)));
            var gender = rom.U8(at + 16) switch
            {
                0 => GenderRatio.AlwaysMale,
                254 => GenderRatio.AlwaysFemale,
                255 => GenderRatio.NoGender,
                var g => new GenderRatio(Math.Round(g * 100.0 / 256, 1)),
            };
            var types = new[] { rom.U8(at + 6), rom.U8(at + 7) }.Select(x => "TYPE_" + Gen3.Types[Math.Min((int)x, Gen3.Types.Length - 1)]).Distinct().ToList();
            var abilities = new[] { rom.U8(at + 22), rom.U8(at + 23) }.Select(x => x < abilityKeys.Count ? abilityKeys[x] : "ABILITY_NONE").ToList();
            string? Held(int o) => rom.U16(o) is var item and > 0 && item < itemKeys.Count ? itemKeys[item] : null;
            var species = new Species
            {
                Id = i,
                Key = key,
                Name = key.StartsWith("SPECIES_UNUSED", StringComparison.Ordinal) ? $"Unused {i}" : name,
                Stats = new BaseStats(rom.U8(at), rom.U8(at + 1), rom.U8(at + 2), rom.U8(at + 4), rom.U8(at + 5), rom.U8(at + 3)),
                Types = types,
                Abilities = abilities,
                HeldItemCommon = Held(at + 12),
                HeldItemRare = Held(at + 14),
                CatchRate = rom.U8(at + 8),
                ExpYield = rom.U8(at + 9),
                Gender = gender,
                Growth = "GROWTH_" + Gen3.Growth[Math.Min((int)rom.U8(at + 19), Gen3.Growth.Length - 1)],
                EggGroups = new[] { rom.U8(at + 20), rom.U8(at + 21) }.Select(x => "EGG_GROUP_" + Gen3.EggGroups[Math.Min((int)x, Gen3.EggGroups.Length - 1)]).Distinct().ToList(),
                NationalDexNumber = dex,
                NationalDex = dex > 0 ? dex : null,
                Tags = Gen3.TagsFor(dex),
                IsEnabled = !key.StartsWith("SPECIES_UNUSED", StringComparison.Ordinal),
                LearnsetKey = key,
                LockedFields = new Dictionary<string, string>
                {
                    ["Category"] = "Pokédex entries can't be changed in ROM mode yet.",
                    ["DexEntry"] = "Pokédex entries can't be changed in ROM mode yet.",
                },
            };
            data.Species.Add(species);
            state.Species[key] = species;

            // learnset: level-up list + TM/HM bits
            var levelUp = new List<LevelUpMove>();
            var lu = rom.Offset(t.LevelUpPointers + (4 * i));
            for (var p = lu; p >= 0 && p + 1 < rom.Length && rom.U16(p) != 0xFFFF && levelUp.Count < 64; p += 2)
            {
                var v = rom.U16(p);
                var move = v & 0x1FF;
                levelUp.Add(new LevelUpMove(v >> 9, move < moveKeys.Count ? moveKeys[move] : "MOVE_NONE"));
            }

            var bits = (ulong)rom.U32(t.TmCompatibility + (8 * i)) | ((ulong)rom.U32(t.TmCompatibility + (8 * i) + 4) << 32);
            var teach = Enumerable.Range(0, 58).Where(b => (bits >> b & 1) != 0).Select(b => state.TmMoves[b]).Distinct().ToList();
            var learnset = new Learnset
            {
                Key = key,
                UsedBy = [key],
                LevelUp = levelUp,
                Teachable = teach,
                LockedParts = new Dictionary<string, string> { ["egg"] = "Egg moves can't be changed in ROM mode yet." },
            };
            data.Learnsets.Add(learnset);
            state.Learnsets[key] = learnset;

            // evolutions
            var evos = new List<Evolution>();
            for (var k = 0; k < Gen3Tables.EvolutionsPerSpecies; k++)
            {
                var e = t.Evolutions + (40 * i) + (8 * k);
                var method = rom.U16(e);
                if (method == 0)
                {
                    continue;
                }

                var param = rom.U16(e + 2);
                var target = rom.U16(e + 4);
                if (target >= speciesKeys.Count)
                {
                    continue;
                }

                evos.Add(ToEvolution(method, param, speciesKeys[target], itemKeys));
            }

            var set = new EvolutionSet { FromSpecies = key, Evolutions = evos };
            data.Evolutions.Add(set);
            state.Evolutions[key] = set;
        }

        // ---- trainers ----
        var classNames = new List<string>();
        for (var i = 0; i < t.ClassCount; i++)
        {
            classNames.Add(Gen3Text.Read(rom.Bytes.AsSpan(t.ClassNames + (13 * i), 13)));
        }

        used.Clear();
        for (var i = 0; i < Math.Max(t.ClassCount, 1); i++)
        {
            var name = i < classNames.Count && classNames[i].Length > 0 ? classNames[i] : $"Class {i}";
            var key = Unique("TRAINER_CLASS_" + Gen3Text.Constant(name), used, i);
            state.ClassIndex[key] = i;
            c.TrainerClasses.Add(new CatalogEntry(key, Title(name)));
        }

        var classKeys = state.ClassIndex.OrderBy(kv => kv.Value).Select(kv => kv.Key).ToList();
        var pics = new SortedSet<int>();
        var nameCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 1; i < t.TrainerCount; i++)
        {
            var at = t.Trainers + (Gen3Tables.TrainerSize * i);
            var name = Gen3Text.Read(rom.Bytes.AsSpan(at + 4, 12));
            var cls = rom.U8(at + 1);
            var baseKey = "TRAINER_" + (name.Length > 0 ? Gen3Text.Constant(name) : $"UNNAMED_{i}");
            nameCounts[baseKey] = nameCounts.GetValueOrDefault(baseKey) + 1;
            var key = $"{baseKey}_{nameCounts[baseKey]}";
            pics.Add(rom.U8(at + 3));
            var trainer = new Trainer
            {
                Id = i,
                Key = key,
                Name = name,
                TrainerClass = cls < classKeys.Count ? classKeys[cls] : classKeys.FirstOrDefault() ?? "",
                Pic = $"TRAINER_PIC_{rom.U8(at + 3)}",
                Gender = (rom.U8(at + 2) & 0x80) != 0 ? "Female" : "Male",
                Music = $"TRAINER_ENCOUNTER_MUSIC_{rom.U8(at + 2) & 0x7F}",
                Items = Enumerable.Range(0, 4).Select(k => rom.U16(at + 16 + (2 * k))).Where(x => x > 0 && x < itemKeys.Count).Select(x => itemKeys[x]).ToList(),
                Battle = rom.U8(at + 24) != 0 ? BattleType.Double : BattleType.Single,
                AiFlags = Enumerable.Range(0, 32).Where(b => (rom.U32(at + 28) >> b & 1) != 0).Select(b => c.AiFlags[b].Key).ToList(),
                Party = ReadParty(rom, at, speciesKeys, moveKeys, itemKeys),
            };
            data.Trainers.Add(trainer);
            state.Trainers[key] = (trainer, i);
        }

        foreach (var p in pics)
        {
            c.TrainerPics.Add(new CatalogEntry($"TRAINER_PIC_{p}", $"Picture {p}"));
        }

        for (var m = 0; m < 128; m++)
        {
            c.TrainerMusic.Add(new CatalogEntry($"TRAINER_ENCOUNTER_MUSIC_{m}", $"Music {m}"));
        }

        // ---- wild Pokémon ----
        ReadEncounters(rom, t, state, data, speciesKeys);
        data.EncounterChances.Add(new EncounterSlotChances("land_mons", [20, 20, 10, 10, 10, 10, 5, 5, 4, 4, 1, 1], new Dictionary<string, IReadOnlyList<int>>()));
        data.EncounterChances.Add(new EncounterSlotChances("water_mons", [60, 30, 5, 4, 1], new Dictionary<string, IReadOnlyList<int>>()));
        data.EncounterChances.Add(new EncounterSlotChances("rock_smash_mons", [60, 30, 5, 4, 1], new Dictionary<string, IReadOnlyList<int>>()));
        data.EncounterChances.Add(new EncounterSlotChances("fishing_mons", [70, 30, 60, 20, 20, 40, 40, 15, 4, 1],
            new Dictionary<string, IReadOnlyList<int>> { ["old_rod"] = [0, 1], ["good_rod"] = [2, 3, 4], ["super_rod"] = [5, 6, 7, 8, 9] }));

        c.Invalidate();
        return (data, state);
    }

    /// <summary>"BULBASAUR" stays as the game shows it; abilities and classes read better in title case.</summary>
    private static string Title(string name) =>
        CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.ToLowerInvariant()).Replace("Pkmn", "Pkmn", StringComparison.Ordinal);

    internal static Evolution ToEvolution(int method, int param, string target, IReadOnlyList<string> itemKeys)
    {
        string ItemKey(int i) => i < itemKeys.Count ? itemKeys[i] : "ITEM_NONE";
        Evolution E(string m, string p, params (string, string)[] conds) =>
            new(m, p, target) { Conditions = conds.Select(x => new EvolutionCondition(x.Item1, x.Item2)).ToList() };
        var lv = param.ToString(CultureInfo.InvariantCulture);
        return method switch
        {
            Gen3.Friendship => E("EVO_LEVEL", "0", ("IF_MIN_FRIENDSHIP", Gen3.FriendshipValue)),
            Gen3.FriendshipDay => E("EVO_LEVEL", "0", ("IF_MIN_FRIENDSHIP", Gen3.FriendshipValue), ("IF_TIME", "TIME_DAY")),
            Gen3.FriendshipNight => E("EVO_LEVEL", "0", ("IF_MIN_FRIENDSHIP", Gen3.FriendshipValue), ("IF_TIME", "TIME_NIGHT")),
            Gen3.Level => E("EVO_LEVEL", lv),
            Gen3.Trade => E("EVO_TRADE", "0"),
            Gen3.TradeItem => E("EVO_TRADE", "0", ("IF_HOLD_ITEM", ItemKey(param))),
            Gen3.Item => E("EVO_ITEM", ItemKey(param)),
            Gen3.AtkGtDef => E("EVO_LEVEL", lv, ("IF_ATK_GT_DEF", "")),
            Gen3.AtkEqDef => E("EVO_LEVEL", lv, ("IF_ATK_EQ_DEF", "")),
            Gen3.AtkLtDef => E("EVO_LEVEL", lv, ("IF_ATK_LT_DEF", "")),
            Gen3.Silcoon => E("EVO_LEVEL", lv, ("IF_SILCOON", "")),
            Gen3.Cascoon => E("EVO_LEVEL", lv, ("IF_CASCOON", "")),
            Gen3.Ninjask => E("EVO_LEVEL_NINJASK", lv),
            Gen3.Shedinja => E("EVO_LEVEL_SHEDINJA", lv),
            Gen3.Beauty => E("EVO_LEVEL", "0", ("IF_MIN_BEAUTY", lv)),
            _ => E($"EVO_METHOD_{method}", lv),
        };
    }

    private static List<TrainerPokemon> ReadParty(RomImage rom, int trainer, IReadOnlyList<string> speciesKeys, IReadOnlyList<string> moveKeys, IReadOnlyList<string> itemKeys)
    {
        var flags = rom.U8(trainer);
        var size = rom.U8(trainer + 32);
        var party = rom.Offset(trainer + 36);
        var custom = (flags & 1) != 0;
        var held = (flags & 2) != 0;
        var stride = custom ? 16 : 8;
        var result = new List<TrainerPokemon>();
        for (var k = 0; k < size && party >= 0 && party + (stride * (k + 1)) <= rom.Length; k++)
        {
            var at = party + (stride * k);
            var iv = rom.U16(at) * 31 / 255;
            var species = rom.U16(at + 4);
            var item = held ? rom.U16(at + 6) : 0;
            var moves = custom
                ? Enumerable.Range(0, 4).Select(m => rom.U16(at + (held ? 8 : 6) + (2 * m))).Where(m => m > 0 && m < moveKeys.Count).Select(m => moveKeys[m]).ToList()
                : [];
            result.Add(new TrainerPokemon
            {
                Species = species < speciesKeys.Count ? speciesKeys[species] : "SPECIES_NONE",
                Level = rom.U8(at + 2),
                HeldItem = item > 0 && item < itemKeys.Count ? itemKeys[item] : null,
                Moves = moves,
                Ivs = new BaseStats(iv, iv, iv, iv, iv, iv),
            });
        }

        return result;
    }

    internal static readonly (string Type, int Slots, int Pointer)[] Methods =
        [("land_mons", 12, 4), ("water_mons", 5, 8), ("rock_smash_mons", 5, 12), ("fishing_mons", 10, 16)];

    private static void ReadEncounters(RomImage rom, Gen3Tables t, Gen3RomModule.State state, GameData data, IReadOnlyList<string> speciesKeys)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < t.WildCount; i++)
        {
            var h = t.WildHeaders + (20 * i);
            var group = rom.U8(h);
            var number = rom.U8(h + 1);
            var methods = new List<EncounterMethodTable>();
            foreach (var (type, slots, ptr) in Methods)
            {
                var info = rom.Offset(h + ptr);
                if (info < 0)
                {
                    continue;
                }

                var mons = rom.Offset(info + 4);
                if (mons < 0)
                {
                    continue;
                }

                methods.Add(new EncounterMethodTable
                {
                    Type = type,
                    Rate = rom.U8(info),
                    Slots = Enumerable.Range(0, slots).Select(k => new EncounterSlot(
                        rom.U16(mons + (4 * k) + 2) is var sp && sp < speciesKeys.Count ? speciesKeys[sp] : "SPECIES_NONE",
                        rom.U8(mons + (4 * k)), rom.U8(mons + (4 * k) + 1))).ToList(),
                });
            }

            var place = PlaceName(rom, t, group, number) ?? $"MAP {group}.{number}";
            var constant = Gen3Text.Constant(place);
            seen[constant] = seen.GetValueOrDefault(constant) + 1;
            var table = new EncounterTable
            {
                Key = $"WILD_{group}_{number}_{i}",
                Map = "MAP_" + constant + (seen[constant] > 1 ? $"_{seen[constant]}" : ""),
                Group = "Wild",
                Methods = methods,
            };
            data.Encounters.Add(table);
            state.Encounters[table.Key] = (table, h);
        }
    }

    /// <summary>Map group/number → header → region map section → name.</summary>
    private static string? PlaceName(RomImage rom, Gen3Tables t, int group, int number)
    {
        if (t.MapGroups < 0 || t.MapNames < 0)
        {
            return null;
        }

        var list = rom.Offset(t.MapGroups + (4 * group));
        var header = list < 0 ? -1 : rom.Offset(list + (4 * number));
        if (header < 0)
        {
            return null;
        }

        var section = rom.U8(header + 0x14);
        int name;
        if (t.MapNamesArePointerList)
        {
            if (section < t.MapNamesFirstSection)
            {
                return null;
            }

            name = rom.Offset(t.MapNames + (4 * (section - t.MapNamesFirstSection)));
        }
        else
        {
            name = rom.Offset(t.MapNames + (8 * section) + 4);
        }

        return name < 0 ? null : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(Gen3Text.Read(rom.Bytes.AsSpan(name, Math.Min(40, rom.Length - name))).ToLowerInvariant());
    }
}
