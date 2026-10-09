using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Command.Handlers;
using ACE.Server.Command.Web;
using ACE.Server.Managers.Market;

namespace ACE.Server.Tests
{
    /// <summary>
    /// WebCommandDispatcher (PLAN-P4.md sections 3.2 and 7) against a fake catalog, world and resolver.
    /// No live Player, Session or world loop: the character bucket's "world thread" is a thread the fake
    /// world starts, and its Session is an identity-only object.
    /// </summary>
    [TestClass]
    public class WebCommandDispatcherTests
    {
        private const string Web = WebCommandBuckets.Web;
        private const string Character = WebCommandBuckets.InGameCharacter;
        private const string InGameOnly = WebCommandBuckets.InGameOnly;

        private FakeWebCommandCatalog catalog;
        private FakeWebCommandWorld world;
        private WebCmdRecorder recorder;
        private Dictionary<CommandHandlerInfo, ResolvedCommand> resolutions;
        private int resolveCalls;
        private List<WebCommandDispatcher> dispatchers;
        private List<ManualResetEventSlim> gates;

        [TestInitialize]
        public void Setup()
        {
            catalog = new FakeWebCommandCatalog();
            recorder = new WebCmdRecorder();
            world = new FakeWebCommandWorld { Recorder = recorder, Character = new WebCommandOnlineCharacter(WebCmd.BareSession(), null, "+Weft") };
            resolutions = new Dictionary<CommandHandlerInfo, ResolvedCommand>();
            resolveCalls = 0;
            dispatchers = new List<WebCommandDispatcher>();
            gates = new List<ManualResetEventSlim>();
        }

        [TestCleanup]
        public void Teardown()
        {
            foreach (var gate in gates)
                gate.Set();

            foreach (var dispatcher in dispatchers)
                dispatcher.Dispose();
        }

        private WebCommandDispatcher Dispatcher(int timeoutMs = 5000)
        {
            var dispatcher = new WebCommandDispatcher(catalog, info =>
            {
                Interlocked.Increment(ref resolveCalls);
                return resolutions.TryGetValue(info, out var resolved) ? resolved : WebCmd.Resolved(InGameOnly);
            }, world, () => Environment.TickCount64, timeoutMs);

            dispatchers.Add(dispatcher);
            return dispatcher;
        }

        private ManualResetEventSlim Gate()
        {
            var gate = new ManualResetEventSlim(false);
            gates.Add(gate);
            return gate;
        }

        private WebCmdHandlerProbe Register(string name, string bucket, AccessLevel access = AccessLevel.Developer, CommandHandlerFlag flags = CommandHandlerFlag.None,
            int parameterCount = -1, bool includeRaw = false, string[] rowFlags = null, WebCmdHandlerProbe probe = null)
        {
            probe ??= new WebCmdHandlerProbe();
            probe.Recorder ??= recorder;

            var info = catalog.Add(WebCmd.Info(name, probe.Handler, access, flags, parameterCount, includeRaw, "Needs two.", "<a> <b>"));
            resolutions[info] = WebCmd.Resolved(bucket, rowFlags ?? Array.Empty<string>());
            return probe;
        }

        private static bool NotBusy(WebCommandOutcome outcome) => outcome.Error != MarketError.CommandBusy;

        // ---- the account check comes first ----

        [TestMethod]
        [DataRow(Web, DisplayName = "AccessCheckedBeforeHandler_WebBucket")]
        [DataRow(Character, DisplayName = "AccessCheckedBeforeHandler_CharacterBucket")]
        [DataRow(InGameOnly, DisplayName = "AccessCheckedBeforeHandler_InGameOnlyBucket")]
        public void AccessCheckedBeforeHandler(string bucket)
        {
            var probe = Register("admincmd", bucket, access: AccessLevel.Admin);
            var dispatcher = Dispatcher();

            var outcome = dispatcher.Run(new AdminPrincipal(WebCmd.AccountId, "weft") { Level = AccessLevel.Developer }, "@admincmd x");

            Assert.AreEqual(MarketError.CommandNotPermitted, outcome.Error);
            Assert.AreEqual(0, probe.Calls, "handler");
            Assert.AreEqual(0, resolveCalls, "the bucket must not even be looked at");
            Assert.AreEqual(0, world.FindCalls, "character lookup");
            Assert.AreEqual(0, world.EnqueueCalls, "world queue");
            Assert.AreEqual(0, dispatcher.WorkerStartAttempts, "web worker");
            Assert.AreEqual(0, catalog.GetCommandHandlerCalls, "GetCommandHandler");
        }

        // ---- web bucket ----

