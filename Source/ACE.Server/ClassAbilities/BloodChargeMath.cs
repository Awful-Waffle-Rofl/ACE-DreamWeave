using System;

namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// Pure math for the Blood Mage's Blood Charge transient stack counter, owned by SANGUINE RESERVE
    /// (Blood Mage T1 game-changer). Kept out of Player so the stack-cap, idle-expiry and damage-ramp
    /// arithmetic used by Player_ClassAbilityBuffs.cs (bloodChargeStacks) is unit-testable without
    /// touching the Player type - the same split FrenzyAbility/NetherRushAbility use for their own stack
    /// math. Not an IClassAbility: <see cref="Abilities.SanguineReserveAbility"/> carries the definition,
    /// and the effect is read directly at the life-damage sites rather than dispatched from a hook.
    ///
    /// The burst half - Exsanguinate consuming the whole pool at a multiple of the per-charge value -
    /// lives in <see cref="ExsanguinateMath"/>.
    /// </summary>
    public static class BloodChargeMath
    {
        /// <summary>
        /// Maximum Blood Charge stacks at a given rank, from the class_ability_bloodmage_charge_stack_cap_rN
        /// tunables (0 for rank 0 / unlearned or any rank outside 1-3).
        /// </summary>
        public static int StackCap(int rank, long capR1, long capR2, long capR3) => rank switch
        {
            1 => (int)capR1,
            2 => (int)capR2,
            3 => (int)capR3,
            _ => 0,
        };

        /// <summary>
        /// TRUE once more than expireSeconds have elapsed since the last stack was added - the same
        /// "now minus last touch, compared against the window" shape as FrenzyAbility's expiry check.
        /// </summary>
        public static bool IsExpired(double now, double lastAddTime, double expireSeconds) =>
            now - lastAddTime > expireSeconds;

        /// <summary>
        /// Sanguine Reserve's life-magic damage multiplier for one strike: 1.0 plus perStack per held
        /// charge (default +7% each, so a full 5-charge pool is 1.35). 1.0 when there are no charges.
        ///
        /// Negative inputs are floored to zero rather than rejected, so a mis-set tunable can never turn
        /// the ability into a damage PENALTY.
        ///
        /// <paramref name="gearPerStack"/> is the BLOOD CHARGE equipment mod (EquipmentModId.BloodCharge),
        /// added to the PER-CHARGE rate INSIDE the ramp - 1.0 + stacks * (perStack + gear) - so it sits on
        /// the ability's own additive axis and is multiplied by the same stack count rather than becoming a
        /// separate factor (DESIGN.md 3.3, the axis rule). It defaults to 0, and adding 0.0 to the rate is
        /// exact in IEEE arithmetic, so an unmodded build reproduces the previous multiplier bit-for-bit.
        ///
        /// The "no bonus at all" test is on the SUM rather than on perStack alone: gear must still pay out
        /// if the ability's own tunable is ever set to zero, and a negative sum must still floor to 1.0.
        /// </summary>
        public static float DamageMultiplier(int stacks, double perStack, double gearPerStack = 0.0)
        {
            if (stacks <= 0)
                return 1.0f;

            var perCharge = perStack + gearPerStack;

            if (perCharge <= 0.0)
                return 1.0f;

            return (float)(1.0 + stacks * perCharge);
        }

        /// <summary>
        /// The whole-number percentage a damage multiplier represents, for PLAYER-FACING TEXT ONLY:
        /// 1.35 -> 35, 2.05 -> 105. Never read by a damage calculation.
        ///
        /// It exists because the Blood Charge feedback the player now gets on every accumulating cast, the
        /// peak line, and the Exsanguinate burst line all have to quote the same number, and three separate
        /// inline round-and-scale expressions is how the three drift apart. A multiplier at or below 1.0
        /// reports 0 rather than a negative "bonus".
        /// </summary>
        public static int BonusPercent(double multiplier)
        {
            if (multiplier <= 1.0)
                return 0;

            return (int)Math.Round((multiplier - 1.0) * 100.0);
        }
    }
}
