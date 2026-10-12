using System.Text.RegularExpressions;
using PokeStudio.Core.Graphics;
using PokeStudio.Core.SourceCode;

namespace PokeStudio.Modules.Expansion;

/// <summary>
/// Brings a trainer's battle sprite or overworld sprite from another expansion project the user has (FireRed has no
/// Hoenn overworld sprites, for example). Each new entry copies the format of an entry the target project already has, so
/// it fits that project's way of building graphics. Lines added to existing files are marked.
/// </summary>
internal static partial class ProjectImport
{
    private const string Mark = "Poke Studio import";
    public const string TrainerGraphics = "src/data/graphics/trainers.h";
    public const string TrainerConstants = "include/constants/trainers.h";
    public const string ObjectGraphics = "src/data/object_events/object_event_graphics.h";
    public const string ObjectPics = "src/data/object_events/object_event_pic_tables.h";
    public const string ObjectInfo = "src/data/object_events/object_event_graphics_info.h";
    public const string ObjectPointers = "src/data/object_events/object_event_graphics_info_pointers.h";
    public const string ObjectConstants = "include/constants/event_objects.h";
    public const string SpriteRules = "spritesheet_rules.mk";

    public static IReadOnlyList<string> TextFiles { get; } =
        [TrainerGraphics, TrainerConstants, ObjectGraphics, ObjectPics, ObjectInfo, ObjectPointers, ObjectConstants, SpriteRules];

    /// <summary>Text files to write and new files (pictures, palettes) to add.</summary>
    public sealed class Plan(Func<string, string?> read, Func<string, bool> exists)
    {
        public bool Exists(string rel) => Binary.ContainsKey(rel) || NewText.ContainsKey(rel) || exists(rel);

        private readonly Dictionary<string, string> _text = new(StringComparer.Ordinal);
        public Dictionary<string, byte[]> Binary { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> NewText { get; } = new(StringComparer.Ordinal);

        public string? Get(string rel) => _text.TryGetValue(rel, out var t) ? t : read(rel);

        public void Set(string rel, string text) => _text[rel] = text;

        public IReadOnlyDictionary<string, string> Changed => _text;
    }

    public static bool HasPic(Func<string, string?> read, string pic) =>
        read(TrainerGraphics) is { } t && Regex.IsMatch(t, $@"\[{pic}\]\s*=");

    public static bool HasLook(Func<string, string?> read, string look) =>
        read(ObjectPointers) is { } t && Regex.IsMatch(t, $@"\[{look}\]\s*=");

    private static string Snake(string constant, string prefix) => constant[prefix.Length..].ToLowerInvariant();

    /// <summary>The file a graphics declaration reads, e.g. INCGFX_U32("…/leader_juan.png", ".4bpp.smol") or INCBIN_U32("….4bpp.smol").</summary>
    private static string? DeclaredPath(string text, string symbol) =>
        Regex.Match(text, $@"\b{symbol}\[\]\s*=\s*\w+\(""([^""]+)""") is { Success: true } m ? m.Groups[1].Value : null;

    private static string? DeclarationLine(string text, string symbol) =>
        Regex.Match(text, $@"^[^\n]*\b{symbol}\[\]\s*=[^\n]*\n", RegexOptions.Multiline) is { Success: true } m ? m.Value : null;

    /// <summary>"…/leader_juan_front_pic.4bpp.smol" → "…/leader_juan_front_pic.png".</summary>
    private static string PngOf(string path)
    {
        var dir = path.Contains('/', StringComparison.Ordinal) ? path[..(path.LastIndexOf('/') + 1)] : "";
        var file = path[dir.Length..];
        return dir + file.Split('.')[0] + ".png";
    }

    private static string InsertAfter(string text, string anchor, string addition)
    {
        var at = text.IndexOf(anchor, StringComparison.Ordinal);
        return at < 0 ? text : text.Insert(at + anchor.Length, addition);
    }

