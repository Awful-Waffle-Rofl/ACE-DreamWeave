using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ACE.DatLoader;
using ACE.Entity.Enum;
using ACE.Server.ClassAbilities;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using DbSpell = ACE.Database.Models.World.Spell;
using Spell = ACE.Server.Entity.Spell;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Echo Cast wisp qualification on REAL spell data (owner, 2026-11-23): the Heal and Revitalize families and the Drain
    /// Other family, read from client_portal.dat (MetaSpellType, school, the Beneficial and SelfTargeted flags), with the
    /// world database's spell row fields the rules read (damage_Type for a Boost, transfer_Bitfield for a Transfer) copied
    /// from the local ace_world spell table, since ACE.Server.Tests has no database:
    ///   SELECT id, name, damage_Type, transfer_Bitfield FROM spell WHERE id IN (...)
    /// run against ace_world on 2026-11-23 (ace_ro). RequiresDatFiles: Inconclusive without ACE_DAT_PATH, skipped in CI;
    /// the dat-free pins on the same rules are EchoWispRestoreTests.
    /// </summary>
    [TestClass]
    public class EchoWispSpellDataDatTests
    {
        // id, ace_world damage_Type (0x80 Health, 0x100 Stamina, 0x200 Mana)
        private static readonly (uint Id, string Name, int DamageType)[] Restores =
        {
            (6, "Heal Self I", 128), (5, "Heal Other I", 128), (1161, "Heal Self VI", 128), (1166, "Heal Other VI", 128),
            (4311, "Incantation of Heal Self", 128), (4310, "Incantation of Heal Other", 128),
            (1177, "Revitalize Self I", 256), (1183, "Revitalize Other I", 256), (1182, "Revitalize Self VI", 256), (1188, "Revitalize Other VI", 256),
            (4321, "Incantation of Revitalize Self", 256), (4320, "Incantation of Revitalize Other", 256),
        };

        // the other spells next to them that must stay out: Mana Boost (a beneficial Boost of Mana), Harm and Enfeeble
        // (harmful Boosts, Damage spells), Infuse (a beneficial Transfer), and a buff
        private static readonly (uint Id, string Name, int? DamageType, uint? TransferBitfield)[] NotRestores =
        {
            (1201, "Mana Boost Self I", 512, null), (1207, "Mana Boost Other I", 512, null),
            (7, "Harm Other I", 128, null), (1195, "Enfeeble Other I", 256, null),
            (1225, "Infuse Health Other I", null, 9u), (1, "Strength Other I", null, null),
        };

        // ace_world transfer_Bitfield 6 = TargetSource | CasterDestination on every row
        private static readonly (uint Id, string Name)[] Drains =
        {
            (1237, "Drain Health Other I"), (1242, "Drain Health Other VI"),
            (1249, "Drain Stamina Other I"), (1254, "Drain Stamina Other VI"),
            (1260, "Drain Mana Other I"), (1265, "Drain Mana Other VI"),
        };

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void HealAndRevitalize_AreRestoreSpells_FromTheirRealData_AndQualifyOnlyAtTheCasterOrAFellow()
        {
            RequirePortalDat();

            foreach (var (id, name, damageType) in Restores)
            {
                var spell = Load(id, name, new DbSpell { Id = id, DamageType = damageType });

                Assert.AreEqual(SpellType.Boost, spell.MetaSpellType, $"{name}: a Boost in the dat");
                Assert.IsTrue(spell.IsBeneficial, $"{name}: beneficial in the dat");
                Assert.AreEqual(MagicSchool.LifeMagic, spell.School, $"{name}: Life Magic, so the body is blue");
                Assert.AreEqual(EchoWispMath.LifeBody, EchoWispMath.BodyWcidFor(spell.School), $"{name}: the blue body");
                Assert.AreEqual(name.Contains(" Self"), spell.IsSelfTargeted, $"{name}: the dat's SelfTargeted flag tells Self from Other");

                var vital = EchoWisp.RestoreVitalOf(spell);
                Assert.IsTrue(EchoWispMath.IsRestore(spell.MetaSpellType, spell.IsBeneficial, vital), $"{name}: a Restore spell");
                Assert.AreEqual(EchoWispSpellKind.Restore, EchoWispMath.KindOf(spell.MetaSpellType, spell.IsBeneficial, vital), name);

                Assert.IsTrue(EchoWispMath.Qualifies(spell.MetaSpellType, spell.IsHarmful, false, true, true, true, false, vital, true), $"{name}: at the caster or a fellow");
                Assert.IsFalse(EchoWispMath.Qualifies(spell.MetaSpellType, spell.IsHarmful, false, true, true, true, false, vital, false), $"{name}: never at a stranger");
                Assert.IsFalse(EchoWispMath.Qualifies(spell.MetaSpellType, spell.IsHarmful, false, true, true, false, false, vital, false), $"{name}: never at a monster");
            }
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void ManaBoostHarmInfuseAndBuffs_AreNotRestoreSpells()
        {
            RequirePortalDat();

            foreach (var (id, name, damageType, transfer) in NotRestores)
            {
                var spell = Load(id, name, new DbSpell { Id = id, DamageType = damageType, TransferBitfield = transfer });
                var vital = EchoWisp.RestoreVitalOf(spell);

                Assert.IsFalse(EchoWispMath.IsRestore(spell.MetaSpellType, spell.IsBeneficial, vital), $"{name}: not a Restore spell");
                Assert.IsFalse(EchoWispMath.Qualifies(spell.MetaSpellType, spell.IsHarmful, false, true, true, true, false, vital, true),
                    $"{name}: never qualifies at the caster or a fellow");
            }
        }

        /// <summary>
        /// The owner asked for drains to work (2026-11-23). From the real data: a harmful Transfer (so it qualifies at a
        /// monster and never at a player), a Damage kind (it stays on its target until it dies), and transfer_Bitfield 6 =
        /// TargetSource | CasterDestination, so HandleCastSpell_Transfer sends the vital to `this` - the player, since
        /// CastAsEchoWisp runs HandleCastSpell on the player (EchoWispRestoreTests.Drain_SendsTheVitalToTheCaster_NotTheWisp).
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void Drains_QualifyFromTheirRealData_AndSendTheVitalToTheCaster()
        {
            RequirePortalDat();

            foreach (var (id, name) in Drains)
            {
                var spell = Load(id, name, new DbSpell { Id = id, TransferBitfield = 6u });

                Assert.AreEqual(SpellType.Transfer, spell.MetaSpellType, $"{name}: a Transfer in the dat");
                Assert.IsTrue(spell.IsHarmful, $"{name}: harmful in the dat (no Beneficial flag)");
                Assert.AreEqual(MagicSchool.LifeMagic, spell.School, name);
                Assert.IsFalse(spell.IsSelfTargeted, name);

                var vital = EchoWisp.RestoreVitalOf(spell);

                Assert.IsTrue(EchoWispMath.Qualifies(spell.MetaSpellType, spell.IsHarmful, false, true, true, false, false, vital, false), $"{name}: qualifies at a monster");
                Assert.IsFalse(EchoWispMath.Qualifies(spell.MetaSpellType, spell.IsHarmful, false, true, true, true, false, vital, true), $"{name}: never at a player, a fellow included");
                Assert.AreEqual(EchoWispSpellKind.Damage, EchoWispMath.KindOf(spell.MetaSpellType, spell.IsBeneficial, vital), $"{name}: stays on its target until it dies");

                Assert.IsTrue(spell.TransferFlags.HasFlag(TransferFlags.CasterDestination), $"{name}: the vital goes to the caster");
                Assert.IsTrue(spell.TransferFlags.HasFlag(TransferFlags.TargetSource), $"{name}: and comes from the target");
            }
        }

        private static Spell Load(uint id, string name, DbSpell row)
        {
            var spell = new Spell(id, false);
            Assert.IsNotNull(spell._spellBase, $"{id} is in the portal dat");
            Assert.AreEqual(name, spell._spellBase.Name, $"control: spell {id} is {name}");
            spell._spell = row;
            return spell;
        }

        // ===================== the portal dat (the EchoWispPickDatTests / CombatSimulatorPlayerSnapshotTests pattern) =====================

        private static readonly object initLock = new object();
        private static bool initialized;
        private static string skipReason;

        private static void RequirePortalDat()
        {
            lock (initLock)
            {
                if (!initialized)
                {
                    initialized = true;
                    skipReason = Initialize();
                }
            }

            if (skipReason != null)
                Assert.Inconclusive(skipReason);
        }

        private static string Initialize()
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
                return $"Skipped: client_portal.dat not found in any of [{string.Join(", ", candidates)}]. Set ACE_DAT_PATH.";

            try
            {
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                DatManager.Initialize(datDir, true, false);
            }
            catch (Exception ex)
            {
                return $"Skipped: DatManager.Initialize({datDir}) failed: {ex.Message}";
            }

            return DatManager.PortalDat == null ? $"Skipped: DatManager.Initialize({datDir}) did not produce a PortalDat." : null;
        }
    }
}
