using System;

using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Rogue T2: evading a melee attack builds a Surefooted stack (cap 5), and each stack adds +1/2/3% by
    /// rank to the player's Parry chance. Stacks lapse after 10 seconds without an evade, and a landed
    /// MELEE hit knocks them all off at once.
    ///
    /// WHY THIS CARRIES NO COMBAT HOOK. Like Resonance and Withering, both halves of the mechanic live on
    /// the Player: the stack pool is transient state in Player_ClassAbilityBuffs, the trigger is
    /// Player.OnEvade, and the bonus is read in Player.RollClassAbilityAvoidance. There is no combat event
    /// this handler would need to be dispatched for, so it takes IPassiveStatAbility and is exempt from
    /// the "Implemented abilities must hook something" rule (see IPassiveStatAbility in ClassAbilityHooks).
    ///
    /// IT DOES NOT REQUIRE PARRY (a DECIDED design call). The bonus is added into the same parryChance
    /// term Parry feeds, before ClassAbilityAvoidance.Resolve, so a Rogue who bought only Surefooted still
    /// parries - and the pooled 50% Shield Block + Parry cap applies to the combined figure unchanged. That
    /// placement is the whole containment story: Surefooted cannot push total avoidance past the cap, it
    /// can only claim more of a pool that was already bounded.
    ///
    /// IT MILDLY SUPPRESSES ITS OWN TRIGGER AS IT RAMPS, BY DESIGN. The class-ability avoidance roll is
    /// resolved BEFORE the normal evade roll, so every point of parry chance Surefooted adds is one fewer
    /// attack that reaches the evade that would have granted the next stack. The pool therefore self-limits
    /// short of a runaway; it is not a bug and must not be "fixed" by moving the roll order, which would
    /// change what procs Thorns / Shield Check / Riposte.
    /// </summary>
    public class SurefootedAbility : IClassAbility, IPassiveStatAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.Surefooted,
            AbilityClass = ClassAbilityClass.Rogue,
            Tier = 2,
            Name = "surefooted",
            DisplayName = "Surefooted",
            Description = "Each melee attack you evade grants a stack (max 5); each stack adds +1/2/3% parry " +
                          "chance by rank. Stacks fade after 10 s without an evade or when a melee hit lands " +
                          "on you. Counts inside the 50% parry+block cap.",
            MaxRank = 3,
            CostPerRank = new[] { 2, 2, 2 },
            Implemented = true,
        };

        /// <summary>
        /// How many stacks a player may hold, from the tunable. Rank-invariant on purpose: rank buys a
        /// bigger bonus PER stack, not a deeper pool, so the ramp time to full is the same at every rank.
        /// </summary>
        public static int StackCap() =>
            (int)Math.Max(0L, PropertyManager.GetLong("class_ability_surefooted_stack_cap").Item);

        /// <summary>
        /// TRUE once the idle window has lapsed since the last evade. Pure, so the boundary is testable
        /// without a live Player: the window is INCLUSIVE, so a pool touched exactly windowSeconds ago is
        /// still alive and one touched a hair later is not.
        ///
        /// This is only the IDLE lapse. The other way the pool dies - a landed melee hit - is immediate and
        /// unconditional, and lives in Player.TakeDamage(DamageEvent), which is the only player-damage entry
        /// point that still knows the attack's CombatType.
        /// </summary>
        public static bool StacksExpired(double now, double lastEvadeTime, double windowSeconds) =>
            now - lastEvadeTime > windowSeconds;

        /// <summary>
        /// The parry chance currently granted by Surefooted: rank * stacks * the per-rank-per-stack rate.
        /// Pure for testability, matching FrenzyAbility.AttackSpeedMultiplier / ResonanceAbility.PerStack.
        ///
        /// Returns 0 for rank &lt;= 0 or stacks &lt;= 0, so a caller that has not looked up ownership still
        /// gets the inert answer. The CALLER owns clamping stacks to <see cref="StackCap"/> - this method
        /// deliberately does not, so a test can ask what an out-of-range pool would be worth.
        /// </summary>
        public static double ParryBonus(int rank, int stacks, double percentPerRankPerStack)
        {
            if (rank <= 0 || stacks <= 0 || percentPerRankPerStack <= 0.0)
                return 0.0;

            return rank * stacks * percentPerRankPerStack;
        }

        /// <summary>
        /// Reports what the player's CURRENT pool is worth right now - rank * held stacks * the per-stack
        /// rate - which is exactly the term Player.RollClassAbilityAvoidance adds into parryChance. No
        /// affinity rider and no equipment mod exist for this ability, so Skill carries the whole figure
        /// and Effective equals Total.
        ///
        /// A LIVE TOTAL RATHER THAN A "/stack" RATE, unlike Resonance and Frenzy. Those two report a rate
        /// because their pools are near-permanently full in practice and the rate is the stable, comparable
        /// number. Surefooted's pool is the opposite: it is knocked to zero by any landed melee hit, so
        /// "what am I holding right now" is the question a player opens /abilities to answer, and a rate
        /// that never moves would hide the only interesting state this ability has. Stacks are clamped to
        /// <see cref="StackCap"/> here for the same reason combat clamps them.
        ///
        /// EFFECTIVE IS DELIBERATELY NOT POOL-CAPPED. Parry and Shield Block share the 50% avoidance cap,
        /// but that cap applies to the COMBINED chance and ParryAbility.GetReadout is the line that owns
        /// reporting it - it calls ClassAbilityAvoidance.Pooled and now feeds this contribution into its
        /// input. Reporting a pooled-capped number here too would print the same cap note on two lines and
        /// leave the reader unable to see what Surefooted itself contributes.
        ///
        /// The null-conditional on the stack read is for the readout unit tests, which call this with a
        /// null Player because Player's static initializer cannot run under the test host.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var perRankPerStack = PropertyManager.GetDouble("class_ability_surefooted_percent_per_rank_per_stack").Item;

            var cap = StackCap();
            var stacks = Math.Clamp(player?.GetSurefootedStacks() ?? 0, 0, Math.Max(0, cap));

            var current = ParryBonus(rank, stacks, perRankPerStack) * 100.0;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = current,
                Affinity = 0.0,
                Gear = 0.0,
                Effective = current,
                Unit = "%",
                Label = "parry",
                Per = null,
                CapNote = null,
            };
        }
    }
}
