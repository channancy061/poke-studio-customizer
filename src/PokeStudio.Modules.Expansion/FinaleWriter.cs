using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PokeStudio.Core.Modules;
using PokeStudio.Core.SourceCode;
using PokeStudio.Core.Story;

namespace PokeStudio.Modules.Expansion;

/// <summary>
/// The final chapter in an expansion project. Its flags go in the shared generated header; a few marked lines hook the
/// game: the new-game flag reset (the guide starts hidden), the Hall of Fame (the guide calls the player), the credits
/// (held until the chapter is over), and the ball throw (no catching while the boss's forms attack). The guide's people,
/// the scripts and the boss's game code (src/poke_studio_finale.c) are generated.
/// </summary>
internal static partial class FinaleWriter
{
    public const string Code = "src/poke_studio_finale.c";
    public const string NewGame = "data/scripts/new_game.inc";
    public const string HallOfFameC = "src/hall_of_fame.c";
    public const string ItemUse = "src/item_use.c";
    public const string Specials = "data/specials.inc";
    private const string Mark = "Poke Studio final chapter";
    private const string Label = "PokeStudio_Finale";
    private const string HomeScript = StoryWriter.ScriptPrefix + "FinaleHome";
    private const string IslandScript = StoryWriter.ScriptPrefix + "FinaleIsland";

    public static IReadOnlyList<string> Files { get; } = [Code, NewGame, HallOfFameC, ItemUse];

    /// <summary>The maps the chapter puts people on (besides the guide's battle).</summary>
    public static IEnumerable<string> Maps(GameData data) =>
        data.Story?.Finale is { } f ? [.. f.HomeMaps, f.IslandMap] : [];

