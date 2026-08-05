using System;
using System.Collections.Generic;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Entity;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// Summonable monsters combat AI
    /// </summary>
    public partial class CombatPet : Pet
    {
        /// <summary>
        /// A new biota be created taking all of its values from weenie.
        /// </summary>
        public CombatPet(Weenie weenie, ObjectGuid guid) : base(weenie, guid)
        {
            SetEphemeralValues();
        }

        /// <summary>
        /// Restore a WorldObject from the database.
        /// </summary>
        public CombatPet(Biota biota) : base(biota)
        {
            SetEphemeralValues();
        }

        private void SetEphemeralValues()
        {
        }

        public override bool? Init(Player player, PetDevice petDevice, bool spawnStagger = false)
        {
            var success = base.Init(player, petDevice, spawnStagger);

            if (success == null || !success.Value)
                return success;

            SetCombatMode(CombatMode.Melee);
            MonsterState = State.Awake;
            IsAwake = true;

            // copy ratings from pet device
            DamageRating = petDevice.GearDamage;
            DamageResistRating = petDevice.GearDamageResist;
            CritDamageRating = petDevice.GearCritDamage;
            CritDamageResistRating = petDevice.GearCritDamageResist;
            CritRating = petDevice.GearCrit;
            CritResistRating = petDevice.GearCritResist;

            // class ability: Empowered Summons scales the pet's stats (health, damage, defenses). A rating
            // point is ~1% here, so the same fraction feeds health (as a %) and the rating pools (as points).
            var statMod = player.GetEmpoweredSummonsStatMod();
            if (statMod > 1.0f)
            {
                var bonusFraction = statMod - 1.0f;

                Health.StartingValue += (uint)Math.Round(Health.MaxValue * bonusFraction);
                Health.Current = Health.MaxValue;

                var ratingBonus = (int)Math.Round(bonusFraction * 100.0f);
                DamageRating = (DamageRating ?? 0) + ratingBonus;
                DamageResistRating = (DamageResistRating ?? 0) + ratingBonus;
                CritDamageResistRating = (CritDamageResistRating ?? 0) + ratingBonus;
            }

            // are CombatPets supposed to attack monsters that are in the same faction as the pet owner?
            // if not, there are a couple of different approaches to this
            // the easiest way for the code would be to simply set Faction1Bits for the CombatPet to match the pet owner's
            // however, retail pcaps did not contain Faction1Bits for CombatPets

            // doing this the easiest way for the code here, and just removing during appraisal
            Faction1Bits = player.Faction1Bits;

            return true;
        }

        /// <summary>
        /// Soul Tether reduces the damage this pet takes. Read here - at the pet's incoming-damage site, on
        /// every hit - rather than at summon time, so a rank learned or unlearned while the pet is already
        /// in the world takes effect immediately. Returns the multiplier to 1.0 (no change) when the owner
        /// is gone or has not learned the ability.
        /// </summary>
        public override uint TakeDamage(WorldObject source, DamageType damageType, float amount, bool crit = false)
        {
            var mod = P_PetOwner?.GetSoulTetherDamageReductionMod() ?? 1.0f;

            if (mod < 1.0f && amount > 0.0f)
                amount *= mod;

            return base.TakeDamage(source, damageType, amount, crit);
        }

        /// <summary>
        /// Notifies the owner that its combat pet has fallen, which arms Soul Tether's resummon-cooldown
        /// skip. Recorded for every owner (the ability is checked when the skip is read), so the flag stays
        /// accurate if the ability is learned between the pet's death and the resummon.
        /// </summary>
        public override DeathMessage OnDeath(DamageHistoryInfo lastDamager, DamageType damageType, bool criticalHit = false)
        {
            P_PetOwner?.OnCombatPetDied();

            return base.OnDeath(lastDamager, damageType, criticalHit);
        }

        public override void HandleFindTarget()
        {
            var creature = AttackTarget as Creature;

            if (creature == null || creature.IsDead || !IsVisibleTarget(creature))
                FindNextTarget();
        }

        public override bool FindNextTarget()
        {
            var nearbyMonsters = GetNearbyMonsters();
            if (nearbyMonsters.Count == 0)
            {
                //Console.WriteLine($"{Name}.FindNextTarget(): empty");
                return false;
            }

            // get nearest monster
            var nearest = BuildTargetDistance(nearbyMonsters, true);

            if (nearest[0].Distance > VisualAwarenessRangeSq)
            {
                //Console.WriteLine($"{Name}.FindNextTarget(): next object out-of-range (dist: {Math.Round(Math.Sqrt(nearest[0].Distance))})");
                return false;
            }

            AttackTarget = nearest[0].Target;

            //Console.WriteLine($"{Name}.FindNextTarget(): {AttackTarget.Name}");

            return true;
        }

        /// <summary>
        /// Returns a list of attackable monsters in this pet's visible targets
        /// </summary>
        public List<Creature> GetNearbyMonsters()
        {
            var monsters = new List<Creature>();

            foreach (var creature in PhysicsObj.ObjMaint.GetVisibleTargetsValuesOfTypeCreature())
            {
                // why does this need to be in here?
                if (creature.IsDead || !creature.Attackable || creature.Visibility)
                {
                    //Console.WriteLine($"{Name}.GetNearbyMonsters(): refusing to add dead creature {creature.Name} ({creature.Guid})");
                    continue;
                }

                // combat pets do not aggro monsters belonging to the same faction as the pet owner?
                if (SameFaction(creature))
                {
                    // unless the pet owner or the pet is being retaliated against?
                    if (!creature.HasRetaliateTarget(P_PetOwner) && !creature.HasRetaliateTarget(this))
                        continue;
                }

                monsters.Add(creature);
            }

            return monsters;
        }

        public override void Sleep()
        {
            // pets dont really go to sleep, per say
            // they keep scanning for new targets,
            // which is the reverse of the current ACE jurassic park model

            return;  // empty by default
        }
    }
}
