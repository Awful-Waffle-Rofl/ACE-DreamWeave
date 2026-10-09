using System;
using System.IO;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.WorldObjects.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// EnchantmentManager.ShouldApplyWitheringVoidDotMod: the pure gate for Withering's nether DoT tick
    /// scaling, extracted from the tick loop in ApplyDamageTick because a real Player (and hence a real
    /// tick) cannot be built without the world DB (see DotTickCreditsTests for the same limit).
    ///
    /// THE BUG THIS FIXES. Withering scaled a caster's nether DoT tick unconditionally, with no PvP gate,
    /// while the adjacent Hemomancy comment already claimed "matching Withering's own PvP carve-out at
    /// this site" - a carve-out that did not exist. The fix gates Withering PvE-only through
    /// PvpClassifier.IsPlayerPairIncludingSelf, matching Hemomancy's own targetPlayer == null gate
    /// (self-cast counts as a Player target, so Withering must not apply to a self-cast tick either).
    /// </summary>
    [TestClass]
    public class WitheringVoidDotModTests
    {
        // ---- the pure gate -------------------------------------------------------------------------

        [TestMethod]
        public void AppliesOnly_ForNetherTick_FromPlayer_OnNonPlayerTarget()
        {
            Assert.IsTrue(EnchantmentManager.ShouldApplyWitheringVoidDotMod(DamageType.Nether, sourceIsPlayer: true, isPlayerPairIncludingSelf: false),
                "a player's nether DoT tick on a creature must be scaled");
        }

        [TestMethod]
        // isPlayerPairIncludingSelf is true for both a self-cast tick and a tick on another player; the
        // distinction between those two cases is covered by PvpClassifierTests, not here.
        public void DoesNotApply_WhenTargetIsAPlayer()
        {
            Assert.IsFalse(EnchantmentManager.ShouldApplyWitheringVoidDotMod(DamageType.Nether, sourceIsPlayer: true, isPlayerPairIncludingSelf: true),
                "a player's nether DoT tick on another player must not be scaled");
        }

        [TestMethod]
        public void DoesNotApply_WhenSourceIsNotAPlayer()
        {
            Assert.IsFalse(EnchantmentManager.ShouldApplyWitheringVoidDotMod(DamageType.Nether, sourceIsPlayer: false, isPlayerPairIncludingSelf: false),
                "a non-player damager (e.g. a creature DoT) must never scale by Withering");
        }

        [TestMethod]
        public void DoesNotApply_ForNonNetherDamageType()
        {
            Assert.IsFalse(EnchantmentManager.ShouldApplyWitheringVoidDotMod(DamageType.Slash, sourceIsPlayer: true, isPlayerPairIncludingSelf: false),
                "Withering is nether-only, unlike Hemomancy which is not gated by damageType");
        }

        // ---- source-text pin: the tick loop actually calls the gate ---------------------------------

        /// <summary>
        /// Pins that ApplyDamageTick's Withering line calls ShouldApplyWitheringVoidDotMod (rather than an
        /// inline `sourcePlayer != null` test with no PvP gate), matched on CODE only: each line is
        /// stripped of any // comment first, and the whole statement must match exactly once.
        /// </summary>
        [TestMethod]
        public void TickLoop_CallsTheGate()
        {
            const string file = "ACE.Server/WorldObjects/Managers/EnchantmentManager.cs";
            const string code = "if (ShouldApplyWitheringVoidDotMod(damageType, sourcePlayer != null, PvpClassifier.IsPlayerPairIncludingSelf(damager, WorldObject)))";

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
