using System;

using ACE.Common;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.MonsterEffects.Effects
{
    /// <summary>
    /// Phase 5: after the monster finishes casting a spell, a chance to immediately cast that SAME spell
    /// again for free - the recast hook Creature.CanChainMonsterEffectCast and the castHookDepth backstop in
    /// Creature_MonsterEffects exist for.
    ///
    /// CHAINS SYNCHRONOUSLY, NOT THROUGH AN ACTIONCHAIN. This is a deliberate departure from
    /// FlatDamageEffect/DotEffect's deferred-proc shape, called out explicitly by the recast guard's own
    /// doc comment: "a handler that defers its recast through an ActionChain has already left this scope
    /// when the new cast begins and is not counted", which would let the guard's own depth counting be
    /// bypassed and the chain run unbounded. Casting again from directly inside the hook is what keeps
    /// castHookDepth (and therefore CanChainMonsterEffectCast) accurate.
    ///
    /// max= is THIS RECORD's own depth budget, separate from and in addition to monster_effect_recast_cap:
    /// state.Stacks counts the levels this record's own chain has taken so far and is unwound when the
    /// chain ends, so authoring max=1 means "one extra cast, never a chain of chains" regardless of how
    /// generous the server-wide cap is. See Chain below for why the chain is a loop over this one record
    /// rather than a re-entry into the whole cast-hook list.
    /// </summary>
    public sealed class RecastEffect : IMonsterEffect, IMonsterCastHook
    {
        public string Kind => "recast";

        public bool Validate(MonsterEffectSpec spec, out string error)
        {
            var chance = spec.GetDouble("chance", 0.0);
            if (chance <= 0.0 || chance > 1.0)
            {
                error = "recast requires chance= in (0, 1]";
                return false;
            }

            if (spec.GetInt("max", 1) <= 0)
            {
                error = "recast requires max= > 0";
                return false;
            }

            error = null;
            return true;
        }

        public void OnCastComplete(Creature caster, Spell spell, MonsterEffectSpec spec, ref MonsterEffectState state)
        {
            // ordered before the roll now that ShouldChain reads the caster's own proc-chance ramp
            if (caster == null || caster.IsDead)
                return;

            Chain(caster, spec, ref state, () => caster.CastSpell(spell));
        }

        /// <summary>
        /// The chain itself, and the number of extra casts it performed.
        /// </summary>
        ///
        /// <remarks>
        /// A LOOP OVER THIS RECORD ALONE, not a re-entry into Creature.OnMonsterEffectCastComplete. Chaining
        /// by re-dispatching re-ran EVERY cast-hook record the monster carries, so each level multiplied by
        /// the number of chaining records rather than adding one: two "recast" records at
        /// monster_effect_recast_cap 2 produced six extra casts, not two, and the tunable's own description
        /// ("maximum extra casts one completed cast may chain into") stopped describing anything. Looping
        /// here makes the count LINEAR - each record chains up to the cap on its own, so N records cost N
        /// times the cap rather than branching.
        ///
        /// The consequence to know: a chained cast's completion is answered by THIS record only. It does not
        /// re-run the monster's other cast hooks. Today that is invisible (recast is the only IMonsterCastHook
        /// there is), and it is the deliberate price of a bounded chain.
        ///
        /// TWO BUDGETS, BOTH STILL ENFORCED. state.Stacks counts the levels this record has taken and is
        /// bounded by its own max=; Creature.TryEnterMonsterEffectCastChain claims a level of the shared,
        /// thread-scoped depth that monster_effect_recast_cap bounds, which is what stops a generous max=
        /// from outrunning the server. Both are unwound in the finally, so a throw from the cast leaves
        /// neither counter stuck.
        ///
        /// <paramref name="cast"/> is a delegate purely so this is testable: caster.CastSpell needs a real
        /// Spell, which needs the client dat (DatManager.PortalDat) that the test assembly never loads.
        /// </remarks>
        internal static int Chain(Creature caster, MonsterEffectSpec spec, ref MonsterEffectState state, Action cast)
        {
            var chained = 0;
            var claimedStacks = 0;
            var claimedDepth = 0;

            try
            {
                while (ShouldChain(caster, spec, ref state))
                {
                    state.Stacks++;
                    claimedStacks++;

                    cast();
                    chained++;

                    // claim the level the cast just made would sit at, so the next pass's
                    // CanChainMonsterEffectCast read sees the depth this chain has actually reached
                    if (!Creature.TryEnterMonsterEffectCastChain())
                        break;

                    claimedDepth++;
                }
            }
            finally
            {
                state.Stacks -= claimedStacks;

                for (var i = 0; i < claimedDepth; i++)
                    Creature.ExitMonsterEffectCastChain();
            }

            return chained;
        }

        /// <summary>
        /// The guard-and-roll half: true when this record's own max= depth is not spent, the shared recast
        /// latch (Creature.CanChainMonsterEffectCast) allows another chained cast, and the (capped) chance
        /// rolls a hit. Pure and dat-free - split out because OnCastComplete's actual recast needs a real
        /// Spell object (caster.CastSpell(spell)), and constructing one needs the client dat
        /// (DatManager.PortalDat), which this test assembly never loads. See MonsterEffectRecastTests for
        /// where that limit is hit.
        /// </summary>
        internal static bool ShouldChain(Creature caster, MonsterEffectSpec spec, ref MonsterEffectState state)
        {
            var max = spec.GetInt("max", 1);
            if (max <= 0)
                return false;

            if (state.Stacks >= max)
                return false;

            if (!Creature.CanChainMonsterEffectCast)
                return false;

            var chance = spec.GetDouble("chance", 0.0);
            if (chance <= 0.0)
                return false;

            // scaled by the casting monster's ramp axis=procchance, then capped - see
            // Creature.ScaleMonsterEffectProcChance
            var clamped = caster.ScaleMonsterEffectProcChance(chance);
            if (clamped <= 0.0)
                return false;

            if (ThreadSafeRandom.Next(0.0f, 1.0f) > clamped)
                return false;

            return true;
        }
    }
}
