using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.PuzzleGates;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The host seam and the gate-model indirection of the puzzle gate: the open-vs-destroy rule, the barrier panel
    /// layout (ported from the site tool's PuzzleSiteFit), the admin host staying exactly today's behaviour, and the
    /// run door pass skipping a puzzle gate. Pure where possible; the door pass runs over in-memory Doors.
    /// </summary>
    [TestClass]
    public class PuzzleGateModelTests
    {
        private const string ManagerPath = "Source/ACE.Server/PuzzleGates/PuzzleGateManager.cs";

        // ---- the destroy-vs-open rule ---------------------------------------------------------------------

        [TestMethod]
        public void Zero_length_open_animation_doors_and_barriers_are_destroyed_the_rest_open()
        {
            Assert.AreEqual(PuzzleGateSolveAction.Open, PuzzleGateTunables.SolveActionFor(PuzzleGateForm.Door, 1006850));
            Assert.AreEqual(PuzzleGateSolveAction.Open, PuzzleGateTunables.SolveActionFor(PuzzleGateForm.Door, 1006853));
            Assert.AreEqual(PuzzleGateSolveAction.Open, PuzzleGateTunables.SolveActionFor(PuzzleGateForm.Door, 1006855));

            Assert.AreEqual(PuzzleGateSolveAction.Destroy, PuzzleGateTunables.SolveActionFor(PuzzleGateForm.Door, 1006854), "reinforced: zero-length open anim");
            Assert.AreEqual(PuzzleGateSolveAction.Destroy, PuzzleGateTunables.SolveActionFor(PuzzleGateForm.Door, 1006856), "barrier wcid as a door: zero-length open anim");

            // Kind wins over wcid: a barrier or a focal object is destroyed whatever wcid it uses.
            Assert.AreEqual(PuzzleGateSolveAction.Destroy, PuzzleGateTunables.SolveActionFor(PuzzleGateForm.Barrier, 1006850));
            Assert.AreEqual(PuzzleGateSolveAction.Destroy, PuzzleGateTunables.SolveActionFor(PuzzleGateForm.Focal, 1006852));

            Assert.AreEqual(PuzzleGateSolveAction.Open, PuzzleGateModel.Default.SolveAction, "the admin default opens, as today");
        }

        [TestMethod]
        public void Solve_text_keeps_the_door_line_for_an_opened_door()
        {
            Assert.AreEqual(PuzzleGateText.Solved, PuzzleGateText.SolvedFor(PuzzleGateForm.Door, PuzzleGateSolveAction.Open));
            Assert.AreEqual(PuzzleGateText.SolvedWard, PuzzleGateText.SolvedFor(PuzzleGateForm.Door, PuzzleGateSolveAction.Destroy));
            Assert.AreEqual(PuzzleGateText.SolvedWard, PuzzleGateText.SolvedFor(PuzzleGateForm.Barrier, PuzzleGateSolveAction.Destroy));
            Assert.AreEqual(PuzzleGateText.SolvedSeal, PuzzleGateText.SolvedFor(PuzzleGateForm.Focal, PuzzleGateSolveAction.Destroy));
        }

        // ---- barrier layout -------------------------------------------------------------------------------

        [TestMethod]
        public void Barrier_pitch_matches_the_site_tool_formula()
        {
            // PuzzleSiteFit: pitch = (W - 2*inset - panelWidth) / (n - 1), panelWidth = 3.333 * s (the measured 0x020014FF width), inset 0.05.
            var pitch = PuzzleGateModel.PanelPitch(3, 1.0f, 8.0f);
            Assert.AreEqual((8.0f - 0.1f - 3.333f) / 2f, pitch, 1e-5f);

            var scaled = PuzzleGateModel.PanelPitch(2, 0.9f, 5.0f);
            Assert.AreEqual(5.0f - 0.1f - 3.333f * 0.9f, scaled, 1e-5f);

            Assert.AreEqual(0f, PuzzleGateModel.PanelPitch(1, 1f, 3.2f), "one panel: no pitch");
            Assert.AreEqual(3.333f * 1.1f, PuzzleGateModel.PanelPitch(2, 1.1f, 0f), 1e-5f, "unknown width: edge to edge");
        }

        [TestMethod]
        public void Barrier_panels_span_the_doorway_symmetrically_with_edges_inside_the_jambs()
        {
            const float width = 8.0f;
            var model = PuzzleGateModel.Barrier(PuzzleGateTunables.BarrierPanelWcid, 1.0f, 3, width);
            var gate = new Vector3(0, 7, 0);
            var parts = model.PartLocals(gate);

            Assert.AreEqual(3, parts.Count);
            Assert.AreEqual(0f, parts.Sum(p => p.X), 1e-4f, "centred on the gate point");
            Assert.IsTrue(parts.All(p => p.Y == 7f && p.Z == 0f), "all on the gate line");

            var half = PuzzleGateTunables.BarrierPanelWidth / 2f;
            var outerLeft = parts.Min(p => p.X) - half;
            var outerRight = parts.Max(p => p.X) + half;

            Assert.AreEqual(-width / 2f + PuzzleGateTunables.BarrierJambInset, outerLeft, 1e-4f, "left edge inset from the jamb");
            Assert.AreEqual(width / 2f - PuzzleGateTunables.BarrierJambInset, outerRight, 1e-4f, "right edge inset from the jamb");

            // A door is its single gate point; a focal object is lifted.
            CollectionAssert.AreEqual(new[] { gate }, PuzzleGateModel.Door(1006850, 1f).PartLocals(gate).ToArray());
            Assert.AreEqual(PuzzleGateTunables.FocalHeight, PuzzleGateModel.Focal(1006852, 1).PartLocals(gate)[0].Z, 1e-6f);
        }

        // ---- admin host unchanged -------------------------------------------------------------------------

        [TestMethod]
        public void A_placement_with_no_host_is_the_admin_host_with_todays_gate()
        {
            Assert.IsTrue(PuzzleGateOptions.TryParse(new[] { "sigil" }, out var options, out _));
            var p = new PuzzleGatePlacement(1, options, 7, new Position(0x0150018A, 1, 2, 0, 0, 0, 0, 1, 0), 0f, null, 0x50000001, null, DateTime.UtcNow);

            Assert.AreSame(AdminPuzzleGateHost.Instance, p.Host);
            Assert.AreSame(PuzzleGateModel.Default, p.GateModel);
            Assert.IsNull(p.Layout, "admin placements use the generator's default layout");

            Assert.AreEqual(PuzzleGateForm.Door, PuzzleGateModel.Default.Form);
            Assert.AreEqual(PuzzleGateTunables.GateWcid, PuzzleGateModel.Default.Wcid);
            Assert.AreEqual(1.0f, PuzzleGateModel.Default.Scale);
            Assert.AreEqual(0u, PuzzleGateModel.Default.Script);

            var host = AdminPuzzleGateHost.Instance;
            Assert.AreEqual(PuzzlePolicyMode.None, host.PolicyMode);
            Assert.AreEqual(0u, host.RunId);
            Assert.IsTrue(host.AllowAmbush, "admin ambush stays as the options say");
            Assert.IsNull(host.CheckActivation(p, null), "no veto");
        }

        [TestMethod]
        public void The_admin_command_path_still_places_the_default_gate_with_the_admin_host()
        {
            var src = PooledLootSourceText.Read(ManagerPath);
            var place = PooledLootSourceText.MethodBody(src, "public static string Place(Player admin, PuzzleGateOptions options)");

            StringAssert.Contains(place, "CheckWeenies(options, PuzzleGateModel.Default, out error)");
            StringAssert.Contains(place, "Register(Interlocked.Increment(ref nextId), options, seed, anchor, yaw, spots, admin.Guid.Full, landblock);",
                "no host, model or layout passed: the placement defaults to the admin host and today's gate");

            var handle = PooledLootSourceText.MethodBody(src, "private static void HandleActivation(PuzzleGatePlacement p, WorldObject lever, Player player)");
            StringAssert.Contains(handle, "if (p.Host.AllowAmbush)", "the ambush is the host's call");
            StringAssert.Contains(handle, "p.Host.CheckActivation(p, player)", "the veto is taken before scoring");
            Assert.IsTrue(handle.IndexOf("p.Host.CheckActivation", StringComparison.Ordinal) < handle.IndexOf("p.Rules.Activate", StringComparison.Ordinal),
                "the veto runs before the rules score the pull");
        }

        // ---- marker and the run door pass -----------------------------------------------------------------

        private static int nextGuid = 0x7F300000;

        private static Door LockedDoor()
        {
            var door = new Door(new Weenie
            {
                WeenieClassId = 1006850,
                WeenieType = WeenieType.Door,
                PropertiesBool = new Dictionary<PropertyBool, bool> { { PropertyBool.DefaultLocked, true }, { PropertyBool.Locked, true } },
            }, new ObjectGuid((uint)System.Threading.Interlocked.Increment(ref nextGuid)));

            Assert.IsTrue(door.IsLocked, "fixture: a DefaultLocked door starts locked");
            return door;
        }

        [TestMethod]
        public void The_run_door_pass_skips_a_puzzle_gate_and_unlocks_every_other_door()
        {
            var content = LockedDoor();
            var gate = LockedDoor();
            gate.IsPuzzleGateObject = true;

            var unlocked = ThreadDungeonSpawner.UnlockDoors(new List<WorldObject> { content, gate });

            Assert.AreEqual(1, unlocked);
            Assert.IsFalse(content.IsLocked, "content doors still unlock (ruling R16)");
            Assert.IsFalse(content.GetProperty(PropertyBool.DefaultLocked) ?? false);

            Assert.IsTrue(gate.IsLocked, "the puzzle gate stays locked");
            Assert.IsTrue(gate.GetProperty(PropertyBool.DefaultLocked) ?? false, "and keeps DefaultLocked, so Door.Reset re-locks it");
        }

        [TestMethod]
        public void The_gate_and_focal_prepare_steps_set_the_marker()
        {
            var src = PooledLootSourceText.Read(ManagerPath);

            StringAssert.Contains(PooledLootSourceText.MethodBody(src, "private static string PrepareGate(WorldObject wo)"), "door.IsPuzzleGateObject = true;");
            StringAssert.Contains(PooledLootSourceText.MethodBody(src, "private static string PrepareFocal(WorldObject wo)"), "wo.IsPuzzleGateObject = true;");

            var spawnGate = PooledLootSourceText.MethodBody(src, "private static bool SpawnGate(PuzzleGatePlacement p, Landblock landblock, PuzzlePlan plan, out string error)");
            StringAssert.Contains(spawnGate, "model.RequiresDoor ? (Func<WorldObject, string>)PrepareGate : PrepareFocal", "every gate part goes through a marking prepare step");
            Assert.AreEqual(1, spawnGate.Split("CreateWcid(").Length - 1, "one create path for gate parts");
        }

        // ---- reap rule after a destroying solve -----------------------------------------------------------

        [TestMethod]
        public void A_solve_that_destroyed_the_gate_is_not_a_missing_gate_until_the_levers_are_gone_too()
        {
            Assert.IsTrue(PuzzleGateOptions.TryParse(new[] { "odd" }, out var options, out _));
            var p = new PuzzleGatePlacement(2, options, 7, null, 0f, null, 0, null, DateTime.UtcNow);

            Assert.IsTrue(p.GateMissing(), "no gate yet = missing (today's rule)");

            var lever = new GenericObject(new Weenie { WeenieClassId = 1006851, WeenieType = WeenieType.Generic }, new ObjectGuid((uint)System.Threading.Interlocked.Increment(ref nextGuid)));
            p.RoundObjects.Add(lever);
            p.GateRemovedBySolve = true;

            Assert.IsFalse(p.GateMissing(), "solved and destroyed by the solve: the levers still stand");

            p.RoundObjects.Clear();
            Assert.IsTrue(p.GateMissing(), "levers gone too (a reload): reapable");
        }

        [TestMethod]
        public void A_placement_reports_its_removal_once()
        {
            Assert.IsTrue(PuzzleGateOptions.TryParse(new[] { "odd" }, out var options, out _));
            var p = new PuzzleGatePlacement(3, options, 7, null, 0f, null, 0, null, DateTime.UtcNow);

            Assert.IsTrue(p.TryClaimRemovalNotice());
            Assert.IsFalse(p.TryClaimRemovalNotice());
        }
    }
}
