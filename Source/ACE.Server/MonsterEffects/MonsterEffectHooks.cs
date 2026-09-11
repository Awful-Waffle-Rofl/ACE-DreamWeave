using System;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.MonsterEffects
{
    // The hook vocabulary for monster combat effects. An effect implements IMonsterEffect plus whichever
    // hook interfaces its mechanic needs; MonsterEffectRegistry buckets handlers per hook at startup, and
    // MonsterEffectSet prefilters those buckets down to the effects one monster actually carries, so the
    // combat hot paths iterate only that monster's effects (and, for the overwhelming majority of monsters,
    // never get there at all - Creature.MonsterEffects is null).
    //
    // THE DESIGN RULE. Adding a new EFFECT touches exactly two things: a new handler file under
    // MonsterEffects/Effects, and one line in MonsterEffectRegistry.handWritten. It never touches core
    // combat code, and giving a monster that effect is one SQL row (PropertyString 9015).
    //
    // Adding a new HOOK is a rare, deliberate one-time edit: define the interface here, bucket it in
    // MonsterEffectRegistry, index it in MonsterEffectSet, and place ONE dispatch call at the core combat
    // site. Shared preconditions (system enabled, null-set check, cap clamping) live at the dispatch site,
    // not in individual handlers.
    //
    // Within one hook, handlers run in ascending IMonsterEffect.DispatchOrder, and within one DispatchOrder
    // in the order the records were authored on the weenie (MonsterEffectSet sorts once, stably, at
    // construction). It is NOT registration order - this comment claimed that for a while and the code never
    // did it, which is how the leech/execute bug below shipped.
    //
    // THE ORDER IS LOAD BEARING, so it is declared rather than inherited from whatever a content author
    // happened to type. Some effects CHANGE the damage figure a hook carries and some SIZE THEMSELVES from
    // it: execute and rangeramp add to DamageEvent.Damage while leech heals off it, ward and manabarrier
    // subtract from the incoming amount while reflect sends a fraction of it back. Left in authored order,
    // "leech ...; execute ..." and "execute ...; leech ..." are two different monsters with no warning and
    // no other difference. DispatchOrder puts every mutator ahead of every reader, so a reader always sees
    // the final figure.
    //
    // A new effect that neither reads nor changes a hook's damage figure needs nothing here - the default is
    // Neutral. One that does either must say so, next to the code that does it.
    //
    // Every hook takes its own MonsterEffectSpec (the authored args for THIS monster) and a ref to its own
    // slot in the monster's MonsterEffectState[] - the handler is a stateless singleton shared by every
    // monster in the world, so all mutable per-monster state lives in that struct and nowhere else.

    /// <summary>
    /// The axis a speed effect moves. Split rather than folded into one multiplier because the two are read
    /// at different sites and are capped independently.
    /// </summary>
    public enum MonsterSpeedAxis
    {
        Attack,
        Cast,
    }

    /// <summary>
    /// The axis a ramp accumulates on. A ramp effect holds stacks and publishes a multiplier here; any other
    /// effect reads it through <see cref="MonsterEffectSet.GetRampMultiplier"/> without knowing which effect
    /// (if any) produced it.
    /// </summary>
    public enum MonsterRampAxis
    {
        AttackSpeed,
        CastSpeed,
        MagicDamage,
        ProcChance,
        Avoid,
    }

    /// <summary>
    /// What made an effect fire, as authored in the "on=" arg. Flags rather than a plain enum because a
    /// single effect legitimately answers to several triggers at once - "on=hit,avoid" is one authored
    /// value that must reach two dispatch sites.
    /// </summary>
    [Flags]
    public enum MonsterEffectTrigger
    {
        None      = 0x00,
        Hit       = 0x01,
        SpellHit  = 0x02,
        Avoid     = 0x04,
        Spawn     = 0x08,
        Heartbeat = 0x10,
        HpBelow   = 0x20,
    }

    /// <summary>
    /// The three rungs of same-hook dispatch order, ascending. Constants rather than an enum so the scale
    /// has room between the rungs: a future effect that must run between two of these gets a number, not a
    /// renumbering of the enum and of every handler that named a member of it.
    /// </summary>
    public static class MonsterEffectDispatchOrder
    {
        /// <summary>Changes a damage figure other effects on the same hook read. Runs first.</summary>
        public const int DamageMutator = -100;

        /// <summary>Neither reads nor changes the hook's damage figure. The default.</summary>
        public const int Neutral = 0;

        /// <summary>Sizes itself from a damage figure other effects may have changed. Runs last.</summary>
        public const int DamageReader = 100;
    }

    /// <summary>
    /// Every monster effect handler. The handler is a singleton with no fields; <paramref name="spec"/>
    /// carries the authored args and the state struct carries everything mutable.
    /// </summary>
    public interface IMonsterEffect
    {
        /// <summary>
        /// Where this effect sits in same-hook dispatch order - see
        /// <see cref="MonsterEffectDispatchOrder"/> and the ordering paragraph at the top of this file.
        /// Defaulted so only the handlers that actually touch a hook's damage figure have to say anything,
        /// and so a test fake is unaffected.
        /// </summary>
        int DispatchOrder => MonsterEffectDispatchOrder.Neutral;

        /// <summary>
        /// The authored kind token, lowercase, e.g. "flatdamage". Matched case-insensitively by the parser
        /// and by <see cref="MonsterEffectRegistry.TryGetHandler"/>.
        /// </summary>
        string Kind { get; }

        /// <summary>
        /// Checks the authored args at BUILD time, once per (wcid, string), so a mis-authored weenie is a
        /// startup warning naming the wcid rather than a silently inert effect in combat. Return false with
        /// a message describing what is wrong and which arg is at fault.
        /// </summary>
        bool Validate(MonsterEffectSpec spec, out string error);
    }

    /// <summary>
    /// Reacts to (and may modify) this monster's landed melee/missile hit, after damage calculation and
    /// before the hit is applied - so the damage the defender sees and the damage reported both include the
    /// change.
    /// </summary>
    public interface IMonsterOutgoingHit : IMonsterEffect
    {
        void OnOutgoingHit(Creature attacker, Creature defender, DamageEvent damageEvent, MonsterEffectSpec spec, ref MonsterEffectState state);
    }

    /// <summary>
    /// Filters damage arriving at this monster, returning the amount that actually lands. A ward or damage
    /// shield subtracts here; a reflect returns the amount unchanged and sends damage back at the source.
    ///
    /// A handler that hits back MUST route through Creature.TryEnterReflect / ExitReflect - see the reflect
    /// latch in Creature_MonsterEffects.cs for why that is load bearing rather than defensive.
    /// </summary>
    public interface IMonsterIncomingDamage : IMonsterEffect
    {
        uint OnIncomingDamage(Creature defender, WorldObject source, DamageType damageType, uint amount, MonsterEffectSpec spec, ref MonsterEffectState state);
    }

    /// <summary>
    /// Contributes a chance for this monster to avoid an incoming attack outright, rolled before the normal
    /// evade check. GetAvoidChance is a pure read (it must not mutate anything a second call would see);
    /// OnAvoided is the notification that the roll actually won, and is where stacks and timers move.
    /// </summary>
    public interface IMonsterAvoidance : IMonsterEffect
    {
        double GetAvoidChance(Creature defender, Creature attacker, CombatType combatType, MonsterEffectSpec spec, ref MonsterEffectState state);

        void OnAvoided(Creature defender, Creature attacker, MonsterEffectSpec spec, ref MonsterEffectState state);
    }

    /// <summary>
    /// Reacts to a spell projectile landing, on either side of the exchange - this monster's spell striking
    /// a target, or a spell striking this monster. <paramref name="damage"/> is by ref so an effect can
    /// scale the spell's damage in place.
    /// </summary>
    public interface IMonsterSpellHit : IMonsterEffect
    {
        void OnSpellHit(Creature caster, Creature target, SpellProjectile projectile, ref float damage, MonsterEffectSpec spec, ref MonsterEffectState state);
    }

    /// <summary>
    /// Multiplies this monster's attack or cast speed. Multipliers from several effects compose by product
    /// and the total is clamped by monster_effect_speed_cap at the dispatch site, never here.
    /// </summary>
    public interface IMonsterSpeedMod : IMonsterEffect
    {
        double GetSpeedMultiplier(Creature creature, MonsterSpeedAxis axis, MonsterEffectSpec spec, ref MonsterEffectState state);
    }

    /// <summary>
    /// Fires when this monster finishes casting a spell - the hook a recast effect rides.
    /// </summary>
    public interface IMonsterCastHook : IMonsterEffect
    {
        void OnCastComplete(Creature caster, Spell spell, MonsterEffectSpec spec, ref MonsterEffectState state);
    }

    /// <summary>
    /// Fires on the monster's regular heartbeat - periodic ticks (damage over time, ward refresh) and
    /// lazy expiry of anything the combat hooks left behind.
    /// </summary>
    public interface IMonsterHeartbeat : IMonsterEffect
    {
        void OnHeartbeat(Creature creature, MonsterEffectSpec spec, ref MonsterEffectState state);
    }

    /// <summary>
    /// Publishes a ramp multiplier on one axis. This is the ONE indirection that lets an effect read
    /// "how ramped is this monster right now" without naming the effect that ramps: the reader calls
    /// <see cref="MonsterEffectSet.GetRampMultiplier"/> with an axis, and the set asks every effect that
    /// implements this interface. Without it, every consumer of a ramp would have to know the ramp kind by
    /// name, which is exactly the coupling the registry exists to avoid.
    /// </summary>
    public interface IMonsterRampSource : IMonsterEffect
    {
        /// <summary>
        /// True when THIS spec (not merely this handler) ramps the given axis - the axis is an authored arg,
        /// so one handler serves every axis and only the spec knows which one it was pointed at.
        /// </summary>
        bool ProvidesRamp(MonsterRampAxis axis, MonsterEffectSpec spec);

        /// <summary>
        /// The multiplier this spec currently contributes on that axis; 1.0 when nothing is held. A pure
        /// read - expiry that needs to be observed belongs on the heartbeat hook.
        /// </summary>
        double GetRampMultiplier(MonsterRampAxis axis, MonsterEffectSpec spec, ref MonsterEffectState state);
    }
}
