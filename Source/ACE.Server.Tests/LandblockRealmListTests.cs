using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Server.Realms;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Pins <see cref="LandblockRealmList"/>, the one realm-aware parser behind mule_landblocks,
    /// pvp_safe_landblocks, account_vault_allowlist / account_vault_denylist and facet_allowlist. Pure - no
    /// PropertyManager reads - so no key needs seeding.
    ///
    /// The realm-scoped cases are the ones that discriminate against the pre-realm parsers: those matched on
    /// the landblock id alone (a HashSet of ushort), so every "realm 0 does not match 01F5@1" assertion below
    /// would fail against them, and "01F5@1" itself would have been skipped as malformed.
    /// </summary>
    [TestClass]
    public class LandblockRealmListTests
    {
        private static uint Instance(ushort realm, bool ephemeral = false, ushort shortId = 0)
            => Position.InstanceIDFromVars(realm, ephemeral ? (ushort)1 : shortId, ephemeral);

        // ================= parsing =================

        [TestMethod]
        public void Parse_BareEntries_AcceptEveryLegacySpelling()
        {
            var rejected = new List<string>();
            var list = LandblockRealmList.Parse("0x016C, 017d ,0X01FF,16C", "test", rejected: rejected);

            Assert.AreEqual(0, rejected.Count, string.Join("|", rejected));
            Assert.AreEqual(3, list.Count, "16C and 0x016C are the same entry");
            Assert.AreEqual("016C,017D,01FF", list.ToString());
        }

        [TestMethod]
        public void Parse_RealmScopedEntries()
        {
            var rejected = new List<string>();
            var list = LandblockRealmList.Parse("01F5@1, 0x01f5 @ 2 ,016C", "test", rejected: rejected);

            Assert.AreEqual(0, rejected.Count, string.Join("|", rejected));
            Assert.AreEqual(3, list.Count);
            Assert.AreEqual("016C,01F5@1,01F5@2", list.ToString());
        }

        [TestMethod]
        public void Parse_SkipsMalformedEntries_WithoutThrowing()
        {
            var rejected = new List<string>();
            var list = LandblockRealmList.Parse("016C, , not-hex ,01F5@,01F5@x,01F5@32768,@1,1FFFF,017D,", "test", rejected: rejected);

            Assert.AreEqual("016C,017D", list.ToString());
            CollectionAssert.AreEquivalent(new[] { " not-hex ", "01F5@", "01F5@x", "01F5@32768", "@1", "1FFFF" }, rejected);
        }

        [TestMethod]
        public void Parse_NullOrBlank_IsEmpty()
        {
            Assert.AreEqual(0, LandblockRealmList.Parse(null, "test").Count);
            Assert.AreEqual(0, LandblockRealmList.Parse("   ", "test").Count);
            Assert.AreEqual(0, LandblockRealmList.Parse(",,", "test").Count);
        }

        [TestMethod]
        public void ToString_RoundTrips()
        {
            var list = LandblockRealmList.Parse("01F5@1,016C,0090@32767", "test");

            Assert.AreEqual(list.ToString(), LandblockRealmList.Parse(list.ToString(), "test").ToString());
        }

        [TestMethod]
        public void NormalizeEntry_CanonicalSpellings()
        {
            Assert.AreEqual("016C", LandblockRealmList.NormalizeEntry("0x016c"));
            Assert.AreEqual("01F5@1", LandblockRealmList.NormalizeEntry(" 0x01f5 @ 1 "));
            Assert.IsNull(LandblockRealmList.NormalizeEntry("01F5@"));
            Assert.IsNull(LandblockRealmList.NormalizeEntry("zz"));
        }

        // ================= membership: bare entries keep the old semantics =================

        [TestMethod]
        public void BareEntry_MatchesEveryPersistentRealm()
        {
            var list = LandblockRealmList.Parse("016C", "test");

            Assert.IsTrue(list.Contains(0x016C, 0, false));
            Assert.IsTrue(list.Contains(0x016C, 1, false));
            Assert.IsTrue(list.Contains(0x016C, 7, false));
            Assert.IsFalse(list.Contains(0x016D, 0, false), "control: another landblock");
        }

        [TestMethod]
        public void BareEntry_Ephemeral_FollowsTheListsFlag()
        {
            var excludes = LandblockRealmList.Parse("016C", "test", bareMatchesEphemeral: false);
            var includes = LandblockRealmList.Parse("016C", "test", bareMatchesEphemeral: true);

            Assert.IsFalse(excludes.Contains(0x016C, 0, true));
            Assert.IsTrue(includes.Contains(0x016C, 0, true));
        }

        // ================= membership: realm-scoped entries =================

        [TestMethod]
        public void RealmScopedEntry_MatchesOnlyItsRealm()
        {
            var list = LandblockRealmList.Parse("01F5@1", "test");

            Assert.IsTrue(list.Contains(0x01F5, 1, false));
            Assert.IsFalse(list.Contains(0x01F5, 0, false), "realm 0's Aerfalle Keep is not the Marketplace");
            Assert.IsFalse(list.Contains(0x01F5, 2, false));
            Assert.IsFalse(list.Contains(0x01F6, 1, false), "control: neighbouring landblock");
        }

        [TestMethod]
        public void RealmScopedEntry_NeverMatchesEphemeral_EvenWhenBareEntriesDo()
        {
            var list = LandblockRealmList.Parse("01F5@1", "test", bareMatchesEphemeral: true);

            Assert.IsFalse(list.Contains(0x01F5, 1, true));
        }

        [TestMethod]
        public void ContainsInstance_DecodesRealmAndEphemeralBit()
        {
            var list = LandblockRealmList.Parse("01F5@1", "test");

            Assert.IsTrue(list.ContainsInstance(0x01F5, Instance(1)));
            Assert.IsFalse(list.ContainsInstance(0x01F5, Instance(0)));
            Assert.IsFalse(list.ContainsInstance(0x01F5, Instance(1, ephemeral: true)));
        }

        [TestMethod]
        public void ContainsPosition_UsesLandblockAndInstance()
        {
            var list = LandblockRealmList.Parse("01F5@1", "test");

            Assert.IsTrue(list.Contains(new Position(0x01F50229, 50f, -180f, 0f, 0f, 0f, 0f, 1f, Instance(1))));
            Assert.IsFalse(list.Contains(new Position(0x01F50229, 50f, -180f, 0f, 0f, 0f, 0f, 1f, 0)));
            Assert.IsFalse(list.Contains((Position)null));
        }

        [TestMethod]
        public void MixedList_BareAndScoped_BothApply()
        {
            var list = LandblockRealmList.Parse("016C,01F5@1", "test");

            Assert.IsTrue(list.Contains(0x016C, 0, false));
            Assert.IsTrue(list.Contains(0x01F5, 1, false));
            Assert.IsFalse(list.Contains(0x01F5, 0, false));
        }
    }
}
