using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.ClassAbilities;
using ACE.Server.Entity;
using ACE.Server.MonsterEffects;
using ACE.Server.MonsterEffects.Effects;

namespace ACE.Server.WorldObjects
{
    partial class Creature
    {
        // ---- Monster combat effects: attachment ---------------------------------------------------
        //
        // A monster's effects are authored as ONE string on its weenie (PropertyString 9015
        // MonsterCombatEffects) and resolved here at construction, the same shape as WorldEventObjective a
        // few lines up in SetEphemeralValues: read the weenie once, cache the answer in a field, and never
        // touch the property dictionary again on a hot path.
        //
        // MonsterEffects is NULL for every monster that authored nothing, which is nearly all of them. That
        // is the whole point of doing this here - every future dispatch site is one null check on a field,
        // not a dictionary lookup and a parse.
        //
        // Never persisted, and correctly so: the effects belong to the WEENIE, so a creature restored from
        // the shard rebuilds them from the current weenie and immediately picks up an authoring change,
        // exactly like Weakened Blood in the sibling file rebuilds from nothing.

        /// <summary>
        /// This monster's resolved combat effects, or NULL when its weenie carries no PropertyString 9015
        /// MonsterCombatEffects (or carries one that resolved to nothing). Immutable and SHARED with every
        /// other creature of the same wcid and authored string - per-creature mutable state lives in
        /// <see cref="monsterEffectState"/>, never here.
        /// </summary>
        public MonsterEffectSet MonsterEffects { get; private set; }

        /// <summary>
        /// This creature's own state, one slot per entry in <see cref="MonsterEffects"/> and indexed
        /// identically. Allocated with the set and never resized, so an index handed to a hook stays valid
        /// for the creature's life.
        /// </summary>
        private MonsterEffectState[] monsterEffectState;

        /// <summary>
        /// Exposed to the test assembly (InternalsVisibleTo) so a test can assert that two creatures of one
        /// wcid share the immutable set but hold DISTINCT state arrays - the invariant that keeps one
        /// drudge's ramp stacks out of the drudge next to it.
        /// </summary>
        internal MonsterEffectState[] MonsterEffectStates => monsterEffectState;

        /// <summary>
        /// Parse-once cache, keyed by (wcid, authored string) rather than wcid alone so an admin editing the
        /// weenie's property live gets a fresh resolve instead of the stale one. A landblock activating with
        /// two hundred creatures of one wcid parses once and hands out the same immutable set two hundred
        /// times.
        ///
        /// A null VALUE is cached deliberately: it records "this string resolves to nothing", so a weenie
        /// with a typo does not re-parse and re-log on every spawn.
        /// </summary>
        private static readonly ConcurrentDictionary<(uint Wcid, string Spec), MonsterEffectSet> monsterEffectSetCache
            = new ConcurrentDictionary<(uint, string), MonsterEffectSet>();

        /// <summary>
        /// Resolves this creature's authored effects. Called once from SetEphemeralValues.
        ///
        /// Reads the weenie property BEFORE anything else, and reads no server tunable unless the property
        /// is present - the overwhelmingly common path costs one dictionary miss. In particular the master
        /// switch is NOT consulted here: it is a dispatch-site check, so toggling monster_effects_enabled
        /// back on does not require every monster in the world to be rebuilt.
        /// </summary>
        private void BuildMonsterEffects()
        {
            var authored = GetProperty(PropertyString.MonsterCombatEffects);

            if (string.IsNullOrWhiteSpace(authored))
                return;

            AttachMonsterEffects(authored);
        }

