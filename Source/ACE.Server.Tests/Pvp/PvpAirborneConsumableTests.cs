using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.Pvp;
using ACE.Server.Pvp.Rules;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// The L3 airborne / interval consumable block (Docs/Pvp/DESIGN.md "PvP rules (levers)"): a healing kit,
    /// food or gem use that COMPLETES while the actor is airborne is refused (pvp_block_airborne_consumables),
    /// and a completion inside the transient minimum interval since the actor's last consumable use is refused
    /// too (pvp_consumable_min_interval_ms), both ONLY while the actor is bound to a LIVE arena match
    /// (PvpPlayerRules.InLiveMatch). ARENA ONLY since the owner ruling of 2026-09-27: a PK whose PK timer is running
    /// but who is not in a Live match (which the old, since-deleted engagement gate counted) is allowed and reads neither setting.
    /// The two reasons are distinguished by <see cref="PvpConsumableBlock"/> so the airborne refusal keeps the
    /// existing WeenieError.YouCantDoThatWhileInTheAir while the interval refusal gets its own system-chat text
    /// (a grounded player must never be told they can't act "while in the air").
    ///
    /// Exercised through PvpRules.TryBlockConsumable / ShouldBlockConsumable directly - a real Player cannot be
    /// constructed without the world database, and Player.IsJumping reads PhysicsObj, so isJumping and
    /// lastUseUtc are passed as explicit parameters exactly as the production call sites (Healer.cs H1,
    /// Player_Use.cs H2) pass them from the live actor. Which call site passes which point, that H2 wraps the
    /// SAME action for both ApplyConsumable branches, that the wrapper never sends its own UseDone on refusal
    /// (both branches already send exactly one, unconditionally, once their own use ends), and that the
    /// interval stamp only happens after action() returns, are all pinned by source text below (matched on
    /// CODE only).
    ///
    /// SEEDING: the dials come from a swapped PvpRuleTunables.DialSource and "now" from a swapped
    /// PvpRules.UtcNowSource. The one PropertyManager key read is pk_timer, by the PK-timer-only fixture's
    /// PKTimerActive preconditions, so ClassInitialize seeds every registered default through
    /// DefaultPropertyManager.LoadDefaultProperties() (defaults only - nothing to restore). Every seam
    /// (DialSource, UtcNowSource, Observer) is saved and restored per test.
    /// </summary>
    [TestClass]
    public class PvpAirborneConsumableTests
    {
        [ClassInitialize]
        public static void ClassSetup(TestContext context)
        {
            ACE.Server.Managers.DefaultPropertyManager.LoadDefaultProperties();
        }

        private static readonly DateTime Now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private Func<PvpRuleDials> savedDialSource;
        private Func<DateTime> savedUtcNowSource;
        private Action<PvpChokePoint, double, double> savedObserver;

        private List<(PvpChokePoint Point, double Before, double After)> observed;

        [TestInitialize]
        public void Setup()
        {
            savedDialSource = PvpRuleTunables.DialSource;
            savedUtcNowSource = PvpRules.UtcNowSource;
            savedObserver = PvpRules.Observer;

            observed = new List<(PvpChokePoint, double, double)>();

            PvpRules.UtcNowSource = () => Now;
            PvpRules.Observer = (p, b, a) => observed.Add((p, b, a));
        }

        [TestCleanup]
        public void Cleanup()
        {
            PvpRuleTunables.DialSource = savedDialSource;
            PvpRules.UtcNowSource = savedUtcNowSource;
            PvpRules.Observer = savedObserver;
        }

        // ================= fixtures =================

        private static PvpRuleDials Dials(bool blockAirborne, long minIntervalMs, bool enabled = true) =>
            PvpRuleTunables.Defaults with { Enabled = enabled, BlockAirborneConsumables = blockAirborne, ConsumableMinIntervalMs = minIntervalMs };

        private static void UseDials(PvpRuleDials dials) => PvpRuleTunables.DialSource = () => dials;

        private static Player Seeded()
        {
            var player = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));

            SetInherited(player, "<Biota>k__BackingField", new ACE.Entity.Models.Biota());
            SetInherited(player, "BiotaDatabaseLock", new ReaderWriterLockSlim());

            return player;
        }

        private static void SetInherited(WorldObject wo, string name, object value)
        {
            var field = typeof(WorldObject).GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(field, $"WorldObject.{name} was not found by reflection - has it been renamed?");
            field.SetValue(wo, value);
        }

        private static PvpMatch NewMatch()
        {
            var teams = new List<PvpTeam>
            {
                new PvpTeam(0, new List<PvpParticipant> { new PvpParticipant(1, 1500) }),
                new PvpTeam(1, new List<PvpParticipant> { new PvpParticipant(2, 1500) })
            };

            return new PvpMatch(Guid.NewGuid(), "arena_1v1", teams, Now);
        }

        /// <summary>Bound to a Live arena match (the gate these settings require) - never touches pk_timer.</summary>
        private static Player ArenaBoundPlayer()
        {
            var player = Seeded();
            player.PlayerKillerStatus = PlayerKillerStatus.NPK;
            player.SetPvpBindingForTests(new PvpPlayerBinding(NewMatch(), 0, PvpMatchState.Live, false, false, false, false, false));
            return player;
        }

        /// <summary>NPK with no arena binding: outside the arena gate.</summary>
        private static Player UnboundPlayer()
        {
            var player = Seeded();
            player.PlayerKillerStatus = PlayerKillerStatus.NPK;
            return player;
        }

        // ================= arena-bound + airborne =================

        [TestMethod]
        public void ArenaBound_Airborne_IsRefused_WithAirborneReason_AndReported()
        {
            UseDials(Dials(blockAirborne: true, minIntervalMs: 0));

            var actor = ArenaBoundPlayer();

            var block = PvpRules.TryBlockConsumable(PvpChokePoint.H1, actor, isJumping: true, lastUseUtc: null);

            Assert.AreEqual(PvpConsumableBlock.Airborne, block, "an arena-bound, airborne actor must be refused for Airborne");
            Assert.AreEqual(1, observed.Count);
            Assert.AreEqual(PvpChokePoint.H1, observed[0].Point);
            Assert.AreEqual(1.0, observed[0].Before);
            Assert.AreEqual(0.0, observed[0].After);
        }

        [TestMethod]
        public void ArenaBound_Airborne_IsRefused_AtH2Too()
        {
            UseDials(Dials(blockAirborne: true, minIntervalMs: 0));

            var actor = ArenaBoundPlayer();

            Assert.AreEqual(PvpConsumableBlock.Airborne, PvpRules.TryBlockConsumable(PvpChokePoint.H2, actor, isJumping: true, lastUseUtc: null));
            Assert.AreEqual(PvpChokePoint.H2, observed[0].Point);
        }

        [TestMethod]
        public void ArenaBound_NotAirborne_IsAllowed()
        {
            UseDials(Dials(blockAirborne: true, minIntervalMs: 0));

            var actor = ArenaBoundPlayer();

            Assert.AreEqual(PvpConsumableBlock.None, PvpRules.TryBlockConsumable(PvpChokePoint.H1, actor, isJumping: false, lastUseUtc: null));
            Assert.AreEqual(0, observed.Count);
        }

        // ================= NOT arena-bound: the control =================

        /// <summary>
        /// Control: the same airborne actor, NOT bound to a Live arena match, is allowed - and the dials are never even read,
        /// because the arena gate is checked before ReadDials.
        /// </summary>
        [TestMethod]
        public void Unbound_Airborne_IsAllowed_AndReadsNoLever()
        {
            var dialReads = 0;
            PvpRuleTunables.DialSource = () => { dialReads++; return Dials(blockAirborne: true, minIntervalMs: 1_000_000); };

            var actor = UnboundPlayer();

            Assert.AreEqual(PvpConsumableBlock.None, PvpRules.TryBlockConsumable(PvpChokePoint.H1, actor, isJumping: true, lastUseUtc: Now));
            Assert.AreEqual(0, dialReads, "an actor outside a Live arena match must never read the PvP levers");
            Assert.AreEqual(0, observed.Count);
        }

        [TestMethod]
        public void NullActor_IsAllowed()
        {
            Assert.AreEqual(PvpConsumableBlock.None, PvpRules.TryBlockConsumable(PvpChokePoint.H1, null, isJumping: true, lastUseUtc: null));
        }

        // ================= ARENA ONLY (owner ruling 2026-09-27): a PK timer alone never applies either setting =================

        /// <summary>A PK whose PK timer IS running (the old, since-deleted engagement gate counted this), with no arena binding.</summary>
        private static Player PkTimerOnlyPlayer()
        {
            var pk = Seeded();
            pk.PlayerKillerStatus = PlayerKillerStatus.PK;
            pk.LastPkAttackTimestamp = ACE.Common.Time.GetUnixTime();

            Assert.IsTrue(pk.PKTimerActive, "precondition: the PK timer is running (the old gate counted this as engaged)");
            Assert.IsNull(pk.PvpBinding, "precondition: no arena binding");

            return pk;
        }

        /// <summary>
        /// A PK-timer-only actor, airborne AND inside a huge interval, with both settings on, is allowed at H1 and
        /// H2, and neither setting is read: the arena gate runs before ReadDials. Under the old engagement gate this
        /// actor was refused for Airborne.
        /// </summary>
        [TestMethod]
        public void PkTimerOnly_AirborneAndInsideInterval_IsAllowed_AtH1AndH2_AndReadsNoLever()
        {
            var dialReads = 0;
            PvpRuleTunables.DialSource = () => { dialReads++; return Dials(blockAirborne: true, minIntervalMs: 1_000_000); };

            var pk = PkTimerOnlyPlayer();

            Assert.AreEqual(PvpConsumableBlock.None, PvpRules.TryBlockConsumable(PvpChokePoint.H1, pk, isJumping: true, lastUseUtc: null), "H1 airborne");
            Assert.AreEqual(PvpConsumableBlock.None, PvpRules.TryBlockConsumable(PvpChokePoint.H2, pk, isJumping: true, lastUseUtc: Now), "H2 airborne and inside the interval");
            Assert.AreEqual(PvpConsumableBlock.None, PvpRules.TryBlockConsumable(PvpChokePoint.H2, pk, isJumping: false, lastUseUtc: Now.AddMilliseconds(-1)), "H2 grounded, inside the interval");

            Assert.AreEqual(0, dialReads, "a PK-timer-only actor must never read pvp_block_airborne_consumables or pvp_consumable_min_interval_ms");
            Assert.AreEqual(0, observed.Count);
        }

        /// <summary>
        /// THE AUDIT SCENARIO: a PK with the PK timer running, fighting a creature, drinks a Health potion mid-jump
        /// half a second after the previous one. Both H2 checks run in the wrapper's order (the overtime refusal,
        /// then the consumable rules) and neither refuses; zero setting reads.
        /// </summary>
        [TestMethod]
        public void Audit_PkTimerActive_DrinkingAPotion_WhileFightingACreature_IsAllowed()
        {
            var dialReads = 0;
            PvpRuleTunables.DialSource = () => { dialReads++; return Dials(blockAirborne: true, minIntervalMs: 5000); };

            var pk = PkTimerOnlyPlayer();

            Assert.AreEqual(PvpConsumableBlock.None, PvpRules.TryBlockOvertimeHeal(PvpChokePoint.H2, pk), "overtime half of H2");
            Assert.AreEqual(PvpConsumableBlock.None, PvpRules.TryBlockConsumable(PvpChokePoint.H2, pk, isJumping: true, lastUseUtc: Now.AddMilliseconds(-500)), "consumable rules half of H2");

            Assert.AreEqual(0, dialReads);
            Assert.AreEqual(0, observed.Count);
        }

        /// <summary>Positive control for the two tests above: the SAME PK, timer still running, once bound to a Live arena match, is refused for both reasons.</summary>
        [TestMethod]
        public void Control_PkTimerActive_AndArenaBound_IsRefused()
        {
            UseDials(Dials(blockAirborne: true, minIntervalMs: 5000));

            var pk = PkTimerOnlyPlayer();
            pk.SetPvpBindingForTests(new PvpPlayerBinding(NewMatch(), 0, PvpMatchState.Live, false, false, false, false, false));

            Assert.AreEqual(PvpConsumableBlock.Airborne, PvpRules.TryBlockConsumable(PvpChokePoint.H1, pk, isJumping: true, lastUseUtc: null));
            Assert.AreEqual(PvpConsumableBlock.Interval, PvpRules.TryBlockConsumable(PvpChokePoint.H2, pk, isJumping: false, lastUseUtc: Now.AddMilliseconds(-500)));
            Assert.AreEqual(2, observed.Count);
        }

        /// <summary>Only a LIVE match counts: a player bound to a match still in Staging is allowed and reads nothing.</summary>
        [TestMethod]
        public void StagingBound_Airborne_IsAllowed_AndReadsNoLever()
        {
            var dialReads = 0;
            PvpRuleTunables.DialSource = () => { dialReads++; return Dials(blockAirborne: true, minIntervalMs: 1_000_000); };

            var staging = UnboundPlayer();
            staging.SetPvpBindingForTests(new PvpPlayerBinding(NewMatch(), 0, PvpMatchState.Staging, false, false, false, false, false));

            Assert.AreEqual(PvpConsumableBlock.None, PvpRules.TryBlockConsumable(PvpChokePoint.H2, staging, isJumping: true, lastUseUtc: Now));
            Assert.AreEqual(0, dialReads);
        }

        // ================= interval =================

        [TestMethod]
        public void Interval_WithinWindow_IsRefused_WithIntervalReason_ThenAfterElapsing_IsAllowed()
        {
            UseDials(Dials(blockAirborne: false, minIntervalMs: 5000));

            var actor = ArenaBoundPlayer();

            // last use 2s ago, interval 5s: still inside the window
            var lastUse = Now.AddMilliseconds(-2000);
            Assert.AreEqual(PvpConsumableBlock.Interval, PvpRules.TryBlockConsumable(PvpChokePoint.H2, actor, isJumping: false, lastUseUtc: lastUse), "2s < 5s interval must refuse for Interval");

            // last use 6s ago: past the window
            lastUse = Now.AddMilliseconds(-6000);
            Assert.AreEqual(PvpConsumableBlock.None, PvpRules.TryBlockConsumable(PvpChokePoint.H2, actor, isJumping: false, lastUseUtc: lastUse), "6s > 5s interval must allow");
        }

        [TestMethod]
        public void Interval_NoPriorUse_IsAllowed()
        {
            UseDials(Dials(blockAirborne: false, minIntervalMs: 5000));

            Assert.AreEqual(PvpConsumableBlock.None, PvpRules.TryBlockConsumable(PvpChokePoint.H2, ArenaBoundPlayer(), isJumping: false, lastUseUtc: null), "no recorded prior use never triggers the interval");
        }

        /// <summary>Airborne is checked before the interval, so a grounded-but-in-window actor gets Interval, never Airborne.</summary>
        [TestMethod]
        public void AirborneAndInterval_AreDistinctReasons()
        {
            UseDials(Dials(blockAirborne: true, minIntervalMs: 5000));

            var actor = ArenaBoundPlayer();

            Assert.AreEqual(PvpConsumableBlock.Airborne, PvpRules.TryBlockConsumable(PvpChokePoint.H2, actor, isJumping: true, lastUseUtc: null), "airborne wins even with no prior use");
            Assert.AreEqual(PvpConsumableBlock.Interval, PvpRules.TryBlockConsumable(PvpChokePoint.H2, actor, isJumping: false, lastUseUtc: Now.AddMilliseconds(-1000)), "grounded, inside the window: Interval, never Airborne");
        }

        // ================= setting off =================

        [TestMethod]
        public void BothLeversOff_Airborne_IsAllowed()
        {
            UseDials(Dials(blockAirborne: false, minIntervalMs: 0));

            var actor = ArenaBoundPlayer();

            Assert.AreEqual(PvpConsumableBlock.None, PvpRules.TryBlockConsumable(PvpChokePoint.H1, actor, isJumping: true, lastUseUtc: Now.AddMilliseconds(-1)));
            Assert.AreEqual(0, observed.Count);
        }

        [TestMethod]
        public void MasterSwitchOff_Airborne_IsAllowed()
        {
            UseDials(Dials(blockAirborne: true, minIntervalMs: 1_000_000, enabled: false));

            var actor = ArenaBoundPlayer();

            Assert.AreEqual(PvpConsumableBlock.None, PvpRules.TryBlockConsumable(PvpChokePoint.H1, actor, isJumping: true, lastUseUtc: Now));
            Assert.AreEqual(0, observed.Count);
        }

        // ================= pure ShouldBlockConsumable =================

        [TestMethod]
        public void ShouldBlockConsumable_Pure()
        {
            var airborneOn = Dials(blockAirborne: true, minIntervalMs: 0);
            var intervalOn = Dials(blockAirborne: false, minIntervalMs: 5000);
            var bothOff = Dials(blockAirborne: false, minIntervalMs: 0);

            Assert.AreEqual(PvpConsumableBlock.Airborne, PvpRules.ShouldBlockConsumable(airborneOn, isJumping: true, lastUseUtc: null, now: Now));
            Assert.AreEqual(PvpConsumableBlock.None, PvpRules.ShouldBlockConsumable(airborneOn, isJumping: false, lastUseUtc: null, now: Now));

            Assert.AreEqual(PvpConsumableBlock.Interval, PvpRules.ShouldBlockConsumable(intervalOn, isJumping: false, lastUseUtc: Now.AddMilliseconds(-1000), now: Now));
            Assert.AreEqual(PvpConsumableBlock.None, PvpRules.ShouldBlockConsumable(intervalOn, isJumping: false, lastUseUtc: Now.AddMilliseconds(-6000), now: Now));
            Assert.AreEqual(PvpConsumableBlock.None, PvpRules.ShouldBlockConsumable(intervalOn, isJumping: false, lastUseUtc: null, now: Now));

            Assert.AreEqual(PvpConsumableBlock.None, PvpRules.ShouldBlockConsumable(bothOff, isJumping: true, lastUseUtc: Now, now: Now));
        }

        // ================= call-site pins =================

        /// <summary>
        /// H1 is wired at exactly one call site, in Healer.cs, beside the PK-only movement cap and BEFORE the
        /// DoHealing call it guards (so a refusal never decrements a kit's uses). Matched on CODE only.
        /// </summary>
        [TestMethod]
        public void H1_IsPinned_ToHealerCs_BeforeDoHealing()
        {
            var root = FindSourceRoot();

            AssertSiteBefore(root, "ACE.Server/WorldObjects/Healer.cs",
                "if (PvpRules.TryBlockConsumable(PvpChokePoint.H1, healer, healer.IsJumping, null) == PvpConsumableBlock.Airborne)",
                "if (dist < Healing_MaxMove || healer.PlayerKillerStatus == PlayerKillerStatus.NPK)");
        }

        /// <summary>
        /// H2 is wired at exactly one call site, inside Player_Use.cs's WrapPvpConsumableCompletion, and that
        /// SAME wrapped action is used by both of ApplyConsumable's branches: the fast-chug branch
        /// (ApplyConsumableWithAnimationCallbacks) and the normal action-chain branch (actionChain.AddAction).
        /// The two refusal reasons send distinct messages (a GameEventWeenieError for Airborne, a
        /// GameMessageSystemChat for Interval), and the interval stamp happens only after action() returns.
        /// Matched on CODE only.
        /// </summary>
        [TestMethod]
        public void H2_IsPinned_ToPlayerUseCs_AndCoversBothApplyConsumableBranches()
        {
            var root = FindSourceRoot();
            var path = Path.Combine(root, "ACE.Server", "WorldObjects", "Player_Use.cs");
            Assert.IsTrue(File.Exists(path), $"missing {path}");

            AssertCodeLineCount(path, "switch (PvpRules.TryBlockConsumable(PvpChokePoint.H2, this, IsJumping, PvpLastConsumableUseUtc))", 1);
            AssertCodeLineCount(path, "case PvpConsumableBlock.Airborne:", 1);
            AssertCodeLineCount(path, "Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.YouCantDoThatWhileInTheAir));", 1);
            AssertCodeLineCount(path, "case PvpConsumableBlock.Interval:", 1);
            AssertCodeLineCount(path, "Session.Network.EnqueueSend(new GameMessageSystemChat(ConsumableIntervalMessage, ChatMessageType.Broadcast));", 1);
            AssertCodeLineCount(path, "ApplyConsumableWithAnimationCallbacks(useMotion, pvpGuardedAction);", 1);
            AssertCodeLineCount(path, "actionChain.AddAction(this, pvpGuardedAction);", 1);
            AssertCodeLineCount(path, "var pvpGuardedAction = WrapPvpConsumableCompletion(action, healsHealth);", 1);

            AssertSiteBefore(root, "ACE.Server/WorldObjects/Player_Use.cs",
                "action();",
                "PvpLastConsumableUseUtc = PvpRules.UtcNowSource();");
        }

        /// <summary>
        /// Round 1 fix: the wrapper must never send its own UseDone on refusal. Both of ApplyConsumable's
        /// branches already send exactly one, unconditionally, once their own use ends (TryUseItem's delayed
        /// chain for the action-chain branch; HandleMotionDone_UseConsumable's FinishChugging branch for
        /// fast-chug) - a UseDone from the wrapper too would leave the client with an error UseDone immediately
        /// followed by a spurious success one. Extracts WrapPvpConsumableCompletion's own body (between its
        /// signature and ApplyConsumable's) and asserts no line in it mentions SendUseDoneEvent.
        /// </summary>
        [TestMethod]
        public void H2Wrapper_NeverSendsItsOwnUseDoneEvent_OnRefusal()
        {
            var root = FindSourceRoot();
            var path = Path.Combine(root, "ACE.Server", "WorldObjects", "Player_Use.cs");
            Assert.IsTrue(File.Exists(path), $"missing {path}");

            var lines = File.ReadAllLines(path);

            var start = Array.FindIndex(lines, l => l.Trim() == "private Action WrapPvpConsumableCompletion(Action action, bool healsHealth)");
            var end = Array.FindIndex(lines, l => l.Trim() == "public void ApplyConsumable(MotionCommand useMotion, Action action, float animMod = 1.0f, bool healsHealth = false)");

            Assert.IsTrue(start >= 0, "could not find WrapPvpConsumableCompletion's signature");
            Assert.IsTrue(end > start, "could not find ApplyConsumable's signature after the wrapper");

            for (var i = start; i < end; i++)
                Assert.IsFalse(lines[i].Contains("SendUseDoneEvent"), $"line {i + 1} of Player_Use.cs: the H2 wrapper must not call SendUseDoneEvent - `{lines[i].Trim()}`");
        }

        private static void AssertSiteBefore(string root, string file, string code, string laterCode)
        {
            var path = Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar));
            Assert.IsTrue(File.Exists(path), $"missing {path}");

            var lines = File.ReadAllLines(path);
            var hits = new List<int>();
            var later = new List<int>();

            for (var i = 0; i < lines.Length; i++)
            {
                var raw = lines[i];
                var commentAt = raw.IndexOf("//", StringComparison.Ordinal);
                var stripped = (commentAt >= 0 ? raw.Substring(0, commentAt) : raw).Trim();

                if (stripped == code)
                    hits.Add(i);

                if (stripped == laterCode)
                    later.Add(i);
            }

            Assert.AreEqual(1, hits.Count, $"{file}: expected exactly one code line `{code}`");
            Assert.AreEqual(1, later.Count, $"{file}: expected exactly one code line `{laterCode}` to order against");
            Assert.IsTrue(hits[0] < later[0], $"{file}: `{code}` (line {hits[0] + 1}) must come before `{laterCode}` (line {later[0] + 1})");
        }

        private static void AssertCodeLineCount(string path, string code, int expected)
        {
            var hits = 0;

            foreach (var raw in File.ReadAllLines(path))
            {
                var commentAt = raw.IndexOf("//", StringComparison.Ordinal);
                var stripped = (commentAt >= 0 ? raw.Substring(0, commentAt) : raw).Trim();

                if (stripped == code)
                    hits++;
            }

            Assert.AreEqual(expected, hits, $"{path}: expected {expected} code line(s) exactly `{code}`");
        }

        private static string FindSourceRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ACE.Server", "Entity", "DamageEvent.cs")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find ACE.Server/Entity/DamageEvent.cs by walking up from {AppContext.BaseDirectory}");
            return dir.FullName;
        }
    }
}
