using System;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.DatLoader;
using ACE.Entity.Enum;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Lugian spell casters (Obsidian Chanter 1008452, Deepvein Chanter 1008456) share motion table 0x09000006, which has no Magic
    /// stance and no CastSpell animation, so Monster_Magic.PreCastMotion broadcast a motion that plays nothing and returned a wind-up
    /// length of 0: the caster stood still. MonsterCastGesture maps that table to a gesture it does have.
    ///
    /// The dat-backed pair pins the dat facts the map rests on. The selection tests are dat-free. The source pin is what sees
    /// PreCastMotion consult the map at all (a live Monster needs a landblock, out of reach in this project), so it is the guard
    /// against the map existing while nothing reads it.
    /// </summary>
    [TestClass]
    public class MonsterCastGestureTests
    {
        private const uint LugianTable = 0x09000006;

        // the stances a Chanter casts from: it wields nothing, so Melee combat mode resolves to HandCombat; NonCombat covers a pre-combat cast
        private static readonly MotionStance[] CastingStances = { MotionStance.HandCombat, MotionStance.NonCombat };

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void Lugian_table_has_no_CastSpell_in_the_stances_a_Chanter_casts_from()
        {
            RequireDats();

            foreach (var stance in CastingStances)
                Assert.AreEqual(0.0f, ACE.Server.Physics.Animation.MotionTable.GetAnimationLength(LugianTable, stance, MotionCommand.CastSpell, 2.0f), $"{stance}: CastSpell length");
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void Lugian_fallback_gesture_exists_in_the_casting_stances_with_a_positive_length()
        {
            RequireDats();

            var gesture = MonsterCastGesture.Fallbacks[LugianTable];

            foreach (var stance in CastingStances)
            {
                var length = ACE.Server.Physics.Animation.MotionTable.GetAnimationLength(LugianTable, stance, gesture, 2.0f);
                Assert.IsTrue(length > 0.0f, $"{stance}: {gesture} length {length}");
            }
        }

        [TestMethod]
        public void A_table_with_CastSpell_keeps_CastSpell()
        {
            Assert.AreEqual(MotionCommand.CastSpell, MonsterCastGesture.Select(LugianTable, 1.2f), "even a mapped table keeps CastSpell when it has one");
            Assert.AreEqual(MotionCommand.CastSpell, MonsterCastGesture.Select(0x09000001, 1.2f));
        }

        [TestMethod]
        public void A_mapped_table_without_CastSpell_gets_its_fallback()
        {
            Assert.AreEqual(MotionCommand.Twitch1, MonsterCastGesture.Select(LugianTable, 0.0f));
        }

        [TestMethod]
        public void An_unmapped_table_without_CastSpell_is_unchanged()
        {
            Assert.AreEqual(MotionCommand.CastSpell, MonsterCastGesture.Select(0x09000001, 0.0f));
            Assert.AreEqual(MotionCommand.CastSpell, MonsterCastGesture.Select(0, 0.0f));
        }

        [TestMethod]
        public void PreCastLength_for_the_fallback_is_the_fallback_gestures_length()
        {
            var asked = MotionCommand.Invalid;
            var length = MonsterCastGesture.PreCastLength(MotionCommand.Twitch1, 0.0f, m => { asked = m; return 1.13f; });

            Assert.AreEqual(1.13f, length);
            Assert.AreEqual(MotionCommand.Twitch1, asked);
        }

        [TestMethod]
        public void PreCastLength_for_CastSpell_is_the_CastSpell_length_and_never_consults_the_lookup()
        {
            var length = MonsterCastGesture.PreCastLength(MotionCommand.CastSpell, 1.5f, m => throw new AssertFailedException("lookup must not run for CastSpell"));

            Assert.AreEqual(1.5f, length);
        }

        [TestMethod]
        public void PreCastMotion_broadcasts_the_selected_motion_and_returns_the_helpers_length()
        {
            var text = File.ReadAllText(Path.Combine(FindSourceDir(), "ACE.Server", "WorldObjects", "Monster_Magic.cs")).Replace("\r\n", "\n");

            var start = text.IndexOf("public float PreCastMotion(", StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, "PreCastMotion not found");
            var end = text.IndexOf("private float PreCastMotion_Human", start, StringComparison.Ordinal);
            Assert.IsTrue(end > start, "PreCastMotion_Human not found after PreCastMotion");

            // comment lines out first, so a commented-out statement cannot satisfy a pin
            var body = string.Join("\n", text.Substring(start, end - start).Split('\n').Where(l => !l.TrimStart().StartsWith("//")));

            foreach (var statement in new[]
            {
                "var castMotion = MonsterCastGesture.Select(MotionTableId, castSpellLength);",
                "var motion = new Motion(this, castMotion, PreCastSpeed);",
                "CurrentMotionState = motion;",
                "EnqueueBroadcastMotion(motion);",
                "return MonsterCastGesture.PreCastLength(castMotion, castSpellLength,",
                "m => MotionTable.GetAnimationLength(MotionTableId, CurrentMotionState.Stance, m, PreCastSpeed));",
            })
                StringAssert.Contains(body, statement);
        }

        private static string FindSourceDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "property-registry.tsv")))
                dir = dir.Parent;
            Assert.IsNotNull(dir, "Source/ not found by walking up from " + AppContext.BaseDirectory);
            return dir.FullName;
        }

        private static void RequireDats()
        {
            if (DatManager.PortalDat != null)
                return;

            var datDir = new[] { Environment.GetEnvironmentVariable("ACE_DAT_PATH"), @"C:\ACE\Dats\", @"C:\Turbine\Asheron's Call\" }
                .FirstOrDefault(d => !string.IsNullOrWhiteSpace(d) && File.Exists(Path.Combine(d, "client_portal.dat")));

            if (datDir == null)
                Assert.Inconclusive("client_portal.dat not found (set ACE_DAT_PATH to the directory holding the client .dat files).");

            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            DatManager.Initialize(datDir, true, false);
        }
    }
}
