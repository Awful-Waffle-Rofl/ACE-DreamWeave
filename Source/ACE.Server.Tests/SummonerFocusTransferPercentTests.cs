using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

using static ACE.Server.Tests.SummonerTransferFixtures;

namespace ACE.Server.Tests
{
    /// <summary>
    /// summoner_pet_mod_transfer_percent: the share of every focus weapon mod bonus a pet reads, asserted on the values the
    /// hits consume (the crit chance getter, the Ambush opener through Creature.TakeDamage, the Tier A rating getters, and
    /// the PetFocusModValue the Execution, Defensive Weapon and utility sites multiply in). The wielder's own reads are
    /// never scaled. The imbue share (summoner_pet_imbue_transfer_percent) is pinned where each imbue hook lands.
    /// </summary>
    [TestClass]
    public class SummonerFocusTransferPercentTests
    {
        [ClassInitialize]
        public static void Init(TestContext _)
        {
            TestGameTables.EnsureInitialized();
            DefaultPropertyManager.LoadDefaultProperties();
        }

        [TestInitialize]
        public void SeedKeys() => Seed();

        [TestCleanup]
        public void RestoreKeys() => Restore();

        private static void SetModShare(double f) => PropertyManager.ModifyDouble(SummonerFocus.PetModTransferPercentKey, f);

        private static readonly WeaponModId[] TierB =
        {
            WeaponModId.Ambush, WeaponModId.Focus, WeaponModId.Execution,
            WeaponModId.DefensiveWeapon, WeaponModId.SecondWind, WeaponModId.Cleanse, WeaponModId.Siphon,
        };

        private static readonly (WeaponModId Id, PropertyInt Native)[] TierA =
        {
            (WeaponModId.Bloodthirst, PropertyInt.GearDamage), (WeaponModId.WeakPoint, PropertyInt.GearCrit), (WeaponModId.Devastation, PropertyInt.GearCritDamage),
        };

        private static Caster FullFocus() => Wand(true, ImbuedEffectType.Undef,
            WeaponModId.Ambush, WeaponModId.Focus, WeaponModId.Execution, WeaponModId.DefensiveWeapon, WeaponModId.SecondWind, WeaponModId.Cleanse, WeaponModId.Siphon,
            WeaponModId.Bloodthirst, WeaponModId.WeakPoint, WeaponModId.Devastation);

        private static int Rating(Creature c, PropertyInt native) =>
            native == PropertyInt.GearDamage ? c.GetDamageRating() : native == PropertyInt.GearCrit ? c.GetCritRating() : c.GetCritDamageRating();

        [TestMethod]
        public void Sanitize_and_scalers()
        {
            Assert.AreEqual(1.0, SummonerFocusMath.SanitizeTransferPercent(double.NaN, 1.0));
            Assert.AreEqual(1.0, SummonerFocusMath.SanitizeTransferPercent(double.PositiveInfinity, 1.0));
            Assert.AreEqual(1.0, SummonerFocusMath.SanitizeTransferPercent(double.NegativeInfinity, 1.0));
            Assert.AreEqual(1.0, SummonerFocusMath.SanitizeTransferPercent(-0.1, 1.0));
            Assert.AreEqual(1.0, SummonerFocusMath.SanitizeTransferPercent(1.7, 1.0), "clamped to 1");
            Assert.AreEqual(0.5, SummonerFocusMath.SanitizeTransferPercent(0.5, 1.0));
            Assert.AreEqual(0.0, SummonerFocusMath.SanitizeTransferPercent(0.0, 1.0));

            Assert.AreEqual(7, SummonerFocusMath.ScaleRating(15, 0.5), "rounded down");
            Assert.AreEqual(15, SummonerFocusMath.ScaleRating(15, 1.0));
            Assert.AreEqual(0, SummonerFocusMath.ScaleRating(15, 0.0));

            Assert.AreEqual(0.30f, SummonerFocusMath.ScaleImbueChance(0.50f, 0.10f, 0.5), 1e-6f, "default + (CS - default) * f");
            Assert.AreEqual(3.5f, SummonerFocusMath.ScaleImbueMultiplier(6.0f, 0.5), 1e-6f, "1 + (CB - 1) * f");
            Assert.AreEqual(0.7f, SummonerFocusMath.ScaleArmorRending(0.4f, 0.5), 1e-6f, "1 - (1 - AR) * f");
            Assert.AreEqual(6.0f, SummonerFocusMath.ScaleImbueMultiplier(6.0f, 1.0), "exact at 1.0");
            Assert.AreEqual(0.4f, SummonerFocusMath.ScaleArmorRending(0.4f, 1.0), "exact at 1.0");
        }

        [TestMethod]
        public void Tier_B_pet_reads_scale_by_the_share()
        {
            var focus = FullFocus();
            var pet = Pet(Owner(focus));
            var monster = Monster();

            foreach (var f in new[] { 1.0, 0.5, 0.0 })
            {
                SetModShare(f);

                foreach (var id in TierB)
                {
                    Assert.IsTrue(Mag(id, focus) > 0.0, $"positive control: the focus carries {id}");
                    Assert.AreEqual(Mag(id, focus) * f, SummonerFocus.PetFocusModValue(pet, monster, id), 1e-12, $"{id} at share {f}");
                }
            }
        }