        /// <summary>
        /// Resolves <paramref name="spec"/> through the parse-once cache and attaches it, with a fresh state
        /// array. The ONE attach path, shared by construction (<see cref="BuildMonsterEffects"/>) and by a
        /// runtime overlay (<see cref="ApplyMonsterEffectOverlay"/>), so an overlay is cached, validated,
        /// truncated and logged exactly as an authored spec is. Returns false, leaving whatever was attached
        /// before untouched, when the spec resolves to nothing.
        /// </summary>
        private bool AttachMonsterEffects(string spec)
        {
            if (string.IsNullOrWhiteSpace(spec))
                return false;

            // GamePiece attacks return before the damage calculation entirely (Monster_Melee.cs: the
            // WeenieType.GamePiece branch deals target.Health.Current and returns), so every outgoing-hit
            // effect on one would be silently inert. Refuse it loudly at build time instead of shipping a
            // monster whose authored effects never fire and never say why.
            if (WeenieType == WeenieType.GamePiece)
            {
                log.Warn($"Monster effects: wcid {WeenieClassId} ({Name}) is a GamePiece, whose attacks bypass the damage pipeline - its authored effects would never fire. Ignoring '{spec}'.");
                return false;
            }

            var set = monsterEffectSetCache.GetOrAdd((WeenieClassId, spec), key => ResolveMonsterEffects(key.Wcid, key.Spec));

            if (set == null)
                return false;

            MonsterEffects = set;
            monsterEffectState = new MonsterEffectState[set.Count];

            return true;
        }

        /// <summary>
        /// Adds <paramref name="extra"/> - one or more records in the same PropertyString 9015 grammar - on top
        /// of this creature's AUTHORED effects, for this creature only. The ML digsite Boss Rush boss uses it
        /// to carry a mechanic its weenie does not author.
        ///
        /// HOW IT FITS THE CACHE. The spec is read once, at construction, and resolved sets are cached by
        /// (wcid, spec string). The overlay composes "authored; extra" (<see cref="ComposeMonsterEffectSpec"/>)
        /// and resolves THAT string through the same cache and the same ResolveMonsterEffects path, so it is
        /// parsed once per distinct combination, shared immutably by every creature carrying the same
        /// combination, and never pollutes the plain authored entry other creatures of the wcid resolve to.
        /// The weenie property itself is NOT rewritten.
        ///
        /// STATE. A fresh state array is allocated with the new set, exactly as construction does, so call
        /// this BEFORE the creature enters the world: any per-effect state accrued before the overlay (a ramp
        /// stack, a spawn-latched ward) is discarded with the old array. An empty or whitespace
        /// <paramref name="extra"/> changes nothing. Returns whether a set was attached.
        /// </summary>
        public bool ApplyMonsterEffectOverlay(string extra)
        {
            if (string.IsNullOrWhiteSpace(extra))
                return false;

            return AttachMonsterEffects(ComposeMonsterEffectSpec(GetProperty(PropertyString.MonsterCombatEffects), extra));
        }

        /// <summary>
        /// "authored; extra", tolerating a blank authored spec and a trailing separator on it. Pure, so the
        /// composition is testable without a creature.
        /// </summary>
        internal static string ComposeMonsterEffectSpec(string authored, string extra)
        {
            var add = extra?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(authored))
                return add;

            if (add.Length == 0)
                return authored;

            return authored.TrimEnd().TrimEnd(';').TrimEnd() + "; " + add;
        }

        /// <summary>
        /// The cache miss path: parse, report every rejected record, truncate to
        /// monster_effect_max_per_monster, and resolve handlers. Runs once per (wcid, string), so the
        /// warnings below are one per authoring mistake rather than one per spawn.
        /// </summary>
        private static MonsterEffectSet ResolveMonsterEffects(uint wcid, string authored)
        {
            MonsterEffectParser.Parse(authored, out var specs, out var errors);

            foreach (var error in errors)
                log.Warn($"Monster effects: wcid {wcid} - {error}");

            var max = MonsterEffectCaps.MaxPerMonster;

            if (max > 0 && specs.Count > max)
            {
                log.Warn($"Monster effects: wcid {wcid} authors {specs.Count} effects but monster_effect_max_per_monster is {max}; dropping the last {specs.Count - max}.");
                specs = specs.GetRange(0, max);
            }

            var buildErrors = new List<string>();

            var set = MonsterEffectSet.Build(specs, buildErrors);

            foreach (var error in buildErrors)
                log.Warn($"Monster effects: wcid {wcid} - {error}");

            return set;
        }

