using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.WorldObjects;

namespace ACE.Server.Frays
{
    /// <summary>
    /// The run score and the weekly climb score.
    /// </summary>
    public static class FraysScore
    {
        /// <summary>Pure: a camp's run score. Peak Heat counts most, then Snarls and kills; deaths cost.</summary>
        public static long RunScore(int peak, int kills, int snarls, int deaths) =>
            ClimbScore(peak, kills, snarls, 0, deaths, 0, 10, 1, 50, 0, 25);

        /// <summary>
        /// Pure: the climb score, Torghast style: the peak Heat (times itself), the time to each new Heat
        /// (<paramref name="timeBonus"/>, from <see cref="TimeBonus"/>), Snarls and Gilded killed and every kill, less deaths. Never below 0.
        /// </summary>
        public static long ClimbScore(int peak, int kills, int snarls, int gilded, int deaths, double timeBonus,
            double heatPts, double killPts, double snarlPts, double gildedPts, double deathPts) =>
            (long)Math.Max(0, Math.Round(heatPts * peak * peak + killPts * kills + snarlPts * snarls + gildedPts * gilded + timeBonus - deathPts * deaths));

        /// <summary>
        /// Pure: the time part of the climb score. Each new Heat step pays <paramref name="points"/> for an instant step, half at
        /// <paramref name="parMinutes"/>, nothing at twice par or slower.
        /// </summary>
        public static double TimeBonus(IEnumerable<double> stepMinutes, double points, double parMinutes) =>
            parMinutes <= 0 ? 0 : stepMinutes.Sum(m => points * Math.Clamp(1 - Math.Max(0, m) / (2 * parMinutes), 0, 1));

        /// <summary>
        /// Pure: the minutes each Heat step took, from the minute each Heat was first reached in the camp. Heats at or below
        /// <paramref name="startLevel"/> came free and have no step.
        /// </summary>
        public static List<double> StepMinutes(IReadOnlyDictionary<int, double> reachedMinute, int startLevel, int peak)
        {
            var steps = new List<double>();
            var last = 0.0;

            for (var h = startLevel + 1; h <= peak; h++)
            {
                if (!reachedMinute.TryGetValue(h, out var at))
                    continue;

                steps.Add(Math.Max(0, at - last));
                last = at;
            }

            return steps;
        }

        /// <summary>Pure: 0 none, 1 Bronze, 2 Silver, 3 Gold for a score.</summary>
        public static int ScoreTier(long score, double bronze, double silver, double gold) =>
            gold > 0 && score >= gold ? 3 : silver > 0 && score >= silver ? 2 : bronze > 0 && score >= bronze ? 1 : 0;

        public static string TierName(int tier) => tier switch { 3 => "Gold", 2 => "Silver", 1 => "Bronze", _ => "none" };

        /// <summary>Pure: the Vault Keys still owed this week moving from tier <paramref name="paid"/> to <paramref name="tier"/> (each tier's keys are its total).</summary>
        public static int KeysOwed(int paid, int tier, int bronzeKeys, int silverKeys, int goldKeys)
        {
            int Keys(int t) => t switch { 3 => goldKeys, 2 => silverKeys, 1 => bronzeKeys, _ => 0 };
            return tier > paid ? Math.Max(0, Keys(tier) - Keys(paid)) : 0;
        }
    }

    /// <summary>
    /// Weekly affixes: every Gilded creature of the week carries the same one, a test of the gear brought rather
    /// than of reactions (play is often unattended, so the challenge is preparation). It changes the Gilded creature's resistances
    /// (lower = less damage taken).
    /// </summary>
    public static class FraysWards
    {
        public sealed class Affix
        {
            public string Name;
            public string Text;
            public PropertyFloat Resist;
            public PropertyFloat Weak;
            public Action<Creature> Apply;
        }

        private static void Scale(Creature c, PropertyFloat p, double mult) => c.SetProperty(p, (c.GetProperty(p) ?? 1.0) * mult);

        /// <summary>
        /// One damage type resisted, its opposite a weakness (in place of
        /// whole groups). Fire and cold, acid and lightning pair up; the three weapon types run in a ring. So the week asks
        /// for one imbue or wand swap, not a whole new build.
        /// </summary>
        public const double ResistMult = 0.5, WeakMult = 1.5;

        private static Affix Single(string name, string resists, PropertyFloat resist, string weak, PropertyFloat weakProp) => new Affix
        {
            Name = name,
            Text = $"resist {resists} (take half), weak to {weak} (take 150%)",
            Resist = resist,
            Weak = weakProp,
            Apply = c => { Scale(c, resist, ResistMult); Scale(c, weakProp, WeakMult); },
        };

        public static readonly IReadOnlyList<Affix> All = new List<Affix>
        {
            Single("Fireward", "fire", PropertyFloat.ResistFire, "cold", PropertyFloat.ResistCold),
            Single("Bladeward", "slashing", PropertyFloat.ResistSlash, "piercing", PropertyFloat.ResistPierce),
            Single("Acidward", "acid", PropertyFloat.ResistAcid, "lightning", PropertyFloat.ResistElectric),
            Single("Spikeward", "piercing", PropertyFloat.ResistPierce, "bludgeoning", PropertyFloat.ResistBludgeon),
            Single("Frostward", "cold", PropertyFloat.ResistCold, "fire", PropertyFloat.ResistFire),
            Single("Maceward", "bludgeoning", PropertyFloat.ResistBludgeon, "slashing", PropertyFloat.ResistSlash),
            Single("Stormward", "lightning", PropertyFloat.ResistElectric, "acid", PropertyFloat.ResistAcid),
        };

        /// <summary>This week's affix.</summary>
        public static Affix ForWeek(int week) => All[((week % All.Count) + All.Count) % All.Count];

        public static string WeekText(int week) => $"{ForWeek(week).Name} Gilded creatures: {ForWeek(week).Text}";

        /// <summary>Puts this week's ward on a Gilded creature (its resistances and a name prefix).</summary>
        public static void ApplyTo(Creature gilded)
        {
            if (gilded == null)
                return;

            var a = ForWeek(FraysWeek.Current);
            a.Apply(gilded);
            gilded.Name = $"{a.Name} {gilded.Name}";
        }
    }
}
