using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;
using PokeStudio.Core.Safety;
using PokeStudio.Modules.Gen3Rom;
using PokeStudio.Modules.Nds;

namespace PokeStudio.Core.Tests;

/// <summary>Dialogue editing in ROM mode, on copies of the user's own ROMs when their environment variables are set.</summary>
public class RomTextTests
{
    private static async Task EditAndReload(IGameModule module, string? real, string file, int atLeast, string needle = "{PLAYER}", string added = "\\pPoke Studio was here, {PLAYER}!\\nThis line is new.")
    {
        if (string.IsNullOrEmpty(real) || !File.Exists(real))
        {
            return;
        }

        using var tmp = new TempDir();
        var rom = tmp.Combine(file);
        File.Copy(real, rom);
        Assert.True(module.Capabilities.HasFlag(ModuleCapabilities.Text));
        var data = await module.LoadDataAsync(rom, tmp.Combine("data"));
        Assert.True(data.Texts.Count >= atLeast, $"only {data.Texts.Count} messages found");
        Assert.Contains(data.Texts, t => t.Text.Contains(needle, StringComparison.Ordinal));
        Assert.All(data.Texts, t => Assert.False(string.IsNullOrWhiteSpace(t.Place)));
        Assert.Empty(module.Validate(data));

        // a sample to read through (kept for review)
        var outDir = Path.Combine(AppContext.BaseDirectory, "TestResults", "text");
        Directory.CreateDirectory(outDir);
        await File.WriteAllLinesAsync(Path.Combine(outDir, Path.ChangeExtension(file, ".txt")),
            [$"{data.Texts.Count} messages", .. data.Texts.Where((_, n) => n % Math.Max(1, data.Texts.Count / 300) == 0).Select(t => $"[{t.Place}] {t.Text}")]);

        // one shorter (stays in place), one much longer (moves to free space)
        var shortIndex = data.Texts.FindIndex(t => t.Text.Length > 30 && !t.Text.Contains('{', StringComparison.Ordinal));
        var longIndex = data.Texts.FindIndex(shortIndex + 1, t => t.Text.Length > 10);
        var shortKey = data.Texts[shortIndex].Key;
        var longKey = data.Texts[longIndex].Key;
        var longText = data.Texts[longIndex].Text + added;
        data.Texts[shortIndex] = data.Texts[shortIndex] with { Text = "Hi!" };
        data.Texts[longIndex] = data.Texts[longIndex] with { Text = longText };
        Assert.Empty(module.Validate(data));
        await module.SaveDataAsync(data, new ModuleSaveContext(new SafeFileWriter(tmp.Path), tmp.Combine("data"), rom));

        var reloaded = await module.LoadDataAsync(rom, tmp.Combine("data"));
        Assert.Equal("Hi!", reloaded.Texts.Single(t => t.Key == shortKey).Text);
        Assert.Equal(longText, reloaded.Texts.Single(t => t.Key == longKey).Text);
        Assert.InRange(reloaded.Texts.Count, data.Texts.Count - 2, data.Texts.Count + 2);

        // text the font can't show is refused before saving (Gen 5 stores any character)
        if (needle == "{PLAYER}" || file.Contains("platinum", StringComparison.Ordinal))
        {
            var i = reloaded.Texts.FindIndex(t => t.Key == shortKey);
            reloaded.Texts[i] = reloaded.Texts[i] with { Text = "Hello 世界" };
            Assert.Contains(module.Validate(reloaded), v => v.Area == "Text");
        }
    }

    [Fact]
    public void Gen3_text_codes_round_trip()
    {
        var bytes = Gen3Dialogue.Write("Hi, {PLAYER}!\\nLook{FC 01 02} here.\\pBye{RIVAL}…", out var problem);
        Assert.Null(problem);
        var rom = new RomImage([.. bytes!, .. new byte[16]]);
        Assert.Equal("Hi, {PLAYER}!\\nLook{FC 01 02} here.\\pBye{RIVAL}…", Gen3Dialogue.Read(rom, 0));
        Assert.Null(Gen3Dialogue.Write("{NOT_A_CODE}", out problem));
        Assert.NotNull(problem);
    }

    [Fact]
    public Task Real_emerald_dialogue_edits_when_available() =>
        EditAndReload(new Gen3RomModule(), Environment.GetEnvironmentVariable("POKESTUDIO_EMERALD_ROM"), "emerald.gba", 2000);

    [Fact]
    public Task Real_firered_dialogue_edits_when_available() =>
        EditAndReload(new Gen3RomModule(), Environment.GetEnvironmentVariable("POKESTUDIO_FIRERED_ROM"), "firered.gba", 1500);

    [Fact]
    public Task Real_platinum_dialogue_edits_when_available() =>
        EditAndReload(new NdsModule(), Environment.GetEnvironmentVariable("POKESTUDIO_PLATINUM_ROM"), "platinum.nds", 10000, "{CMD", "\\fPoke Studio was here!\\nThis line is new.");

    [Fact]
    public Task Real_black_dialogue_edits_when_available() =>
        EditAndReload(new NdsModule(), Environment.GetEnvironmentVariable("POKESTUDIO_BLACK_ROM"), "black.nds", 5000, "{CMD", "\\fPoke Studio was here!\\nThis line is new.");
}
