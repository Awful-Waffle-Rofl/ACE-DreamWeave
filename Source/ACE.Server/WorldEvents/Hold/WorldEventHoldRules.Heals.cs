using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.WorldEvents.Hold;

// World Events Hold H4b: the pure heal, shrine, pacing and cosmetic rules (D6 - no clock, no world, no settings: every
// tunable arrives as an argument from one WorldEventHoldTuning snapshot). Ported from the contributor's WorldEventHoldRules /
// WorldEventHoldDefense with the HoldSettings reads lifted into parameters. H4's own partial; later slices reuse these
// (HealthPercent, ProtectedHealthLine and the shrine lines included), never redefine them.
namespace ACE.Server.WorldEvents.Hold
{
    /// <summary>Where a heal on a knight comes from. Every source runs through Creature.ApplyHoldKnightHeal on the knight's landblock.</summary>
    public enum HoldHealSource
    {
        /// <summary>A player's kit or heal spell. The amount is RAW player heal points, converted by the player-heal rules.</summary>
        Player,

        /// <summary>A Shrine of Restoration. The amount is a share of max health.</summary>
        Shrine,

        /// <summary>A corrupted monster's death. A share of max health.</summary>
        CorruptedKill,

        /// <summary>A player killed a monster that was attacking the knight. A share, also held to rescue_cap_per_wave.</summary>
        Rescue,

        /// <summary>Clear regeneration. A share.</summary>
        Regen,

        /// <summary>The heal between waves. A share.</summary>
        BetweenWaves
    }

    /// <summary>What one heal on a knight came to.</summary>
    public enum HoldHealOutcome
    {
        /// <summary>Some health was restored.</summary>
        Healed,

        /// <summary>Nothing: the knight was already at full health.</summary>
        Full,

        /// <summary>Nothing: the knight's healing budget for this wave is spent.</summary>
        BudgetSpent,

        /// <summary>Nothing: the player-heal cap (player_heal_cap per ten seconds) is spent for now.</summary>
        Capped,

        /// <summary>Nothing: player heals are off (player_heal), or the run is over.</summary>
        Refused,

        /// <summary>Nothing: not a living knight, or nothing asked for.</summary>
        None
    }

    /// <summary>What a shrine use may do now.</summary>
    public enum HoldShrineUseState { Ready, ShrineResting, PlayerResting }

    /// <summary>The cosmetic defense: what a knight does this tick.</summary>
    public enum HoldPoseAction
    {
        /// <summary>Nothing: waiting for the next swing, or already facing outward.</summary>
        None,

        /// <summary>Draw its weapon (enter the combat stance).</summary>
        DrawWeapon,

        /// <summary>Turn to face the monster first.</summary>
        Face,

        /// <summary>Swing at the monster (an animation only).</summary>
        Swing,

        /// <summary>Turn back outward.</summary>
        FaceOutward
    }
}

namespace ACE.Server.WorldEvents
{
    public static partial class WorldEventHoldRules
    {
        // ---- heals ------------------------------------------------------------------------------------------------

        /// <summary>
        /// The share of a heal that lands on a knight last hit <paramref name="secondsSinceHit"/> ago (pure): 1 - reduction
        /// inside the window, else 1. A knight never hit (null) heals in full.
        /// </summary>
        public static double HealAfterHitFactor(double? secondsSinceHit, double reduction, double windowSeconds)
        {
            if (secondsSinceHit == null || !double.IsFinite(secondsSinceHit.Value) || !(reduction > 0) || !(windowSeconds > 0))
                return 1.0;

            if (secondsSinceHit.Value < 0 || secondsSinceHit.Value >= windowSeconds)
                return 1.0;

            return Math.Max(0.0, 1.0 - Math.Min(1.0, reduction));
        }

