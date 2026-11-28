using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

using ACE.Common;
using ACE.Entity;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.EquipmentMods;
using ACE.Server.Factories;
using ACE.Server.SpellReroll;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

namespace ACE.Server.SalvageRoller
{
    /// <summary>
    /// The Salvage Roller's candidate engine (Docs/SalvageRoller/DESIGN.md 5.5): rolls N results for one target
    /// and returns each as a concrete diff plus a summary, never touching the target itself.
    ///
    /// SCRATCH COPIES ONLY. Every roll runs on a copy built from a DuoGemSnapshot of the target with
    /// ObjectGuid.Invalid (the VaultReferenceCache precedent), so it takes no dynamic guid, never enters the world,
    /// is never saved and is never Destroyed. The stored result is the difference between that copy before and
    /// after the roll, and <see cref="ApplyDiff"/> reproduces the rolled copy from the original exactly.
    ///
    /// WHAT IS REUSED, per material - the same mutation functions the bag paths call, so a roll here IS a manual
    /// application, minus the fuel, the save and the network:
    ///   Tourmaline  fresh copy per roll; WeaponModManager.CanApply then ApplyReroll.
    ///   Amethyst    CHAIN: ONE working copy takes swap 1, then swap 2 on top of it, up to swap N; the state after
    ///               each swap is candidate k. CanApply runs before EVERY swap.
    ///   Obsidian    fresh copy per roll; EquipmentModManager.TargetStateRefusal resolves Convert or Reroll and the
    ///               mod count off the copy, then ApplyConvertOrReroll with WriteRatingToItem as the rating sink.
    ///   Green Jade  CHAIN, like Amethyst: ONE working copy takes GreenJadeManager.ApplyStep per charge (an add while the
    ///               piece has fewer than ten aspects, a swap at ten); the state after k steps is candidate k.
    ///   Serpentine  fresh copy per roll; SpellRerollManager.BuildPlan with a ThreadSafeRandom picker, then ApplyPlan.
    ///
    /// The tier aura is NOT run here: the rollers never did, and it runs once on the real item at take.
    ///
    /// A ROLL THAT CANNOT BE MADE THROWS. Eligibility (SalvageRollerEligibility.VerifyBasket) has already accepted the
    /// target, and every roll but Amethyst's starts from that same accepted state, so a refusal here means the item
    /// changed underneath the batch or a manager's rules drifted from the verify. Neither may produce a partial
    /// candidate list: the caller (the sell pipeline) treats the exception as an abort before anything is committed.
    /// </summary>
    public static class SalvageRollerEngine
    {
        /// <summary>The hard ceiling on rolls per batch (owner ruling 6), whatever salvage_roller_max_candidates says.</summary>
        public const int MaxRolls = 100;

        /// <summary>
        /// Plans one Serpentine reroll of a scratch copy. Test seam: production is SpellRerollManager.BuildPlan with the
        /// same ThreadSafeRandom picker the bag path uses, which reads the world database's weenie and loot tables.
        /// </summary>
        internal static Func<WorldObject, (SpellRerollPlan Plan, RerollRefusal Refusal)> SerpentinePlanner = target =>
        {
            var plan = SpellRerollManager.BuildPlan(target, n => ThreadSafeRandom.Next(0, n - 1), out var refusal);
            return (plan, refusal);
        };

        /// <summary>
        /// The Green Jade draw: a uniform value in [0, n). Test seam, so the chain-parity tests can replay one draw
        /// sequence through the roller and through hand-applied steps. Production is GreenJadeManager.DefaultNext.
        /// </summary>
        internal static Func<int, int> GreenJadeNext = GreenJadeManager.DefaultNext;

        /// <summary>One Serpentine swap as a summary line. Test seam: production reads spell names from the DAT.</summary>
        internal static Func<SpellRerollSwap, string> SerpentineSwapDescriber = SpellRerollManager.DescribeSwap;

        /// <summary>
        /// Rolls <paramref name="rolls"/> candidates (1 to <see cref="MaxRolls"/>) for <paramref name="target"/>, in
        /// roll order, sequence numbers 1..N. The target is read, never changed.
        /// </summary>
        public static List<SalvageRollerCandidate> Roll(WorldObject target, SalvageRollerAction action, int rolls)
        {
            return Roll(target, action, rolls, null);
        }

