using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Objectives;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The 2026-10-04 reward-credit rule (TECH-DESIGN 2.6): a player who deals ANY damage to, or takes ANY
    /// damage from, a run creature while the run is Active gets a participation record - and therefore the
    /// ordinary crate rather than the booby prize - through the single hook in DamageHistory.Add.
    ///
    /// The end-to-end tests drive the real DamageHistory.Add, so they prove the call site reaches the hook,
    /// not only that the hook works when called. Players cannot be constructed in this test tree (no
    /// Session, no database), so they are bare-reflection objects seeded with exactly the fields the
    /// recording path reads: Guid, Biota and the biota lock. A player VICTIM goes through the hook line directly;
    /// see PlayerTakesDamage for why.
    /// </summary>
    [TestClass]
    public class WorldEventCombatCreditTests
    {
        private const double T0 = 1_700_000_000d;

        private static uint nextGuid = 0x50F00000;

        // ---- builders ----------------------------------------------------------------------------------

        private sealed class OpenObjective : IWorldEventObjective
        {
            public bool IsComplete => false;

            public string ProgressText => "open";

            public void OnCreatureDied(Creature creature, DamageHistoryInfo lastDamager, DamageHistoryInfo topDamager)
            {
            }

            public void Tick(double now)
            {
            }

            public WorldEventMvp Mvp() => WorldEventMvp.None;
        }

        private static WorldEventComposition BuildComposition()
        {
            var source = new SourceThemeDef
            {
                Id = "ambush",
                DisplayName = "Ambush",
                Geometry = "edges",
                GeometryKind = SourceGeometry.Edges,
                GeometryRadius = 45f,
                GeometryPoints = 3,
                WaveIntervalSeconds = 45,
                MaxAlive = 24,
                WaveCount = new ScaledCount { Base = 4, PerParticipant = 1.5, Cap = 18 },
                HoldAdjacentLandblocks = false,
                RewardRadius = 60f,
                CompatibleGoals = new List<string> { "kill_count" }
            };

            var goal = new GoalDef
            {
                Id = "kill_count",
                DisplayName = "Kill Count",
                Type = "KillCount",
                TypeKind = GoalType.KillCount,
                MvpRule = "mostKills",
                RuleKind = ACE.Server.WorldEvents.Defs.MvpRule.MostKills,
                Count = new ScaledCount { Base = 20, PerParticipant = 6, Cap = 150 },
                ProgressTemplate = "{killed} of {target} slain."
            };

            var reward = new RewardDef
            {
                Id = "standard",
                DisplayName = "Hammer Crate",
                SuccessCrateWcid = 1002600,
                ConsolationCrateWcid = 1002601,
                CacheWcid = 1002602,
                ParticipantsPerCache = 8,
                ClaimWindowSeconds = 120,
                GateByCharacter = true,
                GateByAccount = true,
                GateByIp = true
            };

            var family = new FamilyDef
            {
                Id = "emberwrought",
                DisplayName = "the Emberwrought",
                HueKey = "ember",
                BiomeTags = new List<string> { "volcanic" },
                Members = new List<FamilyMember>
                {
                    new FamilyMember { Wcid = 1002604, Name = "Emberwrought Thrall", Level = 20, Role = 0 }
                }
            };

            var anchor = new AnchorDef
            {
                Id = "here",
                DisplayName = "the test anchor",
                CellId = 0x016C019E,
                BiomeTags = new List<string>()
            };

            return new WorldEventComposition(source, new[] { family }, BossDef.None, goal, reward, anchor,
                new Position(0x016C019E, 10f, 10f, 0f, 0f, 0f, 0f, 1f, 0), WorldEventAxisStore.Empty);
        }

        /// <summary>A run in Idle (built, never staged). <see cref="ToActive"/> moves it on.</summary>
        private static WorldEvent BuildEvent()
        {
            var request = new WorldEventRequest
            {
                SourceId = "ambush",
                FamilyId = "emberwrought",
                GoalId = "kill_count",
                AnnounceLeadSeconds = 30,
                Invoker = "test"
            };

            return new WorldEvent(1, BuildComposition(), request, new OpenObjective(),
                () => new AudienceEstimate(3, 100, 150), () => T0);
        }

        private static WorldEvent ToAnnounced(WorldEvent evt)
        {
            evt.Stage(T0);
            Assert.AreEqual(WorldEventState.Announced, evt.State, "sanity: Stage must reach Announced");
            return evt;
        }

        private static WorldEvent ToActive(WorldEvent evt)
        {
            ToAnnounced(evt);
            evt.Tick(T0 + 30);
            Assert.AreEqual(WorldEventState.Active, evt.State, "sanity: the announce lead must reach Active");
            return evt;
        }

        private static WorldEvent ActiveEvent() => ToActive(BuildEvent());

        private static Creature MakeCreature(WorldEvent run)
        {
            var wcid = 0x7E000000u + (nextGuid & 0xFFFF);

            var weenie = new Weenie
            {
                WeenieClassId = wcid,
                ClassName = $"combatcredittest{wcid}",
                WeenieType = WeenieType.Creature,
                PropertiesBool = new Dictionary<PropertyBool, bool> { { PropertyBool.Attackable, true } },
                PropertiesInt = new Dictionary<PropertyInt, int>(),
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Combat Credit Test Creature" } }
            };

            TestGameTables.EnsureInitialized();   // Creature's ctor reads the vital formula tables

            var creature = new Creature(weenie, new ObjectGuid(nextGuid++));
            creature.P_WorldEvent = run;
            return creature;
        }

        private static void SetField(object target, Type declaring, string name, object value)
        {
            var field = declaring.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, $"{declaring.Name}.{name} was not found by reflection - has it been renamed?");
            field.SetValue(target, value);
        }

        /// <summary>
        /// A bare-reflection WorldObject of type <typeparamref name="T"/> with a Guid, a Biota carrying a
        /// Name, and the biota lock every property read takes - the fields DamageHistoryInfo's constructor
        /// and the ledger's identity capture read.
        /// </summary>
        private static T MakeBare<T>(string name) where T : WorldObject
        {
            var wo = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

            SetField(wo, typeof(WorldObject), "<Guid>k__BackingField", new ObjectGuid(nextGuid++));
            SetField(wo, typeof(WorldObject), "<Biota>k__BackingField", new ACE.Entity.Models.Biota
            {
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, name } }
            });
            SetField(wo, typeof(WorldObject), "BiotaDatabaseLock", new ReaderWriterLockSlim());

            return wo;
        }

        private static Player MakePlayer(string name) => MakeBare<Player>(name);

        /// <summary>
        /// Records <paramref name="amount"/> of damage from <paramref name="attacker"/> against a PLAYER victim,
        /// exactly as DamageHistory.Add does once it has logged the entry: the one hook line, with the victim
        /// as `Creature`. A bare-reflection Player cannot run the rest of DamageHistory.Add - DamageHistoryEntry
        /// reads Health.MaxValue, which walks the attribute formula, gear and enchantment manager - so the
        /// player-victim tests call the hook the way that line does, and the creature-victim tests above prove
        /// the line itself is reached.
        /// </summary>
        private static void PlayerTakesDamage(Player victim, WorldObject attacker, uint amount)
            => WorldEventCombatCreditHook.OnDamageRecorded(victim, attacker, amount);
        private static CombatPet MakePet(Player owner)
        {
            var pet = MakeBare<CombatPet>("Test Pet");
            pet.P_PetOwner = owner;
            return pet;
        }

        private static bool HasRecord(WorldEvent evt, WorldObject player) =>
            evt.Participation.TryGetRecord(player.Guid.Full, out _);

        // ---- hit-time credit, both directions, through the real DamageHistory.Add -------------------------

        [TestMethod]
        public void HitButNoKill_PlayerWhoDamagedARunCreature_IsCredited()
        {
            var evt = ActiveEvent();
            var creature = MakeCreature(evt);
            var player = MakePlayer("Hitter");

            // DamageHistory.Add only RECORDS the hit; the creature is never killed and the death path never runs
            creature.DamageHistory.Add(player, DamageType.Slash, 25);

            Assert.IsTrue(evt.Participation.TryGetRecord(player.Guid.Full, out var rec),
                "a player who hit a run creature that never died must have a record");
            Assert.IsTrue(WorldEventParticipation.HasCreditOrUnknown(evt.Participation, player.Guid.Full),
                "and so must be paid the ordinary crate, not coal");
            Assert.AreEqual(0f, rec.Damage, "the hit-time credit records no damage - that stays with the death walk");
            Assert.AreEqual(0, rec.Kills);
            Assert.AreEqual("Hitter", rec.Name);
        }

        [TestMethod]
        public void TookDamageOnly_PlayerHurtByARunCreature_IsCredited()
        {
            var evt = ActiveEvent();
            var creature = MakeCreature(evt);
            var victim = MakePlayer("Tank");

            PlayerTakesDamage(victim, creature, 40);

            Assert.IsTrue(evt.Participation.TryGetRecord(victim.Guid.Full, out var rec),
                "a player who only TOOK damage from a run creature must have a record");
            Assert.IsTrue(rec.IsContactOnly);
            Assert.AreEqual(1, evt.Participation.Count, "the creature itself is never credited");
        }

        [TestMethod]
        public void PetDamage_CreditsThePetsOwner()
        {
            var evt = ActiveEvent();
            var creature = MakeCreature(evt);
            var owner = MakePlayer("Summoner");
            var pet = MakePet(owner);

            creature.DamageHistory.Add(pet, DamageType.Cold, 12);

            Assert.IsTrue(HasRecord(evt, owner), "pet damage resolves to the owner, as the death walk does");
            Assert.IsFalse(evt.Participation.TryGetRecord(pet.Guid.Full, out _), "the pet itself is never a participant");
        }

        [TestMethod]
        public void RepeatHits_CreateOneRecord()
        {
            var evt = ActiveEvent();
            var creature = MakeCreature(evt);
            var player = MakePlayer("Repeat");

            for (var i = 0; i < 5; i++)
                creature.DamageHistory.Add(player, DamageType.Slash, 10);

            Assert.AreEqual(1, evt.Participation.Count);
        }

        /// <summary>
        /// OnDamageRecorded is the AMOUNT entry and ignores 0 - since the 2026-10-04 follow-up ruling a
        /// landed-for-zero hit is credited through OnCombatContact instead, which DamageHistory.Add calls from
        /// its own zero branch. So the two entries never both fire for one hit.
        /// </summary>
        [TestMethod]
        public void ZeroAmount_GoesThroughContact_NotTheAmountEntry()
        {
            var evt = ActiveEvent();
            var creature = MakeCreature(evt);
            var player = MakePlayer("Absorbed");

            WorldEventCombatCreditHook.OnDamageRecorded(creature, player, 0);
            Assert.AreEqual(0, evt.Participation.Count, "the amount entry ignores 0");

            creature.DamageHistory.Add(player, DamageType.Slash, 0);
            Assert.AreEqual(1, evt.Participation.Count, "DamageHistory.Add's zero branch credits it as contact");
            Assert.AreEqual(0, creature.DamageHistory.Log.Count, "and logs nothing, exactly as before");
        }

        // ---- what does NOT credit ----------------------------------------------------------------------

        [TestMethod]
        public void DamageFromANonRunCreature_IsNotCredit()
        {
            var evt = ActiveEvent();
            var stranger = MakeCreature(null);
            var victim = MakePlayer("Bystander");

            PlayerTakesDamage(victim, stranger, 30);

            Assert.AreEqual(0, evt.Participation.Count, "a creature with no P_WorldEvent is not the run's");
        }

        [TestMethod]
        public void HittingANonRunCreature_IsNotCredit()
        {
            var evt = ActiveEvent();
            var stranger = MakeCreature(null);
            var player = MakePlayer("Elsewhere");

            stranger.DamageHistory.Add(player, DamageType.Slash, 30);

            Assert.AreEqual(0, evt.Participation.Count);
        }

        [TestMethod]
        public void DamageFromAnotherPlayer_IsNotCredit()
        {
            var evt = ActiveEvent();
            var attacker = MakePlayer("Griefer");
            var victim = MakePlayer("Target");

            PlayerTakesDamage(victim, attacker, 30);

            Assert.AreEqual(0, evt.Participation.Count, "player-on-player damage is never run credit");
        }

        [TestMethod]
        public void SelfInflictedDamage_IsNotCredit()
        {
            var evt = ActiveEvent();
            var victim = MakePlayer("Faller");

            // falling (Player_Move) and a negative heal-over-time both record the player as its own attacker
            PlayerTakesDamage(victim, victim, 30);

            Assert.AreEqual(0, evt.Participation.Count);
        }

        [TestMethod]
        public void RunCreatureHurtingRunCreature_IsNotCredit()
        {
            var evt = ActiveEvent();
            var a = MakeCreature(evt);
            var b = MakeCreature(evt);

            a.DamageHistory.Add(b, DamageType.Slash, 30);

            Assert.AreEqual(0, evt.Participation.Count);
        }

        // ---- the state gate ----------------------------------------------------------------------------

        [TestMethod]
        public void CombatCreditStates_AreActiveOnly()
        {
            foreach (WorldEventState state in Enum.GetValues(typeof(WorldEventState)))
                Assert.AreEqual(state == WorldEventState.Active, WorldEvent.IsCombatCreditState(state), state.ToString());
        }

        [TestMethod]
        public void NoCredit_BeforeTheRunIsActive()
        {
            var idle = BuildEvent();
            var idleCreature = MakeCreature(idle);
            var p1 = MakePlayer("Early1");

            idleCreature.DamageHistory.Add(p1, DamageType.Slash, 10);
            Assert.AreEqual(0, idle.Participation.Count, "Idle (the teaser) is not the fight");

            var announced = ToAnnounced(BuildEvent());
            var announcedCreature = MakeCreature(announced);
            var p2 = MakePlayer("Early2");

            announcedCreature.DamageHistory.Add(p2, DamageType.Slash, 10);
            Assert.AreEqual(0, announced.Participation.Count, "Announced is not the fight");

            // and the same run, once Active, does credit - so the gate, not the setup, is what refused above
            announced.Tick(T0 + 30);
            Assert.AreEqual(WorldEventState.Active, announced.State, "sanity");
            announcedCreature.DamageHistory.Add(p2, DamageType.Slash, 10);
            Assert.AreEqual(1, announced.Participation.Count, "positive control: Active credits");
        }

        [TestMethod]
        public void NoCredit_OnceTheRunIsRewarding()
        {
            var evt = ActiveEvent();

            // Not adopted by the spawner, so Finish's creature pass does NOT null this back-reference: the
            // state gate is the only thing that can refuse here.
            var straggler = MakeCreature(evt);
            var player = MakePlayer("Late");

            evt.Finish(WorldEventOutcome.Success);
            Assert.AreEqual(WorldEventState.Rewarding, evt.State, "sanity");
            Assert.AreSame(evt, straggler.P_WorldEvent, "sanity: the back-reference is still live");

            straggler.DamageHistory.Add(player, DamageType.Slash, 10);
            Assert.IsFalse(evt.CreditCombatContact(player.Guid.Full, 0, null, "Late"));

            Assert.AreEqual(0, evt.Participation.Count, "nothing credits during the claim window");
        }

        // ---- MVP and top damage are not distorted ------------------------------------------------------

        [TestMethod]
        public void ContactOnlyRecords_NeverBecomeTopKillerOrTopDamager()
        {
            var clock = T0;
            var ledger = new WorldEventParticipation(() => clock);

            ledger.CreditCombatContact(1001, 0, null, "Hurt");
            ledger.CreditCombatContact(1002, 0, null, "AlsoHurt");

            Assert.IsNull(ledger.TopKiller(), "a ledger of nothing but contact credits has no top killer");
            Assert.IsNull(ledger.TopDamager());

            var mvp = WorldEventMvpResolver.MostKills(ledger);
            Assert.IsTrue(string.IsNullOrEmpty(mvp.Name), "and the mostKills MVP stays empty, exactly as for an empty ledger");

            clock = T0 + 10;
            ledger.CreditDamageForTest(2001, 5f);

            Assert.AreEqual(2001u, ledger.TopKiller().CharacterGuid, "a real damager outranks contact-only records at 0 kills");
            Assert.AreEqual(2001u, ledger.TopDamager().CharacterGuid);
            Assert.AreEqual(3, ledger.Count, "but every contact credit still counts as a participant");
        }

        [TestMethod]
        public void BeingHitEarly_DoesNotWinAKillTie()
        {
            var clock = T0;
            var ledger = new WorldEventParticipation(() => clock);

            // A is hit at T0, long before anything dies...
            ledger.CreditCombatContact(1001, 0, null, "HitEarly");

            // ...B lands the first kill at T0+10, A matches it at T0+20.
            clock = T0 + 10;
            ledger.CreditKillForTest(1002, "KilledFirst");
            clock = T0 + 20;
            ledger.CreditKillForTest(1001, "HitEarly");

            Assert.AreEqual("KilledFirst", ledger.TopKiller().Name,
                "the tie-break is the first DAMAGE OR KILL credit; a hit-time contact credit must not move it");
            ledger.TryGetRecord(1001, out var a);
            Assert.AreEqual(T0 + 20, a.FirstCredit, "FirstCredit is A's first KILL, not the earlier contact");
        }

        [TestMethod]
        public void ContactCredit_ThenDeathWalk_DamageIsCountedExactlyOnce()
        {
            var evt = ActiveEvent();
            var creature = MakeCreature(evt);
            var player = MakePlayer("Finisher");

            creature.DamageHistory.Add(player, DamageType.Slash, 30);
            creature.DamageHistory.Add(player, DamageType.Slash, 20);

            // the death path, exactly as WorldEvent.OnCreatureDied runs it
            evt.Participation.CreditDamage(creature);
            evt.Participation.CreditKill(creature.DamageHistory.LastDamager);

            Assert.IsTrue(evt.Participation.TryGetRecord(player.Guid.Full, out var rec));
            Assert.AreEqual(50f, rec.Damage, "the hit-time credit added nothing; the walk added the 50 once");
            Assert.AreEqual(1, rec.Kills);
            Assert.AreEqual(1, evt.Participation.Count);
            Assert.AreEqual(player.Guid.Full, evt.Participation.TopDamager().CharacterGuid);
        }

        [TestMethod]
        public void ContactCredit_OnAnExistingRecord_ChangesNothing()
        {
            var clock = T0;
            var ledger = new WorldEventParticipation(() => clock);

            ledger.CreditDamageForTest(1001, 40f);
            clock = T0 + 5;

            Assert.IsFalse(ledger.CreditCombatContact(1001, 0, null, "X"), "no new record for a known guid");
            ledger.TryGetRecord(1001, out var rec);
            Assert.AreEqual(40f, rec.Damage);
            Assert.AreEqual(T0, rec.FirstCredit);
        }

        /// <summary>
        /// Each reader is checked ON ITS OWN, so a single reader that leaks the live record fails here even if
        /// the others clone correctly: mutate what that one reader handed out, then read back through a
        /// different reader.
        /// </summary>
        [TestMethod]
        public void ReadersHandBackClones_NotTheLiveRecord()
        {
            var readers = new (string name, Func<WorldEventParticipation, ParticipantRecord> read)[]
            {
                ("TryGetRecord", l => { l.TryGetRecord(1001, out var r); return r; }),
                ("Records", l => l.Records.First()),
                ("TopDamager", l => l.TopDamager()),
                ("TopKiller", l => l.TopKiller()),
            };

            foreach (var (name, read) in readers)
            {
                var ledger = new WorldEventParticipation(() => T0);
                ledger.CreditDamageForTest(1001, 10f);
                ledger.CreditKillForTest(1001);

                var handed = read(ledger);
                Assert.IsNotNull(handed, $"sanity: {name} must return the record");
                handed.Damage = 9999f;
                handed.Kills = 9999;

                var fresh = name == "Records" ? ledger.TopKiller() : ledger.Records.First();
                Assert.AreEqual(10f, fresh.Damage, $"{name}: a caller mutating what it was handed must not reach the ledger");
                Assert.AreEqual(1, fresh.Kills, $"{name}: kills must be untouched too");
            }
        }
        [TestMethod]
        public void ContactOnlyPlayerWhoIsNotOnline_DoesNotHoldTheRunOpen()
        {
            var ledger = new WorldEventParticipation(() => T0);
            ledger.CreditCombatContact(0x50FFFFF0, 0, null, "LeftAlready");

            Assert.AreEqual(0, ledger.AliveNear(new Position(0x016C019E, 10f, 10f, 0f, 0f, 0f, 0f, 1f, 0), 60f),
                "AliveNear still filters online/alive/near, so a hit-and-gone player is not a live participant");
        }

        // ---- the hot path: an existing record costs no identity capture --------------------------------

        [TestMethod]
        public void RepeatHitsByAKnownPlayer_TakeTheFastPath()
        {
            var clockCalls = 0;
            var evt = new WorldEvent(1, BuildComposition(), new WorldEventRequest
            {
                SourceId = "ambush", FamilyId = "emberwrought", GoalId = "kill_count", AnnounceLeadSeconds = 30, Invoker = "test"
            }, new OpenObjective(), () => new AudienceEstimate(3, 100, 150), () => { clockCalls++; return T0; });

            ToActive(evt);

            var creature = MakeCreature(evt);
            var player = MakePlayer("Regular");

            // the first hit creates the record
            WorldEventCombatCreditHook.OnDamageRecorded(creature, player, 10);
            Assert.AreEqual(1, evt.Participation.Count, "sanity: the first hit credits");

            var clockBefore = clockCalls;
            var capturesBefore = evt.Participation.ContactCapturesForTest;

            for (var i = 0; i < 10_000; i++)
                WorldEventCombatCreditHook.OnDamageRecorded(creature, player, 10);

            Assert.IsTrue(clockCalls - clockBefore <= 1, $"10,000 hits by a known player must call the clock at most once (called {clockCalls - clockBefore})");
            Assert.AreEqual(capturesBefore, evt.Participation.ContactCapturesForTest,
                "a known player must never get past the existing-record probe to an identity capture");
            Assert.AreEqual(1, evt.Participation.Count);
        }

        // ---- a victim who never actually takes the damage ----------------------------------------------

        [TestMethod]
        public void VictimStateRefusesCredit_TruthTable()
        {
            Assert.IsFalse(WorldEventCombatCreditHook.VictimStateRefusesCredit(false, false));
            Assert.IsTrue(WorldEventCombatCreditHook.VictimStateRefusesCredit(true, false), "Invincible");
            Assert.IsTrue(WorldEventCombatCreditHook.VictimStateRefusesCredit(false, true), "lifestone protection");
            Assert.IsTrue(WorldEventCombatCreditHook.VictimStateRefusesCredit(true, true));
        }

        /// <summary>
        /// The DoT over-credit: ApplyDamageTick records each tick in DamageHistory BEFORE
        /// Player.TakeDamageOverTime discards it for these two states. The hook must refuse them. Each case
        /// runs against a fresh run, and a positive control on the same player with the flag cleared proves
        /// the flag, not the setup, is what refused.
        /// </summary>
        [TestMethod]
        [DataRow("Invincible")]
        [DataRow("UnderLifestoneProtection")]
        public void PlayerHurtWhileTheHitCannotLand_IsNotCredited(string state)
        {
            var evt = ActiveEvent();
            var creature = MakeCreature(evt);
            var victim = MakePlayer("Protected");

            if (state == "Invincible")
                victim.Invincible = true;
            else
                victim.UnderLifestoneProtection = true;

            PlayerTakesDamage(victim, creature, 25);
            Assert.AreEqual(0, evt.Participation.Count, $"{state}: the recorded tick never landed, so it is not contact");

            if (state == "Invincible")
                victim.Invincible = false;
            else
                victim.UnderLifestoneProtection = false;

            PlayerTakesDamage(victim, creature, 25);
            Assert.AreEqual(1, evt.Participation.Count, "positive control: the same player is credited once the state clears");
        }

        // ---- pets ---------------------------------------------------------------------------------------

        [TestMethod]
        public void OwnerlessPet_IsNotCreditedAndDoesNotThrow()
        {
            var evt = ActiveEvent();
            var creature = MakeCreature(evt);
            var pet = MakePet(null);

            creature.DamageHistory.Add(pet, DamageType.Cold, 12);
            WorldEventCombatCreditHook.OnDamageRecorded(creature, pet, 12);

            Assert.AreEqual(0, evt.Participation.Count, "a pet with no owner credits nobody");
        }

        // ---- the call site exists ------------------------------------------------------------------------

        private static readonly System.Text.RegularExpressions.Regex HookCall = new System.Text.RegularExpressions.Regex(
            @"\bWorldEventCombatCreditHook\s*\.\s*OnDamageRecorded\s*\(\s*Creature\s*,\s*attacker\s*,\s*amount\s*\)\s*;");

        /// <summary>Removes // line comments and /* */ block comments, so a commented-out call cannot match.</summary>
        private static string StripComments(string source)
        {
            var noBlock = System.Text.RegularExpressions.Regex.Replace(source, @"/\*.*?\*/", "", System.Text.RegularExpressions.RegexOptions.Singleline);
            return System.Text.RegularExpressions.Regex.Replace(noBlock, @"//[^\n]*", "");
        }

        /// <summary>The body of DamageHistory.Add(WorldObject, DamageType, uint): from its signature to the next member's.</summary>
        private static string AddMethodBody(string source)
        {
            var start = source.IndexOf("public void Add(WorldObject attacker, DamageType damageType, uint amount)", StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, "DamageHistory.Add(WorldObject, DamageType, uint) was not found - has it been renamed?");

            var end = source.IndexOf("private void AddInternal", start, StringComparison.Ordinal);
            Assert.IsTrue(end > start, "the member after DamageHistory.Add was not found");

            return source.Substring(start, end - start);
        }

        private static bool AddCallsTheHook(string damageHistorySource) =>
            HookCall.IsMatch(AddMethodBody(StripComments(damageHistorySource)));

        [TestMethod]
        public void DamageHistoryAdd_CallsTheCombatCreditHook()
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            string path = null;

            while (dir != null && path == null)
            {
                var candidate = System.IO.Path.Combine(dir.FullName, "Source", "ACE.Server", "Entity", "DamageHistory.cs");

                if (System.IO.File.Exists(candidate))
                    path = candidate;

                dir = dir.Parent;
            }

            Assert.IsNotNull(path, $"Could not find Source/ACE.Server/Entity/DamageHistory.cs by walking up from {AppContext.BaseDirectory}");

            var source = System.IO.File.ReadAllText(path);

            Assert.IsTrue(AddCallsTheHook(source),
                "DamageHistory.Add must call WorldEventCombatCreditHook.OnDamageRecorded(Creature, attacker, amount) - it is the single site the World Events contact credit hangs off");

            // The matcher discriminates: the same file with the call commented out, or with a different
            // victim argument, must NOT pass.
            var commentedOut = HookCall.Replace(source, m => "// " + m.Value, 1);
            Assert.IsFalse(AddCallsTheHook(commentedOut), "a commented-out call must not satisfy the scan");

            var wrongVictim = HookCall.Replace(source, "ACE.Server.WorldEvents.WorldEventCombatCreditHook.OnDamageRecorded(attacker, attacker, amount);", 1);
            Assert.IsFalse(AddCallsTheHook(wrongVictim), "a call with a different victim argument must not satisfy the scan");
        }

        // ---- 2026-10-04 follow-up: absorbed hits and stamina/mana drains are contact -------------------

        /// <summary>A monster ward that eats every point - the fully-absorbed case at its real sink.</summary>
        private sealed class AbsorbAllWard : ACE.Server.MonsterEffects.IMonsterIncomingDamage, ACE.Server.MonsterEffects.IMonsterAvoidance
        {
            public string Kind => "testabsorball";

            public double AvoidChance;

            public bool Validate(ACE.Server.MonsterEffects.MonsterEffectSpec spec, out string error)
            {
                error = null;
                return true;
            }

            public uint OnIncomingDamage(Creature defender, WorldObject source, DamageType damageType, uint amount,
                ACE.Server.MonsterEffects.IncomingDamageOrigin origin, ACE.Server.MonsterEffects.MonsterEffectSpec spec,
                ref ACE.Server.MonsterEffects.MonsterEffectState state) => 0;

            public double GetAvoidChance(Creature defender, Creature attacker, CombatType combatType,
                ACE.Server.MonsterEffects.MonsterEffectSpec spec, ref ACE.Server.MonsterEffects.MonsterEffectState state) => AvoidChance;

            public void OnAvoided(Creature defender, Creature attacker, ACE.Server.MonsterEffects.MonsterEffectSpec spec,
                ref ACE.Server.MonsterEffects.MonsterEffectState state)
            {
            }
        }

        /// <summary>A run creature with real health and the absorb-everything ward attached.</summary>
        private static Creature MakeWardedRunCreature(WorldEvent run, AbsorbAllWard ward)
        {
            ACE.Server.Managers.DefaultPropertyManager.LoadDefaultProperties();   // monster_effects_enabled

            var creature = TestCreatures.CreateDefender(maxHealth: 500);
            creature.AttachMonsterEffectsForTest(ACE.Server.MonsterEffects.MonsterEffectSet.BuildFromHandlers(
                (ward, new ACE.Server.MonsterEffects.MonsterEffectSpec(ward.Kind, new Dictionary<string, string>()))));
            creature.P_WorldEvent = run;
            return creature;
        }

        [TestMethod]
        public void FullyAbsorbedHit_PlayerOnRunCreature_IsCredited()
        {
            var evt = ActiveEvent();
            var creature = MakeWardedRunCreature(evt, new AbsorbAllWard());
            var player = MakePlayer("Warded");
            var before = creature.Health.Current;

            // the real sink: Creature.TakeDamage runs the ward, then records 0 in DamageHistory.Add
            var applied = creature.TakeDamage(player, DamageType.Slash, 50f, false, ACE.Server.MonsterEffects.IncomingDamageOrigin.DirectHit);

            Assert.AreEqual(0u, applied, "sanity: the ward must eat the whole hit");
            Assert.AreEqual(before, creature.Health.Current, "sanity: no health was taken");
            Assert.IsTrue(evt.Participation.TryGetRecord(player.Guid.Full, out var rec), "a landed hit the ward fully absorbed is still contact");
            Assert.AreEqual(0f, rec.Damage);
        }

        [TestMethod]
        public void FullyAbsorbedHit_RunCreatureOnPlayer_IsCredited()
        {
            var evt = ActiveEvent();
            var creature = MakeCreature(evt);
            var victim = MakePlayer("BarrierUp");

            // Player.TakeDamage, SpellProjectile.DamageTarget and the Drain Health branch all record the
            // post-absorb amount in the victim's DamageHistory; Sanguine Ward / Mana Barrier eating it all
            // leaves 0. The zero branch returns before it touches Health, so a bare Player can run it.
            new DamageHistory(victim).Add(creature, DamageType.Fire, 0);

            Assert.IsTrue(HasRecord(evt, victim), "a hit Mana Barrier or Sanguine Ward fully absorbed is still contact");
        }

        [TestMethod]
        public void FullyAbsorbedHit_ByANonRunSource_IsNotCredited()
        {
            var evt = ActiveEvent();
            var stranger = MakeWardedRunCreature(null, new AbsorbAllWard());
            var player = MakePlayer("Elsewhere");

            stranger.TakeDamage(player, DamageType.Slash, 50f, false, ACE.Server.MonsterEffects.IncomingDamageOrigin.DirectHit);
            new DamageHistory(MakePlayer("Bystander")).Add(MakeCreature(null), DamageType.Fire, 0);

            Assert.AreEqual(0, evt.Participation.Count);
        }

        [TestMethod]
        public void FullyAbsorbedHit_OnAProtectedPlayer_IsNotCredited()
        {
            var evt = ActiveEvent();
            var creature = MakeCreature(evt);
            var victim = MakePlayer("Invulnerable");
            victim.Invincible = true;

            new DamageHistory(victim).Add(creature, DamageType.Fire, 0);

            Assert.AreEqual(0, evt.Participation.Count, "the victim-state refusal applies to contact too");
        }

        /// <summary>
        /// An evade never reaches a sink: every melee/missile caller gates its TakeDamage call on
        /// DamageEvent.HasDamage, which is false for an evade, block, parry or lifestone save. This drives a
        /// real evade against a run creature and asserts that gate's input; the gate itself is pinned by
        /// <see cref="MeleeAndMissileSinks_AreGatedOnHasDamage"/>.
        /// </summary>
        [TestMethod]
        public void Evade_HasNoDamage_AndCreditsNothing()
        {
            var evt = ActiveEvent();
            var creature = MakeWardedRunCreature(evt, new AbsorbAllWard { AvoidChance = 1.0 });

            // the pooled avoid chance is clamped by this cap before it is rolled (MonsterEffectDispatchTests)
            Assert.IsTrue(ACE.Server.Managers.PropertyManager.ModifyDouble("monster_effect_avoidance_cap", 1.0));

            try
            {
                var attacker = TestCreatures.CreateAttacker(overpower: false);
                var damageEvent = DamageEvent.CalculateDamage(attacker, creature, null);

                Assert.IsTrue(damageEvent.Evaded, "sanity: a certain avoid must evade");
                Assert.IsFalse(damageEvent.HasDamage, "an evade carries no damage, so no sink is called");
                Assert.AreEqual(0, evt.Participation.Count);
            }
            finally
            {
                ACE.Server.Managers.PropertyManager.ModifyDouble("monster_effect_avoidance_cap",
                    ACE.Server.Managers.DefaultPropertyManager.DefaultDoubleProperties["monster_effect_avoidance_cap"].Item);
            }
        }

        [TestMethod]
        public void StaminaOrManaContact_BothDirections_Credits()
        {
            var evt = ActiveEvent();
            var creature = MakeCreature(evt);
            var caster = MakePlayer("Drainer");
            var victim = MakePlayer("Drained");

            WorldEventCombatCreditHook.OnCombatContact(creature, caster);   // player drains a run creature
            WorldEventCombatCreditHook.OnCombatContact(victim, creature);   // run creature drains a player

            Assert.IsTrue(HasRecord(evt, caster));
            Assert.IsTrue(HasRecord(evt, victim));
            Assert.IsTrue(evt.Participation.Records.All(r => r.IsContactOnly), "contact never moves a damage total");
        }

        [TestMethod]
        public void Contact_FromNonRunOrSelf_DoesNotCredit()
        {
            var evt = ActiveEvent();
            var runCreature = MakeCreature(evt);
            var player = MakePlayer("Self");

            WorldEventCombatCreditHook.OnCombatContact(MakeCreature(null), player);   // non-run victim
            WorldEventCombatCreditHook.OnCombatContact(player, MakeCreature(null));   // non-run attacker
            WorldEventCombatCreditHook.OnCombatContact(player, MakePlayer("Other"));  // player on player
            WorldEventCombatCreditHook.OnCombatContact(player, player);               // a caster-source transfer
            WorldEventCombatCreditHook.OnCombatContact(runCreature, runCreature);     // a run creature's own transfer
            WorldEventCombatCreditHook.OnCombatContact(null, player);
            WorldEventCombatCreditHook.OnCombatContact(runCreature, null);

            Assert.AreEqual(0, evt.Participation.Count);
        }

        [TestMethod]
        public void Contact_OutsideActive_DoesNotCredit()
        {
            var evt = ToAnnounced(BuildEvent());
            WorldEventCombatCreditHook.OnCombatContact(MakeCreature(evt), MakePlayer("Early"));
            Assert.AreEqual(0, evt.Participation.Count);
        }

        /// <summary>
        /// Each contact call site, bound to its enclosing member and with comments stripped, so reverting
        /// any ONE of them fails exactly its own row. The stamina/mana and absorbed-Harm sites need a live
        /// Spell / SpellProjectile to drive, which this project cannot build, so this is their coverage.
        /// </summary>
        [TestMethod]
        [DataRow("Source/ACE.Server/Entity/DamageHistory.cs", "public void Add(WorldObject attacker, DamageType damageType, uint amount)",
            @"if\s*\(\s*amount\s*==\s*0\s*\)\s*\{\s*ACE\.Server\.WorldEvents\.WorldEventCombatCreditHook\.OnCombatContact\(\s*Creature\s*,\s*attacker\s*\)\s*;\s*return\s*;\s*\}", 1)]
        [DataRow("Source/ACE.Server/WorldObjects/SpellProjectile.cs", "public void DamageTarget(Creature target, float damage, bool critical, bool critDefended, bool overpower)",
            @"Spell\.Category\s*==\s*SpellCategory\.StaminaLowering\s*\)\s*\{[^{}]*UpdateVitalDelta\(\s*target\.Stamina[^{}]*WorldEventCombatCreditHook\.OnCombatContact\(\s*target\s*,\s*ProjectileSource\s*\)\s*;\s*\}", 1)]
        [DataRow("Source/ACE.Server/WorldObjects/SpellProjectile.cs", "public void DamageTarget(Creature target, float damage, bool critical, bool critDefended, bool overpower)",
            @"Spell\.Category\s*==\s*SpellCategory\.ManaLowering\s*\)\s*\{[^{}]*UpdateVitalDelta\(\s*target\.Mana[^{}]*WorldEventCombatCreditHook\.OnCombatContact\(\s*target\s*,\s*ProjectileSource\s*\)\s*;\s*\}", 1)]
        [DataRow("Source/ACE.Server/WorldObjects/WorldObject_Magic.cs", "private void HandleCastSpell_Boost(",
            @"if\s*\(\s*harmfulBoost\s*&&\s*\(\s*srcVital\s*!=\s*""health""\s*\|\|\s*boost\s*>=\s*0\s*\)\s*\)\s*ACE\.Server\.WorldEvents\.WorldEventCombatCreditHook\.OnCombatContact\(\s*targetCreature\s*,\s*this\s*\)\s*;", 1)]
        [DataRow("Source/ACE.Server/WorldObjects/WorldObject_Magic.cs", "private void HandleCastSpell_Transfer(",
            @"UpdateVitalDelta\(\s*transferSource\.Mana[^;]*;\s*ACE\.Server\.WorldEvents\.WorldEventCombatCreditHook\.OnCombatContact\(\s*transferSource\s*,\s*this\s*\)\s*;", 1)]
        [DataRow("Source/ACE.Server/WorldObjects/WorldObject_Magic.cs", "private void HandleCastSpell_Transfer(",
            @"UpdateVitalDelta\(\s*transferSource\.Stamina[^;]*;\s*ACE\.Server\.WorldEvents\.WorldEventCombatCreditHook\.OnCombatContact\(\s*transferSource\s*,\s*this\s*\)\s*;", 1)]
        public void ContactCallSite_IsWired(string relativePath, string memberSignature, string pattern, int expected)
        {
            var body = MemberBody(StripComments(ReadSource(relativePath)), memberSignature);

            Assert.AreEqual(expected, System.Text.RegularExpressions.Regex.Matches(body, pattern).Count,
                $"{relativePath} {memberSignature}: the World Events contact call is missing or moved");
        }

        [TestMethod]
        public void HarmfulBoost_IsCapturedBeforeTheAbsorbers()
        {
            var body = MemberBody(StripComments(ReadSource("Source/ACE.Server/WorldObjects/WorldObject_Magic.cs")), "private void HandleCastSpell_Boost(");

            var capture = body.IndexOf("var harmfulBoost = minBoostValue < 0;", StringComparison.Ordinal);
            var roll = body.IndexOf("int tryBoost = ThreadSafeRandom.Next(", StringComparison.Ordinal);

            Assert.IsTrue(capture >= 0 && capture < roll,
                "harmfulBoost must come from the spell's intent (minBoostValue < 0) before the roll, so a harmful roll that rounds to 0 still credits");
            var firstAbsorber = body.IndexOf("ApplyPreWriteDamageClassAbilities", StringComparison.Ordinal);
            var cloak = body.IndexOf("Cloak.GetReducedAmount", StringComparison.Ordinal);

            Assert.IsTrue(capture >= 0, "harmfulBoost must be captured");
            Assert.IsTrue(capture < firstAbsorber && capture < cloak, "harmfulBoost must be captured before the cloak proc and the absorbers can zero tryBoost");
        }

        /// <summary>
        /// The evade exclusion is structural: each landed-hit sink must sit INSIDE the block of an
        /// `if (...)` whose condition tests a NON-negated `damageEvent.HasDamage`. Polarity matters -
        /// ProjectileCollisionHelper also carries `!damageEvent.HasDamage` (the evade branch) right next to a
        /// sink, so a "nearest HasDamage text" scan would pass with the real gate deleted.
        /// </summary>
        [TestMethod]
        public void MeleeAndMissileSinks_AreGatedOnHasDamage()
        {
            var sites = new (string path, string call)[]
            {
                ("Source/ACE.Server/WorldObjects/Player_Combat.cs", "targetPlayer.TakeDamage(this, damageEvent);"),
                ("Source/ACE.Server/WorldObjects/Player_Combat.cs", "target.TakeDamage(this, damageEvent.DamageType, damageEvent.Damage, damageEvent.IsCritical, IncomingDamageOrigin.DirectHit);"),
                ("Source/ACE.Server/WorldObjects/Monster_Melee.cs", "targetPlayer.TakeDamage(this, damageEvent);"),
                ("Source/ACE.Server/WorldObjects/Monster_Melee.cs", "target.TakeDamage(this, damageEvent.DamageType, damageEvent.Damage, false, IncomingDamageOrigin.DirectHit);"),
                ("Source/ACE.Server/WorldObjects/ProjectileCollisionHelper.cs", "targetPlayer.TakeDamage(sourceCreature, damageEvent);"),
                ("Source/ACE.Server/WorldObjects/ProjectileCollisionHelper.cs", "targetCreature.TakeDamage(sourceCreature, damageEvent.DamageType, damageEvent.Damage, false, IncomingDamageOrigin.DirectHit);"),
            };

            foreach (var (path, call) in sites)
            {
                var source = StripComments(ReadSource(path));
                var at = source.IndexOf(call, StringComparison.Ordinal);
                Assert.IsTrue(at >= 0, $"{path}: sink call not found: {call}");

                Assert.IsTrue(IsInsidePositiveHasDamageIf(source, at), $"{path}: {call} is not inside an if-block gated on a non-negated damageEvent.HasDamage");
            }

            // The matcher discriminates: the real file with the pet-sink gate turned into `if (true)` must fail.
            var helper = StripComments(ReadSource("Source/ACE.Server/WorldObjects/ProjectileCollisionHelper.cs"));
            var petSink = "targetCreature.TakeDamage(sourceCreature, damageEvent.DamageType, damageEvent.Damage, false, IncomingDamageOrigin.DirectHit);";
            var petAt = helper.IndexOf(petSink, StringComparison.Ordinal);
            var gateAt = helper.LastIndexOf("if (damageEvent.HasDamage)", petAt, StringComparison.Ordinal);
            Assert.IsTrue(gateAt >= 0, "sanity: the pet-sink gate was found");

            var ungated = helper.Substring(0, gateAt) + "if (true)" + helper.Substring(gateAt + "if (damageEvent.HasDamage)".Length);
            Assert.IsFalse(IsInsidePositiveHasDamageIf(ungated, ungated.IndexOf(petSink, StringComparison.Ordinal)),
                "an `if (true)` in place of the gate must not satisfy the scan");
        }

        /// <summary>
        /// Riposte's counter-swing is the one sink not wrapped in an if-block: it early-returns on
        /// `!damageEvent.HasDamage` instead. Pin that guard ahead of both of its TakeDamage calls.
        /// </summary>
        [TestMethod]
        public void RiposteSinks_ReturnEarlyWithoutHasDamage()
        {
            var body = MemberBody(StripComments(ReadSource("Source/ACE.Server/MonsterEffects/Effects/RiposteEffect.cs")),
                "internal static void DealCounterDamage(");

            var guard = System.Text.RegularExpressions.Regex.Match(body, @"if\s*\(\s*!\s*damageEvent\s*\.\s*HasDamage\s*\)\s*return\s*;");
            Assert.IsTrue(guard.Success, "DealCounterDamage must return when the counter carries no damage");

            foreach (var call in new[]
            {
                "targetPlayer.TakeDamage(counterAttacker, damageEvent);",
                "counterTarget.TakeDamage(counterAttacker, damageEvent.DamageType, damageEvent.Damage, damageEvent.IsCritical, IncomingDamageOrigin.DirectHit);",
            })
            {
                var at = body.IndexOf(call, StringComparison.Ordinal);
                Assert.IsTrue(at > guard.Index, $"Riposte sink must follow the HasDamage guard: {call}");
            }
        }

        private static readonly System.Text.RegularExpressions.Regex PositiveHasDamage =
            new System.Text.RegularExpressions.Regex(@"(?<![!\w.])damageEvent\.HasDamage");

        /// <summary>
        /// True when <paramref name="at"/> lies inside the body of some `if (cond)` whose cond contains a
        /// non-negated damageEvent.HasDamage. The body is the brace-matched block, or the single statement up
        /// to its `;` when the if has no braces.
        /// </summary>
        private static bool IsInsidePositiveHasDamageIf(string source, int at)
        {
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(source.Substring(0, at), @"\bif\s*\("))
            {
                var open = m.Index + m.Length - 1;
                var depth = 0;
                var close = -1;

                for (var i = open; i < source.Length; i++)
                {
                    if (source[i] == '(') depth++;
                    else if (source[i] == ')' && --depth == 0) { close = i; break; }
                }

                if (close < 0 || close > at)
                    continue;

                if (!PositiveHasDamage.IsMatch(source.Substring(open, close - open + 1)))
                    continue;

                var bodyStart = close + 1;
                while (bodyStart < source.Length && char.IsWhiteSpace(source[bodyStart]))
                    bodyStart++;

                int bodyEnd = -1;

                if (source[bodyStart] == '{')
                {
                    depth = 0;

                    for (var i = bodyStart; i < source.Length; i++)
                    {
                        if (source[i] == '{') depth++;
                        else if (source[i] == '}' && --depth == 0) { bodyEnd = i; break; }
                    }
                }
                else
                    bodyEnd = source.IndexOf(';', bodyStart);

                if (bodyStart <= at && bodyEnd > at)
                    return true;
            }

            return false;
        }

        private static string ReadSource(string relativePath)
        {
            var native = relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar);

            for (var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = System.IO.Path.Combine(dir.FullName, native);

                if (System.IO.File.Exists(candidate))
                    return System.IO.File.ReadAllText(candidate);
            }

            Assert.Fail($"Could not find {relativePath} by walking up from {AppContext.BaseDirectory}");
            return null;
        }

        /// <summary>From <paramref name="signature"/> to the matching close brace of its body.</summary>
        private static string MemberBody(string source, string signature)
        {
            var start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"member not found: {signature}");

            var open = source.IndexOf('{', start);
            var depth = 0;

            for (var i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0)
                    return source.Substring(start, i - start + 1);
            }

            Assert.Fail($"unbalanced braces after {signature}");
            return null;
        }

        // ---- concurrency smoke -------------------------------------------------------------------------

        [TestMethod]
        public void ParallelCredits_FromManyThreads_StayConsistent()
        {
            var ledger = new WorldEventParticipation(() => T0);

            const int threads = 8;
            const int guidsPerThread = 2000;
            const int sharedGuids = 50;

            using var stop = new CancellationTokenSource();
            var readerFaults = 0;

            // A concurrent reader doing what the claim handler and the announcer do. Without the ledger lock
            // this enumeration races the writers and throws "Collection was modified".
            var reader = Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    try
                    {
                        _ = ledger.Records.Count;
                        _ = ledger.TopKiller();
                        _ = ledger.TopDamager();
                        _ = ledger.TryGetRecord(1, out _);
                    }
                    catch (Exception)
                    {
                        Interlocked.Increment(ref readerFaults);
                    }
                }
            });

            Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
            {
                for (var i = 0; i < guidsPerThread; i++)
                {
                    var own = (uint)(100_000 + t * guidsPerThread + i);
                    var shared = (uint)(1 + i % sharedGuids);

                    ledger.CreditCombatContact(own, 0, null, null);
                    ledger.CreditCombatContact(shared, 0, null, null);
                    ledger.CreditDamageForTest(shared, 1f);
                }
            });

            stop.Cancel();
            reader.Wait();

            Assert.AreEqual(0, readerFaults, "no reader may fault while writers credit");
            Assert.AreEqual(threads * guidsPerThread + sharedGuids, ledger.Count, "one record per distinct guid, none lost");

            var records = ledger.Records;
            Assert.AreEqual(ledger.Count, records.Count);
            Assert.AreEqual(threads * guidsPerThread, (int)records.Sum(r => r.Damage), "every damage credit landed exactly once");
            Assert.AreEqual(threads * guidsPerThread, records.Count(r => r.IsContactOnly), "the own-guid records stayed contact-only");
        }

        /// <summary>
        /// The Player overload is probe-then-insert across TWO lock acquisitions (Contains, identity capture
        /// outside the lock, then CreditContactCore). Many threads hammering the same small set of real
        /// players races exactly that window: two threads can both see "absent" and both capture. The insert
        /// must still create one record per player, and exactly one call per player may report that it did.
        /// </summary>
        [TestMethod]
        public void ParallelPlayerCredits_ProbeThenInsert_CreateOneRecordPerPlayer()
        {
            var ledger = new WorldEventParticipation(() => T0);

            const int threads = 8;
            const int distinctPlayers = 200;
            const int passes = 50;

            var players = Enumerable.Range(0, distinctPlayers).Select(i => MakePlayer($"Racer{i}")).ToArray();
            var created = new int[distinctPlayers];

            Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
            {
                for (var pass = 0; pass < passes; pass++)
                {
                    // each thread walks the players from a different offset, so threads collide on the
                    // same player at the same moment rather than marching in lockstep
                    for (var k = 0; k < distinctPlayers; k++)
                    {
                        var idx = (k + t * 37) % distinctPlayers;

                        if (ledger.CreditCombatContact(players[idx]))
                            Interlocked.Increment(ref created[idx]);
                    }
                }
            });

            Assert.AreEqual(distinctPlayers, ledger.Count, "record count must equal the number of distinct players");
            Assert.AreEqual(distinctPlayers, players.Select(p => p.Guid.Full).Distinct().Count(), "sanity: the players have distinct guids");

            foreach (var p in players)
                Assert.IsTrue(ledger.TryGetRecord(p.Guid.Full, out _), $"{p.Name} must have a record");

            for (var i = 0; i < distinctPlayers; i++)
                Assert.AreEqual(1, created[i], $"exactly one call may report creating {players[i].Name}'s record");

            Assert.IsTrue(ledger.ContactCapturesForTest >= distinctPlayers, "every player paid for at least one capture");
            Assert.IsTrue(ledger.ContactCapturesForTest < (long)threads * passes * distinctPlayers,
                "the fast path must have absorbed the repeat calls");
        }
    }
}
