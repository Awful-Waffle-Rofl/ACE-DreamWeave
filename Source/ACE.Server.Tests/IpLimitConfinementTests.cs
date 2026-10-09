using System.Collections.Generic;

using ACE.Server.Managers;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// WaffleACE: the pure confinement rule (IpLimitManager.IsConfinedCore) that decides whether a character
    /// counts as CONFINED for the IP active-player limit - the Marketplace/mule-landblock case that already
    /// existed, plus the new PvP arena / Proving Grounds activity exemption. Pure - no PropertyManager reads,
    /// no world state - so it is unit-tested directly here, the same way IpLimitRuleTests pins SelectViolators.
    /// </summary>
    [TestClass]
    public class IpLimitConfinementTests
    {
        private static IpLimitZoneDials Dials(bool arena = true, bool proving = true, bool speed = true, int tailSeconds = 15)
        {
            return new IpLimitZoneDials(arena, proving, speed, tailSeconds);
        }

        // ==================================================================================
        // Control: every dial off reduces exactly to the pre-existing Marketplace-only rule.
        // ==================================================================================

        [TestMethod]
        public void AllDialsOff_TeleportingOnly_IsConfined()
        {
            var dials = Dials(arena: false, proving: false, speed: false);

            var reason = IpLimitManager.IsConfinedCore(
                teleporting: true, inMuleLandblock: false, inArenaMatchSpace: false,
                inSpeedRun: false, inProvingGroundsRun: false, inActivityTail: false, dials: dials);

            Assert.AreEqual(IpLimitConfinementReason.Teleporting, reason);
        }

        [TestMethod]
        public void AllDialsOff_MuleLandblockOnly_IsConfined()
        {
            var dials = Dials(arena: false, proving: false, speed: false);

            var reason = IpLimitManager.IsConfinedCore(
                teleporting: false, inMuleLandblock: true, inArenaMatchSpace: false,
                inSpeedRun: false, inProvingGroundsRun: false, inActivityTail: false, dials: dials);

            Assert.AreEqual(IpLimitConfinementReason.MuleLandblock, reason);
        }

        /// <summary>
        /// The actual control: every activity term true, but every dial off, and no tail (RefreshActivityStamp
        /// never stamps when a dial is off, so a real caller could never produce inActivityTail=true here - this
        /// pins that IsConfinedCore itself also agrees). Result must be None, exactly today's Marketplace-only
        /// behaviour for a character standing outside a mule landblock and not teleporting.
        /// </summary>
        [TestMethod]
        public void AllDialsOff_EveryActivityTermTrue_IsNotConfined()
        {
            var dials = Dials(arena: false, proving: false, speed: false);

            var reason = IpLimitManager.IsConfinedCore(
                teleporting: false, inMuleLandblock: false, inArenaMatchSpace: true,
                inSpeedRun: true, inProvingGroundsRun: true, inActivityTail: false, dials: dials);

            Assert.AreEqual(IpLimitConfinementReason.None, reason);
        }

        /// <summary>
        /// End-to-end control, composing IsWithinActivityTail with IsConfinedCore: a tail STAMPED while its
        /// dial was on, then read after every ip_limit_confine_* dial (including that one) has been turned
        /// off, must resolve to None - the dial-off case is not just "IsConfinedCore trusts a false fact", it
        /// is "the fact itself goes false the moment the dial does", which is what IsWithinActivityTail exists
        /// to guarantee (code-review finding 2, 2026-09-26).
        /// </summary>
        [TestMethod]
        public void AllDialsOff_TailStampedWhileOn_IsNotConfined()
        {
            var allOff = Dials(arena: false, proving: false, speed: false);

            var inActivityTail = IpLimitManager.IsWithinActivityTail(
                seenAt: StampedAt, stampedInstance: StampedInstance, stampedReason: IpLimitConfinementReason.PvpArena,
                isEphemeralRealm: true, currentInstance: StampedInstance, now: StampedAt + 5, dials: allOff);

            Assert.IsFalse(inActivityTail, "sanity: the dial-off gate inside IsWithinActivityTail itself");

            var reason = IpLimitManager.IsConfinedCore(
                teleporting: false, inMuleLandblock: false, inArenaMatchSpace: false,
                inSpeedRun: false, inProvingGroundsRun: false, inActivityTail: inActivityTail, dials: allOff);

            Assert.AreEqual(IpLimitConfinementReason.None, reason);
        }

        [TestMethod]
        public void NothingTrue_IsNotConfined()
        {
            var reason = IpLimitManager.IsConfinedCore(
                teleporting: false, inMuleLandblock: false, inArenaMatchSpace: false,
                inSpeedRun: false, inProvingGroundsRun: false, inActivityTail: false, dials: Dials());

            Assert.AreEqual(IpLimitConfinementReason.None, reason);
        }

        // ==================================================================================
        // Each activity term, dial on.
        // ==================================================================================

        [TestMethod]
        public void ArenaMatchSpace_DialOn_IsConfined()
        {
            var reason = IpLimitManager.IsConfinedCore(
                teleporting: false, inMuleLandblock: false, inArenaMatchSpace: true,
                inSpeedRun: false, inProvingGroundsRun: false, inActivityTail: false, dials: Dials());

            Assert.AreEqual(IpLimitConfinementReason.PvpArena, reason);
        }

        [TestMethod]
        public void ArenaMatchSpace_DialOff_IsNotConfined()
        {
            var reason = IpLimitManager.IsConfinedCore(
                teleporting: false, inMuleLandblock: false, inArenaMatchSpace: true,
                inSpeedRun: false, inProvingGroundsRun: false, inActivityTail: false, dials: Dials(arena: false));

            Assert.AreEqual(IpLimitConfinementReason.None, reason);
        }

        [TestMethod]
        public void SpeedRun_DialOn_IsConfined()
        {
            var reason = IpLimitManager.IsConfinedCore(
                teleporting: false, inMuleLandblock: false, inArenaMatchSpace: false,
                inSpeedRun: true, inProvingGroundsRun: false, inActivityTail: false, dials: Dials());

            Assert.AreEqual(IpLimitConfinementReason.SpeedRun, reason);
        }

        /// <summary>
        /// Speed has its OWN dial, separate from ip_limit_confine_proving_grounds - this pins that turning off
        /// only ConfineSpeed does not touch a speed run's inProvingGroundsRun-shaped input, and that Speed is
        /// unaffected when ConfineProving (not ConfineSpeed) is the one switched off.
        /// </summary>
        [TestMethod]
        public void SpeedRun_SpeedDialOff_ProvingDialOn_IsNotConfined()
        {
            var reason = IpLimitManager.IsConfinedCore(
                teleporting: false, inMuleLandblock: false, inArenaMatchSpace: false,
                inSpeedRun: true, inProvingGroundsRun: false, inActivityTail: false, dials: Dials(speed: false));

            Assert.AreEqual(IpLimitConfinementReason.None, reason);
        }

        [TestMethod]
        public void SpeedRun_ProvingDialOff_SpeedDialOn_IsStillConfined()
        {
            var reason = IpLimitManager.IsConfinedCore(
                teleporting: false, inMuleLandblock: false, inArenaMatchSpace: false,
                inSpeedRun: true, inProvingGroundsRun: false, inActivityTail: false, dials: Dials(proving: false));

            Assert.AreEqual(IpLimitConfinementReason.SpeedRun, reason);
        }

        [TestMethod]
        public void ProvingGroundsRun_DialOn_IsConfined()
        {
            var reason = IpLimitManager.IsConfinedCore(
                teleporting: false, inMuleLandblock: false, inArenaMatchSpace: false,
                inSpeedRun: false, inProvingGroundsRun: true, inActivityTail: false, dials: Dials());

            Assert.AreEqual(IpLimitConfinementReason.ProvingGrounds, reason);
        }

        [TestMethod]
        public void ProvingGroundsRun_DialOff_IsNotConfined()
        {
            var reason = IpLimitManager.IsConfinedCore(
                teleporting: false, inMuleLandblock: false, inArenaMatchSpace: false,
                inSpeedRun: false, inProvingGroundsRun: true, inActivityTail: false, dials: Dials(proving: false));

            Assert.AreEqual(IpLimitConfinementReason.None, reason);
        }

        [TestMethod]
        public void ActivityTail_IsConfined_RegardlessOfDials()
        {
            // Dials off; the tail fact itself already encodes "a caller decided this still counts" - a real
            // caller (RefreshActivityStamp) only ever produces inActivityTail=true when a dial was on at the
            // time of the stamp, but IsConfinedCore does not re-derive that, it trusts the fact it is given.
            var reason = IpLimitManager.IsConfinedCore(
                teleporting: false, inMuleLandblock: false, inArenaMatchSpace: false,
                inSpeedRun: false, inProvingGroundsRun: false, inActivityTail: true, dials: Dials(arena: false, proving: false, speed: false));

            Assert.AreEqual(IpLimitConfinementReason.ActivityTail, reason);
        }

        // ==================================================================================
        // Priority order: first applicable reason wins.
        // ==================================================================================

        [TestMethod]
        public void Teleporting_TakesPriorityOverEverythingElse()
        {
            var reason = IpLimitManager.IsConfinedCore(
                teleporting: true, inMuleLandblock: true, inArenaMatchSpace: true,
                inSpeedRun: true, inProvingGroundsRun: true, inActivityTail: true, dials: Dials());

            Assert.AreEqual(IpLimitConfinementReason.Teleporting, reason);
        }

        [TestMethod]
        public void MuleLandblock_TakesPriorityOverActivityTermsAndTail()
        {
            var reason = IpLimitManager.IsConfinedCore(
                teleporting: false, inMuleLandblock: true, inArenaMatchSpace: true,
                inSpeedRun: true, inProvingGroundsRun: true, inActivityTail: true, dials: Dials());

            Assert.AreEqual(IpLimitConfinementReason.MuleLandblock, reason);
        }

        [TestMethod]
        public void ActivityTail_TakesPriorityOverLiveActivityTerms()
        {
            var reason = IpLimitManager.IsConfinedCore(
                teleporting: false, inMuleLandblock: false, inArenaMatchSpace: true,
                inSpeedRun: true, inProvingGroundsRun: true, inActivityTail: true, dials: Dials());

            Assert.AreEqual(IpLimitConfinementReason.ActivityTail, reason);
        }

        [TestMethod]
        public void ArenaMatchSpace_TakesPriorityOverSpeedAndProving()
        {
            var reason = IpLimitManager.IsConfinedCore(
                teleporting: false, inMuleLandblock: false, inArenaMatchSpace: true,
                inSpeedRun: true, inProvingGroundsRun: true, inActivityTail: false, dials: Dials());

            Assert.AreEqual(IpLimitConfinementReason.PvpArena, reason);
        }

        [TestMethod]
        public void SpeedRun_TakesPriorityOverProvingGrounds()
        {
            var reason = IpLimitManager.IsConfinedCore(
                teleporting: false, inMuleLandblock: false, inArenaMatchSpace: false,
                inSpeedRun: true, inProvingGroundsRun: true, inActivityTail: false, dials: Dials());

            Assert.AreEqual(IpLimitConfinementReason.SpeedRun, reason);
        }

        // ==================================================================================
        // IsWithinActivityTail - the pure post-activity-tail rule. Code-review findings (2026-09-26):
        // (1) the tail must require the character to still be in the EXACT instance it was stamped in, not
        //     just any ephemeral realm; (2) the tail must honour the stamping reason's OWN dial at READ time,
        //     not just at stamp time.
        // ==================================================================================

        private const uint StampedInstance = 12345u;
        private const uint OtherInstance = 67890u;
        private const double StampedAt = 1000.0;

        [TestMethod]
        public void SameInstanceWithinWindow_IsWithinTail()
        {
            var within = IpLimitManager.IsWithinActivityTail(
                seenAt: StampedAt, stampedInstance: StampedInstance, stampedReason: IpLimitConfinementReason.PvpArena,
                isEphemeralRealm: true, currentInstance: StampedInstance, now: StampedAt + 10, dials: Dials());

            Assert.IsTrue(within);
        }

        /// <summary>
        /// Finding 1: a character stamped in an arena match that moves into an UNRELATED ephemeral instance
        /// (a Thread dungeon, a private instance) within the tail window must NOT read as confined there.
        /// </summary>
        [TestMethod]
        public void DifferentEphemeralInstance_IsNotWithinTail()
        {
            var within = IpLimitManager.IsWithinActivityTail(
                seenAt: StampedAt, stampedInstance: StampedInstance, stampedReason: IpLimitConfinementReason.PvpArena,
                isEphemeralRealm: true, currentInstance: OtherInstance, now: StampedAt + 10, dials: Dials());

            Assert.IsFalse(within);
        }

        [TestMethod]
        public void SameInstanceAfterWindow_IsNotWithinTail()
        {
            var dials = Dials(tailSeconds: 15);

            var within = IpLimitManager.IsWithinActivityTail(
                seenAt: StampedAt, stampedInstance: StampedInstance, stampedReason: IpLimitConfinementReason.PvpArena,
                isEphemeralRealm: true, currentInstance: StampedInstance, now: StampedAt + 16, dials: dials);

            Assert.IsFalse(within);
        }

        [TestMethod]
        public void NotEphemeralRealm_IsNotWithinTail()
        {
            // Same instance id can coincidentally match a non-ephemeral 0, so isEphemeralRealm is checked
            // independently rather than inferred from the instance id alone.
            var within = IpLimitManager.IsWithinActivityTail(
                seenAt: StampedAt, stampedInstance: StampedInstance, stampedReason: IpLimitConfinementReason.PvpArena,
                isEphemeralRealm: false, currentInstance: StampedInstance, now: StampedAt + 5, dials: Dials());

            Assert.IsFalse(within);
        }

        [TestMethod]
        public void NoStamp_IsNotWithinTail()
        {
            var within = IpLimitManager.IsWithinActivityTail(
                seenAt: null, stampedInstance: null, stampedReason: null,
                isEphemeralRealm: true, currentInstance: StampedInstance, now: StampedAt, dials: Dials());

            Assert.IsFalse(within);
        }

        /// <summary>
        /// Finding 2: a tail stamped while its dial was ON must stop counting the moment that SAME dial is
        /// read as OFF, even though the stamp itself (seenAt/instance/reason) is untouched and still fresh.
        /// </summary>
        [TestMethod]
        public void StampedWhileOn_ThenArenaDialTurnedOff_IsNotWithinTail()
        {
            var within = IpLimitManager.IsWithinActivityTail(
                seenAt: StampedAt, stampedInstance: StampedInstance, stampedReason: IpLimitConfinementReason.PvpArena,
                isEphemeralRealm: true, currentInstance: StampedInstance, now: StampedAt + 5, dials: Dials(arena: false));

            Assert.IsFalse(within);
        }

        [TestMethod]
        public void StampedWhileOn_ThenSpeedDialTurnedOff_IsNotWithinTail()
        {
            var within = IpLimitManager.IsWithinActivityTail(
                seenAt: StampedAt, stampedInstance: StampedInstance, stampedReason: IpLimitConfinementReason.SpeedRun,
                isEphemeralRealm: true, currentInstance: StampedInstance, now: StampedAt + 5, dials: Dials(speed: false));

            Assert.IsFalse(within);
        }

        [TestMethod]
        public void StampedWhileOn_ThenProvingDialTurnedOff_IsNotWithinTail()
        {
            var within = IpLimitManager.IsWithinActivityTail(
                seenAt: StampedAt, stampedInstance: StampedInstance, stampedReason: IpLimitConfinementReason.ProvingGrounds,
                isEphemeralRealm: true, currentInstance: StampedInstance, now: StampedAt + 5, dials: Dials(proving: false));

            Assert.IsFalse(within);
        }

        /// <summary>
        /// Turning a DIFFERENT dial off must not touch an unrelated reason's still-live tail - only the
        /// stamping reason's own dial governs it.
        /// </summary>
        [TestMethod]
        public void StampedArena_OtherDialsOff_IsStillWithinTail()
        {
            var within = IpLimitManager.IsWithinActivityTail(
                seenAt: StampedAt, stampedInstance: StampedInstance, stampedReason: IpLimitConfinementReason.PvpArena,
                isEphemeralRealm: true, currentInstance: StampedInstance, now: StampedAt + 5, dials: Dials(proving: false, speed: false));

            Assert.IsTrue(within);
        }

        [TestMethod]
        public void TailSecondsZero_IsNotWithinTail()
        {
            var within = IpLimitManager.IsWithinActivityTail(
                seenAt: StampedAt, stampedInstance: StampedInstance, stampedReason: IpLimitConfinementReason.PvpArena,
                isEphemeralRealm: true, currentInstance: StampedInstance, now: StampedAt, dials: Dials(tailSeconds: 0));

            Assert.IsFalse(within);
        }

        // ==================================================================================
        // IpLimitZoneTunables - PropertyManager.Get* throws in ACE.Server.Tests for any uncached key, so
        // DialSource must catch that and fall back to Defaults, following PvpTunables' own precedent.
        // ==================================================================================

        [TestMethod]
        public void DialSource_InTestEnvironment_FallsBackToDefaults()
        {
            var dials = IpLimitZoneTunables.DialSource();

            Assert.AreEqual(IpLimitZoneTunables.Defaults, dials);
        }

        [TestMethod]
        public void Defaults_AreAllOnWithFifteenSecondTail()
        {
            var d = IpLimitZoneTunables.Defaults;

            Assert.IsTrue(d.ConfineArena);
            Assert.IsTrue(d.ConfineProving);
            Assert.IsTrue(d.ConfineSpeed);
            Assert.AreEqual(15, d.TailSeconds);
        }

        // ==================================================================================
        // Sweep-level: IsConfinedCore composed with SelectViolators, in the style of
        // IpLimitSweepTests.DedupeThenSelectViolators_StaleSessionDoesNotEvictItsOwnAccount - this pins that a
        // character IsConfinedCore marks CONFINED for an activity reason is treated exactly like a
        // mule-landblock CONFINED character by the eviction rule: it does not count against the OUTSIDE cap,
        // freeing the address's one unrestricted slot for the main out fighting elsewhere.
        // ==================================================================================
        [TestMethod]
        public void ArenaConfinedAlt_DoesNotCountAgainstTheOutsideCap()
        {
            var mainConfined = IpLimitManager.IsConfinedCore(
                teleporting: false, inMuleLandblock: false, inArenaMatchSpace: false,
                inSpeedRun: false, inProvingGroundsRun: false, inActivityTail: false, dials: Dials()) != IpLimitConfinementReason.None;

            var altConfined = IpLimitManager.IsConfinedCore(
                teleporting: false, inMuleLandblock: false, inArenaMatchSpace: true,
                inSpeedRun: false, inProvingGroundsRun: false, inActivityTail: false, dials: Dials()) != IpLimitConfinementReason.None;

            Assert.IsFalse(mainConfined, "sanity: the main, out fighting, is not confined");
            Assert.IsTrue(altConfined, "sanity: the alt, in the arena, is confined");

            var candidates = new List<IpLimitCandidate>
            {
                new IpLimitCandidate(1, 100, mainConfined, 100), // main, out fighting
                new IpLimitCandidate(2, 50, altConfined, 200),   // alt, in the arena
            };

            // maxFree 1: exactly enough room for the one unconfined character (the main). If the arena alt
            // were NOT recognized as confined, it would also count as "outside" and one of the two would be
            // evicted - which is the bug this feature exists to prevent.
            var violators = IpLimitManager.SelectViolators(candidates, maxFree: 1, maxConfined: 2);

            Assert.AreEqual(0, violators.Count);
        }

        // ==================================================================================
        // mule_landblocks membership (IsInMuleLandblock) - realm-aware since the Marketplace
        // moved into realm 1's copy of Aerfalle Keep (01F5@1). Shared by the live confinement
        // check and the login gate's PERSISTED location.
        // ==================================================================================

        private static uint Instance(ushort realm, bool ephemeral = false)
            => ACE.Entity.Position.InstanceIDFromVars(realm, ephemeral ? (ushort)1 : (ushort)0, ephemeral);

        private static ACE.Server.Realms.LandblockRealmList MuleList(string raw)
            => ACE.Server.Realms.LandblockRealmList.Parse(raw, IpLimitManager.MuleLandblocksConfigKey, IpLimitManager.MuleLandblocksBareMatchesEphemeral);

        /// <summary>
        /// The login gate reads the persisted instance too: a character that logged out in realm 0's Aerfalle
        /// Keep (the retail dungeon) is NOT in the Marketplace under the realm-scoped default. Fails against the
        /// pre-realm gate, which compared (ObjCellId >> 16) alone.
        /// </summary>
        [TestMethod]
        public void MuleLandblock_RealmScopedDefault_OnlyRealm1()
        {
            var list = MuleList(ACE.Server.Managers.DefaultPropertyManager.DefaultStringProperties[IpLimitManager.MuleLandblocksConfigKey].Item);

            Assert.IsTrue(IpLimitManager.IsInMuleLandblock(list, 0x01F5, Instance(1)));
            Assert.IsFalse(IpLimitManager.IsInMuleLandblock(list, 0x01F5, Instance(0)));
            Assert.IsFalse(IpLimitManager.IsInMuleLandblock(list, 0x016C, Instance(0)), "the old Marketplace landblock is no longer a mule landblock by default");
        }

        /// <summary>Back-compat: a bare 016C row still matches every instance of 016C, ephemeral included, exactly as before.</summary>
        [TestMethod]
        public void MuleLandblock_BareEntry_Unchanged()
        {
            var list = MuleList("016C");

            Assert.IsTrue(IpLimitManager.IsInMuleLandblock(list, 0x016C, Instance(0)));
            Assert.IsTrue(IpLimitManager.IsInMuleLandblock(list, 0x016C, Instance(1)));
            Assert.IsTrue(IpLimitManager.IsInMuleLandblock(list, 0x016C, Instance(1, ephemeral: true)));
            Assert.IsFalse(IpLimitManager.IsInMuleLandblock(list, 0x016D, Instance(0)));
        }

        [TestMethod]
        public void MuleLandblock_NullList_NeverConfines()
        {
            Assert.IsFalse(IpLimitManager.IsInMuleLandblock(null, 0x016C, 0));
        }

        /// <summary>The /iplimit landblock add normaliser accepts the @R form and canonicalises it.</summary>
        [TestMethod]
        public void NormalizeLandblockToken_AcceptsRealmScopedEntries()
        {
            Assert.AreEqual("01F5@1", IpLimitManager.NormalizeLandblockToken("0x01f5@1"));
            Assert.AreEqual("016C", IpLimitManager.NormalizeLandblockToken("016c"));
            Assert.IsNull(IpLimitManager.NormalizeLandblockToken("01F5@"));
        }
    }
}
