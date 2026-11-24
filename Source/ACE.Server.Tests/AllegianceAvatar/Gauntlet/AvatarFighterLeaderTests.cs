using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Avatars.Gauntlet;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldObjects;

using static ACE.Server.Tests.Avatars.Gauntlet.GauntletFighterFixtures;
using C = ACE.Server.Avatars.Gauntlet.GauntletAvatarFighter.LeaderCandidate;

namespace ACE.Server.Tests.Avatars.Gauntlet
{
    /// <summary>
    /// The fighter follows a leader (owner ruling 2026-11-23): the highest-level living participant, then the next when that
    /// one dies or leaves, and nobody when all are down. The pure choice (GauntletAvatarFighter.ChooseLeader), the live
    /// re-check (RefreshLeader, through its Describe seam), and the CombatPet wiring that makes the follow and the summon
    /// assist read the leader. Dat-free; no PropertyManager key is read.
    /// </summary>
    [TestClass]
    public class AvatarFighterLeaderTests
    {
        [ClassInitialize]
        public static void Init(TestContext _) => EnsureInitialized();

        private const uint A = 0x50000001, B = 0x50000002, Cg = 0x50000003;

        // ---- the pure choice ----------------------------------------------------------------------------------------------

        [TestMethod]
        public void Choose_TheHighestLevelLivingPresentParticipant()
        {
            Assert.AreEqual(B, GauntletAvatarFighter.ChooseLeader(new[] { new C(A, 200, true, true), new C(B, 275, true, true), new C(Cg, 150, true, true) }, null));
        }

        [TestMethod]
        public void Choose_Ties_KeepTheCurrentLeader_ElseTheLowestGuid()
        {
            var tied = new[] { new C(B, 275, true, true), new C(A, 275, true, true) };

            Assert.AreEqual(B, GauntletAvatarFighter.ChooseLeader(tied, B), "a tie keeps the current leader");
            Assert.AreEqual(A, GauntletAvatarFighter.ChooseLeader(tied, null), "no current leader: the lowest guid");
            Assert.AreEqual(A, GauntletAvatarFighter.ChooseLeader(tied, Cg), "a current leader outside the tie: the lowest guid");
            Assert.AreEqual(B, GauntletAvatarFighter.ChooseLeader(new[] { new C(A, 200, true, true), new C(B, 275, true, true) }, A),
                "a higher-level member outranks the current leader");
        }

        [TestMethod]
        public void Choose_ALeaderWhoDied_GivesWayToTheNextHighest()
        {
            Assert.AreEqual(A, GauntletAvatarFighter.ChooseLeader(new[] { new C(A, 200, true, true), new C(B, 275, false, true), new C(Cg, 150, true, true) }, B));
        }

        [TestMethod]
        public void Choose_EveryoneDead_NoLeader()
        {
            Assert.IsNull(GauntletAvatarFighter.ChooseLeader(new[] { new C(A, 200, false, true), new C(B, 275, false, true) }, B));
            Assert.IsNull(GauntletAvatarFighter.ChooseLeader(new C[0], null));
            Assert.IsNull(GauntletAvatarFighter.ChooseLeader(null, null));
        }

        [TestMethod]
        public void Choose_AParticipantWhoLeftTheLandblock_IsNeverChosen()
        {
            Assert.AreEqual(A, GauntletAvatarFighter.ChooseLeader(new[] { new C(A, 200, true, true), new C(B, 275, true, false) }, B));
            Assert.IsNull(GauntletAvatarFighter.ChooseLeader(new[] { new C(B, 275, true, false) }, B), "alive but gone is not a leader");
        }

        // ---- the live re-check ----------------------------------------------------------------------------------------------

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<Guid>k__BackingField")]
        private static extern ref ObjectGuid GuidField(WorldObject wo);

        private static Player Member(uint guid)
        {
            var p = BarePlayer();
            GuidField(p) = new ObjectGuid(guid);
            return p;
        }

        private sealed class Table
        {
            public readonly Dictionary<uint, C> Rows = new Dictionary<uint, C>();
            public void Set(uint guid, int level, bool alive = true, bool present = true) => Rows[guid] = new C(guid, level, alive, present);
            public C Describe(Player p) => Rows[p.Guid.Full];
        }

