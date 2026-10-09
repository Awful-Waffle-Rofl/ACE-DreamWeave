using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Managers;
using ACE.Server.Pvp;
using ACE.Server.Pvp.Templates;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// The player half of "match end, admin cancel, death and logout each restore exactly once" (the coordinator half
    /// is PvpMatchCoordinatorTests.Exits_MatchEnd_Death_Logout_AdminCancel_EachExitOnce). Every coordinator-driven exit
    /// (match end, admin cancel, the post-death exit) and the logout path reach Player.ExitPvpMatchNow, whose FIRST step
    /// is the restore; the death path also restores at its arrival. The restore is reached through the
    /// PvpTemplateSettings.Restore seam, faked here with the real one's contract - idempotent, keyed on the 9022
    /// record - so "exactly once" means the record is consumed by one call and every later call is a no-op.
    ///
    /// Seeding is PvpPlayerHookTests' (an uninitialized Player plus only the inherited WorldObject state these paths
    /// read; never a Player-declared static or field), with player_save_interval seeded for RushNextPlayerSave.
    /// </summary>
    [TestClass]
    public class PvpTemplateExitHookTests
    {
        private Func<Player, string, bool> savedRestore;
        private long savedSaveInterval;

        private int restoreCalls;
        private int restoresDone;
        private readonly List<string> reasons = new();

        [TestInitialize]
        public void Setup()
        {
            savedRestore = PvpTemplateSettings.Restore;
            savedSaveInterval = DefaultPropertyManager.DefaultLongProperties["player_save_interval"].Item;
            Assert.IsTrue(PropertyManager.ModifyLong("player_save_interval", savedSaveInterval));

            restoreCalls = 0;
            restoresDone = 0;
            reasons.Clear();

            PvpTemplateSettings.Restore = (player, reason) =>
            {
                restoreCalls++;
                reasons.Add(reason);

                if (!player.IsPvpTemplated)
                    return false;

                player.RemoveProperty(PropertyString.PvpTemplateRestore);
                restoresDone++;
                return true;
            };
        }

        [TestCleanup]
        public void Cleanup()
        {
            PvpTemplateSettings.Restore = savedRestore;
            PropertyManager.ModifyLong("player_save_interval", savedSaveInterval);
        }

        private static Player TemplatedPlayer(bool bound, PvpTemplateDefinition template = null)
        {
            var player = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));

            SetInherited(player, "<Biota>k__BackingField", new ACE.Entity.Models.Biota());
            SetInherited(player, "BiotaDatabaseLock", new ReaderWriterLockSlim());
            SetInherited(player, "positionCache", new Dictionary<PositionType, Position>());
            SetInherited(player, "ephemeralPositions", new Dictionary<PositionType, Position>());
            SetInherited(player, "<Sequences>k__BackingField", new ACE.Server.Network.Sequence.SequenceManager());

            player.PlayerKillerStatus = PlayerKillerStatus.PKLite;
            player.Location = new Position { LandblockId = new LandblockId(0x00900100), PositionX = 50f, PositionY = 50f, RotationW = 1f };
            player.SetProperty(PropertyString.PvpTemplateRestore, "{\"record\":\"stand-in\"}");

            if (bound)
            {
                var match = new PvpMatch(Guid.NewGuid(), "arena_1v1",
                    new List<PvpTeam> { new PvpTeam(0, new List<PvpParticipant> { new PvpParticipant(1, 1500) }), new PvpTeam(1, new List<PvpParticipant> { new PvpParticipant(2, 1500) }) },
                    new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

                player.SetPvpBindingForTests(new PvpPlayerBinding(match, 0, PvpMatchState.Live, true, true, true, true, true, template: template ?? new PvpTemplateDefinition { Key = "duelist", Version = 1 }));
                player.PvpMatchReturnPkStatus = (int)PlayerKillerStatus.NPK;
            }

            Assert.IsTrue(player.IsPvpTemplated);
            return player;
        }

        private static void SetInherited(WorldObject wo, string name, object value)
        {
            var field = typeof(WorldObject).GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(field, $"WorldObject.{name} was not found by reflection - has it been renamed?");
            field.SetValue(wo, value);
        }

        /// <summary>
        /// Match end / admin cancel / forfeit / the post-death exit / logout all land in ExitPvpMatchNow. DISCRIMINATES
        /// the restore as its first step: the record is consumed on the first exit, and a second exit restores nothing.
        /// </summary>
        [TestMethod]
        public void ExitPvpMatchNow_RestoresFirst_ExactlyOnceAcrossRepeatedExits()
        {
            var player = TemplatedPlayer(bound: true);

            Assert.IsTrue(player.ExitPvpMatchNow("match end"));
            Assert.IsFalse(player.IsPvpTemplated);
            Assert.AreEqual(1, restoresDone);
            Assert.AreEqual("match exit: match end", reasons[0]);
            Assert.AreEqual(PlayerKillerStatus.NPK, player.PlayerKillerStatus, "the rest of the exit still ran");

            Assert.IsFalse(player.ExitPvpMatchNow("admin cancel"));
            Assert.AreEqual(1, restoresDone, "a repeat exit never restores twice");
        }

        /// <summary>A templated player whose bind failed (no binding, no marker) is still restored by an exit: the restore precedes the early return.</summary>
        [TestMethod]
        public void ExitPvpMatchNow_UnboundButTemplated_StillRestores()
        {
            var player = TemplatedPlayer(bound: false);

            Assert.IsFalse(player.ExitPvpMatchNow("entry failed"), "nothing else to undo");
            Assert.AreEqual(1, restoresDone);
            Assert.IsFalse(player.IsPvpTemplated);
        }

        /// <summary>
        /// Death: the arrival restores, then the coordinator's later exit (after the death return lands) finds nothing
        /// to restore. One restore across the whole death path.
        /// </summary>
        [TestMethod]
        public void Death_ArrivalRestores_ThenTheLaterExitIsANoOpRestore()
        {
            var player = TemplatedPlayer(bound: true);

            player.PvpTemplateDeathArrival();
            Assert.AreEqual(1, restoresDone);
            Assert.AreEqual("match death", reasons[0]);

            player.ExitPvpMatchNow("post-death exit");
            Assert.AreEqual(1, restoresDone);
            Assert.AreEqual(2, restoreCalls, "both hooks reached the restore; only the first had a record to consume");
        }

        /// <summary>
        /// King of the Hill (owner ruling 2026-10-03): a battleground pen death does NOT end participation, so its arrival
        /// restores nothing - the player respawns still templated with the issued kit. DISCRIMINATES the respawnsInMatch
        /// early return: the restore seam is never even reached. Leaving the match then restores exactly once.
        /// </summary>
        [TestMethod]
        public void PenDeath_ArrivalKeepsTheTemplate_LeavingRestoresExactlyOnce()
        {
            var player = TemplatedPlayer(bound: true);

            player.PvpTemplateDeathArrival(respawnsInMatch: true);
            Assert.AreEqual(0, restoreCalls, "a respawn death never reaches the restore");
            Assert.IsTrue(player.IsPvpTemplated, "still templated in the pen");

            player.PvpTemplateDeathArrival(respawnsInMatch: true);
            Assert.AreEqual(0, restoreCalls, "nor does a second respawn death");

            Assert.IsTrue(player.ExitPvpMatchNow("left the battleground"));
            Assert.AreEqual(1, restoresDone);
            Assert.IsFalse(player.IsPvpTemplated);

            player.ReturnFromBattlegroundPenNow("match ended while in the pen");
            Assert.AreEqual(1, restoresDone, "the pen return after the exit restores nothing more");
        }

        /// <summary>
        /// The death-completion backstop can send a pen player home before the coordinator's exit runs. DISCRIMINATES the
        /// restore at the top of ReturnFromBattlegroundPenNow: the template comes off on that trip, once.
        /// </summary>
        [TestMethod]
        public void PenReturn_RestoresBeforeTheTrip_WhenTheExitHasNotRunYet()
        {
            var player = TemplatedPlayer(bound: true);

            player.ReturnFromBattlegroundPenNow("death sequence finished after the match stopped being Live");
            Assert.AreEqual(1, restoresDone);
            StringAssert.StartsWith(reasons[0], "left the battleground pen");
            Assert.IsFalse(player.IsPvpTemplated);

            player.ExitPvpMatchNow("post-pen exit");
            Assert.AreEqual(1, restoresDone, "the later exit restores nothing more");
        }

        // ================= M2: the respawn buff re-apply never adds alongside =================

        /// <summary>A template with two buffs (spells 100 and 200, an hour each).</summary>
        private static PvpTemplateDefinition BuffedTemplate()
        {
            var template = new PvpTemplateDefinition { Key = "duelist", Version = 1 };
            template.Buffs.Add(new PvpTemplateEnchantment { SpellId = 100, LayerId = 1, EnchantmentCategory = 1, Duration = 3600, PowerLevel = 7 });
            template.Buffs.Add(new PvpTemplateEnchantment { SpellId = 200, LayerId = 1, EnchantmentCategory = 1, Duration = 3600, PowerLevel = 7 });
            return template;
        }

        /// <summary>A seeded templated player whose enchantment registry and manager are real (the re-apply writes through both).</summary>
        private static Player BuffedPlayer()
        {
            var player = TemplatedPlayer(bound: true, template: BuffedTemplate());
            player.Biota.PropertiesEnchantmentRegistry = new List<ACE.Entity.Models.PropertiesEnchantmentRegistry>();
            player.EnchantmentManager = new ACE.Server.WorldObjects.Managers.EnchantmentManagerWithCaching(player);
            return player;
        }

        /// <summary>
        /// pvp_arena_death_keeps_enchantments on (the default): the template buffs are still there after the death.
        /// DISCRIMINATES the absent-only filter: two re-applies add no row and leave each buff's StartTime (its elapsed
        /// time) untouched - no stacked layer, no refreshed duration.
        /// </summary>
        [TestMethod]
        public void RespawnReapply_BuffsStillPresent_AddsNothing_AndKeepsTheirTime()
        {
            var player = BuffedPlayer();
            player.ReapplyPvpTemplateBuffsAfterRespawn();

            var registry = player.Biota.PropertiesEnchantmentRegistry;
            Assert.AreEqual(2, registry.Count, "fixture: the first re-apply on an empty registry put both buffs on");

            foreach (var row in registry)
                row.StartTime = -120;

            player.ReapplyPvpTemplateBuffsAfterRespawn();
            player.ReapplyPvpTemplateBuffsAfterRespawn();

            Assert.AreEqual(2, registry.Count, "never a second row alongside a buff that is still on");
            Assert.IsTrue(registry.All(r => r.StartTime == -120), "never a refreshed duration");
            Assert.AreEqual(1, registry.Count(r => r.SpellId == 100));
        }

        /// <summary>
        /// The death purge on (or a buff expired): the registry lacks the buffs, and the re-apply restores each exactly
        /// once, also when it runs again. A partially purged registry gets back only the missing one.
        /// </summary>
        [TestMethod]
        public void RespawnReapply_BuffsGone_RestoresEachExactlyOnce()
        {
            var player = BuffedPlayer();

            player.ReapplyPvpTemplateBuffsAfterRespawn();
            player.ReapplyPvpTemplateBuffsAfterRespawn();

            var registry = player.Biota.PropertiesEnchantmentRegistry;
            CollectionAssert.AreEquivalent(new[] { 100, 200 }, registry.Select(r => (int)r.SpellId).ToList());

            registry.Remove(registry.Single(r => r.SpellId == 200));
            player.ReapplyPvpTemplateBuffsAfterRespawn();

            CollectionAssert.AreEquivalent(new[] { 100, 200 }, registry.Select(r => (int)r.SpellId).ToList(), "only the expired buff came back");
        }

        // ================= M3: a battleground exit while dying defers the restore to the death's end =================

        /// <summary>
        /// The coordinator exits a seat still in the death sequence (ExitMatchFromPen while DyingToPen). DISCRIMINATES the
        /// deferral: the exit unbinds but restores nothing (no rebuild of a Health-0 player mid-death); the pen backstop at
        /// the end of the death sequence restores; nothing restores twice.
        /// </summary>
        [TestMethod]
        public void DyingPenExit_DefersTheRestore_TheBackstopRestoresExactlyOnce()
        {
            var player = TemplatedPlayer(bound: true);

            Assert.IsTrue(player.ExitPvpMatchNow("match ended (from pen)", deferRestoreToPenBackstop: true), "the rest of the exit still ran");
            Assert.IsNull(player.PvpBinding, "unbound");
            Assert.AreEqual(0, restoreCalls, "no restore while dying");
            Assert.IsTrue(player.IsPvpTemplated);
            Assert.IsTrue(player.PvpPenBackstopNeeded(Guid.NewGuid()), "the end of the death sequence will run the pen backstop");

            player.ReturnFromBattlegroundPenNow("death sequence finished after the match stopped being Live");
            Assert.AreEqual(1, restoresDone);
            Assert.IsFalse(player.IsPvpTemplated);

            player.ExitPvpMatchNow("late exit");
            Assert.AreEqual(1, restoresDone, "exactly once across the exit and the backstop");
        }

        /// <summary>The deferral rule: only an exit from the pen while still dying defers; every other exit restores at once.</summary>
        [TestMethod]
        public void DeferralRule_OnlyAPenExitWhileDying()
        {
            Assert.IsTrue(PvpPlayerRules.DefersTemplateRestoreToPenBackstop(returnFromPen: true, inDeathProcess: true));
            Assert.IsFalse(PvpPlayerRules.DefersTemplateRestoreToPenBackstop(returnFromPen: true, inDeathProcess: false), "death finished: restore now");
            Assert.IsFalse(PvpPlayerRules.DefersTemplateRestoreToPenBackstop(returnFromPen: false, inDeathProcess: true), "logout or any non-pen exit: restore now");
            Assert.IsFalse(PvpPlayerRules.DefersTemplateRestoreToPenBackstop(returnFromPen: false, inDeathProcess: false));

            var player = TemplatedPlayer(bound: true);
            Assert.IsTrue(player.ExitPvpMatchNow("a living pen exit", PvpPlayerRules.DefersTemplateRestoreToPenBackstop(true, false)));
            Assert.AreEqual(1, restoresDone, "a non-dying exit still restores immediately");
        }

        /// <summary>A restore that throws never stops the exit or the death sequence (the record is kept: the player stays inert).</summary>
        [TestMethod]
        public void ThrowingRestore_NeverPropagates()
        {
            PvpTemplateSettings.Restore = (p, r) => throw new InvalidOperationException("restore blew up");
            var player = TemplatedPlayer(bound: true);

            player.PvpTemplateDeathArrival();
            Assert.IsTrue(player.ExitPvpMatchNow("match end"), "the exit still unbinds");
            Assert.IsNull(player.PvpBinding);
            Assert.IsTrue(player.IsPvpTemplated, "the record is kept");
        }
    }
}
