using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Shared harness for the Jewelry Aspect layer tests: a bare Player (never constructed, so no static database
    /// initializer runs) wearing aspected pieces, and the PropertyManager seed/restore for every key the layers read.
    /// </summary>
    internal static class JewelryAspectTestKit
    {
        private static uint nextGuid = 0x7ED00000;

        public static readonly string[] BoolKeys = { "jewelry_aspects_enabled" };
        public static readonly string[] DoubleKeys = { "jewelry_aspects_fortitude_per_slot", "jewelry_aspects_fortitude_cap", "jewelry_aspects_penetration_per_slot", "jewelry_aspects_penetration_cap", "jewelry_aspects_penetration_amplify", "jewelry_aspects_resolve_per_slot", "jewelry_aspects_resolve_cap" };

        public static void Seed()
        {
            PropertyManager.ModifyBool("jewelry_aspects_enabled", true);
            PropertyManager.ModifyDouble("jewelry_aspects_fortitude_per_slot", 0.01);
            PropertyManager.ModifyDouble("jewelry_aspects_fortitude_cap", 0.50);
            PropertyManager.ModifyDouble("jewelry_aspects_penetration_per_slot", 0.008);
            PropertyManager.ModifyDouble("jewelry_aspects_penetration_cap", 0.40);
            PropertyManager.ModifyDouble("jewelry_aspects_penetration_amplify", 1.0);
            PropertyManager.ModifyDouble("jewelry_aspects_resolve_per_slot", 0.02);
            PropertyManager.ModifyDouble("jewelry_aspects_resolve_cap", 1.0);
        }

        public static void Restore()
        {
            foreach (var key in BoolKeys)
                PropertyManager.ModifyBool(key, DefaultPropertyManager.DefaultBooleanProperties[key].Item);

            foreach (var key in DoubleKeys)
                PropertyManager.ModifyDouble(key, DefaultPropertyManager.DefaultDoubleProperties[key].Item);
        }

        public static Player BarePlayer(out Dictionary<ObjectGuid, WorldObject> equipped)
        {
            var player = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));

            var field = typeof(Creature).GetField("<EquippedObjects>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, "Creature.EquippedObjects backing field not found");

            equipped = new Dictionary<ObjectGuid, WorldObject>();
            field.SetValue(player, equipped);

            return player;
        }

        public static Creature BareCreature() => (Creature)RuntimeHelpers.GetUninitializedObject(typeof(Creature));

        public static void Wear(Dictionary<ObjectGuid, WorldObject> equipped, WorldObject item) => equipped[item.Guid] = item;

        public static WorldObject Piece(int attack, int defense, int utility, EquipMask worn)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 1008099,
                WeenieType = WeenieType.Generic,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.Jewelry },
                    { PropertyInt.ValidLocations, (int)EquipMask.NeckWear },
                    { PropertyInt.JewelryAspectAttack, attack },
                    { PropertyInt.JewelryAspectDefense, defense },
                    { PropertyInt.JewelryAspectUtility, utility },
                },
            };

            var wo = ACE.Server.Factories.WorldObjectFactory.CreateWorldObject(weenie, new ObjectGuid(nextGuid++));
            wo.CurrentWieldedLocation = worn;
            return wo;
        }

        public static string Flat(string body) => System.Text.RegularExpressions.Regex.Replace(body, @"\s+", " ");

        public static int Count(string text, string needle)
        {
            var n = 0;

            for (var i = text.IndexOf(needle, System.StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, System.StringComparison.Ordinal))
                n++;

            return n;
        }
    }
}
