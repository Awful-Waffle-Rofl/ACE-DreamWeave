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
    /// Docs/Pvp/ATTACK-DEFEND.md "Engaged defenders": the pure rule (<see cref="CrystalDefenderReduction"/>), the engagement stamp
    /// (<see cref="Player.OnPvpDamageRecorded"/>), the crystal damage sink driven for real through Creature.TakeDamage on a test crystal,
    /// the kill chip exemption through the real <see cref="LivePvpMatchSpaces.ApplyKillEffect"/>, and source pins for the two sites no unit
    /// host can drive (the spell-projectile sink and the DamageHistory.Add hook).
    /// </summary>
    [TestClass]
    public class AttackDefendDefenderReductionTests
    {
        private const uint Instance = 0x0001_0400;

        private const int Attackers = CrystalWinCondition.AttackerTeam;

        private const int Defenders = CrystalWinCondition.DefenderTeam;

        private static readonly DateTime Now = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);

        private static readonly CrystalDefenderReductionDials Shipped = CrystalDefenderReductionDials.From(BattlegroundTunables.Defaults);

        /// <summary>The Great Hall crystal site, (110, -70, -6) in 0x003C, in the match instance.</summary>
        private static Position At(float x, float y, float z = -6f, uint instance = Instance) => new Position(0x003C01EE, x, y, z, 0f, 0f, 0f, 1f, instance);

        private static readonly Position CrystalAt = At(110f, -70f);

        private Func<DateTime> savedClock;

        private Func<Creature, IEnumerable<DefenderSample>> savedSamples;

        [TestInitialize]
        public void Setup()
        {
            savedClock = PvpArenaHookSettings.UtcNow;
            savedSamples = Creature.DefenderSampleSource;
            PvpArenaHookSettings.UtcNow = () => Now;
        }

        [TestCleanup]
        public void Cleanup()
        {
            PvpArenaHookSettings.UtcNow = savedClock;
            Creature.DefenderSampleSource = savedSamples;
        }

        private static PvpMatch NewMatch(CrystalDefenderReductionDials dials = null, bool withDials = true)
        {
            var teams = new List<PvpTeam>
            {
                new PvpTeam(Attackers, new List<PvpParticipant> { new PvpParticipant(1, 1500) }),
                new PvpTeam(Defenders, new List<PvpParticipant> { new PvpParticipant(2, 1500) }),
            };

            var match = new PvpMatch(Guid.NewGuid(), "bg_ad", teams, Now);

            if (withDials)
                match.DefenderReduction = dials ?? Shipped;

            return match;
        }

        private static PvpPlayerBinding Bound(PvpMatch match, int team, PvpMatchState state = PvpMatchState.Live, bool respawning = false)
            => new PvpPlayerBinding(match, team, state, false, false, false, false, false, respawning: respawning);

        private static BattlegroundObjectiveTag Tag(PvpMatch match) => new BattlegroundObjectiveTag(match.MatchId, 0, Defenders);

        /// <summary>A defender standing <paramref name="metres"/> due south of the crystal, engaged <paramref name="secondsAgo"/> ago in this match.</summary>
        private static DefenderSample Defender(PvpMatch match, float metres = 2f, double secondsAgo = 2.0, bool alive = true, int team = Defenders, bool respawning = false, Guid? engagedIn = null, bool engaged = true)
            => new DefenderSample(
                Bound(match, team, respawning: respawning),
                alive,
                At(110f, -70f - metres),
                engaged ? new PvpEngagementStamp(engagedIn ?? match.MatchId, Now.AddSeconds(-secondsAgo)) : null);

        private static float Hit(PvpMatch match, IEnumerable<DefenderSample> defenders, float amount = 100f, PvpPlayerBinding attacker = null)
            => CrystalDefenderReduction.Apply(Tag(match), attacker ?? Bound(match, Attackers), CrystalAt, defenders, Now, amount);

        // ================= the pure rule =================

        [TestMethod]
        public void Shipped_AreTheOwnersNumbers()
        {
            Assert.AreEqual(new CrystalDefenderReductionDials(0.125, 0.5, 5.0, 10.0), Shipped, "12.5% per defender (the midpoint of 10-15%), cap 50%, 5 m, 10 s");
        }

        [TestMethod]
        public void NoEngagedDefender_FullDamage()
        {
            var match = NewMatch();

            Assert.AreEqual(100f, Hit(match, Array.Empty<DefenderSample>()));
            Assert.AreEqual(100f, Hit(match, null));
        }

        [TestMethod]
        [DataRow(1, 87.5f)]
        [DataRow(2, 75f)]
        [DataRow(3, 62.5f)]
        [DataRow(4, 50f)]
        [DataRow(5, 50f)]
        [DataRow(6, 50f)]
        public void EachEngagedDefender_TakesTwelveAndAHalfPercent_CappedAtHalf(int engaged, float expected)
        {
            var match = NewMatch();
            var defenders = Enumerable.Range(0, engaged).Select(i => Defender(match, metres: 1f + 0.5f * i)).ToList();

            Assert.AreEqual(expected, Hit(match, defenders), 1e-4f);
        }

        [TestMethod]
        public void Multiplier_Arithmetic()
        {
            Assert.AreEqual(1.0, CrystalDefenderReduction.Multiplier(0, Shipped));
            Assert.AreEqual(0.875, CrystalDefenderReduction.Multiplier(1, Shipped), 1e-12);
            Assert.AreEqual(0.5, CrystalDefenderReduction.Multiplier(4, Shipped), 1e-12);
            Assert.AreEqual(0.5, CrystalDefenderReduction.Multiplier(5, Shipped), 1e-12);
            Assert.AreEqual(1.0, CrystalDefenderReduction.Multiplier(3, null), "no dials: unchanged");
            Assert.AreEqual(0.05, CrystalDefenderReduction.Multiplier(3, Shipped with { PerDefender = 0.5, Cap = 7.0 }), 1e-12, "a cap above 0.95 counts as 0.95: a hit always keeps 5%");
            Assert.AreEqual(0.05, CrystalDefenderReduction.Multiplier(10, Shipped with { PerDefender = 5.0, Cap = 1.0 }), 1e-12, "a cap of exactly 1 is held to 0.95 too: never immune");
            Assert.AreEqual(0.1, CrystalDefenderReduction.Multiplier(2, Shipped with { PerDefender = 0.45, Cap = 0.95 }), 1e-12, "below the ceiling the cap is honoured as set");
        }

        /// <summary>Through the whole rule: with every setting maxed out, a crystal still takes 5% of an attacker's hit.</summary>
        [TestMethod]
        public void AMaxedOutSetting_NeverMakesACrystalImmune()
        {
            var match = NewMatch(Shipped with { PerDefender = 1.0, Cap = 1.0 });

            Assert.AreEqual(5f, Hit(match, new[] { Defender(match), Defender(match), Defender(match) }), 1e-4f);
        }

        [TestMethod]
        [DataRow(0.0)]
        [DataRow(-0.1)]
        [DataRow(double.NaN)]
        [DataRow(double.PositiveInfinity)]
        public void AnInvalidSetting_TurnsTheRuleOff(double bad)
        {
            foreach (var dials in new[] { Shipped with { PerDefender = bad }, Shipped with { Cap = bad }, Shipped with { Radius = bad }, Shipped with { WindowSeconds = bad } })
            {
                var match = NewMatch(dials);

                Assert.AreEqual(100f, Hit(match, new[] { Defender(match), Defender(match) }), $"{dials}");
            }
        }

        [TestMethod]
        public void Radius_IsInclusive_ThreeDimensional_AndOutsideDoesNotCount()
        {
            var match = NewMatch();

            Assert.AreEqual(87.5f, Hit(match, new[] { Defender(match, metres: 5f) }), 1e-4f, "exactly 5 m counts");
            Assert.AreEqual(100f, Hit(match, new[] { Defender(match, metres: 5.01f) }), "5.01 m does not");

            var above = new DefenderSample(Bound(match, Defenders), true, At(110f, -73f, -2f), new PvpEngagementStamp(match.MatchId, Now));
            Assert.AreEqual(87.5f, Hit(match, new[] { above }), 1e-4f, "3 m across and 4 m up is exactly 5 m in 3D: counts");

            var higher = new DefenderSample(Bound(match, Defenders), true, At(110f, -73f, -1.5f), new PvpEngagementStamp(match.MatchId, Now));
            Assert.AreEqual(100f, Hit(match, new[] { higher }), "3 m across, 4.5 m up: 5.4 m in 3D although 3 m in 2D");
        }

        [TestMethod]
        public void ADeadOrRespawningDefender_DoesNotCount()
        {
            var match = NewMatch();

            Assert.AreEqual(100f, Hit(match, new[] { Defender(match, alive: false) }), "dead");
            Assert.AreEqual(100f, Hit(match, new[] { Defender(match, respawning: true) }), "in the pen");
            Assert.AreEqual(87.5f, Hit(match, new[] { Defender(match), Defender(match, alive: false) }), 1e-4f, "only the living one");
        }

        [TestMethod]
        public void ADefenderIdlePastTheWindow_DoesNotCount_TheWindowIsInclusive()
        {
            var match = NewMatch();

            Assert.AreEqual(87.5f, Hit(match, new[] { Defender(match, secondsAgo: 10.0) }), 1e-4f, "exactly 10 s ago counts");
            Assert.AreEqual(100f, Hit(match, new[] { Defender(match, secondsAgo: 10.01) }), "10.01 s ago does not");
            Assert.AreEqual(100f, Hit(match, new[] { Defender(match, engaged: false) }), "never engaged");
            Assert.AreEqual(100f, Hit(match, new[] { Defender(match, secondsAgo: -1.0) }), "a stamp in the future is not trusted");
        }

        [TestMethod]
        public void AStampFromAnotherMatch_DoesNotCount()
        {
            var match = NewMatch();

            Assert.AreEqual(100f, Hit(match, new[] { Defender(match, engagedIn: Guid.NewGuid()) }));
        }

        [TestMethod]
        public void AttackersAndOtherMatchesPlayers_AreNeverCounted()
        {
            var match = NewMatch();
            var other = NewMatch();

            Assert.AreEqual(100f, Hit(match, new[] { Defender(match, team: Attackers), Defender(match, team: Attackers) }), "engaged attackers at the crystal");
            Assert.AreEqual(100f, Hit(match, new[] { Defender(other) with { Engagement = new PvpEngagementStamp(match.MatchId, Now) } }), "a defender bound to another match");
            Assert.AreEqual(100f, Hit(match, new[] { new DefenderSample(null, true, At(110f, -71f), new PvpEngagementStamp(match.MatchId, Now)) }), "an unbound player");
        }

        [TestMethod]
        public void ADefenderInAnotherInstance_DoesNotCount()
        {
            var match = NewMatch();
            var elsewhere = new DefenderSample(Bound(match, Defenders), true, At(110f, -71f, instance: Instance + 1), new PvpEngagementStamp(match.MatchId, Now));

            Assert.AreEqual(100f, Hit(match, new[] { elsewhere }));
        }

        [TestMethod]
        public void OnlyALiveAttackerOfThisMatchIsReduced()
        {
            var match = NewMatch();
            var defenders = new[] { Defender(match), Defender(match) };

            Assert.AreEqual(75f, Hit(match, defenders), 1e-4f, "fixture: a Live attacker is reduced");

            foreach (var state in new[] { PvpMatchState.Staging, PvpMatchState.Countdown, PvpMatchState.Resolving })
                Assert.AreEqual(100f, Hit(match, defenders, attacker: Bound(match, Attackers, state)), $"{state}");

            Assert.AreEqual(100f, Hit(match, defenders, attacker: Bound(match, Defenders)), "a defender's own hit (refused by the gate anyway) is not this rule's");
            Assert.AreEqual(100f, Hit(match, defenders, attacker: Bound(NewMatch(), Attackers)), "an attacker of another match");
            Assert.AreEqual(100f, CrystalDefenderReduction.Apply(Tag(match), null, CrystalAt, defenders, Now, 100f), "no binding");
            Assert.AreEqual(100f, Hit(NewMatch(withDials: false), defenders), "a match formed without dials (any other mode)");
        }

        [TestMethod]
        public void ANonPositiveHit_IsUntouched()
        {
            var match = NewMatch();

            Assert.AreEqual(0f, Hit(match, new[] { Defender(match) }, 0f));
            Assert.AreEqual(-5f, Hit(match, new[] { Defender(match) }, -5f));
        }

        [TestMethod]
        public void TheDialsAreTheFormationSnapshot_NotALiveRead()
        {
            var d = BattlegroundTunables.Defaults with { AdDefenderDrPer = 0.2, AdDefenderDrCap = 0.3, AdDefenderDrRadius = 7.0, AdDefenderDrWindowSeconds = 4.0 };

            Assert.AreEqual(new CrystalDefenderReductionDials(0.2, 0.3, 7.0, 4.0), CrystalDefenderReductionDials.From(d));
            Assert.IsNull(CrystalDefenderReductionDials.From(null));
        }

        // ================= the engagement stamp =================

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

        private static Player SeededPlayer(PvpPlayerBinding binding)
        {
            var p = Seeded<Player>();
            p.SetPvpBindingForTests(binding);
            return p;
        }

        [TestMethod]
        public void OpposingPlayersOfOneLiveMatch_StampBoth_DealtAndTaken()
        {
            var match = NewMatch();
            var attacker = SeededPlayer(Bound(match, Attackers));
            var defender = SeededPlayer(Bound(match, Defenders));

            Player.OnPvpDamageRecorded(defender, attacker);

            Assert.AreEqual(new PvpEngagementStamp(match.MatchId, Now), defender.PvpEngagement, "the defender TOOK damage");
            Assert.AreEqual(new PvpEngagementStamp(match.MatchId, Now), attacker.PvpEngagement, "the attacker DEALT it");

            // and the other direction: a defender dealing damage is engaged too
            var defender2 = SeededPlayer(Bound(match, Defenders));
            var attacker2 = SeededPlayer(Bound(match, Attackers));
            Player.OnPvpDamageRecorded(attacker2, defender2);
            Assert.IsNotNull(defender2.PvpEngagement, "the defender DEALT damage");
        }

        [TestMethod]
        public void NoStamp_ForTeammates_OtherMatches_NonLive_OrSelf()
        {
            var match = NewMatch();

            var a = SeededPlayer(Bound(match, Defenders));
            var b = SeededPlayer(Bound(match, Defenders));
            Player.OnPvpDamageRecorded(a, b);
            Assert.IsNull(a.PvpEngagement, "teammate");
            Assert.IsNull(b.PvpEngagement, "teammate");

            var c = SeededPlayer(Bound(match, Defenders));
            var d = SeededPlayer(Bound(NewMatch(), Attackers));
            Player.OnPvpDamageRecorded(c, d);
            Assert.IsNull(c.PvpEngagement, "another match");

            var e = SeededPlayer(Bound(match, Defenders, PvpMatchState.Countdown));
            var f = SeededPlayer(Bound(match, Attackers, PvpMatchState.Countdown));
            Player.OnPvpDamageRecorded(e, f);
            Assert.IsNull(e.PvpEngagement, "not Live (a Countdown vuln)");

            var g = SeededPlayer(Bound(match, Defenders));
            Player.OnPvpDamageRecorded(g, g);
            Assert.IsNull(g.PvpEngagement, "self damage");

            var h = SeededPlayer(null);
            var i = SeededPlayer(Bound(match, Attackers));
            Player.OnPvpDamageRecorded(h, i);
            Assert.IsNull(i.PvpEngagement, "an unbound victim");
        }

        [TestMethod]
        public void DamageWithAnNpcOrACrystal_StampsNothing()
        {
            var match = NewMatch();
            var defender = SeededPlayer(Bound(match, Defenders));
            var npc = TestCreatures.CreateAttacker();

            Player.OnPvpDamageRecorded(defender, npc);
            Assert.IsNull(defender.PvpEngagement, "taken from an NPC");

            var crystal = TestCreatures.CreateDefender(maxHealth: 990);
            Assert.IsTrue(crystal.SetBattlegroundObjective(Tag(match)));
            var attacker = SeededPlayer(Bound(match, Attackers));

            Player.OnPvpDamageRecorded(crystal, attacker);
            Assert.IsNull(attacker.PvpEngagement, "dealt to a crystal");

            Player.OnPvpDamageRecorded(npc, defender);
            Assert.IsNull(defender.PvpEngagement, "dealt to an NPC");
        }

        // ================= the defender sample, read from a real Player =================

        /// <summary>A seeded Player with a working Health vital and position store, so IsDead and Location read real values.</summary>
        private static Player SamplePlayer(PvpPlayerBinding binding, uint health, Position at)
        {
            var p = SeededPlayer(binding);
            p.Biota.PropertiesAttribute2nd = new Dictionary<ACE.Entity.Enum.Properties.PropertyAttribute2nd, ACE.Entity.Models.PropertiesAttribute2nd>();
            p.Biota.PropertiesPosition = new Dictionary<ACE.Entity.Enum.Properties.PositionType, ACE.Entity.Models.PropertiesPosition>();

            var vitals = new Dictionary<ACE.Entity.Enum.Properties.PropertyAttribute2nd, ACE.Server.WorldObjects.Entity.CreatureVital>
            {
                { ACE.Entity.Enum.Properties.PropertyAttribute2nd.MaxHealth, new ACE.Server.WorldObjects.Entity.CreatureVital(p, ACE.Entity.Enum.Properties.PropertyAttribute2nd.MaxHealth) },
            };
            var field = typeof(Creature).GetField("Vitals", BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(field, "Creature.Vitals was not found by reflection - has it been renamed?");
            field.SetValue(p, vitals);

            foreach (var name in new[] { "positionCache", "ephemeralPositions" })
            {
                var store = typeof(WorldObject).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.IsNotNull(store, $"WorldObject.{name} was not found by reflection - has it been renamed?");
                store.SetValue(p, Activator.CreateInstance(store.FieldType));
            }

            p.Health.Current = health;
            p.Location = new Position(at);
            return p;
        }

        [TestMethod]
        public void ToDefenderSample_CarriesBindingLivenessPositionAndStamp()
        {
            var match = NewMatch();
            var binding = Bound(match, Defenders);
            var p = SamplePlayer(binding, 100, At(110f, -72f));
            p.StampPvpEngagement(match.MatchId, Now);

            var s = p.ToDefenderSample();

            Assert.AreSame(binding, s.Binding);
            Assert.IsTrue(s.Alive, "health above 0 and not dying: alive");
            Assert.AreEqual(At(110f, -72f).ToString(), s.Location.ToString());
            Assert.AreEqual(new PvpEngagementStamp(match.MatchId, Now), s.Engagement);
            Assert.AreEqual(87.5f, Hit(match, new[] { s }), 1e-4f, "the real sample counts as one engaged defender");

            p.IsInDeathProcess = true;
            Assert.IsFalse(p.ToDefenderSample().Alive, "in the death process: not alive");

            p.IsInDeathProcess = false;
            p.Health.Current = 0;
            Assert.IsTrue(p.IsDead, "fixture: 0 health is dead");
            Assert.IsFalse(p.ToDefenderSample().Alive, "dead: not alive");
            Assert.AreEqual(100f, Hit(match, new[] { p.ToDefenderSample() }), "a dead defender's sample is not counted");

            Assert.IsNull(SamplePlayer(binding, 100, At(110f, -72f)).ToDefenderSample().Engagement, "never engaged: no stamp");
        }

        /// <summary>The production sample source, for a crystal that is on no landblock (this host), yields no defender rather than null.</summary>
        [TestMethod]
        public void DefaultSampleSource_WithNoLandblock_IsEmpty()
        {
            var match = NewMatch();
            var crystal = Crystal(match);
            Assert.IsNull(crystal.CurrentLandblock, "fixture: the test crystal is on no landblock");

            var samples = savedSamples(crystal);

            Assert.IsNotNull(samples);
            Assert.AreEqual(0, samples.Count());

            Creature.DefenderSampleSource = savedSamples;
            Assert.AreEqual(100u, crystal.TakeDamage(SeededPlayer(Bound(match, Attackers)), DamageType.Slash, 100f), "through the real source: nobody engaged, full damage");
        }

        // ================= the attacker notification (Player.DamageTarget) =================

        [TestMethod]
        public void AttackerNotification_ReportsWhatACrystalActuallyTook()
        {
            Assert.AreEqual(75u, Player.AttackerNotificationDamage(false, 75u, 100f), "a crystal hit reports the reduced amount TakeDamage applied");
            Assert.AreEqual(100u, Player.AttackerNotificationDamage(false, null, 99.6f), "any other target: the rolled damage, rounded");
            Assert.AreEqual(0u, Player.AttackerNotificationDamage(true, 75u, 100f), "a digsite-immune hit still reports 0");
        }

        private const string MeleeSinkCall = "var dealt = target.TakeDamage(this, damageEvent.DamageType, damageEvent.Damage, damageEvent.IsCritical, IncomingDamageOrigin.DirectHit);";

        private const string MeleeKeep = "objectiveDealt = dealt;";

        private const string MeleeNotify = "var intDamage = AttackerNotificationDamage(digsiteImmuneBlocked, objectiveDealt, damageEvent.Damage);";

        /// <summary>Null when Player.DamageTarget keeps TakeDamage's return for a crystal and reports it in the attacker notification.</summary>
        private static string NotificationFault(string body)
        {
            var call = body.IndexOf(MeleeSinkCall, StringComparison.Ordinal);
            var keep = body.IndexOf(MeleeKeep, StringComparison.Ordinal);
            var notify = body.IndexOf(MeleeNotify, StringComparison.Ordinal);

            if (call < 0)
                return "the non-player TakeDamage result is discarded";

            if (keep < call)
                return "the crystal's applied damage is not kept";

            if (notify < keep)
                return "the attacker notification does not report the kept amount";

            return null;
        }

        [TestMethod]
        public void DamageTarget_NotifiesTheAttackerOfTheReducedCrystalHit()
        {
            var body = MethodBody(RepoFile("WorldObjects", "Player_Combat.cs"), "public DamageEvent DamageTarget(Creature target, WorldObject damageSource");

            Assert.IsNull(NotificationFault(body));
            Assert.IsNotNull(NotificationFault(body.Replace(MeleeSinkCall, "target.TakeDamage(this, damageEvent.DamageType, damageEvent.Damage, damageEvent.IsCritical, IncomingDamageOrigin.DirectHit);")), "discarding the return must fail the check");
            Assert.IsNotNull(NotificationFault(body.Replace(MeleeNotify, "var intDamage = digsiteImmuneBlocked ? 0u : (uint)Math.Round(damageEvent.Damage);")), "the old rolled-damage notification must fail the check");
        }

        // ================= the crystal sink, driven for real through Creature.TakeDamage =================

        /// <summary>A tagged 1000-point test crystal at the Great Hall site in the match instance.</summary>
        private static Creature Crystal(PvpMatch match)
        {
            var crystal = TestCreatures.CreateDefender(maxHealth: 990);
            Assert.IsTrue(crystal.SetBattlegroundObjective(Tag(match)));
            crystal.Location = new Position(CrystalAt);
            Assert.AreEqual(1000u, crystal.Health.MaxValue, "fixture: a 1000-point crystal");
            return crystal;
        }

        private static void SetDefenders(params DefenderSample[] samples) => Creature.DefenderSampleSource = _ => samples;

        [TestMethod]
        public void TakeDamage_AttackerHit_IsReducedByTheEngagedDefenders()
        {
            var match = NewMatch();
            var crystal = Crystal(match);
            var attacker = SeededPlayer(Bound(match, Attackers));

            SetDefenders(Defender(match), Defender(match));

            var dealt = crystal.TakeDamage(attacker, DamageType.Slash, 100f);

            Assert.AreEqual(75u, dealt, "two engaged defenders: 25% off");
            Assert.AreEqual(925u, crystal.Health.Current);
        }

        [TestMethod]
        public void TakeDamage_WithNoEngagedDefender_IsFull()
        {
            var match = NewMatch();
            var crystal = Crystal(match);
            var attacker = SeededPlayer(Bound(match, Attackers));

            SetDefenders(Defender(match, metres: 8f), Defender(match, secondsAgo: 30.0), Defender(match, team: Attackers));

            Assert.AreEqual(100u, crystal.TakeDamage(attacker, DamageType.Slash, 100f));
        }

        [TestMethod]
        public void TakeDamage_ANonCrystal_OrANonPlayerSource_IsUntouched()
        {
            var match = NewMatch();
            SetDefenders(Defender(match), Defender(match), Defender(match), Defender(match));

            var plain = TestCreatures.CreateDefender(maxHealth: 990);
            plain.Location = new Position(CrystalAt);
            Assert.AreEqual(100u, plain.TakeDamage(SeededPlayer(Bound(match, Attackers)), DamageType.Slash, 100f), "an untagged creature");

            var crystal = Crystal(match);
            Assert.AreEqual(100u, crystal.TakeDamage(TestCreatures.CreateAttacker(), DamageType.Slash, 100f), "an NPC source");
        }

        [TestMethod]
        public void TakeDamage_BeforeLive_IsUntouched()
        {
            var match = NewMatch();
            var crystal = Crystal(match);
            SetDefenders(Defender(match), Defender(match));

            Assert.AreEqual(100u, crystal.TakeDamage(SeededPlayer(Bound(match, Attackers, PvpMatchState.Countdown)), DamageType.Slash, 100f));
        }

        /// <summary>
        /// The kill chip goes through the same Creature.TakeDamage with the killer, an attacker, as its source, so without the exemption it
        /// would be cut by the engaged defenders. Control first: the same attacker's ordinary hit IS cut in the same setup.
        /// </summary>
        [TestMethod]
        public void TheKillChip_IsNeverReduced()
        {
            var match = NewMatch();
            var crystal = Crystal(match);
            var killer = SeededPlayer(Bound(match, Attackers));

            SetDefenders(Defender(match), Defender(match), Defender(match), Defender(match));

            Assert.AreEqual(5u, crystal.TakeDamage(killer, DamageType.Slash, 10f), "control: an ordinary 10-point hit loses half");

            var before = crystal.Health.Current;

            Assert.IsNotNull(LivePvpMatchSpaces.ApplyKillEffect(crystal, new CrystalKillEffect(0, true, 1.0), killer));

            Assert.AreEqual(before - 10u, crystal.Health.Current, "the 1% chip (10 of 1000) lands whole");
            Assert.IsFalse(crystal.BattlegroundKillChipActive, "the exemption is cleared after the chip");
            Assert.AreEqual(5u, crystal.TakeDamage(killer, DamageType.Slash, 10f), "and the next ordinary hit is reduced again");
        }

        // ================= source pins =================

        private static string RepoFile(params string[] relative)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Source", "property-registry.tsv")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, "could not find the repo root by walking up from the test directory");

            return File.ReadAllText(Path.Combine(new[] { dir.FullName, "Source", "ACE.Server" }.Concat(relative).ToArray())).Replace("\r\n", "\n");
        }

        private static string MethodBody(string text, string signature)
        {
            var start = text.IndexOf(signature, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"{signature} not found");
            var end = text.IndexOf("\n        }\n", start, StringComparison.Ordinal);
            Assert.IsTrue(end > start, $"the end of {signature} not found");
            return text.Substring(start, end - start);
        }

        private const string SpellSinkCall = "damage = target.ApplyBattlegroundDefenderReduction(ProjectileSource, damage);";

        private const string SpellHealthWrite = "amount = (uint)-target.UpdateVitalDelta(target.Health, (int)-Math.Round(damage));";

        /// <summary>Null when SpellProjectile.DamageTarget applies the reduction to the health branch BEFORE its health write.</summary>
        private static string SpellSinkFault(string body)
        {
            var call = body.IndexOf(SpellSinkCall, StringComparison.Ordinal);
            var write = body.IndexOf(SpellHealthWrite, StringComparison.Ordinal);

            if (write < 0)
                return "the health write was not found (the pin needs updating)";

            if (call < 0)
                return "DamageTarget does not apply the engaged-defender reduction";

            if (call > write)
                return "the reduction runs after the health write";

            return null;
        }

        [TestMethod]
        public void TheSpellProjectileSink_ReducesBeforeTheHealthWrite()
        {
            var body = MethodBody(RepoFile("WorldObjects", "SpellProjectile.cs"), "public void DamageTarget(Creature target, float damage, bool critical, bool critDefended, bool overpower)");

            Assert.IsNull(SpellSinkFault(body));
            Assert.IsNotNull(SpellSinkFault(body.Replace(SpellSinkCall, "")), "removing the call must fail the check");
            Assert.IsNotNull(SpellSinkFault(body.Replace(SpellSinkCall, "").Replace(SpellHealthWrite, SpellHealthWrite + "\n" + SpellSinkCall)), "moving it after the write must fail the check");
        }

        private const string HistoryHook = "Player.OnPvpDamageRecorded(Creature, attacker);";

        /// <summary>
        /// Null when DamageHistory.Add stamps engagement AFTER its zero-amount early return (a fully absorbed hit is not damage) and once.
        /// </summary>
        private static string HistoryFault(string body)
        {
            var zero = body.IndexOf("if (amount == 0)", StringComparison.Ordinal);
            var hook = body.IndexOf(HistoryHook, StringComparison.Ordinal);

            if (zero < 0)
                return "the zero-amount branch was not found (the pin needs updating)";

            if (hook < 0)
                return "DamageHistory.Add does not record PvP engagement";

            if (hook < zero || body.IndexOf(HistoryHook, hook + 1, StringComparison.Ordinal) >= 0)
                return "the engagement hook runs for a zero hit, or twice";

            return null;
        }

        [TestMethod]
        public void DamageHistoryAdd_RecordsEngagement_ForEveryDamagingWrite()
        {
            var body = MethodBody(RepoFile("Entity", "DamageHistory.cs"), "public void Add(WorldObject attacker, DamageType damageType, uint amount)");

            Assert.IsNull(HistoryFault(body));
            Assert.IsNotNull(HistoryFault(body.Replace(HistoryHook, "")), "removing the hook must fail the check");
            Assert.IsNotNull(HistoryFault(body.Replace("            if (amount == 0)", "            " + HistoryHook + "\n            if (amount == 0)")), "a hook before the zero return must fail the check");
        }

        /// <summary>The coordinator puts the formation snapshot on the match, next to the crystal sequence, so the sink can read it.</summary>
        [TestMethod]
        public void TheCoordinator_CarriesTheFormationSnapshotOnTheMatch()
        {
            var text = RepoFile("Pvp", "PvpMatchCoordinator.cs");
            var body = MethodBody(text, "private AttackDefendPlan BindAttackDefendPlan(MatchRuntime m, BattlegroundDials d, bool sidesSwapped)");

            StringAssert.Contains(body, "m.Match.DefenderReduction = CrystalDefenderReductionDials.From(d);");
        }
    }
}
