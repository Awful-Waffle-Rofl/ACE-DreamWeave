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
        ///
        /// REWRITTEN 2026-08-07 FOR THE SPECIAL-ONLY REROLL. The sweep used to end each iteration on
        /// "bits + tinkers == 10", which held only because the reroll refilled the budget on every use. It no
        /// longer lays down a tinker at all, so the assertion that replaces it is that the SLOT ARITHMETIC is
        /// still exactly right - ReservedImbueSlots and AvailableSlots are pure and unchanged, and they are
        /// what the refusal gate still reads - while the reroll itself provably spends none of it.
        ///
        /// The name is unchanged because the subject is unchanged: this is still the reserved-slot sweep.
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

                Assert.IsTrue(specials <= WeaponModRegistry.MaxSpecials,
                    $"{bits} imbue bits: {specials} specials exceeds the permanent bound");

                // the reroll spent NONE of the unreserved budget, whatever it rolled: no tinker count, no
                // counter, neither log. The reserved figure the gate reads is therefore unmoved too, which is
                // what makes the sweep above still describe the weapon AFTER a reroll and not just before one.
                Assert.IsNull(weapon.GetProperty(PropertyInt.WeaponModTinkerCount),
                    $"{bits} imbue bits: the reroll wrote a tinker count (specials held: {specials}) - it is special-only and spends no slot");

                Assert.IsNull(weapon.GetProperty(PropertyInt.NumTimesTinkered), $"{bits} imbue bits: the reroll wrote NumTimesTinkered");
                Assert.IsNull(weapon.GetProperty(PropertyString.WeaponModTinkerLog), $"{bits} imbue bits: the reroll wrote its own tinker log");

                Assert.AreEqual(bits, WeaponModTinkerSet.ReadReservedSlots(weapon),
                    $"{bits} imbue bits: the reserved figure moved across the reroll, so a later use would budget against a different weapon than the one it is looking at");
            }

            // every real bit at once - popcount 12, clamped to 10 rather than driving the tinker count to -2
            var everything = ImbueMask(ImbueBits.Length);

            Assert.AreEqual(WeaponModRegistry.TotalSlots, WeaponModTinkerSet.ReservedImbueSlots(everything),
                "an over-full imbue mask must clamp to 10, never exceed it");
            Assert.AreEqual(0, WeaponModTinkerSet.ComputeTinkerCount(WeaponModTinkerSet.ReservedImbueSlots(everything)));
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
        /// REWORKED 2026-08-06 for the Amethyst rework. Nine imbues leave exactly one tinker slot, and that
        /// slot is now COMPLETELY UNTOUCHABLE by a swap - Amethyst never reaches tinkers any more. So this test
        /// no longer describes "the one free slot moving" (there is nothing left for a swap to move it with);
        /// instead it seeds one special into the specials budget (independent of the imbue-reserved tinker
        /// slots since the 2026-08-06 decoupling) and confirms thirty swaps reroll ONLY that special - the
        /// imbue mask and the one tinker slot are byte-identical throughout.
        /// </summary>
        [TestMethod]
        public void Imbues_NineReservedSlotsSurviveThirtySwapsWithTheTinkerSlotUntouched()
        {
            var mask = ImbueMask(9);

            var weapon = WeaponModTestKit.MakeHandTinkered(WeaponClass.Melee, mask);

            // PRE-EXISTING TEST-HELPER QUIRK, surfaced by this change rather than caused by it: MakeHandTinkered
            // sets NumTimesTinkered to the TINKER count alone (1, behind nine imbues) rather than the imbues +
            // tinkers total (10). That was invisible before 2026-08-06 because ApplySwap's WriteComposition
            // unconditionally forced NumTimesTinkered back to 10 on every successful call; now that swap never
            // touches it at all, the helper's own value has to be correct going in.
            weapon.SetProperty(PropertyInt.NumTimesTinkered, WeaponModRegistry.TotalSlots);

            WeaponModTinkerSet.ApplySpecial(weapon, WeaponModRegistry.Get(WeaponModId.ShieldBypass), 0.3);

            var probe = new WeaponModProbe(weapon);

            Assert.AreEqual(9, WeaponModTinkerSet.ReadReservedImbueSlots(weapon));

            var beforeTinkerCount = weapon.GetProperty(PropertyInt.WeaponModTinkerCount);
            var beforeTinkerLog = weapon.GetProperty(PropertyString.WeaponModTinkerLog);

            for (var i = 1; i <= 30; i++)
            {
                if (WeaponModTinkerSet.SpecialCount(weapon) <= 0)
                    break; // a zero-magnitude replacement can drain the one seeded special - nothing left to reroll

                Assert.IsNotNull(WeaponModManager.ApplySwap(weapon, WeaponClass.Melee, 10.0), $"swap {i}: produced no result");

                Assert.AreEqual(mask, weapon.GetProperty(PropertyInt.ImbuedEffect), $"swap {i}: an imbue bit was destroyed");

                probe.AssertInvariants(weapon, WeaponClass.Melee, $"nine imbues swap {i}");

                var specials = WeaponModTinkerSet.SpecialCount(weapon);

                // SINCE THE 2026-08-06 DECOUPLING the ONE free slot is a TINKER slot, and specials no longer
                // compete for it, AND since the same-day Amethyst rework a swap never touches tinkers at all -
                // so the tinker count and log must be byte-identical to before the first swap, every time.
                Assert.AreEqual(beforeTinkerCount, weapon.GetProperty(PropertyInt.WeaponModTinkerCount), $"swap {i}: the tinker count moved");
                Assert.AreEqual(beforeTinkerLog, weapon.GetProperty(PropertyString.WeaponModTinkerLog), $"swap {i}: the tinker log moved");
                Assert.IsTrue(specials <= WeaponModRegistry.MaxSpecials, $"swap {i}: {specials} specials, above the permanent bound");
            }
        }

        /// <summary>
        /// RENAMED 2026-08-07 (was Imbues_TenReservedSlotsAreRefusedRatherThanProducingANegativeTinkerCount).
        ///
        /// THE REFUSALS ARE UNCHANGED AND ARE STILL THE SUBJECT. What changed is the second half: the old name
        /// promised a negative tinker count was the hazard being avoided, and pinned WeaponModTinkerCount at 0
        /// on the direct path to show the subtraction had bottomed out safely. The special-only reroll does not
        /// compute a tinker count at all, so there is no subtraction left to go negative - the hazard is gone
        /// by construction rather than guarded against. Asserting that NOTHING is written is what replaces it,
        /// and it fails against any implementation that starts computing a budget here again.
        /// </summary>
        [TestMethod]
        public void Imbues_TenReservedSlotsAreRefusedAndNoTinkerBudgetIsEverWritten()
        {
            var mask = ImbueMask(10);
            var weapon = WeaponModTestKit.MakeUntinkered(WeaponClass.Melee, mask);

            Assert.AreEqual(WeaponModManager.WeaponModRefusal.NoAvailableSlots,
                WeaponModTestKit.Refusal(WeaponModTestKit.MakeBag(MaterialType.Tourmaline), weapon),
                "a weapon whose imbues take all ten slots has nothing to reroll");

            // INVERTED 2026-08-07: the tinker budget no longer gates Amethyst, so ten imbues refuse the
            // reroll and permit the swap. Amethyst adds or rerolls a SPECIAL, which has its own budget.
            Assert.AreEqual(WeaponModManager.WeaponModRefusal.None,
                WeaponModTestKit.Refusal(WeaponModTestKit.MakeBag(MaterialType.Amethyst), weapon),
                "ten imbues must not refuse a swap - the tinker budget has no bearing on Amethyst");

            // and the application path is still safe if it is ever reached directly, bypassing those refusals
            Assert.IsNotNull(WeaponModManager.ApplyReroll(weapon, WeaponClass.Melee, 10.0));

            Assert.IsNull(weapon.GetProperty(PropertyInt.WeaponModTinkerCount),
                "the reroll wrote a tinker count on a weapon with zero available slots. Since 2026-08-07 it writes one on NO weapon, which is why the count can no longer come out negative here or anywhere else");

            Assert.IsNull(weapon.GetProperty(PropertyInt.NumTimesTinkered),
                "the reroll wrote NumTimesTinkered on a weapon that was never tinkered");

            // SINCE THE 2026-08-06 DECOUPLING a special no longer needs a free slot, so reaching this path
            // directly CAN mint one. That is not a hole: the two NoAvailableSlots refusals asserted above are
            // what make the path unreachable from play, and they are the guard - the arithmetic below never was.
            Assert.IsTrue(WeaponModTinkerSet.SpecialCount(weapon) <= WeaponModRegistry.MaxSpecials,
                "even on the unreachable path the permanent special bound holds");

            Assert.AreEqual(mask, weapon.GetProperty(PropertyInt.ImbuedEffect));
        }

        // ================= D. standard retail tinkering interaction =================

        /// <summary>
        /// A part-tinkered retail weapon: the reroll ACCEPTS while the swap refuses, because a swap trades one
        /// slot for another and must not be a cheaper way of filling empty ones.
        ///
        /// REWRITTEN 2026-08-07 FOR THE SPECIAL-ONLY REROLL, and this test is where the change is most visible
        /// to a player. The reroll used to ACCEPT AND THEN FILL: the weapon walked in at three tinkers and
        /// walked out at ten, which is why the swap became available immediately afterwards. It no longer
        /// touches layer 1, so a part-tinkered weapon stays part-tinkered - it can now gain specials from a
        /// reroll and STILL be refused a swap, permanently, until the player finishes tinkering it by hand.
        /// That is the intended shape: the reroll is a layer on top of ordinary tinkering rather than a
        /// substitute for it.
        ///
        /// The name is unchanged because the headline is unchanged - reroll accepts, swap refuses. What moved
        /// is everything the reroll does after it accepts.
        /// </summary>
        [TestMethod]
        public void Retail_APartTinkeredWeaponAcceptsBothToolsNow()
        {
            var weapon = WeaponModTestKit.MakeWeapon(ItemType.MeleeWeapon, CombatUse.Melee,
                new Dictionary<PropertyInt, int> { { PropertyInt.NumTimesTinkered, 3 }, { PropertyInt.Damage, 15 } });

            weapon.SetProperty(PropertyString.TinkerLog, "61,61,57");

            Assert.IsTrue(WeaponModTinkerSet.PassesIntegrityGate(weapon), "a 3-entry log against NumTimesTinkered 3 is consistent");

            // INVERTED 2026-08-07. A part-tinkered weapon used to be the canonical SwapNeedsFullBudget case:
            // the swap demanded NumTimesTinkered == 10. That rule is gone, so both tools now accept it.
            Assert.AreEqual(WeaponModManager.WeaponModRefusal.None,
                WeaponModTestKit.Refusal(WeaponModTestKit.MakeBag(MaterialType.Amethyst), weapon),
                "a part-tinkered weapon must accept a swap - the tinker counter has no bearing on Amethyst");

            Assert.AreEqual(WeaponModManager.WeaponModRefusal.None,
                WeaponModTestKit.Refusal(WeaponModTestKit.MakeBag(MaterialType.Tourmaline), weapon));

            var probe = new WeaponModProbe(weapon);

            Assert.IsNotNull(WeaponModManager.ApplyReroll(weapon, WeaponClass.Melee, 10.0));

            probe.AssertInvariants(weapon, WeaponClass.Melee, "reroll of a part-tinkered weapon");

            Assert.AreEqual(3, weapon.GetProperty(PropertyInt.NumTimesTinkered),
                "the reroll must leave the player's own tinker count exactly where it found it - three, not filled to ten");

            Assert.AreEqual("61,61,57", weapon.GetProperty(PropertyString.TinkerLog),
                "the reroll must leave the retail log byte-identical - the three entries are the player's work, not this system's");

            Assert.IsNull(weapon.GetProperty(PropertyInt.WeaponModTinkerCount),
                "the reroll must not mark a hand-tinkered weapon as managed - doing so would suppress the retail integrity gate on it for good");

            // the two retail Iron are NOT reversed and NOT rebuilt: the reroll never reaches layer 1, so the
            // weapon's Damage is exactly the number it walked in with
            Assert.AreEqual(15, weapon.GetProperty(PropertyInt.Damage),
                "Damage moved, so the reroll reversed or re-applied a layer 1 tinker. It must do neither");

            // ... and the swap is accepted afterwards too. This assertion is kept as a SECOND check rather
            // than folded into the first because it was the sharp consequence of the old rule: the reroll
            // deliberately stopped filling the tinker budget, which used to mean a rerolled part-tinkered
            // weapon could never be swapped. With the budget requirement gone, the sequence works end to end.
            var specials = WeaponModTinkerSet.SpecialCount(weapon);

            Assert.AreEqual(WeaponModManager.WeaponModRefusal.None,
                WeaponModTestKit.Refusal(WeaponModTestKit.MakeBag(MaterialType.Amethyst), weapon),
                $"the reroll left {specials} specials on a weapon with a 3-tinker budget, and the swap must accept it - the tinker budget is no longer a swap precondition");
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

            // INVERTED 2026-08-07: only the REROLL reverses a tinker set, so only the reroll needs the log
            // to be trustworthy. Amethyst never reads or reverses one.
            Assert.AreEqual(WeaponModManager.WeaponModRefusal.None,
                WeaponModTestKit.Refusal(WeaponModTestKit.MakeBag(MaterialType.Amethyst), weapon),
                "a mismatched log must not refuse a swap - Amethyst never reverses a tinker");

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

            // INVERTED 2026-08-07: the "second free budget" hazard above is a REROLL hazard - it is the reroll
            // that would refill slots. Amethyst adds or rerolls a special and never issues a tinker budget at
            // all, so ten unreversible tinkers are simply not its problem.
            Assert.AreEqual(WeaponModManager.WeaponModRefusal.None,
                WeaponModTestKit.Refusal(WeaponModTestKit.MakeBag(MaterialType.Amethyst), weapon),
                "ten unreversible tinkers must not refuse a swap - Amethyst never hands out a tinker budget");
        }

        /// <summary>
        /// RENAMED AND REWRITTEN 2026-08-07 (was Reroll_UnknownLogEntriesKeepTheirSlotsAndStayInTheRetailLog).
        ///
        /// The old contract was a carry-forward: the reroll rewrote both logs, so the five Oak entries it could
        /// not reverse had to be copied into the new log verbatim or their slots would be handed back on the
        /// NEXT use. The special-only reroll does not rewrite the log at all, which retires the whole class of
        /// bug rather than guarding it - there is no rewrite for the carry-forward to be omitted from.
        ///
        /// WHAT THIS TEST NOW PINS IS STRICTLY STRONGER, and it is a byte comparison rather than a count. The
        /// old form asserted the Oak entries SURVIVED and that the totals still summed to ten; it would have
        /// passed against an implementation that reordered the log, or that reversed the five Iron and laid
        /// down five different ones. This asserts the whole row is character-for-character the string the
        /// player's own tinkering left, and that the Iron's effect on Damage is likewise untouched.
        ///
        /// The Oak case is kept as the fixture rather than simplified away because it is the state where the
        /// old code did the most work, and therefore the state where a partial revert would show up first.
        /// </summary>
        [TestMethod]
        public void Reroll_LeavesAPartlyUnknownTinkerLogByteIdentical()
        {
            const string log = "75,75,75,75,75,61,61,61,61,61";

            var weapon = WeaponModTestKit.MakeWeapon(ItemType.MeleeWeapon, CombatUse.Melee,
                new Dictionary<PropertyInt, int> { { PropertyInt.NumTimesTinkered, 10 }, { PropertyInt.WeaponTime, 0 }, { PropertyInt.Damage, 18 } });

            weapon.SetProperty(PropertyString.TinkerLog, log);

            // the reserved arithmetic still reads five, and the refusal gate still budgets against it - that is
            // unchanged by the rework and is the reason the weapon is accepted at all
            Assert.AreEqual(5, WeaponModTinkerSet.ReadReservedSlots(weapon), "precondition: five Oak entries reserve five slots");

            for (var pass = 1; pass <= 3; pass++)
            {
                Assert.IsNotNull(WeaponModManager.ApplyReroll(weapon, WeaponClass.Melee, 10.0), $"pass {pass}");

                var specials = WeaponModTinkerSet.SpecialCount(weapon);

                Assert.AreEqual(log, weapon.GetProperty(PropertyString.TinkerLog),
                    $"pass {pass}: TinkerLog moved. The reroll is special-only since 2026-08-07 - it must not reorder, rewrite or re-serialize this row, let alone drop the five Oak entries (specials held: {specials})");

                Assert.IsNull(weapon.GetProperty(PropertyString.WeaponModTinkerLog),
                    $"pass {pass}: WeaponModTinkerLog was written, which would switch reversal onto a log this system did not build");

                Assert.IsNull(weapon.GetProperty(PropertyInt.WeaponModTinkerCount),
                    $"pass {pass}: a tinker count was written, which would mark this hand-tinkered weapon as managed and suppress the retail integrity gate on it");

                Assert.AreEqual(WeaponModRegistry.TotalSlots, weapon.GetProperty(PropertyInt.NumTimesTinkered), $"pass {pass}: NumTimesTinkered moved");

                // the five Iron are neither reversed nor rebuilt, so Damage is exactly what it was
                Assert.AreEqual(18, weapon.GetProperty(PropertyInt.Damage),
                    $"pass {pass}: Damage moved, so the reroll reached layer 1. The five Iron in this log belong to the player's tinkering and must be left alone");

                Assert.AreEqual(0, weapon.GetProperty(PropertyInt.WeaponTime), $"pass {pass}: WeaponTime moved - Oak is unreversible and must never be touched");

                // and the carried-forward reading of the log is unchanged, so the NEXT use budgets identically
                CollectionAssert.AreEqual(Enumerable.Repeat(MaterialType.Oak, 5).ToList(), WeaponModTinkerSet.ReadUnaccountedEntries(weapon),
                    $"pass {pass}: the unreversible entries must still read as exactly the five Oak, in log order");

                Assert.AreEqual(5, WeaponModTinkerSet.ReadReservedSlots(weapon), $"pass {pass}: the reserved figure moved, so a later use would budget against a different weapon");
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
        /// RENAMED AND REWRITTEN 2026-08-07 (was Retail_EveryOperationLeavesNumTimesTinkeredAtExactlyTen).
        ///
        /// The old contract was that both operations SET the counter to ten, which is what left the retail
        /// recipe_requirements_int gate blocking further ordinary tinkering. Neither operation writes it any
        /// more, so the property that replaces it is CONSERVATION: whatever the counter read going in, it reads
        /// coming out, across every starting state including having no counter at all.
        ///
        /// THE UNDERLYING SAFETY CONCERN IS UNCHANGED AND IS NOW SATISFIED MORE DIRECTLY. Eleven would be worse
        /// than a refusal - TinkeringDifficulty is an unguarded ten-element list indexed by this counter - and a
        /// path that never writes the counter can never drive it out of range. The starting value 10 is kept in
        /// the sweep because it is the state where a write and a no-write are indistinguishable, so it is the
        /// one that would let a partial revert through if it were dropped.
        /// </summary>
        [TestMethod]
        public void Retail_NoOperationEverMovesNumTimesTinkered()
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

                    // null at startCount 0: the weapon carries no counter at all, and must not acquire one
                    var expected = startCount > 0 ? (int?)startCount : null;
                    var expectedLog = weapon.GetProperty(PropertyString.TinkerLog);

                    Assert.IsNotNull(WeaponModManager.ApplyReroll(weapon, weaponClass, 10.0));

                    Assert.AreEqual(expected, weapon.GetProperty(PropertyInt.NumTimesTinkered),
                        $"{weaponClass} from {startCount} tinkers: a reroll must leave the counter exactly as it found it, including leaving it ABSENT");

                    Assert.AreEqual(expectedLog, weapon.GetProperty(PropertyString.TinkerLog),
                        $"{weaponClass} from {startCount} tinkers: a reroll must leave the retail log exactly as it found it");

                    // refuses only when the reroll happened to land zero specials, which leaves the counter alone
                    WeaponModTestKit.Swap(weapon, weaponClass, 10.0, $"{weaponClass} from {startCount} tinkers");

                    Assert.AreEqual(expected, weapon.GetProperty(PropertyInt.NumTimesTinkered),
                        $"{weaponClass} from {startCount} tinkers: a swap must leave the counter exactly as it found it");

                    Assert.AreEqual(expectedLog, weapon.GetProperty(PropertyString.TinkerLog),
                        $"{weaponClass} from {startCount} tinkers: a swap must leave the retail log exactly as it found it");
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
        /// CONFIRMED DEFECT, ORIGINALLY FOUND AGAINST SWIFT FLIGHT (asserts the CORRECT behaviour and
        /// therefore fails on a broken implementation).
        ///
        /// WeaponModDefinition.ReverseValue:160-162 removes the native row whenever the restored value equals
        /// the engine default. Swift Flight's default was 20.0 (Creature_Missile.cs:517), and six ace_world
        /// launchers carried MaximumVelocity exactly 20.0 - so reversing Swift Flight deleted their velocity
        /// row, and WeaponProfile.cs:57 then read "?? 1.0f" and showed the player a velocity of 1.0.
        ///
        /// MaximumVelocity has no single agreed engine default, so the row must never be removed.
        ///
        /// RETARGETED TO A SYNTHETIC DEFINITION, 2026-08-17. Swift Flight was retired in the catalog v4 pass
        /// and was the only row that ever set RemoveOnDefault = false, so this test would otherwise lose its
        /// only live subject - see WeaponModFixTests.Fix3_ARemoveOnDefaultFalseRowRestoresTheDefaultRatherThanRemovingTheRow
        /// for the sibling test retargeted the same way, with the fuller reasoning for why the test is kept
        /// rather than deleted.
        /// </summary>
        [TestMethod]
        public void Specials_ARemoveOnDefaultFalseRowNeverRemovesMaximumVelocity()
        {
            var definition = new WeaponModDefinition
            {
                Id = WeaponModId.PanicReload,          // any id; nothing here reads the registry
                DisplayName = "Synthetic Disagreeing Default",
                Record = PropertyFloat.WeaponModPanicReload,
                NativeFloat = PropertyFloat.MaximumVelocity,
                MaxRoll = 6.0,
                NativeDefault = 20.0,
                RemoveOnDefault = false,
                Classes = WeaponClass.Missile,
                DisplayFormat = "+{0:0.##} missile velocity",
            };

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
        /// the two must be indistinguishable after a full apply/reverse cycle. Rows with a non-zero NativeFloor
        /// are excluded on purpose - their native cannot legally hold 0, so the premise does not apply - and
        /// the filter is written against the FLOOR rather than against a name so it keeps working whatever the
        /// catalog holds. (It excluded exactly one row, Cleave, until Cleave was removed on 2026-08-07; it
        /// excludes none today and is kept as the rule, not as a workaround for a specific row.) Tier B is
        /// excluded because it has no native property to start present or absent.
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
            // UPDATED 2026-08-07: this used Cleave as the first stranded record until Cleave was removed from
            // the catalog. Shield Bypass replaces it - it is the Tier A row no caster can roll.
            //
            // UPDATED AGAIN 2026-08-17: this used Swift Flight as the second stranded record until it was
            // retired in the catalog v4 pass - it was the other Tier A row no caster can roll, and with it
            // gone Shield Bypass is now the ONLY Tier A row outside the caster pool. Quickening (Tier B,
            // melee-and-missile-only) replaces it, keeping the test's real point intact: TWO records outside
            // the class pool are cleared rather than one, so the walk cannot be passing by luck on a
            // single-element list.
            var shieldBypass = WeaponModRegistry.Get(WeaponModId.ShieldBypass);
            var quickening = WeaponModRegistry.Get(WeaponModId.Quickening);

            Assert.IsFalse(shieldBypass.AppliesTo(WeaponClass.Caster), "precondition: Shield Bypass is not a caster modifier");
            Assert.IsFalse(quickening.AppliesTo(WeaponClass.Caster), "precondition: Quickening is not a caster modifier");

            var weapon = WeaponModTestKit.MakeUntinkered(WeaponClass.Caster);
            var probe = new WeaponModProbe(weapon);

            WeaponModTinkerSet.ApplySpecial(weapon, shieldBypass, 0.3);
            WeaponModTinkerSet.ApplySpecial(weapon, quickening, quickening.MaxRoll);

            Assert.AreEqual(2, WeaponModTinkerSet.SpecialCount(weapon), "precondition: both stranded records are present");

            Assert.IsNotNull(WeaponModManager.ApplyReroll(weapon, WeaponClass.Caster, 10.0));

            Assert.IsNull(weapon.GetProperty(shieldBypass.Record), "the reroll must clear a record from outside the class pool, not skip it");
            Assert.IsNull(weapon.GetProperty(quickening.Record), "the reroll must clear a record from outside the class pool, not skip it");
            Assert.IsNull(weapon.GetProperty(PropertyFloat.IgnoreShield), "IgnoreShield must go back to absent when the stranded Shield Bypass record is cleared");

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
            // Cleave (melee only) was the first two assertions here until it was removed on 2026-08-07, and
            // Swift Flight (missile only) was the next three until it was removed on 2026-08-17. Their absence
            // from EVERY pool is asserted in WeaponModPoolCutTests rather than restated here. Panic Reload
            // (Tier B v4, missile only) replaces Swift Flight as the missile-only case, but UNLIKE Swift
            // Flight it is Tier B and therefore gated by weapon_mods_enabled - the "not reachable" checks hold
            // regardless of gate state (an unreachable row stays unreachable with the gate on too), but the
            // positive "must be available" check needs the gate forced on explicitly rather than relying on
            // the live default either way.
            Assert.IsFalse(WeaponModRegistry.Pool(WeaponClass.Melee).Any(m => m.Id == (WeaponModId)4), "Cleave's retired id must not be reachable from the melee pool");
            Assert.IsFalse(WeaponModRegistry.Pool(WeaponClass.Caster).Any(m => m.Id == WeaponModId.ShieldBypass), "Shield Bypass is not a caster modifier");
            Assert.IsFalse(WeaponModRegistry.Pool(WeaponClass.Caster).Any(m => m.Id == WeaponModId.PanicReload), "Panic Reload is not a caster modifier");
            Assert.IsFalse(WeaponModRegistry.Pool(WeaponClass.Melee).Any(m => m.Id == WeaponModId.PanicReload), "Panic Reload is missile only");
            Assert.IsTrue(WeaponModRegistry.Pool(WeaponClass.Missile, true).Any(m => m.Id == WeaponModId.PanicReload), "Panic Reload must be available to missile weapons with the gate on");

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

            // BOTH accept a part-tinkered weapon as of 2026-08-07. This line was the SwapNeedsFullBudget case
            // until the tinker counter stopped gating Amethyst; there is no swap-only refusal left to assert.
            Assert.AreEqual(WeaponModManager.WeaponModRefusal.None, WeaponModTestKit.Refusal(amethyst, partTinkered), "part-tinkered, swap");
            Assert.AreEqual(WeaponModManager.WeaponModRefusal.None, WeaponModTestKit.Refusal(tourmaline, partTinkered), "part-tinkered, reroll");

            // a weapon loaded to exactly the cap. A FOURTH row is needed because the cap went from three to four
            // on 2026-08-06; the first three alone no longer reach it. Shield Bypass is that fourth as of
            // 2026-08-07 - it was Cleave until Cleave was removed, and Shield Bypass is what the melee Tier A
            // pool has left, which is also why the melee pool is now exactly as deep as the cap.
            var trio = WeaponModTestKit.MakeHandTinkered(WeaponClass.Melee);

            foreach (var (id, magnitude) in new[]
            {
                (WeaponModId.Devastation,  5.0),
                (WeaponModId.WeakPoint,    3.0),
                (WeaponModId.Bloodthirst,  5.0),
                (WeaponModId.ShieldBypass, 0.3),
            })
            {
                WeaponModTinkerSet.ApplySpecial(trio, WeaponModRegistry.Get(id), magnitude);
            }

            Assert.AreEqual(WeaponModRegistry.MaxSpecials, WeaponModTinkerSet.SpecialCount(trio));

            // SUPERSEDED 2026-08-06: SwapAtSpecialCap is gone. A full set is swappable now - Amethyst rerolls a
            // special AT the cap by design.
            Assert.AreEqual(WeaponModManager.WeaponModRefusal.None, WeaponModTestKit.Refusal(amethyst, trio), "at the special cap, swap");
            Assert.AreEqual(WeaponModManager.WeaponModRefusal.None, WeaponModTestKit.Refusal(tourmaline, trio), "at the special cap, reroll");

            // --- ten imbues ---
            var imbued = WeaponModTestKit.MakeUntinkered(WeaponClass.Melee, ImbueMask(10));

            Assert.AreEqual(WeaponModManager.WeaponModRefusal.NoAvailableSlots, WeaponModTestKit.Refusal(tourmaline, imbued), "ten imbues");
            Assert.AreEqual(WeaponModManager.WeaponModRefusal.None, WeaponModTestKit.Refusal(amethyst, imbued), "ten imbues, swap - the tinker budget is reroll-only since 2026-08-07");
        }

        /// <summary>
        /// SUPERSEDED 2026-08-06: this used to pin ApplySwap refusing a weapon already at the special cap, on
        /// the "reroll a single special" concern design section 5 raised. That concern is now moot BY DESIGN:
        /// Amethyst IS the "reroll a single special" tool, deliberately, as of the same-day rework. This test
        /// now pins the opposite: at a full set, ApplySwap succeeds, replaces EXACTLY ONE special, and leaves
        /// tinkers and the tinker log byte-identical, because the new path never reaches them.
        /// </summary>
        [TestMethod]
        public void Swap_ApplySwapRerollsExactlyOneSpecialAtTheCapAndNeverTouchesTinkers()
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                // build the realistic managed state directly - seven tinkers and a full set. Amethyst can no
                // longer grind a weapon up to a full set from zero (it needs a special to reroll, not add), so
                // the set is seeded with ApplySpecial rather than by playing the swap repeatedly.
                var weapon = WeaponModTestKit.MakeHandTinkered(WeaponClass.Melee);

                // Shield Bypass is the fourth row as of 2026-08-07 - it was Cleave until Cleave was removed
                foreach (var (id, magnitude) in new[]
                {
                    (WeaponModId.Devastation,  5.0),
                    (WeaponModId.WeakPoint,    3.0),
                    (WeaponModId.Bloodthirst,  5.0),
                    (WeaponModId.ShieldBypass, 0.3),
                })
                {
                    WeaponModTinkerSet.ApplySpecial(weapon, WeaponModRegistry.Get(id), magnitude);
                }

                Assert.AreEqual(WeaponModRegistry.MaxSpecials, WeaponModTinkerSet.SpecialCount(weapon), $"attempt {attempt}: precondition - a full set");

                var before = WeaponModTinkerSet.ReadSpecials(weapon).Select(s => s.Definition.Id).OrderBy(id => id).ToList();
                var beforeTinkers = weapon.GetProperty(PropertyString.WeaponModTinkerLog);
                var beforeTinkerCount = weapon.GetProperty(PropertyInt.WeaponModTinkerCount);

                var lines = WeaponModManager.ApplySwap(weapon, WeaponClass.Melee, 10.0);

                Assert.IsNotNull(lines, $"attempt {attempt}: ApplySwap must succeed on a full set - rerolling a special AT the cap is its purpose");

                Assert.IsTrue(WeaponModManager.CanApply(weapon, WeaponClass.Melee, WeaponModManager.WeaponModAction.Swap),
                    $"attempt {attempt}: a full set must remain swappable, so a repeated Amethyst use is never refused");

                // a reroll trades one special for one, EXCEPT the rare case where the replacement resolves to a
                // zero magnitude and is dropped (WeaponModValue.IsLiveMagnitude) - the count can then be one
                // lower, never more
                var countAfter = WeaponModTinkerSet.SpecialCount(weapon);
                Assert.IsTrue(countAfter == WeaponModRegistry.MaxSpecials || countAfter == WeaponModRegistry.MaxSpecials - 1,
                    $"attempt {attempt}: expected {WeaponModRegistry.MaxSpecials} or {WeaponModRegistry.MaxSpecials - 1} specials after the swap, got {countAfter}");

                var after = WeaponModTinkerSet.ReadSpecials(weapon).Select(s => s.Definition.Id).OrderBy(id => id).ToList();

                // at most one identity changed: exactly one lost and one gained (a replacement landed), or one
                // lost and none gained (a zero-magnitude replacement was dropped - see the count assertion
                // above), or none lost at all only when the reroll landed back on the identity it just removed
                var lost = before.Except(after).ToList();
                var gained = after.Except(before).ToList();

                Assert.IsTrue(lost.Count <= 1 && gained.Count <= lost.Count,
                    $"attempt {attempt}: expected at most one special lost and no more gained than lost; before [{string.Join(",", before)}], after [{string.Join(",", after)}]");

                Assert.AreEqual(beforeTinkers, weapon.GetProperty(PropertyString.WeaponModTinkerLog),
                    $"attempt {attempt}: the swap must never touch the tinker log");

                Assert.AreEqual(beforeTinkerCount, weapon.GetProperty(PropertyInt.WeaponModTinkerCount),
                    $"attempt {attempt}: the swap must never touch the tinker count");
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

            Assert.AreEqual(0.60, WeaponModDefinition.DefaultMinPotency, 1e-12, "the design's potency floor is 0.60 (retuned from 0.25 on 2026-08-06), and since the binary Cleave was removed on 2026-08-07 it applies to every row without exception");
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

                    // ... and there is no longer any row it does NOT reach: Cleave was the one binary modifier
                    // that ignored the scale entirely, and it was removed on 2026-08-07. Asserted as an
                    // every-row rule rather than dropped, because "the scale reaches every magnitude" is a
                    // stronger statement than the single exception it replaces.
                    foreach (var mod in WeaponModRegistry.AllMods)
                    {
                        Assert.IsFalse(mod.Binary,
                            $"scale {scale}: {mod.Id} is Binary, so it ignores weapon_mod_magnitude_scale. That is legitimate machinery, but no row has used it since Cleave was removed - widen this assertion deliberately rather than deleting it");
                    }
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
            var prior4 = PropertyManager.GetDouble("weapon_mod_special_chance_4").Item;

            try
            {
                foreach (var chance in new[] { 0.0, 1.0 })
                {
                    PropertyManager.ModifyDouble("weapon_mod_special_chance_1", chance);
                    PropertyManager.ModifyDouble("weapon_mod_special_chance_2", chance);
                    PropertyManager.ModifyDouble("weapon_mod_special_chance_3", chance);
                    PropertyManager.ModifyDouble("weapon_mod_special_chance_4", chance);

                    foreach (var weaponClass in AllClasses)
                    {
                        var weapon = WeaponModTestKit.MakeUntinkered(weaponClass);
                        var probe = new WeaponModProbe(weapon);

                        // a draw is bounded by the POOL as well as by the cap, and the Tier A caster pool is
                        // shallower than the cap since the 2026-08-06 raise
                        var reachable = Math.Min(WeaponModRegistry.MaxSpecials, WeaponModRegistry.Pool(weaponClass).Count);

                        for (var i = 1; i <= 20; i++)
                        {
                            Assert.IsNotNull(WeaponModManager.ApplyReroll(weapon, weaponClass, 10.0), $"chance {chance} {weaponClass} reroll {i}");
                            probe.AssertInvariants(weapon, weaponClass, $"special chance {chance}, {weaponClass}, reroll {i}");

                            var specials = WeaponModTinkerSet.SpecialCount(weapon);

                            Assert.AreEqual(chance == 0.0 ? 0 : reachable, specials,
                                $"special chance {chance}, {weaponClass}, reroll {i}: {specials} specials");

                            // REWRITTEN 2026-08-07 FOR THE SPECIAL-ONLY REROLL. This used to assert the tinker
                            // budget came back FULL at both chance extremes, which was the 2026-08-06 decoupling
                            // expressed through a refill. The stronger form of the same claim now that there is
                            // no refill: the tinker side is not merely independent of the special count, it is
                            // not written at all - so driving the special odds from 0 to 1 provably cannot reach
                            // layer 1 by any route.
                            Assert.IsNull(weapon.GetProperty(PropertyInt.WeaponModTinkerCount),
                                $"special chance {chance}, {weaponClass}, reroll {i}: a tinker count was written on an untinkered weapon, so the special odds are reaching layer 1");

                            Assert.IsNull(weapon.GetProperty(PropertyInt.NumTimesTinkered),
                                $"special chance {chance}, {weaponClass}, reroll {i}: NumTimesTinkered was written on an untinkered weapon");

                            Assert.IsNull(weapon.GetProperty(PropertyString.WeaponModTinkerLog),
                                $"special chance {chance}, {weaponClass}, reroll {i}: a tinker log was written on an untinkered weapon");
                        }
                    }
                }
            }
            finally
            {
                PropertyManager.ModifyDouble("weapon_mod_special_chance_1", prior1);
                PropertyManager.ModifyDouble("weapon_mod_special_chance_2", prior2);
                PropertyManager.ModifyDouble("weapon_mod_special_chance_3", prior3);
                PropertyManager.ModifyDouble("weapon_mod_special_chance_4", prior4);
            }
        }

        /// <summary>
        /// SUPERSEDED 2026-08-06 (Amethyst rework). weapon_mod_swap_special_chance used to be the odds ApplySwap
        /// added a special rather than a tinker; that whole "add" half of the swap is gone. THE TUNABLE IS NOW
        /// DEAD ON THIS PATH - a reroll always replaces, unconditionally, per the explicit ruling in
        /// WeaponModManager.ApplySwap's remarks. This test now pins that deadness directly: forcing the tunable
        /// to either extreme changes nothing about ApplySwap's behaviour. The tunable is left declared (not
        /// deleted) in case a future path reads it; if nothing ever does again, that is a separate cleanup.
        /// </summary>
        [TestMethod]
        public void Tunables_SwapChanceIsDeadOnTheApplySwapPath()
        {
            var prior = PropertyManager.GetDouble("weapon_mod_swap_special_chance").Item;

            try
            {
                foreach (var chance in new[] { 0.0, 1.0 })
                {
                    PropertyManager.ModifyDouble("weapon_mod_swap_special_chance", chance);

                    var weapon = WeaponModTestKit.MakeHandTinkered(WeaponClass.Melee);
                    WeaponModTinkerSet.ApplySpecial(weapon, WeaponModRegistry.Get(WeaponModId.ShieldBypass), 0.3);

                    var beforeTinkerLog = weapon.GetProperty(PropertyString.WeaponModTinkerLog);
                    var beforeTinkerCount = weapon.GetProperty(PropertyInt.WeaponModTinkerCount);

                    var probe = new WeaponModProbe(weapon);

                    for (var i = 1; i <= 20 && WeaponModTinkerSet.SpecialCount(weapon) > 0; i++)
                    {
                        Assert.IsNotNull(WeaponModManager.ApplySwap(weapon, WeaponClass.Melee, 10.0), $"chance {chance}: use {i}");
                        probe.AssertInvariants(weapon, WeaponClass.Melee, $"swap chance {chance}, use {i}");

                        // the tunable this test is about must move NOTHING - the swap never reaches tinkers
                        // regardless of what weapon_mod_swap_special_chance is set to
                        Assert.AreEqual(beforeTinkerLog, weapon.GetProperty(PropertyString.WeaponModTinkerLog), $"chance {chance}: use {i} touched the tinker log");
                        Assert.AreEqual(beforeTinkerCount, weapon.GetProperty(PropertyInt.WeaponModTinkerCount), $"chance {chance}: use {i} touched the tinker count");
                    }
                }
            }
            finally
            {
                PropertyManager.ModifyDouble("weapon_mod_swap_special_chance", prior);
            }
        }
    }
}
