using System;
using System.Linq;
using ACE.Server.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// Handles player->monster visibility checks
    /// </summary>
    partial class Player
    {
        /// <summary>
        /// Wakes up any monsters within the applicable range
        /// </summary>
        public void CheckMonsters()
        {
            if (!Attackable || Teleporting) return;

            // this used to fetch every visible Creature and skip the Players in the loop below.
            // Player derives from Creature, so in a crowd almost the whole list was players.
            // GetVisibleObjectsValuesOfTypeNonPlayerCreature applies the exact same "not a Player"
            // test inside the lock, so the set of monsters checked here is unchanged.
            var visibleObjs = PhysicsObj.ObjMaint.GetVisibleObjectsValuesOfTypeNonPlayerCreature();

            foreach (var monster in visibleObjs)
            {
                //if (Location.SquaredDistanceTo(monster.Location) <= monster.VisualAwarenessRangeSq)
                if (PhysicsObj.get_distance_sq_to_object(monster.PhysicsObj, true) <= monster.VisualAwarenessRangeSq)
                    AlertMonster(monster);
            }
        }

        /// <summary>
        /// Called when this player attacks a monster
        /// </summary>
        public void OnAttackMonster(Creature monster, bool primaryTarget = true)
        {
            if (monster == null) return;

            // summon assist: remember what this player just hit, for their combat pets to read on their own tick
            // (CombatPet.HandleFindTarget). Recorded ahead of the Attackable gate below, which is about waking the
            // monster, not about what the player meant to attack. Gated on primaryTarget because splash/cleave/
            // extra-arrow/AOE-child hits also route through here, and recording those would yank the pet off the
            // creature the player actually aimed at onto whatever secondary victim was hit last.
            if (primaryTarget)
                RecordAttackedMonster(monster);

            if (!Attackable) return;

            /*Console.WriteLine($"{Name}.OnAttackMonster({monster.Name})");
            Console.WriteLine($"Attackable: {monster.Attackable}");
            Console.WriteLine($"Tolerance: {monster.Tolerance}");*/

            // faction mobs will retaliate against players belonging to the same faction
            if (SameFaction(monster))
                monster.AddRetaliateTarget(this);

            if (monster.MonsterState != State.Awake && (monster.Tolerance & PlayerCombatPet_RetaliateExclude) == 0)
            {
                monster.AttackTarget = this;
                monster.WakeUp();
            }
        }
    }
}
