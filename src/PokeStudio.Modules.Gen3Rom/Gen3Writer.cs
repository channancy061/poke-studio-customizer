using System.Globalization;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;
using PokeStudio.Core.Validation;

namespace PokeStudio.Modules.Gen3Rom;

/// <summary>Writes changed data back into the ROM: fixed-size records in place, growing lists into free space.</summary>
internal static class Gen3Writer
{
    public static List<ValidationIssue> Check(GameData data, GameLimits limits)
    {
        var issues = new List<ValidationIssue>();
        void Error(string area, string subject, string message, string? target = null) =>
            issues.Add(new ValidationIssue(IssueSeverity.Error, area, subject, message) { TargetKey = target });

        foreach (var s in data.Species.Where(s => s.IsEnabled))
        {
            if (s.Name.Length > limits.SpeciesNameLength || !Gen3Text.CanWrite(s.Name))
            {
                Error("Pokémon", s.Name, $"Names can be up to {limits.SpeciesNameLength} letters, using letters this game can show.", s.Key);
            }
        }

        foreach (var m in data.Moves)
        {
            if (m.Name.Length > limits.MoveNameLength || !Gen3Text.CanWrite(m.Name))
            {
                Error("Moves", m.Name, $"Move names can be up to {limits.MoveNameLength} letters.");
            }
        }

        foreach (var set in data.Evolutions)
        {
            if (set.Evolutions.Count > Gen3Tables.EvolutionsPerSpecies)
            {
                Error("Evolutions", Name(data, set.FromSpecies), "These games allow up to 5 evolutions per Pokémon.", set.FromSpecies);
            }

            foreach (var e in set.Evolutions)
            {
                if (FromEvolution(e, _ => 0) is null)
                {
                    Error("Evolutions", Name(data, set.FromSpecies),
                        $"\"{Describe(e)}\" can't be done in these games. Use a level, an item (stone), a trade or friendship.", set.FromSpecies);
                }
            }
        }

        var tmMoves = data.Catalog.Machines.Select(m => m.Move).ToHashSet(StringComparer.Ordinal);
        foreach (var l in data.Learnsets)
        {
            var extra = l.Teachable.Where(m => !tmMoves.Contains(m)).ToList();
            if (extra.Count > 0)
            {
                Error("Learnsets", Name(data, l.Key), $"Only TM and HM moves can be taught in these games ({string.Join(", ", extra.Select(data.Catalog.NameOf))}).", l.Key);
            }

            if (l.LevelUp.Any(m => m.Level is < 1 or > 100))
            {
                Error("Learnsets", Name(data, l.Key), "Level-up moves need a level from 1 to 100.", l.Key);
            }
        }

        foreach (var t in data.Trainers)
        {
            if (t.Name.Length > limits.TrainerNameLength || !Gen3Text.CanWrite(t.Name))
            {
                Error("Trainers", t.Name, $"Trainer names can be up to {limits.TrainerNameLength} letters.", t.Key);
            }

            if (t.Party.Count is 0 or > 6)
            {
                Error("Trainers", t.Name, "A trainer needs 1 to 6 Pokémon.", t.Key);
            }

        }

        foreach (var i in data.Items)
        {
            if (i.Price is < 0 or > 65_535)
            {
                Error("Items", i.Name, "Prices can be 0 to 65,535 in these games.");
            }
        }

        return issues;
    }

    private static string Name(GameData data, string species) => data.Species.FirstOrDefault(s => s.Key == species)?.Name ?? species;

    private static string Describe(Evolution e) =>
        $"{e.Method} {e.Parameter}" + (e.Conditions.Count > 0 ? " + " + string.Join(", ", e.Conditions.Select(c => c.Kind)) : "");

