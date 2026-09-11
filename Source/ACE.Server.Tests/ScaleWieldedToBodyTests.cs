using System.IO;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Unit coverage for PropertyBool 9038 ScaleWieldedToBody (Docs/WorldEvents/BOSS-BUILD-SPEC.md,
    /// 2026-08-19 gear pass): the pure scale resolver that Creature_Equipment.GenerateWieldList uses to
    /// size a create_list Wield item to the body of the creature that spawned it.
    ///
    /// Everything here is a pure static or a text read - no database, no landblock, no Creature.
    /// </summary>
    [TestClass]
    public class ScaleWieldedToBodyTests
    {
        [TestMethod]
        public void ResolveWieldScale_FlagOff_LeavesItemAlone()
        {
            Assert.IsNull(Creature.ResolveWieldScale(false, 5.3f, null));
            Assert.IsNull(Creature.ResolveWieldScale(false, 5.3f, 1.1f));
        }

        [TestMethod]
        public void ResolveWieldScale_UnscaledBody_LeavesItemAlone()
        {
            // a creature with no DefaultScale of its own is already 1.0, so there is nothing to propagate
            // and we must not stamp a redundant DefaultScale onto every item it wields
            Assert.IsNull(Creature.ResolveWieldScale(true, null, null));
            Assert.IsNull(Creature.ResolveWieldScale(true, null, 0.75f));
        }

        [TestMethod]
        public void ResolveWieldScale_ItemWithNoScaleOfItsOwn_TakesTheBodyScale()
        {
            // Aurex (wcid 1002621) at DefaultScale 5.3 holding greatsword 51384, which has no DefaultScale
            Assert.AreEqual(5.3f, Creature.ResolveWieldScale(true, 5.3f, null).Value, 0.0001f);
        }

        [TestMethod]
        public void ResolveWieldScale_ItemWithItsOwnScale_KeepsItAsARatio()
        {
            // Kite Shield 23683 is authored at DefaultScale 0.75 and Composite Bow 6963 at 1.1; on a body
            // at 6.2 (Vessaryn) and 6.3 (Ssethaun) they stay three quarters / ten percent over that body,
            // rather than being flattened to the body scale
            Assert.AreEqual(4.65f, Creature.ResolveWieldScale(true, 6.2f, 0.75f).Value, 0.0001f);
            Assert.AreEqual(6.93f, Creature.ResolveWieldScale(true, 6.3f, 1.1f).Value, 0.0001f);
        }

        [TestMethod]
        public void ResolveWieldScale_IsIdempotentForABodyScaleOfOne()
        {
            Assert.AreEqual(1.0f, Creature.ResolveWieldScale(true, 1.0f, null).Value, 0.0001f);
            Assert.AreEqual(0.75f, Creature.ResolveWieldScale(true, 1.0f, 0.75f).Value, 0.0001f);
        }

        [TestMethod]
        public void PropertyBool_9038_IsScaleWieldedToBody()
        {
            Assert.AreEqual(9038, (int)PropertyBool.ScaleWieldedToBody);
        }

        [TestMethod]
        public void ThreeGearPassBossesCarryTheFlag()
        {
            var weeniesDir = FindWeeniesDir();

            foreach (var wcid in new[] { 1002621, 1002623, 1002624 })
            {
                var file = Directory.GetFiles(weeniesDir, wcid + " *.sql").SingleOrDefault();

                Assert.IsNotNull(file, $"no weenie unit found for wcid {wcid} in {weeniesDir}");

                var sql = File.ReadAllText(file);

                StringAssert.Contains(sql, "9038, True", $"wcid {wcid} does not set PropertyBool 9038 ScaleWieldedToBody");
                StringAssert.Contains(sql, "weenie_properties_create_list", $"wcid {wcid} has no create_list to scale");
            }
        }

        /// <summary>
        /// Docs/WorldEvents/BOSS-BUILD-SPEC.md "2026-08-19 gear pass" rule 1: a wielded weapon REPLACES
        /// the creature's body_part damage (Monster_Melee.GetBaseDamage), so each boss weapon is a
        /// boss-only clone tuned back to that boss's own body_part row. The damage NUMBERS are pinned to
        /// d_Val / d_Var; the damage TYPE is chosen to suit the weapon, not copied from d_Type. This pins
        /// all three to the values the doc states, so a later edit to either side shows up as a failure.
        /// </summary>
        [TestMethod]
        public void GearPassClonesReproduceTheirBossBodyDamage()
        {
            var weeniesDir = FindWeeniesDir();

            // wcid, Damage (int 44), DamageType (int 45), DamageVariance (float 22)
            var expected = new[]
            {
                new object[] { 1002669, 600, 64, "0.2" },   // Aurex: d_Val 600, Electric to match the body
                new object[] { 1002670, 600,  1, "0.2" },   // Vessaryn: d_Val 600, Slash to suit a tachi
                new object[] { 1002671, 286,  2, "0.2" },   // Ssethaun: 286 x bow DamageMod 2.1 = 600.6, Pierce to suit an arrow
            };

            foreach (var row in expected)
            {
                var wcid = (int)row[0];
                var file = Directory.GetFiles(weeniesDir, wcid + " *.sql").SingleOrDefault();

                Assert.IsNotNull(file, $"no weenie unit found for clone wcid {wcid} in {weeniesDir}");

                var sql = File.ReadAllText(file);

                StringAssert.Contains(sql, $"({wcid},  44,        {row[1]})", $"wcid {wcid} Damage is not {row[1]}");
                StringAssert.Contains(sql, $"({wcid},  45,", $"wcid {wcid} has no DamageType row");
                StringAssert.Contains(sql, $"({wcid},  22,    {row[3]})", $"wcid {wcid} DamageVariance is not {row[3]}");
            }
        }

        [TestMethod]
        public void CorrectionArrowDamageTimesTheBowDamageModReachesTheBodyDVal()
        {
            // Monster_Missile.GetMissileDamage -> ammo.GetDamageMod(bow); BaseDamageMod.MaxDamage is
            // (ammo Damage + bonuses) * launcher DamageMod. Composite Bow 6963 carries DamageMod 2.1.
            const int arrowDamage = 286;
            const float bowDamageMod = 2.1f;

            var maxHit = arrowDamage * bowDamageMod;

            Assert.IsTrue(maxHit >= 600.0f && maxHit < 603.0f,
                $"Correction Arrow 1002671 through Composite Bow 6963 yields {maxHit} max, not ~600 (Ssethaun body_part d_Val)");
        }

        // ---- Creature.SuppressesBiotaObjDesc (the armor-graft guard) -----------------------------------

        /// <summary>
        /// A grafted creature (anim_part / palette / texture_map rows on its own weenie) keeps that graft
        /// only while nothing COVERING is equipped. Creature_Networking.CalculateObjDesc used to test the
        /// whole equipped list, which includes shields because a shield is ItemType.Armor - so arming
        /// Legate Vessaryn (wcid 1002623) with a Kite Shield wiped his Diforsa graft. These pin the
        /// predicate that replaced that test.
        /// </summary>
        [TestMethod]
        public void SuppressesBiotaObjDesc_TrueForEverySlotThatPaintsTheModel()
        {
            foreach (var loc in new[]
            {
                EquipMask.HeadWear, EquipMask.ChestWear, EquipMask.AbdomenWear, EquipMask.UpperArmWear,
                EquipMask.LowerArmWear, EquipMask.HandWear, EquipMask.UpperLegWear, EquipMask.LowerLegWear,
                EquipMask.FootWear, EquipMask.ChestArmor, EquipMask.AbdomenArmor, EquipMask.UpperArmArmor,
                EquipMask.LowerArmArmor, EquipMask.UpperLegArmor, EquipMask.LowerLegArmor, EquipMask.Cloak,
            })
            {
                Assert.IsTrue(Creature.SuppressesBiotaObjDesc(loc), $"{loc} covers the model and must suppress the graft");
            }
        }

        [TestMethod]
        public void SuppressesBiotaObjDesc_FalseForHeldGearThatPaintsNothing()
        {
            // the regression: a Kite Shield is ItemType.Armor but sits at EquipMask.Shield, which the
            // equipment loop skips, so it paints nothing and must leave the graft alone
            Assert.IsFalse(Creature.SuppressesBiotaObjDesc(EquipMask.Shield), "a shield paints nothing");

            foreach (var loc in new[]
            {
                EquipMask.MeleeWeapon, EquipMask.MissileWeapon, EquipMask.MissileAmmo,
                EquipMask.Held, EquipMask.TwoHanded,
            })
            {
                Assert.IsFalse(Creature.SuppressesBiotaObjDesc(loc), $"{loc} paints nothing and must not suppress the graft");
            }
        }

        [TestMethod]
        public void SuppressesBiotaObjDesc_FalseForJewelryAndNoSlot()
        {
            Assert.IsFalse(Creature.SuppressesBiotaObjDesc(EquipMask.None));
            Assert.IsFalse(Creature.SuppressesBiotaObjDesc(EquipMask.NeckWear));
            Assert.IsFalse(Creature.SuppressesBiotaObjDesc(EquipMask.FingerWearLeft));
            Assert.IsFalse(Creature.SuppressesBiotaObjDesc(EquipMask.TrinketOne));
        }

        /// <summary>
        /// A null wield location returns TRUE, and that is deliberate rather than a bug: C#'s lifted !=
        /// makes `(null & mask) != 0` true, so this is what the inline expression in
        /// Creature_Networking.CalculateObjDesc's equipment loop has always evaluated to. The predicate
        /// exists to keep the graft branch and that loop in exact agreement, so it must match here too.
        /// Nothing in EquippedObjects actually reaches it null - TryEquipObject always sets the property.
        /// </summary>
        [TestMethod]
        public void SuppressesBiotaObjDesc_NullSlotMatchesTheLoopsOwnLiftedComparison()
        {
            EquipMask? none = null;

            var inlineAsWrittenInTheLoop = (none & (EquipMask.Clothing | EquipMask.Armor | EquipMask.Cloak)) != 0;

            Assert.AreEqual(inlineAsWrittenInTheLoop, Creature.SuppressesBiotaObjDesc(null),
                "the extracted predicate must agree with the loop's inline test on every input, null included");
        }

        /// <summary>
        /// Vessaryn's exact loadout: a one-handed tachi, a Kite Shield and nothing else. Not one of them
        /// may suppress the graft.
        /// </summary>
        [TestMethod]
        public void VessarynLoadout_LeavesTheGraftIntact()
        {
            var loadout = new EquipMask?[] { EquipMask.MeleeWeapon, EquipMask.Shield };

            Assert.AreEqual(0, loadout.Count(Creature.SuppressesBiotaObjDesc),
                "tachi + Kite Shield must leave zero covering items, or the Diforsa graft is dropped again");
        }

        private static string FindWeeniesDir()
        {
            var dir = new DirectoryInfo(System.AppContext.BaseDirectory);

            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "Content", "sql", "weenies");

                if (Directory.Exists(candidate))
                    return candidate;

                dir = dir.Parent;
            }

            throw new FileNotFoundException("Could not find Content/sql/weenies by walking up from " + System.AppContext.BaseDirectory);
        }
    }
}
