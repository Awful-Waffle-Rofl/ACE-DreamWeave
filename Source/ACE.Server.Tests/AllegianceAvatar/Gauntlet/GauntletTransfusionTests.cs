using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Avatars.Gauntlet;
using ACE.Server.ClassAbilities;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Hold;
using ACE.Server.WorldObjects;

using static ACE.Server.Tests.Avatars.Gauntlet.GauntletFighterFixtures;

namespace ACE.Server.Tests.Avatars.Gauntlet
{
    /// <summary>
    /// Blood Mage Transfusion feeding the Allegiance Avatar's fighter (a Gauntlet run's or a private event's: both are a
    /// GauntletAvatarFighter with an owner). The recipient selection (live copy, alive, landblock, reach), the demand bounded by
    /// the shared kit bucket, the shared bucket itself, the eligibility flag, and source pins on the primary-strike-only scan and
    /// the payout branch. No PropertyManager key is read.
    /// </summary>
    [TestClass]
    public class GauntletTransfusionTests
    {
        [ClassInitialize]
        public static void Init(TestContext _) => EnsureInitialized();

        [TestInitialize]
        public void ResetCount() => GauntletAvatarFighter.ResetOwnedCountForTests();

        [TestCleanup]
        public void ResetCountAfter() => GauntletAvatarFighter.ResetOwnedCountForTests();

        private sealed class Owner : IGauntletFighterOwner
        {
            public bool Live = true;
            public bool IsLiveCopy => Live;
            public void OnFighterDied(GauntletAvatarFighter fighter) { }
        }

        private static GauntletAvatarFighter LiveFighter(uint max, uint current, Owner owner = null)
        {
            var fighter = Fighter(health: max);
            fighter.Owner = owner ?? new Owner();
            fighter.ApplyStamp(new GauntletFighterStamp { Level = 200, MaxHealth = max, MinDeathSeconds = 20, BurstSeconds = 5 }, fullHealth: true);
            fighter.Health.Current = current;
            return fighter;
        }

        private static List<Creature> Select(IEnumerable<Creature> candidates, Func<Creature, double?> distance)
            => GauntletAvatarFighter.SelectTransfusionFighters(candidates, distance);

        // ---- selection ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void LiveFighterInReach_IsARecipient()
        {
            var fighter = LiveFighter(1000, 500);

            var found = Select(new Creature[] { fighter }, _ => 5.0);

            Assert.IsNotNull(found);
            Assert.AreSame(fighter, found.Single());
        }

        [TestMethod]
        public void DeadNotLiveCopyUnownedOrFar_AreNotRecipients()
        {
            var dead = LiveFighter(1000, 500);
            dead.Health.Current = 0;
            Assert.IsTrue(dead.IsDead, "fixture: zero health reads as dead");

            var owner = new Owner();
            var ended = LiveFighter(1000, 500, owner);
            owner.Live = false;

            var unowned = Fighter();
            var far = LiveFighter(1000, 500);

            Assert.IsNull(Select(new Creature[] { dead }, _ => 5.0), "a dead fighter");
            Assert.IsNull(Select(new Creature[] { ended }, _ => 5.0), "a copy that left its live states");
            Assert.IsNull(Select(new Creature[] { unowned }, _ => 5.0), "no run, no event");
            Assert.IsNull(Select(new Creature[] { far }, _ => WorldObject.DrainSurplusRange + 0.5), "out of reach");
            Assert.IsNotNull(Select(new Creature[] { far }, _ => WorldObject.DrainSurplusRange), "the reach is inclusive");
        }

        [TestMethod]
        public void OtherLandblock_IsNeverAskedAboutItsState()
        {
            var fighter = LiveFighter(1000, 500);
            var asked = 0;

            // the distance function answers null for another landblock; the fighter's own state must not be read after that
            var found = Select(new Creature[] { fighter }, _ => { asked++; return null; });

            Assert.IsNull(found);
            Assert.AreEqual(1, asked);
        }

