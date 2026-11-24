using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ACE.Database;
using ACE.DatLoader;
using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The census of the REAL spell table against the Threads spell-strip rule: the portal dat supplies each spell's MetaSpellType and
    /// ace_world.spell its DrainPercentage, exactly the two sources ThreadEnemySpellStrip.IsCasterVitalScaled(int) reads through Spell.
    /// Skipped (Inconclusive) when client_portal.dat or the world database is not available, so it is local only (RequiresDatFiles).
    /// </summary>
    [TestClass]
    public class ThreadEnemySpellStripDatTests
    {
        /// <summary>The 25 caster-vital-scaled spells: Martyr's Hecatomb/Tenacity/Blight tiers and Curse of Raven Fury.</summary>
        private static readonly int[] Expected = Enumerable.Range(2760, 21).Concat(new[] { 3818, 4428, 4429, 4430 }).ToArray();

        /// <summary>Harm Other V, Heart Rend, Exsanguinating Wave, Dark Vortex: fixed-intensity health spells with no DrainPercentage (1175 is a Boost in the dat, probed 2026-11-21).</summary>
        private static readonly int[] FixedIntensity = { 1175, 2070, 3940, 3914 };

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void The_rule_over_the_real_dat_and_world_rows_selects_exactly_the_25_drain_spells()
        {
            var datDir = FindDatDir();

            if (datDir == null)
                Assert.Inconclusive("Skipped: client_portal.dat not found (set ACE_DAT_PATH).");

            TestEnvironment.InitializeConfig();

            Dictionary<uint, float> drain;

            try
            {
                if (!new WorldDatabase().Exists(false))
                    Assert.Inconclusive("Skipped: the world database is not reachable with the configured Config.js.");

                using var context = new ACE.Database.Models.World.WorldDbContext();
                drain = context.Spell.Where(r => r.DrainPercentage > 0f).ToDictionary(r => r.Id, r => r.DrainPercentage.Value);
            }
            catch (Exception ex) when (ex is not AssertInconclusiveException)
            {
                Assert.Inconclusive($"Skipped: the world database could not be read ({ex.Message}).");
                return;
            }

            if (DatManager.PortalDat == null)
            {
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                DatManager.Initialize(datDir, true, false);
            }

            var spells = DatManager.PortalDat.SpellTable.Spells;

            var selected = spells
                .Where(kv => ThreadEnemySpellStrip.IsCasterVitalScaled(kv.Value.MetaSpellType, drain.TryGetValue(kv.Key, out var d) ? d : 0f))
                .Select(kv => (int)kv.Key).OrderBy(i => i).ToList();

            CollectionAssert.AreEqual(Expected.OrderBy(i => i).ToList(), selected, "the rule over every spell in the dat");

            foreach (var id in FixedIntensity)
            {
                Assert.IsFalse(drain.ContainsKey((uint)id), $"{id} has no DrainPercentage, so it stays");
                Assert.IsFalse(selected.Contains(id), $"{id} is not selected");
            }
        }

        private static string FindDatDir()
        {
            var candidates = new List<string>();
            var fromEnv = Environment.GetEnvironmentVariable("ACE_DAT_PATH");

            if (!string.IsNullOrWhiteSpace(fromEnv))
                candidates.Add(fromEnv);

            candidates.Add(@"C:\ACE\Dats\");
            candidates.Add(@"C:\Turbine\Asheron's Call\");

            return candidates.FirstOrDefault(c => File.Exists(Path.Combine(c, "client_portal.dat")));
        }
    }
}
