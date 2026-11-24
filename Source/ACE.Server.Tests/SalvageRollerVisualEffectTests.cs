using System;
using System.IO;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The Salvage Roller delivery bug (owner report, 2026-11-03): a weapon taken from the roller lost the particle
    /// effect it had before (its base VisualEffectScript or tier aura) and never showed the tier aura it gained.
    ///
    /// THE MECHANISM. VisualEffectManager.SendTo sends an object's script at most once per session per guid
    /// (Player.VisualEffectsSent), and only Forget re-opens that record. The roller's target is a weapon the player
    /// usually had drawn earlier in the session, so its guid is recorded. Selling it to the window detaches it with
    /// RemoveFromInventoryAction.SellItem, which sends no delete, so nothing forgets it. The take then delivers the SAME
    /// guid with Player.TryCreateInInventoryWithNetworking, a fresh GameMessageCreateObject that makes the client
    /// rebuild the object and drop its emitters. Before the fix that method touched no record, so the next equip's
    /// SendTo found the stale record and returned, and neither the old script nor the new aura was ever sent again.
    ///
    /// THE FIX. #1968 first fixed this for the roller alone with a VisualEffectManager.CreateInPack helper. The forget
    /// now lives in TryCreateInInventoryWithNetworking itself (PackCreateEffectRecordTests pins it there), so every
    /// caller that re-creates a known guid is covered, and the roller simply calls the choke point.
    ///
    /// The tests here are source assertions because ACE.Server.Tests cannot build a Player or a Session, the limit
    /// FacetVaultRestoreOrderingTests states. They prove the delivery is written to forget, not that the client
    /// renders it; only the in-game check proves that.
    /// </summary>
    [TestClass]
    public class SalvageRollerVisualEffectTests
    {
        private const string HostPath = "Source/ACE.Server/SalvageRoller/SalvageRollerHost.cs";
        private const string VendorPath = "Source/ACE.Server/WorldObjects/SalvageRollerVendor.cs";
        private const string InventoryPath = "Source/ACE.Server/WorldObjects/Player_Inventory.cs";
        private const string EffectsPath = "Source/ACE.Server/Entity/VisualEffectManager.cs";

        // ---------------- premise ----------------

        /// <summary>The once-per-session guard: a recorded guid is skipped unless the caller says it rebuilt the object.</summary>
        [TestMethod]
        public void Premise_SendTo_SkipsAGuidItHasAlreadyRecorded()
        {
            var code = ReadStripped(EffectsPath);

            StringAssert.Contains(code, "if (!player.VisualEffectsSent.Add(wo.Guid.Full) && !rebuilt)",
                "VisualEffectManager.SendTo no longer skips a recorded guid. The roller delivery fix exists only because of that guard; re-derive it.");
        }

        /// <summary>
        /// The pack create sends a CreateObject, FORGETS the item's effect record after it, and never sends the script.
        /// This is what the roller's two deliveries now rely on (it replaced the pre-fix premise that the method touched
        /// no record at all, which is what made the roller need its own helper).
        /// </summary>
        [TestMethod]
        public void Premise_TryCreateInInventoryWithNetworking_SendsACreateThenForgetsTheEffectRecord()
        {
            var body = PackCreateEffectRecordTests.MethodBody(InventoryPath, "public bool TryCreateInInventoryWithNetworking(WorldObject item, out Container container");

            var create = body.IndexOf("new GameMessageCreateObject(item)", StringComparison.Ordinal);
            var forget = body.IndexOf("VisualEffectManager.Forget(Session, item)", StringComparison.Ordinal);

            Assert.IsTrue(create >= 0, "the pack create no longer sends a CreateObject");
            Assert.IsTrue(forget > create, "the pack create must forget the item's effect record after its CreateObject; without it a roller take leaves the weapon bare on the next equip");
            Assert.IsFalse(body.Contains("SendTo("), "the pack create SENDS the script: the item is in the pack, so that attaches to nothing and burns the equip's one send");
            Assert.IsFalse(body.Contains("GameMessagePlayScriptId"), "the pack create sends a script directly");
        }

        /// <summary>The helper #1968 added is gone; a revived copy would be a second, divergent forget path.</summary>
        [TestMethod]
        public void CreateInPackHelper_IsRemoved()
        {
            Assert.IsFalse(ReadStripped(EffectsPath).Contains("CreateInPack("),
                "VisualEffectManager.CreateInPack is back. The forget lives in Player.TryCreateInInventoryWithNetworking now; route callers there instead.");
        }

        // ---------------- structure: every roller delivery routes through the choke point ----------------

        /// <summary>The take and the cancel's hand-back both deliver through the host's TryPutInPack.</summary>
        [TestMethod]
        public void RollerHost_TryPutInPack_DeliversThroughThePackCreate()
        {
            var code = ReadStripped(HostPath);
            var line = Regex.Match(code, @"public bool TryPutInPack\(WorldObject item\)[^\n]*");

            Assert.IsTrue(line.Success, "PlayerSalvageRollerHost.TryPutInPack was not found; re-aim this guard");
            StringAssert.Contains(line.Value, "player.TryCreateInInventoryWithNetworking(item)",
                "the roller's take re-creates a guid the client may already hold; it must go through the pack create, which forgets the effect record, or the weapon stays bare on the next equip");
            Assert.IsFalse(code.Contains("GameMessageCreateObject"),
                "SalvageRollerHost.cs sends its own CreateObject; that bypasses the pack create's forget");
        }

        /// <summary>The station's own hand-back (a refused sale) re-creates the same guid too.</summary>
        [TestMethod]
        public void RollerVendor_TryHandBack_DeliversThroughThePackCreate()
        {
            var body = PackCreateEffectRecordTests.MethodBody(VendorPath, "protected virtual bool TryHandBack(WorldObject item, Player player)");

            StringAssert.Contains(body, "player.TryCreateInInventoryWithNetworking(item)", "the station hand-back must go through the pack create, which forgets the effect record");
            Assert.IsFalse(ReadStripped(VendorPath).Contains("GameMessageCreateObject"),
                "SalvageRollerVendor.cs sends its own CreateObject; that bypasses the pack create's forget");
        }

        private static string ReadStripped(string relativePath) => PackCreateEffectRecordTests.ReadStripped(relativePath);
    }
}
