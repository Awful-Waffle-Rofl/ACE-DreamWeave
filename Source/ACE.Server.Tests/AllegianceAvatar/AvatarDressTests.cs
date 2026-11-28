using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Avatars;
using ACE.Server.Command.Handlers;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Avatars
{
    /// <summary>
    /// /avatar dress and dress reset: the parse, the refusals, and the popup's Yes handler through the Confirmation_Choice
    /// Dispatch seam. Tuning is passed in as AvatarTuning.Defaults with a 3600 s dress cooldown (the shipped default is 0), so the in-cooldown checks stay meaningful and no PropertyManager key is read. Every collaborator is an
    /// AvatarNpcs, AvatarRights, AvatarDress or AvatarAnnouncer seam restored in cleanup.
    /// </summary>
    [TestClass]
    public class AvatarDressTests
    {
        private const double Start = 1_800_000_000.0;
        private static readonly DateTime Today = new DateTime(2027, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime Now = new DateTime(2027, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        // The shipped default is no dress cooldown; the cooldown tests need a real one, so it is set explicitly.
        private static readonly AvatarTuning T = AvatarTuning.Defaults with { DressCooldownSeconds = 3600 };

        private const uint AvatarId = 7;
        private const uint Monarch = 1000;
        private const uint MonarchAcct = 100;
        private const uint Vassal = 1001;
        private const uint VassalAcct = 101;

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
        private List<uint> redressed;
        private AvatarNpcs.RedressResult redressResult;
        private bool near;
        private bool popupOk;
        private Player monarch, vassal;
        private (uint, bool) facts;

        [TestInitialize]
        public void Setup()
        {
            store = new Store();
            activity = new AccountActivityIndex();
            rosters = new Dictionary<uint, AvatarRoster>();
            popups = new List<(Confirmation_Choice, string)>();
            told = new List<(Player, string)>();
            announced = new List<(uint, string)>();
            worldQueue = new List<Action>();
            redressed = new List<uint>();
            redressResult = new AvatarNpcs.RedressResult(1, false);
            near = true;
            popupOk = true;
            facts = (Monarch, false);

            AvatarRights.AllegianceFacts = p => facts;
            AvatarAnnouncer.Sink = (m, text) => announced.Add((m, text));
            AvatarNpcs.WorldEnqueue = work => worldQueue.Add(work);
            AvatarNpcs.SendConfirmation = (p, c, text) => { popups.Add(((Confirmation_Choice)c, text)); return popupOk; };
            AvatarNpcs.Tell = (p, text) => told.Add((p, text));
            AvatarNpcs.OnlinePlayer = g => online.TryGetValue(g, out var p) ? p : null;
            AvatarDress.UtcNow = () => Now;
            AvatarDress.NearAvatar = (p, a) => near;
            AvatarDress.Redress = id => { redressed.Add(id); return redressResult; };

            monarch = MakePlayer(Monarch);
            vassal = MakePlayer(Vassal);
            online = new Dictionary<uint, Player> { [Monarch] = monarch, [Vassal] = vassal };
        }

        [TestCleanup]
        public void Cleanup()
        {
            AvatarNpcs.ResetSeams();
            AvatarRights.ResetSeams();
            AvatarDress.ResetSeams();
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
                LifetimeXp = 10_000, Favor = 5000, LastDecayDay = Today,
            });
            rosters[Monarch] = new AvatarRoster(Monarch, MonarchAcct, false, new List<KeeperCandidate>
            {
                new KeeperCandidate(Monarch, MonarchAcct, 5, 0, 0),
                new KeeperCandidate(Vassal, VassalAcct, 3, 0, 1),
            });
            activity.Touch(MonarchAcct, Start);
            activity.Touch(VassalAcct, Start);

            AvatarManager.Initialize(store, () => T, () => Start, () => activity, () => rosters.Values.ToList(),
                m => rosters.TryGetValue(m, out var r) ? r : null);
            AvatarManager.Tick();

            var a = AvatarManager.Get(AvatarId);
            Assert.IsNotNull(a);
            announced.Clear();
            return a;
        }

        private static void SetLook(AvatarLook look) =>
            Assert.IsTrue(AvatarManager.TrySetLook(AvatarId, AvatarLookJson.Serialize(look), null));

        private static AvatarLook ResetLook(DateTime at) =>
            new AvatarLook(AvatarLookJson.CurrentVersion, at, null, new List<AvatarLookPiece>());

        private bool Begin(Player p, AvatarState a, bool reset, out string refusal) => AvatarDress.TryBegin(p, a, reset, Now, out refusal);

        private void Yes()
        {
            popups.Single().Choice.Dispatch(true, false, Now);
            var work = worldQueue.ToList();
            worldQueue.Clear();
            foreach (var w in work)
                w();
        }

        // ---- parse ----------------------------------------------------------------------------------------------

        [TestMethod]
        public void Parse_dress_and_dress_reset()
        {
            var (verb, arg, error) = AllegianceAvatarCommands.Parse(new[] { "dress" });
            Assert.AreEqual(AvatarCommandVerb.Dress, verb);
            Assert.IsNull(arg);
            Assert.IsNull(error);

            (verb, arg, error) = AllegianceAvatarCommands.Parse(new[] { "DRESS", "Reset" });
            Assert.AreEqual(AvatarCommandVerb.DressReset, verb);
            Assert.IsNull(error);
        }

        [TestMethod]
        public void Parse_dress_extra_arg_is_usage()
        {
            Assert.AreEqual(AllegianceAvatarCommands.UsageText, AllegianceAvatarCommands.Parse(new[] { "dress", "now" }).Error);
            Assert.AreEqual(AllegianceAvatarCommands.UsageText, AllegianceAvatarCommands.Parse(new[] { "dress", "reset", "x" }).Error);
            Assert.AreEqual("Usage: /avatar [summon | seat | heir <name> | dress [reset] | gauntlet [enter]]", AllegianceAvatarCommands.UsageText);
        }

        // ---- rights ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void Dress_refused_for_speaker_and_other_allegiance()
        {
            var a = Init();

            facts = (Monarch, false);
            Assert.IsFalse(Begin(vassal, a, false, out var refusal));
            Assert.AreEqual(AvatarVendor.NotAllowedMessage, refusal, "a Speaker cannot spend");

            facts = (9999, true);
            Assert.IsFalse(Begin(vassal, a, false, out refusal));
            Assert.AreEqual(AvatarVendor.NotAllowedMessage, refusal, "another allegiance's Seneschal cannot");
            Assert.AreEqual(0, popups.Count);
        }

        [TestMethod]
        public void Dress_allowed_for_seneschal_monarch_keeper()
        {
            var a = Init();

            facts = (Monarch, true);
            Assert.IsTrue(Begin(vassal, a, false, out _), "Seneschal");
            facts = (Monarch, false);
            Assert.IsTrue(Begin(monarch, a, false, out _), "Monarch");
            a.KeeperGuid = Vassal;
            Assert.IsTrue(Begin(vassal, a, false, out _), "Keeper");
            Assert.AreEqual(3, popups.Count);
        }

        [TestMethod]
        public void Dress_refused_out_of_range()
        {
            var a = Init();
            near = false;
            Assert.IsFalse(Begin(monarch, a, false, out var refusal));
            Assert.AreEqual("Stand at your Avatar to dress it.", refusal);
            Assert.AreEqual(0, popups.Count);
        }

        [TestMethod]
        public void Dress_refused_in_cooldown_including_after_reset()
        {
            var a = Init();

            SetLook(ResetLook(Now.AddMinutes(-30)));
            Assert.IsFalse(Begin(monarch, a, false, out var refusal));
            Assert.AreEqual("The Avatar was dressed recently. It can be dressed again in 30 minutes.", refusal, "a reset look carries its own cooldown");
            Assert.IsFalse(Begin(monarch, a, true, out refusal));

            SetLook(ResetLook(Now.AddMinutes(-61)));
            Assert.IsTrue(Begin(monarch, a, false, out _));
        }

        [TestMethod]
        public void Dress_allowed_immediately_at_zero_cooldown()
        {
            Assert.AreEqual(0L, AvatarTuning.Defaults.DressCooldownSeconds, "fixture: the shipped default is no cooldown");
            var a = Init();
            AvatarManager.Initialize(store, () => T with { DressCooldownSeconds = 0 }, () => Start, () => activity, () => rosters.Values.ToList(),
                m => rosters.TryGetValue(m, out var r) ? r : null);

            SetLook(ResetLook(Now));
            Assert.IsTrue(Begin(monarch, a, false, out var refusal), refusal);
        }

        [TestMethod]
        public void Reset_survives_restart_cooldown()
        {
            var a = Init();
            SetLook(ResetLook(Now.AddMinutes(-10)));

            var parsed = AvatarLookJson.Parse(a.LookJson);
            Assert.IsNotNull(parsed, "a reset look is stored as a look, never as NULL");
            Assert.IsNull(parsed.Body);
            Assert.AreEqual(Now.AddMinutes(-10), parsed.DressedAtUtc);

            // A restart reloads the row: put the parsed look back through the manager and the cooldown still holds.
            Assert.IsTrue(AvatarManager.TrySetLook(AvatarId, AvatarLookJson.Serialize(parsed), null));
            Assert.IsFalse(Begin(monarch, a, false, out var refusal));
            StringAssert.Contains(refusal, "50 minutes");
        }

        [TestMethod]
        public void Dress_popup_failure_and_reset_text()
        {
            var a = Init();
            SetLook(AvatarLookCapture.FromPlayer(monarch, Now.AddHours(-3)));

            Assert.IsTrue(Begin(monarch, a, true, out _));
            Assert.AreEqual("Restore the Avatar's default look?", popups.Single().Text);

            popupOk = false;
            Assert.IsFalse(Begin(monarch, a, false, out var refusal));
            Assert.AreEqual("Finish answering your other prompt first.", refusal);
        }

        [TestMethod]
        public void Dress_oversize_refused()
        {
            var a = Init();
            monarch.SetProperty(PropertyString.Sex, new string('x', AvatarLookJson.MaxBytes + 10));

            Assert.IsFalse(Begin(monarch, a, false, out var refusal));
            Assert.AreEqual("That outfit is too elaborate to copy.", refusal);
            Assert.AreEqual(0, popups.Count);
        }

        // ---- Yes ------------------------------------------------------------------------------------------------

        [TestMethod]
        public void Dress_yes_saves_and_redresses_and_announces()
        {
            var a = Init();
            monarch.SetProperty(PropertyString.Sex, "Female");
            var before = a.Version;

            Assert.IsTrue(Begin(monarch, a, false, out _));
            StringAssert.Contains(popups.Single().Text, "female");
            Assert.IsNull(a.Look, "nothing is saved before Yes");

            Yes();

            Assert.IsNotNull(a.Look);
            Assert.AreEqual(Now, a.Look.DressedAtUtc, "the command-time capture is stored");
            Assert.AreEqual("Female", a.Look.Body.Strings[PropertyString.Sex]);
            Assert.AreEqual(before + 1, a.Version);
            CollectionAssert.AreEqual(new[] { AvatarId }, redressed);
            Assert.AreEqual((Monarch, $"{monarch.Name} dressed the Avatar."), announced.Single());
        }

        [TestMethod]
        public void Dress_reset_yes_stores_the_reset_look()
        {
            var a = Init();
            SetLook(ResetLook(Now.AddHours(-3)));
            monarch.SetProperty(PropertyString.Sex, "Female");
            SetLookBodyFor(a);

            Assert.IsTrue(Begin(monarch, a, true, out _));
            Yes();

            Assert.IsNull(a.Look.Body);
            Assert.AreEqual(Now, a.Look.DressedAtUtc);
            Assert.AreEqual((Monarch, $"{monarch.Name} restored the Avatar's default look."), announced.Single());
        }

        private void SetLookBodyFor(AvatarState a) => SetLook(AvatarLookCapture.FromPlayer(monarch, Now.AddHours(-3)));

        [TestMethod]
        public void Dress_yes_tells_the_officer_when_the_summon_ended()
        {
            var a = Init();
            redressResult = new AvatarNpcs.RedressResult(1, true);

            Assert.IsTrue(Begin(monarch, a, false, out _));
            Yes();

            Assert.AreEqual("Your Avatar could not stand there; summon it again.", told.Last().Text);
        }

        [TestMethod]
        public void Dress_yes_with_changed_figure_saves_nothing()
        {
            var a = Init();
            monarch.SetProperty(PropertyString.Sex, "Female");
            Assert.IsTrue(Begin(monarch, a, false, out _));

            monarch.SetProperty(PropertyString.Sex, "Male");
            Yes();

            Assert.IsNull(a.Look);
            Assert.AreEqual(0, redressed.Count);
            Assert.AreEqual("Your outfit changed; run /avatar dress again.", told.Last().Text);
        }

        [TestMethod]
        public void Dress_yes_after_rights_lost_saves_nothing()
        {
            var a = Init();
            Assert.IsTrue(Begin(monarch, a, false, out _));

            facts = (9999, false);
            Yes();

            Assert.IsNull(a.Look);
            Assert.AreEqual(0, redressed.Count);
            Assert.AreEqual(AvatarVendor.NotAllowedMessage, told.Last().Text);
        }

        [TestMethod]
        public void Dress_yes_after_version_change_saves_nothing()
        {
            var a = Init();
            Assert.IsTrue(Begin(monarch, a, false, out _));

            // Another change to the Avatar (here an unrelated look write by someone else) bumps Version.
            Assert.IsTrue(AvatarManager.TrySetLook(AvatarId, AvatarLookJson.Serialize(ResetLook(Now.AddHours(-5))), null));
            var other = a.LookJson;
            Yes();

            Assert.AreEqual(other, a.LookJson);
            Assert.AreEqual(0, redressed.Count);
            Assert.AreEqual("The Avatar changed while you were deciding; try again.", told.Last().Text);
        }

        [TestMethod]
        public void Dress_yes_after_cooldown_started_saves_nothing()
        {
            var a = Init();
            Assert.IsTrue(Begin(monarch, a, false, out _));

            SetLook(ResetLook(Now.AddMinutes(-1)));
            Yes();

            Assert.AreEqual(0, redressed.Count);
            StringAssert.Contains(told.Last().Text, "dressed recently");
        }

        [TestMethod]
        public void Dress_yes_when_switched_off_saves_nothing()
        {
            var a = Init();
            Assert.IsTrue(Begin(monarch, a, false, out _));

            AvatarManager.Initialize(store, () => T with { Enabled = false }, () => Start, () => activity, () => rosters.Values.ToList(),
                m => rosters.TryGetValue(m, out var r) ? r : null);
            Yes();

            Assert.AreEqual(AvatarSeatRules.SwitchedOffMessage, told.Last().Text);
            Assert.IsNull(AvatarManager.Get(AvatarId)?.Look);
            Assert.AreEqual(0, redressed.Count);
        }

        [TestMethod]
        public void Dress_yes_when_avatar_not_bound_says_no_avatar()
        {
            var a = Init();
            Assert.IsTrue(Begin(monarch, a, false, out _));

            a.State = AvatarBindState.Absorbed;
            Yes();

            Assert.AreEqual(AllegianceAvatarCommands.NoAvatarText(T), told.Last().Text);
            Assert.IsNull(a.Look);
            Assert.AreEqual(0, redressed.Count);
        }

        [TestMethod]
        public void Dress_yes_when_moved_away_saves_nothing()
        {
            var a = Init();
            Assert.IsTrue(Begin(monarch, a, false, out _));

            near = false;
            Yes();

            Assert.AreEqual("Stand at your Avatar to dress it.", told.Last().Text);
            Assert.IsNull(a.Look);
            Assert.AreEqual(0, redressed.Count);
        }

        [TestMethod]
        public void Reset_refused_when_already_default()
        {
            var a = Init();

            Assert.IsFalse(Begin(monarch, a, true, out var refusal), "never dressed");
            Assert.AreEqual("The Avatar already wears its default look.", refusal);

            SetLook(ResetLook(Now.AddHours(-3)));
            Assert.IsFalse(Begin(monarch, a, true, out refusal), "already the reset look");
            Assert.AreEqual("The Avatar already wears its default look.", refusal);
            Assert.AreEqual(0, popups.Count);
            Assert.AreEqual(0, announced.Count);
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