        [TestMethod]
        public void WebBucket_NeverCallsGetCommandHandler()
        {
            var probe = Register("webcmd", Web);
            probe.Body = (s, p) => CommandHandlerHelper.WriteOutputInfo(s, "listed 0 players");

            var outcome = Dispatcher().Run(WebCmd.Admin(), "webcmd");

            Assert.AreEqual(MarketError.None, outcome.Error);
            Assert.AreEqual("ok", outcome.Result);
            Assert.IsTrue(outcome.Ran);
            Assert.AreEqual("web", outcome.Bucket);
            Assert.AreEqual("webcmd", outcome.Command);
            Assert.IsNull(outcome.CharacterName);
            Assert.AreEqual(1, probe.Calls);
            Assert.IsTrue(probe.SawNullSession, "the web bucket invokes with a null session");
            Assert.AreEqual(0, catalog.GetCommandHandlerCalls);
            Assert.AreEqual(0, world.FindCalls);
            Assert.AreEqual(0, world.EnqueueCalls);
            Assert.AreEqual(1, outcome.Output.Count);
            Assert.AreEqual("info", outcome.Output[0].Level);
            Assert.AreEqual("listed 0 players", outcome.Output[0].Text);
        }

        [TestMethod]
        public void WebRow_WithLiveRequiresWorld_RefusedInGameOnly()
        {
            var probe = Register("worldcmd", Web, flags: CommandHandlerFlag.RequiresWorld);
            var dispatcher = Dispatcher();

            var outcome = dispatcher.Run(WebCmd.Admin(), "worldcmd");

            Assert.AreEqual(MarketError.CommandInGameOnly, outcome.Error);
            Assert.AreEqual(0, probe.Calls);
            Assert.AreEqual(0, dispatcher.WorkerStartAttempts);
        }

        [TestMethod]
        public void InGameOnlyBucket_Refused_NothingTouched()
        {
            var probe = Register("gameonly", InGameOnly);
            var dispatcher = Dispatcher();

            var outcome = dispatcher.Run(WebCmd.Admin(), "gameonly");

            Assert.AreEqual(MarketError.CommandInGameOnly, outcome.Error);
            Assert.AreEqual(0, probe.Calls);
            Assert.AreEqual(0, dispatcher.WorkerStartAttempts);
            Assert.AreEqual(0, world.FindCalls);
        }

        // ---- real classification: mods and handler identity ----

        private static readonly Lazy<List<(string Command, string Handler, CommandHandlerAttribute Attribute, System.Reflection.MethodInfo Method)>> Registered =
            new Lazy<List<(string, string, CommandHandlerAttribute, System.Reflection.MethodInfo)>>(() => CommandClassification.ReflectRegistered(typeof(CommandManager).Assembly.GetTypes()));

        private WebCommandDispatcher RealClassificationDispatcher()
        {
            var dispatcher = new WebCommandDispatcher(catalog, CommandClassification.LoadEmbedded(), world, () => Environment.TickCount64, 5000);
            dispatchers.Add(dispatcher);
            return dispatcher;
        }

        [TestMethod]
        public void ModRegistered_InGameOnly()
        {
            var probe = new WebCmdHandlerProbe();
            catalog.Add(WebCmd.Info("p4b-mod-command", probe.Handler));
            var dispatcher = RealClassificationDispatcher();

            var outcome = dispatcher.Run(WebCmd.Admin(), "p4b-mod-command");

            Assert.AreEqual(MarketError.CommandInGameOnly, outcome.Error);
            Assert.AreEqual(0, probe.Calls);
            Assert.AreEqual(0, dispatcher.WorkerStartAttempts);
        }

        [TestMethod]
        public void ModOverridesWebCommand_InGameOnly()
        {
            var live = Registered.Value.Single(r => r.Command == "serverstatus");
            var table = CommandClassification.LoadEmbedded();

            // CONTROL: the genuine handler really is web, so the refusal below is about the override.
            Assert.AreEqual(Web, table.Resolve(new CommandHandlerInfo { Attribute = live.Attribute, Handler = Delegate.CreateDelegate(typeof(CommandHandler), live.Method) }).Bucket);

            var probe = new WebCmdHandlerProbe();
            catalog.Add(new CommandHandlerInfo { Attribute = live.Attribute, Handler = probe.Handler });

            var outcome = RealClassificationDispatcher().Run(WebCmd.Admin(), "serverstatus");

            Assert.AreEqual(MarketError.CommandInGameOnly, outcome.Error);
            Assert.AreEqual(0, probe.Calls);
        }

        [TestMethod]
        public void DuplicateName_LiveHandlerWithoutRow_InGameOnly()
        {
            var live = Registered.Value.Single(r => r.Command == "serverstatus");
            var other = Registered.Value.First(r => r.Command != "serverstatus" && r.Method != live.Method);

            // serverstatus's attribute, but a DIFFERENT ACE.Server handler: no reviewed row matches the pair.
            catalog.Add(new CommandHandlerInfo { Attribute = live.Attribute, Handler = Delegate.CreateDelegate(typeof(CommandHandler), other.Method) });
            var dispatcher = RealClassificationDispatcher();

            var outcome = dispatcher.Run(WebCmd.Admin(), "serverstatus");

            Assert.AreEqual(MarketError.CommandInGameOnly, outcome.Error);
            Assert.AreEqual(0, dispatcher.WorkerStartAttempts, "the mismatched handler must never reach the worker");
        }

