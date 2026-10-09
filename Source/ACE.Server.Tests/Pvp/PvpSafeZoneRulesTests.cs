using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;
using ACE.Server.Pvp.Rules;
using ACE.Server.Realms;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// Pure tests for PvpSafeZoneRules.IsPvpSafe/IsInSafeZone - the Marketplace combat-free zone rule (owner
    /// report: "I was able to shoot another PK in the Marketplace. Marketplace should be a combat free zone.").
    /// No PropertyManager key is read here.
    /// </summary>
    [TestClass]
    public class PvpSafeZoneRulesTests
    {
        private const ushort Marketplace = 0x016C;
        private const ushort AerfalleKeep = 0x01F5;
        private const ushort Elsewhere = 0x0090;
        private const ushort AlsoElsewhere = 0x00A1;

        private const ushort Realm0 = 0;
        private const ushort Realm1 = 1;

        private const bool Persistent = false;
        private const bool Ephemeral = true;

        // Parsed exactly the way PvpSafeZoneTunables parses the tunable (bare entries never match ephemeral).
        private static LandblockRealmList SafeSet(string raw) => LandblockRealmList.Parse(raw, "pvp_safe_landblocks", PvpSafeZoneTunables.BareMatchesEphemeral);

        /// <summary>Control: neither party is in a safe landblock - never refused.</summary>
        [TestMethod]
        public void NeitherInSafeZone_NotSafe()
        {
            Assert.IsFalse(PvpSafeZoneRules.IsPvpSafe(Elsewhere, Realm0, Persistent, AlsoElsewhere, Realm0, Persistent, SafeSet("016C")));
        }

        [TestMethod]
        public void AttackerInSafeZone_TargetOutside_IsSafe()
        {
            Assert.IsTrue(PvpSafeZoneRules.IsPvpSafe(Marketplace, Realm0, Persistent, Elsewhere, Realm0, Persistent, SafeSet("016C")));
        }

        [TestMethod]
        public void TargetInSafeZone_AttackerOutside_IsSafe()
        {
            Assert.IsTrue(PvpSafeZoneRules.IsPvpSafe(Elsewhere, Realm0, Persistent, Marketplace, Realm0, Persistent, SafeSet("016C")));
        }

        [TestMethod]
        public void BothInSafeZone_IsSafe()
        {
            Assert.IsTrue(PvpSafeZoneRules.IsPvpSafe(Marketplace, Realm0, Persistent, Marketplace, Realm0, Persistent, SafeSet("016C")));
        }

        /// <summary>An empty safe set (the tunable parsed to nothing) never applies, even in the Marketplace.</summary>
        [TestMethod]
        public void EmptySafeSet_NeverSafe()
        {
            Assert.IsFalse(PvpSafeZoneRules.IsPvpSafe(Marketplace, Realm0, Persistent, Marketplace, Realm0, Persistent, SafeSet("")));
        }

        [TestMethod]
        public void NullSafeSet_NeverSafe()
        {
            Assert.IsFalse(PvpSafeZoneRules.IsPvpSafe(Marketplace, Realm0, Persistent, Marketplace, Realm0, Persistent, null));
        }

        /// <summary>Multiple configured landblocks: a match against either one is safe, a match against neither is not.</summary>
        [TestMethod]
        public void MultipleSafeLandblocks_MatchesEither()
        {
            var safe = SafeSet("016C,00A1");

            Assert.IsTrue(PvpSafeZoneRules.IsPvpSafe(AlsoElsewhere, Realm0, Persistent, Elsewhere, Realm0, Persistent, safe));
            Assert.IsFalse(PvpSafeZoneRules.IsPvpSafe(Elsewhere, Realm0, Persistent, Elsewhere, Realm0, Persistent, safe));
        }

        // ================= ephemeral instance exclusion (code review 2026-09-26) =================

        /// <summary>
        /// A listed landblock id copied into an ephemeral instance (an arena match, a Proving Grounds run, a
        /// Thread dungeon) is NOT the Marketplace and must not inherit its protection, even though
        /// Position.LandblockShort (realm- and instance-blind) matches the configured id.
        /// </summary>
        [TestMethod]
        public void ListedLandblock_InEphemeralInstance_NotSafe()
        {
            Assert.IsFalse(PvpSafeZoneRules.IsInSafeZone(Marketplace, Realm0, Ephemeral, SafeSet("016C")));
        }

        [TestMethod]
        public void ListedLandblock_InPersistentRealm_IsSafe()
        {
            Assert.IsTrue(PvpSafeZoneRules.IsInSafeZone(Marketplace, Realm0, Persistent, SafeSet("016C")));
        }

        /// <summary>Whole-gate version: the attacker's copy of the Marketplace landblock id is ephemeral (an
        /// instanced match), the target's is the real, persistent Marketplace - only the target's counts.</summary>
        [TestMethod]
        public void AttackerInEphemeralCopyOfMarketplace_TargetInRealMarketplace_IsSafe()
        {
            Assert.IsTrue(PvpSafeZoneRules.IsPvpSafe(Marketplace, Realm0, Ephemeral, Marketplace, Realm0, Persistent, SafeSet("016C")));
        }

        /// <summary>Both copies of the listed landblock id are ephemeral - neither counts, so the gate is not safe.</summary>
        [TestMethod]
        public void BothInEphemeralCopiesOfMarketplace_NotSafe()
        {
            Assert.IsFalse(PvpSafeZoneRules.IsPvpSafe(Marketplace, Realm0, Ephemeral, Marketplace, Realm0, Ephemeral, SafeSet("016C")));
        }

        // ================= realm-scoped entries (Marketplace move to 01F5@1, 2026-09-26) =================

        /// <summary>
        /// The shipped default is realm-scoped: realm 0's Aerfalle Keep (0x01F5) is the retail dungeon and must
        /// NOT be a safe zone. Fails against the pre-realm rule, which matched on the landblock id alone.
        /// </summary>
        [TestMethod]
        public void RealmScopedDefault_Realm0AerfalleKeep_NotSafe()
        {
            Assert.IsFalse(PvpSafeZoneRules.IsInSafeZone(AerfalleKeep, Realm0, Persistent, SafeSet(PvpSafeZoneTunables.DefaultSafeLandblocks)));
        }

        /// <summary>
        /// Positive control for the case above: the same landblock in realm 1 (the Marketplace) IS safe, so the
        /// realm-0 refusal is the realm scoping at work and not the entry failing to parse.
        /// </summary>
        [TestMethod]
        public void RealmScopedDefault_Realm1Marketplace_IsSafe()
        {
            Assert.IsTrue(PvpSafeZoneRules.IsInSafeZone(AerfalleKeep, Realm1, Persistent, SafeSet(PvpSafeZoneTunables.DefaultSafeLandblocks)));
        }

        /// <summary>An ephemeral instance of realm 1's 0x01F5 is not the Marketplace, realm-scoped entry or not.</summary>
        [TestMethod]
        public void RealmScopedDefault_EphemeralRealm1Copy_NotSafe()
        {
            Assert.IsFalse(PvpSafeZoneRules.IsInSafeZone(AerfalleKeep, Realm1, Ephemeral, SafeSet(PvpSafeZoneTunables.DefaultSafeLandblocks)));
        }

        /// <summary>
        /// Back-compat: an existing BARE 016C row keeps its old meaning exactly - the landblock in every
        /// persistent realm, never an ephemeral instance.
        /// </summary>
        [TestMethod]
        public void BareEntry_016C_Unchanged_AnyPersistentRealm_NoEphemeral()
        {
            var safe = SafeSet("016C");

            Assert.IsTrue(PvpSafeZoneRules.IsInSafeZone(Marketplace, Realm0, Persistent, safe));
            Assert.IsTrue(PvpSafeZoneRules.IsInSafeZone(Marketplace, Realm1, Persistent, safe));
            Assert.IsFalse(PvpSafeZoneRules.IsInSafeZone(Marketplace, Realm1, Ephemeral, safe));
        }

        /// <summary>Whole-gate: attacker in realm 0's Aerfalle Keep, target in realm 1's Marketplace - the target's side refuses.</summary>
        [TestMethod]
        public void RealmScopedDefault_TargetInMarketplace_AttackerInRealm0Keep_IsSafe()
        {
            Assert.IsTrue(PvpSafeZoneRules.IsPvpSafe(AerfalleKeep, Realm0, Persistent, AerfalleKeep, Realm1, Persistent, SafeSet(PvpSafeZoneTunables.DefaultSafeLandblocks)));
            Assert.IsFalse(PvpSafeZoneRules.IsPvpSafe(AerfalleKeep, Realm0, Persistent, AerfalleKeep, Realm0, Persistent, SafeSet(PvpSafeZoneTunables.DefaultSafeLandblocks)));
        }
    }
}
