using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Managers;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Interaction coverage for the weapon-mod system: imbues, the standard retail tinkering the system has to
    /// share a weapon with, exact quantities, the per-class pools, the refusal matrix over REAL items, and the
    /// tunable extremes.
    ///
    /// Several tests here assert the CORRECT behaviour of confirmed defects and therefore FAIL against the code
    /// as it stands. Each one says so in its own doc comment and names the source line. They are deliberately not
    /// weakened, skipped or ignored: they are the specification for the fix.
    ///
    /// Not reachable from a unit test, and left to the live loop: the whole player-facing half of
    /// WeaponModManager.UseObjectOnTarget (busy guard, peace-mode guard, ClapHands chain and the second
    /// VerifyUseRequirements behind it, NextUseTime) and the source-equals-target guard in
    /// VerifyUseRequirements, which calls player.SendTransientError before returning and so cannot run without
    /// a Player with a session. The confirmation round trip is no longer on that list because this system no
    /// longer confirms - the client's own tinkering prompt is the gate as of 2026-07-30.
    /// </summary>
    [TestClass]
    public class WeaponModInteractionTests
    {
        private static readonly WeaponClass[] AllClasses = { WeaponClass.Melee, WeaponClass.Missile, WeaponClass.Caster };

        /// <summary>Real ImbuedEffectType bits, high bits first so every popcount sweep exercises them.</summary>
        private static readonly ImbuedEffectType[] ImbueBits =
        {
            ImbuedEffectType.IgnoreAllArmor,                    // 0x80000000
            ImbuedEffectType.AlwaysCritical,                    // 0x40000000
            ImbuedEffectType.IgnoreSomeMagicProjectileDamage,   // 0x20000000
            ImbuedEffectType.CriticalStrike,
            ImbuedEffectType.CripplingBlow,
            ImbuedEffectType.ArmorRending,
            ImbuedEffectType.SlashRending,
            ImbuedEffectType.PierceRending,
            ImbuedEffectType.BludgeonRending,
            ImbuedEffectType.AcidRending,
            ImbuedEffectType.ColdRending,
            ImbuedEffectType.ElectricRending,
        };

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        private static int ImbueMask(int bits)
        {
            var mask = 0;

            for (var i = 0; i < bits; i++)
                mask |= unchecked((int)ImbueBits[i]);

            return mask;
        }

        // ================= C. imbues =================

        /// <summary>
        /// Reserved slots 0 through 10, built from real ImbuedEffectType combinations including the three high
        /// bits, which are what an unclamped popcount gets wrong.
        /// </summary>
        [TestMethod]
        public void Imbues_ReservedSlotSweepZeroThroughTenUsingRealImbueCombinations()
        {
            for (var bits = 0; bits <= 10; bits++)
            {
                var mask = ImbueMask(bits);

                Assert.AreEqual(bits, WeaponModTinkerSet.ReservedImbueSlots(mask),
                    $"{bits} imbue bits (mask 0x{mask:X8}) must reserve exactly {bits} slots");

                var weapon = WeaponModTestKit.MakeUntinkered(WeaponClass.Melee, mask);

                Assert.AreEqual(bits, WeaponModTinkerSet.ReadReservedImbueSlots(weapon), $"{bits} imbue bits read off a real item");
                Assert.AreEqual(WeaponModRegistry.TotalSlots - bits, WeaponModTinkerSet.AvailableSlots(bits), $"{bits} imbue bits: available slots");

                var probe = new WeaponModProbe(weapon);

                Assert.IsNotNull(WeaponModManager.ApplyReroll(weapon, WeaponClass.Melee, 10.0), $"{bits} imbue bits: reroll produced no result");

                probe.AssertInvariants(weapon, WeaponClass.Melee, $"{bits} imbue bits after a reroll");

                var specials = WeaponModTinkerSet.SpecialCount(weapon);
                var tinkers = weapon.GetProperty(PropertyInt.WeaponModTinkerCount) ?? -1;

                Assert.IsTrue(tinkers >= 0, $"{bits} imbue bits: negative tinker count {tinkers}");
                Assert.IsTrue(specials <= Math.Min(WeaponModRegistry.MaxSpecials, WeaponModRegistry.TotalSlots - bits),
                    $"{bits} imbue bits: {specials} specials exceeds min(3, 10 - reserved)");
                Assert.AreEqual(WeaponModRegistry.TotalSlots, bits + specials + tinkers, $"{bits} imbue bits: the budget does not add to 10");
            }

            // every real bit at once - popcount 12, clamped to 10 rather than driving the tinker count to -2
            var everything = ImbueMask(ImbueBits.Length);

            Assert.AreEqual(WeaponModRegistry.TotalSlots, WeaponModTinkerSet.ReservedImbueSlots(everything),
                "an over-full imbue mask must clamp to 10, never exceed it");
            Assert.AreEqual(0, WeaponModTinkerSet.ComputeTinkerCount(WeaponModTinkerSet.ReservedImbueSlots(everything), 0));
        }

        [TestMethod]
        public void Imbues_AreByteIdenticalAfterEveryRerollAndEverySwap()
        {
            for (var bits = 0; bits <= 9; bits++)
            {
                var mask = ImbueMask(bits);

                foreach (var weaponClass in AllClasses)
                {
                    var weapon = WeaponModTestKit.MakeHandTinkered(weaponClass, mask);
                    var probe = new WeaponModProbe(weapon);

                    for (var i = 1; i <= 6; i++)
                    {
                        Assert.IsNotNull(WeaponModManager.ApplyReroll(weapon, weaponClass, 10.0), $"{weaponClass} {bits} imbues: reroll {i}");

                        Assert.AreEqual(mask, weapon.GetProperty(PropertyInt.ImbuedEffect),
                            $"{weaponClass} {bits} imbues: ImbuedEffect changed across reroll {i} - imbues are preserved by design and their loss is unrecoverable");

                        probe.AssertInvariants(weapon, weaponClass, $"{weaponClass} {bits} imbues reroll {i}");

                        // Swap() asserts the swap's own null contract: an empty weapon, or one already at the
                        // three-special bound. Here the reachable case is the imbues having taken every slot.
                        var lines = WeaponModTestKit.Swap(weapon, weaponClass, 10.0, $"{weaponClass} {bits} imbues swap {i}");

                        if (lines == null)
                            continue;

                        Assert.AreEqual(mask, weapon.GetProperty(PropertyInt.ImbuedEffect),
                            $"{weaponClass} {bits} imbues: ImbuedEffect changed across swap {i}");

                        probe.AssertInvariants(weapon, weaponClass, $"{weaponClass} {bits} imbues swap {i}");
                    }
                }
            }
        }

        /// <summary>
        /// Nine imbues leave exactly one movable slot. Thirty swaps must move only that one slot, never an imbue,
        /// and the ten-slot arithmetic must survive being run right up against its edge.
        /// </summary>
        [TestMethod]
        public void Imbues_NineReservedSlotsSurviveThirtySwapsWithOnlyTheOneFreeSlotMoving()
        {
            var mask = ImbueMask(9);

            var weapon = WeaponModTestKit.MakeHandTinkered(WeaponClass.Melee, mask);
            var probe = new WeaponModProbe(weapon);

            Assert.AreEqual(9, WeaponModTinkerSet.ReadReservedImbueSlots(weapon));

            for (var i = 1; i <= 30; i++)
            {
                Assert.IsNotNull(WeaponModManager.ApplySwap(weapon, WeaponClass.Melee, 10.0), $"swap {i}: produced no result");

                Assert.AreEqual(mask, weapon.GetProperty(PropertyInt.ImbuedEffect), $"swap {i}: an imbue bit was destroyed");

                probe.AssertInvariants(weapon, WeaponClass.Melee, $"nine imbues swap {i}");

                var specials = WeaponModTinkerSet.SpecialCount(weapon);
                var tinkers = weapon.GetProperty(PropertyInt.WeaponModTinkerCount) ?? -1;

                Assert.AreEqual(1, specials + tinkers, $"swap {i}: {specials} specials and {tinkers} tinkers - only ONE slot is free behind nine imbues");
                Assert.IsTrue(specials <= 1, $"swap {i}: {specials} specials cannot fit behind nine imbues");
            }
        }

        [TestMethod]
        public void Imbues_TenReservedSlotsAreRefusedRatherThanProducingANegativeTinkerCount()
        {
            var mask = ImbueMask(10);
            var weapon = WeaponModTestKit.MakeUntinkered(WeaponClass.Melee, mask);

            Assert.AreEqual(WeaponModManager.WeaponModRefusal.NoAvailableSlots,
                WeaponModTestKit.Refusal(WeaponModTestKit.MakeBag(MaterialType.Tourmaline), weapon),
                "a weapon whose imbues take all ten slots has nothing to reroll");

            Assert.AreEqual(WeaponModManager.WeaponModRefusal.NoAvailableSlots,
                WeaponModTestKit.Refusal(WeaponModTestKit.MakeBag(MaterialType.Amethyst), weapon),
                "a weapon whose imbues take all ten slots has nothing to swap");

            // and the arithmetic underneath is still safe if the application path is ever reached directly
            Assert.IsNotNull(WeaponModManager.ApplyReroll(weapon, WeaponClass.Melee, 10.0));

            Assert.AreEqual(0, weapon.GetProperty(PropertyInt.WeaponModTinkerCount), "ten reserved slots leaves no room for a tinker");
            Assert.AreEqual(0, WeaponModTinkerSet.SpecialCount(weapon), "ten reserved slots leaves no room for a special");
            Assert.AreEqual(WeaponModRegistry.TotalSlots, weapon.GetProperty(PropertyInt.NumTimesTinkered));
            Assert.AreEqual(mask, weapon.GetProperty(PropertyInt.ImbuedEffect));
        }

        // ================= D. standard retail tinkering interaction =================

        /// <summary>
        /// A part-tinkered retail weapon: the reroll ACCEPTS and fills the budget to ten, while the swap refuses,
        /// because a swap trades one slot for another and must not be a cheaper way of filling empty ones.
        /// </summary>
        [TestMethod]
        public void Retail_APartTinkeredWeaponAcceptsARerollAndRefusesASwap()
        {
            var weapon = WeaponModTestKit.MakeWeapon(ItemType.MeleeWeapon, CombatUse.Melee,
                new Dictionary<PropertyInt, int> { { PropertyInt.NumTimesTinkered, 3 }, { PropertyInt.Damage, 15 } });

            weapon.SetProperty(PropertyString.TinkerLog, "61,61,57");

            Assert.IsTrue(WeaponModTinkerSet.PassesIntegrityGate(weapon), "a 3-entry log against NumTimesTinkered 3 is consistent");

            Assert.AreEqual(WeaponModManager.WeaponModRefusal.SwapNeedsFullBudget,
                WeaponModTestKit.Refusal(WeaponModTestKit.MakeBag(MaterialType.Amethyst), weapon));

            Assert.AreEqual(WeaponModManager.WeaponModRefusal.None,
                WeaponModTestKit.Refusal(WeaponModTestKit.MakeBag(MaterialType.Tourmaline), weapon));

            var probe = new WeaponModProbe(weapon);

            Assert.IsNotNull(WeaponModManager.ApplyReroll(weapon, WeaponClass.Melee, 10.0));

            probe.AssertInvariants(weapon, WeaponClass.Melee, "reroll of a part-tinkered weapon");

            Assert.AreEqual(WeaponModRegistry.TotalSlots, weapon.GetProperty(PropertyInt.NumTimesTinkered),
                "the reroll fills the budget, so the counter lands on exactly ten");

            // the two retail Iron and one Brass were reversed before the refill: Damage is back off 15 by two
            // and then rebuilt, so it can only be inside the fresh band
            var damage = weapon.GetProperty(PropertyInt.Damage) ?? 0;

            Assert.IsTrue(damage >= 13 && damage <= 23, $"Damage {damage} is outside the band a 15-base weapon reaches once its two retail Iron are reversed");

            // ... and now that it is full, the swap is available - unless the reroll happened to land a full
            // trio, which is a 2% draw at the default odds and refuses for a completely different reason.
            //
            // PRE-EXISTING FLAKE, fixed 2026-07-30: this asserted None unconditionally and failed about one run
            // in fifty on the trio draw. The unconditional form is not a stronger assertion, it is a wrong one -
            // SwapAtSpecialCap is the correct answer in that state, and it is the SAME assertion for both.
            var specials = WeaponModTinkerSet.SpecialCount(weapon);

            Assert.AreEqual(specials >= WeaponModRegistry.MaxSpecials
                    ? WeaponModManager.WeaponModRefusal.SwapAtSpecialCap
                    : WeaponModManager.WeaponModRefusal.None,
                WeaponModTestKit.Refusal(WeaponModTestKit.MakeBag(MaterialType.Amethyst), weapon),
                $"a full budget makes the swap available, and only the three-special bound may take it away (the reroll left {specials} specials)");
        }

        /// <summary>
        /// The retail-era shape the integrity gate exists for: a tinker count that the log does not account for.
        /// Reversing a set you cannot fully see corrupts the item, so BOTH flows refuse.
        /// </summary>
        [TestMethod]
        public void Retail_ATenTinkerWeaponWithASevenEntryLogIsRefusedByBothFlows()
        {
            var weapon = WeaponModTestKit.MakeWeapon(ItemType.MeleeWeapon, CombatUse.Melee,
                new Dictionary<PropertyInt, int> { { PropertyInt.NumTimesTinkered, 10 }, { PropertyInt.Damage, 22 } });

            weapon.SetProperty(PropertyString.TinkerLog, "61,61,61,61,61,57,57");

            Assert.IsFalse(WeaponModTinkerSet.PassesIntegrityGate(weapon), "seven entries against NumTimesTinkered 10 must not pass");

            Assert.AreEqual(WeaponModManager.WeaponModRefusal.TinkerLogMismatch,
                WeaponModTestKit.Refusal(WeaponModTestKit.MakeBag(MaterialType.Tourmaline), weapon));

            Assert.AreEqual(WeaponModManager.WeaponModRefusal.TinkerLogMismatch,
                WeaponModTestKit.Refusal(WeaponModTestKit.MakeBag(MaterialType.Amethyst), weapon));

            // an unreadable log is refused the same way, rather than being treated as empty
            weapon.SetProperty(PropertyString.TinkerLog, "61,iron,57");
            Assert.IsFalse(WeaponModTinkerSet.PassesIntegrityGate(weapon), "a log that does not parse must not pass the gate");
        }

        /// <summary>
        /// Entries this system does not own are left STRICTLY alone. Oak is the case that matters: its DAT script
        /// is "WeaponTime -= 50" with a floor at 0, which nominal subtraction cannot reverse, so the system must
        /// never touch WeaponTime at all.
        /// </summary>
        [TestMethod]
        public void Retail_UnknownLogEntriesAreNeverReversedAndWeaponTimeIsUntouched()
        {
            // five Oak, an imbue salvage material and a raw wcid fallback entry, alongside four Iron
            var log = new List<uint> { 75, 75, 75, 75, 75, 38, 21082, 61, 61, 61 };

            var weapon = WeaponModTestKit.MakeWeapon(ItemType.MeleeWeapon, CombatUse.Melee,
                new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.NumTimesTinkered, 10 },
                    { PropertyInt.Damage, 18 },
                    { PropertyInt.WeaponTime, 0 },   // already floored by the first Oak, as 75% of retail weapons are
                });

            weapon.SetProperty(PropertyString.TinkerLog, string.Join(",", log));

            Assert.IsTrue(WeaponModTinkerSet.PassesIntegrityGate(weapon), "ten entries against NumTimesTinkered 10 is consistent");

            // only the three Iron are ours to reverse
            var composition = WeaponModTinkerSet.ReadComposition(weapon);

            Assert.AreEqual(3, composition.Count, "only the three Iron entries are materials this system owns");
            CollectionAssert.AreEqual(new[] { MaterialType.Iron, MaterialType.Iron, MaterialType.Iron }, composition);

            Assert.IsNotNull(WeaponModManager.ApplyReroll(weapon, WeaponClass.Melee, 10.0));

            Assert.AreEqual(0, weapon.GetProperty(PropertyInt.WeaponTime),
                "WeaponTime moved. Oak is not in any pool precisely because subtraction INFLATES a floored WeaponTime without bound, and there is no upper clamp at the combat read.");
        }

        /// <summary>
        /// CONFIRMED DEFECT (asserts the CORRECT behaviour and therefore fails against the current code).
        ///
        /// WeaponModTinkerSet.ReadComposition:144-152 filters the log to materials this system owns, but
        /// PassesIntegrityGate:299-302 counts ALL parsed entries against NumTimesTinkered. A weapon hand-tinkered
        /// with ten Oak (cookbook wcid 20989, reachable today) therefore passes the gate at 10 == 10, has NOTHING
        /// reversed, and is handed all ten slots fresh: twenty tinkers of effect on one weapon, which breaks the
        /// "10 = reserved + specials + tinkers" premise the power assessment is priced on.
        ///
        /// The unaccounted entries must reserve their own slots:
        ///
        ///     reserved = clamp(imbuePopcount + max(0, unknownLogEntries - imbuePopcount), 0, 10)
        ///
        /// so imbue salvage entries are absorbed by the popcount they already produced, while Oak and raw wcid
        /// entries cost what they are worth.
        /// </summary>
        [TestMethod]
        public void Slots_UnknownLogEntriesReserveTheirOwnSlots()
        {
            // ten Oak: nothing reversible, so nothing may be refilled
            AssertReserved(10, null, new uint[] { 75, 75, 75, 75, 75, 75, 75, 75, 75, 75 },
                "ten Oak entries account for the whole budget, so none of it is available");

            // five Oak, five Iron: half the budget is ours
            AssertReserved(5, null, new uint[] { 75, 75, 75, 75, 75, 61, 61, 61, 61, 61 },
                "five Oak entries hold five slots, leaving five to refill");

            // an imbue's salvage entry is unknown too, but the imbue bit it produced already reserved that slot -
            // charging for both would double-count
            AssertReserved(1, (int)ImbuedEffectType.CriticalStrike, new uint[] { 38, 61, 61, 61, 61, 61, 61, 61, 61, 61 },
                "one imbue and its salvage entry is ONE slot, not two");

            // a raw wcid fallback entry costs the same as an Oak
            AssertReserved(1, null, new uint[] { 21082, 61, 61, 61, 61, 61, 61, 61, 61, 61 },
                "a raw wcid log entry is a slot this system cannot reverse");

            // the plain cases must not regress
            AssertReserved(0, null, new uint[] { 61, 61, 61, 61, 61, 61, 61, 61, 61, 61 }, "an all-known log reserves nothing");
            AssertReserved(2, (int)(ImbuedEffectType.CriticalStrike | ImbuedEffectType.ArmorRending), new uint[] { 61, 61, 61, 61, 61, 61, 61, 61 },
                "two imbues and an eight-entry known log reserve exactly two");
        }

        private static void AssertReserved(int expected, int? imbuedEffect, uint[] log, string why)
        {
            var ints = new Dictionary<PropertyInt, int> { { PropertyInt.NumTimesTinkered, log.Length } };

            if (imbuedEffect != null)
                ints[PropertyInt.ImbuedEffect] = imbuedEffect.Value;

            var weapon = WeaponModTestKit.MakeWeapon(ItemType.MeleeWeapon, CombatUse.Melee, ints);

            weapon.SetProperty(PropertyString.TinkerLog, string.Join(",", log));

            // TEST DEFECT, fixed 2026-07-30: this helper asserted against ReadReservedImbueSlots, which is the
            // imbue popcount ALONE and by construction knows nothing about the log - so it could never have
            // observed the behaviour this test exists to pin, and the "ten Oak reserves ten" case read 0.
            // ReadReservedSlots is the accessor that carries the rule (imbues plus the unreversible log entries)
            // and the one production budgets and refuses against (WeaponModManager.cs, ResolveRefusal/ApplyReroll).
            Assert.AreEqual(expected, WeaponModTinkerSet.ReadReservedSlots(weapon),
                $"reserved slots for log [{string.Join(",", log)}] with imbues 0x{(imbuedEffect ?? 0):X}: {why}");

            // ... and the imbue-only accessor stays imbue-only: it must read the ImbuedEffect popcount and
            // nothing else, whatever the log holds. That separation is what lets ComputeReservedSlots subtract
            // the popcount from the unknown count without double-charging an imbue's own salvage entry.
            Assert.AreEqual(WeaponModTinkerSet.ReservedImbueSlots(imbuedEffect ?? 0), WeaponModTinkerSet.ReadReservedImbueSlots(weapon),
                $"the imbue-only accessor must ignore the log entirely, log [{string.Join(",", log)}]");
        }

        /// <summary>
        /// CONFIRMED DEFECT (asserts the CORRECT behaviour and therefore fails). The end-to-end consequence of
        /// the reserved-slot arithmetic above: a ten-Oak weapon must be refused outright rather than being handed
        /// a second full budget.
        /// </summary>
        [TestMethod]
        public void Refusal_ATenOakWeaponIsRefusedRatherThanBeingHandedTwentySlots()
        {
            var weapon = WeaponModTestKit.MakeWeapon(ItemType.MeleeWeapon, CombatUse.Melee,
                new Dictionary<PropertyInt, int> { { PropertyInt.NumTimesTinkered, 10 }, { PropertyInt.WeaponTime, 0 }, { PropertyInt.Damage, 18 } });

            weapon.SetProperty(PropertyString.TinkerLog, "75,75,75,75,75,75,75,75,75,75");

            Assert.AreEqual(WeaponModManager.WeaponModRefusal.NoAvailableSlots,
                WeaponModTestKit.Refusal(WeaponModTestKit.MakeBag(MaterialType.Tourmaline), weapon),
                "a weapon whose ten tinkers are all unreversible has no slots to offer, so a reroll would be a second free budget on top of the first");

            Assert.AreEqual(WeaponModManager.WeaponModRefusal.NoAvailableSlots,
                WeaponModTestKit.Refusal(WeaponModTestKit.MakeBag(MaterialType.Amethyst), weapon));
        }

        /// <summary>
        /// CONFIRMED DEFECT (asserts the CORRECT behaviour and therefore fails). A partly unknown log must keep
        /// its unaccounted entries: they cost slots, they stay in the retail log, and the refill only covers what
        /// this system actually reversed. Dropping them from the log is what would hand the slots back on the
        /// NEXT reroll even after the reserved arithmetic is fixed.
        /// </summary>
        [TestMethod]
        public void Reroll_UnknownLogEntriesKeepTheirSlotsAndStayInTheRetailLog()
        {
            var weapon = WeaponModTestKit.MakeWeapon(ItemType.MeleeWeapon, CombatUse.Melee,
                new Dictionary<PropertyInt, int> { { PropertyInt.NumTimesTinkered, 10 }, { PropertyInt.WeaponTime, 0 }, { PropertyInt.Damage, 18 } });

            weapon.SetProperty(PropertyString.TinkerLog, "75,75,75,75,75,61,61,61,61,61");

            for (var pass = 1; pass <= 3; pass++)
            {
                Assert.IsNotNull(WeaponModManager.ApplyReroll(weapon, WeaponClass.Melee, 10.0), $"pass {pass}");

                var specials = WeaponModTinkerSet.SpecialCount(weapon);
                var tinkers = weapon.GetProperty(PropertyInt.WeaponModTinkerCount) ?? -1;

                Assert.AreEqual(5, specials + tinkers,
                    $"pass {pass}: five Oak entries hold five slots, so only five may be refilled - {specials} specials plus {tinkers} tinkers is a budget of {specials + tinkers + 5}");

                Assert.IsTrue(WeaponModTinkerSet.TryParseLog(weapon.GetProperty(PropertyString.TinkerLog), out var retail), $"pass {pass}: TinkerLog does not parse");

                Assert.AreEqual(5, retail.Count(m => m == MaterialType.Oak),
                    $"pass {pass}: the five Oak entries were dropped from TinkerLog. They are the only record that those slots are spent, so dropping them hands them back on the next reroll.");

                // TEST DEFECT, fixed 2026-07-30: this used to require TinkerLog to hold exactly ten entries. A
                // rolled SPECIAL occupies a slot but is not a tinker and has no MaterialType, so by design it is
                // not a log entry at all (WeaponModTinkerSet.WriteComposition writes carried entries + tinkers,
                // and the specials live in their own PropertyFloat records). With one special the log is
                // legitimately 5 + 4 = 9, so the old assert failed only on the passes where a special happened to
                // roll - a flaky test pinning an invariant the system never had. The three relations below are
                // what actually hold, and together they are stricter: they tie the log length to the carried
                // entries and the tinker counter, and the whole budget to the carried entries rather than to a
                // hardcoded 5.
                var unaccounted = WeaponModTinkerSet.ReadUnaccountedEntries(weapon);

                CollectionAssert.AreEqual(Enumerable.Repeat(MaterialType.Oak, 5).ToList(), unaccounted,
                    $"pass {pass}: the carried-forward entries must be exactly the five Oak, in log order");

                Assert.AreEqual(unaccounted.Count + tinkers, retail.Count,
                    $"pass {pass}: TinkerLog holds {retail.Count} entries against {unaccounted.Count} carried Oak plus {tinkers} fresh tinkers - the log describes the carried entries and the tinkers, and nothing else");

                Assert.AreEqual(WeaponModRegistry.TotalSlots, unaccounted.Count + specials + tinkers,
                    $"pass {pass}: {unaccounted.Count} unreversible entries + {specials} specials + {tinkers} tinkers must be exactly the ten-slot budget");

                Assert.IsTrue(WeaponModTinkerSet.TryParseLog(weapon.GetProperty(PropertyString.WeaponModTinkerLog), out var own), $"pass {pass}: WeaponModTinkerLog does not parse");
                CollectionAssert.AreEqual(retail, own, $"pass {pass}: both logs must describe the same composition");
                Assert.AreEqual(WeaponModRegistry.TotalSlots, weapon.GetProperty(PropertyInt.NumTimesTinkered), $"pass {pass}: NumTimesTinkered");
                Assert.AreEqual(0, weapon.GetProperty(PropertyInt.WeaponTime), $"pass {pass}: WeaponTime moved");
            }
        }

        [TestMethod]
        public void Retail_AManagedWeaponSuppressesTheIntegrityGateEvenWithAStaleOrAbsentLog()
        {
            // stale retail log, inconsistent with the counter, but the weapon is ours
            var stale = WeaponModTestKit.MakeWeapon(ItemType.MeleeWeapon, CombatUse.Melee,
                new Dictionary<PropertyInt, int> { { PropertyInt.NumTimesTinkered, 10 }, { PropertyInt.WeaponModTinkerCount, 8 } });

            stale.SetProperty(PropertyString.TinkerLog, "61,61");
            stale.SetProperty(PropertyString.WeaponModTinkerLog, "61,61,61,61,61,61,61,61");

            Assert.IsTrue(WeaponModTinkerSet.IsManaged(stale));
            Assert.IsTrue(WeaponModTinkerSet.PassesIntegrityGate(stale), "a managed weapon reads its own log, so the retail gate is suppressed");
            Assert.AreEqual(PropertyString.WeaponModTinkerLog, WeaponModTinkerSet.ReversalLog(stale));
            Assert.AreEqual(8, WeaponModTinkerSet.ReadComposition(stale).Count, "reversal must read OUR log, not the stale retail one");

            // absent retail log entirely
            var absent = WeaponModTestKit.MakeWeapon(ItemType.MeleeWeapon, CombatUse.Melee,
                new Dictionary<PropertyInt, int> { { PropertyInt.NumTimesTinkered, 10 }, { PropertyInt.WeaponModTinkerCount, 0 } });

            Assert.IsTrue(WeaponModTinkerSet.IsManaged(absent), "WeaponModTinkerCount 0 is PRESENT, so the weapon is managed");
            Assert.IsTrue(WeaponModTinkerSet.PassesIntegrityGate(absent));

            // ... while the same weapon WITHOUT the marker is refused
            absent.RemoveProperty(PropertyInt.WeaponModTinkerCount);
            Assert.IsFalse(WeaponModTinkerSet.IsManaged(absent));
            Assert.IsFalse(WeaponModTinkerSet.PassesIntegrityGate(absent), "an unmanaged weapon with a tinker count and no log must be refused");
        }

        /// <summary>
        /// After ANY operation the counter reads exactly ten, which is what leaves the retail
        /// recipe_requirements_int gate blocking further ordinary tinkering. Eleven would be worse than a refusal:
        /// TinkeringDifficulty is an unguarded ten-element list indexed by this counter.
        /// </summary>
        [TestMethod]
        public void Retail_EveryOperationLeavesNumTimesTinkeredAtExactlyTen()
        {
            foreach (var weaponClass in AllClasses)
            {
                foreach (var startCount in new[] { 0, 1, 3, 9, 10 })
                {
                    var weapon = WeaponModTestKit.MakeUntinkered(weaponClass);

                    if (startCount > 0)
                    {
                        var composition = WeaponModTestKit.HandTinkeredComposition(weaponClass).Take(startCount).ToList();

                        WeaponModTinkerSet.ApplyTinkers(weapon, composition);
                        weapon.SetProperty(PropertyString.TinkerLog, WeaponModTinkerSet.SerializeLog(composition));
                        weapon.SetProperty(PropertyInt.NumTimesTinkered, startCount);
                    }

                    Assert.IsNotNull(WeaponModManager.ApplyReroll(weapon, weaponClass, 10.0));
                    Assert.AreEqual(WeaponModRegistry.TotalSlots, weapon.GetProperty(PropertyInt.NumTimesTinkered),
                        $"{weaponClass} from {startCount} tinkers: a reroll must leave the counter on exactly ten");

                    // refuses only when the reroll happened to land a full trio, which leaves the counter alone
                    WeaponModTestKit.Swap(weapon, weaponClass, 10.0, $"{weaponClass} from {startCount} tinkers");

                    Assert.AreEqual(WeaponModRegistry.TotalSlots, weapon.GetProperty(PropertyInt.NumTimesTinkered),
                        $"{weaponClass} from {startCount} tinkers: a swap must leave the counter on exactly ten");
                }
            }
        }

        // ================= E. quantities =================

        /// <summary>
        /// Exact round trips through a real item for counts 0 to 10, every material, against both an absent and a
        /// present starting value. The present case must come back to the same number; the absent case must come
        /// back to no row at all.
        /// </summary>
        [TestMethod]
        public void Quantities_ApplyAndReverseRoundTripForEveryCountAndMaterial()
        {
            foreach (var material in WeaponTinkerTable.AllMaterials)
            {
                for (var count = 0; count <= WeaponModRegistry.TotalSlots; count++)
                {
                    var set = Enumerable.Repeat(material.Material, count).ToList();

                    // --- absent start ---
                    var bare = WeaponModTestKit.MakeWeapon();

                    WeaponModTinkerSet.ApplyTinkers(bare, set);
                    WeaponModTinkerSet.ReverseTinkers(bare, set);

                    Assert.IsNull(material.ReadValue(bare),
                        $"{material.Material} x{count}: a property that started ABSENT must come back absent, not left sitting at the engine default");

                    // --- present start ---
                    var start = material.IntProperty != null ? material.EngineDefault + 12.0 : material.EngineDefault + 0.5;

                    var seeded = WeaponModTestKit.MakeWeapon();

                    material.WriteValue(seeded, start);

                    WeaponModTinkerSet.ApplyTinkers(seeded, set);

                    var applied = material.ReadValue(seeded);

                    Assert.IsNotNull(applied, $"{material.Material} x{count}: the applied value is missing");
                    Assert.AreEqual(material.ApplyValue(start, count), applied.Value, 1e-9,
                        $"{material.Material} x{count}: apply from {start} landed on {applied.Value}");

                    WeaponModTinkerSet.ReverseTinkers(seeded, set);

                    var reversed = material.ReadValue(seeded);

                    Assert.IsNotNull(reversed, $"{material.Material} x{count}: a value of {start} was REMOVED rather than restored");
                    Assert.AreEqual(start, reversed.Value, 1e-9, $"{material.Material} x{count}: round trip from {start} landed on {reversed.Value}");
                }
            }
        }

        [TestMethod]
        public void Quantities_AllTenSlotsOfASingleMaterialForEveryMaterialInEveryClassPool()
        {
            foreach (var weaponClass in AllClasses)
            {
                foreach (var material in WeaponTinkerTable.Pool(weaponClass))
                {
                    var shape = WeaponModTestKit.Shape(weaponClass);
                    var weapon = WeaponModTestKit.MakeWeapon(shape.Type, shape.Use);

                    var start = material.IntProperty != null ? material.EngineDefault + 12.0 : material.EngineDefault + 0.5;

                    material.WriteValue(weapon, start);

                    var set = Enumerable.Repeat(material.Material, WeaponModRegistry.TotalSlots).ToList();

                    WeaponModTinkerSet.ApplyTinkers(weapon, set);

                    var expected = material.Op == WeaponTinkerOp.Add
                        ? start + 10 * material.Delta
                        : start * Math.Pow(material.Delta, 10);

                    Assert.AreEqual(expected, material.ReadValue(weapon).Value, 1e-9,
                        $"{weaponClass} ten {material.Material}: the tenth application is not the same size as the first");

                    WeaponModTinkerSet.ReverseTinkers(weapon, set);

                    Assert.AreEqual(start, material.ReadValue(weapon).Value, 1e-9, $"{weaponClass} ten {material.Material}: round trip");
                }
            }
        }

        [TestMethod]
        public void Quantities_MixedCompositionsSummingToTenRoundTripExactly()
        {
            foreach (var weaponClass in AllClasses)
            {
                var pool = WeaponTinkerTable.Pool(weaponClass).ToList();

                // every split of ten slots across the class pool that a reroll can actually produce, sampled
                // deterministically: shift the weight from one material to the next
                for (var pivot = 0; pivot < pool.Count; pivot++)
                {
                    for (var weight = 0; weight <= WeaponModRegistry.TotalSlots; weight++)
                    {
                        var composition = new List<MaterialType>();

                        for (var i = 0; i < weight; i++)
                            composition.Add(pool[pivot].Material);

                        for (var i = weight; i < WeaponModRegistry.TotalSlots; i++)
                            composition.Add(pool[(pivot + 1) % pool.Count].Material);

                        var shape = WeaponModTestKit.Shape(weaponClass);
                        var weapon = WeaponModTestKit.MakeWeapon(shape.Type, shape.Use);

                        var starts = new Dictionary<MaterialType, double>();

                        foreach (var material in pool)
                        {
                            var start = material.IntProperty != null ? material.EngineDefault + 12.0 : material.EngineDefault + 0.5;

                            starts[material.Material] = start;
                            material.WriteValue(weapon, start);
                        }

                        WeaponModTinkerSet.ApplyTinkers(weapon, composition);
                        WeaponModTinkerSet.ReverseTinkers(weapon, composition);

                        foreach (var material in pool)
                        {
                            var value = material.ReadValue(weapon);

                            Assert.IsNotNull(value, $"{weaponClass} pivot {pivot} weight {weight}: {material.Material}'s property was removed");
                            Assert.AreEqual(starts[material.Material], value.Value, 1e-9,
                                $"{weaponClass} pivot {pivot} weight {weight}: {material.Material} did not round trip");
                        }

                        Assert.AreEqual(WeaponModRegistry.TotalSlots, composition.Count);
                    }
                }
            }
        }

        [TestMethod]
        public void Quantities_ReversingAStrictSubsetLeavesTheRemainderApplied()
        {
            var weapon = WeaponModTestKit.MakeWeapon(ItemType.MeleeWeapon, CombatUse.Melee,
                new Dictionary<PropertyInt, int> { { PropertyInt.Damage, 20 } },
                new Dictionary<PropertyFloat, double> { { PropertyFloat.DamageVariance, 0.5 }, { PropertyFloat.WeaponDefense, 1.02 } });

            var applied = new List<MaterialType>
            {
                MaterialType.Iron, MaterialType.Iron, MaterialType.Iron, MaterialType.Iron, MaterialType.Iron, MaterialType.Iron,
                MaterialType.Granite, MaterialType.Granite,
                MaterialType.Brass, MaterialType.Brass,
            };

            WeaponModTinkerSet.ApplyTinkers(weapon, applied);

            Assert.AreEqual(26, weapon.GetProperty(PropertyInt.Damage));
            Assert.AreEqual(0.5 * 0.64, weapon.GetProperty(PropertyFloat.DamageVariance).Value, 1e-12);
            Assert.AreEqual(1.04, weapon.GetProperty(PropertyFloat.WeaponDefense).Value, 1e-12);

            // take back a strict subset: two Iron and one Granite, leaving four Iron, one Granite, two Brass
            WeaponModTinkerSet.ReverseTinkers(weapon, new[] { MaterialType.Iron, MaterialType.Iron, MaterialType.Granite });

            Assert.AreEqual(24, weapon.GetProperty(PropertyInt.Damage), "reversing two of six Iron must leave four applied");
            Assert.AreEqual(0.5 * 0.8, weapon.GetProperty(PropertyFloat.DamageVariance).Value, 1e-12, "reversing one of two Granite must leave one applied");
            Assert.AreEqual(1.04, weapon.GetProperty(PropertyFloat.WeaponDefense).Value, 1e-12, "an untouched material must not move");

            // and reversing the remainder lands back on the start
            WeaponModTinkerSet.ReverseTinkers(weapon, new[]
            {
                MaterialType.Iron, MaterialType.Iron, MaterialType.Iron, MaterialType.Iron,
                MaterialType.Granite, MaterialType.Brass, MaterialType.Brass,
            });

            Assert.AreEqual(20, weapon.GetProperty(PropertyInt.Damage));
            Assert.AreEqual(0.5, weapon.GetProperty(PropertyFloat.DamageVariance).Value, 1e-9);
            Assert.AreEqual(1.02, weapon.GetProperty(PropertyFloat.WeaponDefense).Value, 1e-9);
        }

        // ================= reversal, attacked directly =================

        /// <summary>
        /// REGRESSION GUARD for a confirmed defect, fixed 2026-07-30.
        ///
        /// ReverseValue used to remove ANY result landing below EngineDefault. That rule was justified as recovery
        /// from the retail "set 0.01" branch, but only two of the seven materials have a "set" branch in their DAT
        /// script - Green Garnet (ElementalDamageMod) and Opal (ManaConversionMod). Mahogany, Brass and Velvet are
        /// plain "+=" scripts, so a below-default value on those is the weapon's own loot-generated number, and
        /// deleting it handed the player a free permanent buff (a DamageMod 0.5 bow reading 1.0 after two rerolls).
        ///
        /// The material list here is deliberately hardcoded rather than read off the production flag, so that
        /// flipping the flag on a plain "+=" material fails this test instead of silently agreeing with it.
        /// </summary>
        [TestMethod]
        public void Reversal_BelowDefaultRemovalAppliesOnlyToTheSetBranchMaterials()
        {
            // the only two materials whose DAT script can write a value below the engine default
            var setBranch = new HashSet<MaterialType> { MaterialType.GreenGarnet, MaterialType.Opal };

            foreach (var material in WeaponTinkerTable.AllMaterials)
            {
                // Iron, Granite and Opal all default to 0, so "below the engine default" is not a reachable
                // state for them - the universal floor at 0 catches it, and a landing ON 0 is removed by the
                // epsilon rule instead. That is why Opal's set-branch flag is a no-op.
                if (material.EngineDefault <= 0.0)
                    continue;

                // a value that reverses to just under the engine default
                var landing = material.EngineDefault - 0.02;
                var current = material.Op == WeaponTinkerOp.Add
                    ? landing + material.Delta
                    : landing * material.Delta;

                var reversed = material.ReverseValue(current, 1);

                if (setBranch.Contains(material.Material))
                {
                    Assert.IsNull(reversed,
                        $"{material.Material}: its DAT script really does have a 'set' branch, so a below-default value is wreckage from it and absent is the correct restore");
                }
                else
                {
                    Assert.IsNotNull(reversed,
                        $"{material.Material}: a below-default value on a plain '+=' material is the weapon's OWN loot-generated number, and removing it lets the engine's '?? {material.EngineDefault}' hand the player a free permanent buff. ace_world carries 11 weapons with DamageMod below 1.0 and 26 with WeaponDefense below 1.0.");

                    Assert.AreEqual(landing, reversed.Value, 1e-9,
                        $"{material.Material}: a below-default reversal must restore the exact value, not {reversed.Value}");
                }
            }
        }

        /// <summary>
        /// The reversal math attacked over a randomized domain rather than by example: thousands of
        /// (start, count) pairs per material, above the engine default where the round trip must be an identity
        /// unconditionally, plus both sides of the epsilon threshold that decides removal.
        /// </summary>
        [TestMethod]
        public void Reversal_IsAnExactIdentityOverARandomizedDomainAboveTheEngineDefault()
        {
            var random = new Random(730202607);

            foreach (var material in WeaponTinkerTable.AllMaterials)
            {
                for (var i = 0; i < 3000; i++)
                {
                    var count = 1 + random.Next(WeaponModRegistry.TotalSlots);

                    // deliberately awkward numbers: nothing that divides evenly into the deltas
                    var start = material.EngineDefault + 0.001 + random.NextDouble() * 37.0;

                    var applied = material.ApplyValue(start, count);
                    var reversed = material.ReverseValue(applied, count);

                    Assert.IsNotNull(reversed, $"{material.Material} x{count} from {start}: a value above the engine default must never be removed");
                    Assert.AreEqual(start, reversed.Value, 1e-9, $"{material.Material} x{count}: round trip from {start} landed on {reversed.Value}");

                    // applying in two stages must match applying in one - otherwise a swap and a reroll disagree
                    var split = random.Next(count + 1);
                    var staged = material.ApplyValue(material.ApplyValue(start, split), count - split);

                    Assert.AreEqual(applied, staged, 1e-9,
                        $"{material.Material}: {split} then {count - split} applications must equal {count} in one go, or the swap and the reroll drift apart");
                }

                // the epsilon threshold, from both sides
                var justInside = material.EngineDefault + WeaponModRegistry.Epsilon / 2.0;
                var justOutside = material.EngineDefault + WeaponModRegistry.Epsilon * 100.0;

                Assert.IsNull(material.ReverseValue(material.ApplyValue(justInside, 1), 1),
                    $"{material.Material}: a value within {WeaponModRegistry.Epsilon} of the engine default is absorbed into 'absent' by design");

                var outside = material.ReverseValue(material.ApplyValue(justOutside, 1), 1);

                Assert.IsNotNull(outside, $"{material.Material}: a value clear of the epsilon band must survive the round trip");
                Assert.AreEqual(justOutside, outside.Value, WeaponModRegistry.Epsilon,
                    $"{material.Material}: a value just outside the epsilon band landed on {outside.Value}");
            }
        }

        /// <summary>
        /// The same attack on the special reversal: a random loot-generated native, a random applied magnitude,
        /// and the requirement that the native comes back exactly. The magnitude is drawn across the whole legal
        /// band including both endpoints of the potency range.
        ///
        /// TIER A ONLY, and not as a narrowing. A Tier B row writes no native property at all, so there is no
        /// native to seed, subtract or restore - its whole reversal is the RemoveProperty this test's subject
        /// wraps. That case is covered directly in WeaponModTierBTests.
        /// </summary>
        [TestMethod]
        public void Reversal_SpecialsAreAnExactIdentityOverARandomizedDomain()
        {
            var random = new Random(26073020);

            foreach (var definition in WeaponModRegistry.TierAMods)
            {
                for (var i = 0; i < 500; i++)
                {
                    // a plausible loot value strictly above the engine default, awkwardly valued
                    var start = definition.NativeDefault + (definition.IsInteger ? 1 + random.Next(40) : 0.017 + random.NextDouble() * 3.0);

                    var potency = random.Next(3) == 0
                        ? (random.Next(2) == 0 ? WeaponModDefinition.DefaultMinPotency : 1.0)   // both endpoints, exactly
                        : WeaponModDefinition.DefaultMinPotency + random.NextDouble() * (1.0 - WeaponModDefinition.DefaultMinPotency);

                    var workmanship = 1 + random.Next(10);
                    var magnitude = WeaponModValue.Resolve(definition, potency, workmanship, 1.0);

                    Assert.IsTrue(magnitude > 0.0,
                        $"{definition.Id}: potency {potency} at workmanship {workmanship} resolved to {magnitude}, so the slot buys nothing");

                    var weapon = WeaponModTestKit.MakeWeapon();

                    definition.WriteNative(weapon, start);

                    var seeded = definition.ReadNative(weapon).Value;

                    WeaponModTinkerSet.ApplySpecial(weapon, definition, magnitude);

                    Assert.AreEqual(magnitude, weapon.GetProperty(definition.Record).Value, 1e-12,
                        $"{definition.Id}: the record must hold the applied magnitude exactly");

                    WeaponModTinkerSet.ReverseSpecial(weapon, definition);

                    var after = definition.ReadNative(weapon);

                    Assert.IsNotNull(after, $"{definition.Id}: a loot value of {seeded} was REMOVED by the reversal");
                    Assert.AreEqual(seeded, after.Value, 1e-9, $"{definition.Id}: magnitude {magnitude} on a base of {seeded} came back as {after.Value}");
                    Assert.IsNull(weapon.GetProperty(definition.Record), $"{definition.Id}: the record must be REMOVED, never zeroed");
                }
            }
        }

        /// <summary>
        /// CONFIRMED DEFECT (asserts the CORRECT behaviour and therefore fails).
        ///
        /// WeaponModDefinition.ReverseValue:160-162 removes the native row whenever the restored value equals the
        /// engine default. For Swift Flight that default is 20.0 (Creature_Missile.cs:517), and six ace_world
        /// launchers carry MaximumVelocity exactly 20.0 - so reversing Swift Flight deletes their velocity row,
        /// and WeaponProfile.cs:57 then reads "?? 1.0f" and shows the player a velocity of 1.0.
        ///
        /// MaximumVelocity has no single agreed engine default, so the row must never be removed.
        /// </summary>
        [TestMethod]
        public void Specials_SwiftFlightNeverRemovesMaximumVelocity()
        {
            var definition = WeaponModRegistry.Get(WeaponModId.SwiftFlight);

            Assert.AreEqual(PropertyFloat.MaximumVelocity, definition.NativeFloat);

            foreach (var start in new[] { 20.0, 18.0, 24.0 })
            {
                var weapon = WeaponModTestKit.MakeWeapon(ItemType.MissileWeapon, CombatUse.Missile, null,
                    new Dictionary<PropertyFloat, double> { { PropertyFloat.MaximumVelocity, start } });

                WeaponModTinkerSet.ApplySpecial(weapon, definition, 6.0);

                Assert.AreEqual(start + 6.0, weapon.GetProperty(PropertyFloat.MaximumVelocity).Value, 1e-12, $"apply from {start}");

                WeaponModTinkerSet.ReverseSpecial(weapon, definition);

                var after = weapon.GetProperty(PropertyFloat.MaximumVelocity);

                Assert.IsNotNull(after,
                    $"a launcher whose MaximumVelocity was {start} lost the row entirely. The read sites disagree on the fallback - WeaponProfile.cs:57 uses 1.0 - so an absent row shows the player a velocity of 1.0 on the appraisal panel.");

                Assert.AreEqual(start, after.Value, 1e-12, $"MaximumVelocity round trip from {start}");
            }
        }

        /// <summary>
        /// CONFIRMED DEFECT (asserts the CORRECT behaviour and therefore fails).
        ///
        /// WeaponTinkerMaterial.Apply on a multiply material whose property is ABSENT computes
        /// "EngineDefault x Delta^n", which for Granite is 0.0, and WriteValue:165 then writes that 0.0 as a real
        /// row. It reads identically to absent but marks a clean weapon as tinkered, and it is exactly the dead
        /// row the design forbids elsewhere.
        /// </summary>
        [TestMethod]
        public void Tinkers_GraniteOnAnAbsentDamageVarianceWritesNoJunkRow()
        {
            for (var count = 1; count <= WeaponModRegistry.TotalSlots; count++)
            {
                var weapon = WeaponModTestKit.MakeWeapon();

                Assert.IsNull(weapon.GetProperty(PropertyFloat.DamageVariance), "precondition: the weapon has no DamageVariance");

                WeaponModTinkerSet.ApplyTinkers(weapon, Enumerable.Repeat(MaterialType.Granite, count));

                Assert.IsNull(weapon.GetProperty(PropertyFloat.DamageVariance),
                    $"{count}x Granite on an absent DamageVariance wrote a row of {weapon.GetProperty(PropertyFloat.DamageVariance)}. Multiplying the engine default of 0 leaves the default, and a row holding the default is the dead row the design forbids.");
            }
        }

        /// <summary>
        /// A native property that is PRESENT but zero is a different starting state from one that is absent, and
        /// the two must be indistinguishable after a full apply/reverse cycle. Cleave is excluded on purpose: its
        /// native is a TOTAL target count whose floor is 1, so 0 is not a value the property can legally hold.
        /// Tier B is excluded because it has no native property to start present or absent.
        /// </summary>
        [TestMethod]
        public void Specials_APresentButZeroNativeIsEffectivelyUnchangedByApplyThenReverse()
        {
            foreach (var definition in WeaponModRegistry.TierAMods.Where(d => d.NativeFloor == 0.0))
            {
                for (var workmanship = 1; workmanship <= 10; workmanship++)
                {
                    var present = WeaponModTestKit.MakeWeapon();
                    var absent = WeaponModTestKit.MakeWeapon();

                    definition.WriteNative(present, 0.0);

                    Assert.IsNotNull(definition.ReadNative(present), $"{definition.Id}: precondition - the row is present and zero");
                    Assert.IsNull(definition.ReadNative(absent), $"{definition.Id}: precondition - the row is absent");

                    var magnitude = WeaponModValue.Resolve(definition, 1.0, workmanship, 1.0);

                    WeaponModTinkerSet.ApplySpecial(present, definition, magnitude);
                    WeaponModTinkerSet.ApplySpecial(absent, definition, magnitude);

                    WeaponModTinkerSet.ReverseSpecial(present, definition);
                    WeaponModTinkerSet.ReverseSpecial(absent, definition);

                    var fromPresent = definition.ReadNative(present) ?? definition.NativeDefault;
                    var fromAbsent = definition.ReadNative(absent) ?? definition.NativeDefault;

                    Assert.AreEqual(0.0, fromPresent, 1e-9,
                        $"{definition.Id} at workmanship {workmanship}: a native that started PRESENT at zero reads {fromPresent} after a full cycle");

                    // the two starting states are only equivalent when the engine reads an absent row as 0.
                    // Swift Flight's default is 20 (Creature_Missile.cs:517), so a present zero and an absent
                    // row are genuinely different weapons there and must NOT converge.
                    if (definition.NativeDefault == 0.0)
                    {
                        Assert.AreEqual(fromPresent, fromAbsent, 1e-9,
                            $"{definition.Id} at workmanship {workmanship}: a present-but-zero native and an absent one diverged ({fromPresent} against {fromAbsent}) even though the engine reads both as 0");
                    }
                    else
                    {
                        Assert.AreEqual(definition.NativeDefault, fromAbsent, 1e-9,
                            $"{definition.Id} at workmanship {workmanship}: a native that started ABSENT reads {fromAbsent} after a full cycle rather than its engine default of {definition.NativeDefault}");
                    }

                    Assert.IsNull(present.GetProperty(definition.Record), $"{definition.Id}: the record must be removed either way");
                    Assert.IsNull(absent.GetProperty(definition.Record), $"{definition.Id}: the record must be removed either way");
                }
            }
        }

        /// <summary>
        /// A structural invariant nothing else pins: no Tier A special may write to a property a layer 1 material
        /// also owns. ApplyReroll reverses the whole layer 1 composition and then clears the specials, so a
        /// shared property would be reversed twice from a value only one of them put there - and the drift would
        /// be invisible until a player noticed their weapon had quietly changed.
        /// </summary>
        [TestMethod]
        public void Registry_NoSpecialSharesANativePropertyWithALayerOneMaterial()
        {
            var layerOneInts = new HashSet<PropertyInt>(WeaponTinkerTable.AllMaterials.Where(m => m.IntProperty != null).Select(m => m.IntProperty.Value));
            var layerOneFloats = new HashSet<PropertyFloat>(WeaponTinkerTable.AllMaterials.Where(m => m.FloatProperty != null).Select(m => m.FloatProperty.Value));

            Assert.IsTrue(layerOneInts.Count + layerOneFloats.Count == WeaponTinkerTable.AllMaterials.Count,
                "two layer 1 materials share a property, so their reversals would interleave");

            foreach (var definition in WeaponModRegistry.TierAMods)
            {
                if (definition.NativeInt != null)
                {
                    Assert.IsFalse(layerOneInts.Contains(definition.NativeInt.Value),
                        $"{definition.Id} writes {definition.NativeInt.Value}, which a layer 1 material also owns - the reroll would reverse it twice");
                }

                if (definition.NativeFloat != null)
                {
                    Assert.IsFalse(layerOneFloats.Contains(definition.NativeFloat.Value),
                        $"{definition.Id} writes {definition.NativeFloat.Value}, which a layer 1 material also owns - the reroll would reverse it twice");
                }
            }

            // and no two specials share a native either, or their magnitudes would add into one number that
            // neither reversal could unpick
            var natives = WeaponModRegistry.TierAMods
                .Select(m => m.NativeInt != null ? $"int:{(int)m.NativeInt.Value}" : $"float:{(int)m.NativeFloat.Value}")
                .ToList();

            Assert.AreEqual(natives.Count, natives.Distinct().Count(), "two Tier A specials share a native property");

            // Tier B cannot collide with anything here by construction, and that is asserted rather than
            // assumed: a Tier B row that grew a native property would need this whole test applied to it.
            foreach (var definition in WeaponModRegistry.TierBMods)
            {
                Assert.IsFalse(definition.WritesNative,
                    $"{definition.Id} is Tier B but writes a native property, so it can now collide with a layer 1 material or another special - widen this test rather than deleting the assertion");
            }
        }

        /// <summary>
        /// A record for a special that is NOT in the target's class pool - the shape an item reaches if it was
        /// ever misclassified, or if a class pool is narrowed in a later tuning pass. The reroll must still clear
        /// it and restore its native, because ClearSpecials walks the whole registry rather than the class pool.
        /// A stranded record would occupy a slot forever and refuse every future swap.
        /// </summary>
        [TestMethod]
        public void Specials_ARerollClearsARecordThatIsOutsideTheTargetsClassPool()
        {
            var cleave = WeaponModRegistry.Get(WeaponModId.Cleave);
            var swiftFlight = WeaponModRegistry.Get(WeaponModId.SwiftFlight);

            Assert.IsFalse(cleave.AppliesTo(WeaponClass.Caster), "precondition: Cleave is not a caster modifier");
            Assert.IsFalse(swiftFlight.AppliesTo(WeaponClass.Caster), "precondition: Swift Flight is not a caster modifier");

            var weapon = WeaponModTestKit.MakeUntinkered(WeaponClass.Caster);
            var probe = new WeaponModProbe(weapon);

            WeaponModTinkerSet.ApplySpecial(weapon, cleave, 1.0);
            WeaponModTinkerSet.ApplySpecial(weapon, swiftFlight, 6.0);

            Assert.AreEqual(2, WeaponModTinkerSet.SpecialCount(weapon), "precondition: both stranded records are present");

            Assert.IsNotNull(WeaponModManager.ApplyReroll(weapon, WeaponClass.Caster, 10.0));

            Assert.IsNull(weapon.GetProperty(cleave.Record), "the reroll must clear a record from outside the class pool, not skip it");
            Assert.IsNull(weapon.GetProperty(swiftFlight.Record), "the reroll must clear a record from outside the class pool, not skip it");
            Assert.IsNull(weapon.GetProperty(PropertyInt.Cleaving), "Cleaving must go back to absent when the stranded Cleave record is cleared");

            probe.AssertInvariants(weapon, WeaponClass.Caster, "after clearing stranded out-of-class records");
        }

        // ================= F. weapon classes =================

        [TestMethod]
        public void Classes_TinkerPoolsExcludeEveryMaterialThatIsDeadOrWrongForTheClass()
        {
            var forbidden = new Dictionary<WeaponClass, MaterialType[]>
            {
                // WeaponOffense is dead on ranged weapons, and Iron's Damage is a melee-only axis
                { WeaponClass.Missile, new[] { MaterialType.Velvet, MaterialType.Iron } },
                { WeaponClass.Caster, new[] { MaterialType.Iron, MaterialType.Mahogany, MaterialType.Granite } },
                { WeaponClass.Melee, new[] { MaterialType.Mahogany, MaterialType.GreenGarnet, MaterialType.Opal } },
            };

            foreach (var kvp in forbidden)
            {
                foreach (var material in kvp.Value)
                {
                    Assert.IsFalse(WeaponTinkerTable.Pool(kvp.Key).Any(m => m.Material == material),
                        $"{material} must not be in the {kvp.Key} tinker pool");
                }

                // and the draw actually respects it, over enough samples to cover the whole pool many times
                var seen = new HashSet<MaterialType>();

                for (var i = 0; i < 1000; i++)
                {
                    foreach (var material in WeaponModRoller.RollTinkers(kvp.Key, WeaponModRegistry.TotalSlots))
                    {
                        Assert.IsFalse(kvp.Value.Contains(material), $"the {kvp.Key} draw produced {material}");
                        seen.Add(material);
                    }
                }

                Assert.AreEqual(WeaponTinkerTable.Pool(kvp.Key).Count, seen.Count, $"the {kvp.Key} draw did not cover its whole pool");
            }
        }

        [TestMethod]
        public void Classes_SpecialPoolsRespectTheClassRestrictions()
        {
            Assert.IsFalse(WeaponModRegistry.Pool(WeaponClass.Missile).Any(m => m.Id == WeaponModId.Cleave), "Cleave is melee only");
            Assert.IsFalse(WeaponModRegistry.Pool(WeaponClass.Caster).Any(m => m.Id == WeaponModId.Cleave), "Cleave is melee only");
            Assert.IsFalse(WeaponModRegistry.Pool(WeaponClass.Caster).Any(m => m.Id == WeaponModId.ShieldBypass), "Shield Bypass is not a caster modifier");
            Assert.IsFalse(WeaponModRegistry.Pool(WeaponClass.Caster).Any(m => m.Id == WeaponModId.SwiftFlight), "Swift Flight is not a caster modifier");
            Assert.IsFalse(WeaponModRegistry.Pool(WeaponClass.Melee).Any(m => m.Id == WeaponModId.SwiftFlight), "Swift Flight is missile only");
            Assert.IsTrue(WeaponModRegistry.Pool(WeaponClass.Missile).Any(m => m.Id == WeaponModId.SwiftFlight), "Swift Flight must be available to missile weapons");

            // and again through the real roll, so a pool that is right but a draw that is wrong still fails
            foreach (var weaponClass in AllClasses)
            {
                var seen = new HashSet<WeaponModId>();

                for (var i = 0; i < 1500; i++)
                {
                    foreach (var definition in WeaponModRoller.RollDistinctSpecials(weaponClass, WeaponModRegistry.MaxSpecials))
                    {
                        Assert.IsTrue(definition.AppliesTo(weaponClass), $"the {weaponClass} draw produced {definition.Id}");
                        seen.Add(definition.Id);
                    }
                }

                Assert.AreEqual(WeaponModRegistry.Pool(weaponClass).Count, seen.Count, $"the {weaponClass} special draw did not cover its whole pool");
            }
        }

        /// <summary>Every material and special a REAL reroll writes onto a weapon is legal for that weapon's class.</summary>
        [TestMethod]
        public void Classes_ARealRerollNeverWritesAnOutOfClassMaterialOrSpecial()
        {
            foreach (var weaponClass in AllClasses)
            {
                var weapon = WeaponModTestKit.MakeUntinkered(weaponClass);
                var probe = new WeaponModProbe(weapon);

                for (var i = 1; i <= 300; i++)
                {
                    Assert.IsNotNull(WeaponModManager.ApplyReroll(weapon, weaponClass, 10.0), $"{weaponClass} reroll {i}");

                    probe.AssertInvariants(weapon, weaponClass, $"{weaponClass} class-purity reroll {i}");

                    Assert.IsTrue(WeaponModTinkerSet.TryParseLog(weapon.GetProperty(PropertyString.WeaponModTinkerLog), out var entries));

                    foreach (var material in entries)
                    {
                        Assert.IsTrue(WeaponTinkerTable.TryGet(material, out var definition), $"{weaponClass} reroll {i}: {material} is not a known material");
                        Assert.IsTrue(definition.AppliesTo(weaponClass), $"{weaponClass} reroll {i}: {material} is not a {weaponClass} material");
                    }
                }
            }
        }

        // ================= I. the refusal matrix, over real items =================

        [TestMethod]
        public void Refusal_MatrixOverRealItems()
        {
            var tourmaline = WeaponModTestKit.MakeBag(MaterialType.Tourmaline);
            var amethyst = WeaponModTestKit.MakeBag(MaterialType.Amethyst);

            // --- the target is not a weapon ---
            Assert.AreEqual(WeaponModManager.WeaponModRefusal.NotAWeapon,
                WeaponModTestKit.Refusal(tourmaline, WeaponModTestKit.MakeWeapon(ItemType.MeleeWeapon, CombatUse.Shield)), "shield");

            Assert.AreEqual(WeaponModManager.WeaponModRefusal.NotAWeapon,
                WeaponModTestKit.Refusal(tourmaline, WeaponModTestKit.MakeWeapon(ItemType.MissileWeapon, CombatUse.Ammo)), "ammunition");

            Assert.AreEqual(WeaponModManager.WeaponModRefusal.NotAWeapon,
                WeaponModTestKit.Refusal(tourmaline, WeaponModTestKit.MakeWeapon(ItemType.Armor, CombatUse.None)), "armor");

            // --- workmanship ---
            Assert.AreEqual(WeaponModManager.WeaponModRefusal.NoWorkmanship,
                WeaponModTestKit.Refusal(tourmaline, WeaponModTestKit.MakeWeapon(ItemType.MeleeWeapon, CombatUse.Melee, null, null, null)),
                "a weapon with no ItemWorkmanship row would mint specials worth nothing");

            // ItemWorkmanship 0 cannot produce a workmanship of 0: the derived accessor's recovery branch clamps
            // it to 1, so the weapon is ACCEPTED at the minimum magnitude rather than refused
            var zeroWork = WeaponModTestKit.MakeWeapon(ItemType.MeleeWeapon, CombatUse.Melee, null, null, 0);

            Assert.IsNotNull(zeroWork.Workmanship, "ItemWorkmanship 0 is a present row, so Workmanship is not null");
            Assert.AreEqual(1.0f, zeroWork.Workmanship.Value, 1e-6f, "ItemWorkmanship 0 is recovered to a workmanship of 1 by WorldObject_Properties.cs:1560-1589");
            Assert.AreEqual(WeaponModManager.WeaponModRefusal.None, WeaponModTestKit.Refusal(tourmaline, zeroWork), "workmanship 0 is unreachable, so this is accepted at workmanship 1");

            // --- the source is not a designated salvage bag ---
            var gem = WeaponModTestKit.MakeBag(MaterialType.Tourmaline, 100, 100, ItemType.Gem);

            Assert.AreEqual(WeaponModManager.WeaponModRefusal.NotAModMaterial,
                WeaponModTestKit.Refusal(gem, WeaponModTestKit.MakeUntinkered(WeaponClass.Melee)),
                "a raw Tourmaline GEM carries the right MaterialType but is ItemType.Gem");

            Assert.AreEqual(WeaponModManager.WeaponModRefusal.NotAModMaterial,
                WeaponModTestKit.Refusal(WeaponModTestKit.MakeBag(MaterialType.Silver), WeaponModTestKit.MakeUntinkered(WeaponClass.Melee)),
                "a Silver salvage bag keeps its ordinary tinkering behaviour");

            // --- source equals target ---
            // the dedicated guard in VerifyUseRequirements calls player.SendTransientError and cannot run without
            // a Player, but the rule set refuses the pair anyway: a salvage bag is not a weapon
            Assert.AreEqual(WeaponModManager.WeaponModRefusal.NotAWeapon, WeaponModTestKit.Refusal(tourmaline, tourmaline),
                "a bag used on itself must never resolve to an action");

            // --- bag fullness, which VerifyUseRequirements checks directly ---
            Assert.IsTrue(WeaponModManager.IsFullBag(tourmaline));
            Assert.IsFalse(WeaponModManager.IsFullBag(WeaponModTestKit.MakeBag(MaterialType.Tourmaline, 99)), "a 99/100 bag is not a full unit of salvage");
            Assert.IsTrue(WeaponModManager.IsFullBag(WeaponModTestKit.MakeBag(MaterialType.Tourmaline, 100, null)),
                "the bag weenies carry no MaxStructure of their own, so a missing one must fall back to 100 rather than rejecting every bag");

            // --- swap-only rules ---
            var partTinkered = WeaponModTestKit.MakeWeapon(ItemType.MeleeWeapon, CombatUse.Melee,
                new Dictionary<PropertyInt, int> { { PropertyInt.NumTimesTinkered, 6 } });

            partTinkered.SetProperty(PropertyString.TinkerLog, "61,61,61,61,61,61");

            Assert.AreEqual(WeaponModManager.WeaponModRefusal.SwapNeedsFullBudget, WeaponModTestKit.Refusal(amethyst, partTinkered), "part-tinkered, swap");
            Assert.AreEqual(WeaponModManager.WeaponModRefusal.None, WeaponModTestKit.Refusal(tourmaline, partTinkered), "part-tinkered, reroll");

            var trio = WeaponModTestKit.MakeHandTinkered(WeaponClass.Melee);

            foreach (var id in new[] { WeaponModId.Devastation, WeaponModId.WeakPoint, WeaponModId.Bloodthirst })
                WeaponModTinkerSet.ApplySpecial(trio, WeaponModRegistry.Get(id), 5.0);

            Assert.AreEqual(WeaponModRegistry.MaxSpecials, WeaponModTinkerSet.SpecialCount(trio));
            Assert.AreEqual(WeaponModManager.WeaponModRefusal.SwapAtSpecialCap, WeaponModTestKit.Refusal(amethyst, trio), "three specials, swap");
            Assert.AreEqual(WeaponModManager.WeaponModRefusal.None, WeaponModTestKit.Refusal(tourmaline, trio), "three specials, reroll");

            // --- ten imbues ---
            var imbued = WeaponModTestKit.MakeUntinkered(WeaponClass.Melee, ImbueMask(10));

            Assert.AreEqual(WeaponModManager.WeaponModRefusal.NoAvailableSlots, WeaponModTestKit.Refusal(tourmaline, imbued), "ten imbues");
            Assert.AreEqual(WeaponModManager.WeaponModRefusal.NoAvailableSlots, WeaponModTestKit.Refusal(amethyst, imbued), "ten imbues, swap");
        }

        /// <summary>
        /// The three-special bound now has TWO enforcement points on the swap path, and this pins the second one.
        ///
        /// It previously had exactly one - ResolveRefusal returning SwapAtSpecialCap - and an earlier revision of
        /// this test asserted the resulting gap as a known fact: ApplySwap entered at a full trio would remove one
        /// of the three and roll a replacement, which is precisely the "reroll a single special" capability design
        /// section 5 says invalidates its +101.1% power assessment. Anything that reached ApplySwap without going
        /// through the refusal - a new caller, an admin command, a refactor - reopened it.
        ///
        /// ApplySwap now refuses on its own. This drives the weapon to a full trio through the real swap path,
        /// then calls ApplySwap DIRECTLY, with no refusal in front of it, and requires the trio to survive
        /// byte for byte.
        /// </summary>
        [TestMethod]
        public void Swap_ApplySwapRefusesAtThreeSpecialsWithoutTheRefusalInFrontOfIt()
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                // build the realistic managed state - seven tinkers and a full trio - by playing the swap
                var weapon = WeaponModTestKit.MakeHandTinkered(WeaponClass.Melee);

                var uses = 0;

                while (WeaponModTinkerSet.SpecialCount(weapon) < WeaponModRegistry.MaxSpecials)
                {
                    Assert.IsTrue(++uses < 500, "could not reach three specials");
                    Assert.IsNotNull(WeaponModTestKit.Swap(weapon, WeaponClass.Melee, 10.0, $"attempt {attempt} use {uses}"));
                }

                var before = WeaponModTinkerSet.ReadSpecials(weapon).Select(s => (s.Definition.Id, s.Magnitude)).OrderBy(s => s.Id).ToList();
                var beforeTinkers = weapon.GetProperty(PropertyString.WeaponModTinkerLog);

                Assert.IsNull(WeaponModManager.ApplySwap(weapon, WeaponClass.Melee, 10.0),
                    $"attempt {attempt}: ApplySwap ran on a weapon already holding three specials - the ONLY other guard is ResolveRefusal, and a caller that skips it can then reroll one special of a chosen trio");

                Assert.IsFalse(WeaponModManager.CanApply(weapon, WeaponClass.Melee, WeaponModManager.WeaponModAction.Swap),
                    $"attempt {attempt}: CanApply still says yes at three specials, so the bag would be consumed for a refusal");

                var after = WeaponModTinkerSet.ReadSpecials(weapon).Select(s => (s.Definition.Id, s.Magnitude)).OrderBy(s => s.Id).ToList();

                CollectionAssert.AreEqual(before, after, $"attempt {attempt}: the refused swap still altered the trio");

                Assert.AreEqual(beforeTinkers, weapon.GetProperty(PropertyString.WeaponModTinkerLog),
                    $"attempt {attempt}: the refused swap still rewrote the tinker log");
            }
        }

        // ================= magnitude =================

        [TestMethod]
        public void Magnitude_ScalesLinearlyWithWorkmanshipAndKeepsPotencyInBand()
        {
            // the float-valued natives carry no rounding, so linearity is exact there
            foreach (var definition in WeaponModRegistry.AllMods.Where(d => !d.IsInteger && !d.Binary))
            {
                var unit = WeaponModValue.Resolve(definition, 1.0, 1.0, 1.0);

                for (var workmanship = 1; workmanship <= 10; workmanship++)
                {
                    Assert.AreEqual(unit * workmanship, WeaponModValue.Resolve(definition, 1.0, workmanship, 1.0), 1e-12,
                        $"{definition.Id}: workmanship {workmanship} must be exactly {workmanship} times workmanship 1");
                }

                Assert.AreEqual(definition.MaxRoll, WeaponModValue.Resolve(definition, 1.0, 10.0, 1.0), 1e-12, $"{definition.Id}: a perfect roll on a workmanship 10 weapon is MaxRoll");
            }

            // The integer natives are their linear value, rounded, then FLOORED AT 1.
            //
            // TIGHTENED 2026-07-30, when Resolute's MaxRoll was retuned from 30 to 2. This used to assert
            // "within half a rounding step of the linear value", which silently assumed every MaxRoll was large
            // enough that the floor-at-1 rule in WeaponModValue.Resolve never bound. At MaxRoll 2 it binds hard:
            // workmanship 1 gives a linear 0.2 that rounds to 0 and is then floored to 1, which is 0.8 away from
            // linear and failed the old tolerance. The floor is deliberate ("a rolled special is never a no-op"),
            // so the assertion is now the EXACT composition of both rules rather than a tolerance around one of
            // them - strictly stronger than what it replaces, and it no longer encodes an assumption about how
            // big a MaxRoll has to be.
            foreach (var definition in WeaponModRegistry.AllMods.Where(d => d.IsInteger && !d.Binary))
            {
                for (var workmanship = 1; workmanship <= 10; workmanship++)
                {
                    var exact = definition.MaxRoll * workmanship / 10.0;
                    var value = WeaponModValue.Resolve(definition, 1.0, workmanship, 1.0);

                    var expected = Math.Max(1.0, Math.Round(exact, MidpointRounding.AwayFromZero));

                    Assert.AreEqual(expected, value, 1e-12,
                        $"{definition.Id}: workmanship {workmanship} resolved to {value}; the linear value is {exact}, which rounds and floors to {expected}");

                    Assert.IsTrue(value >= 1.0, $"{definition.Id}: workmanship {workmanship} resolved to {value}, so the slot buys nothing");
                }
            }

            // the rolled potency stays inside [MinPotency, 1] at both endpoints of every definition
            foreach (var definition in WeaponModRegistry.AllMods)
            {
                var floor = WeaponModRoller.MinPotency(definition);

                Assert.IsTrue(floor >= 0.0 && floor < 1.0, $"{definition.Id}: sanitized potency floor {floor} is outside [0, 1)");

                for (var i = 0; i < 3000; i++)
                {
                    var potency = WeaponModRoller.RollPotency(definition);

                    Assert.IsTrue(potency >= floor - 1e-12 && potency <= 1.0 + 1e-12, $"{definition.Id}: rolled potency {potency} outside [{floor}, 1]");
                }
            }

            Assert.AreEqual(0.25, WeaponModDefinition.DefaultMinPotency, 1e-12, "the design's potency floor is 0.25 for everything except the binary Cleave");
        }

        // ================= J. tunable extremes =================

        [TestMethod]
        public void Tunables_MagnitudeScaleExtremesHoldEveryInvariant()
        {
            var prior = PropertyManager.GetDouble("weapon_mod_magnitude_scale").Item;

            try
            {
                foreach (var scale in new[] { 0.0, 0.5, 2.0, 100.0 })
                {
                    PropertyManager.ModifyDouble("weapon_mod_magnitude_scale", scale);

                    foreach (var weaponClass in AllClasses)
                    {
                        var weapon = WeaponModTestKit.MakeHandTinkered(weaponClass);

                        // NO OPT-OUT at scale 0. A special that resolves to a magnitude of zero is no longer
                        // applied at all - its slot converts to a tinker - so the "a slot always buys something"
                        // invariant holds at every scale, including this one.
                        var probe = new WeaponModProbe(weapon);

                        for (var i = 1; i <= 10; i++)
                        {
                            Assert.IsNotNull(WeaponModManager.ApplyReroll(weapon, weaponClass, 10.0), $"scale {scale} {weaponClass} reroll {i}");
                            probe.AssertInvariants(weapon, weaponClass, $"magnitude scale {scale}, {weaponClass}, reroll {i}");

                            WeaponModTestKit.Swap(weapon, weaponClass, 10.0, $"scale {scale} {weaponClass} swap {i}");
                            probe.AssertInvariants(weapon, weaponClass, $"magnitude scale {scale}, {weaponClass}, swap {i}");
                        }
                    }

                    // the scale reaches the magnitude and nothing else
                    var devastation = WeaponModRegistry.Get(WeaponModId.Devastation);

                    Assert.AreEqual(Math.Round(devastation.MaxRoll * scale, MidpointRounding.AwayFromZero),
                        WeaponModValue.Resolve(devastation, 1.0, 10.0, scale), 1e-9, $"scale {scale}: a perfect Devastation");

                    // ... except on the binary modifier, which ignores it entirely
                    Assert.AreEqual(1.0, WeaponModValue.Resolve(WeaponModRegistry.Get(WeaponModId.Cleave), 1.0, 10.0, scale), 1e-12,
                        $"scale {scale}: Cleave is binary and must stay exactly +1");
                }

                // a negative or NaN scale is sanitized rather than producing a negative magnitude
                Assert.AreEqual(0.0, WeaponModValue.Resolve(WeaponModRegistry.Get(WeaponModId.Bloodthirst), 1.0, 10.0, -5.0), 1e-12, "a negative scale must not produce a negative magnitude");
                Assert.AreEqual(0.0, WeaponModValue.Resolve(WeaponModRegistry.Get(WeaponModId.Bloodthirst), 1.0, 10.0, double.NaN), 1e-12, "a NaN scale must not propagate");
            }
            finally
            {
                PropertyManager.ModifyDouble("weapon_mod_magnitude_scale", prior);
            }
        }

        [TestMethod]
        public void Tunables_SpecialChanceExtremesHoldEveryInvariant()
        {
            var prior1 = PropertyManager.GetDouble("weapon_mod_special_chance_1").Item;
            var prior2 = PropertyManager.GetDouble("weapon_mod_special_chance_2").Item;
            var prior3 = PropertyManager.GetDouble("weapon_mod_special_chance_3").Item;

            try
            {
                foreach (var chance in new[] { 0.0, 1.0 })
                {
                    PropertyManager.ModifyDouble("weapon_mod_special_chance_1", chance);
                    PropertyManager.ModifyDouble("weapon_mod_special_chance_2", chance);
                    PropertyManager.ModifyDouble("weapon_mod_special_chance_3", chance);

                    foreach (var weaponClass in AllClasses)
                    {
                        var weapon = WeaponModTestKit.MakeUntinkered(weaponClass);
                        var probe = new WeaponModProbe(weapon);

                        for (var i = 1; i <= 20; i++)
                        {
                            Assert.IsNotNull(WeaponModManager.ApplyReroll(weapon, weaponClass, 10.0), $"chance {chance} {weaponClass} reroll {i}");
                            probe.AssertInvariants(weapon, weaponClass, $"special chance {chance}, {weaponClass}, reroll {i}");

                            var specials = WeaponModTinkerSet.SpecialCount(weapon);

                            Assert.AreEqual(chance == 0.0 ? 0 : WeaponModRegistry.MaxSpecials, specials,
                                $"special chance {chance}, {weaponClass}, reroll {i}: {specials} specials");

                            Assert.AreEqual(WeaponModRegistry.TotalSlots - specials, weapon.GetProperty(PropertyInt.WeaponModTinkerCount),
                                $"special chance {chance}, {weaponClass}, reroll {i}: the remainder of the budget must go to tinkers");
                        }
                    }
                }
            }
            finally
            {
                PropertyManager.ModifyDouble("weapon_mod_special_chance_1", prior1);
                PropertyManager.ModifyDouble("weapon_mod_special_chance_2", prior2);
                PropertyManager.ModifyDouble("weapon_mod_special_chance_3", prior3);
            }
        }

        [TestMethod]
        public void Tunables_SwapChanceExtremesHoldEveryInvariant()
        {
            var prior = PropertyManager.GetDouble("weapon_mod_swap_special_chance").Item;

            try
            {
                // never a special: every swap trades a tinker for a tinker, forever
                PropertyManager.ModifyDouble("weapon_mod_swap_special_chance", 0.0);

                var never = WeaponModTestKit.MakeHandTinkered(WeaponClass.Melee);
                var neverProbe = new WeaponModProbe(never);

                for (var i = 1; i <= 40; i++)
                {
                    Assert.IsNotNull(WeaponModManager.ApplySwap(never, WeaponClass.Melee, 10.0), $"swap chance 0: use {i}");
                    neverProbe.AssertInvariants(never, WeaponClass.Melee, $"swap chance 0, use {i}");

                    Assert.AreEqual(0, WeaponModTinkerSet.SpecialCount(never), $"swap chance 0: use {i} granted a special");
                    Assert.AreEqual(WeaponModRegistry.TotalSlots, never.GetProperty(PropertyInt.WeaponModTinkerCount), $"swap chance 0: use {i} lost a slot");
                }

                // always a special: the trio arrives in exactly three swaps, and the fourth swap is REFUSED by
                // ApplySwap's own three-special guard rather than replacing one of the three
                PropertyManager.ModifyDouble("weapon_mod_swap_special_chance", 1.0);

                var always = WeaponModTestKit.MakeHandTinkered(WeaponClass.Melee);
                var alwaysProbe = new WeaponModProbe(always);

                var reachedThree = false;

                for (var i = 1; i <= 40; i++)
                {
                    var count = WeaponModTinkerSet.SpecialCount(always);

                    if (count >= WeaponModRegistry.MaxSpecials)
                    {
                        reachedThree = true;

                        Assert.IsNull(WeaponModManager.ApplySwap(always, WeaponClass.Melee, 10.0),
                            $"swap chance 1: use {i} ran on a full trio - the permanent bound must refuse, not replace one of the three");

                        continue;
                    }

                    Assert.IsNotNull(WeaponModTestKit.Swap(always, WeaponClass.Melee, 10.0, $"swap chance 1 use {i}"), $"swap chance 1: use {i}");
                    alwaysProbe.AssertInvariants(always, WeaponClass.Melee, $"swap chance 1, use {i}");

                    var after = WeaponModTinkerSet.SpecialCount(always);

                    // the removal may take one of the specials, but at a certain chance it is always replaced,
                    // so the count never goes DOWN and never passes the bound
                    Assert.IsTrue(after >= count && after <= WeaponModRegistry.MaxSpecials,
                        $"swap chance 1: use {i} moved the special count from {count} to {after}");
                }

                Assert.IsTrue(reachedThree, "swap chance 1: forty swaps did not reach a full trio");

                Assert.IsTrue(reachedThree, "forty swaps at a certain special chance must reach the bound of three");
            }
            finally
            {
                PropertyManager.ModifyDouble("weapon_mod_swap_special_chance", prior);
            }
        }
    }
}
