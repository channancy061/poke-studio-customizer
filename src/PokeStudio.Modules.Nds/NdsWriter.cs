using System.Buffers.Binary;
using System.Globalization;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;
using PokeStudio.Core.Validation;

namespace PokeStudio.Modules.Nds;

/// <summary>Writes changed data back into the DS game's archives (rebuilt only when something in them changed).</summary>
internal static class NdsWriter
{
    public static List<ValidationIssue> Check(NdsState s, GameData data, GameLimits limits)
    {
        var issues = new List<ValidationIssue>();
        void Error(string area, string subject, string message, string? key = null) =>
            issues.Add(new ValidationIssue(IssueSeverity.Error, area, subject, message) { TargetKey = key });
        bool Writable(string text) => s.Game.Generation == 5 || Gen4Text.CanWrite(text);

        foreach (var sp in data.Species)
        {
            if (sp.Name.Length > limits.SpeciesNameLength || !Writable(sp.Name))
            {
                Error("Pokémon", sp.Name, $"Names can be up to {limits.SpeciesNameLength} letters this game can show.", sp.Key);
            }
        }

        foreach (var set in data.Evolutions)
        {
            if (set.Evolutions.Count > s.Game.EvolutionsPerSpecies)
            {
                Error("Evolutions", set.FromSpecies, "These games allow up to 7 evolutions per Pokémon.", set.FromSpecies);
            }

            foreach (var e in set.Evolutions.Where(e => FromEvolution(s.Game.Generation, e, _ => 0, _ => 0, _ => 0) is null))
            {
                Error("Evolutions", set.FromSpecies, $"\"{e.Method} {e.Parameter}\" with these conditions can't be done in this game.", set.FromSpecies);
            }
        }

        var tm = s.TmMoves.ToHashSet(StringComparer.Ordinal);
        foreach (var l in data.Learnsets)
        {
            if (tm.Count > 0 && l.Teachable.Any(m => !tm.Contains(m)))
            {
                Error("Learnsets", l.Key, "Only TM and HM moves can be taught in this game (ROM mode).", l.Key);
            }
        }

        foreach (var t in data.Trainers)
        {
            if (t.Party.Count is 0 or > 6)
            {
                Error("Trainers", t.Name, "A trainer needs 1 to 6 Pokémon.", t.Key);
            }

            if (t.HasAdaptiveSlot)
            {
                issues.Add(Core.ChampionLab.AdaptiveSlots.NotSupportedIssue(t));
            }
        }

        return issues;
    }