        /// <summary>
        /// How much of a heal the per-wave budget lets through (pure): <paramref name="requested"/> health against a budget of
        /// <paramref name="budgetShare"/> of <paramref name="maxHealth"/>, of which <paramref name="spentThisWave"/> is used. A
        /// budget of 0 or less means no budget. Never negative.
        /// </summary>
        public static int HealWithinBudget(int requested, uint maxHealth, double budgetShare, int spentThisWave)
        {
            if (requested <= 0)
                return 0;

            if (!(budgetShare > 0) || !double.IsFinite(budgetShare))
                return requested;

            var budget = (int)Math.Floor(Math.Min(1.0, budgetShare) * maxHealth);

            return Math.Max(0, Math.Min(requested, budget - Math.Max(0, spentThisWave)));
        }

        /// <summary>What counts as one typical player heal (a Heal VI cast or a decent healing kit), in raw health points.</summary>
        public const double TypicalPlayerHeal = 100.0;

        /// <summary>The share of a knight's max health one typical player heal restores.</summary>
        public const double HealShareOfTypicalHeal = 0.04;

        /// <summary>
        /// A player's heal on a knight, converted (pure): damage to knights is time-based (a share of max health), so a raw
        /// heal is converted the same way - one typical player heal restores <see cref="HealShareOfTypicalHeal"/> of max
        /// health, bigger heals proportionally more - then held by the knight's own bucket at
        /// <paramref name="maxHealPerTenSeconds"/> of max health per ten seconds. Returns the health to restore.
        /// </summary>
        public static int ConvertPlayerHeal(double rawHeal, uint maxHealth, double maxHealPerTenSeconds, ref double tokens, ref double lastAt, double now)
        {
            if (!(rawHeal > 0) || maxHealth == 0 || !double.IsFinite(maxHealPerTenSeconds) || !(maxHealPerTenSeconds > 0))
                return 0;

            var wanted = maxHealth * HealShareOfTypicalHeal * (rawHeal / TypicalPlayerHeal);
            var windowAllowance = maxHealth * maxHealPerTenSeconds;

            return (int)Math.Floor(TakeFromBucket(wanted, windowAllowance / 10.0, windowAllowance, ref tokens, ref lastAt, now));
        }

        /// <summary>The health a share-based heal asks for (pure): floor(min(1, share) x max x factor), 0 for anything unusable.</summary>
        public static int ShareHeal(double share, uint maxHealth, double factor)
        {
            if (!(share > 0) || !double.IsFinite(share) || maxHealth == 0 || !(factor > 0) || !double.IsFinite(factor))
                return 0;

            return (int)Math.Floor(Math.Min(1.0, share) * maxHealth * factor);
        }

        /// <summary>What is left of a knight's rescue cap this wave (pure): floor(clamp(cap, 0, 1) x max) - spent, never negative.</summary>
        public static int RescueRoom(double rescueCapShare, uint maxHealth, int rescueSpent)
        {
            if (!double.IsFinite(rescueCapShare))
                return 0;

            return Math.Max(0, (int)Math.Floor(Math.Clamp(rescueCapShare, 0, 1) * maxHealth) - Math.Max(0, rescueSpent));
        }

        /// <summary>
        /// The only spells a player may cast on a knight (pure): a beneficial Boost that raises HEALTH. Every other spell -
        /// buffs, other vitals, anything harmful - stays refused.
        /// </summary>
        public static bool IsKnightHealSpell(bool isBeneficial, SpellType type, DamageType vital, int boost, int maxBoost)
            => isBeneficial && type == SpellType.Boost && vital == DamageType.Health && Math.Max(boost, maxBoost) > 0;

