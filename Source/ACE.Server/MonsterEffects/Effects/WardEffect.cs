using System;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.MonsterEffects.Effects
{
    /// <summary>
    /// Phase 2: an absorb pool that soaks incoming damage until spent or expired - reuses
    /// SanguineWardMath's grant/expiry/absorb arithmetic wholesale (Blood Mage T3's ward is the identical
    /// shape: a flat absorb amount, a fixed duration, refresh-not-stack). The grant amount is either a flat
    /// amount= or a fraction pcthp= of the carrier's OWN max health - exactly one of the two, enforced at
    /// Validate time.
    ///
    /// THREE GRANT TRIGGERS, COMBINABLE - on= is a comma list here exactly as it is on reflect, debuff,
    /// castspell and ramp, so "on=spawn,hpbelow" is the boss that shields on arrival and again when it
    /// drops into its second phase. Each mode carries its OWN sentinel field, which is what makes combining
    /// them work rather than merely parse: spawn's one-shot latch and hpbelow's are separate booleans, so a
    /// record naming both grants twice, once for each reason, and neither consumes the other's latch.
    ///
    /// The three modes, and the sentinel each owns:
    ///   on=spawn     - granted exactly once, the first time the heartbeat sees this creature. There is no
    ///                  spawn hook (and this phase must not add one), so "spawn" is simulated as "first
    ///                  heartbeat tick", using state.Announced as the one-shot sentinel.
    ///   on=heartbeat - regranted every heartbeat the pool is empty or expired (SanguineWardMath.IsExpired
    ///                  covers both - an empty pool is defined as expired). No sentinel needed: the check is
    ///                  idempotent every tick.
    ///   on=hpbelow   - granted exactly once, the first heartbeat where the carrier's current health falls
    ///                  to or below trigger= of max - the "second-phase boss" shape. Reuses
    ///                  ExecutionerAbility.IsExecuteRange for the identical hp-fraction check
    ///                  ExecuteEffect already relies on elsewhere in this catalog. state.Stacks is the
    ///                  one-shot sentinel here (0 = not yet triggered, 1 = triggered), kept separate from
    ///                  state.Announced only because both are legitimate booleans and using two different
    ///                  fields is what makes each mode's state independently inspectable in a test.
    ///
    /// The RESOLVED grant amount - whichever of amount=/pcthp= produced it - is capped by
    /// monster_effect_ward_cap, expressed as a multiple of the carrying monster's own maximum health. One
    /// clamp at the point both authoring paths converge covers both, rather than needing a separate ceiling
    /// for each; secs= remains an authored value only, same as debuff's secs=.
    /// </summary>
    public sealed class WardEffect : IMonsterEffect, IMonsterIncomingDamage, IMonsterHeartbeat
    {
        public string Kind => "ward";

        /// <summary>
        /// Runs ahead of every reader on its hooks: this effect CHANGES the damage figure they size
        /// themselves from. See the ordering paragraph in MonsterEffectHooks.cs.
        /// </summary>
        public int DispatchOrder => MonsterEffectDispatchOrder.DamageMutator;

        public bool Validate(MonsterEffectSpec spec, out string error)
        {
            var hasAmount = spec.Has("amount");
            var hasPct = spec.Has("pcthp");

            if (hasAmount == hasPct)
            {
                error = "ward requires exactly one of amount= or pcthp=";
                return false;
            }

            if (hasAmount && spec.GetDouble("amount", 0.0) <= 0.0)
            {
                error = "ward requires amount= > 0";
                return false;
            }

            if (hasPct)
            {
                var pct = spec.GetDouble("pcthp", 0.0);
                if (pct <= 0.0 || pct > 1.0)
                {
                    error = "ward requires pcthp= in (0, 1]";
                    return false;
                }
            }

            if (spec.GetDouble("secs", 0.0) <= 0.0)
            {
                error = "ward requires secs= > 0";
                return false;
            }

            // HAND-PARSED, AND STILL STRICT. Both of the generic accessors are too forgiving to gate on:
            // GetEnum falls back to the default on an unparseable value and GetFlags falls back when NOTHING
            // in the list resolves while silently dropping the tokens that do not, so under either one
            // "on=bogus" and "on=spawn,bogus" would be indistinguishable from a correctly authored record and
            // the typo would ship as on=spawn. Every token is checked by NAME here instead - which also means
            // an authored number never resolves, matching the parser's own "never by number" rule - and one
            // bad token rejects the whole record at build time, naming the wcid.
            var onRaw = spec.GetString("on");
            var on = MonsterEffectTrigger.Spawn;

            if (onRaw != null)
            {
                on = MonsterEffectTrigger.None;

                foreach (var token in onRaw.Split(','))
                {
                    var name = token.Trim();
                    if (name.Length == 0)
                        continue;

                    if (!TryParseGrantTrigger(name, out var parsed))
                    {
                        error = "ward on= must be spawn, heartbeat or hpbelow, or a comma-separated list of them";
                        return false;
                    }

                    on |= parsed;
                }

                if (on == MonsterEffectTrigger.None)
                {
                    error = "ward on= must name at least one of spawn, heartbeat or hpbelow";
                    return false;
                }
            }

            if (on.HasFlag(MonsterEffectTrigger.HpBelow))
            {
                var trigger = spec.GetDouble("trigger", 0.0);
                if (trigger <= 0.0 || trigger > 1.0)
                {
                    error = "ward on=hpbelow requires trigger= in (0, 1]";
                    return false;
                }
            }

            error = null;
            return true;
        }

        public uint OnIncomingDamage(Creature defender, WorldObject source, DamageType damageType, uint amount, MonsterEffectSpec spec, ref MonsterEffectState state)
        {
            if (amount == 0 || state.WardAmount == 0)
                return amount;

            var now = Time.GetUnixTime();

            var current = new SanguineWardMath.WardState { Amount = state.WardAmount, ExpireTime = state.WardExpire };

            var result = SanguineWardMath.Absorb(current, amount, now);

            state.WardAmount = result.Remaining.Amount;
            state.WardExpire = result.Remaining.ExpireTime;

            return result.DamageAfterWard;
        }

        /// <summary>
        /// Only one of the three names is matched by <see cref="MonsterEffectSpec.GetEnum{T}"/>'s rules, so
        /// this is the single place that decides what a ward's on= may say. Kept next to Validate, which is
        /// the only strict gate the arg gets.
        /// </summary>
        private static bool TryParseGrantTrigger(string name, out MonsterEffectTrigger trigger)
        {
            if (string.Equals(name, nameof(MonsterEffectTrigger.Spawn), StringComparison.OrdinalIgnoreCase))
            {
                trigger = MonsterEffectTrigger.Spawn;
                return true;
            }

            if (string.Equals(name, nameof(MonsterEffectTrigger.Heartbeat), StringComparison.OrdinalIgnoreCase))
            {
                trigger = MonsterEffectTrigger.Heartbeat;
                return true;
            }

            if (string.Equals(name, nameof(MonsterEffectTrigger.HpBelow), StringComparison.OrdinalIgnoreCase))
            {
                trigger = MonsterEffectTrigger.HpBelow;
                return true;
            }

            trigger = MonsterEffectTrigger.None;
            return false;
        }

        /// <summary>
        /// SEQUENTIAL, NOT A SWITCH, because on= is a list: a record naming two triggers must be offered
        /// both, in a fixed order. Spawn runs first so a "spawn,heartbeat" record's opening grant is the one
        /// that lands, and heartbeat then only tops the pool back up once that one has actually lapsed.
        ///
        /// Reads on= through GetFlags rather than repeating the strict parse: every token in a record that
        /// reached a live creature was already checked by Validate, so the accessor's forgiving fall-back
        /// cannot mask anything here.
        /// </summary>
        public void OnHeartbeat(Creature creature, MonsterEffectSpec spec, ref MonsterEffectState state)
        {
            var now = Time.GetUnixTime();

            var on = spec.GetFlags("on", MonsterEffectTrigger.Spawn);

            // one-shot on state.Announced: there is no spawn hook, so "spawn" is the first heartbeat
            if (on.HasFlag(MonsterEffectTrigger.Spawn) && !state.Announced)
            {
                Grant(creature, spec, ref state, now);
                state.Announced = true;
            }

            // no sentinel: an empty pool counts as expired, so the check is idempotent every tick
            if (on.HasFlag(MonsterEffectTrigger.Heartbeat))
            {
                var current = new SanguineWardMath.WardState { Amount = state.WardAmount, ExpireTime = state.WardExpire };
                if (SanguineWardMath.IsExpired(current, now))
                    Grant(creature, spec, ref state, now);
            }

            // one-shot on state.Stacks, a DIFFERENT field from spawn's sentinel - which is what lets
            // "on=spawn,hpbelow" grant twice, once for each reason, instead of the first latch eating both
            if (on.HasFlag(MonsterEffectTrigger.HpBelow) && state.Stacks == 0)
            {
                var trigger = spec.GetDouble("trigger", 0.0);
                if (ExecutionerAbility.IsExecuteRange(creature, trigger))
                {
                    Grant(creature, spec, ref state, now);
                    state.Stacks = 1;
                }
            }
        }

        /// <summary>
        /// Computes the grant amount from amount= or pcthp= (whichever Validate accepted) and hands it to
        /// SanguineWardMath.Grant with wardFraction=1.0, so the ward absorbs exactly that many points - the
        /// fraction parameter exists for Sanguine Ward's own "fraction of health lost" shape, which this
        /// effect has no use for.
        /// </summary>
        private static void Grant(Creature creature, MonsterEffectSpec spec, ref MonsterEffectState state, double now)
        {
            var secs = spec.GetDouble("secs", 0.0);
            if (secs <= 0.0)
                return;

            uint amount;

            if (spec.Has("amount"))
            {
                amount = (uint)Math.Max(0.0, Math.Round(spec.GetDouble("amount", 0.0)));
            }
            else
            {
                var pct = spec.GetDouble("pcthp", 0.0);
                var max = creature.Health.MaxValue;
                amount = (uint)Math.Max(0.0, Math.Round(max * pct));
            }

            if (amount == 0)
                return;

            var maxWard = (uint)Math.Max(0.0, Math.Round(creature.Health.MaxValue * MonsterEffectCaps.WardCap));
            amount = Math.Min(amount, maxWard);

            if (amount == 0)
                return;

            var granted = SanguineWardMath.Grant(default, amount, 1.0, now, secs);

            state.WardAmount = granted.Amount;
            state.WardExpire = granted.ExpireTime;
        }
    }
}