    public static void Plan(string root, GameData data, FinaleSettings f, Func<string, string?> read, Action<string, string?, string> put,
        StringBuilder script, Dictionary<string, List<StoryWriter.MapPerson>> people)
    {
        var fireRed = Directory.Exists(Path.Combine(root, "data", "maps", "PalletTown"));
        var nuzlocke = read(GameOptionsWriter.Header)?.Contains("FLAG_PS_NUZ_FAINT", StringComparison.Ordinal) == true;
        var waitAfterLegendary = WaitAfterLegendary(read);

        // 1. flags: in the shared header, written by StoryWriter

        // 2. a new game hides the guide everywhere until the Hall of Fame
        if (read(NewGame) is { } newGame)
        {
            var clean = Unmark(newGame);
            var at = clean.IndexOf("EventScript_ResetAllMapFlags::", StringComparison.Ordinal);
            if (at >= 0)
            {
                var lineEnd = clean.IndexOf('\n', at) + 1;
                var hide = string.Concat(new[] { Finale.HomeHide, Finale.CaveHide, Finale.IslandHide }.Select(fl => $"\tsetflag {fl} @ {Mark}\n"));
                put(NewGame, newGame, clean.Insert(lineEnd, hide));
            }
        }

        // 3. the Hall of Fame: the guide is waiting at home afterwards
        foreach (var dir in HallOfFameMaps(root, fireRed))
        {
            var file = $"data/maps/{dir}/scripts.inc";
            if (read(file) is not { } hof)
            {
                continue;
            }

            var clean = Unmark(hof);
            var edited = Regex.Replace(clean, @"^([ \t]*)(special (?:EnterHallOfFame|GameClear)\b[^\n]*\n)",
                m => $"{m.Groups[1].Value}call_if_unset {Finale.Done}, {Label}_Called @ {Mark}\n{m.Value}", RegexOptions.Multiline);
            put(file, hof, edited);
        }

        // 4. credits are held until the chapter is over (the game is saved at the Hall of Fame; a soft reset continues from home)
        if (fireRed)
        {
            const string indigo = "data/maps/IndigoPlateau_Exterior/scripts.inc";
            if (read(indigo) is { } credits)
            {
                var clean = Unmark(credits);
                var at = Regex.Match(clean, @"IndigoPlateau_Exterior_EventScript_Credits::\s*\n[ \t]*lockall[^\n]*\n");
                if (at.Success)
                {
                    put(indigo, credits, clean.Insert(at.Index + at.Length, $"\tgoto_if_unset {Finale.Done}, {Label}_HoldCredits @ {Mark}\n"));
                }
            }
        }
        else if (read(HallOfFameC) is { } hofC)
        {
            var clean = Unmark(hofC);
            var at = Regex.Match(clean, @"static void StartCredits\(void\)\s*\{[^\n]*\n");
            if (at.Success)
            {
                put(HallOfFameC, hofC, clean.Insert(at.Index + at.Length,
                    $"    if (!FlagGet({Finale.Done})) // {Mark}: held until the story is over\n    {{ // {Mark}\n        DoSoftReset(); // {Mark}\n        return; // {Mark}\n    }} // {Mark}\n"));
            }
        }

        // 5. no catching while the boss's forms attack
        if (read(ItemUse) is { } itemUse)
        {
            var clean = Unmark(itemUse);
            var fn = Regex.Match(clean, @"static u32 GetBallThrowableState\(void\)\s*\{[^\n]*\n");
            if (fn.Success)
            {
                var withCheck = clean.Insert(fn.Index + fn.Length,
                    $"    if (gPokeStudioFinaleNoCatch && (gBattleTypeFlags & BATTLE_TYPE_LEGENDARY)) // {Mark}\n        return BALL_THROW_UNABLE_DISABLED_FLAG; // {Mark}\n");
                put(ItemUse, itemUse, withCheck.Insert(fn.Index, $"extern bool8 gPokeStudioFinaleNoCatch; // {Mark}\n"));
            }
        }

        // 6. the boss's game code
        put(Code, read(Code) ?? "", BossCode(f, data, read));

        // 7. people: the guide at home (each home map) and on the island
        string? MapId(string map) => read($"data/maps/{map}/map.json") is { } json && Regex.Match(json, "\"id\"\\s*:\\s*\"(MAP_\\w+)\"") is { Success: true } m ? m.Groups[1].Value : null;
        foreach (var home in f.HomeMaps)
        {
            if (StoryWriter.FreeSpot(root, home) is { } spot)
            {
                Add(people, home, new StoryWriter.MapPerson(HomeScript, f.GuideLook, spot.X, spot.Y, spot.Elevation, Facing.Down, 0, Finale.HomeHide));
            }
        }

        var arrival = StoryWriter.Arrival(root, f.IslandMap);
        if (arrival is { } a && StoryWriter.FreeSpot(root, f.IslandMap, (a.X, a.Y)) is { } islandSpot)
        {
            Add(people, f.IslandMap, new StoryWriter.MapPerson(IslandScript, f.GuideLook, islandSpot.X, islandSpot.Y, islandSpot.Elevation, Facing.Down, 0, Finale.IslandHide));
        }

        // 8. the scripts
        var island = MapId(f.IslandMap) ?? "MAP_BIRTH_ISLAND_EXTERIOR";
        var (ax, ay) = arrival ?? (0, 0);
        Scripts(script, f, island, ax, ay, fireRed, nuzlocke, waitAfterLegendary);
    }

    private static void Add(Dictionary<string, List<StoryWriter.MapPerson>> people, string map, StoryWriter.MapPerson person)
    {
        if (!people.TryGetValue(map, out var list))
        {
            people[map] = list = [];
        }

        list.Add(person);
    }

    /// <summary>Whether a script has to wait after starting a legendary battle (older projects' special doesn't wait itself).</summary>
    internal static bool WaitAfterLegendary(Func<string, string?> read) =>
        read(Specials) is not { } specials || !Regex.IsMatch(specials, @"def_special BattleSetup_StartLegendaryBattle,\s*waitstate=1");