        // ---- character bucket ----

        [TestMethod]
        public void NoOnlineCharacter_409_NotEnqueued()
        {
            var probe = Register("charcmd", Character);
            world.Character = null;

            var outcome = Dispatcher().Run(WebCmd.Admin(), "charcmd");

            Assert.AreEqual(MarketError.NoCharacterOnline, outcome.Error);
            Assert.AreEqual(1, world.FindCalls);
            Assert.AreEqual(0, world.EnqueueCalls);
            Assert.AreEqual(0, probe.Calls);
        }

        [TestMethod]
        public void CharacterLookupThrows_500_SlotReleased()
        {
            Register("charcmd", Character);
            world.FindThrows = new InvalidOperationException("two sessions for one account");
            var dispatcher = Dispatcher();

            Assert.AreEqual(MarketError.ServerError, dispatcher.Run(WebCmd.Admin(), "charcmd").Error);
            Assert.AreEqual(MarketError.ServerError, dispatcher.Run(WebCmd.Admin(), "charcmd").Error, "a refused run must release the account's slot");
        }

        [TestMethod]
        public void CharacterLeftBeforeStart_409_HandlerNotInvoked()
        {
            var probe = Register("charcmd", Character);
            world.StillEligible = false;

            var outcome = Dispatcher().Run(WebCmd.Admin(), "charcmd");

            Assert.AreEqual(MarketError.NoCharacterOnline, outcome.Error);
            Assert.AreEqual(1, world.EnqueueCalls, "the first check passed, so it was enqueued");
            Assert.AreEqual(0, catalog.GetCommandHandlerCalls, "re-validation runs before GetCommandHandler");
            Assert.AreEqual(0, probe.Calls);
            Assert.AreEqual(0, world.Logs().Count);
            Assert.AreEqual(0, world.Audits().Count);
        }

        [TestMethod]
        public void CharacterBucket_Ok_InvokesWithCharacterSession_OnWorldThread()
        {
            var probe = Register("charcmd", Character);

            var outcome = Dispatcher().Run(WebCmd.Admin(), "charcmd one");

            Assert.AreEqual(MarketError.None, outcome.Error);
            Assert.AreEqual("ok", outcome.Result);
            Assert.AreEqual("in_game_character", outcome.Bucket);
            Assert.AreEqual("+Weft", outcome.CharacterName);
            Assert.AreSame(world.Character.Session, probe.LastSession);
            Assert.AreEqual(world.WorldThreadId, probe.ThreadId);
            Assert.AreEqual(1, catalog.GetCommandHandlerCalls);
        }

        [TestMethod]
        public void CharacterNotStartedInTime_503_ActionLaterDoesNotRun()
        {
            var probe = Register("charcmd", Character);
            world.HoldActions = true;
            var dispatcher = Dispatcher(timeoutMs: 150);

            var outcome = dispatcher.Run(WebCmd.Admin(), "charcmd");

            Assert.AreEqual(MarketError.CommandNotStarted, outcome.Error);

            world.RunHeld();

            Assert.AreEqual(0, probe.Calls, "an abandoned action must not run when the world finally reaches it");
            Assert.AreEqual(0, catalog.GetCommandHandlerCalls);
            Assert.AreEqual(0, world.Audits().Count);

            Assert.AreEqual(MarketError.CommandNotStarted, dispatcher.Run(WebCmd.Admin(), "charcmd").Error, "not started releases the account's slot at once");
        }

        [TestMethod]
        public void CharacterStartedSlow_200Timeout_RanTrue()
        {
            var probe = Register("slowchar", Character);
            probe.Gate = Gate();
            Register("ping", Web);

            // Long enough that a loaded runner still starts the action before the wait expires; the gated
            // handler, not the timeout, is what makes this a timeout result.
            var dispatcher = Dispatcher(timeoutMs: 2000);

            var outcome = dispatcher.Run(WebCmd.Admin(), "slowchar");

            Assert.IsTrue(probe.Entered.Wait(10_000), "the handler must have been entered");

            Assert.AreEqual(MarketError.None, outcome.Error, "a started command is never an error envelope");
            Assert.AreEqual("timeout", outcome.Result);
            Assert.IsTrue(outcome.Ran);
            Assert.AreEqual("+Weft", outcome.CharacterName);

            var busy = dispatcher.Run(WebCmd.Admin(), "ping");
            Assert.AreEqual(MarketError.CommandBusy, busy.Error, "the slot stays held while the command still runs");
            Assert.AreEqual("slowchar", busy.BusyCommand);

            probe.Gate.Set();
            Assert.IsTrue(WebCmd.WaitUntil(() => NotBusy(dispatcher.Run(WebCmd.Admin(), "ping"))), "the slot must be released when the command actually finishes");
        }