        // ---- Reflect re-entry latch ----------------------------------------------------------------
        //
        // LOAD BEARING, NOT DEFENSIVE. A monster reflect effect answers incoming damage by calling
        // player.TakeDamage(...). Player.TakeDamage unconditionally runs ApplyIncomingDamageClassAbilities,
        // which reaches Player.ApplyThornsReflect, which calls attacker.TakeDamage(...) straight back at the
        // monster. Monster reflect then answers that, and the two bounce until the stack ends. This is a
        // genuine infinite loop between two systems that are each individually correct, not a hypothetical.
        //
        // Deferring either side through an ActionChain does NOT fix it. The chain removes the stack overflow
        // and leaves the loop running, so the symptom changes from a crash into two creatures trading damage
        // forever across ticks - strictly worse, because it is no longer obvious.
        //
        // The latch is [ThreadStatic] because landblocks tick in parallel (LandblockManagerParallelOptions):
        // a plain static would let one landblock's reflect suppress another's. One monster's reflect and one
        // player's thorns always resolve on the same thread, inside the same call stack, which is exactly the
        // scope this covers.
        //
        // Depth 1, not a counter with a limit: a reflect of a reflect is never wanted, so the first
        // re-entrant attempt is refused outright.

        [ThreadStatic]
        private static int reflectDepth;

        /// <summary>
        /// Claims the reflect slot for the current thread. Returns FALSE when reflect damage is already
        /// being resolved further up this call stack - the caller must then apply no damage back at all.
        /// Every success must be paired with <see cref="ExitReflect"/> in a finally block.
        /// </summary>
        public static bool TryEnterReflect()
        {
            if (reflectDepth > 0)
                return false;

            reflectDepth++;
            return true;
        }

        /// <summary>
        /// Releases the reflect slot claimed by <see cref="TryEnterReflect"/>.
        /// </summary>
        public static void ExitReflect()
        {
            if (reflectDepth > 0)
                reflectDepth--;
        }

        // ---- Recast re-entry guard -----------------------------------------------------------------
        //
        // The cast-hook analogue of the reflect latch, load bearing for the same reason: a recast effect
        // answers "this monster finished a cast" by starting another cast, whose completion asks the same
        // question of the same handler. Left alone that is unbounded, so the depth of nested cast-hook
        // dispatches is counted here and bounded by monster_effect_recast_cap.
        //
        // TWO HALVES, because either alone is insufficient. CanChainMonsterEffectCast is what a recast
        // handler ASKS before chaining, and is the number the tunable is really about - a cap of 0 means
        // "no chaining", not "no cast hook". The entry check in OnMonsterEffectCastComplete is the BACKSTOP
        // for a handler that never asks: the completion of an unauthorised chained cast is refused its
        // dispatch, so the chain stops at one unauthorised link instead of running until the stack ends.
        //
        // [ThreadStatic] for the same reason as the reflect latch - landblocks tick in parallel, so a plain
        // static would let one landblock's cast chain bound another's.
        //
        // NOTE the limit of any stack-scoped guard: it counts SYNCHRONOUS nesting. A handler that defers its
        // recast through an ActionChain has already left this scope when the new cast begins and is not
        // counted - the same trap the reflect latch above describes, and the same answer: chain
        // synchronously, from inside the hook.

        [ThreadStatic]
        private static int castHookDepth;

        /// <summary>
        /// The most chained casts one completed cast may lead to, floored at 0 so a mis-set tunable can
        /// disable chaining but never the cast hook itself.
        /// </summary>
        private static int RecastDepthLimit => Math.Max(0, MonsterEffectCaps.RecastCap);

        /// <summary>
        /// TRUE while a cast hook may start another cast - the chained casts already on this stack are
        /// below monster_effect_recast_cap. A recast handler reads this BEFORE chaining; reading it
        /// anywhere else is meaningless, because the depth it measures only exists inside a dispatch.
        /// </summary>
        public static bool CanChainMonsterEffectCast => castHookDepth <= RecastDepthLimit;

        /// <summary>
        /// Claims one level of the chained-cast budget WITHOUT dispatching anything, and returns FALSE when
        /// the budget is already spent. Every success must be paired with
        /// <see cref="ExitMonsterEffectCastChain"/> in a finally block.
        ///
        /// THIS IS WHAT LETS A CHAINING HANDLER ANSWER ITS OWN CAST WITHOUT RE-ENTERING THE FULL HOOK LIST.
        /// A handler that chained by calling <see cref="OnMonsterEffectCastComplete"/> again re-ran EVERY
        /// cast-hook record the monster carries, so two chaining records branched by a factor of two per
        /// level and a monster got far more chained casts than monster_effect_recast_cap describes. Claiming
        /// the level here instead keeps the shared depth (and therefore the cap) honest while the handler
        /// loops over itself alone.
        /// </summary>
        public static bool TryEnterMonsterEffectCastChain()
        {
            if (castHookDepth > RecastDepthLimit)
                return false;

            castHookDepth++;
            return true;
        }