    /// <summary>Takes the chapter's hooks out of the game's files again (after it's removed), so nothing points at its scripts.</summary>
    public static void Clean(string root, Func<string, string?> read, Action<string, string?, string> put)
    {
        var fireRed = Directory.Exists(Path.Combine(root, "data", "maps", "PalletTown"));
        var files = new List<string> { NewGame, HallOfFameC, ItemUse, "data/maps/IndigoPlateau_Exterior/scripts.inc" };
        files.AddRange(HallOfFameMaps(root, fireRed).Select(d => $"data/maps/{d}/scripts.inc"));
        foreach (var file in files)
        {
            if (read(file) is { } text)
            {
                put(file, text, Unmark(text));
            }
        }

        if (read(Code) is { } code)
        {
            put(Code, code, "// Generated by Poke Studio: the final chapter is switched off. Add it again on the Story battles page.\n#include \"global.h\"\n");
        }
    }

    private static string Unmark(string text) => Regex.Replace(text, $@"^[^\n]*(?://|@) {Mark}[^\n]*\r?\n", "", RegexOptions.Multiline);

    private static IEnumerable<string> HallOfFameMaps(string root, bool fireRed)
    {
        var maps = Path.Combine(root, "data", "maps");
        return !Directory.Exists(maps)
            ? []
            : Directory.GetDirectories(maps).Select(Path.GetFileName).OfType<string>()
                .Where(d => d.Contains("HallOfFame", StringComparison.Ordinal) && (fireRed || !d.EndsWith("_Frlg", StringComparison.Ordinal)));
    }

