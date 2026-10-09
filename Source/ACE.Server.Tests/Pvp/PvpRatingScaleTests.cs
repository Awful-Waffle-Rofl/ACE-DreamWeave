using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Pvp.Rules;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// The L2 PvP rating-scale lever (Docs/Pvp/DESIGN.md "PvP rules (levers)"): on a PvP direct hit the summed
    /// damage / damage resist ratings are scaled by pvp_damage_rating_scale and the crit damage / crit damage
    /// resist ratings by pvp_crit_damage_rating_scale, as (int)Math.Round(rating * scale) on the SIGNED rating,
    /// before the rating becomes a mod. Choke points R1 (DamageEvent: melee / missile) and R2 (SpellProjectile).
    ///
    /// A real Player cannot be constructed without the world database, so the PvP half runs through the lever's
    /// own entry (PvpRatingScales.Resolve with seeded Players) and the Creature DRR overload, and which call
    /// site passes which point - and that every rating read at R1/R2 goes through the scaler while the PK ratings
    /// do not - is pinned by a source-text test (CODE only, comments stripped). The PvE half runs the real
    /// DamageEvent for a creature pair carrying non-zero ratings at extreme scales.
    ///
    /// SEEDING: DamageEvent and GetDamageResistRatingMod read server tunables (allow_negative_rating_curve and
    /// others), so ClassInitialize seeds every registered default through DefaultPropertyManager.LoadDefaultProperties()
    /// exactly as PvpRulesPveInvarianceTests does (defaults only - nothing to restore). The lever seams (DialSource,
    /// Observer, the arena seam) are saved in TestInitialize and restored in TestCleanup, which MSTest runs in a
    /// finally around every test.
    /// </summary>
    [TestClass]
    public class PvpRatingScaleTests
    {
        private Func<PvpRuleDials> savedDialSource;
        private Action<PvpChokePoint, double, double> savedObserver;
        private Func<Player, Player, bool> savedArenaScope;

        private List<(PvpChokePoint Point, double Before, double After)> observed;
        private int dialReads;

        private static readonly PvpChokePoint[] RatingPoints = { PvpChokePoint.R1, PvpChokePoint.R2 };

        [ClassInitialize]
        public static void ClassSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        [TestInitialize]
        public void Setup()
        {
            savedDialSource = PvpRuleTunables.DialSource;
            savedObserver = PvpRules.Observer;
            savedArenaScope = PvpClassifier.ArenaScopeSource;

            observed = new List<(PvpChokePoint, double, double)>();
            dialReads = 0;

            PvpRules.Observer = (p, b, a) => observed.Add((p, b, a));
            PvpClassifier.ArenaScopeSource = PvpClassifier.DefaultArenaScope;
            UseScales(0.5, 0.25);
        }

        [TestCleanup]
        public void Cleanup()
        {
            PvpRuleTunables.DialSource = savedDialSource;
            PvpRules.Observer = savedObserver;
            PvpClassifier.ArenaScopeSource = savedArenaScope;
        }

        // ================= fixtures =================

        private void UseScales(double damageScale, double critScale, bool enabled = true)
        {
            var dials = PvpRuleTunables.Defaults with { Enabled = enabled, DamageRatingScale = damageScale, CritDamageRatingScale = critScale };
            PvpRuleTunables.DialSource = () => { dialReads++; return dials; };
        }

        private static T Seeded<T>() where T : WorldObject
        {
            var wo = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

            SetInherited(wo, "<Biota>k__BackingField", new ACE.Entity.Models.Biota());
            SetInherited(wo, "BiotaDatabaseLock", new ReaderWriterLockSlim());

            return wo;
        }

        private static void SetInherited(WorldObject wo, string name, object value)
        {
            var field = typeof(WorldObject).GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(field, $"WorldObject.{name} was not found by reflection - has it been renamed?");
            field.SetValue(wo, value);
        }

        // ================= the pure scaler =================

        [TestMethod]
        public void ScaleRating_IsMathRoundOfTheSignedProduct()
        {
            Assert.AreEqual(10, PvpRatingScales.ScaleRating(20, 0.5));
            Assert.AreEqual(-10, PvpRatingScales.ScaleRating(-20, 0.5));
            Assert.AreEqual(40, PvpRatingScales.ScaleRating(20, 2.0));
            Assert.AreEqual(-40, PvpRatingScales.ScaleRating(-20, 2.0));
            Assert.AreEqual(0, PvpRatingScales.ScaleRating(20, 0.0));
            Assert.AreEqual(0, PvpRatingScales.ScaleRating(-20, 0.0));

            // Math.Round's default (to even) on the signed product, verbatim - symmetric about zero
            Assert.AreEqual(4, PvpRatingScales.ScaleRating(7, 0.5));      // 3.5 -> 4
            Assert.AreEqual(2, PvpRatingScales.ScaleRating(5, 0.5));      // 2.5 -> 2
            Assert.AreEqual(-4, PvpRatingScales.ScaleRating(-7, 0.5));
            Assert.AreEqual(-2, PvpRatingScales.ScaleRating(-5, 0.5));
        }

        [TestMethod]
        public void ScaleRating_OneIsTheIdentityForEveryInt()
        {
            foreach (var r in new[] { 0, 1, -1, 7, -7, 99, -99, 100, -100, 1 << 24, (1 << 24) + 1, -((1 << 24) + 1), int.MaxValue, int.MinValue })
                Assert.AreEqual(r, PvpRatingScales.ScaleRating(r, 1.0), $"scale 1.0 changed {r}");
        }

        [TestMethod]
        public void ScaleRating_NegativeRating_NeverFlipsToProtection()
        {
            foreach (var scale in new[] { 0.01, 0.1, 0.25, 0.5, 0.75, 0.99, 1.5, 3.0 })
            {
                for (var r = -1; r >= -300; r--)
                {
                    var scaled = PvpRatingScales.ScaleRating(r, scale);
                    Assert.IsTrue(scaled <= 0, $"{r} x {scale} became {scaled}: a net-negative rating turned positive");
                }
            }
        }

        [TestMethod]
        public void ScaleRating_Guards_BadScales()
        {
            Assert.AreEqual(20, PvpRatingScales.ScaleRating(20, double.NaN), "NaN leaves the rating unchanged");
            Assert.AreEqual(20, PvpRatingScales.ScaleRating(20, double.PositiveInfinity));
            Assert.AreEqual(0, PvpRatingScales.ScaleRating(20, -0.5), "a negative scale counts as 0 - it never inverts the sign");
            Assert.AreEqual(0, PvpRatingScales.ScaleRating(-20, -0.5));
            Assert.AreEqual(int.MaxValue, PvpRatingScales.ScaleRating(int.MaxValue, 10.0), "clamped, never overflowed");
            Assert.AreEqual(int.MinValue, PvpRatingScales.ScaleRating(int.MinValue, 10.0));
        }

        // ================= the lever on a PvP pair, at R1 and R2 =================

        [TestMethod]
        public void PvpPair_HalfScale_HalvesTheRatingContribution_AtR1AndR2_AndNamesThePoint()
        {
            foreach (var point in RatingPoints)
            {
                observed.Clear();

                var scales = PvpRatingScales.Resolve(point, Seeded<Player>(), Seeded<Player>());

                Assert.IsTrue(scales.Applied, $"{point}: a PvP pair with the switch on applies the lever");

                var scaledDamageRating = scales.ScaleDamageRating(20);
                Assert.AreEqual(10, scaledDamageRating, $"{point}: 0.5 x 20 damage rating");

                // the contribution a rating makes is (mod - 1); half the rating is half the contribution
                var fullContribution = Creature.GetPositiveRatingMod(20) - 1.0f;
                var scaledContribution = Creature.GetPositiveRatingMod(scaledDamageRating) - 1.0f;
                Assert.AreEqual(fullContribution * 0.5f, scaledContribution, 1e-6f, $"{point}: damage rating contribution halved");

                Assert.AreEqual(10, scales.ScaleCritDamageRating(40), $"{point}: 0.25 x 40 crit damage rating");

                Assert.AreEqual(2, observed.Count, $"{point}: one report per changed rating");
                Assert.AreEqual((point, 20.0, 10.0), observed[0]);
                Assert.AreEqual((point, 40.0, 10.0), observed[1]);
            }
        }

        [TestMethod]
        public void PvpPair_ReadsTheDialsOnce_PerResolve()
        {
            var scales = PvpRatingScales.Resolve(PvpChokePoint.R1, Seeded<Player>(), Seeded<Player>());

            scales.ScaleDamageRating(20);
            scales.ScaleCritDamageRating(20);
            scales.ScaleDamageRating(30);
            scales.ReportIfChanged(10, 5);

            Assert.AreEqual(1, dialReads, "one read per hit, however many ratings are scaled");
        }

        [TestMethod]
        public void PvpPair_UnchangedRating_IsNotReported()
        {
            UseScales(1.0, 1.0);

            var scales = PvpRatingScales.Resolve(PvpChokePoint.R2, Seeded<Player>(), Seeded<Player>());

            Assert.IsTrue(scales.Applied, "scale 1.0 still records that the lever ran");
            Assert.AreEqual(20, scales.ScaleDamageRating(20));
            Assert.AreEqual(-20, scales.ScaleCritDamageRating(-20));
            scales.ReportIfChanged(15, 15);

            UseScales(0.5, 0.5);
            var half = PvpRatingScales.Resolve(PvpChokePoint.R2, Seeded<Player>(), Seeded<Player>());
            Assert.AreEqual(0, half.ScaleDamageRating(0), "a zero rating is unchanged");

            Assert.AreEqual(0, observed.Count);
        }

        [TestMethod]
        public void PvpPair_ArenaScope_AppliesToo()
        {
            PvpClassifier.ArenaScopeSource = (x, y) => true;

            Assert.AreEqual(10, PvpRatingScales.Resolve(PvpChokePoint.R1, Seeded<Player>(), Seeded<Player>()).ScaleDamageRating(20));
        }

        [TestMethod]
        public void MasterSwitchOff_IsNeutral()
        {
            UseScales(0.0, 0.0, enabled: false);

            foreach (var point in RatingPoints)
            {
                var scales = PvpRatingScales.Resolve(point, Seeded<Player>(), Seeded<Player>());

                Assert.IsFalse(scales.Applied, point.ToString());
                Assert.AreEqual(20, scales.ScaleDamageRating(20));
                Assert.AreEqual(20, scales.ScaleCritDamageRating(20));
                Assert.AreEqual(1.0, scales.DamageRatingScale, "the DRR overload is handed the identity scale");
            }

            Assert.AreEqual(0, observed.Count);
        }

        // ================= negative DRR stays negative through the real Creature overload =================

        [TestMethod]
        public void NegativeDamageResistRating_StaysAVulnerability_WhenScaled()
        {
            var defender = TestCreatures.CreateDefender();
            defender.DamageResistRating = -20;

            var unscaledMod = defender.GetDamageResistRatingMod(CombatType.Melee, attacker: null);
            var identityMod = defender.GetDamageResistRatingMod(CombatType.Melee, true, null, 1.0, out var raw1, out var scaled1);
            var halfMod = defender.GetDamageResistRatingMod(CombatType.Melee, true, null, 0.5, out var raw, out var scaled);

            Assert.AreEqual(unscaledMod, identityMod, "the old overload is the new one at scale 1.0");
            Assert.AreEqual(-20, raw1);
            Assert.AreEqual(-20, scaled1);

            Assert.AreEqual(-20, raw);
            Assert.AreEqual(-10, scaled, "the net-negative DRR shrinks toward zero and stays negative");

            // the same curve flag GetDamageResistRatingMod reads (seeded from the registered defaults)
            var allowBug = PropertyManager.GetBool("allow_negative_rating_curve").Item;

            Assert.AreEqual(Creature.GetNegativeRatingMod(-20, allowBug), unscaledMod, 1e-6f);
            Assert.AreEqual(Creature.GetNegativeRatingMod(-10, allowBug), halfMod, 1e-6f);
            Assert.IsTrue(halfMod > 1.0f, $"a scaled negative DRR must still INCREASE damage taken (mod {halfMod}); a Math.Abs approach would give {Creature.GetNegativeRatingMod(10)}");
            Assert.IsTrue(halfMod < unscaledMod, "and by less than the unscaled vulnerability");
        }

        // ================= default struct, and the negative-resist ruling =================

        [TestMethod]
        public void DefaultStruct_ScalesAreTheIdentity()
        {
            var d = default(PvpRatingScales);

            Assert.IsFalse(d.Applied);
            Assert.AreEqual(1.0, d.DamageRatingScale, "default(PvpRatingScales) must hand the DRR overload 1.0, not 0.0");
            Assert.AreEqual(1.0, d.CritDamageRatingScale);
            Assert.AreEqual(1.0, PvpRatingScales.Neutral.DamageRatingScale);
            Assert.AreEqual(1.0, PvpRatingScales.Neutral.CritDamageRatingScale);
        }

        [TestMethod]
        public void ScaleResistRating_NetNegative_IsNeverDeepened()
        {
            Assert.AreEqual(-40, PvpRatingScales.ScaleResistRating(-40, 2.0), "a scale above 1 does not deepen a negative resist rating");
            Assert.AreEqual(-40, PvpRatingScales.ScaleResistRating(-40, 3.0));
            Assert.AreEqual(-20, PvpRatingScales.ScaleResistRating(-40, 0.5), "a scale below 1 still shrinks it");
            Assert.AreEqual(80, PvpRatingScales.ScaleResistRating(40, 2.0), "a positive resist rating scales as before");
            Assert.AreEqual(20, PvpRatingScales.ScaleResistRating(40, 0.5));
            Assert.AreEqual(0, PvpRatingScales.ScaleResistRating(0, 2.0));
        }

        [TestMethod]
        public void NegativeDamageResistRating_ScaleAboveOne_GivesTheUnscaledMod_ThroughTheOverload()
        {
            var defender = TestCreatures.CreateDefender();
            defender.DamageResistRating = -40;

            var unscaledMod = defender.GetDamageResistRatingMod(CombatType.Melee, attacker: null);

            var doubledMod = defender.GetDamageResistRatingMod(CombatType.Melee, true, null, 2.0, out var raw2, out var scaled2);
            Assert.AreEqual(-40, raw2);
            Assert.AreEqual(-40, scaled2, "-40 at scale 2.0 is not deepened to -80");
            Assert.AreEqual(unscaledMod, doubledMod, "the same mod as unscaled");

            defender.GetDamageResistRatingMod(CombatType.Melee, true, null, 0.5, out _, out var scaledHalf);
            Assert.AreEqual(-20, scaledHalf, "-40 at scale 0.5 still shrinks to -20");

            defender.DamageResistRating = 40;
            defender.GetDamageResistRatingMod(CombatType.Melee, true, null, 2.0, out _, out var scaledPositive);
            Assert.AreEqual(80, scaledPositive, "+40 at scale 2.0 still becomes +80");
        }

        [TestMethod]
        public void PvpPair_NegativeCritDamageResist_NotDeepened_AttackerRatingsStillScale()
        {
            UseScales(2.0, 2.0);

            var scales = PvpRatingScales.Resolve(PvpChokePoint.R1, Seeded<Player>(), Seeded<Player>());

            Assert.AreEqual(-40, scales.ScaleCritDamageResistRating(-40), "the defender's negative CDRR is not deepened");
            Assert.AreEqual(80, scales.ScaleCritDamageResistRating(40), "a positive CDRR scales");
            Assert.AreEqual(-80, scales.ScaleCritDamageRating(-40), "the attacker's crit damage rating scales as before, sign kept");
            Assert.AreEqual(-80, scales.ScaleDamageRating(-40), "the attacker's damage rating scales as before, sign kept");

            Assert.AreEqual(3, observed.Count, "the unchanged -40 CDRR is not reported");
        }

        [TestMethod]
        public void PositiveDamageResistRating_HalfScale_HalvesTheRating()
        {
            var defender = TestCreatures.CreateDefender();
            defender.DamageResistRating = 20;

            var mod = defender.GetDamageResistRatingMod(CombatType.Magic, true, null, 0.5, out var raw, out var scaled);

            Assert.AreEqual(20, raw);
            Assert.AreEqual(10, scaled);
            Assert.AreEqual(Creature.GetNegativeRatingMod(10, PropertyManager.GetBool("allow_negative_rating_curve").Item), mod, 1e-6f);
        }

        // ================= PvE: unchanged at extreme scales, and no lever read =================

        [TestMethod]
        public void NonPvpPairs_AreNeutral_AndReadNoLever_AtExtremeScales()
        {
            var creatureA = TestCreatures.CreateDefender();
            var creatureB = TestCreatures.CreateDefender();
            var player = Seeded<Player>();
            var pet = Seeded<CombatPet>();

            PvpClassifier.ArenaScopeSource = (x, y) => { throw new InvalidOperationException("the arena seam must not be read for a non-PvP pair"); };

            var pairs = new List<(string Name, WorldObject Source, Creature Target)>
            {
                ("creature -> creature", creatureA, creatureB),
                ("player -> creature", player, creatureA),
                ("creature -> player", creatureA, player),
                ("player -> self", player, player),
                ("combat pet -> player", pet, player),
                ("player -> combat pet", player, pet),
                ("null source", null, player),
                ("null target", player, null),
            };

            foreach (var extreme in new[] { 0.0, 10.0 })
            {
                UseScales(extreme, extreme);

                foreach (var (name, source, target) in pairs)
                {
                    foreach (var point in RatingPoints)
                    {
                        var scales = PvpRatingScales.Resolve(point, source, target);

                        Assert.IsFalse(scales.Applied, $"{name} at {point}");
                        Assert.AreEqual(1.0, scales.DamageRatingScale, $"{name} at {point}");
                        Assert.AreEqual(1.0, scales.CritDamageRatingScale, $"{name} at {point}");
                        Assert.AreEqual(37, scales.ScaleDamageRating(37), $"{name} at {point}");
                        Assert.AreEqual(-37, scales.ScaleCritDamageRating(-37), $"{name} at {point}");
                    }
                }
            }

            Assert.AreEqual(0, dialReads, "a non-PvP interaction read the PvP levers");
            Assert.AreEqual(0, observed.Count, "a non-PvP interaction reported a lever");
        }

        /// <summary>
        /// The real R1 site for a creature pair carrying non-zero ratings on all four channels: at rating scales of
        /// 0 and 10 every rating mod is exactly the unscaled one, no debug record is written, and no lever is read.
        /// Scale 0 would zero every rating (mods of exactly 1.0) if R1 ran, so this discriminates.
        /// </summary>
        [TestMethod]
        public void CreatureVsCreature_DamageEvent_RatingModsUnchanged_AtExtremeScales()
        {
            var attacker = TestCreatures.CreateAttacker(maxDamage: 10, variance: 0.5f, strength: 100);
            attacker.DamageRating = 30;
            attacker.CritDamageRating = 40;

            var defender = TestCreatures.CreateDefender(baseArmor: 200);
            defender.DamageResistRating = 20;
            defender.CritDamageResistRating = 10;

            var expectedDamageRatingBaseMod = Creature.GetPositiveRatingMod(30);
            var expectedCritDamageRatingMod = Creature.GetPositiveRatingMod(40);
            var expectedDrrBaseMod = Creature.GetNegativeRatingMod(20);
            var expectedCdrrMod = Creature.GetNegativeRatingMod(10);

            foreach (var extreme in new[] { 0.0, 10.0 })
            {
                UseScales(extreme, extreme);

                var sawCritical = false;
                var sawNonCritical = false;

                for (var i = 0; i < 500; i++)
                {
                    var damageEvent = DamageEvent.CalculateDamage(attacker, defender, null);

                    Assert.IsFalse(damageEvent.Evaded);
                    Assert.IsFalse(damageEvent.GeneralFailure);

                    Assert.AreEqual(expectedDamageRatingBaseMod, damageEvent.DamageRatingBaseMod, 1e-6f, $"scale {extreme}");
                    Assert.AreEqual(expectedDrrBaseMod, damageEvent.DamageResistanceRatingBaseMod, 1e-6f, $"scale {extreme}");
                    Assert.IsFalse(damageEvent.PvpRatingScalesApplied);
                    Assert.AreEqual(0.0f, damageEvent.PvpDamageRatingScale);
                    Assert.AreEqual(0.0f, damageEvent.PvpCritDamageRatingScale);

                    if (damageEvent.IsCritical)
                    {
                        sawCritical = true;
                        Assert.AreEqual(expectedCritDamageRatingMod, damageEvent.CriticalDamageRatingMod, 1e-6f, $"scale {extreme}");
                        Assert.AreEqual(expectedCdrrMod, damageEvent.CriticalDamageResistanceRatingMod, 1e-6f, $"scale {extreme}");
                    }
                    else
                        sawNonCritical = true;
                }

                Assert.IsTrue(sawCritical, "no critical hit in 500 attacks");
                Assert.IsTrue(sawNonCritical, "no normal hit in 500 attacks");
            }

            Assert.AreEqual(0, dialReads, "a creature-vs-creature DamageEvent read the PvP levers");
            Assert.AreEqual(0, observed.Count);
        }

        // ================= call-site pins =================

        /// <summary>
        /// R1 and R2 are each resolved at exactly one call site, before the first rating read there; every damage /
        /// crit damage / DRR / CDRR read at that site goes through the scaler; the PK ratings are read unscaled.
        /// Matched on CODE only.
        /// </summary>
        [TestMethod]
        public void RatingChokePoints_ArePinned()
        {
            var root = FindSourceRoot();

            var r1 = CodeLines(root, "ACE.Server/Entity/DamageEvent.cs");

            AssertOrdered(r1, "DamageEvent.cs",
                "var ratingScales = PvpRatingScales.Resolve(PvpChokePoint.R1, attacker, defender);",
                "DamageRatingBaseMod = Creature.GetPositiveRatingMod(ratingScales.ScaleDamageRating(attacker.GetDamageRating()));",
                "CriticalDamageRatingMod = Creature.GetPositiveRatingMod(ratingScales.ScaleCritDamageRating(attacker.GetCritDamageRating()));",
                "DamageResistanceRatingMod = DamageResistanceRatingBaseMod = defender.GetDamageResistRatingMod(CombatType, true, Attacker, ratingScales.DamageRatingScale, out var rawDamageResistRating, out var scaledDamageResistRating);",
                "ratingScales.ReportIfChanged(rawDamageResistRating, scaledDamageResistRating);",
                "CriticalDamageResistanceRatingMod = Creature.GetNegativeRatingMod(ratingScales.ScaleCritDamageResistRating(defender.GetCritDamageResistRating()));");

            AssertExactlyOnce(r1, "DamageEvent.cs", "PkDamageMod = Creature.GetPositiveRatingMod(attacker.GetPKDamageRating());");
            AssertExactlyOnce(r1, "DamageEvent.cs", "PkDamageResistanceMod = Creature.GetNegativeRatingMod(defender.GetPKDamageResistRating());");
            AssertRatingReadsAllScaled(r1, "DamageEvent.cs");

            var r2 = CodeLines(root, "ACE.Server/WorldObjects/SpellProjectile.cs");

            AssertOrdered(r2, "SpellProjectile.cs",
                "var ratingScales = PvpRatingScales.Resolve(PvpChokePoint.R2, ProjectileSource, target);",
                "var damageRating = ratingScales.ScaleDamageRating(sourceCreature?.GetDamageRating() ?? 0);",
                "damageResistRatingMod = target.GetDamageResistRatingMod(CombatType.Magic, true, ProjectileSource, ratingScales.DamageRatingScale, out var rawDamageResistRating, out var scaledDamageResistRating);",
                "ratingScales.ReportIfChanged(rawDamageResistRating, scaledDamageResistRating);",
                "critDamageRatingMod = Creature.GetPositiveRatingMod(ratingScales.ScaleCritDamageRating(sourceCreature?.GetCritDamageRating() ?? 0));",
                "critDamageResistRatingMod = Creature.GetNegativeRatingMod(ratingScales.ScaleCritDamageResistRating(target.GetCritDamageResistRating()));",
                "damage = PvpRules.ApplyDamageCap(PvpChokePoint.C2, ProjectileSource, target, damage);");

            AssertExactlyOnce(r2, "SpellProjectile.cs", "pkDamageRatingMod = Creature.GetPositiveRatingMod(sourceCreature?.GetPKDamageRating() ?? 0);");
            AssertExactlyOnce(r2, "SpellProjectile.cs", "pkDamageResistRatingMod = Creature.GetNegativeRatingMod(target.GetPKDamageResistRating());");
            AssertRatingReadsAllScaled(r2, "SpellProjectile.cs");
        }

        /// <summary>Every non-PK damage / crit damage / crit damage resist rating read in the file sits inside a ratingScales call, and every DRR mod read passes the scale.</summary>
        private static void AssertRatingReadsAllScaled(string[] lines, string file)
        {
            foreach (var line in lines)
            {
                foreach (var read in new[] { ".GetDamageRating()", ".GetCritDamageRating()", ".GetCritDamageResistRating()" })
                {
                    if (line.Contains(read) && !line.Contains("ratingScales.Scale"))
                        Assert.Fail($"{file}: unscaled rating read `{line}`");
                }

                if (line.Contains(".GetDamageResistRatingMod(") && !line.Contains("ratingScales.DamageRatingScale"))
                    Assert.Fail($"{file}: DRR mod read without the rating scale `{line}`");
            }
        }

        private static string[] CodeLines(string root, string file)
        {
            var path = Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar));
            Assert.IsTrue(File.Exists(path), $"missing {path}");

            return File.ReadAllLines(path)
                .Select(raw =>
                {
                    var commentAt = raw.IndexOf("//", StringComparison.Ordinal);
                    return (commentAt >= 0 ? raw.Substring(0, commentAt) : raw).Trim();
                })
                .ToArray();
        }

        private static int IndexOfOnly(string[] lines, string file, string code)
        {
            var hits = Enumerable.Range(0, lines.Length).Where(i => lines[i] == code).ToList();
            Assert.AreEqual(1, hits.Count, $"{file}: expected exactly one code line `{code}`");
            return hits[0];
        }

        private static void AssertExactlyOnce(string[] lines, string file, string code) => IndexOfOnly(lines, file, code);

        private static void AssertOrdered(string[] lines, string file, params string[] codes)
        {
            var previous = -1;

            foreach (var code in codes)
            {
                var at = IndexOfOnly(lines, file, code);
                Assert.IsTrue(at > previous, $"{file}: `{code}` (line {at + 1}) is out of order");
                previous = at;
            }
        }

        private static string FindSourceRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ACE.Server", "Entity", "DamageEvent.cs")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find ACE.Server/Entity/DamageEvent.cs by walking up from {AppContext.BaseDirectory}");
            return dir.FullName;
        }
    }
}
