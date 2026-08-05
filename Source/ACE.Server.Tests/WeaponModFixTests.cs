using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Regression tests for five confirmed defects in the weapon-mod system, one class per defect. Each test
    /// pins the corrected behaviour AND names the exact wrong behaviour it replaces, so a future change that
    /// re-introduces the defect fails with a message explaining why the old shape was wrong rather than just
    /// "expected true, was false".
    ///
    /// Kept in its own file rather than appended to WeaponModTests.cs so the two can be reviewed independently.
    /// </summary>
    [TestClass]
    public class WeaponModFixTests
    {
        private static uint nextGuid = 0x7E800000;   // static guid range, clear of GuidManager and WeaponModTests
        private static uint nextWcid = 993000;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        /// <summary>
        /// A bare item from an in-memory weenie - no database, no dat files. Every read and write the weapon-mod
        /// flow performs goes through the generic GetProperty/SetProperty surface, so the concrete WorldObject
        /// subclass is irrelevant.
        /// </summary>
        private static WorldObject MakeWeapon(ItemType itemType = ItemType.MeleeWeapon, CombatUse? combatUse = CombatUse.Melee,
            Dictionary<PropertyInt, int> ints = null, Dictionary<PropertyFloat, double> floats = null,
            Dictionary<PropertyString, string> strings = null)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Clothing,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)itemType },
                    { PropertyInt.ItemWorkmanship, 10 },
                },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Test Weapon" } },
            };

            if (combatUse != null)
                weenie.PropertiesInt[PropertyInt.CombatUse] = (int)combatUse.Value;

            if (ints != null)
            {
                foreach (var kvp in ints)
                    weenie.PropertiesInt[kvp.Key] = kvp.Value;
            }

            if (floats != null)
            {
                weenie.PropertiesFloat = new Dictionary<PropertyFloat, double>();

                foreach (var kvp in floats)
                    weenie.PropertiesFloat[kvp.Key] = kvp.Value;
            }

            if (strings != null)
            {
                foreach (var kvp in strings)
                    weenie.PropertiesString[kvp.Key] = kvp.Value;
            }

            return new Clothing(weenie, new ObjectGuid(nextGuid++));
        }

        private static string Log(params MaterialType[] materials) =>
            string.Join(",", materials.Select(m => ((uint)m).ToString()));

        // ================= FIX 1 - the below-default recovery is per-material =================

        /// <summary>
        /// THE DEFECT: reversal removed the row for ANY multiplier-floor material whose result landed below the
        /// engine default, on the premise that a below-default value could only be the retail "set 0.01"
        /// artifact. VERIFIED FALSE against ace_world: 11 weapon weenies ship DamageMod below 1.0 (0.1 to 0.6,
        /// including bowphantom 21964) and 26 ship WeaponDefense below 1.0 (0.8 x23, 0.92 x2, 0.96).
        ///
        /// This reproduces the exact reported failure: a bowphantom at DamageMod 0.5, three Mahogany, then a
        /// reroll that reverses them. Under the defect the row was deleted and the engine's "?? 1.0" turned a
        /// 0.5 damage multiplier into 1.0 - a permanent 100% damage gain for two salvage bags.
        /// </summary>
        [TestMethod]
        public void Fix1_MahoganyReversalKeepsALegitimatelyBelowDefaultDamageMod()
        {
            var bowphantom = MakeWeapon(ItemType.MissileWeapon, CombatUse.Missile,
                floats: new Dictionary<PropertyFloat, double> { { PropertyFloat.DamageMod, 0.5 } });

            Assert.IsTrue(WeaponTinkerTable.TryGet(MaterialType.Mahogany, out var mahogany));
            Assert.IsFalse(mahogany.RecoverSetBranch, "Mahogany's live script is a plain 'DamageMod += 0.04'");

            mahogany.Apply(bowphantom, 3);

            Assert.AreEqual(0.62, bowphantom.GetProperty(PropertyFloat.DamageMod).Value, 1e-9);

            mahogany.Reverse(bowphantom, 3);

            var restored = bowphantom.GetProperty(PropertyFloat.DamageMod);

            Assert.IsNotNull(restored, "the DamageMod row must survive: deleting it reads as 1.0, a 100% damage gain");
            Assert.AreEqual(0.5, restored.Value, 1e-9, "the weapon's own below-default DamageMod must come back exactly");
        }

        /// <summary>The same shape on WeaponDefense, the other property with real below-default shipped data.</summary>
        [TestMethod]
        public void Fix1_BrassReversalKeepsALegitimatelyBelowDefaultWeaponDefense()
        {
            var weapon = MakeWeapon(floats: new Dictionary<PropertyFloat, double> { { PropertyFloat.WeaponDefense, 0.8 } });

            Assert.IsTrue(WeaponTinkerTable.TryGet(MaterialType.Brass, out var brass));
            Assert.IsFalse(brass.RecoverSetBranch, "Brass's live script is a plain 'WeaponDefense += 0.01'");

            brass.Apply(weapon, 4);
            Assert.AreEqual(0.84, weapon.GetProperty(PropertyFloat.WeaponDefense).Value, 1e-9);

            brass.Reverse(weapon, 4);

            var restored = weapon.GetProperty(PropertyFloat.WeaponDefense);

            Assert.IsNotNull(restored, "23 weenies ship WeaponDefense 0.8 - that row is real data, not a set-branch artifact");
            Assert.AreEqual(0.8, restored.Value, 1e-9);
        }

        /// <summary>
        /// Green Garnet is the ONLY material in the table whose live DAT script carries the set branch
        /// ("ElementalDamageMod (>= 0.01 ? add : set) 0.01"), so it is the only one that may remove a
        /// below-default result. Audited 2026-07-30 over every file in Entity/Mutations/Recipes/.
        /// </summary>
        [TestMethod]
        public void Fix1_OnlyGreenGarnetCarriesTheSetBranchRecovery()
        {
            var withRecovery = WeaponTinkerTable.AllMaterials.Where(m => m.RecoverSetBranch).Select(m => m.Material).ToList();

            CollectionAssert.AreEquivalent(new[] { MaterialType.GreenGarnet }, withRecovery,
                "only a material whose live mutation script has a 'set' branch may recover by removing the row");

            Assert.IsTrue(WeaponTinkerTable.TryGet(MaterialType.GreenGarnet, out var greenGarnet));
            Assert.IsNull(greenGarnet.ReverseValue(0.01, 1), "0.01 on a property the engine reads as 1.0 when absent IS the artifact");

            // Opal's script has the branch too, but the flag would be dead configuration there: ManaConversionMod's
            // engine default is 0.0, so the artifact is already caught by the "within epsilon of the default" rule
            Assert.IsTrue(WeaponTinkerTable.TryGet(MaterialType.Opal, out var opal));
            Assert.IsFalse(opal.RecoverSetBranch);
            Assert.AreEqual(0.0, opal.EngineDefault, 1e-12);
            Assert.IsNull(opal.ReverseValue(0.01, 1), "Opal's set-branch artifact reverses onto its own default and is removed anyway");
        }

        /// <summary>No reversal may ever write a negative, whatever the log claims.</summary>
        [TestMethod]
        public void Fix1_AReversalIsFlooredAtZeroForEveryMaterial()
        {
            foreach (var material in WeaponTinkerTable.AllMaterials)
            {
                // a log claiming far more applications than the item carries is the only way here
                var reversed = material.ReverseValue(0.05, WeaponModRegistry.TotalSlots * 10);

                Assert.IsTrue(reversed == null || reversed.Value >= 0.0,
                    $"{material.Material}: a reversal wrote {reversed}, which is negative");
            }
        }

        // ================= FIX 2 - unknown log entries consume slots =================

        /// <summary>
        /// THE DEFECT: ReadComposition filtered the log to owned materials while PassesIntegrityGate counted ALL
        /// parsed entries, so a slot spent on a material this system does not own became invisible to the budget.
        /// materialoak wcid 20989 has a live cook_book row, so ten hand-tinkered Oak passed the gate, reversed
        /// nothing, and then took ten fresh tinkers plus up to three specials on top: twenty tinkers of effect on
        /// one weapon, plus a permanently floored WeaponTime.
        /// </summary>
        [TestMethod]
        public void Fix2_UnaccountedLogEntriesAreReservedRatherThanRefilled()
        {
            // the exploit shape: ten Oak, no imbues
            var tenOak = MakeWeapon(ints: new Dictionary<PropertyInt, int> { { PropertyInt.NumTimesTinkered, 10 } },
                strings: new Dictionary<PropertyString, string>
                {
                    { PropertyString.TinkerLog, Log(Enumerable.Repeat(MaterialType.Oak, 10).ToArray()) },
                });

            Assert.IsTrue(WeaponModTinkerSet.PassesIntegrityGate(tenOak), "ten Oak passes the integrity gate - that is the whole trap");
            Assert.AreEqual(0, WeaponModTinkerSet.ReadComposition(tenOak).Count, "none of it is reversible by this system");
            Assert.AreEqual(10, WeaponModTinkerSet.ReadReservedSlots(tenOak), "all ten slots must be reserved");
            Assert.AreEqual(0, WeaponModTinkerSet.AvailableSlots(WeaponModTinkerSet.ReadReservedSlots(tenOak)));

            Assert.AreEqual(WeaponModManager.WeaponModRefusal.NoAvailableSlots,
                WeaponModManager.ResolveRefusal(WeaponModManager.WeaponModAction.Reroll, WeaponClass.Melee, true, true, 10, 0,
                    WeaponModTinkerSet.ReadReservedSlots(tenOak)),
                "a weapon whose whole budget is unreversible must be refused, not refilled");
        }

        [TestMethod]
        public void Fix2_AMixedLogReservesOnlyTheUnaccountedHalf()
        {
            var mixed = MakeWeapon(ints: new Dictionary<PropertyInt, int> { { PropertyInt.NumTimesTinkered, 10 } },
                strings: new Dictionary<PropertyString, string>
                {
                    { PropertyString.TinkerLog, Log(MaterialType.Oak, MaterialType.Oak, MaterialType.Oak, MaterialType.Oak, MaterialType.Oak,
                                                    MaterialType.Iron, MaterialType.Iron, MaterialType.Iron, MaterialType.Iron, MaterialType.Iron) },
                });

            Assert.AreEqual(5, WeaponModTinkerSet.ReadReservedSlots(mixed));
            Assert.AreEqual(5, WeaponModTinkerSet.ReadComposition(mixed).Count);
            Assert.AreEqual(5, WeaponModTinkerSet.ComputeTinkerCount(WeaponModTinkerSet.ReadReservedSlots(mixed), 0),
                "five reversible slots get refilled, so the total effect stays at ten");
        }

        /// <summary>
        /// An imbue stamps BOTH an ImbuedEffect bit and a log entry, so charging for each separately would
        /// double-count. This is the "one imbue material plus nine Iron" case, which must be unchanged from
        /// before the fix: reserved 1, nine refilled.
        /// </summary>
        [TestMethod]
        public void Fix2_AnImbueIsNotChargedTwice()
        {
            var imbued = MakeWeapon(ints: new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.NumTimesTinkered, 10 },
                    { PropertyInt.ImbuedEffect, (int)ImbuedEffectType.CriticalStrike },
                },
                strings: new Dictionary<PropertyString, string>
                {
                    { PropertyString.TinkerLog, Log(MaterialType.BlackOpal,
                                                    MaterialType.Iron, MaterialType.Iron, MaterialType.Iron, MaterialType.Iron,
                                                    MaterialType.Iron, MaterialType.Iron, MaterialType.Iron, MaterialType.Iron, MaterialType.Iron) },
                });

            Assert.AreEqual(1, WeaponModTinkerSet.ReadReservedImbueSlots(imbued));
            Assert.AreEqual(1, WeaponModTinkerSet.ReadReservedSlots(imbued),
                "the imbue's ImbuedEffect bit and its log entry are the same slot");
            Assert.AreEqual(9, WeaponModTinkerSet.ComputeTinkerCount(WeaponModTinkerSet.ReadReservedSlots(imbued), 0));
        }

        /// <summary>The pure arithmetic, over the whole table rather than the three worked examples.</summary>
        [TestMethod]
        public void Fix2_ReservedSlotArithmeticIsBoundedAndNeverNegative()
        {
            for (var imbue = 0; imbue <= 12; imbue++)
            {
                for (var logCount = 0; logCount <= 12; logCount++)
                {
                    for (var known = 0; known <= logCount; known++)
                    {
                        var reserved = WeaponModTinkerSet.ComputeReservedSlots(imbue, logCount, known);

                        Assert.IsTrue(reserved >= 0 && reserved <= WeaponModRegistry.TotalSlots,
                            $"imbue {imbue}, log {logCount}, known {known}: reserved {reserved} is out of [0, 10]");

                        Assert.IsTrue(reserved >= Math.Min(imbue, WeaponModRegistry.TotalSlots),
                            $"imbue {imbue}, log {logCount}, known {known}: reserved {reserved} lost an imbue slot");

                        Assert.IsTrue(WeaponModTinkerSet.ComputeTinkerCount(reserved, 3) >= 0);
                    }
                }
            }
        }

        /// <summary>
        /// The second half of the same defect: WriteComposition used to overwrite the log with the new set only,
        /// erasing the record of the entries whose slots had just been reserved. One more reroll and the weapon
        /// would look clean, reserve nothing, and refill all ten on top of effects that were never reversed.
        /// </summary>
        [TestMethod]
        public void Fix2_UnaccountedEntriesSurviveALogRewrite()
        {
            var mixed = MakeWeapon(ints: new Dictionary<PropertyInt, int> { { PropertyInt.NumTimesTinkered, 10 } },
                strings: new Dictionary<PropertyString, string>
                {
                    { PropertyString.TinkerLog, Log(MaterialType.Oak, MaterialType.Oak, MaterialType.Oak, MaterialType.Oak, MaterialType.Oak,
                                                    MaterialType.Iron, MaterialType.Iron, MaterialType.Iron, MaterialType.Iron, MaterialType.Iron) },
                });

            var preserved = WeaponModTinkerSet.ReadUnaccountedEntries(mixed);

            CollectionAssert.AreEqual(Enumerable.Repeat(MaterialType.Oak, 5).ToList(), preserved);

            var refill = Enumerable.Repeat(MaterialType.Brass, 5).ToList();

            WeaponModTinkerSet.WriteComposition(mixed, preserved, refill);

            var after = WeaponModTinkerSet.ReadFullComposition(mixed);

            Assert.AreEqual(10, after.Count, "the rewritten log must still account for all ten slots");
            Assert.AreEqual(5, after.Count(m => m == MaterialType.Oak), "the unreversible entries must be carried forward verbatim");
            Assert.AreEqual(5, WeaponModTinkerSet.ReadReservedSlots(mixed), "so the NEXT use reserves them too");
            Assert.AreEqual(5, mixed.GetProperty(PropertyInt.WeaponModTinkerCount), "the marker counts only what this system can reverse");
        }

        // ================= FIX 3 - Swift Flight must not delete MaximumVelocity at 20.0 =================

        /// <summary>
        /// THE DEFECT: a reversal landing exactly on NativeDefault removed the row for every modifier. Swift
        /// Flight's default is 20.0 (Creature_Missile.cs:517) and five weenies (518, 521, 531, 537, 23109) carry
        /// MaximumVelocity at exactly 20.0, so a reversal deleted a real row - and the read sites disagree
        /// (WeaponProfile.cs:57 falls back to 1.0), which leaves the appraisal panel reporting velocity 1.0.
        /// </summary>
        [TestMethod]
        public void Fix3_SwiftFlightReversalRestoresTwentyRatherThanRemovingTheRow()
        {
            var swiftFlight = WeaponModRegistry.Get(WeaponModId.SwiftFlight);

            Assert.IsFalse(swiftFlight.RemoveOnDefault, "Swift Flight must never remove MaximumVelocity");
            Assert.AreEqual(20.0, swiftFlight.NativeDefault, 1e-12);

            var launcher = MakeWeapon(ItemType.MissileWeapon, CombatUse.Missile,
                floats: new Dictionary<PropertyFloat, double> { { PropertyFloat.MaximumVelocity, 20.0 } });

            WeaponModTinkerSet.ApplySpecial(launcher, swiftFlight, 4.0);
            Assert.AreEqual(24.0, launcher.GetProperty(PropertyFloat.MaximumVelocity).Value, 1e-9);

            WeaponModTinkerSet.ReverseSpecial(launcher, swiftFlight);

            var restored = launcher.GetProperty(PropertyFloat.MaximumVelocity);

            Assert.IsNotNull(restored, "removing the row makes WeaponProfile.cs:57 read velocity 1.0 on an unchanged bow");
            Assert.AreEqual(20.0, restored.Value, 1e-9);
            Assert.IsNull(launcher.GetProperty(swiftFlight.Record), "the record row is still cleared, never zeroed");
        }

        /// <summary>
        /// Cleave MUST keep removal-on-default. Cleaving is consumed as a null test
        /// (WorldObject_Weapon.cs:47-62), so writing its restored default of 1 explicitly would leave the weapon
        /// permanently flagged as cleaving.
        /// </summary>
        [TestMethod]
        public void Fix3_CleaveStillRemovesItsRowOnReversal()
        {
            var cleave = WeaponModRegistry.Get(WeaponModId.Cleave);

            Assert.IsTrue(cleave.RemoveOnDefault, "Cleaving is a null test - writing its default back would flag the weapon forever");

            var sword = MakeWeapon();

            WeaponModTinkerSet.ApplySpecial(sword, cleave, 1.0);
            Assert.AreEqual(2, sword.GetProperty(PropertyInt.Cleaving));

            WeaponModTinkerSet.ReverseSpecial(sword, cleave);

            Assert.IsNull(sword.GetProperty(PropertyInt.Cleaving), "Cleaving must be REMOVED, restoring IsCleaving == false");
        }

        /// <summary>
        /// Every rating special keeps removal-on-default: absent unambiguously reads 0 at their read sites, so
        /// removing the row is the correct restore and leaves no junk behind.
        /// </summary>
        [TestMethod]
        public void Fix3_OnlySwiftFlightOptsOutOfRemovalOnDefault()
        {
            var optedOut = WeaponModRegistry.AllMods.Where(m => !m.RemoveOnDefault).Select(m => m.Id).ToList();

            CollectionAssert.AreEquivalent(new[] { WeaponModId.SwiftFlight }, optedOut,
                "only MaximumVelocity has read sites that disagree about what absent means");
        }

        // ================= FIX 4 - Granite must not write a junk zero row =================

        /// <summary>
        /// THE DEFECT: Granite is "DamageVariance *= 0.8", so on a weapon carrying no DamageVariance row it
        /// computed 0.0 * 0.8^n = 0.0 and wrote a SetProperty(DamageVariance, 0.0) where no row had existed.
        ///
        /// THAT GRANITE IS A DEAD ROLL ON A ZERO-VARIANCE WEAPON IS FAITHFUL TO RETAIL AND IS NOT THE BUG. Only
        /// the junk row was. Do not "fix" the dead roll by giving Granite a floor or an additive branch.
        /// </summary>
        [TestMethod]
        public void Fix4_GraniteWritesNoRowOnAWeaponWithNoDamageVariance()
        {
            var weapon = MakeWeapon();

            Assert.IsNull(weapon.GetProperty(PropertyFloat.DamageVariance), "precondition: no DamageVariance row");

            Assert.IsTrue(WeaponTinkerTable.TryGet(MaterialType.Granite, out var granite));

            granite.Apply(weapon, 3);

            Assert.IsNull(weapon.GetProperty(PropertyFloat.DamageVariance),
                "a dead Granite roll must leave the item untouched, not stamp a 0.0 row that reads as absent");
        }

        /// <summary>Granite on a weapon that DOES carry variance is unchanged - the dead roll is the only no-op.</summary>
        [TestMethod]
        public void Fix4_GraniteStillAppliesWhenTheWeaponCarriesVariance()
        {
            var weapon = MakeWeapon(floats: new Dictionary<PropertyFloat, double> { { PropertyFloat.DamageVariance, 0.4 } });

            Assert.IsTrue(WeaponTinkerTable.TryGet(MaterialType.Granite, out var granite));

            granite.Apply(weapon, 3);

            Assert.AreEqual(0.4 * Math.Pow(0.8, 3), weapon.GetProperty(PropertyFloat.DamageVariance).Value, 1e-12);
        }

        /// <summary>The same guard applied across the table: no material may create a row that reads as absent.</summary>
        [TestMethod]
        public void Fix4_NoMaterialWritesARowEqualToItsOwnEngineDefault()
        {
            foreach (var material in WeaponTinkerTable.AllMaterials)
            {
                var weapon = MakeWeapon();

                material.Apply(weapon, 4);

                var written = material.ReadValue(weapon);

                if (written == null)
                    continue;

                Assert.IsTrue(Math.Abs(written.Value - material.EngineDefault) > WeaponModRegistry.Epsilon,
                    $"{material.Material}: wrote {written} onto an absent property, which is exactly its engine default");
            }
        }

        // ================= FIX 5 - the bag is consumed before the weapon is mutated =================

        /// <summary>
        /// THE DEFECT: HandleApply mutated and saved the weapon, then consumed the bag. A consume failure left
        /// the reroll applied with the bag still in the player's pack - a free reroll.
        ///
        /// The consume is now taken first, which is only safe because CanApply covers EVERY condition under
        /// which ApplyReroll / ApplySwap would bail out and return null. That is what this test pins: if CanApply
        /// says yes, the apply cannot refuse, so the bag can never be eaten for nothing. HandleApply itself needs
        /// a Player with a session and inventory and is covered by the live loop, not here.
        /// </summary>
        [TestMethod]
        public void Fix5_CanApplyCoversEveryBailOutInBothFlows()
        {
            foreach (var weaponClass in new[] { WeaponClass.Melee, WeaponClass.Missile, WeaponClass.Caster })
            {
                var itemType = weaponClass == WeaponClass.Missile ? ItemType.MissileWeapon
                    : weaponClass == WeaponClass.Caster ? ItemType.Caster : ItemType.MeleeWeapon;

                var combatUse = weaponClass == WeaponClass.Missile ? CombatUse.Missile : CombatUse.Melee;

                // a fresh weapon: the reroll can always proceed, the swap has nothing to take away
                var fresh = MakeWeapon(itemType, combatUse);

                Assert.IsTrue(WeaponModManager.CanApply(fresh, weaponClass, WeaponModManager.WeaponModAction.Reroll));
                Assert.IsNotNull(WeaponModManager.ApplyReroll(fresh, weaponClass, 10.0),
                    $"{weaponClass}: CanApply said yes but the reroll refused - the bag would already be gone");

                // after the reroll the weapon carries a set, so the swap has something to remove - unless that
                // reroll happened to land a full trio, in which case BOTH must refuse together
                var atCap = WeaponModTinkerSet.SpecialCount(fresh) >= WeaponModRegistry.MaxSpecials;

                var canSwap = WeaponModManager.CanApply(fresh, weaponClass, WeaponModManager.WeaponModAction.Swap);

                Assert.AreEqual(!atCap, canSwap,
                    $"{weaponClass}: CanApply says {canSwap} on a weapon holding {WeaponModTinkerSet.SpecialCount(fresh)} specials");

                Assert.AreEqual(canSwap, WeaponModManager.ApplySwap(fresh, weaponClass, 10.0) != null,
                    $"{weaponClass}: CanApply and ApplySwap disagree - the bag is consumed on CanApply's word alone, so a disagreement is either a free swap or a bag eaten for nothing");
            }
        }

        [TestMethod]
        public void Fix5_CanApplyRefusesEveryShapeTheApplyPathWouldRefuse()
        {
            var weapon = MakeWeapon();

            Assert.IsFalse(WeaponModManager.CanApply(null, WeaponClass.Melee, WeaponModManager.WeaponModAction.Reroll));
            Assert.IsFalse(WeaponModManager.CanApply(weapon, WeaponClass.None, WeaponModManager.WeaponModAction.Reroll));
            Assert.IsFalse(WeaponModManager.CanApply(weapon, WeaponClass.Melee, WeaponModManager.WeaponModAction.None));

            // an empty weapon has nothing for the swap to remove, and ApplySwap returns null for exactly that
            Assert.IsFalse(WeaponModManager.CanApply(weapon, WeaponClass.Melee, WeaponModManager.WeaponModAction.Swap));
            Assert.IsNull(WeaponModManager.ApplySwap(weapon, WeaponClass.Melee, 10.0));
        }

        // ================= drift stone: an explicit refusal, not a gate coincidence =================

        /// <summary>
        /// A Prismatic Drift Stone weapon is refused today only because it carries NumTimesTinkered 10 against a
        /// 2..6 entry log, which the integrity gate rejects on the count mismatch. That is a coincidence of the
        /// stone's current MinBoosts/MaxBoosts tuning: raise MaxBoosts to 9 and the log reaches ten entries, the
        /// gate passes, and a weapon the game promised "can never be tinkered again" opens to a full reroll.
        ///
        /// The independence is proved by suppressing the gate: PassesIntegrityGate returns true unconditionally
        /// on a weapon carrying WeaponModTinkerCount, so a drift-stoned weapon with that marker sails past the
        /// coincidence. The refusal must still fire. The signature's own length window is derived from
        /// MinBoosts/MaxBoosts, so it tracks a retuning of the stone automatically.
        /// </summary>
        [TestMethod]
        public void DriftStone_IsRefusedExplicitlyEvenWhenTheIntegrityGateWouldPassIt()
        {
            var materials = Enumerable.Repeat(MaterialType.Iron, PrismaticDriftStone.MaxBoosts).ToList();
            materials.Add(PrismaticDriftStone.GetRendMaterial(ImbuedEffectType.SlashRending));

            var stoned = MakeWeapon(ints: new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.NumTimesTinkered, PrismaticDriftStone.LockedTinkerCount },
                    { PropertyInt.DamageType, (int)DamageType.Slash },
                    { PropertyInt.ImbuedEffect, (int)ImbuedEffectType.SlashRending },
                    // suppresses the integrity gate, which is the only thing refusing this weapon today
                    { PropertyInt.WeaponModTinkerCount, 0 },
                },
                strings: new Dictionary<PropertyString, string> { { PropertyString.TinkerLog, Log(materials.ToArray()) } });

            Assert.IsTrue(WeaponModTinkerSet.PassesIntegrityGate(stoned),
                "precondition: the gate is suppressed here, so it cannot be what refuses the weapon");

            Assert.IsTrue(PrismaticDriftStone.MatchesAppliedSignature(stoned));

            Assert.AreEqual(WeaponModManager.WeaponModRefusal.DriftStoneLocked,
                WeaponModManager.ResolveDriftStoneRefusal(stoned),
                "the refusal must be explicit, not a side effect of the integrity gate");
        }

        /// <summary>The signature the stone actually leaves today is matched too, tuning unchanged.</summary>
        [TestMethod]
        public void DriftStone_MatchesTheSignatureItWritesAtTheCurrentTuning()
        {
            for (var boosts = PrismaticDriftStone.MinBoosts; boosts <= PrismaticDriftStone.MaxBoosts; boosts++)
            {
                var materials = Enumerable.Repeat(MaterialType.Mahogany, boosts).ToList();
                materials.Add(PrismaticDriftStone.GetRendMaterial(ImbuedEffectType.PierceRending));

                var stoned = MakeWeapon(ItemType.MissileWeapon, CombatUse.Missile,
                    new Dictionary<PropertyInt, int>
                    {
                        { PropertyInt.NumTimesTinkered, PrismaticDriftStone.LockedTinkerCount },
                        { PropertyInt.DamageType, (int)DamageType.Pierce },
                        { PropertyInt.ImbuedEffect, (int)ImbuedEffectType.PierceRending },
                    },
                    strings: new Dictionary<PropertyString, string> { { PropertyString.TinkerLog, Log(materials.ToArray()) } });

                Assert.IsTrue(PrismaticDriftStone.MatchesAppliedSignature(stoned), $"{boosts} boosts");
                Assert.AreEqual(WeaponModManager.WeaponModRefusal.DriftStoneLocked, WeaponModManager.ResolveDriftStoneRefusal(stoned));
            }
        }

        /// <summary>An ordinary tinkered weapon is not caught by the drift-stone refusal.</summary>
        [TestMethod]
        public void DriftStone_DoesNotRefuseAnOrdinaryTinkeredWeapon()
        {
            var ordinary = MakeWeapon(ints: new Dictionary<PropertyInt, int> { { PropertyInt.NumTimesTinkered, 10 } },
                strings: new Dictionary<PropertyString, string>
                {
                    { PropertyString.TinkerLog, Log(MaterialType.Iron, MaterialType.Iron, MaterialType.Brass, MaterialType.Velvet,
                                                    MaterialType.Granite, MaterialType.Iron, MaterialType.Brass, MaterialType.Iron,
                                                    MaterialType.Velvet, MaterialType.Granite) },
                });

            Assert.AreEqual(WeaponModManager.WeaponModRefusal.None, WeaponModManager.ResolveDriftStoneRefusal(ordinary));
            Assert.IsFalse(PrismaticDriftStone.MatchesAppliedSignature(MakeWeapon()));
        }

        // ================= FIX 6 - a zero-magnitude special is not applied at all =================

        /// <summary>
        /// How many rows the two LIVE record bands hold between them - 8130-8135 for Tier A and 8141-8147 for
        /// Tier B. One per held special, never more. Both, because both are written and cleared by the same
        /// ApplySpecial / ReverseSpecial pair, so counting only Tier A's would miss a zeroed Tier B row.
        /// </summary>
        private static int BandRows(WorldObject weapon)
        {
            var rows = 0;

            for (var id = WeaponModRegistry.PropertyBandStart; id <= WeaponModRegistry.PropertyBandEnd; id++)
            {
                if (weapon.GetProperty((PropertyFloat)id) != null)
                    rows++;
            }

            for (var id = WeaponModRegistry.TierBPropertyBandStart; id <= WeaponModRegistry.TierBPropertyBandEnd; id++)
            {
                if (weapon.GetProperty((PropertyFloat)id) != null)
                    rows++;
            }

            return rows;
        }

        /// <summary>
        /// THE DEFECT: with weapon_mod_magnitude_scale = 0 every non-binary special resolved to a magnitude of
        /// exactly 0, still consumed a slot, and still wrote a 0.0 record into the reserved 8130-8135 band. That
        /// is precisely the dead row the design forbids ("clear with RemoveProperty, never SetProperty(0)"),
        /// reached from the other direction, and it silently taxed three of the ten slots for nothing.
        ///
        /// A scale of 0 means MUTE THE LAYER, which means all ten slots go to tinkers. The special chances are
        /// forced to certainty here so the drop path runs on every single iteration rather than 35% of them.
        ///
        /// Cleave is deliberately unaffected and is why the melee arm asserts a different thing: it is Binary, so
        /// it ignores the scale by design and always applies its full +1. Its slot is not dead, so there is
        /// nothing to convert. Only a ZERO magnitude is dropped, never a small one.
        /// </summary>
        [TestMethod]
        public void Fix6_AMutedMagnitudeScaleSpendsEverySlotOnTinkersRatherThanOnDeadRecords()
        {
            var priorScale = PropertyManager.GetDouble("weapon_mod_magnitude_scale").Item;
            var priorOne = PropertyManager.GetDouble("weapon_mod_special_chance_1").Item;
            var priorTwo = PropertyManager.GetDouble("weapon_mod_special_chance_2").Item;
            var priorThree = PropertyManager.GetDouble("weapon_mod_special_chance_3").Item;

            try
            {
                PropertyManager.ModifyDouble("weapon_mod_magnitude_scale", 0.0);
                PropertyManager.ModifyDouble("weapon_mod_special_chance_1", 1.0);
                PropertyManager.ModifyDouble("weapon_mod_special_chance_2", 1.0);
                PropertyManager.ModifyDouble("weapon_mod_special_chance_3", 1.0);

                // missile and caster carry no binary modifier, so at a muted scale NOTHING may be held
                foreach (var weaponClass in new[] { WeaponClass.Missile, WeaponClass.Caster })
                {
                    var itemType = weaponClass == WeaponClass.Missile ? ItemType.MissileWeapon : ItemType.Caster;
                    var combatUse = weaponClass == WeaponClass.Missile ? CombatUse.Missile : CombatUse.Melee;

                    for (var i = 1; i <= 40; i++)
                    {
                        var weapon = MakeWeapon(itemType, combatUse);

                        Assert.IsNotNull(WeaponModManager.ApplyReroll(weapon, weaponClass, 10.0), $"{weaponClass} reroll {i}");

                        Assert.AreEqual(0, WeaponModTinkerSet.SpecialCount(weapon),
                            $"{weaponClass} reroll {i}: a special resolving to zero magnitude must not be held");

                        Assert.AreEqual(0, BandRows(weapon),
                            $"{weaponClass} reroll {i}: the reserved 8130-8135 band holds a record for a special that buys nothing");

                        Assert.AreEqual(WeaponModRegistry.TotalSlots, weapon.GetProperty(PropertyInt.WeaponModTinkerCount),
                            $"{weaponClass} reroll {i}: a dropped special's slot must convert to a tinker, so all ten go to layer 1");

                        Assert.IsTrue(WeaponModTinkerSet.TryParseLog(weapon.GetProperty(PropertyString.WeaponModTinkerLog), out var log));
                        Assert.AreEqual(WeaponModRegistry.TotalSlots, log.Count, $"{weaponClass} reroll {i}: the log must account for all ten slots");
                    }
                }

                // melee: only the binary Cleave may survive a muted scale, and the budget still sums to ten
                for (var i = 1; i <= 60; i++)
                {
                    var weapon = MakeWeapon();

                    Assert.IsNotNull(WeaponModManager.ApplyReroll(weapon, WeaponClass.Melee, 10.0), $"melee reroll {i}");

                    var specials = WeaponModTinkerSet.ReadSpecials(weapon);

                    foreach (var (definition, magnitude) in specials)
                    {
                        Assert.IsTrue(definition.Binary,
                            $"melee reroll {i}: {definition.Id} is held at a muted scale but is not binary, so its slot bought nothing");

                        Assert.IsTrue(magnitude > 0.0, $"melee reroll {i}: {definition.Id} holds a magnitude of {magnitude}");
                    }

                    Assert.AreEqual(specials.Count, BandRows(weapon), $"melee reroll {i}: dead rows in the reserved band");

                    Assert.AreEqual(WeaponModRegistry.TotalSlots - specials.Count, weapon.GetProperty(PropertyInt.WeaponModTinkerCount),
                        $"melee reroll {i}: every slot not bought by a live special must convert to a tinker");
                }
            }
            finally
            {
                PropertyManager.ModifyDouble("weapon_mod_magnitude_scale", priorScale);
                PropertyManager.ModifyDouble("weapon_mod_special_chance_1", priorOne);
                PropertyManager.ModifyDouble("weapon_mod_special_chance_2", priorTwo);
                PropertyManager.ModifyDouble("weapon_mod_special_chance_3", priorThree);
            }
        }

        /// <summary>
        /// The swap half of the same rule. A draw that would add a zero-magnitude special adds a TINKER instead;
        /// it never refuses, because CanApply has already promised the bag by the time the apply runs. The swap
        /// chance is forced to certainty so every one of these twenty uses takes the conversion path.
        /// </summary>
        [TestMethod]
        public void Fix6_AMutedSwapDrawConvertsToATinkerRatherThanRefusing()
        {
            var priorScale = PropertyManager.GetDouble("weapon_mod_magnitude_scale").Item;
            var priorSwap = PropertyManager.GetDouble("weapon_mod_swap_special_chance").Item;

            try
            {
                PropertyManager.ModifyDouble("weapon_mod_magnitude_scale", 0.0);
                PropertyManager.ModifyDouble("weapon_mod_swap_special_chance", 1.0);

                // missile carries no binary modifier, so every draw here resolves to zero
                var weapon = WeaponModTestKit.MakeHandTinkered(WeaponClass.Missile);

                for (var i = 1; i <= 20; i++)
                {
                    Assert.IsTrue(WeaponModManager.CanApply(weapon, WeaponClass.Missile, WeaponModManager.WeaponModAction.Swap),
                        $"swap {i}: CanApply must still say yes - a muted draw is a tinker, not a refusal");

                    Assert.IsNotNull(WeaponModManager.ApplySwap(weapon, WeaponClass.Missile, 10.0),
                        $"swap {i}: a zero-magnitude draw must convert to a tinker; refusing here would eat the bag for nothing");

                    Assert.AreEqual(0, WeaponModTinkerSet.SpecialCount(weapon), $"swap {i}: a zero-magnitude special was held");
                    Assert.AreEqual(0, BandRows(weapon), $"swap {i}: a dead record was written into the reserved band");

                    Assert.AreEqual(WeaponModRegistry.TotalSlots, weapon.GetProperty(PropertyInt.WeaponModTinkerCount),
                        $"swap {i}: the converted slot went missing instead of becoming a tinker");
                }
            }
            finally
            {
                PropertyManager.ModifyDouble("weapon_mod_magnitude_scale", priorScale);
                PropertyManager.ModifyDouble("weapon_mod_swap_special_chance", priorSwap);
            }
        }

        /// <summary>
        /// The drop test is PER DEFINITION, because the quantization is: WeaponModDefinition.ApplyValue rounds
        /// before writing an integer-valued native, so a magnitude that rounds to zero adds literally nothing to
        /// GearCritDamage while the same magnitude on a float-valued native is applied faithfully.
        ///
        /// The rounded-to-zero case is NOT reachable through WeaponModValue.Resolve today, and that is by
        /// construction rather than by luck: Resolve floors an integer magnitude at 1 whenever the unrounded value
        /// was above 0, which is the other half of the design's MinPotency 0.25 rule. The second half of this test
        /// pins that, so a small scale stays a small buff rather than becoming a silent tax, and only a GENUINE
        /// zero converts its slot. If that floor is ever removed, the per-definition branch below is what stops
        /// the dead rows coming straight back.
        /// </summary>
        [TestMethod]
        public void Fix6_TheZeroTestIsPerDefinitionAndOnlyAGenuineZeroIsDropped()
        {
            var devastation = WeaponModRegistry.Get(WeaponModId.Devastation);
            var bypass = WeaponModRegistry.Get(WeaponModId.ShieldBypass);

            Assert.IsTrue(devastation.IsInteger, "precondition: GearCritDamage is an integer-valued native");
            Assert.IsFalse(bypass.IsInteger, "precondition: IgnoreShield is a float-valued native");

            Assert.IsFalse(WeaponModValue.IsLiveMagnitude(devastation, 0.4),
                "0.4 on an integer native rounds to 0 in ApplyValue, so the record would be the only thing the slot bought");

            Assert.IsTrue(WeaponModValue.IsLiveMagnitude(bypass, 0.4), "0.4 on a float native is applied exactly");
            Assert.IsTrue(WeaponModValue.IsLiveMagnitude(bypass, 1e-9), "a float native carries any non-zero magnitude");

            Assert.IsFalse(WeaponModValue.IsLiveMagnitude(devastation, 0.0));
            Assert.IsFalse(WeaponModValue.IsLiveMagnitude(bypass, 0.0));
            Assert.IsFalse(WeaponModValue.IsLiveMagnitude(devastation, double.NaN), "a NaN magnitude must never be applied");
            Assert.IsFalse(WeaponModValue.IsLiveMagnitude(null, 5.0));

            // a tiny scale on the WORST case - workmanship 1, potency at the floor - still buys something on
            // every definition, so nothing but a muted layer is ever dropped
            foreach (var definition in WeaponModRegistry.AllMods)
            {
                foreach (var scale in new[] { 1e-12, 1e-6, 0.01 })
                {
                    var magnitude = WeaponModValue.Resolve(definition, WeaponModRoller.MinPotency(definition), 1.0, scale);

                    Assert.IsTrue(WeaponModValue.IsLiveMagnitude(definition, magnitude),
                        $"{definition.Id} at scale {scale} resolved to {magnitude}, which would be dropped - a small scale must be a small buff, not a silent slot tax");

                    if (definition.IsInteger)
                    {
                        Assert.IsTrue(magnitude >= 1.0,
                            $"{definition.Id} at scale {scale} resolved to {magnitude}: an integer native is floored at 1 whenever the unrounded value is above 0");
                    }
                }

                // and a muted scale drops everything EXCEPT the binary modifier, which ignores the scale by design
                var muted = WeaponModValue.Resolve(definition, 1.0, 10.0, 0.0);

                if (definition.Binary)
                {
                    Assert.AreEqual(definition.MaxRoll, muted, 1e-12, $"{definition.Id} is binary and must ignore the scale entirely");
                    Assert.IsTrue(WeaponModValue.IsLiveMagnitude(definition, muted), $"{definition.Id} is binary, so its slot is never dead");
                }
                else
                {
                    Assert.AreEqual(0.0, muted, 1e-12, $"{definition.Id} at a muted scale");
                    Assert.IsFalse(WeaponModValue.IsLiveMagnitude(definition, muted), $"{definition.Id} at a muted scale must not be applied");
                }
            }
        }

        // ================= FIX 7 - defence in depth on the three-special bound =================

        /// <summary>
        /// THE GAP: the permanent three-special bound had exactly ONE enforcement point on the swap path -
        /// ResolveRefusal returning SwapAtSpecialCap. ApplySwap itself would happily remove one of a trio and roll
        /// a replacement, which is exactly the "reroll a single special" capability design section 5 says
        /// invalidates its power assessment ("Remove either property and the pricing in this section stops
        /// holding"). Any caller that reached ApplySwap without the refusal in front of it reopened that.
        ///
        /// This constructs the trio DIRECTLY rather than playing the swap for it, and calls ApplySwap with no
        /// refusal anywhere in the picture, so the guard is what is under test and nothing else.
        /// </summary>
        [TestMethod]
        public void Fix7_ApplySwapRefusesAtThreeSpecialsIndependentlyOfTheRefusal()
        {
            var sevenIron = Log(MaterialType.Iron, MaterialType.Iron, MaterialType.Iron, MaterialType.Iron,
                                MaterialType.Iron, MaterialType.Iron, MaterialType.Iron);

            var weapon = MakeWeapon(ints: new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.NumTimesTinkered, WeaponModRegistry.TotalSlots },
                    { PropertyInt.WeaponModTinkerCount, 7 },
                },
                strings: new Dictionary<PropertyString, string>
                {
                    { PropertyString.TinkerLog, sevenIron },
                    { PropertyString.WeaponModTinkerLog, sevenIron },
                });

            var trio = new[] { WeaponModId.Devastation, WeaponModId.WeakPoint, WeaponModId.Bloodthirst };

            foreach (var id in trio)
                WeaponModTinkerSet.ApplySpecial(weapon, WeaponModRegistry.Get(id), 5.0);

            Assert.AreEqual(WeaponModRegistry.MaxSpecials, WeaponModTinkerSet.SpecialCount(weapon), "precondition: a full trio");

            // ---- the guard, with no refusal in front of it ----
            Assert.IsNull(WeaponModManager.ApplySwap(weapon, WeaponClass.Melee, 10.0),
                "ApplySwap ran on a weapon already holding three specials: a caller that skips ResolveRefusal can then reroll one special of a chosen trio, which invalidates the design's +101.1% ceiling");

            Assert.IsFalse(WeaponModManager.CanApply(weapon, WeaponClass.Melee, WeaponModManager.WeaponModAction.Swap),
                "CanApply must mirror the guard: the bag is consumed on its word alone, so a yes here eats the bag for a refusal");

            // ---- and the refused swap changed absolutely nothing ----
            foreach (var id in trio)
            {
                Assert.AreEqual(5.0, weapon.GetProperty(WeaponModRegistry.Get(id).Record).Value, 1e-12,
                    $"the refused swap altered {id}'s record");
            }

            Assert.AreEqual(sevenIron, weapon.GetProperty(PropertyString.WeaponModTinkerLog), "the refused swap rewrote the log");
            Assert.AreEqual(7, weapon.GetProperty(PropertyInt.WeaponModTinkerCount), "the refused swap moved the tinker count");

            // ---- the two enforcement points agree rather than being alternatives ----
            Assert.AreEqual(WeaponModManager.WeaponModRefusal.SwapAtSpecialCap,
                WeaponModManager.ResolveRefusal(WeaponModManager.WeaponModAction.Swap, weapon),
                "the refusal must still be the one the player sees");

            // ---- dropping back to two reopens the swap, so the guard is a bound and not a one-way lock ----
            WeaponModTinkerSet.ReverseSpecial(weapon, WeaponModRegistry.Get(WeaponModId.Bloodthirst));

            Assert.AreEqual(2, WeaponModTinkerSet.SpecialCount(weapon));
            Assert.IsTrue(WeaponModManager.CanApply(weapon, WeaponClass.Melee, WeaponModManager.WeaponModAction.Swap));
            Assert.IsNotNull(WeaponModManager.ApplySwap(weapon, WeaponClass.Melee, 10.0), "a weapon below the bound must still accept a swap");
        }

        /// <summary>
        /// CanApply exists to guarantee the bag is never consumed for a no-op, so it has to agree with the apply
        /// path in BOTH directions on every state that reaches it - including the two states this change added:
        /// a weapon at the three-special bound, and a muted magnitude scale (which is NOT a refusal, because the
        /// draw converts to a tinker). Driven over a long random walk rather than the three worked cases, because
        /// a disagreement is a free swap or a bag eaten for nothing and neither is visible in a single sample.
        /// </summary>
        [TestMethod]
        public void Fix7_CanApplyAgreesWithApplySwapOnEveryStateIncludingTheNewOnes()
        {
            var priorScale = PropertyManager.GetDouble("weapon_mod_magnitude_scale").Item;

            try
            {
                foreach (var scale in new[] { 1.0, 0.0 })
                {
                    PropertyManager.ModifyDouble("weapon_mod_magnitude_scale", scale);

                    foreach (var weaponClass in new[] { WeaponClass.Melee, WeaponClass.Missile, WeaponClass.Caster })
                    {
                        var weapon = WeaponModTestKit.MakeHandTinkered(weaponClass);

                        var refusals = 0;

                        for (var i = 1; i <= 60; i++)
                        {
                            var canSwap = WeaponModManager.CanApply(weapon, weaponClass, WeaponModManager.WeaponModAction.Swap);
                            var swapped = WeaponModManager.ApplySwap(weapon, weaponClass, 10.0) != null;

                            Assert.AreEqual(canSwap, swapped,
                                $"scale {scale} {weaponClass} use {i}: CanApply said {canSwap} but ApplySwap {(swapped ? "ran" : "refused")}");

                            if (!swapped)
                            {
                                refusals++;

                                Assert.AreEqual(WeaponModRegistry.MaxSpecials, WeaponModTinkerSet.SpecialCount(weapon),
                                    $"scale {scale} {weaponClass} use {i}: the swap refused for a reason other than the three-special bound");

                                // a reroll is the way back out of a full trio
                                Assert.IsNotNull(WeaponModManager.ApplyReroll(weapon, weaponClass, 10.0));
                            }

                            var canReroll = WeaponModManager.CanApply(weapon, weaponClass, WeaponModManager.WeaponModAction.Reroll);

                            Assert.AreEqual(canReroll, WeaponModManager.ApplyReroll(weapon, weaponClass, 10.0) != null,
                                $"scale {scale} {weaponClass} use {i}: CanApply and ApplyReroll disagree");
                        }

                        // a muted scale can never reach the bound, so the refusal is reachable only at scale 1
                        if (scale == 0.0 && weaponClass != WeaponClass.Melee)
                            Assert.AreEqual(0, refusals, $"{weaponClass} at a muted scale can hold no special, so no swap may refuse");
                    }
                }
            }
            finally
            {
                PropertyManager.ModifyDouble("weapon_mod_magnitude_scale", priorScale);
            }
        }
    }
}
