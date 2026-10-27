using System.Globalization;
using System.Text.RegularExpressions;
using PokeStudio.Core.SourceCode;

namespace PokeStudio.Modules.Expansion;

/// <summary>
/// A bigger Items pocket. The main save block has no room left (FireRed's is full), so the pocket moves to save block 3,
/// which has plenty: a new array there, the bag pointed at it, and FireRed's filler made smaller by the same amount. The
/// main save block keeps its layout. Every changed line carries the mark (or keeps the original in a comment), so a
/// save without the setting puts each file back as it was.
/// </summary>
internal static partial class BiggerBag
{
    public const string Constants = "include/constants/global.h";
    public const string Global = "include/global.h";
    public const string Item = "src/item.c";
    public const string ItemMenu = "src/item_menu.c";
    public static readonly string[] Files = [Constants, Global, Item, ItemMenu];

    private const string Mark = "Poke Studio: bigger bag";
    private const string PocketName = "gSaveBlock3Ptr->psBagItems";
    private const string OldPocketName = "gSaveBlock1Ptr->bag.items";
    private const string ShrinkFiller = " - sizeof(struct ItemSlot) * PS_BAG_ITEMS_COUNT";

    /// <summary>The largest pocket offered: room in save block 3 with plenty to spare for other features.</summary>
    public const int MaxSlots = 150;

    /// <summary>One of the four <see cref="Files"/> with a pocket of <paramref name="slots"/> items, or as it was when it's 0.</summary>
    public static string Apply(string file, string text, int slots)
    {
        var clean = Clean(text);
        if (slots <= 0)
        {
            return clean;
        }

        return file switch
        {
            Constants => WithDefine(clean, Math.Min(slots, MaxSlots)),
            Global => WithSaveBlock3Array(clean),
            Item => WithPointer(clean),
            ItemMenu => WithMenuSizes(clean),
            _ => text,
        };
    }

    /// <summary>A file with every bigger-bag change taken out (marked lines removed, the old names and filler put back).</summary>
    internal static string Clean(string text)
    {
        text = Regex.Replace(text, $@"^[^\n]*// {Mark}[^\n]*\n", "", RegexOptions.Multiline);
        return text.Replace(ShrinkFiller, "", StringComparison.Ordinal)
            .Replace(PocketName, OldPocketName, StringComparison.Ordinal)
            .Replace("PS_BAG_ITEMS_COUNT", "BAG_ITEMS_COUNT", StringComparison.Ordinal);
    }

    private static string WithDefine(string text, int slots)
    {
        var at = Regex.Match(text, @"^#define BAG_ITEMS_COUNT[^\n]*\n", RegexOptions.Multiline);
        return at.Success
            ? text.Insert(at.Index + at.Length, $"#define PS_BAG_ITEMS_COUNT {slots.ToString(CultureInfo.InvariantCulture)} // {Mark}{CScanner.LineEnding(text)}")
            : text;
    }

    private static string WithSaveBlock3Array(string text)
    {
        var start = text.IndexOf("struct SaveBlock3\n", StringComparison.Ordinal) is var s and >= 0 ? s : text.IndexOf("struct SaveBlock3\r\n", StringComparison.Ordinal);
        if (start < 0)
        {
            return text;
        }

        var open = text.IndexOf('{', start);
        var close = CScanner.FindMatching(text, open);
        var nl = CScanner.LineEnding(text);
        var body = text[open..close];

        // FireRed pads save block 3 to its full size: the filler gives up the room the pocket takes
        var filler = Regex.Match(body, @"u8 unused\[([^\]\n]*)\]");
        if (filler.Success)
        {
            var at = open + filler.Groups[1].Index + filler.Groups[1].Length;
            text = text.Insert(at, ShrinkFiller);
            close += ShrinkFiller.Length;
        }

        var lineStart = text.LastIndexOf('\n', close) + 1;
        return text.Insert(lineStart, $"    struct ItemSlot psBagItems[PS_BAG_ITEMS_COUNT]; // {Mark}{nl}");
    }

    private static string WithPointer(string text)
    {
        var at = Regex.Match(text, @"^([ \t]*)gBagPockets\[POCKET_ITEMS\]\.capacity = BAG_ITEMS_COUNT;[^\n]*\n", RegexOptions.Multiline);
        if (!at.Success)
        {
            return text;
        }

        var i = at.Groups[1].Value;
        var nl = CScanner.LineEnding(text);
        text = text.Insert(at.Index + at.Length,
            $"{i}gBagPockets[POCKET_ITEMS].itemSlots = gSaveBlock3Ptr->psBagItems; // {Mark}{nl}{i}gBagPockets[POCKET_ITEMS].capacity = PS_BAG_ITEMS_COUNT; // {Mark}{nl}" +
            $"{i}if (gSaveBlock3Ptr->psBagItems[0].itemId == ITEM_NONE && gSaveBlock1Ptr->bag.items[0].itemId != ITEM_NONE) {{ memcpy(gSaveBlock3Ptr->psBagItems, gSaveBlock1Ptr->bag.items, min(sizeof(gSaveBlock1Ptr->bag.items), sizeof(gSaveBlock3Ptr->psBagItems))); memset(gSaveBlock1Ptr->bag.items, 0, sizeof(gSaveBlock1Ptr->bag.items)); }} // {Mark}: an older save's items move over{nl}");

        // a new game empties the pocket too
        var clear = Regex.Match(text, @"^([ \t]*)CpuFastFill\(0, &gSaveBlock1Ptr->bag, sizeof\(struct Bag\)\);[^\n]*\n", RegexOptions.Multiline);
        return clear.Success
            ? text.Insert(clear.Index + clear.Length, $"{clear.Groups[1].Value}CpuFastFill(0, gSaveBlock3Ptr->psBagItems, sizeof(gSaveBlock3Ptr->psBagItems)); // {Mark}{nl}")
            : text;
    }

    private static string WithMenuSizes(string text)
    {
        // the bag menu's list sizes and the tutorial's bag backup (Oak's / Wally's catching demo) follow the new pocket;
        // changed in place (one line sits inside a macro, where a comment can't go), and Clean renames them back
        text = Regex.Replace(text, @"(?<![\w])BAG_ITEMS_COUNT\b", "PS_BAG_ITEMS_COUNT");
        return text.Replace(OldPocketName, PocketName, StringComparison.Ordinal);
    }
}
