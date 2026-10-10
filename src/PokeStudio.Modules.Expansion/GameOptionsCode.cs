using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PokeStudio.Core.GameOptions;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;
using PokeStudio.Core.SourceCode;

namespace PokeStudio.Modules.Expansion;

/// <summary>
/// The game-code half of the new-game options: Nuzlocke rules, the adaptive boss slot and the Pokémon Center helper.
/// Everything lives in a generated src/poke_studio.c; the game's own files get one marked line each where it hooks in.
/// </summary>
internal static partial class GameOptionsWriter
{
    public const string CodeFile = "src/poke_studio.c";
    public const string CodeHeader = "include/poke_studio.h";
    public const string BattleSetup = "src/battle_setup.c";
    public const string BattleScripts = "src/battle_script_commands.c";
    public const string PoolsSource = "src/trainer_pools.c";
    public const string PoolsHeader = "include/trainer_pools.h";
    public const string WildConfig = "include/config/wild_encounter.h";

    /// <summary>What the code needs from the rest of the project.</summary>
    public sealed record Extras(
        IReadOnlyDictionary<string, string> FamilyRoots,
        IReadOnlyList<string> EncounterAreas,
        IReadOnlyList<string> CenterMaps)
    {
        public static readonly Extras None = new(new Dictionary<string, string>(), [], []);
    }

    public static IReadOnlyList<string> CodeFiles => [CodeFile, CodeHeader, BattleSetup, BattleScripts, PoolsSource, PoolsHeader, WildConfig];

    private static readonly string[] CodeFlagNames =
        ["FLAG_PS_NUZ_FIRST", "FLAG_PS_NUZ_FAINT", "FLAG_PS_NUZ_DUPES", "FLAG_PS_NUZ_EEVEE", "FLAG_PS_NUZ_SHINY", "FLAG_PS_NUZ_NICK", "FLAG_PS_ADAPTIVE", "FLAG_PS_NO_CATCH"];

    private static bool CodeUsed(GameOptionsSettings o) => o.NuzlockeUsed || o.AdaptiveSlot != OptionMode.NotUsed || o.CenterHelper.AnyService;

    public static string AreaFlag(string mapsec) => "FLAG_PS_AREA_" + mapsec.Replace("MAPSEC_", "", StringComparison.Ordinal);

    /// <summary>Flag names the code needs beyond the base options.</summary>
    internal static IEnumerable<string> ExtraFlagNames(GameOptionsSettings o, Extras x)
    {
        if (!CodeUsed(o))
        {
            return [];
        }

        var areas = o.NuzRuleUsed(o.NuzFirstEncounter) ? x.EncounterAreas.Select(AreaFlag) : [];
        return [.. CodeFlagNames, .. areas];
    }

    /// <summary>Areas with wild Pokémon (region map sections of the maps in wild_encounters.json) and evolution families.</summary>
    internal static Extras BuildExtras(ExpansionLayout layout, GameData? data)
    {
        var roots = new Dictionary<string, string>(StringComparer.Ordinal);
        if (data is not null)
        {
            var known = data.Species.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
            var parent = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var set in data.Evolutions)
            {
                foreach (var e in set.Evolutions)
                {
                    if (e.TargetSpecies != set.FromSpecies && known.Contains(e.TargetSpecies) && known.Contains(set.FromSpecies))
                    {
                        parent.TryAdd(e.TargetSpecies, set.FromSpecies);
                    }
                }
            }

            foreach (var species in parent.Keys)
            {
                var root = species;
                var seen = new HashSet<string>(StringComparer.Ordinal) { root };
                while (parent.TryGetValue(root, out var up) && seen.Add(up))
                {
                    root = up;
                }

                if (root != species)
                {
                    roots[species] = root;
                }
            }
        }

        var mapsecByMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var mapsDir = layout.Full("data/maps");
        if (Directory.Exists(mapsDir))
        {
            foreach (var file in Directory.GetDirectories(mapsDir).Select(d => Path.Combine(d, "map.json")).Where(File.Exists))
            {
                var text = File.ReadAllText(file);
                var id = Regex.Match(text, "\"id\"\\s*:\\s*\"(\\w+)\"");
                var sec = Regex.Match(text, "\"region_map_section\"\\s*:\\s*\"(\\w+)\"");
                if (id.Success && sec.Success)
                {
                    mapsecByMap[id.Groups[1].Value] = sec.Groups[1].Value;
                }
            }
        }