        [TestMethod]
        public void CharacterFlagsLag_NotAuthorized_403()
        {
            var probe = Register("charcmd", Character);
            var info = catalog.Entries["charcmd"];
            catalog.OnGetCommandHandler = (s, c, p) => (CommandHandlerResponse.NotAuthorized, info);

            var outcome = Dispatcher().Run(WebCmd.Admin(), "charcmd");

            Assert.AreEqual(MarketError.CommandNotPermitted, outcome.Error);
            Assert.AreEqual(0, probe.Calls);
            Assert.AreEqual(0, world.Logs().Count);
            Assert.AreEqual(0, world.Audits().Count);
        }

        [TestMethod]
        public void CharacterBucket_NotInWorld_200_RanFalse()
        {
            var probe = Register("charcmd", Character);
            var info = catalog.Entries["charcmd"];
            catalog.OnGetCommandHandler = (s, c, p) => (CommandHandlerResponse.NotInWorld, info);

            var outcome = Dispatcher().Run(WebCmd.Admin(), "charcmd");

            Assert.AreEqual(MarketError.None, outcome.Error);
            Assert.AreEqual("not_in_world", outcome.Result);
            Assert.IsFalse(outcome.Ran);
            Assert.AreEqual(0, outcome.Output.Count);
            Assert.AreEqual(0, probe.Calls);
            Assert.AreEqual(0, world.Audits().Count);
        }

        [TestMethod]
        public void CharacterBucket_HandlerReplacedBeforeStart_InGameOnly_NotInvoked()
        {
            var original = Register("charcmd", Character);
            var replacement = new WebCmdHandlerProbe();
            var replacementInfo = WebCmd.Info("charcmd", replacement.Handler);
            catalog.OnGetCommandHandler = (s, c, p) => (CommandHandlerResponse.Ok, replacementInfo);

            var outcome = Dispatcher().Run(WebCmd.Admin(), "charcmd");

            Assert.AreEqual(MarketError.CommandInGameOnly, outcome.Error);
            Assert.AreEqual(0, original.Calls, "the classified handler");
            Assert.AreEqual(0, replacement.Calls, "the replacement handler");
            Assert.AreEqual(0, world.Logs().Count);
            Assert.AreEqual(0, world.Audits().Count);
        }

        // ---- single flight and the worker ----

        [TestMethod]
        public void CommandBusy_NamesHoldingCommand()
        {
            var slow = Register("slowweb", Web);
            slow.Gate = Gate();
            var ping = Register("ping", Web);
            var dispatcher = Dispatcher(timeoutMs: 2000);

            var first = dispatcher.Run(WebCmd.Admin(), "slowweb");
            Assert.IsTrue(slow.Entered.Wait(10_000), "the slow handler must have been entered");
            Assert.AreEqual("timeout", first.Result);

            // A DIFFERENT account: only the single web worker is busy.
            var second = dispatcher.Run(WebCmd.Admin(accountId: 43, accountName: "other"), "ping");

            Assert.AreEqual(MarketError.CommandBusy, second.Error);
            Assert.AreEqual("slowweb", second.BusyCommand);
            Assert.AreEqual(0, ping.Calls);

            slow.Gate.Set();
            Assert.IsTrue(WebCmd.WaitUntil(() => NotBusy(dispatcher.Run(WebCmd.Admin(accountId: 43, accountName: "other"), "ping"))));
        }

        [TestMethod]
        public void SameAccountConcurrent_409()
        {
            var slow = Register("slowweb", Web);
            slow.Gate = Gate();
            Register("charcmd", Character);
            var dispatcher = Dispatcher(timeoutMs: 2000);

            Assert.AreEqual("timeout", dispatcher.Run(WebCmd.Admin(), "slowweb").Result);
            Assert.IsTrue(slow.Entered.Wait(10_000), "the slow handler must have been entered");

            var second = dispatcher.Run(WebCmd.Admin(), "charcmd");

            Assert.AreEqual(MarketError.CommandBusy, second.Error, "a character command from the same account waits for the account's slot, not the web worker");
            Assert.AreEqual("slowweb", second.BusyCommand);
            Assert.AreEqual(0, world.FindCalls, "busy is decided before the character lookup");
        }

        [TestMethod]
        public void LateOutputOfTimedOutRun_NotInAnotherRunsResult()
        {
            var slow = Register("slowweb", Web);
            slow.Gate = Gate();
            slow.Body = (s, p) => CommandHandlerHelper.WriteOutputInfo(null, "late line from slowweb");
            var fast = Register("fastweb", Web);
            fast.Body = (s, p) => CommandHandlerHelper.WriteOutputInfo(null, "fast line");
            var dispatcher = Dispatcher(timeoutMs: 2000);

            var first = dispatcher.Run(WebCmd.Admin(), "slowweb");
            Assert.IsTrue(slow.Entered.Wait(10_000), "the slow handler must have been entered");
            Assert.AreEqual("timeout", first.Result);
            Assert.AreEqual(0, first.Output.Count);

            slow.Gate.Set();

            WebCommandOutcome second = null;
            Assert.IsTrue(WebCmd.WaitUntil(() => NotBusy(second = dispatcher.Run(WebCmd.Admin(), "fastweb"))));

            Assert.AreEqual("ok", second.Result);
            CollectionAssert.AreEqual(new[] { "fast line" }, second.Output.Select(o => o.Text).ToList());
        }

