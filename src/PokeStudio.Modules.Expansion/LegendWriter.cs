using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PokeStudio.Core.Modules;
using PokeStudio.Core.SourceCode;
using PokeStudio.Core.Story;

namespace PokeStudio.Modules.Expansion;

/// <summary>
/// Legendary Pokémon on the maps (see <see cref="Legends"/>): a Pokémon-shaped person per legend, a script that starts
/// a legendary battle, and a hiding flag the new game sets and the revealing trainer's first win clears.
/// </summary>
internal static class LegendWriter
{
    private const string Mark = "Poke Studio legend";

    /// <summary>The legends that can be written: their Pokémon exists, and their revealer (if any) is a story battle.</summary>
    public static IReadOnlyList<StoryLegend> Active(GameData data, IReadOnlyList<StoryBattle> battles) =>
        (data.Story?.Legends ?? [])
            .Where(l => data.Species.Any(s => s.Key == l.Species) && (l.RevealedBy is null || battles.Any(b => b.TrainerKey == l.RevealedBy && b.OnWin is null)))
            .ToList();

    public static IEnumerable<string> Maps(GameData data) => (data.Story?.Legends ?? []).Select(l => l.Map);

    public static string ScriptOf(StoryLegend l) => StoryWriter.ScriptPrefix + "Legend_" + l.Id;

    public static StoryWriter.MapPerson PersonOf(StoryLegend l) =>
        new(ScriptOf(l), $"OBJ_EVENT_GFX_SPECIES({l.Id})", l.X, l.Y, l.Elevation, Facing.Down, 0, Legends.FlagOf(l));

    /// <summary>new_game.inc with a "hidden" line per legend that waits for its revealer (earlier lines are replaced).</summary>
    public static string NewGame(string text, IEnumerable<StoryLegend> legends)
    {
        var clean = Regex.Replace(text, $@"^[^\n]*@ {Mark}[^\n]*\r?\n", "", RegexOptions.Multiline);
        var at = clean.IndexOf("EventScript_ResetAllMapFlags::", StringComparison.Ordinal);
        if (at < 0)
        {
            return clean;
        }

        var lineEnd = clean.IndexOf('\n', at) + 1;
        return clean.Insert(lineEnd, string.Concat(legends.Where(l => l.RevealedBy is not null).Select(l => $"\tsetflag {Legends.FlagOf(l)} @ {Mark}\n")));
    }

    /// <summary>
    /// The battle script: the cry, then a legendary battle. The legend is hidden before the battle; if it isn't caught it
    /// comes back the next time the player enters the map.
    /// </summary>
    public static void Scripts(StringBuilder s, IReadOnlyList<StoryLegend> legends, Func<string, string> name, bool waitAfterLegendary)
    {
        void L(string line) => s.Append(line).Append('\n');
        foreach (var l in legends)
        {
            var label = ScriptOf(l);
            var flag = Legends.FlagOf(l);
            L("");
            L($"{label}::");
            L("\tlock");
            L("\tfaceplayer");
            L("\twaitse");
            L($"\tplaymoncry {l.Species}, CRY_MODE_ENCOUNTER");
            L("\tdelay 40");
            L("\twaitmoncry");
            L($"\tsetwildbattle {l.Species}, {l.Level.ToString(CultureInfo.InvariantCulture)}");
            L($"\tsetflag {flag}");
            L("\tspecial BattleSetup_StartLegendaryBattle");
            if (waitAfterLegendary)
            {
                L("\twaitstate");
            }

            L("\tspecialvar VAR_RESULT, GetBattleOutcome");

            // the flag hides it only when the map loads again, so it's taken off the map now (or it could be fought again
            // straight away); removeobject sets the flag, so a Pokémon that got away has it cleared after
            L("\tfadescreen FADE_TO_BLACK");
            L("\tremoveobject VAR_LAST_TALKED");
            L("\tfadescreen FADE_FROM_BLACK");
            L($"\tgoto_if_eq VAR_RESULT, B_OUTCOME_CAUGHT, {label}_Caught");
            L($"\tclearflag {flag}");
            L($"\tmsgbox {label}_Away, MSGBOX_DEFAULT");
            L($"{label}_Caught:");
            L("\trelease");
            L("\tend");
            s.Append('\n').Append(label).Append("_Away:\n")
                .Append(TextScripts.Format($"{name(l.Species)} went away…\\pMaybe it will come back here.", "\t", true, "\n")).Append('\n');
        }
    }
}