        var areas = new SortedSet<string>(StringComparer.Ordinal);
        if (File.Exists(layout.Full(EncountersFile.File)))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(layout.Full(EncountersFile.File)));
            foreach (var group in doc.RootElement.GetProperty("wild_encounter_groups").EnumerateArray())
            {
                if (!group.TryGetProperty("for_maps", out var forMaps) || !forMaps.GetBoolean())
                {
                    continue;
                }

                foreach (var enc in group.GetProperty("encounters").EnumerateArray())
                {
                    if (enc.TryGetProperty("map", out var map) && mapsecByMap.TryGetValue(map.GetString()!, out var sec) && sec != "MAPSEC_NONE")
                    {
                        areas.Add(sec);
                    }
                }
            }
        }

        return new Extras(roots, [.. areas], [.. CenterPlacement.CenterMaps(layout.Root)]);
    }

    /// <summary>Plans the code files and hooks (called from <see cref="Plan"/>).</summary>
    private static void PlanCode(ExpansionLayout layout, GameOptionsSettings o, Ids ids, Func<string, string?> read, Func<string, byte[]?> readBytes,
        Action<string, string?, string> put, Extras x, List<string> notes)
    {
        if (!CodeUsed(o))
        {
            return;
        }

        put(CodeHeader, read(CodeHeader), BuildCodeHeader());
        put(CodeFile, read(CodeFile), BuildCode(o, ids, x));

        // the "can't catch" flag: expansion's own setting, pointed at our flag unless the creator already set one
        var (cfgFile, cfgName) = read(WildConfig)?.Contains("WE_FLAG_NO_CATCHING", StringComparison.Ordinal) == true
            ? (WildConfig, "WE_FLAG_NO_CATCHING") : ("include/config/battle.h", "B_FLAG_NO_CATCHING");
        if (read(cfgFile) is { } cfg && Define(cfg, cfgName) == "0")
        {
            put(cfgFile, cfg, SetDefine(cfg, cfgName, ids.Flags["FLAG_PS_NO_CATCH"]));
        }

        // battle_setup.c: before a wild battle and after every battle
        if (read(BattleSetup) is { } setup && !setup.Contains("PokeStudio_", StringComparison.Ordinal))
        {
            var updated = AddInclude(setup);
            updated = InsertAtBodyStart(updated, "static void DoStandardWildBattle(bool32 isDouble)", "PokeStudio_BeforeWildBattle(); // Poke Studio: Nuzlocke area rules");
            updated = InsertAfterFollowerBlock(updated, "static void CB2_EndWildBattle(void)");
            updated = InsertAfterFollowerBlock(updated, "static void CB2_EndTrainerBattle(void)");
            put(BattleSetup, setup, updated);
        }

        // battle_script_commands.c: nicknames required → straight to the naming screen
        if (read(BattleScripts) is { } scripts && !scripts.Contains("FLAG_PS_NUZ_NICK", StringComparison.Ordinal))
        {
            var fn = scripts.IndexOf("static void Cmd_trygivecaughtmonnick(void)\n", StringComparison.Ordinal) is var a and >= 0
                ? a : scripts.IndexOf("static void Cmd_trygivecaughtmonnick(void)\r\n", StringComparison.Ordinal);
            var case0 = fn < 0 ? -1 : scripts.IndexOf("case 0:", fn, StringComparison.Ordinal);
            if (case0 >= 0)
            {
                var nl = CScanner.LineEnding(scripts);
                var lineEnd = scripts.IndexOf('\n', case0) + 1;
                var indent = CScanner.IndentAt(scripts, case0) + "    ";
                var block = string.Join(nl,
                    $"{indent}if (FlagGet(FLAG_PS_NUZ_NICK)) // {Mark}: Nuzlocke nicknames - no yes/no, straight to naming",
                    $"{indent}{{",
                    $"{indent}    gBattleCommunication[MULTIUSE_STATE] = 2;",
                    $"{indent}    BeginFastPaletteFade(3);",
                    $"{indent}    break;",
                    $"{indent}}}") + nl;
                put(BattleScripts, scripts, scripts.Insert(lineEnd, block));
            }
            else
            {
                notes.Add("The caught-Pokémon nickname step wasn't found, so \"nicknames required\" only works as a rule the player follows.");
            }
        }

        // trainer pools: our pick function for the adaptive boss slot
        if (read(PoolsHeader) is { } poolsH && !poolsH.Contains("POOL_PICK_POKE_STUDIO_ADAPTIVE", StringComparison.Ordinal))
        {
            var m = Regex.Match(poolsH, @"^([ \t]*)POOL_PICK_LOWEST,[^\n]*\n", RegexOptions.Multiline);
            if (m.Success)
            {
                put(PoolsHeader, poolsH, poolsH.Insert(m.Index + m.Length, $"{m.Groups[1].Value}POOL_PICK_POKE_STUDIO_ADAPTIVE, // {Mark}: adaptive boss slot{CScanner.LineEnding(poolsH)}"));
            }
        }

        if (read(PoolsSource) is { } poolsC && !poolsC.Contains("POOL_PICK_POKE_STUDIO_ADAPTIVE", StringComparison.Ordinal))
        {
            var fn = poolsC.IndexOf("GetPickFunctions(", StringComparison.Ordinal);
            var def = fn < 0 ? -1 : poolsC.IndexOf("default:", fn, StringComparison.Ordinal);
            if (def >= 0)
            {
                var nl = CScanner.LineEnding(poolsC);
                var lineStart = poolsC.LastIndexOf('\n', def) + 1;
                var indent = CScanner.IndentAt(poolsC, def);
                var block = string.Join(nl,
                    $"{indent}case POOL_PICK_POKE_STUDIO_ADAPTIVE: // {Mark}: team in order, one slot chosen against the player's party",
                    $"{indent}    pickFunctions.LeadFunction = &PokeStudio_PickNone;",
                    $"{indent}    pickFunctions.AceFunction = &PokeStudio_PickNone;",
                    $"{indent}    pickFunctions.OtherFunction = &PokeStudio_PickAdaptive;",
                    $"{indent}    break;") + nl;
                put(PoolsSource, poolsC, AddInclude(poolsC.Insert(lineStart, block)));
            }
        }

        // the Pokémon Center helper stands in every Center
        if (o.CenterHelper.AnyService)
        {
            var gfx = read("include/constants/event_objects.h")?.Contains("OBJ_EVENT_GFX_SCIENTIST_1,", StringComparison.Ordinal) == true
                ? "OBJ_EVENT_GFX_SCIENTIST_1" : "OBJ_EVENT_GFX_SCIENTIST";
            var skipped = new List<string>();
            foreach (var map in x.CenterMaps)
            {
                if (read(map) is not { } json || json.Contains(CenterPlacement.HelperScript, StringComparison.Ordinal))
                {
                    continue;
                }

                var layoutId = Regex.Match(json, "\"layout\"\\s*:\\s*\"(\\w+)\"").Groups[1].Value;
                var grid = CenterPlacement.LoadGrid(read, readBytes, layoutId);
                var spot = grid is null ? null : CenterPlacement.Find(json, grid);
                if (spot is null)
                {
                    skipped.Add(map.Split('/')[2]);
                    continue;
                }

                put(map, json, CenterPlacement.AddHelper(json, spot, gfx));
            }

            if (skipped.Count > 0)
            {
                notes.Add($"No free spot for the Pokémon Center helper in: {string.Join(", ", skipped)}.");
            }
        }
    }

    private static string AddInclude(string c)
    {
        if (c.Contains("#include \"poke_studio.h\"", StringComparison.Ordinal))
        {
            return c;
        }

        var nl = CScanner.LineEnding(c);
        var last = Regex.Matches(c, @"^#include [""<][^\n]*\n", RegexOptions.Multiline).LastOrDefault();
        return last is null ? c : c.Insert(last.Index + last.Length, $"#include \"poke_studio.h\" // {Mark}{nl}");
    }

    private static string InsertAtBodyStart(string c, string signature, string statement)
    {
        var at = c.IndexOf(signature + "\n", StringComparison.Ordinal) is var a and >= 0 ? a : c.IndexOf(signature + "\r\n", StringComparison.Ordinal);
        if (at < 0)
        {
            return c;
        }

        var open = c.IndexOf('{', at);
        var lineEnd = c.IndexOf('\n', open) + 1;
        return c.Insert(lineEnd, "    " + statement + CScanner.LineEnding(c));
    }

    /// <summary>After the follower-partner block (which restores the party), or at the start of the function.</summary>
    private static string InsertAfterFollowerBlock(string c, string signature)
    {
        const string statement = "PokeStudio_AfterBattle(); // Poke Studio: Nuzlocke rules after a battle";
        var at = c.IndexOf(signature + "\n", StringComparison.Ordinal) is var a and >= 0 ? a : c.IndexOf(signature + "\r\n", StringComparison.Ordinal);
        if (at < 0)
        {
            return c;
        }

        var open = c.IndexOf('{', at);
        var close = CScanner.FindMatching(c, open);
        var restore = c.IndexOf("RestorePartyAfterFollowerNPCBattle();", open, StringComparison.Ordinal);
        if (restore > 0 && restore < close)
        {
            var blockOpen = c.LastIndexOf('{', restore);
            var blockClose = CScanner.FindMatching(c, blockOpen);
            var lineEnd = c.IndexOf('\n', blockClose) + 1;
            var nl = CScanner.LineEnding(c);
            return c.Insert(lineEnd, nl + "    " + statement + nl);
        }

        return InsertAtBodyStart(c, signature, statement);
    }

    private static string BuildCodeHeader() => """
        // Generated by Poke Studio: functions of the new-game options code (src/poke_studio.c).
        #ifndef GUARD_POKE_STUDIO_H
        #define GUARD_POKE_STUDIO_H

        struct Trainer;
        struct PoolRules;

        void PokeStudio_BeforeWildBattle(void);
        void PokeStudio_AfterBattle(void);
        u32 PokeStudio_PickNone(const struct Trainer *trainer, u8 *poolIndexArray, u32 partyIndex, u32 monsCount, u32 battleTypeFlags, struct PoolRules *rules);
        u32 PokeStudio_PickAdaptive(const struct Trainer *trainer, u8 *poolIndexArray, u32 partyIndex, u32 monsCount, u32 battleTypeFlags, struct PoolRules *rules);
        void PokeStudio_MaxIvs(void);
        void PokeStudio_SetEvSpread(void);

        #endif // GUARD_POKE_STUDIO_H

        """.ReplaceLineEndings("\n");

    internal static string BuildCode(GameOptionsSettings o, Ids ids, Extras x)
    {
        var sb = new StringBuilder();
        void L(string line = "") => sb.Append(line).Append('\n');
        L("// Generated by Poke Studio: Nuzlocke rules, the adaptive boss slot and the Pokémon Center helper.");
        L("// Edit them in the app (Game options); changes made here are replaced on the next save.");
        L("#include \"global.h\"");
        L("#include \"battle.h\"");
        L("#include \"battle_util.h\"");
        L("#include \"data.h\"");
        L("#include \"event_data.h\"");
        L("#include \"fieldmap.h\"");
        L("#include \"pokedex.h\"");
        L("#include \"pokemon.h\"");
        L("#include \"pokemon_storage_system.h\"");
        L("#include \"trainer_pools.h\"");
        L("#include \"constants/region_map_sections.h\"");
        L("#include \"poke_studio.h\"");
        L();
        L("#if defined(WE_FLAG_NO_CATCHING) && WE_FLAG_NO_CATCHING != 0");
        L("#define PS_NO_CATCH WE_FLAG_NO_CATCHING");
        L("#elif defined(B_FLAG_NO_CATCHING) && B_FLAG_NO_CATCHING != 0");
        L("#define PS_NO_CATCH B_FLAG_NO_CATCHING");
        L("#else");
        L("#define PS_NO_CATCH FLAG_PS_NO_CATCH");
        L("#endif");
        L();

        var areas = o.NuzRuleUsed(o.NuzFirstEncounter) ? x.EncounterAreas.Where(a => ids.Flags.ContainsKey(AreaFlag(a))).ToList() : [];
        L("// Region map section → flag set once the first wild Pokémon there was met.");
        L("static const u16 sAreaFlags[][2] =");
        L("{");
        foreach (var a in areas)
        {
            L($"    {{{a}, {AreaFlag(a)}}},");
        }

        L("    {MAPSEC_NONE, 0},");
        L("};");
        L();
        L("// Evolution family of each species (the first stage); species not listed are their own family.");
        L("static const u16 sFamilyRoot[NUM_SPECIES] =");
        L("{");
        foreach (var (species, root) in x.FamilyRoots.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            L($"    [{species}] = {root},");
        }

        L("};");
        L();
        L("""
            static u32 FamilyRoot(u32 species)
            {
                return (species < NUM_SPECIES && sFamilyRoot[species] != SPECIES_NONE) ? sFamilyRoot[species] : species;
            }

            static bool32 FamilyCaught(u32 species)
            {
                u32 root = FamilyRoot(species);
                for (u32 i = 1; i < NUM_SPECIES; i++)
                {
                    if (FamilyRoot(i) == root && GetSetPokedexFlag(SpeciesToNationalPokedexNum(i), FLAG_GET_CAUGHT))
                        return TRUE;
                }
                return FALSE;
            }

            static u32 AreaFlag(u32 mapsec)
            {
                for (u32 i = 0; sAreaFlags[i][1] != 0; i++)
                {
                    if (sAreaFlags[i][0] == mapsec)
                        return sAreaFlags[i][1];
                }
                return 0;
            }

            // Before a wild battle: only the first Pokémon met in an area can be caught (dupes and shiny clauses aside).
            void PokeStudio_BeforeWildBattle(void)
            {
                struct Pokemon *wild = &gEnemyParty[0];
                u32 species = GetMonData(wild, MON_DATA_SPECIES);
                u32 flag;

                FlagClear(PS_NO_CATCH);
                if (!FlagGet(FLAG_PS_NUZ_FIRST))
                    return;
                if (FlagGet(FLAG_PS_NUZ_SHINY) && IsMonShiny(wild))
                    return;
                if (FlagGet(FLAG_PS_NUZ_DUPES) && FamilyCaught(species)
                 && !(FlagGet(FLAG_PS_NUZ_EEVEE) && FamilyRoot(species) == SPECIES_EEVEE))
                {
                    FlagSet(PS_NO_CATCH); // a duplicate doesn't use up the area
                    return;
                }
                flag = AreaFlag(gMapHeader.regionMapSectionId);
                if (flag == 0)
                    return;
                if (FlagGet(flag))
                    FlagSet(PS_NO_CATCH);
                else
                    FlagSet(flag);
            }

            static bool32 IsUsableMon(struct Pokemon *mon)
            {
                return GetMonData(mon, MON_DATA_SPECIES) != SPECIES_NONE && !GetMonData(mon, MON_DATA_IS_EGG);
            }

            // After any battle: Pokémon that fainted are gone, unless the whole team fainted (the run is over; the player decides).
            void PokeStudio_AfterBattle(void)
            {
                u32 alive = 0, released = 0;

                FlagClear(PS_NO_CATCH);
                if (!FlagGet(FLAG_PS_NUZ_FAINT))
                    return;
                for (u32 i = 0; i < PARTY_SIZE; i++)
                {
                    if (IsUsableMon(&gPlayerParty[i]) && GetMonData(&gPlayerParty[i], MON_DATA_HP) != 0)
                        alive++;
                }
                if (alive == 0)
                    return;
                for (u32 i = 0; i < PARTY_SIZE; i++)
                {
                    if (IsUsableMon(&gPlayerParty[i]) && GetMonData(&gPlayerParty[i], MON_DATA_HP) == 0)
                    {
                        ZeroMonData(&gPlayerParty[i]);
                        released++;
                    }
                }
                if (released != 0)
                {
                    CompactPartySlots();
                    CalculatePlayerPartyCount();
                }
            }

            // Adaptive boss slot: the team stays in order; the flexible member (tag 7) and its candidates compete for one slot.
            u32 PokeStudio_PickNone(const struct Trainer *trainer, u8 *poolIndexArray, u32 partyIndex, u32 monsCount, u32 battleTypeFlags, struct PoolRules *rules)
            {
                return POOL_SLOT_DISABLED;
            }

            static uq4_12_t Effectiveness(enum Type attack, u32 species)
            {
                enum Type t1 = gSpeciesInfo[species].types[0], t2 = gSpeciesInfo[species].types[1];
                uq4_12_t mod = GetTypeModifier(attack, t1);
                if (t2 != t1)
                    mod = uq4_12_multiply(mod, GetTypeModifier(attack, t2));
                return mod;
            }

            // How well a species does against the player's party: its best STAB hit minus the worst hit it takes.
            static s32 MatchupScore(u32 species)
            {
                s32 score = 0;
                for (u32 i = 0; i < PARTY_SIZE; i++)
                {
                    struct Pokemon *mon = &gPlayerParty[i];
                    u32 foe = GetMonData(mon, MON_DATA_SPECIES);
                    s32 attack = 0, defend = 0;
                    if (!IsUsableMon(mon) || GetMonData(mon, MON_DATA_HP) == 0)
                        continue;
                    for (u32 j = 0; j < 2; j++)
                    {
                        s32 hit = (s32)Effectiveness(gSpeciesInfo[species].types[j], foe);
                        s32 taken = (s32)Effectiveness(gSpeciesInfo[foe].types[j], species);
                        if (hit > attack)
                            attack = hit;
                        if (taken > defend)
                            defend = taken;
                    }
                    score += attack - defend;
                }
                return score;
            }

            // Marks a member as used; the team's own members are always returned, even if a pool rule pruned them.
            static u32 TakeFromPool(u8 *poolIndexArray, u32 poolSize, u32 index)
            {
                for (u32 a = 0; a < poolSize; a++)
                {
                    if (poolIndexArray[a] == index)
                        poolIndexArray[a] = POOL_SLOT_DISABLED;
                }
                return index;
            }

            static bool32 SameAsTeamMember(const struct Trainer *trainer, u32 monsCount, u32 flexIndex, u32 species)
            {
                for (u32 i = 0; i < monsCount; i++)
                {
                    if (i != flexIndex && gSpeciesInfo[trainer->party[i].species].natDexNum == gSpeciesInfo[species].natDexNum)
                        return TRUE;
                }
                return FALSE;
            }

            u32 PokeStudio_PickAdaptive(const struct Trainer *trainer, u8 *poolIndexArray, u32 partyIndex, u32 monsCount, u32 battleTypeFlags, struct PoolRules *rules)
            {
                u32 flexIndex = POOL_SLOT_DISABLED, best = POOL_SLOT_DISABLED;
                s32 bestScore = 0;

                for (u32 i = 0; i < monsCount && i < trainer->poolSize; i++)
                {
                    if (trainer->party[i].tags & MON_POOL_TAG_TAG7)
                    {
                        flexIndex = i;
                        break;
                    }
                }
                // the team itself, in its own order (candidates come after it in the pool)
                if (partyIndex != flexIndex)
                    return TakeFromPool(poolIndexArray, trainer->poolSize, partyIndex);
                if (!FlagGet(FLAG_PS_ADAPTIVE))
                    return TakeFromPool(poolIndexArray, trainer->poolSize, flexIndex);
                for (u32 i = 0; i < trainer->poolSize; i++)
                {
                    if (i != flexIndex && i < monsCount)
                        continue;
                    if (!(trainer->party[i].tags & MON_POOL_TAG_TAG7))
                        continue;
                    bool32 available = FALSE;
                    for (u32 a = 0; a < trainer->poolSize; a++)
                    {
                        if (poolIndexArray[a] == i)
                            available = TRUE;
                    }
                    if (!available || SameAsTeamMember(trainer, monsCount, flexIndex, trainer->party[i].species))
                        continue;
                    s32 score = MatchupScore(trainer->party[i].species);
                    if (best == POOL_SLOT_DISABLED || score > bestScore)
                    {
                        best = i;
                        bestScore = score;
                    }
                }
                return TakeFromPool(poolIndexArray, trainer->poolSize, best != POOL_SLOT_DISABLED ? best : flexIndex);
            }

            // Pokémon Center helper (callnative): perfect IVs for the chosen party member (gSpecialVar_0x8004).
            void PokeStudio_MaxIvs(void)
            {
                struct Pokemon *mon = &gPlayerParty[gSpecialVar_0x8004];
                u32 iv = MAX_PER_STAT_IVS;
                for (u32 i = 0; i < NUM_STATS; i++)
                    SetMonData(mon, MON_DATA_HP_IV + i, &iv);
                CalculateMonStats(mon);
            }

            // EV spreads offered by the helper, chosen with gSpecialVar_Result.
            static const u8 sEvSpreads[][NUM_STATS] =
            {
                {0},
                {[STAT_HP] = 4, [STAT_ATK] = 252, [STAT_SPEED] = 252},
                {[STAT_HP] = 4, [STAT_SPATK] = 252, [STAT_SPEED] = 252},
                {[STAT_HP] = 252, [STAT_ATK] = 252, [STAT_SPDEF] = 4},
                {[STAT_HP] = 252, [STAT_SPATK] = 252, [STAT_SPDEF] = 4},
                {[STAT_HP] = 252, [STAT_DEF] = 252, [STAT_SPDEF] = 4},
                {[STAT_HP] = 252, [STAT_DEF] = 4, [STAT_SPDEF] = 252},
            };

            void PokeStudio_SetEvSpread(void)
            {
                struct Pokemon *mon = &gPlayerParty[gSpecialVar_0x8004];
                u32 spread = gSpecialVar_Result < ARRAY_COUNT(sEvSpreads) ? gSpecialVar_Result : 0;
                for (u32 i = 0; i < NUM_STATS; i++)
                {
                    u32 ev = sEvSpreads[spread][i];
                    SetMonData(mon, MON_DATA_HP_EV + i, &ev);
                }
                CalculateMonStats(mon);
            }
            """.ReplaceLineEndings("\n"));
        return sb.ToString();
    }
}
