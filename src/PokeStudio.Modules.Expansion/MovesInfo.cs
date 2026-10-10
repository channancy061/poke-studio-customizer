using System.Globalization;
using System.Text.RegularExpressions;
using PokeStudio.Core.Models;
using PokeStudio.Core.SourceCode;

namespace PokeStudio.Modules.Expansion;

/// <summary>Reads moves from src/data/moves_info.h and turns edits back into minimal text changes.</summary>
internal static partial class MovesInfo
{
    public const string File = "src/data/moves_info.h";

    [GeneratedRegex(@"^-?\d+$")]
    private static partial Regex PlainNumber();

    public static Move ToMove(SourceFile file, SpeciesBlock block, int index)
    {
        var locked = new Dictionary<string, string>();
        var dependent = new Dictionary<string, string>();
        string? Value(string field, string modelField)
        {
            var f = block.Get(field);
            if (f is null)
            {
                return null;
            }

            if (f.SpansDirectives)
            {
                locked[modelField] = "This value is split across game settings in the source file, so it can only be edited there for now.";
                return null;
            }

            var resolved = SpeciesInfo.Resolve(SpeciesInfo.Raw(file, f), file.Macros, out var cond);
            if (cond is not null || block.IsSettingDependent(field))
            {
                dependent[modelField] = $"Depends on a battle setting ({cond ?? f.Condition ?? "#if"}). Edits change the value your settings use; the other stays as it was.";
            }

            return resolved;
        }

        int Int(string field, string modelField, int fallback)
        {
            var v = Value(field, modelField);
            if (v is null)
            {
                return fallback;
            }

            var n = file.Macros.TryEvaluate(v);
            if (n is null)
            {
                locked[modelField] = $"Set with “{v}”, which the app can't calculate yet.";
                return fallback;
            }

            if (!PlainNumber().IsMatch(v.Trim()))
            {
                dependent.TryAdd(modelField, $"Calculated from “{v.Trim()}”. Editing replaces it with a plain number.");
            }

            return (int)n.Value;
        }

        var name = Value("name", "Name") is { } n ? CStrings.DecodeLiterals(n) : GameCatalog.Prettify(block.Symbol);
        var description = "";
        if (Value("description", "Description") is { } d)
        {
            if (d.StartsWith("COMPOUND_STRING", StringComparison.Ordinal) || d.StartsWith("_(", StringComparison.Ordinal) || d.StartsWith('"'))
            {
                description = CStrings.DecodeLiterals(d);
            }
            else
            {
                locked["Description"] = $"This description is shared with other moves (“{d}”); edit it in the source file for now.";
            }
        }

        var category = Value("category", "Category") switch
        {
            "DAMAGE_CATEGORY_SPECIAL" => MoveCategory.Special,
            "DAMAGE_CATEGORY_STATUS" => MoveCategory.Status,
            _ => MoveCategory.Physical,
        };

        if (!block.Active)
        {
            locked["*"] = "This move is switched off by your game's settings.";
        }

        return new Move
        {
            Id = index,
            Key = block.Symbol,
            Name = name,
            Description = description,
            Type = Value("type", "Type") ?? "TYPE_NORMAL",
            Category = category,
            Power = Int("power", "Power", 0),
            Accuracy = Int("accuracy", "Accuracy", 0),
            Pp = Int("pp", "Pp", 0),
            Priority = Int("priority", "Priority", 0),
            Effect = Value("effect", "Effect") ?? "EFFECT_HIT",
            Target = Value("target", "Target") ?? "TARGET_SELECTED",
            MakesContact = Value("makesContact", "MakesContact") is { } c && c != "FALSE" && c != "0",
            LockedFields = locked,
            SettingDependentFields = dependent,
        };
    }

