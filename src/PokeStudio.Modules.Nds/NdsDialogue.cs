using System.Globalization;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;
using PokeStudio.Core.Validation;

namespace PokeStudio.Modules.Nds;

/// <summary>
/// Dialogue in a DS game: every message file except the name lists the other editors own. Text uses the app's
/// spellings: \n new line, \r scroll to the next line, \f new box; commands (insert a name, a number…) read as
/// {CMD 0100 0000} and must stay as they are.
/// </summary>
internal static class NdsDialogue
{
    public static string Archive(NdsGame game) => game.StoryText ?? game.Text;

    private static HashSet<int> NameFiles(NdsGame game, NdsTables t) =>
        Archive(game) != game.Text
            ? []
            : [.. new[] { t.SpeciesNames, t.MoveNames, t.MoveNamesUpper, t.AbilityNames, t.AbilityNamesUpper, t.ItemNames, t.ItemNamesUpper, t.TrainerNames, t.ClassNames }.Where(i => i >= 0)];

    private static string Key(int file, int line) => string.Create(CultureInfo.InvariantCulture, $"MSG_{file:D4}_{line:D3}");

    private static (int File, int Line)? Parse(string key) =>
        key.StartsWith("MSG_", StringComparison.Ordinal) && key.Length == 12
        && int.TryParse(key.AsSpan(4, 4), CultureInfo.InvariantCulture, out var f) && int.TryParse(key.AsSpan(9, 3), CultureInfo.InvariantCulture, out var l)
            ? (f, l)
            : null;

    private static List<string>? ReadFile(NdsGame game, byte[] raw, out ushort seed)
    {
        seed = 0;
        try
        {
            return game.Generation == 4 ? Gen4Text.ReadFile(raw, out seed) : Gen5Text.ReadFile(raw);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    public static string ToDisplay(NdsGame game, string raw)
    {
        var s = raw.Replace("\n", "\\n", StringComparison.Ordinal);
        return game.Generation == 4
            ? s.Replace("{25BC}", "\\r", StringComparison.Ordinal).Replace("{25BD}", "\\f", StringComparison.Ordinal)
            : s.Replace("{CMD BE01}", "\\r", StringComparison.Ordinal).Replace("{CMD BE00}", "\\f", StringComparison.Ordinal);
    }

    public static string FromDisplay(NdsGame game, string text)
    {
        var s = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\\n", "\n", StringComparison.Ordinal);
        return game.Generation == 4
            ? s.Replace("\\r", "{25BC}", StringComparison.Ordinal).Replace("\\f", "{25BD}", StringComparison.Ordinal)
            : s.Replace("\\r", "{CMD BE01}", StringComparison.Ordinal).Replace("\\f", "{CMD BE00}", StringComparison.Ordinal);
    }

    public static void Load(NdsState state, GameData data)
    {
        var narc = new Narc(state.Rom.Read(Archive(state.Game)));
        var skip = NameFiles(state.Game, state.Tables);
        for (var f = 0; f < narc.Members.Count; f++)
        {
            if (skip.Contains(f) || ReadFile(state.Game, narc.Members[f], out _) is not { } lines)
            {
                continue;
            }

            for (var l = 0; l < lines.Count && l < 1000; l++)
            {
                if (lines[l].Length == 0)
                {
                    continue;
                }

                var text = ToDisplay(state.Game, lines[l]);
                var entry = new TextEntry { Key = Key(f, l), Place = $"Text file {f}", Text = text, Original = text };
                data.Texts.Add(entry);
                state.Texts[entry.Key] = entry;
            }
        }
    }

    /// <summary>Writes the edited messages back into their files (one archive rebuild for all of them).</summary>
    public static void Save(NdsState state, GameData data)
    {
        var changed = data.Texts
            .Where(t => state.Texts.TryGetValue(t.Key, out var before) && before.Text != t.Text && Parse(t.Key) is not null)
            .GroupBy(t => Parse(t.Key)!.Value.File)
            .ToList();
        if (changed.Count == 0)
        {
            return;
        }

        var archive = Archive(state.Game);
        var narc = new Narc(state.Rom.Read(archive));
        foreach (var group in changed)
        {
            var raw = narc.Members[group.Key];
            if (ReadFile(state.Game, raw, out var seed) is not { } lines)
            {
                continue;
            }

            foreach (var t in group)
            {
                lines[Parse(t.Key)!.Value.Line] = FromDisplay(state.Game, t.Text);
            }

            narc.Members[group.Key] = state.Game.Generation == 4 ? Gen4Text.WriteFile(lines, seed) : Gen5Text.WriteFile(lines, raw);
        }

        state.Rom.Write(archive, narc.Build());
    }

    public static IEnumerable<ValidationIssue> Check(NdsState state, GameData data)
    {
        foreach (var t in data.Texts)
        {
            if (!state.Texts.TryGetValue(t.Key, out var before) || before.Text == t.Text)
            {
                continue;
            }

            if (state.Game.Generation == 4 && !Gen4Text.CanWrite(FromDisplay(state.Game, t.Text)))
            {
                yield return new ValidationIssue(IssueSeverity.Error, "Text", t.Place, "This text has a character the game's font doesn't have.",
                    "Use plain letters, numbers and common punctuation.") { TargetKey = t.Key };
            }

            var codes = before.Text.Split('{').Skip(1).Select(p => p.Split('}')[0]).Where(c => c.StartsWith("CMD", StringComparison.Ordinal)).Order().ToList();
            var now = t.Text.Split('{').Skip(1).Select(p => p.Split('}')[0]).Where(c => c.StartsWith("CMD", StringComparison.Ordinal)).Order().ToList();
            if (!codes.SequenceEqual(now))
            {
                yield return new ValidationIssue(IssueSeverity.Warning, "Text", t.Place, "The {CMD …} codes changed. They insert names and numbers; the game may show the wrong thing.",
                    "Keep every {CMD …} code from the original text.") { TargetKey = t.Key };
            }
        }
    }
}
