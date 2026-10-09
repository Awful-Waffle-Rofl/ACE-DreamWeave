using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// The Thread Gem's generator inputs, stored as one PropertyString (DungeonGemSpec, 9014) so a gem is
    /// pure content: no shard table, tradeable and bankable like any item. Pure codec, no engine references.
    ///
    /// Format v2 (TECH-DESIGN 4.1 plus phase 2 attunement):
    ///   v2|dg=&lt;id|any&gt;|lvl=&lt;1..500&gt;|tier=&lt;1..8&gt;|fam=&lt;species id|any&gt;|seed=&lt;int&gt;|mods=&lt;id:mag,...&gt;|press=&lt;n&gt;|lock=&lt;id,...&gt;|load=&lt;wcid:doses,...&gt;[|guide=&lt;rung:serial:owner&gt;][|run=&lt;uint&gt;|owner=&lt;uint&gt;]
    /// lvl= is bounded by the RUN ceiling (<see cref="MaxRunLevel"/>, 500), not the gem ceiling
    /// (<see cref="MaxGemLevel"/>, 375): only the press can lift a spec above 375 (split 2026-10-08).
    /// run/owner are present only once the first use has bound the gem to a live run (Q10). guide= is
    /// present only on a Thread-Guide item (see the guide= note below).
    ///
    /// TryParse accepts v1 and v2 (MinVersion..CurrentVersion). A v1 string reads with Presses 0 and empty
    /// Locks/Load; Serialize ALWAYS emits v2, including empty mods=, lock= and load=, so there is exactly
    /// one canonical on-item form (phase 2 plan, fork B option 1).
    ///
    /// COMPATIBILITY SHIM - DO NOT "CLEAN UP" THE inst= FIELD. The instability mechanic was removed by owner
    /// ruling on 2026-09-07, so Serialize no longer WRITES inst=. TryParse must go on ACCEPTING it, and
    /// silently ignoring it, forever: stage was deployed on 2026-09-06 and every gem and fragment created
    /// there carries an inst= field in its persisted DungeonGemSpec property (a real example:
    /// "...|mods=...|inst=32|press=1|lock=|load="). If inst= were dropped from the allowed-field set, TryParse
    /// would reject every one of those items as an unknown field and each one would become permanently inert:
    /// using the gem logs the parse error and answers "This gem's inscription is unreadable"
    /// (ThreadDungeonGemHandler.cs:150-154), and the Fragment Press refuses the fragment the same way
    /// (FragmentPressStation.cs:308-313). It is NOT destroyed - ThreadDungeonSweeper.IsDeadBoundGem returns
    /// false on a TryParse failure (ThreadDungeonSweeper.cs:29-30), so the sweeper never touches it - but a
    /// dead item in a player's pack is bad enough. The value is not read anywhere, so it is deliberately not
    /// range-checked either: the field's only remaining job is to not be a parse failure. See
    /// DungeonGemSpecTests.A_v2_string_that_still_carries_inst_parses_and_the_field_is_ignored.
    ///
    /// THE OPTIONAL guide= FIELD, AND ITS ROLLBACK HAZARD. guide=&lt;rung&gt;:&lt;serial&gt;:&lt;ownerGuid&gt; marks a
    /// Thread-Guide fragment (see <see cref="GuideTag"/> and ThreadGuideRules, the one place guide detection
    /// lives). It is written only when present, so every spec WITHOUT it serializes byte-identically to the
    /// build before it existed. The hazard is the inst= one run backwards: a build OLDER than this field does
    /// not have "guide" in its allowed-field set, so if the server is ever rolled back past it, every guide
    /// item already persisted reads as an unknown field and becomes inert in exactly the way the inst= note
    /// above describes (unreadable gem, refused at the press, never swept). Rolling back past this field is
    /// therefore a decision to strand every outstanding guide item, not a free revert. And once it has
    /// shipped, "guide" must stay in the allowed-field set forever, for the same reason inst= does.
    ///
    /// Item-level invariants are deliberately NOT enforced here (they belong to the item factory):
    /// a Thread Gem always has an empty Load, a Raw Fragment always has seed=0.
    /// </summary>
    public sealed class DungeonGemSpec
    {
        public const int CurrentVersion = 2;
        public const int MinVersion = 1;
        public const string Any = "any";
        /// <summary>
        /// The GEM level ceiling: the highest level anything can ROLL or ISSUE. Raised 275 -> 375 by owner
        /// ruling on 2026-09-09, together with the four new Raw Fragment rungs at 300/325/350/375. Read by the
        /// gem roller, by the survey level ring, and as the DEFAULT of the survey reward's reference level - the
        /// reference level is a separate tunable (dynamic_dungeons_survey_reference_level) precisely so the
        /// payout curve can be pinned below the ceiling if a raise ever needs to land without repricing the
        /// survey. DungeonGemFactory's rung table and ThreadGuideLadder top out at the same 375 on their own.
        ///
        /// SPLIT FROM THE RUN CEILING on 2026-10-08 (owner ruling, final): the gem ceiling stays 375 - no new
        /// Raw Fragment rungs, no roller, Thread-Guide or survey change - while the level a RUN plays at may go
        /// higher (<see cref="MaxRunLevel"/>). The rename from MaxLevel was deliberate: it turned every reader
        /// of the old single constant into a compile error, so each one had to choose a side.
        /// </summary>
        public const int MaxGemLevel = 375;

        /// <summary>
        /// The RUN level ceiling: the highest spec.Level the codec accepts and the Fragment Press can produce
        /// (owner ruling 2026-10-08). A 375 fragment plus a Mana Scarab (+35, Content/dungeons/dynamic/
        /// attunement.json, limit 1 per fragment) presses to a 410 run, and the press refuses a fragment that
        /// has already been pressed (FragmentPressStation.Press, the Seed != 0 check), so 410 is the highest
        /// level a player can reach with the shipped components. 500 is the headroom above that: the codec,
        /// the press clamps, every dungeon's index.json maxLevel and the health curve's top level all sit at
        /// it, so an admin-issued or a future-component gem up to 500 opens a correctly-priced run rather than
        /// an unreadable item.
        ///
        /// NOT a gem ceiling. Nothing ROLLS above <see cref="MaxGemLevel"/>; this is only how high an
        /// already-rolled gem may be pushed and still play.
        /// </summary>
        public const int MaxRunLevel = 500;
        public const int MaxTier = 8;
        public const int MaxLocks = 2;

        /// <summary>
        /// The most doses of one component wcid a single load entry can carry. Public because the
        /// attunement lint (ThreadDungeonStore rule 8) caps a component type's limit at this value:
        /// a larger limit would let RawFragmentRules.Load build a spec Serialize could write but
        /// TryParse would refuse.
        /// </summary>
        public const int MaxDoses = 99;

        public int Version => CurrentVersion;
        public string DungeonId { get; }
        public int Level { get; }
        public int Tier { get; }
        public string Family { get; }
        public int Seed { get; }
        public IReadOnlyList<(string Id, double Magnitude)> Modifiers { get; }
        public uint RunId { get; }
        public uint OwnerGuid { get; }

        /// <summary>How many times this spec has been through the Fragment Press. Never negative.</summary>
        public int Presses { get; }

        /// <summary>
        /// Modifier ids the player has pinned. At most MaxLocks, no duplicates, and every id must name a
        /// modifier present in Modifiers - enforced here AND in TryParse, so Serialize can never produce a
        /// string TryParse would refuse.
        /// </summary>
        public IReadOnlyList<string> Locks { get; }

        /// <summary>
        /// Component doses accepted so far, in the order they were written. Every wcid > 0, every dose count
        /// 1..MaxDoses, no duplicate wcid - enforced here AND in TryParse, so Serialize can never produce a
        /// string TryParse would refuse.
        /// </summary>
        public IReadOnlyList<(uint Wcid, int Doses)> Load { get; }

        public bool IsBound => RunId != 0;

        /// <summary>
        /// The Thread-Guide tag, or null for every ordinary gem and fragment. Do not test this field
        /// directly from game code: ThreadGuideRules.IsGuide / IsCurrent are the one place guide detection
        /// lives. A GuideTag is valid by construction, so Serialize can never write a guide= TryParse refuses.
        /// </summary>
        public GuideTag Guide { get; }

        /// <summary>
        /// The three phase 2 parameters and the guide tag are optional and defaulted, so the phase 1 call
        /// sites (WithBinding here, DungeonGemRoller.Roll) compile unchanged.
        /// Throws ArgumentException when the lock list breaks the lock rule (see Locks) or the load list
        /// breaks the load rule (see Load).
        /// </summary>
        public DungeonGemSpec(string dungeonId, int level, int tier, string family, int seed,
            IEnumerable<(string Id, double Magnitude)> modifiers, uint runId, uint ownerGuid,
            int presses = 0,
            IEnumerable<string> locks = null, IEnumerable<(uint Wcid, int Doses)> load = null,
            GuideTag guide = null)
        {
            Guide = guide;
            DungeonId = dungeonId;
            Level = level;
            Tier = tier;
            Family = family;
            Seed = seed;
            Modifiers = modifiers == null ? new List<(string, double)>() : modifiers.ToList();
            RunId = runId;
            OwnerGuid = ownerGuid;
            Presses = presses;
            var lockList = locks == null ? new List<string>() : locks.ToList();
            if (!LocksAreLegal(lockList, Modifiers, out var lockError))
                throw new ArgumentException(lockError, nameof(locks));
            Locks = lockList;
            var loadList = load == null ? new List<(uint Wcid, int Doses)>() : load.ToList();
            if (!LoadIsLegal(loadList, out var loadError))
                throw new ArgumentException(loadError, nameof(load));
            Load = loadList;
        }

        // Every With* below passes Guide through. A With* that dropped it would silently turn a guide
        // fragment into an ordinary one the first time the press rebuilt its spec (Resolve chains five of
        // them), so DungeonGemSpecTests.Every_With_preserves_the_guide_tag pins each one.

        public DungeonGemSpec WithBinding(uint runId, uint ownerGuid)
            => new DungeonGemSpec(DungeonId, Level, Tier, Family, Seed, Modifiers, runId, ownerGuid, Presses, Locks, Load, Guide);

        /// <summary>
        /// Replaces the modifier list. Any lock naming a modifier that is no longer present is DROPPED,
        /// rather than throwing, so a re-roll can freely rewrite the modifiers; the surviving locks keep
        /// their order. Use WithLocks to set locks explicitly.
        /// </summary>
        public DungeonGemSpec WithModifiers(IEnumerable<(string Id, double Magnitude)> modifiers)
        {
            var mods = modifiers == null ? new List<(string Id, double Magnitude)>() : modifiers.ToList();
            var kept = Locks.Where(l => mods.Any(m => m.Id == l)).ToList();
            return new DungeonGemSpec(DungeonId, Level, Tier, Family, Seed, mods, RunId, OwnerGuid, Presses, kept, Load, Guide);
        }

        public DungeonGemSpec WithLevel(int level)
            => new DungeonGemSpec(DungeonId, level, Tier, Family, Seed, Modifiers, RunId, OwnerGuid, Presses, Locks, Load, Guide);

        public DungeonGemSpec WithSeed(int seed)
            => new DungeonGemSpec(DungeonId, Level, Tier, Family, seed, Modifiers, RunId, OwnerGuid, Presses, Locks, Load, Guide);

        public DungeonGemSpec WithPresses(int presses)
            => new DungeonGemSpec(DungeonId, Level, Tier, Family, Seed, Modifiers, RunId, OwnerGuid, presses, Locks, Load, Guide);

        /// <summary>Throws ArgumentException when the new list breaks the lock rule (see Locks).</summary>
        public DungeonGemSpec WithLocks(IEnumerable<string> locks)
            => new DungeonGemSpec(DungeonId, Level, Tier, Family, Seed, Modifiers, RunId, OwnerGuid, Presses, locks, Load, Guide);

        /// <summary>Throws ArgumentException when the new list breaks the load rule (see Load).</summary>
        public DungeonGemSpec WithLoad(IEnumerable<(uint Wcid, int Doses)> load)
            => new DungeonGemSpec(DungeonId, Level, Tier, Family, Seed, Modifiers, RunId, OwnerGuid, Presses, Locks, load, Guide);

        /// <summary>Sets or (with null) clears the Thread-Guide tag. Every other field is carried unchanged.</summary>
        public DungeonGemSpec WithGuide(GuideTag guide)
            => new DungeonGemSpec(DungeonId, Level, Tier, Family, Seed, Modifiers, RunId, OwnerGuid, Presses, Locks, Load, guide);

        /// <summary>
        /// Internal so callers that build a spec (DungeonGemRoller.Roll) can reject a malformed id/family
        /// BEFORE it is baked into a spec that TryParse will later refuse to read back (WP-10 fix round 1).
        /// </summary>
        internal static bool IsIdent(string s) => !string.IsNullOrEmpty(s) && s.All(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_');

        /// <summary>
        /// The one lock rule, shared by TryParse and the constructor so the two can never disagree:
        /// every id is an ident, ids do not repeat, there are at most MaxLocks of them, and each names a
        /// modifier present in <paramref name="modifiers"/>.
        /// </summary>
        private static bool LocksAreLegal(IReadOnlyList<string> locks, IReadOnlyList<(string Id, double Magnitude)> modifiers, out string error)
        {
            error = null;

            if (locks.Count > MaxLocks) { error = $"at most {MaxLocks} lock ids allowed, got {locks.Count}"; return false; }

            for (var i = 0; i < locks.Count; i++)
            {
                var id = locks[i];
                if (!IsIdent(id)) { error = $"lock id '{id}' must be [a-z0-9_]+"; return false; }
                for (var j = 0; j < i; j++)
                    if (locks[j] == id) { error = $"lock id '{id}' repeated"; return false; }
                if (!modifiers.Any(m => m.Id == id)) { error = $"lock id '{id}' is not a present modifier"; return false; }
            }

            return true;
        }

        /// <summary>
        /// The one load rule, shared by TryParse and the constructor so the two can never disagree:
        /// every wcid is non-zero, every dose count is 1..MaxDoses, and no wcid repeats. Order is not
        /// constrained - the written order is meaningful and is preserved.
        /// </summary>
        private static bool LoadIsLegal(IReadOnlyList<(uint Wcid, int Doses)> load, out string error)
        {
            error = null;

            for (var i = 0; i < load.Count; i++)
            {
                var wcid = load[i].Wcid;
                var doses = load[i].Doses;
                var wcidText = wcid.ToString(CultureInfo.InvariantCulture);

                if (wcid == 0) { error = $"load wcid '{wcidText}' must be a non-zero uint"; return false; }
                if (doses < 1 || doses > MaxDoses) { error = $"load doses for '{wcidText}' must be 1..{MaxDoses}"; return false; }
                for (var j = 0; j < i; j++)
                    if (load[j].Wcid == wcid) { error = $"load wcid '{wcidText}' repeated"; return false; }
            }

            return true;
        }

        public static bool TryParse(string text, out DungeonGemSpec spec, out string error)
        {
            spec = null;
            error = null;

            if (string.IsNullOrWhiteSpace(text)) { error = "spec is empty"; return false; }

            var parts = text.Split('|');
            if (parts[0].Length < 2 || parts[0][0] != 'v'
                || !int.TryParse(parts[0].Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var version)
                || version < MinVersion || version > CurrentVersion)
            { error = $"unsupported version '{parts[0]}'"; return false; }

            var kv = new Dictionary<string, string>();
            foreach (var part in parts.Skip(1))
            {
                var eq = part.IndexOf('=');
                if (eq <= 0) { error = $"malformed field '{part}'"; return false; }
                var key = part.Substring(0, eq);
                if (kv.ContainsKey(key)) { error = $"duplicate field '{key}'"; return false; }
                kv[key] = part.Substring(eq + 1);
            }

            // "inst" is the compatibility shim described in the class header: it is ACCEPTED so that gems and
            // fragments written before the instability mechanic was removed still parse, and it is never read
            // afterwards. Removing it from this set would make every such live item unreadable. "guide" is
            // optional and carries the reverse hazard (an older build refuses it) - see the class header.
            var allowed = new HashSet<string> { "dg", "lvl", "tier", "fam", "seed", "mods", "run", "owner", "inst", "press", "lock", "load", "guide" };
            foreach (var key in kv.Keys)
                if (!allowed.Contains(key)) { error = $"unknown field '{key}'"; return false; }

            foreach (var required in new[] { "dg", "lvl", "tier", "fam", "seed", "mods" })
                if (!kv.ContainsKey(required)) { error = $"missing field '{required}'"; return false; }

            var dg = kv["dg"];
            if (!IsIdent(dg)) { error = $"dg '{dg}' must be 'any' or [a-z0-9_]+"; return false; }

            // The RUN ceiling, not the gem one: a pressed fragment legitimately carries a level above anything
            // the roller can produce (MaxRunLevel's doc comment), and a codec that refused it would turn the
            // press's own output into an unreadable item.
            if (!int.TryParse(kv["lvl"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var level) || level < 1 || level > MaxRunLevel)
            { error = $"lvl '{kv["lvl"]}' must be 1..{MaxRunLevel}"; return false; }

            if (!int.TryParse(kv["tier"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var tier) || tier < 1 || tier > MaxTier)
            { error = $"tier '{kv["tier"]}' must be 1..{MaxTier}"; return false; }

            var fam = kv["fam"];
            if (!IsIdent(fam)) { error = $"fam '{fam}' must be 'any' or [a-z0-9_]+"; return false; }

            if (!int.TryParse(kv["seed"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seed))
            { error = $"seed '{kv["seed"]}' is not an integer"; return false; }

            var mods = new List<(string Id, double Magnitude)>();
            if (kv["mods"].Length > 0)
            {
                foreach (var item in kv["mods"].Split(','))
                {
                    var colon = item.IndexOf(':');
                    if (colon <= 0) { error = $"mods entry '{item}' must be id:magnitude"; return false; }
                    var id = item.Substring(0, colon);
                    if (!IsIdent(id)) { error = $"mods id '{id}' must be [a-z0-9_]+"; return false; }
                    if (!double.TryParse(item.Substring(colon + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var mag) || double.IsNaN(mag) || double.IsInfinity(mag))
                    { error = $"mods magnitude for '{id}' is not a number"; return false; }
                    if (mods.Any(m => m.Id == id)) { error = $"mods id '{id}' repeated"; return false; }
                    mods.Add((id, mag));
                }
            }

            // NOTE: kv["inst"], if present, is deliberately neither read nor validated - see the shim note on
            // the allowed-field set above and in this class's header.

            var presses = 0;
            if (kv.TryGetValue("press", out var pressText))
            {
                if (!int.TryParse(pressText, NumberStyles.Integer, CultureInfo.InvariantCulture, out presses) || presses < 0)
                { error = $"press '{pressText}' must be >= 0"; return false; }
            }

            var locks = new List<string>();
            if (kv.TryGetValue("lock", out var lockText) && lockText.Length > 0)
                locks.AddRange(lockText.Split(','));
            if (!LocksAreLegal(locks, mods, out error)) return false;

            var load = new List<(uint Wcid, int Doses)>();
            if (kv.TryGetValue("load", out var loadText) && loadText.Length > 0)
            {
                foreach (var item in loadText.Split(','))
                {
                    var colon = item.IndexOf(':');
                    if (colon <= 0) { error = $"load entry '{item}' must be wcid:doses"; return false; }
                    var wcidText = item.Substring(0, colon);
                    if (!uint.TryParse(wcidText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var wcid))
                    { error = $"load wcid '{wcidText}' must be a non-zero uint"; return false; }
                    if (!int.TryParse(item.Substring(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var doses))
                    { error = $"load doses for '{wcidText}' must be 1..{MaxDoses}"; return false; }
                    load.Add((wcid, doses));
                }

                // Value rules (non-zero wcid, dose range, no duplicate) come from the same helper the
                // constructor uses, so a parsed spec and a constructed one are legal on identical terms.
                if (!LoadIsLegal(load, out error)) return false;
            }

            uint runId = 0, owner = 0;
            var hasRun = kv.ContainsKey("run");
            var hasOwner = kv.ContainsKey("owner");
            if (hasRun != hasOwner) { error = "run and owner must appear together"; return false; }
            if (hasRun)
            {
                if (!uint.TryParse(kv["run"], NumberStyles.Integer, CultureInfo.InvariantCulture, out runId) || runId == 0)
                { error = $"run '{kv["run"]}' is not a non-zero uint"; return false; }
                if (!uint.TryParse(kv["owner"], NumberStyles.Integer, CultureInfo.InvariantCulture, out owner) || owner == 0)
                { error = $"owner '{kv["owner"]}' is not a non-zero uint"; return false; }
            }

            GuideTag guide = null;
            if (kv.TryGetValue("guide", out var guideText) && !GuideTag.TryParse(guideText, out guide, out error))
                return false;

            spec = new DungeonGemSpec(dg, level, tier, fam, seed, mods, runId, owner, presses, locks, load, guide);
            return true;
        }

        /// <summary>
        /// Always emits the v2 form, including empty mods=, lock= and load=; run/owner stay last. No inst=
        /// field is written any more (the instability mechanic was removed, owner ruling 2026-09-07); TryParse
        /// still accepts one so already-persisted items keep reading. guide= is written ONLY when the spec
        /// carries a tag, between load= and run=, so a spec without one is byte-identical to the pre-guide form.
        /// </summary>
        public string Serialize()
        {
            var sb = new StringBuilder();
            sb.Append('v').Append(CurrentVersion);
            sb.Append("|dg=").Append(DungeonId);
            sb.Append("|lvl=").Append(Level.ToString(CultureInfo.InvariantCulture));
            sb.Append("|tier=").Append(Tier.ToString(CultureInfo.InvariantCulture));
            sb.Append("|fam=").Append(Family);
            sb.Append("|seed=").Append(Seed.ToString(CultureInfo.InvariantCulture));
            sb.Append("|mods=").Append(string.Join(",", Modifiers.Select(m => m.Id + ":" + m.Magnitude.ToString("0.##", CultureInfo.InvariantCulture))));
            sb.Append("|press=").Append(Presses.ToString(CultureInfo.InvariantCulture));
            sb.Append("|lock=").Append(string.Join(",", Locks));
            sb.Append("|load=").Append(string.Join(",", Load.Select(l => l.Wcid.ToString(CultureInfo.InvariantCulture) + ":" + l.Doses.ToString(CultureInfo.InvariantCulture))));
            if (Guide != null)
                sb.Append("|guide=").Append(Guide.Format());
            if (IsBound)
            {
                sb.Append("|run=").Append(RunId.ToString(CultureInfo.InvariantCulture));
                sb.Append("|owner=").Append(OwnerGuid.ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        public override string ToString() => Serialize();
    }
}
