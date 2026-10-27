using System.Text;
using PokeStudio.Core.Story;
using PokeStudio.Modules.Expansion;

namespace PokeStudio.Core.Tests;

public class LegendScriptTests
{
    [Fact]
    public void A_legendary_leaves_the_map_after_the_battle_and_comes_back_only_when_it_got_away()
    {
        var sb = new StringBuilder();
        LegendWriter.Scripts(sb, [new StoryLegend { Species = "SPECIES_REGIROCK", Level = 15, Map = "RockTunnel_B1F", Place = "Rock Tunnel" }], _ => "Regirock", true);
        var s = sb.ToString();

        // it's taken off the map straight away (the flag alone only hides it when the map loads again)
        var battle = s.IndexOf("special BattleSetup_StartLegendaryBattle", StringComparison.Ordinal);
        var remove = s.IndexOf("\tremoveobject VAR_LAST_TALKED", StringComparison.Ordinal);
        var caught = s.IndexOf("\tgoto_if_eq VAR_RESULT, B_OUTCOME_CAUGHT", StringComparison.Ordinal);
        var clear = s.IndexOf("\tclearflag FLAG_PS_LEGEND_REGIROCK", StringComparison.Ordinal);
        Assert.True(battle < remove && remove < caught && caught < clear, s);
    }
}
