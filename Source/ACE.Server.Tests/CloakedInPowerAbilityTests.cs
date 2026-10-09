using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Common;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Cloaked in Power (Vanguard T2, added 2026-09-29): a floor under the wearer's cloak spell-proc chance.
    ///
    /// Covers the floor formula and its affinity clamp, that the floor never LOWERS a chance already above it,
    /// and - the part that is a ruling rather than arithmetic - that it bypasses neither of the two gates
    /// sitting ahead of the chance computation in Cloak.RollProc (the 5 second per-cloak cooldown and the
    /// ItemLevel >= 1 refusal).
    ///
    /// THE GATE TESTS DRIVE A REAL Cloak.RollProc AND ARE STILL DETERMINISTIC, which is why they pass an
    /// absurd floor of 2.0 rather than the shipped 0.10: a chance of 2.0 beats every possible draw from
    /// ThreadSafeRandom.Next(0.0f, 1.0f) (which is documented inclusive at its upper bound, so even 1.0 would
    /// not be strictly deterministic against `rng &lt; chance`). A refusal under a floor of 2.0 can therefore
    /// only be the gate, never an unlucky roll. Each gate test is paired with a POSITIVE CONTROL that lifts
    /// only that gate and shows the same call then succeeds.
    ///
    /// The floor ARITHMETIC is exercised through Cloak.ProcChance and CloakedInPowerAbility.Floor, both pure,
    /// because Player's static initializer cannot run under this test host - the same constraint
    /// ClassAbilityAffinityCapReadoutInvariantTests records.
    /// </summary>
    [TestClass]
    public class CloakedInPowerAbilityTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            // seeds the server-tunable property cache from hardcoded defaults, no database involved
            DefaultPropertyManager.LoadDefaultProperties();
        }

        private static uint nextWcid = 990100;
        private static uint nextGuid = 0x7F300000;

        /// <summary>
        /// A bare cloak item from an in-memory weenie: no database, no dat files (Clothing's
        /// SetEphemeralValues is empty). Same idiom as EquipmentModApplicationTests.MakeItem. ValidLocations
        /// is EquipMask.Cloak so Cloak.IsCloak accepts it.
        ///
        /// WorldObject.ItemLevel has NO SETTER - it is computed by
        /// ExperienceSystem.ItemTotalXPToLevel(ItemTotalXp, ItemBaseXp, ItemMaxLevel, ItemXpStyle) and returns
        /// null unless all three of the latter are present and positive (WorldObject.HasItemLevel). Under
        /// ItemXpStyle.Fixed the level is floor(totalXp / baseXp), so a base of
        /// <see cref="XpPerItemLevel"/> and a total of itemLevel * that produces exactly the level asked for.
        /// The assertion below is the control that it really did.
        /// </summary>
        private const long XpPerItemLevel = 100;

        private static WorldObject MakeCloak(int itemLevel, double useTimestamp)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Clothing,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ValidLocations, (int)EquipMask.Cloak },
                    { PropertyInt.ItemType, (int)ItemType.Clothing },
                },
            };

            var wo = new Clothing(weenie, new ObjectGuid(nextGuid++));

            wo.SetProperty(PropertyInt64.ItemBaseXp, XpPerItemLevel);
            wo.SetProperty(PropertyInt.ItemMaxLevel, 5);
            wo.SetProperty(PropertyInt.ItemXpStyle, (int)ItemXpStyle.Fixed);
            wo.SetProperty(PropertyInt64.ItemTotalXp, itemLevel * XpPerItemLevel);

            wo.UseTimestamp = useTimestamp;

            Assert.AreEqual(itemLevel, wo.ItemLevel ?? 0, "control: the fixture must really carry that item level");

            return wo;
        }

        // ---------------------------------------------------------------- the tunable and the definition

        [TestMethod]
        public void Tunable_FloorDefault_IsTenPercent()
        {
            Assert.AreEqual(0.10, PropertyManager.GetDouble("class_ability_cloakedinpower_floor").Item, 1e-9,
                "class_ability_cloakedinpower_floor default");
        }

        [TestMethod]
        public void Definition_IsVanguardTier2_OneRank_OneCap_ArmorTinkering()
        {
            var def = ClassAbilityRegistry.Get(ClassAbilityId.CloakedInPower);

            Assert.AreEqual(ClassAbilityClass.Vanguard, def.AbilityClass);
            Assert.AreEqual(2, def.Tier);
            Assert.AreEqual(1, def.MaxRank);
            CollectionAssert.AreEqual(new[] { 1 }, def.CostPerRank);
            Assert.IsTrue(def.AffinitySkill.HasValue, "AffinitySkill must be declared - the planner reads it");
            Assert.AreEqual(Skill.ArmorTinkering, def.AffinitySkill.Value);
            Assert.IsTrue(def.Implemented);
            Assert.AreEqual("cloaked_in_power", def.Name);
        }

        /// <summary>
        /// The marker that exempts an Implemented handler from the "must hook something" rule. Asserted
        /// directly rather than left to ClassAbilityRegistryTests' generic scan, because the whole design
        /// depends on this entry being a read-at-the-site passive rather than a dispatched hook.
        /// </summary>
        [TestMethod]
        public void Handler_IsAPassiveStatAbility_AndHooksNothing()
        {
            var handler = ClassAbilityRegistry.GetHandler(ClassAbilityId.CloakedInPower);

            Assert.IsInstanceOfType(handler, typeof(IPassiveStatAbility));
            Assert.IsFalse(handler is IOutgoingDamageAbility);
            Assert.IsFalse(handler is IIncomingDamageAbility);
            Assert.IsFalse(handler is IPreWriteDamageAbility);
            Assert.IsFalse(handler is IItemProcAbility);
            Assert.IsFalse(handler is ISpellHitAbility);
            Assert.IsFalse(handler is ICreatureDeathAbility);
            Assert.IsFalse(handler is IMissileVolleyAbility);
        }

        // ---------------------------------------------------------------- the floor formula

        [TestMethod]
        public void Floor_AtRankZero_IsZero()
        {
            Assert.AreEqual(0.0, CloakedInPowerAbility.Floor(0, 0.10, 1.0, 0.20), 1e-9);
        }

        [TestMethod]
        public void Floor_AtNeutralAffinity_IsTheBareTunable()
        {
            Assert.AreEqual(0.10, CloakedInPowerAbility.Floor(1, 0.10, 1.0, 0.20), 1e-9);
        }

        /// <summary>
        /// A caller handing over 0.0 - the neutral value of the OLD additive affinity primitive - must degrade
        /// to tunable-only, not multiply the whole floor away.
        /// </summary>
        [TestMethod]
        public void Floor_AtZeroMultiplier_DegradesToTunableOnly()
        {
            Assert.AreEqual(0.10, CloakedInPowerAbility.Floor(1, 0.10, 0.0, 0.20), 1e-9);
        }

        [TestMethod]
        public void Floor_AffinityAddsTheScaledAmount_BelowTheCap()
        {
            // multiplier 1.5 on a 0.10 base adds 0.05, which is under the 0.20 cap
            Assert.AreEqual(0.15, CloakedInPowerAbility.Floor(1, 0.10, 1.5, 0.20), 1e-9);
        }

        [TestMethod]
        public void Floor_AffinityAddedAmount_IsClampedByTheChanceCap()
        {
            // multiplier 5.0 on a 0.10 base would add 0.40; the cap allows only 0.20
            Assert.AreEqual(0.30, CloakedInPowerAbility.Floor(1, 0.10, 5.0, 0.20), 1e-9);

            // a cap of 0 means uncapped, which is what the tunable's own doc comment says
            Assert.AreEqual(0.50, CloakedInPowerAbility.Floor(1, 0.10, 5.0, 0.0), 1e-9);
        }

        // ---------------------------------------------------------------- the floor never lowers a chance

        [TestMethod]
        public void ProcChance_FloorRaisesASmallHit()
        {
            // a 1%-of-MaxHealth hit against an item-level-1 cloak: 0.01 normally, 0.10 with the floor
            Assert.AreEqual(0.01f, Cloak.ProcChance(0.01f, 0.25f, 0f, 0f), 1e-6f, "control: no ability, unchanged");
            Assert.AreEqual(0.10f, Cloak.ProcChance(0.01f, 0.25f, 0f, 0.10f), 1e-6f);
        }

        [TestMethod]
        public void ProcChance_FloorNeverLowersAChanceAlreadyAboveIt()
        {
            // item level 5 -> maxProcRate 0.30; a hit worth 28% of MaxHealth already beats a 0.10 floor
            Assert.AreEqual(0.28f, Cloak.ProcChance(0.28f, 0.30f, 0f, 0f), 1e-6f, "control: no ability");
            Assert.AreEqual(0.28f, Cloak.ProcChance(0.28f, 0.30f, 0f, 0.10f), 1e-6f);

            // and it still does not lower the value when the hit is at the cloak's own cap
            Assert.AreEqual(0.30f, Cloak.ProcChance(0.90f, 0.30f, 0f, 0.10f), 1e-6f);
        }

        /// <summary>
        /// minProc and the ability floor are combined with each other before the Math.Max against the hit, so
        /// an operator who raises cloak_min_proc above the ability's floor is not silently overridden by it.
        /// </summary>
        [TestMethod]
        public void ProcChance_TakesTheHigherOfMinProcAndTheAbilityFloor()
        {
            Assert.AreEqual(0.40f, Cloak.ProcChance(0.01f, 0.25f, 0.40f, 0.10f), 1e-6f);
            Assert.AreEqual(0.40f, Cloak.ProcChance(0.01f, 0.25f, 0.10f, 0.40f), 1e-6f);
        }

        // ---------------------------------------------------------------- the two gates it must not bypass

        [TestMethod]
        public void RollProc_AnUnleveledCloakStillNeverProcs_EvenWithAnAbsurdFloor()
        {
            // UseTimestamp 0 (the epoch) is far outside the cooldown, so the ONLY thing that can refuse this
            // is the ItemLevel < 1 gate
            var cloak = MakeCloak(itemLevel: 0, useTimestamp: 0.0);

            for (var i = 0; i < 50; i++)
                Assert.IsFalse(Cloak.RollProc(cloak, 0.5f, abilityFloor: 2.0f),
                    "an ItemLevel 0 cloak must never proc, however high the ability floor is");

            // POSITIVE CONTROL: lift ONLY the item-level gate and the identical call succeeds, which proves
            // the refusals above were that gate and not some other early return.
            var leveled = MakeCloak(itemLevel: 1, useTimestamp: 0.0);
            Assert.IsTrue(Cloak.RollProc(leveled, 0.5f, abilityFloor: 2.0f));
        }

        [TestMethod]
        public void RollProc_TheFiveSecondCooldownStillCapsThroughput_EvenWithAnAbsurdFloor()
        {
            // a cloak that procced just now: ItemLevel is fine, so the ONLY thing that can refuse is the
            // cooldown
            var cloak = MakeCloak(itemLevel: 3, useTimestamp: Time.GetUnixTime());

            for (var i = 0; i < 50; i++)
                Assert.IsFalse(Cloak.RollProc(cloak, 0.5f, abilityFloor: 2.0f),
                    "a cloak inside its cooldown must never proc, however high the ability floor is");

            // POSITIVE CONTROL: the same cloak with the cooldown expired (MinDelay is 5.0s at the shipped
            // default) succeeds on the identical call.
            cloak.UseTimestamp = Time.GetUnixTime() - 60.0;
            Assert.IsTrue(Cloak.RollProc(cloak, 0.5f, abilityFloor: 2.0f));
        }

        /// <summary>
        /// A successful roll stamps UseTimestamp, which is what makes the cooldown self-sustaining - the floor
        /// raises how often the roll WINS, never how often it may be taken.
        /// </summary>
        [TestMethod]
        public void RollProc_ASuccessfulRollRestartsTheCooldown()
        {
            var cloak = MakeCloak(itemLevel: 3, useTimestamp: 0.0);

            Assert.IsTrue(Cloak.RollProc(cloak, 0.5f, abilityFloor: 2.0f));
            Assert.IsTrue(cloak.UseTimestamp > 0.0, "a winning roll must stamp UseTimestamp");
            Assert.IsFalse(Cloak.RollProc(cloak, 0.5f, abilityFloor: 2.0f),
                "the very next roll must be refused by the cooldown the previous success started");
        }

        /// <summary>
        /// The default value of the new parameter reproduces the pre-ability behaviour, so every existing
        /// caller (all four damage-reduction sites call RollProc with two arguments) is unchanged.
        /// </summary>
        [TestMethod]
        public void RollProc_DefaultFloor_ReproducesTheShippedBehaviour()
        {
            var cloak = MakeCloak(itemLevel: 1, useTimestamp: 0.0);

            // damage_percent 0 and no floor -> chance 0 -> refused for every draw in [0, 1]
            for (var i = 0; i < 50; i++)
                Assert.IsFalse(Cloak.RollProc(cloak, 0.0f),
                    "with cloak_min_proc at its shipped 0 and no ability floor, a zero-damage hit can never proc");
        }

        [TestMethod]
        public void CloakedInPowerFloor_IsZeroForANonPlayerWearer()
        {
            // a monster wearing a cloak has no class abilities at all; the wiring must not assume a Player
            Assert.AreEqual(0f, Cloak.CloakedInPowerFloor(null), 1e-9f);
        }

        // ---------------------------------------------------------------- the readout

        /// <summary>
        /// GetReadout is callable with a null Player (the affinity read is null-conditional and falls back to
        /// the NEUTRAL factor 1.0), so the sum invariant can be checked on the real readout object as well as
        /// on the pure helper in ClassAbilityAffinityCapReadoutInvariantTests.
        /// </summary>
        [TestMethod]
        public void GetReadout_ReportsDisplayUnits_AndTermsSumToEffective()
        {
            var handler = (IAbilityReadout)ClassAbilityRegistry.GetHandler(ClassAbilityId.CloakedInPower);

            var readout = handler.GetReadout(null, 1);

            Assert.IsTrue(readout.HasValue);
            Assert.AreEqual("%", readout.Unit);

            // DISPLAY units: a 10% floor reports as 10.0, not 0.10
            Assert.AreEqual(10.0, readout.Skill, 1e-6, "Skill term is the tunable in display units");
            Assert.AreEqual(0.0, readout.Affinity, 1e-6, "neutral affinity adds nothing");
            Assert.AreEqual(0.0, readout.Gear, 1e-6, "no equipment mod");
            Assert.AreEqual(10.0, readout.Effective, 1e-6);
            Assert.AreEqual(readout.Effective, readout.Skill + readout.Affinity + readout.Gear, 1e-6,
                "Skill + Affinity + Gear must equal Effective exactly");
            Assert.IsNull(readout.CapNote, "nothing was capped at a neutral multiplier");
        }

        [TestMethod]
        public void GetReadout_AtRankZero_ReportsNothing()
        {
            var handler = (IAbilityReadout)ClassAbilityRegistry.GetHandler(ClassAbilityId.CloakedInPower);

            var readout = handler.GetReadout(null, 0);

            Assert.AreEqual(0.0, readout.Skill, 1e-6);
            Assert.AreEqual(0.0, readout.Effective, 1e-6);
        }
    }
}