        /// <summary>
        /// Releases one level claimed by <see cref="TryEnterMonsterEffectCastChain"/>.
        /// </summary>
        public static void ExitMonsterEffectCastChain()
        {
            if (castHookDepth > 0)
                castHookDepth--;
        }

        // ---- Combat-site dispatch ------------------------------------------------------------------
        //
        // One method per hook, and one CALL per combat site. The core combat files get a single line; every
        // precondition (the null set, the master switch, the empty hook bucket), the iteration, the state
        // plumbing and the cap clamping live here - the same division Player.ApplyOutgoingDamageClassAbilities
        // uses against Player.DamageTarget.
        //
        // Every one of these early-outs in the SAME order, and the order is the point:
        //   1. MonsterEffects == null - one null check on a field, and the answer for nearly every monster
        //      in the world. Deliberately first, so the overwhelmingly common path never reads a tunable.
        //   2. MonsterEffectCaps.Enabled - the master switch, read live at dispatch so toggling it back on
        //      needs no relog and no landblock reset.
        //   3. the prefiltered index array for this hook - a monster carrying three effects, none of which
        //      touch this hook, walks an empty array rather than testing three interfaces.

        /// <summary>
        /// IMonsterOutgoingHit dispatch - called from DamageEvent.CalculateDamage after the damage
        /// calculation and before the event is returned, so the damage the defender takes and the damage
        /// reported both include whatever a rider changed. Covers monster melee (Monster_Melee) and monster
        /// missile (ProjectileCollisionHelper) at once: both resolve through the same DamageEvent.
        /// </summary>
        public void ApplyOutgoingHitMonsterEffects(Creature defender, DamageEvent damageEvent)
        {
            var effects = MonsterEffects;

            if (effects == null || !MonsterEffectCaps.Enabled)
                return;

            var indexes = effects.OutgoingHitIndexes;

            if (indexes.Count == 0)
                return;

            var state = monsterEffectState;

            for (var i = 0; i < indexes.Count; i++)
            {
                var index = indexes[i];
                var entry = effects[index];

                ((IMonsterOutgoingHit)entry.Handler).OnOutgoingHit(this, defender, damageEvent, entry.Spec, ref state[index]);
            }
        }

        /// <summary>
        /// IMonsterAvoidance dispatch - the non-player mirror of Player.RollClassAbilityAvoidance, rolled
        /// from the same slot in DamageEvent.DoCalculateDamage (before the evade roll). TRUE when this
        /// monster avoids the attack outright.
        ///
        /// ONE POOLED ROLL, not one roll per effect. Independent rolls compound past the ceiling - two 40%
        /// effects would avoid 64% of hits with monster_effect_avoidance_cap at 50% - so the contributions
        /// are summed, clamped once against the cap, and rolled once.
        ///
        /// GRANTERS AND REACTORS ARE TWO DIFFERENT THINGS, and this hook has one method for each.
        /// GetAvoidChance is the GRANTER half - only an effect that gives the monster a reason to avoid
        /// (avoid) returns anything but 0.0 from it. OnAvoided is the REACTOR half - riposte, reflect
        /// on=avoid and debuff on=avoid all answer an avoid that has ALREADY happened and supply none of it
        /// themselves. So when the pooled roll wins, EVERY carried handler is notified: a reactor must not
        /// have to buy its notification with avoidance chance it does not grant.
        ///
        /// DO NOT "OPTIMISE" THIS BACK INTO A WEIGHTED PICK. Attributing a pooled win to one contributor
        /// chosen by a second GetAvoidChance pass gives every pure-0.0 reactor zero width on that wheel, so
        /// its OnAvoided can never be selected no matter what the monster carries alongside it - which left
        /// riposte, reflect on=avoid and debuff on=avoid silently inert in live play for a whole phase. A
        /// granter that wants to react at less than the pooled rate throttles itself inside its own
        /// OnAvoided, where it can see its own state; the dispatch site does not do it on the granter's
        /// behalf.
        /// </summary>
        public bool RollMonsterEffectAvoidance(Creature attacker, CombatType combatType)
        {
            var effects = MonsterEffects;

            if (effects == null || !MonsterEffectCaps.Enabled)
                return false;

            var indexes = effects.AvoidanceIndexes;

            if (indexes.Count == 0)
                return false;

            var state = monsterEffectState;

            var total = 0.0;

            for (var i = 0; i < indexes.Count; i++)
            {
                var index = indexes[i];
                var entry = effects[index];

                total += Math.Max(0.0, ((IMonsterAvoidance)entry.Handler).GetAvoidChance(this, attacker, combatType, entry.Spec, ref state[index]));
            }

            if (total <= 0.0)
                return false;

            // ramp axis=avoid scales the pooled grant before the cap. The ramp seam is deliberately
            // uncapped at the source (see MonsterEffectSet.GetRampMultiplier) precisely so the cap can be
            // applied once here, where the number is consumed.
            total *= effects.GetRampMultiplier(MonsterRampAxis.Avoid, state);

            var chance = Math.Min(total, MonsterEffectCaps.AvoidanceCap);

            if (chance <= 0.0 || ThreadSafeRandom.Next(0.0f, 1.0f) > chance)
                return false;

            // every carried handler, granter and reactor alike - see the doc comment above for why this is
            // not a weighted pick
            for (var i = 0; i < indexes.Count; i++)
            {
                var index = indexes[i];
                var entry = effects[index];

                ((IMonsterAvoidance)entry.Handler).OnAvoided(this, attacker, entry.Spec, ref state[index]);
            }

            return true;
        }