    /// <summary>The app's evolution → Gen 3 (method, parameter), or null when these games can't do it.</summary>
    internal static (int Method, int Param)? FromEvolution(Evolution e, Func<string, int> item)
    {
        var kinds = e.Conditions.Select(c => c.Kind).Order(StringComparer.Ordinal).ToList();
        var time = e.Conditions.FirstOrDefault(c => c.Kind == "IF_TIME")?.Value;
        int Level() => int.TryParse(e.Parameter, CultureInfo.InvariantCulture, out var l) ? l : -1;
        string Cond(string kind) => e.Conditions.First(c => c.Kind == kind).Value;
        return (e.Method, string.Join("+", kinds)) switch
        {
            ("EVO_LEVEL", "") when Level() is > 0 and <= 100 => (Gen3.Level, Level()),
            ("EVO_LEVEL", "IF_MIN_FRIENDSHIP") => (Gen3.Friendship, 0),
            ("EVO_LEVEL", "IF_MIN_FRIENDSHIP+IF_TIME") when time is "TIME_DAY" => (Gen3.FriendshipDay, 0),
            ("EVO_LEVEL", "IF_MIN_FRIENDSHIP+IF_TIME") when time is "TIME_NIGHT" => (Gen3.FriendshipNight, 0),
            ("EVO_LEVEL", "IF_ATK_GT_DEF") when Level() > 0 => (Gen3.AtkGtDef, Level()),
            ("EVO_LEVEL", "IF_ATK_EQ_DEF") when Level() > 0 => (Gen3.AtkEqDef, Level()),
            ("EVO_LEVEL", "IF_ATK_LT_DEF") when Level() > 0 => (Gen3.AtkLtDef, Level()),
            ("EVO_LEVEL", "IF_SILCOON") when Level() > 0 => (Gen3.Silcoon, Level()),
            ("EVO_LEVEL", "IF_CASCOON") when Level() > 0 => (Gen3.Cascoon, Level()),
            ("EVO_LEVEL", "IF_MIN_BEAUTY") => (Gen3.Beauty, int.TryParse(Cond("IF_MIN_BEAUTY"), CultureInfo.InvariantCulture, out var b) ? b : 170),
            ("EVO_LEVEL_NINJASK", "") when Level() > 0 => (Gen3.Ninjask, Level()),
            ("EVO_LEVEL_SHEDINJA", "") when Level() > 0 => (Gen3.Shedinja, Level()),
            ("EVO_TRADE", "") => (Gen3.Trade, 0),
            ("EVO_TRADE", "IF_HOLD_ITEM") => (Gen3.TradeItem, item(Cond("IF_HOLD_ITEM"))),
            ("EVO_ITEM", "") => (Gen3.Item, item(e.Parameter)),
            _ => null,
        };
    }

