using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

using ACE.Entity.Enum;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.Managers;
using ACE.Server.WorldEvents;

using log4net;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// Immutable snapshot of Content/dungeons/dynamic/*.json. Modelled on WorldEventAxisStore: Parse is pure
    /// and testable, Load reads a folder, LoadFromServerConfig resolves the folder from PropertyManager.
    /// Every validation failure is a Diagnostics line; a STRUCTURALLY invalid dungeon (no spawn file, an
    /// off-landblock point, a duplicate id) is dropped, never half-loaded. The level-range lint at the end of
    /// that same loop is the exception and reports without dropping - see its own comment for why.
    /// </summary>
    public class ThreadDungeonStore
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        public IReadOnlyDictionary<string, DungeonEntryDef> Dungeons { get; }
        public IReadOnlyList<BossEntryDef> Bosses { get; }
        public IReadOnlyDictionary<string, ModifierDef> Modifiers => modifiers;

        /// <summary>
        /// The backing store for <see cref="Modifiers"/>. Held mutable for exactly one caller,
        /// <see cref="ValidateWorldWcids"/>, which can drop a salvage_affinity row whose base wcid does not
        /// resolve - a check Parse cannot make, because Parse is pure and the world database is not up when it
        /// runs. Nothing else may mutate it; the store is an immutable snapshot everywhere else.
        ///
        /// THAT MUTATION IS ONLY SAFE BECAUSE OF WHEN IT HAPPENS, and it is NOT once-at-world-start:
        /// ThreadDungeonManager.Reload runs it live from an admin command with runs in flight. What makes it
        /// safe is that both ThreadDungeonManager.Initialize and .Reload validate a LOCAL store and assign
        /// ThreadDungeonManager.Store only afterwards, so no other thread can hold a reference to this
        /// instance while the drop is happening. <see cref="Modifiers"/> is read unsynchronized from the world
        /// thread, from landblock action chains and from the appraisal path, and
        /// Dictionary&lt;TKey,TValue&gt; guarantees nothing under a concurrent write. Any future caller of
        /// ValidateWorldWcids must preserve that ordering, or make the drop return a new store instead.
        /// </summary>
        private readonly Dictionary<string, ModifierDef> modifiers;
        public IReadOnlyList<XpLadderRungDef> XpLadder { get; }
        public AttunementDef Attunement { get; }

        /// <summary>
        /// Measured physical clearance per dungeon, keyed by index.json dungeon id (clearance.json).
        ///
        /// A DUNGEON WITH NO ROW HERE READS AS "NO CONSTRAINT", NEVER AS "NOTHING FITS". That is the whole
        /// fail-open contract of the fit filter and it holds in four separate places: an absent row (this
        /// dictionary simply has no key), a row whose
        /// <see cref="DungeonClearanceEntryDef.MaxCollisionHeightFullAccess"/> is at or below zero, a setup
        /// with no collision spheres, and a server with no client dat loaded at all. Every one of them ends
        /// with every creature eligible, because a run that opens with no trash and no boss is a worse
        /// outcome than a creature standing somewhere it cannot path out of.
        ///
        /// Empty, never null - the store is an immutable snapshot and a missing or unreadable clearance.json
        /// is a diagnostic plus an empty dictionary, exactly as every other file here behaves.
        /// </summary>
        public IReadOnlyDictionary<string, DungeonClearanceEntryDef> Clearance { get; }

        public IReadOnlyList<string> Diagnostics { get; }

        public static readonly ThreadDungeonStore Empty = new ThreadDungeonStore(
            new Dictionary<string, DungeonEntryDef>(), new List<BossEntryDef>(), new Dictionary<string, ModifierDef>(),
            new List<XpLadderRungDef>(), AttunementDef.Empty, new Dictionary<string, DungeonClearanceEntryDef>(), new List<string>());

        private ThreadDungeonStore(Dictionary<string, DungeonEntryDef> dungeons, List<BossEntryDef> bosses,
            Dictionary<string, ModifierDef> modifiers, List<XpLadderRungDef> ladder, AttunementDef attunement,
            Dictionary<string, DungeonClearanceEntryDef> clearance, List<string> diagnostics)
        {
            Dungeons = dungeons;
            Bosses = bosses;
            this.modifiers = modifiers;
            XpLadder = ladder;
            Attunement = attunement;
            Clearance = clearance;
            Diagnostics = diagnostics;
        }

        public static bool TryParseLandblockHex(string hex, out ushort landblock)
        {
            landblock = 0;
            if (string.IsNullOrEmpty(hex)) return false;
            var s = hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex.Substring(2) : hex;
            return ushort.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out landblock);
        }

        /// <param name="dungeonJsonByStem">file stem ("0x0150") -> file text for every <0xLLLL>.json present</param>
        /// <param name="preDiagnostics">
        /// Diagnostics raised before Parse was reached - Load's file-read failures. Threaded in rather than
        /// merged afterwards so <see cref="Diagnostics"/> stays immutable once the store is built.
        /// </param>
        /// <param name="clearanceJson">
        /// Content/dungeons/dynamic/clearance.json. Trailing and optional, beside <paramref name="attunementJson"/>,
        /// so every call site that predates the fit filter keeps compiling - and null or empty is a legitimate
        /// input meaning "no dungeon has a measured clearance", which every consumer reads as "no constraint".
        /// </param>
        public static ThreadDungeonStore Parse(string indexJson, string bossesJson, string modifiersJson, IDictionary<string, string> dungeonJsonByStem,
            IEnumerable<string> preDiagnostics = null, string attunementJson = null, string clearanceJson = null)
        {
            dungeonJsonByStem ??= new Dictionary<string, string>();

            var diagnostics = preDiagnostics == null ? new List<string>() : new List<string>(preDiagnostics);
            var dungeons = new Dictionary<string, DungeonEntryDef>();
            var bosses = new List<BossEntryDef>();
            var modifiers = new Dictionary<string, ModifierDef>();
            var ladder = new List<XpLadderRungDef>();

            var index = Deserialize<DungeonIndexDef>(indexJson, "index.json", diagnostics) ?? new DungeonIndexDef();
            var bossList = Deserialize<BossListDef>(bossesJson, "bosses.json", diagnostics) ?? new BossListDef();
            var modList = Deserialize<ModifierListDef>(modifiersJson, "modifiers.json", diagnostics) ?? new ModifierListDef();

            // TECH-DESIGN S9.4: a file whose schema version this build does not know is a diagnostic and is
            // treated as EMPTY, never half-read. A future writer that changes the meaning of a field would
            // otherwise be silently misinterpreted by an older server.
            if (!VersionSupported(index.Version, "index.json", diagnostics)) index = new DungeonIndexDef();
            if (!VersionSupported(bossList.Version, "bosses.json", diagnostics)) bossList = new BossListDef();
            if (!VersionSupported(modList.Version, "modifiers.json", diagnostics)) modList = new ModifierListDef();

            // System.Text.Json overwrites a "= new List<T>()" initialiser with null when the JSON
            // explicitly says "field": null. Normalise every nullable list root right after
            // deserialization so nothing downstream ever has to null-check them.
            index.Dungeons ??= new List<DungeonEntryDef>();
            bossList.Bosses ??= new List<BossEntryDef>();
            modList.Modifiers ??= new List<ModifierDef>();
            modList.XpLadder ??= new List<XpLadderRungDef>();

            foreach (var b in bossList.Bosses)
                b.Modifiers ??= new List<string>();

            foreach (var d in index.Dungeons)
            {
                d.Families ??= new List<string>();
                d.CreatureTypes ??= new List<string>();
            }

            foreach (var m in modList.Modifiers)
            {
                if (string.IsNullOrEmpty(m.Id) || !m.Id.All(c => char.IsLower(c) || char.IsDigit(c) || c == '_'))
                { diagnostics.Add($"modifiers.json: modifier id '{m.Id}' must match [a-z0-9_]+"); continue; }
                if (modifiers.ContainsKey(m.Id))
                { diagnostics.Add($"modifiers.json: duplicate modifier id '{m.Id}'"); continue; }
                if (m.Target != "monster" && m.Target != "boss" && m.Target != "run")
                { diagnostics.Add($"modifiers.json: modifier '{m.Id}' target must be monster|boss|run"); continue; }
                if (m.MinMagnitude > m.MaxMagnitude)
                { diagnostics.Add($"modifiers.json: modifier '{m.Id}' minMagnitude > maxMagnitude"); continue; }

                // A salvage_affinity row carries BOTH halves of what it drops, so nothing downstream has to
                // infer whether a material is a gem or a stone. Both are checked here; the third half - that
                // the wcid names a weenie that actually exists - needs the world database and so lives in
                // ValidateWorldWcids.
                if (m.MonsterEffectKind == DungeonRewardMath.SalvageAffinity)
                {
                    if (!IsSalvageMaterial(m.SalvageMaterial))
                    { diagnostics.Add($"modifiers.json: modifier '{m.Id}' salvageMaterial {m.SalvageMaterial} is not a defined MaterialType"); continue; }
                    if (m.SalvageBaseWcid == 0)
                    { diagnostics.Add($"modifiers.json: modifier '{m.Id}' salvageBaseWcid must be non-zero"); continue; }
                }

                // Rule 24 (draw weights): a drawWeight that is negative, NaN or infinite is CORRECTED to the
                // 1.0 default with a diagnostic rather than dropping the row, in the same spirit as rule 22.
                // A tuning value is not worth losing a modifier over, and the two failure modes it prevents
                // are both silent: a negative weight would subtract from the running total inside the
                // weighted pick and could make a whole pool read as degenerate, and a NaN or an infinity
                // would poison the total for every OTHER row in the same pool. There is deliberately no
                // upper clamp - a weight above 1.0 is the legitimate way to make a modifier commoner.
                //
                // Reachability, measured 2026-09-08 against these JsonOptions: a negative reaches here as
                // written, and an overflowing literal ("1e400") reaches here as Infinity. A bare NaN token
                // does NOT - System.Text.Json rejects it without AllowNamedFloatingPointLiterals and the
                // whole modifiers.json fails to deserialize with its own diagnostic, which is louder still.
                // The NaN arm is kept as a guard on the value rather than on the token, because a weight that
                // is NaN would poison the running total for every OTHER row in the same pool.
                //
                // Formatted through InvariantCulture on purpose: the current culture renders an infinity as
                // the non-ASCII "infinity" glyph, and a diagnostic line is text this repo keeps ASCII.
                if (double.IsNaN(m.DrawWeight) || double.IsInfinity(m.DrawWeight) || m.DrawWeight < 0.0)
                {
                    diagnostics.Add($"modifiers.json: modifier '{m.Id}' drawWeight {m.DrawWeight.ToString(CultureInfo.InvariantCulture)} must be a finite number >= 0; using 1.0");
                    m.DrawWeight = 1.0;
                }

                modifiers[m.Id] = m;
            }

            // Parsed AFTER the modifier dictionary above, because lint rule 13 cross-references it.
            var attunement = ParseAttunement(attunementJson, modifiers, diagnostics);

            for (var i = 0; i < modList.XpLadder.Count; i++)
            {
                var rung = modList.XpLadder[i];
                if (rung.Level <= 0 || rung.Xp <= 0 || (i > 0 && rung.Level <= modList.XpLadder[i - 1].Level))
                { diagnostics.Add($"modifiers.json: xpLadder must be ascending in level with positive xp (rung {i})"); ladder.Clear(); break; }
                ladder.Add(rung);
            }

            foreach (var b in bossList.Bosses)
            {
                if (b.Wcid == 0) { diagnostics.Add("bosses.json: boss with wcid 0"); continue; }
                foreach (var modId in b.Modifiers)
                    if (!modifiers.ContainsKey(modId))
                        diagnostics.Add($"bosses.json: boss {b.Wcid} names unknown modifier '{modId}'");
                bosses.Add(b);
            }

            foreach (var d in index.Dungeons)
            {
                if (string.IsNullOrEmpty(d.Id)) { diagnostics.Add("index.json: dungeon with empty id"); continue; }
                if (!TryParseLandblockHex(d.LandblockHex, out var lb))
                { diagnostics.Add($"index.json: dungeon '{d.Id}' has unparseable landblock '{d.LandblockHex}'"); continue; }
                d.Landblock = lb;
                var stem = $"0x{lb:X4}";
                if (!dungeonJsonByStem.TryGetValue(stem, out var fileText))
                { diagnostics.Add($"index.json: dungeon '{d.Id}' has no spawn file {stem}.json"); continue; }
                var spawnFile = Deserialize<DungeonSpawnFileDef>(fileText, stem + ".json", diagnostics);
                if (spawnFile == null) continue;
                if (!VersionSupported(spawnFile.Version, stem + ".json", diagnostics)) continue;
                spawnFile.Points ??= new List<DungeonSpawnPointDef>();
                if (spawnFile.Points.Count == 0)
                { diagnostics.Add($"{stem}.json: no points"); continue; }
                if (spawnFile.BossAnchor == null)
                { diagnostics.Add($"{stem}.json: no bossAnchor"); continue; }
                // The boss anchor is checked against the landblock alongside the points: it is placed by the
                // same spawner and an off-landblock anchor would put the boss outside the copy just as surely.
                if (spawnFile.Points.Any(p => (p.Cell >> 16) != lb) || (spawnFile.BossAnchor.Cell >> 16) != lb)
                { diagnostics.Add($"{stem}.json: a point or the boss anchor is not on landblock {stem}"); continue; }
                if (d.ExitPortalWcid == 0)
                { diagnostics.Add($"index.json: dungeon '{d.Id}' has exitPortalWcid 0"); continue; }
                if (dungeons.ContainsKey(d.Id))
                { diagnostics.Add($"index.json: duplicate dungeon id '{d.Id}'"); continue; }

                // LEVEL-RANGE LINT. Neither of these DROPS the dungeon, unlike every check above: a level
                // range is a tuning value, and taking a whole dungeon offline over one would be a worse
                // outcome than the fault being reported. Both are diagnostics only.
                //
                // Why the ceiling check exists at all: ThreadDungeonGemHandler.cs:252-269 picks an "any" gem's
                // dungeon by filtering on [MinLevel, MaxLevel], so a roster that caps below the gem ceiling
                // leaves a top-rung gem with no candidates and the ONE player-visible symptom is the single
                // chat line "No dungeon answers this gem's call right now." Nothing else says why. Forgetting
                // to raise these alongside DungeonGemSpec.MaxLevel was therefore silent until this check.
                //
                // Enabled entries only, because a disabled dungeon cannot strand a gem - it is out of the
                // candidate pool either way, and an author parking one at an old ceiling should not be nagged.
                if (d.Enabled && d.MaxLevel < DungeonGemSpec.MaxLevel)
                    diagnostics.Add($"index.json: dungeon '{d.Id}' maxLevel {d.MaxLevel} is below the gem level ceiling {DungeonGemSpec.MaxLevel}; gems above {d.MaxLevel} can never roll it");

                // The inverted-range check is NOT gated on Enabled: an inverted range is a plain authoring
                // error rather than a deliberate cap, and it is worth naming even on a parked entry.
                if (d.MinLevel > d.MaxLevel)
                    diagnostics.Add($"index.json: dungeon '{d.Id}' minLevel {d.MinLevel} exceeds maxLevel {d.MaxLevel}; no gem level can roll it");

                d.Entry = spawnFile.Entry;
                d.Points = spawnFile.Points;
                d.BossAnchor = spawnFile.BossAnchor;
                dungeons[d.Id] = d;
            }

            // Parsed LAST, because both of its cross-checks read the dungeon dictionary the loop above built.
            var clearance = ParseClearance(clearanceJson, dungeons, diagnostics);

            return new ThreadDungeonStore(dungeons, bosses, modifiers, ladder, attunement, clearance, diagnostics);
        }

        /// <summary>
        /// clearance.json -> dungeon id -> measured clearance. EVERY failure here is NON-FATAL and ends with
        /// fewer rows, never with a run refused: a dungeon that reaches the fit filter with no row is treated
        /// as having no height constraint at all, so a missing, unreadable, unsupported-version or entirely
        /// malformed clearance.json degrades to exactly today's behaviour rather than to an empty dungeon.
        ///
        /// Two diagnostics are pure cross-checks against index.json and drop nothing:
        ///   - a clearance row whose id is not an index.json dungeon (a stale row, or a typo), and
        ///   - a row whose landblock disagrees with the index entry's (the two files describe different
        ///     geometry, so at least one of them was measured against the wrong map).
        ///
        /// A row with an empty id, or a duplicate id, IS dropped - it cannot be keyed, and a duplicate would
        /// otherwise silently pick a winner.
        /// </summary>
        private static Dictionary<string, DungeonClearanceEntryDef> ParseClearance(string clearanceJson,
            Dictionary<string, DungeonEntryDef> dungeons, List<string> diagnostics)
        {
            var clearance = new Dictionary<string, DungeonClearanceEntryDef>();

            // Missing file / empty text is NOT a diagnostic worth raising the way attunement.json's is: a
            // server with no clearance table is a supported configuration (the fit filter simply constrains
            // nothing), whereas an empty attunement table means the press has no ingredients.
            if (string.IsNullOrWhiteSpace(clearanceJson))
                return clearance;

            var file = Deserialize<DungeonClearanceFileDef>(clearanceJson, "clearance.json", diagnostics);

            if (file == null)
                return clearance;

            // Same TECH-DESIGN S9.4 rule the other four files follow: an unknown schema version is a
            // diagnostic and the file is treated as EMPTY, never half-read.
            if (!VersionSupported(file.Version, "clearance.json", diagnostics))
                return clearance;

            // System.Text.Json overwrites a "= new List<T>()" initialiser with null when the JSON explicitly
            // says "field": null, exactly as it does for index/bosses/modifiers above.
            file.Dungeons ??= new List<DungeonClearanceEntryDef>();

            foreach (var c in file.Dungeons)
            {
                if (c == null)
                    continue;

                c.Envelope ??= new List<DungeonClearanceEnvelopeDef>();

                if (string.IsNullOrEmpty(c.Id))
                { diagnostics.Add("clearance.json: clearance row with empty id"); continue; }

                if (clearance.ContainsKey(c.Id))
                { diagnostics.Add($"clearance.json: duplicate clearance row id '{c.Id}'"); continue; }

                if (!dungeons.TryGetValue(c.Id, out var indexed))
                {
                    diagnostics.Add($"clearance.json: row '{c.Id}' names no dungeon in index.json; it constrains nothing");
                }
                else if (TryParseLandblockHex(c.LandblockHex, out var lb) && lb != indexed.Landblock)
                {
                    diagnostics.Add($"clearance.json: row '{c.Id}' landblock {c.LandblockHex} disagrees with index.json's 0x{indexed.Landblock:X4}; " +
                                    "one of the two was measured against the wrong map");
                }

                clearance[c.Id] = c;
            }

            return clearance;
        }

        /// <summary>
        /// Lint rules 1-23 for attunement.json (PHASE-2-IMPLEMENTATION-PLAN.md task D2). Every failure is a
        /// Diagnostics line; an invalid entry is dropped and never half-loaded, mirroring the modifier loop above.
        ///
        /// Rules 19-22 arrived with press v2 (owner ruling, 2026-09-07): 19 the band edges, 20 the op scope,
        /// 21 set_difficulty's entry count, and 22 the type's load limit against the press board's slot count.
        /// 22 is the one rule that CORRECTS rather than drops, and says why at its own site.
        ///
        /// Rule 23 arrived with powder aiming (owner ruling, 2026-09-07): only a powder may reach a salvage
        /// affinity. See its own site for why the invariant needed writing down rather than assuming.
        ///
        /// Rule 24 is NOT an attunement.json rule and is not applied here: it polices modifiers.json's
        /// drawWeight and lives in the modifier loop in <see cref="Parse"/>. The number is taken out of the
        /// same sequence anyway so no two rules ever share one, since the rule numbers are cited by test
        /// method names.
        ///
        /// Three rules were RETIRED with the instability mechanic (owner ruling, 2026-09-07) and their numbers
        /// are deliberately left unfilled rather than renumbering the survivors, because the rule numbers are
        /// cited in the plan doc and in AttunementStoreTests' method names: rule 9 (component instability in
        /// -100..100), rule 17 (a wild op that is forbidden or carries a modifier arg) and rule 18 (the wild
        /// list is empty after filtering). The fields those rules policed no longer exist on the def types, so
        /// a file still carrying them is simply not read.
        /// </summary>
        private static AttunementDef ParseAttunement(string attunementJson, IReadOnlyDictionary<string, ModifierDef> modifiers, List<string> diagnostics)
        {
            // Rule 2: missing file / empty text -> "attunement.json: empty" and an empty AttunementDef. Not
            // fatal - the press then refuses everything.
            if (string.IsNullOrWhiteSpace(attunementJson))
            {
                diagnostics.Add("attunement.json: empty");
                return AttunementDef.Empty;
            }

            var file = Deserialize<AttunementFileDef>(attunementJson, "attunement.json", diagnostics);
            if (file == null)
                return AttunementDef.Empty;

            // Rule 1: version != SupportedVersion -> whole file ignored.
            if (!VersionSupported(file.Version, "attunement.json", diagnostics))
                return AttunementDef.Empty;

            file.Colors ??= new List<ColorMapDef>();
            file.Components ??= new List<ComponentDef>();

            // The six press v2 slot types (owner ruling, 2026-09-07). "glyph", "ink" and "quill" retired with
            // that ruling; a file still carrying one now trips rule 5 and the component is dropped loudly.
            var validTypes = new HashSet<string>(RawFragmentRules.SlotCounts.Keys);
            var components = new Dictionary<uint, ComponentDef>();
            var typeLimits = new Dictionary<string, (int Limit, uint Wcid)>();

            foreach (var c in file.Components)
            {
                c.Ops ??= new List<OpDef>();

                // Rule 3.
                if (c.Wcid == 0)
                { diagnostics.Add("attunement.json: component with wcid 0"); continue; }

                // Rule 4: duplicate wcid -> drop the later entry.
                if (components.ContainsKey(c.Wcid))
                { diagnostics.Add($"attunement.json: duplicate component wcid {c.Wcid}"); continue; }

                // Rule 5.
                if (string.IsNullOrEmpty(c.Type) || !validTypes.Contains(c.Type))
                { diagnostics.Add($"attunement.json: component {c.Wcid} has unknown type '{c.Type}'"); continue; }

                // Rule 6: the Pressed: line needs it.
                if (string.IsNullOrWhiteSpace(c.Name))
                { diagnostics.Add($"attunement.json: component {c.Wcid} has no name"); continue; }

                // Rule 7.
                if (c.Dose < 1)
                { diagnostics.Add($"attunement.json: component {c.Wcid} dose must be >= 1"); continue; }

                // Rule 8. The upper bound is DungeonGemSpec.MaxDoses: a type limit above it would let
                // RawFragmentRules.Load build a load entry that Serialize writes and TryParse then refuses.
                if (c.Limit < 1)
                { diagnostics.Add($"attunement.json: component {c.Wcid} limit must be >= 1"); continue; }

                if (c.Limit > DungeonGemSpec.MaxDoses)
                { diagnostics.Add($"attunement.json: component {c.Wcid} limit must be <= {DungeonGemSpec.MaxDoses}"); continue; }

                // Rule 9 retired: see the method summary.

                // Rule 10.
                if (c.Ops.Count == 0)
                { diagnostics.Add($"attunement.json: component {c.Wcid} has no ops"); continue; }

                var validOps = new List<OpDef>();
                var dropEntry = false;

                foreach (var op in c.Ops)
                {
                    // Rule 11: an op whose "op" is not known drops the ENTRY, not just the op - a
                    // half-applied component is worse than a refused one.
                    if (string.IsNullOrEmpty(op.Op) || !AttunementOps.Known.Contains(op.Op))
                    { diagnostics.Add($"attunement.json: component {c.Wcid} has unknown op '{op.Op}'"); dropEntry = true; break; }

                    // Rule 12.
                    if (AttunementOps.RequireModifierArg.Contains(op.Op) && string.IsNullOrEmpty(op.Modifier))
                    { diagnostics.Add($"attunement.json: component {c.Wcid} op '{op.Op}' requires a modifier"); dropEntry = true; break; }

                    // Rule 13.
                    if (!string.IsNullOrEmpty(op.Modifier) && !modifiers.ContainsKey(op.Modifier))
                    { diagnostics.Add($"attunement.json: component {c.Wcid} op '{op.Op}' names unknown modifier '{op.Modifier}'"); dropEntry = true; break; }

                    // Rule 19 (press v2): a band edge outside [0, 1], or NaN, or an inverted pair. Bands are
                    // FRACTIONS of the modifier's range, so a value of "25" meaning a magnitude is the
                    // expected authoring mistake and it must be loud - the roll would otherwise clamp to the
                    // modifier's maximum and the component would silently be the strongest in the file.
                    if (double.IsNaN(op.BandMin) || double.IsNaN(op.BandMax)
                        || op.BandMin < 0.0 || op.BandMin > 1.0 || op.BandMax < 0.0 || op.BandMax > 1.0
                        || op.BandMin > op.BandMax)
                    {
                        diagnostics.Add($"attunement.json: component {c.Wcid} op '{op.Op}' band [{op.BandMin}, {op.BandMax}] must be two fractions in [0, 1] with bandMin <= bandMax");
                        dropEntry = true;
                        break;
                    }

                    // Rule 20 (press v2): an unknown scope would silently read as "any" and let a component
                    // reach modifiers its design never intended.
                    if (!string.IsNullOrEmpty(op.Scope) && !AttunementScopes.Known.Contains(op.Scope))
                    { diagnostics.Add($"attunement.json: component {c.Wcid} op '{op.Op}' has unknown scope '{op.Scope}'"); dropEntry = true; break; }

                    // Rule 21 (press v2): set_difficulty's entry count, when it carries one, has to be a
                    // count the gem can actually hold. 0 is legal and means "leave the entry count alone".
                    if (op.Entries < 0 || op.Entries > RawFragmentRules.MaxEntries)
                    { diagnostics.Add($"attunement.json: component {c.Wcid} op '{op.Op}' entries {op.Entries} must be 0..{RawFragmentRules.MaxEntries}"); dropEntry = true; break; }

                    // Rule 23 (powder aiming, owner ruling 2026-09-07): ONLY A POWDER MAY REACH A SALVAGE
                    // AFFINITY. This encodes what the slot board already means - the powder slot IS the
                    // salvage slot, with its own budget (RawFragmentRules.Budget.Salvage) - as a content rule
                    // rather than as a convention nobody checks.
                    //
                    // It exists because the naming exception and the load limit key on DIFFERENT things, and
                    // that gap was real (caught in review, 2026-09-07). CanLoad refuses a second powder by
                    // component TYPE, while DungeonModifierCategories.IsNameable keys on the modifier's
                    // effect KIND, so a taper row pointing at affinity_iron would have loaded happily beside
                    // a powder and produced two named affinities on a board with one salvage budget.
                    //
                    // BOTH routes to an affinity are covered, or the rule would not be worth having: naming
                    // one outright, and drawing one through the salvage scope. Drops the OP rather than the
                    // entry, matching rule 14's op half - the rest of the component is still coherent, and a
                    // component left with no ops is then caught by rule 14's entry half.
                    var reachesAffinity =
                        (!string.IsNullOrEmpty(op.Modifier) && modifiers.TryGetValue(op.Modifier, out var opMod)
                            && DungeonModifierCategories.IsSalvageAffinity(opMod))
                            ? $"modifier '{op.Modifier}'"
                            : op.Scope == AttunementScopes.Salvage ? "scope 'salvage'" : null;

                    if (reachesAffinity != null && c.Type != RawFragmentRules.PowderType)
                    {
                        diagnostics.Add($"attunement.json: component {c.Wcid} op '{op.Op}' reaches a salvage affinity ({reachesAffinity}) but its type is '{c.Type}', not '{RawFragmentRules.PowderType}'; the powder slot is the salvage slot");
                        continue;
                    }

                    // Rule 14 (op half): a non-positive weight drops the OP, not the entry.
                    if (op.Weight <= 0)
                    { diagnostics.Add($"attunement.json: component {c.Wcid} op '{op.Op}' has weight <= 0"); continue; }

                    validOps.Add(op);
                }

                if (dropEntry)
                    continue;

                // Rule 14 (entry half): if every op is filtered out, drop the entry.
                if (validOps.Count == 0)
                { diagnostics.Add($"attunement.json: component {c.Wcid} has no ops left after filtering"); continue; }

                c.Ops = validOps;

                // Rule 16: two entries of the same type with a different limit - diagnostic naming both
                // wcids; the FIRST entry's limit wins.
                if (typeLimits.TryGetValue(c.Type, out var seen))
                {
                    if (seen.Limit != c.Limit)
                    {
                        diagnostics.Add($"attunement.json: component {c.Wcid} limit {c.Limit} disagrees with component {seen.Wcid} limit {seen.Limit} for type '{c.Type}'; using {seen.Limit}");
                        c.Limit = seen.Limit;
                    }
                }
                else
                {
                    typeLimits[c.Type] = (c.Limit, c.Wcid);
                }

                // Rule 22 (press v2): a type's load limit IS its slot count. Diagnostic and CORRECTED rather
                // than dropped, because either direction of mismatch is a silent gameplay bug rather than an
                // unusable component: a limit above the slot count lets a player load doses that no slot
                // will ever resolve, and one below it stops them filling a slot the board offers. Ordered
                // after rule 16 on purpose, so a genuine authoring disagreement between two entries of the
                // same type is still reported on its own terms before this corrects the survivor.
                var slots = RawFragmentRules.SlotCounts[c.Type];

                if (c.Limit != slots)
                {
                    diagnostics.Add($"attunement.json: component {c.Wcid} limit {c.Limit} does not match the {slots} '{c.Type}' slot(s) on the press board; using {slots}");
                    c.Limit = slots;
                }

                components[c.Wcid] = c;
            }

            var colorModifier = new Dictionary<string, string>();
            foreach (var row in file.Colors)
            {
                // Rule 15: a colour whose modifier is non-null and unknown - diagnostic, colour dropped.
                if (!string.IsNullOrEmpty(row.Modifier) && !modifiers.ContainsKey(row.Modifier))
                { diagnostics.Add($"attunement.json: colour '{row.Color}' names unknown modifier '{row.Modifier}'"); continue; }

                if (!string.IsNullOrEmpty(row.Modifier))
                    colorModifier[row.Color] = row.Modifier;
            }

            // Rules 17 and 18 retired with the wild list: see the method summary.

            return new AttunementDef(components, colorModifier);
        }

        /// <summary>The only schema version this build reads (TECH-DESIGN S9.4).</summary>
        public const int SupportedVersion = 1;

        /// <summary>
        /// True when <paramref name="value"/> names a real, usable MaterialType.
        ///
        /// Zero is rejected explicitly: MaterialType.Unknown IS a defined member, so Enum.IsDefined alone
        /// would wave an unset field straight through, and an item stamped Unknown appraises as nothing and
        /// salvages into nothing.
        ///
        /// The range check in front is not redundant. MaterialType's underlying type is UINT, and the
        /// non-generic Enum.IsDefined THROWS ArgumentException when handed a boxed value of any other type -
        /// hence the explicit (uint) cast rather than passing the int through.
        /// </summary>
        internal static bool IsSalvageMaterial(int value)
        {
            if (value <= 0 || value > (int)MaterialType.Teak)
                return false;

            return Enum.IsDefined(typeof(MaterialType), (uint)value);
        }

        private static bool VersionSupported(int version, string fileName, List<string> diagnostics)
        {
            if (version == SupportedVersion)
                return true;

            diagnostics.Add($"{fileName}: unsupported version {version} (this server reads version {SupportedVersion}); file ignored");
            return false;
        }

        private static T Deserialize<T>(string json, string fileName, List<string> diagnostics) where T : class
        {
            if (string.IsNullOrWhiteSpace(json)) { diagnostics.Add($"{fileName}: empty"); return null; }
            try { return JsonSerializer.Deserialize<T>(json, JsonOptions); }
            catch (JsonException ex) { diagnostics.Add($"{fileName}: {ex.Message}"); return null; }
        }

        /// <summary>
        /// Reads the folder. TECH-DESIGN S9 invariant 5: a diagnostic and an empty store, never a throw -
        /// Initialize() runs from Program.Main outside any try (Program.cs:434), so an unreadable file here
        /// would abort server startup for a feature that ships disabled, and /dd reload would rethrow the
        /// same. Modelled on WorldEventAxisStore.Load.
        /// </summary>
        public static ThreadDungeonStore Load(string folder)
        {
            var diagnostics = new List<string>();

            string ReadOrEmpty(string name)
            {
                var path = Path.Combine(folder, name);

                if (!File.Exists(path))
                    return "";

                try
                {
                    return File.ReadAllText(path);
                }
                catch (Exception ex)
                {
                    diagnostics.Add($"{name}: could not read - {ex.Message}");
                    return "";
                }
            }

            var byStem = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string[] files;

            try
            {
                files = Directory.GetFiles(folder, "0x*.json");
            }
            catch (Exception ex)
            {
                diagnostics.Add($"{folder}: could not enumerate - {ex.Message}");
                files = new string[0];
            }

            foreach (var path in files)
            {
                var stem = Path.GetFileNameWithoutExtension(path);

                if (byStem.ContainsKey(stem))
                {
                    diagnostics.Add($"{stem}.json: duplicate spawn file '{path}' ignored");
                    continue;
                }

                try
                {
                    byStem[stem] = File.ReadAllText(path);
                }
                catch (Exception ex)
                {
                    diagnostics.Add($"{stem}.json: could not read - {ex.Message}");
                }
            }

            return Parse(ReadOrEmpty("index.json"), ReadOrEmpty("bosses.json"), ReadOrEmpty("modifiers.json"), byStem, diagnostics,
                attunementJson: ReadOrEmpty("attunement.json"), clearanceJson: ReadOrEmpty("clearance.json"));
        }

        /// <summary>
        /// The wcid checks Parse cannot make, because Parse is pure and the world database is not up when it
        /// runs. Call once the world database is available; returns one diagnostic string per problem, for
        /// the caller to log at WARN (mirrors ThreadDungeonManager.ValidateBossWeenies).
        ///
        /// Two kinds, and they end differently. An attunement component whose wcid does not resolve is
        /// REPORTED and left in place - it is a press ingredient, and dropping it would silently change what a
        /// player's fragment can be pressed with. A salvage_affinity modifier whose base wcid does not resolve
        /// is reported AND DROPPED: it can only ever produce a null from WorldObjectFactory at the death path,
        /// so every kill under it would pay a modifier the gem advertised and the corpse never carried.
        ///
        /// CALL THIS BEFORE PUBLISHING THE STORE, never after. The drop mutates the dictionary behind
        /// <see cref="Modifiers"/>, which is read unsynchronized from several threads once
        /// ThreadDungeonManager.Store points at this instance; see the remarks on the backing field.
        /// </summary>
        public IReadOnlyList<string> ValidateWorldWcids(Func<uint, bool> weenieExists)
        {
            var problems = new List<string>();

            foreach (var kv in Attunement?.Components ?? (IReadOnlyDictionary<uint, ComponentDef>)new Dictionary<uint, ComponentDef>())
            {
                if (!weenieExists(kv.Key))
                    problems.Add($"attunement.json: component wcid {kv.Key} '{kv.Value.Name}': no such weenie in the world database");
            }

            foreach (var id in modifiers.Where(kv => kv.Value.MonsterEffectKind == DungeonRewardMath.SalvageAffinity
                                                  && !weenieExists(kv.Value.SalvageBaseWcid))
                                        .Select(kv => kv.Key).ToList())
            {
                problems.Add($"modifiers.json: modifier '{id}' salvageBaseWcid {modifiers[id].SalvageBaseWcid}: no such weenie in the world database; modifier dropped");
                modifiers.Remove(id);
            }

            return problems;
        }

        public static ThreadDungeonStore LoadFromServerConfig()
        {
            var overrideFolder = PropertyManager.GetString("dynamic_dungeons_data_folder").Item;
            var contentFolder = PropertyManager.GetString("content_folder").Item;
            var exeFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            var folder = WorldEventAxisStore.ResolveFolder(overrideFolder, contentFolder, exeFolder, new[] { "dungeons", "dynamic" }, Directory.Exists, out var reason);

            if (folder == null)
            {
                log.Warn("[DYNDUNGEON] no data folder found; store is empty");
                return Empty;
            }

            var store = Load(folder);
            log.Info($"[DYNDUNGEON] loaded {store.Dungeons.Count} dungeons, {store.Bosses.Count} bosses, {store.Modifiers.Count} modifiers from {folder} ({reason})");
            foreach (var d in store.Diagnostics)
                log.Warn($"[DYNDUNGEON] {d}");
            return store;
        }
    }
}
