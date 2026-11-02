using System;
using System.IO;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Source-text pins for the Focus DoT bonus wiring (same single-match style as WitheringVoidDotModTests):
    /// each statement must appear exactly once as CODE (// comments stripped), so a refactor that drops or
    /// duplicates a mark site fails loudly instead of silently re-enabling the bonus for a proc DoT.
    /// </summary>
    [TestClass]
    public class FocusDotWiringPinTests
    {
        [TestMethod]
        public void TickLoop_AppliesFocusThroughTheGate() => Pin(
            "ACE.Server/WorldObjects/Managers/EnchantmentManager.cs",
            "if (ShouldApplyFocusDotMod(true, false, aetheria, ProcDotMarks.IsProcSourced(enchantment)))");

        [TestMethod]
        public void TickLoop_MultipliesByTheFocusWrapper() => Pin(
            "ACE.Server/WorldObjects/Managers/EnchantmentManager.cs",
            "tickAmount *= sourcePlayer.GetFocusSpellDamageMod();");

        [TestMethod]
        public void HandleCastSpell_MarksBothEnchantmentCalls()
        {
            const string file = "ACE.Server/WorldObjects/WorldObject_Magic.cs";
            Pin(file, "CreateEnchantment(targetCreature ?? target, itemCaster, itemCaster, spell, equip, procSourced: enchantProcSourced);");
            Pin(file, "CreateEnchantment(targetCreature ?? target, this, weapon, spell, equip, isWeaponSpell: isWeaponSpell, procSourced: enchantProcSourced);");
            Pin(file, "var enchantProcSourced = fromProc || (this is Player enchantProcPlayer && enchantProcPlayer.ClassAbilityProcCastActive);");
        }

        [TestMethod]
        public void SpellProjectile_EnchantmentLanding_MarksSecondaryOrigin() => Pin(
            "ACE.Server/WorldObjects/SpellProjectile.cs",
            "ProjectileSource.CreateEnchantment(creatureTarget, ProjectileSource, ProjectileLauncher, Spell, false, FromProc, procSourced: IncomingDamageOrigin == IncomingDamageOrigin.Secondary);");

        [TestMethod]
        public void Add_SetsTheMarkOnBothExits()
        {
            const string file = "ACE.Server/WorldObjects/Managers/EnchantmentManager.cs";
            Pin(file, "ProcDotMarks.Set(newEntry, procSourced);");
            Pin(file, "ProcDotMarks.Set(result.Enchantment, procSourced);");
        }

        [TestMethod]
        public void NetherBloom_CarriesTheSourceMark() => Pin(
            "ACE.Server/ClassAbilities/Abilities/NetherBloomAbility.cs",
            "var result = target.EnchantmentManager.Add(spell, killer, null, procSourced: ProcDotMarks.IsProcSourced(source));");

        [TestMethod]
        public void DirtyFightingBleed_IsExcluded() => Pin(
            "ACE.Server/WorldObjects/Creature_Combat.cs",
            "var addResult = target.EnchantmentManager.Add(spell, this, weapon, procSourced: true);");

        private static void Pin(string file, string code)
        {
            var path = Path.Combine(FindSourceRoot(), file.Replace('/', Path.DirectorySeparatorChar));
            Assert.IsTrue(File.Exists(path), $"missing {path}");

            var hits = 0;

            foreach (var raw in File.ReadAllLines(path))
            {
                var commentAt = raw.IndexOf("//", StringComparison.Ordinal);
                var codeOnly = (commentAt >= 0 ? raw.Substring(0, commentAt) : raw).Trim();

                if (codeOnly == code)
                    hits++;
            }

            Assert.AreEqual(1, hits, $"expected exactly 1 code line in {file} matching `{code}`");
        }

        private static string FindSourceRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ACE.Server", "WorldObjects", "Managers", "EnchantmentManager.cs")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find ACE.Server/WorldObjects/Managers/EnchantmentManager.cs by walking up from {AppContext.BaseDirectory}");
            return dir.FullName;
        }
    }
}