        /// <summary>
        /// IMonsterIncomingDamage dispatch - returns the amount that actually lands after every filter this
        /// monster carries (a ward subtracting, a reflect answering). Called from all THREE paths by which
        /// damage reaches a monster, because they share no common sink:
        ///   Creature.TakeDamage        - melee, missile, hotspots, damage over time
        ///   SpellProjectile.DamageTarget - war/void spell projectiles, which write the vital directly
        ///   WorldObject.HandleCastSpell_Boost - life magic (Harm, Drain Health), likewise
        /// A defensive effect wired at only one or two of the three is silently partial - the identical trap
        /// Mana Barrier and Sanguine Ward each hit on the player side.
        ///
        /// THE REFLECT LATCH IS HELD ACROSS THE WHOLE DISPATCH, not just around a reflect. That is a
        /// deliberate trade: a monster resolving reflected damage runs none of its incoming-damage effects,
        /// so a ward does not absorb a hit that its own reflect caused, and in exchange no handler can
        /// reopen the loop by forgetting the latch exists. See the latch comment above for why the loop is
        /// real rather than hypothetical.
        ///
        /// <paramref name="origin"/> is REQUIRED, not defaulted, so every one of the three paths has to say
        /// whether it is delivering an initial direct hit or something secondary (a DoT tick, a proc, a
        /// splash child). It is passed through untouched to every handler; see IncomingDamageOrigin.
        /// </summary>
        public uint AbsorbMonsterEffectDamage(WorldObject source, DamageType damageType, uint amount, IncomingDamageOrigin origin)
        {
            // a Player never carries monster effects; stated rather than left to the null check because
            // TakeDamage below is reachable with a Player receiver
            if (this is Player)
                return amount;

            var effects = MonsterEffects;

            if (effects == null || !MonsterEffectCaps.Enabled)
                return amount;

            var indexes = effects.IncomingDamageIndexes;

            if (indexes.Count == 0)
                return amount;

            if (!TryEnterReflect())
                return amount;

            try
            {
                var state = monsterEffectState;

                for (var i = 0; i < indexes.Count; i++)
                {
                    var index = indexes[i];
                    var entry = effects[index];

                    amount = ((IMonsterIncomingDamage)entry.Handler).OnIncomingDamage(this, source, damageType, amount, origin, entry.Spec, ref state[index]);
                }

                return amount;
            }
            finally
            {
                ExitReflect();
            }
        }