        [TestMethod]
        public void Refresh_FollowsTheTop_SwitchesAtOnceOnADeath_AndLetsGoWhenAllAreDown()
        {
            var fighter = Fighter();
            var a = Member(A);
            var b = Member(B);
            var t = new Table();
            t.Set(A, 200);
            t.Set(B, 275);

            var changes = new List<(Player, Player)>();
            fighter.Describe = t.Describe;
            fighter.Participants = () => new[] { a, b };
            fighter.LeaderChanged = (f, from, to) => changes.Add((from, to));

            Assert.IsTrue(fighter.RefreshLeader(100.0));
            Assert.AreSame(b, fighter.Leader, "the highest level leads");

            t.Set(B, 275, alive: false);
            Assert.IsTrue(fighter.RefreshLeader(100.1), "a dead leader is replaced at once, inside the re-check interval");
            Assert.AreSame(a, fighter.Leader, "the next highest");

            t.Set(A, 200, alive: false);
            Assert.IsTrue(fighter.RefreshLeader(100.2));
            Assert.IsNull(fighter.Leader, "everyone down: no leader");

            Assert.AreEqual(3, changes.Count);
            Assert.AreEqual((null, b), (changes[0].Item1, changes[0].Item2));
            Assert.AreEqual((b, a), (changes[1].Item1, changes[1].Item2));
            Assert.AreEqual((a, (Player)null), (changes[2].Item1, changes[2].Item2));
        }

        [TestMethod]
        public void Refresh_AHigherLevelArrival_TakesTheLeadAtTheNextRecheck()
        {
            var fighter = Fighter();
            var a = Member(A);
            var c = Member(Cg);
            var t = new Table();
            t.Set(A, 200);
            t.Set(Cg, 275, present: false);

            fighter.Describe = t.Describe;
            fighter.Participants = () => new[] { a, c };

            fighter.RefreshLeader(10.0);
            Assert.AreSame(a, fighter.Leader, "control: the absent one is not chosen");

            t.Set(Cg, 275);
            Assert.IsFalse(fighter.RefreshLeader(10.0 + GauntletAvatarFighter.LeaderRecheckSeconds / 2), "an eligible leader is kept until the re-check");
            Assert.AreSame(a, fighter.Leader);

            Assert.IsTrue(fighter.RefreshLeader(10.0 + GauntletAvatarFighter.LeaderRecheckSeconds));
            Assert.AreSame(c, fighter.Leader);
        }

        [TestMethod]
        public void Refresh_ALeaderWhoLeftTheLandblock_IsReplacedAtOnce()
        {
            var fighter = Fighter();
            var a = Member(A);
            var b = Member(B);
            var t = new Table();
            t.Set(A, 200);
            t.Set(B, 275);

            fighter.Describe = t.Describe;
            fighter.Participants = () => new[] { a, b };
            fighter.RefreshLeader(5.0);

            t.Set(B, 275, present: false);
            Assert.IsTrue(fighter.RefreshLeader(5.1));
            Assert.AreSame(a, fighter.Leader);
        }

        [TestMethod]
        public void Refresh_NoParticipants_OrAThrowingRead_NoLeader()
        {
            var fighter = Fighter();
            Assert.IsFalse(fighter.RefreshLeader(1.0), "no participant source: nothing changes");
            Assert.IsNull(fighter.Leader);

            fighter.Participants = () => throw new InvalidOperationException("snapshot gone");
            Assert.IsFalse(fighter.RefreshLeader(3.0), "a throwing read is contained");
            Assert.IsNull(fighter.Leader);
        }

        [TestMethod]
        public void NoLeader_ReadsTheParticipantsOncePerRecheck_NotEveryTick()
        {
            var fighter = Fighter();
            var a = Member(A);
            var t = new Table();
            t.Set(A, 200, alive: false);
            fighter.Describe = t.Describe;
            var reads = 0;
            fighter.Participants = () => { reads++; return new[] { a }; };

            for (var i = 0; i < 10; i++)
                fighter.RefreshLeader(1.0 + i * 0.09);

            Assert.IsNull(fighter.Leader);
            Assert.AreEqual(1, reads, "ten no-leader ticks inside one recheck window read the participants once");

            fighter.RefreshLeader(1.0 + GauntletAvatarFighter.LeaderRecheckSeconds);
            Assert.AreEqual(2, reads, "the next window reads again");
        }

        // ---- the CombatPet wiring (the consumer) -------------------------------------------------------------------------

