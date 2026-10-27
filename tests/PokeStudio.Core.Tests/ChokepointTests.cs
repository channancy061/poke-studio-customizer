using PokeStudio.Modules.Expansion;
using Xunit;

namespace PokeStudio.Core.Tests;

public class ChokepointTests
{
    [Fact]
    public void Behaviours_are_read_from_an_enum_or_defines()
    {
        var fromEnum = Chokepoints.Behaviors("enum\n{\n    MB_NORMAL,\n    MB_TALL_GRASS = 0x2,\n    MB_POND_WATER,\n};\n");
        Assert.Equal(0, fromEnum["MB_NORMAL"]);
        Assert.Equal(3, fromEnum["MB_POND_WATER"]);
        var fromDefines = Chokepoints.Behaviors("#define MB_NORMAL 0x00\n#define MB_JUMP_EAST 0x38\n");
        Assert.Equal(0x38, fromDefines["MB_JUMP_EAST"]);
    }

    [Fact]
    public void A_route_script_gets_the_moves_first_in_its_table_and_loses_them_again()
    {
        const string plain = "Route9_MapScripts::\n\tmap_script MAP_SCRIPT_ON_TRANSITION, Route9_OnTransition\n\t.byte 0\n\nRoute9_OnTransition::\n\tend\n";
        var moved = Chokepoints.Script(plain, "Route9", [new Chokepoints.Move(3, "Route9_EventScript_Bob", 5, 5, 12, 7, "RIGHT")]);
        Assert.StartsWith("Route9_MapScripts::\n\tmap_script MAP_SCRIPT_ON_TRANSITION, PokeStudio_Choke_Route9 @", moved, StringComparison.Ordinal);
        Assert.Contains("\tsetobjectxyperm 3, 12, 7 @", moved, StringComparison.Ordinal);
        Assert.Contains("\tsetobjectmovementtype 3, MOVEMENT_TYPE_FACE_RIGHT @", moved, StringComparison.Ordinal);
        Assert.Contains("\tgoto Route9_OnTransition @", moved, StringComparison.Ordinal); // the map's own script still runs
        Assert.Equal(plain, Chokepoints.Script(moved, "Route9", []));
    }

    private static void RealProject(string? root)
    {
        if (root is null || !Directory.Exists(root))
        {
            return;
        }

        string? Read(string rel) => File.Exists(Path.Combine(root, rel)) ? File.ReadAllText(Path.Combine(root, rel)) : null;
        byte[]? ReadBytes(string rel) => File.Exists(Path.Combine(root, rel)) ? File.ReadAllBytes(Path.Combine(root, rel)) : null;
        var behaviors = Chokepoints.Behaviors(Read("include/constants/metatile_behaviors.h"));
        Assert.Contains("MB_POND_WATER", behaviors.Keys);
        var report = new List<string>();
        var moved = 0;
        foreach (var map in Chokepoints.Routes(root, indoors: true))
        {
            var plan = Chokepoints.PlanRoute(map, Read, ReadBytes, behaviors, picture: map.StartsWith("RocketHideout", StringComparison.Ordinal));
            Assert.NotNull(plan);
            Assert.Equal(plan.Moves.Count, plan.Moves.Select(m => (m.X, m.Y)).Distinct().Count());
            // trainers stay in their own part of the route (FireRed's Lv 31 Crissy stays in her corner of Route 4)
            Assert.All(plan.Moves, m => Assert.True(Math.Abs(m.X - m.FromX) + Math.Abs(m.Y - m.FromY) <= 15, $"{m.Script} moved from ({m.FromX},{m.FromY}) to ({m.X},{m.Y})"));
            moved += plan.Moves.Count;
            report.Add($"{map}: {plan.Moves.Count} moved, {plan.AlreadyInTheWay.Count} in the way, {plan.Kept.Count} kept [{plan.Why}]"
                + string.Concat(plan.Moves.Select(m => $"\n  {m.Script} #{m.LocalId}: ({m.FromX},{m.FromY}) -> ({m.X},{m.Y}) facing {m.Facing}"))
                + (plan.Kept.Count == 0 ? "" : $"\n  kept: {string.Join(", ", plan.Kept)}"));
        }

        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, $"chokepoints-{Path.GetFileName(root)}.txt"), string.Join("\n", report));
        Assert.True(moved >= 10, $"only {moved} trainers found a chokepoint");
    }

    [Fact]
    public void Real_firered_route_trainers_get_chokepoints_when_available() => RealProject(Environment.GetEnvironmentVariable("POKESTUDIO_FIRERED_DIR"));

    [Fact]
    public void Real_emerald_route_trainers_get_chokepoints_when_available() => RealProject(Environment.GetEnvironmentVariable("POKESTUDIO_EXPANSION_DIR"));
}
