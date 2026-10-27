using System.Text;
using System.Text.RegularExpressions;
using PokeStudio.Core.SourceCode;

namespace PokeStudio.Modules.Expansion;

/// <summary>
/// The player's Tools menu (<see cref="Core.GameOptions.GameOptionsSettings.PlayerTools"/>): a "TOOLS" line in the start
/// menu after the Pokédex, running a script with the useful parts of the expansion's debug menu. src/start_menu.c gets the
/// action, its line in the normal menu, its callback and its place among the callbacks that don't fade the screen; every
/// added line carries the mark, so turning it off takes them out.
/// </summary>
internal static class ToolsWriter
{
    public const string StartMenu = "src/start_menu.c";
    private const string Mark = "// Poke Studio: tools";

    public static string Patch(string text, bool on)
    {
        var nl = CScanner.LineEnding(text);
        text = Regex.Replace(text, $@"^[^\n]*{Regex.Escape(Mark)}\r?\n", "", RegexOptions.Multiline);
        if (!on)
        {
            return text;
        }

        string After(string t, string pattern, string lines)
        {
            var m = Regex.Match(t, pattern, RegexOptions.Multiline);
            return m.Success ? t.Insert(m.Index + m.Length, lines) : t;
        }

        string L(string line) => $"{line} {Mark}{nl}";
        text = After(text, @"^([ \t]*)MENU_ACTION_DEXNAV,[^\n]*\n", L("    MENU_ACTION_PS_TOOLS,"));
        text = After(text, @"^static bool8 StartMenuDexNavCallback\(void\);[^\n]*\n",
            L("static bool8 StartMenuPsToolsCallback(void);") + L("extern const u8 PokeStudio_EventScript_Tools[];"));
        text = After(text, @"^[ \t]*\[MENU_ACTION_DEXNAV\][^\n]*\n", L("    [MENU_ACTION_PS_TOOLS] = {COMPOUND_STRING(\"TOOLS\"), {.u8_void = StartMenuPsToolsCallback}},"));
        text = After(text, @"^[ \t]*if \(DN_FLAG_DEXNAV_GET != 0 && FlagGet\(DN_FLAG_DEXNAV_GET\)\)[^\n]*\n[^\n]*AddStartMenuAction\(MENU_ACTION_DEXNAV\);[^\n]*\n",
            L("    if (FlagGet(FLAG_SYS_POKEDEX_GET) == TRUE)") + L("        AddStartMenuAction(MENU_ACTION_PS_TOOLS);"));
        text = After(text, @"^([ \t]*)&& gMenuCallback != StartMenuSafariZoneRetireCallback[^\n]*\n", L("            && gMenuCallback != StartMenuPsToolsCallback"));
        // the callback at the end, after a marked line in place of a blank one (so taking it out leaves the file as it was)
        var fn = new StringBuilder();
        foreach (var line in new[]
        {
            "",
            "static bool8 StartMenuPsToolsCallback(void)",
            "{",
            "    RemoveExtraStartMenuWindows();",
            "    HideStartMenu();",
            "    ScriptContext_SetupScript(PokeStudio_EventScript_Tools);",
            "    return TRUE;",
            "}",
        })
        {
            fn.Append(L(line));
        }

        return text + (text.EndsWith('\n') ? "" : nl) + fn;
    }

    /// <summary>The menu's script (in the options script).</summary>
    public static string Script(bool hardRules, string nl)
    {
        var b = new StringBuilder();
        void L(string line) => b.Append(line).Append(nl);
        L("PokeStudio_EventScript_Tools::");
        L("\tlockall");
        var items = new[] { ("Fly", "Fly"), ("Pc", "Pokémon PC"), ("Heal", "Heal party"), ("Relearn", "Relearn moves"), ("Friendship", "Friendship"), ("Nature", "Hidden nature") };
        for (var i = 0; i < items.Length; i++)
        {
            L($"\tdynmultipush PokeStudio_Text_Tools{items[i].Item1}, {i}");
        }

        L($"\tdynmultistack 0, 0, FALSE, {items.Length}, FALSE, 0, NULL");
        L("\tswitch VAR_RESULT");
        L("\tcase 0, PokeStudio_EventScript_ToolsFly");
        L("\tcase 1, PokeStudio_EventScript_ToolsPc");
        L("\tcase 2, PokeStudio_EventScript_ToolsHeal");
        L("\tcase 3, PokeStudio_EventScript_ToolsRelearn");
        L("\tcase 4, Debug_EventScript_SetFriendship");
        L("\tcase 5, Debug_EventScript_SetHiddenNature");
        L("\treleaseall");
        L("\tend");
        L("");
        L("PokeStudio_EventScript_ToolsFly:");
        L("\tfadescreen FADE_TO_BLACK");
        L("\tcallnative PokeStudio_ToolsFly");
        L("\treleaseall");
        L("\tend");
        L("");
        L("PokeStudio_EventScript_ToolsPc:");
        if (hardRules)
        {
            L("\tcallnative PokeStudio_ToolsBlocked");
            L("\tgoto_if_eq VAR_RESULT, TRUE, PokeStudio_EventScript_ToolsBlocked");
        }

        L("\tplayse SE_PC_LOGIN");
        L("\tspecial ShowPokemonStorageSystemPC");
        L("\twaitstate");
        L("\treleaseall");
        L("\tend");
        L("");
        L("PokeStudio_EventScript_ToolsHeal:");
        if (hardRules)
        {
            L("\tcallnative PokeStudio_ToolsBlocked");
            L("\tgoto_if_eq VAR_RESULT, TRUE, PokeStudio_EventScript_ToolsBlocked");
        }

        L("\tspecial HealPlayerParty");
        L("\tplayfanfare MUS_HEAL");
        L("\twaitfanfare");
        L("\tmsgbox PokeStudio_Text_ToolsHealed, MSGBOX_DEFAULT");
        L("\treleaseall");
        L("\tend");
        L("");
        L("PokeStudio_EventScript_ToolsRelearn:");
        L("\tmessage MoveRelearner_Text_WouldLearnNewMoves");
        L("\twaitmessage");
        L("\tgoto Common_EventScript_MoveRelearnerDynMultiChoice");
        L("");
        L("PokeStudio_EventScript_ToolsBlocked:");
        L("\tmsgbox PokeStudio_Text_ToolsBlocked, MSGBOX_DEFAULT");
        L("\treleaseall");
        L("\tend");
        L("");
        foreach (var (id, text) in items)
        {
            L($"PokeStudio_Text_Tools{id}:");
            L($"\t.string \"{text}$\"");
        }

        L("PokeStudio_Text_ToolsHealed:");
        L("\t.string \"Your Pokémon are fully healed.$\"");
        L("PokeStudio_Text_ToolsBlocked:");
        L("\t.string \"Not while trainers here are still\n\"");
        L("\t.string \"waiting to battle you!$\"");
        L("");
        return b.ToString();
    }
}
