using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum.Properties;
using ACE.Server.Pvp;
using ACE.Server.Pvp.Rules;

namespace ACE.Server.Tests
{
    /// <summary>
    /// PropertyBool.PveOnly (9081). The two pure predicates on every input combination, the chat text, and source pins for each call
    /// site (no test here can drive a live equip or a live hit). No PropertyManager read anywhere.
    /// </summary>
    [TestClass]
    public class PvpPveOnlyRuleTests
    {
        [TestMethod]
        public void RefusesEquip_TruthTable()
        {
            Assert.IsFalse(PvpPveOnlyRule.RefusesEquip(inMatch: false, itemIsPveOnly: false));
            Assert.IsFalse(PvpPveOnlyRule.RefusesEquip(inMatch: false, itemIsPveOnly: true), "outside a match the item may be worn");
            Assert.IsFalse(PvpPveOnlyRule.RefusesEquip(inMatch: true, itemIsPveOnly: false), "an ordinary item is never refused by this rule");
            Assert.IsTrue(PvpPveOnlyRule.RefusesEquip(inMatch: true, itemIsPveOnly: true));
        }

        [TestMethod]
        public void BlocksDamage_TruthTable()
        {
            Assert.IsFalse(PvpPveOnlyRule.BlocksDamage(targetIsPlayer: false, weaponIsPveOnly: false));
            Assert.IsFalse(PvpPveOnlyRule.BlocksDamage(targetIsPlayer: false, weaponIsPveOnly: true), "monsters are fair game");
            Assert.IsFalse(PvpPveOnlyRule.BlocksDamage(targetIsPlayer: true, weaponIsPveOnly: false), "an ordinary weapon is untouched");
            Assert.IsTrue(PvpPveOnlyRule.BlocksDamage(targetIsPlayer: true, weaponIsPveOnly: true));
        }

        [TestMethod]
        public void PropertyId_Is9081()
            => Assert.AreEqual(9081, (int)PropertyBool.PveOnly);

        [TestMethod]
        public void ChatText_IsTheOwnersLine_AndAscii()
        {
            Assert.AreEqual("The Nether Nekode will not be raised against another player.", PvpArenaText.PveOnlyWillNotBeRaised("Nether Nekode"));
            Assert.AreEqual("Your Nether Nekode will not harm another player.", PvpArenaText.PveOnlyWillNotHarm("Nether Nekode"));
            Assert.IsTrue(PvpArenaText.PveOnlyWillNotBeRaised(null).All(c => c < 128));
            Assert.IsTrue(PvpArenaText.PveOnlyWillNotHarm(null).All(c => c < 128));
        }

        // ---------------- source pins ----------------

        private static string Read(string relativePath)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Source", "ACE.Server", "WorldObjects")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, "repo root not found above the test output dir");

            var path = Path.Combine(dir.FullName, "Source", relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.IsTrue(File.Exists(path), $"expected source file not found: {path}");

            return File.ReadAllText(path).Replace("\r\n", "\n");
        }

        private static int Count(string source, string needle) => Regex.Matches(source, Regex.Escape(needle)).Count;

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

        [TestMethod]
        public void EquipChokePoint_RefusesOnce_AfterDualWield_BeforeWieldChecks()
        {
            var src = Read("ACE.Server/WorldObjects/Player_Inventory.cs");

            Assert.AreEqual(1, Count(src, "PveOnlyEquipRefuses("), "exactly one equip refusal site");

            var order = new[] { "if (PvpDualWieldRefuses(wieldedLocation, item.IsShield))", "if (PveOnlyEquipRefuses(item))", "PvpArenaText.PveOnlyWillNotBeRaised(item.Name)", "return false;", "CheckWieldRequirements(item)" };
            Assert.IsTrue(InOrder(src, order));
            Assert.IsFalse(InOrder(src.Replace("if (PveOnlyEquipRefuses(item))", "if (false)"), order), "discrimination: without the call the matcher fails");
        }

