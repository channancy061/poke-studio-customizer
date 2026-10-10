using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PokeStudio.Core.Models;
using PokeStudio.Core.SourceCode;

namespace PokeStudio.Modules.Expansion;

/// <summary>
/// src/data/shops.json (pokefirered-expansion): each shop has stages; the game sells the newest stage whose
/// unlock function passes. The file's spacing is mixed, so it is edited in place: only the changed arrays are
/// rewritten. Badge-unlocked stock becomes stages using small "has badge N" functions added to src/shop.c.
/// </summary>
internal static partial class ShopsJson
{
    public const string File = "src/data/shops.json";
    public const string ShopC = "src/shop.c";
    public const string SharedPrefix = "shared:";
    private const string BadgeFunc = "PokeStudio_HasBadge";
    private const string CodeStart = "// Poke Studio: badge checks for shop stages (generated; edit shops in the app)";
    private const string CodeEnd = "// Poke Studio: end";

    public sealed record Stage(string? UnlockFunc, string? Shared, IReadOnlyList<string> Items, int ItemsOpen, int ItemsClose);

    public sealed record Shop(string Id, IReadOnlyList<Stage> Stages, int StagesOpen, int StagesClose);

    public sealed record SharedList(string Label, IReadOnlyList<string> Items, int ItemsOpen, int ItemsClose);

    public sealed record Parsed(string Text, List<Shop> Shops, List<SharedList> Shared);

    /// <summary>Finds every array's position with a small scanner (values are plain strings, no escapes needed).</summary>
    public static Parsed? Parse(string text)
    {
        using var doc = JsonDocument.Parse(text);
        var shops = new List<Shop>();
        var shared = new List<SharedList>();
        var pos = 0;

        // shared lists, in order
        foreach (var s in doc.RootElement.TryGetProperty("shared_items", out var sh) ? sh.EnumerateArray() : default)
        {
            var label = s.GetProperty("label").GetString()!;
            var at = text.IndexOf($"\"{label}\"", pos, StringComparison.Ordinal);
            var (open, close) = ArrayAfter(text, "\"items\"", at);
            shared.Add(new SharedList(label, s.GetProperty("items").EnumerateArray().Select(x => x.GetString()!).ToList(), open, close));
            pos = close;
        }

        foreach (var s in doc.RootElement.GetProperty("shops").EnumerateArray())
        {
            var id = s.GetProperty("id").GetString()!;
            var at = text.IndexOf($"\"{id}\"", pos, StringComparison.Ordinal);
            var (stagesOpen, stagesClose) = ArrayAfter(text, "\"stages\"", at);
            var stages = new List<Stage>();
            var p = stagesOpen;
            foreach (var st in s.GetProperty("stages").EnumerateArray())
            {
                var func = st.TryGetProperty("isUnlockedFunc", out var f) ? f.GetString() : null;
                var sharedName = st.TryGetProperty("sharedItems", out var si) ? si.GetString() : null;
                if (st.TryGetProperty("items", out var items))
                {
                    var (open, close) = ArrayAfter(text, "\"items\"", p);
                    stages.Add(new Stage(func, sharedName, items.EnumerateArray().Select(x => x.GetString()!).ToList(), open, close));
                    p = close;
                }
                else
                {
                    stages.Add(new Stage(func, sharedName, [], -1, -1));
                }
            }

            shops.Add(new Shop(id, stages, stagesOpen, stagesClose));
            pos = stagesClose;
        }

        return new Parsed(text, shops, shared);
    }

