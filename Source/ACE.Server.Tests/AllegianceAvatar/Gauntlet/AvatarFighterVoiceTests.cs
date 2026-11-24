using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Avatars.Gauntlet;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldObjects;

using static ACE.Server.Tests.Avatars.Gauntlet.GauntletFighterFixtures;
using C = ACE.Server.Avatars.Gauntlet.GauntletAvatarFighter.LeaderCandidate;

namespace ACE.Server.Tests.Avatars.Gauntlet
{
    /// <summary>
    /// The fighter's voice (AvatarFighterVoice): the table, the one send site, the audience (participants only), and every
    /// trigger through the code that fires it. No PropertyManager key is read: the period goes through its seam.
    /// </summary>
    [TestClass]
    public class AvatarFighterVoiceTests
    {
        private readonly List<(IReadOnlyList<Player> To, string Text)> sent = new List<(IReadOnlyList<Player>, string)>();

        [ClassInitialize]
        public static void Init(TestContext _) => EnsureInitialized();

        [TestInitialize]
        public void Setup()
        {
            AvatarFighterVoice.ResetSeams();
            AvatarFighterVoice.Sink = (to, text) => sent.Add((to, text));
            AvatarFighterVoice.PickIndex = _ => 0;
            AvatarFighterVoice.PeriodSeconds = () => 120;
            AvatarFighterProbe.Enabled = () => false;
        }