    public static void Write(Gen3RomModule.State s, GameData data)
    {
        var rom = s.Rom;
        var t = s.Tables;
        int Species(string key) => s.SpeciesIndex.GetValueOrDefault(key);
        int Move(string key) => s.MoveIndex.GetValueOrDefault(key);
        int Item(string? key) => key is null ? 0 : s.ItemIndex.GetValueOrDefault(key);
        int Type(string key) => Math.Max(0, Array.IndexOf(Gen3.Types, key.Replace("TYPE_", "", StringComparison.Ordinal)));

        void Text(int at, int size, string text)
        {
            var bytes = Gen3Text.Write(text);
            for (var i = 0; i < size; i++)
            {
                rom.W8(at + i, i < bytes.Length ? bytes[i] : 0);
            }
        }

        // ---- species ----
        foreach (var sp in data.Species)
        {
            if (!s.Species.TryGetValue(sp.Key, out var before) || ReferenceEquals(before, sp))
            {
                continue;
            }

            var i = Species(sp.Key);
            var at = t.BaseStats + (Gen3Tables.BaseStatsSize * i);
            rom.W8(at, sp.Stats.Hp);
            rom.W8(at + 1, sp.Stats.Attack);
            rom.W8(at + 2, sp.Stats.Defense);
            rom.W8(at + 3, sp.Stats.Speed);
            rom.W8(at + 4, sp.Stats.SpAttack);
            rom.W8(at + 5, sp.Stats.SpDefense);
            rom.W8(at + 6, Type(sp.Types.ElementAtOrDefault(0) ?? "TYPE_NORMAL"));
            rom.W8(at + 7, Type(sp.Types.ElementAtOrDefault(1) ?? sp.Types.ElementAtOrDefault(0) ?? "TYPE_NORMAL"));
            rom.W8(at + 8, sp.CatchRate);
            rom.W8(at + 9, sp.ExpYield);
            rom.W16(at + 12, Item(sp.HeldItemCommon));
            rom.W16(at + 14, Item(sp.HeldItemRare));
            rom.W8(at + 16, sp.Gender switch
            {
                { Genderless: true } => 255,
                { PercentFemale: 0 } => 0,
                { PercentFemale: 100 } => 254,
                { PercentFemale: { } f } => (int)Math.Clamp(Math.Round(f * 256 / 100), 1, 253),
                _ => rom.U8(at + 16),
            });
            rom.W8(at + 19, Math.Max(0, Array.IndexOf(Gen3.Growth, sp.Growth.Replace("GROWTH_", "", StringComparison.Ordinal))));
            var eggs = sp.EggGroups.Select(g => Math.Max(0, Array.IndexOf(Gen3.EggGroups, g.Replace("EGG_GROUP_", "", StringComparison.Ordinal)))).ToList();
            rom.W8(at + 20, eggs.ElementAtOrDefault(0));
            rom.W8(at + 21, eggs.Count > 1 ? eggs[1] : eggs.ElementAtOrDefault(0));
            rom.W8(at + 22, s.AbilityIndex.GetValueOrDefault(sp.Abilities.ElementAtOrDefault(0) ?? "ABILITY_NONE"));
            rom.W8(at + 23, s.AbilityIndex.GetValueOrDefault(sp.Abilities.ElementAtOrDefault(1) ?? "ABILITY_NONE"));
            if (sp.Name != before.Name)
            {
                Text(t.SpeciesNames + (11 * i), 11, sp.Name);
            }
        }

        // ---- moves ----
        foreach (var mv in data.Moves)
        {
            if (!s.Moves.TryGetValue(mv.Key, out var before) || ReferenceEquals(before, mv))
            {
                continue;
            }

            var i = Move(mv.Key);
            var at = t.Moves + (Gen3Tables.MoveSize * i);
            if (int.TryParse(mv.Effect.Replace("EFFECT_", "", StringComparison.Ordinal), CultureInfo.InvariantCulture, out var effect))
            {
                rom.W8(at, effect);
            }

            rom.W8(at + 1, mv.Power);
            rom.W8(at + 2, Type(mv.Type));
            rom.W8(at + 3, mv.Accuracy);
            rom.W8(at + 4, mv.Pp);
            rom.W8(at + 6, Gen3.Targets.FirstOrDefault(x => x.Key == mv.Target).Bit);
            rom.W8(at + 7, (sbyte)mv.Priority);
            rom.W8(at + 8, (rom.U8(at + 8) & ~1) | (mv.MakesContact ? 1 : 0));
            if (mv.Name != before.Name)
            {
                Text(t.MoveNames + (13 * i), 13, mv.Name);
            }
        }

        // ---- learnsets ----
        foreach (var l in data.Learnsets)
        {
            if (!s.Learnsets.TryGetValue(l.Key, out var before) || ReferenceEquals(before, l))
            {
                continue;
            }

            var i = Species(l.Key);
            if (!before.LevelUp.SequenceEqual(l.LevelUp))
            {
                var bytes = l.LevelUp.OrderBy(m => m.Level).SelectMany(m => BitConverter.GetBytes((ushort)((m.Level << 9) | Move(m.Move)))).Concat(new byte[] { 0xFF, 0xFF }).ToArray();
                var at = rom.Offset(t.LevelUpPointers + (4 * i));
                if (bytes.Length > (before.LevelUp.Count * 2) + 2 || at < 0)
                {
                    at = rom.FindFree(bytes.Length);
                    if (at < 0)
                    {
                        throw new InvalidOperationException("The ROM has no free space left for the longer level-up move lists.");
                    }

                    rom.WritePointer(t.LevelUpPointers + (4 * i), at);
                }

                bytes.CopyTo(rom.Bytes, at);
            }

            if (!before.Teachable.SequenceEqual(l.Teachable))
            {
                ulong bits = 0;
                var original = (ulong)rom.U32(t.TmCompatibility + (8 * i)) | ((ulong)rom.U32(t.TmCompatibility + (8 * i) + 4) << 32);
                for (var b = 0; b < s.TmMoves.Count; b++)
                {
                    if (l.Teachable.Contains(s.TmMoves[b]))
                    {
                        bits |= 1UL << b;
                    }
                }

                bits |= original & ~((1UL << 58) - 1); // keep the unused high bits as they were
                rom.W32(t.TmCompatibility + (8 * i), (uint)bits);
                rom.W32(t.TmCompatibility + (8 * i) + 4, (uint)(bits >> 32));
            }
        }

        // ---- evolutions ----
        foreach (var set in data.Evolutions)
        {
            if (!s.Evolutions.TryGetValue(set.FromSpecies, out var before) || ReferenceEquals(before, set))
            {
                continue;
            }

            var i = Species(set.FromSpecies);
            for (var k = 0; k < Gen3Tables.EvolutionsPerSpecies; k++)
            {
                var at = t.Evolutions + (40 * i) + (8 * k);
                var e = k < set.Evolutions.Count ? set.Evolutions[k] : null;
                var native = e is null ? null : FromEvolution(e, key => Item(key));
                rom.W16(at, native?.Method ?? 0);
                rom.W16(at + 2, native?.Param ?? 0);
                rom.W16(at + 4, native is null ? 0 : Species(e!.TargetSpecies));
                rom.W16(at + 6, 0);
            }
        }

        // ---- trainers ----
        foreach (var tr in data.Trainers)
        {
            if (!s.Trainers.TryGetValue(tr.Key, out var before) || ReferenceEquals(before.Trainer, tr))
            {
                continue;
            }

            var at = t.Trainers + (Gen3Tables.TrainerSize * before.Index);
            rom.W8(at + 1, s.ClassIndex.GetValueOrDefault(tr.TrainerClass));
            if (tr.Pic.StartsWith("TRAINER_PIC_", StringComparison.Ordinal) && int.TryParse(tr.Pic[12..], CultureInfo.InvariantCulture, out var pic))
            {
                rom.W8(at + 3, pic);
            }

            var music = tr.Music.StartsWith("TRAINER_ENCOUNTER_MUSIC_", StringComparison.Ordinal) && int.TryParse(tr.Music[24..], CultureInfo.InvariantCulture, out var mu) ? mu : rom.U8(at + 2) & 0x7F;
            rom.W8(at + 2, (music & 0x7F) | (tr.Gender == "Female" ? 0x80 : 0));
            Text(at + 4, 12, tr.Name);
            for (var k = 0; k < 4; k++)
            {
                rom.W16(at + 16 + (2 * k), k < tr.Items.Count ? Item(tr.Items[k]) : 0);
            }

            rom.W8(at + 24, tr.Battle == BattleType.Double ? 1 : 0);
            uint ai = 0;
            foreach (var f in tr.AiFlags)
            {
                var b = data.Catalog.AiFlags.FindIndex(x => x.Key == f);
                if (b is >= 0 and < 32)
                {
                    ai |= 1u << b;
                }
            }

            rom.W32(at + 28, ai);
            if (!SameParty(before.Trainer.Party, tr.Party))
            {
                WriteParty(rom, at, tr.Party, Species, Move, Item);
            }
        }

        // ---- wild Pokémon ----
        foreach (var e in data.Encounters)
        {
            if (!s.Encounters.TryGetValue(e.Key, out var before) || ReferenceEquals(before.Table, e))
            {
                continue;
            }

            foreach (var (type, slots, ptr) in Gen3Reader.Methods)
            {
                var method = e.Methods.FirstOrDefault(m => m.Type == type);
                var info = rom.Offset(before.Header + ptr);
                var mons = info < 0 ? -1 : rom.Offset(info + 4);
                if (method is null || mons < 0)
                {
                    continue;
                }

                rom.W8(info, method.Rate);
                for (var k = 0; k < Math.Min(slots, method.Slots.Count); k++)
                {
                    rom.W8(mons + (4 * k), method.Slots[k].MinLevel);
                    rom.W8(mons + (4 * k) + 1, method.Slots[k].MaxLevel);
                    rom.W16(mons + (4 * k) + 2, Species(method.Slots[k].Species));
                }
            }
        }

        // ---- items ----
        foreach (var it in data.Items)
        {
            if (!s.Items.TryGetValue(it.Key, out var before) || ReferenceEquals(before, it))
            {
                continue;
            }

            var at = t.Items + (Gen3Tables.ItemSize * s.ItemIndex[it.Key]);
            rom.W16(at + 16, it.Price);
            if (it.Name != before.Name)
            {
                Text(at, 14, it.Name);
            }
        }
    }

