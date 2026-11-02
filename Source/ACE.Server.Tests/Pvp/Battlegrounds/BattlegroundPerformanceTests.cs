using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Pvp;
using ACE.Server.Pvp.Battlegrounds;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Pvp.Battlegrounds
{
    /// <summary>
    /// Docs/Pvp/BATTLEGROUNDS.md "Performance stats": the pure ledger and <see cref="CrystalProximity"/>, the damage hook
    /// (<see cref="Player.OnPvpDamageRecorded"/>) on seeded players and a real test crystal driven through Creature.TakeDamage (and so
    /// through DamageHistory.Add), the King of the Hill hill-time credit, and a source pin on the DamageHistory.Add call. The coordinator
    /// wiring (killshots, the Kills fix, the crystal frame, the Resolve log lines) is driven in BattlegroundCoordinatorTests and
    /// AttackDefendCoordinatorTests.
    /// </summary>
    [TestClass]
    public class BattlegroundPerformanceTests
    {
        private const uint Instance = 0x0001_0400;

        private const uint Landblock = 0x003C;

        private const int Attackers = CrystalWinCondition.AttackerTeam;

        private const int Defenders = CrystalWinCondition.DefenderTeam;

        private const uint AttackerId = 0x50000101, DefenderId = 0x50000102, Attacker2Id = 0x50000103, Defender2Id = 0x50000104;

        private static readonly DateTime Now = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);

        /// <summary>Two crystals: Great Hall at (110, -70, -6) and West Cavern at (56, -57, -30), as on bg_003c.</summary>
        private static readonly IReadOnlyList<PlannedCrystal> Crystals = new List<PlannedCrystal>
        {
            new PlannedCrystal(0, new BattlegroundCrystalSite("Great Hall", 0x01EE, 110f, -70f, -6f), 1000, "east"),
            new PlannedCrystal(1, new BattlegroundCrystalSite("West Cavern", 0x0161, 56f, -57f, -30f), 1000, "west"),
        };

        private static Position At(float x, float y, float z = -6f, uint instance = Instance) => new Position((Landblock << 16) | 0x01EE, x, y, z, 0f, 0f, 0f, 1f, instance);

        private static readonly Position NearHall = At(110f, -72f);

        private static readonly Position FarFromHall = At(60f, -120f);

        private Func<DateTime> savedClock;

        private Func<Creature, IEnumerable<DefenderSample>> savedSamples;

        [TestInitialize]
        public void Setup()
        {
            savedClock = PvpArenaHookSettings.UtcNow;
            savedSamples = Creature.DefenderSampleSource;
            PvpArenaHookSettings.UtcNow = () => Now;
            Creature.DefenderSampleSource = _ => Array.Empty<DefenderSample>();
        }

        [TestCleanup]
        public void Cleanup()
        {
            PvpArenaHookSettings.UtcNow = savedClock;
            Creature.DefenderSampleSource = savedSamples;
        }

        // ================= fixtures =================

        private static PvpMatch NewMatch(bool withLedger = true, bool sequential = true, bool bindCrystals = true)
        {
            var teams = new List<PvpTeam>
            {
                new PvpTeam(Attackers, new List<PvpParticipant> { new PvpParticipant(AttackerId, 1500), new PvpParticipant(Attacker2Id, 1500) }),
                new PvpTeam(Defenders, new List<PvpParticipant> { new PvpParticipant(DefenderId, 1500), new PvpParticipant(Defender2Id, 1500) }),
            };

            var match = new PvpMatch(Guid.NewGuid(), BattlegroundModes.AttackDefendModeKey, teams, Now);

            if (withLedger)
            {
                match.Performance = BattlegroundPerformanceLedger.ForTeams(teams);

                if (bindCrystals)
                    match.Performance.BindCrystals(new CrystalProximityFrame(Crystals, sequential ? new CrystalSequence(Crystals.Select(c => c.Site.Name)) : null, Landblock, Instance, 25.0));
            }

            return match;
        }

        private static PvpPlayerBinding Bound(PvpMatch match, int team, PvpMatchState state = PvpMatchState.Live)
            => new PvpPlayerBinding(match, team, state, false, false, false, false, false);

        private static T Seeded<T>() where T : WorldObject
        {
            var wo = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
            SetInherited(wo, "<Biota>k__BackingField", new ACE.Entity.Models.Biota());
            SetInherited(wo, "BiotaDatabaseLock", new ReaderWriterLockSlim());
            return wo;
        }

        private static void SetInherited(WorldObject wo, string name, object value)
        {
            var field = typeof(WorldObject).GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(field, $"WorldObject.{name} was not found by reflection - has it been renamed?");
            field.SetValue(wo, value);
        }

        /// <summary>A seeded player with a guid, a binding and (for a victim) a Location.</summary>
        private static Player SeededPlayer(uint id, PvpPlayerBinding binding, Position at = null)
        {
            var p = Seeded<Player>();
            SetInherited(p, "<Guid>k__BackingField", new ObjectGuid(id));
            p.SetPvpBindingForTests(binding);

            foreach (var name in new[] { "positionCache", "ephemeralPositions" })
            {
                var store = typeof(WorldObject).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.IsNotNull(store, $"WorldObject.{name} was not found by reflection - has it been renamed?");
                store.SetValue(p, Activator.CreateInstance(store.FieldType));
            }

            p.Biota.PropertiesPosition = new Dictionary<ACE.Entity.Enum.Properties.PositionType, ACE.Entity.Models.PropertiesPosition>();

            if (at != null)
                p.Location = new Position(at);

            return p;
        }

        private static SeatPerformanceSnapshot Stats(PvpMatch match, uint id) => match.Performance.Snapshot().Single(s => s.CharacterId == id);

        // ================= CrystalProximity (the one "near" rule) =================

        [TestMethod]
        public void Proximity_Sequential_OnlyTheVulnerableCrystalCounts()
        {
            var sequence = new CrystalSequence(Crystals.Select(c => c.Site.Name));
            var nearCavern = At(56f, -58f, -30f);

            Assert.AreEqual(0, CrystalProximity.VulnerableIndexNear(Crystals, sequence, Landblock, Instance, 25.0, NearHall));
            Assert.AreEqual(-1, CrystalProximity.VulnerableIndexNear(Crystals, sequence, Landblock, Instance, 25.0, nearCavern), "the sealed West Cavern is not vulnerable yet");

            sequence.MarkDestroyed(0);

            Assert.AreEqual(-1, CrystalProximity.VulnerableIndexNear(Crystals, sequence, Landblock, Instance, 25.0, NearHall), "the fallen Great Hall is no longer vulnerable");
            Assert.AreEqual(1, CrystalProximity.VulnerableIndexNear(Crystals, sequence, Landblock, Instance, 25.0, nearCavern));
        }

        [TestMethod]
        public void Proximity_AnyOrder_NearestStanding_SkipsFallen()
        {
            Assert.AreEqual(0, CrystalProximity.VulnerableIndexNear(Crystals, null, Landblock, Instance, 25.0, NearHall));
            Assert.AreEqual(-1, CrystalProximity.VulnerableIndexNear(Crystals, null, Landblock, Instance, 25.0, NearHall, i => i != 0), "a fallen crystal is skipped");
        }

        [TestMethod]
        public void Proximity_RangeIsInclusive_AndBadInputsAreNeverNear()
        {
            Assert.IsTrue(CrystalProximity.IsNearVulnerable(Crystals, null, Landblock, Instance, 2.0, NearHall), "exactly 2 m counts");
            Assert.IsFalse(CrystalProximity.IsNearVulnerable(Crystals, null, Landblock, Instance, 1.99, NearHall));

            foreach (var range in new[] { 0.0, -1.0, double.NaN, double.PositiveInfinity })
                Assert.IsFalse(CrystalProximity.IsNearVulnerable(Crystals, null, Landblock, Instance, range, NearHall), $"range {range}");

            Assert.IsFalse(CrystalProximity.IsNearVulnerable(Crystals, null, Landblock, Instance, 25.0, null), "no position");
            Assert.IsFalse(CrystalProximity.IsNearVulnerable(Crystals, null, Landblock, Instance, 25.0, At(110f, -72f, instance: Instance + 1)), "another instance");
            Assert.IsFalse(CrystalProximity.IsNearVulnerable(new List<PlannedCrystal>(), null, Landblock, Instance, 25.0, NearHall), "no crystals");
            Assert.IsFalse(CrystalProximity.IsNearVulnerable(null, null, Landblock, Instance, 25.0, NearHall), "no crystals");
        }

        // ================= the ledger =================

        [TestMethod]
        public void Ledger_MembershipIsFixedAtFormation_StrangersAreIgnored()
        {
            var match = NewMatch();
            var perf = match.Performance;

            perf.CreditPlayerDamage(0x5FFFFFFF, 50, NearHall);
            perf.CreditCrystalDamage(0x5FFFFFFF, 50);
            perf.CreditKillshot(0x5FFFFFFF, true, false);
            perf.CreditHillSeconds(0x5FFFFFFF, 5);

            Assert.AreEqual(4, perf.Snapshot().Count, "the four seats, nobody added");
            Assert.IsTrue(perf.Snapshot().All(s => s.PlayerDamage == 0 && s.CrystalDamage == 0 && s.Killshots == 0 && s.HillSeconds == 0));
            Assert.IsNull(perf.Seat(0x5FFFFFFF));
            Assert.AreEqual(Defenders, perf.Seat(DefenderId).TeamIndex);
        }

        [TestMethod]
        public void Ledger_KillshotsSplitNearFarAndHill()
        {
            var perf = NewMatch().Performance;

            perf.CreditKillshot(AttackerId, true, false);
            perf.CreditKillshot(AttackerId, false, false);
            perf.CreditKillshot(AttackerId, null, true);
            perf.CreditKillshot(AttackerId, null, false);

            var s = perf.Seat(AttackerId).Snapshot();
            Assert.AreEqual(4, s.Killshots);
            Assert.AreEqual(1, s.KillshotsNearCrystal);
            Assert.AreEqual(1, s.KillshotsFarCrystal);
            Assert.AreEqual(1, s.KillshotsNearHill);
        }

        [TestMethod]
        public void Ledger_DestroyedMask_MovesNearWithTheSequence()
        {
            var perf = NewMatch(sequential: false).Performance;

            Assert.AreEqual(true, perf.IsNearVulnerableCrystal(NearHall));

            perf.MarkCrystalDestroyed(0);

            Assert.IsFalse(perf.IsCrystalStanding(0));
            Assert.IsTrue(perf.IsCrystalStanding(1));
            Assert.AreEqual(false, perf.IsNearVulnerableCrystal(NearHall), "a fallen crystal is never near");
            Assert.IsNull(NewMatch(bindCrystals: false).Performance.IsNearVulnerableCrystal(NearHall), "no frame (King of the Hill): neither near nor far");
        }

        /// <summary>
        /// The prebuilt-sites overload is the same rule as the shared one the kill chip uses: over a grid of points around both crystals,
        /// in sequential and any-order play, with and without a fallen crystal, both answer the same index.
        /// </summary>
        [TestMethod]
        public void Proximity_PrebuiltSites_AgreeWithTheSharedRule()
        {
            var sites = CrystalProximity.SitePositions(Crystals, Landblock, Instance);
            var checkedPoints = 0;

            foreach (var sequential in new[] { true, false })
            {
                foreach (Func<int, bool> standing in new Func<int, bool>[] { null, i => i != 0, i => i != 1 })
                {
                    var sequence = sequential ? new CrystalSequence(Crystals.Select(c => c.Site.Name)) : null;

                    for (var x = 40f; x <= 130f; x += 7.5f)
                        for (var y = -130f; y <= -40f; y += 7.5f)
                            foreach (var z in new[] { -30f, -6f })
                            {
                                var p = At(x, y, z);
                                Assert.AreEqual(
                                    CrystalProximity.VulnerableIndexNear(Crystals, sequence, Landblock, Instance, 25.0, p, standing),
                                    CrystalProximity.VulnerableIndexNear(Crystals, sites, sequence, Instance, 25.0, p, standing),
                                    $"({x}, {y}, {z}) sequential {sequential}");
                                checkedPoints++;
                            }
                }
            }

            Assert.IsTrue(checkedPoints > 1000, "fixture: a real grid was compared");
            Assert.AreEqual(-1, CrystalProximity.VulnerableIndexNear(Crystals, new Position[1], null, Instance, 25.0, NearHall), "mismatched lengths are never near");
        }

        /// <summary>
        /// The per-hit check allocates nothing once warm: no Position per crystal, no enumerator, no delegate per call (the destroyed-set
        /// delegate is created once in the ledger's constructor). Measured on this thread over 1000 calls, both play orders.
        /// </summary>
        [TestMethod]
        public void Ledger_IsNearVulnerableCrystal_AllocatesNothing()
        {
            foreach (var sequential in new[] { true, false })
            {
                var perf = NewMatch(sequential: sequential).Performance;
                perf.MarkCrystalDestroyed(1);

                for (var i = 0; i < 100; i++)
                {
                    perf.IsNearVulnerableCrystal(NearHall);
                    perf.IsNearVulnerableCrystal(FarFromHall);
                }

                var before = GC.GetAllocatedBytesForCurrentThread();

                for (var i = 0; i < 500; i++)
                {
                    perf.IsNearVulnerableCrystal(NearHall);
                    perf.IsNearVulnerableCrystal(FarFromHall);
                }

                var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

                Assert.AreEqual(0L, allocated, $"1000 calls allocated {allocated} bytes (sequential {sequential})");
                Assert.AreEqual(true, perf.IsNearVulnerableCrystal(NearHall), "fixture: the calls did real work");
            }
        }

        [TestMethod]
        public void Ledger_BindCrystals_IsOnce()
        {
            var perf = NewMatch().Performance;
            var first = perf.Crystals;

            perf.BindCrystals(new CrystalProximityFrame(Crystals, null, Landblock, Instance + 7, 1.0));

            Assert.AreSame(first, perf.Crystals);
        }

        [TestMethod]
        public void IsNearHill_WidensTheRadius_KeepsTheHeight_AndSanitizesTheMargin()
        {
            var zone = new KothZone(55, -35, 0, Radius: 6, Height: 3);

            Assert.IsTrue(BattlegroundPerformanceLedger.IsNearHill(zone, 6.0, 55 + 11.9, -35, 0), "inside radius + margin");
            Assert.IsFalse(BattlegroundPerformanceLedger.IsNearHill(zone, 6.0, 55 + 12.1, -35, 0), "outside radius + margin");
            Assert.IsFalse(BattlegroundPerformanceLedger.IsNearHill(zone, 6.0, 55, -35, 3.5), "the height tolerance is the zone's own");

            foreach (var bad in new[] { -2.0, double.NaN, double.PositiveInfinity })
            {
                Assert.IsTrue(BattlegroundPerformanceLedger.IsNearHill(zone, bad, 55 + 5.9, -35, 0), $"margin {bad} counts as 0: inside the zone");
                Assert.IsFalse(BattlegroundPerformanceLedger.IsNearHill(zone, bad, 55 + 6.1, -35, 0), $"margin {bad} counts as 0: outside it");
            }
        }

        [TestMethod]
        public void FormatLogLine_IsTheDocumentedShape()
        {
            var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
            var s = new SeatPerformanceSnapshot(0x50000011, 1, 1234, 560, 300, 40, 3, 2, 1, 0, 45, 870, 61);

            Assert.AreEqual(
                "[PVP] match 11111111-2222-3333-4444-555555555555 perf: Alpha (0x50000011) team 1: player_dmg 1234, crystal_dmg 560, def_dmg_near 300, def_dmg_far 40, kills 3, kills_near_crystal 2, kills_far_crystal 1, kills_near_hill 0, hill_s 45, healing 870, engaged_s 61",
                BattlegroundPerformanceLedger.FormatLogLine(id, "Alpha", s));
        }

        // ================= the damage hook: players =================

        [TestMethod]
        public void Hook_OpposingPlayersOfOneLiveMatch_CreditTheAttacker()
        {
            var match = NewMatch();
            var attacker = SeededPlayer(AttackerId, Bound(match, Attackers));
            var defender = SeededPlayer(DefenderId, Bound(match, Defenders), FarFromHall);

            Player.OnPvpDamageRecorded(defender, attacker, 37);

            Assert.AreEqual(37, Stats(match, AttackerId).PlayerDamage);
            Assert.AreEqual(0, Stats(match, DefenderId).PlayerDamage, "the victim is not credited");
            Assert.AreEqual(0, Stats(match, AttackerId).DefenderDamageNear + Stats(match, AttackerId).DefenderDamageFar, "an attacker's damage is never split as defender damage");
        }

        [TestMethod]
        public void Hook_SameTeam_OtherMatch_Self_NonPlayer_NotLive_CreditNothing()
        {
            var match = NewMatch();
            var other = NewMatch();

            var a = SeededPlayer(AttackerId, Bound(match, Attackers));
            var a2 = SeededPlayer(Attacker2Id, Bound(match, Attackers), NearHall);
            Player.OnPvpDamageRecorded(a2, a, 10);
            Assert.AreEqual(0, Stats(match, AttackerId).PlayerDamage, "teammate");

            var stranger = SeededPlayer(DefenderId, Bound(other, Defenders), NearHall);
            Player.OnPvpDamageRecorded(stranger, a, 10);
            Assert.AreEqual(0, Stats(match, AttackerId).PlayerDamage, "a defender of another match");
            Assert.AreEqual(0, Stats(other, DefenderId).PlayerDamage);

            Player.OnPvpDamageRecorded(a, a, 10);
            Assert.AreEqual(0, Stats(match, AttackerId).PlayerDamage, "self");

            var d = SeededPlayer(DefenderId, Bound(match, Defenders), NearHall);
            Player.OnPvpDamageRecorded(d, TestCreatures.CreateAttacker(), 10);
            Assert.IsTrue(match.Performance.Snapshot().All(s => s.PlayerDamage == 0), "an NPC attacker");

            var countdownA = SeededPlayer(AttackerId, Bound(match, Attackers, PvpMatchState.Countdown));
            var countdownD = SeededPlayer(DefenderId, Bound(match, Defenders, PvpMatchState.Countdown), NearHall);
            Player.OnPvpDamageRecorded(countdownD, countdownA, 10);
            Assert.AreEqual(0, Stats(match, AttackerId).PlayerDamage, "not Live");

            // control: the same pair, Live, IS credited
            Player.OnPvpDamageRecorded(d, a, 10);
            Assert.AreEqual(10, Stats(match, AttackerId).PlayerDamage, "control");
        }

        /// <summary>A copy of the match with the same id is a different match instance: never credited.</summary>
        [TestMethod]
        public void Hook_SameMatchIdButAnotherInstance_CreditsNothing()
        {
            var match = NewMatch();
            var twin = new PvpMatch(match.MatchId, match.ModeKey, match.Teams, Now) { Performance = BattlegroundPerformanceLedger.ForTeams(match.Teams) };

            var a = SeededPlayer(AttackerId, Bound(match, Attackers));
            var d = SeededPlayer(DefenderId, Bound(twin, Defenders), NearHall);

            Player.OnPvpDamageRecorded(d, a, 10);

            Assert.AreEqual(0, Stats(match, AttackerId).PlayerDamage);
            Assert.AreEqual(0, Stats(twin, AttackerId).PlayerDamage);
        }

        /// <summary>
        /// Owner ruling 2026-10-08, consistent with PvpClassifier: a combat pet's hit credits nobody, not even its owner, and stamps no
        /// engagement. The control (the owner's own hit on the same victim) proves the zero is the pet rule, not a broken fixture.
        /// </summary>
        [TestMethod]
        public void Hook_APetsHit_CreditsNobody_AndStampsNoEngagement()
        {
            var match = NewMatch();
            var owner = SeededPlayer(AttackerId, Bound(match, Attackers));
            var pet = Seeded<CombatPet>();
            pet.P_PetOwner = owner;
            var defender = SeededPlayer(DefenderId, Bound(match, Defenders), FarFromHall);

            Player.OnPvpDamageRecorded(defender, pet, 25);

            Assert.AreEqual(0, Stats(match, AttackerId).PlayerDamage, "the owner is not credited for the pet's hit");
            Assert.IsNull(owner.PvpEngagement, "no engagement from a pet's hit");
            Assert.IsNull(defender.PvpEngagement);

            Player.OnPvpDamageRecorded(defender, owner, 25);
            Assert.AreEqual(25, Stats(match, AttackerId).PlayerDamage, "control: the owner's own hit is credited");
            Assert.IsNotNull(owner.PvpEngagement, "control: and stamps");
        }

        [TestMethod]
        public void Hook_DefenderDamage_SplitsByTheVictimsPosition()
        {
            var match = NewMatch();
            var defender = SeededPlayer(DefenderId, Bound(match, Defenders));
            var nearVictim = SeededPlayer(AttackerId, Bound(match, Attackers), NearHall);
            var farVictim = SeededPlayer(Attacker2Id, Bound(match, Attackers), FarFromHall);

            Player.OnPvpDamageRecorded(nearVictim, defender, 30);
            Player.OnPvpDamageRecorded(farVictim, defender, 7);

            var s = Stats(match, DefenderId);
            Assert.AreEqual(37, s.PlayerDamage);
            Assert.AreEqual(30, s.DefenderDamageNear, "the victim stood 2 m from the vulnerable crystal");
            Assert.AreEqual(7, s.DefenderDamageFar);

            // the same victim moves: classification follows the victim's own Location at hit time
            nearVictim.Location = new Position(FarFromHall);
            Player.OnPvpDamageRecorded(nearVictim, defender, 5);
            Assert.AreEqual(12, Stats(match, DefenderId).DefenderDamageFar);
        }

        [TestMethod]
        public void Hook_ALifestoneProtectedVictim_IsNotCredited()
        {
            var match = NewMatch();
            var attacker = SeededPlayer(AttackerId, Bound(match, Attackers));
            var defender = SeededPlayer(DefenderId, Bound(match, Defenders), NearHall);

            defender.UnderLifestoneProtection = true;
            Player.OnPvpDamageRecorded(defender, attacker, 10);
            Assert.AreEqual(0, Stats(match, AttackerId).PlayerDamage, "a DoT tick on a protected player records but never lands");

            defender.UnderLifestoneProtection = false;
            Player.OnPvpDamageRecorded(defender, attacker, 10);
            Assert.AreEqual(10, Stats(match, AttackerId).PlayerDamage, "control");
        }

        [TestMethod]
        public void Hook_AMatchWithNoLedger_IsANoOp()
        {
            var match = NewMatch(withLedger: false);
            var attacker = SeededPlayer(AttackerId, Bound(match, Attackers));
            var defender = SeededPlayer(DefenderId, Bound(match, Defenders), NearHall);

            Player.OnPvpDamageRecorded(defender, attacker, 10);

            Assert.IsNotNull(attacker.PvpEngagement, "engagement still stamps (an arena-shaped match has no ledger)");
        }

        // ================= the damage hook: crystals, through the real Creature.TakeDamage and DamageHistory.Add =================

        private static Creature Crystal(PvpMatch match)
        {
            var crystal = TestCreatures.CreateDefender(maxHealth: 990);
            Assert.IsTrue(crystal.SetBattlegroundObjective(new BattlegroundObjectiveTag(match.MatchId, 0, Defenders)));
            crystal.Location = At(110f, -70f);
            return crystal;
        }

        [TestMethod]
        public void CrystalHit_CreditsTheAmountActuallyRemoved()
        {
            var match = NewMatch();
            var crystal = Crystal(match);
            var attacker = SeededPlayer(AttackerId, Bound(match, Attackers));

            Assert.AreEqual(40u, crystal.TakeDamage(attacker, DamageType.Slash, 40f));
            Assert.AreEqual(40, Stats(match, AttackerId).CrystalDamage);
            Assert.AreEqual(0, Stats(match, AttackerId).PlayerDamage, "a crystal is not a player");

            // with two engaged defenders the hit is cut by 25% before the write: the ledger takes what was removed, not what was rolled
            match.DefenderReduction = CrystalDefenderReductionDials.From(BattlegroundTunables.Defaults);
            Creature.DefenderSampleSource = _ => new[] { EngagedDefender(match), EngagedDefender(match) };
            Assert.AreEqual(75u, crystal.TakeDamage(attacker, DamageType.Slash, 100f));
            Assert.AreEqual(115, Stats(match, AttackerId).CrystalDamage);
        }

        private static DefenderSample EngagedDefender(PvpMatch match)
            => new DefenderSample(Bound(match, Defenders), true, At(110f, -71f), new PvpEngagementStamp(match.MatchId, Now));

        [TestMethod]
        public void CrystalHit_ByADefender_OrAnotherMatch_OrBeforeLive_CreditsNothing()
        {
            var match = NewMatch();
            var crystal = Crystal(match);

            crystal.TakeDamage(SeededPlayer(DefenderId, Bound(match, Defenders)), DamageType.Slash, 10f);
            crystal.TakeDamage(SeededPlayer(AttackerId, Bound(NewMatch(), Attackers)), DamageType.Slash, 10f);
            crystal.TakeDamage(SeededPlayer(AttackerId, Bound(match, Attackers, PvpMatchState.Countdown)), DamageType.Slash, 10f);
            crystal.TakeDamage(TestCreatures.CreateAttacker(), DamageType.Slash, 10f);

            Assert.IsTrue(match.Performance.Snapshot().All(s => s.CrystalDamage == 0));
        }

        /// <summary>A pet's hit on a crystal credits nobody (owner ruling 2026-10-08); the owner's own hit is the control.</summary>
        [TestMethod]
        public void CrystalHit_ByAPet_CreditsNobody()
        {
            var match = NewMatch();
            var crystal = Crystal(match);
            var owner = SeededPlayer(AttackerId, Bound(match, Attackers));
            var pet = Seeded<CombatPet>();
            pet.P_PetOwner = owner;

            Player.OnPvpDamageRecorded(crystal, pet, 15);
            Assert.AreEqual(0, Stats(match, AttackerId).CrystalDamage, "the owner is not credited for the pet's hit");

            Player.OnPvpDamageRecorded(crystal, owner, 15);
            Assert.AreEqual(15, Stats(match, AttackerId).CrystalDamage, "control: the owner's own hit is credited");
        }

        /// <summary>The kill chip reaches TakeDamage with the killer as its source, but it is not the killer's hit. Control first.</summary>
        [TestMethod]
        public void TheKillChip_IsNotCrystalDamage()
        {
            var match = NewMatch();
            var crystal = Crystal(match);
            var killer = SeededPlayer(AttackerId, Bound(match, Attackers));

            crystal.TakeDamage(killer, DamageType.Slash, 10f);
            Assert.AreEqual(10, Stats(match, AttackerId).CrystalDamage, "control: an ordinary hit counts");

            var before = crystal.Health.Current;
            Assert.IsNotNull(LivePvpMatchSpaces.ApplyKillEffect(crystal, new CrystalKillEffect(0, true, 1.0), killer));
            Assert.AreEqual(before - 10u, crystal.Health.Current, "fixture: the 1% chip landed");

            Assert.AreEqual(10, Stats(match, AttackerId).CrystalDamage, "the chip is not credited");
        }

        // ================= hill seconds (KothTickHandler) =================

        [TestMethod]
        public void HillSeconds_OnlyFromIpDedupedCountedSamples()
        {
            var dials = BattlegroundTunables.Defaults with { KothTickSeconds = 4, KothDedupeIp = true };
            var zone = new KothZone(10, 20, 1, Radius: 5, Height: 2);
            var m = new FakeBattlegroundContext();
            var h = new KothTickHandler(dials, zone);
            h.OnLive(m);

            m.Sample(m.Member(0, 0), 10, 20, 1, ip: "shared");
            m.Sample(m.Member(0, 1), 11, 20, 1, ip: "shared");      // same IP, same team: deduped
            m.Sample(m.Member(1, 0), 40, 40, 1);                     // off the point
            m.Sample(m.Member(1, 1), 10, 21, 1, dead: true);         // dead: not eligible

            h.OnTick(m, FakeBattlegroundContext.Start.AddSeconds(4));

            CollectionAssert.AreEqual(new[] { (m.Member(0, 0).CharacterId, 4) }, m.ObjectiveTime.ToArray(), "one counted sample, one tick of seconds");
            Assert.AreEqual(2, m.Drains.Count, "control: both eligible players on the point are still drained");
        }

        [TestMethod]
        public void HillSeconds_ContestedPointStillEarnsTime_AndDedupeOffCountsEachPlayer()
        {
            var dials = BattlegroundTunables.Defaults with { KothTickSeconds = 5, KothDedupeIp = false };
            var m = new FakeBattlegroundContext();
            var h = new KothTickHandler(dials, new KothZone(10, 20, 1, Radius: 5, Height: 2));
            h.OnLive(m);

            m.Sample(m.Member(0, 0), 10, 20, 1, ip: "shared");
            m.Sample(m.Member(0, 1), 11, 20, 1, ip: "shared");
            m.Sample(m.Member(1, 0), 12, 20, 1);

            h.OnTick(m, FakeBattlegroundContext.Start.AddSeconds(5));

            Assert.AreEqual(3, m.ObjectiveTime.Count);
            Assert.IsTrue(m.ObjectiveTime.All(t => t.Seconds == 5));
        }

        // ================= source pin: the hook inside DamageHistory.Add =================

        private const string PerfHook = "Player.OnPvpDamageRecorded(Creature, attacker, amount);";

        private static string RepoFile(params string[] relative)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Source", "property-registry.tsv")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, "could not find the repo root by walking up from the test directory");

            return File.ReadAllText(Path.Combine(new[] { dir.FullName, "Source", "ACE.Server" }.Concat(relative).ToArray())).Replace("\r\n", "\n");
        }

        private static string AddBody()
        {
            var text = RepoFile("Entity", "DamageHistory.cs");
            const string signature = "public void Add(WorldObject attacker, DamageType damageType, uint amount)";
            var start = text.IndexOf(signature, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"{signature} not found");
            var end = text.IndexOf("\n        }\n", start, StringComparison.Ordinal);
            Assert.IsTrue(end > start);
            return text.Substring(start, end - start);
        }

        /// <summary>
        /// Null when DamageHistory.Add hands the hook the amount it was given (the health actually removed), exactly once, after the
        /// zero-amount return and after the log entry is recorded.
        /// </summary>
        private static string PerfHookFault(string body)
        {
            var zero = body.IndexOf("if (amount == 0)", StringComparison.Ordinal);
            var logged = body.IndexOf("Log.Add(entry);", StringComparison.Ordinal);
            var hook = body.IndexOf(PerfHook, StringComparison.Ordinal);

            if (zero < 0 || logged < 0)
                return "the pin's anchors were not found (the pin needs updating)";

            if (hook < 0)
                return "DamageHistory.Add does not pass the recorded amount to the PvP hook";

            if (hook < zero || hook < logged)
                return "the hook runs before the zero return or before the hit is logged";

            if (body.IndexOf("Player.OnPvpDamageRecorded(", hook + 1, StringComparison.Ordinal) >= 0)
                return "the hook runs twice";

            return null;
        }

        [TestMethod]
        public void DamageHistoryAdd_PassesTheRecordedAmountToTheHook_Once()
        {
            var body = AddBody();

            Assert.IsNull(PerfHookFault(body));
            Assert.IsNotNull(PerfHookFault(body.Replace(PerfHook, "")), "removing the hook must fail the check");
            Assert.IsNotNull(PerfHookFault(body.Replace(PerfHook, "Player.OnPvpDamageRecorded(Creature, attacker, 1);")), "a constant amount must fail the check");
            Assert.IsNotNull(PerfHookFault(body.Replace(PerfHook, PerfHook + "\n" + PerfHook)), "a second call must fail the check");
            Assert.IsNotNull(PerfHookFault(body.Replace(PerfHook, "").Replace("            if (amount == 0)", "            " + PerfHook + "\n            if (amount == 0)")), "a hook before the zero return must fail the check");
        }
    }
}
