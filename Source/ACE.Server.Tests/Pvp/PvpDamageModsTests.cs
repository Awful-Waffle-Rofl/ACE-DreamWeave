using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.Pvp.Rules;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// The PvP damage mods (Docs/Pvp/DESIGN.md "PvP rules (levers)"): pvp_war_magic_damage_mod,
    /// pvp_melee_damage_mod, pvp_missile_damage_mod and pvp_crit_damage_mod at M1/M2, and pvp_magic_absorb_mod at
    /// AB1. Exercised through PvpRules' pure functions and choke-point entries on a PvP pair of reflection-seeded
    /// Players. Which call site passes which point, kind and crit flag - and that M1/M2 precede the cap and AB1
    /// follows the retail 0.72 - is a source-order placement check in
    /// PvpDamageCapTests.ChokePointCallSites_ArePinned_AndPrecedeTheCloakProc, because a real damage computation
    /// reads Spell.School / MinDamage from the client dat, which this test host does not have.
    ///
    /// SEEDING: no PropertyManager key is read - the dials come from a swapped PvpRuleTunables.DialSource. Every
    /// seam (DialSource, Observer, PvpClassifier.ArenaScopeSource) is saved and restored per test.
    /// </summary>
    [TestClass]
    public class PvpDamageModsTests
    {
        private Func<PvpRuleDials> savedDialSource;
        private Action<PvpChokePoint, double, double> savedObserver;
        private Func<Player, Player, bool> savedArenaScope;

        private List<(PvpChokePoint Point, double Before, double After)> observed;

        [TestInitialize]
        public void Setup()
        {
            savedDialSource = PvpRuleTunables.DialSource;
            savedObserver = PvpRules.Observer;
            savedArenaScope = PvpClassifier.ArenaScopeSource;

            observed = new List<(PvpChokePoint, double, double)>();

            PvpRules.Observer = (p, b, a) => observed.Add((p, b, a));
            PvpClassifier.ArenaScopeSource = PvpClassifier.DefaultArenaScope;
            UseDials(PvpRuleTunables.Defaults);
        }

        [TestCleanup]
        public void Cleanup()
        {
            PvpRuleTunables.DialSource = savedDialSource;
            PvpRules.Observer = savedObserver;
            PvpClassifier.ArenaScopeSource = savedArenaScope;
        }

        // ================= fixtures =================

        private static PvpRuleDials Mods(double war = 1.0, double melee = 1.0, double missile = 1.0, double crit = 1.0, double absorb = 1.0, bool enabled = true) =>
            PvpRuleTunables.Defaults with
            {
                Enabled = enabled,
                WarMagicDamageMod = war,
                MeleeDamageMod = melee,
                MissileDamageMod = missile,
                CritDamageMod = crit,
                MagicAbsorbMod = absorb,
            };

        private static void UseDials(PvpRuleDials dials) => PvpRuleTunables.DialSource = () => dials;

        private static Player SeededPlayer()
        {
            var player = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));

            SetInherited(player, "<Biota>k__BackingField", new ACE.Entity.Models.Biota());
            SetInherited(player, "BiotaDatabaseLock", new ReaderWriterLockSlim());
            player.PlayerKillerStatus = PlayerKillerStatus.PK;

            return player;
        }

        private static void SetInherited(WorldObject wo, string name, object value)
        {
            var field = typeof(WorldObject).GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(field, $"WorldObject.{name} was not found by reflection - has it been renamed?");
            field.SetValue(wo, value);
        }

        private static float Hit(PvpChokePoint point, PvpDamageKind kind, bool crit, float hit = 800.0f)
            => PvpRules.ApplyDamageMods(point, SeededPlayer(), SeededPlayer(), hit, kind, crit, default);

        // ================= pure: SanitizeMod =================

        [TestMethod]
        public void SanitizeMod_BadValuesAreTheIdentity()
        {
            Assert.AreEqual(1.0, PvpRules.SanitizeMod(double.NaN));
            Assert.AreEqual(1.0, PvpRules.SanitizeMod(double.PositiveInfinity));
            Assert.AreEqual(1.0, PvpRules.SanitizeMod(double.NegativeInfinity));
            Assert.AreEqual(1.0, PvpRules.SanitizeMod(-0.5));
            Assert.AreEqual(0.0, PvpRules.SanitizeMod(0.0), "0 is a legal value: it zeroes the hit");
            Assert.AreEqual(0.5, PvpRules.SanitizeMod(0.5));
            Assert.AreEqual(2.5, PvpRules.SanitizeMod(2.5));
        }

        // ================= pure: kinds =================

        [TestMethod]
        public void WeaponDamageKind_MapsMeleeAndMissileOnly()
        {
            Assert.AreEqual(PvpDamageKind.Melee, PvpRules.WeaponDamageKind(CombatType.Melee));
            Assert.AreEqual(PvpDamageKind.Missile, PvpRules.WeaponDamageKind(CombatType.Missile));
            Assert.AreEqual(PvpDamageKind.Other, PvpRules.WeaponDamageKind(CombatType.Magic));
            Assert.AreEqual(PvpDamageKind.Other, PvpRules.WeaponDamageKind((CombatType)99));
        }

        [TestMethod]
        public void ProjectileDamageKind_OnlyWarMagicIsWar()
        {
            Assert.AreEqual(PvpDamageKind.WarMagic, PvpRules.ProjectileDamageKind(MagicSchool.WarMagic));
            Assert.AreEqual(PvpDamageKind.Other, PvpRules.ProjectileDamageKind(MagicSchool.VoidMagic), "void is covered by void_pvp_modifier");
            Assert.AreEqual(PvpDamageKind.Other, PvpRules.ProjectileDamageKind(MagicSchool.LifeMagic), "life projectiles are never war");
        }

        // ================= pure: multiplier =================

        [TestMethod]
        public void DamageModMultiplier_PicksTheKindsMod_AndMultipliesTheCritMod()
        {
            var dials = Mods(war: 2, melee: 3, missile: 5, crit: 7);

            Assert.AreEqual(2.0, PvpRules.DamageModMultiplier(dials, PvpDamageKind.WarMagic, false));
            Assert.AreEqual(3.0, PvpRules.DamageModMultiplier(dials, PvpDamageKind.Melee, false));
            Assert.AreEqual(5.0, PvpRules.DamageModMultiplier(dials, PvpDamageKind.Missile, false));
            Assert.AreEqual(1.0, PvpRules.DamageModMultiplier(dials, PvpDamageKind.Other, false));

            Assert.AreEqual(14.0, PvpRules.DamageModMultiplier(dials, PvpDamageKind.WarMagic, true));
            Assert.AreEqual(21.0, PvpRules.DamageModMultiplier(dials, PvpDamageKind.Melee, true));
            Assert.AreEqual(35.0, PvpRules.DamageModMultiplier(dials, PvpDamageKind.Missile, true));
            Assert.AreEqual(7.0, PvpRules.DamageModMultiplier(dials, PvpDamageKind.Other, true), "a void or life crit gets the crit mod only");
        }

        [TestMethod]
        public void DamageModMultiplier_BadValuesAreTheIdentity()
        {
            foreach (var bad in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1.0 })
            {
                var dials = Mods(war: bad, melee: bad, missile: bad, crit: bad);

                foreach (PvpDamageKind kind in Enum.GetValues(typeof(PvpDamageKind)))
                {
                    Assert.AreEqual(1.0, PvpRules.DamageModMultiplier(dials, kind, false), $"{kind} with {bad}");
                    Assert.AreEqual(1.0, PvpRules.DamageModMultiplier(dials, kind, true), $"{kind} crit with {bad}");
                }
            }
        }

        // ================= choke-point entry: each lever scales only its own hits =================

        [TestMethod]
        public void MeleeMod_ChangesMeleeOnly()
        {
            UseDials(Mods(melee: 0.5));

            Assert.AreEqual(400.0f, Hit(PvpChokePoint.M1, PvpDamageKind.Melee, false));
            Assert.AreEqual(800.0f, Hit(PvpChokePoint.M1, PvpDamageKind.Missile, false), "missile must not take the melee mod");
            Assert.AreEqual(800.0f, Hit(PvpChokePoint.M2, PvpDamageKind.WarMagic, false), "war must not take the melee mod");
            Assert.AreEqual(800.0f, Hit(PvpChokePoint.M2, PvpDamageKind.Other, false), "void/life must not take the melee mod");
        }

        [TestMethod]
        public void MissileMod_ChangesMissileOnly()
        {
            UseDials(Mods(missile: 0.5));

            Assert.AreEqual(400.0f, Hit(PvpChokePoint.M1, PvpDamageKind.Missile, false));
            Assert.AreEqual(800.0f, Hit(PvpChokePoint.M1, PvpDamageKind.Melee, false), "melee must not take the missile mod");
            Assert.AreEqual(800.0f, Hit(PvpChokePoint.M2, PvpDamageKind.WarMagic, false));
            Assert.AreEqual(800.0f, Hit(PvpChokePoint.M2, PvpDamageKind.Other, false));
        }

        /// <summary>The owner's live check in miniature: pvp_war_magic_damage_mod 0.5 halves a PK war bolt and leaves void and life alone.</summary>
        [TestMethod]
        public void WarMod_ChangesWarOnly_NotVoidOrLife()
        {
            UseDials(Mods(war: 0.5));

            Assert.AreEqual(400.0f, Hit(PvpChokePoint.M2, PvpRules.ProjectileDamageKind(MagicSchool.WarMagic), false));
            Assert.AreEqual(800.0f, Hit(PvpChokePoint.M2, PvpRules.ProjectileDamageKind(MagicSchool.VoidMagic), false), "void must not take the war mod");
            Assert.AreEqual(800.0f, Hit(PvpChokePoint.M2, PvpRules.ProjectileDamageKind(MagicSchool.LifeMagic), false), "life must not take the war mod");
            Assert.AreEqual(800.0f, Hit(PvpChokePoint.M1, PvpDamageKind.Melee, false));
            Assert.AreEqual(800.0f, Hit(PvpChokePoint.M1, PvpDamageKind.Missile, false));
        }

        [TestMethod]
        public void CritMod_ChangesCritsOnly_OfEveryKind()
        {
            UseDials(Mods(crit: 2.0));

            foreach (PvpDamageKind kind in Enum.GetValues(typeof(PvpDamageKind)))
            {
                Assert.AreEqual(800.0f, Hit(PvpChokePoint.M1, kind, false), $"{kind}: a non-crit must not take the crit mod");
                Assert.AreEqual(1600.0f, Hit(PvpChokePoint.M1, kind, true), $"{kind}: a crit takes the crit mod on the WHOLE hit");
            }
        }

        [TestMethod]
        public void KindAndCritMods_Multiply()
        {
            UseDials(Mods(war: 0.5, crit: 3.0));

            Assert.AreEqual(1200.0f, Hit(PvpChokePoint.M2, PvpDamageKind.WarMagic, true));
        }

        [TestMethod]
        public void BadValues_AtTheEntry_LeaveTheHitUnchanged_AndReportNothing()
        {
            foreach (var bad in new[] { double.NaN, double.PositiveInfinity, -2.0 })
            {
                UseDials(Mods(war: bad, melee: bad, missile: bad, crit: bad));

                foreach (PvpDamageKind kind in Enum.GetValues(typeof(PvpDamageKind)))
                    Assert.AreEqual(800.0f, Hit(PvpChokePoint.M1, kind, true), $"{kind} with {bad}");
            }

            Assert.AreEqual(0, observed.Count);
        }

        [TestMethod]
        public void ZeroMod_ZeroesTheHit()
        {
            UseDials(Mods(melee: 0.0));

            Assert.AreEqual(0.0f, Hit(PvpChokePoint.M1, PvpDamageKind.Melee, false));
        }

        // ================= choke-point entry: reporting, gating, read timing =================

        [TestMethod]
        public void Report_OnlyWhenChanged_AndNamesThePoint()
        {
            Hit(PvpChokePoint.M1, PvpDamageKind.Melee, true);
            Hit(PvpChokePoint.M2, PvpDamageKind.WarMagic, true);
            Assert.AreEqual(0, observed.Count, "the identity defaults report nothing");

            UseDials(Mods(war: 0.5));
            Hit(PvpChokePoint.M2, PvpDamageKind.WarMagic, false);

            Assert.AreEqual(1, observed.Count);
            Assert.AreEqual(PvpChokePoint.M2, observed[0].Point);
            Assert.AreEqual(800.0, observed[0].Before, 1e-9);
            Assert.AreEqual(400.0, observed[0].After, 1e-9);
        }

        [TestMethod]
        public void Defaults_AreTheIdentity_ForEveryKindAndCrit()
        {
            foreach (PvpDamageKind kind in Enum.GetValues(typeof(PvpDamageKind)))
            {
                foreach (var crit in new[] { false, true })
                {
                    Assert.AreEqual(800.0f, Hit(PvpChokePoint.M1, kind, crit));
                    Assert.AreEqual(123.456f, Hit(PvpChokePoint.M2, kind, crit, 123.456f));
                }
            }

            Assert.AreEqual(0.64f, PvpRules.ApplyMagicAbsorbMod(SeededPlayer(), SeededPlayer(), 0.64f));
            Assert.AreEqual(0, observed.Count);
        }

        [TestMethod]
        public void MasterSwitchOff_IsUnchanged()
        {
            UseDials(Mods(war: 0.1, melee: 0.1, missile: 0.1, crit: 0.1, absorb: 5.0, enabled: false));

            foreach (PvpDamageKind kind in Enum.GetValues(typeof(PvpDamageKind)))
                Assert.AreEqual(800.0f, Hit(PvpChokePoint.M1, kind, true), kind.ToString());

            Assert.AreEqual(0.64f, PvpRules.ApplyMagicAbsorbMod(SeededPlayer(), SeededPlayer(), 0.64f));
            Assert.AreEqual(0, observed.Count);
        }

        [TestMethod]
        public void SelfHit_IsUnchanged()
        {
            UseDials(Mods(war: 0.1, melee: 0.1, missile: 0.1, crit: 0.1, absorb: 5.0));

            var player = SeededPlayer();

            Assert.AreEqual(800.0f, PvpRules.ApplyDamageMods(PvpChokePoint.M1, player, player, 800.0f, PvpDamageKind.Melee, true, default));
            Assert.AreEqual(800.0f, PvpRules.ApplyDamageMods(PvpChokePoint.M2, player, player, 800.0f, PvpDamageKind.WarMagic, true, default));
            Assert.AreEqual(0.64f, PvpRules.ApplyMagicAbsorbMod(player, player, 0.64f));
        }

        /// <summary>Read per call, never cached: a changed setting takes effect on the very next hit.</summary>
        [TestMethod]
        public void Dials_AreReadPerCall()
        {
            UseDials(Mods(melee: 0.5));
            Assert.AreEqual(400.0f, Hit(PvpChokePoint.M1, PvpDamageKind.Melee, false));

            UseDials(Mods(melee: 2.0));
            Assert.AreEqual(1600.0f, Hit(PvpChokePoint.M1, PvpDamageKind.Melee, false));
        }

        // ================= magic absorb mod (AB1) =================

        [TestMethod]
        public void ScaleAbsorb_Pure()
        {
            // a 0.36 reduction (absorbMod 0.64)
            Assert.AreEqual(0.64, PvpRules.ScaleAbsorb(0.64, 1.0), 1e-12, "1.0 is the identity");
            Assert.AreEqual(1.0, PvpRules.ScaleAbsorb(0.64, 0.0), 1e-12, "0: absorption does nothing");
            Assert.AreEqual(0.28, PvpRules.ScaleAbsorb(0.64, 2.0), 1e-12, "2: twice the reduction");
            Assert.AreEqual(0.82, PvpRules.ScaleAbsorb(0.64, 0.5), 1e-12, "0.5: half the reduction");
            Assert.AreEqual(0.0, PvpRules.ScaleAbsorb(0.64, 10.0), 1e-12, "capped at absorbing 100%");
            Assert.AreEqual(1.0, PvpRules.ScaleAbsorb(1.0, 10.0), 1e-12, "nothing absorbing stays nothing absorbing");

            foreach (var bad in new[] { double.NaN, double.PositiveInfinity, -1.0 })
                Assert.AreEqual(0.64, PvpRules.ScaleAbsorb(0.64, bad), 1e-12, $"{bad} is the identity");
        }

        [TestMethod]
        public void ScaleAbsorb_IdentityIsBitExact()
        {
            // 1 - (1 - x) is not always x in floating point, so the identity must short-circuit
            foreach (var x in new[] { 0.1, 0.3333333, 0.64, 0.7777777, 0.999 })
                Assert.AreEqual(BitConverter.DoubleToInt64Bits(x), BitConverter.DoubleToInt64Bits(PvpRules.ScaleAbsorb(x, 1.0)), x.ToString());
        }

        /// <summary>
        /// The absorb mod applies AFTER the retail PvP 0.72: a shield absorbing 50% (absorbMod 0.5) is 72% effective
        /// in PvP (0.5 x 0.72 = 0.36 reduction, absorbMod 0.64), and m = 2 then doubles THAT reduction to 0.72
        /// (absorbMod 0.28). Applied before the 0.72 it would double 0.5 to 1.0 and the 0.72 would leave 0.28
        /// reduction (absorbMod 0.72) - a different number. The site order itself is pinned in PvpDamageCapTests.
        /// </summary>
        [TestMethod]
        public void AbsorbMod_AppliesAfterTheRetail072()
        {
            UseDials(Mods(absorb: 2.0));

            var absorbMod = 0.5f;

            // the retail block at SpellProjectile.CalculateDamage, verbatim
            absorbMod = 1 - absorbMod;
            absorbMod *= 0.72f;
            absorbMod = 1 - absorbMod;

            var result = PvpRules.ApplyMagicAbsorbMod(SeededPlayer(), SeededPlayer(), absorbMod);

            Assert.AreEqual(0.28f, result, 1e-5f);
            Assert.AreNotEqual(0.72f, result, 1e-3f, "the before-0.72 order would give 0.72");

            Assert.AreEqual(1, observed.Count);
            Assert.AreEqual(PvpChokePoint.AB1, observed[0].Point);
        }

        [TestMethod]
        public void AbsorbMod_NothingAbsorbing_IsUnchanged_AndReadsNothing()
        {
            var reads = 0;
            var dials = Mods(absorb: 5.0);
            PvpRuleTunables.DialSource = () => { reads++; return dials; };

            Assert.AreEqual(1.0f, PvpRules.ApplyMagicAbsorbMod(SeededPlayer(), SeededPlayer(), 1.0f));
            Assert.AreEqual(0, reads, "no shield, launcher or wand absorbing: nothing to scale, no read");
            Assert.AreEqual(0, observed.Count);
        }
    }
}
