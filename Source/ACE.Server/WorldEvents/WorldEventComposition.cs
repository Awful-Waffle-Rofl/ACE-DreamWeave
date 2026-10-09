using System.Collections.Generic;
using System.Linq;

using ACE.Entity;
using ACE.Server.WorldEvents.Defs;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// One fully resolved set of composition axes (TECH-DESIGN 2.3). Effectively immutable: it is built
    /// once by <see cref="WorldEventComposer"/> and then read by the whole run.
    ///
    /// ONE exception, and it is not a general licence to mutate a composition: <see cref="PairingTier"/>
    /// and <see cref="PairingFloorLevel"/> are get/set, and <see cref="WorldEventComposer"/>'s random path
    /// writes them once, immediately after the draw that produced this instance succeeds and before the
    /// composition is handed back to its caller. They are never written again, and nothing outside the
    /// composer writes them at all. They are settable only because the composer is pure (TECH-DESIGN D6)
    /// and cannot log the pairing line itself - it does not know the run id - so the tier has to travel on
    /// the composition to WorldEventManager.TryStart, which logs it. By the time any run, command or test
    /// can observe a composition, every property here is final.
    ///
    /// <see cref="StoreSnapshot"/> is the axis store the composition was resolved against, held by
    /// reference on purpose - "/worldevent reload" swaps WorldEventManager.Store, and a run already in
    /// flight must keep the definitions it staged with.
    /// </summary>
    public sealed class WorldEventComposition
    {
        public SourceThemeDef Source { get; }

        /// <summary>
        /// The composed families in slot order - one, or two since two-family composition (2026-08-29).
        /// Each is a COPY of the store's family definition with Members filled in from the weenie-scan
        /// catalog, never the store's own instance, so filling a roster cannot mutate the shared store.
        ///
        /// Never null; empty only for the hand-built compositions some unit tests use.
        /// </summary>
        public IReadOnlyList<FamilyDef> Families { get; }

        /// <summary>
        /// The FIRST composed family, i.e. the one the pairing floor rule bound. Null when
        /// <see cref="Families"/> is empty. Prefer <see cref="Roster"/> for anything that draws monsters -
        /// this one deliberately does NOT see the second family's members.
        /// </summary>
        public FamilyDef Family => Families.Count > 0 ? Families[0] : null;

        /// <summary>
        /// What the run actually draws from: ONE synthetic FamilyDef over the UNION of every composed
        /// family's members, de-duplicated by wcid and sorted by (Level, Wcid) so it keeps the same
        /// ordering invariant a catalog family has. Id is the composed ids joined with "+"
        /// ("drudge+virindi"), DisplayName is <see cref="FamilyDisplayName"/>.
        ///
        /// Every per-slot draw goes through here rather than through one family at a time, which is the
        /// whole point of composing two: a wave's trash, elite and champion slots each pick from the
        /// combined roster, so the level coverage of the pair is what the band arithmetic sees.
        ///
        /// Null when <see cref="Families"/> is empty, matching what <see cref="Family"/> used to return in
        /// that case - callers already null-check it.
        ///
        /// HueKey and BiomeTags are deliberately left unset: they are per-family metadata with no defined
        /// meaning for a union, and nothing on the draw path reads them.
        /// </summary>
        public FamilyDef Roster { get; }

        public BossDef Boss { get; }
        public GoalDef Goal { get; }
        public RewardDef Reward { get; }

        /// <summary>
        /// The anchor definition. For the "--here" path this is synthesized from the request position with
        /// id "here" and displayName taken from the request's anchor label.
        /// </summary>
        public AnchorDef Anchor { get; }

        /// <summary>
        /// The resolved anchor position, including its landblock instance. AnchorDef carries no instance
        /// field, so a "--here" anchor would otherwise lose the realm/instance it was invoked in.
        /// </summary>
        public Position AnchorPosition { get; }

        public WorldEventAxisStore StoreSnapshot { get; }

        /// <summary>
        /// The goal name the run's announcements use: the SOURCE's goalDisplayName override when it has one
        /// AND the composed goal is DestroySource, otherwise the goal's own DisplayName (2026-08-19). This is
        /// what lets destroy_source read as "Shatter the Pillars" on an element portal and "Destroy the
        /// Rifts" on the rift theme, while the SAME element portal under kill_count/kill_boss reads as
        /// "Defend Against the Waves"/"Slay the Champion" - the override only makes sense for the goal it was
        /// authored to rename, never for an unrelated goal the source happens to also accept.
        /// </summary>
        public string GoalDisplayName =>
            Goal?.TypeKind == GoalType.DestroySource && !string.IsNullOrWhiteSpace(Source?.GoalDisplayName)
                ? Source.GoalDisplayName
                : Goal?.DisplayName;

        /// <summary>
        /// The Success outcome-line template the run's announcements use, same DestroySource-only source
        /// override rule as <see cref="GoalDisplayName"/> (2026-08-19). Null when neither the source (under
        /// DestroySource) nor the goal declares one - the caller then falls back to the legacy fixed shape.
        /// </summary>
        public string SuccessTemplate =>
            Goal?.TypeKind == GoalType.DestroySource && !string.IsNullOrWhiteSpace(Source?.SuccessTemplate)
                ? Source.SuccessTemplate
                : Goal?.SuccessTemplate;

        /// <summary>
        /// The Failed* outcome-line template the run's announcements use, same rule as
        /// <see cref="SuccessTemplate"/>.
        /// </summary>
        public string FailTemplate =>
            Goal?.TypeKind == GoalType.DestroySource && !string.IsNullOrWhiteSpace(Source?.FailTemplate)
                ? Source.FailTemplate
                : Goal?.FailTemplate;

        /// <summary>
        /// What one of this run's objective spawns is called - "pillar"/"pillars" on an element portal,
        /// "rift"/"rifts" everywhere else. Never null: the axis store repairs a blank to the default.
        /// </summary>
        public string ObjectiveNoun =>
            !string.IsNullOrWhiteSpace(Source?.ObjectiveNoun) ? Source.ObjectiveNoun : SourceThemeDef.DefaultObjectiveNoun;

        public string ObjectiveNounPlural =>
            !string.IsNullOrWhiteSpace(Source?.ObjectiveNounPlural) ? Source.ObjectiveNounPlural : SourceThemeDef.DefaultObjectiveNounPlural;

        /// <summary>
        /// How constrained the family pair this run drew was, and the floor it was drawn against. Set only
        /// on the RANDOM path (<see cref="WorldEventComposer"/>); null on every explicitly composed run,
        /// including an explicit "--family a,b", because an admin naming families bypasses the rule
        /// entirely and there is no tier to report.
        ///
        /// Carried here rather than logged where it is decided because the composer is pure (TECH-DESIGN
        /// D6) and does not know the run id; WorldEventManager.TryStart logs it once the run id exists.
        /// </summary>
        public WorldEventFamilyPairing.PairTier? PairingTier { get; set; }

        /// <summary>The floor level <see cref="PairingTier"/> was decided against. Meaningless when that is null.</summary>
        public int PairingFloorLevel { get; set; }

        /// <summary>
        /// The display names of every composed family, joined with " and " - "the Blackwing and the
        /// Drudges". This is what announcements name, in place of a single family's DisplayName. Null when
        /// no composed family has a display name at all, so an announcement that used to drop the family
        /// tag entirely still does.
        /// </summary>
        public string FamilyDisplayName
        {
            get
            {
                var names = Families
                    .Where(f => !string.IsNullOrWhiteSpace(f?.DisplayName))
                    .Select(f => f.DisplayName)
                    .ToList();

                return names.Count == 0 ? null : string.Join(" and ", names);
            }
        }

        public WorldEventComposition(SourceThemeDef source, IReadOnlyList<FamilyDef> families, BossDef boss,
            GoalDef goal, RewardDef reward, AnchorDef anchor, Position anchorPosition,
            WorldEventAxisStore storeSnapshot)
        {
            Source = source;
            Families = families == null
                ? new List<FamilyDef>()
                : families.Where(f => f != null).ToList();
            Roster = BuildRoster(Families, FamilyDisplayName);
            Boss = boss;
            Goal = goal;
            Reward = reward;
            Anchor = anchor;
            AnchorPosition = anchorPosition;
            StoreSnapshot = storeSnapshot;
        }

        /// <summary>
        /// The union roster, built once at construction. Members are de-duplicated by wcid, first family
        /// wins a shared wcid, and the result is sorted by (Level, Wcid) - the same ordering the catalog
        /// gives a single family, so nothing downstream can tell a union roster from a plain one.
        /// </summary>
        private static FamilyDef BuildRoster(IReadOnlyList<FamilyDef> families, string displayName)
        {
            if (families == null || families.Count == 0)
                return null;

            var members = new List<FamilyMember>();
            var seen = new HashSet<uint>();

            foreach (var family in families)
            {
                foreach (var member in family.Members ?? new List<FamilyMember>())
                {
                    if (member != null && seen.Add(member.Wcid))
                        members.Add(member);
                }
            }

            return new FamilyDef
            {
                Id = string.Join("+", families.Select(f => f.Id)),
                DisplayName = displayName,
                Members = members.OrderBy(m => m.Level).ThenBy(m => m.Wcid).ToList()
            };
        }

        /// <summary>
        /// The axis half of the compose log line, ids only (never display names). Shared by the run log
        /// line and by the dry-run line so the two can never drift.
        ///
        /// Two composed families render as ONE token in the SAME field, joined with "+"
        /// ("family=drudge+virindi"), so the fixed format declared in TECH-DESIGN 5.2 - which monitoring
        /// queries are written against - keeps its field count and field order.
        /// </summary>
        public string ToAxisSummary()
        {
            var families = string.Join("+", Families.Select(f => f?.Id));

            return $"source={Source?.Id} family={families} boss={Boss?.Id} goal={Goal?.Id} reward={Reward?.Id} anchor={Anchor?.Id}";
        }

        /// <summary>
        /// TECH-DESIGN 5.2, fixed format - monitoring queries are written against it:
        /// [WORLDEVENT] run=&lt;id&gt; compose source=&lt;s&gt; family=&lt;f&gt; boss=&lt;b&gt; goal=&lt;g&gt; reward=&lt;r&gt; anchor=&lt;a&gt;
        /// </summary>
        public string ToLogLine(uint runId)
        {
            return $"[WORLDEVENT] run={runId} compose {ToAxisSummary()}";
        }
    }
}
