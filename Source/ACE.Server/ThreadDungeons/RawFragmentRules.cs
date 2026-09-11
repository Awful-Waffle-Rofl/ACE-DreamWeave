using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using ACE.Server.ThreadDungeons.Defs;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// Why a Raw Fragment refused a dose. NotAValidTarget is the DISPATCH-level outcome (the target is
    /// neither a mapped component nor the press) and is never returned by CanLoad.
    /// </summary>
    public enum LoadRefusal
    {
        None,
        NotAValidTarget,
        NotAComponent,
        OverLimit,
        NotEnough,
    }

    /// <summary>
    /// Every tunable the press needs, read by the CALLER (FragmentPressStation) and handed in. RawFragmentRules
    /// never touches PropertyManager: it is a pure core, and PropertyManager reads throw in unit tests.
    /// </summary>
    public sealed class PressLimits
    {
        /// <summary>
        /// dynamic_dungeons_press_max_mods, default 4: the TAPER BUDGET - how many monster/boss modifiers one
        /// gem may carry.
        ///
        /// This was a single flat cap across every modifier until press v2 (owner ruling, 2026-09-07). It had
        /// to become per-category because four taper slots plus a herb plus a powder can name six distinct
        /// things, and a flat 4 would have made the herb and the powder lose races against the tapers -
        /// silently, and differently depending on the slot order.
        /// </summary>
        public readonly int MaxMonsterMods;

        /// <summary>The herb slot's budget: how many BONUS modifiers one gem may carry. 1.</summary>
        public readonly int MaxBonusMods;

        /// <summary>The powder slot's budget: how many SALVAGE AFFINITY modifiers one gem may carry. 1.</summary>
        public readonly int MaxSalvageMods;

        /// <summary>DungeonGemSpec.MaxLocks, 2.</summary>
        public readonly int MaxLocks;

        /// <summary>RawFragmentRules.MaxEntries, 6.</summary>
        public readonly int MaxEntries;

        /// <summary>DungeonGemSpec.MaxLevel, 375.</summary>
        public readonly int MaxLevel;

        /// <summary>
        /// dynamic_dungeons_press_aim_chance, default 0.5: how often an op that NAMES a modifier actually
        /// lands that modifier (owner ruling, 2026-09-07 - "a component the player selects should land its
        /// associated effect only 50% of the time"). The other half of the time the op resolves to a
        /// uniformly random pick from the same BUDGET CLASS as the named modifier, excluding the named
        /// modifier itself.
        ///
        /// ALWAYS IN [0, 1], because the constructor sanitizes it: 1.0 is "always lands", which is the
        /// pre-ruling behaviour and is what most of RawFragmentRulesTests presses with so that its
        /// per-component assertions still mean what they say.
        /// </summary>
        public readonly double AimChance;

        // WildShare and FractureRate, with their dynamic_dungeons_press_wild_share and
        // dynamic_dungeons_press_fracture_rate tunables, went with the instability mechanic (owner ruling,
        // 2026-09-07). A press now applies the loaded components' own ops and nothing else.

        /// <summary>
        /// The two v2 budgets and the aim chance are OPTIONAL-DEFAULTED so every pre-v2 four-argument call
        /// site keeps compiling and gets the SHIPPED values rather than zero, on the same contract
        /// DungeonPopulationLimits uses for its own late-added dials.
        ///
        /// <paramref name="aimChance"/> is the one argument this type sanitizes, and it does so HERE rather
        /// than at the point of use: NaN reads as the default and anything else is clamped to [0, 1], so
        /// <see cref="AimChance"/> is always a usable probability and RawFragmentRules never has to guess
        /// what a NaN comparison meant. FragmentPressStation.BuildLimits clamps the live tunable as well;
        /// the two are not redundant, because a test constructs this type directly and never goes near
        /// PropertyManager.
        /// </summary>
        public PressLimits(int maxMonsterMods, int maxLocks, int maxEntries, int maxLevel,
            int maxBonusMods = DefaultMaxBonusMods, int maxSalvageMods = DefaultMaxSalvageMods,
            double aimChance = DefaultAimChance)
        {
            MaxMonsterMods = maxMonsterMods;
            MaxLocks = maxLocks;
            MaxEntries = maxEntries;
            MaxLevel = maxLevel;
            MaxBonusMods = maxBonusMods;
            MaxSalvageMods = maxSalvageMods;
            AimChance = double.IsNaN(aimChance) ? DefaultAimChance : Math.Clamp(aimChance, 0.0, 1.0);
        }

        /// <summary>
        /// The herb and powder budgets are compiled constants rather than tunables, because each is the
        /// arithmetic consequence of there being exactly ONE herb slot and ONE powder slot. A tunable that
        /// disagreed with the slot count would not be a tuning choice, it would be a bug with a knob on it.
        /// </summary>
        public const int DefaultMaxBonusMods = 1;

        /// <summary>See <see cref="DefaultMaxBonusMods"/>.</summary>
        public const int DefaultMaxSalvageMods = 1;

        /// <summary>
        /// The shipped aim chance, and the registered default of dynamic_dungeons_press_aim_chance. Unlike
        /// the two budgets above this one IS a tunable, because it is a taste dial rather than an arithmetic
        /// consequence of the board: how often aiming works is a question about how the game should feel,
        /// and the slot count does not answer it.
        /// </summary>
        public const double DefaultAimChance = 0.5;
    }

    /// <summary>
    /// The two pieces of press state that do not both live on the spec: the spec itself, and the entry count
    /// (which lives on the item as Structure, not in the spec string).
    /// </summary>
    public sealed class PressState
    {
        public DungeonGemSpec Spec { get; }
        public int Entries { get; }

        public PressState(DungeonGemSpec spec, int entries)
        {
            Spec = spec;
            Entries = entries;
        }
    }

    /// <summary>
    /// One dose of one component, as it happened. Before/After are the compact "mods=&lt;id:mag,...&gt;"
    /// rendering the press logs (D5) - a diffable line, not the whole spec.
    ///
    /// The three counter pairs exist because Before/After cannot see them: Render encodes only the modifier
    /// list, so lock_one, level_up and add_entry are all INVISIBLE to a Before/After diff, and a narrator
    /// reading only those two strings has to guess whether the op did anything. Each of those three ops has a
    /// real silent no-op branch (a lock that is already held or names an absent modifier, a level already at
    /// MaxLevel, an entry count already at MaxEntries), so the guess is wrong on live content - two Hazel
    /// Talismans in one press being the shipped case.
    ///
    /// These pairs became LOAD-BEARING, not merely helpful, when instability was removed (owner ruling,
    /// 2026-09-07): Render used to carry an "|inst=&lt;n&gt;" term, so a lock/level/entry dose still moved the
    /// rendered string through its component's instability cost. It no longer does, so for those three ops
    /// Before now EQUALS After on both the success and the failure path and the counters are the only
    /// evidence left that the op landed. The pairs are captured around the Apply call ALONE, not across the
    /// whole dose, so a later step cannot masquerade as a failed lock.
    /// </summary>
    public sealed class DoseLogEntry
    {
        public uint Wcid;
        public string Name;
        public string Op;
        public string Before;
        public string After;

        /// <summary>Which of the nine press v2 slots this dose filled - one of RawFragmentRules' type constants.</summary>
        public string Slot;

        /// <summary>
        /// True when the slot was EMPTY and the component was drawn at random rather than loaded by the
        /// player. Post-press feedback names it, because a player who loaded one taper and got a nine-slot
        /// gem back has to be able to tell which eight the board filled in for them.
        /// </summary>
        public bool Drawn;

        public int LocksBefore;
        public int LocksAfter;
        public int LevelBefore;
        public int LevelAfter;
        public int EntriesBefore;
        public int EntriesAfter;
    }

    /// <summary>
    /// The Raw Fragment attunement core: what a fragment will accept, what order the doses resolve in, what
    /// each op does, and what comes out the other side. Pure - spec + attunement defs + modifier defs + limits
    /// + Random in, spec out. No PropertyManager read, no engine reference, no I/O.
    ///
    /// THE SLOT BOARD (press v2, owner ruling 2026-09-07). A press is no longer a replay of whatever the
    /// player loaded: it is a fixed board of NINE slots, and EVERY slot resolves, always. A slot the player
    /// filled resolves that component; a slot left empty resolves a randomly drawn component of that slot's
    /// type. So a bare fragment presses into a complete, fully random gem and a fully loaded fragment presses
    /// into a completely chosen one. See <see cref="SlotOrder"/> for the board and the order.
    ///
    /// RNG DRAW ORDER (binding; the seeded tests depend on it). Slots resolve in <see cref="SlotOrder"/>.
    /// Per slot, in this exact order:
    ///   1. rng.NextDouble()          - ONLY when the slot is EMPTY: which component of that type is drawn,
    ///                                  over the type's components ordered by wcid ASCENDING (never
    ///                                  dictionary order), weighted by each component's derived draw weight.
    ///                                  See <see cref="WeightedPick{T}"/>.
    ///   2. rng.Next(totalWeight)     - the weighted op draw over the resolved component's own op list
    ///   3. the AIM roll               - ONLY for an op that NAMES a modifier that resolves (add_or_raise,
    ///                                  and sharpen with a modifier argument): one rng.NextDouble(), then
    ///                                  on a MISS one rng.NextDouble() over the named modifier's budget
    ///                                  siblings ordered ordinally by id, weighted by DrawWeight. See
    ///                                  <see cref="Aim"/>.
    ///   4. zero or more op draws     - see each op in Apply; a banded magnitude is ONE rng.NextDouble(),
    ///                                  and a weighted modifier draw (add_random, reroll_one) is ONE
    ///                                  rng.NextDouble()
    /// Then once, after the last slot: rng.Next(1, int.MaxValue) for the new seed.
    /// Candidate lists drawn from the modifier DICTIONARY are sorted ordinally by id first, so the draw does
    /// not depend on dictionary enumeration order; candidate lists drawn from the spec's own modifier list
    /// keep that list's order, which is already deterministic.
    ///
    /// This order CHANGED TWICE on 2026-09-07. First with the removal of the instability mechanic (the
    /// leading wild-blend roll and the trailing fracture roll went), then again with the slot board (nine
    /// slots always resolve where before only loaded components did, and every magnitude is now a banded
    /// roll rather than a fixed minimum or a fixed step). A given seed does not reproduce either earlier
    /// build's result, and every seeded expectation in RawFragmentRulesTests was RE-DERIVED against this
    /// order rather than loosened.
    ///
    /// It changed a THIRD time with the aim roll (step 3 above). The roll is taken UNCONDITIONALLY on every
    /// aimable op, whatever <see cref="PressLimits.AimChance"/> happens to be, for the same reason
    /// RollBanded takes its own draw unconditionally: a draw taken only on some values of a dial would make
    /// the seed's meaning depend on the dial's setting in a way no reader could predict from the order
    /// above. The MISS draw is genuinely conditional, because there is nothing to pick when the aim lands.
    ///
    /// It changed a FOURTH time on 2026-09-08 with per-modifier DRAW WEIGHTS (owner ruling: "rarity weights
    /// everywhere, but make Hollow the only reduced one for now"). The four random ADD paths - the empty-slot
    /// component draw, add_random, the aim miss and reroll_one - now pick through
    /// <see cref="WeightedPick{T}"/>, which spends ONE rng.NextDouble() where each used to spend one
    /// rng.Next(count). The NUMBER and POSITION of draws is therefore unchanged, and everything above still
    /// reads true; the VALUES a given seed produces do change, because NextDouble is a different draw from
    /// Next(n). Nothing a player LOADED is weighted - the weight only ever scales a draw the board made on
    /// its own.
    /// </summary>
    public static class RawFragmentRules
    {
        /// <summary>One raise step is (max - min) / RaiseSteps, rounded to 2 dp and clamped to the modifier's range.</summary>
        public const int RaiseSteps = 4;

        /// <summary>
        /// Most entries a pressed gem may carry. Raised from 5 to 6 with press v2: the Mana Scarab, the top
        /// rung of the scarab ladder, sets 6.
        /// </summary>
        public const int MaxEntries = 6;

        public const string RefuseNotAComponent = "The fragment does not take that.";
        public const string RefuseOverLimit = "It will take no more of that.";
        public const string RefuseNotAValidTarget = "The fragment does not answer that.";

        /// <summary>{0} is the component's dose. Parameterised, so it is a format rather than a flat const.</summary>
        public const string RefuseNotEnoughFormat = "There is not enough there. It takes {0} at a time.";

        // ---- the slot board (press v2) ---------------------------------------------------------------

        public const string ScarabType = "scarab";
        public const string HerbType = "herb";
        public const string PowderType = "powder";
        public const string TaperType = "taper";
        public const string PotionType = "potion";
        public const string TalismanType = "talisman";

        /// <summary>
        /// The six slot types and how many slots each owns (owner ruling, 2026-09-07: "1 scarab, 1 herb,
        /// 1 powder, 1 potion, 1 talisman, 4 tapers"). Also the per-type LOAD limit every component of that
        /// type must declare in attunement.json - lint rule 22 cross-checks the two, so the file and the
        /// board cannot disagree.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, int> SlotCounts = new Dictionary<string, int>
        {
            [ScarabType] = 1,
            [HerbType] = 1,
            [PowderType] = 1,
            [TaperType] = 4,
            [PotionType] = 1,
            [TalismanType] = 1,
        };

        /// <summary>
        /// THE DRAW ORDER, and it is a contract rather than an accident: a gem is only reproducible from a
        /// seed if the slots resolve in a fixed sequence. Nine entries, one per slot.
        ///
        /// The order is chosen so that each slot sees a board its own op can actually work on:
        ///   scarab first, because it sets the run's level and entry count and touches no modifier at all;
        ///   herb then powder, because each fills its own private budget (bonus, salvage) and so cannot lose
        ///     a race against the tapers however the tapers roll;
        ///   the four tapers, which fill the monster/boss budget and are the only slot type that can stack
        ///     onto itself;
        ///   potion second-to-last, because it re-rolls the WEAKEST contributions and needs the board
        ///     finished before "weakest" means anything;
        ///   talisman last, because it sharpens one modifier already present and should get the final word.
        ///
        /// Do not reorder this without accepting that every gem a seed would have produced changes.
        /// </summary>
        public static readonly IReadOnlyList<string> SlotOrder = new[]
        {
            ScarabType,
            HerbType,
            PowderType,
            TaperType, TaperType, TaperType, TaperType,
            PotionType,
            TalismanType,
        };

        /// <summary>
        /// Check order is BINDING - it is what makes the refusals informative. NotAComponent comes first so an
        /// unmapped item clicked at a full fragment says "does not take that" rather than the misleading
        /// "will take no more of that", then NotEnough, then OverLimit.
        ///
        /// The TooUnstable refusal (and the calming-component exemption that skipped it, ruling P2-R24) went
        /// with the instability mechanic on 2026-09-07: nothing accumulates any more, so the only ceiling on
        /// loading is the per-type limit.
        /// </summary>
        public static LoadRefusal CanLoad(DungeonGemSpec spec, AttunementDef defs, uint componentWcid, int stackSize,
            out ComponentDef component, out string refusal)
        {
            component = null;
            refusal = null;

            if (spec == null || defs == null || !defs.TryGet(componentWcid, out component))
            {
                component = null;
                refusal = RefuseNotAComponent;
                return LoadRefusal.NotAComponent;
            }

            if (stackSize < component.Dose)
            {
                refusal = string.Format(CultureInfo.InvariantCulture, RefuseNotEnoughFormat, component.Dose);
                return LoadRefusal.NotEnough;
            }

            if (DosesOfType(spec, defs, component.Type) >= component.Limit)
            {
                refusal = RefuseOverLimit;
                return LoadRefusal.OverLimit;
            }

            return LoadRefusal.None;
        }

        /// <summary>
        /// Appends one dose. An existing entry for the same wcid is merged IN PLACE - the first-load position
        /// is what ResolveOrder and the Pressed: line both read, so it must survive a second dose.
        /// </summary>
        public static DungeonGemSpec Load(DungeonGemSpec spec, ComponentDef component)
        {
            if (spec == null) throw new ArgumentNullException(nameof(spec));
            if (component == null) throw new ArgumentNullException(nameof(component));

            var load = spec.Load.ToList();
            for (var i = 0; i < load.Count; i++)
            {
                if (load[i].Wcid != component.Wcid) continue;
                load[i] = (load[i].Wcid, load[i].Doses + 1);
                return spec.WithLoad(load);
            }

            load.Add((component.Wcid, 1));
            return spec.WithLoad(load);
        }

        /// <summary>Doses of one component TYPE already loaded. The limit is per type, not per wcid.</summary>
        public static int DosesOfType(DungeonGemSpec spec, AttunementDef defs, string type)
        {
            if (spec == null || defs == null || string.IsNullOrEmpty(type)) return 0;

            var total = 0;
            foreach (var entry in spec.Load)
                if (defs.TryGet(entry.Wcid, out var component) && component.Type == type)
                    total += entry.Doses;
            return total;
        }

        /// <summary>One resolved slot: which slot type it is, what component filled it, and how.</summary>
        public sealed class SlotFill
        {
            public string Type { get; }
            public ComponentDef Component { get; }

            /// <summary>False when the slot was empty and the component was drawn at random.</summary>
            public bool Loaded { get; }

            public SlotFill(string type, ComponentDef component, bool loaded)
            {
                Type = type;
                Component = component;
                Loaded = loaded;
            }
        }

        /// <summary>
        /// Walks <see cref="SlotOrder"/> and decides what fills every slot. THE ONE PLACE the empty-slot draw
        /// happens, so the draw order documented on this class has a single implementation.
        ///
        /// A slot is filled from the player's load when that type still has an unspent dose, in LOAD order
        /// (so a fragment carrying Red, Red, Blue puts Red in taper slots 1 and 2 and Blue in slot 3);
        /// otherwise a component of that type is drawn at random over the type's components ordered by wcid
        /// ASCENDING. Ordering by wcid rather than by dictionary enumeration is what keeps a seed
        /// reproducible across runs and across .NET versions.
        ///
        /// A slot whose type has NO components at all in <paramref name="defs"/> is SKIPPED - it produces no
        /// entry, and takes no draw. That is unreachable with the shipped file, which carries all six types,
        /// and it is what lets a test fixture exercise one slot type without the other five drawing into its
        /// result.
        ///
        /// A loaded dose whose type is not one of the six is DROPPED, silently as far as the press is
        /// concerned: the store's lint already refused that component with a diagnostic, so the only way one
        /// reaches here is on a spec written by an older build.
        ///
        /// THE EMPTY-SLOT DRAW IS WEIGHTED by each candidate component's derived draw weight (see
        /// <see cref="ComponentDrawWeight"/>), so a component that can produce a reduced-weight modifier is
        /// drawn proportionally less often. It still costs exactly one Random call, so the documented draw
        /// order is unchanged in shape. Passing a null or empty <paramref name="modifiers"/> makes every
        /// weight 1.0, which is the uniform draw this replaced.
        /// </summary>
        public static IReadOnlyList<SlotFill> ResolveSlots(IReadOnlyList<(uint Wcid, int Doses)> load, AttunementDef defs,
            IReadOnlyDictionary<string, ModifierDef> modifiers, Random rng)
        {
            var result = new List<SlotFill>();
            if (defs == null || rng == null) return result;

            // Per type, the loaded doses in load order. Flattened once so a slot's "is there a dose left"
            // test is a queue pop rather than a re-scan.
            var pending = new Dictionary<string, Queue<ComponentDef>>();

            foreach (var entry in load ?? new List<(uint, int)>())
            {
                if (!defs.TryGet(entry.Wcid, out var component) || component?.Type == null) continue;
                if (!SlotCounts.ContainsKey(component.Type)) continue;

                if (!pending.TryGetValue(component.Type, out var queue))
                    pending[component.Type] = queue = new Queue<ComponentDef>();

                for (var i = 0; i < entry.Doses; i++)
                    queue.Enqueue(component);
            }

            // The random pool per type, built once and ordered by wcid so the draw is stable.
            var pools = new Dictionary<string, List<ComponentDef>>();

            foreach (var type in SlotCounts.Keys)
                pools[type] = defs.Components.Values
                    .Where(c => c != null && c.Type == type)
                    .OrderBy(c => c.Wcid)
                    .ToList();

            foreach (var type in SlotOrder)
            {
                if (pending.TryGetValue(type, out var queue) && queue.Count > 0)
                {
                    result.Add(new SlotFill(type, queue.Dequeue(), true));
                    continue;
                }

                var pool = pools[type];
                if (pool.Count == 0) continue;

                result.Add(new SlotFill(type, WeightedPick(pool, c => ComponentDrawWeight(c, modifiers), rng), false));
            }

            return result;
        }

        /// <summary>
        /// Resolves the whole nine-slot board and returns the pressed spec. An empty load still presses, and
        /// under press v2 it presses into a COMPLETE gem rather than a bare one: every slot resolves, so a
        /// bare fragment comes out fully random and a fully loaded one comes out fully chosen.
        ///
        /// Per slot, in order: resolve what fills it (see <see cref="ResolveSlots"/>), draw the component's
        /// own op, apply it under the per-category budgets, then drop any lock whose modifier is gone.
        ///
        /// NOTE ON ENTRIES. <paramref name="state"/>'s entry count is an INPUT that the scarab slot then
        /// overwrites, because the scarab is what decides how hard the run is and how many entries it pays
        /// for. Since the scarab slot always resolves, a pressed gem's entry count comes from its scarab and
        /// not from the fragment it was pressed from. It survives untouched only when the attunement file
        /// carries no scarab at all.
        /// </summary>
        public static PressState Resolve(PressState state, AttunementDef defs,
            IReadOnlyDictionary<string, ModifierDef> modifiers, PressLimits limits, Random rng,
            out IReadOnlyList<DoseLogEntry> log)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (state.Spec == null) throw new ArgumentNullException(nameof(state));
            if (defs == null) throw new ArgumentNullException(nameof(defs));
            if (modifiers == null) throw new ArgumentNullException(nameof(modifiers));
            if (limits == null) throw new ArgumentNullException(nameof(limits));
            if (rng == null) throw new ArgumentNullException(nameof(rng));

            var spec = state.Spec;
            var work = new Work
            {
                Mods = spec.Modifiers.ToList(),
                Locks = spec.Locks.ToList(),
                Level = spec.Level,
                Entries = state.Entries,
            };

            var doses = new List<DoseLogEntry>();

            foreach (var slot in ResolveSlots(spec.Load, defs, modifiers, rng))
            {
                var component = slot.Component;
                var before = Render(work.Mods);

                // 1. Pick, from the component's own op list. There is nothing else to draw from any more.
                var op = DrawOp(component.Ops, rng);

                // 2. Apply, under the caps. The three counters Render cannot encode are sampled either side
                // of this call and nowhere else: that window is exactly what the op did, so a later prune
                // cannot be read back as the op having failed. Bookkeeping only - no rng draw here, and
                // nothing below reads these values.
                var locksBeforeOp = work.Locks.Count;
                var levelBeforeOp = work.Level;
                var entriesBeforeOp = work.Entries;

                if (op != null)
                    Apply(op, work, modifiers, limits, rng);

                var locksAfterOp = work.Locks.Count;
                var levelAfterOp = work.Level;
                var entriesAfterOp = work.Entries;

                // 3. A lock naming a modifier that is no longer present would make Serialize emit a string
                // TryParse refuses, so prune after every dose rather than once at the end.
                work.Locks.RemoveAll(id => IndexOf(work.Mods, id) < 0);

                doses.Add(new DoseLogEntry
                {
                    Wcid = component.Wcid,
                    Name = component.Name,
                    Slot = slot.Type,
                    Drawn = !slot.Loaded,
                    Op = op == null ? AttunementOps.Nothing : op.Op,
                    Before = before,
                    After = Render(work.Mods),
                    LocksBefore = locksBeforeOp,
                    LocksAfter = locksAfterOp,
                    LevelBefore = levelBeforeOp,
                    LevelAfter = levelAfterOp,
                    EntriesBefore = entriesBeforeOp,
                    EntriesAfter = entriesAfterOp,
                });
            }

            log = doses;

            // WithModifiers first: it prunes the OLD locks against the new modifier list, so WithLocks can
            // never be handed a lock the constructor would throw on.
            var pressed = spec
                .WithModifiers(work.Mods)
                .WithLocks(work.Locks)
                .WithLevel(work.Level)
                .WithPresses(spec.Presses + 1)
                .WithLoad(new List<(uint Wcid, int Doses)>())
                .WithSeed(rng.Next(1, int.MaxValue));

            return new PressState(pressed, work.Entries);
        }

        // StabilityWord (the appraisal panel's "Stability:" line) and SteadinessWord (the press's one-word
        // report) both took an instability and are gone with it (owner ruling, 2026-09-07). Their two callers
        // are now ThreadDungeonGemHandler.ComposeLongDesc, which no longer emits a Stability: line at all,
        // and FragmentPressStation.ComposePressedMessage, which keeps the word the old low band produced.

        /// <summary>The Pressed: line - "Red Taper x2, Blue Taper, Gold Scarab", or "nothing".</summary>
        public static string ComposePressedLine(IReadOnlyList<(uint Wcid, int Doses)> load, AttunementDef defs)
            => ComposePressedLine(load, defs == null ? (Func<uint, string>)null : defs.DisplayName);

        /// <summary>
        /// The same line, resolved through a name function rather than a whole AttunementDef, so the pure
        /// half of ThreadDungeonGemHandler.ComposeLongDesc (D4c) can render it without taking a store
        /// dependency. One implementation, so the two callers cannot format counts differently.
        /// </summary>
        public static string ComposePressedLine(IReadOnlyList<(uint Wcid, int Doses)> load, Func<uint, string> nameOf)
        {
            if (load == null || load.Count == 0) return "nothing";

            var parts = new List<string>();
            foreach (var entry in load)
            {
                var name = nameOf?.Invoke(entry.Wcid);
                if (string.IsNullOrEmpty(name)) name = $"wcid {entry.Wcid}";
                parts.Add(entry.Doses > 1 ? name + " x" + entry.Doses.ToString(CultureInfo.InvariantCulture) : name);
            }

            return parts.Count == 0 ? "nothing" : string.Join(", ", parts);
        }

        // ---- internals -------------------------------------------------------------------------------

        private sealed class Work
        {
            public List<(string Id, double Magnitude)> Mods;
            public List<string> Locks;
            public int Level;
            public int Entries;
        }

        /// <summary>
        /// The diffable per-dose rendering. The trailing "|inst=&lt;n&gt;" term went with the instability
        /// mechanic (owner ruling, 2026-09-07); the "mods=" prefix and the pipe-separated shape are kept so
        /// DungeonGemNarrator.ParseRender - a read-only mirror of this format - stays a one-branch parser.
        /// </summary>
        private static string Render(IReadOnlyList<(string Id, double Magnitude)> mods)
            => "mods=" + string.Join(",", mods.Select(m => m.Id + ":" + m.Magnitude.ToString("0.##", CultureInfo.InvariantCulture)));

        private static int IndexOf(IReadOnlyList<(string Id, double Magnitude)> mods, string id)
        {
            for (var i = 0; i < mods.Count; i++)
                if (mods[i].Id == id) return i;
            return -1;
        }

        private static List<int> UnlockedIndices(Work work)
        {
            var result = new List<int>();
            for (var i = 0; i < work.Mods.Count; i++)
                if (!work.Locks.Contains(work.Mods[i].Id)) result.Add(i);
            return result;
        }

        /// <summary>Weighted draw over an op list. rng.Next(totalWeight), walked in list order.</summary>
        private static OpDef DrawOp(IReadOnlyList<OpDef> ops, Random rng)
        {
            if (ops == null || ops.Count == 0) return null;

            var total = 0;
            foreach (var op in ops) total += Math.Max(0, op.Weight);
            if (total <= 0) return ops[0];

            var roll = rng.Next(total);
            foreach (var op in ops)
            {
                roll -= Math.Max(0, op.Weight);
                if (roll < 0) return op;
            }
            return ops[ops.Count - 1];
        }

        // ---- draw weights (owner ruling, 2026-09-08) --------------------------------------------------
        //
        // "Rarity weights everywhere, but make Hollow the only reduced one for now." A modifier row carries
        // a drawWeight (default 1.0, see ModifierDef.DrawWeight) that scales how often a RANDOM selection
        // lands on it. It is deliberately NOT the "rarity" string - that one is /dd give's knob and three
        // other rows share hollow's "rare" tag - so the catalog can be tuned a row at a time from play data.
        //
        // Four random paths can ADD a modifier to a gem and all four route through WeightedPick below:
        //   A. the empty-slot component draw in ResolveSlots (weighted by the COMPONENT weight derived here)
        //   B. add_random's scope pool (the Turquoise Taper)
        //   C. the aim MISS fallback pool in Aim
        //   D. reroll_one's same-rarity replacement pool
        // Nothing else is weighted, and in particular nothing the player LOADED is: a White Taper still aims
        // at hollow at the ordinary PressLimits.AimChance, and its hit still lands.

        /// <summary>
        /// A COMPONENT's draw weight, derived from the modifiers its ops NAME: the MINIMUM DrawWeight over
        /// every op whose <see cref="OpDef.Modifier"/> resolves in <paramref name="modifiers"/>, and 1.0 when
        /// its ops name none (add_random, an unnamed sharpen, reroll_weakest, set_difficulty, level_up,
        /// add_entry, lock_one, remove_one).
        ///
        /// MINIMUM, not product, and that is the whole design choice. A component that can produce hollow
        /// should be exactly as rare as hollow - no rarer - and a hypothetical two-op component naming two
        /// reduced modifiers must not compound into a weight so near zero that it is effectively unreachable.
        /// The minimum says "as rare as the rarest thing it can produce", which is the sentence a content
        /// author can hold in their head.
        ///
        /// An op naming a modifier the store cannot resolve is treated as naming NONE rather than as weight
        /// 0: lint rule 13 already drops such a component with a diagnostic, so the only way one reaches here
        /// is a spec written by an older build, and silently making it unreachable would be a worse answer
        /// than treating it as ordinary.
        ///
        /// Public and pure so a test can pin the derivation directly rather than inferring it from a draw.
        /// </summary>
        public static double ComponentDrawWeight(ComponentDef component, IReadOnlyDictionary<string, ModifierDef> modifiers)
        {
            if (component?.Ops == null || modifiers == null) return 1.0;

            var weight = 1.0;
            var named = false;

            foreach (var op in component.Ops)
            {
                if (op == null || string.IsNullOrEmpty(op.Modifier)) continue;
                if (!modifiers.TryGetValue(op.Modifier, out var def) || def == null) continue;

                if (!named || def.DrawWeight < weight) weight = def.DrawWeight;
                named = true;
            }

            return named ? weight : 1.0;
        }

        /// <summary>
        /// THE ONE weighted pick, shared by all four random ADD paths so a tuning value cannot mean one thing
        /// in one of them and another somewhere else.
        ///
        /// COSTS EXACTLY ONE Random CALL, whichever branch it takes. That is a hard requirement rather than an
        /// efficiency: the class header documents the draw ORDER as binding and the seeded tests depend on it,
        /// so a weighted pick that took two draws (or took one only sometimes) would change the number and
        /// position of every draw after it. The VALUES a given seed produces do change, because
        /// rng.NextDouble() is a different draw from rng.Next(count) - that is expected and the header records
        /// it.
        ///
        /// DEGENERATE POOLS FALL BACK TO UNIFORM. If every candidate weighs 0, or the total is not a finite
        /// positive number, the pick is the plain rng.Next(count) it replaced. A slot must never go unfilled
        /// because of a tuning value: an author who zeroes a whole pool gets a uniform draw, not a hole in the
        /// board. (A pool where only SOME weigh 0 is not degenerate - those candidates are simply never
        /// drawn, which is what a 0 is for.)
        /// </summary>
        private static T WeightedPick<T>(IReadOnlyList<T> pool, Func<T, double> weightOf, Random rng)
        {
            if (pool == null || pool.Count == 0) return default;

            var total = 0.0;
            for (var i = 0; i < pool.Count; i++)
            {
                var w = weightOf(pool[i]);
                if (double.IsNaN(w) || w <= 0.0) continue;
                total += w;
            }

            if (!double.IsFinite(total) || total <= 0.0)
                return pool[rng.Next(pool.Count)];

            var roll = rng.NextDouble() * total;
            var running = 0.0;

            for (var i = 0; i < pool.Count; i++)
            {
                var w = weightOf(pool[i]);
                if (double.IsNaN(w) || w <= 0.0) continue;

                running += w;
                if (roll < running) return pool[i];
            }

            // Floating-point round-off only: the running sum can finish a hair under the total the roll was
            // scaled by. Fall to the LAST candidate that could legally have been drawn rather than to the last
            // element, so a trailing zero-weight row can never be returned by the rounding path.
            for (var i = pool.Count - 1; i >= 0; i--)
            {
                var w = weightOf(pool[i]);
                if (!double.IsNaN(w) && w > 0.0) return pool[i];
            }

            return pool[pool.Count - 1];
        }

        // ---- press v2: bands and per-category budgets -------------------------------------------------

        /// <summary>
        /// The three budgets a modifier can occupy. Deliberately DERIVED from
        /// <see cref="DungeonModifierCategories"/> rather than switching on the row a second time: that
        /// classifier is the single source of truth for what a modifier IS, and the only thing this adds is
        /// that a salvage affinity - which the classifier quite correctly calls a bonus to the player - has
        /// its own slot on the board and so its own budget.
        /// </summary>
        private enum Budget
        {
            Monster,
            Bonus,
            Salvage,
        }

        private static Budget BudgetOf(ModifierDef def)
        {
            if (def != null && def.MonsterEffectKind == DungeonRewardMath.SalvageAffinity)
                return Budget.Salvage;

            return DungeonModifierCategories.Of(def) == DungeonModifierCategory.Bonus ? Budget.Bonus : Budget.Monster;
        }

        private static int LimitFor(Budget budget, PressLimits limits)
        {
            switch (budget)
            {
                case Budget.Salvage: return limits.MaxSalvageMods;
                case Budget.Bonus: return limits.MaxBonusMods;
                default: return limits.MaxMonsterMods;
            }
        }

        private static ModifierDef DefOf(IReadOnlyDictionary<string, ModifierDef> modifiers, string id)
            => id != null && modifiers.TryGetValue(id, out var def) ? def : null;

        /// <summary>Indices of the present modifiers that sit in <paramref name="budget"/>.</summary>
        private static List<int> IndicesIn(Work work, IReadOnlyDictionary<string, ModifierDef> modifiers, Budget budget)
        {
            var result = new List<int>();
            for (var i = 0; i < work.Mods.Count; i++)
                if (BudgetOf(DefOf(modifiers, work.Mods[i].Id)) == budget) result.Add(i);
            return result;
        }

        /// <summary>Whether one more modifier of <paramref name="def"/>'s budget would still fit.</summary>
        private static bool HasBudgetFor(Work work, IReadOnlyDictionary<string, ModifierDef> modifiers, ModifierDef def, PressLimits limits)
        {
            var budget = BudgetOf(def);
            return IndicesIn(work, modifiers, budget).Count < LimitFor(budget, limits);
        }

        /// <summary>
        /// THE BAND PRIMITIVE. Rolls a magnitude for <paramref name="def"/> inside the op's band:
        ///
        ///     rolled = min + (max - min) * uniform(bandMin, bandMax)
        ///
        /// Bands are FRACTIONS of the modifier's own declared range, never absolute magnitudes, so retuning
        /// the modifier retunes every component pointing at it. See OpDef.BandMin.
        ///
        /// EXACTLY ONE rng.NextDouble() is taken, unconditionally - before the edges are validated and
        /// whatever the modifier's range turns out to be. That is not tidiness, it is the reproducibility
        /// contract: a draw taken only on some data shapes would make the seed's meaning depend on the
        /// content file, and a band typo would silently shift every LATER slot's roll as well as its own.
        /// </summary>
        private static double RollBanded(ModifierDef def, OpDef op, Random rng)
        {
            var roll = rng.NextDouble();

            var low = Math.Clamp(op?.BandMin ?? 0.0, 0.0, 1.0);
            var high = Math.Clamp(op?.BandMax ?? 1.0, 0.0, 1.0);

            if (double.IsNaN(low)) low = 0.0;
            if (double.IsNaN(high)) high = 1.0;
            if (high < low) { var swap = low; low = high; high = swap; }

            var fraction = low + (high - low) * roll;
            var value = def.MinMagnitude + (def.MaxMagnitude - def.MinMagnitude) * fraction;

            return Math.Clamp(Math.Round(value, 2), def.MinMagnitude, def.MaxMagnitude);
        }

        /// <summary>
        /// What a SECOND component naming an already-present modifier does: it stacks its own contribution
        /// on top rather than adding a second entry (owner ruling, 2026-09-07 - "four Red Tapers gives one
        /// extremely savage dungeon"). The contribution is the rolled value measured from the modifier's
        /// FLOOR, so a full-band component adds 0..(max - min) and a narrow low band adds almost nothing -
        /// which is exactly the difference between a safe component and a gamble.
        /// </summary>
        private static double Stack(double current, double rolled, ModifierDef def)
            => Math.Clamp(Math.Round(current + (rolled - def.MinMagnitude), 2), def.MinMagnitude, def.MaxMagnitude);

        /// <summary>
        /// Whether a modifier is reachable by an op carrying <paramref name="scope"/>. Resolved through the
        /// shared category classifier, never a private id list, so a new modifiers.json row lands in the
        /// right pool the moment it is added. An unknown or omitted scope means "any".
        /// </summary>
        private static bool InScope(ModifierDef def, string scope)
        {
            switch (scope)
            {
                case AttunementScopes.Monster: return DungeonModifierCategories.Of(def) == DungeonModifierCategory.Monster;
                case AttunementScopes.Boss: return DungeonModifierCategories.Of(def) == DungeonModifierCategory.Boss;
                case AttunementScopes.Difficulty: return BudgetOf(def) == Budget.Monster;

                // The wildcard powders. Read off the SAME budget the powder slot is capped by, so the pool
                // and the cap can never disagree and a new affinity row in modifiers.json is drawable
                // immediately - there is no list of materials anywhere in this file to keep in step.
                case AttunementScopes.Salvage: return BudgetOf(def) == Budget.Salvage;

                default: return true;
            }
        }

        /// <summary>Indices of the present modifiers an op with this scope may sharpen.</summary>
        private static List<int> ScopedIndices(Work work, IReadOnlyDictionary<string, ModifierDef> modifiers, string scope)
        {
            var result = new List<int>();
            for (var i = 0; i < work.Mods.Count; i++)
                if (InScope(DefOf(modifiers, work.Mods[i].Id), scope)) result.Add(i);
            return result;
        }

        /// <summary>
        /// Raises the modifier at <paramref name="index"/> by the op's band, read as the size of the bump
        /// measured from the modifier's floor. Locks are deliberately NOT consulted: raising is never
        /// destructive, which is the same reason the v1 add_or_raise raised a locked modifier.
        /// </summary>
        private static void SharpenAt(Work work, int index, IReadOnlyDictionary<string, ModifierDef> modifiers, OpDef op, Random rng)
        {
            var mod = work.Mods[index];
            var def = DefOf(modifiers, mod.Id);
            if (def == null) return;

            work.Mods[index] = (mod.Id, Stack(mod.Magnitude, RollBanded(def, op, rng), def));
        }

        /// <summary>
        /// The shared body of add_or_raise and add_random. Three outcomes: ADD the modifier, STACK onto the
        /// copy already there, or - when its budget is full and it is not already present - sharpen something
        /// in the same budget, so the slot still did something.
        ///
        /// WHICH MODIFIER RECEIVES THE ROLL IS RESOLVED FIRST, AND THE ROLL IS TAKEN ONCE, AT THE END. That
        /// order is the whole point of this method's shape and it must not be inverted back. An earlier
        /// version rolled up front "so all three outcomes cost the same one draw", which was exactly wrong:
        /// the budget-full path discarded that value and let SharpenAt roll again, so a full-budget dose cost
        /// TWO NextDouble calls where the class header and RollBanded's own doc both promise one per op.
        /// Measured on a four-dose full-budget re-press, that was 8 draws where 4 were documented (caught in
        /// review, 2026-09-07). It broke no gameplay - the effect landed correctly and a seed still
        /// reproduced its gem - but a falsified draw-count contract is how the NEXT thing built on "N ops = N
        /// draws" goes quietly wrong. Pinned by
        /// RawFragmentRulesTests.A_full_budget_dose_costs_exactly_one_banded_roll.
        ///
        /// The roll's RANGE is the def that actually receives it, not the def the op named: sharpening a
        /// sibling bands against the sibling's own range, because a band is a fraction of the range it is
        /// applied to.
        /// </summary>
        private static void AddOrStack(ModifierDef def, Work work, IReadOnlyDictionary<string, ModifierDef> modifiers,
            PressLimits limits, OpDef op, Random rng)
        {
            var index = IndexOf(work.Mods, def.Id);

            if (index < 0 && !HasBudgetFor(work, modifiers, def, limits))
            {
                // The budget is full and this modifier is not on the board. Spend the dose on the modifiers
                // that ARE there, in the same budget, so a fifth taper is a stronger dungeon rather than a
                // wasted slot. Picking the sibling HERE rather than in a separate branch below is what keeps
                // the roll to a single call site.
                var siblings = IndicesIn(work, modifiers, BudgetOf(def));

                // Unreachable with any budget of 1 or more: a full budget implies at least one occupant. It
                // is the only path that takes no banded roll at all, and it takes none because there is
                // nothing left to roll a magnitude FOR.
                if (siblings.Count == 0) return;

                index = siblings[rng.Next(siblings.Count)];
            }

            if (index < 0)
            {
                work.Mods.Add((def.Id, RollBanded(def, op, rng)));
                return;
            }

            var target = DefOf(modifiers, work.Mods[index].Id) ?? def;

            work.Mods[index] = (work.Mods[index].Id, Stack(work.Mods[index].Magnitude, RollBanded(target, op, rng), target));
        }

        /// <summary>
        /// THE AIM ROLL (owner ruling, 2026-09-07): "a component the player selects should land its
        /// associated effect only 50% of the time; the other 50% draws from the pool of everything else in
        /// the same category."
        ///
        /// Applies to the two ops that NAME a target - add_or_raise and a targeted sharpen - and to nothing
        /// else, because nothing else has an aim to miss: add_random is already a wildcard, reroll_weakest
        /// picks by measurement, and set_difficulty / level_up / add_entry / lock_one / remove_one touch no
        /// named modifier at all.
        ///
        /// THE FALLBACK POOL IS DERIVED FROM THE NAMED MODIFIER'S OWN BUDGET, never from a list: a powder
        /// aimed at an affinity misses into the other affinities (Budget.Salvage), a taper aimed at stalwart
        /// misses into the other monster/boss modifiers (Budget.Monster), and a herb misses into the other
        /// bonuses. A ninth affinity row in modifiers.json is therefore in the pool the moment it is added,
        /// exactly as it already is for the wildcard powders (see InScope).
        ///
        /// TWO THINGS THIS DELIBERATELY DOES NOT DO. It does not consider what is already ON the board -
        /// whatever it returns goes through the ordinary AddOrStack/SharpenAt path, so an id already present
        /// STACKS rather than appearing twice, and the miss cannot be used to smuggle a duplicate past the
        /// de-duplication. And it does not filter the pool by budget headroom: a miss onto a full budget is
        /// answered by AddOrStack's existing sibling-sharpen consolation, which is the same answer a hit
        /// onto a full budget already gets.
        ///
        /// The NextDouble is taken before anything is validated - see the class header's note on why the
        /// roll is unconditional. An EMPTY pool (a budget class with exactly one modifier in it) falls back
        /// to the named modifier rather than doing nothing, so a miss is never a wasted slot.
        /// </summary>
        private static ModifierDef Aim(ModifierDef named, IReadOnlyDictionary<string, ModifierDef> modifiers,
            PressLimits limits, Random rng)
        {
            var roll = rng.NextDouble();

            if (roll < limits.AimChance) return named;

            var budget = BudgetOf(named);

            var pool = modifiers.Values
                .Where(m => m != null && m.Id != named.Id && BudgetOf(m) == budget)
                .OrderBy(m => m.Id, StringComparer.Ordinal).ToList();

            // The MISS is weighted by each candidate's own DrawWeight, so a reduced modifier is no likelier to
            // arrive by accident than it is to be drawn deliberately. The named modifier's HIT above is not
            // weighted and never will be: a player who loaded a component chose that outcome.
            return pool.Count == 0 ? named : WeightedPick(pool, m => m.DrawWeight, rng);
        }

        /// <summary>Sharpens a random present modifier inside the op's scope; a no-op when none is in scope.</summary>
        private static void SharpenRandomInScope(Work work, IReadOnlyDictionary<string, ModifierDef> modifiers, OpDef op, Random rng)
        {
            var candidates = ScopedIndices(work, modifiers, op.Scope);
            if (candidates.Count == 0) return;

            SharpenAt(work, candidates[rng.Next(candidates.Count)], modifiers, op, rng);
        }

        /// <summary>
        /// Where a magnitude sits inside its own modifier's declared range, in [0, 1] - the only comparable
        /// measure of "how strong is this one" across modifiers whose ranges have nothing in common.
        ///
        /// A zero-width range reads as 0 here, which would make a fixed-magnitude modifier sort as the
        /// weakest thing on any board it sits on. That is exactly why reroll_weakest EXCLUDES such a
        /// modifier before it gets here rather than relying on this function to rank it sensibly - see the
        /// note on that op.
        /// </summary>
        private static double FractionOfRange(double magnitude, ModifierDef def)
        {
            var span = def.MaxMagnitude - def.MinMagnitude;
            if (span <= 0 || double.IsNaN(magnitude)) return 0.0;
            return Math.Clamp((magnitude - def.MinMagnitude) / span, 0.0, 1.0);
        }

        private static void Raise(Work work, int index, IReadOnlyDictionary<string, ModifierDef> modifiers)
        {
            var mod = work.Mods[index];
            if (!modifiers.TryGetValue(mod.Id, out var def)) return;

            var step = Math.Round((def.MaxMagnitude - def.MinMagnitude) / RaiseSteps, 2);
            var magnitude = Math.Clamp(Math.Round(mod.Magnitude + step, 2), def.MinMagnitude, def.MaxMagnitude);
            work.Mods[index] = (mod.Id, magnitude);
        }

        private static void RaiseRandomUnlocked(Work work, IReadOnlyDictionary<string, ModifierDef> modifiers, Random rng)
        {
            var unlocked = UnlockedIndices(work);
            if (unlocked.Count == 0) return;
            Raise(work, unlocked[rng.Next(unlocked.Count)], modifiers);
        }

        /// <summary>
        /// The op table (PHASE-2-IMPLEMENTATION-PLAN.md task D2). Tier is never touched by any op.
        /// A component op may still omit its modifier argument for add_or_raise/lock_one (nothing shipped
        /// does - lint rule 12 requires it - so those branches are defensive), in which case the op picks its
        /// own target.
        /// </summary>
        private static void Apply(OpDef op, Work work, IReadOnlyDictionary<string, ModifierDef> modifiers, PressLimits limits, Random rng)
        {
            switch (op.Op)
            {
                case AttunementOps.AddOrRaise:
                {
                    var def = DefOf(modifiers, op.Modifier);

                    // Lint rule 12 requires the argument and rule 13 requires it to resolve, so this is
                    // defensive against a spec written by an older build rather than a shipped shape.
                    if (def == null) return;

                    // The op NAMES a modifier, so it is aimable: half the time (PressLimits.AimChance) it
                    // lands somewhere else in the same budget. Resolved BEFORE AddOrStack so the whole
                    // add/stack/consolation path below is unchanged and still sees exactly one target.
                    AddOrStack(Aim(def, modifiers, limits, rng), work, modifiers, limits, op, rng);
                    return;
                }

                case AttunementOps.AddRandom:
                {
                    // The Turquoise Taper's "aim roughly": the op names no modifier, so one is drawn from its
                    // scope pool. Ordinally ordered by id so the draw does not depend on dictionary
                    // enumeration order.
                    var candidates = modifiers.Values
                        .Where(m => InScope(m, op.Scope) && IndexOf(work.Mods, m.Id) < 0)
                        .OrderBy(m => m.Id, StringComparer.Ordinal).ToList();

                    if (candidates.Count == 0)
                    {
                        // Every modifier in scope is already on the board. Sharpen one instead, so the slot
                        // is never wasted - the same consolation AddOrStack pays when a budget is full.
                        SharpenRandomInScope(work, modifiers, op, rng);
                        return;
                    }

                    AddOrStack(WeightedPick(candidates, m => m.DrawWeight, rng), work, modifiers, limits, op, rng);
                    return;
                }

                case AttunementOps.Sharpen:
                {
                    // The talisman slot. Its own wood's modifier when that modifier is on the board;
                    // otherwise a random present one inside its scope.
                    //
                    // A NAMED sharpen is aimable, so the wood's own modifier lands only AimChance of the
                    // time and the rest of the time the talisman sharpens a budget sibling instead. An
                    // UNNAMED sharpen (Oak, Hemlock) takes no aim roll and no draw: its target was never a
                    // choice, and the scoped fallback below is already a random pick.
                    //
                    // The named modifier is aimed only when the store can RESOLVE it - an unresolvable id
                    // has no budget class, so there is no pool to miss into and no honest roll to take.
                    var target = op.Modifier;

                    if (!string.IsNullOrEmpty(target) && DefOf(modifiers, target) is ModifierDef named)
                        target = Aim(named, modifiers, limits, rng).Id;

                    var index = string.IsNullOrEmpty(target) ? -1 : IndexOf(work.Mods, target);

                    if (index >= 0)
                    {
                        SharpenAt(work, index, modifiers, op, rng);
                        return;
                    }

                    SharpenRandomInScope(work, modifiers, op, rng);
                    return;
                }

                case AttunementOps.RerollWeakest:
                {
                    // The potion slot. WHAT it re-rolls is deterministic - the N weakest unlocked
                    // contributions - and what it re-rolls them INTO is not. "Weakest" is measured as a
                    // fraction of each modifier's own range, because raw magnitudes are not comparable
                    // across modifiers (savage runs 10..40, hardy runs 1.3..2.0).
                    //
                    // A FIXED-MAGNITUDE modifier is excluded outright, and this is not a tidiness filter.
                    // Its range has zero width, so it reads as fraction 0 and would sort as the weakest
                    // thing on the board every time - while re-rolling it cannot change it by definition.
                    // Without this guard the shipped "hollow" row would silently eat a Brimstone's whole
                    // effect on any gem carrying it. The exclusion also keeps the DRAW count honest: a
                    // modifier that cannot move takes no roll.
                    var ranked = UnlockedIndices(work)
                        .Where(i => DefOf(modifiers, work.Mods[i].Id) is ModifierDef d && d.MaxMagnitude > d.MinMagnitude)
                        .Select(i => (Index: i, Fraction: FractionOfRange(work.Mods[i].Magnitude, DefOf(modifiers, work.Mods[i].Id))))
                        // ThenBy the index so ties resolve by board position rather than by sort instability.
                        .OrderBy(x => x.Fraction).ThenBy(x => x.Index)
                        .Select(x => x.Index)
                        .Take(Math.Max(1, op.Amount))
                        .ToList();

                    foreach (var index in ranked)
                    {
                        var mod = work.Mods[index];
                        var def = DefOf(modifiers, mod.Id);
                        if (def == null) continue;

                        // The id is UNCHANGED: a potion refines what is there rather than replacing it, so a
                        // player who chose their modifiers never loses one to the refinement slot.
                        work.Mods[index] = (mod.Id, RollBanded(def, op, rng));
                    }

                    return;
                }

                case AttunementOps.SetDifficulty:
                {
                    // The scarab slot: one op, both dials. amount is a LEVEL offset (signed); entries SETS
                    // the count, and 0 means "leave it alone" so an op omitting the field is inert on it.
                    work.Level = Math.Clamp(work.Level + op.Amount, 1, limits.MaxLevel);

                    if (op.Entries > 0)
                        work.Entries = Math.Clamp(op.Entries, 1, limits.MaxEntries);

                    return;
                }

                case AttunementOps.RaiseRandom:
                    if (work.Mods.Count > 0)
                        Raise(work, rng.Next(work.Mods.Count), modifiers);
                    return;

                case AttunementOps.RaiseAll:
                    for (var i = 0; i < work.Mods.Count; i++)
                        Raise(work, i, modifiers);
                    return;

                case AttunementOps.SetMax:
                {
                    if (string.IsNullOrEmpty(op.Modifier) || !modifiers.TryGetValue(op.Modifier, out var def)) return;
                    var magnitude = Math.Round(def.MaxMagnitude, 2);
                    var index = IndexOf(work.Mods, op.Modifier);
                    if (index >= 0) work.Mods[index] = (op.Modifier, magnitude);
                    else if (HasBudgetFor(work, modifiers, def, limits)) work.Mods.Add((op.Modifier, magnitude));
                    return;
                }

                case AttunementOps.RemoveOne:
                {
                    // Nothing removable is a genuine no-op now: the consolation instability rebate it used to
                    // pay (PotionCalmOnNothingToRemove) went with the mechanic. The narrator still reports it
                    // honestly as "found nothing to strip" rather than silently.
                    var unlocked = UnlockedIndices(work);
                    if (unlocked.Count == 0)
                        return;
                    work.Mods.RemoveAt(unlocked[rng.Next(unlocked.Count)]);
                    return;
                }

                case AttunementOps.RerollOne:
                {
                    var unlocked = UnlockedIndices(work);
                    if (unlocked.Count == 0) return;

                    var index = unlocked[rng.Next(unlocked.Count)];
                    var old = work.Mods[index];
                    if (!modifiers.TryGetValue(old.Id, out var oldDef)) return;

                    var candidates = modifiers.Values
                        .Where(m => m.Rarity == oldDef.Rarity && m.Id != old.Id && IndexOf(work.Mods, m.Id) < 0)
                        .OrderBy(m => m.Id, StringComparer.Ordinal).ToList();
                    if (candidates.Count == 0) return;

                    var pick = WeightedPick(candidates, m => m.DrawWeight, rng);
                    var span = oldDef.MaxMagnitude - oldDef.MinMagnitude;
                    var fraction = span > 0 ? Math.Clamp((old.Magnitude - oldDef.MinMagnitude) / span, 0.0, 1.0) : 0.0;
                    var magnitude = Math.Clamp(Math.Round(pick.MinMagnitude + fraction * (pick.MaxMagnitude - pick.MinMagnitude), 2),
                        pick.MinMagnitude, pick.MaxMagnitude);
                    work.Mods[index] = (pick.Id, magnitude);
                    return;
                }

                case AttunementOps.LockOne:
                {
                    if (work.Locks.Count >= limits.MaxLocks) return;

                    var id = op.Modifier;
                    if (string.IsNullOrEmpty(id))
                    {
                        var unlocked = UnlockedIndices(work);
                        if (unlocked.Count == 0) return;
                        id = work.Mods[unlocked[rng.Next(unlocked.Count)]].Id;
                    }

                    if (IndexOf(work.Mods, id) < 0 || work.Locks.Contains(id)) return;
                    work.Locks.Add(id);
                    return;
                }

                case AttunementOps.LevelUp:
                    work.Level = Math.Clamp(work.Level + op.Amount * 10, 1, limits.MaxLevel);
                    return;

                case AttunementOps.AddEntry:
                    work.Entries = Math.Min(work.Entries + 1, limits.MaxEntries);
                    return;

                default:
                    // AttunementOps.Nothing, and anything the loader let through that this build does not know.
                    return;
            }
        }
    }
}