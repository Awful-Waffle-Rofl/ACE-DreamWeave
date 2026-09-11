using System;
using System.IO;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Guards the facet gear restore's VAULT path against the client desync fixed alongside this file:
    /// the server had all 14 of slot 1's remembered items wielded (wielder_Id set, container_Id NULL on
    /// every row) while the client drew the five vault-sourced ones as pack items forever, and refused to
    /// equip them because the server already had them equipped.
    ///
    /// THE MECHANISM, because every assertion here only makes sense against it. The old shape delivered a
    /// withdrawn object with TryCreateInInventoryWithNetworking and then detached-and-equipped it, so the
    /// object's first mention to the client was a GameMessageCreateObject describing a pack item, followed
    /// by the detach and wield messages that move it out of the pack. Those halves ride different message
    /// groups - the create is SmartboxQueue, everything after it is UIQueue - and NetworkSession.Update
    /// flushes bundles in ASCENDING group order, with UIQueue (0x09) below SmartboxQueue (0x0A). So every
    /// detach and wield message went out naming a guid the client had never been told about, and the create
    /// that finally introduced the object described it sitting in the pack. Nothing sent BEFORE the create
    /// can survive, which is also why un-suppressing GameMessageInventoryRemoveObject would not have helped:
    /// that message is UIQueue too.
    ///
    /// WHAT EACH TEST CATCHES is stated on the test. Two kinds live here.
    ///
    /// The PREMISE tests pin the network-layer facts the fix is built on. They would NOT have gone red on
    /// the broken code and are not claimed to - they exist because the fix silently becomes wrong (or
    /// unnecessary) if an upstream merge moves one of these messages between groups, and nothing else in the
    /// suite would notice.
    ///
    /// The STRUCTURE tests are the discriminating ones: each of them fails against the pre-fix source. They
    /// are source-order assertions rather than behavioural ones because ACE.Server.Tests has no database, no
    /// live Player and no Session, so neither restore path can be executed here at all - the same limit
    /// FacetStripRoomTests states, and the same instrument VaultCollapseTests and AccountVaultPurgeTests
    /// already use for exactly this reason. BE CLEAR ABOUT THE LIMIT: they prove the calls are written in the
    /// right order, not that the client is happy. Only the live check does that, and it is queued.
    /// </summary>
    [TestClass]
    public class FacetVaultRestoreOrderingTests
    {
        private const string FacetsRelativePath = "Source/ACE.Server/WorldObjects/Player_Facets.cs";

        // The method each guard reads. MethodBody bounds a region by the NEXT member declaration rather than
        // by a named successor, so no guard depends on any method other than the one it is about.
        private const string DetachAndEquipSignature = "private bool TryDetachAndEquipForFacet(";
        private const string EquipWithdrawnSignature = "private bool TryEquipWithdrawnForFacet(";
        private const string WithdrawAndRestoreSignature = "private void WithdrawAndRestoreFromVault(";

        #region Premise: the message groups and the flush order the fix is built on

        /// <summary>
        /// CATCHES: an enum reorder that would put SmartboxQueue below UIQueue, which would invert the whole
        /// premise - the create would then be flushed FIRST and the old shape would have been correct.
        /// </summary>
        [TestMethod]
        public void UIQueue_IsFlushedBeforeSmartboxQueue()
        {
            Assert.IsTrue((int)GameMessageGroup.UIQueue < (int)GameMessageGroup.SmartboxQueue,
                $"UIQueue (0x{(int)GameMessageGroup.UIQueue:X2}) must sort below SmartboxQueue (0x{(int)GameMessageGroup.SmartboxQueue:X2}). NetworkSession.Update walks currentBundles in ascending group order, so this ordering is what makes a GameMessageCreateObject reach the client AFTER the UIQueue traffic enqueued beside it. If this ever inverts, re-read TryEquipWithdrawnForFacet - its trailing create would no longer be needed.");
        }

        /// <summary>
        /// CATCHES: NetworkSession.Update being changed to walk its bundles in some other order (descending,
        /// or a hand-written priority list). The ascending walk is the only reason the group numbers above
        /// mean anything.
        /// </summary>
        [TestMethod]
        public void NetworkSessionUpdate_StillWalksItsBundlesInAscendingGroupOrder()
        {
            var code = ReadStripped("Source/ACE.Server/Network/NetworkSession.cs");

            StringAssert.Contains(code, "for (int i = 0; i < currentBundles.Length; i++)",
                "NetworkSession.Update no longer walks currentBundles in ascending index order. That walk is what makes UIQueue flush before SmartboxQueue, which is the entire premise of the facet vault-restore fix - re-derive it before changing this test.");
        }

        /// <summary>
        /// CATCHES: a message moving between groups. Constructing GameMessageCreateObject or
        /// GameMessagePublicUpdateInstanceID needs a real WorldObject (and, for the create, the client dats),
        /// so those two are pinned by reading their constructors; GameMessagePlayScriptId takes a bare guid
        /// and is checked live, which also proves the Group property is really set from that base call.
        /// </summary>
        [TestMethod]
        public void TheRestorePathsMessages_SitOnTheGroupsTheFixAssumes()
        {
            AssertDeclaredGroup("GameMessageCreateObject.cs", "SmartboxQueue");
            AssertDeclaredGroup("GameMessagePublicUpdateInstanceID.cs", "UIQueue");
            AssertDeclaredGroup("GameMessageInventoryRemoveObject.cs", "UIQueue");
            AssertDeclaredGroup("GameMessageObjDescEvent.cs", "SmartboxQueue");

            var playScript = new GameMessagePlayScriptId(new ObjectGuid(0x50000014), 0x33000000);

            Assert.AreEqual(GameMessageGroup.SmartboxQueue, playScript.Group,
                "GameMessagePlayScriptId moved off SmartboxQueue. TryEquipWithdrawnForFacet re-sends the visual effect with rebuilt:true specifically because the equip's own send lands ahead of the create on this queue - re-derive that if the group has changed.");
        }

        /// <summary>
        /// CATCHES: GameEventWieldItem moving off UIQueue. Kept separate from the message check above because
        /// GameEvents carry their group through a different base constructor.
        /// </summary>
        [TestMethod]
        public void GameEventWieldItem_IsStillOnTheUIQueue()
        {
            var code = ReadStripped("Source/ACE.Server/Network/GameEvent/Events/GameEventWieldItem.cs");

            StringAssert.Contains(code, "GameMessageGroup.UIQueue",
                "GameEventWieldItem moved off UIQueue. If it is now SmartboxQueue it would arrive in create order and the vault path's trailing GameMessageCreateObject may no longer be the only thing dressing the item - re-derive TryEquipWithdrawnForFacet's remarks.");
        }

        #endregion

        #region Structure: the discriminating guards

        /// <summary>
        /// CONTROL for every source assertion below. If the comment stripper stopped stripping, all of them
        /// could be satisfied by prose in a doc comment - and this file's doc comments name every one of the
        /// symbols being searched for, so that failure mode is live rather than hypothetical.
        /// </summary>
        [TestMethod]
        public void CommentStripper_ActuallyRemovesTheDocCommentsThatNameTheseSymbols()
        {
            var raw = File.ReadAllText(FindInSourceTree(FacetsRelativePath));
            var code = AccountVaultPurgeTests.StripComments(raw);

            StringAssert.Contains(raw, "/// <summary>", "Player_Facets.cs has no doc comments at all, so this control proves nothing - re-aim it.");
            Assert.IsFalse(code.Contains("/// <summary>"),
                "StripComments left doc comments in place, so every source assertion in this file can be satisfied by a comment mentioning the symbol rather than by code calling it.");
        }

        /// <summary>
        /// CATCHES the fix being reverted. Pre-fix, WithdrawAndRestoreFromVault called
        /// TryCreateInInventoryWithNetworking FIRST and equipped afterwards, which is the desync itself: the
        /// pack delivery announced the object to the client, and the equip's messages could never overtake
        /// that announcement.
        /// </summary>
        [TestMethod]
        public void VaultRestore_AttemptsTheEquipBeforeItEverAnnouncesAPackDelivery()
        {
            var body = MethodBody(WithdrawAndRestoreSignature);

            var iEquip = body.IndexOf("TryEquipWithdrawnForFacet(", StringComparison.Ordinal);
            var iCreate = body.IndexOf("TryCreateInInventoryWithNetworking(", StringComparison.Ordinal);

            Assert.IsTrue(iEquip >= 0, "WithdrawAndRestoreFromVault no longer calls TryEquipWithdrawnForFacet - this guard must be re-aimed rather than left scanning nothing.");
            Assert.IsTrue(iCreate >= 0, "WithdrawAndRestoreFromVault no longer calls TryCreateInInventoryWithNetworking - this guard must be re-aimed rather than left scanning nothing.");

            Assert.IsTrue(iEquip < iCreate,
                "WithdrawAndRestoreFromVault must attempt the equip BEFORE any pack delivery. TryCreateInInventoryWithNetworking is the client's introduction to a freshly withdrawn object; once it has run, the wield messages that follow it are flushed on the UIQueue ahead of its SmartboxQueue create and are lost, leaving the item drawn in the pack forever while the server has it wielded.");
        }

        /// <summary>
        /// CATCHES the vault path being routed back through the inventory path's helper. That helper detaches
        /// with RemoveFromInventoryAction.ToWieldedSlot, which is correct only for an object the client
        /// already has in a pack slot; a withdrawn object comes back parented to nothing and has no container
        /// to detach from, so the detach would be both a no-op and one more doomed UIQueue message.
        /// </summary>
        [TestMethod]
        public void VaultRestore_DoesNotRouteThroughTheInventoryPathsDetach()
        {
            var body = MethodBody(WithdrawAndRestoreSignature);

            Assert.IsFalse(body.Contains("TryDetachAndEquipForFacet("),
                "WithdrawAndRestoreFromVault is calling TryDetachAndEquipForFacet again. That helper is the INVENTORY path's, and using it here re-creates the desync: it presumes the client already draws the item in a pack slot, which is only true because a pack delivery announced it first.");
        }

        /// <summary>
        /// CATCHES the trailing create being dropped or moved ahead of the equip, and the visual-effect
        /// re-send being dropped. The create is the ONLY thing that reaches the client describing the item as
        /// worn (this is exactly what SendInventoryAndWieldedItems does at login), and it has to describe the
        /// finished state, so it cannot be sent before the equip has happened.
        /// </summary>
        [TestMethod]
        public void EquipWithdrawn_AnnouncesTheObjectOnlyAfterItIsActuallyWielded()
        {
            var body = MethodBody(EquipWithdrawnSignature);

            var iEquip = body.IndexOf("TryEquipObjectWithNetworking(", StringComparison.Ordinal);
            var iCreate = body.IndexOf("new GameMessageCreateObject(item)", StringComparison.Ordinal);
            var iEffect = body.IndexOf("VisualEffectManager.SendTo(", StringComparison.Ordinal);

            Assert.IsTrue(iEquip >= 0, "TryEquipWithdrawnForFacet no longer calls TryEquipObjectWithNetworking - re-aim this guard.");
            Assert.IsTrue(iCreate >= 0,
                "TryEquipWithdrawnForFacet no longer sends a GameMessageCreateObject. Without it the client is never told the withdrawn object exists in any form - every message the equip sent named a guid the client had not heard of.");
            Assert.IsTrue(iEffect >= 0, "TryEquipWithdrawnForFacet no longer re-sends the visual effect - re-aim this guard.");

            Assert.IsTrue(iEquip < iCreate,
                "The GameMessageCreateObject must be sent AFTER the equip, so that it serializes the item as wielded. Sent before, it describes an unparented object and the client has nowhere to draw it.");

            Assert.IsTrue(iCreate < iEffect,
                "The visual-effect re-send must follow the create. GameMessagePlayScriptId is SmartboxQueue like the create, so a send enqueued ahead of it reaches the client before the object exists and is lost - and VisualEffectManager.SendTo is once-per-lifetime guarded, so that lost send suppresses the effect permanently.");

            StringAssert.Contains(body, "rebuilt: true",
                "The visual-effect re-send must pass rebuilt:true. TryEquipObjectWithNetworking has already called VisualEffectManager.SendTo and burned the once-per-lifetime flag with a send the client could not apply, so a plain SendTo here returns immediately and the effect never renders.");
        }

        /// <summary>
        /// CATCHES the double-parenting regression on the INVENTORY path's equip helper. This and its sibling
        /// below are the only assertions in this file that guard SHARD state rather than client messaging.
        ///
        /// Creature_Equipment.TryEquipObject - which sets WielderId and registers the item in EquippedObjects
        /// - is the first thing TryEquipObjectWithNetworking does, and every throw source inside it runs after
        /// that point. A catch that forces equipped=false therefore sends a genuinely-worn item down a path
        /// that sets ContainerId beside the live WielderId, which is the corrupt both-ids row
        /// 2026-09-06-00-Repair-Loadout-Double-Parented-Items.sql exists to clean up.
        ///
        /// DELIBERATELY ONE METHOD PER TEST, not a loop over both. When these two shared a test body, running
        /// it against the pre-fix source reddened it on MethodBody's own "signature not found" precondition -
        /// TryEquipWithdrawnForFacet does not exist there - and the substantive equipped=false assertion for
        /// TryDetachAndEquipForFacet never executed at all. One method's missing bounding signature must not
        /// be able to stand in for the other method's real assertion.
        /// </summary>
        [TestMethod]
        public void DetachAndEquipCatch_BelievesEquippedObjectsRatherThanForcingFailure()
        {
            AssertCatchBelievesEquippedObjects(
                "TryDetachAndEquipForFacet",
                MethodBody(DetachAndEquipSignature));
        }

        /// <summary>
        /// CATCHES the same double-parenting regression on the VAULT path's equip helper. See the sibling
        /// above for the argument and for why these are two tests rather than one loop.
        /// </summary>
        [TestMethod]
        public void EquipWithdrawnCatch_BelievesEquippedObjectsRatherThanForcingFailure()
        {
            AssertCatchBelievesEquippedObjects(
                "TryEquipWithdrawnForFacet",
                MethodBody(EquipWithdrawnSignature));
        }

        private static void AssertCatchBelievesEquippedObjects(string name, string body)
        {
            StringAssert.Contains(body, "catch (Exception ex)",
                $"{name} no longer catches the equip. TryEquipObjectWithNetworking is not throw-free - TryActivateSpells, EquipItemFromSet and VisualEffectManager.SendTo all run inside it.");

            StringAssert.Contains(body, "equipped = HasEquippedItem(item.Guid);",
                $"{name} must resolve a thrown equip by asking EquippedObjects whether the item actually landed.");

            Assert.IsFalse(body.Contains("equipped = false;"),
                $"{name} forces equipped=false somewhere. A throw after Creature_Equipment.TryEquipObject leaves the item genuinely worn; treating that as a failure and putting it in the pack sets ContainerId beside a live WielderId, which is the double-parented shard row this feature already needed a repair migration for.");
        }

        #endregion

        #region Helpers

        /// <summary>
        /// One method's body, bounded by the NEXT member declaration at class indent rather than by a named
        /// successor. Naming the successor is what let a missing signature in one test mask another's real
        /// assertion: when TryDetachAndEquipForFacet was bounded by "the TryEquipWithdrawnForFacet
        /// signature", running against a source tree without that method failed on the bound rather than on
        /// anything about the method under test. A self-bounding region cannot do that - each test either
        /// reads exactly its own method or fails saying that method is missing.
        /// </summary>
        private static string MethodBody(string startSignature)
        {
            var code = ReadStripped(FacetsRelativePath);

            var start = code.IndexOf(startSignature, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"'{startSignature}' was not found in Player_Facets.cs - has it been renamed? This guard must follow it, not silently scan nothing.");

            // Comments are already stripped, so the next class-indented access modifier is the next member.
            var next = Regex.Match(code.Substring(start + startSignature.Length), @"\n        (?:private|public|internal|protected)\s");

            Assert.IsTrue(next.Success, $"No member declaration follows '{startSignature}' in Player_Facets.cs, so this guard cannot bound the method body it is meant to read. Re-aim it rather than letting it scan the rest of the file.");

            return code.Substring(start, startSignature.Length + next.Index);
        }

        private static void AssertDeclaredGroup(string messageFileName, string expectedGroup)
        {
            var code = ReadStripped("Source/ACE.Server/Network/GameMessages/Messages/" + messageFileName);

            StringAssert.Contains(code, "GameMessageGroup." + expectedGroup,
                $"{messageFileName} no longer declares GameMessageGroup.{expectedGroup}. The facet vault-restore fix is built on which queue each of these rides; re-derive TryEquipWithdrawnForFacet's remarks before changing this expectation.");
        }

        private static string ReadStripped(string relativePath)
        {
            return AccountVaultPurgeTests.StripComments(File.ReadAllText(FindInSourceTree(relativePath)));
        }

        /// <summary>Same walk-up idiom as VaultCollapseTests.FindInSourceTree, kept local rather than shared.</summary>
        private static string FindInSourceTree(string relativePath)
        {
            var native = relativePath.Replace('/', Path.DirectorySeparatorChar);

            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, native);

                if (File.Exists(candidate))
                    return candidate;
            }

            Assert.Fail($"Could not find {relativePath} by walking up from {AppContext.BaseDirectory}.");
            return null;
        }

        #endregion
    }
}