        [TestMethod]
        public void ThreadStartedByHandler_SeesNoContextAfterRun()
        {
            var release = Gate();
            var observed = new ManualResetEventSlim(false);
            object seenAfter = "unset";

            var probe = Register("spawner", Web);
            probe.Body = (s, p) =>
            {
                var t = new Thread(() =>
                {
                    release.Wait(10_000);
                    seenAfter = WebCommandContext.Current;
                    observed.Set();
                }) { IsBackground = true };
                t.Start();
            };

            var outcome = Dispatcher().Run(WebCmd.Admin(), "spawner");
            Assert.AreEqual("ok", outcome.Result);

            release.Set();
            Assert.IsTrue(observed.Wait(10_000));
            Assert.IsNull(seenAfter, "a thread the handler started inherits the context, but once the run is sealed Current must read null");
        }

        private static readonly AsyncLocal<string> FlowProbe = new AsyncLocal<string>();

        [TestMethod]
        public void WebWorker_CreatedWithoutFlowingContext()
        {
            FlowProbe.Value = "constructing-thread-value";
            try
            {
                // Negative control: an ordinary thread started here DOES inherit the value.
                string inherited = null;
                var control = new Thread(() => inherited = FlowProbe.Value);
                control.Start();
                control.Join();
                Assert.AreEqual("constructing-thread-value", inherited);

                string seen = "unset";
                var probe = Register("flowprobe", Web);
                probe.Body = (s, p) => seen = FlowProbe.Value;

                var outcome = Dispatcher().Run(WebCmd.Admin(), "flowprobe");

                Assert.AreEqual("ok", outcome.Result);
                Assert.IsNull(seen, "the worker thread must not inherit the constructing thread's AsyncLocal values");
            }
            finally
            {
                FlowProbe.Value = null;
            }
        }

        // ---- results ----

        [TestMethod]
        [DataRow(Web)]
        [DataRow(Character)]
        public void HandlerThrows_200Exception_TypeNameOnly(string bucket)
        {
            var probe = Register("thrower", bucket);
            probe.Body = (s, p) =>
            {
                CommandHandlerHelper.WriteOutputInfo(null, "before");
                throw new InvalidOperationException("secret detail");
            };
            var dispatcher = Dispatcher();

            var outcome = dispatcher.Run(WebCmd.Admin(), "thrower");

            Assert.AreEqual(MarketError.None, outcome.Error);
            Assert.AreEqual("exception", outcome.Result);
            Assert.IsTrue(outcome.Ran);
            Assert.AreEqual("error", outcome.Output.Last().Level);
            Assert.AreEqual("InvalidOperationException", outcome.Output.Last().Text);
            Assert.IsFalse(outcome.Output.Any(o => o.Text.Contains("secret detail")), "only the exception TYPE reaches the response");

            if (bucket == Web)
                Assert.AreEqual("before", outcome.Output.First().Text, "null-session output before the throw is kept");

            Assert.AreEqual(MarketError.None, dispatcher.Run(WebCmd.Admin(), "thrower").Error, "the slot is released after an exception");
        }

        [TestMethod]
        [DataRow(Web)]
        [DataRow(Character)]
        public void ShortParameters_200Usage_LinesMatchInGame(string bucket)
        {
            var probe = Register("needs2", bucket, parameterCount: 2);

            var outcome = Dispatcher().Run(WebCmd.Admin(), "@needs2 one");

            Assert.AreEqual(MarketError.None, outcome.Error);
            Assert.AreEqual("usage", outcome.Result);
            Assert.IsFalse(outcome.Ran);
            Assert.AreEqual(0, probe.Calls);

            // The three GameActionTalk InvalidParameterCount sends, byte for byte.
            CollectionAssert.AreEqual(new[]
            {
                "Invalid parameter count, got 1, expected 2!",
                "@needs2 - Needs two.",
                "Usage: @needs2 <a> <b>",
            }, outcome.Output.Select(o => o.Text).ToList());

            Assert.AreEqual(0, world.Logs().Count, "usage posts no [CMD_AUDIT]");
            Assert.AreEqual(0, world.Audits().Count, "usage posts no audit line");
        }