    private static bool SameParty(IReadOnlyList<TrainerPokemon> a, IReadOnlyList<TrainerPokemon> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First.Equivalent(p.Second));

    /// <summary>Picks the smallest format that holds the team (moves / held items), in place when it fits.</summary>
    private static void WriteParty(RomImage rom, int trainer, IReadOnlyList<TrainerPokemon> party, Func<string, int> species, Func<string, int> move, Func<string?, int> item)
    {
        var custom = party.Any(p => p.Moves.Count > 0);
        var held = party.Any(p => p.HeldItem is not null && p.HeldItem != "ITEM_NONE");
        var stride = custom ? 16 : 8;
        var bytes = new byte[stride * party.Count];
        for (var k = 0; k < party.Count; k++)
        {
            var p = party[k];
            var o = stride * k;
            var iv = p.Ivs is { } ivs ? (int)Math.Round((ivs.Hp + ivs.Attack + ivs.Defense + ivs.SpAttack + ivs.SpDefense + ivs.Speed) / 6.0 * 255 / 31) : 0;
            BitConverter.TryWriteBytes(bytes.AsSpan(o), (ushort)Math.Clamp(iv, 0, 255));
            bytes[o + 2] = (byte)Math.Clamp(p.Level, 1, 100);
            BitConverter.TryWriteBytes(bytes.AsSpan(o + 4), (ushort)species(p.Species));
            if (held)
            {
                BitConverter.TryWriteBytes(bytes.AsSpan(o + 6), (ushort)item(p.HeldItem));
            }

            if (custom)
            {
                for (var m = 0; m < 4; m++)
                {
                    BitConverter.TryWriteBytes(bytes.AsSpan(o + (held ? 8 : 6) + (2 * m)), (ushort)(m < p.Moves.Count ? move(p.Moves[m]) : 0));
                }
            }
        }

        var oldFlags = rom.U8(trainer);
        var oldStride = (oldFlags & 1) != 0 ? 16 : 8;
        var oldBytes = oldStride * rom.U8(trainer + 32);
        var at = rom.Offset(trainer + 36);
        if (bytes.Length > oldBytes || at < 0)
        {
            at = rom.FindFree(bytes.Length);
            if (at < 0)
            {
                throw new InvalidOperationException("The ROM has no free space left for the bigger trainer teams.");
            }

            rom.WritePointer(trainer + 36, at);
        }

        bytes.CopyTo(rom.Bytes, at);
        rom.W8(trainer, (custom ? 1 : 0) | (held ? 2 : 0));
        rom.W8(trainer + 32, party.Count);
    }
}
