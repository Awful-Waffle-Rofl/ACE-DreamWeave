using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ACE.Server.Frays
{
    /// <summary>
    /// The Heat curves. Heat makes creatures harder only by adding levels (heat_curve_levels): the added levels go
    /// through Threads' own stat and health curves (<see cref="FraysLevelScale"/>), so Frays creatures are always
    /// priced the way Threads prices that level. heat_curve_income is the explicit XP/lum bonus Heat adds to the pay.
    /// heat_curve_health and heat_curve_damage are optional extra multipliers on top (empty = none, the default).
    /// Each is a "heat:value,heat:value" table, straight lines between its points and the last stretch carried on.
    /// </summary>
    public static class FraysCurve
    {
        /// <summary>A value from a "x:y,x:y" table at <paramref name="x"/>, or null when the table is empty or unreadable.</summary>
        public static double? At(string table, double x)
        {
            var pts = new List<(double X, double Y)>();

            foreach (var part in (table ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split(':');

                if (kv.Length == 2
                    && double.TryParse(kv[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var px)
                    && double.TryParse(kv[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var py))
                    pts.Add((px, py));
            }

            if (pts.Count == 0)
                return null;

            pts = pts.OrderBy(p => p.X).ToList();

            if (pts.Count == 1 || x <= pts[0].X)
                return pts[0].Y;

            for (var i = 1; i < pts.Count; i++)
            {
                if (x <= pts[i].X)
                    return Line(pts[i - 1], pts[i], x);
            }

            return Line(pts[^2], pts[^1], x);
        }

        private static double Line((double X, double Y) a, (double X, double Y) b, double x) =>
            b.X == a.X ? b.Y : a.Y + (x - a.X) / (b.X - a.X) * (b.Y - a.Y);

        /// <summary>Creature levels added above the camper's level at <paramref name="heat"/>.</summary>
        public static double LevelsAdded(int heat) =>
            At(FraysSettings.Text(FraysSettings.HeatCurveLevels), heat)
            ?? Math.Max(0, heat) * FraysSettings.Number(FraysSettings.HeatLevelsPerHeat);

        /// <summary>The optional extra health multiplier at <paramref name="heat"/> (heat_curve_health; empty = 1).</summary>
        public static double Health(int heat) =>
            Math.Max(0.05, At(FraysSettings.Text(FraysSettings.HeatCurveHealth), heat) ?? 1.0);

        /// <summary>The optional extra damage multiplier at <paramref name="heat"/> (heat_curve_damage; empty = 1).</summary>
        public static double Damage(int heat) =>
            Math.Max(0.05, At(FraysSettings.Text(FraysSettings.HeatCurveDamage), heat) ?? 1.0);

        /// <summary>A creature weapon hit as a share of the camper's max health at <paramref name="heat"/> (heat_curve_hit_share); null = not set.</summary>
        public static double? HitShare(int heat) =>
            At(FraysSettings.Text(FraysSettings.HeatCurveHitShare), heat) is double v && v > 0 ? v : null;

        /// <summary>The largest share of the camper's max health one creature hit can take at <paramref name="heat"/> (heat_curve_hit_cap); null = no cap.</summary>
        public static double? HitCap(int heat) =>
            At(FraysSettings.Text(FraysSettings.HeatCurveHitCap), heat) is double v && v > 0 ? v : null;

        /// <summary>The once-a-camp line for a new Heat: "Heat 13: the creatures grow fiercer (health x2.5, damage x1.5)."</summary>
        public static string FierceLine(int heat)
        {
            var hp = Health(heat);
            var dmg = Damage(heat);
            var word = heat >= 18 ? "grow monstrous" : heat >= 12 ? "grow fiercer" : heat >= 6 ? "grow bolder" : "stir";
            var extra = hp != 1 || dmg != 1 ? $" (health x{hp.ToString("0.0#", CultureInfo.InvariantCulture)}, damage x{dmg.ToString("0.0#", CultureInfo.InvariantCulture)})" : "";
            var upkeep = FraysRules.HeatUpkeep(heat);
            var burn = upkeep > 0 ? $" This Heat burns down {upkeep.ToString("0.#", CultureInfo.InvariantCulture)} points a minute: kill fast enough to stay." : "";
            return $"Heat {heat}: the creatures {word}{extra}.{burn}";
        }

        /// <summary>The XP and lum multiplier at <paramref name="heat"/>: 1 + the income curve's bonus.</summary>
        public static double Income(int heat)
        {
            var bonus = At(FraysSettings.Text(FraysSettings.HeatCurveIncome), heat);

            return bonus is double b
                ? 1 + Math.Max(0, b)
                : 1.0;   // no heat_curve_income row: no Heat bonus
        }

        /// <summary>
        /// The XP and lum multiplier at <paramref name="heat"/> for a camper of <paramref name="camperLevel"/>. With
        /// heat_pay_curve "threads" (the default, so Heat scales on the Threads curve):
        /// Threads' XP ladder at the creature level the Heat sets, over the ladder at the camper's own level, so Heat
        /// pays what Threads pays for creatures that hard. Never below 1. "table" (or no ladder loaded) is
        /// heat_curve_income, as before.
        /// </summary>
        public static double Income(int heat, int camperLevel)
        {
            if (!string.Equals(FraysSettings.Text(FraysSettings.HeatPayCurve), "table", StringComparison.OrdinalIgnoreCase))
            {
                var ladder = ThreadDungeons.ThreadDungeonManager.Store?.XpLadder;
                var ratio = LadderRatio(ladder, camperLevel, FraysLevelScale.TargetLevel(camperLevel, heat));

                if (ratio is double r)
                    return r;
            }

            return Income(heat);
        }

        /// <summary>Threads' ladder XP at <paramref name="toLevel"/> over <paramref name="fromLevel"/> (at least 1), or null with no ladder.</summary>
        public static double? LadderRatio(IReadOnlyList<ThreadDungeons.Defs.XpLadderRungDef> ladder, int fromLevel, int toLevel)
        {
            if (ladder == null || ladder.Count == 0)
                return null;

            var from = ThreadDungeons.DungeonRewardMath.LadderXp(ladder, fromLevel);
            var to = ThreadDungeons.DungeonRewardMath.LadderXp(ladder, toLevel);

            return from <= 0 ? null : Math.Max(1.0, (double)to / from);
        }

        /// <summary>The whole curve, one line per Heat level up to the cap, for /frays admin curve.</summary>
        public static string Describe(int camperLevel)
        {
            var cap = FraysSettings.Int(FraysSettings.HeatCap) + FraysPerks.All.First(p => p.Key == "higherflame").MaxRank;
            var lines = new List<string> { $"Heat curve (for a level {camperLevel} camper): Heat - creature level - standard health (Threads curve) - income" };

            for (var h = 0; h <= cap; h++)
            {
                var level = FraysLevelScale.TargetLevel(camperLevel, h);
                var hp = FraysLevelScale.CurveHealth(FraysLevelScale.HealthLevel(level, camperLevel, h, FraysSettings.Int(FraysSettings.HealthCamperLevelCap))) * Health(h);
                var extras = (Health(h) != 1 ? $", extra health x{Health(h):0.00}" : "") + (Damage(h) != 1 ? $", extra damage x{Damage(h):0.00}" : "");
                lines.Add($"{h,2}: level {level} (+{level - camperLevel}), health {hp:N0}{extras}, income +{(Income(h, camperLevel) - 1) * 100:0}%");
            }

            lines.Add("Damage and defense follow Threads' stat curve at that level. Tables: frays_heat_curve_levels and _income (optional extras: _health, _damage).");
            return string.Join("\n", lines);
        }
    }
}