        /// <summary>
        /// TRUE when this creature carries at least one ward effect entry (regardless of whether it is
        /// currently up), with <paramref name="remaining"/> set to the sum of WardAmount across every ward
        /// entry that is not expired right now (0 if every ward this creature carries is spent or lapsed).
        /// A monster can author more than one ward record, so this SUMS rather than reports the first one
        /// found - the same "pool everything this hook carries" shape RollMonsterEffectAvoidance uses.
        ///
        /// Used by the spell-hit attacker line (SpellProjectile / life magic) to decide whether "why did
        /// that hit for 0" needs the ward called out - captured BEFORE AbsorbMonsterEffectDamage runs, since
        /// that call can zero the pool as a side effect of the very hit being reported.
        ///
        /// Same three early-outs, same order, as every other dispatch above: a Player never carries monster
        /// effects, the null set is the answer for nearly every monster, and the master switch is read live
        /// so toggling it back on needs no relog.
        /// </summary>
        public bool TryGetMonsterWardRemaining(out uint remaining)
        {
            remaining = 0;

            if (this is Player)
                return false;

            var effects = MonsterEffects;

            if (effects == null || !MonsterEffectCaps.Enabled)
                return false;

            var indexes = effects.IncomingDamageIndexes;

            if (indexes.Count == 0)
                return false;

            var state = monsterEffectState;

            var now = Time.GetUnixTime();

            var found = false;
            uint total = 0;

            for (var i = 0; i < indexes.Count; i++)
            {
                var index = indexes[i];
                var entry = effects[index];

                if (entry.Handler is not WardEffect)
                    continue;

                found = true;

                var current = new SanguineWardMath.WardState { Amount = state[index].WardAmount, ExpireTime = state[index].WardExpire };

                if (!SanguineWardMath.IsExpired(current, now))
                    total += state[index].WardAmount;
            }

            remaining = total;
            return found;
        }

        /// <summary>
        /// IMonsterSpellHit dispatch for the CASTER side - this monster's spell projectile landing on a
        /// target. Called from SpellProjectile.OnCollideObject BEFORE the damage is applied, so a
        /// magic-damage ramp scales the number the target actually takes; the player mirror
        /// (ApplySpellHitClassAbilities) sits at the same site and runs after the hit, because nothing it
        /// does scales the hit.
        ///
        /// The defender side of this hook is NOT wired: spell damage arriving at a monster is filtered by
        /// IMonsterIncomingDamage at SpellProjectile.DamageTarget instead.
        /// </summary>
        public void ApplySpellHitMonsterEffects(Creature target, SpellProjectile projectile, ref float damage)
        {
            var effects = MonsterEffects;

            if (effects == null || !MonsterEffectCaps.Enabled)
                return;

            var indexes = effects.SpellHitIndexes;

            if (indexes.Count == 0)
                return;

            var state = monsterEffectState;

            for (var i = 0; i < indexes.Count; i++)
            {
                var index = indexes[i];
                var entry = effects[index];

                ((IMonsterSpellHit)entry.Handler).OnSpellHit(this, target, projectile, ref damage, entry.Spec, ref state[index]);
            }
        }

        /// <summary>
        /// This monster's composed speed multiplier on one axis: the product of every IMonsterSpeedMod it
        /// carries AND whatever it currently holds on the matching ramp axis, clamped against
        /// monster_effect_speed_cap. 1.0 when it carries neither, which is also what a cap of 1.0 or lower
        /// pins every monster to.
        ///
        /// THE RAMP SEAM IS PART OF THE COMPOSITION, not a separate number some other caller adds later.
        /// ramp axis=attackspeed IS frenzy and ramp axis=castspeed is its cast-side twin; both hold their
        /// magnitude through IMonsterRampSource rather than IMonsterSpeedMod, so a composer that read only
        /// the speed-mod bucket left the flagship effect accruing and lapsing stacks that changed nothing at
        /// all. MonsterSpeedAxis and MonsterRampAxis stay separate vocabularies on purpose - the ramp axes
        /// include magicdamage, procchance and avoid, which have no speed meaning - so the two are bridged
        /// by the explicit map in <see cref="RampAxisFor"/> rather than by casting one enum to the other.
        ///
        /// Clamped SYMMETRICALLY - [1/cap, cap] rather than [0, cap] - so the cap bounds a slow as tightly
        /// as a haste, and so no consumer can be handed a zero (the cast site divides by this). ONE clamp,
        /// applied last to the combined product: clamping the speed-mod product and the ramp separately
        /// would let each half sit at the cap and reach cap-squared together.
        /// </summary>
        private double GetMonsterEffectSpeedMultiplier(MonsterSpeedAxis axis)
        {
            var effects = MonsterEffects;

            if (effects == null || !MonsterEffectCaps.Enabled)
                return 1.0;

            var indexes = effects.SpeedModIndexes;

            // both buckets, because either one alone can move this axis
            if (indexes.Count == 0 && effects.RampSourceIndexes.Count == 0)
                return 1.0;

            var state = monsterEffectState;

            var mult = 1.0;

            for (var i = 0; i < indexes.Count; i++)
            {
                var index = indexes[i];
                var entry = effects[index];

                mult *= ((IMonsterSpeedMod)entry.Handler).GetSpeedMultiplier(this, axis, entry.Spec, ref state[index]);
            }

            mult *= effects.GetRampMultiplier(RampAxisFor(axis), state);

            var cap = Math.Max(1.0, MonsterEffectCaps.SpeedCap);

            return Math.Clamp(mult, 1.0 / cap, cap);
        }

