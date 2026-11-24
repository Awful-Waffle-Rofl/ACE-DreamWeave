using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.DatLoader;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Pinnacles;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Pinnacles
{
    /// <summary>
    /// The grant semantics of the Pinnacle owner buff (LivePinnacleWorld.GrantBuff, the body of GrantOwnerBuff) on real
    /// creatures and the real EnchantmentManager: the written entry lasts exactly the given seconds from now and names the
    /// caster, a second grant from the same caster refreshes instead of layering, an entry the owner cast itself is never
    /// touched, and a spell that is not an enchantment (or a bad duration) writes nothing. The spells' dat half comes from
    /// client_portal.dat; the world-database half (the stat mod row) is a blank row set here, which the entry fields under
    /// test do not read. The dat-free companion is PinnacleLiveSourceTests.GrantOwnerBuff_GrantsThroughGrantBuff_*.
    /// Skips (Inconclusive) when the dats are absent.
    /// </summary>
    [TestClass]
    public class PinnacleOwnerBuffGrantTests
    {
        /// <summary>Legendary Acid Ward (owner ruling 2026-11-18, HARBINGER-ROUND2-PLAN.md section 8).</summary>
        private const uint WardSpellId = 6080;

        private const double Seconds = 120;

        private static readonly object initLock = new object();
        private static bool initialized;
        private static string skipReason;

        private static Spell DatSpell(uint id)
        {
            var spell = new Spell(id, loadDB: false);

            if (spell._spellBase != null)
                spell._spell = new ACE.Database.Models.World.Spell { Id = id, Name = spell.Name };

            return spell;
        }

        private static List<ACE.Entity.Models.PropertiesEnchantmentRegistry> Entries(Creature c, Spell spell)
            => c.EnchantmentManager.GetEnchantments(spell.Category);

        /// <summary>
        /// The Harbinger's attunement spells (HarbingerElementDef.AttuneSpell, round 2 B1) are each element's Legendary Ward and
        /// enchantments (GrantOwnerBuff refuses anything else), and the SpellId enum names the same ids.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void HarbingerAttuneSpells_AreTheElementsLegendaryWards_AndEnchantments()
        {
            RequireDats();

            var expected = new Dictionary<ACE.Server.Pinnacles.Harbinger.HarbingerElement, (uint Id, string Name, SpellId Enum)>
            {
                [ACE.Server.Pinnacles.Harbinger.HarbingerElement.Acid] = (6080, "Legendary Acid Ward", SpellId.CantripAcidWard4),
                [ACE.Server.Pinnacles.Harbinger.HarbingerElement.Lightning] = (6079, "Legendary Storm Ward", SpellId.CantripStormWard4),
                [ACE.Server.Pinnacles.Harbinger.HarbingerElement.Frost] = (6083, "Legendary Frost Ward", SpellId.CantripFrostWard4),
                [ACE.Server.Pinnacles.Harbinger.HarbingerElement.Fire] = (6082, "Legendary Flame Ward", SpellId.CantripFlameWard4),
            };

            foreach (var def in ACE.Server.Pinnacles.Harbinger.HarbingerData.Elements)
            {
                var (id, name, en) = expected[def.Element];
                var spell = DatSpell(def.AttuneSpell);

                Assert.AreEqual(id, def.AttuneSpell, def.Tag);
                Assert.AreEqual((uint)en, def.AttuneSpell, def.Tag + ": the SpellId enum");
                Assert.IsFalse(spell.NotFound, def.Tag);
                Assert.AreEqual(name, spell.Name, def.Tag);
                Assert.AreEqual(SpellType.Enchantment, spell.MetaSpellType, def.Tag);
            }
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void Grant_WritesTheGivenDuration_FromNow_CastByTheCaster()
        {
            RequireDats();

            var spell = DatSpell(WardSpellId);
            Assert.IsFalse(spell.NotFound, "the ward is in the dat");
            Assert.AreEqual(SpellType.Enchantment, spell.MetaSpellType, "the ward is an enchantment");
            Assert.AreNotEqual(Seconds, spell.Duration, "control: the spell's own duration differs, so the override is visible");

            var owner = TestCreatures.CreateQuestBearer();
            var head = TestCreatures.CreateDefender();

            var entry = LivePinnacleWorld.GrantBuff(owner, head, spell, Seconds);

            Assert.IsNotNull(entry);
            Assert.AreEqual(Seconds, entry.Duration, "the grant's duration, not the spell's");
            Assert.AreEqual(0.0, entry.StartTime, "from now");
            Assert.AreEqual(head.Guid.Full, entry.CasterObjectId, "the head is the caster");
            Assert.AreEqual((int)WardSpellId, entry.SpellId);
            Assert.AreEqual(1, Entries(owner, spell).Count);
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void ReGrant_FromTheSameCaster_RefreshesInsteadOfLayering()
        {
            RequireDats();

            var spell = DatSpell(WardSpellId);
            var owner = TestCreatures.CreateQuestBearer();
            var head = TestCreatures.CreateDefender();

            var first = LivePinnacleWorld.GrantBuff(owner, head, spell, Seconds);
            first.StartTime = -50;   // as if 50 s had run

            var second = LivePinnacleWorld.GrantBuff(owner, head, spell, Seconds);

            Assert.AreEqual(1, Entries(owner, spell).Count, "one layer per caster");
            Assert.AreSame(first, second, "the same entry, refreshed");
            Assert.AreEqual(0.0, second.StartTime, "the refresh restarts the clock");
            Assert.AreEqual(Seconds, second.Duration);
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void Grant_LeavesTheOwnersOwnCastOfTheSpellUntouched()
        {
            RequireDats();

            var spell = DatSpell(WardSpellId);
            var owner = TestCreatures.CreateQuestBearer();
            var head = TestCreatures.CreateDefender();

            var own = owner.EnchantmentManager.Add(spell, owner, null).Enchantment;
            own.StartTime = -30;
            var before = (own.LayerId, own.StartTime, own.Duration, own.CasterObjectId, own.StatModValue);

            var granted = LivePinnacleWorld.GrantBuff(owner, head, spell, Seconds);

            Assert.AreNotSame(own, granted, "a separate layer for a different caster");
            Assert.AreEqual(2, Entries(owner, spell).Count);
            Assert.AreEqual(before, (own.LayerId, own.StartTime, own.Duration, own.CasterObjectId, own.StatModValue), "the owner's own entry is untouched");
            Assert.AreEqual(owner.Guid.Full, own.CasterObjectId);
            Assert.AreEqual(Seconds, granted.Duration);
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void NonEnchantmentSpell_IsRefused_AndWritesNothing()
        {
            RequireDats();

            // the lowest-id projectile spell in the dat (a war bolt): not an enchantment
            var projectileId = DatManager.PortalDat.SpellTable.Spells.Where(kv => kv.Value.MetaSpellType == SpellType.Projectile).Min(kv => kv.Key);
            var spell = DatSpell(projectileId);
            Assert.AreEqual(SpellType.Projectile, spell.MetaSpellType);

            var owner = TestCreatures.CreateQuestBearer();
            var head = TestCreatures.CreateDefender();
            var rowsBefore = owner.Biota.PropertiesEnchantmentRegistry?.Count ?? 0;

            Assert.IsNull(LivePinnacleWorld.GrantBuff(owner, head, spell, Seconds), $"spell {projectileId} ({spell.Name}) refused");
            Assert.AreEqual(rowsBefore, owner.Biota.PropertiesEnchantmentRegistry?.Count ?? 0, "no registry row");
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void BadDuration_OrMissingParties_WriteNothing()
        {
            RequireDats();

            var spell = DatSpell(WardSpellId);
            var owner = TestCreatures.CreateQuestBearer();
            var head = TestCreatures.CreateDefender();

            foreach (var bad in new[] { 0.0, -1.0, double.NaN, double.PositiveInfinity })
                Assert.IsNull(LivePinnacleWorld.GrantBuff(owner, head, spell, bad), bad.ToString());

            Assert.IsNull(LivePinnacleWorld.GrantBuff(owner, null, spell, Seconds));
            Assert.IsNull(LivePinnacleWorld.GrantBuff(null, head, spell, Seconds));
            Assert.IsNull(LivePinnacleWorld.GrantBuff(owner, head, null, Seconds));
            Assert.AreEqual(0, Entries(owner, spell).Count);
        }

        // ================= dat handling (the portal dat only) =================

        private static void RequireDats()
        {
            lock (initLock)
            {
                if (!initialized)
                {
                    initialized = true;
                    skipReason = InitializeDats();
                }
            }

            if (skipReason != null)
                Assert.Inconclusive(skipReason);
        }

        private static string InitializeDats()
        {
            if (DatManager.PortalDat != null)
                return null;

            var candidates = new List<string>();

            var fromEnv = Environment.GetEnvironmentVariable("ACE_DAT_PATH");
            if (!string.IsNullOrWhiteSpace(fromEnv))
                candidates.Add(fromEnv);

            candidates.Add(@"C:\ACE\Dats\");
            candidates.Add(@"C:\Turbine\Asheron's Call\");

            var datDir = candidates.FirstOrDefault(c => File.Exists(Path.Combine(c, "client_portal.dat")));

            if (datDir == null)
                return $"Skipped: client_portal.dat not found in any of [{string.Join(", ", candidates)}]. Set ACE_DAT_PATH to a directory containing the client .dat files.";

            try
            {
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                DatManager.Initialize(datDir, true, false);
            }
            catch (Exception ex)
            {
                return $"Skipped: loading the dats from {datDir} failed: {ex.Message}";
            }

            return DatManager.PortalDat == null ? $"Skipped: {datDir} did not produce a PortalDat." : null;
        }
    }
}