        /// <summary>The line a player gets for a heal on a knight (pure). ASCII.</summary>
        public static string PlayerHealLine(string knightName, int healed, HoldHealOutcome outcome, bool critical, string kitRemaining)
        {
            var name = string.IsNullOrWhiteSpace(knightName) ? "The defender" : knightName.Trim();
            var tail = string.IsNullOrEmpty(kitRemaining) ? "" : kitRemaining;

            switch (outcome)
            {
                case HoldHealOutcome.Healed:
                    return $"You {(critical ? "expertly " : "")}heal {name} for {healed} health point{(healed == 1 ? "" : "s")}.{tail}";
                case HoldHealOutcome.Full:
                    return $"{name} is already at full health.{tail}";
                case HoldHealOutcome.Refused:
                    return $"{name} cannot be healed right now.{tail}";
                default:
                    return $"{name} has been healed as much as it can be for now.{tail}";
            }
        }

        // ---- the shrine -----------------------------------------------------------------------------------------

        /// <summary>
        /// Whether a shrine use may go ahead (pure): the shrine's own rest first, then the player's (0 seconds = no player
        /// rest). Times are unix seconds, 0 = never used.
        /// </summary>
        public static HoldShrineUseState ShrineUseCheck(double now, double shrineReadyAt, double playerReadyAt, double playerCooldownSeconds)
        {
            if (now < shrineReadyAt)
                return HoldShrineUseState.ShrineResting;

            if (playerCooldownSeconds > 0 && now < playerReadyAt)
                return HoldShrineUseState.PlayerResting;

            return HoldShrineUseState.Ready;
        }

        /// <summary>The shrine rest in force (pure): <paramref name="seconds"/> clamped to 0..600 (0 allowed on purpose).</summary>
        public static double ShrineCooldown(double seconds) => double.IsFinite(seconds) ? Math.Clamp(seconds, 0, 600) : 45;

        /// <summary>What a player is told using a shrine that is resting (pure). ASCII.</summary>
        public static string ShrineCooldownLine(double secondsLeft)
        {
            var s = (int)Math.Ceiling(Math.Max(1, secondsLeft));

            return $"The shrine is still gathering its strength. It will be ready in {s} second{(s == 1 ? "" : "s")}.";
        }

        /// <summary>What a player is told using a shrine before their own shrine cooldown is over (pure). ASCII.</summary>
        public static string ShrinePlayerCooldownLine(double secondsLeft)
        {
            var s = (int)Math.Ceiling(Math.Max(1, secondsLeft));

            return $"You must gather yourself before calling on a shrine again. You can in {s} second{(s == 1 ? "" : "s")}.";
        }

        /// <summary>What a player is told when a shrine heal restores nothing (pure). ASCII.</summary>
        public static string ShrineWastedLine(string knightName, HoldHealOutcome outcome)
        {
            var name = string.IsNullOrWhiteSpace(knightName) ? "The defender" : knightName;

            switch (outcome)
            {
                case HoldHealOutcome.BudgetSpent:
                    return $"{name} can draw no more strength this wave. The shrine holds its power for now.";
                case HoldHealOutcome.Healed:
                    return $"{name} needs almost no restoring. The shrine holds its power for now.";
                default:
                    return $"{name} needs no restoring. The shrine holds its power for now.";
            }
        }

        /// <summary>The most a shrine heal must restore, as a share of the knight's max health, to count as a use (shrine griefing guard).</summary>
        public const double ShrineMinUsefulShare = 0.02;

        /// <summary>
        /// Whether a shrine heal of <paramref name="healed"/> on a knight of <paramref name="maxHealth"/> counts as a use (pure). The
        /// floor is min(<see cref="ShrineMinUsefulShare"/>, half of <paramref name="shrineHealShare"/>) of max health, so a low
        /// configured shrine_heal cannot make every use too small to count, and so free.
        /// </summary>
        public static bool ShrineHealCounts(int healed, uint maxHealth, double shrineHealShare)
        {
            if (healed <= 0)
                return false;

            var half = double.IsFinite(shrineHealShare) && shrineHealShare > 0 ? shrineHealShare * 0.5 : ShrineMinUsefulShare;

            return healed >= Math.Min(ShrineMinUsefulShare, half) * maxHealth;
        }

        /// <summary>The shrine's lines when it does nothing (pure). ASCII.</summary>
        public const string ShrineQuietLine = "The shrine is quiet.";

