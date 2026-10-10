using System.Globalization;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;
using PokeStudio.Core.Validation;

namespace PokeStudio.Modules.N3ds;

/// <summary>
/// The English story dialogue of a 3DS game. Text uses the app's spellings: \n new line, \r scroll to the next line,
/// \f new box; other commands (insert a name, a number…) read as {CMD 0100 …} and must stay as they are.
/// </summary>
internal static class N3dsDialogue
{
    private static string Key(int file, int line) => string.Create(CultureInfo.InvariantCulture, $"MSG_{file:D4}_{line:D3}");

    private static (int File, int Line)? Parse(string key) =>
        key.StartsWith("MSG_", StringComparison.Ordinal) && key.Length == 12
        && int.TryParse(key.AsSpan(4, 4), CultureInfo.InvariantCulture, out var f) && int.TryParse(key.AsSpan(9, 3), CultureInfo.InvariantCulture, out var l)
            ? (f, l)
            : null;

    public static string ToDisplay(string raw) =>
        raw.Replace("{000A}", "\\n", StringComparison.Ordinal).Replace("{CMD BE01}", "\\r", StringComparison.Ordinal).Replace("{CMD BE00}", "\\f", StringComparison.Ordinal);

    public static string FromDisplay(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "{000A}", StringComparison.Ordinal).Replace("\\n", "{000A}", StringComparison.Ordinal)
            .Replace("\\r", "{CMD BE01}", StringComparison.Ordinal).Replace("\\f", "{CMD BE00}", StringComparison.Ordinal);

    private static List<string>? ReadFile(byte[] raw)
    {
        try
        {
            return Gen6Text.Read(raw);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    public static void Load(N3dsModule.State state, GameData data)
    {
        if (state.Tables.StoryText is not { } path)
        {
            return;
        }

        var garc = new Garc(state.File(path));
        for (var f = 0; f < garc.Entries.Count; f++)
        {
            if (ReadFile(garc.Entries[f].Files[0]) is not { } lines)
            {
                continue;
            }

            for (var l = 0; l < lines.Count && l < 1000; l++)
            {
                if (lines[l].Length == 0)
                {
                    continue;
                }

                var text = ToDisplay(lines[l]);
                var entry = new TextEntry { Key = Key(f, l), Place = $"Text file {f}", Text = text, Original = text };
                data.Texts.Add(entry);
                state.Texts[entry.Key] = entry;
            }
        }
    }

    /// <summary>Adds the rebuilt story archive to <paramref name="changed"/> when any message was edited.</summary>
    public static void Save(N3dsModule.State state, GameData data, Dictionary<string, byte[]> changed)
    {
        if (state.Tables.StoryText is not { } path)
        {
            return;
        }

        var edits = data.Texts
            .Where(t => state.Texts.TryGetValue(t.Key, out var before) && before.Text != t.Text && Parse(t.Key) is not null)
            .GroupBy(t => Parse(t.Key)!.Value.File)
            .ToList();
        if (edits.Count == 0)
        {
            return;
        }

        var garc = new Garc(state.File(path));
        foreach (var group in edits)
        {
            var raw = garc.Entries[group.Key].Files[0];
            if (ReadFile(raw) is not { } lines)
            {
                continue;
            }

            foreach (var t in group)
            {
                lines[Parse(t.Key)!.Value.Line] = FromDisplay(t.Text);
            }

            garc.Entries[group.Key].Files[0] = Gen6Text.Write(lines, raw);
        }

        changed[path] = garc.Build();
    }

    public static IEnumerable<ValidationIssue> Check(N3dsModule.State state, GameData data)
    {
        static List<string> Commands(string text) =>
            [.. text.Split('{').Skip(1).Select(p => p.Split('}')[0]).Where(c => c.StartsWith("CMD", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];

        foreach (var t in data.Texts)
        {
            if (state.Texts.TryGetValue(t.Key, out var before) && before.Text != t.Text && !Commands(before.Text).SequenceEqual(Commands(t.Text)))
            {
                yield return new ValidationIssue(IssueSeverity.Warning, "Text", t.Place, "The {CMD …} codes changed. They insert names and numbers; the game may show the wrong thing.",
                    "Keep every {CMD …} code from the original text.") { TargetKey = t.Key };
            }
        }
    }
}
