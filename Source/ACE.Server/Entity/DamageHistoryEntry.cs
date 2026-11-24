using System;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity
{
    public class DamageHistoryEntry
    {
        public ObjectGuid Attacker;

        public DamageType DamageType;
        public int Amount;

        public uint CurrentHealth;
        public uint MaxHealth;

        /// <summary>
        /// The damage the hit carried into the health write, after every mitigation but BEFORE the write clamped it
        /// to the victim's remaining health. Amount is the clamped figure, so on an overkill hit the two differ and
        /// only this one says how hard the victim was hit. Null at sinks that do not record it; the death line
        /// (Player.OnDeath) shows nothing rather than fall back to the clamped Amount.
        /// </summary>
        public uint? Thrown;

        // ---- attack context (player_death_event). Recording only: nothing in the damage path reads these. ----

        /// <summary>What kind of attack this was. Set by the sinks that record <see cref="Thrown"/>; null everywhere else.</summary>
        public DamageAttackKind? AttackKind;

        /// <summary>Whether the hit was a critical, when the sink knows. Null at sinks that do not say.</summary>
        public bool? IsCritical;

        /// <summary>The spell that dealt the hit (spell projectile, Harm, Drain Health); null for a physical hit.</summary>
        public uint? SpellId;

        /// <summary>
        /// The attacker as it stood at the hit, captured only on a hit that left a PLAYER at 0 health
        /// (DeathTelemetry.CaptureKillingHit): the attacker can be destroyed before the death row is built, and
        /// <see cref="Attacker"/> is only a guid. Null on every other entry.
        /// </summary>
        public uint? AttackerWcid;
        public string AttackerName;
        public int? AttackerLevel;
        public bool? AttackerIsPlayer;

        /// <summary>
        /// The victim's protective/debuff state at the killing hit, captured with the attacker snapshot above and only
        /// while analytics is enabled. Taken here and not in OnDeath because the death handler runs after enchantments
        /// can expire or be stripped. Null on every other entry.
        /// </summary>
        public DeathDefenseSnapshot Defense;

        public DateTime Time;

        /// <summary>
        /// Constructs a new entry for the DamageHistory
        /// </summary>
        /// <param name="attacker">The creature source of the damage. In the case of a projectile, this would be the Creature that launched the projectile.</param>
        /// <param name="amount">A negative amount for damage taken, positive for healing</param>
        public DamageHistoryEntry(Creature creature, ObjectGuid attacker, DamageType damageType, int amount)
        {
            Attacker = attacker;

            DamageType = damageType;
            Amount = amount;

            CurrentHealth = creature.Health.Current;
            MaxHealth = creature.Health.MaxValue;

            Time = DateTime.UtcNow;
        }
    }
}
