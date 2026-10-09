using System.Threading;

using ACE.Entity.Enum.Properties;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        /// <summary>
        /// Summon assist: whether this player's combat pets switch to the monster the player most recently
        /// attacked (read by CombatPet.HandleFindTarget). A null underlying property means the player has
        /// never set a preference, which is ON. Set by the player via /summonattackontarget on|off.
        /// </summary>
        public bool SummonAttackOnTarget
        {
            get => GetProperty(PropertyBool.SummonAttackOnTarget) ?? true;
            set => SetProperty(PropertyBool.SummonAttackOnTarget, value);
        }

        /// <summary>
        /// The player's own SummonAttackOnTarget choice, or null if they have never set one and are on the
        /// default (on).
        /// </summary>
        public bool? SummonAttackOnTargetOverride => GetProperty(PropertyBool.SummonAttackOnTarget);

        /// <summary>
        /// The monster this player most recently landed an attack on. Runtime-only, never persisted.
        ///
        /// Written by OnAttackMonster, which can run on a projectile-collision path, and read by this player's
        /// combat pets on their own monster ticks - potentially different threads. It is a single reference,
        /// written and read with Volatile so a reader always sees a whole, current reference; no other state
        /// is shared, and the pet re-validates the monster on every read.
        /// </summary>
        private Creature lastAttackedMonster;

        public Creature LastAttackedMonster => Volatile.Read(ref lastAttackedMonster);

        private void RecordAttackedMonster(Creature monster)
        {
            Volatile.Write(ref lastAttackedMonster, monster);
        }
    }
}
