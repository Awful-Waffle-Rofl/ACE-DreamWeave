using System;

using ACE.Entity;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.WorldObjects
{
    public class GenericObject : WorldObject
    {
        /// <summary>
        /// A new biota be created taking all of its values from weenie.
        /// </summary>
        public GenericObject(Weenie weenie, ObjectGuid guid) : base(weenie, guid)
        {
            SetEphemeralValues();
        }

        /// <summary>
        /// Restore a WorldObject from the database.
        /// </summary>
        public GenericObject(Biota biota) : base(biota)
        {
            SetEphemeralValues();
        }

        private void SetEphemeralValues()
        {
            //StackSize = null;
            //StackUnitEncumbrance = null;
            //StackUnitValue = null;
            //MaxStackSize = null;

            // Linkable Item Generator (linkitemgen2minutes) fix
            if (WeenieClassId == 4142)
            {
                MaxGeneratedObjects = 0;
                InitGeneratedObjects = 0;
            }
        }

        public override void ActOnUse(WorldObject activator)
        {
            if (!(activator is Player player))
                return;

            // Drift Network class-ability props (the Arcane Pedestal Luminance-exchange stone) opt in by data
            // and run their interaction here; any other Generic object falls through to normal use behavior.
            if (ClassAbilities.ClassAbilityTrainer.TryHandleUse(this, player))
                return;

            // World Events (WaffleACE): the Weave Cache reward giver opts in by PropertyBool.WorldEventCache and runs its claim here (TECH-DESIGN 2.8 option C).
            if (GetProperty(PropertyBool.WorldEventCache) == true && WorldEvents.WorldEventCacheHandler.TryHandleUse(this, player))
                return;

            // Marketplace Tinkerer's Inspiration pedestal: opts in by PropertyBool.TinkerersInspiration and applies its free tinkering buffs here.
            if (ACE.Server.Entity.TinkerersInspiration.TryHandleUse(this, player))
                return;

            // ML digsite (WaffleACE): the Boss Rush interrupt drum opts in by PropertyInt.MlDigsiteEncounterId, which only an object a
            // digsite encounter itself placed ever carries - so the cost for every other Generic object here is one dictionary miss.
            if (MlDigsite.MlDigsiteInterruptObject.TryHandleUse(this, player))
                return;

            if (UseSound > 0)
                player.Session.Network.EnqueueSend(new GameMessageSound(player.Guid, UseSound));
        }
    }
}