    /// <summary>The app's evolution → this generation's (method, parameter), or null when the game can't do it.</summary>
    internal static (int Method, int Param)? FromEvolution(int gen, Evolution e, Func<string, int> item, Func<string, int> move, Func<string, int> species)
    {
        var kinds = string.Join("+", e.Conditions.Select(c => c.Kind).Order(StringComparer.Ordinal));
        string Cond(string kind) => e.Conditions.First(c => c.Kind == kind).Value;
        var time = e.Conditions.FirstOrDefault(c => c.Kind == "IF_TIME")?.Value;
        var male = e.Conditions.FirstOrDefault(c => c.Kind == "IF_GENDER")?.Value.Contains("FEMALE", StringComparison.Ordinal) != true;
        int Level() => int.TryParse(e.Parameter, CultureInfo.InvariantCulture, out var l) ? l : -1;
        (int, int)? result = (e.Method, kinds) switch
        {
            ("EVO_LEVEL", "") when Level() is > 0 and <= 100 => (4, Level()),
            ("EVO_LEVEL", "IF_MIN_FRIENDSHIP") => (1, 0),
            ("EVO_LEVEL", "IF_MIN_FRIENDSHIP+IF_TIME") => (time == "TIME_NIGHT" ? 3 : 2, 0),
            ("EVO_TRADE", "") => (5, 0),
            ("EVO_TRADE", "IF_HOLD_ITEM") => (6, item(Cond("IF_HOLD_ITEM"))),
            ("EVO_TRADE", "IF_TRADE_PARTNER_SPECIES") when gen == 5 => (-7, species(Cond("IF_TRADE_PARTNER_SPECIES"))),
            ("EVO_ITEM", "") => (7, item(e.Parameter)),
            ("EVO_LEVEL", "IF_ATK_GT_DEF") when Level() > 0 => (8, Level()),
            ("EVO_LEVEL", "IF_ATK_EQ_DEF") when Level() > 0 => (9, Level()),
            ("EVO_LEVEL", "IF_ATK_LT_DEF") when Level() > 0 => (10, Level()),
            ("EVO_LEVEL", "IF_SILCOON") when Level() > 0 => (11, Level()),
            ("EVO_LEVEL", "IF_CASCOON") when Level() > 0 => (12, Level()),
            ("EVO_LEVEL_NINJASK", "") when Level() > 0 => (13, Level()),
            ("EVO_LEVEL_SHEDINJA", "") when Level() > 0 => (14, Level()),
            ("EVO_LEVEL", "IF_MIN_BEAUTY") => (15, int.TryParse(Cond("IF_MIN_BEAUTY"), CultureInfo.InvariantCulture, out var b) ? b : 170),
            ("EVO_ITEM", "IF_GENDER") => (male ? 16 : 17, item(e.Parameter)),
            ("EVO_LEVEL", "IF_HOLD_ITEM+IF_TIME") => (time == "TIME_NIGHT" ? 19 : 18, item(Cond("IF_HOLD_ITEM"))),
            ("EVO_LEVEL", "IF_KNOWS_MOVE") => (20, move(Cond("IF_KNOWS_MOVE"))),
            ("EVO_LEVEL", "IF_SPECIES_IN_PARTY") => (21, species(Cond("IF_SPECIES_IN_PARTY"))),
            ("EVO_LEVEL", "IF_GENDER") when Level() > 0 => (male ? 22 : 23, Level()),
            ("EVO_LEVEL", "IF_IN_MAPSEC") => (Cond("IF_IN_MAPSEC") switch { "LOCATION_MAGNETIC_FIELD" => 24, "LOCATION_MOSS_ROCK" => 25, _ => 26 }, 0),
            _ => null,
        };
        if (result is not { } r)
        {
            return null;
        }

        // Gen 5 inserted "trade for a species" at 7; the later methods move up by one
        return gen == 5 ? r.Item1 == -7 ? (7, r.Item2) : r.Item1 >= 7 ? (r.Item1 + 1, r.Item2) : r : r.Item1 == -7 ? null : r;
    }

    public static void Write(NdsState s, GameData data)
    {
        var rom = s.Rom;
        var g = s.Game;
        var gen = g.Generation;
        var types = NdsConst.Types(gen);
        int Species(string key) => s.SpeciesIndex.GetValueOrDefault(key);
        int Move(string key) => s.MoveIndex.GetValueOrDefault(key);
        int Item(string? key) => key is null ? 0 : s.ItemIndex.GetValueOrDefault(key);
        int Type(string key) => Math.Max(0, Array.IndexOf(types, key.Replace("TYPE_", "", StringComparison.Ordinal)));
        void W16(byte[] b, int o, int v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o), (ushort)v);