        /// <summary>
        /// The ramp axis a speed axis reads. MonsterSpeedAxis has exactly two members, so the ternary is
        /// total; if a third is ever added, add its ramp axis here at the same time rather than letting it
        /// fall through to the attack ramp.
        /// </summary>
        private static MonsterRampAxis RampAxisFor(MonsterSpeedAxis axis) =>
            axis == MonsterSpeedAxis.Cast ? MonsterRampAxis.CastSpeed : MonsterRampAxis.AttackSpeed;

        /// <summary>
        /// The chance one of this monster's proc effects actually rolls against: its authored chance= scaled
        /// by whatever ramp axis=procchance the monster is holding right now, then clamped by
        /// monster_effect_proc_chance_cap - the order that tunable's own description states ("after every
        /// ramp and bonus"). Capping AFTER the ramp is what keeps the ceiling a ceiling rather than an
        /// advisory number a long enough ramp can walk past.
        ///
        /// Every handler that rolls a chance= reads its number through here instead of clamping against the
        /// cap itself, so no effect can end up wired to the cap but not to the ramp.
        /// </summary>
        public double ScaleMonsterEffectProcChance(double chance)
        {
            var effects = MonsterEffects;

            if (effects != null && MonsterEffectCaps.Enabled)
                chance *= effects.GetRampMultiplier(MonsterRampAxis.ProcChance, monsterEffectState);

            return Math.Min(chance, MonsterEffectCaps.ProcChanceCap);
        }

        /// <summary>
        /// IMonsterSpeedMod on the attack axis - called from Creature.GetAnimSpeed, in the branch the
        /// player's ApplyClassAbilityAttackSpeed does not take. Multiplies the animation speed, so a larger
        /// value is a faster attack.
        /// </summary>
        public float ApplyMonsterEffectAttackSpeed(float animSpeed)
        {
            if (MonsterEffects == null)
                return animSpeed;

            return (float)(animSpeed * GetMonsterEffectSpeedMultiplier(MonsterSpeedAxis.Attack));
        }

        /// <summary>
        /// IMonsterSpeedMod on the cast axis - called from Creature.MagicAttack for the pre-cast and
        /// post-cast times. A DURATION, so the multiplier DIVIDES: a 2.0 cast-speed multiplier halves both
        /// halves of the cadence.
        ///
        /// Scales the SCHEDULING only. The windup animation is broadcast by PreCastMotion at the fixed
        /// PreCastSpeed, so a future speed handler that wants the animation to keep pace with the cadence
        /// has to scale there as well; this phase ships no such handler, and with none registered the
        /// multiplier is 1.0 and the two cannot disagree.
        /// </summary>
        public float ScaleMonsterEffectCastTime(float castTime)
        {
            if (MonsterEffects == null)
                return castTime;

            return (float)(castTime / GetMonsterEffectSpeedMultiplier(MonsterSpeedAxis.Cast));
        }