        /// <summary>
        /// <see cref="Roll(WorldObject, SalvageRollerAction, int)"/>, handing each rolled scratch copy to
        /// <paramref name="observeRolledCopy"/> (sequence number, copy) the moment its candidate is taken. For the
        /// tests that prove the original plus a candidate's diff equals the rolled copy.
        /// </summary>
        internal static List<SalvageRollerCandidate> Roll(WorldObject target, SalvageRollerAction action, int rolls, Action<int, WorldObject> observeRolledCopy)
        {
            if (target == null)
                throw new ArgumentNullException(nameof(target));

            if (rolls < 1 || rolls > MaxRolls)
                throw new ArgumentOutOfRangeException(nameof(rolls), rolls, $"a batch rolls 1 to {MaxRolls} times");

            var snapshot = DuoGemSnapshot.Capture(target);
            var candidates = new List<SalvageRollerCandidate>(rolls);

            switch (action)
            {
                case SalvageRollerAction.TourmalineReroll:

                    for (var seq = 1; seq <= rolls; seq++)
                    {
                        var copy = NewScratchCopy(snapshot);
                        var before = DuoGemSnapshot.Capture(copy);

                        RollWeapon(copy, WeaponModManager.WeaponModAction.Reroll, target, seq);

                        candidates.Add(Candidate(seq, before, copy, WeaponSummary(copy)));
                        observeRolledCopy?.Invoke(seq, copy);
                    }
                    break;

                case SalvageRollerAction.AmethystSwap:
                {
                    // CHAIN (owner ruling 2): one working copy, every swap applied on top of the last, exactly as
                    // swaps applied by hand. Each candidate is still diffed against the ORIGINAL, so candidate k
                    // applied to the real item reproduces the weapon after k swaps.
                    var copy = NewScratchCopy(snapshot);
                    var before = DuoGemSnapshot.Capture(copy);

                    for (var seq = 1; seq <= rolls; seq++)
                    {
                        RollWeapon(copy, WeaponModManager.WeaponModAction.Swap, target, seq);

                        candidates.Add(Candidate(seq, before, copy, WeaponSummary(copy)));
                        observeRolledCopy?.Invoke(seq, copy);
                    }
                    break;
                }

                case SalvageRollerAction.GreenJadeSwap:
                {
                    // CHAIN, the Amethyst shape: one working copy, ApplyStep per charge, candidate k diffed against the
                    // ORIGINAL. A piece mid-fill adds until it reaches ten and swaps from there, exactly as by hand.
                    var copy = NewScratchCopy(snapshot);
                    var before = DuoGemSnapshot.Capture(copy);

                    for (var seq = 1; seq <= rolls; seq++)
                    {
                        if (!JewelryAspectMath.IsAspected(copy))
                            throw RollFailed(target, action, seq, "the piece is not aspected");

                        var counts = GreenJadeManager.ApplyStep(copy, GreenJadeNext, out _, out _, out _);

                        candidates.Add(Candidate(seq, before, copy, GreenJadeSummary(counts)));
                        observeRolledCopy?.Invoke(seq, copy);
                    }
                    break;
                }

                case SalvageRollerAction.ObsidianConvert:
                case SalvageRollerAction.ObsidianReroll:

                    var expected = action == SalvageRollerAction.ObsidianConvert
                        ? EquipmentModManager.ModAction.Convert
                        : EquipmentModManager.ModAction.Reroll;

                    for (var seq = 1; seq <= rolls; seq++)
                    {
                        var copy = NewScratchCopy(snapshot);
                        var before = DuoGemSnapshot.Capture(copy);

                        var refusal = EquipmentModManager.TargetStateRefusal(copy, false, out var modAction, out var count);

                        if (refusal != null || modAction != expected)
                            throw RollFailed(target, action, seq, refusal ?? $"the item now resolves to {modAction}, not {expected}");

                        if (EquipmentModManager.ApplyConvertOrReroll(copy, modAction, count, (property, value) => EquipmentModManager.WriteRatingToItem(copy, property, value)) == null)
                            throw RollFailed(target, action, seq, $"ApplyConvertOrReroll refused {modAction}");

                        candidates.Add(Candidate(seq, before, copy, ObsidianSummary(copy)));
                        observeRolledCopy?.Invoke(seq, copy);
                    }
                    break;

                case SalvageRollerAction.SerpentineReroll:

                    for (var seq = 1; seq <= rolls; seq++)
                    {
                        var copy = NewScratchCopy(snapshot);
                        var before = DuoGemSnapshot.Capture(copy);

                        var (plan, refusal) = SerpentinePlanner(copy);

                        if (refusal != RerollRefusal.None || plan == null || plan.Swaps.Count == 0)
                            throw RollFailed(target, action, seq, $"the reroll plan was refused ({refusal})");

                        var applied = SpellRerollManager.ApplyPlan(copy, plan, $"SalvageRollerEngine.Roll({target.Name}, 0x{target.Guid.Full:X8}, roll {seq})");

                        var summary = new SalvageRollerSummary { Lines = applied.Select(SerpentineSwapDescriber).ToList() };

                        candidates.Add(Candidate(seq, before, copy, summary));
                        observeRolledCopy?.Invoke(seq, copy);
                    }
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(action), action, "not a Salvage Roller action");
            }

