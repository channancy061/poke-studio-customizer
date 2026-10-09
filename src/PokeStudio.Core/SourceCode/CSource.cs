using System.Globalization;
using System.Text;

namespace PokeStudio.Core.SourceCode;

/// <summary>A replacement of [Start, Start+Length) with NewText in an original text.</summary>
public sealed record TextEdit(int Start, int Length, string NewText);

public static class TextEdits
{
    /// <summary>Applies non-overlapping edits; everything outside them stays byte-for-byte identical.</summary>
    public static string Apply(string text, IEnumerable<TextEdit> edits)
    {
        var ordered = edits.OrderBy(e => e.Start).ToList();
        var sb = new StringBuilder(text.Length + 256);
        var pos = 0;
        foreach (var e in ordered)
        {
            if (e.Start < pos)
            {
                throw new InvalidOperationException("Two edits overlap; this is a bug in the module.");
            }

            sb.Append(text, pos, e.Start - pos).Append(e.NewText);
            pos = e.Start + e.Length;
        }

        sb.Append(text, pos, text.Length - pos);
        return sb.ToString();
    }
}

/// <summary>Lexical helpers for C source that respect strings, character literals and comments.</summary>
public static class CScanner
{
    /// <summary>Index just past a comment or literal starting at <paramref name="i"/>, or <paramref name="i"/> if none starts there.</summary>
    public static int SkipTrivia(string s, int i)
    {
        if (i + 1 < s.Length && s[i] == '/' && s[i + 1] == '/')
        {
            var nl = s.IndexOf('\n', i);
            return nl < 0 ? s.Length : nl;
        }

        if (i + 1 < s.Length && s[i] == '/' && s[i + 1] == '*')
        {
            var end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
            return end < 0 ? s.Length : end + 2;
        }

        if (s[i] is '"' or '\'')
        {
            var quote = s[i];
            var j = i + 1;
            while (j < s.Length && s[j] != quote)
            {
                j += s[j] == '\\' ? 2 : 1;
            }

            return Math.Min(j + 1, s.Length);
        }

        return i;
    }

    /// <summary>Position of the bracket matching the one at <paramref name="open"/>.</summary>
    public static int FindMatching(string s, int open)
    {
        var (o, c) = s[open] switch
        {
            '(' => ('(', ')'),
            '{' => ('{', '}'),
            '[' => ('[', ']'),
            _ => throw new ArgumentException("Not an opening bracket.", nameof(open)),
        };
        var depth = 0;
        for (var i = open; i < s.Length;)
        {
            var skipped = SkipTrivia(s, i);
            if (skipped != i)
            {
                i = skipped;
                continue;
            }

            if (s[i] == o)
            {
                depth++;
            }
            else if (s[i] == c && --depth == 0)
            {
                return i;
            }

            i++;
        }

        throw new FormatException($"Unbalanced '{o}' starting at offset {open}.");
    }

    /// <summary>Splits [start,end) at <paramref name="separator"/> characters that are not nested inside brackets, strings or comments.</summary>
    public static List<(int Start, int End)> SplitTopLevel(string s, int start, int end, char separator = ',')
    {
        var parts = new List<(int, int)>();
        var depth = 0;
        var partStart = start;
        for (var i = start; i < end;)
        {
            var skipped = SkipTrivia(s, i);
            if (skipped != i)
            {
                i = skipped;
                continue;
            }

            var ch = s[i];
            if (ch is '(' or '{' or '[')
            {
                depth++;
            }
            else if (ch is ')' or '}' or ']')
            {
                depth--;
            }
            else if (ch == separator && depth == 0)
            {
                parts.Add((partStart, i));
                partStart = i + 1;
            }

            i++;
        }

        if (end > partStart)
        {
            parts.Add((partStart, end));
        }

        return parts;
    }

    /// <summary>Removes comments (keeps strings) and trims.</summary>
    public static string StripComments(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length;)
        {
            if (s[i] == '/' && i + 1 < s.Length && (s[i + 1] == '/' || s[i + 1] == '*'))
            {
                i = SkipTrivia(s, i);
                sb.Append(' ');
                continue;
            }

            if (s[i] is '"' or '\'')
            {
                var j = SkipTrivia(s, i);
                sb.Append(s, i, j - i);
                i = j;
                continue;
            }

            sb.Append(s[i++]);
        }