        [TestMethod]
        public void Precision_crit_chance_the_hit_consumes_halves()
        {
            // Precision alone: Weak Point's crit rating rides the same getter and scales too (pinned separately)
            var focus = Wand(true, ImbuedEffectType.Undef, WeaponModId.Focus);
            var owner = Owner(focus);
            var pet = Pet(owner);
            var monster = Monster();

            var full = WorldObject.GetWeaponCriticalChancePreResist(null, pet, null, monster);
            SetModShare(0.5);
            var half = WorldObject.GetWeaponCriticalChancePreResist(null, pet, null, monster);
            SetModShare(0.0);
            var none = WorldObject.GetWeaponCriticalChancePreResist(null, pet, null, monster);

            AssertNear(Mag(WeaponModId.Focus, focus), full - none, 1e-6, "positive control: the full Precision");
            AssertNear(Mag(WeaponModId.Focus, focus) * 0.5, half - none, 1e-6, "half the Precision bonus");

            owner.EquippedObjects.Remove(focus.Guid);
            AssertNear(none, WorldObject.GetWeaponCriticalChancePreResist(null, pet, null, monster), 1e-6, "share 0 is no transfer: same as no focus");
        }

        [TestMethod]
        public void Ambush_opener_through_TakeDamage_halves()
        {
            var focus = FullFocus();
            var pet = Pet(Owner(focus));
            var dealt = SummonerFocusMath.DealtMultiplier(WorldObject.SumCasterElementalDamageMod(focus, pet.P_PetOwner), 1.0);

            uint Opener(double f)
            {
                SetModShare(f);
                var monster = Monster();
                var before = monster.Health.Current;
                monster.TakeDamage(pet, DamageType.Slash, 100f);
                return before - monster.Health.Current;
            }

            Assert.AreEqual((uint)Math.Round(100.0 * dealt * (1.0 + Mag(WeaponModId.Ambush, focus))), Opener(1.0), "positive control: the full Ambush opener");
            Assert.AreEqual((uint)Math.Round(100.0 * dealt * (1.0 + 0.5 * Mag(WeaponModId.Ambush, focus))), Opener(0.5), "half the Ambush bonus");
            Assert.AreEqual((uint)Math.Round(100.0 * dealt), Opener(0.0), "no Ambush at share 0");
        }

        [TestMethod]
        public void Tier_A_pet_ratings_scale_and_round_down()
        {
            var focus = FullFocus();
            var pet = Pet(Owner(focus));
            var bare = Pet(Owner(Wand(true)));

            foreach (var (id, native) in TierA)
            {
                var full = SummonerFocus.FocusTierARating(focus, native);
                Assert.IsTrue(full > 1, $"positive control: {id} writes a rating above 1 (got {full})");

                foreach (var f in new[] { 1.0, 0.5, 0.0 })
                {
                    SetModShare(f);
                    Assert.AreEqual((int)Math.Floor(full * f), Rating(pet, native) - Rating(bare, native), $"{id} rating at share {f}");
                }

                SetModShare(1.0);
            }
        }

        [TestMethod]
        public void A_NaN_negative_or_infinite_share_reads_as_the_full_transfer()
        {
            var focus = FullFocus();
            var pet = Pet(Owner(focus));
            var monster = Monster();

            foreach (var bad in new[] { double.NaN, -1.0, double.PositiveInfinity })
            {
                SetModShare(bad);
                Assert.AreEqual(Mag(WeaponModId.Execution, focus), SummonerFocus.PetFocusModValue(pet, monster, WeaponModId.Execution), 1e-12, $"share {bad} reads as 1.0");
            }

            SetModShare(2.0);
            Assert.AreEqual(Mag(WeaponModId.Execution, focus), SummonerFocus.PetFocusModValue(pet, monster, WeaponModId.Execution), 1e-12, "above 1 clamps to 1");
        }

        [TestMethod]
        public void The_wielders_own_reads_are_never_scaled()
        {
            var focus = FullFocus();
            var owner = Owner(focus);

            var dwFull = owner.GetDefensiveWeaponModValue();
            Assert.AreEqual(200.0, dwFull, 1e-9, "positive control: a full caster Defensive Weapon roll is 200");

            SetModShare(0.5);
            Assert.AreEqual(dwFull, owner.GetDefensiveWeaponModValue(), 1e-9, "Shared: the wielder's Defensive Weapon is unchanged at share 0.5");
            Assert.AreEqual(Mag(WeaponModId.Siphon, focus), WeaponModCombat.ReadWeaponOnly(focus, WeaponModId.Siphon, true, 1.0), 1e-12, "Shared: the wielder's Siphon is unchanged");

            // the wielder strip removes the full Tier A amount whatever the pets' share
            foreach (var (id, native) in TierA)
                Assert.AreEqual(SummonerFocus.FocusTierARating(focus, native), SummonerFocus.WielderFocusRatingStrip(owner, native), $"{id} wielder strip is the full amount");
        }

        [TestMethod]
        public void The_share_never_reaches_a_player_target_or_an_echo()
        {
            var focus = FullFocus();
            var owner = Owner(focus);
            SetModShare(0.5);

            Assert.AreEqual(0.0, SummonerFocus.PetFocusModValue(Pet(owner), PlayerTarget(), WeaponModId.Execution), "PvP guard");
            Assert.AreEqual(0.0, SummonerFocus.PetFocusModValue(Pet(owner, echo: true), Monster(), WeaponModId.Execution), "Echo");
            Assert.AreEqual(0.0, SummonerFocus.PetFocusModValue(Pet(Owner(Wand(false, ImbuedEffectType.Undef, WeaponModId.Execution))), Monster(), WeaponModId.Execution), "plain wand");
        }
    }
}
