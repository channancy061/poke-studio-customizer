using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PokeStudio.Core.Models;

namespace PokeStudio.Modules.Expansion;

/// <summary>
/// Day and night encounters: the night tables (<see cref="Core.Progression.EncounterPlanner.NightTables"/>) go into
/// wild_encounters.json right after their day table, keyed "…_Night" as the game's converter expects, and marked with a
/// "poke_studio" field it ignores, so only these are taken out again; the time-of-day switch in overworld.h turns on.
/// </summary>
internal static class NightWriter
{
    public const string MarkKey = "poke_studio";
    public const string Overworld = "include/config/overworld.h";
    private const string DefineMark = "Poke Studio: day and night";

    public static string Encounters(string json, IReadOnlyList<EncounterTable> nights, bool on)
    {
        var root = JsonNode.Parse(json)!;
        var changed = false;
        var byDay = nights.ToDictionary(t => t.Key[..^"_Night".Length], StringComparer.Ordinal);
        foreach (var group in root["wild_encounter_groups"]?.AsArray() ?? [])
        {
            if (group?["encounters"] is not JsonArray encounters)
            {
                continue;
            }

            for (var i = encounters.Count - 1; i >= 0; i--)
            {
                if (encounters[i]?[MarkKey] is not null)
                {
                    encounters.RemoveAt(i);
                    changed = true;
                }
            }

            if (!on)
            {
                continue;
            }

            for (var i = 0; i < encounters.Count; i++)
            {
                if (encounters[i]?["base_label"]?.GetValue<string>() is not { } label || !byDay.TryGetValue(label, out var night))
                {
                    continue;
                }

                var entry = new JsonObject
                {
                    ["map"] = night.Map,
                    ["base_label"] = night.Key,
                };
                foreach (var m in night.Methods)
                {
                    entry[m.Type] = new JsonObject
                    {
                        ["encounter_rate"] = encounters[i]![m.Type]?["encounter_rate"]?.GetValue<int>() ?? m.Rate,
                        ["mons"] = new JsonArray([.. m.Slots.Select(s => (JsonNode)new JsonObject { ["min_level"] = s.MinLevel, ["max_level"] = s.MaxLevel, ["species"] = s.Species })]),
                    };
                }

                entry[MarkKey] = "night";
                encounters.Insert(i + 1, entry);
                i++;
                changed = true;
            }
        }

        if (!changed)
        {
            return json;
        }

        var text = root.ToJsonString(EncountersFile.WriteOptions);
        return json.EndsWith('\n') ? text + "\n" : text;
    }

    /// <summary>overworld.h with the time-of-day encounters on, or as it was.</summary>
    public static string Config(string text, bool on)
    {
        text = Regex.Replace(text, $@"^(#define OW_TIME_OF_DAY_ENCOUNTERS[ \t]+)\S+(.*?) // {DefineMark} \(was (\S+)\)$", "${1}$3$2", RegexOptions.Multiline);
        return on
            ? Regex.Replace(text, @"^(#define OW_TIME_OF_DAY_ENCOUNTERS[ \t]+)(FALSE)([^\r\n]*)", m => $"{m.Groups[1].Value}TRUE{m.Groups[3].Value} // {DefineMark} (was {m.Groups[2].Value})", RegexOptions.Multiline)
            : text;
    }
}