            return candidates;
        }

        // ---------------- rolls ----------------

        private static void RollWeapon(WorldObject copy, WeaponModManager.WeaponModAction action, WorldObject target, int seq)
        {
            var weaponClass = WeaponClassifier.Classify(copy);

            if (!WeaponModManager.CanApply(copy, weaponClass, action))
                throw RollFailed(target, action == WeaponModManager.WeaponModAction.Reroll ? SalvageRollerAction.TourmalineReroll : SalvageRollerAction.AmethystSwap, seq, "CanApply refused");

            var workmanship = copy.Workmanship ?? 0.0f;

            var lines = action == WeaponModManager.WeaponModAction.Reroll
                ? WeaponModManager.ApplyReroll(copy, weaponClass, workmanship)
                : WeaponModManager.ApplySwap(copy, weaponClass, workmanship);

            if (lines == null)
                throw RollFailed(target, action == WeaponModManager.WeaponModAction.Reroll ? SalvageRollerAction.TourmalineReroll : SalvageRollerAction.AmethystSwap, seq, "the roller produced no result");
        }

        private static InvalidOperationException RollFailed(WorldObject target, SalvageRollerAction action, int seq, string reason)
        {
            return new InvalidOperationException($"Salvage Roller: {action} roll {seq} on {target.Name} (0x{target.Guid.Full:X8}) could not be made: {reason}");
        }

        /// <summary>
        /// A throwaway copy of the snapshotted item: guid ObjectGuid.Invalid, so it takes nothing from the dynamic guid
        /// pool and has nothing to release. Built from the snapshot alone (no weenie base), which carries every
        /// property table the rollers read and write.
        /// </summary>
        internal static WorldObject NewScratchCopy(DuoGemSnapshot snapshot)
        {
            var copy = WorldObjectFactory.CreateWorldObject(snapshot.BuildRestoredBiota(ObjectGuid.Invalid.Full));

            if (copy == null)
                throw new InvalidOperationException($"Salvage Roller: could not build a scratch copy of wcid {snapshot.Item.WeenieClassId}");

            return copy;
        }

        // ---------------- summaries ----------------

        /// <summary>
        /// A weapon candidate: its quality tier, its summed special intensity, and one line per special it now holds,
        /// in the bag path's own wording (WeaponModDisplay.Describe). The lines describe the resulting STATE rather
        /// than the last roll's Lost/Gained lines, because an Amethyst candidate k is k swaps away from the original.
        /// </summary>
        internal static SalvageRollerSummary WeaponSummary(WorldObject weapon)
        {
            var total = WeaponQualityTiers.TotalIntensity(weapon);
            var scale = WeaponModValue.MagnitudeScale();

            var lines = WeaponModTinkerSet.ReadSpecials(weapon)
                .OrderBy(s => s.Definition, WeaponModParagonDisplay.ByName)
                .Select(s => WeaponModDisplay.Describe(s.Definition, s.Magnitude, scale, weapon))
                .ToList();

            if (lines.Count == 0)
                lines.Add("No special modifier took hold.");

            return new SalvageRollerSummary { Tier = (int)TierFor(weapon, total), Total = total, Lines = lines };
        }

