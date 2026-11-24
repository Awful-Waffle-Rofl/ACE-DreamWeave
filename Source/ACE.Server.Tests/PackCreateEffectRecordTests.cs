using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// A fresh GameMessageCreateObject for an object the client is not drawing (an item put in the pack, or shown
    /// inside a container) rebuilds the object if the client already held it, and a rebuild drops its emitters.
    /// VisualEffectManager.SendTo sends an item's script at most once per session per guid, so a record left in place
    /// across that rebuild makes the next equip skip the script and the item stays bare for the session (the vault
    /// withdraw, trade, vendor and mule hand-back, and Salvage Roller reports). Every such create therefore forgets the
    /// record, and never sends the script itself (WeaponModManager.ResetAuraForRebuild gives why).
    ///
    /// Source assertions, because ACE.Server.Tests cannot build a Player or a Session (FacetVaultRestoreOrderingTests).
    /// They prove each create is written to forget, not that the client renders the aura; the in-game check does that.
    /// </summary>
    [TestClass]
    public class PackCreateEffectRecordTests
    {
        private const string InventoryPath = "Source/ACE.Server/WorldObjects/Player_Inventory.cs";
        private const string ContainerPath = "Source/ACE.Server/WorldObjects/Container.cs";
        private const string DuoGemPath = "Source/ACE.Server/Entity/DuoSummonGem.cs";
        private const string VaultDeliveryPath = "Source/ACE.Server/Entity/AccountVault/VaultPackDelivery.cs";
        private const string ThreadCachePath = "Source/ACE.Server/ThreadDungeons/ThreadCachePlacer.cs";
        private const string TigerEyePath = "Source/ACE.Server/Entity/TigerEyeArmorTinker.cs";
        private const string PrismaticPath = "Source/ACE.Server/Entity/PrismaticDriftStone.cs";
        private const string PackCreateSignature = "public bool TryCreateInInventoryWithNetworking(WorldObject item, out Container container";

        // ---------------- the choke point ----------------

        /// <summary>
        /// Forget runs only on the success path: after the TryAddToInventory guard has returned false for a failed add,
        /// and after the CreateObject that does the rebuilding. A failed add sent nothing, so the record is still true.
        /// </summary>
        [TestMethod]
        public void PackCreate_ForgetsOnlyAfterASuccessfulAddAndItsCreate()
        {
            var body = MethodBody(InventoryPath, PackCreateSignature);

            var guard = body.IndexOf("if (!TryAddToInventory(", StringComparison.Ordinal);
            var failReturn = body.IndexOf("return false;", StringComparison.Ordinal);
            var create = body.IndexOf("new GameMessageCreateObject(item)", StringComparison.Ordinal);
            var firstForget = body.IndexOf("VisualEffectManager.Forget(", StringComparison.Ordinal);
            var forgetItem = body.IndexOf("VisualEffectManager.Forget(Session, item)", StringComparison.Ordinal);
            var success = body.LastIndexOf("return true;", StringComparison.Ordinal);

            Assert.IsTrue(guard >= 0 && failReturn > guard, "the failed-add early return was not found; re-aim this guard");
            Assert.AreEqual(1, Regex.Matches(body, @"return false;").Count, "the pack create grew a second failure return; re-check that Forget cannot run on it");
            Assert.IsTrue(create > failReturn, "the CreateObject send moved ahead of the failed-add return");
            Assert.IsTrue(forgetItem >= 0, "the pack create no longer forgets the item's effect record; a re-created known guid (vault, trade, vendor, roller) stays bare on the next equip");
            Assert.AreEqual(forgetItem, firstForget, "some other Forget runs before the item's own");
            Assert.IsTrue(forgetItem > create, "Forget runs before the CreateObject; it must follow the create that does the rebuilding, on the success path only");
            Assert.IsTrue(success > forgetItem, "Forget sits after the method's success return");
            Assert.AreEqual(1, Regex.Matches(body, @"VisualEffectManager\.Forget\(Session, item\)").Count, "the item is forgotten more than once");
        }

        /// <summary>A container put in the pack also re-creates each item inside it, so each of those is forgotten too.</summary>
        [TestMethod]
        public void PackCreate_ForgetsEachItemInsideACreatedContainer()
        {
            var body = MethodBody(InventoryPath, PackCreateSignature);

            Assert.IsTrue(Regex.IsMatch(body,
                @"foreach \(var obj in itemAsContainer\.Inventory\.Values\)\s*\{\s*Session\.Network\.EnqueueSend\(new GameMessageCreateObject\(obj[^)]*\)\);\s*VisualEffectManager\.Forget\(Session, obj\);\s*\}"),
                "each item inside a container put in the pack gets its own CreateObject; it must be forgotten right after it");
        }

        /// <summary>No double emitter and no burned send: nothing on the pack create path sends a script.</summary>
        [TestMethod]
        public void PackCreate_NeverSendsTheScript()
        {
            var body = MethodBody(InventoryPath, PackCreateSignature);

            Assert.IsFalse(body.Contains("SendTo("), "the pack create calls SendTo: the item is in the pack, so the script attaches to nothing and spends the equip's one send");
            Assert.IsFalse(body.Contains("GameMessagePlayScriptId"), "the pack create sends a script directly");
        }

        /// <summary>The short overload is a pure delegation, so it inherits the forget rather than needing its own.</summary>
        [TestMethod]
        public void PackCreate_ShortOverload_DelegatesToTheChokePoint()
        {
            var body = MethodBody(InventoryPath, "public bool TryCreateInInventoryWithNetworking(WorldObject item)");

            StringAssert.Contains(body, "return TryCreateInInventoryWithNetworking(item, out _);");
            Assert.IsFalse(body.Contains("GameMessageCreateObject"), "the short overload sends its own CreateObject, bypassing the choke point's forget");
        }

        // ---------------- the other non-drawn creates ----------------

        /// <summary>
        /// Opening a chest or corpse re-creates its contents. Looting then MOVES an item (DoHandleActionPutItemInContainer
        /// sends only UpdateInstanceID and ItemServerSaysContainId, no create), so this create is the item's last rebuild
        /// before the equip and is where its record must be forgotten.
        /// </summary>
        [TestMethod]
        public void ContainerOpen_ForgetsEachItemItRecreates_AndSendsNoScript()
        {
            var body = MethodBody(ContainerPath, "private void SendInventory(Player player)");

            Assert.IsTrue(Regex.IsMatch(body, @"itemsToSend\.Add\(new GameMessageCreateObject\(item\)\);\s*(?:ACE\.Server\.Entity\.)?VisualEffectManager\.Forget\(player\.Session, item\);"),
                "Container.SendInventory re-creates each item without forgetting its effect record");
            Assert.IsTrue(Regex.IsMatch(body, @"itemsToSend\.Add\(new GameMessageCreateObject\(containerItem\)\);\s*(?:ACE\.Server\.Entity\.)?VisualEffectManager\.Forget\(player\.Session, containerItem\);"),
                "Container.SendInventory re-creates each nested item without forgetting its effect record");
            Assert.IsFalse(body.Contains("SendTo(") || body.Contains("GameMessagePlayScriptId"), "Container.SendInventory sends a script to an item inside a container");
        }

        /// <summary>The premise behind the test above: a loot move sends no CreateObject for the moved item.</summary>
        [TestMethod]
        public void Premise_PutItemInContainer_SendsNoCreateForTheMovedItem()
        {
            var body = MethodBody(InventoryPath, "private bool DoHandleActionPutItemInContainer(");

            Assert.IsFalse(body.Contains("GameMessageCreateObject"),
                "DoHandleActionPutItemInContainer now sends a CreateObject. That rebuilds the moved item, so it must forget the item's effect record too; re-derive.");
        }

        /// <summary>Picking up a pack re-creates each item inside it.</summary>
        [TestMethod]
        public void PackPickup_ForgetsEachItemItRecreates()
        {
            var code = ReadStripped(InventoryPath);

            Assert.IsTrue(Regex.IsMatch(code, @"Session\.Network\.EnqueueSend\(new GameMessageCreateObject\(packItem\.Value\)\);\s*VisualEffectManager\.Forget\(Session, packItem\.Value\);"),
                "picking up a pack re-creates its contents without forgetting their effect records");
        }

        /// <summary>The summon-gem hammer's network half mirrors the pack create, forget included.</summary>
        [TestMethod]
        public void DuoGemCommitAdd_MirrorsThePackCreateForget()
        {
            var body = MethodBody(DuoGemPath, "public void CommitAdd(WorldObject wo)");

            var create = body.IndexOf("new GameMessageCreateObject(wo)", StringComparison.Ordinal);
            var forget = body.IndexOf("VisualEffectManager.Forget(player.Session, wo)", StringComparison.Ordinal);

            Assert.IsTrue(create >= 0, "CommitAdd no longer sends a CreateObject; re-aim this guard");
            Assert.IsTrue(forget > create, "CommitAdd must forget the effect record after its CreateObject, as TryCreateInInventoryWithNetworking does");
        }

        /// <summary>An open Thread cache's live fill mirrors Container.SendInventory, forget included.</summary>
        [TestMethod]
        public void ThreadCacheDeliver_ForgetsEachItemItCreates()
        {
            var code = ReadStripped(ThreadCachePath);

            Assert.IsTrue(Regex.IsMatch(code, @"viewer\.Session\.Network\.EnqueueSend\(new GameMessageCreateObject\(item\)\);\s*VisualEffectManager\.Forget\(viewer\.Session, item\);"),
                "ThreadCachePlacer.Deliver creates items in an open cache without forgetting their effect records, unlike Container.SendInventory");
        }

        /// <summary>
        /// The Tiger Eye and Prismatic Drift tinkers send an UpdateObject and then re-slot a packed item with
        /// MoveItemToFirstContainerSlot (server-side only; it sends nothing). That is the same pair as WeaponModManager.UpdateObj,
        /// which is observed live to rebuild the object client-side (WeaponModManager's ResetAuraForRebuild call), so each
        /// forgets right after the re-slot, inside the packed branch, the same as RecipeManager.UpdateObj.
        /// </summary>
        [DataTestMethod]
        [DataRow(TigerEyePath)]
        [DataRow(PrismaticPath)]
        public void TinkerUpdateObj_ForgetsAfterThePackedRebuild(string path)
        {
            var body = MethodBody(path, "static void UpdateObj(Player player, WorldObject obj)");

            Assert.IsTrue(Regex.IsMatch(body, @"if \(invObj != null\)\s*\{\s*player\.MoveItemToFirstContainerSlot\(obj\);\s*VisualEffectManager\.Forget\(player\.Session, obj\);\s*\}"),
                $"{path} UpdateObj rebuilds the packed item without forgetting its effect record; the item stays bare on the next equip");
            Assert.IsFalse(body.Contains("SendTo(") || body.Contains("GameMessagePlayScriptId"), $"{path} UpdateObj sends a script to a packed item");
        }

        // ---------------- audited callers ----------------

        /// <summary>The vault withdraw (the owner's reported case) delivers through the choke point.</summary>
        [TestMethod]
        public void VaultWithdraw_DeliversThroughThePackCreate()
        {
            StringAssert.Contains(ReadStripped(VaultDeliveryPath), "player.TryCreateInInventoryWithNetworking);",
                "VaultPackDelivery.WithdrawToPack no longer binds the pack delivery to Player.TryCreateInInventoryWithNetworking, which is what forgets the effect record; re-audit the withdraw");
        }

        /// <summary>
        /// Audit tripwire. Every GameMessageCreateObject construction in ACE.Server, by file, as of the audit that put the
        /// forget into the pack create. A new site, or one moved between files, fails here so it gets the same question:
        /// does it rebuild an object the client may have recorded, and if so is that object drawn (SendTo rebuilt:true) or
        /// not (Forget)? Update the table only after answering it.
        /// </summary>
        [TestMethod]
        public void CreateObjectSites_MatchTheAudit()
        {
            var expected = new SortedDictionary<string, int>(StringComparer.Ordinal)
            {
                { "Entity/DuoSummonGem.cs", 1 },                 // CommitAdd: forgets
                { "ThreadDungeons/ThreadCachePlacer.cs", 1 },    // Deliver: forgets (recycled-guid guard)
                { "WorldObjects/Container.cs", 2 },              // SendInventory: forgets
                { "WorldObjects/Player_Facets.cs", 1 },          // equip-on-withdraw: SendTo rebuilt:true
                { "WorldObjects/Player_Inventory.cs", 5 },       // pack create x2 and pack pickup: forget; split stack: new guid; landscape merge ghost: ground object
                { "WorldObjects/Player_Networking.cs", 4 },      // login: VisualEffectsSent is new with the Player
                { "WorldObjects/Player_Tracking.cs", 4 },        // drawn objects: SendTo(resend)
                { "WorldObjects/WorldObject_Networking.cs", 1 }, // shared create for TrackObject
            };

            AssertCensus(@"new GameMessageCreateObject\(", expected);
        }

        /// <summary>
        /// The same tripwire for GameMessageUpdateObject. The UpdateObject plus container re-slot sequence of
        /// WeaponModManager.UpdateObj is observed live to rebuild the object client-side (WeaponModManager's
        /// ResetAuraForRebuild call); MoveItemToFirstContainerSlot alone is server-side only and sends nothing. Whether a
        /// bare UpdateObject with no re-slot drops a live emitter is UNPROVEN (Docs/WeaponMods/VISUAL-EFFECTS.md 3.1 covers
        /// appearance, not emitters); the owner's tailoring check in PR #1970 decides it. So the question a new site must
        /// answer is whether it follows that same sequence (then Forget, as RecipeManager, WeaponModManager and the two
        /// tinkers pinned above do) or not (then forgetting may let the next equip stack a second emitter on a live one).
        /// </summary>
        [TestMethod]
        public void UpdateObjectSites_MatchTheAudit()
        {
            var expected = new SortedDictionary<string, int>(StringComparer.Ordinal)
            {
                // weapon and armor rebuilds that already forget
                { "Managers/RecipeManager.cs", 1 },               // UpdateObj: UpdateObject + re-slot, then Forget
                { "WeaponMods/WeaponModManager.cs", 1 },          // UpdateObj: UpdateObject + re-slot, then ResetAuraForRebuild
                { "WeaponMods/ParagonModManager.cs", 1 },         // UpdateObj: UpdateObject + re-slot, then Forget (pack weapons only)
                { "Entity/TigerEyeArmorTinker.cs", 1 },           // UpdateObj: UpdateObject + re-slot, then Forget (pinned above)
                { "Entity/PrismaticDriftStone.cs", 1 },           // UpdateObj: UpdateObject + re-slot, then Forget (pinned above)
                { "EquipmentMods/EquipmentModManager.cs", 1 },    // Forget
                { "RefireStations/RefireStationCommon.cs", 1 },   // Forget
                { "SpellReroll/SpellRerollManager.cs", 1 },       // Forget
                { "Command/Handlers/PvpHueCommands.cs", 2 },      // Forget
                // bare UpdateObject with no re-slot: whether it drops a live emitter is unproven; the owner tailoring check decides
                { "Entity/Tailoring.cs", 2 },                     // Forget only when the script changed; see PR #1970
                { "Entity/Aetheria.cs", 1 },                      // a sigil, not a drawn weapon
                // non-weapon or world objects, not audited for the effect record
                { "Entity/DuoSummonGem.cs", 1 },
                { "Entity/SacrificialDagger.cs", 1 },
                { "HazardMechanics/HazardContext.cs", 1 },
                { "Knots/KnotLowering.cs", 1 },
                { "Knots/KnotSeam.cs", 2 },
                { "Pinnacles/LivePinnacleWorld.cs", 1 },
                { "ThreadDungeons/FragmentMold.cs", 2 },
                { "ThreadDungeons/RawFragment.cs", 1 },
                { "ThreadDungeons/ThreadDungeonGemHandler.cs", 2 },
                { "WorldObjects/Gem.cs", 1 },
                { "WorldObjects/Hook.cs", 2 },
                { "WorldObjects/PetDevice.cs", 1 },
                { "WorldObjects/Player.cs", 1 },
                { "WorldObjects/WorldObject.cs", 1 },
            };

            AssertCensus(@"new GameMessageUpdateObject\(", expected);
        }

        private static void AssertCensus(string pattern, SortedDictionary<string, int> expected)
        {
            var root = FindInSourceTree("Source/ACE.Server/ACE.Server.csproj");
            var serverDir = Path.GetDirectoryName(root);
            var actual = new SortedDictionary<string, int>(StringComparer.Ordinal);

            foreach (var file in Directory.EnumerateFiles(serverDir, "*.cs", SearchOption.AllDirectories))
            {
                var relative = file.Substring(serverDir.Length + 1).Replace(Path.DirectorySeparatorChar, '/');

                if (relative.StartsWith("bin/", StringComparison.Ordinal) || relative.StartsWith("obj/", StringComparison.Ordinal))
                    continue;

                var count = Regex.Matches(AccountVaultPurgeTests.StripComments(File.ReadAllText(file)), pattern).Count;

                if (count > 0)
                    actual[relative] = count;
            }

            CollectionAssert.AreEqual(expected.ToList(), actual.ToList(),
                $"'{pattern}' sites changed since the pack-create effect-record audit. Actual: " + string.Join(", ", actual.Select(kv => $"{{ \"{kv.Key}\", {kv.Value} }}")));
        }

        // ---------------- helpers ----------------

        /// <summary>One member's body, bounded by the next class-indented member declaration (FacetVaultRestoreOrderingTests' idiom).</summary>
        internal static string MethodBody(string relativePath, string startSignature)
        {
            var code = ReadStripped(relativePath);

            var start = code.IndexOf(startSignature, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"'{startSignature}' was not found in {relativePath}; re-aim this guard rather than scanning nothing.");

            var next = Regex.Match(code.Substring(start + startSignature.Length), @"\n        (?:private|public|internal|protected)\s");

            return next.Success ? code.Substring(start, startSignature.Length + next.Index) : code.Substring(start);
        }

        internal static string ReadStripped(string relativePath)
        {
            return AccountVaultPurgeTests.StripComments(File.ReadAllText(FindInSourceTree(relativePath)));
        }

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
    }
}