        [TestMethod]
        [DataRow("@echoraw hello  big world")]
        [DataRow("echoraw hello  big world")]
        [DataRow("/echoraw hello  big world")]
        public void IncludeRaw_StuffedLikeInGame(string text)
        {
            var probe = Register("echoraw", Web, includeRaw: true);

            var outcome = Dispatcher().Run(WebCmd.Admin(), text);

            Assert.AreEqual("ok", outcome.Result);

            // What GameActionTalk passes for "@echoraw hello  big world": raw = the message minus its "@".
            var inGame = CommandManager.StuffRawIntoParameters("echoraw hello  big world", "echoraw", new[] { "hello", "big", "world" });
            CollectionAssert.AreEqual(inGame, probe.LastParameters);
            CollectionAssert.AreEqual(new[] { "hello  big world", "hello", "big", "world" }, probe.LastParameters);

            CollectionAssert.AreEqual(new[] { "hello", "big", "world" }, world.Logs().Single().Parameters, "[CMD_AUDIT] gets the parsed parameters, as in game");
        }

        [TestMethod]
        public void Sudo_UnknownCommand()
        {
            var sudo = new WebCmdHandlerProbe();
            catalog.Add(WebCmd.Info("sudo", sudo.Handler));
            resolutions[catalog.Entries["sudo"]] = WebCmd.Resolved(Web);
            var target = Register("webcmd", Web);

            var outcome = Dispatcher().Run(WebCmd.Admin(), "sudo webcmd");

            Assert.AreEqual(MarketError.UnknownCommand, outcome.Error, "sudo is refused by name even when something is registered under it");
            Assert.AreEqual(0, sudo.Calls);
            Assert.AreEqual(0, target.Calls);
            Assert.AreEqual(0, resolveCalls);
        }

        [TestMethod]
        public void UnregisteredName_UnknownCommand()
        {
            Assert.AreEqual(MarketError.UnknownCommand, Dispatcher().Run(WebCmd.Admin(), "nosuchcommand").Error);
            Assert.AreEqual(0, resolveCalls);
        }

        [TestMethod]
        [DataRow("@webcmd")]
        [DataRow("/webcmd")]
        [DataRow("webcmd")]
        [DataRow("   @webcmd   ")]
        public void LeadingAtOrSlash_Stripped(string text)
        {
            var probe = Register("webcmd", Web);

            var outcome = Dispatcher().Run(WebCmd.Admin(), text);

            Assert.AreEqual("ok", outcome.Result);
            Assert.AreEqual("webcmd", outcome.Command);
            Assert.AreEqual(1, probe.Calls);
        }

        [TestMethod]
        [DataRow("", DisplayName = "Text_Empty_400")]
        [DataRow("   ", DisplayName = "Text_Whitespace_400")]
        [DataRow("@", DisplayName = "Text_BareAt_400")]
        [DataRow("/", DisplayName = "Text_BareSlash_400")]
        [DataRow("web\tcmd", DisplayName = "Text_ControlChar_Tab_400")]
        [DataRow("webcmd\n", DisplayName = "Text_ControlChar_Newline_400")]
        [DataRow("listplayers x\u2028y", DisplayName = "Text_LineSeparator_400")]
        [DataRow("listplayers x\u2029y", DisplayName = "Text_ParagraphSeparator_400")]
        [DataRow("listplayers \u202Ex", DisplayName = "Text_BidiOverride_400")]
        [DataRow("listplayers x\u200By", DisplayName = "Text_ZeroWidthSpace_400")]
        [DataRow("webcmd x\u0085", DisplayName = "Text_ControlChar_NextLine_400")]
        public void Text_Invalid_400(string text)
        {
            Register("webcmd", Web);

            var outcome = Dispatcher().Run(WebCmd.Admin(), text);

            Assert.AreEqual(MarketError.InvalidCommandText, outcome.Error);
            Assert.AreEqual(0, catalog.LookupCalls);
        }

        [TestMethod]
        public void Text_ControlChar_Bell_400()
        {
            Register("webcmd", Web);

            var outcome = Dispatcher().Run(WebCmd.Admin(), "webcmd " + (char)7);

            Assert.AreEqual(MarketError.InvalidCommandText, outcome.Error);
            Assert.AreEqual(0, catalog.LookupCalls);
        }

        [TestMethod]
        [DataRow(0xD800, -1, DisplayName = "Text_UnpairedSurrogate_HighAtEnd_400")]
        [DataRow(0xD800, 0x78, DisplayName = "Text_UnpairedSurrogate_HighThenLetter_400")]
        [DataRow(0xDC00, -1, DisplayName = "Text_UnpairedSurrogate_LoneLow_400")]
        [DataRow(0xDC00, 0xD800, DisplayName = "Text_UnpairedSurrogate_Reversed_400")]
        public void Text_UnpairedSurrogate_400(int first, int second)
        {
            Register("webcmd", Web);

            // Built from code units: an attribute string is stored as UTF-8, which cannot carry a lone surrogate.
            var text = "listplayers x" + (char)first + (second >= 0 ? ((char)second).ToString() : "");

            var outcome = Dispatcher().Run(WebCmd.Admin(), text);

            Assert.AreEqual(MarketError.InvalidCommandText, outcome.Error);
            Assert.AreEqual(0, catalog.LookupCalls);
        }