        // ---- personal (species), learnsets, evolutions ----
        var personal = new Narc(rom.Read(g.Personal));
        var learn = new Narc(rom.Read(g.Learnsets));
        var evo = new Narc(rom.Read(g.Evolutions));
        bool personalChanged = false, learnChanged = false, evoChanged = false;
        var renamedSpecies = new Dictionary<int, string>();
        foreach (var sp in data.Species)
        {
            if (!s.Species.TryGetValue(sp.Key, out var before) || ReferenceEquals(before, sp))
            {
                continue;
            }

            var i = Species(sp.Key);
            var p = personal.Members[i];
            p[0] = (byte)sp.Stats.Hp;
            p[1] = (byte)sp.Stats.Attack;
            p[2] = (byte)sp.Stats.Defense;
            p[3] = (byte)sp.Stats.Speed;
            p[4] = (byte)sp.Stats.SpAttack;
            p[5] = (byte)sp.Stats.SpDefense;
            p[6] = (byte)Type(sp.Types.ElementAtOrDefault(0) ?? "TYPE_NORMAL");
            p[7] = (byte)Type(sp.Types.ElementAtOrDefault(1) ?? sp.Types.ElementAtOrDefault(0) ?? "TYPE_NORMAL");
            p[8] = (byte)sp.CatchRate;
            if (gen == 4)
            {
                p[9] = (byte)Math.Min(255, sp.ExpYield);
            }
            else
            {
                W16(p, 34, sp.ExpYield);
            }

            W16(p, 12, Item(sp.HeldItemCommon));
            W16(p, 14, Item(sp.HeldItemRare));
            var genderAt = gen == 4 ? 16 : 18;
            p[genderAt] = sp.Gender switch
            {
                { Genderless: true } => 255,
                { PercentFemale: 0 } => 0,
                { PercentFemale: 100 } => 254,
                { PercentFemale: { } f } => (byte)Math.Clamp(Math.Round(f * 256 / 100), 1, 253),
                _ => p[genderAt],
            };
            p[gen == 4 ? 19 : 21] = (byte)Math.Max(0, Array.IndexOf(NdsConst.Growth, sp.Growth.Replace("GROWTH_", "", StringComparison.Ordinal)));
            var eggs = sp.EggGroups.Select(e => Math.Max(0, Array.IndexOf(NdsConst.EggGroups, e.Replace("EGG_GROUP_", "", StringComparison.Ordinal)))).ToList();
            var eggAt = gen == 4 ? 20 : 22;
            p[eggAt] = (byte)eggs.ElementAtOrDefault(0);
            p[eggAt + 1] = (byte)(eggs.Count > 1 ? eggs[1] : eggs.ElementAtOrDefault(0));
            var abilityAt = gen == 4 ? 22 : 24;
            for (var k = 0; k < (gen == 4 ? 2 : 3); k++)
            {
                p[abilityAt + k] = (byte)s.AbilityIndex.GetValueOrDefault(sp.Abilities.ElementAtOrDefault(k) ?? "ABILITY_NONE");
            }

            personalChanged = true;
            if (sp.Name != before.Name)
            {
                renamedSpecies[i] = sp.Name;
            }
        }

        foreach (var l in data.Learnsets)
        {
            if (!s.Learnsets.TryGetValue(l.Key, out var before) || ReferenceEquals(before, l))
            {
                continue;
            }

            var i = Species(l.Key);
            if (!before.LevelUp.SequenceEqual(l.LevelUp))
            {
                var bytes = new List<byte>();
                foreach (var m in l.LevelUp.OrderBy(m => m.Level))
                {
                    if (gen == 4)
                    {
                        bytes.AddRange(BitConverter.GetBytes((ushort)((m.Level << 9) | Move(m.Move))));
                    }
                    else
                    {
                        bytes.AddRange(BitConverter.GetBytes((ushort)Move(m.Move)));
                        bytes.AddRange(BitConverter.GetBytes((ushort)m.Level));
                    }
                }

                bytes.AddRange(gen == 4 ? [0xFF, 0xFF] : [0xFF, 0xFF, 0xFF, 0xFF]);
                while (bytes.Count % 4 != 0)
                {
                    bytes.Add(0);
                }

                learn.Members[i] = [.. bytes];
                learnChanged = true;
            }

            if (!before.Teachable.SequenceEqual(l.Teachable) && s.TmMoves.Count > 0)
            {
                var p = personal.Members[i];
                for (var b = 0; b < s.TmMoves.Count; b++)
                {
                    var at = g.TmBitsOffset + (b / 8);
                    var mask = (byte)(1 << (b % 8));
                    p[at] = l.Teachable.Contains(s.TmMoves[b]) ? (byte)(p[at] | mask) : (byte)(p[at] & ~mask);
                }

                personalChanged = true;
            }
        }

