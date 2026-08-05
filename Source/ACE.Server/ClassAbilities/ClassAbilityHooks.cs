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
    /// </summary>
    public interface IOutgoingDamageAbility : IClassAbility
    {
        void ModifyOutgoingDamage(Player attacker, int rank, Creature target, DamageEvent damageEvent);
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
    /// Reacts to one of the player's offensive war-magic spell projectiles successfully damaging a
    /// monster (SpellProjectile.OnCollideObject, after the hit lands - never on a resist). Monsters
    /// only (PvP excluded), and never for a projectile that is itself a class-ability-spawned child, so
    /// effects that spawn further projectiles can't cascade. The handler receives the struck target
    /// and the projectile that hit it.
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