        [TestMethod]
        public void MatchEntry_ClearsPveOnly_Once_AfterTheBinding()
        {
            var src = Read("ACE.Server/WorldObjects/Player_PvpArena.cs");

            Assert.AreEqual(1, Count(src, "ClearPvpPveOnlyItems(binding.Template?.Key)"));
            Assert.IsTrue(InOrder(src, "pvpBinding = binding;", "ClearPvpOffHandWeapon(binding.Template?.Key)", "ClearPvpPveOnlyItems(binding.Template?.Key)", "InvalidateClassAbilityHookCaches();"));
        }

        [TestMethod]
        public void KitApply_ClearsPveOnly_Once_AfterTheKitIsIssued_BeforeTheBuffs()
        {
            var src = Read("ACE.Server/WorldObjects/Player_PvpTemplate.cs");

            Assert.AreEqual(1, Count(src, "ClearPvpPveOnlyItems(definition.Key)"));
            Assert.IsTrue(InOrder(src, "TryIssuePvpTemplateKitItem(kitItem", "ClearPvpOffHandWeapon(definition.Key)", "ClearPvpPveOnlyItems(definition.Key)", "PvpTemplateOverlay.BuildTemplateBuffs(definition, Guid.Full)"));
        }

        [TestMethod]
        public void WeaponHit_DropsBeforeTheDamageRoll_Once()
        {
            var src = Read("ACE.Server/WorldObjects/Player_Combat.cs");

            Assert.AreEqual(1, Count(src, "PveOnlyBlocksWeaponHit(target, damageSource)"));

            var order = new[] { "var pkError = CheckPKStatusVsTarget(target, null);", "if (PveOnlyBlocksWeaponHit(target, damageSource))", "return null;", "DamageEvent.CalculateDamage(this, target, damageSource)" };
            Assert.IsTrue(InOrder(src, order));
            Assert.IsFalse(InOrder(src.Replace("if (PveOnlyBlocksWeaponHit(target, damageSource))", "if (false)"), order), "discrimination: without the call the matcher fails");
        }

        [TestMethod]
        public void SpellHit_DropsBeforeTheDamageRoll_Once()
        {
            var src = Read("ACE.Server/WorldObjects/SpellProjectile.cs");

            Assert.AreEqual(1, Count(src, "PveOnlyBlocksSpellHit("));

            var order = new[] { "ProjectileSource?.CheckPKStatusVsTarget(creatureTarget, Spell)", "player.PveOnlyBlocksSpellHit(creatureTarget, ProjectileLauncher)", "return;", "var damage = CalculateDamage(ProjectileSource, creatureTarget," };
            Assert.IsTrue(InOrder(src, order));
            Assert.IsFalse(InOrder(src.Replace("player.PveOnlyBlocksSpellHit(creatureTarget, ProjectileLauncher)", "false"), order), "discrimination: without the call the matcher fails");
        }

        [TestMethod]
        public void PlayerSide_FeedsTheRule_AndThrottlesPerPlayer()
        {
            var src = Read("ACE.Server/WorldObjects/Player_PveOnly.cs");

            Assert.IsTrue(src.Contains("PvpPveOnlyRule.RefusesEquip(IsInPvpMatch || IsPvpTemplated, IsPveOnlyItem(item))"), "in a match = binding OR worn kit, same as the dual-wield predicate");
            Assert.AreEqual(2, Count(src, "PvpPveOnlyRule.BlocksDamage("), "weapon site and spell site");
            Assert.IsTrue(src.Contains("private DateTime pveOnlyHarmNoticeAtUtc;"), "per-player timestamp");
            Assert.IsFalse(src.Contains("static Dictionary"), "no static throttle dictionary");
            Assert.IsTrue(src.Contains("PveOnlyHarmNoticeIntervalSeconds = 10.0"));
        }
    }
}