        [TestMethod]
        public void NonFightersAreSkipped_AndAFighterIsNeverListedTwice()
        {
            var fighter = LiveFighter(1000, 500);

            var found = Select(new Creature[] { Monster(), fighter, fighter }, _ => 3.0);

            Assert.AreEqual(1, found.Count);
            Assert.AreSame(fighter, found[0]);
            Assert.IsNull(Select(new Creature[] { Monster() }, _ => 3.0));
        }

        [TestMethod]
        public void Qualifies_IsAPureAcceptanceAndReachRule()
        {
            Assert.IsTrue(GauntletAvatarFighter.TransfusionFighterQualifies(true, 9.9, 10));
            Assert.IsFalse(GauntletAvatarFighter.TransfusionFighterQualifies(false, 1, 10));
            Assert.IsFalse(GauntletAvatarFighter.TransfusionFighterQualifies(true, null, 10));
            Assert.IsFalse(GauntletAvatarFighter.TransfusionFighterQualifies(true, 10.1, 10));
        }

        [TestMethod]
        public void Gate_OwnedCount_FollowsTheOwnerAndReleasesOnDeath()
        {
            var fighter = Fighter();
            Assert.AreEqual(0, GauntletAvatarFighter.OwnedCount);
            Assert.IsFalse(GauntletAvatarFighter.AnyOwned);

            fighter.Owner = new Owner();
            fighter.Owner = new Owner();
            Assert.AreEqual(1, GauntletAvatarFighter.OwnedCount, "set twice counts once");
            Assert.IsTrue(GauntletAvatarFighter.AnyOwned);

            fighter.Owner = null;
            Assert.AreEqual(0, GauntletAvatarFighter.OwnedCount, "cleared releases");

            var unowned = Fighter();
            unowned.Owner = null;
            Assert.AreEqual(0, GauntletAvatarFighter.OwnedCount, "null on a null fighter changes nothing");

            var dying = Fighter();
            dying.Owner = new Owner();
            Assert.AreEqual(1, GauntletAvatarFighter.OwnedCount);
            dying.ReportDeathOnce();
            Assert.AreEqual(0, GauntletAvatarFighter.OwnedCount, "a death releases the fighter");
            Assert.IsFalse(GauntletAvatarFighter.AnyOwned, "the gate closes once the last fighter has died");
            Assert.IsNotNull(dying.Owner, "Owner itself is left for the paths that read it");

            dying.Owner = null;
            Assert.AreEqual(0, GauntletAvatarFighter.OwnedCount, "clearing after the death does not go negative");
        }

        // ---- demand and the shared bucket --------------------------------------------------------------------------

        [TestMethod]
        public void KitRoom_IsTheSmallerOfMissingHealthAndTheBucket()
        {
            // 25% of 1000 = 250 tokens, full when never used (lastAt 0)
            Assert.AreEqual(250, GauntletAvatarFighter.KitRoom(1000, 100, 0.25, 0, 0, 1000.0), "bucket binds");
            Assert.AreEqual(30, GauntletAvatarFighter.KitRoom(1000, 970, 0.25, 0, 0, 1000.0), "missing health binds");
            Assert.AreEqual(0, GauntletAvatarFighter.KitRoom(1000, 1000, 0.25, 0, 0, 1000.0), "full health");
            Assert.AreEqual(0, GauntletAvatarFighter.KitRoom(1000, 500, 0, 0, 0, 1000.0), "a share of 0 refuses");
            Assert.AreEqual(0, GauntletAvatarFighter.KitRoom(0, 0, 0.25, 0, 0, 1000.0), "no max");
            Assert.AreEqual(0, GauntletAvatarFighter.KitRoom(1000, 500, 0.25, 0, 990.0, 990.0), "an empty bucket just drained");
            Assert.AreEqual(125, GauntletAvatarFighter.KitRoom(1000, 100, 0.25, 0, 990.0, 995.0), "half a window of refill");
        }

