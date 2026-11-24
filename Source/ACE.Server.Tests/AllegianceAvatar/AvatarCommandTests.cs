using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;
using ACE.Entity;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Enum;
using ACE.Server.Avatars;
using ACE.Server.Command.Handlers;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Avatars
{
    /// <summary>
    /// The /avatar command: the pure parser, and the heir popup through its Confirmation_Choice Dispatch seam. Every
    /// collaborator is an AvatarNpcs, AvatarRights or AllegianceAvatarCommands seam restored in cleanup. No PropertyManager
    /// key is read (tuning is passed in as AvatarTuning.Defaults).
    /// </summary>
    [TestClass]
    public class AvatarCommandTests
    {
        private const double Start = 1_800_000_000.0;
        private const double Day = 86400.0;
        private static readonly DateTime Today = new DateTime(2027, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        private static readonly AvatarTuning T = AvatarTuning.Defaults;

        private const uint AvatarId = 7;
        private const uint Monarch = 1000;
        private const uint MonarchAcct = 100;
        private const uint Vassal = 1001;
        private const uint VassalAcct = 101;
        private const uint Stranger = 3000;

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "positionCache")]
        private static extern ref Dictionary<PositionType, Position> PositionCache(WorldObject wo);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "ephemeralPositions")]
        private static extern ref Dictionary<PositionType, Position> EphemeralPositions(WorldObject wo);

        private Store store;
        private AccountActivityIndex activity;
        private Dictionary<uint, AvatarRoster> rosters;
        private List<(Confirmation_Choice Choice, string Text)> popups;
        private List<(Player To, string Text)> told;
        private List<(uint Monarch, string Text)> announced;
        private List<Action> worldQueue;
        private Dictionary<uint, Player> online;
        private Player monarch, vassal;
        private AvatarTuning cur = T;

        [TestInitialize]
        public void Setup()
        {
            AvatarTestChart.Install();
            store = new Store();
            cur = T;
            activity = new AccountActivityIndex();
            rosters = new Dictionary<uint, AvatarRoster>();
            popups = new List<(Confirmation_Choice, string)>();
            told = new List<(Player, string)>();
            announced = new List<(uint, string)>();
            worldQueue = new List<Action>();

            AvatarRights.AllegianceFacts = p => (Monarch, false);
            AvatarAnnouncer.Sink = (m, text) => announced.Add((m, text));
            AvatarNpcs.WorldEnqueue = work => worldQueue.Add(work);
            AvatarNpcs.SendConfirmation = (p, c, text) => { popups.Add(((Confirmation_Choice)c, text)); return true; };
            AvatarNpcs.Tell = (p, text) => told.Add((p, text));
            AvatarNpcs.OnlinePlayer = g => online.TryGetValue(g, out var p) ? p : null;
            AllegianceAvatarCommands.FindCharacter = name =>
                string.Equals(name, "Vassal Name", StringComparison.OrdinalIgnoreCase) ? (Vassal, "Vassal Name")
                : string.Equals(name, "Stranger", StringComparison.OrdinalIgnoreCase) ? (Stranger, "Stranger")
                : ((uint, string)?)null;

            monarch = MakePlayer(Monarch);
            vassal = MakePlayer(Vassal);
            online = new Dictionary<uint, Player> { [Monarch] = monarch, [Vassal] = vassal };
        }

        [TestCleanup]
        public void Cleanup()
        {
            AvatarTestChart.Reset();
            AvatarNpcs.ResetSeams();
            AvatarRights.ResetSeams();
            AvatarLifecycle.ResetSeams();
            AllegianceAvatarCommands.ResetSeams();
            AvatarAnnouncer.Sink = null;
            AvatarManager.Initialize(new Store { LoadAllReturnsNull = true }, () => T, () => Start, () => new AccountActivityIndex(), () => new List<AvatarRoster>());
        }

        private static Player MakePlayer(uint guid)
        {
            var p = SalvageRollerVendorTests.MakePlayer(guid);
            PositionCache(p) = new Dictionary<PositionType, Position>();
            EphemeralPositions(p) = new Dictionary<PositionType, Position>();
            return p;
        }

        private AvatarState Init()
        {
            store.Avatars.Add(new AllegianceAvatar
            {
                AvatarId = AvatarId, MonarchGuid = Monarch, State = (byte)AvatarBindState.Bound,
                LifetimeXp = 10_000, LevelXp = 10_000, Favor = 5000, LastDecayDay = Today,
            });
            rosters[Monarch] = new AvatarRoster(Monarch, MonarchAcct, false, new List<KeeperCandidate>
            {
                new KeeperCandidate(Monarch, MonarchAcct, 5, 0, 0),
                new KeeperCandidate(Vassal, VassalAcct, 3, 0, 1),
            });
            activity.Touch(MonarchAcct, Start);
            activity.Touch(VassalAcct, Start);

            AvatarManager.Initialize(store, () => cur, () => Start, () => activity, () => rosters.Values.ToList(),
                m => rosters.TryGetValue(m, out var r) ? r : null);
            AvatarManager.Tick();

            var a = AvatarManager.Get(AvatarId);
            Assert.IsNotNull(a);
            Assert.AreEqual(Monarch, a.KeeperGuid, "the fixture's Keeper is the monarch");
            announced.Clear();
            return a;
        }

        // ---- parse ----------------------------------------------------------------------------------------------

        [TestMethod]
        public void Parse_Empty_IsStatus()
        {
            var (verb, arg, error) = AllegianceAvatarCommands.Parse(new string[0]);
            Assert.AreEqual(AvatarCommandVerb.Status, verb);
            Assert.IsNull(arg);
            Assert.IsNull(error);
        }

        [TestMethod]
        public void Parse_HeirWithoutName_IsError()
        {
            var (verb, _, error) = AllegianceAvatarCommands.Parse(new[] { "heir" });
            Assert.AreEqual(AvatarCommandVerb.Heir, verb);
            Assert.AreEqual("Usage: /avatar heir <name>", error);
        }

        [TestMethod]
        public void Parse_UnknownVerb_IsError()
        {
            var (_, _, error) = AllegianceAvatarCommands.Parse(new[] { "dance" });
            Assert.IsNotNull(error);
        }

        [TestMethod]
        public void Parse_HeirNameWithSpaces_JoinsArgs()
        {
            var (verb, arg, error) = AllegianceAvatarCommands.Parse(new[] { "HEIR", "Vassal", "Name" });
            Assert.AreEqual(AvatarCommandVerb.Heir, verb);
            Assert.AreEqual("Vassal Name", arg);
            Assert.IsNull(error);
        }

        // ---- heir -----------------------------------------------------------------------------------------------

        [TestMethod]
        public void Heir_SetsOnlyAfterYes()
        {
            var a = Init();
            Assert.AreNotEqual(Vassal, a.HeirGuid);
            var before = a.HeirGuid;

            AllegianceAvatarCommands.Run(monarch, new[] { "heir", "Vassal", "Name" });

            Assert.AreEqual(1, popups.Count);
            Assert.AreEqual("Name Vassal Name as the Avatar's heir?", popups[0].Text);
            Assert.AreEqual(before, a.HeirGuid, "nothing is set before the answer");

            popups[0].Choice.Dispatch(false, false, DateTime.UtcNow);
            Assert.AreEqual(0, worldQueue.Count, "No queues nothing");
            Assert.AreEqual(before, a.HeirGuid, "No leaves the heir unchanged");

            popups.Clear();
            AllegianceAvatarCommands.Run(monarch, new[] { "heir", "Vassal", "Name" });
            popups.Single().Choice.Dispatch(true, false, DateTime.UtcNow);
            Assert.AreEqual(before, a.HeirGuid, "Yes hands the write to the world thread");
            worldQueue.Single()();

            Assert.AreEqual(Vassal, a.HeirGuid);
            Assert.IsTrue(announced.Any(x => x.Monarch == Monarch && x.Text.Contains("is now heir to the Avatar")), "the allegiance is told");
        }

        [TestMethod]
        public void Heir_YesAfterAChange_IsCancelled()
        {
            var a = Init();
            AllegianceAvatarCommands.Run(monarch, new[] { "heir", "Vassal", "Name" });
            a.Version++;

            popups.Single().Choice.Dispatch(true, false, DateTime.UtcNow);
            worldQueue.Single()();

            Assert.AreNotEqual(Vassal, a.HeirGuid);
            Assert.AreEqual("The heir was not named: the Avatar has changed since you were asked.", told.Last().Text);
        }

        [TestMethod]
        public void Heir_YesAfterTargetLeaves_IsCancelled()
        {
            var a = Init();
            AllegianceAvatarCommands.Run(monarch, new[] { "heir", "Vassal", "Name" });
            rosters[Monarch] = new AvatarRoster(Monarch, MonarchAcct, false, new List<KeeperCandidate> { new KeeperCandidate(Monarch, MonarchAcct, 5, 0, 0) });

            popups.Single().Choice.Dispatch(true, false, DateTime.UtcNow);
            worldQueue.Single()();

            Assert.AreNotEqual(Vassal, a.HeirGuid);
            StringAssert.Contains(told.Last().Text, "no longer a member");
        }

        [TestMethod]
        public void Heir_Refusals_AskNothing()
        {
            Init();

            AllegianceAvatarCommands.Run(monarch, new[] { "heir", "Stranger" });
            Assert.AreEqual("That character is not a member of your allegiance.", told.Last().Text);

            AllegianceAvatarCommands.Run(monarch, new[] { "heir", "Nobody" });
            Assert.AreEqual("That character is not a member of your allegiance.", told.Last().Text);

            AllegianceAvatarCommands.Run(vassal, new[] { "heir", "Vassal", "Name" });
            Assert.AreEqual("Only the Keeper can name the Avatar's heir.", told.Last().Text);

            Assert.AreEqual(0, popups.Count);
        }

        [TestMethod]
        public void Heir_YesAfterKeeperChanges_IsCancelled()
        {
            var a = Init();
            AllegianceAvatarCommands.Run(monarch, new[] { "heir", "Vassal", "Name" });
            a.KeeperGuid = Vassal;

            popups.Single().Choice.Dispatch(true, false, DateTime.UtcNow);
            worldQueue.Single()();

            Assert.AreNotEqual(Vassal, a.HeirGuid);
            Assert.AreEqual("The heir was not named: you are no longer the Keeper.", told.Last().Text);
        }

        [TestMethod]
        public void Heir_YesAfterAvatarNoLongerBound_IsCancelled()
        {
            var a = Init();
            AllegianceAvatarCommands.Run(monarch, new[] { "heir", "Vassal", "Name" });
            a.State = AvatarBindState.Absorbed;

            popups.Single().Choice.Dispatch(true, false, DateTime.UtcNow);
            worldQueue.Single()();

            Assert.AreNotEqual(Vassal, a.HeirGuid);
            Assert.AreEqual("The heir was not named: you are no longer the Keeper.", told.Last().Text);
        }

        [TestMethod]
        public void Heir_YesAfterSwitchOff_IsRefused()
        {
            var a = Init();
            AllegianceAvatarCommands.Run(monarch, new[] { "heir", "Vassal", "Name" });
            cur = T with { Enabled = false };

            popups.Single().Choice.Dispatch(true, false, DateTime.UtcNow);
            worldQueue.Single()();

            Assert.AreNotEqual(Vassal, a.HeirGuid);
            Assert.AreEqual(AvatarSeatRules.SwitchedOffMessage, told.Last().Text);
        }

        [TestMethod]
        public void Heir_NamingYourself_IsRefused()
        {
            Init();
            AllegianceAvatarCommands.FindCharacter = name => (Monarch, "Monarch Name");

            AllegianceAvatarCommands.Run(monarch, new[] { "heir", "Monarch", "Name" });

            Assert.AreEqual("You are already the Keeper.", told.Last().Text);
            Assert.AreEqual(0, popups.Count);
        }

        // ---- master switch and routing ---------------------------------------------------------------------------

        [TestMethod]
        public void Switch_Off_RefusesSummonSeatAndHeir_StatusStillWorks()
        {
            var a = Init();
            cur = T with { Enabled = false };
            var summoned = 0;
            AvatarNpcs.SummonerState = (Player p, out string r) => { summoned++; r = null; return true; };

            foreach (var args in new[] { new[] { "summon" }, new[] { "seat" }, new[] { "heir", "Vassal", "Name" } })
            {
                told.Clear();
                AllegianceAvatarCommands.Run(monarch, args);
                Assert.AreEqual(1, told.Count, args[0]);
                Assert.AreEqual("The Allegiance Avatar is switched off.", told[0].Text, args[0]);
            }

            Assert.AreEqual(0, popups.Count);
            Assert.AreEqual(0, summoned, "summon never reached TrySummon");
            Assert.AreNotEqual(Vassal, a.HeirGuid);

            told.Clear();
            AllegianceAvatarCommands.Run(monarch, new string[0]);
            StringAssert.StartsWith(told[0].Text, "Allegiance Avatar - level");
        }

        [TestMethod]
        public void Summon_PlainMember_RefusedWithRightsLine()
        {
            Init();
            AllegianceAvatarCommands.Run(vassal, new[] { "summon" });
            Assert.AreEqual(AvatarSeatRules.SummonNotAllowedMessage, told.Last().Text);
        }

        [TestMethod]
        public void Seat_ReachesRequestSeat()
        {
            Init();
            // Only RequestSeat's own check produces the not-Keeper line for a non-Keeper member.
            AllegianceAvatarCommands.Run(vassal, new[] { "seat" });
            Assert.AreEqual(AvatarSeatRules.SeatNotKeeperMessage, told.Last().Text);
        }

        [TestMethod]
        public void Parse_TrailingArgsOnSummonAndSeat_AreUsageErrors()
        {
            Assert.AreEqual("Usage: /avatar [summon | seat | heir <name> | dress [reset] | gauntlet [enter]]", AllegianceAvatarCommands.Parse(new[] { "summon", "now" }).Error);
            Assert.AreEqual("Usage: /avatar [summon | seat | heir <name> | dress [reset] | gauntlet [enter]]", AllegianceAvatarCommands.Parse(new[] { "seat", "here" }).Error);
            Assert.AreEqual(AvatarCommandVerb.Summon, AllegianceAvatarCommands.Parse(new[] { "SUMMON" }).Verb);
            Assert.IsNull(AllegianceAvatarCommands.Parse(new[] { "SUMMON" }).Error);
        }

        // ---- gauntlet subverb -----------------------------------------------------------------------------------

        [TestMethod]
        public void Parse_Gauntlet_IsItsOwnVerb_NoArguments()
        {
            var (verb, arg, error) = AllegianceAvatarCommands.Parse(new[] { "GAUNTLET" });
            Assert.AreEqual(AvatarCommandVerb.Gauntlet, verb);
            Assert.IsNull(arg);
            Assert.IsNull(error);

            Assert.IsNotNull(AllegianceAvatarCommands.Parse(new[] { "gauntlet", "now" }).Error, "trailing arguments are a usage error");
        }

        [TestMethod]
        public void Gauntlet_AnyMember_ReceivesTheStatusLines_ForTheirOwnAvatar()
        {
            var a = Init();
            AvatarState seenAvatar = null;
            Player seenPlayer = null;
            AllegianceAvatarCommands.GauntletStatusLines = (p, av) =>
            {
                seenPlayer = p;
                seenAvatar = av;
                return new[] { "line one", "line two" };
            };

            // a plain vassal, who is neither Keeper nor monarch: the verb is open to every member
            AllegianceAvatarCommands.Run(vassal, new[] { "gauntlet" });

            Assert.AreSame(a, seenAvatar, "the lines are built for the player's own allegiance's Avatar");
            Assert.AreSame(vassal, seenPlayer);
            CollectionAssert.AreEqual(new[] { "line one", "line two" }, told.Select(t => t.Text).ToArray());
            Assert.IsTrue(told.All(t => ReferenceEquals(t.To, vassal)));
        }

        [TestMethod]
        public void Gauntlet_NoAvatar_GetsTheNoAvatarLine()
        {
            AllegianceAvatarCommands.GauntletStatusLines = (p, av) => throw new AssertFailedException("never asked without an Avatar");

            AllegianceAvatarCommands.Run(vassal, new[] { "gauntlet" });

            Assert.AreEqual(AllegianceAvatarCommands.NoAvatarText(T), told.Single().Text);
        }

        // ---- status / no avatar ---------------------------------------------------------------------------------

        [TestMethod]
        public void Status_ShowsLinesToAMember()
        {
            Init();
            AllegianceAvatarCommands.Run(vassal, new string[0]);

            Assert.IsTrue(told.Count >= 4);
            StringAssert.StartsWith(told[0].Text, "Allegiance Avatar - level");
            Assert.IsTrue(told.All(t => t.Text.All(c => c < 128)), "every reply is plain ASCII");
        }

        [TestMethod]
        public void NoAvatar_ExplainsWithTheTunables()
        {
            AvatarManager.Initialize(new Store { LoadAllReturnsNull = true }, () => T, () => Start, () => activity, () => new List<AvatarRoster>());
            AllegianceAvatarCommands.Run(monarch, new string[0]);

            Assert.AreEqual("Your allegiance has no Avatar yet. It appears once 2 or more accounts in it have played in the last 7 days.", told.Last().Text);
        }

        private sealed class Store : IAvatarStore
        {
            public readonly List<AllegianceAvatar> Avatars = new List<AllegianceAvatar>();
            public bool LoadAllReturnsNull;

            public List<AllegianceAvatar> LoadAll() => LoadAllReturnsNull ? null : Avatars.ToList();
            public List<AllegianceAvatarContribution> LoadContributionsSince(DateTime dayUtc) => new List<AllegianceAvatarContribution>();
            public void SaveBatch(IReadOnlyList<AllegianceAvatar> a, IReadOnlyList<AllegianceAvatarContribution> r, Action<bool> done) => done(true);
            public void Insert(AllegianceAvatar row, Action<bool, uint> done) => done(true, 0);
        }
    }
}
