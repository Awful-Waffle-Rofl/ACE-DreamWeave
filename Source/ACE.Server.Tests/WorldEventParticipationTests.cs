using System.Collections.Generic;

using ACE.Server.WorldEvents;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Unit coverage for <see cref="WorldEventParticipation"/> (TECH-DESIGN 2.6). Only the pure static
    /// cores (<see cref="WorldEventParticipation.CreditDamageCore"/>, TopKillerCore, TopDamagerCore) are
    /// exercised - CreditDamage/CreditKill all need a live Creature/Player, which D6 forbids a test from
    /// constructing, so a real ledger can never be populated from here.
    ///
    /// The last block is the exception: the INSTANCE readers on an empty ledger. TryGetRecord became a
    /// claim-time decision on 2026-09-07 (crate vs coal), and its empty-ledger answer is the whole coal
    /// branch, so it is pinned even though the populated case is out of reach.
    /// </summary>
    [TestClass]
    public class WorldEventParticipationTests
    {
        private const double T0 = 1_700_000_000d;

        private static ParticipantRecord Record(uint guid, string name, float damage = 0, int kills = 0, double firstCredit = T0)
        {
            return new ParticipantRecord
            {
                CharacterGuid = guid,
                Name = name,
                Damage = damage,
                Kills = kills,
                FirstCredit = firstCredit
            };
        }

        // ---- CreditDamageCore -------------------------------------------------------------------------

        [TestMethod]
        public void CreditDamageCore_RepeatDamager_AccumulatesOntoTheSameRecord()
        {
            var records = new Dictionary<uint, ParticipantRecord>();

            var damagers = new List<(uint guid, uint accountId, string ip, string name, float damage)>
            {
                (1001, 5, "10.0.0.1", "Alice", 30f),
                (1001, 5, "10.0.0.1", "Alice", 20f)
            };

            WorldEventParticipation.CreditDamageCore(damagers, records, T0);

            Assert.AreEqual(1, records.Count, "a repeat guid must not create a second record");
            Assert.AreEqual(50f, records[1001].Damage);
            Assert.AreEqual("Alice", records[1001].Name);
            Assert.AreEqual(5u, records[1001].AccountId);
            Assert.AreEqual(T0, records[1001].FirstCredit);
        }

        [TestMethod]
        public void CreditDamageCore_FirstCreditMetadataIsCapturedOnlyOnce()
        {
            var records = new Dictionary<uint, ParticipantRecord>();

            WorldEventParticipation.CreditDamageCore(
                new List<(uint guid, uint accountId, string ip, string name, float damage)> { (1001, 5, "10.0.0.1", "Alice", 10f) },
                records, T0);

            // A later credit for the same guid, from a different creature's death, must not move
            // FirstCredit or overwrite the identity fields - only the running damage total changes.
            WorldEventParticipation.CreditDamageCore(
                new List<(uint guid, uint accountId, string ip, string name, float damage)> { (1001, 999, "10.0.0.2", "AliceRenamed", 5f) },
                records, T0 + 100);

            Assert.AreEqual(15f, records[1001].Damage);
            Assert.AreEqual(T0, records[1001].FirstCredit);
            Assert.AreEqual(5u, records[1001].AccountId);
            Assert.AreEqual("Alice", records[1001].Name);
        }

        [TestMethod]
        public void CreditDamageCore_PetDamageLandsOnTheOwner()
        {
            // Simulates WorldEventParticipation.CreditDamage resolving a CombatPet's DamageHistoryInfo to
            // its owner's guid before the tuple ever reaches the core - two entries in the same call, both
            // carrying the OWNER's guid (once from their own hits, once from their pet's), must land on one
            // record with the combined total.
            var records = new Dictionary<uint, ParticipantRecord>();

            const uint ownerGuid = 2002;

            var damagers = new List<(uint guid, uint accountId, string ip, string name, float damage)>
            {
                (ownerGuid, 7, "10.0.0.3", "Bob", 40f),   // Bob's own hits
                (ownerGuid, 7, "10.0.0.3", "Bob", 60f)    // Bob's pet's hits, resolved to Bob
            };

            WorldEventParticipation.CreditDamageCore(damagers, records, T0);

            Assert.AreEqual(1, records.Count);
            Assert.AreEqual(100f, records[ownerGuid].Damage);
        }

        [TestMethod]
        public void CreditDamageCore_ZeroGuidIsIgnored()
        {
            var records = new Dictionary<uint, ParticipantRecord>();

            WorldEventParticipation.CreditDamageCore(
                new List<(uint guid, uint accountId, string ip, string name, float damage)> { (0, 0, null, null, 10f) },
                records, T0);

            Assert.AreEqual(0, records.Count);
        }

        [TestMethod]
        public void CreditDamageCore_NullInputsAreNoOps()
        {
            var records = new Dictionary<uint, ParticipantRecord>();

            WorldEventParticipation.CreditDamageCore(null, records, T0);
            Assert.AreEqual(0, records.Count);

            WorldEventParticipation.CreditDamageCore(
                new List<(uint guid, uint accountId, string ip, string name, float damage)> { (1, 0, null, null, 1f) },
                null, T0);
        }

        /// <summary>
        /// Code review finding (PR #600): DamageHistory.OnHealInternal zeroes a damager's TotalDamage in
        /// place on a heal-to-full rather than removing the entry, so an unguarded walk would credit a
        /// phantom zero-damage participant - inflating Count and letting AliveNear keep refreshing
        /// LastAliveParticipantAt, suppressing a real wipe. A non-positive damage tuple must create no
        /// record at all, not merely add zero to an existing one.
        /// </summary>
        [TestMethod]
        public void CreditDamageCore_NonPositiveDamage_CreatesNoRecord()
        {
            var records = new Dictionary<uint, ParticipantRecord>();

            var damagers = new List<(uint guid, uint accountId, string ip, string name, float damage)>
            {
                (1001, 5, "10.0.0.1", "Alice", 0f),
                (1002, 6, "10.0.0.2", "Bob", -5f)
            };

            WorldEventParticipation.CreditDamageCore(damagers, records, T0);

            Assert.AreEqual(0, records.Count, "a non-positive damage credit must not create a phantom participant");
        }

        [TestMethod]
        public void CreditDamageCore_NonPositiveDamage_DoesNotDisturbAnExistingRecord()
        {
            var records = new Dictionary<uint, ParticipantRecord>();

            WorldEventParticipation.CreditDamageCore(
                new List<(uint guid, uint accountId, string ip, string name, float damage)> { (1001, 5, "10.0.0.1", "Alice", 40f) },
                records, T0);

            WorldEventParticipation.CreditDamageCore(
                new List<(uint guid, uint accountId, string ip, string name, float damage)> { (1001, 5, "10.0.0.1", "Alice", 0f) },
                records, T0 + 10);

            Assert.AreEqual(1, records.Count);
            Assert.AreEqual(40f, records[1001].Damage, "a zero-damage credit on an existing guid must not change its total");
        }

        // ---- TopKillerCore ----------------------------------------------------------------------------

        [TestMethod]
        public void TopKillerCore_EmptyReturnsNull()
        {
            Assert.IsNull(WorldEventParticipation.TopKillerCore(new List<ParticipantRecord>()));
            Assert.IsNull(WorldEventParticipation.TopKillerCore(null));
        }

        [TestMethod]
        public void TopKillerCore_MostKillsWins()
        {
            var records = new List<ParticipantRecord>
            {
                Record(1, "Alice", kills: 2),
                Record(2, "Bob", kills: 5),
                Record(3, "Carol", kills: 3)
            };

            var top = WorldEventParticipation.TopKillerCore(records);

            Assert.AreEqual("Bob", top.Name);
        }

        [TestMethod]
        public void TopKillerCore_TieBreaksByEarliestFirstCredit()
        {
            var records = new List<ParticipantRecord>
            {
                Record(1, "Alice", kills: 3, firstCredit: T0 + 50),
                Record(2, "Bob", kills: 3, firstCredit: T0 + 10),
                Record(3, "Carol", kills: 3, firstCredit: T0 + 30)
            };

            var top = WorldEventParticipation.TopKillerCore(records);

            Assert.AreEqual("Bob", top.Name, "the earliest FirstCredit among the tied kill counts must win");
        }

        // ---- TopDamagerCore ---------------------------------------------------------------------------

        [TestMethod]
        public void TopDamagerCore_EmptyReturnsNull()
        {
            Assert.IsNull(WorldEventParticipation.TopDamagerCore(new List<ParticipantRecord>()));
            Assert.IsNull(WorldEventParticipation.TopDamagerCore(null));
        }

        [TestMethod]
        public void TopDamagerCore_MostDamageWins()
        {
            var records = new List<ParticipantRecord>
            {
                Record(1, "Alice", damage: 120f),
                Record(2, "Bob", damage: 340f),
                Record(3, "Carol", damage: 200f)
            };

            var top = WorldEventParticipation.TopDamagerCore(records);

            Assert.AreEqual("Bob", top.Name);
        }

        // ---- Count, via the pure core -------------------------------------------------------------------

        [TestMethod]
        public void CreditDamageCore_CountReflectsDistinctGuidsOnly()
        {
            var records = new Dictionary<uint, ParticipantRecord>();

            var damagers = new List<(uint guid, uint accountId, string ip, string name, float damage)>
            {
                (1, 0, null, "Alice", 10f),
                (2, 0, null, "Bob", 10f),
                (1, 0, null, "Alice", 5f)
            };

            WorldEventParticipation.CreditDamageCore(damagers, records, T0);

            Assert.AreEqual(2, records.Count);
        }

        // ---- CreditKillCore ---------------------------------------------------------------------------

        [TestMethod]
        public void CreditKillCore_FirstKill_CapturesMetadataAndSetsKillsToOne()
        {
            var records = new Dictionary<uint, ParticipantRecord>();

            WorldEventParticipation.CreditKillCore(3003, 9, "10.0.0.4", "Dave", records, T0);

            Assert.AreEqual(1, records.Count);

            var rec = records[3003];

            Assert.AreEqual(3003u, rec.CharacterGuid);
            Assert.AreEqual(9u, rec.AccountId);
            Assert.AreEqual("10.0.0.4", rec.Ip);
            Assert.AreEqual("Dave", rec.Name);
            Assert.AreEqual(1, rec.Kills);
            Assert.AreEqual(T0, rec.FirstCredit);
            Assert.AreEqual(0f, rec.Damage, "a kill credit alone must not fabricate a damage total");
        }

        [TestMethod]
        public void CreditKillCore_RepeatKill_IncrementsWithoutMovingFirstCredit()
        {
            var records = new Dictionary<uint, ParticipantRecord>();

            WorldEventParticipation.CreditKillCore(3003, 9, "10.0.0.4", "Dave", records, T0);
            WorldEventParticipation.CreditKillCore(3003, 999, "10.0.0.9", "DaveRenamed", records, T0 + 100);

            Assert.AreEqual(1, records.Count);

            var rec = records[3003];

            Assert.AreEqual(2, rec.Kills);
            Assert.AreEqual(T0, rec.FirstCredit, "a repeat kill must not move FirstCredit");
            Assert.AreEqual(9u, rec.AccountId, "a repeat kill must not overwrite identity fields captured on the first credit");
            Assert.AreEqual("Dave", rec.Name);
        }

        [TestMethod]
        public void CreditKillCore_ZeroGuidIsIgnored()
        {
            var records = new Dictionary<uint, ParticipantRecord>();

            WorldEventParticipation.CreditKillCore(0, 0, null, null, records, T0);

            Assert.AreEqual(0, records.Count);
        }

        [TestMethod]
        public void CreditKillCore_NullRecordsIsANoOp()
        {
            // Must not throw.
            WorldEventParticipation.CreditKillCore(1, 0, null, "Alice", null, T0);
        }

        [TestMethod]
        public void CreditKillCore_SharesARecordWithCreditDamageCore()
        {
            // A player credited for damage first, then a killing blow, must land on the SAME record -
            // matching WorldEventParticipation.CreditKill's doc comment ("a player credited for the first
            // time here... gets a fresh record, same as CreditDamage would create").
            var records = new Dictionary<uint, ParticipantRecord>();

            WorldEventParticipation.CreditDamageCore(
                new List<(uint guid, uint accountId, string ip, string name, float damage)> { (3003, 9, "10.0.0.4", "Dave", 50f) },
                records, T0);

            WorldEventParticipation.CreditKillCore(3003, 9, "10.0.0.4", "Dave", records, T0 + 5);

            Assert.AreEqual(1, records.Count);

            var rec = records[3003];

            Assert.AreEqual(50f, rec.Damage);
            Assert.AreEqual(1, rec.Kills);
            Assert.AreEqual(T0, rec.FirstCredit, "the damage credit came first, so FirstCredit must stay at the earlier time");
        }

        // ---- instance readers on an empty ledger ------------------------------------------------------
        //
        // These are the only INSTANCE members a unit test can reach: CreditDamage/CreditKill need a live
        // Creature/DamageHistoryInfo, so a test can never populate a real ledger (D6). What it can pin is
        // the empty case, and for TryGetRecord that case is not a corner - it IS the coal branch of the
        // 2026-09-07 participation payout. WorldEventCacheHandler asks the ledger "does this character have
        // a record", and "no" is what routes the claimant to the booby prize, so a TryGetRecord that threw
        // or reported true on an empty ledger would mis-pay every claim on a run nobody fought.

        [TestMethod]
        public void TryGetRecord_EmptyLedger_ReportsNoCreditAndDoesNotThrow()
        {
            var ledger = new WorldEventParticipation(() => T0);

            Assert.IsFalse(ledger.TryGetRecord(1001, out var rec), "an empty ledger credits nobody");
            Assert.IsNull(rec);
            Assert.AreEqual(0, ledger.Count);
            Assert.IsNull(ledger.TopKiller());
            Assert.IsNull(ledger.TopDamager());
        }

        [TestMethod]
        public void TryGetRecord_EmptyLedger_GuidZeroIsAlsoAMiss()
        {
            // Guid 0 is what a caller with no resolved character would pass. It must read as "no credit"
            // like any other absent guid rather than matching a sentinel record.
            var ledger = new WorldEventParticipation(() => T0);

            Assert.IsFalse(ledger.TryGetRecord(0, out var rec));
            Assert.IsNull(rec);
        }

        [TestMethod]
        public void AliveNear_NullAnchor_IsZero()
        {
            // Pins the tolerance the class comment promises: a directly constructed run has no landblock
            // bridge and therefore no anchor, and AliveNear must return 0 rather than dereference it. This
            // is also the one instance reader that reaches outside the ledger (PlayerManager), so the null
            // anchor has to short-circuit BEFORE that call - which is what keeps this test host-safe.
            var ledger = new WorldEventParticipation(() => T0);

            Assert.AreEqual(0, ledger.AliveNear(null, 10f));
        }

        // ---- HasCreditOrUnknown (the 2026-09-07 payout gate's credit question) ------------------------

        [TestMethod]
        public void HasCreditOrUnknown_NullLedger_ReportsCredit_SoThePayoutFailsOpenToTheCrate()
        {
            // The whole point of the null case: an absent ledger must NEVER route a player to the booby
            // prize. True here means PaysCoal sees hasCredit and pays the ordinary crate.
            Assert.IsTrue(WorldEventParticipation.HasCreditOrUnknown(null, 0x50000001));
        }

        [TestMethod]
        public void HasCreditOrUnknown_EmptyLedger_ReportsNoCredit()
        {
            // The coal branch proper: a real ledger that simply has no record for this character.
            Assert.IsFalse(WorldEventParticipation.HasCreditOrUnknown(new WorldEventParticipation(), 0x50000001));
        }
    }
}
