using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Taunt's proc-source filter, widened on 2026-09-29 so a cloak spell proc taunts as well as an Aetheria
    /// surge.
    ///
    /// The filter is the whole safety story for routing cloak procs through the existing IItemProcAbility hook:
    /// Cloak.TryProcSpell now calls Player.OnClassAbilityItemProc(cloak), which fans out to every learned
    /// IItemProcAbility handler. Taunt and Hunter's Echo are the only implementors - asserted below rather than assumed - and
    /// TriggersOn is a positive ALLOWLIST so that a weapon's cast-on-strike, or any third proc source wired
    /// into the hook later, is excluded by default rather than included by accident.
    /// </summary>
    [TestClass]
    public class TauntProcSourceTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            // seeds the server-tunable property cache from hardcoded defaults, no database involved
            DefaultPropertyManager.LoadDefaultProperties();
        }

        private static uint nextGuid = 0x7F400000;

        private static WorldObject MakeItem(uint wcid, EquipMask validLocations)
        {
            var weenie = new Weenie
            {
                WeenieClassId = wcid,
                WeenieType = WeenieType.Clothing,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ValidLocations, (int)validLocations },
                    { PropertyInt.ItemType, (int)ItemType.Clothing },
                },
            };

            return new Clothing(weenie, new ObjectGuid(nextGuid++));
        }

        /// <summary>
        /// A real aetheria wcid, read off Aetheria's own constant rather than typed as a literal, so the test
        /// cannot drift from what Aetheria.IsAetheria actually accepts.
        /// </summary>
        private static uint AnAetheriaWcid()
        {
            var wcid = Aetheria.AetheriaBlue;

            Assert.IsTrue(Aetheria.IsAetheria(wcid), "the wcid this test uses must really be an aetheria");

            return wcid;
        }

        [TestMethod]
        public void TriggersOn_AcceptsAnAetheria()
        {
            // ValidLocations on the real weenie is an aetheria slot, not Cloak - so this case is carried
            // entirely by the wcid test, which is the point of asserting it separately from the cloak case
            var aetheria = MakeItem(AnAetheriaWcid(), EquipMask.TrinketOne);

            Assert.IsTrue(TauntAbility.TriggersOn(aetheria));
        }

        [TestMethod]
        public void TriggersOn_AcceptsACloak()
        {
            var cloak = MakeItem(990200, EquipMask.Cloak);

            Assert.IsTrue(Cloak.IsCloak(cloak), "control: the fixture must really be a cloak");
            Assert.IsTrue(TauntAbility.TriggersOn(cloak));
        }

        [TestMethod]
        public void TriggersOn_RefusesEverythingElse()
        {
            // an ordinary proccing weapon, the other thing WorldObject.TryProcItem dispatches for
            var weapon = MakeItem(990201, EquipMask.MeleeWeapon);

            Assert.IsFalse(Aetheria.IsAetheria(weapon.WeenieClassId), "control: not an aetheria");
            Assert.IsFalse(Cloak.IsCloak(weapon), "control: not a cloak");
            Assert.IsFalse(TauntAbility.TriggersOn(weapon),
                "a weapon cast-on-strike proc must not taunt - the filter is an allowlist, not a denylist");

            // a shield, and a piece of chest armor - neither is a proc source Taunt rides
            Assert.IsFalse(TauntAbility.TriggersOn(MakeItem(990202, EquipMask.Shield)));
            Assert.IsFalse(TauntAbility.TriggersOn(MakeItem(990203, EquipMask.ChestArmor)));

            // and a null source is inert rather than an exception on the hit path
            Assert.IsFalse(TauntAbility.TriggersOn(null));
        }

        /// <summary>
        /// The chat line names the right source, so a cloak proc does not claim to be a "surging aetheria".
        /// </summary>
        [TestMethod]
        public void SourceClause_DistinguishesTheTwoSources()
        {
            Assert.AreEqual("Your cloak's woven magic", TauntAbility.SourceClause(MakeItem(990204, EquipMask.Cloak)));
            Assert.AreEqual("Your surging aetheria", TauntAbility.SourceClause(MakeItem(AnAetheriaWcid(), EquipMask.TrinketOne)));
        }

        /// <summary>
        /// THE PRECONDITION THE WHOLE APPROACH RESTS ON. Cloak.TryProcSpell dispatches the shared
        /// IItemProcAbility hook, so anything implementing that interface acquires a cloak trigger. Since
        /// 2026-11-10 there are exactly two implementors, Taunt and Hunter's Echo, in that order (the Echo takes
        /// Taunt's fresh holds over), and Hunter's Echo asked for the cloak trigger: it filters through
        /// TauntAbility.TriggersOn itself. Asserted against the live registry, and separately as a source scan over
        /// the handler directory so an unregistered-but-present handler is caught too.
        /// </summary>
        [TestMethod]
        public void ItemProcAbilities_AreExactlyTauntThenHuntersEcho()
        {
            CollectionAssert.AreEqual(
                new[] { typeof(TauntAbility), typeof(HuntersEchoAbility) },
                ClassAbilityRegistry.ItemProcAbilities.Select(h => h.GetType()).ToArray(),
                "the IItemProcAbility handlers changed, so a new one now also fires on a cloak spell proc, or Taunt " +
                "no longer runs ahead of Hunter's Echo. Either give a new one its own filter or give cloak procs a hook of their own.");

            var abilities = Path.Combine(
                ClassAbilityAffinityDeclarationTests.RepoRoot(),
                "Source", "ACE.Server", "ClassAbilities", "Abilities");

            Assert.IsTrue(Directory.Exists(abilities), $"handler directory not found: {abilities}");

            var declaring = Directory
                .GetFiles(abilities, "*.cs")
                .Where(f => File.ReadAllText(f).Contains("IItemProcAbility"))
                .Select(Path.GetFileName)
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToArray();

            CollectionAssert.AreEqual(new[] { "HuntersEchoAbility.cs", "TauntAbility.cs" }, declaring,
                "a handler file under ClassAbilities/Abilities mentions IItemProcAbility other than TauntAbility and " +
                "HuntersEchoAbility. See the assertion above for why that matters.");

            // Hunter's Echo rides EXACTLY Taunt's two sources: its filter is a call to Taunt's, never a copy
            var echo = File.ReadAllText(Path.Combine(abilities, "HuntersEchoAbility.cs"));

            StringAssert.Contains(echo, "public static bool TriggersOn(WorldObject procSource) => TauntAbility.TriggersOn(procSource);",
                "HuntersEchoAbility.TriggersOn must delegate to TauntAbility.TriggersOn.");
            Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(echo,
                    @"public void OnItemProc\(Player wielder, int rank, WorldObject procSource\)\s*\{\s*(//[^\n]*\s*)*if \(!TriggersOn\(procSource\)\)\s*return;"),
                "HuntersEchoAbility.OnItemProc must open with the TriggersOn filter.");
        }

        /// <summary>The Echo's allowlist is Taunt's: same answers for an aetheria, a cloak, a weapon and null.</summary>
        [TestMethod]
        public void HuntersEcho_TriggersOnMatchesTaunt()
        {
            Assert.IsTrue(HuntersEchoAbility.TriggersOn(MakeItem(990204, EquipMask.Cloak)));
            Assert.IsTrue(HuntersEchoAbility.TriggersOn(MakeItem(AnAetheriaWcid(), EquipMask.TrinketOne)));
            Assert.IsFalse(HuntersEchoAbility.TriggersOn(MakeItem(990205, EquipMask.MissileWeapon)));
            Assert.IsFalse(HuntersEchoAbility.TriggersOn(null));
        }
    }
}
