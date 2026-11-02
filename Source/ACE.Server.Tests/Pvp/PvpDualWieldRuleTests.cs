using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Managers;
using ACE.Server.Pvp;
using ACE.Server.Pvp.Rules;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// pvp_match_block_dual_wield (Docs/Pvp/TEMPLATES.md "Dual wield"). Three layers: the pure decision on every input combination,
    /// the computed Player predicate on a seeded Player (a binding or a worn kit makes it "in a match"), and source-binding tests for
    /// each call site, because no test here can drive a live equip. The setting is read through
    /// PvpArenaHookSettings.MatchBlockDualWieldSource, seeded in every test that reads it and restored in Cleanup.
    /// </summary>
    [TestClass]
    public class PvpDualWieldRuleTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

        private Func<bool> savedSource;

        [TestInitialize]
        public void Setup() => savedSource = PvpArenaHookSettings.MatchBlockDualWieldSource;

        [TestCleanup]
        public void Cleanup() => PvpArenaHookSettings.MatchBlockDualWieldSource = savedSource;

        // ---------------- the pure decision ----------------

        private static readonly EquipMask[] Slots =
        {
            EquipMask.None,
            EquipMask.Shield,
            EquipMask.MeleeWeapon,
            EquipMask.TwoHanded,
            EquipMask.MissileWeapon,
            EquipMask.Held,
            EquipMask.ChestArmor,
            EquipMask.Shield | EquipMask.MeleeWeapon,
        };

        /// <summary>Every combination of inMatch, settingOn, slot and shield-ness: refused exactly when all of match, setting, an off-hand slot and a non-shield hold.</summary>
        [TestMethod]
        public void Refuses_EveryInputCombination()
        {
            var combos = 0;

            foreach (var inMatch in new[] { false, true })
            foreach (var settingOn in new[] { false, true })
            foreach (var slot in Slots)
            foreach (var isShield in new[] { false, true })
            {
                var expected = inMatch && settingOn && (slot & EquipMask.Shield) != 0 && !isShield;

                Assert.AreEqual(expected, PvpDualWieldRule.Refuses(inMatch, settingOn, slot, isShield), $"inMatch {inMatch}, setting {settingOn}, slot {slot}, shield {isShield}");
                combos++;
            }

            Assert.AreEqual(2 * 2 * Slots.Length * 2, combos);
        }

        [TestMethod]
        public void Refuses_AWeaponInTheOffHand_InAMatch_WithTheSettingOn()
        {
            Assert.IsTrue(PvpDualWieldRule.Refuses(inMatch: true, settingOn: true, EquipMask.Shield, itemIsShield: false));
        }

        [TestMethod]
        public void Refuses_NeverAShield_InAMatch()
        {
            Assert.IsFalse(PvpDualWieldRule.Refuses(inMatch: true, settingOn: true, EquipMask.Shield, itemIsShield: true));
        }

        [TestMethod]
        public void Refuses_NeverOutsideAMatch_AWeaponMayDualWield()
        {
            Assert.IsFalse(PvpDualWieldRule.Refuses(inMatch: false, settingOn: true, EquipMask.Shield, itemIsShield: false));
        }

        [TestMethod]
        public void Refuses_NeverWithTheSettingOff()
        {
            Assert.IsFalse(PvpDualWieldRule.Refuses(inMatch: true, settingOn: false, EquipMask.Shield, itemIsShield: false));
        }

        [TestMethod]
        public void Refuses_AMainHandOrArmorSlot_IsNeverRefused()
        {
            foreach (var slot in new[] { EquipMask.MeleeWeapon, EquipMask.TwoHanded, EquipMask.MissileWeapon, EquipMask.Held, EquipMask.ChestArmor })
                Assert.IsFalse(PvpDualWieldRule.Refuses(true, true, slot, false), slot.ToString());
        }

        // ---------------- the setting ----------------

        [TestMethod]
        public void Setting_DefaultsOn_AndTheCompiledDefaultMatchesTheRegisteredOne()
        {
            Assert.IsTrue(PvpArenaHookSettings.MatchBlockDualWieldDefault, "new settings default ON");
            Assert.AreEqual(PvpArenaHookSettings.MatchBlockDualWieldDefault, DefaultPropertyManager.DefaultBooleanProperties["pvp_match_block_dual_wield"].Item);
        }

        [TestMethod]
        public void Setting_WithPropertyManagerUnreadable_FallsBackToTheCompiledDefault()
        {
            // PropertyManager reads throw in this harness (no shard config); the real seam must fall back, not throw.
            Assert.IsTrue(PvpArenaHookSettings.ReadMatchBlockDualWield());
        }

        // ---------------- the Player predicate ----------------

        private static PvpPlayerBinding Bind() =>
            new PvpPlayerBinding(new PvpMatch(Guid.NewGuid(), "ffa", new List<PvpTeam>
            {
                new PvpTeam(0, new List<PvpParticipant> { new PvpParticipant(1, 1500) }),
                new PvpTeam(1, new List<PvpParticipant> { new PvpParticipant(3, 1500) })
            }, T0), 0, PvpMatchState.Live, true, true, true, true, true);

        private static void SetInherited(WorldObject wo, string name, object value)
        {
            var field = typeof(WorldObject).GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            Assert.IsNotNull(field, $"WorldObject.{name} was not found by reflection - has it been renamed?");
            field.SetValue(wo, value);
        }

        private static Player Seeded(PvpPlayerBinding binding, bool wearingKit = false)
        {
            var wo = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));
            SetInherited(wo, "<Biota>k__BackingField", new ACE.Entity.Models.Biota());
            SetInherited(wo, "BiotaDatabaseLock", new ReaderWriterLockSlim());
            wo.SetPvpBindingForTests(binding);

            if (wearingKit)
                wo.SetProperty(PropertyString.PvpTemplateRestore, "{\"seeded\":true}");

            return wo;
        }

        [TestMethod]
        public void Player_Bound_WeaponInTheOffHand_IsRefused_AShieldIsNot()
        {
            PvpArenaHookSettings.MatchBlockDualWieldSource = () => true;
            var p = Seeded(Bind());

            Assert.IsTrue(p.PvpDualWieldRefuses(EquipMask.Shield, itemIsShield: false));
            Assert.IsFalse(p.PvpDualWieldRefuses(EquipMask.Shield, itemIsShield: true));
            Assert.IsFalse(p.PvpDualWieldRefuses(EquipMask.MeleeWeapon, itemIsShield: false));
        }

        [TestMethod]
        public void Player_WearingAnIssuedKit_WithNoBindingYet_IsStillInAMatch()
        {
            PvpArenaHookSettings.MatchBlockDualWieldSource = () => true;
            var p = Seeded(binding: null, wearingKit: true);

            Assert.IsFalse(p.IsInPvpMatch, "fixture: no binding");
            Assert.IsTrue(p.IsPvpTemplated, "fixture: the kit mark");
            Assert.IsTrue(p.PvpDualWieldRefuses(EquipMask.Shield, itemIsShield: false), "the kit-apply window is covered");
        }

        [TestMethod]
        public void Player_NotInAMatch_AWeaponMayGoInTheOffHand()
        {
            PvpArenaHookSettings.MatchBlockDualWieldSource = () => true;

            Assert.IsFalse(Seeded(null).PvpDualWieldRefuses(EquipMask.Shield, itemIsShield: false));
        }

        [TestMethod]
        public void Player_SettingOff_NothingIsRefused_EvenInAMatch()
        {
            PvpArenaHookSettings.MatchBlockDualWieldSource = () => false;

            Assert.IsFalse(Seeded(Bind()).PvpDualWieldRefuses(EquipMask.Shield, itemIsShield: false));
            Assert.IsFalse(Seeded(null, wearingKit: true).PvpDualWieldRefuses(EquipMask.Shield, itemIsShield: false));
        }

        [TestMethod]
        public void Player_SettingIsReadAtEachCall_NotCached()
        {
            var on = true;
            PvpArenaHookSettings.MatchBlockDualWieldSource = () => on;
            var p = Seeded(Bind());

            Assert.IsTrue(p.PvpDualWieldRefuses(EquipMask.Shield, false));
            on = false;
            Assert.IsFalse(p.PvpDualWieldRefuses(EquipMask.Shield, false));
        }

        [TestMethod]
        public void RefusalLine_IsTheOwnersText_AndAscii()
        {
            Assert.AreEqual("Dual wielding is disabled in PvP matches.", PvpArenaText.DualWieldDisabled);
            Assert.IsTrue(PvpArenaText.DualWieldDisabled.All(c => c < 128));
        }

        // ---------------- source bindings: each call site ----------------

        private static string ReadServerSource(string relativePath)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Source", "ACE.Server", "WorldObjects")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, "repo root (a directory containing Source/ACE.Server/WorldObjects) not found above the test output dir");

            var path = Path.Combine(dir.FullName, "Source", relativePath.Replace('/', Path.DirectorySeparatorChar));

            Assert.IsTrue(File.Exists(path), $"expected source file not found: {path}");

            return File.ReadAllText(path).Replace("\r\n", "\n");
        }

        /// <summary>The body of the method whose declaration contains <paramref name="signature"/>: from its opening brace to the matching close. Fails when the signature is missing or ambiguous.</summary>
        private static string MethodBody(string source, string signature)
        {
            var at = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.IsTrue(at >= 0, $"method not found: {signature}");
            Assert.AreEqual(-1, source.IndexOf(signature, at + signature.Length, StringComparison.Ordinal), $"signature is ambiguous: {signature}");

            var open = source.IndexOf('{', at);
            var depth = 0;

            for (var i = open; i < source.Length; i++)
            {
                if (source[i] == '{')
                    depth++;
                else if (source[i] == '}')
                {
                    depth--;

                    if (depth == 0)
                        return source.Substring(open, i - open + 1);
                }
            }

            Assert.Fail($"unbalanced braces after {signature}");
            return null;
        }

        /// <summary>True when every needle appears in <paramref name="body"/>, in the given order.</summary>
        private static bool InOrder(string body, params string[] needles)
        {
            var from = 0;

            foreach (var needle in needles)
            {
                var at = body.IndexOf(needle, from, StringComparison.Ordinal);

                if (at < 0)
                    return false;

                from = at + needle.Length;
            }

            return true;
        }

        private static readonly string[] EquipChokePoint =
        {
            "if (PvpDualWieldRefuses(wieldedLocation, item.IsShield))",
            "new GameEventInventoryServerSaveFailed(Session, item.Guid.Full, WeenieError.ConflictingInventoryLocation)",
            "PvpArenaText.DualWieldDisabled",
            "return false;",
            "CheckWieldRequirements(item)",
        };

        /// <summary>
        /// Call site 1, every player-driven equip: the refusal sits at the top of DoHandleActionGetAndWieldItem, with the thrown-weapon
        /// refusal's error, the chat line, and a return BEFORE the wield checks. DISCRIMINATES: the matcher fails on a copy with the call
        /// removed and on a copy with the refusal moved after the wield check.
        /// </summary>
        [TestMethod]
        public void EquipChokePoint_RefusesBeforeAnyWieldCheck()
        {
            var body = MethodBody(ReadServerSource("ACE.Server/WorldObjects/Player_Inventory.cs"),
                "private bool DoHandleActionGetAndWieldItem(WorldObject item, Container fromContainer, Container itemRootOwner, bool wasEquipped, EquipMask wieldedLocation, bool fromSplit = false)");

            Assert.IsTrue(InOrder(body, EquipChokePoint), "the off-hand refusal is missing from DoHandleActionGetAndWieldItem or sits after the wield check");

            var removed = body.Replace("if (PvpDualWieldRefuses(wieldedLocation, item.IsShield))", "if (false)");
            Assert.IsFalse(InOrder(removed, EquipChokePoint), "discrimination: a copy without the call must fail the matcher");

            var refusalAfterTheCheck = removed + "\n" + string.Join("\n", EquipChokePoint.Take(EquipChokePoint.Length - 1));
            Assert.IsFalse(InOrder(refusalAfterTheCheck, EquipChokePoint), "discrimination: a refusal placed after the wield check must fail the matcher");
        }

        /// <summary>The refusal in the choke point passes the TARGET slot and the item's shield-ness to the Player predicate, and nothing else decides it.</summary>
        [TestMethod]
        public void EquipChokePoint_FeedsTheRuleTheTargetSlotAndTheItem()
        {
            var inventory = ReadServerSource("ACE.Server/WorldObjects/Player_Inventory.cs");
            var arena = ReadServerSource("ACE.Server/WorldObjects/Player_PvpArena.cs");

            Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(inventory, @"PvpDualWieldRefuses\(").Count, "exactly one refusal site in Player_Inventory.cs");

            // An expression-bodied member: the text from its signature to the closing semicolon.
            var signature = "internal bool PvpDualWieldRefuses(EquipMask slot, bool itemIsShield)";
            var at = arena.IndexOf(signature, StringComparison.Ordinal);
            Assert.IsTrue(at >= 0, "predicate not found");
            var predicate = arena.Substring(at, arena.IndexOf(';', at) - at);

            Assert.IsTrue(InOrder(predicate, "PvpDualWieldRule.Refuses(", "IsInPvpMatch || IsPvpTemplated", "PvpArenaHookSettings.MatchBlockDualWieldSource()", "slot, itemIsShield"),
                "the predicate must combine the binding OR the kit, the setting seam and the slot");
            Assert.IsFalse(InOrder(predicate.Replace("IsInPvpMatch || IsPvpTemplated", "IsInPvpMatch"), "PvpDualWieldRule.Refuses(", "IsInPvpMatch || IsPvpTemplated"), "discrimination: dropping the kit term fails the matcher");
        }

        private static readonly string[] KitApplyClear =
        {
            "TryIssuePvpTemplateKitItem(kitItem",
            "ClearPvpOffHandWeapon(definition.Key)",
            "PvpTemplateOverlay.BuildTemplateBuffs(definition, Guid.Full)",
        };

        /// <summary>
        /// Call site 2, a kit that contains an off-hand weapon: the clear runs after the whole kit is issued and before the buffs, inside
        /// ApplyPvpTemplateNow. DISCRIMINATES against a copy with the call removed.
        /// </summary>
        [TestMethod]
        public void KitApply_ClearsTheOffHand_AfterTheKitIsIssued()
        {
            var body = MethodBody(ReadServerSource("ACE.Server/WorldObjects/Player_PvpTemplate.cs"), "internal PvpTemplateApplyResult ApplyPvpTemplateNow(PvpTemplateDefinition definition, Guid matchId)");

            Assert.IsTrue(InOrder(body, KitApplyClear), "ApplyPvpTemplateNow must clear the off-hand weapon between the kit issue and the buffs");
            Assert.IsFalse(InOrder(body.Replace("ClearPvpOffHandWeapon(definition.Key)", "false"), KitApplyClear), "discrimination: a copy without the call must fail the matcher");
        }

        private static readonly string[] EntryClear =
        {
            "pvpBinding = binding;",
            "ClearPvpOffHandWeapon(binding.Template?.Key)",
            "InvalidateClassAbilityHookCaches();",
        };

        /// <summary>
        /// Call site 3, match entry (a mode with no kit, or any leftover): the clear runs once the binding is set. The binding must come FIRST,
        /// or the predicate would not see a match yet. DISCRIMINATES against a copy with the call removed and a copy with the order swapped.
        /// </summary>
        [TestMethod]
        public void MatchEntry_ClearsTheOffHand_AfterTheBindingIsSet()
        {
            var body = MethodBody(ReadServerSource("ACE.Server/WorldObjects/Player_PvpArena.cs"), "internal void EnterPvpMatchNow(PvpPlayerBinding binding, Position exitTo)");

            Assert.IsTrue(InOrder(body, EntryClear), "EnterPvpMatchNow must clear the off-hand weapon after pvpBinding = binding");
            Assert.IsFalse(InOrder(body.Replace("ClearPvpOffHandWeapon(binding.Template?.Key)", "false"), EntryClear), "discrimination: a copy without the call must fail the matcher");
            Assert.IsFalse(InOrder(body.Replace("pvpBinding = binding;", "pvpBinding = null;"), EntryClear), "discrimination: the call before the binding must fail the matcher");
        }

        /// <summary>The clear only ever takes a non-shield from the off-hand slot to the pack, behind the same predicate, and puts it back when the pack is full.</summary>
        [TestMethod]
        public void ClearOffHand_UsesThePredicate_TakesTheOffHandWeaponToThePack_AndRestoresItWhenThePackIsFull()
        {
            var body = MethodBody(ReadServerSource("ACE.Server/WorldObjects/Player_PvpArena.cs"), "internal bool ClearPvpOffHandWeapon(string templateKey)");

            var order = new[]
            {
                "PvpDualWieldRefuses(EquipMask.Shield, itemIsShield: false)",
                "GetDualWieldWeapon()",
                "PvpTemplate.IsMarkedIssued(weapon)",
                "issued ? DequipObjectAction.ConsumeItem : DequipObjectAction.DequipToPack",
                "if (issued)",
                "PvpArenaText.DualWieldDisabled",
                "TryCreateInInventoryWithNetworking(dequipped",
                "TryEquipObjectWithNetworking(dequipped, EquipMask.Shield)",
                "HasEquippedItem(dequipped.Guid)",
                "PvpArenaText.DualWieldDisabled",
                "Log.Warn(",
            };

            Assert.IsTrue(InOrder(body, order));
            Assert.IsFalse(InOrder(body.Replace("PvpDualWieldRefuses(EquipMask.Shield, itemIsShield: false)", "true"), order), "discrimination: without the predicate the matcher fails");
            Assert.IsFalse(InOrder(body.Replace("PvpTemplate.IsMarkedIssued(weapon)", "false"), order), "discrimination: without the issued test the matcher fails");
            Assert.IsFalse(InOrder(body.Replace("DequipObjectAction.ConsumeItem", "DequipObjectAction.DequipToPack"), order), "discrimination: an issued weapon sent to the pack (which can be full) fails the matcher");
            Assert.IsFalse(InOrder(body.Replace("HasEquippedItem(dequipped.Guid)", "false"), order), "discrimination: a throw during the re-equip must be judged by HasEquippedItem");
            Assert.IsFalse(body.Contains("TryRemoveFromInventory"), "never detaches by hand: the weapon goes to the pack or is consumed");
        }

        private static readonly string[] RestoreForbidden = { "DoHandleActionGetAndWieldItem", "HandleActionGetAndWieldItem", "PvpDualWieldRefuses" };

        private static bool HoldsAnyForbidden(string body) => RestoreForbidden.Any(n => body.Contains(n));

        /// <summary>
        /// The restore must never be refused by the dual-wield rule: the bodies that re-equip the player's own gear after a match
        /// (RestorePvpTemplateOwnEquip) and the facet detach-and-equip it relies on (TryDetachAndEquipForFacet) call neither the
        /// player-equip choke point nor the predicate. DISCRIMINATES: the same scan fails on copies with each call spliced in.
        /// </summary>
        [TestMethod]
        public void RestorePaths_NeverCallTheEquipChokePointOrThePredicate()
        {
            var restore = MethodBody(ReadServerSource("ACE.Server/WorldObjects/Player_PvpTemplate.cs"), "internal List<string> RestorePvpTemplateOwnEquip(");
            var detach = MethodBody(ReadServerSource("ACE.Server/WorldObjects/Player_Facets.cs"), "private bool TryDetachAndEquipForFacet(");

            Assert.IsFalse(HoldsAnyForbidden(restore), "RestorePvpTemplateOwnEquip must not route through the player equip rule");
            Assert.IsFalse(HoldsAnyForbidden(detach), "TryDetachAndEquipForFacet must not route through the player equip rule");

            foreach (var name in RestoreForbidden)
            {
                Assert.IsTrue(HoldsAnyForbidden(restore.Replace("{", "{ " + name + "(); ")), $"discrimination: {name} spliced into the restore body is caught");
                Assert.IsTrue(HoldsAnyForbidden(detach.Replace("{", "{ " + name + "(); ")), $"discrimination: {name} spliced into the detach body is caught");
            }
        }
    }
}