        public const string ShrineNobodyLine = "There is no one here for the shrine to restore.";

        // ---- health shown to players -----------------------------------------------------------------------------

        /// <summary>A knight's health as a whole percent (pure), 0 to 100, rounded up so a living knight never reads 0.</summary>
        public static int HealthPercent(uint current, uint max)
        {
            if (max == 0 || current == 0)
                return 0;

            return (int)Math.Clamp(Math.Ceiling(current * 100.0 / max), 1, 100);
        }

        /// <summary>The line a player gets when assessing a knight (pure): "Sir Donovan is at 64% health."</summary>
        public static string ProtectedHealthLine(string name, uint current, uint max)
            => $"{(string.IsNullOrWhiteSpace(name) ? "It" : name)} is at {HealthPercent(current, max)}% health.";

        /// <summary>The seconds between two health lines for the same knight to one player (the client re-requests an open appraisal).</summary>
        public const double HealthLineIntervalSeconds = 3.0;

        // ---- pacing -----------------------------------------------------------------------------------------------

        /// <summary>A tag speed multiplier as a hit uses it (pure): <paramref name="mult"/> when finite and above 0, else 1 (no change).</summary>
        public static float SpeedMult(float mult) => float.IsFinite(mult) && mult > 0 ? mult : 1f;

        /// <summary>A melee recovery delay at a swing-speed multiplier (pure): delay / mult; a faster swinger waits less.</summary>
        public static double ScaledMeleeDelay(double delay, float attackSpeedMult) => delay / SpeedMult(attackSpeedMult);

        /// <summary>A swing animation speed at a swing-speed multiplier (pure): speed x mult.</summary>
        public static float ScaledAnimSpeed(float animSpeed, float attackSpeedMult) => animSpeed * SpeedMult(attackSpeedMult);

        /// <summary>A run rate at a run-speed multiplier (pure): rate x mult.</summary>
        public static float ScaledRunRate(float runRate, float runRateMult) => runRate * SpeedMult(runRateMult);

        // ---- ranged monsters' offensive spells ---------------------------------------------------------------------

        /// <summary>
        /// Whether a spell is one a ranged hold can attack with (pure): a projectile (war or void bolt) or a harmful health
        /// Boost (Harm). Buffs and debuffs are not.
        /// </summary>
        public static bool IsOffensiveSpell(uint? projectileWcid, int? numProjectiles, int? boost, int? vitalDamageType)
        {
            if ((projectileWcid ?? 0) != 0 || (numProjectiles ?? 0) > 0)
                return true;

            return (boost ?? 0) < 0 && vitalDamageType == (int)DamageType.Health;
        }

        /// <summary>
        /// One of a spellbook's offensive spells, weighted by their own book chances (pure). <paramref name="roll"/> in [0, 1)
        /// picks along the cumulative weight; a weight of 0 or less counts as a small chance. 0 when none is offensive.
        /// </summary>
        public static int PickOffensiveSpell(IEnumerable<KeyValuePair<int, float>> book, Func<uint, bool> isOffensive, double roll)
        {
            if (book == null || isOffensive == null)
                return 0;

            var offensive = book.Where(kv => kv.Key > 0 && isOffensive((uint)kv.Key))
                .Select(kv => (Id: kv.Key, Weight: float.IsFinite(kv.Value) && kv.Value > 0 ? (double)kv.Value : 0.01))
                .ToList();

            if (offensive.Count == 0)
                return 0;

            var total = offensive.Sum(o => o.Weight);
            var at = Math.Clamp(double.IsFinite(roll) ? roll : 0, 0, 0.999999) * total;

            foreach (var (id, weight) in offensive)
            {
                if (at < weight)
                    return id;

                at -= weight;
            }

            return offensive[offensive.Count - 1].Id;
        }

        // ---- the cosmetic defense ----------------------------------------------------------------------------------

