using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Avatars;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Avatars
{
    /// <summary>
    /// The WorldObjectFactory opt-in for the Allegiance Avatar NPC (PropertyBool.AllegianceAvatarVendor, 9098): a type-10
    /// (Creature) weenie WITH the flag builds an AvatarVendor, WITHOUT it builds what it built before, keyed on the bool and
    /// not on WeenieType, in both factory arms. Modelled on ThreadPeddlerVendorFactoryTests. The one PropertyManager key that
    /// seeds (vendor_shop_uses_generator) is restored to its shipped default in TestCleanup, so the class passes alone.
    /// </summary>
    [TestClass]
    public class AvatarFactoryTests
    {
        private const string ShopGeneratorKey = "vendor_shop_uses_generator";

        private static uint nextGuid = 0x7EC80000;

        private static uint NextGuid() => ++nextGuid;

        [TestInitialize]
        public void Setup()
        {
            SalvageRollerVendorTests.SeedVendorConstructible();
        }

        [TestCleanup]
        public void Cleanup()
        {
            PropertyManager.ModifyBool(ShopGeneratorKey, DefaultPropertyManager.DefaultBooleanProperties[ShopGeneratorKey].Item);
        }

        private static Weenie MakeAvatarWeenie(bool optIn, WeenieType type = WeenieType.Creature)
        {
            var properties = new Dictionary<PropertyBool, bool>();

            if (optIn)
                properties[PropertyBool.AllegianceAvatarVendor] = true;

            return new Weenie
            {
                WeenieClassId = AvatarVendor.AvatarWcid,
                WeenieType = type,
                PropertiesBool = properties,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.MerchandiseItemTypes, 0 },
                    { PropertyInt.MerchandiseMinValue, 0 },
                    { PropertyInt.MerchandiseMaxValue, 0 },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double>
                {
                    { PropertyFloat.BuyPrice, 0 },
                    { PropertyFloat.SellPrice, 1 },
                },
                PropertiesString = new Dictionary<PropertyString, string>
                {
                    { PropertyString.Name, "Allegiance Avatar" },
                },
            };
        }

        [TestMethod]
        public void Wcids_AreTheReservedBlock()
        {
            Assert.AreEqual(1008500u, AvatarVendor.AvatarWcid);
            Assert.AreEqual(1008501u, AvatarVendor.FavorWcid);
            Assert.AreEqual(1008502u, AvatarVendor.EventListingWcid);
        }

        [TestMethod]
        public void PropertyBool9098_NameMatchesRegistry()
        {
            Assert.AreEqual(9098, (int)PropertyBool.AllegianceAvatarVendor);
            Assert.AreEqual("AllegianceAvatarVendor", Enum.GetName(typeof(PropertyBool), 9098));

            var registry = SalvageRollerVendorTests.FindInSourceTree("Source/property-registry.tsv");
            var row = File.ReadAllLines(registry)
                .Select(l => l.Split('\t'))
                .Where(c => c.Length > 3 && c[0] == "PropertyBool" && c[1] == "9098")
                .ToList();

            Assert.AreEqual(1, row.Count, "exactly one registry row reserves PropertyBool 9098");
            Assert.AreEqual("AllegianceAvatarVendor", row[0][2], "the registry name must be the enum member name");
        }

        [TestMethod]
        public void Weenie_WithoutBool9098_BuildsWhatItBuiltBefore()
        {
            var creature = WorldObjectFactory.CreateWorldObject(MakeAvatarWeenie(optIn: false), new ObjectGuid(NextGuid()));
            Assert.AreEqual(typeof(Creature), creature.GetType(), "without bool 9098 a type-10 weenie builds a plain Creature");

            var vendor = WorldObjectFactory.CreateWorldObject(MakeAvatarWeenie(optIn: false, type: WeenieType.Vendor), new ObjectGuid(NextGuid()));
            Assert.AreEqual(typeof(Vendor), vendor.GetType(), "without bool 9098 a type-12 weenie builds a retail Vendor");
        }

        [TestMethod]
        public void Weenie_WithFalseBool9098_IsNotAnOptIn()
        {
            var weenie = MakeAvatarWeenie(optIn: false);
            weenie.PropertiesBool[PropertyBool.AllegianceAvatarVendor] = false;

            var wo = WorldObjectFactory.CreateWorldObject(weenie, new ObjectGuid(NextGuid()));

            Assert.AreEqual(typeof(Creature), wo.GetType());
        }

        [TestMethod]
        public void Weenie_WithBool9098_BuildsAvatarVendor()
        {
            var weenie = MakeAvatarWeenie(optIn: true);
            Assert.AreEqual(WeenieType.Creature, weenie.WeenieType, "sanity: the Avatar ships as type 10");

            var wo = WorldObjectFactory.CreateWorldObject(weenie, new ObjectGuid(NextGuid()));

            Assert.AreEqual(typeof(AvatarVendor), wo.GetType(), "bool 9098 decides the class, not WeenieType");
        }

        [TestMethod]
        public void VendorWeenie_WithBool9098_BuildsAvatarVendor()
        {
            var wo = WorldObjectFactory.CreateWorldObject(MakeAvatarWeenie(optIn: true, type: WeenieType.Vendor), new ObjectGuid(NextGuid()));

            Assert.AreEqual(typeof(AvatarVendor), wo.GetType(), "a type-12 weenie carrying 9098 is the Avatar too, never a retail Vendor");
        }

        [TestMethod]
        public void ShippedSql_CarriesTheOptInAndTheFavorCurrency()
        {
            // The shipped weenie is what production builds: without the 9098 row it is a plain Creature, and without
            // DID 57 its vendor panel has no Favor currency.
            var sql = File.ReadAllText(SalvageRollerVendorTests.FindInSourceTree("Content/sql/weenies/1008500 Allegiance Avatar.sql"));

            Assert.IsTrue(Regex.IsMatch(Insert(sql, "weenie_properties_bool"), @"\(\s*1008500\s*,\s*9098\s*,\s*True\s*\)"), "weenie_properties_bool row (1008500, 9098, True)");
            Assert.IsTrue(Regex.IsMatch(Insert(sql, "weenie_properties_d_i_d"), @"\(\s*1008500\s*,\s*57\s*,\s*1008501\s*\)"), "weenie_properties_d_i_d row (1008500, 57, 1008501)");
        }

        /// <summary>The one INSERT statement into <paramref name="table"/>, up to its terminating semicolon.</summary>
        private static string Insert(string sql, string table)
        {
            var start = sql.IndexOf("INSERT INTO `" + table + "`", StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"no INSERT INTO `{table}`");
            var end = sql.IndexOf(";", start, StringComparison.Ordinal);
            Assert.IsTrue(end > start, $"the INSERT INTO `{table}` is not terminated");
            return sql.Substring(start, end - start);
        }

        [TestMethod]
        public void Biota_WithBool9098_BuildsAvatarVendor()
        {
            var weenie = MakeAvatarWeenie(optIn: true);
            var biota = new Biota
            {
                Id = NextGuid(),
                WeenieClassId = weenie.WeenieClassId,
                WeenieType = WeenieType.Creature,
                PropertiesBool = new Dictionary<PropertyBool, bool>(weenie.PropertiesBool),
                PropertiesInt = new Dictionary<PropertyInt, int>(weenie.PropertiesInt),
                PropertiesFloat = new Dictionary<PropertyFloat, double>(weenie.PropertiesFloat),
                PropertiesString = new Dictionary<PropertyString, string>(weenie.PropertiesString),
            };

            // As in ThreadPeddlerVendorFactoryTests: the biota constructor chain loads inventory through DatabaseManager.Shard,
            // which this host does not have, so construction cannot finish. The constructor the factory entered is on the
            // stack of the exception the missing shard raises.
            StringAssert.Contains(ConstructorEntered(biota), "AvatarVendor..ctor", "a type-10 biota carrying 9098 must come back as the Avatar");

            biota.PropertiesBool.Remove(PropertyBool.AllegianceAvatarVendor);
            biota.Id = NextGuid();

            var control = ConstructorEntered(biota);
            Assert.IsFalse(control.Contains("AvatarVendor..ctor"), "control: without 9098 a type-10 biota is not the Avatar");
            StringAssert.Contains(control, "Creature..ctor", "control: it takes the Creature arm");
        }

        private static string ConstructorEntered(Biota biota)
        {
            try
            {
                var wo = WorldObjectFactory.CreateWorldObject(biota);
                return wo == null ? "" : $"{wo.GetType().Name}..ctor (completed)";
            }
            catch (Exception ex)
            {
                return ex.StackTrace ?? "";
            }
        }

        [TestMethod]
        public void Factory_BothArmsKeyOnTheFlag_AheadOfTheTypeSwitch()
        {
            var code = AccountVaultPurgeTests.StripComments(File.ReadAllText(SalvageRollerVendorTests.FindInSourceTree("Source/ACE.Server/Factories/WorldObjectFactory.cs")));

            var weenieCheck = code.IndexOf("if (weenie.GetProperty(PropertyBool.AllegianceAvatarVendor) == true)", StringComparison.Ordinal);
            var weenieSwitch = code.IndexOf("switch (objWeenieType)", StringComparison.Ordinal);
            var biotaCheck = code.IndexOf("biota.PropertiesBool.TryGetValue(PropertyBool.AllegianceAvatarVendor, out var isAllegianceAvatar) && isAllegianceAvatar)", StringComparison.Ordinal);
            var biotaSwitch = code.IndexOf("switch (biota.WeenieType)", StringComparison.Ordinal);

            Assert.IsTrue(weenieCheck > 0 && weenieCheck < weenieSwitch, $"the weenie arm must test 9098 before its type switch (check={weenieCheck}, switch={weenieSwitch})");
            Assert.IsTrue(biotaCheck > 0 && biotaCheck < biotaSwitch, $"the biota arm must test 9098 before its type switch (check={biotaCheck}, switch={biotaSwitch})");
            Assert.AreEqual(1, Regex.Matches(code, Regex.Escape("return new ACE.Server.Avatars.AvatarVendor(weenie, guid);")).Count);
            Assert.AreEqual(1, Regex.Matches(code, Regex.Escape("return new ACE.Server.Avatars.AvatarVendor(biota);")).Count);
        }
    }
}
