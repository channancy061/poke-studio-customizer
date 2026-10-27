using System.Globalization;
using System.Text.RegularExpressions;
using PokeStudio.Core.GameOptions;

namespace PokeStudio.Modules.Expansion;

/// <summary>
/// Professor Oak's aides (HM05 Flash on Route 2, the Everstone, Itemfinder, Amulet Coin and Exp. Share) give their item
/// once the player has caught enough kinds of Pokémon. The originals count only the 151 Kanto Pokémon, which a game with
/// every generation in the wild barely has; with <see cref="GameOptionsSettings.AidesCountEveryPokemon"/> they count
/// every Pokémon, and each aide's number can be set. Lines carry the mark, so turning it off puts them back.
/// </summary>
internal static class AideWriter
{
    private const string Mark = "@ Poke Studio: aide";

    /// <summary>Each aide's map (Emerald's copies of Kanto end in _Frlg) and what it gives.</summary>
    public static readonly (string Map, string Gives)[] Aides =
    [
        ("Route2_EastBuilding", "HM05 Flash"),
        ("Route10_PokemonCenter_1F", "Everstone"),
        ("Route11_EastEntrance_2F", "Itemfinder"),
        ("Route16_NorthEntrance_2F", "Amulet Coin"),
        ("Route15_WestEntrance_2F", "Exp. Share"),
    ];

    public static IEnumerable<string> Files(string root) =>
        Aides.SelectMany(a => new[] { a.Map, a.Map + "_Frlg" })
            .Select(m => $"data/maps/{m}/scripts.inc")
            .Where(f => File.Exists(Path.Combine(root, f)));

    /// <summary>How many the aide on this map asks for (0 = the game's own number).</summary>
    public static int CountFor(GameOptionsSettings o, string file) =>
        Array.FindIndex(Aides, a => file.Contains($"/{a.Map}/", StringComparison.Ordinal) || file.Contains($"/{a.Map}_Frlg/", StringComparison.Ordinal)) is var i and >= 0
        && i < o.AideCounts.Count ? o.AideCounts[i] : 0;

    public static string Apply(string text, bool everyPokemon, int count)
    {
        text = Clean(text);
        if (everyPokemon)
        {
            text = Regex.Replace(text, @"^(\tsetvar VAR_0x8004, )0(\r?\n\tspecialvar VAR_RESULT, Get\w*PokedexCount)", $"${{1}}1 {Mark}s count every Pokémon$2", RegexOptions.Multiline);
        }

        if (count > 0)
        {
            text = Regex.Replace(text, @"^(\.equ REQUIRED_\w+_MONS, )(\d+)[ \t]*$", m =>
                $"{m.Groups[1].Value}{count.ToString(CultureInfo.InvariantCulture)} {Mark}'s number (was {m.Groups[2].Value})", RegexOptions.Multiline);
        }

        return text;
    }

    private static string Clean(string text)
    {
        text = Regex.Replace(text, $@"^(\tsetvar VAR_0x8004, )1 {Regex.Escape(Mark)}s count every Pokémon", "${1}0", RegexOptions.Multiline);
        return Regex.Replace(text, $@"^(\.equ REQUIRED_\w+_MONS, )\d+ {Regex.Escape(Mark)}'s number \(was (\d+)\)", "${1}$2", RegexOptions.Multiline);
    }
}