        /// <summary>
        /// IMonsterCastHook dispatch - called from Creature.MagicAttack once the spell has been cast. Bounded
        /// by the recast guard above: see <see cref="CanChainMonsterEffectCast"/> for the half a chaining
        /// handler must read, and the entry check here for the backstop that bounds one which does not.
        /// </summary>
        public void OnMonsterEffectCastComplete(Spell spell)
        {
            var effects = MonsterEffects;

            if (effects == null || !MonsterEffectCaps.Enabled)
                return;

            var indexes = effects.CastHookIndexes;

            if (indexes.Count == 0)
                return;

            // backstop: this dispatch is already nested deeper than the cap allows, so the cast that
            // produced it was never authorised. Refusing here is what ends the chain.
            if (castHookDepth > RecastDepthLimit)
                return;

            castHookDepth++;

            try
            {
                var state = monsterEffectState;

                for (var i = 0; i < indexes.Count; i++)
                {
                    var index = indexes[i];
                    var entry = effects[index];

                    ((IMonsterCastHook)entry.Handler).OnCastComplete(this, spell, entry.Spec, ref state[index]);
                }
            }
            finally
            {
                castHookDepth--;
            }
        }

        /// <summary>
        /// IMonsterHeartbeat dispatch - called from Creature.Heartbeat, the ~5s clock that runs whether or
        /// not the monster is awake. Deliberately NOT the 0.2s AI tick, which does not run while a monster
        /// is idle, and not the emote heartbeat, which returns early once the monster has aggro; a periodic
        /// effect on either would stop at exactly the wrong moment.
        /// </summary>
        public void MonsterEffectHeartbeat()
        {
            var effects = MonsterEffects;

            if (effects == null || !MonsterEffectCaps.Enabled)
                return;

            var indexes = effects.HeartbeatIndexes;

            if (indexes.Count == 0)
                return;

            var state = monsterEffectState;

            for (var i = 0; i < indexes.Count; i++)
            {
                var index = indexes[i];
                var entry = effects[index];

                ((IMonsterHeartbeat)entry.Handler).OnHeartbeat(this, entry.Spec, ref state[index]);
            }
        }

        /// <summary>
        /// TRUE when this creature's digsite "immune" effect is filtering incoming melee/missile damage to
        /// zero RIGHT NOW - P_DigsiteImmune is set, the creature actually carries an ImmuneEffect record, AND
        /// the monster_effects_enabled master switch is on. All three together, not any one alone:
        /// P_DigsiteImmune with no attached effect would report a hit as zero for a creature
        /// ImmuneEffect.OnIncomingDamage never touches; a carried-but-not-yet-toggled effect (a boss between
        /// phases) must keep reporting real damage; and with the master switch off, AbsorbMonsterEffectDamage
        /// itself short-circuits and hands the full amount through (MonsterEffectImmuneTests,
        /// "the master switch also turns the immunity off") - this predicate has to agree or the notification
        /// would report 0 for a hit that actually landed in full.
        ///
        /// Read from Player.DamageTarget BEFORE (or from the same pre-TakeDamage state as) the hit is
        /// resolved, so the attacker notification's reported damage matches what ImmuneEffect actually
        /// filtered - see the doc comment on ImmuneEffect.OnIncomingDamage for why P_DigsiteImmune is the
        /// authority and not PropertyBool.Invincible. Scoped to the immune effect only: every other incoming-
        /// damage filter (ward, mana barrier, reflect) keeps reporting the pre-filter figure, unchanged.
        /// </summary>
        public bool IsDigsiteImmuneActive()
        {
            if (!P_DigsiteImmune)
                return false;

            var effects = MonsterEffects;

            if (effects == null || !MonsterEffectCaps.Enabled)
                return false;

            var indexes = effects.IncomingDamageIndexes;

            for (var i = 0; i < indexes.Count; i++)
            {
                if (effects[indexes[i]].Handler is ImmuneEffect)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Test seam (InternalsVisibleTo, ACE.Server.csproj:15): attaches a set built directly from handler
        /// instances, which is the only way to exercise dispatch while MonsterEffectRegistry.handWritten is
        /// empty. Allocates the matching state array, exactly as BuildMonsterEffects does.
        /// </summary>
        internal void AttachMonsterEffectsForTest(MonsterEffectSet set)
        {
            MonsterEffects = set;
            monsterEffectState = set == null ? null : new MonsterEffectState[set.Count];
        }
    }
}
