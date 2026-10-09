using System;

using ACE.Entity.Enum;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Covers Player.GetCharacterPermissionFlagsForAccessLevel, the pure mapping SetEphemeralValues uses
    /// (under Server.Accounts.OverrideCharacterPermissions) to make a character's saved STAFF permission
    /// flags (IsAdmin/IsArch/IsEnvoy/IsSentinel) exactly reflect its account's current AccessLevel on
    /// login - including clearing them on demotion. IsAdvocate is deliberately NOT covered by this
    /// mapping: Advocate.cs also grants IsAdvocate per character independent of account AccessLevel, and
    /// clears it on removal, so login must never force that flag either way. See the comment on
    /// GetCharacterPermissionFlagsForAccessLevel and in SetEphemeralValues.
    /// Accessed via InternalsVisibleTo (ACE.Server.csproj:15); ACE.Server.Tests cannot construct a live
    /// Player, so this mapping is exercised directly instead of through the constructor.
    /// </summary>
    [TestClass]
    public class CharacterPermissionFlagsTests
    {
        [TestMethod]
        public void EveryAccessLevel_IsCovered()
        {
            // fails loudly if a new AccessLevel value is ever added without updating the mapping/this test
            foreach (AccessLevel accessLevel in Enum.GetValues(typeof(AccessLevel)))
            {
                var flags = Player.GetCharacterPermissionFlagsForAccessLevel(accessLevel);

                switch (accessLevel)
                {
                    case AccessLevel.Admin:
                        AssertOnlyFlagsSet(flags, isAdmin: true);
                        break;
                    case AccessLevel.Developer:
                        AssertOnlyFlagsSet(flags, isArch: true);
                        break;
                    case AccessLevel.Envoy:
                        AssertOnlyFlagsSet(flags, isEnvoy: true, isSentinel: true);
                        break;
                    case AccessLevel.Sentinel:
                        AssertOnlyFlagsSet(flags, isSentinel: true);
                        break;
                    case AccessLevel.Advocate:
                        // the mapping grants none of the four STAFF flags for Advocate - IsAdvocate itself
                        // is handled separately by the caller (SetEphemeralValues), not by this mapping
                        AssertOnlyFlagsSet(flags);
                        break;
                    case AccessLevel.Player:
                        AssertOnlyFlagsSet(flags);
                        break;
                    default:
                        Assert.Fail($"AccessLevel.{accessLevel} is not covered by this test - add a case.");
                        break;
                }
            }
        }

        [TestMethod]
        public void Admin_YieldsOnlyIsAdmin()
        {
            var flags = Player.GetCharacterPermissionFlagsForAccessLevel(AccessLevel.Admin);

            AssertOnlyFlagsSet(flags, isAdmin: true);
        }

        [TestMethod]
        public void Developer_YieldsOnlyIsArch()
        {
            var flags = Player.GetCharacterPermissionFlagsForAccessLevel(AccessLevel.Developer);

            AssertOnlyFlagsSet(flags, isArch: true);
        }

        [TestMethod]
        public void Envoy_YieldsIsEnvoyAndIsSentinelOnly()
        {
            var flags = Player.GetCharacterPermissionFlagsForAccessLevel(AccessLevel.Envoy);

            AssertOnlyFlagsSet(flags, isEnvoy: true, isSentinel: true);
        }

        [TestMethod]
        public void Sentinel_YieldsOnlyIsSentinel()
        {
            var flags = Player.GetCharacterPermissionFlagsForAccessLevel(AccessLevel.Sentinel);

            AssertOnlyFlagsSet(flags, isSentinel: true);
        }

        [TestMethod]
        public void Advocate_YieldsAllFourStaffFlagsFalse()
        {
            // Advocate does not grant any of the four staff flags via this mapping; IsAdvocate itself is
            // out of scope for CharacterPermissionFlags (see the type-level comment above)
            var flags = Player.GetCharacterPermissionFlagsForAccessLevel(AccessLevel.Advocate);

            AssertOnlyFlagsSet(flags);
        }

        [TestMethod]
        public void Player_YieldsAllFourStaffFlagsFalse()
        {
            var flags = Player.GetCharacterPermissionFlagsForAccessLevel(AccessLevel.Player);

            AssertOnlyFlagsSet(flags);
        }

        [TestMethod]
        public void DemotionFromAdminToPlayer_ClearsIsAdmin()
        {
            // the bug: an account demoted from Admin to Player must no longer resolve IsAdmin true
            var demoted = Player.GetCharacterPermissionFlagsForAccessLevel(AccessLevel.Player);

            Assert.IsFalse(demoted.IsAdmin, "IsAdmin must be cleared when the account is demoted to Player.");
        }

        [TestMethod]
        public void DemotionFromAdminToSentinel_ClearsIsAdminAndSetsOnlyIsSentinel()
        {
            // the bug: an account demoted from Admin to Sentinel must lose IsAdmin, and must not also
            // retain IsArch/IsEnvoy from any prior level
            var demoted = Player.GetCharacterPermissionFlagsForAccessLevel(AccessLevel.Sentinel);

            AssertOnlyFlagsSet(demoted, isSentinel: true);
        }

        [TestMethod]
        public void ShouldGrantAdvocateOnLogin_IsTrueOnlyAtAdvocateLevel_AndNeverImpliesClearing()
        {
            // Covers the rule at Player.cs's SetEphemeralValues:
            //     if (ShouldGrantAdvocateOnLogin(Session.AccessLevel)) IsAdvocate = true;
            // which is deliberately one-directional: login may only ever SET IsAdvocate true from this
            // helper, never clear it, because Advocate.cs also grants IsAdvocate per character
            // independent of account AccessLevel (including to a Player-level account) and clears it on
            // removal. A Player-level account can therefore still own an in-game-granted advocate, and
            // this sweep pins that every non-Advocate level - Admin included - returns false, so the
            // caller's guard is never accidentally widened to cover them.
            foreach (AccessLevel accessLevel in Enum.GetValues(typeof(AccessLevel)))
            {
                var expected = accessLevel == AccessLevel.Advocate;

                Assert.AreEqual(
                    expected,
                    Player.ShouldGrantAdvocateOnLogin(accessLevel),
                    $"ShouldGrantAdvocateOnLogin(AccessLevel.{accessLevel}) should be {expected}.");
            }
        }

        private static void AssertOnlyFlagsSet(
            Player.CharacterPermissionFlags flags,
            bool isAdmin = false,
            bool isArch = false,
            bool isEnvoy = false,
            bool isSentinel = false)
        {
            Assert.AreEqual(isAdmin, flags.IsAdmin, nameof(flags.IsAdmin));
            Assert.AreEqual(isArch, flags.IsArch, nameof(flags.IsArch));
            Assert.AreEqual(isEnvoy, flags.IsEnvoy, nameof(flags.IsEnvoy));
            Assert.AreEqual(isSentinel, flags.IsSentinel, nameof(flags.IsSentinel));
        }
    }
}
