using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameAction;
using ACE.Server.Network.GameAction.Actions;
using ACE.Server.Network.GameMessages;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// WaffleACE slow-tick capture: a chat @command is charged to cmd_&lt;registered name&gt; instead of
    /// ga_Talk. Every test drives the REAL GameActionTalk.Handle inside the same scope sequence the inbound
    /// message path uses (BeginMessage as GameAction, RelabelAsGameAction(Talk), handler, EndInvoke), then
    /// reads what InboundOpcodeProfile actually charged.
    /// </summary>
    [TestClass]
    public class SlowTickCommandAttributionTests
    {
        private const string ProbeCommand = "slowtickcmdprobe";

        private static int probeInvocations;

        public static void ProbeHandler(Session session, string[] parameters) => probeInvocations++;

        [TestInitialize]
        public void Setup()
        {
            // GameMessageSystemChat (the "Unknown command" reply) writes cp1252, as ACE.Server's Program does at startup
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            WorldTickProfile.BeginIteration(); // claims this thread as the world thread, as the world loop does
            InboundOpcodeProfile.Enabled = true;
            InboundOpcodeProfile.BeginPhase();
            probeInvocations = 0;
        }

        [TestCleanup]
        public void Teardown()
        {
            InboundOpcodeProfile.Enabled = false;
            InboundOpcodeProfile.BeginPhase();
            CommandProfileIds.ResetForTests();
            CommandManager.TryRemoveCommand(ProbeCommand);
        }

        /// <summary>A Talk game action's payload: a String16L, padded to 4 bytes, after the 4-byte opcode ClientMessage reads.</summary>
        private static ClientMessage TalkMessage(string text)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);

            w.Write((uint)GameMessageOpcode.GameAction);
            w.Write((ushort)text.Length);
            w.Write(Encoding.ASCII.GetBytes(text));

            while ((ms.Length - 4) % 4 != 0)
                w.Write((byte)0);

            return new ClientMessage(ms.ToArray());
        }

        /// <summary>A Session whose NetworkSession can run EnqueueSend with no socket (same shape as WebCommandContextTests).</summary>
        private static Session SessionWithNetwork()
        {
            const BindingFlags Instance = BindingFlags.NonPublic | BindingFlags.Instance;

            var session = (Session)RuntimeHelpers.GetUninitializedObject(typeof(Session));
            var network = (NetworkSession)RuntimeHelpers.GetUninitializedObject(typeof(NetworkSession));

            var count = (int)GameMessageGroup.QueueMax;
            var locks = new object[count];
            var bundles = new NetworkBundle[count];

            for (var i = 0; i < count; i++)
            {
                locks[i] = new object();
                bundles[i] = new NetworkBundle();
            }

            typeof(NetworkSession).GetField("session", Instance).SetValue(network, session);
            typeof(NetworkSession).GetField("currentBundleLocks", Instance).SetValue(network, locks);
            typeof(NetworkSession).GetField("currentBundles", Instance).SetValue(network, bundles);

            session.Network = network;
            return session;
        }

        /// <summary>One inbound Talk action through the production scope API; returns the key it was charged to.</summary>
        private static long RunTalk(string text, Session session)
        {
            InboundOpcodeProfile.BeginPhase();

            var scope = InboundOpcodeProfile.BeginMessage((int)GameMessageOpcode.GameAction);

            try
            {
                InboundOpcodeProfile.RelabelAsGameAction((int)GameActionType.Talk);

                GameActionTalk.Handle(TalkMessage(text), session);
            }
            finally
            {
                InboundOpcodeProfile.EndInvoke(scope);
            }

            Assert.AreEqual(1, InboundOpcodeProfile.FilledCount, "exactly one charge per message");
            Assert.AreEqual(1, InboundOpcodeProfile.CallsAt(0));

            return InboundOpcodeProfile.KeyAt(0);
        }

        private static string Render(long key)
        {
            var s = new SlowTickSnapshot();
            s.OfferOpcode(key, 1, 1, 1);

            var sb = new StringBuilder();
            SlowTickLine.Append(sb, s);

            var line = sb.ToString();
            var at = line.IndexOf(" op1=", StringComparison.Ordinal) + 5;

            return line.Substring(at, line.IndexOf(' ', at) - at);
        }

        [TestMethod]
        public void ACommandIsChargedToItsRegisteredNameNotToTalk()
        {
            Assert.IsTrue(CommandManager.TryAddCommand(ProbeHandler, ProbeCommand, AccessLevel.Player));

            // A null session takes GetCommandHandler's console-style path, which resolves a Player-access
            // command with no RequiresWorld flag to Ok without needing a live Player.
            var key = RunTalk("@SlowTickCmdProbe some args", null);

            Assert.AreEqual(1, probeInvocations, "control: the command handler really ran inside the scope");
            Assert.AreEqual(InboundOpcodeProfile.CommandFlag, key & InboundOpcodeProfile.CommandFlag, "charged as a command");
            Assert.AreEqual("cmd_" + ProbeCommand, Render(key), "the REGISTERED name, not the typed casing or arguments");
            Assert.AreNotEqual(InboundOpcodeProfile.ActionKey((int)GameActionType.Talk), key);
        }

        [TestMethod]
        public void AnUnknownCommandIsChargedToCmdUnknown()
        {
            var key = RunTalk("@nosuchcommand_" + Guid.NewGuid().ToString("N") + " x", SessionWithNetwork());

            Assert.AreEqual(InboundOpcodeProfile.CommandKey(CommandProfileIds.Unknown), key);
            Assert.AreEqual("cmd_unknown", Render(key), "the typed text must never become a label");
        }

        [TestMethod]
        public void PlainChatStaysGaTalk()
        {
            var session = SessionWithNetwork();

            // An inert Player: the chat branch is entered and throws on its first property read, which is
            // enough - no relabel can happen after that point, and the throw proves which branch ran.
            var player = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));
            typeof(Session).GetProperty(nameof(Session.Player)).SetValue(session, player);

            long key = 0;
            Exception thrown = null;

            try
            {
                key = RunTalk("hello there @notacommand", session);
            }
            catch (Exception ex)
            {
                thrown = ex;
                key = InboundOpcodeProfile.KeyAt(0);
            }

            Assert.IsNotNull(thrown, "control: the inert player's chat path was expected to throw");
            StringAssert.Contains(thrown.StackTrace, "HandleActionTalk", "control: the ordinary chat branch must be the one that ran");
            Assert.AreEqual(1, InboundOpcodeProfile.FilledCount);
            Assert.AreEqual(InboundOpcodeProfile.ActionKey((int)GameActionType.Talk), key);
            Assert.AreEqual("ga_Talk", Render(key));
        }

        [TestMethod]
        public void NothingIsRelabelledWhileTheCaptureIsOff()
        {
            Assert.IsTrue(CommandManager.TryAddCommand(ProbeHandler, ProbeCommand, AccessLevel.Player));

            InboundOpcodeProfile.Enabled = false;

            InboundOpcodeProfile.BeginPhase();
            var scope = InboundOpcodeProfile.BeginMessage((int)GameMessageOpcode.GameAction);
            InboundOpcodeProfile.RelabelAsGameAction((int)GameActionType.Talk);
            GameActionTalk.Handle(TalkMessage("@" + ProbeCommand), null);
            InboundOpcodeProfile.EndInvoke(scope);

            Assert.AreEqual(1, probeInvocations, "the command still runs");
            Assert.AreEqual(0, InboundOpcodeProfile.FilledCount, "and nothing is charged");
        }
    }
}
