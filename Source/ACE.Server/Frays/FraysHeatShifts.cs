using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.Frays
{
    /// <summary>
    /// The mechanic Shifts: Raging and the others below sit in the weekly Shift rotation with the
    /// others (FraysWeek.Shifts). In a week that has one, a camp's creatures carry it from heat_shift_first (Heat 5), so
    /// new players are not hit on their first runs. One Shift a week, never more.
    /// UCM-safe: nothing needs dodging or reacting, each only changes numbers you can see and heal through.
    /// - Raging: a creature below 30% health enrages and hits 25% harder.
    /// Six more mechanics (Grievous, Venomous, Vampiric, Mana Burn, Linked, Quaking) and the twelve
    /// number Shifts (Numbers: the Threads gem modifiers, Fortified, Tyrannical) live here too.
    /// </summary>
    public static class FraysHeatShifts
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>The mechanic Shifts: on only from heat_shift_first.</summary>
        public static readonly FraysAffix[] All =
        {
            FraysAffix.Raging,
            FraysAffix.Grievous, FraysAffix.Venomous, FraysAffix.Vampiric, FraysAffix.ManaBurn, FraysAffix.Linked, FraysAffix.Quaking,
        };

        /// <summary>
        /// The number Shifts (the Threads gem modifiers, WoW's Fortified and Tyrannical): on at every Heat,
        /// written onto each creature at spawn (OnSpawn). Unlike Threads, none of them pays more.
        /// </summary>
        public static readonly FraysAffix[] Numbers =
        {
            FraysAffix.Savage, FraysAffix.Precise, FraysAffix.Stalwart, FraysAffix.Swift,
            FraysAffix.Teeming, FraysAffix.Champions, FraysAffix.Enraged, FraysAffix.Guarded, FraysAffix.Fortified, FraysAffix.Tyrannical,
        };

        public const int SavageRating = 20;            // creature damage rating
        public const int PreciseRating = 20;           // creature crit rating
        public const int StalwartRating = 20;          // creature damage resist rating (about 17% less damage taken)
        public const double SwiftRun = 1.25;           // creature run skill
        public const double GrievousBelow = 0.60;      // milder: was 0.90
        public const double GrievousPerStack = 0.0025; // of max health, each tick (was 0.005)
        public const double VenomPerStack = 0.0025;    // of max health, each tick
        public const int VenomMaxStacks = 10;
        public const double VampiricShare = 0.30;      // of the damage a creature deals, healed to it
        public const double ManaBurnShare = 0.15;      // of the damage a creature deals, burned from your mana (milder: was 0.50)
        public const double LinkShare = 0.30;          // of the damage to a creature, moved to those near it
        public const double LinkRange = 8;
        public const double QuakeSeconds = 20;
        public const double QuakeShare = 0.05;         // of max health

        /// <summary>Pure: the health and damage a number Shift gives a creature (pack, Gilded or Snarl).</summary>
        public static (double Health, double Damage) SpawnMults(FraysAffix shift, bool gilded, bool snarl) => shift switch
        {
            FraysAffix.Fortified when !gilded && !snarl => (1.2, 1.3),
            FraysAffix.Tyrannical when gilded || snarl => (1.3, 1.15),
            FraysAffix.Guarded when snarl => (2.0, 1.0),
            FraysAffix.Enraged when snarl => (1.0, 1.4),
            _ => (1.0, 1.0),
        };

        /// <summary>Pure: the ratings a number Shift adds to every creature (damage, crit, damage resist).</summary>
        public static (int Damage, int Crit, int Resist) Ratings(FraysAffix shift) => shift switch
        {
            FraysAffix.Savage => (SavageRating, 0, 0),
            FraysAffix.Precise => (0, PreciseRating, 0),
            FraysAffix.Stalwart => (0, 0, StalwartRating),
            _ => (0, 0, 0),
        };

        public const int MaxStacks = 5;
        public const double StackSeconds = 6;       // a poison stack lasts this long after the last hit
        public const double StackTickSeconds = 2;   // bleed and poison tick
        public const double RageBelow = 0.30;
        public const double RageDamage = 1.25;

        /// <summary>Pure: the mechanic Shift on at <paramref name="heat"/> when the week's Shift is <paramref name="shift"/> (None: none).</summary>
        public static FraysAffix ActiveAt(FraysAffix shift, int heat, int first) =>
            All.Contains(shift) && heat >= first ? shift : FraysAffix.None;

        public static FraysAffix Active(int heat) =>
            !FraysSettings.Toggle(FraysSettings.HeatShiftsEnabled) ? FraysAffix.None
                : ActiveAt(FraysWeek.Shift, heat, FraysSettings.Int(FraysSettings.HeatShiftFirst));

        public static bool On(FraysAffix shift, int heat) => shift != FraysAffix.None && Active(heat) == shift;

        public static string Effect(FraysAffix s) => s switch
        {
            FraysAffix.Raging => "creatures below 30% health hit 25% harder",
            FraysAffix.Grievous => "below 60% health you bleed a little, more the longer you stay there (to 5 stacks)",
            FraysAffix.Venomous => "creature hits poison you (stacks to 10, gone 6 s after the last hit)",
            FraysAffix.Vampiric => "creatures heal for 30% of the damage they deal",
            FraysAffix.ManaBurn => "creature hits burn a little of your mana (15% of the damage dealt)",
            FraysAffix.Linked => "30% of the damage to a creature spreads to those within 8 m (it never kills them)",
            FraysAffix.Quaking => "every 20 s the ground shakes, hurting each camper (5% of their health)",
            FraysAffix.Savage => "creatures have +20 damage rating",
            FraysAffix.Precise => "creatures have +20 crit rating",
            FraysAffix.Stalwart => "creatures have +20 damage resist rating",
            FraysAffix.Swift => "creatures run 25% faster",
            FraysAffix.Teeming => "packs 2 bigger",
            FraysAffix.Champions => "Gilded creatures have twice the health",
            FraysAffix.Enraged => "Snarls deal 40% more damage",
            FraysAffix.Guarded => "Snarls have twice the health",
            FraysAffix.Fortified => "pack creatures have +20% health and +30% damage",
            FraysAffix.Tyrannical => "Gilded creatures and Snarls have +30% health and +15% damage",
            _ => "",
        };

        /// <summary>The line when a camp's Heat turns the week's mechanic Shift on (empty: not at this Heat).</summary>
        public static string OnLine(int heat)
        {
            var now = Active(heat);
            return now == FraysAffix.None || Active(heat - 1) == now ? "" : $"The week's affix wakes: {FraysWeek.Name(now)} - {Effect(now)}.";
        }

        // ---- the mechanics ----------------------------------------------------------------------------------------

        /// <summary>A fire creature's hit: Raging below 30% health. The multiplier on its damage.</summary>
        public static double DamageMult(Creature attacker, FraysCreatureTag tag)
        {
            var mult = tag.ShiftDamage;

            if (attacker.Health.MaxValue > 0 && (double)attacker.Health.Current / attacker.Health.MaxValue < RageBelow && On(FraysAffix.Raging, tag.Heat))
            {
                mult *= RageDamage;

                if (!tag.Raged)
                {
                    tag.Raged = true;
                    FraysManager.Effect(attacker, PlayScript.SpecialStateRed);
                }
            }

            return mult;
        }

        /// <summary>Each camp tick: Grievous's bleed, Venomous's poison and Quaking's shake on the camper.</summary>
        public static void Tick(FraysFire fire, FraysCamper c, Player p, double now)
        {
            if (c == null || p == null || p.IsDead)
                return;

            try
            {
                var on = Active(c.Level);

                if (on == FraysAffix.Grievous)
                {
                    if ((double)p.Health.Current / Math.Max(1, p.Health.MaxValue) >= GrievousBelow)
                        c.GrievousStacks = 0;
                    else if (now >= c.NextGrievousAt)
                    {
                        c.NextGrievousAt = now + StackTickSeconds;
                        c.GrievousStacks = Math.Min(MaxStacks, c.GrievousStacks + 1);
                        Hurt(fire, p, GrievousPerStack * c.GrievousStacks, DamageType.Slash, PlayScript.HealthDownRed);
                    }
                }

                if (c.VenomStacks > 0)
                {
                    if (on != FraysAffix.Venomous || now >= c.VenomUntil)
                        c.VenomStacks = 0;
                    else if (now >= c.NextVenomAt)
                    {
                        c.NextVenomAt = now + StackTickSeconds;
                        Hurt(fire, p, VenomPerStack * c.VenomStacks, DamageType.Acid, PlayScript.HealthDownRed);
                    }
                }

                if (on == FraysAffix.Quaking)
                {
                    if (c.NextQuakeAt <= 0)
                        c.NextQuakeAt = now + QuakeSeconds;
                    else if (now >= c.NextQuakeAt)
                    {
                        c.NextQuakeAt = now + QuakeSeconds;
                        Hurt(fire, p, QuakeShare, DamageType.Bludgeon, PlayScript.Explode);
                    }
                }
            }
            catch (Exception ex)
            {
                log.Error("[FRAYS] shift tick threw", ex);
            }
        }

        /// <summary>Damage from the nearest of the camp's creatures (none left: nothing happens), a share of max health.</summary>
        private static void Hurt(FraysFire fire, Player p, double share, DamageType type, PlayScript fx)
        {
            var from = fire.AliveSnapshot().Where(m => m.Location != null && p.Location != null).OrderBy(m => m.Location.DistanceTo(p.Location)).FirstOrDefault();

            if (from == null)
                return;

            p.TakeDamage(from, type, (float)Math.Max(1, p.Health.MaxValue * share), BodyPart.Chest);
            FraysManager.Effect(p, fx);
        }

        /// <summary>
        /// A new fire creature, after its health and level are set: the number Shifts. Health and damage multiply in
        /// (damage through tag.ShiftDamage, read by DamageMult); ratings and run speed are written
        /// the way Threads writes its gem modifiers (ThreadDungeonSpawner).
        /// </summary>
        public static void OnSpawn(Creature creature, FraysCreatureTag tag)
        {
            try
            {
                var shift = FraysWeek.Shift;

                if (!Numbers.Contains(shift) || creature == null || tag == null)
                    return;

                var (health, damage) = SpawnMults(shift, tag.Rich, tag.Snarl || tag.SnarlAdd);

                if (health != 1)
                {
                    FraysLevelScale.SetMaxHealth(creature, creature.Health.MaxValue * health);
                    creature.Health.Current = creature.Health.MaxValue;
                }

                tag.ShiftDamage *= damage;

                var (dr, cr, drr) = Ratings(shift);
                if (dr != 0) creature.DamageRating = (creature.DamageRating ?? 0) + dr;
                if (cr != 0) creature.CritRating = (creature.CritRating ?? 0) + cr;
                if (drr != 0) creature.DamageResistRating = (creature.DamageResistRating ?? 0) + drr;

                if (shift == FraysAffix.Swift)
                {
                    var run = creature.GetCreatureSkill(Skill.Run, false);

                    if (run != null)
                        run.InitLevel = (uint)Math.Clamp(Math.Round(run.InitLevel * SwiftRun), 0, uint.MaxValue);
                }
            }
            catch (Exception ex)
            {
                log.Error("[FRAYS] shift on spawn threw", ex);
            }
        }

        /// <summary>A fire creature's hit landed on a camper for <paramref name="damage"/>: Vampiric, Mana Burn, Venomous.</summary>
        public static void OnHitPlayer(Creature attacker, FraysCreatureTag tag, Player defender, float damage)
        {
            try
            {
                var on = Active(tag.Heat);

                if (on == FraysAffix.Vampiric && !attacker.IsDead && attacker.Health.Current < attacker.Health.MaxValue)
                    attacker.UpdateVitalDelta(attacker.Health, (int)Math.Max(1, Math.Round(damage * VampiricShare)));

                if (on == FraysAffix.ManaBurn && defender.Mana.Current > 0)
                    defender.UpdateVitalDelta(defender.Mana, -(int)Math.Min(defender.Mana.Current, Math.Max(1, Math.Round(damage * ManaBurnShare))));

                if (on == FraysAffix.Venomous && tag.Fire.CamperOf(defender) is FraysCamper c)
                {
                    var now = ACE.Common.Time.GetUnixTime();
                    c.VenomStacks = Math.Min(VenomMaxStacks, c.VenomStacks + 1);
                    c.VenomUntil = now + StackSeconds;

                    if (c.NextVenomAt < now)
                        c.NextVenomAt = now + StackTickSeconds;
                }
            }
            catch (Exception ex)
            {
                log.Error("[FRAYS] shift on hit threw", ex);
            }
        }

        /// <summary>
        /// Pure: Linked's split of <paramref name="damage"/> to a creature with <paramref name="near"/> others near it. The moved
        /// share comes only from damage the target could take (<paramref name="targetHealth"/>), never from overkill, so a
        /// Linked week never removes more health per hit than a plain week (pay-neutral).
        /// </summary>
        public static (double Kept, double EachOther) LinkSplit(double damage, int near, double targetHealth = double.MaxValue)
        {
            if (near <= 0 || damage <= 0)
                return (damage, 0);

            var moved = Math.Min(damage, Math.Max(0, targetHealth)) * LinkShare;
            return (damage - moved, moved / near);
        }

        /// <summary>
        /// A camper's hit on a fire creature: Linked moves a share to the creatures near it. Returns the damage the target
        /// keeps. The moved share never kills (each stops at 1 health), so kills and their credit stay with the target.
        /// </summary>
        public static float OnHitMonster(Creature target, FraysCreatureTag tag, float damage)
        {
            try
            {
                if (Active(tag.Heat) != FraysAffix.Linked || target.Location == null)
                    return damage;

                var near = tag.Fire.AliveSnapshot().Where(m => m != target && m.FraysTag != null && !m.IsDead && m.Location != null
                    && m.Location.DistanceTo(target.Location) <= LinkRange && m.Health.Current > 1).ToList();
                var (kept, each) = LinkSplit(damage, near.Count, target.Health.Current);

                foreach (var m in near)
                {
                    var take = (int)Math.Min(m.Health.Current - 1, Math.Round(each));

                    if (take > 0)
                        m.UpdateVitalDelta(m.Health, -take);
                }

                return (float)kept;
            }
            catch (Exception ex)
            {
                log.Error("[FRAYS] linked threw", ex);
                return damage;
            }
        }
    }
}