        [TestMethod]
        public void Text_ValidSurrogatePair_Allowed()
        {
            // CONTROL for the surrogate rule: a well-formed pair (U+1F600) is an ordinary character.
            var probe = Register("webcmd", Web);

            var outcome = Dispatcher().Run(WebCmd.Admin(), "webcmd " + (char)0xD83D + (char)0xDE00);

            Assert.AreEqual("ok", outcome.Result);
            Assert.AreEqual(1, probe.Calls);
        }

        [TestMethod]
        public void WebExecuteThrowsBeforeInvoke_500_NotTimeout_Released()
        {
            var probe = Register("webcmd", Web);
            var dispatcher = Dispatcher();
            dispatcher.TestHookBeforeWebExecute = () => throw new InvalidOperationException("forced before invoke");

            var outcome = dispatcher.Run(WebCmd.Admin(), "webcmd");

            Assert.AreEqual(MarketError.ServerError, outcome.Error, "a throw before Invoke is a refusal, never 'timeout, ran: true'");
            Assert.IsFalse(outcome.Ran);
            Assert.AreEqual(0, probe.Calls);
            Assert.AreEqual(0, world.Logs().Count);
            Assert.AreEqual(0, world.Audits().Count);

            dispatcher.TestHookBeforeWebExecute = null;
            Assert.AreEqual("ok", dispatcher.Run(WebCmd.Admin(), "webcmd").Result, "the account slot and the worker are released");
        }

        [TestMethod]
        public void Text_Null_400()
        {
            Assert.AreEqual(MarketError.InvalidCommandText, Dispatcher().Run(WebCmd.Admin(), null).Error);
        }

        [TestMethod]
        public void Text_TooLong_400()
        {
            var probe = Register("webcmd", Web);
            var dispatcher = Dispatcher();

            var exactly1000 = "webcmd " + new string('a', 1000 - "webcmd ".Length);
            Assert.AreEqual(1000, exactly1000.Length);
            Assert.AreEqual("ok", dispatcher.Run(WebCmd.Admin(), exactly1000).Result, "CONTROL: 1000 characters is allowed");

            var outcome = dispatcher.Run(WebCmd.Admin(), exactly1000 + "a");

            Assert.AreEqual(MarketError.InvalidCommandText, outcome.Error);
            Assert.AreEqual(1, probe.Calls);
        }

        // ---- audit (R4, R11) ----

        [TestMethod]
        public void AuditLine_WebBucket_OnWorkerThread_ImmediatelyBeforeInvoke()
        {
            Register("auditweb", Web);

            var outcome = Dispatcher().Run(WebCmd.Admin(), "auditweb a b");

            Assert.AreEqual("ok", outcome.Result);

            var events = recorder.Snapshot();
            CollectionAssert.AreEqual(new[] { "log", "audit", "invoke" }, events.Select(e => e.What).ToList());
            Assert.AreEqual(1, events.Select(e => e.Thread).Distinct().Count(), "all three on one thread");
            Assert.AreNotEqual(Environment.CurrentManagedThreadId, events[0].Thread, "never on the request thread");

            var audit = world.Audits().Single();
            Assert.AreEqual("weft", audit.Label, "the web account name, the same label the Settings and Announce routes use");
            Assert.AreEqual("ran @auditweb a b via the web console", audit.Message);
            Assert.IsNull(world.Logs().Single().Session, "[CMD_AUDIT] for the web bucket has a null session");
        }

        [TestMethod]
        public void AuditLine_CharacterBucket_OnWorldThread_ImmediatelyBeforeInvoke()
        {
            Register("auditchar", Character);

            var outcome = Dispatcher().Run(WebCmd.Admin(), "auditchar");

            Assert.AreEqual("ok", outcome.Result);

            var events = recorder.Snapshot();
            CollectionAssert.AreEqual(new[] { "log", "audit", "invoke" }, events.Select(e => e.What).ToList());
            Assert.IsTrue(events.All(e => e.Thread == world.WorldThreadId), "all three on the world thread");

            var audit = world.Audits().Single();
            Assert.AreEqual("weft via +Weft", audit.Label);
            Assert.AreEqual("ran @auditchar via the web console", audit.Message);
            Assert.AreSame(world.Character.Session, world.Logs().Single().Session);
        }

        [TestMethod]
        public void AuditLine_RedactedCommand_ParametersRedacted()
        {
            Register("accountcreate", Web, access: AccessLevel.Admin);

            Dispatcher().Run(WebCmd.Admin(), "accountcreate bob hunter2");

            var audit = world.Audits().Single();
            Assert.AreEqual("ran @accountcreate bob [redacted] via the web console", audit.Message);
            Assert.IsFalse(audit.Message.Contains("hunter2"));
        }

