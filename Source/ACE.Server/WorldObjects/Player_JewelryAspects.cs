using System;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.ClassAbilities;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Pvp.Rules;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        /// <summary>
        /// The aspect counts summed over every aspected piece currently worn in a Neck, Wrist or Finger slot (JewelryAspectMath.SumWorn). No cache:
        /// recomputed per event, like the equipment-mod sums, so equipping and unequipping need no bookkeeping. A piece
        /// with no aspects, or with invalid counts (negative, or more than 10 in total), contributes nothing.
        /// </summary>
        public AspectCounts GetJewelryAspectTotals()
        {
            // same null guard as Player.GetCloakRearGuardMod: EquippedObjects can be absent on a half-built creature
            return JewelryAspectMath.SumWorn(EquippedObjects?.Values);
        }

        /// <summary>
        /// Fortitude: the fraction of damage taken removed by worn Defense aspects. 0.0 when the feature is off, for a
        /// PvP source, and for self-inflicted damage (source == this, which keeps /rip lethal). A null source (an
        /// environmental or sourceless hit) is PvE and is eligible; hazards are excluded by the dispatcher's IsHazard check, not here.
        /// </summary>
        public double GetFortitude(WorldObject source)
        {
            // one eligibility rule, shared with the dispatcher: not a PvP pair and not self-inflicted (a null source is PvE)
            if (!ClassAbilityAttackerFilter.IsFortitudeEligibleSource(source, this))
                return 0.0;

            var enabled = PropertyManager.GetBool("jewelry_aspects_enabled").Item;

            if (!enabled)
                return 0.0;

            return JewelryAspectMath.Layer(
                GetJewelryAspectTotals().Defense,
                PropertyManager.GetDouble("jewelry_aspects_fortitude_per_slot").Item,
                PropertyManager.GetDouble("jewelry_aspects_fortitude_cap").Item,
                enabled,
                false);
        }

        /// <summary>Penetration p: the fraction of a target's elemental resistance ignored by worn Attack aspects (never armor), which also scales damage up against a target that does not resist (see GetPenetrationAmplify). 0.0 when off or against a player.</summary>
        public double GetPenetrationAgainst(WorldObject target)
        {
            var enabled = PropertyManager.GetBool("jewelry_aspects_enabled").Item;

            if (!enabled)
                return 0.0;

            return JewelryAspectMath.Layer(
                GetJewelryAspectTotals().Attack,
                PropertyManager.GetDouble("jewelry_aspects_penetration_per_slot").Item,
                PropertyManager.GetDouble("jewelry_aspects_penetration_cap").Item,
                enabled,
                PvpClassifier.IsPvp(this, target));
        }

        /// <summary>Penetration amplify k (jewelry_aspects_penetration_amplify): how strongly Penetration p also raises damage against targets that do not resist. Passed to <see cref="JewelryAspectMath.Penetrate"/> beside p; a NaN or negative value reads as 0 there.</summary>
        public double GetPenetrationAmplify()
        {
            return PropertyManager.GetDouble("jewelry_aspects_penetration_amplify").Item;
        }

        /// <summary>
        /// Resolve: the chance, rolled when this player (or this player's pet) gets a kill, to remove one monster-cast
        /// debuff, from worn Utility aspects. 0.0 when the feature is off. There is no caster and no PvP argument: the
        /// roll is PvE-only by where it is called from (ApplyJewelryResolveCreatureDeath).
        /// </summary>
        public double GetResolveChance()
        {
            var enabled = PropertyManager.GetBool("jewelry_aspects_enabled").Item;

            if (!enabled)
                return 0.0;

            return JewelryAspectMath.Layer(
                GetJewelryAspectTotals().Utility,
                PropertyManager.GetDouble("jewelry_aspects_resolve_per_slot").Item,
                PropertyManager.GetDouble("jewelry_aspects_resolve_cap").Item,
                enabled,
                false);
        }

        /// <summary>
        /// Resolve on a kill: rolls <see cref="GetResolveChance"/> and, on success, removes ONE random monster-cast debuff
        /// from this player (<see cref="JewelryAspectMath.SelectResolvable"/>). Called from Creature.OnDeath for the killer,
        /// or for the owner of a killing pet. Gated by jewelry_aspects_enabled only (class_abilities_enabled is not read).
        /// A victim that is a player, a pet or this player never rolls. Nothing eligible: no removal and no message.
        /// </summary>
        public void ApplyJewelryResolveCreatureDeath(Creature victim)
        {
            if (victim == null || victim == this || victim is Player || victim is Pet)
                return;

            var chance = GetResolveChance();

            if (chance <= 0.0)
                return;

            if (!JewelryAspectMath.ResolveRolls(chance, ThreadSafeRandom.Next(0f, 1f)))
                return;

            var removed = RemoveOneResolvableDebuff(ThreadSafeRandom.Next);

            if (removed == null)
                return;

            var spell = new Spell(removed.SpellId);

            Session?.Network.EnqueueSend(new GameMessageSystemChat($"Your Resolve shakes off {spell.Name}.", ChatMessageType.Magic));
        }

        /// <summary>
        /// Picks one resolvable debuff uniformly (<paramref name="next"/>(min, max) is inclusive) from the top layer of this
        /// player's own registry and removes it, with every other eligible layer in the same spell category (every monster's copy,
        /// JewelryAspectMath.SelectAllLayers), through the caching-aware EnchantmentManager.Dispel(list), which also tells the
        /// client. Returns the chosen entry, or null when nothing was eligible.
        /// </summary>
        private PropertiesEnchantmentRegistry RemoveOneResolvableDebuff(Func<int, int, int> next)
        {
            var eligible = JewelryAspectMath.SelectResolvable(
                EnchantmentManager.GetEnchantments_TopLayer(EnchantmentTypeFlags.Undef), Guid.Full, IsHarmfulSpell);

            var entry = JewelryAspectMath.PickOne(eligible, next);

            if (entry == null)
                return null;

            var layers = JewelryAspectMath.SelectAllLayers(entry, EnchantmentManager.GetEnchantments(entry.SpellCategory), Guid.Full, IsHarmfulSpell);

            EnchantmentManager.Dispel(layers);

            return entry;
        }
    }
}