        return sb.ToString().Trim();
    }

    public static string LineEnding(string text) => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    /// <summary>Leading whitespace of the line containing <paramref name="offset"/>.</summary>
    public static string IndentAt(string text, int offset)
    {
        var lineStart = text.LastIndexOf('\n', Math.Max(0, offset - 1)) + 1;
        var i = lineStart;
        while (i < text.Length && text[i] is ' ' or '\t')
        {
            i++;
        }

        return text[lineStart..i];
    }
}

/// <summary>C string literal decoding/encoding (for names and Pokédex text).</summary>
public static class CStrings
{
    /// <summary>Concatenates and unescapes every "…" literal in <paramref name="expr"/> (e.g. inside _("…") or COMPOUND_STRING("a\n" "b")).</summary>
    public static string DecodeLiterals(string expr)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < expr.Length; i++)
        {
            if (expr[i] == '/' && i + 1 < expr.Length && expr[i + 1] is '/' or '*')
            {
                i = CScanner.SkipTrivia(expr, i) - 1;
                continue;
            }

            if (expr[i] != '"')
            {
                continue;
            }

            for (i++; i < expr.Length && expr[i] != '"'; i++)
            {
                if (expr[i] == '\\' && i + 1 < expr.Length)
                {
                    i++;
                    sb.Append(expr[i] switch { 'n' => '\n', 't' => '\t', '"' => '"', '\\' => '\\', 'p' => '\n', 'l' => '\n', _ => expr[i] });
                }
                else
                {
                    sb.Append(expr[i]);
                }
            }
        }

        return sb.ToString();
    }

    public static string Escape(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    /// <summary>One literal per line, each line ending in \n except the last.</summary>
    public static string EncodeLines(string text, string indent, string newline)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var sb = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            sb.Append(newline).Append(indent).Append('"').Append(Escape(lines[i])).Append(i < lines.Length - 1 ? "\\n\"" : "\"");
        }

        return sb.ToString();
    }
}

/// <summary>
/// Object-like #define table plus a C preprocessor expression evaluator, enough to decide which
/// #if branches a project's settings select (e.g. "P_UPDATED_STATS >= GEN_6").
/// </summary>
public sealed class MacroTable
{
    private readonly Dictionary<string, string> _defines = new(StringComparer.Ordinal);

    public void Define(string name, string value) => _defines[name] = value;

    public MacroTable Clone()
    {
        var copy = new MacroTable();
        foreach (var (k, v) in _defines)
        {
            copy._defines[k] = v;
        }

        return copy;
    }

    /// <summary>Adds the #defines of <paramref name="text"/> that the preprocessor would keep under <paramref name="map"/>.</summary>
    public void LoadActiveDefines(string text, ConditionalMap map)
    {
        var pos = 0;
        while (pos < text.Length)
        {
            var nl = text.IndexOf('\n', pos);
            var end = nl < 0 ? text.Length : nl + 1;
            var line = text.AsSpan(pos, end - pos).TrimStart();
            if (line.StartsWith("#define", StringComparison.Ordinal) && map.IsActive(pos))
            {
                LoadDefines(line.ToString());
            }

            pos = end;
        }
    }

    public bool IsDefined(string name) => _defines.ContainsKey(name);

    public string? Raw(string name) => _defines.GetValueOrDefault(name);

    /// <summary>Reads simple "#define NAME value" lines (comments stripped, continuations joined). Function-like macros are skipped.</summary>
    public void LoadDefines(string text)
    {
        var joined = text.Replace("\\\r\n", " ", StringComparison.Ordinal).Replace("\\\n", " ", StringComparison.Ordinal);
        foreach (var raw in joined.Split('\n'))
        {
            var line = raw.TrimStart();
            if (!line.StartsWith("#define", StringComparison.Ordinal) && !line.StartsWith("# define", StringComparison.Ordinal))
            {
                continue;
            }

            var body = CScanner.StripComments(line[(line.IndexOf("define", StringComparison.Ordinal) + 6)..]);
            var nameEnd = 0;
            while (nameEnd < body.Length && (char.IsLetterOrDigit(body[nameEnd]) || body[nameEnd] == '_'))
            {
                nameEnd++;
            }

            if (nameEnd == 0 || (nameEnd < body.Length && body[nameEnd] == '('))
            {
                continue; // function-like macro
            }

            Define(body[..nameEnd], body[nameEnd..].Trim());
        }
    }

    public long Evaluate(string expression) => new Parser(this, CScanner.StripComments(expression)).ParseFull();

