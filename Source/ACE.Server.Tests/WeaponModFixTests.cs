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
                WeaponModManager.ResolveRefusal(WeaponModManager.WeaponModAction.Reroll, WeaponClass.Melee, true, true,
                    WeaponModTinkerSet.ReadReservedSlots(tenOak)),
                "a weapon whose whole budget is unreversible must be refused, not refilled");

            // ... but only the REROLL is refused. Since 2026-08-07 the same ten-Oak weapon is Amethyst-able,
            // because Amethyst neither reverses nor writes a tinker and so cannot be harmed by a budget it
            // cannot touch. The trap this test guards is the reroll silently refilling on top of Oak; the swap
            // was never capable of that.
            Assert.AreEqual(WeaponModManager.WeaponModRefusal.None,
                WeaponModManager.ResolveRefusal(WeaponModManager.WeaponModAction.Swap, WeaponClass.Melee, true, true,
                    WeaponModTinkerSet.ReadReservedSlots(tenOak)),
                "ten Oak must NOT refuse a swap - the tinker budget has no bearing on Amethyst");
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
            Assert.AreEqual(5, WeaponModTinkerSet.ComputeTinkerCount(WeaponModTinkerSet.ReadReservedSlots(mixed)),
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
            Assert.AreEqual(9, WeaponModTinkerSet.ComputeTinkerCount(WeaponModTinkerSet.ReadReservedSlots(imbued)));
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

                        Assert.IsTrue(WeaponModTinkerSet.ComputeTinkerCount(reserved) >= 0);
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

        // ================= FIX 3 - a RemoveOnDefault == false row must not delete its native at the default =================

        /// <summary>
        /// THE DEFECT, AS ORIGINALLY FOUND: a reversal landing exactly on NativeDefault removed the row for
        /// every modifier. Swift Flight's default was 20.0 (Creature_Missile.cs:517) and five weenies (518,
        /// 521, 531, 537, 23109) carried MaximumVelocity at exactly 20.0, so a reversal deleted a real row -
        /// and the read sites disagree (WeaponProfile.cs:57 falls back to 1.0), which left the appraisal panel
        /// reporting velocity 1.0.
        ///
        /// RETARGETED TO A SYNTHETIC DEFINITION, 2026-08-17. Swift Flight was retired in the catalog v4 pass
        /// and was the only row that ever set RemoveOnDefault = false, so this test would otherwise lose its
        /// only live subject. THE TEST IS KEPT AND RETARGETED RATHER THAN DELETED WITH THE ROW, for the same
        /// reason WeaponModTests.Machinery_ABinaryFlooredModifierAppliesFlatAndReversesBackToAbsent was
        /// retargeted when Cleave went: the RemoveOnDefault machinery is still in WeaponModDefinition, still
        /// reachable, and still the correct handling for the next native whose read sites disagree about what
        /// absent means. Deleting the coverage along with its only current caller is how that machinery
        /// silently rots into something that no longer works when a row finally needs it again.
        /// </summary>
        [TestMethod]
        public void Fix3_ARemoveOnDefaultFalseRowRestoresTheDefaultRatherThanRemovingTheRow()
        {
            // the shape Swift Flight had: a native whose disagreeing read sites make "absent" NOT equivalent to
            // its own default
            var definition = new WeaponModDefinition
            {
                Id = WeaponModId.PanicReload,          // any id; nothing here reads the registry
                DisplayName = "Synthetic Disagreeing Default",
                Record = PropertyFloat.WeaponModPanicReload,
                NativeFloat = PropertyFloat.MaximumVelocity,
                MaxRoll = 4.0,
                NativeDefault = 20.0,
                RemoveOnDefault = false,
                Classes = WeaponClass.Missile,
                DisplayFormat = "+{0:0.##} missile velocity",
            };

            var launcher = MakeWeapon(ItemType.MissileWeapon, CombatUse.Missile,
                floats: new Dictionary<PropertyFloat, double> { { PropertyFloat.MaximumVelocity, 20.0 } });

            WeaponModTinkerSet.ApplySpecial(launcher, definition, 4.0);
            Assert.AreEqual(24.0, launcher.GetProperty(PropertyFloat.MaximumVelocity).Value, 1e-9);

            WeaponModTinkerSet.ReverseSpecial(launcher, definition);

            var restored = launcher.GetProperty(PropertyFloat.MaximumVelocity);

            Assert.IsNotNull(restored, "removing the row makes WeaponProfile.cs:57 read velocity 1.0 on an unchanged bow");
            Assert.AreEqual(20.0, restored.Value, 1e-9);
            Assert.IsNull(launcher.GetProperty(definition.Record), "the record row is still cleared, never zeroed");
        }

        // Fix3_CleaveStillRemovesItsRowOnReversal sat here until 2026-08-07. It pinned the OTHER direction of
        // the removal-on-default rule - a native consumed as a null TEST (Cleaving, WorldObject_Weapon.cs:47-62)
        // must have its row removed rather than have its default written back, or the weapon stays flagged
        // forever. Cleave was removed from the catalog that day and was the only row of that shape, so the rule
        // has no live subject here any more. It is NOT untested: the same arithmetic is driven over a synthetic
        // definition in WeaponModTests.Machinery_ABinaryFlooredModifierAppliesFlatAndReversesBackToAbsent,
        // which was retargeted rather than deleted for exactly this reason.

        /// <summary>
        /// Every LIVE registry row currently keeps removal-on-default: absent unambiguously reads 0 (or, for
        /// Mana Well, is simply not consulted) at their read sites, so removing the row is the correct restore
        /// and leaves no junk behind. Swift Flight was the only row that ever opted out, and it was retired
        /// 2026-08-17 in the catalog v4 pass, so the live registry currently has NO opt-outs - the opt-out
        /// machinery itself survives, exercised synthetically above.
        /// </summary>
        [TestMethod]
        public void Fix3_NoLiveRowCurrentlyOptsOutOfRemovalOnDefault()
        {
            var optedOut = WeaponModRegistry.AllMods.Where(m => !m.RemoveOnDefault).Select(m => m.Id).ToList();

            CollectionAssert.AreEquivalent(Array.Empty<WeaponModId>(), optedOut,
                "no live registry row should opt out of RemoveOnDefault right now - Swift Flight was the only one and it is retired");
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

                // After the reroll the weapon carries a set, so the swap has something to reroll.
                //
                // CHANGED TWICE, and the history is the point. It first read Assert.AreEqual(!atCap, canSwap)
                // - being AT the cap was a refusal, because the swap's job was ADDING a special. The
                // 2026-08-06 rework made it a special-only REROLL, so the cap stopped being a refusal and the
                // rule became "holding at least one special". The 2026-08-07 directive removed that too:
                // Amethyst ADDS a first special when none are held, so EVERY special count is workable and
                // CanApply is now unconditionally true for a valid weapon.
                //
                // The middle version failed INTERMITTENTLY rather than always, which is why it survived its
                // rework unnoticed: the assertion only bit on a run where the reroll happened to land a full
                // set, roughly one run in ten. The unconditional form below cannot hide that way.
                var specialsHeld = WeaponModTinkerSet.SpecialCount(fresh);

                var canSwap = WeaponModManager.CanApply(fresh, weaponClass, WeaponModManager.WeaponModAction.Swap);

                Assert.IsTrue(canSwap,
                    $"{weaponClass}: CanApply refused a swap on a weapon holding {specialsHeld} specials - no special count may refuse Amethyst, zero included");

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

            // INVERTED 2026-08-07. An empty weapon used to be the swap's one refusal; Amethyst now ADDS a
            // first special instead, so this is a working case and both sides must say so. It is kept here,
            // inverted, rather than deleted - this test's subject is CanApply agreeing with the apply path,
            // and the empty weapon is still the state most likely to make them disagree.
            Assert.IsTrue(WeaponModManager.CanApply(weapon, WeaponClass.Melee, WeaponModManager.WeaponModAction.Swap),
                "a weapon holding no specials must be swappable - Amethyst adds a first one");

            var onEmpty = WeaponModManager.ApplySwap(weapon, WeaponClass.Melee, 10.0);

            Assert.IsNotNull(onEmpty, "ApplySwap must not refuse a weapon holding no specials");
            Assert.AreEqual(1, WeaponModTinkerSet.SpecialCount(weapon),
                "a swap onto an empty weapon must leave exactly one special - it adds, it does not remove first");
            Assert.IsFalse(onEmpty.Any(l => l.StartsWith("Lost:", StringComparison.Ordinal)),
                "nothing was held, so nothing may be reported as lost");
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

        /// <summary>
        /// A life caster stoned BEFORE GetRend grew its Health branch was stamped with the old mapping's
        /// CriticalStrike fallback (BlackOpal closing the log). The signature must keep matching that legacy
        /// stamp: recomputing only the new HealthRending shape would silently unseal every pre-fix life wand,
        /// and the Swap path has no integrity-gate backstop (PassesIntegrityGate is reroll-only by the
        /// 2026-08-07 policy), so an unsealed wand would open to Amethyst on a weapon the game promised
        /// "can never be tinkered again".
        /// </summary>
        [TestMethod]
        public void DriftStone_StillRefusesALifeWandStonedUnderTheOldCriticalStrikeMapping()
        {
            foreach (var (imbue, rendMaterial, label) in new[]
            {
                (ImbuedEffectType.CriticalStrike, PrismaticDriftStone.GetRendMaterial(ImbuedEffectType.CriticalStrike), "legacy pre-Health stamp"),
                (ImbuedEffectType.HealthRending,  PrismaticDriftStone.GetRendMaterial(ImbuedEffectType.HealthRending),  "current Health stamp"),
            })
            {
                var materials = Enumerable.Repeat(MaterialType.GreenGarnet, PrismaticDriftStone.MinBoosts).ToList();
                materials.Add(rendMaterial);

                var lifeWand = MakeWeapon(ItemType.Caster, null,
                    new Dictionary<PropertyInt, int>
                    {
                        { PropertyInt.NumTimesTinkered, PrismaticDriftStone.LockedTinkerCount },
                        { PropertyInt.DamageType, (int)DamageType.Health },
                        { PropertyInt.ImbuedEffect, (int)imbue },
                    },
                    strings: new Dictionary<PropertyString, string> { { PropertyString.TinkerLog, Log(materials.ToArray()) } });

                Assert.IsTrue(PrismaticDriftStone.MatchesAppliedSignature(lifeWand), label);
                Assert.AreEqual(WeaponModManager.WeaponModRefusal.DriftStoneLocked,
                    WeaponModManager.ResolveDriftStoneRefusal(lifeWand), label);
            }

            // the legacy fallback must not over-accept: a Health wand carrying a rend NEITHER mapping
            // could have produced is not a drift-stone signature
            var mismatched = MakeWeapon(ItemType.Caster, null,
                new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.NumTimesTinkered, PrismaticDriftStone.LockedTinkerCount },
                    { PropertyInt.DamageType, (int)DamageType.Health },
                    { PropertyInt.ImbuedEffect, (int)ImbuedEffectType.FireRending },
                },
                strings: new Dictionary<PropertyString, string>
                {
                    { PropertyString.TinkerLog, Log(MaterialType.GreenGarnet, MaterialType.GreenGarnet,
                        PrismaticDriftStone.GetRendMaterial(ImbuedEffectType.FireRending)) },
                });

            Assert.IsFalse(PrismaticDriftStone.MatchesAppliedSignature(mismatched),
                "a Health wand with a rend neither the old nor the new mapping produces must not match");
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
        /// How many rows the four LIVE record bands hold between them - 8130-8134 for Tier A, 8141-8147 for
        /// Tier B v2, 8021-8026 for the Tier B v3 expansion, and 8027-8035 for the Tier B v4 expansion. One per
        /// held special, never more. All four, because all four are written and cleared by the same
        /// ApplySpecial / ReverseSpecial pair, so counting only the original Tier A band would miss a zeroed
        /// v2, v3 or v4 Tier B row. Omitting the v4 band here would leave a stuck record at 8027-8035 invisible
        /// to this harness.
        /// </summary>
        private static int BandRows(WorldObject weapon)
        {
            var rows = 0;

            for (var id = WeaponModRegistry.PropertyBandStart; id <= WeaponModRegistry.PropertyBandEnd; id++)
            {
                if (weapon.GetProperty((PropertyFloat)id) != null)
                    rows++;
            }

            for (var id = WeaponModRegistry.TierBExpansionBandStart; id <= WeaponModRegistry.TierBExpansionBandEnd; id++)
            {
                if (weapon.GetProperty((PropertyFloat)id) != null)
                    rows++;
            }

            for (var id = WeaponModRegistry.TierBV4BandStart; id <= WeaponModRegistry.TierBV4BandEnd; id++)
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
        /// RENAMED AND REWRITTEN 2026-08-07 (was
        /// Fix6_AMutedMagnitudeScaleSpendsEverySlotOnTinkersRatherThanOnDeadRecords). Two changes landed on the
        /// same day and between them removed both halves of the old name:
        ///
        ///   - THE SPECIAL-ONLY REROLL means a dropped special's slot no longer "converts to a tinker". There
        ///     is no conversion target: the reroll lays down no tinkers at all, so a muted scale simply yields
        ///     a weapon holding nothing, with its layer 1 exactly as the player left it.
        ///   - THE CLEAVE REMOVAL means there is no longer a binary modifier to survive the mute. Cleave was
        ///     the reason the melee arm asserted something different from the other two classes; with it gone,
        ///     all three classes assert the same thing and the arms are merged.
        ///
        /// The rule the test is actually about is UNCHANGED and is the one thing both arms always agreed on: a
        /// zero magnitude is never applied and never written. Only a ZERO magnitude is dropped, never a small
        /// one. The special chances are forced to certainty so the drop path runs on every single iteration.
        /// </summary>
        [TestMethod]
        public void Fix6_AMutedMagnitudeScaleHoldsNoSpecialAndLeavesLayerOneAlone()
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

                // no class carries a binary modifier since the 2026-08-07 Cleave removal, so at a muted scale
                // NOTHING may be held on any of them
                foreach (var weaponClass in new[] { WeaponClass.Melee, WeaponClass.Missile, WeaponClass.Caster })
                {
                    var itemType = weaponClass == WeaponClass.Missile ? ItemType.MissileWeapon
                        : weaponClass == WeaponClass.Caster ? ItemType.Caster : ItemType.MeleeWeapon;

                    var combatUse = weaponClass == WeaponClass.Missile ? CombatUse.Missile : CombatUse.Melee;

                    for (var i = 1; i <= 40; i++)
                    {
                        // a HAND-TINKERED weapon, deliberately: an untinkered one would pass the layer 1
                        // assertions below vacuously, and layer 1 surviving the mute is half of what is at stake
                        var weapon = WeaponModTestKit.MakeHandTinkered(weaponClass);

                        var beforeLog = weapon.GetProperty(PropertyString.TinkerLog);
                        var beforeCount = weapon.GetProperty(PropertyInt.NumTimesTinkered);

                        Assert.IsNotNull(WeaponModManager.ApplyReroll(weapon, weaponClass, 10.0), $"{weaponClass} reroll {i}");

                        Assert.AreEqual(0, WeaponModTinkerSet.SpecialCount(weapon),
                            $"{weaponClass} reroll {i}: a special resolving to zero magnitude must not be held");

                        Assert.AreEqual(0, BandRows(weapon),
                            $"{weaponClass} reroll {i}: the reserved record bands hold a row for a special that buys nothing");

                        // and the muted layer left the player's tinkering completely alone
                        Assert.AreEqual(beforeLog, weapon.GetProperty(PropertyString.TinkerLog),
                            $"{weaponClass} reroll {i}: the retail tinker log moved at a muted scale");

                        Assert.AreEqual(beforeCount, weapon.GetProperty(PropertyInt.NumTimesTinkered),
                            $"{weaponClass} reroll {i}: NumTimesTinkered moved at a muted scale");

                        Assert.IsNull(weapon.GetProperty(PropertyInt.WeaponModTinkerCount),
                            $"{weaponClass} reroll {i}: a tinker count was written. A dropped special no longer converts to a tinker - since 2026-08-07 there is no tinker for it to convert INTO");

                        Assert.IsNull(weapon.GetProperty(PropertyString.WeaponModTinkerLog),
                            $"{weaponClass} reroll {i}: this system's own tinker log was written on a weapon whose tinkers it does not manage");
                    }
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
        /// SUPERSEDED 2026-08-06 (Amethyst rework). Before the rework, a swap draw that resolved to a
        /// zero-magnitude special converted to a TINKER instead of refusing. Amethyst no longer touches tinkers
        /// at all, so there is no conversion target any more: a muted-scale replacement simply is not applied,
        /// and the removed special is lost for that use - never refused, and never leaves a dead record in the
        /// reserved band.
        /// </summary>
        [TestMethod]
        public void Fix6_AMutedSwapReplacementIsSimplyLostRatherThanRefusingOrLeavingADeadRecord()
        {
            var priorScale = PropertyManager.GetDouble("weapon_mod_magnitude_scale").Item;

            try
            {
                // a weapon holding one non-binary special to start from, so the swap has something to reroll
                var weapon = WeaponModTestKit.MakeHandTinkered(WeaponClass.Missile);
                WeaponModTinkerSet.ApplySpecial(weapon, WeaponModRegistry.Get(WeaponModId.ShieldBypass), 0.3);

                PropertyManager.ModifyDouble("weapon_mod_magnitude_scale", 0.0);

                for (var i = 1; i <= 20; i++)
                {
                    var before = WeaponModTinkerSet.SpecialCount(weapon);

                    Assert.IsTrue(WeaponModManager.CanApply(weapon, WeaponClass.Missile, WeaponModManager.WeaponModAction.Swap),
                        $"swap {i}: CanApply must say yes whenever a special is held, whatever the scale is set to");

                    Assert.IsNotNull(WeaponModManager.ApplySwap(weapon, WeaponClass.Missile, 10.0),
                        $"swap {i}: a zero-magnitude replacement must not refuse; refusing here would eat the bag for nothing");

                    // the removed special is lost and the muted replacement never lands, so the count can only
                    // ever go down or stay put (a fresh special could theoretically be re-added by a later use,
                    // but never at a muted scale) - and never leaves a dead record in the reserved band
                    Assert.IsTrue(WeaponModTinkerSet.SpecialCount(weapon) <= before, $"swap {i}: special count rose at a muted scale");
                    Assert.AreEqual(WeaponModTinkerSet.SpecialCount(weapon), BandRows(weapon), $"swap {i}: dead rows in the reserved band");

                    if (WeaponModTinkerSet.SpecialCount(weapon) == 0)
                        break; // nothing left to reroll on the next use
                }
            }
            finally
            {
                PropertyManager.ModifyDouble("weapon_mod_magnitude_scale", priorScale);
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
        /// SUPERSEDED 2026-08-06. Before the Amethyst rework, ApplySwap refused a weapon already at the special
        /// cap - the guard this test originally pinned. That guard is GONE ON PURPOSE: Amethyst is now a
        /// special-only reroll, so a weapon AT the cap is exactly the state it exists to act on. This test now
        /// pins the OPPOSITE claim - a full set is swappable, exactly one special changes, tinkers and the
        /// tinker log are UNTOUCHED (the new path never reaches them at all), and the still-held specials'
        /// records survive exactly.
        /// </summary>
        [TestMethod]
        public void Fix7_ApplySwapRerollsOneSpecialOfAFullSetAndLeavesTinkersUntouched()
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

            // a full SET: the cap went from three to four on 2026-08-06, so a fourth row is needed. It was
            // Cleave until 2026-08-07; Shield Bypass is what the melee Tier A pool has left.
            var set = new[]
            {
                (WeaponModId.Devastation,  5.0),
                (WeaponModId.WeakPoint,    3.0),
                (WeaponModId.Bloodthirst,  5.0),
                (WeaponModId.ShieldBypass, 0.3),
            };

            foreach (var (id, magnitude) in set)
                WeaponModTinkerSet.ApplySpecial(weapon, WeaponModRegistry.Get(id), magnitude);

            Assert.AreEqual(WeaponModRegistry.MaxSpecials, WeaponModTinkerSet.SpecialCount(weapon), "precondition: a full set");

            Assert.AreEqual(WeaponModManager.WeaponModRefusal.None,
                WeaponModManager.ResolveRefusal(WeaponModManager.WeaponModAction.Swap, weapon),
                "a full set must be swappable now - rerolling AT the cap is the tool's purpose");

            Assert.IsTrue(WeaponModManager.CanApply(weapon, WeaponClass.Melee, WeaponModManager.WeaponModAction.Swap));

            var lines = WeaponModManager.ApplySwap(weapon, WeaponClass.Melee, 10.0);

            Assert.IsNotNull(lines, "ApplySwap must succeed on a full set");
            Assert.AreEqual(WeaponModRegistry.MaxSpecials, WeaponModTinkerSet.SpecialCount(weapon), "a reroll trades one special for one - the count never moves");

            // tinkers and the tinker log are UNTOUCHED - the new path never reaches them
            Assert.AreEqual(sevenIron, weapon.GetProperty(PropertyString.WeaponModTinkerLog), "the swap must not touch the tinker log");
            Assert.AreEqual(7, weapon.GetProperty(PropertyInt.WeaponModTinkerCount), "the swap must not touch the tinker count");
        }

        /// <summary>
        /// REWORKED TWICE. CanApply exists to guarantee the bag is never consumed for a no-op, so it has to
        /// agree with the apply path in BOTH directions on every state that reaches it.
        ///
        /// As of the 2026-08-07 directive there is no swap refusal left at all, so "they agree" now means they
        /// agree on YES, every time, at every special count. The muted-scale arm is what makes that
        /// non-trivial: at scale 0 every replacement resolves to zero and is dropped, so repeated swaps drain
        /// the weapon to zero specials - and it must STILL never refuse. That drain used to end in a refusal
        /// (SwapNeedsASpecial), and this test is the one that would have caught the change silently.
        ///
        /// The bag is still consumed for a no-op in that muted case, which is correct and unchanged: the same
        /// is true of Tourmaline at scale 0, and a muted layer is an operator decision rather than a state the
        /// refusal table is meant to protect players from.
        /// </summary>
        [TestMethod]
        public void Fix7_CanApplyAgreesWithApplySwapOnEveryStateIncludingTheNewOnes()
        {
            var priorScale = PropertyManager.GetDouble("weapon_mod_magnitude_scale").Item;
            var prior1 = PropertyManager.GetDouble("weapon_mod_special_chance_1").Item;
            var prior2 = PropertyManager.GetDouble("weapon_mod_special_chance_2").Item;
            var prior3 = PropertyManager.GetDouble("weapon_mod_special_chance_3").Item;
            var prior4 = PropertyManager.GetDouble("weapon_mod_special_chance_4").Item;

            try
            {
                foreach (var scale in new[] { 1.0, 0.0 })
                {
                    foreach (var weaponClass in new[] { WeaponClass.Melee, WeaponClass.Missile, WeaponClass.Caster })
                    {
                        // seed at least one special via a forced-odds reroll at scale 1, THEN switch to the
                        // scale under test - a muted scale can never seed a non-binary special in the first
                        // place (WeaponModValue.IsLiveMagnitude), so seeding has to happen before it is applied
                        PropertyManager.ModifyDouble("weapon_mod_magnitude_scale", 1.0);
                        PropertyManager.ModifyDouble("weapon_mod_special_chance_1", 1.0);
                        PropertyManager.ModifyDouble("weapon_mod_special_chance_2", 1.0);
                        PropertyManager.ModifyDouble("weapon_mod_special_chance_3", 1.0);
                        PropertyManager.ModifyDouble("weapon_mod_special_chance_4", 1.0);

                        var weapon = WeaponModTestKit.MakeHandTinkered(weaponClass);
                        Assert.IsNotNull(WeaponModManager.ApplyReroll(weapon, weaponClass, 10.0));
                        Assert.IsTrue(WeaponModTinkerSet.SpecialCount(weapon) > 0, $"scale {scale} {weaponClass}: seeding reroll produced no special");

                        PropertyManager.ModifyDouble("weapon_mod_magnitude_scale", scale);

                        for (var i = 1; i <= 60; i++)
                        {
                            var canSwap = WeaponModManager.CanApply(weapon, weaponClass, WeaponModManager.WeaponModAction.Swap);
                            var swapped = WeaponModManager.ApplySwap(weapon, weaponClass, 10.0) != null;

                            Assert.AreEqual(canSwap, swapped,
                                $"scale {scale} {weaponClass} use {i}: CanApply said {canSwap} but ApplySwap {(swapped ? "ran" : "refused")}");

                            Assert.IsTrue(canSwap,
                                $"scale {scale} {weaponClass} use {i}: a swap was refused at {WeaponModTinkerSet.SpecialCount(weapon)} specials - since 2026-08-07 no special count may refuse one");
                        }

                        // at scale 0 every replacement is dropped without being re-added, so sixty uses drain
                        // the weapon completely. The point is that it keeps saying YES the whole way down and
                        // at the bottom - the drain used to end in a refusal, and that refusal is gone.
                        if (scale == 0.0)
                        {
                            Assert.AreEqual(0, WeaponModTinkerSet.SpecialCount(weapon),
                                $"{weaponClass} at a muted scale must drain to zero specials over sixty swaps");

                            Assert.IsTrue(WeaponModManager.CanApply(weapon, weaponClass, WeaponModManager.WeaponModAction.Swap),
                                $"{weaponClass}: a fully drained weapon must still accept a swap");
                        }
                    }
                }
            }
            finally
            {
                PropertyManager.ModifyDouble("weapon_mod_magnitude_scale", priorScale);
                PropertyManager.ModifyDouble("weapon_mod_special_chance_1", prior1);
                PropertyManager.ModifyDouble("weapon_mod_special_chance_2", prior2);
                PropertyManager.ModifyDouble("weapon_mod_special_chance_3", prior3);
                PropertyManager.ModifyDouble("weapon_mod_special_chance_4", prior4);
            }
        }
    }
}
