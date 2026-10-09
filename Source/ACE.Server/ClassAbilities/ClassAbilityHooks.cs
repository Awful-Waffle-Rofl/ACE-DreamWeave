using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities
{
    // The hook vocabulary for class ability effects. A skill implements IClassAbility plus whichever
    // hook interfaces its mechanic needs; ClassAbilityRegistry buckets handlers per hook at startup,
    // and Player_ClassAbilities caches each player's *learned* handlers per hook, so the combat hot
    // paths iterate only skills the player actually has (usually an empty array).
    //
    // Adding a new skill never touches core combat code - only a new handler class. Adding a new
    // *hook* is a rare, deliberate one-time edit: define the interface here, bucket it in
    // ClassAbilityRegistry, cache it in Player_ClassAbilities, and place one dispatch call at the
    // core site. Shared preconditions (system enabled, PvP exclusion, dead/self attacker checks)
    // live in the Player dispatch methods, not in individual handlers.
    //
    // Within one hook, handlers run in ClassAbilityRegistry registration order. Keep effects
    // order-independent (e.g. flat additive damage); if an order-sensitive effect is ever needed,
    // introduce an explicit priority/phase rather than relying on registration order.
    //
    // Two hooks have taken that escape hatch. IPreWriteDamageAbility, because mitigation genuinely cannot
    // be order-independent - see its own doc comment and DamageMitigationOrder below. And
    // IOutgoingDamageAbility, because a war proc cast from inside that dispatch feeds Spellweave's charge
    // loop and must resolve after Spellweave's weapon half - see OutgoingDamageDispatchOrder below.

    public interface IClassAbility
    {
        ClassAbilityDefinition Definition { get; }
    }

    /// <summary>
    /// Marker for a class ability whose effect is passive and always-on, read directly off the player at
    /// the relevant computation site rather than dispatched from a combat event. Examples: the
    /// "Enhanced X" family (read in CreatureSkill/CreatureAttribute/CreatureVital via
    /// Player.GetEnhanced*Bonus) and Battle Hardened (read in Creature.GetDamageResistRatingMod via
    /// Player.GetBattleHardenedDamageResistMod). Because such a skill legitimately needs no hook
    /// interface and no dispatch call, it is intentionally exempt from the "Implemented skills must hook
    /// something" rule that applies to reactive combat perks.
    /// </summary>
    public interface IPassiveStatAbility : IClassAbility
    {
    }

    /// <summary>
    /// A passive that grants the Enhanced-stat bonus (+10/25/50 by rank) to SEVERAL skills at once - the
    /// class "Training" bundles, Advanced Weaponry, and Questionable Tactics. Read alongside the single
    /// Enhanced-stat family in Player.GetEnhancedSkillBonus, additively (a skill covered by both a bundle
    /// and its own Enhanced skill gets both bonuses). Passive, so it also carries IPassiveStatAbility.
    /// </summary>
    public interface IStatBundleAbility : IClassAbility
    {
        IReadOnlyList<Skill> BundledSkills { get; }
    }

    /// <summary>
    /// Mutates a player's outgoing melee/missile hit (Player.DamageTarget) after damage
    /// calculation, before the hit is applied/reported - so the attacker notification shows
    /// the modified total. Monsters only; never called for PvP or on a miss. Spells don't
    /// route through this.
    ///
    /// THE SECOND HOOK WITH AN EXPLICIT ORDER. ClassAbilityRegistry sorts this bucket by
    /// <see cref="DispatchOrder"/> (a stable sort, so equal orders keep registration order). The reason is
    /// Spellweave (Spellsword T1): its weapon half consumes a charge that a spell CAST armed and then arms
    /// the player's next spell, and the three war procs (Spellblade, Runeblade, Spellstorm) are themselves
    /// spell casts made from inside this very dispatch. If a proc ran first, its cast would arm the weapon
    /// charge and Spellweave would then spend it on the hit that triggered the proc - a proc boosting its
    /// own trigger - and the proc would miss the spell charge that hit is supposed to arm. Registration
    /// order happened to put every war proc AHEAD of Spellweave, so that ordering is now stated here rather
    /// than left to where a line sits in the registry.
    /// </summary>
    public interface IOutgoingDamageAbility : IClassAbility
    {
        void ModifyOutgoingDamage(Player attacker, int rank, Creature target, DamageEvent damageEvent);

        /// <summary>
        /// Ascending run order within this hook. Defaults to <see cref="OutgoingDamageDispatchOrder.Modify"/>,
        /// which is right for every handler that modifies or reacts to the hit. A handler that CASTS A SPELL
        /// from this hook (through Player.CastClassAbilityProc) must declare
        /// <see cref="OutgoingDamageDispatchOrder.SpellCastingProc"/> - see that band for why.
        /// </summary>
        int DispatchOrder => OutgoingDamageDispatchOrder.Modify;
    }

    /// <summary>
    /// The ordering bands for <see cref="IOutgoingDamageAbility.DispatchOrder"/>, spaced so a new band can be
    /// slotted between two existing ones without renumbering anything.
    /// </summary>
    public static class OutgoingDamageDispatchOrder
    {
        /// <summary>
        /// A class ability's crit-damage bonus on the strike - today only Killer Instinct. FIRST, because it
        /// stashes the bonus on DamageEvent.ClassAbilityCritDamageBonus and the on-hit procs (Poison Weapon,
        /// Acid Proc) read that through DamageEvent.ProcDamageMultiplier later in the same dispatch (owner
        /// ruling 2026-09-22). Moving it ahead of the Modify band changes one other behaviour: Bloodlust (the
        /// ability) now heals from the Killer-Instinct-boosted Damage on a crit, the same number the Bloodlust
        /// gear mod already read, since that runs after the whole loop. Every other Modify handler either
        /// multiplies Damage (which commutes) or does not read it.
        /// </summary>
        public const int CritDamage = 50;

        /// <summary>
        /// Everything that modifies the landed hit's damage or reacts to it without casting a spell - which is
        /// every handler except the war procs, including Spellweave's weapon half.
        /// </summary>
        public const int Modify = 100;

        /// <summary>
        /// Handlers that CAST A SPELL off the landed hit (Spellblade, Runeblade, Spellstorm). LAST, because a
        /// proc cast runs Spellweave's cast side (Player.CastClassAbilityProc -> Player.OnSpellweaveCast),
        /// and that must happen strictly AFTER Spellweave's weapon half has resolved for the same hit: the hit
        /// may only consume a weapon charge an EARLIER cast armed, and the proc must consume the spell charge
        /// this hit armed. Running last also means a proc never sits between two damage modifiers. A proc
        /// does not touch DamageEvent.Damage, so moving it later changes no damage figure.
        /// </summary>
        public const int SpellCastingProc = 900;
    }

    /// <summary>
    /// Reacts to a landed hit on the player (Player.TakeDamage), before the death check - so it still
    /// fires on a killing blow. Fires on enemy CONTACT, including a hit fully mitigated to 0 damage
    /// (damageTaken may be 0); only misses/evades are excluded (they never reach TakeDamage). Monster
    /// attackers only (never PvP, self, or dead attackers). A handler that cares about damage magnitude
    /// can check damageTaken.
    /// </summary>
    public interface IIncomingDamageAbility : IClassAbility
    {
        void OnDamageTaken(Player defender, int rank, Creature attacker, DamageType damageType, uint damageTaken);
    }

    /// <summary>
    /// The mutable per-hit context handed to every <see cref="IPreWriteDamageAbility"/> handler. A context
    /// object rather than a long parameter list or a `ref uint`, on purpose: the mitigation abilities queued
    /// behind this hook each need to read or write something different, and adding a field here costs one
    /// line instead of re-editing all five core call sites again.
    ///
    /// <see cref="Damage"/> IS THE DAMAGE AS THROWN, never a figure already clamped to the victim's Health,
    /// and every call site is responsible for keeping it that way. That is the whole point of running before
    /// the write: a health-clamped input is exactly what made Mana Barrier unkillable before 2026-09-08 (see
    /// ManaBarrierAbility for the incident). A handler that wants a death save must therefore read the
    /// defender's Health itself and lower Damage deliberately - it must never assume Damage is bounded by it.
    ///
    /// EVERY OTHER FIELD IS INIT-ONLY. Only Damage is settable, so a handler cannot rewrite who attacked or
    /// what damage type landed out from under the handlers that run after it.
    /// </summary>
    public class PreWriteDamageContext
    {
        /// <summary>
        /// The raw damage source exactly as the call site knows it: may be a Player (PvP), may be the
        /// defender themselves (self-damage), may be null (a DoT tick carries no source at all). Use this
        /// only for things that are correct for ANY source, such as a squelch check.
        /// </summary>
        public WorldObject Source { get; init; }

        /// <summary>
        /// <see cref="Source"/> after the dispatch's shared attacker preconditions: non-null ONLY when the
        /// source is a live, non-player Creature other than the defender. Null means PvP, self-damage, a
        /// dead attacker, or no source at all. A handler that must only fire on monster damage reads this
        /// and does nothing when it is null; a handler that mitigates any damage whatever ignores it.
        /// </summary>
        public Creature Attacker { get; init; }

        public DamageType DamageType { get; init; }

        /// <summary>
        /// TRUE only for the accumulated damage-over-time tick (EnchantmentManager.ApplyDamageTick), the one
        /// site with no source to filter on. This is NOT the same as `Attacker == null` - an excluded PvP
        /// attacker also leaves Attacker null - and the two must not be conflated, because a handler that
        /// skips unattributed damage and a handler that skips PvP want different answers here.
        /// </summary>
        public bool IsDamageOverTime { get; init; }

        /// <summary>
        /// The damage still heading for Health. Each handler lowers it (never raises it); the dispatch
        /// returns the final value and the call site writes THAT to the vital. It may already be 0 by the
        /// time a later handler runs, so every handler must tolerate a zero and leave it alone.
        /// </summary>
        public uint Damage { get; set; }
    }

    /// <summary>
    /// Mitigates an incoming hit BEFORE the defender's health write, at every one of the five sites through
    /// which a player can lose health (Player.TakeDamage, SpellProjectile.DamageTarget,
    /// WorldObject_Magic.HandleCastSpell_Boost and HandleCastSpell_Transfer, and
    /// EnchantmentManager.ApplyDamageTick). Dispatched by Player.ApplyPreWriteDamageClassAbilities, which
    /// hands back the reduced figure for the call site to write.
    ///
    /// THIS IS THE HOOK <see cref="IIncomingDamageAbility"/> CANNOT BE. That one fires AFTER the write, so
    /// it can only react: it takes damage by value and returns void, and the write it follows has already
    /// clamped at zero. Anything that has to reduce the damage actually written, or has to keep a lethal
    /// blow from being lethal, belongs here instead.
    ///
    /// A DEATH SAVE FALLS OUT OF REDUCING THE DAMAGE and needs no flag of its own: every site writes
    /// <see cref="PreWriteDamageContext.Damage"/> to the vital and only then checks for death, so a handler
    /// that leaves less damage than the defender has Health has saved them. Deliberately NOT modelled as a
    /// "survive this" boolean, which would have to be honoured by five separate death checks in four
    /// different files, where the damage figure is already the one input all five share.
    ///
    /// ORDER IS EXPLICIT HERE AND NOWHERE ELSE IN THIS FILE. The other hooks ask handlers to stay
    /// order-independent; mitigation cannot be. Two reductions that both scale off what is left do not
    /// commute: a 50 point absorb pool ahead of a 25% divert leaves 38 of a 100 point hit, while the same
    /// two in the other order leave 25 and spend twice the Mana. Registration order would therefore decide
    /// live combat numbers as a side effect of where a line sits in ClassAbilityRegistry's list, which is
    /// precisely the accident the header comment above warns about. So this hook carries
    /// <see cref="MitigationOrder"/>, and ClassAbilityRegistry sorts the bucket by it (a stable sort, so
    /// equal orders keep registration order).
    /// </summary>
    public interface IPreWriteDamageAbility : IClassAbility
    {
        /// <summary>
        /// Ascending run order within this hook. Use a band from <see cref="DamageMitigationOrder"/> rather
        /// than a bare number, and say in the handler's own doc comment why that band is the right one.
        /// </summary>
        int MitigationOrder { get; }

        /// <summary>
        /// TRUE when this handler must stay in a player's learned-handler cache even at rank 0, because its
        /// effect can outlive the rank that created it.
        ///
        /// THE CACHE IS RANK-FILTERED, AND THAT IS A TRAP FOR ANYTHING HOLDING A POOL.
        /// Player.BuildClassAbilityHookCache keeps only handlers the player currently holds a rank in, which
        /// is correct for anything recomputed from rank on each hit and WRONG for anything that granted a
        /// persistent pool earlier: an unlearn, a facet swap or a wholesale rank sweep would strand that
        /// pool with nothing left to drain it, and it would silently stop absorbing mid-window. Sanguine
        /// Ward is exactly that case - its ward is granted at cast time and then absorbs for its own 15
        /// second window regardless of rank - so it declares TRUE and keeps draining, which is what it did
        /// before it was dispatched at all.
        ///
        /// A handler that declares TRUE MUST tolerate being called with rank 0, and must early-out on its
        /// own state: an always-present handler costs one array slot and one comparison per hit, which is
        /// only acceptable because the ward returns immediately when its pool is empty. Anything that
        /// derives its effect from rank declares FALSE and stays rank-gated.
        /// </summary>
        bool RunsWithoutLearnedRank { get; }

        /// <summary>
        /// Lowers <see cref="PreWriteDamageContext.Damage"/> by whatever this ability mitigates. State
        /// plainly in the implementation's own doc comment whether it is rank-gated or rank-independent -
        /// <paramref name="rank"/> is 0 for a handler that declared
        /// <see cref="RunsWithoutLearnedRank"/> and whose player no longer holds the ability.
        /// </summary>
        void ModifyIncomingDamage(Player defender, int rank, PreWriteDamageContext context);
    }

    /// <summary>
    /// The ordering bands for <see cref="IPreWriteDamageAbility.MitigationOrder"/>, spaced so a new
    /// mitigation can be slotted between two existing ones without renumbering anything.
    /// </summary>
    public static class DamageMitigationOrder
    {
        /// <summary>
        /// READ-ONLY REACTIONS THAT MITIGATE NOTHING (Adrenaline arming its window, Kinetic Charge counting
        /// a charge). They ride this hook rather than <see cref="IIncomingDamageAbility"/> because that one
        /// is PHYSICAL ONLY - it hangs off Player.TakeDamage, which magic damage and DoT ticks never reach -
        /// and both of these abilities promise the player they react to damage of ANY type, which only the
        /// five-site pre-write dispatch delivers.
        ///
        /// FIRST, AND DELIBERATELY OUTSIDE THE MITIGATION SANDWICH. An observer must never sit between two
        /// mitigations, because a reader glancing at the bucket order would then have to decide whether it
        /// changed the damage the next band sees (it does not, and must not). Putting them ahead of every
        /// mitigation keeps AbsorbPool -> Divert -> DeathSave literally adjacent, so the death save is still
        /// the LAST entry in the bucket rather than merely the last mitigation. It also means an observer
        /// sees the hit AS THROWN, which is the honest input for "an attack reached you" - a blow a ward
        /// swallows entirely still reached you.
        ///
        /// A handler in this band MUST NOT write <see cref="PreWriteDamageContext.Damage"/>. There is no
        /// compiler guard for that; the band is the contract.
        ///
        /// IT ALSO ALONE SEES A LANDED 0-DAMAGE HIT. <see cref="ShouldDispatchPreWriteHandler"/> is the
        /// gate: a hit that arrives at the dispatcher with 0 damage (armor soaked it entirely, or a
        /// sub-0.5 blow rounded down) still reached the defender, and Kinetic Charge's whole promise is
        /// "every attack that reaches you" - excluding a 0 hit would silently narrow that to "every attack
        /// that reaches you AND deals damage", which is a smaller ability than the one that was signed off.
        /// Every other band mitigates nothing when there is nothing left to mitigate, so a 0 hit is not
        /// dispatched to them at all.
        /// </summary>
        public const int Observe = 50;

        /// <summary>
        /// TRUE when a handler at <paramref name="mitigationOrder"/> should run against a hit of
        /// <paramref name="incomingDamage"/> at dispatch entry. Non-zero damage always dispatches, matching
        /// every band's existing behaviour; 0 damage dispatches ONLY the Observe band, because a mitigation
        /// has nothing left to mitigate while a read-only observer must still see that the hit landed. Pure,
        /// so it is unit-testable without a live Player - the dispatcher
        /// (<see cref="Player.ApplyPreWriteDamageClassAbilities"/>) is the only caller and cannot be tested
        /// directly (see PreWriteDamageHookTests's class summary for why).
        /// </summary>
        public static bool ShouldDispatchPreWriteHandler(int mitigationOrder, uint incomingDamage) =>
            incomingDamage != 0 || mitigationOrder == Observe;

        /// <summary>
        /// Finite absorb pools that eat damage outright (Sanguine Ward, Runic Ward). First of the
        /// mitigations, so a pool is spent against the full hit rather than against whatever a proportional
        /// mitigation left of it - which is the order these abilities have run in at every site since they
        /// were wired by hand. Two pools at this same band are ordered between themselves by registration
        /// order (the sort is stable), which is correct: two finite pools drain in sequence and commute.
        /// </summary>
        public const int AbsorbPool = 100;

        /// <summary>
        /// Proportional diversions that pay a SHARE of whatever is still coming (Mana Barrier). After the
        /// pools, so the share is taken of the reduced figure and the resource cost is proportional to the
        /// damage that was genuinely still going to land.
        /// </summary>
        public const int Divert = 200;

        /// <summary>
        /// Last-resort effects that need the final post-mitigation figure to decide anything (a death save).
        /// Last, because any mitigation running after one would invalidate the decision it just made.
        /// </summary>
        public const int DeathSave = 900;
    }

    /// <summary>
    /// Contributes extra shots to a missile volley (Player_Missile launch). Appends one damage
    /// multiplier per extra shot; the missile code owns targeting and cosmetic projectiles.
    /// Only consulted when the primary target is a monster.
    /// </summary>
    public interface IMissileVolleyAbility : IClassAbility
    {
        void AddExtraShots(Player attacker, int rank, List<float> shotDamageMultipliers);
    }

    /// <summary>
    /// Rides along with an equipped item's successful proc roll (WorldObject.TryProcItem),
    /// e.g. an aetheria surge - fires regardless of whether the proc spell itself lands.
    /// Handlers filter which item kinds they care about.
    /// </summary>
    public interface IItemProcAbility : IClassAbility
    {
        void OnItemProc(Player wielder, int rank, WorldObject procSource);
    }

    /// <summary>
    /// Reacts to one of the player's offensive damaging spell projectiles successfully damaging a monster
    /// (SpellProjectile.OnCollideObject, after the hit lands - never on a resist). EVERY SCHOOL as of
    /// 2026-09-13: the dispatch site tested War Magic until then and no longer does, so war, void and life
    /// projectiles all arrive here. Monsters only (PvP excluded). The handler receives the struck target
    /// and the projectile that hit it.
    ///
    /// A spell that produces no projectile at all (Harm, Drain Health) never reaches this hook, because it
    /// resolves on the boost/transfer path instead - so a handler here covers damaging spell PROJECTILES,
    /// not "all magic", and its player-facing text must say so.
    ///
    /// EXCLUDING class-ability-spawned children is NOT done for you. Several handlers self-exclude on
    /// SpellProjectile.IsClassAbilitySpawned so that effects spawning further projectiles cannot cascade;
    /// Elemental Rend deliberately does not, and Cascade uses a generation counter instead. An Echo Cast
    /// recast is NOT flagged IsClassAbilitySpawned (it is a full second cast, so Spell AOE radiates off it);
    /// it carries SpellProjectile.IsEchoCopy, which only Echo Cast reads.
    /// </summary>
    public interface ISpellHitAbility : IClassAbility
    {
        void OnSpellHit(Player caster, int rank, Creature primaryTarget, SpellProjectile projectile);
    }

    /// <summary>
    /// Reacts to a hostile creature dying with one of the player's blows as the last damage
    /// (Creature.OnDeath, resolved through DamageHistoryInfo.TryGetAttacker). Monsters only: the victim is
    /// never a player (PvP excluded) nor one of the killer's own pets, and the killer is never the victim.
    /// Fires exactly once per death - Creature.OnDeath re-entry is already guarded upstream.
    /// </summary>
    public interface ICreatureDeathAbility : IClassAbility
    {
        void OnCreatureKilled(Player killer, int rank, Creature victim);
    }

    /// <summary>
    /// Optional: lets the /testskill developer command trigger the skill's effect directly,
    /// bypassing its normal trigger and the learned requirement. Returns the result message.
    /// </summary>
    public interface ITestableClassAbility : IClassAbility
    {
        string Test(Player player);
    }
}