    public bool IsTrue(string expression)
    {
        try
        {
            return Evaluate(expression) != 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Tries to evaluate; returns null for anything that isn't a constant expression.</summary>
    public long? TryEvaluate(string expression)
    {
        try
        {
            return Evaluate(expression);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private sealed class Parser(MacroTable macros, string text, int depth = 0)
    {
        private int _pos;

        public long ParseFull()
        {
            if (depth > 32)
            {
                throw new FormatException("Macro expansion too deep.");
            }

            var v = Ternary();
            SkipWs();
            if (_pos != text.Length)
            {
                throw new FormatException($"Unexpected '{text[_pos..]}'.");
            }

            return v;
        }

        private long Ternary()
        {
            var cond = Binary(0);
            SkipWs();
            if (Peek('?'))
            {
                _pos++;
                var a = Ternary();
                Expect(':');
                var b = Ternary();
                return cond != 0 ? a : b;
            }

            return cond;
        }

        private static readonly string[][] Levels =
        [
            ["||"], ["&&"], ["|"], ["^"], ["&"], ["==", "!="], ["<=", ">=", "<", ">"], ["<<", ">>"], ["+", "-"], ["*", "/", "%"],
        ];

        private long Binary(int level)
        {
            if (level == Levels.Length)
            {
                return Unary();
            }

            var left = Binary(level + 1);
            while (true)
            {
                SkipWs();
                var op = Levels[level].FirstOrDefault(o => text.AsSpan(_pos).StartsWith(o, StringComparison.Ordinal)
                    && !(o is "|" or "&" && _pos + 1 < text.Length && text[_pos + 1] == o[0])
                    && !(o is "<" or ">" && _pos + 1 < text.Length && text[_pos + 1] is '=' or '<' or '>'));
                if (op is null)
                {
                    return left;
                }

                _pos += op.Length;
                var right = Binary(level + 1);
                left = op switch
                {
                    "||" => (left != 0 || right != 0) ? 1 : 0,
                    "&&" => (left != 0 && right != 0) ? 1 : 0,
                    "|" => left | right,
                    "^" => left ^ right,
                    "&" => left & right,
                    "==" => left == right ? 1 : 0,
                    "!=" => left != right ? 1 : 0,
                    "<=" => left <= right ? 1 : 0,
                    ">=" => left >= right ? 1 : 0,
                    "<" => left < right ? 1 : 0,
                    ">" => left > right ? 1 : 0,
                    "<<" => left << (int)right,
                    ">>" => left >> (int)right,
                    "+" => left + right,
                    "-" => left - right,
                    "*" => left * right,
                    "/" => right == 0 ? throw new FormatException("Division by zero.") : left / right,
                    _ => right == 0 ? throw new FormatException("Division by zero.") : left % right,
                };
            }
        }

        private long Unary()
        {
            SkipWs();
            if (Peek('!'))
            {
                _pos++;
                return Unary() == 0 ? 1 : 0;
            }

            if (Peek('-'))
            {
                _pos++;
                return -Unary();
            }

            if (Peek('+'))
            {
                _pos++;
                return Unary();
            }

            if (Peek('~'))
            {
                _pos++;
                return ~Unary();
            }

            return Primary();
        }

        private long Primary()
        {
            SkipWs();
            if (Peek('('))
            {
                _pos++;
                var v = Ternary();
                Expect(')');
                return v;
            }

            if (_pos < text.Length && char.IsDigit(text[_pos]))
            {
                var start = _pos;
                while (_pos < text.Length && (char.IsLetterOrDigit(text[_pos]) || text[_pos] == '.'))
                {
                    _pos++;
                }

                var lit = text[start.._pos].TrimEnd('u', 'U', 'l', 'L');
                if (lit.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    return long.Parse(lit[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                }

                if (lit.Contains('.', StringComparison.Ordinal))
                {
                    return (long)double.Parse(lit, CultureInfo.InvariantCulture);
                }

                return long.Parse(lit, CultureInfo.InvariantCulture);
            }

            if (_pos < text.Length && (char.IsLetter(text[_pos]) || text[_pos] == '_'))
            {
                var name = Identifier();
                if (name == "defined")
                {
                    SkipWs();
                    var paren = Peek('(');
                    if (paren)
                    {
                        _pos++;
                    }

                    SkipWs();
                    var target = Identifier();
                    if (paren)
                    {
                        Expect(')');
                    }

                    return macros.IsDefined(target) ? 1 : 0;
                }

                SkipWs();
                if (Peek('('))
                {
                    throw new FormatException($"Function-like macro {name} isn't supported.");
                }

                if (name is "TRUE")
                {
                    return 1;
                }

                if (name is "FALSE")
                {
                    return 0;
                }

                var raw = macros.Raw(name);
                if (raw is null)
                {
                    return 0; // C: unknown identifiers in #if are 0
                }

                return string.IsNullOrWhiteSpace(raw) ? 1 : new Parser(macros, raw, depth + 1).ParseFull();
            }

            throw new FormatException(_pos < text.Length ? $"Unexpected '{text[_pos]}'." : "Unexpected end of expression.");
        }

        private string Identifier()
        {
            var start = _pos;
            while (_pos < text.Length && (char.IsLetterOrDigit(text[_pos]) || text[_pos] == '_'))
            {
                _pos++;
            }

            if (start == _pos)
            {
                throw new FormatException("Expected a name.");
            }

            return text[start.._pos];
        }

        private void SkipWs()
        {
            while (_pos < text.Length && char.IsWhiteSpace(text[_pos]))
            {
                _pos++;
            }
        }

        private bool Peek(char c) => _pos < text.Length && text[_pos] == c;

        private void Expect(char c)
        {
            SkipWs();
            if (!Peek(c))
            {
                throw new FormatException($"Expected '{c}'.");
            }

            _pos++;
        }
    }
}

/// <summary>
/// Works out which parts of a file the preprocessor keeps, given a <see cref="MacroTable"/>.
/// Also remembers the condition text guarding each region so the UI can say "depends on P_UPDATED_STATS".
/// </summary>
public sealed class ConditionalMap
{
    private readonly List<(int Offset, bool Active, string? Condition)> _marks = [];

    public ConditionalMap(string text, MacroTable macros)
    {
        var stack = new Stack<(bool ParentActive, bool Taken, bool Active, string? Condition)>();
        var active = true;
        string? condition = null;
        _marks.Add((0, true, null));
        var pos = 0;
        while (pos < text.Length)
        {
            var nl = text.IndexOf('\n', pos);
            var lineEnd = nl < 0 ? text.Length : nl + 1;
            var line = text.AsSpan(pos, lineEnd - pos).TrimStart();
            if (line.Length > 0 && line[0] == '#')
            {
                var directive = line[1..].TrimStart().ToString();
                var word = new string(directive.TakeWhile(char.IsLetter).ToArray());
                var rest = CScanner.StripComments(directive[word.Length..]);
                switch (word)
                {
                    case "if":
                    case "ifdef":
                    case "ifndef":
                    {
                        var value = word switch
                        {
                            "ifdef" => macros.IsDefined(rest.Trim()),
                            "ifndef" => !macros.IsDefined(rest.Trim()),
                            _ => macros.IsTrue(rest),
                        };
                        stack.Push((active, value, active && value, condition));
                        active = active && value;
                        condition = word == "if" ? rest : $"{word} {rest}";
                        break;
                    }

                    case "elif":
                        if (stack.Count > 0)
                        {
                            var top = stack.Pop();
                            var value = !top.Taken && macros.IsTrue(rest);
                            stack.Push((top.ParentActive, top.Taken || value, top.ParentActive && value, top.Condition));
                            active = top.ParentActive && value;
                            condition = rest;
                        }

                        break;
                    case "else":
                        if (stack.Count > 0)
                        {
                            var top = stack.Pop();
                            stack.Push((top.ParentActive, true, top.ParentActive && !top.Taken, top.Condition));
                            active = top.ParentActive && !top.Taken;
                            condition = "not (" + (condition ?? "") + ")";
                        }

                        break;
                    case "endif":
                        if (stack.Count > 0)
                        {
                            var top = stack.Pop();
                            active = top.ParentActive;
                            condition = top.Condition;
                        }

                        break;
                }

                _marks.Add((lineEnd, active, stack.Count == 0 ? null : condition));
            }

            pos = lineEnd;
        }
    }

    /// <summary>True when the code at <paramref name="offset"/> is compiled with the current settings.</summary>
    public bool IsActive(int offset) => Find(offset).Active;

    /// <summary>Innermost #if condition around <paramref name="offset"/>, or null at top level.</summary>
    public string? ConditionAt(int offset) => Find(offset).Condition;

    /// <summary>True when any preprocessor directive sits strictly between the two offsets.</summary>
    public bool HasDirectiveBetween(int start, int end) => _marks.Any(m => m.Offset > start && m.Offset <= end);

    private (int Offset, bool Active, string? Condition) Find(int offset)
    {
        int lo = 0, hi = _marks.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (_marks[mid].Offset <= offset)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return _marks[lo];
    }
}