        /// <summary>
        /// The candidate's quality tier, by the same rule as WeaponQualityTiers.Evaluate: a shield never earns one (no
        /// aura), so its candidates rank and read by their summed intensity alone.
        /// </summary>
        private static WeaponQualityTier TierFor(WorldObject weapon, int total) =>
            WeaponClassifier.Classify(weapon) == WeaponClass.Shield ? WeaponQualityTier.None : WeaponQualityTiers.FromTotal(total);

        /// <summary>
        /// A Green Jade candidate: Tier is the highest single-type count and Total the second-highest count (so the
        /// display sort ranks a piece by its leading type, then by how strong its runner-up is), with one line
        /// "Attack A / Defense D / Utility U".
        /// </summary>
        internal static SalvageRollerSummary GreenJadeSummary(AspectCounts counts)
        {
            var sorted = new[] { counts.Attack, counts.Defense, counts.Utility }.OrderByDescending(n => n).ToArray();

            return new SalvageRollerSummary
            {
                Tier = sorted[0],
                Total = sorted[1],
                Lines = new List<string> { $"Attack {counts.Attack} / Defense {counts.Defense} / Utility {counts.Utility}" },
            };
        }

        /// <summary>An Obsidian candidate: tier 0, the summed mod fractions, one line per mod (EquipmentModDisplay.Describe).</summary>
        internal static SalvageRollerSummary ObsidianSummary(WorldObject item)
        {
            var mods = EquipmentModDisplay.GetModsByName(item);

            return new SalvageRollerSummary
            {
                Tier = 0,
                Total = mods.Sum(m => m.Potency),
                Lines = mods.Select(m => $"{EquipmentModDisplay.Describe(m.Definition, m.Potency)} ({EquipmentModDisplay.AbilityRequirement(m.Definition)})").ToList(),
            };
        }

        private static SalvageRollerCandidate Candidate(int seq, DuoGemSnapshot before, WorldObject rolledCopy, SalvageRollerSummary summary)
        {
            return new SalvageRollerCandidate
            {
                Seq = seq,
                Diff = ComputeDiff(before, DuoGemSnapshot.Capture(rolledCopy)),
                Summary = summary,
            };
        }

        // ---------------- diff ----------------

        /// <summary>
        /// Every property that differs between two snapshots of the same item: its new value, or null when it was
        /// removed. Keys are written in ascending order so the same result always serializes the same way.
        ///
        /// Throws when a change lands in any table DuoGemSnapshot captures that the diff does not carry: weenie class
        /// or type, the Scribe instance id, anim parts, palettes, texture maps, enchantments. No roller writes any of
        /// them today; a roller that started to would otherwise have that part of its result silently dropped at take.
        ///
        /// Tables DuoGemSnapshot does NOT capture are invisible here, so a change to one of them is neither carried
        /// nor refused: every instance id other than Scribe, positions, the two placement ints (CurrentWieldedLocation,
        /// PlacementPosition), and the creature/content tables (attributes, skills, body parts, emotes, create list,
        /// generators, book pages). None of the four rollers writes any of those.
        /// </summary>
        internal static SalvageRollerDiff ComputeDiff(DuoGemSnapshot before, DuoGemSnapshot after)
        {
            var b = before.Item;
            var a = after.Item;

            if (b.WeenieClassId != a.WeenieClassId || b.WeenieType != a.WeenieType)
                throw new InvalidOperationException("Salvage Roller: a roll changed the item's weenie class or type");

            if (before.Scribe != after.Scribe)
                throw UncarriedChange("instance ids");

            if (!SameJson(b.AnimParts, a.AnimParts)) throw UncarriedChange("anim parts");
            if (!SameJson(b.Palettes, a.Palettes)) throw UncarriedChange("palettes");
            if (!SameJson(b.TextureMaps, a.TextureMaps)) throw UncarriedChange("texture maps");
            if (!SameJson(b.Enchantments, a.Enchantments)) throw UncarriedChange("enchantments");

            var diff = new SalvageRollerDiff
            {
                Ints = DiffTable(b.Ints, a.Ints),
                Int64s = DiffTable(b.Int64s, a.Int64s),
                Floats = DiffTable(b.Floats, a.Floats),
                DataIds = DiffTable(b.DataIds, a.DataIds),
                Bools = DiffTable(b.Bools, a.Bools),
            };

            foreach (var key in b.Strings.Keys.Union(a.Strings.Keys).OrderBy(k => k))
            {
                b.Strings.TryGetValue(key, out var was);
                var has = a.Strings.TryGetValue(key, out var now);

                if (!has)
                    diff.Strings[key] = null;
                else if (!b.Strings.ContainsKey(key) || was != now)
                    diff.Strings[key] = now;
            }

            foreach (var spell in b.SpellBook.Keys.Union(a.SpellBook.Keys).OrderBy(k => k))
            {
                var had = b.SpellBook.TryGetValue(spell, out var wasProbability);
                var has = a.SpellBook.TryGetValue(spell, out var probability);

                if (!has)
                    diff.Spells.Remove.Add(spell);
                else if (!had || wasProbability != probability)
                    diff.Spells.Add[spell] = probability;
            }

            return diff;
        }

