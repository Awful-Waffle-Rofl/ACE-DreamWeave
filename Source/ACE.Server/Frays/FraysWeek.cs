using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;

namespace ACE.Server.Frays
{
    public enum FraysAffix
    {
        None,
        // shift: changes the best build
        HeavyThread, BrittleDream, UnspooledSwarm, TangledKings, FrayedMasses,
        // mechanic shifts: on only from heat_shift_first (Heat 5); FraysHeatShifts runs them
        Raging,
        // the Threads gem modifiers (numbers, on at every Heat) and UCM-safe affixes from other games
        Savage, Precise, Stalwart, Swift, Teeming, Champions, Enraged, Guarded, Fortified, Tyrannical,
        // mechanics from other games, on from heat_shift_first like the four above
        Grievous, Venomous, Vampiric, ManaBurn, Linked, Quaking,
    }

    public enum SnarlPact { Off, Normal }   // no Hard Snarl Pact

    /// <summary>
    /// The Frays' week: its number (counted from the weekly reset, a setting), its one Shift and its creature families.
    /// Every campfire gets the same Shift. Admins can pin it with a setting.
    /// One weekly affix, no Gifts, and no Shift pays more: Shifts only make the week harder.
    /// </summary>
    public static class FraysWeek
    {
        public static readonly FraysAffix[] Shifts =
        {
            FraysAffix.HeavyThread,
            FraysAffix.BrittleDream, FraysAffix.UnspooledSwarm, FraysAffix.TangledKings, FraysAffix.FrayedMasses,
            FraysAffix.Raging,
            FraysAffix.Savage, FraysAffix.Precise, FraysAffix.Stalwart, FraysAffix.Swift,
            FraysAffix.Teeming, FraysAffix.Champions, FraysAffix.Enraged, FraysAffix.Guarded, FraysAffix.Fortified, FraysAffix.Tyrannical,
            FraysAffix.Grievous, FraysAffix.Venomous, FraysAffix.Vampiric, FraysAffix.ManaBurn, FraysAffix.Linked, FraysAffix.Quaking,
        };

        public static readonly DamageType[] Elements = { DamageType.Fire, DamageType.Cold, DamageType.Electric, DamageType.Acid };
        public static readonly DamageType[] Physical = { DamageType.Slash, DamageType.Pierce, DamageType.Bludgeon };

        /// <summary>The week number at <paramref name="utc"/>: whole weeks since a reset on the given day and hour (UTC).</summary>
        public static int WeekAt(DateTime utc, int resetDay, int resetHour)
        {
            // 2024-01-01 was a Monday (DayOfWeek 1).
            var epoch = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(((resetDay % 7) - 1 + 7) % 7).AddHours(resetHour);
            var days = (utc - epoch).TotalDays;
            return (int)Math.Floor(days / 7);
        }

        public static int Current => WeekAt(DateTime.UtcNow, FraysSettings.Int(FraysSettings.WeekResetDay), FraysSettings.Int(FraysSettings.WeekResetHour))
            + FraysSettings.Int(FraysSettings.WeekOffset);

        /// <summary>When the current week ends (UTC).</summary>
        public static DateTime NextReset
        {
            get
            {
                var day = FraysSettings.Int(FraysSettings.WeekResetDay);
                var hour = FraysSettings.Int(FraysSettings.WeekResetHour);
                var now = DateTime.UtcNow;
                var w = WeekAt(now, day, hour);
                var epoch = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(((day % 7) - 1 + 7) % 7).AddHours(hour);
                return epoch.AddDays(7 * (w + 1));
            }
        }

        public static FraysAffix ShiftFor(int week) => Shifts[Mod(week, Shifts.Length)];

        public static FraysAffix Shift => Pinned(FraysSettings.AffixShift, Shifts) ?? ShiftFor(Current);

        private static FraysAffix? Pinned(string key, FraysAffix[] allowed)
        {
            var text = FraysSettings.Text(key);
            return Enum.TryParse<FraysAffix>(text, true, out var a) && allowed.Contains(a) ? a : null;
        }

        private static int Mod(int a, int n) => ((a % n) + n) % n;

        public static string Name(FraysAffix a) => a switch
        {
            FraysAffix.HeavyThread => "Heavy Thread",
            FraysAffix.BrittleDream => "Brittle Dream",
            FraysAffix.UnspooledSwarm => "Unspooled Swarm",
            FraysAffix.TangledKings => "Tangled Kings",
            FraysAffix.FrayedMasses => "Frayed Masses",
            FraysAffix.Raging => "Raging",
            FraysAffix.Savage => "Savage",
            FraysAffix.Precise => "Precise",
            FraysAffix.Stalwart => "Stalwart",
            FraysAffix.Swift => "Swift",
            FraysAffix.Teeming => "Teeming",
            FraysAffix.Champions => "Champions",
            FraysAffix.Enraged => "Enraged Snarls",
            FraysAffix.Guarded => "Guarded Snarls",
            FraysAffix.Fortified => "Fortified",
            FraysAffix.Tyrannical => "Tyrannical",
            FraysAffix.Grievous => "Grievous",
            FraysAffix.Venomous => "Venomous",
            FraysAffix.Vampiric => "Vampiric",
            FraysAffix.ManaBurn => "Mana Burn",
            FraysAffix.Linked => "Linked",
            FraysAffix.Quaking => "Quaking",
            _ => "none",
        };

        public static string Effect(FraysAffix a) => a switch
        {
            FraysAffix.HeavyThread => "+40% creature health",
            FraysAffix.BrittleDream => "-30% creature health, +15% creature damage",
            FraysAffix.UnspooledSwarm => "packs 1 bigger",
            FraysAffix.TangledKings => "packs 1 smaller, Gilded creatures 50% tougher",
            FraysAffix.FrayedMasses => "pack creatures 20% weaker, Gilded creatures 40% tougher",
            _ when FraysHeatShifts.All.Contains(a) => $"{FraysHeatShifts.Effect(a)}, from Heat {FraysSettings.Int(FraysSettings.HeatShiftFirst)}",
            _ when FraysHeatShifts.Numbers.Contains(a) => FraysHeatShifts.Effect(a),
            _ => "",
        };

        public static string ElementName(DamageType d) => d switch
        {
            DamageType.Fire => "Fire",
            DamageType.Cold => "Frost",
            DamageType.Electric => "Lightning",
            DamageType.Acid => "Acid",
            DamageType.Slash => "Slashing",
            DamageType.Pierce => "Piercing",
            DamageType.Bludgeon => "Bludgeoning",
            _ => d.ToString(),
        };

        public static string Summary() => $"This week's affix: {Name(Shift)} ({Effect(Shift)}). Resets {NextReset:ddd HH:mm} UTC.";

    }
}