    /// <summary>Adds a battle sprite (TRAINER_PIC_…) taken from the source project.</summary>
    public static void AddPic(Plan plan, string sourceRoot, string pic)
    {
        string? Src(string rel) => File.Exists(Path.Combine(sourceRoot, rel)) ? File.ReadAllText(Path.Combine(sourceRoot, rel)) : null;
        var srcTable = Src(TrainerGraphics) ?? throw new InvalidOperationException($"{sourceRoot} doesn't look like an expansion project (no {TrainerGraphics}).");
        var entry = Regex.Match(srcTable, $@"\[{pic}\]\s*=\s*\{{\s*\.frontPic\s*=\s*TRAINER_FRONT_PIC\((\w+),\s*(\w+)");
        if (!entry.Success)
        {
            throw new InvalidOperationException($"The project at {sourceRoot} has no battle sprite {pic}.");
        }

        var (frontSym, palSym) = (entry.Groups[1].Value, entry.Groups[2].Value);
        var srcFront = DeclaredPath(srcTable, frontSym) ?? throw new InvalidOperationException($"Can't find the picture for {pic} in {sourceRoot}.");
        var srcPng = Path.Combine(sourceRoot, PngOf(srcFront));
        if (!File.Exists(srcPng))
        {
            throw new InvalidOperationException($"{srcPng} is missing.");
        }

        var png = File.ReadAllBytes(srcPng);
        var image = Png.Read(png) as IndexedImage ?? throw new InvalidOperationException($"{srcPng} isn't a 16-colour picture.");

        // the target's own format, from an entry it has
        var table = plan.Get(TrainerGraphics)!;
        var template = TemplatePic().Match(table);
        if (!template.Success)
        {
            throw new InvalidOperationException("This project's battle sprite list has an unexpected format.");
        }

        var (tPic, tFront, tPal, tRest) = (template.Groups[1].Value, template.Groups[2].Value, template.Groups[3].Value, template.Groups[4].Value);
        string tSnake = Snake(tPic, "TRAINER_PIC_"), snake = Snake(pic, "TRAINER_PIC_");
        var frontLine = DeclarationLine(table, tFront)!;
        var palLine = DeclarationLine(table, tPal)!;
        string Unmarked(string line) => line.Replace($" // {Mark}", "", StringComparison.Ordinal);
        var tFrontPath = DeclaredPath(table, tFront)!;
        var tPalPath = DeclaredPath(table, tPal)!;
        var frontPath = tFrontPath.Replace(tSnake, snake, StringComparison.Ordinal);
        var palPath = tPalPath.Replace(tSnake, snake, StringComparison.Ordinal);
        var newFront = Unmarked(frontLine).Replace(tFront, frontSym, StringComparison.Ordinal).Replace(tFrontPath, frontPath, StringComparison.Ordinal).TrimEnd('\n') + $" // {Mark}\n";
        var newPal = Unmarked(palLine).Replace(tPal, palSym, StringComparison.Ordinal).Replace(tPalPath, palPath, StringComparison.Ordinal).TrimEnd('\n') + $" // {Mark}\n";
        var entryText = template.Value.Replace(tPic, pic, StringComparison.Ordinal).Replace(tFront, frontSym, StringComparison.Ordinal).Replace(tPal, palSym, StringComparison.Ordinal);
        table = InsertAfter(table, palLine, newFront + newPal);

        // the declarations above moved the text: find the template entry again before adding ours after it
        var entryAt = table.IndexOf(template.Value, StringComparison.Ordinal);
        table = table.Insert(entryAt + template.Length, $"\n    // {Mark}\n    {entryText.TrimStart()}");
        plan.Set(TrainerGraphics, table);

        if (plan.Exists(PngOf(frontPath)))
        {
            throw new InvalidOperationException($"{PngOf(frontPath)} already exists in this project.");
        }

        plan.Binary[PngOf(frontPath)] = png;
        if (!palPath.EndsWith(".png", StringComparison.Ordinal) && !palPath.Contains(".png\"", StringComparison.Ordinal))
        {
            // a separate palette file: written as a JASC .pal next to where the build expects it
            var pal = palPath.Split('.')[0] + ".pal";
            if (pal != PngOf(frontPath))
            {
                plan.NewText[pal] = JascPalette.Write(image.Palette.Take(16).Concat(Enumerable.Repeat(new Rgb(0, 0, 0), Math.Max(0, 16 - image.Palette.Count))).ToList());
            }
        }

        // the constant, after the template's
        var constants = plan.Get(TrainerConstants)!;
        var line = Regex.Match(constants, $@"^([ \t]*){tPic},[^\n]*\n", RegexOptions.Multiline);
        if (!line.Success)
        {
            throw new InvalidOperationException($"Can't find {tPic} in {TrainerConstants}.");
        }

        plan.Set(TrainerConstants, constants.Insert(line.Index + line.Length, $"{line.Groups[1].Value}{pic}, // {Mark}\n"));
    }