        [TestMethod]
        public void Demand_IsBoundedByTheBucket_AndNeverExceedsWhatTheHealWouldRestore()
        {
            // missing 900, bucket 250: room 250 health = 250 / (1000 * 0.04) * 100 = 625 raw, capped at the 250-point ceiling
            var fighter = LiveFighter(1000, 100);
            Assert.AreEqual(250u, fighter.TransfusionDemand(1000.0), "a fresh bucket and a big hole bid the ceiling");

            // the bucket nearly spent by a kit: only the sliver left may be asked for
            var spent = LiveFighter(1000, 100);
            Assert.AreEqual(250, spent.ApplyKitHeal(10_000, 1000.0, out _), "fixture: a huge kit drains the bucket");
            Assert.AreEqual(0u, spent.TransfusionDemand(1000.0), "no room, no bid: the surplus goes to others");

            // a small hole: demand converts back to at most the missing health
            var small = LiveFighter(1000, 980);
            var demand = small.TransfusionDemand(1000.0);
            Assert.AreEqual(50u, demand, "20 missing health = 50 raw points");
            Assert.AreEqual(20, small.ApplyKitHeal(demand, 1000.0, out _), "and the bid heals exactly that");
        }

        [TestMethod]
        public void Demand_IsZero_ForAFighterThatRefusesHeals()
        {
            var owner = new Owner();
            var fighter = LiveFighter(1000, 100, owner);
            owner.Live = false;
            Assert.AreEqual(0u, fighter.TransfusionDemand(1000.0));

            Assert.AreEqual(0u, LiveFighter(1000, 1000).TransfusionDemand(1000.0), "full health");
            Assert.AreEqual(0u, Fighter().TransfusionDemand(1000.0), "no run");
        }

        [TestMethod]
        public void Share_TakesTheKitBucket_AndCreditsTheLedger()
        {
            var fighter = LiveFighter(1000, 100);
            fighter.Ledger = new GauntletFighterLedger();

            try
            {
                GauntletAvatarFighter.QueueHeal = (f, raw, reply) =>
                {
                    var healed = f.ApplyKitHeal(raw, 1000.0, out var outcome);
                    reply(healed, outcome);
                    return true;
                };

                Assert.IsTrue(GauntletAvatarFighter.TryQueueTransfusionShare(null, fighter, 100), "a Transfusion share is queued as a heal");
                Assert.AreEqual(140u, fighter.Health.Current, "converted like a kit: 100 raw = 4% of max");

                // the bucket is shared: a share that takes the rest leaves a kit nothing
                Assert.IsTrue(GauntletAvatarFighter.TryQueueTransfusionShare(null, fighter, 1_000_000));
                Assert.AreEqual(350u, fighter.Health.Current, "the share is held by the ten-second bucket");
                Assert.AreEqual(0, fighter.ApplyKitHeal(WorldEventHooks.TypicalPlayerHeal, 1000.0, out var kit), "a kit after the share finds less room");
                Assert.AreEqual(HoldHealOutcome.BudgetSpent, kit);

                Assert.AreEqual(250L, fighter.Ledger.Take().Healed, "both shares are credited to the run's heal tally");
            }
            finally
            {
                GauntletAvatarFighter.QueueHeal = (f, raw, reply) => f.QueueKitHeal(raw, reply);
            }
        }

        [TestMethod]
        public void AKitBeforeAShare_LeavesTheShareLessRoom()
        {
            var fighter = LiveFighter(1000, 100);
            var fresh = fighter.TransfusionDemand(1000.0);

            Assert.IsTrue(fighter.ApplyKitHeal(WorldEventHooks.TypicalPlayerHeal * 3, 1000.0, out _) > 0);

            Assert.IsTrue(fighter.TransfusionDemand(1000.0) <= fresh, "the same bucket");
            Assert.AreEqual(130, GauntletAvatarFighter.KitRoom(1000, fighter.Health.Current, 0.25, 130, 1000.0, 1000.0), "250 tokens less the 120 a kit took");
        }

