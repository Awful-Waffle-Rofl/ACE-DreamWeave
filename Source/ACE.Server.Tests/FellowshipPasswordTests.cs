using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Entity;
using ACE.Server.Command.Handlers;

using static ACE.Server.Command.Handlers.FellowshipCommands;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Pure statics behind password-protected fellowships: <see cref="Fellowship"/>'s normalize/validate/
    /// hash/compare and guess-lockout rule, and <see cref="FellowshipCommands"/>'s "xp" tell body parser and
    /// password-required hint text.
    ///
    /// Only these are tested here - PropertyManager reads throw in unit tests, so the instance-level guard
    /// methods that read 'fellowship_password_max_attempts'/'fellowship_password_lockout'
    /// (IsPasswordGuessLockedOut, RecordFailedPasswordAttempt) and anything constructing a Fellowship are out
    /// of reach, matching the existing pattern in FellowshipLeechWarningTests.
    /// </summary>
    [TestClass]
    public class FellowshipPasswordTests
    {
        // ---- TryNormalizePassword ----

        [TestMethod]
        public void Normalize_HappyPath_TrimsAndLowercases()
        {
            Assert.IsTrue(Fellowship.TryNormalizePassword("  HeLLo  ", out var normalized, out var error));
            Assert.AreEqual("hello", normalized);
            Assert.IsNull(error);
        }

        [TestMethod]
        public void Normalize_Null_IsRejected()
        {
            Assert.IsFalse(Fellowship.TryNormalizePassword(null, out var normalized, out var error));
            Assert.IsNull(normalized);
            Assert.IsNotNull(error);
        }

        [TestMethod]
        public void Normalize_EmptyAfterTrim_IsRejected()
        {
            Assert.IsFalse(Fellowship.TryNormalizePassword("   ", out _, out var error));
            Assert.IsNotNull(error);
        }

        [TestMethod]
        public void Normalize_InternalWhitespace_IsRejected()
        {
            Assert.IsFalse(Fellowship.TryNormalizePassword("hello world", out _, out var error));
            Assert.IsNotNull(error);
        }

        [TestMethod]
        public void Normalize_TabCharacter_IsRejected()
        {
            Assert.IsFalse(Fellowship.TryNormalizePassword("hello\tworld", out _, out var error));
            Assert.IsNotNull(error);
        }

        [TestMethod]
        public void Normalize_ExactlyMaxLength_IsAccepted()
        {
            var pw = new string('a', 32);
            Assert.IsTrue(Fellowship.TryNormalizePassword(pw, out var normalized, out _));
            Assert.AreEqual(pw, normalized);
        }

        [TestMethod]
        public void Normalize_OverMaxLength_IsRejected()
        {
            var pw = new string('a', 33);
            Assert.IsFalse(Fellowship.TryNormalizePassword(pw, out _, out var error));
            Assert.IsNotNull(error);
        }

        // ---- ComputePasswordHash / CheckPasswordHash ----

        [TestMethod]
        public void HashAndCheck_CorrectPassword_Matches()
        {
            var salt = Fellowship.GenerateSalt();
            var hash = Fellowship.ComputePasswordHash("hunter2", salt);

            Assert.IsTrue(Fellowship.CheckPasswordHash("hunter2", salt, hash));
        }

        [TestMethod]
        public void HashAndCheck_IsCaseInsensitiveWhenBothSidesNormalized()
        {
            // simulates the real call path: both sides are normalized (lower-invariant) before hashing/checking
            Fellowship.TryNormalizePassword("HuNTeR2", out var setNormalized, out _);
            Fellowship.TryNormalizePassword("hunter2", out var checkNormalized, out _);

            var salt = Fellowship.GenerateSalt();
            var hash = Fellowship.ComputePasswordHash(setNormalized, salt);

            Assert.IsTrue(Fellowship.CheckPasswordHash(checkNormalized, salt, hash));
        }

        [TestMethod]
        public void HashAndCheck_WrongPassword_Fails()
        {
            var salt = Fellowship.GenerateSalt();
            var hash = Fellowship.ComputePasswordHash("hunter2", salt);

            Assert.IsFalse(Fellowship.CheckPasswordHash("hunter3", salt, hash));
        }

        [TestMethod]
        public void HashAndCheck_DifferentSalts_ProduceDifferentHashes()
        {
            var salt1 = Fellowship.GenerateSalt();
            var salt2 = Fellowship.GenerateSalt();

            var hash1 = Fellowship.ComputePasswordHash("hunter2", salt1);
            var hash2 = Fellowship.ComputePasswordHash("hunter2", salt2);

            CollectionAssert.AreNotEqual(hash1, hash2);
        }

        [TestMethod]
        public void CheckPasswordHash_NullSaltOrHash_ReturnsFalse()
        {
            var salt = Fellowship.GenerateSalt();
            var hash = Fellowship.ComputePasswordHash("hunter2", salt);

            Assert.IsFalse(Fellowship.CheckPasswordHash("hunter2", null, hash));
            Assert.IsFalse(Fellowship.CheckPasswordHash("hunter2", salt, null));
        }

        // ---- IsPasswordLockedOut / PasswordLockoutRemaining ----

        [TestMethod]
        public void Lockout_UnderMaxAttempts_NotLockedOut()
        {
            Assert.IsFalse(Fellowship.IsPasswordLockedOut(4, 1000, 1010, 5, 300));
        }

        [TestMethod]
        public void Lockout_AtMaxAttemptsWithinWindow_IsLockedOut()
        {
            Assert.IsTrue(Fellowship.IsPasswordLockedOut(5, 1000, 1010, 5, 300));
        }

        [TestMethod]
        public void Lockout_AtMaxAttemptsButWindowExpired_NotLockedOut()
        {
            Assert.IsFalse(Fellowship.IsPasswordLockedOut(5, 1000, 1301, 5, 300));
        }

        [TestMethod]
        public void Lockout_MaxAttemptsZeroOrLess_NeverLockedOut()
        {
            Assert.IsFalse(Fellowship.IsPasswordLockedOut(100, 1000, 1001, 0, 300));
            Assert.IsFalse(Fellowship.IsPasswordLockedOut(100, 1000, 1001, -1, 300));
        }

        [TestMethod]
        public void LockoutRemaining_WithinWindow_ReturnsPositiveRemainder()
        {
            Assert.AreEqual(200, Fellowship.PasswordLockoutRemaining(1000, 1100, 300));
        }

        [TestMethod]
        public void LockoutRemaining_PastWindow_ClampsToZero()
        {
            Assert.AreEqual(0, Fellowship.PasswordLockoutRemaining(1000, 5000, 300));
        }

        // ---- TryParseXpTellBody ----

        [TestMethod]
        public void ParseXp_BareLowercase_Matches()
        {
            Assert.IsTrue(TryParseXpTellBody("xp", out var password));
            Assert.IsNull(password);
        }

        [TestMethod]
        public void ParseXp_BareUppercase_Matches()
        {
            Assert.IsTrue(TryParseXpTellBody("XP", out var password));
            Assert.IsNull(password);
        }

        [TestMethod]
        public void ParseXp_WithPasswordAndPadding_Matches()
        {
            Assert.IsTrue(TryParseXpTellBody(" xp  pw ", out var password));
            Assert.AreEqual("pw", password);
        }

        [TestMethod]
        public void ParseXp_TooManyTokens_IsRejected()
        {
            Assert.IsFalse(TryParseXpTellBody("xp a b", out var password));
            Assert.IsNull(password);
        }

        [TestMethod]
        public void ParseXp_SimilarButWrongWord_IsRejected()
        {
            Assert.IsFalse(TryParseXpTellBody("xpp", out var password));
            Assert.IsNull(password);
        }

        [TestMethod]
        public void ParseXp_Null_IsRejected()
        {
            Assert.IsFalse(TryParseXpTellBody(null, out var password));
            Assert.IsNull(password);
        }

        [TestMethod]
        public void ParseXp_EmptyString_IsRejected()
        {
            Assert.IsFalse(TryParseXpTellBody("", out var password));
            Assert.IsNull(password);
        }

        [TestMethod]
        public void ParseXp_UnrelatedMessage_IsRejected()
        {
            Assert.IsFalse(TryParseXpTellBody("hello there", out var password));
            Assert.IsNull(password);
        }

        // ---- BuildPasswordRequiredHint ----

        [TestMethod]
        public void Hint_Join_NamesTheJoinCommand()
        {
            var hint = BuildPasswordRequiredHint(FellowshipJoinEntryPath.Join, "Bob");

            StringAssert.Contains(hint, "/fship join Bob <password>");
        }

        [TestMethod]
        public void Hint_XpTell_NamesTheTellCommand()
        {
            var hint = BuildPasswordRequiredHint(FellowshipJoinEntryPath.XpTell, "Bob");

            StringAssert.Contains(hint, "/tell Bob xp <password>");
        }

        // ---- SelectJoinLandblockCandidate ----

        [TestMethod]
        public void JoinLandblock_NoPasswordSupplied_PicksLargestOpen()
        {
            var candidates = new List<JoinLandblockCandidate>
            {
                new JoinLandblockCandidate(1, true, false, false, 2),
                new JoinLandblockCandidate(2, true, false, false, 5),
            };

            var selection = SelectJoinLandblockCandidate(candidates, passwordSupplied: false);

            Assert.AreEqual(2, selection.SelectedId);
            Assert.AreEqual(0, selection.RecordFailureIds.Count);
            Assert.IsFalse(selection.NoMatch);
        }

        [TestMethod]
        public void JoinLandblock_NoCandidatesAtAll_SelectsNothingAndDoesNotReportNoMatch()
        {
            var selection = SelectJoinLandblockCandidate(new List<JoinLandblockCandidate>(), passwordSupplied: false);

            Assert.IsNull(selection.SelectedId);
            Assert.AreEqual(0, selection.RecordFailureIds.Count);
            Assert.IsFalse(selection.NoMatch);
        }

        [TestMethod]
        public void JoinLandblock_PasswordSupplied_NoPasswordCandidatesAtAll_FallsBackToOpenOnly()
        {
            // password supplied, but every candidate on the landblock is Open - unchanged behavior
            var candidates = new List<JoinLandblockCandidate>
            {
                new JoinLandblockCandidate(1, true, false, false, 3),
            };

            var selection = SelectJoinLandblockCandidate(candidates, passwordSupplied: true);

            Assert.AreEqual(1, selection.SelectedId);
            Assert.AreEqual(0, selection.RecordFailureIds.Count);
            Assert.IsFalse(selection.NoMatch);
        }

        [TestMethod]
        public void JoinLandblock_PasswordMatchesOneClosedCandidate_SelectsIt()
        {
            var candidates = new List<JoinLandblockCandidate>
            {
                new JoinLandblockCandidate(1, false, false, true, 3),
            };

            var selection = SelectJoinLandblockCandidate(candidates, passwordSupplied: true);

            Assert.AreEqual(1, selection.SelectedId);
            Assert.AreEqual(0, selection.RecordFailureIds.Count);
            Assert.IsFalse(selection.NoMatch);
        }

        [TestMethod]
        public void JoinLandblock_LargestMatchWinsOverSmallerOpenOrMatchingCandidate()
        {
            var candidates = new List<JoinLandblockCandidate>
            {
                new JoinLandblockCandidate(1, true, false, false, 2),   // Open, small
                new JoinLandblockCandidate(2, false, false, true, 7),   // Closed, matches, largest
                new JoinLandblockCandidate(3, false, false, false, 9),  // Closed, does NOT match, largest overall
            };

            var selection = SelectJoinLandblockCandidate(candidates, passwordSupplied: true);

            Assert.AreEqual(2, selection.SelectedId);
        }

        [TestMethod]
        public void JoinLandblock_NoMatchesAtAll_RecordsFailureAgainstEveryEvaluatedCandidate_NoCreate()
        {
            var candidates = new List<JoinLandblockCandidate>
            {
                new JoinLandblockCandidate(1, false, false, false, 3),
                new JoinLandblockCandidate(2, false, false, false, 5),
            };

            var selection = SelectJoinLandblockCandidate(candidates, passwordSupplied: true);

            Assert.IsNull(selection.SelectedId);
            Assert.IsTrue(selection.NoMatch);
            CollectionAssert.AreEquivalent(new List<int> { 1, 2 }, selection.RecordFailureIds);
        }

        [TestMethod]
        public void JoinLandblock_LockedOutCandidatesAreSkipped_NotRecordedAgain()
        {
            var candidates = new List<JoinLandblockCandidate>
            {
                new JoinLandblockCandidate(1, false, true, false, 3),   // locked out - must not be evaluated/recorded
                new JoinLandblockCandidate(2, false, false, false, 5),  // evaluated, mismatch - recorded
            };

            var selection = SelectJoinLandblockCandidate(candidates, passwordSupplied: true);

            Assert.IsNull(selection.SelectedId);
            Assert.IsTrue(selection.NoMatch);
            CollectionAssert.AreEqual(new List<int> { 2 }, selection.RecordFailureIds);
        }

        [TestMethod]
        public void JoinLandblock_OpenCandidateWinsEvenWhenPasswordedCandidatesAllMismatch()
        {
            var candidates = new List<JoinLandblockCandidate>
            {
                new JoinLandblockCandidate(1, true, false, false, 1),
                new JoinLandblockCandidate(2, false, false, false, 9),
            };

            var selection = SelectJoinLandblockCandidate(candidates, passwordSupplied: true);

            Assert.AreEqual(1, selection.SelectedId);
            Assert.IsFalse(selection.NoMatch);
            // the Open candidate won outright, so the mismatching Closed candidate must not be dinged either
            Assert.AreEqual(0, selection.RecordFailureIds.Count);
        }

        /// <summary>
        /// Demonstrates that repeated wrong guesses routed through this selection function reach the same
        /// guess-lockout threshold as <see cref="Fellowship.IsPasswordLockedOut"/> - i.e. the caller wiring
        /// (SelectJoinLandblockCandidate's RecordFailureIds -> Fellowship.RecordFailedPasswordAttempt's
        /// count/first-failure bookkeeping -> Fellowship.IsPasswordGuessLockedOut's GuessLockedOut input on
        /// the next call) converges on lockout for that one player+fellowship, and that once locked out the
        /// candidate is skipped rather than evaluated or re-recorded.
        /// </summary>
        [TestMethod]
        public void JoinLandblock_RepeatedMismatches_ReachLockout_ThenSkipsEvaluation()
        {
            const long maxAttempts = 3;
            const long lockoutSeconds = 300;
            const double now = 10000;

            var count = 0;
            var firstFailure = 0.0;

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var lockedOutBeforeThisAttempt = Fellowship.IsPasswordLockedOut(count, firstFailure, now, maxAttempts, lockoutSeconds);
                Assert.IsFalse(lockedOutBeforeThisAttempt, $"should not be locked out before attempt {attempt}");

                var candidates = new List<JoinLandblockCandidate>
                {
                    new JoinLandblockCandidate(1, false, lockedOutBeforeThisAttempt, false, 3),
                };

                var selection = SelectJoinLandblockCandidate(candidates, passwordSupplied: true);

                Assert.IsTrue(selection.NoMatch);
                CollectionAssert.AreEqual(new List<int> { 1 }, selection.RecordFailureIds);

                // mirrors Fellowship.RecordFailedPasswordAttempt's reset-on-stale-window rule
                if (count > 0 && now - firstFailure < lockoutSeconds)
                    count++;
                else
                {
                    count = 1;
                    firstFailure = now;
                }
            }

            // after 'maxAttempts' recorded failures within the window, the guess is now locked out
            Assert.IsTrue(Fellowship.IsPasswordLockedOut(count, firstFailure, now, maxAttempts, lockoutSeconds));

            var lockedCandidates = new List<JoinLandblockCandidate>
            {
                new JoinLandblockCandidate(1, false, true, false, 3),
            };

            var lockedSelection = SelectJoinLandblockCandidate(lockedCandidates, passwordSupplied: true);

            // the locked-out candidate is skipped entirely: not evaluated, not re-recorded
            Assert.IsTrue(lockedSelection.NoMatch);
            Assert.AreEqual(0, lockedSelection.RecordFailureIds.Count);
        }
    }
}