        /// <summary>
        /// The monster a knight swings at (pure): the one it already faces while that one stays in reach, else the nearest in
        /// reach. <paramref name="foes"/> are the run's living monsters with their edge-to-edge gap in metres. 0 when none.
        /// </summary>
        public static uint PickDefendFoe(IEnumerable<(uint Guid, double Gap)> foes, double reach, uint current)
        {
            var inReach = (foes ?? Enumerable.Empty<(uint, double)>())
                .Where(f => f.Guid != 0 && double.IsFinite(f.Gap) && f.Gap <= reach)
                .ToList();

            if (inReach.Count == 0)
                return 0;

            if (current != 0 && inReach.Any(f => f.Guid == current))
                return current;

            return inReach.OrderBy(f => f.Gap).ThenBy(f => f.Guid).First().Guid;
        }

        /// <summary>
        /// The next cosmetic step (pure). In order: draw the weapon when at peace; with a foe, turn to it when it is more than
        /// <paramref name="faceDegrees"/> off the facing, else swing when the rhythm allows; with none, turn back outward once
        /// <paramref name="turnBackSeconds"/> have passed since the last foe.
        /// </summary>
        public static HoldPoseAction DecidePose(bool inCombatStance, bool hasFoe, double angleToFoe, double now, double nextSwingAt,
            double lastFoeAt, bool facingOutward, double faceDegrees, double turnBackSeconds)
        {
            if (!inCombatStance)
                return HoldPoseAction.DrawWeapon;

            if (hasFoe)
            {
                if (angleToFoe > faceDegrees)
                    return HoldPoseAction.Face;

                return now >= nextSwingAt ? HoldPoseAction.Swing : HoldPoseAction.None;
            }

            if (!facingOutward && now - lastFoeAt >= turnBackSeconds)
                return HoldPoseAction.FaceOutward;

            return HoldPoseAction.None;
        }

        /// <summary>
        /// When the next cosmetic swing may start (pure): after this swing's animation, and a rhythm of
        /// <paramref name="swingSeconds"/> to <paramref name="swingMaxSeconds"/> (<paramref name="unitRoll"/> in [0, 1) picks
        /// within it) from now, whichever is later.
        /// </summary>
        public static double NextDefendSwingAt(double now, double animSeconds, double unitRoll, double swingSeconds, double swingMaxSeconds)
        {
            var lo = Math.Max(0.5, double.IsFinite(swingSeconds) ? swingSeconds : 2);
            var hi = Math.Max(lo, double.IsFinite(swingMaxSeconds) ? swingMaxSeconds : lo);
            var rhythm = lo + Math.Clamp(double.IsFinite(unitRoll) ? unitRoll : 0, 0, 1) * (hi - lo);

            return now + Math.Max(Math.Max(0, animSeconds), rhythm);
        }

        /// <summary>The swing animations a stance may use, best first (pure). The caller keeps the ones the knight's motion table has.</summary>
        public static IReadOnlyList<MotionCommand> DefendSwingCandidates(MotionStance stance)
        {
            switch (stance)
            {
                case MotionStance.HandCombat:
                    return new[] { MotionCommand.AttackHigh1, MotionCommand.AttackMed1, MotionCommand.AttackLow1, MotionCommand.AttackMed2 };

                case MotionStance.DualWieldCombat:
                case MotionStance.SwordCombat:
                case MotionStance.SwordShieldCombat:
                case MotionStance.TwoHandedSwordCombat:
                case MotionStance.TwoHandedStaffCombat:
                    return new[] { MotionCommand.SlashHigh, MotionCommand.SlashMed, MotionCommand.SlashLow, MotionCommand.BackhandMed,
                                   MotionCommand.ThrustMed, MotionCommand.ThrustHigh };

                default:
                    return new[] { MotionCommand.SlashMed, MotionCommand.ThrustMed, MotionCommand.BackhandMed, MotionCommand.AttackMed1 };
            }
        }
    }
}