    [GeneratedRegex(@"(?<=[a-z0-9])(?=[A-Z])")]
    private static partial Regex SnakeName();

    [GeneratedRegex(@"\[(TRAINER_PIC_\w+)\]\s*=\s*\{\s*\.frontPic\s*=\s*TRAINER_FRONT_PIC\((\w+),\s*(\w+)([^)]*)\),\s*\},")]
    private static partial Regex TemplatePic();

    /// <summary>Adds an overworld sprite (OBJ_EVENT_GFX_…) taken from the source project.</summary>
    public static void AddLook(Plan plan, string sourceRoot, string look)
    {
        string? Src(string rel) => File.Exists(Path.Combine(sourceRoot, rel)) ? File.ReadAllText(Path.Combine(sourceRoot, rel)) : null;
        var srcPointers = Src(ObjectPointers) ?? throw new InvalidOperationException($"{sourceRoot} doesn't look like an expansion project (no {ObjectPointers}).");
        var pointer = Regex.Match(srcPointers, $@"\[{look}\]\s*=\s*&(\w+)");
        if (!pointer.Success)
        {
            throw new InvalidOperationException($"The project at {sourceRoot} has no overworld sprite {look}.");
        }

        var infoSym = pointer.Groups[1].Value;
        var name = infoSym.Replace("gObjectEventGraphicsInfo_", "", StringComparison.Ordinal);
        var srcInfo = Block(Src(ObjectInfo) ?? "", $"{infoSym} =") ?? throw new InvalidOperationException($"Can't find {infoSym} in {sourceRoot}.");
        string Field(string block, string field) => Regex.Match(block, $@"\.{field}\s*=\s*([^,\n]+),") is { Success: true } m ? m.Groups[1].Value.Trim() : "";
        var (palTag, palSlot, width, height) = (Field(srcInfo, "paletteTag"), Field(srcInfo, "paletteSlot"), Field(srcInfo, "width"), Field(srcInfo, "height"));
        var picTable = Field(srcInfo, "images");
        var srcPics = Block(Src(ObjectPics) ?? "", $"{picTable}[] =") ?? throw new InvalidOperationException($"Can't find {picTable} in {sourceRoot}.");
        var picSym = Regex.Match(srcPics, @"overworld_frame\((\w+)").Groups[1].Value;
        var srcPath = DeclaredPath(Src(ObjectGraphics) ?? "", picSym) ?? throw new InvalidOperationException($"Can't find the picture for {look} in {sourceRoot}.");
        var srcPng = Path.Combine(sourceRoot, PngOf(srcPath));
        if (!File.Exists(srcPng))
        {
            throw new InvalidOperationException($"{srcPng} is missing.");
        }

        // the target must have the palette the sprite was drawn for
        var info = plan.Get(ObjectInfo)!;
        if (!info.Contains($"paletteTag = {palTag},", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"This project has no {palTag} palette, which {name}'s overworld sprite needs.");
        }

        // a target sprite of the same size with the standard walking animations is the template
        var template = Regex.Matches(info, @"const struct ObjectEventGraphicsInfo (gObjectEventGraphicsInfo_(\w+)) = \{[^}]*\};")
            .FirstOrDefault(m => m.Value.Contains($".width = {width},", StringComparison.Ordinal) && m.Value.Contains($".height = {height},", StringComparison.Ordinal)
                && m.Value.Contains("sAnimTable_Standard", StringComparison.Ordinal) && !m.Value.Contains(".compressed = TRUE", StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"This project has no {width}x{height} walking sprite to copy the format from.");
        var (tInfo, tName) = (template.Groups[1].Value, template.Groups[2].Value);
        if (info.Contains($"gObjectEventGraphicsInfo_{name} =", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"This project already has a sprite called {name}.");
        }

        string Rename(string text) => text.Replace("_" + tName, "_" + name, StringComparison.Ordinal);
        var newInfo = Regex.Replace(Rename(template.Value), @"\.paletteTag = [^,]+,", $".paletteTag = {palTag},");
        newInfo = Regex.Replace(newInfo, @"\.paletteSlot = [^,]+,", $".paletteSlot = {palSlot},");
        plan.Set(ObjectInfo, info.Insert(template.Index + template.Length, $"\n\n// {Mark}\n{newInfo}"));

        var pics = plan.Get(ObjectPics)!;
        var tPicTable = Regex.Match(template.Value, @"\.images = (\w+),").Groups[1].Value;
        var tPics = Block(pics, $"{tPicTable}[] =") ?? throw new InvalidOperationException($"Can't find {tPicTable}.");
        var tPicSym = Regex.Match(tPics, @"overworld_frame\((\w+)").Groups[1].Value;
        var at = pics.IndexOf(tPics, StringComparison.Ordinal) + tPics.Length;
        plan.Set(ObjectPics, pics.Insert(at, $"\n\n// {Mark}\n{Rename(tPics)}"));

        var gfx = plan.Get(ObjectGraphics)!;
        var tLine = DeclarationLine(gfx, tPicSym)!;
        var tPath = DeclaredPath(gfx, tPicSym)!;
        var tFile = tPath[(tPath.LastIndexOf('/') + 1)..].Split('.')[0];
        // named after the character (Emerald keeps some as e.g. wallace/walking.png)
        var file = SnakeName().Replace(name, "_").ToLowerInvariant();
        var path = tPath[..(tPath.LastIndexOf('/') + 1)] + file + tPath[(tPath.LastIndexOf('/') + 1 + tFile.Length)..];
        if (plan.Exists(PngOf(path)))
        {
            throw new InvalidOperationException($"{PngOf(path)} already exists in this project.");
        }

        gfx = InsertAfter(gfx, tLine, Rename(tLine).Replace($" // {Mark}", "", StringComparison.Ordinal).Replace(tPath, path, StringComparison.Ordinal).TrimEnd('\n') + $" // {Mark}\n");
        plan.Set(ObjectGraphics, gfx);
        plan.Binary[PngOf(path)] = File.ReadAllBytes(srcPng);

        // the build rule, when the target lists one per sprite (FireRed's spritesheet_rules.mk)
        if (plan.Get(SpriteRules) is { } rules)
        {
            var tRule = Regex.Match(rules, $@"^\$\(OBJEVENTGFXDIR\)/[^\n]*/{tFile}\.4bpp:[^\n]*\n\t[^\n]*\n", RegexOptions.Multiline);
            if (tRule.Success)
            {
                var rule = tRule.Value.Replace($"/{tFile}.4bpp", $"/{file}.4bpp", StringComparison.Ordinal);
                plan.Set(SpriteRules, rules.Insert(tRule.Index + tRule.Length, $"\n# {Mark}\n{rule}"));
            }
        }

        var pointers = plan.Get(ObjectPointers)!;
        var ext = Regex.Match(pointers, $@"^extern const struct ObjectEventGraphicsInfo {tInfo};[^\n]*\n", RegexOptions.Multiline);
        if (ext.Success)
        {
            pointers = pointers.Insert(ext.Index + ext.Length, $"extern const struct ObjectEventGraphicsInfo gObjectEventGraphicsInfo_{name}; // {Mark}\n");
        }

        var map = Regex.Match(pointers, $@"^([ \t]*)\[\w+\]\s*=\s*&{tInfo},[^\n]*\n", RegexOptions.Multiline);
        if (!map.Success)
        {
            throw new InvalidOperationException($"Can't find {tInfo} in the sprite list.");
        }

        plan.Set(ObjectPointers, pointers.Insert(map.Index + map.Length, $"{map.Groups[1].Value}[{look}] = &gObjectEventGraphicsInfo_{name}, // {Mark}\n"));

        // the constant: an enum member before the count
        var constants = plan.Get(ObjectConstants)!;
        var count = Regex.Match(constants, @"^([ \t]*)NUM_OBJ_EVENT_GFX,", RegexOptions.Multiline);
        if (!count.Success)
        {
            throw new InvalidOperationException("This project numbers its overworld sprites by hand; add the sprite with Porymap instead.");
        }

        plan.Set(ObjectConstants, constants.Insert(count.Index, $"{count.Groups[1].Value}{look}, // {Mark}\n"));
    }

    /// <summary>A C definition "name = { … };" (or "name[] = { … };"), or null.</summary>
    private static string? Block(string text, string head)
    {
        var at = text.IndexOf(head, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var start = text.LastIndexOf('\n', at) + 1;
        var open = text.IndexOf('{', at);
        var close = CScanner.FindMatching(text, open);
        var end = text.IndexOf(';', close);
        return close < 0 || end < 0 ? null : text[start..(end + 1)];
    }
}