    /// <summary>Position of the '[' after <paramref name="key"/> (searching from <paramref name="from"/>) and its matching ']'.</summary>
    private static (int Open, int Close) ArrayAfter(string text, string key, int from)
    {
        var k = text.IndexOf(key, from, StringComparison.Ordinal);
        var open = text.IndexOf('[', k);
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '"')
            {
                i = text.IndexOf('"', i + 1);
            }
            else if (text[i] == '[')
            {
                depth++;
            }
            else if (text[i] == ']' && --depth == 0)
            {
                return (open, i);
            }
        }

        throw new FormatException("Unbalanced brackets in shops.json.");
    }

    public static string Place(string shopId)
    {
        var name = shopId.Replace("SHOP_ID_", "", StringComparison.Ordinal);
        return GameCatalog.Prettify(name).Replace("Pokemon", "Pokémon", StringComparison.Ordinal);
    }

    public static IEnumerable<Mart> ToMarts(Parsed parsed)
    {
        foreach (var s in parsed.Shared)
        {
            var users = parsed.Shops.Count(sh => sh.Stages.Any(st => st.Shared == s.Label));
            yield return new Mart { Key = SharedPrefix + s.Label, Place = $"{GameCatalog.Prettify(s.Label)} (shared by {users} shop{(users == 1 ? "" : "s")})", Items = s.Items };
        }

        foreach (var shop in parsed.Shops)
        {
            var first = shop.Stages.FirstOrDefault();
            if (first is null)
            {
                continue;
            }

            if (first.Shared is { } sharedName)
            {
                yield return new Mart { Key = shop.Id, Place = Place(shop.Id), Items = parsed.Shared.FirstOrDefault(x => x.Label == sharedName)?.Items ?? [],
                    LockedReason = $"This shop uses the shared list “{GameCatalog.Prettify(sharedName)}”; edit that list instead." };
                continue;
            }

            var badgeStages = shop.Stages.Skip(1).ToList();
            var onlyBadges = badgeStages.All(st => st.UnlockFunc is { } f && f.StartsWith(BadgeFunc, StringComparison.Ordinal) && st.Shared is null);
            var tiers = new Dictionary<int, IReadOnlyList<string>>();
            if (onlyBadges)
            {
                var seen = first.Items.ToHashSet(StringComparer.Ordinal);
                foreach (var st in badgeStages)
                {
                    var badge = int.Parse(st.UnlockFunc![BadgeFunc.Length..], CultureInfo.InvariantCulture);
                    var added = st.Items.Where(i => !seen.Contains(i)).ToList();
                    seen.UnionWith(added);
                    tiers[badge] = added;
                }
            }

            yield return new Mart
            {
                Key = shop.Id,
                Place = Place(shop.Id),
                Items = first.Items,
                BadgeTiers = tiers,
                TiersLockedReason = onlyBadges ? null : "This shop already changes its stock during the story, so badge stock can't be added here.",
            };
        }
    }

    /// <summary>In-place edits for changed shops; whether src/shop.c needs the badge functions.</summary>
    public static (List<TextEdit> Edits, bool NeedsBadgeFunctions) EditsFor(Parsed parsed, IReadOnlyList<(Mart Before, Mart After)> changed)
    {
        var edits = new List<TextEdit>();
        var needsFuncs = false;
        var text = parsed.Text;
        var nl = CScanner.LineEnding(text);
        foreach (var (before, after) in changed)
        {
            if (after.Key.StartsWith(SharedPrefix, StringComparison.Ordinal))
            {
                var shared = parsed.Shared.First(s => SharedPrefix + s.Label == after.Key);
                if (!before.Items.SequenceEqual(after.Items))
                {
                    edits.Add(ArrayEdit(text, shared.ItemsOpen, shared.ItemsClose, after.Items, nl));
                }

                continue;
            }

            var shop = parsed.Shops.First(s => s.Id == after.Key);
            var tiersChanged = !SameTiers(before.BadgeTiers, after.BadgeTiers);
            if (!tiersChanged)
            {
                if (!before.Items.SequenceEqual(after.Items))
                {
                    edits.Add(ArrayEdit(text, shop.Stages[0].ItemsOpen, shop.Stages[0].ItemsClose, after.Items, nl));
                    // badge stages hold the full stock, so they follow the base list
                    var stock = after.Items.ToList();
                    foreach (var (badge, st) in after.BadgeTiers.OrderBy(kv => kv.Key).Zip(shop.Stages.Skip(1)))
                    {
                        stock = [.. stock, .. badge.Value.Where(i => !stock.Contains(i))];
                        edits.Add(ArrayEdit(text, st.ItemsOpen, st.ItemsClose, stock, nl));
                    }
                }

                continue;
            }

            // tiers changed: rewrite this shop's stages array
            needsFuncs |= after.BadgeTiers.Count > 0;
            var indent = IndentOf(text, shop.StagesOpen);
            var inner = indent + "    ";
            var sb = new StringBuilder("[");
            var stages = new List<(string? Func, IReadOnlyList<string> Items)> { (null, after.Items) };
            var full = after.Items.ToList();
            foreach (var (badge, items) in after.BadgeTiers.OrderBy(kv => kv.Key))
            {
                full = [.. full, .. items.Where(i => !full.Contains(i))];
                stages.Add(($"{BadgeFunc}{badge}", full.ToList()));
            }

            for (var i = 0; i < stages.Count; i++)
            {
                var (func, items) = stages[i];
                sb.Append(nl).Append(inner).Append('{');
                if (func is not null)
                {
                    sb.Append(nl).Append(inner).Append("    \"isUnlockedFunc\": \"").Append(func).Append("\",");
                }

                sb.Append(nl).Append(inner).Append("    \"items\": [");
                sb.Append(string.Join(",", items.Select(it => $"{nl}{inner}        \"{it}\"")));
                sb.Append(nl).Append(inner).Append("    ]").Append(nl).Append(inner).Append('}').Append(i < stages.Count - 1 ? "," : "");
            }

            sb.Append(nl).Append(indent).Append(']');
            edits.Add(new TextEdit(shop.StagesOpen, shop.StagesClose + 1 - shop.StagesOpen, sb.ToString()));
        }

        return (edits, needsFuncs);
    }

    private static TextEdit ArrayEdit(string text, int open, int close, IReadOnlyList<string> items, string nl)
    {
        // keep the indentation the first item had (tabs or spaces), and the closing bracket's own indentation
        var firstQuote = text.IndexOf('"', open);
        var itemIndent = firstQuote > 0 && firstQuote < close ? IndentOf(text, firstQuote) : IndentOf(text, open) + "    ";
        var closeIndent = IndentOf(text, close);
        var body = items.Count == 0 ? "" : string.Join(",", items.Select(i => $"{nl}{itemIndent}\"{i}\"")) + nl + closeIndent;
        return new TextEdit(open + 1, close - open - 1, body);
    }

    private static string IndentOf(string text, int pos)
    {
        var lineStart = text.LastIndexOf('\n', Math.Max(0, pos - 1)) + 1;
        var i = lineStart;
        while (i < text.Length && text[i] is ' ' or '\t')
        {
            i++;
        }

        return text[lineStart..i];
    }

    private static bool SameTiers(IReadOnlyDictionary<int, IReadOnlyList<string>> a, IReadOnlyDictionary<int, IReadOnlyList<string>> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v.SequenceEqual(kv.Value));

    /// <summary>Adds the eight badge checks to src/shop.c (before the generated shop data is included), once.</summary>
    public static TextEdit? BadgeFunctionsEdit(string shopC)
    {
        if (shopC.Contains(CodeStart, StringComparison.Ordinal))
        {
            return null;
        }

        var include = Regex.Match(shopC, @"^#include ""data/shops\.h""", RegexOptions.Multiline);
        if (!include.Success)
        {
            return null;
        }

        var nl = CScanner.LineEnding(shopC);
        var sb = new StringBuilder(CodeStart).Append(nl);
        for (var b = 1; b <= 8; b++)
        {
            sb.Append($"static bool32 {BadgeFunc}{b}(void) {{ return FlagGet(FLAG_BADGE0{b}_GET); }}").Append(nl);
        }

        sb.Append(CodeEnd).Append(nl).Append(nl);
        return new TextEdit(include.Index, 0, sb.ToString());
    }
}