        [TestMethod]
        public void TheFollowAndTheAssist_ReadTheLeader_AndTheAssistIsAlwaysOn()
        {
            var fighter = Fighter();
            var a = Member(A);
            var t = new Table();
            t.Set(A, 200);
            fighter.Describe = t.Describe;
            fighter.Participants = () => new[] { a };
            fighter.RefreshLeader(1.0);

            var leaderProp = typeof(CombatPet).GetProperty("FollowLeader", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(leaderProp, "CombatPet.FollowLeader not found");
            Assert.AreSame(a, leaderProp.GetValue(fighter), "CombatPet's follow and assist see the fighter's leader");

            var assist = typeof(CombatPet).GetMethod("AssistsLeaderTarget", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(assist, "CombatPet.AssistsLeaderTarget not found");
            Assert.AreEqual(true, assist.Invoke(fighter, new object[] { a }), "the fighter always assists, whatever the leader's own pet setting");

            Assert.IsNull(fighter.P_PetOwner, "the leader is never made the pet owner, so no kill credit is handed to them");
            Assert.IsNull(PlayerPet().GetType().GetProperty("FollowLeader", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(PlayerPet()) as Player,
                "control: an owned pet's leader is its P_PetOwner (none in this fixture)");
        }

        [TestMethod]
        public void APlainPet_FollowsItsOwner_AndAssistsPerTheOwnersSetting()
        {
            var pet = PlayerPet();
            var owner = AvatarFighterVoiceTests.Named(0x50000099, "Owner");
            pet.P_PetOwner = owner;

            var leaderProp = typeof(CombatPet).GetProperty("FollowLeader", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var assist = typeof(CombatPet).GetMethod("AssistsLeaderTarget", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

            Assert.AreSame(owner, leaderProp.GetValue(pet), "an owned pet's leader is its P_PetOwner");

            owner.SummonAttackOnTarget = true;
            Assert.AreEqual(true, assist.Invoke(pet, new object[] { owner }));

            owner.SummonAttackOnTarget = false;
            Assert.AreEqual(false, assist.Invoke(pet, new object[] { owner }), "a plain pet's assist follows /summonattackontarget");
        }

        private static string Body(string file, string signature) => PooledLootSourceText.MethodBody(PooledLootSourceText.Read(file), signature);

        internal static void AssertFollowSites(string follow, string find)
        {
            Assert.IsTrue(follow.Contains("var owner = FollowLeader;", StringComparison.Ordinal), "TickFollowOwner must follow FollowLeader");
            Assert.IsFalse(follow.Contains("P_PetOwner", StringComparison.Ordinal), "TickFollowOwner must not read P_PetOwner directly");
            Assert.IsTrue(find.Contains("var owner = FollowLeader;", StringComparison.Ordinal), "HandleFindTarget must assist FollowLeader's target");
            Assert.IsTrue(find.Contains("AssistsLeaderTarget(owner)", StringComparison.Ordinal), "HandleFindTarget must ask AssistsLeaderTarget");
        }

        [TestMethod]
        public void CombatPet_FollowAndAssist_ReadTheVirtualLeader()
        {
            var follow = Body("Source/ACE.Server/WorldObjects/CombatPet_Follow.cs", "public virtual void TickFollowOwner()");
            var find = Body("Source/ACE.Server/WorldObjects/CombatPet.cs", "public override void HandleFindTarget()");

            AssertFollowSites(follow, find);

            Assert.ThrowsExactly<AssertFailedException>(() => AssertFollowSites(follow.Replace("var owner = FollowLeader;", "var owner = P_PetOwner;"), find), "mutation: the follow reads the owner");
            Assert.ThrowsExactly<AssertFailedException>(() => AssertFollowSites(follow, find.Replace("AssistsLeaderTarget(owner)", "owner.SummonAttackOnTarget")), "mutation: the assist reads the owner setting");
        }

        [TestMethod]
        public void Fighter_ChoosesItsLeader_BeforeCombatPetPicksATarget()
        {
            var body = Body("Source/ACE.Server/AllegianceAvatar/Gauntlet/GauntletAvatarFighter.cs", "public override void HandleFindTarget()");
            var refresh = body.IndexOf("RefreshLeader(", StringComparison.Ordinal);
            var pick = body.IndexOf("base.HandleFindTarget();", StringComparison.Ordinal);

            Assert.IsTrue(refresh >= 0 && pick > refresh, "the leader is re-chosen before the assist reads it");
            Assert.IsFalse(PooledLootSourceText.Read("Source/ACE.Server/AllegianceAvatar/Gauntlet/GauntletAvatarFighter.cs").Contains("override void TickFollowOwner", StringComparison.Ordinal),
                "no idle walk-home override: CombatPet's own follow runs (owner ruling 2026-11-23)");
        }

        // ---- what it never attacks ------------------------------------------------------------------------------------------

        [TestMethod]
        public void NeverTargets_ANonHostileNpc_OrAPlayersPet()
        {
            var fighter = Fighter();
            var npcWeenie = new Weenie
            {
                WeenieClassId = 995899,
                WeenieType = WeenieType.Creature,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Friendly Npc" } },
                PropertiesBool = new Dictionary<PropertyBool, bool> { { PropertyBool.Attackable, false } },
                PropertiesAttribute2nd = Vitals(),
            };
            var npc = new Creature(npcWeenie, NextGuid());
            npc.Location = At(52.0f, InstanceA);
            WithPhysics(npc, InstanceA);
            var pet = PlayerPet();
            var monster = Monster();

            fighter.PhysicsObj.ObjMaint.AddVisibleTargets(new[] { npc.PhysicsObj, pet.PhysicsObj, monster.PhysicsObj });

            CollectionAssert.AreEqual(new[] { monster }, fighter.GetNearbyMonsters(fighter.NeverInitiates), "only the hostile monster; control included");
            Assert.IsFalse(fighter.CanDamage(pet), "and its splash never reaches a player's pet");
        }
    }
}