        [TestMethod]
        public void AuditPostThrows_NotInvoked_500()
        {
            var probe = Register("auditweb", Web);
            world.PostAuditThrows = new InvalidOperationException("audit sink down");
            var dispatcher = Dispatcher();

            var outcome = dispatcher.Run(WebCmd.Admin(), "auditweb");

            Assert.AreEqual(MarketError.ServerError, outcome.Error, "a command must not run without its audit line");
            Assert.AreEqual(0, probe.Calls);

            world.PostAuditThrows = null;
            Assert.AreEqual("ok", dispatcher.Run(WebCmd.Admin(), "auditweb").Result, "the slot and worker are released");
        }

        [TestMethod]
        public void SelfAuditRow_PostsNoGenericAuditLine()
        {
            var probe = Register("modifyfake", Web, access: AccessLevel.Admin, rowFlags: new[] { CommandClassification.FlagSelfAudit });

            var outcome = Dispatcher().Run(WebCmd.Admin(), "modifyfake pk_server true");

            Assert.AreEqual("ok", outcome.Result);
            Assert.AreEqual(1, probe.Calls);
            Assert.AreEqual(0, world.Audits().Count, "R11: the service's own line is the one line");
            Assert.AreEqual(1, world.Logs().Count, "[CMD_AUDIT] is still written");
        }

        [TestMethod]
        public void PlayerLevelCommand_PostsNoGenericAuditLine()
        {
            Register("playercmd", Web, access: AccessLevel.Player);

            Assert.AreEqual("ok", Dispatcher().Run(WebCmd.Admin(), "playercmd").Result);
            Assert.AreEqual(0, world.Audits().Count, "R4 covers access above Player only");
        }

        [TestMethod]
        [DataRow("unknown")]
        [DataRow("not_permitted")]
        [DataRow("in_game_only")]
        [DataRow("busy")]
        [DataRow("no_character")]
        [DataRow("not_started")]
        [DataRow("usage")]
        [DataRow("not_in_world")]
        [DataRow("handler_replaced")]
        [DataRow("character_flags")]
        public void Refusals_PostNoAuditLine(string scenario)
        {
            var timeoutMs = 5000;
            var text = "refused";
            var principal = WebCmd.Admin();
            WebCmdHandlerProbe refused = null;

            switch (scenario)
            {
                case "unknown":
                    text = "refusednotregistered";
                    break;
                case "not_permitted":
                    refused = Register("refused", Web, access: AccessLevel.Admin);
                    principal = new AdminPrincipal(WebCmd.AccountId, "weft") { Level = AccessLevel.Envoy };
                    break;
                case "in_game_only":
                    refused = Register("refused", InGameOnly);
                    break;
                case "busy":
                    var holder = Register("holder", Web);
                    holder.Gate = Gate();
                    refused = Register("refused", Web);
                    timeoutMs = 2000;
                    break;
                case "no_character":
                    refused = Register("refused", Character);
                    world.Character = null;
                    break;
                case "not_started":
                    refused = Register("refused", Character);
                    world.HoldActions = true;
                    timeoutMs = 150;
                    break;
                case "usage":
                    refused = Register("refused", Web, parameterCount: 3);
                    break;
                case "not_in_world":
                    refused = Register("refused", Character);
                    catalog.OnGetCommandHandler = (s, c, p) => (CommandHandlerResponse.NotInWorld, catalog.Entries["refused"]);
                    break;
                case "handler_replaced":
                    refused = Register("refused", Character);
                    var replacement = WebCmd.Info("refused", new WebCmdHandlerProbe().Handler);
                    catalog.OnGetCommandHandler = (s, c, p) => (CommandHandlerResponse.Ok, replacement);
                    break;
                case "character_flags":
                    refused = Register("refused", Character);
                    catalog.OnGetCommandHandler = (s, c, p) => (CommandHandlerResponse.NotAuthorized, catalog.Entries["refused"]);
                    break;
            }

            var dispatcher = Dispatcher(timeoutMs);

            if (scenario == "busy")
                Assert.AreEqual("timeout", dispatcher.Run(WebCmd.Admin(accountId: 7, accountName: "holder-account"), "holder").Result);

            var outcome = dispatcher.Run(principal, text);

            Assert.IsTrue(outcome.Error != MarketError.None || !outcome.Ran, $"{scenario}: expected a refusal or a not-run result");
            Assert.AreEqual(0, refused?.Calls ?? 0, $"{scenario}: the handler must not run");
            Assert.IsFalse(world.Audits().Any(a => a.Message.Contains("@refused")), $"{scenario}: a refusal posts no audit line");
            Assert.IsFalse(world.Logs().Any(l => l.Command.StartsWith("refused", StringComparison.Ordinal)), $"{scenario}: a refusal writes no [CMD_AUDIT]");
        }
    }
}
