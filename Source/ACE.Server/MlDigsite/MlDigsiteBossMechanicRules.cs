using System;
using System.Collections.Generic;
using System.Globalization;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.MlDigsite
{
    /// <summary>
    /// Which mechanic a Boss Rush set slot runs. One member per module under MlDigsite/Mechanics, plus
    /// <see cref="None"/> for an empty secondary slot.
    ///
    /// <see cref="Immune50"/> is NOT a sixth module: it is <see cref="ImmunePhases"/> with its threshold list
    /// narrowed to 0.50 alone, which is exactly what the tester's set 5 asks for ("secondary Immune phase at
    /// 50% health only"). Keeping it an authored token rather than a second tunable string means the set table
    /// stays the one place a designer looks.
    /// </summary>
    public enum MlDigsiteMechanic
    {
        None,
        Volatile,
        Drums,
        ImmunePhases,
        Interrupt,
        SafeZones,
        Immune50,
    }

    /// <summary>
    /// Which slot of a set a mechanic is running in. The slot does not change the code path - it changes the
    /// cadence multiplier (ml_digsite_bossrush_secondary_cadence_mult), so a secondary mechanic is present but
    /// less frequent than the same mechanic would be as a main.
    /// </summary>
    public enum MlDigsiteMechanicSlot
    {
        Main,
        Secondary,
    }

    /// <summary>
    /// One authored row of ml_digsite_bossrush_sets: a set id, its two mechanics, and the add scaling this set
    /// gives the ONE shared add weenie (owner ruling 2026-09-20: one reused add weenie whose health and speed
    /// scale per set, never a distinct weenie per set).
    /// </summary>
    public readonly struct MlDigsiteMechanicSet
    {
        public MlDigsiteMechanicSet(int setId, MlDigsiteMechanic main, MlDigsiteMechanic secondary,
            double addHealth, double addSpeed)
        {
            SetId = setId;
            Main = main;
            Secondary = secondary;
            AddHealth = addHealth;
            AddSpeed = addSpeed;
        }

        public int SetId { get; }

        public MlDigsiteMechanic Main { get; }

        public MlDigsiteMechanic Secondary { get; }

        /// <summary>Health multiplier applied to an add spawned by THIS set. 1.0 means the weenie's own health.</summary>
        public double AddHealth { get; }

        /// <summary>RunRate multiplier applied to an add spawned by THIS set. 1.0 means the weenie's own speed.</summary>
        public double AddSpeed { get; }

        public override string ToString() => $"set={SetId} main={Main} secondary={Secondary}";
    }

    /// <summary>
    /// One mechanic's authored numbers: the key=value tokens of its own tunable string, read through total
    /// accessors that fall back to the caller's documented default rather than to zero.
    ///
    /// Total on purpose, the same contract MonsterEffectSpec states for the same reason: these are read on the
    /// digsite tick, and a bad value in a live tunable must cost the designer a wrong number, never the tick.
    /// A value a module actually depends on is range-checked by the module at read time, not here.
    /// </summary>
    public sealed class MlDigsiteMechanicArgs
    {
        public static readonly MlDigsiteMechanicArgs Empty = new MlDigsiteMechanicArgs(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        private readonly IReadOnlyDictionary<string, string> args;

        public MlDigsiteMechanicArgs(IReadOnlyDictionary<string, string> args)
        {
            this.args = args ?? throw new ArgumentNullException(nameof(args));
        }

        public bool Has(string key) => key != null && args.ContainsKey(key);

        public string GetString(string key, string defaultValue = null)
            => key != null && args.TryGetValue(key, out var value) ? value : defaultValue;

        public double GetDouble(string key, double defaultValue)
        {
            var raw = GetString(key);

            return raw != null
                && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                && double.IsFinite(value)
                    ? value
                    : defaultValue;
        }

        public int GetInt(string key, int defaultValue)
        {
            var raw = GetString(key);

            return raw != null && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : defaultValue;
        }

        /// <summary>
        /// A comma-separated list of fractions, e.g. "thresholds=0.85,0.50,0.25". Non-numeric and
        /// out-of-(0,1] entries are dropped; a list that resolves to nothing falls back to
        /// <paramref name="defaultValue"/> rather than to an empty list, so a typo cannot silently disable a
        /// mechanic's whole trigger set.
        /// </summary>
        public IReadOnlyList<double> GetFractions(string key, IReadOnlyList<double> defaultValue)
        {
            var raw = GetString(key);

            if (raw == null)
                return defaultValue;

            var parsed = new List<double>();

            foreach (var token in raw.Split(','))
            {
                var text = token.Trim();

                if (text.Length == 0)
                    continue;

                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    continue;

                if (!double.IsFinite(value) || value <= 0.0 || value > 1.0)
                    continue;

                parsed.Add(value);
            }

            return parsed.Count == 0 ? defaultValue : parsed;
        }
    }

    /// <summary>
    /// The pure half of the Boss Rush mechanic driver: the authoring grammar, the shipped defaults, and the
    /// set roll. Nothing here reads PropertyManager (MlDigsiteTunables is this system's single read point and
    /// PropertyManager throws under the test harness), touches a landblock, or holds state - which is what
    /// makes every number and every decision in section 5 of the design testable without a live world.
    ///
    /// THE GRAMMAR IS DELIBERATELY THE SHAPE OF PropertyString 9015 (MonsterEffectParser): records separated
    /// by ';', tokens within a record separated by whitespace, every token key=value. An author fluent in a
    /// monster's MonsterCombatEffects string reads a set table with no new mental model.
    ///
    /// COMPOSITION AND NUMBERS ARE SEPARATE TUNABLES ON PURPOSE. ml_digsite_bossrush_sets carries only which
    /// mechanics a set runs; each mechanic's numbers live in its own string. One tunable holding thirty
    /// numbers is unreadable, and one typo in it would fall the WHOLE table back to its default.
    /// </summary>
    public static class MlDigsiteBossMechanicRules
    {
        // ---- shipped defaults ----------------------------------------------------------------------------
        //
        // Every number below is either DERIVED from an existing comparable in this repo or a FRESH proposal
        // to be tuned live; each is called out in the doc comment of the tunable that carries it
        // (MlDigsiteTunables) and in its PropertyManager description.

        /// <summary>
        /// The five sets the tester specified, in their words:
        ///   1 volatile adds; 2 drums + volatile; 3 immune phases + drums; 4 interrupt + volatile;
        ///   5 safe zones + immune at 50% only.
        /// addhp/addspeed are per-set because the one shared add weenie has to read as "fast and weak" when it
        /// is a volatile bomb (sets 1, 2, 4) and as "a thing you must actually kill" when it is an immune-phase
        /// gate (sets 3, 5).
        /// </summary>
        public const string DefaultSets =
            "set=1 main=volatile secondary=none addhp=0.25 addspeed=1.30; " +
            "set=2 main=drums secondary=volatile addhp=0.25 addspeed=1.30; " +
            "set=3 main=immunephases secondary=drums addhp=0.40 addspeed=1.10; " +
            "set=4 main=interrupt secondary=volatile addhp=0.25 addspeed=1.30; " +
            "set=5 main=safezones secondary=immune50 addhp=0.40 addspeed=1.10";

        public const string DefaultVolatile = "every=20 count=3 maxlive=6 radius=6 damage=400 fuse=2";

        /// <summary>
        /// Round 17 added burndamage/burnseconds/burninterval: each 3-beat VOLLEY mark becomes burning ground
        /// that deals burndamage Fire damage every burninterval seconds, for burnseconds, to each player inside
        /// volleyradius of it (DrumCadenceMechanic.StartBurn). burndamage=0 or burnseconds=0 turns the burn off.
        /// </summary>
        public const string DefaultDrums =
            "every=25 beatgap=1 resolve=2 damage=500 ringradius=8 wallwidth=6 volleyradius=4 volleytargets=3 burndamage=150 burnseconds=8 burninterval=1";

        public const string DefaultImmune = "thresholds=0.85,0.50,0.25 adds=4 timeout=45";

        /// <summary>
        /// Round 17 owner ruling: window 20 s (was 12) and distance 15 m (was 30) - the drum expired unanswered
        /// on stage with the tester unable to reach a drum placed 30 m out in 12 s. pulse= is new: the seconds
        /// between repeats of the drum's visibility flare while the window is open.
        /// </summary>
        public const string DefaultInterrupt = "every=45 window=20 distance=15 damage=0.35 pulse=2";

        /// <summary>
        /// damage=1.0 is a FRACTION of the player's max health (see <see cref="ResolveDamage"/>), which is
        /// what makes the tester's sentence literally true: the raw hit is the size of the whole health bar,
        /// so it would kill, and the first forgiveness= misses survive it only because
        /// <see cref="NonLethalDamage"/> caps them by construction. A flat number here would have made the
        /// forgiveness a decoration on a hit that a geared player walks off anyway.
        /// </summary>
        public const string DefaultSafeZones =
            "every=15 telegraph=4 zones=3 saferadius=5 ring=18 forgiveness=2 decay=60 damage=1.0";

        /// <summary>
        /// The immune-phase thresholds used when the authored string names none. 0.85/0.50/0.25 are the
        /// tester's own numbers; 0.50 additionally matches the threshold the shipped
        /// ml_digsite_bossrush_mechanic ward already fires at.
        /// </summary>
        public static readonly IReadOnlyList<double> DefaultImmuneThresholds = new[] { 0.85, 0.50, 0.25 };

        /// <summary>The single threshold an <see cref="MlDigsiteMechanic.Immune50"/> slot uses, whatever the string says.</summary>
        public static readonly IReadOnlyList<double> Immune50Thresholds = new[] { 0.50 };

        /// <summary>
        /// The authored monster-effect record that carries the immune phase's damage FILTER
        /// (MonsterEffects/Effects/ImmuneEffect). No args: an immune record on a creature nothing ever
        /// toggles is permanently inert, which is exactly what it should be.
        /// </summary>
        public const string ImmuneRecord = "immune";

        /// <summary>
        /// How much longer a drum wall is than it is wide. ONE constant, read both by the telegraph that
        /// DRAWS the wall and by the resolve that decides who is inside it, because the two drifting apart is
        /// precisely the defect this replaced: the drawn span was width * 4 while the hit region had no
        /// length bound at all, so the wall hit players three times further out than any marker stood.
        /// </summary>
        public const double WallLengthFactor = 4.0;

        /// <summary>
        /// Seconds-left marks an interrupt window is counted down at. One line each, latched once per window.
        /// </summary>
        public static readonly IReadOnlyList<int> InterruptCountdownMarks = new[] { 6, 3 };

        // ---- the args reader -----------------------------------------------------------------------------

        /// <summary>
        /// Reads one mechanic's "key=value key=value" string. A token with no '=' , an empty key, or a
        /// repeated key is dropped with a line in <paramref name="errors"/> (when one is supplied) rather than
        /// failing the whole string: a designer who fat-fingers one number keeps the other nine.
        /// Never returns null.
        /// </summary>
        public static MlDigsiteMechanicArgs ParseArgs(string raw, List<string> errors = null)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(raw))
                return new MlDigsiteMechanicArgs(map);

            foreach (var token in raw.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var equals = token.IndexOf('=');

                if (equals <= 0 || equals == token.Length - 1)
                {
                    errors?.Add($"token '{token}' is not key=value");
                    continue;
                }

                var key = token.Substring(0, equals).Trim();
                var value = token.Substring(equals + 1).Trim();

                if (key.Length == 0 || value.Length == 0)
                {
                    errors?.Add($"token '{token}' is not key=value");
                    continue;
                }

                if (map.ContainsKey(key))
                {
                    errors?.Add($"token '{token}' repeats key '{key}'; the first one is kept");
                    continue;
                }

                map[key] = value;
            }

            return new MlDigsiteMechanicArgs(map);
        }

        // ---- the set table -------------------------------------------------------------------------------

        /// <summary>
        /// The authored token for a mechanic, matched case-insensitively. Returns false for anything the
        /// modules do not implement, which is what keeps a typo ("volatle") out of a live encounter instead of
        /// silently running an empty slot.
        /// </summary>
        public static bool TryParseMechanic(string token, out MlDigsiteMechanic mechanic)
        {
            mechanic = MlDigsiteMechanic.None;

            if (string.IsNullOrWhiteSpace(token))
                return false;

            switch (token.Trim().ToLowerInvariant())
            {
                case "none": mechanic = MlDigsiteMechanic.None; return true;
                case "volatile": mechanic = MlDigsiteMechanic.Volatile; return true;
                case "drums": mechanic = MlDigsiteMechanic.Drums; return true;
                case "immunephases": mechanic = MlDigsiteMechanic.ImmunePhases; return true;
                case "interrupt": mechanic = MlDigsiteMechanic.Interrupt; return true;
                case "safezones": mechanic = MlDigsiteMechanic.SafeZones; return true;
                case "immune50": mechanic = MlDigsiteMechanic.Immune50; return true;
                default: return false;
            }
        }

        /// <summary>
        /// Parses ml_digsite_bossrush_sets into the set table, in authored order.
        ///
        /// NOTHING IS DROPPED SILENTLY, the same contract MlDigsiteRules.ParseTierTable states: a record with
        /// no recognised main mechanic, a duplicate set id, or an out-of-range add scale each add a line to
        /// <paramref name="errors"/> and are skipped. The CALLER decides what an empty result means
        /// (MlDigsiteTunables falls back to <see cref="DefaultSets"/>), so a mis-typed table can never leave
        /// Boss Rush with no mechanics at all.
        ///
        /// A record's set= is the id a designer pins with ml_digsite_bossrush_set_force and the id the status
        /// line shows; it is NOT the index the roll draws, so removing set 3 from the table leaves 1/2/4/5
        /// pinnable by their own ids.
        /// </summary>
        public static IReadOnlyList<MlDigsiteMechanicSet> ParseSets(string raw, List<string> errors = null)
        {
            var result = new List<MlDigsiteMechanicSet>();

            if (string.IsNullOrWhiteSpace(raw))
                return result;

            var seen = new HashSet<int>();

            foreach (var rawRecord in raw.Split(';'))
            {
                var record = rawRecord.Trim();

                if (record.Length == 0)
                    continue;

                var args = ParseArgs(record, errors);

                var setId = args.GetInt("set", 0);

                if (setId < 1)
                {
                    errors?.Add($"set record '{record}' has no set= id of at least 1");
                    continue;
                }

                if (!seen.Add(setId))
                {
                    errors?.Add($"set record '{record}' repeats set {setId}; the first one is kept");
                    continue;
                }

                if (!TryParseMechanic(args.GetString("main"), out var main) || main == MlDigsiteMechanic.None)
                {
                    errors?.Add($"set record '{record}' has no recognised main= mechanic");
                    continue;
                }

                if (!TryParseMechanic(args.GetString("secondary", "none"), out var secondary))
                {
                    errors?.Add($"set record '{record}' has an unrecognised secondary= mechanic; treated as none");
                    secondary = MlDigsiteMechanic.None;
                }

                result.Add(new MlDigsiteMechanicSet(setId, main, secondary,
                    ClampAddScale(args.GetDouble("addhp", 1.0)),
                    ClampAddScale(args.GetDouble("addspeed", 1.0))));
            }

            return result;
        }

        /// <summary>
        /// Add health/speed scales are confined to [0.05, 10]. The floor stops a typo producing a one-hit-point
        /// add that dies to a stray proc before anyone sees it; the ceiling stops one producing an add harder
        /// than the boss.
        /// </summary>
        public static double ClampAddScale(double value)
            => !double.IsFinite(value) ? 1.0 : Math.Clamp(value, 0.05, 10.0);

        /// <summary>
        /// The StartingValue a mechanic add needs so that its MaxValue comes out at
        /// <paramref name="mult"/> times <paramref name="baseMax"/>.
        ///
        /// BIDIRECTIONAL, which is the whole reason this exists rather than a call to
        /// WorldEventSpawner.ScaledStartingValue: that helper returns its input unchanged for any multiplier
        /// at or below 1.0, and every volatile set's addhp is below 1.0 by design. Works in the same currency
        /// it does - the DELTA between the current max and the wanted max, applied to StartingValue, so the
        /// endurance-derived part of MaxValue is preserved rather than recomputed.
        ///
        /// Floored at 1: a multiplier small enough to take StartingValue to zero would hand the fight an add
        /// that dies to a stray proc before anyone has seen it, and a wrap here would hand it one with four
        /// billion health.
        /// </summary>
        public static uint ScaledAddHealth(uint startingValue, uint baseMax, double mult)
        {
            if (!double.IsFinite(mult) || mult <= 0.0 || baseMax == 0 || Math.Abs(mult - 1.0) < 1e-9)
                return startingValue;

            var wanted = Math.Round(baseMax * mult, MidpointRounding.AwayFromZero);

            if (wanted > uint.MaxValue)
                wanted = uint.MaxValue;

            var target = (uint)Math.Max(1.0, wanted);

            if (target >= baseMax)
            {
                var delta = target - baseMax;

                return startingValue > uint.MaxValue - delta ? uint.MaxValue : startingValue + delta;
            }

            var reduce = baseMax - target;

            return startingValue > reduce ? startingValue - reduce : 1u;
        }

        // ---- the boss's monster-effect overlay -------------------------------------------------------------

        /// <summary>
        /// Whether <paramref name="set"/> needs the boss to carry the immune FILTER record at all: true when
        /// either slot runs an immune mechanic, since both slots share one phase state.
        /// </summary>
        public static bool NeedsImmuneRecord(MlDigsiteMechanicSet? set)
            => set != null && (IsImmuneMechanic(set.Value.Main) || IsImmuneMechanic(set.Value.Secondary));

        private static bool IsImmuneMechanic(MlDigsiteMechanic mechanic)
            => mechanic == MlDigsiteMechanic.ImmunePhases || mechanic == MlDigsiteMechanic.Immune50;

        /// <summary>
        /// The monster-effect overlay a Boss Rush boss actually carries: the authored
        /// ml_digsite_bossrush_mechanic string, PLUS the immune filter record when the set this encounter
        /// rolled runs an immune mechanic in either slot.
        ///
        /// THE RECORD HAS TO BE COMPOSED HERE, at spawn, and cannot be bolted on when the threshold is
        /// crossed: Creature.ApplyMonsterEffectOverlay allocates a fresh effect-state array, so attaching an
        /// immunity at 85% health would silently reset every other effect the boss carries. It ships INERT
        /// with the boss - ImmuneEffect filters nothing until ImmunePhasesMechanic sets P_DigsiteImmune - and
        /// without it the flag has nothing reading it and the phase is a chat line with no immunity behind it.
        ///
        /// Only for the sets that need it, so a volatile or drum set's boss carries exactly the effects it
        /// carried before the mechanics landed. Pure, so the composition is testable without a world.
        /// </summary>
        public static string ComposeBossOverlay(string authoredOverlay, MlDigsiteMechanicSet? set)
        {
            var overlay = authoredOverlay == null ? string.Empty : authoredOverlay.Trim();

            if (!NeedsImmuneRecord(set))
                return overlay;

            overlay = overlay.TrimEnd(';', ' ', '\t');

            return overlay.Length == 0 ? ImmuneRecord : overlay + "; " + ImmuneRecord;
        }

        // ---- the roll ------------------------------------------------------------------------------------

        /// <summary>
        /// Which set this encounter runs.
        ///
        /// ROLLED, NEVER KEYED TO THE BOSS. A uniform draw over the table, independent of which of the six
        /// bosses was picked, satisfies both halves of the requirement at once - random each time, and sets
        /// reused across bosses once the pool exceeds five - and is what keeps "add the seventh boss" to one
        /// line in MlDigsiteRoster.BossEntries with no mechanic work at all.
        ///
        /// <paramref name="forcedSetId"/> is ml_digsite_bossrush_set_force: 0 rolls, and any other value pins
        /// the set with that authored set= id. A forced id the table does not carry falls back to the roll
        /// rather than refusing, because a stale pin must never stop Boss Rush having a mechanic.
        ///
        /// System.Random.Next(count) is EXCLUSIVE of its upper bound; ThreadSafeRandom.Next(int, int) is
        /// inclusive and is deliberately not used here (MlDigsiteManager.NewRandom says the same).
        /// </summary>
        public static MlDigsiteMechanicSet? RollSet(IReadOnlyList<MlDigsiteMechanicSet> sets, long forcedSetId, Random rng)
        {
            if (sets == null || sets.Count == 0)
                return null;

            if (forcedSetId > 0)
            {
                for (var i = 0; i < sets.Count; i++)
                {
                    if (sets[i].SetId == forcedSetId)
                        return sets[i];
                }
            }

            rng = rng ?? new Random();

            return sets[rng.Next(sets.Count)];
        }

        // ---- shared arithmetic ---------------------------------------------------------------------------

        /// <summary>
        /// The seconds between two runs of one mechanic in one slot: its own authored every=, multiplied by
        /// <paramref name="secondaryMultiplier"/> when it is the SECONDARY of its set. Floored at 1 s, which
        /// is the driver tick's own granularity - anything finer is not expressible and a 0 would make the
        /// mechanic fire on every tick.
        /// </summary>
        public static double CadenceSeconds(double everySeconds, MlDigsiteMechanicSlot slot, double secondaryMultiplier)
        {
            if (!double.IsFinite(everySeconds) || everySeconds < 1.0)
                everySeconds = 1.0;

            if (slot != MlDigsiteMechanicSlot.Secondary)
                return everySeconds;

            if (!double.IsFinite(secondaryMultiplier) || secondaryMultiplier <= 0.0)
                secondaryMultiplier = 1.0;

            return Math.Max(1.0, everySeconds * secondaryMultiplier);
        }

        /// <summary>
        /// The immune thresholds a slot actually uses: the authored list for <see cref="MlDigsiteMechanic.ImmunePhases"/>,
        /// and 0.50 alone for <see cref="MlDigsiteMechanic.Immune50"/> whatever the string says. Returned
        /// DESCENDING, because the driver fires the highest threshold the boss has fallen below first.
        /// </summary>
        public static IReadOnlyList<double> ImmuneThresholds(MlDigsiteMechanic mechanic, MlDigsiteMechanicArgs args)
        {
            if (mechanic == MlDigsiteMechanic.Immune50)
                return Immune50Thresholds;

            var authored = (args ?? MlDigsiteMechanicArgs.Empty).GetFractions("thresholds", DefaultImmuneThresholds);

            var ordered = new List<double>(authored);
            ordered.Sort((a, b) => b.CompareTo(a));

            return ordered;
        }

        /// <summary>
        /// Whether a set turns the boss immune at the same whole-percent health a boss-HP milestone line
        /// would announce. The two coincide at 50 for both shipped sets that carry an immune mechanic, and
        /// two lines about one moment read as a bug - so the milestone line is the one that gives way (owner
        /// ruling 2026-09-20).
        ///
        /// Checks BOTH slots, and reads Immune50 through <see cref="ImmuneThresholds"/> rather than through
        /// the authored list, so a set whose secondary is immune50 suppresses 50 whatever thresholds= says.
        /// </summary>
        public static bool ImmuneCoversMilestone(MlDigsiteMechanicSet set, MlDigsiteMechanicArgs args, int milestonePercent)
            => SlotCoversMilestone(set.Main, args, milestonePercent)
                || SlotCoversMilestone(set.Secondary, args, milestonePercent);

        private static bool SlotCoversMilestone(MlDigsiteMechanic mechanic, MlDigsiteMechanicArgs args, int milestonePercent)
        {
            if (mechanic != MlDigsiteMechanic.ImmunePhases && mechanic != MlDigsiteMechanic.Immune50)
                return false;

            var thresholds = ImmuneThresholds(mechanic, args);

            for (var i = 0; i < thresholds.Count; i++)
            {
                if ((int)Math.Round(thresholds[i] * 100.0) == milestonePercent)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// How far from the anchor the interrupt object may stand, CONSTRAINED BY THE TETHER. It must be
        /// strictly inside ml_digsite_boss_tether_radius or a player running to the object drags the boss past
        /// its tether and the boss disengages - which would turn the interrupt into an accidental reset button
        /// (design risk R8). Clamped into [5, tether - 5], and a tether too small to hold both ends collapses
        /// to half of it.
        /// </summary>
        public static double ClampInterruptDistance(double distance, double tetherRadius)
        {
            if (!double.IsFinite(distance) || distance <= 0.0)
                distance = 5.0;

            if (!double.IsFinite(tetherRadius) || tetherRadius <= 0.0)
                return Math.Clamp(distance, 5.0, 30.0);

            var ceiling = tetherRadius - 5.0;

            if (ceiling < 5.0)
                return Math.Max(1.0, tetherRadius * 0.5);

            return Math.Clamp(distance, 5.0, ceiling);
        }

        /// <summary>
        /// The safe-zone forgiveness rule, in its concrete form: the first <paramref name="forgiveness"/>
        /// misses in a window CANNOT kill - the damage is capped at one point short of the player's current
        /// health, by construction rather than by tuning - and the one after them is lethal.
        ///
        /// <paramref name="missesAlready"/> is how many misses this player had already accrued BEFORE this one,
        /// so the first miss of a fight arrives with 0.
        /// </summary>
        public static bool MissIsLethal(int missesAlready, int forgiveness)
            => missesAlready >= Math.Max(0, forgiveness);

        /// <summary>
        /// ONE DAMAGE CONVENTION FOR THE WHOLE FEATURE, so an operator retuning a number live cannot misread
        /// its units: an authored damage AT OR BELOW 1.0 is a FRACTION of the hit player's own max health,
        /// and anything above 1.0 is a flat pre-resistance number. Both the safe zones' lethal hit and the
        /// interrupt's missed hit read through here; the resistance mod is applied afterwards by
        /// MlDigsiteMechanicContext.Hit, exactly as it is for a flat number.
        ///
        /// A fraction is what makes a punishment scale the same way on a level 205 boss and a level 275 one,
        /// which is why it is the shipped form for both of those. A flat number stays expressible because the
        /// drum cadence's slow bleed is deliberately NOT relative to the player.
        ///
        /// 0, a non-finite value and a max health of 0 all resolve to 0 damage, which Hit refuses outright.
        /// </summary>
        public static double ResolveDamage(double authored, uint maxHealth)
        {
            if (!double.IsFinite(authored) || authored <= 0.0)
                return 0.0;

            if (authored > 1.0)
                return authored;

            return maxHealth == 0 ? 0.0 : maxHealth * authored;
        }

        /// <summary>
        /// The damage a non-lethal safe-zone miss actually applies: never more than one point short of what
        /// the player is standing on. Returns 0 when they are already at 1 health or less, so a forgiven miss
        /// can never be the thing that kills.
        /// </summary>
        public static uint NonLethalDamage(uint rolled, uint currentHealth)
        {
            if (currentHealth <= 1)
                return 0;

            var ceiling = currentHealth - 1;

            return rolled > ceiling ? ceiling : rolled;
        }

        /// <summary>
        /// The warning line for a forgiven miss, naming the margin that is left. Player-facing, ASCII only.
        ///
        /// THE MARGIN IS COUNTED IN MISSES-UNTIL-DEATH, INCLUSIVE, which is the only reading a player can act
        /// on: with forgiveness=2, miss 1 leaves two more misses before the lethal one lands, and miss 2
        /// leaves one. Counting the misses still FORGIVEN instead is off by one and tells a player who has
        /// just used their second life that they have one left when they have none (design section 5.5:
        /// "Two more like that and you are finished." / "...One more.").
        ///
        /// <paramref name="missesNow"/> INCLUDES the miss being announced, so the first miss of a fight
        /// arrives as 1 - the opposite end from <see cref="MissIsLethal"/>, which takes the count BEFORE it.
        /// </summary>
        public static string MissWarning(int missesNow, int forgiveness)
        {
            var left = Math.Max(0, forgiveness) - missesNow + 1;

            // Unreachable from the resolve, which only warns on a miss it has already forgiven; kept so a
            // future caller that warns on a lethal miss says something true rather than "0 more like that".
            if (left <= 0)
                return "The stone grinds past you. The next one will not.";

            if (left == 1)
                return "The stone grinds past you. One more like that and you are finished.";

            if (left == 2)
                return "The stone grinds past you. Two more like that and you are finished.";

            return $"The stone grinds past you. {left} more like that and you are finished.";
        }

        /// <summary>
        /// Whether the countdown reminder for <paramref name="mark"/> is due with
        /// <paramref name="secondsLeft"/> left on an interrupt window.
        ///
        /// AT OR BELOW THE MARK, NEVER EQUAL TO IT. The driver tick is nominally 1 s, but a stalled landblock
        /// or a long reap can take a window from 7 s left straight to 5 s, and an equality test would drop
        /// that window's 6 s reminder entirely - silently, and precisely when the server is already
        /// struggling. The caller's per-mark latch (MlDigsiteBossMechanicState.TryLatchInterruptWarning) is
        /// what keeps it to one line per mark per window.
        ///
        /// 0 or less is the expiry's business, not a reminder's.
        /// </summary>
        public static bool CountdownMarkDue(int secondsLeft, int mark) => secondsLeft > 0 && secondsLeft <= mark;

        // ---- burning ground (round 17, drum Volley) ------------------------------------------------------

        /// <summary>
        /// How many damage ticks a burning patch gets: one per <paramref name="intervalSeconds"/> for
        /// <paramref name="durationSeconds"/>, the first one interval AFTER the resolve hit (which is its own,
        /// unchanged hit). 8 s at 1 s is 8 ticks. A non-finite or non-positive duration or interval is 0 ticks,
        /// which is how the burn is switched off.
        /// </summary>
        public static int BurnTickCount(double durationSeconds, double intervalSeconds)
        {
            if (!double.IsFinite(durationSeconds) || !double.IsFinite(intervalSeconds) || durationSeconds <= 0.0 || intervalSeconds <= 0.0)
                return 0;

            // The epsilon keeps 8 / 1 at 8 against a representation error below it, never above it.
            return (int)Math.Min(1000, Math.Floor(durationSeconds / intervalSeconds + 1e-9));
        }

        /// <summary>
        /// Whether a player at <paramref name="point"/> is standing in ANY burning patch: same instance as the
        /// patch, and within <paramref name="radius"/> metres of its centre by Position.DistanceTo - the same
        /// test RadialHit applies to the resolve hit, so the burn and the hit it follows cover the same ground.
        /// Checked fresh on every tick, which is what makes stepping out stop the damage. A player standing in
        /// two overlapping patches is still one hit per tick.
        /// </summary>
        public static bool InsideAnyBurn(ACE.Entity.Position point, IReadOnlyList<ACE.Entity.Position> patches, double radius)
        {
            if (point == null || patches == null || !(radius > 0.0))
                return false;

            for (var i = 0; i < patches.Count; i++)
            {
                var patch = patches[i];

                if (patch == null || patch.Instance != point.Instance)
                    continue;

                if (point.DistanceTo(patch) <= radius)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Whether a point is inside the RECTANGLE of width <paramref name="width"/> and length
        /// <paramref name="length"/> centred on <paramref name="originX"/>/<paramref name="originY"/> and
        /// aligned with the heading (<paramref name="headingX"/>, <paramref name="headingY"/>) - the drum
        /// cadence's "wall" shape.
        ///
        /// BOUNDED ON BOTH AXES, and the length half is not tidiness. The wall is DRAWN as a fixed span of
        /// markers (MlDigsiteMechanicGeometry.Line, width * <see cref="WallLengthFactor"/> end to end); an
        /// unbounded band hit every participant standing on that infinite line, out to the audience radius,
        /// which is roughly three times further than the drawn shape ever reached. A player who correctly
        /// read the telegraph and stood well past its end was hit by something that was never shown to them.
        /// <paramref name="length"/> is the TOTAL span, so the test is half of it either side of the origin -
        /// the same centring <see cref="MlDigsiteMechanicGeometry.Line"/> draws with.
        ///
        /// A non-positive or non-finite length means UNBOUNDED along the axis, so the perpendicular test
        /// alone decides; that is a caller's explicit choice, never a fallback a missing argument reaches.
        /// </summary>
        public static bool InsideWall(double originX, double originY, double headingX, double headingY,
            double pointX, double pointY, double width, double length)
        {
            var magnitude = Math.Sqrt(headingX * headingX + headingY * headingY);

            if (!double.IsFinite(magnitude) || magnitude <= 0.0)
                return false;

            var ux = headingX / magnitude;
            var uy = headingY / magnitude;

            var dx = pointX - originX;
            var dy = pointY - originY;

            var perpendicular = Math.Abs(dx * -uy + dy * ux);

            if (perpendicular > Math.Max(0.0, width) * 0.5)
                return false;

            if (!double.IsFinite(length) || length <= 0.0)
                return true;

            return Math.Abs(dx * ux + dy * uy) <= length * 0.5;
        }

        /// <summary>
        /// How many drum beats this cycle rolls: 1, 2 or 3, which is the whole vocabulary the tester described.
        /// Pure given <paramref name="rng"/> so a test can pin the shape a given seed produces.
        /// </summary>
        public static int RollBeats(Random rng) => (rng ?? new Random()).Next(1, 4);

        /// <summary>The shape a beat count resolves to. One beat a ring, two a wall, three a volley.</summary>
        public static MlDigsiteDrumShape ShapeForBeats(int beats)
        {
            switch (beats)
            {
                case 1: return MlDigsiteDrumShape.Ring;
                case 2: return MlDigsiteDrumShape.Wall;
                default: return MlDigsiteDrumShape.Volley;
            }
        }

        /// <summary>
        /// The one-shot visual DrumCadenceMechanic plays on the boss for beat <paramref name="beat"/>, so the
        /// colour also counts the beat: blue on 1, green on 2, gold on 3 (owner ruling, RoZ playtest feedback
        /// that the beats had a chat line and a sound but no visible cue on the boss itself).
        /// PlayScript.Invalid for anything outside 1-3, which MlDigsiteProps.PlayScriptOn still sends
        /// harmlessly - beat is never anything else in practice (DrumCadenceMechanic.Tick loops i in 1..beats
        /// and beats is 1-3, see <see cref="RollBeats"/>).
        /// </summary>
        public static PlayScript DrumBeatPlayScript(int beat)
        {
            switch (beat)
            {
                case 1: return PlayScript.RestrictionEffectBlue;
                case 2: return PlayScript.RestrictionEffectGreen;
                case 3: return PlayScript.RestrictionEffectGold;
                default: return PlayScript.Invalid;
            }
        }

        /// <summary>
        /// Whether <paramref name="spellId"/> is fit to cast for a drum shape: positive, AND its classifier
        /// result (SpellProjectile.GetProjectileSpellType(spellId), which the caller resolves - this rule
        /// never touches DatManager itself, which is what keeps it unit-testable) matches
        /// <paramref name="expectedType"/> (Ring or Wall). 0 or a spell of the wrong shape is how an operator
        /// falls a shape back to the original geometric damage without the driver ever throwing.
        /// </summary>
        public static bool IsValidDrumSpell(long spellId, ProjectileSpellType actualType, ProjectileSpellType expectedType)
            => spellId > 0 && actualType == expectedType;

        /// <summary>
        /// Which live creatures cast the drum's Ring/Wall spell at one resolve: <paramref name="boss"/> first
        /// (if it is still alive and in the world), then <paramref name="adds"/> in the order given, up to
        /// <paramref name="maxCasters"/> total - the boss counts as one of that total, not an addition to it.
        /// A dead or destroyed creature, boss or add, is skipped rather than cast for. Pure given the two
        /// liveness reads (IsDestroyed, IsDead), which are plain field/property reads that never touch
        /// DatManager, so this is unit-testable with TestCreatures fixtures.
        /// </summary>
        public static List<Creature> SelectDrumCasters(Creature boss, IReadOnlyList<Creature> adds, int maxCasters)
        {
            var casters = new List<Creature>();

            if (maxCasters < 1)
                return casters;

            if (boss != null && !boss.IsDestroyed && !boss.IsDead)
                casters.Add(boss);

            if (adds != null)
            {
                for (var i = 0; i < adds.Count && casters.Count < maxCasters; i++)
                {
                    var add = adds[i];

                    if (add != null && !add.IsDestroyed && !add.IsDead)
                        casters.Add(add);
                }
            }

            return casters;
        }
    }

    /// <summary>What a drum cadence's beat count resolves into. See MlDigsiteBossMechanicRules.ShapeForBeats.</summary>
    public enum MlDigsiteDrumShape
    {
        Ring,
        Wall,
        Volley,
    }
}
