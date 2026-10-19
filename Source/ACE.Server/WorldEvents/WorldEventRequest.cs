using System.Collections.Generic;

using ACE.Entity;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// Everything /worldevent start (WP-07) hands the manager: the axis ids to compose from, where the
    /// event is anchored, and the per-run timer overrides. Plain data, no behaviour.
    ///
    /// Either <see cref="AnchorId"/> (a loaded AnchorDef, P2) or <see cref="AnchorPosition"/> plus
    /// <see cref="AnchorLabel"/> (the "--here" path, P0/P1) must be present; the composer refuses otherwise.
    /// </summary>
    public class WorldEventRequest
    {
        public string SourceId { get; set; }

        /// <summary>
        /// The composed families, in slot order (two-family composition, 2026-08-29). One or two ids; the
        /// composer refuses three or more, and refuses a duplicate. Empty means "not pinned" - which is a
        /// refusal on the ordinary path ("missing --family") and the signal to ROLL a pair on the random
        /// path.
        ///
        /// Never null: the <see cref="FamilyId"/> proxy below writes through to this list.
        /// </summary>
        public List<string> FamilyIds { get; set; } = new List<string>();

        /// <summary>
        /// The FIRST composed family. A get/set proxy over <see cref="FamilyIds"/>[0], kept because a
        /// single-family request is still the common case and every caller and test that predates
        /// two-family composition sets one id. Setting a value replaces element 0 (leaving a second id
        /// alone); setting null or blank clears the whole list, since the only meaning that has is "no
        /// family pinned".
        /// </summary>
        public string FamilyId
        {
            get => FamilyIds != null && FamilyIds.Count > 0 ? FamilyIds[0] : null;

            set
            {
                FamilyIds ??= new List<string>();

                if (string.IsNullOrWhiteSpace(value))
                    FamilyIds.Clear();
                else if (FamilyIds.Count == 0)
                    FamilyIds.Add(value);
                else
                    FamilyIds[0] = value;
            }
        }

        public string BossId { get; set; }
        public string GoalId { get; set; }
        public string RewardId { get; set; }

        /// <summary>
        /// "/worldevent start random" (TECH-DESIGN 5.3). When set, <see cref="WorldEventComposer.TryCompose"/>
        /// fills in whichever of SourceId/FamilyId/GoalId/BossId this request left blank with a randomly
        /// chosen, mutually compatible pick before composing. Any axis already set here is honoured as a
        /// pin - "start random --source ambush" only ever rolls the rest.
        /// </summary>
        public bool Random { get; set; }

        public string AnchorId { get; set; }

        /// <summary>The "--here" anchor: the invoking admin's position.</summary>
        public Position AnchorPosition { get; set; }

        /// <summary>Display name for the "--here" anchor, used in announcement flavour text.</summary>
        public string AnchorLabel { get; set; }

        /// <summary>
        /// The synthetic anchor id an <see cref="AnchorPosition"/> run is logged under ("anchor=" in the
        /// fixed compose line). Null, the in-game value, means <see cref="WorldEventComposer.HereAnchorId"/>;
        /// the web Admin panel passes <see cref="WorldEventComposer.WebAnchorId"/> so a web-started run is
        /// identifiable from the log alone. Ignored when <see cref="AnchorId"/> names a loaded anchor.
        /// </summary>
        public string PositionAnchorId { get; set; }

        /// <summary>
        /// When non-null, the only source ids this request may compose (Docs/AdminPanel/WORLD-EVENTS-START.md).
        /// A random composition searches only these; an explicit source outside the set is refused. Null, the
        /// in-game value, allows every loaded source.
        /// </summary>
        public IReadOnlyCollection<string> AllowedSourceIds { get; set; }

        /// <summary>Who asked for this run, for the audit and log lines.</summary>
        public string Invoker { get; set; }

        /// <summary>Compose and report, stage nothing.</summary>
        public bool DryRun { get; set; }

        /// <summary>Seconds between the start announcement and the event going Active. 0 is allowed.</summary>
        public int AnnounceLeadSeconds { get; set; } = WorldEvent.DefaultAnnounceLeadSeconds;

        public int MaxDurationSeconds { get; set; } = WorldEvent.DefaultMaxDurationSeconds;

        /// <summary>
        /// The floor on how long the run lasts, from the moment it goes Active (TECH-DESIGN 2.15). Set by
        /// "--min-duration &lt;seconds&gt;". 0 disables the minimum for this run; anything at or above
        /// <see cref="MaxDurationSeconds"/> is refused at start, never clamped.
        /// </summary>
        public int MinDurationSeconds { get; set; } = WorldEvent.DefaultMinDurationSeconds;

        public int AbandonAfterSeconds { get; set; } = WorldEvent.DefaultAbandonAfterSeconds;

        public int WipeGraceSeconds { get; set; } = WorldEvent.DefaultWipeGraceSeconds;

        /// <summary>
        /// Seconds between the teaser broadcast and Stage(). null (the default) means "use the
        /// world_events_teaser_lead_seconds tunable"; 0 disables the teaser and stages immediately, which is
        /// exactly the pre-teaser behaviour.
        /// </summary>
        public int? TeaserLeadSeconds { get; set; }

        /// <summary>
        /// "--level n" (admin testing): when set, every audience sample treats the participants as level n,
        /// and an empty field as one such participant - see <see cref="WorldEvent.ApplyTestLevel"/>. Null (the
        /// default, and every web-panel start) uses the real players.
        /// </summary>
        public int? TestLevel { get; set; }

        /// <summary>
        /// "--wave-type t" (admin testing, Hold runs only): every wave that is not the finale is this special
        /// type (breach, runners, ranged, hunters), as long as the goal defines it. Null rolls normally.
        /// </summary>
        public ACE.Server.WorldEvents.Defs.HoldWaveType? ForcedWaveType { get; set; }

        /// <summary>World Events chain: which stage of a chain this run is (1 = a run nobody chained into).</summary>
        public int ChainStage { get; set; } = 1;

        /// <summary>World Events chain: the chain's length, carried from its first stage's goal (0 = not part of a chain).</summary>
        public int ChainMaxLength { get; set; }

        /// <summary>World Events chain: the first stage's run id (0 = this run is the first, or no chain).</summary>
        public uint ChainRootRunId { get; set; }

        /// <summary>World Events chain: the first stage's goal chain settings, carried to every later stage (null = the run's own goal's).</summary>
        public ACE.Server.WorldEvents.Defs.ChainDef ChainSettings { get; set; }
    }
}