        private static Dictionary<int, T?> DiffTable<T>(Dictionary<int, T> before, Dictionary<int, T> after) where T : struct
        {
            var diff = new Dictionary<int, T?>();

            foreach (var key in before.Keys.Union(after.Keys).OrderBy(k => k))
            {
                var had = before.TryGetValue(key, out var was);
                var has = after.TryGetValue(key, out var now);

                if (!has)
                    diff[key] = null;
                else if (!had || !EqualityComparer<T>.Default.Equals(was, now))
                    diff[key] = now;
            }

            return diff;
        }

        private static bool SameJson<T>(T left, T right) =>
            JsonSerializer.Serialize(left, SalvageRollerPayload.Options) == JsonSerializer.Serialize(right, SalvageRollerPayload.Options);

        private static InvalidOperationException UncarriedChange(string table) =>
            new InvalidOperationException($"Salvage Roller: a roll changed the item's {table}, which DuoGemSnapshot captures but a candidate diff does not carry");

        // ---------------- apply ----------------

        /// <summary>
        /// Writes a candidate's diff onto an item: every set or removed property, every removed spell, and every added
        /// spell at its recorded probability. The item's object only - no save, no network, no tier aura. The take step
        /// (DESIGN.md 5.4 step 7) runs this on the real escrowed item and saves it once.
        /// </summary>
        public static void ApplyDiff(WorldObject item, SalvageRollerDiff diff)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            if (diff == null) throw new ArgumentNullException(nameof(diff));

            foreach (var kvp in diff.Ints)
            {
                if (kvp.Value.HasValue) item.SetProperty((PropertyInt)kvp.Key, kvp.Value.Value);
                else item.RemoveProperty((PropertyInt)kvp.Key);
            }

            foreach (var kvp in diff.Int64s)
            {
                if (kvp.Value.HasValue) item.SetProperty((PropertyInt64)kvp.Key, kvp.Value.Value);
                else item.RemoveProperty((PropertyInt64)kvp.Key);
            }

            foreach (var kvp in diff.Floats)
            {
                if (kvp.Value.HasValue) item.SetProperty((PropertyFloat)kvp.Key, kvp.Value.Value);
                else item.RemoveProperty((PropertyFloat)kvp.Key);
            }

            foreach (var kvp in diff.Strings)
            {
                if (kvp.Value != null) item.SetProperty((PropertyString)kvp.Key, kvp.Value);
                else item.RemoveProperty((PropertyString)kvp.Key);
            }

            foreach (var kvp in diff.DataIds)
            {
                if (kvp.Value.HasValue) item.SetProperty((PropertyDataId)kvp.Key, kvp.Value.Value);
                else item.RemoveProperty((PropertyDataId)kvp.Key);
            }

            foreach (var kvp in diff.Bools)
            {
                if (kvp.Value.HasValue) item.SetProperty((PropertyBool)kvp.Key, kvp.Value.Value);
                else item.RemoveProperty((PropertyBool)kvp.Key);
            }

            foreach (var spell in diff.Spells.Remove)
                item.Biota.TryRemoveKnownSpell(spell, item.BiotaDatabaseLock);

            foreach (var kvp in diff.Spells.Add)
            {
                // GetOrAddKnownSpell leaves an existing entry's probability alone, so a changed probability is a
                // remove and re-add
                item.Biota.TryRemoveKnownSpell(kvp.Key, item.BiotaDatabaseLock);
                item.Biota.GetOrAddKnownSpell(kvp.Key, item.BiotaDatabaseLock, out _, kvp.Value);
            }
        }
    }
}