    private static void Scripts(StringBuilder s, FinaleSettings f, string island, int ax, int ay, bool fireRed, bool nuzlocke, bool waitAfterLegendary)
    {
        const string nl = "\n";
        void L(string line) => s.Append(line).Append('\n');
        void T(string label, string text) => s.Append('\n').Append(label).Append(':').Append('\n').Append(TextScripts.Format(text, "\t", true, nl)).Append('\n');
        void Round(int round)
        {
            L($"\tsetvar VAR_0x8004, {round.ToString(CultureInfo.InvariantCulture)}");
            L($"\tcallnative PokeStudio_PrepareFinalBoss");
            L("\tspecial BattleSetup_StartLegendaryBattle");
            if (waitAfterLegendary)
            {
                L("\twaitstate");
            }

            L("\tspecialvar VAR_RESULT, GetBattleOutcome");
        }

        L("");
        L($"@ The final chapter: {f.GuideName} calls you after the Hall of Fame, waits at {f.MeetName}, then {f.IslandName}.");
        L($"{Label}_Called::");
        L($"\tclearflag {Finale.HomeHide}");
        L("\treturn");
        L("");
        L($"{Label}_HoldCredits::");
        if (fireRed)
        {
            L("\tsetvar VAR_MAP_SCENE_INDIGO_PLATEAU_EXTERIOR, 0");
        }

        L("\tcallnative DoSoftReset");
        L("\tend");

        // at home: the call to the meeting place
        L("");
        L($"{HomeScript}::");
        L("\tlock");
        L("\tfaceplayer");
        L($"\tmsgbox {Label}_Text_Call, MSGBOX_DEFAULT");
        L("\tclosemessage");
        L($"\tsetflag {Finale.HomeHide}");
        L($"\tclearflag {Finale.CaveHide}");
        L("\tremoveobject VAR_LAST_TALKED");
        L("\trelease");
        L("\tend");

        // after the guide's battle: to the island
        L("");
        L($"{Finale.WonScript}::");
        L($"\tmsgbox {Label}_Text_Cause, MSGBOX_DEFAULT");
        L($"\tclearflag {Finale.IslandHide}");
        L($"{Label}_GoToIsland:");
        L("\tclosemessage");
        L("\tfadescreen FADE_TO_BLACK");
        L($"\twarp {island}, {ax.ToString(CultureInfo.InvariantCulture)}, {ay.ToString(CultureInfo.InvariantCulture)}");
        L("\twaitstate");
        L("\trelease");
        L("\tend");

        // talking to the guide again at the meeting place
        L("");
        L($"{Finale.AfterBattleScript}::");
        L($"\tgoto_if_set {Finale.Done}, {Label}_Thanks");
        if (nuzlocke)
        {
            L($"\tcall_if_set FLAG_PS_NUZ_FAINT, {Label}_CheckLost");
        }

        L($"\tmsgbox {Label}_Text_GoBack, MSGBOX_YESNO");
        L($"\tgoto_if_eq VAR_RESULT, YES, {Label}_GoToIsland");
        L("\trelease");
        L("\tend");
        L($"{Label}_Thanks:");
        L($"\tmsgbox {Label}_Text_Thanks, MSGBOX_AUTOCLOSE");
        L("\tend");
        if (nuzlocke)
        {
            L($"{Label}_CheckLost:");
            L($"\tgoto_if_set {Finale.Tried}, {Label}_Lost");
            L("\treturn");
        }

        L($"{Label}_Lost:");
        L($"\tmsgbox {Label}_Text_Lost, MSGBOX_DEFAULT");
        L("\trelease");
        L("\tend");

        // on the island: explain, heal, get ready, then the forms one after another and the catch
        L("");
        L($"{IslandScript}::");
        L("\tlock");
        L("\tfaceplayer");
        L("\tcallnative PokeStudio_FinaleCatchable");
        L($"\tgoto_if_set {Finale.Done}, {Label}_Thanks");
        if (nuzlocke)
        {
            L($"\tcall_if_set FLAG_PS_NUZ_FAINT, {Label}_CheckLost");
        }

        L($"\tgoto_if_set {Finale.FormsBeaten}, {Label}_Catch");
        L($"\tmsgbox {Label}_Text_Island, MSGBOX_DEFAULT");
        L("\tspecial HealPlayerParty");
        L($"\tmsgbox {Label}_Text_Ready, MSGBOX_YESNO");
        L($"\tgoto_if_eq VAR_RESULT, NO, {Label}_NotReady");
        L($"\tmsgbox {Label}_Text_Sure, MSGBOX_YESNO");
        L($"\tgoto_if_eq VAR_RESULT, NO, {Label}_NotReady");
        L($"\tsetflag {Finale.Tried}");
        L($"\tmsgbox {Label}_Text_HereItComes, MSGBOX_DEFAULT");
        L("\tclosemessage");
        for (var round = 0; round < f.Forms.Count; round++)
        {
            if (round > 0)
            {
                L($"\tmsgbox {Label}_Text_NextForm, MSGBOX_DEFAULT");
                L("\tclosemessage");
            }

            Round(round);
            L($"\tgoto_if_ne VAR_RESULT, B_OUTCOME_WON, {Label}_Stopped");
        }

        L($"\tsetflag {Finale.FormsBeaten}");
        L($"{Label}_Catch:");
        L($"\tmsgbox {Label}_Text_CatchIt, MSGBOX_DEFAULT");
        L("\tclosemessage");
        Round(99);
        L($"\tgoto_if_eq VAR_RESULT, B_OUTCOME_CAUGHT, {Label}_End");
        L($"\tmsgbox {Label}_Text_GotAway, MSGBOX_DEFAULT");
        L("\trelease");
        L("\tend");
        L($"{Label}_Stopped:");
        L("\tcallnative PokeStudio_FinaleCatchable");
        L($"\tmsgbox {Label}_Text_Stopped, MSGBOX_DEFAULT");
        L("\trelease");
        L("\tend");
        L($"{Label}_NotReady:");
        L($"\tmsgbox {Label}_Text_TakeYourTime, MSGBOX_DEFAULT");
        L("\trelease");
        L("\tend");

        // the end: the regions part, the challengers go home, the credits roll
        L($"{Label}_End:");
        L($"\tmsgbox {Label}_Text_TheEnd, MSGBOX_DEFAULT");
        L($"\tsetflag {Finale.Done}");
        L("\tclosemessage");
        if (fireRed)
        {
            L("\tsetvar VAR_MAP_SCENE_INDIGO_PLATEAU_EXTERIOR, 1");
            L("\tfadescreen FADE_TO_BLACK");
            L("\twarp MAP_INDIGO_PLATEAU_EXTERIOR, 11, 6");
            L("\twaitstate");
        }
        else
        {
            L("\tfadescreenspeed FADE_TO_BLACK, 24");
            L("\tspecial GameClear");
        }

        L("\treleaseall");
        L("\tend");

        T($"{Label}_Text_Call", $"{f.GuideName}: {{PLAYER}}! Congratulations,\\nChampion. But it isn't over.\\p{f.Regions} are still collapsing\\ninto each other. The challengers\\lwho stood in your way felt it too.\\pMeet me at {f.MeetName}. Hurry!");
        T($"{Label}_Text_Cause", $"{f.GuideName}: The collapse has one cause:\\n{f.BossName}.\\pIt fell on {f.IslandName}, and its\\npower is pulling the regions\\ltogether.\\pAs Champion, only you can stop it.\\nCome. We go now.");
        T($"{Label}_Text_GoBack", $"{f.GuideName}: Ready to go back to\\n{f.IslandName}?");
        T($"{Label}_Text_Thanks", $"{f.GuideName}: {f.Regions} are their own\\nregions again. Thank you, Champion.");
        T($"{Label}_Text_Lost", $"{f.GuideName}: {f.BossName} was too strong…\\pThe collapse can't be stopped now.\\nYour Nuzlocke ends here.");
        T($"{Label}_Text_Island", $"{f.GuideName}: This is where {f.BossName}\\nfell. Its forms will attack one\\lafter another, without rest.\\pLet me heal your team. Then put\\nit in the order you want.");
        T($"{Label}_Text_Ready", "Are you ready to face it?");
        T($"{Label}_Text_Sure", "There's no healing between its\\nforms. Are you sure?");
        T($"{Label}_Text_HereItComes", $"{f.BossName} is here!");
        T($"{Label}_Text_NextForm", $"{f.BossName} changed its form!");
        T($"{Label}_Text_CatchIt", $"{f.BossName} is weakened!\\nCatch it to stop the collapse!");
        T($"{Label}_Text_GotAway", $"{f.GuideName}: It's still out there.\\nTalk to me when you're ready\\lto try again.");
        T($"{Label}_Text_Stopped", $"{f.GuideName}: {f.BossName} is still raging.\\nTalk to me when you're ready.");
        T($"{Label}_Text_TakeYourTime", $"{f.GuideName}: Take your time. Get your\\nteam in order, then talk to me.");
        T($"{Label}_Text_TheEnd", $"The rift is closing!\\p{f.Regions} are drifting\\napart. The challengers are\\lheading home.\\p{f.GuideName}: You did it, Champion.");
    }

