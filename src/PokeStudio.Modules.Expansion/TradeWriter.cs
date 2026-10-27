using System.Text.RegularExpressions;
using PokeStudio.Core.Modules;
using PokeStudio.Core.Progression;

namespace PokeStudio.Modules.Expansion;

/// <summary>
/// In-game trades ask for a Pokémon the player can catch before the trader (see <see cref="TradePlanner"/>). The trade
/// table is pokefirered's src/data/ingame_trades.h or pokeemerald's src/data/trade.h; the trader's place is the map whose
/// script names the trade. A changed line keeps the original in its comment, so turning it off puts it back. The traders'
/// words name the Pokémon through the game's buffers, so they follow.
/// </summary>
internal static partial class TradeWriter
{
    private const string Mark = "// Poke Studio: trade (was ";
    public static readonly string[] Tables = ["src/data/ingame_trades.h", "src/data/trade.h"];

    public static IEnumerable<string> Files(string root) => Tables.Where(f => File.Exists(Path.Combine(root, f)));

    /// <summary>Trade id → the Pokémon to ask for instead, from the trades in the project and the planned wild Pokémon.</summary>
    public static IReadOnlyDictionary<string, string> Requests(ExpansionLayout layout, GameData data)
    {
        if (Files(layout.Root).FirstOrDefault() is not { } file)
        {
            return new Dictionary<string, string>();
        }

        var table = Clean(File.ReadAllText(layout.Full(file)));
        var places = Places(layout.Full("data/maps"));
        var trades = Entries().Matches(table)
            .Select(m => (Id: m.Groups[1].Value, Body: m.Groups[2].Value))
            .Select(e => new InGameTrade(
                e.Id,
                places.GetValueOrDefault(e.Id, ""),
                Requested().Match(e.Body).Groups[2].Value,
                Regex.Match(e.Body, @"\.species = (SPECIES_\w+)").Groups[1].Value))
            .Where(t => t.Requested.Length > 0 && t.Place.Length > 0)
            .ToList();
        return TradePlanner.Requests(data, trades);
    }

    public static string Apply(string text, IReadOnlyDictionary<string, string> requests, bool on)
    {
        text = Clean(text);
        if (!on || requests.Count == 0)
        {
            return text;
        }

        // every requested line of the entry (some have one per game version)
        return Entries().Replace(text, m => requests.TryGetValue(m.Groups[1].Value, out var ask)
            ? Requested().Replace(m.Value, r => $"{r.Groups[1].Value}{ask}{r.Groups[3].Value} {Mark}{r.Groups[2].Value})")
            : m.Value);
    }

    private static string Clean(string text) =>
        Regex.Replace(text, $@"(\.requestedSpecies = )SPECIES_\w+(,?) {Regex.Escape(Mark)}(SPECIES_\w+)\)", "$1$3$2");

    /// <summary>Trade id → "Route 2 House" (the first map whose script names it).</summary>
    private static Dictionary<string, string> Places(string mapsDir)
    {
        var places = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(mapsDir))
        {
            return places;
        }

        foreach (var dir in Directory.GetDirectories(mapsDir).Order(StringComparer.Ordinal))
        {
            var script = Path.Combine(dir, "scripts.inc");
            var json = Path.Combine(dir, "map.json");
            if (!File.Exists(script) || !File.Exists(json))
            {
                continue;
            }

            var id = Regex.Match(File.ReadAllText(json), "\"id\"\\s*:\\s*\"(\\w+)\"");
            foreach (Match m in Regex.Matches(File.ReadAllText(script), @"\b(INGAME_TRADE_\w+)"))
            {
                if (id.Success)
                {
                    places.TryAdd(m.Groups[1].Value, Core.Progression.StoryOrder.AreaName(Regex.Replace(id.Groups[1].Value, "_FRLG$", "")));
                }
            }
        }

        return places;
    }

    [GeneratedRegex(@"\[(INGAME_TRADE_\w+)\]\s*=\s*\{(.*?)\n\s*\},", RegexOptions.Singleline)]
    private static partial Regex Entries();

    [GeneratedRegex(@"(\.requestedSpecies = )(SPECIES_\w+)(,?)")]
    private static partial Regex Requested();
}
