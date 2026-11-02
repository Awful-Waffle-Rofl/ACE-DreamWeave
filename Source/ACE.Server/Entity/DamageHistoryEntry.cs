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
