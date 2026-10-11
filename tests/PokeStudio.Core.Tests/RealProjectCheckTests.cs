using PokeStudio.Core.Validation;
using PokeStudio.Modules.Expansion;

namespace PokeStudio.Core.Tests;

/// <summary>
/// The user's real expansion projects, untouched, must not show errors: every one would be the app misreading the game
/// (family shortcuts, settings inside values, template-built forms, generated names…), and errors block saving.
/// </summary>
public class RealProjectCheckTests
{
    private static async Task NoErrors(string? real)
    {
        if (string.IsNullOrEmpty(real) || !Directory.Exists(real))
        {
            return;
        }

        using var tmp = new TempDir();
        var module = new ExpansionModule();
        var data = await module.LoadDataAsync(real, tmp.Combine("data"));
        var errors = GameValidator.Run(data, module).Where(i => i.Severity == IssueSeverity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors.Take(10).Select(e => $"{e.Area} {e.Subject}: {e.Message}")));

        var byKey = data.Species.ToDictionary(s => s.Key);
        Assert.Equal(["TYPE_ELECTRIC", "TYPE_STEEL"], byKey["SPECIES_MAGNETON"].Types);
        Assert.True(byKey["SPECIES_UNOWN"].LockedFields.ContainsKey("*"));
        Assert.Equal(15, data.Moves.Single(m => m.Key == "MOVE_ICE_PUNCH").Pp);
        Assert.Contains(data.Catalog.Moves, m => m.Key == "MOVE_COUNTER");
    }

    [Fact]
    public Task Real_emerald_project_reads_without_false_errors_when_available() => NoErrors(Environment.GetEnvironmentVariable("POKESTUDIO_EXPANSION_DIR"));

    [Fact]
    public Task Real_firered_project_reads_without_false_errors_when_available() => NoErrors(Environment.GetEnvironmentVariable("POKESTUDIO_FIRERED_DIR"));
}