        // ---- eligibility -------------------------------------------------------------------------------------------

        [TestMethod]
        public void SoloBloodMage_WithOnlyAFighterInReach_Qualifies()
        {
            Assert.IsFalse(DrainSurplusEligibility.CasterQualifies(true, true, true, 1, false, false), "default: unchanged");
            Assert.IsTrue(DrainSurplusEligibility.CasterQualifies(true, true, true, 1, false, false, hasAvatarFighter: true));
            Assert.IsFalse(DrainSurplusEligibility.CasterQualifies(true, true, true, 0, false, false, hasAvatarFighter: true), "unlearned");
            Assert.IsFalse(DrainSurplusEligibility.CasterQualifies(false, true, true, 1, false, false, hasAvatarFighter: true), "monster caster");
            Assert.IsFalse(DrainSurplusEligibility.CasterQualifies(true, true, false, 1, false, false, hasAvatarFighter: true), "PvP victim");
            Assert.IsFalse(DrainSurplusEligibility.CasterQualifies(true, false, true, 1, false, false, hasAvatarFighter: true), "not the destination");
        }

        // ---- source pins -------------------------------------------------------------------------------------------

        private static string MagicSource()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Source", "ACE.Server", "WorldObjects", "WorldObject_Magic.cs")))
                dir = dir.Parent;

            if (dir == null)
                Assert.Inconclusive("Could not find WorldObject_Magic.cs by walking up from the test assembly.");

            return File.ReadAllText(Path.Combine(dir.FullName, "Source", "ACE.Server", "WorldObjects", "WorldObject_Magic.cs")).Replace("\r\n", "\n");
        }

        [TestMethod]
        public void FighterScan_IsPrimaryStrikeOnly_AfterTheScalarGates()
        {
            var magic = MagicSource();

            Assert.AreEqual(1, Count(magic, "var avatarFighters = !isSecondaryStrike && caster != null && destination == caster\n"
                + "                && targetCreature != null && targetCreature is not Player && transfusionRank >= 1\n"
                + "                ? ACE.Server.Avatars.Gauntlet.GauntletHooks.TransfusionFighters(caster) : null;"),
                "primary strike only (Crimson Harvest), the same gates as the envoy scan");
            Assert.AreEqual(1, Count(magic, "hasAvatarFighter: avatarFighters != null))"), "the flag reaches CasterQualifies");
        }

        [TestMethod]
        public void FighterPayout_QueuesTheKitHeal_BeforeAnyDirectWrite()
        {
            var magic = MagicSource();
            var at = magic.IndexOf("if (ACE.Server.Avatars.Gauntlet.GauntletHooks.IsAvatarFighter(fellow))", StringComparison.Ordinal);

            Assert.IsTrue(at > 0, "the fighter payout branch exists");

            var queueAt = magic.IndexOf("TryQueueTransfusionShare(this as Player, fellow, fellowShares[i]);", at, StringComparison.Ordinal);
            var continueAt = magic.IndexOf("continue;", queueAt, StringComparison.Ordinal);
            var write = magic.IndexOf("fellow.UpdateVitalDelta(", at, StringComparison.Ordinal);
            var pvp = magic.IndexOf("PvpRules.ApplyHealingModToRecipient(", at, StringComparison.Ordinal);

            Assert.IsTrue(queueAt > at && continueAt > queueAt, "the branch queues, then continues");
            Assert.IsTrue(continueAt < write && continueAt < pvp, "the branch ends before the first direct write and the PvP healing mod");
        }

        private static int Count(string haystack, string needle)
        {
            var n = 0;

            for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
                n++;

            return n;
        }
    }
}