    public static List<TextEdit> EditsFor(SourceFile file, SpeciesBlock block, Move before, Move after)
    {
        var edits = new List<TextEdit>();
        var nl = CScanner.LineEnding(file.Text);
        void Set(string field, string value, string? removeWhen = null)
        {
            var f = block.Get(field);
            if (f is not null)
            {
                if (removeWhen is not null && value == removeWhen && !block.IsSettingDependent(field) && SpeciesInfo.Raw(file, f).IndexOf('?', StringComparison.Ordinal) < 0)
                {
                    edits.Add(RemoveLine(file.Text, f));
                    return;
                }

                var (s, e) = SpeciesInfo.ActiveSpan(file.Text, f.ValueStart, f.ValueEnd, file.Macros);
                edits.Add(new TextEdit(s, e - s, value));
                return;
            }

            if (removeWhen is not null && value == removeWhen)
            {
                return; // the default already applies
            }

            // add after the closest earlier field in the usual order
            var order = new[] { "name", "description", "effect", "power", "type", "accuracy", "pp", "target", "priority", "category", "makesContact" };
            var anchor = order.TakeWhile(o => o != field).Select(block.Get).LastOrDefault(x => x is not null) ?? block.Fields.LastOrDefault(x => x.Active);
            if (anchor is null)
            {
                return;
            }

            var indent = CScanner.IndentAt(file.Text, anchor.DesignatorStart);
            edits.Add(anchor.HasComma
                ? new TextEdit(anchor.SegmentEnd + 1, 0, $"{nl}{indent}.{field} = {value},")
                : new TextEdit(anchor.ValueEnd, 0, $",{nl}{indent}.{field} = {value},"));
        }

        static string I(int v) => v.ToString(CultureInfo.InvariantCulture);

        if (before.Name != after.Name)
        {
            Set("name", $"COMPOUND_STRING(\"{CStrings.Escape(after.Name)}\")");
        }

        if (before.Description != after.Description && block.Get("description") is { } desc)
        {
            var indent = CScanner.IndentAt(file.Text, desc.DesignatorStart) + "    ";
            edits.Add(new TextEdit(desc.ValueStart, desc.ValueEnd - desc.ValueStart, "COMPOUND_STRING(" + CStrings.EncodeLines(after.Description, indent, nl) + ")"));
        }

        if (before.Effect != after.Effect)
        {
            Set("effect", after.Effect);
        }

        if (before.Power != after.Power)
        {
            Set("power", I(after.Power));
        }

        if (before.Type != after.Type)
        {
            Set("type", after.Type);
        }

        if (before.Accuracy != after.Accuracy)
        {
            Set("accuracy", I(after.Accuracy));
        }

        if (before.Pp != after.Pp)
        {
            Set("pp", I(after.Pp));
        }

        if (before.Target != after.Target)
        {
            Set("target", after.Target);
        }

        if (before.Priority != after.Priority)
        {
            Set("priority", I(after.Priority));
        }

        if (before.Category != after.Category)
        {
            Set("category", after.Category switch
            {
                MoveCategory.Special => "DAMAGE_CATEGORY_SPECIAL",
                MoveCategory.Status => "DAMAGE_CATEGORY_STATUS",
                _ => "DAMAGE_CATEGORY_PHYSICAL",
            });
        }

        if (before.MakesContact != after.MakesContact)
        {
            Set("makesContact", after.MakesContact ? "TRUE" : "FALSE", removeWhen: "FALSE");
        }

        return edits;
    }

    /// <summary>Removes a whole ".field = value," line.</summary>
    private static TextEdit RemoveLine(string text, FieldOccurrence f)
    {
        var lineStart = text.LastIndexOf('\n', Math.Max(0, f.DesignatorStart - 1)) + 1;
        var end = f.HasComma ? f.SegmentEnd + 1 : f.ValueEnd;
        if (end < text.Length && text[end] == '\r')
        {
            end++;
        }

        if (end < text.Length && text[end] == '\n')
        {
            end++;
        }

        return new TextEdit(lineStart, end - lineStart, "");
    }

    /// <summary>Plain-English names for move targets.</summary>
    public static readonly (string Key, string Name)[] Targets =
    [
        ("TARGET_SELECTED", "One chosen Pokémon"),
        ("TARGET_SMART", "One chosen Pokémon (redirects to its partner)"),
        ("TARGET_DEPENDS", "Depends on the move"),
        ("TARGET_OPPONENT", "One opponent"),
        ("TARGET_RANDOM", "A random opponent"),
        ("TARGET_BOTH", "Both opponents"),
        ("TARGET_USER", "The user"),
        ("TARGET_ALLY", "The user's partner"),
        ("TARGET_USER_AND_ALLY", "The user and its partner"),
        ("TARGET_USER_OR_ALLY", "The user or its partner"),
        ("TARGET_FOES_AND_ALLY", "Everyone except the user"),
        ("TARGET_FIELD", "The whole field"),
        ("TARGET_OPPONENTS_FIELD", "The opponents' side"),
        ("TARGET_ALL_BATTLERS", "Every Pokémon"),
    ];
}
