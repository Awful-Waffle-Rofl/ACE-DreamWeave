using System;
using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Avatars;
using ACE.Server.Avatars.Gauntlet;
using ACE.Server.Tests.Pvp;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Avatars.Gauntlet
{
    /// <summary>
    /// The Gauntlet Gate use handler (Task 10). The run is a fake <see cref="IGauntletGateTarget"/> that composes a real
    /// <see cref="GauntletArenaHold"/> for admission, as Task 7's run will. No PropertyManager key is read.
    /// </summary>
    [TestClass]
    public class GauntletGateTests
    {
        private const uint Monarch = 1000;
        private const uint OutsiderGuid = 2000;
        private static uint nextGuid = 0x7ECB0000;
        private static readonly Position Entry = new Position(0xA9B40019u, 10f, 20f, 30f, 0f, 0f, 0f, 1f, 0x80010001u);

        private readonly List<string> told = new List<string>();
        private readonly List<(Player Who, Position To)> teleports = new List<(Player, Position)>();
        private Player member, outsider;
        private FakeTarget target;

        [TestInitialize]
        public void Setup()
        {
            var facts = new Dictionary<uint, (uint, bool)> { { Monarch, (Monarch, false) }, { OutsiderGuid, (OutsiderGuid, false) } };
            AvatarRights.AllegianceFacts = p => facts[p.Guid.Full];

            GauntletGate.ResetSeams();
            GauntletGate.Told = (p, text) => told.Add(text);
            GauntletGate.Teleporter = (p, to) => teleports.Add((p, to));

            member = SeedPositions(SalvageRollerVendorTests.MakePlayer(Monarch));
            outsider = SeedPositions(SalvageRollerVendorTests.MakePlayer(OutsiderGuid));
            member.Location = new Position(0xA9B40019u, 84f, 96f, 20f, 0f, 0f, 0f, 1f, 0);
            outsider.Location = new Position(0xA9B40019u, 85f, 96f, 20f, 0f, 0f, 0f, 1f, 0);

            // Admission is the real hold, keyed on the player's own monarch fact (the seam above), not the guid-keyed login reader.
            target = new FakeTarget(new GauntletArenaHold(p => AvatarRights.AllegianceFacts(p).MonarchGuid == Monarch, TimeSpan.FromHours(2), GauntletState.Active));
        }

        [TestCleanup]
        public void Cleanup()
        {
            GauntletGate.ResetSeams();
            AvatarRights.ResetSeams();
            ACE.Server.Command.Handlers.AllegianceAvatarCommands.ResetSeams();
        }

        // ---- /avatar gauntlet enter: the gate's checks with no gate object ------------------------------------------------

        private void Enter(Player who, IGauntletGateTarget run)
        {
            ACE.Server.Command.Handlers.AllegianceAvatarCommands.GauntletTargetFor = id => run;
            ACE.Server.Command.Handlers.AllegianceAvatarCommands.EnterGauntlet(who, 77);
        }

        [TestMethod]
        public void Parse_GauntletEnter_IsItsOwnVerb()
        {
            Assert.AreEqual(ACE.Server.Command.Handlers.AvatarCommandVerb.GauntletEnter, ACE.Server.Command.Handlers.AllegianceAvatarCommands.Parse(new[] { "gauntlet", "enter" }).Verb);
            Assert.IsNull(ACE.Server.Command.Handlers.AllegianceAvatarCommands.Parse(new[] { "Gauntlet", "ENTER" }).Error);
            Assert.IsNotNull(ACE.Server.Command.Handlers.AllegianceAvatarCommands.Parse(new[] { "gauntlet", "leave" }).Error, "any other word is the usage line");
        }

        [TestMethod]
        public void Enter_Member_StampsExitAndTeleports()
        {
            Enter(member, target);

            Assert.AreEqual(1, teleports.Count);
            Assert.AreSame(Entry, teleports[0].To);
            Assert.AreEqual(84f, member.GetPosition(PositionType.EphemeralRealmExitTo).PositionX, "the same exit stamp the gate writes");
        }

        [TestMethod]
        public void Enter_RefusesInEveryGateRefusalCase()
        {
            Enter(member, null);
            CollectionAssert.AreEqual(new[] { GauntletGate.ClosedLine }, told, "no live run");

            told.Clear();
            Enter(outsider, target);
            CollectionAssert.AreEqual(new[] { GauntletGate.NotMemberLine }, told, "not a member");

            told.Clear();
            target.Hold.State = GauntletState.Closed;
            Enter(member, target);
            CollectionAssert.AreEqual(new[] { GauntletGate.ClosedLine }, told, "not admitting");
            target.Hold.State = GauntletState.Active;

            told.Clear();
            target.HoldLeft = 9.5;
            Enter(member, target);
            Assert.AreEqual(1, told.Count);
            StringAssert.Contains(told[0], "10 seconds", "held");
            target.HoldLeft = 0;

            told.Clear();
            var at = member.Location;
            member.Location = new Position(Entry.Cell, 1f, 2f, 3f, 0f, 0f, 0f, 1f, 0x80010001u);
            Enter(member, target);
            CollectionAssert.AreEqual(new[] { GauntletGate.InstanceLine }, told, "already in an instance");
            member.Location = at;

            told.Clear();
            target.Entry = null;
            Enter(member, target);
            CollectionAssert.AreEqual(new[] { GauntletGate.NoEntryLine }, told, "no entry point");

            told.Clear();
            target.Entry = Entry;
            target.ThrowOnAdmits = true;
            Enter(member, target);
            CollectionAssert.AreEqual(new[] { GauntletGate.ClosedLine }, told, "a throwing run fails closed");

            Assert.AreEqual(0, teleports.Count, "no refusal teleports");
            Assert.IsNull(member.GetPosition(PositionType.EphemeralRealmExitTo), "no refusal writes the stamp");
        }

        /// <summary>The seeded player is built without constructors, so the two position caches WorldObject initializes inline are missing; set them on WorldObject (never a Player field).</summary>
        private static Player SeedPositions(Player p)
        {
            PvpTemplatePlayerTests.SetField(typeof(WorldObject), p, "ephemeralPositions", new Dictionary<PositionType, Position>());
            PvpTemplatePlayerTests.SetField(typeof(WorldObject), p, "positionCache", new Dictionary<PositionType, Position>());
            return p;
        }

        private static WorldObject MakeGate(bool marked = true)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 1008505,
                WeenieType = WeenieType.Generic,
                PropertiesBool = marked ? new Dictionary<PropertyBool, bool> { { PropertyBool.AllegianceGauntletGate, true } } : new Dictionary<PropertyBool, bool>(),
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Gauntlet Gate" } },
            };

            return new GenericObject(weenie, new ObjectGuid(++nextGuid));
        }

        [TestMethod]
        public void Use_Member_StampsExitAndTeleportsToEntry()
        {
            var gate = MakeGate();
            GauntletGate.Register(gate.Guid.Full, target);

            Assert.IsTrue(GauntletGate.TryHandleUse(gate, member));

            var stamp = member.GetPosition(PositionType.EphemeralRealmExitTo);
            Assert.IsNotNull(stamp, "the exit stamp is written");
            Assert.AreEqual(84f, stamp.PositionX);
            Assert.AreEqual(0u, stamp.Instance);
            Assert.AreNotSame(member.Location, stamp, "a copy, never the live position object");
            Assert.AreEqual(1, teleports.Count);
            Assert.AreSame(member, teleports[0].Who);
            Assert.AreSame(Entry, teleports[0].To);
            Assert.AreEqual(1, target.EntryCalls);
        }

        [TestMethod]
        public void Use_NonMember_Refused()
        {
            var gate = MakeGate();
            GauntletGate.Register(gate.Guid.Full, target);

            Assert.IsTrue(GauntletGate.TryHandleUse(gate, outsider), "the use is consumed even when refused");

            Assert.AreEqual(0, teleports.Count);
            Assert.IsNull(outsider.GetPosition(PositionType.EphemeralRealmExitTo), "no stamp for a refused player");
            CollectionAssert.AreEqual(new[] { GauntletGate.NotMemberLine }, told);
            Assert.AreEqual(0, target.EntryCalls, "the rotation does not advance for a refusal");
        }

        [TestMethod]
        public void Use_RewardingAndEligible_Admitted()
        {
            var gate = MakeGate();
            GauntletGate.Register(gate.Guid.Full, target);
            target.Hold.State = GauntletState.Rewarding;

            Assert.IsTrue(GauntletGate.TryHandleUse(gate, member));

            Assert.AreEqual(1, teleports.Count, "a member still gets in to claim during Rewarding");
        }

        [TestMethod]
        public void Use_StateNotAdmitting_RefusedAsClosed()
        {
            var gate = MakeGate();
            GauntletGate.Register(gate.Guid.Full, target);

            foreach (var s in new[] { GauntletState.Purchased, GauntletState.Ending, GauntletState.Closed, GauntletState.Aborted })
            {
                told.Clear();
                target.Hold.State = s;

                Assert.IsTrue(GauntletGate.TryHandleUse(gate, member));
                CollectionAssert.AreEqual(new[] { GauntletGate.ClosedLine }, told, s.ToString());
            }

            Assert.AreEqual(0, teleports.Count);
        }

        [TestMethod]
        public void Use_HeldPlayer_RefusedWithTimeLeft()
        {
            var gate = MakeGate();
            GauntletGate.Register(gate.Guid.Full, target);
            target.HoldLeft = 41.2;

            Assert.IsTrue(GauntletGate.TryHandleUse(gate, member));

            Assert.AreEqual(0, teleports.Count);
            Assert.IsNull(member.GetPosition(PositionType.EphemeralRealmExitTo));
            Assert.AreEqual(1, told.Count);
            StringAssert.Contains(told[0], "42 seconds");
        }

        [TestMethod]
        public void Use_AlreadyInAnInstance_RefusedWithoutOverwritingTheStamp()
        {
            var gate = MakeGate();
            GauntletGate.Register(gate.Guid.Full, target);
            member.Location = new Position(Entry.Cell, 1f, 2f, 3f, 0f, 0f, 0f, 1f, 0x80010001u);

            Assert.IsTrue(GauntletGate.TryHandleUse(gate, member));

            Assert.AreEqual(0, teleports.Count);
            CollectionAssert.AreEqual(new[] { GauntletGate.InstanceLine }, told);
        }

        [TestMethod]
        public void Use_NoEntryPoint_Refused()
        {
            var gate = MakeGate();
            GauntletGate.Register(gate.Guid.Full, target);
            target.Entry = null;

            Assert.IsTrue(GauntletGate.TryHandleUse(gate, member));

            Assert.AreEqual(0, teleports.Count);
            CollectionAssert.AreEqual(new[] { GauntletGate.NoEntryLine }, told);
        }

        [TestMethod]
        public void Use_UnregisteredGate_IsDormant_AndUnmarkedObjectsFallThrough()
        {
            var gate = MakeGate();

            Assert.IsTrue(GauntletGate.TryHandleUse(gate, member));
            CollectionAssert.AreEqual(new[] { GauntletGate.DormantLine }, told);

            GauntletGate.Register(gate.Guid.Full, target);
            GauntletGate.Unregister(gate.Guid.Full);
            told.Clear();
            Assert.IsTrue(GauntletGate.TryHandleUse(gate, member), "a closed run's gate answers dormant");
            CollectionAssert.AreEqual(new[] { GauntletGate.DormantLine }, told);

            var plain = MakeGate(marked: false);
            GauntletGate.Register(plain.Guid.Full, target);
            told.Clear();
            Assert.IsFalse(GauntletGate.TryHandleUse(plain, member), "an object without the marker is not a gate, even if a target is bound to its guid");
            Assert.AreEqual(0, told.Count);
        }

        [TestMethod]
        public void Use_TargetThrows_FailsClosed()
        {
            var gate = MakeGate();
            GauntletGate.Register(gate.Guid.Full, target);
            target.ThrowOnAdmits = true;

            Assert.IsTrue(GauntletGate.TryHandleUse(gate, member));

            Assert.AreEqual(0, teleports.Count);
            CollectionAssert.AreEqual(new[] { GauntletGate.ClosedLine }, told);
        }

        [TestMethod]
        public void GenericObject_ActOnUse_DispatchesGateBeforeTheFallbacks()
        {
            // Source pin on the dispatch site: the gate line sits in GenericObject.ActOnUse and returns when it handled the use.
            var path = FindRepoFile("Source/ACE.Server/WorldObjects/GenericObject.cs");
            var text = System.IO.File.ReadAllText(path).Replace("\r\n", "\n");

            StringAssert.Contains(text, "if (ACE.Server.Avatars.Gauntlet.GauntletGate.TryHandleUse(this, player))\n                return;");
            Assert.IsTrue(text.IndexOf("GauntletGate.TryHandleUse", StringComparison.Ordinal) < text.IndexOf("if (UseSound > 0)", StringComparison.Ordinal));
        }

        private static string FindRepoFile(string rel)
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                var candidate = System.IO.Path.Combine(dir.FullName, rel);
                if (System.IO.File.Exists(candidate))
                    return candidate;
                dir = dir.Parent;
            }

            throw new System.IO.FileNotFoundException($"Could not find {rel} by walking up from {AppContext.BaseDirectory}");
        }

        private sealed class FakeTarget : IGauntletGateTarget
        {
            public readonly GauntletArenaHold Hold;
            public Position Entry = GauntletGateTests.Entry;
            public double HoldLeft;
            public int EntryCalls;
            public bool ThrowOnAdmits;

            public FakeTarget(GauntletArenaHold hold) => Hold = hold;

            public GauntletState State => Hold.State;

            public bool Admits(Player player) => ThrowOnAdmits ? throw new InvalidOperationException("boom") : Hold.Admits(player);

            public Position NextEntryPoint()
            {
                EntryCalls++;
                return Entry;
            }

            public double DeathHoldSecondsLeft(Player player) => HoldLeft;
        }
    }
}