        foreach (var set in data.Evolutions)
        {
            if (!s.Evolutions.TryGetValue(set.FromSpecies, out var before) || ReferenceEquals(before, set))
            {
                continue;
            }

            var e = evo.Members[Species(set.FromSpecies)];
            for (var k = 0; k < g.EvolutionsPerSpecies; k++)
            {
                var ev = k < set.Evolutions.Count ? set.Evolutions[k] : null;
                var native = ev is null ? null : FromEvolution(gen, ev, key => Item(key), Move, Species);
                W16(e, 6 * k, native?.Method ?? 0);
                W16(e, (6 * k) + 2, native?.Param ?? 0);
                W16(e, (6 * k) + 4, native is null ? 0 : Species(ev!.TargetSpecies));
            }

            evoChanged = true;
        }

        if (personalChanged)
        {
            rom.Write(g.Personal, personal.Build());
        }

        if (learnChanged)
        {
            rom.Write(g.Learnsets, learn.Build());
        }

        if (evoChanged)
        {
            rom.Write(g.Evolutions, evo.Build());
        }

        // ---- moves ----
        var moves = new Narc(rom.Read(g.Moves));
        var movesChanged = false;
        var renamedMoves = new Dictionary<int, string>();
        foreach (var mv in data.Moves)
        {
            if (!s.Moves.TryGetValue(mv.Key, out var before) || ReferenceEquals(before, mv))
            {
                continue;
            }

            var i = Move(mv.Key);
            var d = moves.Members[i];
            if (gen == 4)
            {
                d[2] = (byte)mv.Category;
                d[3] = (byte)mv.Power;
                d[4] = (byte)Type(mv.Type);
                d[5] = (byte)mv.Accuracy;
                d[6] = (byte)mv.Pp;
                d[10] = (byte)(sbyte)mv.Priority;
                d[11] = (byte)((d[11] & ~1) | (mv.MakesContact ? 1 : 0));
            }
            else
            {
                d[0] = (byte)Type(mv.Type);
                d[2] = mv.Category switch { MoveCategory.Physical => 1, MoveCategory.Special => 2, _ => 0 };
                d[3] = (byte)mv.Power;
                d[4] = (byte)mv.Accuracy;
                d[5] = (byte)mv.Pp;
                d[6] = (byte)(sbyte)mv.Priority;
                var flags = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(32));
                BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(32), mv.MakesContact ? flags | 1 : flags & ~1u);
            }

            movesChanged = true;
            if (mv.Name != before.Name)
            {
                renamedMoves[i] = mv.Name;
            }
        }

        if (movesChanged)
        {
            rom.Write(g.Moves, moves.Build());
        }

        // ---- items ----
        var items = new Narc(rom.Read(g.Items));
        var itemsChanged = false;
        var renamedItems = new Dictionary<int, string>();
        foreach (var it in data.Items)
        {
            if (!s.Items.TryGetValue(it.Key, out var before) || ReferenceEquals(before, it))
            {
                continue;
            }

            var i = Item(it.Key);
            if (it.Price != before.Price && i < items.Members.Count && items.Members[i].Length >= 2)
            {
                W16(items.Members[i], 0, gen == 5 ? it.Price / 10 : it.Price);
                itemsChanged = true;
            }

            if (it.Name != before.Name)
            {
                renamedItems[i] = it.Name;
            }
        }

        if (itemsChanged)
        {
            rom.Write(g.Items, items.Build());
        }

        // ---- trainers ----
        var trData = new Narc(rom.Read(g.TrainerData));
        var trPoke = new Narc(rom.Read(g.TrainerParties));
        var trainersChanged = false;
        var renamedTrainers = new Dictionary<int, string>();
        foreach (var tr in data.Trainers)
        {
            if (!s.Trainers.TryGetValue(tr.Key, out var before) || ReferenceEquals(before.Trainer, tr))
            {
                continue;
            }

            var i = before.Index;
            var d = trData.Members[i];
            d[1] = (byte)s.ClassIndex.GetValueOrDefault(tr.TrainerClass);
            for (var k = 0; k < 4; k++)
            {
                W16(d, 4 + (2 * k), k < tr.Items.Count ? Item(tr.Items[k]) : 0);
            }

            uint ai = 0;
            foreach (var f in tr.AiFlags)
            {
                var b = data.Catalog.AiFlags.FindIndex(x => x.Key == f);
                if (b is >= 0 and < 32)
                {
                    ai |= 1u << b;
                }
            }

            BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(12), ai);
            if (gen == 4)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(16), tr.Battle == BattleType.Double ? 1u : 0u);
            }
            else
            {
                d[2] = tr.Battle switch { BattleType.Double => 1, BattleType.Triple => 2, BattleType.Rotation => 3, _ => 0 };
            }

            if (!before.Trainer.Party.SequenceEqual(tr.Party, PartyComparer.Instance))
            {
                var custom = tr.Party.Any(p => p.Moves.Count > 0);
                var held = tr.Party.Any(p => p.HeldItem is not null && p.HeldItem != "ITEM_NONE");
                var flags = (custom ? 1 : 0) | (held ? 2 : 0);
                var stride = NdsReader.PartyStride(g, flags);
                var bytes = new byte[stride * tr.Party.Count];
                for (var k = 0; k < tr.Party.Count; k++)
                {
                    var p = tr.Party[k];
                    var o = stride * k;
                    var iv = p.Ivs is { } ivs ? (int)Math.Round((ivs.Hp + ivs.Attack + ivs.Defense + ivs.SpAttack + ivs.SpDefense + ivs.Speed) / 6.0 * 255 / 31) : 0;
                    bytes[o] = (byte)Math.Clamp(iv, 0, 255);
                    int at;
                    if (gen == 4)
                    {
                        W16(bytes, o + 2, Math.Clamp(p.Level, 1, 100));
                        W16(bytes, o + 4, Species(p.Species));
                        at = o + 6;
                    }
                    else
                    {
                        bytes[o + 2] = (byte)Math.Clamp(p.Level, 1, 100);
                        W16(bytes, o + 4, Species(p.Species));
                        at = o + 8;
                    }

                    if (held)
                    {
                        W16(bytes, at, Item(p.HeldItem));
                        at += 2;
                    }

                    if (custom)
                    {
                        for (var m = 0; m < 4; m++)
                        {
                            W16(bytes, at + (2 * m), m < p.Moves.Count ? Move(p.Moves[m]) : 0);
                        }
                    }
                }

                d[0] = (byte)flags;
                d[3] = (byte)tr.Party.Count;
                trPoke.Members[i] = bytes;
            }

            trainersChanged = true;
            if (tr.Name != before.Trainer.Name)
            {
                renamedTrainers[i] = tr.Name;
            }
        }

        if (trainersChanged)
        {
            rom.Write(g.TrainerData, trData.Build());
            rom.Write(g.TrainerParties, trPoke.Build());
        }

        // ---- wild Pokémon ----
        var enc = new Narc(rom.Read(g.Encounters));
        var encChanged = false;
        foreach (var e in data.Encounters)
        {
            if (!s.Encounters.TryGetValue(e.Key, out var before) || ReferenceEquals(before.Table, e))
            {
                continue;
            }

            var d = enc.Members[before.Index];
            foreach (var (type, slots, offset, size, format) in NdsReader.Layout(g.EncounterFormat))
            {
                var method = e.Methods.FirstOrDefault(m => m.Type == type);
                var old = before.Table.Methods.FirstOrDefault(m => m.Type == type);
                if (method is null || old is null)
                {
                    continue;
                }

                for (var k = 0; k < Math.Min(slots, method.Slots.Count); k++)
                {
                    var slot = method.Slots[k];
                    if (slot == old.Slots.ElementAtOrDefault(k))
                    {
                        continue;
                    }

                    WriteSlot(d, format, offset, size, k, slot, Species(old.Slots[k].Species), Species(slot.Species));
                    encChanged = true;
                }
            }
        }

        if (encChanged)
        {
            rom.Write(g.Encounters, enc.Build());
        }

        // ---- names ----
        if (renamedSpecies.Count + renamedMoves.Count + renamedItems.Count + renamedTrainers.Count > 0)
        {
            var text = new Narc(rom.Read(g.Text));
            void Rename(int member, Dictionary<int, string> names, bool upper)
            {
                if (member < 0 || names.Count == 0)
                {
                    return;
                }

                var raw = text.Members[member];
                ushort seed = 0;
                var list = gen == 4 ? Gen4Text.ReadFile(raw, out seed) : Gen5Text.ReadFile(raw);
                foreach (var (i, name) in names)
                {
                    if (i < list.Count)
                    {
                        list[i] = upper ? name.ToUpperInvariant() : name;
                    }
                }

                text.Members[member] = gen == 4 ? Gen4Text.WriteFile(list, seed) : Gen5Text.WriteFile(list, raw);
            }

            var t = s.Tables;
            Rename(t.SpeciesNames, renamedSpecies, false);
            Rename(t.MoveNames, renamedMoves, false);
            Rename(t.MoveNamesUpper, renamedMoves, true);
            Rename(t.ItemNames, renamedItems, false);
            Rename(t.ItemNamesUpper, renamedItems, true);
            Rename(t.TrainerNames, renamedTrainers, false);
            rom.Write(g.Text, text.Build());
        }
    }

    private static void WriteSlot(byte[] d, string format, int offset, int size, int k, EncounterSlot slot, int oldSpecies, int species)
    {
        var o = NdsReader.SlotOffset(format, offset, size, k);
        switch (format)
        {
            case "lvl32-sp32":
                BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(o), (uint)slot.MinLevel);
                BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(o + 4), (uint)species);
                break;
            case "max-min-sp32":
                d[o] = (byte)slot.MaxLevel;
                d[o + 1] = (byte)slot.MinLevel;
                BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(o + 4), (uint)species);
                break;
            case "min-max-sp16":
                d[o] = (byte)slot.MinLevel;
                d[o + 1] = (byte)slot.MaxLevel;
                BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(o + 2), (ushort)species);
                break;
            case "hgss-land":
                d[8 + k] = (byte)slot.MinLevel;
                // daytime always; morning and night too when they matched the old daytime Pokémon
                var oldDay = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(44 + (2 * k)));
                foreach (var at in new[] { 20, 44, 68 })
                {
                    if (at == 44 || BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(at + (2 * k))) == oldDay)
                    {
                        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(at + (2 * k)), (ushort)species);
                    }
                }

                break;
            default: // bw: every season that matched the old slot
                for (var season = 0; season * 232 < d.Length; season++)
                {
                    var so = (season * 232) + o;
                    if (season > 0 && (BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(so)) & 0x7FF) != oldSpecies)
                    {
                        continue;
                    }

                    var form = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(so)) & ~0x7FF;
                    BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(so), (ushort)(form | species));
                    d[so + 2] = (byte)slot.MinLevel;
                    d[so + 3] = (byte)slot.MaxLevel;
                }

                break;
        }
    }

    private sealed class PartyComparer : IEqualityComparer<TrainerPokemon>
    {
        public static readonly PartyComparer Instance = new();

        public bool Equals(TrainerPokemon? x, TrainerPokemon? y) => x is not null && y is not null && x.Equivalent(y);

        public int GetHashCode(TrainerPokemon obj) => obj.Species.GetHashCode(StringComparison.Ordinal);
    }
}
