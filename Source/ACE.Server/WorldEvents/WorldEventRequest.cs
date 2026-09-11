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
    }
}
