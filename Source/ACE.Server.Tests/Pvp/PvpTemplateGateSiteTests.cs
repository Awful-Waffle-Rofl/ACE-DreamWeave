using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ClassAbilities;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameEvent;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Pvp.Templates;
using ACE.Server.WorldObjects;
using ACE.Server.WorldObjects.Managers;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// One test per PvP template gate site (TEMPLATES.md "Gates"), each naming the call site it reached.
    ///
    /// Where the harness allows, the test calls the REAL handler on a seeded Player (no database, no world, no
    /// landblock) whose Session is a recording fake, and asserts what the player was told: the refusal text and
    /// the packet that releases the client. Where a site cannot be reached without a landblock (a pickup off the
    /// ground, a give to an NPC standing in the world, a vendor in range) the test pins the narrowest real
    /// method that can be reached and a SOURCE SCAN of the handler that proves the gate call is present and runs
    /// before the first side effect (the move-to chain, the first write). The scans bind the identifier, so
    /// deleting a gate call fails its test.
    ///
    /// Every site's test was shown to fail with its one gate call neutralized and to pass with it restored
    /// (mutation run done when this file was written; to repeat it, replace a site's gate expression with null and run this class).
    /// </summary>
    [TestClass]
    public class PvpTemplateGateSiteTests
    {
        private Func<bool> savedEnabled;
        private Func<bool> savedKeepOwnBuffs;
        private Func<bool> savedSuppressHeritage;
        private Action<WorldObject> savedSave;
        private Action<WorldObject> savedDestroy;

        [TestInitialize]
        public void Setup()
        {
            // The server registers the code-page provider at startup; GameMessageSystemChat writes windows-1252.
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

            savedEnabled = PvpTemplateSettings.EnabledSource;
            savedKeepOwnBuffs = PvpTemplateSettings.KeepOwnBuffsSource;
            savedSuppressHeritage = PvpTemplateSettings.SuppressHeritageBonusSource;
            savedSave = PvpTemplateSettings.SaveBiota;
            savedDestroy = PvpTemplateSettings.DestroyItem;

            PvpTemplateSettings.SaveBiota = wo => { };
            PvpTemplateSettings.DestroyItem = wo => { };
        }

        [TestCleanup]
        public void Cleanup()
        {
            PvpTemplateSettings.EnabledSource = savedEnabled;
            PvpTemplateSettings.KeepOwnBuffsSource = savedKeepOwnBuffs;
            PvpTemplateSettings.SuppressHeritageBonusSource = savedSuppressHeritage;
            PvpTemplateSettings.SaveBiota = savedSave;
            PvpTemplateSettings.DestroyItem = savedDestroy;
        }

        // ======================================================================================
        // fixtures
        // ======================================================================================

        /// <summary>A Session whose NetworkSession keeps what is sent in its bundles, so a test can read it back.</summary>
        private sealed class Recorder
        {
            private readonly NetworkBundle[] bundles = new NetworkBundle[(int)GameMessageGroup.QueueMax];
            public readonly List<GameMessage> Sent = new List<GameMessage>();
            public Session Session;

            public Recorder(Player player)
            {
                var session = (Session)RuntimeHelpers.GetUninitializedObject(typeof(Session));
                var network = (NetworkSession)RuntimeHelpers.GetUninitializedObject(typeof(NetworkSession));

                var locks = new object[bundles.Length];

                for (var i = 0; i < bundles.Length; i++)
                {
                    locks[i] = new object();
                    bundles[i] = new NetworkBundle();
                }

                PvpTemplatePlayerTests.SetField(typeof(NetworkSession), network, "session", session);
                PvpTemplatePlayerTests.SetField(typeof(NetworkSession), network, "currentBundleLocks", locks);
                PvpTemplatePlayerTests.SetField(typeof(NetworkSession), network, "currentBundles", bundles);

                session.Network = network;
                Session = session;
                PvpTemplatePlayerTests.SetField(typeof(Session), session, "<Player>k__BackingField", player);
                PvpTemplatePlayerTests.SetField(typeof(Session), session, "<Account>k__BackingField", "gate-test-account");

                SetInstanceFieldWithoutClassInit(typeof(Player), player, "<Session>k__BackingField", session);
            }

            /// <summary>
            /// FieldInfo.SetValue runs the declaring type's static constructor first, and Player's reads the World
            /// database (the same trap PvpTemplatePlayerTests documents for its seeded Player). A DynamicMethod stfld
            /// is a plain instance store and runs nothing.
            /// </summary>
            public static void SetInstanceFieldWithoutClassInit(Type declaring, object target, string name, object value)
            {
                var field = declaring.GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                Assert.IsNotNull(field, $"{declaring.Name}.{name} was not found by reflection");

                var store = new System.Reflection.Emit.DynamicMethod("store_" + name, typeof(void), new[] { typeof(object), typeof(object) }, declaring.Module, true);
                var il = store.GetILGenerator();
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
                il.Emit(System.Reflection.Emit.OpCodes.Castclass, declaring);
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
                il.Emit(System.Reflection.Emit.OpCodes.Castclass, field.FieldType);
                il.Emit(System.Reflection.Emit.OpCodes.Stfld, field);
                il.Emit(System.Reflection.Emit.OpCodes.Ret);

                ((Action<object, object>)store.CreateDelegate(typeof(Action<object, object>)))(target, value);
            }

            public void Drain()
            {
                foreach (var bundle in bundles)
                {
                    while (bundle.HasMoreMessages)
                        Sent.Add(bundle.Dequeue());
                }
            }

            public List<string> Chat()
            {
                Drain();
                return Sent.OfType<GameMessageSystemChat>().Select(m => m.Text).ToList();
            }

            public int Events(GameEventType type)
            {
                Drain();
                return Sent.OfType<GameEventMessage>().Count(e => e.EventType == type);
            }
        }

        private static Player Templated(params int[] templateSpells)
        {
            var player = PvpTemplatePlayerTests.SeededPlayer();
            player.SetProperty(PropertyString.PvpTemplateRestore, PvpTemplatePlayerTests.RecordWithSpells(templateSpells));
            return player;
        }

        private static Player Plain() => PvpTemplatePlayerTests.SeededPlayer();

        private static uint nextGuid = 0x80300000;

        private static T Seed<T>(bool issued, uint wcid = 0, int? stackSize = null) where T : WorldObject
        {
            var wo = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

            var biota = new Biota
            {
                WeenieClassId = wcid,
                WeenieType = WeenieType.Generic,
                PropertiesBool = new Dictionary<PropertyBool, bool>(),
                PropertiesInt = new Dictionary<PropertyInt, int>(),
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, issued ? "Issued Thing" : "Own Thing" } },
                PropertiesIID = new Dictionary<PropertyInstanceId, uint>(),
            };

            if (issued)
                biota.PropertiesBool[PropertyBool.PvpTemplateIssued] = true;

            if (stackSize.HasValue)
            {
                biota.PropertiesInt[PropertyInt.StackSize] = stackSize.Value;
                biota.PropertiesInt[PropertyInt.MaxStackSize] = 100;
            }

            PvpTemplatePlayerTests.SeedWorldObject(wo, biota, Interlocked.Increment(ref nextGuid));

            return wo;
        }

        private static void Pack(Player player, WorldObject item)
        {
            item.PlacementPosition = player.Inventory.Count;
            player.Inventory[item.Guid] = item;
        }

        private static void Wear(Player player, WorldObject item)
        {
            player.EquippedObjects[item.Guid] = item;
        }

        private static Container SidePack(Player player, params WorldObject[] contents)
        {
            var pack = Seed<Container>(false);
            PvpTemplatePlayerTests.SetField(typeof(Container), pack, "<Inventory>k__BackingField", new Dictionary<ObjectGuid, WorldObject>());

            foreach (var item in contents)
                pack.Inventory[item.Guid] = item;

            Pack(player, pack);
            return pack;
        }

        // ---- source scan ----

        private static string SourceRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Source", "ACE.Server")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find Source/ACE.Server by walking up from {AppContext.BaseDirectory}");

            return Path.Combine(dir.FullName, "Source", "ACE.Server");
        }

        /// <summary>The text of a source file, line endings normalised, line comments and string literals blanked so braces in them do not count.</summary>
        private static string Code(string relativePath)
        {
            var path = Path.Combine(SourceRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.IsTrue(File.Exists(path), $"{path} not found");

            var text = File.ReadAllText(path).Replace("\r\n", "\n");
            text = Regex.Replace(text, @"//[^\n]*", "");
            text = Regex.Replace(text, "\"(?:[^\"\\\\\n]|\\\\.)*\"", "\"\"");

            return text;
        }

        /// <summary>The body of the method whose declaration contains <paramref name="signature"/> (matched as the first occurrence followed by the parameter list's opening paren), brace-matched.</summary>
        private static string Body(string relativePath, string signature, string parameterHint = null)
        {
            var code = Code(relativePath);
            var declaration = Regex.Matches(code, @"(?m)^\s*(?:public|private|internal|protected)[^;{=\n]*\b" + Regex.Escape(signature) + @"\(([^)]*)\)")
                .Cast<Match>().FirstOrDefault(m => parameterHint == null || m.Groups[1].Value.Contains(parameterHint));

            Assert.IsNotNull(declaration, $"{relativePath}: declaration of {signature}({parameterHint}...) not found");

            var open = code.IndexOf('{', declaration.Index);
            var depth = 0;

            for (var i = open; i < code.Length; i++)
            {
                if (code[i] == '{') depth++;
                else if (code[i] == '}' && --depth == 0)
                    return code.Substring(open, i - open + 1);
            }

            Assert.Fail($"{relativePath}: unbalanced braces in {signature}");
            return null;
        }

        /// <summary>
        /// The site calls the named gate and the call precedes every side-effect marker. The markers are substrings
        /// of the method's own later code (the move-to chain, the first write), so the order is checked in source.
        /// </summary>
        private static void AssertGated(string relativePath, string signature, string gate, params string[] sideEffects)
        {
            // "Name|parameter text" picks one overload by a fragment of its parameter list.
            var parts = signature.Split('|');
            var body = Body(relativePath, parts[0], parts.Length > 1 ? parts[1] : null);
            var at = body.IndexOf(gate, StringComparison.Ordinal);

            Assert.IsTrue(at >= 0, $"{relativePath}: {signature} no longer contains the gate call `{gate}`");

            foreach (var marker in sideEffects)
            {
                var m = body.IndexOf(marker, StringComparison.Ordinal);
                Assert.IsTrue(m >= 0, $"{relativePath}: {signature} no longer contains the side-effect marker `{marker}`; update this test");
                Assert.IsTrue(at < m, $"{relativePath}: in {signature} the gate `{gate}` runs AFTER `{marker}`; a gate must run before any side effect");
            }
        }

        private static void AssertRefused(Recorder rec, string text, string site)
        {
            var chat = rec.Chat();
            Assert.IsTrue(chat.Contains(text), $"{site}: expected the refusal `{text}`; the player was told: [{string.Join(" | ", chat)}]");
        }

        // ======================================================================================
        // inventory: Player_Inventory.cs
        // ======================================================================================

        [TestMethod]
        public void HandleActionGetAndWieldItem_PersonalItemWhileTemplated_IsRefusedAndTheClientReleased()
        {
            var player = Templated(3);
            var rec = new Recorder(player);
            var own = Seed<GenericObject>(false);
            Pack(player, own);

            player.HandleActionGetAndWieldItem(own.Guid.Full, EquipMask.MeleeWeapon);

            AssertRefused(rec, PvpTemplateText.PersonalItemLocked, "HandleActionGetAndWieldItem");
            Assert.IsTrue(rec.Events(GameEventType.InventoryServerSaveFailed) >= 1, "the client must be released with an InventoryServerSaveFailed");
            Assert.IsTrue(player.Inventory.ContainsKey(own.Guid) && player.EquippedObjects.Count == 0, "nothing was equipped");
            AssertGated("WorldObjects/Player_Inventory.cs", "HandleActionGetAndWieldItem", "PvpTemplateBlocked(PvpTemplateAction.Equip, item)", "CreateMoveToChain(", "DoHandleActionGetAndWieldItem(");
        }

        [TestMethod]
        public void HandleActionGetAndWieldItem_IssuedItemOutsideAMatch_IsRefused()
        {
            var player = Plain();
            var rec = new Recorder(player);
            var issued = Seed<GenericObject>(true);
            Pack(player, issued);

            player.HandleActionGetAndWieldItem(issued.Guid.Full, EquipMask.MeleeWeapon);

            AssertRefused(rec, PvpTemplateText.IssuedItemOutsideMatch, "HandleActionGetAndWieldItem (not templated)");
        }

        /// <summary>
        /// Owner ruling 2026-10-04: issued gear can be taken off and put back on freely while templated (the issued
        /// shield off, a melee player switching to the issued wand). The destination is the player's own inventory,
        /// main pack or a side pack; the gate that sees the unequip asks Unequip, which no longer refuses.
        /// </summary>
        [TestMethod]
        public void HandleActionPutItemInContainer_UnequippingAnIssuedItem_IsAllowed_ToTheMainPackOrASidePack()
        {
            var player = Templated(3);
            var rec = new Recorder(player);
            var shield = Seed<GenericObject>(true);
            Wear(player, shield);
            var pack = SidePack(player);

            // The handler's gate expression, evaluated for this item: past it the handler does the real move, which
            // this harness cannot drive, so the expression itself is pinned in the handler below.
            Assert.AreEqual(PvpTemplateAction.Unequip, Player.PvpTemplateMoveAction(true, true, true, false), "an unequip into the player's own inventory is Unequip");
            Assert.IsNull(player.PvpTemplateBlocked(Player.PvpTemplateMoveAction(true, true, true, false), shield), "an issued unequip while templated is allowed");
            Assert.IsNotNull(player.FindObject(pack.Guid.Full, Player.SearchLocations.MyInventory, out _, out var root, out _));
            Assert.AreSame(player, root, "a side pack is the player's own inventory too (containerRootOwner == this)");

            AssertGated("WorldObjects/Player_Inventory.cs", "HandleActionPutItemInContainer",
                "PvpTemplateBlocked(PvpTemplateMoveAction(itemRootOwner == this, containerRootOwner == this, itemWasEquipped, false), item)",
                "DoHandleActionPutItemInContainer(");
            Assert.AreEqual(0, rec.Chat().Count);
        }

        [TestMethod]
        public void PersonalEquip_IsStillRefusedWhileTemplated_AfterTheUnequipRuling()
        {
            var player = Templated(3);
            var own = Seed<GenericObject>(false);
            Pack(player, own);

            Assert.AreEqual(PvpTemplateText.PersonalItemLocked, player.PvpTemplateBlocked(PvpTemplateAction.Equip, own));

            // Unequipping a personal item was always allowed (it is how the backstop's survivors come off).
            var worn = Seed<GenericObject>(false);
            Wear(player, worn);
            Assert.IsNull(player.PvpTemplateBlocked(PvpTemplateAction.Unequip, worn));
        }

        [TestMethod]
        public void AnUnequippedIssuedItem_StillCannotLeaveThePlayer()
        {
            var player = Templated(3);
            var rec = new Recorder(player);
            var issued = Seed<GenericObject>(true);
            Pack(player, issued);
            var packed = Seed<GenericObject>(true);
            var pack = SidePack(player, packed);

            player.HandleActionDropItem(issued.Guid.Full);
            AssertRefused(rec, PvpTemplateText.IssuedItemLocked, "HandleActionDropItem (unequipped issued item, templated)");
            Assert.IsTrue(player.Inventory.ContainsKey(issued.Guid), "the issued item was not dropped");

            // Out of the player to anywhere else, from the pack or straight from a slot, is the foreign-container move.
            Assert.AreEqual(PvpTemplateAction.MoveToForeignContainer, Player.PvpTemplateMoveAction(true, false, false, false));
            Assert.AreEqual(PvpTemplateAction.MoveToForeignContainer, Player.PvpTemplateMoveAction(true, false, true, false));

            foreach (var action in new[] { PvpTemplateAction.Drop, PvpTemplateAction.MoveToForeignContainer, PvpTemplateAction.GiveToPlayer, PvpTemplateAction.GiveToNpc, PvpTemplateAction.Trade, PvpTemplateAction.Salvage, PvpTemplateAction.Vault, PvpTemplateAction.Mule, PvpTemplateAction.MarketList, PvpTemplateAction.VendorSell })
            {
                Assert.AreEqual(PvpTemplateText.IssuedItemLocked, player.PvpTemplateBlocked(action, issued), $"{action} of an unequipped issued item");
                Assert.AreEqual(PvpTemplateText.IssuedItemLocked, player.PvpTemplateBlocked(action, pack), $"{action} of a side pack holding an issued item");
            }
        }

        /// <summary>
        /// A swap: a melee player with the issued shield on wields the issued wand. The server never auto-displaces
        /// (the DoHandleActionGetAndWieldItem_DequipItemToInventory calls are commented out); the client sends the
        /// shield's unequip first (PutItemInContainer, Unequip) and then the wield (GetAndWieldItem, Equip). Both must pass.
        /// </summary>
        [TestMethod]
        public void DisplacementSwap_IssuedWandDisplacingTheIssuedShield_IsAllowed()
        {
            var player = Templated(3);
            var rec = new Recorder(player);
            var shield = Seed<GenericObject>(true);
            Wear(player, shield);
            var wand = Seed<GenericObject>(true);
            Pack(player, wand);

            Assert.IsNull(player.PvpTemplateBlocked(Player.PvpTemplateMoveAction(true, true, true, false), shield), "the client's unequip of the issued shield passes");
            Assert.IsNull(player.PvpTemplateBlocked(PvpTemplateAction.Equip, wand), "the issued wand may be equipped");
            Assert.AreEqual(0, rec.Chat().Count);
        }

        /// <summary>
        /// Review of #1530: the kit goes on without wield checks, so an issued item the player took off must go back on
        /// without them too. A level-60 player re-equipping a level-100 issued shield was refused (and an issued item
        /// cannot be dropped), leaving it stuck in the pack for the rest of the match.
        /// </summary>
        [TestMethod]
        public void ReEquip_AnIssuedItemAboveThePlayersLevel_SkipsTheWieldRequirements()
        {
            var player = Templated(3);
            player.SetProperty(PropertyInt.Level, 60);
            var rec = new Recorder(player);

            var armor = Seed<GenericObject>(true);
            armor.SetProperty(PropertyInt.ValidLocations, (int)EquipMask.ChestArmor);
            armor.SetProperty(PropertyInt.WieldRequirements, (int)WieldRequirement.Level);
            armor.SetProperty(PropertyInt.WieldDifficulty, 100);
            Pack(player, armor);

            // CheckWieldRequirements reads use_wield_requirements; set it so the check is live, and put it back after.
            var priorWieldRequirements = DefaultPropertyManager.DefaultBooleanProperties["use_wield_requirements"].Item;
            Assert.IsTrue(PropertyManager.ModifyBool("use_wield_requirements", true));

            try
            {
                // Positive control: the same requirement on a personal item, outside a template, is refused by the wield check.
                var plain = Plain();
                plain.SetProperty(PropertyInt.Level, 60);
                var plainRec = new Recorder(plain);
                var own = Seed<GenericObject>(false);
                own.SetProperty(PropertyInt.ValidLocations, (int)EquipMask.ChestArmor);
                own.SetProperty(PropertyInt.WieldRequirements, (int)WieldRequirement.Level);
                own.SetProperty(PropertyInt.WieldDifficulty, 100);
                Pack(plain, own);
                plain.HandleActionGetAndWieldItem(own.Guid.Full, EquipMask.ChestArmor);
                Assert.IsFalse(plain.EquippedObjects.ContainsKey(own.Guid), "control: a level-100 item does not go on a level-60 player");

                // Past the wield check the item goes into EquippedObjects (Creature.TryEquipObject) and then into the
                // wield hooks (TrySetChild, OnWield), which need world state this harness does not build. Reaching
                // EquippedObjects is the proof the wield requirements were waived; the hooks' harness throw is not.
                try
                {
                    player.HandleActionGetAndWieldItem(armor.Guid.Full, EquipMask.ChestArmor);
                }
                catch (NullReferenceException) when (player.EquippedObjects.ContainsKey(armor.Guid))
                {
                }

                Assert.AreEqual(0, rec.Chat().Count, $"[{string.Join(" | ", rec.Chat())}]");
                Assert.IsTrue(plainRec.Events(GameEventType.InventoryServerSaveFailed) >= 1, "control: the refusal is an InventoryServerSaveFailed");
                Assert.AreEqual(0, rec.Events(GameEventType.InventoryServerSaveFailed), "the issued armor was refused on re-equip (wield requirements)");
                Assert.IsTrue(player.EquippedObjects.ContainsKey(armor.Guid), "the issued armor is back on");
            }
            finally
            {
                PropertyManager.ModifyBool("use_wield_requirements", priorWieldRequirements);
            }
        }

        [TestMethod]
        public void DoHandleActionGetAndWieldItem_AsksTheIssuedWaiverBeforeTheWieldCheck()
        {
            var body = Body("WorldObjects/Player_Inventory.cs", "DoHandleActionGetAndWieldItem", "WorldObject item, Container fromContainer");
            var marked = body.IndexOf("PvpTemplateWaivesWieldRequirements(item)", StringComparison.Ordinal);
            var check = body.IndexOf("CheckWieldRequirements(item)", StringComparison.Ordinal);
            Assert.IsTrue(marked >= 0 && check >= 0 && marked < check, "DoHandleActionGetAndWieldItem must ask PvpTemplateWaivesWieldRequirements (IsMarkedIssued) before CheckWieldRequirements");
        }

        [TestMethod]
        public void DeathDrop_ExcludesAPersonalPackHoldingAnIssuedItem()
        {
            var player = Templated(3);
            var issued = Seed<GenericObject>(true);
            var pack = SidePack(player, issued);
            var own = Seed<GenericObject>(false);
            Pack(player, own);

            Assert.IsTrue(Player.ExcludedFromDeathDrop(pack), "a personal pack holding an issued item never goes to the corpse");
            Assert.IsTrue(Player.ExcludedFromDeathDrop(issued), "an issued item never goes to the corpse");
            Assert.IsFalse(Player.ExcludedFromDeathDrop(own), "control: an ordinary personal item is still a death-drop candidate");

            var bonded = Seed<GenericObject>(false);
            bonded.SetProperty(PropertyInt.Bonded, (int)BondedStatus.Bonded);
            Assert.IsTrue(Player.ExcludedFromDeathDrop(bonded), "Bonded is still excluded");

            var site = Body("WorldObjects/Player_Death.cs", "CalculateDeathItems", "Corpse corpse");
            Assert.IsTrue(site.Contains("Where(i => !ExcludedFromDeathDrop(i))"), "CalculateDeathItems keeps only the candidates ExcludedFromDeathDrop does not exclude");

            // The Slippery path (always drops on death) runs its own query: a Slippery personal pack holding an issued
            // item must not reach the corpse either, while an ordinary Slippery item still does.
            var slipperyPack = SidePack(player, Seed<GenericObject>(true));
            slipperyPack.SetProperty(PropertyInt.Bonded, (int)BondedStatus.Slippery);
            var slipperyOwn = Seed<GenericObject>(false);
            slipperyOwn.SetProperty(PropertyInt.Bonded, (int)BondedStatus.Slippery);
            Pack(player, slipperyOwn);

            var slippery = Player.SlipperyDeathDrops(player.GetAllPossessions());
            Assert.IsFalse(slippery.Contains(slipperyPack), "a Slippery personal pack holding an issued item never goes to the corpse");
            CollectionAssert.AreEqual(new WorldObject[] { slipperyOwn }, slippery, "control: an ordinary Slippery item still drops");
            Assert.IsTrue(Body("WorldObjects/Player_Death.cs", "GetSlipperyItems").Contains("SlipperyDeathDrops(GetAllPossessions())"), "GetSlipperyItems goes through SlipperyDeathDrops");
        }

        [TestMethod]
        public void PvpTemplateWaivesWieldRequirements_TruthTable()
        {
            var issued = Seed<GenericObject>(true);
            var personal = Seed<GenericObject>(false);

            Assert.IsTrue(Templated(3).PvpTemplateWaivesWieldRequirements(issued), "templated + issued: waived");
            Assert.IsFalse(Templated(3).PvpTemplateWaivesWieldRequirements(personal), "templated + personal: checked");
            Assert.IsFalse(Plain().PvpTemplateWaivesWieldRequirements(issued), "not templated + issued: checked");
            Assert.IsFalse(Plain().PvpTemplateWaivesWieldRequirements(personal), "not templated + personal: checked");
        }

        [TestMethod]
        public void HandleActionPutItemInContainer_PickupAndMoveOut_RunTheGateBeforeTheMoveToChain()
        {
            // A pickup or a move to a world container needs a landblock to find the object, so the reachable parts are
            // the mapping from the move to a gate action, the decision for each, and the order in the handler.
            Assert.AreEqual(PvpTemplateAction.MoveWithinPack, Player.PvpTemplateMoveAction(true, true, false, false));
            Assert.AreEqual(PvpTemplateAction.Unequip, Player.PvpTemplateMoveAction(true, true, true, false));
            Assert.AreEqual(PvpTemplateAction.MergeOrSplit, Player.PvpTemplateMoveAction(true, true, false, true));
            Assert.AreEqual(PvpTemplateAction.GroundPickup, Player.PvpTemplateMoveAction(false, true, false, false));
            Assert.AreEqual(PvpTemplateAction.MoveToForeignContainer, Player.PvpTemplateMoveAction(true, false, false, false));
            Assert.AreEqual(PvpTemplateAction.MoveToForeignContainer, Player.PvpTemplateMoveAction(true, false, true, true));

            var player = Templated(3);
            var ground = Seed<GenericObject>(false);

            Assert.AreEqual(PvpTemplateText.EconomyLocked, player.PvpTemplateBlocked(PvpTemplateAction.GroundPickup, ground), "a pickup while templated");
            Assert.IsNull(Plain().PvpTemplateBlocked(PvpTemplateAction.GroundPickup, ground), "a pickup while not templated");

            AssertGated("WorldObjects/Player_Inventory.cs", "HandleActionPutItemInContainer",
                "PvpTemplateBlocked(PvpTemplateMoveAction(itemRootOwner == this, containerRootOwner == this, itemWasEquipped, false), item)",
                "CreateMoveToChain(", "DoHandleActionPutItemInContainer(");
        }

        [TestMethod]
        public void HandleActionDropItem_PersonalItemWhileTemplated_IsRefused()
        {
            var player = Templated(3);
            var rec = new Recorder(player);
            var own = Seed<GenericObject>(false);
            Pack(player, own);

            player.HandleActionDropItem(own.Guid.Full);

            AssertRefused(rec, PvpTemplateText.EconomyLocked, "HandleActionDropItem");
            Assert.IsTrue(player.Inventory.ContainsKey(own.Guid), "the item was not dropped");
            Assert.IsTrue(rec.Events(GameEventType.InventoryServerSaveFailed) >= 1);
        }

        [TestMethod]
        public void HandleActionDropItem_IssuedItemWhenNotTemplated_IsRefused()
        {
            var player = Plain();
            var rec = new Recorder(player);
            var issued = Seed<GenericObject>(true);
            Pack(player, issued);

            player.HandleActionDropItem(issued.Guid.Full);

            AssertRefused(rec, PvpTemplateText.IssuedItemLocked, "HandleActionDropItem (issued, not templated)");
            AssertGated("WorldObjects/Player_Inventory.cs", "HandleActionDropItem", "PvpTemplateBlocked(PvpTemplateAction.Drop, item)", "StartPickupChain(");
        }

        [TestMethod]
        public void HandleActionStackableMerge_IssuedIntoPersonalStack_IsRefused()
        {
            var player = Templated(3);
            var rec = new Recorder(player);
            var source = Seed<Stackable>(true, 7001, 5);
            var target = Seed<Stackable>(false, 7001, 5);
            Pack(player, source);
            Pack(player, target);

            player.HandleActionStackableMerge(source.Guid.Full, target.Guid.Full, 3);

            AssertRefused(rec, PvpTemplateText.MixedStack, "HandleActionStackableMerge");
            Assert.AreEqual(5, source.StackSize, "the source stack is untouched");
            Assert.AreEqual(5, target.StackSize, "the target stack is untouched");
            Assert.IsTrue(rec.Events(GameEventType.InventoryServerSaveFailed) >= 1);
        }

        [TestMethod]
        public void HandleActionStackableMerge_PersonalIntoIssuedStack_IsRefused_AndIssuedIntoIssuedIsNot()
        {
            var player = Templated(3);
            var rec = new Recorder(player);
            var personal = Seed<Stackable>(false, 7002, 5);
            var issued = Seed<Stackable>(true, 7002, 5);
            Pack(player, personal);
            Pack(player, issued);

            player.HandleActionStackableMerge(personal.Guid.Full, issued.Guid.Full, 3);

            AssertRefused(rec, PvpTemplateText.MixedStack, "HandleActionStackableMerge (other direction)");

            // The decision for the permitted case, on the same seeded pair of kinds.
            var issued2 = Seed<Stackable>(true, 7002, 5);
            Pack(player, issued2);
            Assert.IsNull(player.PvpTemplateBlocked(PvpTemplateAction.MergeOrSplit, issued, issued2), "issued into issued is allowed");

            AssertGated("WorldObjects/Player_Inventory.cs", "HandleActionStackableMerge",
                "PvpTemplateBlocked(PvpTemplateMoveAction(sourceStackRootOwner == this, targetStackRootOwner == this, false, true), sourceStack,",
                "CanMergeToInventory(", "CreateMoveToChain(");
        }

        [TestMethod]
        public void HandleActionStackableSplitToContainer_PersonalStackIntoAPackHoldingIssuedItems_IsRefused()
        {
            var player = Templated(3);
            var rec = new Recorder(player);
            var stack = Seed<Stackable>(false, 7003, 10);
            Pack(player, stack);
            var pack = SidePack(player, Seed<GenericObject>(true));

            player.HandleActionStackableSplitToContainer(stack.Guid.Full, pack.Guid.Full, 0, 4);

            AssertRefused(rec, PvpTemplateText.MixedStack, "HandleActionStackableSplitToContainer");
            Assert.AreEqual(10, stack.StackSize, "the stack was not split");
            AssertGated("WorldObjects/Player_Inventory.cs", "HandleActionStackableSplitToContainer",
                "PvpTemplateBlocked(PvpTemplateMoveAction(stackRootOwner == this, containerRootOwner == this, false, true), stack,",
                "CreateMoveToChain(", "CreateNewWorldObject(");
        }

        [TestMethod]
        public void HandleActionStackableSplitTo3D_IsADrop()
        {
            var player = Templated(3);
            var rec = new Recorder(player);
            var stack = Seed<Stackable>(false, 7004, 10);
            Pack(player, stack);

            player.HandleActionStackableSplitTo3D(stack.Guid.Full, 4);

            AssertRefused(rec, PvpTemplateText.EconomyLocked, "HandleActionStackableSplitTo3D");
            Assert.AreEqual(10, stack.StackSize);
            AssertGated("WorldObjects/Player_Inventory.cs", "HandleActionStackableSplitTo3D", "PvpTemplateBlocked(PvpTemplateAction.Drop, stack)", "StartPickupChain(");
        }

        [TestMethod]
        public void HandleActionStackableSplitToWield_PersonalStackWhileTemplated_IsRefused()
        {
            var player = Templated(3);
            var rec = new Recorder(player);
            var stack = Seed<Stackable>(false, 7005, 10);
            Pack(player, stack);

            player.HandleActionStackableSplitToWield(stack.Guid.Full, EquipMask.MissileAmmo, 4);

            AssertRefused(rec, PvpTemplateText.PersonalItemLocked, "HandleActionStackableSplitToWield");
            Assert.AreEqual(10, stack.StackSize);
            AssertGated("WorldObjects/Player_Inventory.cs", "HandleActionStackableSplitToWield", "PvpTemplateBlocked(PvpTemplateAction.Equip, stack)", "CreateMoveToChain(", "CreateNewWorldObject(");
        }

        [TestMethod]
        public void SplitStacks_OfAnIssuedStack_CarryTheIssuedStamps()
        {
            var issued = Seed<Stackable>(true, 7006, 10);
            var personal = Seed<Stackable>(false, 7006, 10);
            var childOfIssued = Seed<Stackable>(false, 7006, 4);
            var childOfPersonal = Seed<Stackable>(false, 7006, 4);
            childOfIssued.SetProperty(PropertyInt.Value, 500);

            Player.StampPvpTemplateSplitStack(issued, childOfIssued);
            Player.StampPvpTemplateSplitStack(personal, childOfPersonal);

            Assert.IsTrue(PvpTemplate.IsMarkedIssued(childOfIssued), "a split-off issued stack is issued, or the restore and the login sweep would never remove it");
            Assert.AreEqual((int)AttunedStatus.Attuned, childOfIssued.GetProperty(PropertyInt.Attuned));
            Assert.AreEqual((int)BondedStatus.Bonded, childOfIssued.GetProperty(PropertyInt.Bonded));
            Assert.AreEqual(0, childOfIssued.GetProperty(PropertyInt.Value));
            Assert.IsFalse(PvpTemplate.IsMarkedIssued(childOfPersonal), "a personal stack's child stays personal");

            // Every split path stamps: container x2, wield x2 (the 3D split drops, which an issued stack cannot do).
            var inventory = Code("WorldObjects/Player_Inventory.cs");
            Assert.AreEqual(4, Regex.Matches(inventory, Regex.Escape("StampPvpTemplateSplitStack(stack, newStack);")).Count, "the four split-to-container / split-to-wield branches each stamp the new stack");
        }

        [TestMethod]
        public void HandleActionGiveObjectRequest_RunsTheGateBeforeTheMoveToChain_AndDecidesPerTarget()
        {
            // The target is found on the landblock, so the handler itself is not reachable here: pin the order and the
            // decision. One gate covers a player and an NPC, ahead of GiveObjectToNPC's station and emote intercepts.
            AssertGated("WorldObjects/Player_Inventory.cs", "HandleActionGiveObjectRequest",
                "PvpTemplateBlocked(target is Player ? PvpTemplateAction.GiveToPlayer : PvpTemplateAction.GiveToNpc, item)",
                "CreateMoveToChain(", "GiveObjectToNPC(", "GiveObjectToPlayer(");

            var player = Templated(3);
            var own = Seed<GenericObject>(false);
            var issued = Seed<GenericObject>(true);
            Pack(player, own);
            Pack(player, issued);

            Assert.AreEqual(PvpTemplateText.EconomyLocked, player.PvpTemplateBlocked(PvpTemplateAction.GiveToNpc, own));
            Assert.AreEqual(PvpTemplateText.IssuedItemLocked, player.PvpTemplateBlocked(PvpTemplateAction.GiveToNpc, issued));

            var plain = Plain();
            var plainIssued = Seed<GenericObject>(true);
            Pack(plain, plainIssued);
            Assert.AreEqual(PvpTemplateText.IssuedItemLocked, plain.PvpTemplateBlocked(PvpTemplateAction.GiveToNpc, plainIssued), "an issued item is never given, templated or not");
            Assert.IsNull(plain.PvpTemplateBlocked(PvpTemplateAction.GiveToNpc, Seed<GenericObject>(false)), "an ordinary give is untouched");
        }

        [TestMethod]
        public void GiveObjectToPlayer_ATemplatedReceiverTakesNothing()
        {
            var giver = Plain();
            var rec = new Recorder(giver);
            var receiver = Templated(3);
            var gift = Seed<GenericObject>(false);
            Pack(giver, gift);

            var method = typeof(Player).GetMethod("GiveObjectToPlayer", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(method, "Player.GiveObjectToPlayer was renamed");

            method.Invoke(giver, new object[] { receiver, gift, null, giver, false, 1 });

            AssertRefused(rec, PvpTemplateText.EconomyLocked, "GiveObjectToPlayer");
            Assert.IsTrue(giver.Inventory.ContainsKey(gift.Guid), "the gift stays with the giver");
            Assert.IsTrue(rec.Events(GameEventType.InventoryServerSaveFailed) >= 1);
        }

        [TestMethod]
        public void TryCreateForGive_ATemplatedPlayerIsHandedNothing()
        {
            var player = Templated(3);
            var rec = new Recorder(player);
            var giver = Seed<GenericObject>(false);
            var item = Seed<GenericObject>(false);

            Assert.IsFalse(player.TryCreateForGive(giver, item));

            AssertRefused(rec, PvpTemplateText.EconomyLocked, "TryCreateForGive");
            Assert.AreEqual(0, player.Inventory.Count);
        }

        // ======================================================================================
        // use: Player_Use.cs
        // ======================================================================================

        [TestMethod]
        public void HandleActionUseItem_PersonalItemWhileTemplated_IsRefusedAndUseDoneSent()
        {
            var player = Templated(3);
            var rec = new Recorder(player);
            var potion = Seed<GenericObject>(false);
            Pack(player, potion);

            player.HandleActionUseItem(potion.Guid.Full);

            AssertRefused(rec, PvpTemplateText.PersonalItemLocked, "HandleActionUseItem");
            Assert.IsTrue(rec.Events(GameEventType.UseDone) >= 1, "UseDone releases the client");
            AssertGated("WorldObjects/Player_Use.cs", "HandleActionUseItem", "PvpTemplateBlocked(PvpTemplateAction.Use, item)", "CreateMoveToChain(", "TryUseItem(");
        }

        [TestMethod]
        public void HandleActionUseItem_IssuedItemOutsideAMatch_IsRefused()
        {
            var player = Plain();
            var rec = new Recorder(player);
            var issued = Seed<GenericObject>(true);
            Pack(player, issued);

            player.HandleActionUseItem(issued.Guid.Full);

            AssertRefused(rec, PvpTemplateText.IssuedItemOutsideMatch, "HandleActionUseItem (not templated)");
        }

        [TestMethod]
        public void HandleActionUseWithTarget_PersonalTargetOrSourceWhileTemplated_IsRefused()
        {
            var player = Templated(3);
            var rec = new Recorder(player);
            var issuedKit = Seed<GenericObject>(true);
            var ownTarget = Seed<GenericObject>(false);
            Pack(player, issuedKit);
            Pack(player, ownTarget);

            player.HandleActionUseWithTarget(issuedKit.Guid.Full, ownTarget.Guid.Full);

            AssertRefused(rec, PvpTemplateText.PersonalItemLocked, "HandleActionUseWithTarget");
            Assert.IsTrue(rec.Events(GameEventType.UseDone) >= 1);
            AssertGated("WorldObjects/Player_Use.cs", "HandleActionUseWithTarget", "PvpTemplateBlocked(PvpTemplateAction.UseWithTarget, sourceItem, target)",
                "HandleActionCastTargetedSpell(", "TinkerLock.IsRefused(", "CreateMoveToChain(", "HandleActionUseOnTarget(");
        }

        [TestMethod]
        public void RecipeManagerUseObjectOnTarget_PersonalToolWhileTemplated_IsRefused()
        {
            var player = Templated(3);
            var rec = new Recorder(player);
            var tool = Seed<GenericObject>(false);
            var target = Seed<GenericObject>(false);
            Pack(player, tool);
            Pack(player, target);

            RecipeManager.UseObjectOnTarget(player, tool, target);

            AssertRefused(rec, PvpTemplateText.PersonalItemLocked, "RecipeManager.UseObjectOnTarget");
            Assert.IsTrue(rec.Events(GameEventType.UseDone) >= 1);
        }

        // ======================================================================================
        // crafting and commerce
        // ======================================================================================

        [TestMethod]
        public void HandleSalvaging_RefusedWholesaleWhileTemplated()
        {
            var player = Templated(3);
            var rec = new Recorder(player);

            player.HandleSalvaging(0x1, new List<uint>());

            AssertRefused(rec, PvpTemplateText.EconomyLocked, "HandleSalvaging");
            AssertGated("WorldObjects/Player_Crafting.cs", "HandleSalvaging", "PvpTemplateBlocked(PvpTemplateAction.Salvage)", "ToolIsValidUst(", "TryConsumeFromInventoryWithNetworking(");
        }

        [TestMethod]
        public void HandleSalvaging_AnIssuedItemIsRefusedOneByOne_EvenWhenNotTemplated()
        {
            var player = Plain();
            var rec = new Recorder(player);
            var squelches = (SquelchManager)RuntimeHelpers.GetUninitializedObject(typeof(SquelchManager));
            squelches.Squelches = new ACE.Server.Network.Structure.SquelchDB(new List<ACE.Database.Models.Shard.CharacterPropertiesSquelch>(), SquelchMask.None);
            Recorder.SetInstanceFieldWithoutClassInit(typeof(Player), player, "SquelchManager", squelches);
            var tool = Seed<GenericObject>(false, (uint)ACE.Entity.Enum.WeenieClassName.W_TINKERINGTOOL_CLASS);
            var issued = Seed<GenericObject>(true);
            Pack(player, tool);
            Pack(player, issued);

            player.HandleSalvaging(tool.Guid.Full, new List<uint> { issued.Guid.Full });

            AssertRefused(rec, PvpTemplateText.IssuedItemLocked, "HandleSalvaging (per item)");
            Assert.IsTrue(player.Inventory.ContainsKey(issued.Guid), "the issued item was not consumed");
            AssertGated("WorldObjects/Player_Crafting.cs", "HandleSalvaging", "PvpTemplateBlocked(PvpTemplateAction.Salvage, item)", "item.MaterialType == null", "AddSalvage(");
        }

        [TestMethod]
        public void HandleActionBuyItem_RefusedWhileTemplated_BeforeTheVendorIsLookedUp()
        {
            var player = Templated(3);
            var rec = new Recorder(player);

            player.HandleActionBuyItem(0x7000, new List<ItemProfile>());

            AssertRefused(rec, PvpTemplateText.EconomyLocked, "HandleActionBuyItem");
            Assert.IsTrue(rec.Events(GameEventType.InventoryServerSaveFailed) >= 1);
            Assert.IsTrue(rec.Events(GameEventType.UseDone) >= 1);
            AssertGated("WorldObjects/Player_Commerce.cs", "HandleActionBuyItem", "PvpTemplateBlocked(PvpTemplateAction.VendorBuy)", "CurrentLandblock?.GetObject(", "BuyItems_ValidateTransaction(");
        }

        [TestMethod]
        public void HandleActionSellItem_RefusedWhileTemplated_BeforeTheVendorIsLookedUp()
        {
            var player = Templated(3);
            var rec = new Recorder(player);

            player.HandleActionSellItem(0x7000, new List<ItemProfile>());

            AssertRefused(rec, PvpTemplateText.EconomyLocked, "HandleActionSellItem");
            Assert.IsTrue(rec.Events(GameEventType.InventoryServerSaveFailed) >= 1);
            AssertGated("WorldObjects/Player_Commerce.cs", "HandleActionSellItemCore", "PvpTemplateBlocked(PvpTemplateAction.VendorSell)", "CurrentLandblock?.GetObject(", "VerifySellItems(");
        }

        [TestMethod]
        public void VerifySellItems_AnIssuedItemIsNeverSoldOrDeposited()
        {
            AssertGated("WorldObjects/Player_Commerce.cs", "VerifySellItems",
                "PvpTemplateBlocked(isVault ? PvpTemplateAction.Vault : PvpTemplateAction.VendorSell, wo)", "IsAcceptableToSell(", "CanAccept(");

            var plain = Plain();
            var issued = Seed<GenericObject>(true);
            Pack(plain, issued);
            Assert.AreEqual(PvpTemplateText.IssuedItemLocked, plain.PvpTemplateBlocked(PvpTemplateAction.VendorSell, issued));
            Assert.AreEqual(PvpTemplateText.IssuedItemLocked, plain.PvpTemplateBlocked(PvpTemplateAction.Vault, issued));
        }

        // ======================================================================================
        // progression lock
        // ======================================================================================

        [TestMethod]
        public void GrantXP_ATemplatedPlayerEarnsNothing()
        {
            var player = Templated(3);
            new Recorder(player);
            player.SetProperty(PropertyInt64.AvailableExperience, 100);
            player.SetProperty(PropertyInt64.TotalExperience, 100);

            player.GrantXP(500, XpType.Kill, ShareType.None, false);

            Assert.AreEqual(100L, player.GetProperty(PropertyInt64.AvailableExperience));
            Assert.AreEqual(100L, player.GetProperty(PropertyInt64.TotalExperience));
            AssertGated("WorldObjects/Player_Xp.cs", "GrantXP|bool combatShare", "PvpTemplateBlocked(PvpTemplateAction.Experience)", "IsOlthoiPlayer", "UpdateXpAndLevel(");
        }

        [TestMethod]
        public void GrantLuminance_ATemplatedPlayerEarnsNothing()
        {
            var player = Templated(3);
            new Recorder(player);
            player.SetProperty(PropertyInt64.AvailableLuminance, 100);

            player.GrantLuminance(500, XpType.Kill, ShareType.None, false);

            Assert.AreEqual(100L, player.GetProperty(PropertyInt64.AvailableLuminance));
            AssertGated("WorldObjects/Player_Luminance.cs", "GrantLuminance|bool combatShare", "PvpTemplateBlocked(PvpTemplateAction.Luminance)", "IsOlthoiPlayer", "Fellowship.SplitLuminance(");
        }

        [TestMethod]
        public void QuestManagerUpdateAndSetQuestCompletions_ATemplatedPlayerMakesNoQuestProgress()
        {
            var player = Templated(3);
            var quests = new QuestManager(player);

            quests.Update("PvpTemplateGateTestQuest");
            quests.SetQuestCompletions("PvpTemplateGateTestQuest", 3);

            AssertGated("Managers/QuestManager.cs", "Update", "templateCheck.PvpTemplateBlocked(PvpTemplateAction.QuestStamp)", "GetQuestName(", "GetOrCreateQuest(");
            AssertGated("Managers/QuestManager.cs", "SetQuestCompletions", "templateCheck.PvpTemplateBlocked(PvpTemplateAction.QuestStamp)", "GetQuestName(", "GetOrCreateQuest(");
        }

        [TestMethod]
        public void TryClaimPickupBoon_ATemplatedPlayerIsRefusedCleanly()
        {
            // The method opens with IsValidPickupBoonKey, which reads a static Player field (and so runs Player's
            // World-database static constructor), so it cannot be called here: pin the order and the decision.
            AssertGated("WorldObjects/Player_PickupBoons.cs", "TryClaimPickupBoon", "PvpTemplateBlocked(PvpTemplateAction.QuestStamp)", "QuestManager.HasQuest(", "QuestManager.Update(", "SaveCharacterToDatabase(");

            Assert.AreEqual(PvpTemplateText.ProgressionLocked, Templated(3).PvpTemplateBlocked(PvpTemplateAction.QuestStamp));
            Assert.IsNull(Plain().PvpTemplateBlocked(PvpTemplateAction.QuestStamp));
        }

        [TestMethod]
        public void TryBeginOneTimeItemGrant_ATemplatedPlayerIsRefused()
        {
            var player = Templated(3);

            Assert.IsFalse(player.TryBeginOneTimeItemGrant("PvpTemplateGateTestGrant", "Thing", out var error));
            Assert.AreEqual(PvpTemplateText.ProgressionLocked, error);
        }

        [TestMethod]
        public void TryBeginOneTimeClassAbilityGrant_ATemplatedPlayerIsRefused()
        {
            var player = Templated(3);

            Assert.IsFalse(player.TryBeginOneTimeClassAbilityGrant("PvpTemplateGateTestGrant", "Thing", out var error));
            Assert.AreEqual(PvpTemplateText.ProgressionLocked, error);
        }

        [TestMethod]
        public void GrantClassAbilityPoints_ATemplatedPlayerEarnsNone()
        {
            var player = Templated(3);
            new Recorder(player);
            var before = player.GetProperty(PropertyInt.AvailableClassAbilityPoints);

            Assert.IsFalse(player.GrantClassAbilityPoints(5, "a test", CapLedgerReason.GrantEnlightenment));
            Assert.AreEqual(before, player.GetProperty(PropertyInt.AvailableClassAbilityPoints));
            AssertGated("WorldObjects/Player_ClassAbilities.cs", "GrantClassAbilityPoints", "PvpTemplateBlocked(PvpTemplateAction.ClassAbilityPoints)", "AdjustClassAbilityPoints(");
        }

        [TestMethod]
        public void ValidateClassAbilityRankPrerequisites_ATemplatedPlayerBuysNoRank()
        {
            var player = Templated(3);
            var method = typeof(Player).GetMethod("ValidateClassAbilityRankPrerequisites", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(method, "Player.ValidateClassAbilityRankPrerequisites was renamed");

            var args = new object[] { null, 0, null };
            var ok = (bool)method.Invoke(player, args);

            Assert.IsFalse(ok);
            Assert.AreEqual(PvpTemplateText.ProgressionLocked, args[2]);
        }

        [TestMethod]
        public void CanBuyClassAbilityToken_ATemplatedPlayerIsRefusedBeforeAnyCurrencyMoves()
        {
            var player = Templated(3);

            Assert.IsFalse(player.CanBuyClassAbilityToken(null, 1, out var error));
            Assert.AreEqual(PvpTemplateText.ProgressionLocked, error);
        }

        [TestMethod]
        public void AddTitle_ATemplatedPlayerEarnsNone()
        {
            var player = Templated(3);
            new Recorder(player);

            player.AddTitle(1); // would reach the Character registry (null here) without the gate

            AssertGated("WorldObjects/Player_Character.cs", "AddTitle", "PvpTemplateBlocked(PvpTemplateAction.Title)", "AddTitleToRegistry(");
        }

        [TestMethod]
        public void HandleActionBuyHouse_RefusedWhileTemplated()
        {
            // The handler's first line logs through Player's static logger (which runs Player's World-database static
            // constructor), so it cannot be called here: pin the order and the decision.
            AssertGated("WorldObjects/Player_House.cs", "HandleActionBuyHouse", "PvpTemplateBlocked(PvpTemplateAction.House)", "GetHouseInstance(");

            Assert.AreEqual(PvpTemplateText.ProgressionLocked, Templated(3).PvpTemplateBlocked(PvpTemplateAction.House));
            Assert.IsNull(Plain().PvpTemplateBlocked(PvpTemplateAction.House));
        }

        [TestMethod]
        public void ContractManagerAdd_RefusedWhileTemplated()
        {
            var player = Templated(3);
            var rec = new Recorder(player);
            var contracts = (ContractManager)RuntimeHelpers.GetUninitializedObject(typeof(ContractManager));
            PvpTemplatePlayerTests.SetField(typeof(ContractManager), contracts, "<Player>k__BackingField", player);

            Assert.IsFalse(contracts.Add(1u));

            AssertRefused(rec, PvpTemplateText.ProgressionLocked, "ContractManager.Add");
            AssertGated("WorldObjects/Managers/ContractManager.cs", "Add|uint contractId", "Player.PvpTemplateBlocked(PvpTemplateAction.Contract)", "GetContractFromDat(");
        }

        [TestMethod]
        public void StartDpsChallenge_RefusedWhileTemplated()
        {
            var player = Templated(3);
            var rec = new Recorder(player);

            player.StartDpsChallenge(30);

            AssertRefused(rec, PvpTemplateText.ProgressionLocked, "StartDpsChallenge");
        }

        [TestMethod]
        public void StartSpeedChallenge_RefusedWhileTemplated()
        {
            var player = Templated(3);
            var rec = new Recorder(player);

            player.StartSpeedChallenge(null);

            AssertRefused(rec, PvpTemplateText.ProgressionLocked, "StartSpeedChallenge");
        }

        [TestMethod]
        public void StartSurvivalChallenge_RefusedWhileTemplated()
        {
            var player = Templated(3);
            var rec = new Recorder(player);

            player.StartSurvivalChallenge(30, 1.1, 5);

            AssertRefused(rec, PvpTemplateText.ProgressionLocked, "StartSurvivalChallenge");
        }

        [TestMethod]
        public void StartWaveChallenge_RefusedWhileTemplated()
        {
            var player = Templated(3);
            var rec = new Recorder(player);

            player.StartWaveChallenge(3, 1, 5, 30, 60);

            AssertRefused(rec, PvpTemplateText.ProgressionLocked, "StartWaveChallenge");
        }

        [TestMethod]
        public void HandleActionRaiseSkill_RefusedWhileTemplated()
        {
            var player = Templated(3);
            var rec = new Recorder(player);

            Assert.IsFalse(player.HandleActionRaiseSkill(Skill.Sword, 10));

            AssertRefused(rec, PvpTemplateText.ProgressionLocked, "HandleActionRaiseSkill");
        }

        [TestMethod]
        public void HandleActionTrainSkill_RefusedWhileTemplated()
        {
            var player = Templated(3);
            var rec = new Recorder(player);

            Assert.IsFalse(player.HandleActionTrainSkill(Skill.Sword, 6));

            AssertRefused(rec, PvpTemplateText.ProgressionLocked, "HandleActionTrainSkill");
        }

        [TestMethod]
        public void HandleActionRaiseAttribute_RefusedWhileTemplated()
        {
            var player = Templated(3);
            var rec = new Recorder(player);

            Assert.IsFalse(player.HandleActionRaiseAttribute(PropertyAttribute.Strength, 10));

            AssertRefused(rec, PvpTemplateText.ProgressionLocked, "HandleActionRaiseAttribute");
        }

        [TestMethod]
        public void HandleActionRaiseVital_RefusedWhileTemplated()
        {
            var player = Templated(3);
            var rec = new Recorder(player);

            Assert.IsFalse(player.HandleActionRaiseVital(PropertyAttribute2nd.MaxHealth, 10));

            AssertRefused(rec, PvpTemplateText.ProgressionLocked, "HandleActionRaiseVital");
        }

        [TestMethod]
        public void CheckFacetGates_ASwitchWhileTemplatedIsRefused()
        {
            var player = Templated(3);

            Assert.IsFalse(player.CheckFacetGates(2, out var refusal));
            Assert.AreEqual(PvpTemplateText.FacetSwitchLocked, refusal);
            AssertGated("WorldObjects/Player_Facets.cs", "CheckFacetGates", "PvpTemplateBlocked(PvpTemplateAction.FacetSwitch)", "FacetTunables.DialSource(");
        }

        // ======================================================================================
        // masks: casting, components, Enlightenment, heritage
        // ======================================================================================

        [TestMethod]
        public void HandleActionCastTargetedSpell_ANonTemplateSpellIsRefused()
        {
            var player = Templated(3);
            var rec = new Recorder(player);

            player.HandleActionCastTargetedSpell(0x7000, 9);

            AssertRefused(rec, PvpTemplateText.NonTemplateSpell, "HandleActionCastTargetedSpell");
            Assert.IsTrue(rec.Events(GameEventType.UseDone) >= 1);
            AssertGated("WorldObjects/Player_Magic.cs", "HandleActionCastTargetedSpell", "PvpTemplateCastBlocked(spellId, casterItem)", "CombatMode != CombatMode.Magic", "MagicState.CastQueue");
        }

        [TestMethod]
        public void HandleActionCastTargetedSpell_AnIssuedItemsBuiltInSpellNeedsNoSpellbookEntry()
        {
            var player = Templated(3);
            var rec = new Recorder(player);
            var wand = Seed<GenericObject>(true);
            Wear(player, wand);

            try { player.HandleActionCastTargetedSpell(0x7000, 9, wand); }
            catch (Exception) { /* what happens after the gate is outside this test */ }

            var chat = rec.Chat();
            Assert.IsFalse(chat.Contains(PvpTemplateText.NonTemplateSpell) || chat.Contains(PvpTemplateText.PersonalItemLocked),
                $"an issued item's spell must pass the cast gate; the player was told: [{string.Join(" | ", chat)}]");

            Assert.IsNull(player.PvpTemplateCastBlocked(9, wand));
            Assert.AreEqual(PvpTemplateText.NonTemplateSpell, player.PvpTemplateCastBlocked(9), "no source item: the spellbook still decides");
            Assert.IsNull(player.PvpTemplateCastBlocked(3), "a spellbook spell still passes");
        }

        [TestMethod]
        public void HandleActionCastTargetedSpell_APersonalItemsBuiltInSpellIsRefused_EvenIfTheSpellIsInTheSpellbook()
        {
            var player = Templated(3);
            var rec = new Recorder(player);
            var wand = Seed<GenericObject>(false);
            Wear(player, wand);

            player.HandleActionCastTargetedSpell(0x7000, 9, wand);

            AssertRefused(rec, PvpTemplateText.PersonalItemLocked, "HandleActionCastTargetedSpell (personal item)");
            Assert.IsTrue(rec.Events(GameEventType.UseDone) >= 1);
            Assert.AreEqual(PvpTemplateText.PersonalItemLocked, player.PvpTemplateCastBlocked(3, wand), "a spellbook id does not launder a personal item");
        }

        [TestMethod]
        public void HandleActionMagicCastUnTargetedSpell_ANonTemplateSpellIsRefused()
        {
            var player = Templated(3);
            var rec = new Recorder(player);

            player.HandleActionMagicCastUnTargetedSpell(9);

            AssertRefused(rec, PvpTemplateText.NonTemplateSpell, "HandleActionMagicCastUnTargetedSpell");
            Assert.IsTrue(rec.Events(GameEventType.UseDone) >= 1);
            AssertGated("WorldObjects/Player_Magic.cs", "HandleActionMagicCastUnTargetedSpell", "PvpTemplateCastBlocked(spellId, casterItem)", "CombatMode != CombatMode.Magic", "MagicState.CastQueue");
        }

        [TestMethod]
        public void SpellComponents_OnlyIssuedOnesCountWhileTemplated()
        {
            const uint wcid = 7100;

            var player = Templated(3);
            var own = Seed<Stackable>(false, wcid, 20);
            var issued = Seed<Stackable>(true, wcid, 6);
            Pack(player, own);
            Pack(player, issued);

            Assert.AreEqual(6, player.GetNumSpellComponents(wcid), "templated: only the issued stack counts");
            CollectionAssert.AreEqual(new WorldObject[] { issued }, player.GetSpellComponentItems(wcid), "templated: only the issued stack burns");

            var plain = Plain();
            var plainOwn = Seed<Stackable>(false, wcid, 20);
            var plainIssued = Seed<Stackable>(true, wcid, 6);
            Pack(plain, plainOwn);
            Pack(plain, plainIssued);

            Assert.AreEqual(20, plain.GetNumSpellComponents(wcid), "not templated: an issued component counts for nothing");
        }

        [TestMethod]
        public void PlayerMagic_ComponentCountAndBurnGoThroughTheGate()
        {
            var has = Body("WorldObjects/Player_Magic.cs", "HasComponentsForSpell");
            Assert.IsTrue(has.Contains("GetNumSpellComponents("), "HasComponentsForSpell must count through the gate");
            Assert.IsFalse(has.Contains("GetNumInventoryItemsOfWCID("), "HasComponentsForSpell must not count the whole pack");

            var burn = Body("WorldObjects/Player_Magic.cs", "TryBurnComponents");
            Assert.IsTrue(burn.Contains("GetSpellComponentItems("), "TryBurnComponents must burn through the gate");
            Assert.IsFalse(burn.Contains("GetInventoryItemsOfWCID("), "TryBurnComponents must not burn from the whole pack");

            var gates = Code("WorldObjects/Player_PvpTemplateGates.cs");
            Assert.IsTrue(gates.Contains("PvpTemplateBlocked(PvpTemplateAction.SpellComponent, i) == null"), "the component filter asks the SpellComponent gate for each item");
        }

        [TestMethod]
        public void EffectiveEnlightenment_IsMaskedWhileTemplated_AndNeverWritten()
        {
            var plain = Plain();
            plain.SetProperty(PropertyInt.Enlightenment, 7);
            Assert.AreEqual(7, plain.EffectiveEnlightenment);

            var templated = Templated(3);
            templated.SetProperty(PropertyInt.Enlightenment, 7);

            Assert.AreEqual(0, templated.EffectiveEnlightenment, "templated: the build is the template's");
            Assert.AreEqual(7, templated.Enlightenment, "the stored value is never written (the alt-character bonus reads it)");
            Assert.AreEqual(7, templated.GetProperty(PropertyInt.Enlightenment));

            templated.RemoveProperty(PropertyString.PvpTemplateRestore);
            Assert.AreEqual(7, templated.EffectiveEnlightenment, "back to the real value the moment the record is gone");
        }

        [TestMethod]
        public void EnlightenmentReadSites_AllUseTheMask()
        {
            var attribute = Code("WorldObjects/Entity/CreatureAttribute.cs");
            Assert.AreEqual(2, Regex.Matches(attribute, @"\.EffectiveEnlightenment\b").Count, "CreatureAttribute reads Enlightenment at two sites (NetworkStartingValue and Base)");
            Assert.IsFalse(Regex.IsMatch(attribute, @"\.Enlightenment\b"), "CreatureAttribute must not read the raw Enlightenment");

            var skill = Code("WorldObjects/Entity/CreatureSkill.cs");
            Assert.AreEqual(2, Regex.Matches(skill, @"\.EffectiveEnlightenment\b").Count, "CreatureSkill reads Enlightenment at one site (two references)");
            Assert.IsFalse(Regex.IsMatch(skill, @"\.Enlightenment\b"), "CreatureSkill must not read the raw Enlightenment");
        }

        [TestMethod]
        public void HeritageBonus_IsOffWhileTemplated_AndFollowsTheSetting()
        {
            var plain = Plain();
            plain.SetProperty(PropertyInt.HeritageGroup, (int)HeritageGroup.Viamontian);
            Assert.IsTrue(plain.GetHeritageBonus(WeaponType.Sword), "sanity: a Viamontian gets the sword bonus");

            var templated = Templated(3);
            templated.SetProperty(PropertyInt.HeritageGroup, (int)HeritageGroup.Viamontian);

            PvpTemplateSettings.SuppressHeritageBonusSource = () => true;
            Assert.IsFalse(templated.GetHeritageBonus(WeaponType.Sword), "templated with the setting on: no bonus");
            Assert.IsTrue(templated.HeritageBonusSuppressed);

            PvpTemplateSettings.SuppressHeritageBonusSource = () => false;
            Assert.IsTrue(templated.GetHeritageBonus(WeaponType.Sword), "templated with the setting off: the bonus is back");
        }

        [TestMethod]
        public void HeritageBonus_EveryReadSiteGoesThroughThePlayerGetter()
        {
            var getter = Body("WorldObjects/Player_Skills.cs", "GetHeritageBonus", "WorldObject weapon");
            Assert.IsTrue(getter.Contains("HeritageBonusSuppressed"), "the WorldObject overload checks the mask");

            var byType = Body("WorldObjects/Player_Skills.cs", "GetHeritageBonus", "WeaponType weaponType");
            Assert.IsTrue(byType.Contains("HeritageBonusSuppressed"), "the WeaponType overload checks the mask");

            foreach (var file in new[] { "Entity/DamageEvent.cs", "WorldObjects/SpellProjectile.cs", "WorldObjects/WorldObject_Magic.cs", "Network/Structure/AppraiseInfo.cs" })
            {
                var code = Code(file);
                Assert.IsTrue(code.Contains("GetHeritageBonus("), $"{file} reads the bonus through GetHeritageBonus");
                Assert.IsFalse(Regex.IsMatch(code, @"HeritageGroup\s*(==|switch)"), $"{file} must not compute a heritage bonus of its own");
            }
        }

        // ======================================================================================
        // review round: confirmations, trade, rent, economy commands, self-grants, salvage release, cast queue
        // ======================================================================================

        private sealed class RecordingConfirmation : Confirmation
        {
            public int Calls;

            public RecordingConfirmation(ObjectGuid player, ConfirmationType type) : base(player, type) { ContextId = 7; }

            public override void ProcessConfirmation(bool response, bool timeout = false) => Calls++;
        }

        private static ConfirmationManager WithConfirmations(Player player, params Confirmation[] pending)
        {
            var manager = new ConfirmationManager(player);
            Recorder.SetInstanceFieldWithoutClassInit(typeof(Player), player, "ConfirmationManager", manager);

            var dictionary = (System.Collections.Concurrent.ConcurrentDictionary<ConfirmationType, Confirmation>)typeof(ConfirmationManager)
                .GetField("confirmations", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(manager);

            foreach (var confirmation in pending)
                dictionary[confirmation.ConfirmationType] = confirmation;

            return manager;
        }

        [TestMethod]
        public void ConfirmationYes_WhileTemplated_NeverRunsTheAction()
        {
            var player = Templated(3);
            var rec = new Recorder(player);
            var skill = new RecordingConfirmation(player.Guid, ConfirmationType.AlterSkill);
            var aug = new RecordingConfirmation(player.Guid, ConfirmationType.Augmentation);
            var manager = WithConfirmations(player, skill, aug);
            var xp = player.AvailableExperience;

            Assert.IsFalse(manager.HandleResponse(ConfirmationType.AlterSkill, 7, true));
            Assert.IsFalse(manager.HandleResponse(ConfirmationType.Augmentation, 7, true));

            Assert.AreEqual(0, skill.Calls, "a Yes on a pre-opened Gem of Forgetfulness dialog ran while templated");
            Assert.AreEqual(0, aug.Calls, "a Yes on a pre-opened augmentation dialog ran while templated");
            Assert.AreEqual(xp, player.AvailableExperience);
            AssertRefused(rec, PvpTemplateText.PersonalItemLocked, "ConfirmationManager.HandleResponse");
            AssertGated("WorldObjects/Managers/ConfirmationManager.cs", "HandleResponse", "PvpTemplateBlocked(PvpTemplateAction.Confirmation)", "confirm.ProcessConfirmation(");
        }

        [TestMethod]
        public void ConfirmationYes_OnTheArenaLeaveDialog_WhileTemplated_ForfeitsInsteadOfRefusing()
        {
            // Owner report 2026-10-04: /arena leave in a templated match, Yes on the "counts as a loss" popup, and the
            // player got PersonalItemLocked and stayed in the match. The leave dialog is the arena's own exit, so the
            // templated Confirmation gate must let its Yes through to the forfeit hop.
            var player = Templated(3);
            var rec = new Recorder(player);
            var leave = new ACE.Server.Pvp.Confirmation_PvpArenaLeave(player.Guid, Guid.NewGuid()) { ContextId = 7 };
            var manager = WithConfirmations(player, leave);

            var queue = (System.Collections.Concurrent.ConcurrentQueue<ACE.Server.Entity.Actions.IAction>)typeof(ACE.Server.Entity.Actions.ActionQueue)
                .GetProperty("Queue", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(WorldManager.ActionQueue);
            var before = queue.ToArray();

            try
            {
                Assert.IsTrue(manager.HandleResponse(ConfirmationType.Yes_No, 7, true), "a Yes on the leave dialog was refused while templated");

                var chat = rec.Chat();
                Assert.IsFalse(chat.Contains(PvpTemplateText.PersonalItemLocked), $"the leave dialog was refused: [{string.Join(" | ", chat)}]");
                Assert.AreEqual(before.Length + 1, queue.Count, "the Yes must reach Confirmation_PvpArenaLeave, which queues the forfeit on the world queue");
            }
            finally
            {
                // Put the world queue back exactly as it was: the queued forfeit must not leak into another test.
                while (queue.TryDequeue(out _)) { }
                foreach (var action in before)
                    queue.Enqueue(action);
            }

            // The gate is not loosened for anything else, including another Yes_No dialog (Confirmation_Custom for gems
            // and the market, Confirmation_YesNo for emotes share the leave dialog's type): its Yes is still refused.
            var other = Templated(3);
            var otherRec = new Recorder(other);
            var yesNo = new RecordingConfirmation(other.Guid, ConfirmationType.Yes_No);
            Assert.IsFalse(WithConfirmations(other, yesNo).HandleResponse(ConfirmationType.Yes_No, 7, true), "a Yes_No dialog other than the leave dialog ran while templated");
            Assert.AreEqual(0, yesNo.Calls);
            AssertRefused(otherRec, PvpTemplateText.PersonalItemLocked, "ConfirmationManager.HandleResponse");
        }

        [TestMethod]
        public void PermittedWhilePvpTemplated_IsTrueOnlyForTheArenaLeaveDialog()
        {
            // Pin: the exemption belongs to Confirmation_PvpArenaLeave alone. Every Confirmation subclass in the server
            // is checked under every ConfirmationType, so an exemption keyed on the dialog TYPE (Yes_No is shared by
            // Confirmation_Custom and Confirmation_YesNo) fails here, as does a second override.
            var leave = typeof(ACE.Server.Pvp.Confirmation_PvpArenaLeave);
            var subclasses = typeof(Confirmation).Assembly.GetTypes()
                .Where(t => typeof(Confirmation).IsAssignableFrom(t) && !t.IsAbstract).ToList();

            Assert.IsTrue(subclasses.Contains(leave), "the scan must reach the leave dialog");
            Assert.IsTrue(subclasses.Count > 5, $"the scan found only {subclasses.Count} Confirmation subclasses");

            foreach (var type in subclasses)
            {
                var declaring = type.GetProperty(nameof(Confirmation.PermittedWhilePvpTemplated)).GetMethod.DeclaringType;
                Assert.AreEqual(type == leave ? leave : typeof(Confirmation), declaring, $"{type.Name} must not override PermittedWhilePvpTemplated");

                var instance = (Confirmation)RuntimeHelpers.GetUninitializedObject(type);

                foreach (ConfirmationType kind in Enum.GetValues(typeof(ConfirmationType)))
                {
                    instance.ConfirmationType = kind;
                    Assert.AreEqual(type == leave, instance.PermittedWhilePvpTemplated, $"{type.Name} as {kind}");
                }
            }
        }

        [TestMethod]
        public void ConfirmationYes_WhenNotTemplated_StillRuns_AndANoStillPassesWhileTemplated()
        {
            var plain = Plain();
            new Recorder(plain);
            var yes = new RecordingConfirmation(plain.Guid, ConfirmationType.AlterSkill);
            Assert.IsTrue(WithConfirmations(plain, yes).HandleResponse(ConfirmationType.AlterSkill, 7, true));
            Assert.AreEqual(1, yes.Calls, "positive control: an untemplated Yes must run");

            var templated = Templated(3);
            new Recorder(templated);
            var no = new RecordingConfirmation(templated.Guid, ConfirmationType.AlterSkill);
            Assert.IsTrue(WithConfirmations(templated, no).HandleResponse(ConfirmationType.AlterSkill, 7, false));
            Assert.AreEqual(1, no.Calls, "a No (or timeout) must still reach the dialog so it closes");
        }

        [TestMethod]
        public void ConfirmationAbortAll_ClosesEveryPendingDialog_AndTheApplyCallsIt()
        {
            var player = Plain();
            var rec = new Recorder(player);
            var skill = new RecordingConfirmation(player.Guid, ConfirmationType.AlterSkill);
            var aug = new RecordingConfirmation(player.Guid, ConfirmationType.Augmentation);
            var manager = WithConfirmations(player, skill, aug);

            manager.AbortAll();

            Assert.AreEqual(2, rec.Events(GameEventType.CharacterConfirmationDone), "every pending dialog is closed on the client");
            var pending = (System.Collections.Concurrent.ConcurrentDictionary<ConfirmationType, Confirmation>)typeof(ConfirmationManager).GetField("confirmations", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(manager);
            Assert.AreEqual(0, pending.Count, "an aborted dialog cannot be answered: nothing is left pending");
            Assert.AreEqual(0, skill.Calls);
            Assert.AreEqual(0, aug.Calls);

            AssertGated("WorldObjects/Player_PvpTemplate.cs", "ApplyPvpTemplateNow", "ConfirmationManager?.AbortAll()", "PvpTemplateOverlay.Capture(", "BeginPvpTemplateSystemOperation()");
        }

        [TestMethod]
        public void TradeHandlers_AreRefusedWhileTemplated()
        {
            var player = Templated(3);
            var rec = new Recorder(player);

            player.HandleActionOpenTradeNegotiations(0x50000001, true);
            player.HandleActionAddToTrade(0x80000001, 0);
            player.HandleActionAcceptTrade();

            Assert.AreEqual(3, rec.Chat().Count(c => c == PvpTemplateText.EconomyLocked), "each trade handler refuses");
            Assert.IsFalse(player.IsTrading);

            AssertGated("WorldObjects/Player_Trade.cs", "HandleActionOpenTradeNegotiations", "PvpTemplateTradeRefused()", "PlayerManager.GetOnlinePlayer(", "IsTrading = true");
            AssertGated("WorldObjects/Player_Trade.cs", "HandleActionAddToTrade", "PvpTemplateTradeRefused()", "TradeAccepted = false");
            AssertGated("WorldObjects/Player_Trade.cs", "HandleActionAcceptTrade", "PvpTemplateTradeRefused()", "TradeAccepted = true");
        }

        [TestMethod]
        public void TradeOpen_ToATemplatedPartner_IsRefusedForTheInitiator()
        {
            AssertGated("WorldObjects/Player_Trade.cs", "HandleActionOpenTradeNegotiations", "tradePartner.PvpTemplateBlocked(PvpTemplateAction.Trade)", "CreateMoveToChain(");
        }

        [TestMethod]
        public void HandleActionRentHouse_RefusedWhileTemplated()
        {
            // The handler logs through Player's static logger, which cannot run in tests: scan plus the decision.
            Assert.AreEqual(PvpTemplateText.ProgressionLocked, PvpTemplateGate.Decide(PvpTemplateAction.House, true, false, PvpTemplateItemKind.None, PvpTemplateItemKind.None));
            AssertGated("WorldObjects/Player_House.cs", "HandleActionRentHouse", "PvpTemplateBlocked(PvpTemplateAction.House)", "log.Info(", "FindObject(");
        }

        [TestMethod]
        public void EconomyCommands_AreRefusedWhileTemplated()
        {
            var player = Templated(3);
            var rec = new Recorder(player);

            ACE.Server.Command.Handlers.PlayerCommands.HandleBank(rec.Session, "d");
            ACE.Server.Command.Handlers.MarketCommands.HandleMarket(rec.Session, "list");
            ACE.Server.Command.Handlers.MuleCommands.HandleMule(rec.Session);

            Assert.AreEqual(3, rec.Chat().Count(c => c == PvpTemplateText.EconomyLocked), "/bank, /market and /mule each refuse");

            AssertGated("Command/Handlers/PlayerCommands.cs", "HandleBank|string[] parameters", "PvpTemplateRefuses(", "LastBankCommandTime");
            AssertGated("Command/Handlers/MarketCommands.cs", "HandleMarket", "PvpTemplateRefuses(", "MarketCommandParser.Parse(");
            AssertGated("Command/Handlers/MuleCommands.cs", "HandleMule", "PvpTemplateRefuses(", "Dispatch(");
        }

        [TestMethod]
        public void StageSelfGrants_AreRefusedWhileTemplated()
        {
            var player = Templated(3);
            var rec = new Recorder(player);
            var xp = player.AvailableExperience;

            ACE.Server.Command.Handlers.StageTestCommands.HandleMyXp(rec.Session, "1000");
            ACE.Server.Command.Handlers.StageTestCommands.HandleMyLuminance(rec.Session, "1000");
            ACE.Server.Command.Handlers.StageTestCommands.HandleMyAugGem(rec.Session);
            ACE.Server.Command.Handlers.StageTestCommands.HandleMyRespec(rec.Session);

            Assert.AreEqual(4, rec.Chat().Count(c => c == PvpTemplateText.ProgressionLocked), "every self-grant entry refuses");
            Assert.AreEqual(xp, player.AvailableExperience);
            AssertGated("Command/Handlers/StageTestCommands.cs", "Available", "PvpTemplateRefuses(", "SelfGrantsEnabled");
        }

        [TestMethod]
        public void HandleSalvaging_TemplateRefusalReleasesTheClient()
        {
            var player = Templated(3);
            var rec = new Recorder(player);

            player.HandleSalvaging(0x1, new List<uint>());

            Assert.IsTrue(rec.Events(GameEventType.UseDone) >= 1, "the refusal must release the client's salvage panel");
            Assert.IsTrue(Body("WorldObjects/Player_Crafting.cs", "HandleSalvaging").Contains("SendUseDoneEvent()"));
        }

        [TestMethod]
        public void UntargetedCastQueue_CarriesTheCasterItem()
        {
            var issued = Seed<Container>(true);
            var player = Templated(3);

            Assert.IsNull(player.PvpTemplateCastBlocked(9, issued), "an issued item's built-in spell passes while templated");
            Assert.AreEqual(PvpTemplateText.NonTemplateSpell, player.PvpTemplateCastBlocked(9, null), "control: the same spell with no item is refused");

            var entry = Body("WorldObjects/Player_Magic.cs", "HandleActionMagicCastUnTargetedSpell");
            Assert.IsTrue(entry.Contains("PvpTemplateCastBlocked(spellId, casterItem)"));
            Assert.IsTrue(entry.Contains("new CastQueue(CastQueueType.Untargeted, 0, spellId, casterItem)"), "queueing must keep the item");
            Assert.IsTrue(Body("WorldObjects/Player_Magic.cs", "HandleCastQueue").Contains("HandleActionMagicCastUnTargetedSpell(MagicState.CastQueue.SpellId, MagicState.CastQueue.CasterItem)"), "the re-entry must pass the item back");
        }

        [TestMethod]
        public void PvpTemplateBlocked_WhenNotTemplated_SkipsThePersonalClassification()
        {
            var body = Body("WorldObjects/Player_PvpTemplate.cs", "PvpTemplateBlocked", "WorldObject target");
            var notTemplated = body.IndexOf("if (!templated)", StringComparison.Ordinal);
            var full = body.IndexOf("ClassifyForPvpTemplate(", StringComparison.Ordinal);

            var cheap = body.IndexOf("ClassifyIssuedOnly(", StringComparison.Ordinal);

            Assert.IsTrue(notTemplated >= 0 && cheap > notTemplated && full > cheap, "the not-templated branch must classify cheaply, before the full classification");

            // behaviour: the always-on issued lock still fires, a plain item passes
            var player = Plain();
            Assert.AreEqual(PvpTemplateText.IssuedItemLocked, player.PvpTemplateBlocked(PvpTemplateAction.Trade, Seed<Container>(true)));
            Assert.IsNull(player.PvpTemplateBlocked(PvpTemplateAction.Trade, Seed<Container>(false)));
        }

        [TestMethod]
        public void DecideCast_HasOneBypassCheck()
        {
            var text = File.ReadAllText(Path.Combine(SourceRoot(), "Pvp", "Templates", "PvpTemplateGate.cs")).Replace("\r\n", "\n");
            var from = text.IndexOf("ICollection<int> templateSpells, PvpTemplateItemKind casterItem)", StringComparison.Ordinal);
            Assert.IsTrue(from >= 0);
            Assert.AreEqual(1, Regex.Matches(text.Substring(from), @"if \(systemBypass \|\| !templated\)").Count, "the duplicated bypass check came back");
        }
        // ======================================================================================
        // the one predicate, everywhere
        // ======================================================================================

        /// <summary>
        /// No gate site reads the record, IsPvpTemplated or the issued mark inline: every site file calls the predicate.
        /// </summary>
        [TestMethod]
        public void NoSiteReadsTheTemplateStateInline()
        {
            var sites = new[]
            {
                "WorldObjects/Player_Inventory.cs", "WorldObjects/Player_Use.cs", "WorldObjects/Player_Crafting.cs", "WorldObjects/Player_Commerce.cs",
                "WorldObjects/Player_Magic.cs", "WorldObjects/Player_Xp.cs", "WorldObjects/Player_Luminance.cs", "Managers/QuestManager.cs",
                "WorldObjects/Player_PickupBoons.cs", "WorldObjects/Player_OneTimeItemGrant.cs", "WorldObjects/Player_ClassAbilityClaim.cs",
                "WorldObjects/Player_ClassAbilities.cs", "WorldObjects/Player_ClassAbilityTokens.cs", "WorldObjects/Player_Character.cs",
                "WorldObjects/Player_House.cs", "WorldObjects/Managers/ContractManager.cs", "WorldObjects/Player_DpsChallenge.cs",
                "WorldObjects/Player_SpeedChallenge.cs", "WorldObjects/Player_SurvivalChallenge.cs", "WorldObjects/Player_WaveChallenge.cs",
                "WorldObjects/Player_Skills.cs", "WorldObjects/Player_Attributes.cs", "WorldObjects/Player_Vitals.cs", "WorldObjects/Player_Facets.cs",
                "Managers/RecipeManager.cs", "WorldObjects/Managers/ConfirmationManager.cs", "WorldObjects/Player_Trade.cs",
            };

            var offenders = new List<string>();

            foreach (var file in sites)
            {
                var code = Code(file);

                foreach (var forbidden in new[] { "IsPvpTemplated", "PvpTemplateIssued", "PvpTemplateRestore", "IsMarkedIssued(", "PvpTemplate.IsIssued(" })
                {
                    if (code.Contains(forbidden))
                        offenders.Add($"{file}: {forbidden}");
                }
            }

            Assert.AreEqual(0, offenders.Count, "A gate site read the template state inline instead of calling PvpTemplateBlocked: " + string.Join("; ", offenders));
        }
    }
}
