using PokeStudio.Core.Models;
using PokeStudio.Core.SourceCode;
using PokeStudio.Modules.Expansion;

namespace PokeStudio.Core.Tests;

/// <summary>pokefirered-expansion's shops.json, with its mixed tab/space layout (invented content).</summary>
public class ShopsJsonTests
{
    private const string Json =
        "{\n" +
        "    \"shared_items\": [\n" +
        "        {\n            \"label\": \"Island\",\n            \"items\": [\n                \"ITEM_POTION\"\n            ]\n        }\n" +
        "    ],\n" +
        "    \"shops\": [\n" +
        "        {\n            \"id\": \"SHOP_ID_MEADOW_MART\",\n            \"stages\": [\n                {\n                    \"items\": [\n" +
        "\t                    \"ITEM_POKE_BALL\",\n\t                    \"ITEM_POTION\"\n                    ]\n                }\n            ]\n        },\n" +
        "        {\n            \"id\": \"SHOP_ID_STORY_SHOP\",\n            \"stages\": [\n                { \"items\": [\"ITEM_POTION\"] },\n" +
        "                { \"isUnlockedFunc\": \"IsStoryDone\", \"items\": [\"ITEM_POTION\", \"ITEM_REVIVE\"] }\n            ]\n        },\n" +
        "        {\n            \"id\": \"SHOP_ID_ISLAND_SHOP\",\n            \"stages\": [\n                { \"sharedItems\": \"Island\" }\n            ]\n        }\n" +
        "    ]\n}\n";

    [Fact]
    public void Reads_shops_shared_lists_and_story_stages()
    {
        var parsed = ShopsJson.Parse(Json)!;
        var marts = ShopsJson.ToMarts(parsed).ToList();

        Assert.Equal(["shared:Island", "SHOP_ID_MEADOW_MART", "SHOP_ID_STORY_SHOP", "SHOP_ID_ISLAND_SHOP"], marts.Select(m => m.Key));
        Assert.Equal("Island (shared by 1 shop)", marts[0].Place);
        Assert.Equal(("Meadow Mart", 2), (marts[1].Place, marts[1].Items.Count));
        Assert.NotNull(marts[2].TiersLockedReason); // already changes with the story
        Assert.Contains("shared list", marts[3].LockedReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Item_edits_keep_the_files_own_indentation()
    {
        var parsed = ShopsJson.Parse(Json)!;
        var before = ShopsJson.ToMarts(parsed).Single(m => m.Key == "SHOP_ID_MEADOW_MART");

        var (edits, needsFuncs) = ShopsJson.EditsFor(parsed, [(before, before with { Items = [.. before.Items, "ITEM_REVIVE"] })]);
        var after = TextEdits.Apply(Json, edits);

        Assert.False(needsFuncs);
        Assert.Contains("\t                    \"ITEM_POTION\",\n\t                    \"ITEM_REVIVE\"\n                    ]", after, StringComparison.Ordinal);
        Assert.Equal(Json.Split('\n').Length + 1, after.Split('\n').Length);
    }

    [Fact]
    public void Badge_stock_becomes_stages_with_badge_checks_and_reads_back()
    {
        var parsed = ShopsJson.Parse(Json)!;
        var before = ShopsJson.ToMarts(parsed).Single(m => m.Key == "SHOP_ID_MEADOW_MART");
        var tiers = new Dictionary<int, IReadOnlyList<string>> { [3] = ["ITEM_REVIVE"], [5] = ["ITEM_FULL_HEAL"] };

        var (edits, needsFuncs) = ShopsJson.EditsFor(parsed, [(before, before with { BadgeTiers = tiers })]);
        var after = TextEdits.Apply(Json, edits);

        Assert.True(needsFuncs);
        var reread = ShopsJson.ToMarts(ShopsJson.Parse(after)!).Single(m => m.Key == "SHOP_ID_MEADOW_MART");
        Assert.Equal(["ITEM_POKE_BALL", "ITEM_POTION"], reread.Items);
        Assert.Equal(["ITEM_REVIVE"], reread.BadgeTiers[3]);
        Assert.Equal(["ITEM_FULL_HEAL"], reread.BadgeTiers[5]);
        Assert.Null(reread.TiersLockedReason);
        Assert.Contains("\"isUnlockedFunc\": \"PokeStudio_HasBadge5\"", after, StringComparison.Ordinal);
        Assert.Contains("SHOP_ID_STORY_SHOP", after, StringComparison.Ordinal); // other shops untouched

        const string shopC = "#include \"global.h\"\n\n#include \"data/shops.h\"\n";
        var code = TextEdits.Apply(shopC, [ShopsJson.BadgeFunctionsEdit(shopC)!]);
        Assert.Contains("static bool32 PokeStudio_HasBadge8(void) { return FlagGet(FLAG_BADGE08_GET); }\n// Poke Studio: end\n\n#include \"data/shops.h\"", code, StringComparison.Ordinal);
        Assert.Null(ShopsJson.BadgeFunctionsEdit(code)); // added only once
    }

    [Fact]
    public void Shared_lists_are_edited_in_one_place()
    {
        var parsed = ShopsJson.Parse(Json)!;
        var shared = ShopsJson.ToMarts(parsed).First();

        var (edits, _) = ShopsJson.EditsFor(parsed, [(shared, shared with { Items = ["ITEM_POTION", "ITEM_ETHER"] })]);

        Assert.Contains("\"ITEM_ETHER\"", TextEdits.Apply(Json, edits), StringComparison.Ordinal);
    }
}