        [TestCleanup]
        public void Cleanup()
        {
            AvatarFighterVoice.ResetSeams();
            AvatarFighterProbe.ResetSeams();
        }

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<Guid>k__BackingField")]
        private static extern ref ObjectGuid GuidField(WorldObject wo);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<Biota>k__BackingField")]
        private static extern ref ACE.Entity.Models.Biota BiotaField(WorldObject wo);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "BiotaDatabaseLock")]
        private static extern ref ReaderWriterLockSlim BiotaLockField(WorldObject wo);

        /// <summary>A bare player with a guid and a name (and so a biota), nothing else.</summary>
        internal static Player Named(uint guid, string name)
        {
            var p = BarePlayer();
            GuidField(p) = new ObjectGuid(guid);
            BiotaField(p) = new ACE.Entity.Models.Biota();
            BiotaLockField(p) = new ReaderWriterLockSlim();
            p.SetProperty(PropertyString.Name, name);
            return p;
        }

        private static string Source(string file) => PooledLootSourceText.Read(file).Replace("\r\n", "\n");

        // ---- the table ------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void Table_EveryTrigger_FourToSixLines_Under200_AsciiNoDashes()
        {
            foreach (FighterVoiceTrigger t in Enum.GetValues(typeof(FighterVoiceTrigger)))
            {
                Assert.IsTrue(AvatarFighterVoice.Lines.TryGetValue(t, out var lines), $"{t} has lines");
                Assert.IsTrue(lines.Length >= 4 && lines.Length <= 6, $"{t}: {lines.Length} lines");

                foreach (var line in lines)
                {
                    var shown = string.Format(line, "Abcdefghijklmnopqrstuvwxyzabcdef");
                    Assert.IsTrue(shown.Length < 200, $"{t}: {shown.Length} chars: {line}");
                    Assert.IsTrue(line.All(c => c >= 0x20 && c <= 0x7e), $"{t}: not ASCII: {line}");
                    Assert.IsFalse(line.Contains("--"), $"{t}: a dash: {line}");
                    Assert.AreEqual(t == FighterVoiceTrigger.LeaderSwitch, line.Contains("{0}"), $"{t}: only the leader switch names the leader: {line}");
                }
            }
        }

        [TestMethod]
        public void Line_FillsTheLeadersName_AndUsesThePick()
        {
            AvatarFighterVoice.PickIndex = n => n - 1;

            var line = AvatarFighterVoice.Line(FighterVoiceTrigger.LeaderSwitch, "Trial Anvil");

            Assert.AreEqual(string.Format(AvatarFighterVoice.Lines[FighterVoiceTrigger.LeaderSwitch].Last(), "Trial Anvil"), line);
            StringAssert.Contains(line, "Trial Anvil");
        }

        // ---- the send -----------------------------------------------------------------------------------------------------

        [TestMethod]
        public void Send_GoesToTheGivenPlayersOnce_NobodyElse()
        {
            var a = Named(0x50000001, "A");
            var b = Named(0x50000002, "B");

            Assert.AreEqual(2, AvatarFighterVoice.Send(new[] { a, null, b, a }, "line"));

            Assert.AreEqual(1, sent.Count);
            CollectionAssert.AreEqual(new[] { a, b }, sent[0].To.ToList(), "deduplicated, nulls dropped, in order");
            Assert.AreEqual(0, AvatarFighterVoice.Send(new[] { a }, ""), "an empty line goes nowhere");
            Assert.AreEqual(0, AvatarFighterVoice.Send(null, "line"));
        }

        [TestMethod]
        public void Send_RealPath_PlayersWithNoSession_AreSkipped()
        {
            AvatarFighterVoice.Sink = null;
            Assert.AreEqual(0, AvatarFighterVoice.Send(new[] { Named(0x50000003, "C") }, "line"), "no session, nothing sent, no throw");
        }

        internal static void AssertSingleSendSite(string voice, IEnumerable<string> others)
        {
            Assert.AreEqual(1, Regex.Matches(voice, @"EnqueueSend\(").Count, "the voice has exactly one send");
            Assert.AreEqual(1, Regex.Matches(voice, @"new GameMessageSystemChat\(").Count, "and builds exactly one message");
            StringAssert.Contains(voice, "network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Allegiance));", "allegiance chat, the text alone: no prefix");
            Assert.IsFalse(voice.Contains("AvatarAnnouncer.") || voice.Contains("ToAllegiance(") || voice.Contains("AllegianceManager."), "never the whole allegiance");

            foreach (var other in others)
                Assert.IsFalse(other.Contains("ChatMessageType.Allegiance"), "no other fighter file sends allegiance chat");
        }

        private static readonly string[] OtherFighterFiles =
        {
            "Source/ACE.Server/AllegianceAvatar/Gauntlet/GauntletAvatarFighter.cs",
            "Source/ACE.Server/AllegianceAvatar/Gauntlet/LiveGauntletFighterHooks.cs",
            "Source/ACE.Server/AllegianceAvatar/Gauntlet/AvatarFighterFactory.cs",
            "Source/ACE.Server/AllegianceAvatar/Gauntlet/AvatarEventFighterHost.cs",
        };

        [TestMethod]
        public void TheVoice_HasOneSendSite_ToParticipantsOnly_AndTheMutationsFail()
        {
            var voice = Source("Source/ACE.Server/AllegianceAvatar/Gauntlet/AvatarFighterVoice.cs");
            var others = OtherFighterFiles.Select(Source).ToList();

            AssertSingleSendSite(voice, others);

            Assert.ThrowsExactly<AssertFailedException>(() => AssertSingleSendSite(voice + "\nfoo.EnqueueSend(x);", others), "a second send");
            Assert.ThrowsExactly<AssertFailedException>(() => AssertSingleSendSite(voice.Replace("ChatMessageType.Allegiance", "ChatMessageType.Broadcast"), others), "the wrong chat type");
            Assert.ThrowsExactly<AssertFailedException>(() => AssertSingleSendSite(voice + "\n// AvatarAnnouncer.ToAllegiance(m, t);", others), "the whole allegiance");
            Assert.ThrowsExactly<AssertFailedException>(() => AssertSingleSendSite(voice, others.Append("x(ChatMessageType.Allegiance)")), "a send elsewhere");
        }

        // ---- the triggers -------------------------------------------------------------------------------------------------

        private static GauntletAvatarFighter Speaking(params Player[] audience)
        {
            var f = Fighter();
            f.Audience = () => audience;
            return f;
        }

        [TestMethod]
        public void Periodic_OnePeriodAfterTheFirstTick_ThenEveryPeriod_OffAtZero()
        {
            var a = Named(0x50000011, "A");
            var f = Speaking(a);

            Assert.IsFalse(f.TickVoice(1000.0), "the first tick arms the clock");
            Assert.IsFalse(f.TickVoice(1119.9));
            Assert.IsTrue(f.TickVoice(1120.0));
            Assert.AreEqual(AvatarFighterVoice.Lines[FighterVoiceTrigger.Periodic][0], sent.Single().Text);
            CollectionAssert.AreEqual(new[] { a }, sent.Single().To.ToList(), "to the fighter's audience");
            Assert.IsFalse(f.TickVoice(1239.9));
            Assert.IsTrue(f.TickVoice(1240.0));

            AvatarFighterVoice.PeriodSeconds = () => 0;
            var g = Speaking(a);
            for (var t = 0.0; t < 1000; t += 10)
                g.TickVoice(t);
            Assert.AreEqual(2, sent.Count, "0 turns the periodic line off");
        }

        [TestMethod]
        public void LeaderSwitch_NamesTheNewLeader_AllDown_WhenEveryoneIsDead()
        {
            var a = Named(0x50000021, "Aldric");
            var b = Named(0x50000022, "Brenna");
            var f = Speaking(a, b);
            var rows = new Dictionary<uint, C> { [a.Guid.Full] = new C(a.Guid.Full, 200, true, true), [b.Guid.Full] = new C(b.Guid.Full, 275, true, true) };
            f.Describe = p => rows[p.Guid.Full];
            f.Participants = () => new[] { a, b };

            f.RefreshLeader(1.0);
            Assert.AreEqual(0, sent.Count, "the first pick says nothing (the spawn line covers it)");

            rows[b.Guid.Full] = new C(b.Guid.Full, 275, false, true);
            f.RefreshLeader(1.1);
            Assert.AreEqual(string.Format(AvatarFighterVoice.Lines[FighterVoiceTrigger.LeaderSwitch][0], "Aldric"), sent.Single().Text);

            rows[a.Guid.Full] = new C(a.Guid.Full, 200, false, true);
            f.RefreshLeader(1.2);
            Assert.AreEqual(AvatarFighterVoice.Lines[FighterVoiceTrigger.AllDown][0], sent.Last().Text);
            Assert.AreEqual(2, sent.Count);
        }

        [TestMethod]
        public void AHigherLevelArrival_TakesTheLead_Silently()
        {
            var a = Named(0x50000051, "Aldric");
            var b = Named(0x50000052, "Brenna");
            var f = Speaking(a, b);
            var rows = new Dictionary<uint, C> { [a.Guid.Full] = new C(a.Guid.Full, 200, true, true) };
            var inside = new List<Player> { a };
            f.Describe = p => rows[p.Guid.Full];
            f.Participants = () => inside.ToList();
            f.RefreshLeader(1.0);
            Assert.AreSame(a, f.Leader);

            rows[b.Guid.Full] = new C(b.Guid.Full, 275, true, true);
            inside.Add(b);
            Assert.IsTrue(f.RefreshLeader(2.1));

            Assert.AreSame(b, f.Leader, "fixture: the arrival leads");
            Assert.AreEqual(0, sent.Count, "the old leader still stands: no leader-switch line");
        }

        [TestMethod]
        public void TheTopMemberBackFromThePen_TakesTheLead_Silently()
        {
            var a = Named(0x50000061, "Aldric");
            var b = Named(0x50000062, "Brenna");
            var f = Speaking(a, b);
            var rows = new Dictionary<uint, C> { [a.Guid.Full] = new C(a.Guid.Full, 200, true, true), [b.Guid.Full] = new C(b.Guid.Full, 275, false, true) };
            f.Describe = p => rows[p.Guid.Full];
            f.Participants = () => new[] { a, b };
            f.RefreshLeader(1.0);
            Assert.AreSame(a, f.Leader);

            rows[b.Guid.Full] = new C(b.Guid.Full, 275, true, true);
            Assert.IsTrue(f.RefreshLeader(2.1));

            Assert.AreSame(b, f.Leader);
            Assert.AreEqual(0, sent.Count, "a return over a leader who still stands: no line");
        }

        [TestMethod]
        public void AHiddenAdmin_IsNeverNamed()
        {
            var a = Named(0x50000071, "Aldric");
            var admin = Named(0x50000072, "Hidden Admin");
            admin.CloakStatus = CloakStatus.On;
            var f = Speaking(a, admin);
            var rows = new Dictionary<uint, C> { [a.Guid.Full] = new C(a.Guid.Full, 200, true, true), [admin.Guid.Full] = new C(admin.Guid.Full, 300, false, true) };
            f.Describe = p => rows[p.Guid.Full];
            f.Participants = () => new[] { a, admin };
            f.RefreshLeader(1.0);

            rows[a.Guid.Full] = new C(a.Guid.Full, 200, false, true);
            rows[admin.Guid.Full] = new C(admin.Guid.Full, 300, true, true);
            f.RefreshLeader(1.1);

            Assert.AreSame(admin, f.Leader, "fixture: the admin now leads");
            Assert.IsFalse(sent.Any(m => m.Text.Contains("Hidden Admin")), "a cloaked admin is never named");
            Assert.IsTrue(AvatarFighterVoice.IsHidden(admin));
            Assert.IsFalse(AvatarFighterVoice.IsHidden(a));
        }

        [TestMethod]
        public void Gauntlet_EveryMemberHeldInThePen_IsTheAllDownLine()
        {
            var h = new FighterRunHarness();
            var a = Named(FighterRunHarness.MemberGuid, "Aldric");
            var b = Named(0x500000A2, "Brenna");
            h.Inside = new List<GauntletOccupant> { new GauntletOccupant(a.Guid.Full, a), new GauntletOccupant(b.Guid.Full, b) };
            var held = new HashSet<uint>();
            h.Held = g => held.Contains(g);

            h.StartActive();
            var f = h.Placed.Single();
            f.IsPresentSeam = _ => true;

            f.RefreshLeader(1.0);
            Assert.IsNotNull(f.Leader, "fixture: a member leads");

            held.Add(f.Leader.Guid.Full);
            f.RefreshLeader(1.1);
            Assert.IsNotNull(f.Leader, "the other member leads");
            Assert.IsFalse(held.Contains(f.Leader.Guid.Full), "a held member is never the leader");

            held.Add(a.Guid.Full);
            held.Add(b.Guid.Full);
            f.RefreshLeader(1.2);

            Assert.IsNull(f.Leader);
            Assert.AreEqual(AvatarFighterVoice.Lines[FighterVoiceTrigger.AllDown][0], sent.Last().Text, "held members are down candidates, so all held is all down");
            CollectionAssert.AreEquivalent(new[] { a, b }, sent.Last().To.ToList(), "to the members inside");
        }

        [TestMethod]
        public void ALeaderWhoLeft_IsNotAllDown()
        {
            var a = Named(0x50000031, "A");
            var f = Speaking(a);
            var row = new C(a.Guid.Full, 200, true, true);
            f.Describe = _ => row;
            f.Participants = () => new[] { a };
            f.RefreshLeader(1.0);

            row = new C(a.Guid.Full, 200, true, false);
            f.RefreshLeader(1.1);

            Assert.IsNull(f.Leader);
            Assert.AreEqual(0, sent.Count, "everyone left, nobody is down: no line");
        }

        [TestMethod]
        public void Death_OneLine_EvenWhenOnDeathRunsTwice()
        {
            var f = Speaking(Named(0x50000041, "A"));

            f.OnDeath(null, DamageType.Slash);
            f.OnDeath(null, DamageType.Slash);

            Assert.AreEqual(AvatarFighterVoice.Lines[FighterVoiceTrigger.Death][0], sent.Single().Text);
        }

        [TestMethod]
        public void Gauntlet_SpawnThenReturn_AndAWaveClearLine()
        {
            var h = new FighterRunHarness();
            var run = h.StartActive();

            Assert.AreEqual(AvatarFighterVoice.Lines[FighterVoiceTrigger.Spawn][0], sent.Single().Text, "the first placement is the spawn line");

            h.Hooks.WaveCleared(run, 1);
            Assert.AreEqual(AvatarFighterVoice.Lines[FighterVoiceTrigger.WaveClear][0], sent.Last().Text);

            h.Placed.Single().OnDeath(null, DamageType.Slash);
            h.Advance(h.Tuning.FighterRespawnSeconds + 1);

            Assert.AreEqual(2, h.Placed.Count, "fixture: respawned");
            Assert.AreEqual(AvatarFighterVoice.Lines[FighterVoiceTrigger.Death][0], sent[2].Text);
            Assert.AreEqual(AvatarFighterVoice.Lines[FighterVoiceTrigger.Return][0], sent[3].Text, "a later placement is the return line");
        }

        [TestMethod]
        public void TheFightersTick_DrivesThePeriodicLine()
        {
            var body = PooledLootSourceText.MethodBody(Source("Source/ACE.Server/AllegianceAvatar/Gauntlet/GauntletAvatarFighter.cs"), "public override void HandleFindTarget()");

            StringAssert.Contains(body, "TickVoice(Timers.RunningTime);", "Monster_Tick reaches HandleFindTarget on every fighter tick; the voice clock rides it");
        }

        [TestMethod]
        public void GauntletRun_WaveClear_CallsTheFighterHook()
        {
            var body = PooledLootSourceText.MethodBody(Source("Source/ACE.Server/AllegianceAvatar/Gauntlet/GauntletRun.cs"),
                "private void WaveClearedLocked(DateTime now, double elapsed, List<Action> after)");

            StringAssert.Contains(body, "after.Add(() => env.Hooks.Fighter.WaveCleared(this, cleared));");
        }
    }
}
