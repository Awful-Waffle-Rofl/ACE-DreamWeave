using System;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Pvp.Rules;
using ACE.Server.Tests.KnotLoot;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// CLOAK MANTLE Proc Amplifier: a cloak carrying PropertyFloat.CloakProcAmplifier multiplies its existing proc's
    /// magnitude. Covers the flat damage reduction (Cloak.GetDamageReductionAmount), the self-buff / Shroud
    /// StatModValue (Cloak.ProcStatModValue and its refresh), and pins the war/void projectile hook by source scan.
    /// </summary>
    [TestClass]
    public class CloakProcAmplifierTests
    {
        private const uint CloakedInSkill = (uint)SpellId.CloakAllSkill;

        private Func<PvpRuleDials> savedDialSource;
        private Func<Player, Player, bool> savedArenaScope;

        [TestInitialize]
        public void Setup()
        {
            savedDialSource = PvpRuleTunables.DialSource;
            savedArenaScope = PvpClassifier.ArenaScopeSource;
        }

        [TestCleanup]
        public void Cleanup()
        {
            PvpRuleTunables.DialSource = savedDialSource;
            PvpClassifier.ArenaScopeSource = savedArenaScope;
        }

        private static WorldObject Cloak(double? amplifier, int? reductionStamp = null, uint procSpell = CloakedInSkill, double? procStamp = null)
        {
            var cloak = UpgradedCloakTestItems.Item(cloak: true);
            cloak.ProcSpell = procSpell;

            if (amplifier != null)
                cloak.SetProperty(PropertyFloat.CloakProcAmplifier, amplifier.Value);

            if (reductionStamp != null)
                cloak.SetProperty(PropertyInt.CloakDamageReductionAmount, reductionStamp.Value);

            if (procStamp != null)
                cloak.SetProperty(PropertyFloat.CloakProcStatModValue, procStamp.Value);

            return cloak;
        }

        private static Creature WearerOf(WorldObject cloak)
        {
            var wearer = TestCreatures.CreateDefender();
            wearer.EquippedObjects[cloak.Guid] = cloak;
            return wearer;
        }

        [TestMethod]
        public void DamageReduction_Amplified_Pve()
        {
            var wearer = WearerOf(Cloak(0.25));

            Assert.AreEqual(250, ACE.Server.Entity.Cloak.GetDamageReductionAmount(TestCreatures.CreateAttacker(), wearer), "200 x 1.25");
        }

        [TestMethod]
        public void DamageReduction_NoAmplifier_IsRetail()
        {
            var wearer = WearerOf(Cloak(null));

            Assert.AreEqual(200, ACE.Server.Entity.Cloak.GetDamageReductionAmount(TestCreatures.CreateAttacker(), wearer));
        }

        [TestMethod]
        public void DamageReduction_KnotStampStacks()
        {
            var wearer = WearerOf(Cloak(0.25, reductionStamp: 400));

            Assert.AreEqual(500, ACE.Server.Entity.Cloak.GetDamageReductionAmount(TestCreatures.CreateAttacker(), wearer), "400 x 1.25");
        }

        [TestMethod]
        public void DamageReduction_PvpSource_Unamplified()
        {
            // PvP rules master switch off: a PvP pair passes the base straight through, so the explicit pvp gate is
            // the only thing keeping the amplifier out of the result.
            var off = PvpRuleTunables.Defaults with { Enabled = false };
            PvpRuleTunables.DialSource = () => off;
            PvpClassifier.ArenaScopeSource = (x, y) => false;

            var amplified = PvpWearer(Cloak(0.25));
            var plain = PvpWearer(Cloak(null));

            var source = ACE.Server.Tests.Pvp.PvpTemplatePlayerTests.SeededPlayer();

            Assert.IsTrue(PvpClassifier.IsPvp(source, amplified), "control: the pair classifies as PvP");

            var withAmp = ACE.Server.Entity.Cloak.GetDamageReductionAmount(source, amplified);
            var without = ACE.Server.Entity.Cloak.GetDamageReductionAmount(source, plain);

            Assert.AreEqual(without, withAmp, "the amplifier does not reach a PvP hit");
            Assert.AreEqual(200, withAmp, "control: the base passed through the switched-off path unchanged");
        }

        private static Player PvpWearer(WorldObject cloak)
        {
            var player = ACE.Server.Tests.Pvp.PvpTemplatePlayerTests.SeededPlayer();
            player.EquippedObjects[cloak.Guid] = cloak;
            return player;
        }

        [TestMethod]
        public void ProcStatMod_CloakedInSkill_Amplified()
            => Assert.AreEqual(25f, ACE.Server.Entity.Cloak.ProcStatModValue(Cloak(0.25), CloakedInSkill, 20f));

        [TestMethod]
        public void ProcStatMod_StampedAndAmplified_Multiplies()
            => Assert.AreEqual(50f, ACE.Server.Entity.Cloak.ProcStatModValue(Cloak(0.25, procStamp: 40), CloakedInSkill, 20f), "40 x 1.25");

        [TestMethod]
        public void ProcStatMod_NoAmplifier_Unchanged()
            => Assert.AreEqual(20f, ACE.Server.Entity.Cloak.ProcStatModValue(Cloak(null), CloakedInSkill, 20f));

        [TestMethod]
        public void Refresh_CloakedInSkill_KeepsAmplified()
        {
            var cloak = Cloak(0.25);

            Assert.AreEqual(25f, ACE.Server.Entity.Cloak.RefreshedProcStatModValue(cloak, CloakedInSkill, 25f, 20f), "refresh of +20 base on a 0.25 cloak is 25");
            Assert.AreEqual(25f, ACE.Server.Entity.Cloak.RefreshedProcStatModValue(cloak, CloakedInSkill, 20f, 20f), "an unamplified existing entry rises to 25");

            // feed the refreshed result back in as the existing value: it must stay 25, never compound
            var refreshed = ACE.Server.Entity.Cloak.RefreshedProcStatModValue(cloak, CloakedInSkill, 20f, 20f);
            Assert.AreEqual(25f, ACE.Server.Entity.Cloak.RefreshedProcStatModValue(cloak, CloakedInSkill, refreshed, 20f), "a second refresh does not compound");
        }

        [TestMethod]
        public void ProcStatMod_Shroud_PvpTarget_Unamplified()
        {
            var shroud = (uint)SpellId.CloakMagicDLower;
            var cloak = Cloak(0.25, procSpell: shroud);

            Assert.AreEqual(30f, ACE.Server.Entity.Cloak.ProcStatModValue(cloak, shroud, 30f, pvp: true), "a Shroud landing on another player");
            Assert.AreEqual(37.5f, ACE.Server.Entity.Cloak.ProcStatModValue(cloak, shroud, 30f, pvp: false), "control: the same cloak amplifies in PvE");

            // the real Shroud is MULTIPLICATIVE: PvP still gets factor 1, so the value is untouched
            Assert.AreEqual(0.5f, ACE.Server.Entity.Cloak.ProcStatModValue(Cloak(1.0, procSpell: shroud), shroud, 0.5f, pvp: true, statModType: ShroudType));
        }

        // Shroud of Darkness (SpellId.CloakMagicDLower, 5754-5756): a 0.5 multiplicative skill debuff
        private const EnchantmentTypeFlags ShroudType = EnchantmentTypeFlags.Multiplicative | EnchantmentTypeFlags.Skill;

        [TestMethod]
        public void ProcStatMod_MultiplicativeShroud_RaisesTheValueToTheFactor_NeverAboveBase()
        {
            var shroud = (uint)SpellId.CloakMagicDLower;

            Assert.AreEqual(0.25f, ACE.Server.Entity.Cloak.ProcStatModValue(Cloak(1.0, procSpell: shroud), shroud, 0.5f, statModType: ShroudType), 1e-6f, "0.5 ^ 2 at +100%");
            Assert.AreEqual(0.125f, ACE.Server.Entity.Cloak.ProcStatModValue(Cloak(2.0, procSpell: shroud), shroud, 0.5f, statModType: ShroudType), 1e-6f, "0.5 ^ 3 at +200%");

            for (var amp = 0.01; amp <= 5.0; amp += 0.07)
                Assert.IsTrue(ACE.Server.Entity.Cloak.ProcStatModValue(Cloak(amp, procSpell: shroud), shroud, 0.5f, statModType: ShroudType) <= 0.5f, $"a debuff must never weaken or turn into a buff, amp {amp}");

            // control: the additive rule applied to the same 0.5 would give 1.0 (a no-op) and 1.5 (a buff)
            Assert.AreEqual(1.0f, ACE.Server.Entity.Cloak.ProcStatModValue(Cloak(1.0, procSpell: shroud), shroud, 0.5f), 1e-6f, "no stat-mod type passed = additive rule");
        }

        [TestMethod]
        public void ProcStatMod_MultiplicativeAboveOne_UsesTheSamePower()
            => Assert.AreEqual(9f, ACE.Server.Entity.Cloak.ProcStatModValue(Cloak(1.0, procSpell: (uint)SpellId.CloakMagicDLower), (uint)SpellId.CloakMagicDLower, 3f, statModType: EnchantmentTypeFlags.Multiplicative), 1e-4f, "3 ^ 2 (not 3 x 2)");

        [TestMethod]
        public void AmplifyStatMod_Guards_ReturnTheValueUnchanged()
        {
            var m = EnchantmentTypeFlags.Multiplicative;

            Assert.AreEqual(0f, ACE.Server.Entity.Cloak.AmplifyStatMod(0f, 2.0, m));
            Assert.AreEqual(-0.5f, ACE.Server.Entity.Cloak.AmplifyStatMod(-0.5f, 2.0, m));
            Assert.IsTrue(float.IsNaN(ACE.Server.Entity.Cloak.AmplifyStatMod(float.NaN, 2.0, m)));
            Assert.IsTrue(float.IsPositiveInfinity(ACE.Server.Entity.Cloak.AmplifyStatMod(float.PositiveInfinity, 2.0, m)));
            Assert.AreEqual(0.5f, ACE.Server.Entity.Cloak.AmplifyStatMod(0.5f, 1.0, m), "factor 1 (PvP, no amplifier)");
            Assert.AreEqual(0.5f, ACE.Server.Entity.Cloak.AmplifyStatMod(0.5f, 0.5, m), "factor below 1");
            Assert.AreEqual(0.5f, ACE.Server.Entity.Cloak.AmplifyStatMod(0.5f, double.NaN, m));
        }

        [TestMethod]
        public void ProcStatMod_AdditiveCloakedInSkill_AtOneHundredPercent_Doubles()
        {
            Assert.AreEqual(40f, ACE.Server.Entity.Cloak.ProcStatModValue(Cloak(1.0), CloakedInSkill, 20f, statModType: EnchantmentTypeFlags.Skill), "additive 20 x 2");
            Assert.AreEqual(60f, ACE.Server.Entity.Cloak.ProcStatModValue(Cloak(2.0), CloakedInSkill, 20f, statModType: EnchantmentTypeFlags.Skill), "additive 20 x 3");
        }

        [TestMethod]
        public void Refresh_UsesTheSameRuleAsBuild_NeverResetsNorDoubleApplies()
        {
            // additive (Cloaked in Skill): the refresh of a built 40 stays 40 whether it starts from 40 or from the raw 20
            var cloak = Cloak(1.0);
            Assert.AreEqual(40f, ACE.Server.Entity.Cloak.RefreshedProcStatModValue(cloak, CloakedInSkill, 40f, 20f, statModType: EnchantmentTypeFlags.Skill));
            Assert.AreEqual(40f, ACE.Server.Entity.Cloak.RefreshedProcStatModValue(cloak, CloakedInSkill, 20f, 20f, statModType: EnchantmentTypeFlags.Skill));

            // FORWARD-COMPATIBILITY PIN, not a production path: no multiplicative spell is a Cloaked in Skill refresh today
            // (the refresh guard recomputes only CloakAllSkill, which is additive). This synthetic case pins that, if one ever
            // is, the guard applies the same rule as the build and lands on the build value from any starting value.
            var multi = Cloak(2.0);
            var built = ACE.Server.Entity.Cloak.ProcStatModValue(multi, CloakedInSkill, 0.5f, statModType: ShroudType);
            Assert.AreEqual(0.125f, built, 1e-6f);
            Assert.AreEqual(built, ACE.Server.Entity.Cloak.RefreshedProcStatModValue(multi, CloakedInSkill, built, 0.5f, statModType: ShroudType), 1e-6f, "refresh of an amplified value does not compound");
            Assert.AreEqual(built, ACE.Server.Entity.Cloak.RefreshedProcStatModValue(multi, CloakedInSkill, 0.5f, 0.5f, statModType: ShroudType), 1e-6f, "refresh of an unamplified value rises to the same");

            // a real Shroud refresh (not Cloaked in Skill) keeps the value its build already amplified
            var shroud = (uint)SpellId.CloakMagicDLower;
            Assert.AreEqual(0.125f, ACE.Server.Entity.Cloak.RefreshedProcStatModValue(Cloak(2.0, procSpell: shroud), shroud, 0.125f, 0.5f, statModType: ShroudType));
        }

        [TestMethod]
        public void ProcStatMod_SelfBuff_PvpFalse_Amplified()
            => Assert.AreEqual(25f, ACE.Server.Entity.Cloak.ProcStatModValue(Cloak(0.25), CloakedInSkill, 20f, pvp: false), "a self-cast is never IsPvp, so it amplifies");

        [TestMethod]
        public void Refresh_PvpTarget_Unamplified()
            => Assert.AreEqual(20f, ACE.Server.Entity.Cloak.RefreshedProcStatModValue(Cloak(0.25), CloakedInSkill, 25f, 20f, pvp: true));

        [TestMethod]
        public void ProcStatMod_NotCloaksProcSpell_Unchanged()
        {
            var cloak = Cloak(0.25);

            Assert.AreEqual(10f, ACE.Server.Entity.Cloak.ProcStatModValue(cloak, 5697, 10f), "a spell that is not the cloak's ProcSpell");

            var amulet = UpgradedCloakTestItems.Item(cloak: false);
            amulet.ProcSpell = CloakedInSkill;
            amulet.SetProperty(PropertyFloat.CloakProcAmplifier, 0.25);

            Assert.IsFalse(ACE.Server.Entity.Cloak.IsCloak(amulet), "control: the fixture is not a cloak");
            Assert.AreEqual(20f, ACE.Server.Entity.Cloak.ProcStatModValue(amulet, CloakedInSkill, 20f), "a non-cloak carrying the property");
            Assert.AreEqual(20f, ACE.Server.Entity.Cloak.ProcStatModValue(null, CloakedInSkill, 20f), "null weapon");
        }

        [TestMethod]
        public void SpellProjectile_AmplifiesTheProcDamageRightAfterProcDamageScale()
        {
            var text = CloakProcMagnitudeTests.Source("Source/ACE.Server/WorldObjects/SpellProjectile.cs");

            StringAssert.Matches(text,
                new Regex(@"finalDamage \*= ProcDamageScale;\n\s*if \(FromProc && weapon != null && Cloak\.IsCloak\(weapon\) && weapon\.ProcSpell == Spell\.Id\) finalDamage \*= \(float\)CloakMantleMath\.AmplifierFactor\(weapon, isPVP\);"),
                "the war/void hook must follow the ProcDamageScale line directly");
        }
    }
}
