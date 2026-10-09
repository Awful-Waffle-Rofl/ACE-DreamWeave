using System;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Entity;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.MonsterEffects.Effects
{
    /// <summary>
    /// The three player-visible transitions a ward pool can announce. Internal - it is the seam
    /// <see cref="MonsterEffectWardTests"/> in ACE.Server.Tests observes through
    /// <see cref="WardEffect.AnnounceHookForTests"/>, since a unit test can never build a live Player with a
    /// Session to receive the real chat send.
    /// </summary>
    internal enum WardTransition
    {
        /// <summary>A grant actually landed (amount &gt; 0) - every trigger, every time Grant produces one.</summary>
        Up,

        /// <summary>OnIncomingDamage took a live, unexpired pool from &gt; 0 down to exactly 0.</summary>
        Shattered,

        /// <summary>A pool with amount &gt; 0 was found past its own expiry, before it could be spent.</summary>
        Faded,
    }

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
    ///                  strictly below trigger= of max (exactly at it does not fire) - the "second-phase
    ///                  boss" shape. Reuses
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
    ///
    /// THREE PLAYER-VISIBLE TRANSITIONS, each a chat line to nearby players plus a visual on the creature -
    /// see <see cref="WardTransition"/>. UP fires from every successful Grant, however it was triggered.
    /// SHATTERED fires from OnIncomingDamage when a live, unexpired pool is fully spent by that hit.
    /// FADED fires in TWO places, because a pool can lapse before either dispatch site next runs: OnHeartbeat
    /// checks it BEFORE any grant (so a decayed pool announces FADED, then an on=heartbeat record regrants
    /// and announces UP on that same tick, in that order), and OnIncomingDamage checks it before Absorb (so a
    /// hit that lands after expiry but before the next heartbeat still announces the fade rather than letting
    /// Absorb's Remaining = default silently erase the pool with no message). unless silent=true is authored
    /// on the record, which suppresses both the chat line and the visual for all three transitions - the same
    /// throttle manabarrier and avoid already use for their own single-transition announcements.
    /// </summary>
    public sealed class WardEffect : IMonsterEffect, IMonsterIncomingDamage, IMonsterHeartbeat
    {
        public string Kind => "ward";

        /// <summary>
        /// Test-only observation seam: MonsterEffectWardTests sets this to capture (creature, transition,
        /// message) triples that Announce would otherwise only ever hand to network sends a unit test cannot
        /// receive (there is no live Player/Session in this test tree). Reset to null in each test's cleanup
        /// so one test's hook can never observe another's calls.
        /// </summary>
        internal static Action<Creature, WardTransition, string> AnnounceHookForTests;

        /// <summary>
        /// Runs ahead of every reader on its hooks: this effect CHANGES the damage figure they size
        /// themselves from. See the ordering paragraph in MonsterEffectHooks.cs.
        /// </summary>
        public int DispatchOrder => MonsterEffectDispatchOrder.DamageMutator;

        /// <summary>
        /// silent= is read by Announce, not validated here: like every other bool arg in this catalog
        /// (manabarrier's, avoid's), an unrecognized key is simply never read rather than rejected - this
        /// framework has no unknown-arg gate to extend, so silent=true needs no entry here to work.
        /// </summary>
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

        public uint OnIncomingDamage(Creature defender, WorldObject source, DamageType damageType, uint amount, IncomingDamageOrigin origin, MonsterEffectSpec spec, ref MonsterEffectState state)
        {
            if (amount == 0 || state.WardAmount == 0)
                return amount;

            var now = Time.GetUnixTime();

            var current = new SanguineWardMath.WardState { Amount = state.WardAmount, ExpireTime = state.WardExpire };

            // a hit can land after the pool has lapsed but before the next heartbeat catches it - answer
            // that here too, or Absorb's Remaining = default would erase the pool with no message at all
            if (SanguineWardMath.IsExpired(current, now))
            {
                state.WardAmount = 0;
                state.WardExpire = 0;

                Announce(defender, WardTransition.Faded, spec);

                return amount;
            }

            var result = SanguineWardMath.Absorb(current, amount, now);

            state.WardAmount = result.Remaining.Amount;
            state.WardExpire = result.Remaining.ExpireTime;

            // current was confirmed live and unexpired above, so reaching 0 here is the pool being spent
            // out by this hit, not it lapsing - that is exactly SHATTERED, never FADED
            if (result.Remaining.Amount == 0)
                Announce(defender, WardTransition.Shattered, spec);

            return result.DamageAfterWard;
        }

        /// <summary>
        /// The suffix appended to a spell hit's attacker line when the target had a live ward up before the
        /// hit - the ONE place this text is authored, called from both spell-hit call sites (SpellProjectile
        /// and life magic) so the wording can never drift between them. Pure and internal: a test in the
        /// same assembly (InternalsVisibleTo, ACE.Server.csproj:15) pins the exact string, ASCII only, no
        /// em/en dash.
        /// </summary>
        internal static string FormatHitSuffix(uint remaining)
        {
            return remaining == 0
                ? " (Ward shattered)"
                : $" (Ward: {remaining:N0} remaining)";
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

            // BEFORE any grant check: a pool seen here past its own expiry announces FADED and is zeroed,
            // exactly once, so an on=heartbeat record's regrant just below announces UP on the very same
            // tick - FADED then UP, in that order, never silently overwritten by the regrant.
            if (state.WardAmount > 0)
            {
                var expiring = new SanguineWardMath.WardState { Amount = state.WardAmount, ExpireTime = state.WardExpire };
                if (SanguineWardMath.IsExpired(expiring, now))
                {
                    state.WardAmount = 0;
                    state.WardExpire = 0;

                    Announce(creature, WardTransition.Faded, spec);
                }
            }

            var on = spec.GetFlags("on", MonsterEffectTrigger.Spawn);

            // one-shot on state.Announced: there is no spawn hook, so "spawn" is the first heartbeat
            if (on.HasFlag(MonsterEffectTrigger.Spawn) && !state.Announced)
            {
                if (Grant(creature, spec, ref state, now))
                    Announce(creature, WardTransition.Up, spec);

                state.Announced = true;
            }

            // no sentinel: an empty pool counts as expired, so the check is idempotent every tick
            if (on.HasFlag(MonsterEffectTrigger.Heartbeat))
            {
                var current = new SanguineWardMath.WardState { Amount = state.WardAmount, ExpireTime = state.WardExpire };
                if (SanguineWardMath.IsExpired(current, now))
                {
                    if (Grant(creature, spec, ref state, now))
                        Announce(creature, WardTransition.Up, spec);
                }
            }

            // one-shot on state.Stacks, a DIFFERENT field from spawn's sentinel - which is what lets
            // "on=spawn,hpbelow" grant twice, once for each reason, instead of the first latch eating both
            if (on.HasFlag(MonsterEffectTrigger.HpBelow) && state.Stacks == 0)
            {
                var trigger = spec.GetDouble("trigger", 0.0);
                if (ExecutionerAbility.IsExecuteRange(creature, trigger))
                {
                    if (Grant(creature, spec, ref state, now))
                        Announce(creature, WardTransition.Up, spec);

                    state.Stacks = 1;
                }
            }
        }

        /// <summary>
        /// Computes the grant amount from amount= or pcthp= (whichever Validate accepted) and hands it to
        /// SanguineWardMath.Grant with wardFraction=1.0, so the ward absorbs exactly that many points - the
        /// fraction parameter exists for Sanguine Ward's own "fraction of health lost" shape, which this
        /// effect has no use for.
        ///
        /// Returns whether a grant actually landed (amount &gt; 0 after every clamp), so callers can announce
        /// WardTransition.Up only on a real grant - never on a call that bailed out to secs=0, a rounded-to-0
        /// amount=/pcthp=, or a ward cap that clamped an already-tiny grant down to nothing.
        /// </summary>
        private static bool Grant(Creature creature, MonsterEffectSpec spec, ref MonsterEffectState state, double now)
        {
            var secs = spec.GetDouble("secs", 0.0);
            if (secs <= 0.0)
                return false;

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
                return false;

            var maxWard = (uint)Math.Max(0.0, Math.Round(creature.Health.MaxValue * MonsterEffectCaps.WardCap));
            amount = Math.Min(amount, maxWard);

            if (amount == 0)
                return false;

            var granted = SanguineWardMath.Grant(default, amount, 1.0, now, secs);

            state.WardAmount = granted.Amount;
            state.WardExpire = granted.ExpireTime;

            return true;
        }

        /// <summary>
        /// Unless silent=true, tells every player near this creature (its known players within
        /// LocalBroadcastRange, minus the usual dungeon-instance/Visibility/squelch filters) what just
        /// happened to its ward, and plays the matching shield-up/shield-down visual on the creature. Fires
        /// the test observation hook first (see AnnounceHookForTests) so the transition decision stays
        /// verifiable without a live landblock, then no-ops the network side when there is none
        /// (PhysicsObj/CurrentLandblock null - exactly the shape every unit-test creature is built in).
        ///
        /// Custom loop rather than WorldObject.EnqueueBroadcast(msg, range, squelchType), because that
        /// overload only applies the squelch check when the broadcaster is a Player (self != null); a
        /// creature broadcasting its own ward chat needs squelch enforced regardless of who is carrying it.
        /// The visual has no such requirement - squelch never applies to a script effect - so it goes out
        /// through the plain unlimited-range EnqueueBroadcast(GameMessage), the same call every other
        /// monster-effect visual (Creature_Combat's Dirty Fighting riders) already uses.
        /// </summary>
        private static void Announce(Creature creature, WardTransition transition, MonsterEffectSpec spec)
        {
            if (spec.GetBool("silent", false))
                return;

            string message;
            PlayScript script;

            switch (transition)
            {
                case WardTransition.Up:
                    message = $"{creature.Name} is surrounded by a shimmering ward!";
                    script = PlayScript.ShieldUpGrey;
                    break;
                case WardTransition.Shattered:
                    message = $"{creature.Name}'s ward shatters!";
                    script = PlayScript.ShieldDownGrey;
                    break;
                case WardTransition.Faded:
                    message = $"{creature.Name}'s ward fades away.";
                    script = PlayScript.ShieldDownGrey;
                    break;
                default:
                    return;
            }

            AnnounceHookForTests?.Invoke(creature, transition, message);

            if (creature.PhysicsObj == null || creature.CurrentLandblock == null)
                return;

            var isDungeon = creature.CurrentLandblock.PhysicsLandblock != null && creature.CurrentLandblock.PhysicsLandblock.IsDungeon;
            var rangeSquared = WorldObject.LocalBroadcastRange * WorldObject.LocalBroadcastRange;

            foreach (var player in creature.PhysicsObj.ObjMaint.GetKnownPlayersValuesAsPlayer())
            {
                if (player.Session == null)
                    continue;

                if (isDungeon && creature.Location.InstancedLandblock != player.Location.InstancedLandblock)
                    continue;

                if (creature.Visibility && !player.Adminvision)
                    continue;

                if (player.SquelchManager.Squelches.Contains(creature, ChatMessageType.CombatEnemy))
                    continue;

                var distSquared = creature.Location.SquaredDistanceTo(player.Location);
                if (distSquared > rangeSquared)
                    continue;

                player.Session.Network.EnqueueSend(new GameMessageSystemChat(message, ChatMessageType.CombatEnemy));
            }

            creature.EnqueueBroadcast(new GameMessageScript(creature.Guid, script));
        }
    }
}