    /// <summary>src/poke_studio_finale.c: the boss's forms in an order and with moves chosen against the player's team.</summary>
    private static string BossCode(FinaleSettings f, GameData data, Func<string, string?> read)
    {
        var parties = read("include/pokemon.h")?.Contains("gParties[", StringComparison.Ordinal) == true;
        var forms = string.Join(", ", f.Forms);
        return $$"""
            // Generated by Poke Studio: the final chapter's boss. Edit the chapter in the app (Story battles).
            #include "global.h"
            #include "battle.h"
            #include "battle_util.h"
            #include "event_data.h"
            #include "move.h"
            #include "pokemon.h"
            #include "random.h"
            #include "constants/battle.h"
            #include "constants/moves.h"
            #include "constants/species.h"

            #define PS_PLAYER_PARTY {{(parties ? "gParties[B_TRAINER_PLAYER]" : "gPlayerParty")}}
            #define PS_ENEMY_PARTY {{(parties ? "gParties[B_TRAINER_OPPONENT_A]" : "gEnemyParty")}}

            static const u16 sForms[] = { {{forms}} };
            #define FORM_COUNT ARRAY_COUNT(sForms)
            #define FORM_LEVEL {{f.FormLevel.ToString(CultureInfo.InvariantCulture)}}
            #define CATCH_SPECIES {{f.Catch}}
            #define CATCH_LEVEL {{f.CatchLevel.ToString(CultureInfo.InvariantCulture)}}

            EWRAM_DATA bool8 gPokeStudioFinaleNoCatch = FALSE;
            EWRAM_DATA static u8 sOrder[FORM_COUNT] = {0};

            static bool32 IsUsable(struct Pokemon *mon)
            {
                return GetMonData(mon, MON_DATA_SPECIES) != SPECIES_NONE && !GetMonData(mon, MON_DATA_IS_EGG) && GetMonData(mon, MON_DATA_HP) != 0;
            }

            static u32 Effect(enum Type attack, struct Pokemon *mon)
            {
                u32 species = GetMonData(mon, MON_DATA_SPECIES);
                enum Type t1 = gSpeciesInfo[species].types[0], t2 = gSpeciesInfo[species].types[1];
                uq4_12_t mod = GetTypeModifier(attack, t1);
                if (t2 != t1)
                    mod = uq4_12_multiply(mod, GetTypeModifier(attack, t2));
                return mod; // 4096 = normal damage
            }

            // How hard a form is on the player's team: what it deals against their bulk, minus what it takes from their hitters,
            // and outspeeding them.
            static s32 FormScore(u32 form)
            {
                struct Pokemon *boss = &PS_ENEMY_PARTY[0];
                s32 score = 0;
                CreateMon(boss, form, FORM_LEVEL, Random32(), OTID_STRUCT_RANDOM_NO_SHINY);
                for (u32 i = 0; i < PARTY_SIZE; i++)
                {
                    struct Pokemon *mon = &PS_PLAYER_PARTY[i];
                    if (!IsUsable(mon))
                        continue;
                    u32 hit = max(GetMonData(boss, MON_DATA_ATK), GetMonData(boss, MON_DATA_SPATK));
                    u32 bulk = (GetMonData(mon, MON_DATA_DEF) + GetMonData(mon, MON_DATA_SPDEF)) / 2 + 1;
                    u32 theirs = max(GetMonData(mon, MON_DATA_ATK), GetMonData(mon, MON_DATA_SPATK));
                    u32 ours = (GetMonData(boss, MON_DATA_DEF) + GetMonData(boss, MON_DATA_SPDEF)) / 2 + 1;
                    score += (s32)(hit * 100 / bulk) - (s32)(theirs * 100 / ours);
                    if (GetMonData(boss, MON_DATA_SPEED) > GetMonData(mon, MON_DATA_SPEED))
                        score += 25;
                }
                return score;
            }

            static void ChooseOrder(void)
            {
                s32 scores[FORM_COUNT];
                for (u32 i = 0; i < FORM_COUNT; i++)
                {
                    sOrder[i] = i;
                    scores[i] = FormScore(sForms[i]);
                }
                // the form that does best against this team comes first
                for (u32 i = 0; i < FORM_COUNT; i++)
                {
                    for (u32 j = i + 1; j < FORM_COUNT; j++)
                    {
                        if (scores[sOrder[j]] > scores[sOrder[i]])
                        {
                            u8 t = sOrder[i];
                            sOrder[i] = sOrder[j];
                            sOrder[j] = t;
                        }
                    }
                }
            }

            // A move's worth against the player's team: hitting many of them super effectively, hard.
            static u32 MoveValue(enum Move move, u32 species, u32 *covers)
            {
                u32 value = 0, power = GetMovePower(move);
                enum Type type = GetMoveType(move);
                *covers = 0;
                if (power == 0 || GetMoveCategory(move) == DAMAGE_CATEGORY_STATUS)
                    return 0;
                if (power > 150)
                    power = 150;
                if (type == gSpeciesInfo[species].types[0] || type == gSpeciesInfo[species].types[1])
                    power = power * 3 / 2;
                for (u32 i = 0; i < PARTY_SIZE; i++)
                {
                    struct Pokemon *mon = &PS_PLAYER_PARTY[i];
                    if (!IsUsable(mon))
                        continue;
                    u32 effect = Effect(type, mon);
                    if (effect > 4096)
                        (*covers)++;
                    value += power * effect / 4096;
                }
                return value;
            }

            // Four moves, each covering the most of the player's team it can (no two of the same type).
            static void ChooseMoves(struct Pokemon *boss, u32 species)
            {
                enum Move chosen[MAX_MON_MOVES] = {MOVE_NONE, MOVE_NONE, MOVE_NONE, MOVE_NONE};
                const struct LevelUpMove *levelUp = GetSpeciesLevelUpLearnset(species);
                const u16 *teachable = GetSpeciesTeachableLearnset(species);
                for (u32 slot = 0; slot < MAX_MON_MOVES; slot++)
                {
                    enum Move best = MOVE_NONE;
                    u32 bestCovers = 0, bestValue = 0;
                    for (u32 pass = 0; pass < 2; pass++)
                    {
                        for (u32 i = 0; ; i++)
                        {
                            enum Move move;
                            if (pass == 0)
                            {
                                if (levelUp == NULL || levelUp[i].move == LEVEL_UP_MOVE_END)
                                    break;
                                move = levelUp[i].move;
                            }
                            else
                            {
                                if (teachable == NULL || teachable[i] == MOVE_UNAVAILABLE)
                                    break;
                                move = teachable[i];
                            }
                            bool32 taken = FALSE;
                            for (u32 k = 0; k < slot; k++)
                            {
                                if (chosen[k] == move || GetMoveType(chosen[k]) == GetMoveType(move))
                                    taken = TRUE;
                            }
                            if (taken)
                                continue;
                            u32 covers, value = MoveValue(move, species, &covers);
                            if (value > 0 && (covers > bestCovers || (covers == bestCovers && value > bestValue)))
                            {
                                best = move;
                                bestCovers = covers;
                                bestValue = value;
                            }
                        }
                    }
                    chosen[slot] = best;
                }
                for (u32 slot = 0; slot < MAX_MON_MOVES; slot++)
                {
                    if (chosen[slot] != MOVE_NONE)
                        SetMonMoveSlot(boss, chosen[slot], slot);
                }
            }

            // callnative: gSpecialVar_0x8004 = the round (0 is the first form; 99 is the catch). The enemy party then holds
            // the boss for BattleSetup_StartLegendaryBattle.
            void PokeStudio_PrepareFinalBoss(void)
            {
                u32 round = gSpecialVar_0x8004, species, level;
                struct Pokemon *boss = &PS_ENEMY_PARTY[0];
                if (round == 0)
                    ChooseOrder();
                if (round < FORM_COUNT)
                {
                    species = sForms[sOrder[round]];
                    level = FORM_LEVEL;
                    gPokeStudioFinaleNoCatch = TRUE;
                }
                else
                {
                    species = CATCH_SPECIES;
                    level = CATCH_LEVEL;
                    gPokeStudioFinaleNoCatch = FALSE;
                }
                ZeroEnemyPartyMons();
                CreateMon(boss, species, level, Random32(), OTID_STRUCT_RANDOM_NO_SHINY);
                for (u32 stat = 0; stat < NUM_STATS; stat++)
                {
                    u32 iv = MAX_PER_STAT_IVS;
                    SetMonData(boss, MON_DATA_HP_IV + stat, &iv);
                }
                CalculateMonStats(boss);
                ChooseMoves(boss, species);
            }

            void PokeStudio_FinaleCatchable(void)
            {
                gPokeStudioFinaleNoCatch = FALSE;
            }

            """.ReplaceLineEndings("\n");
    }
}
