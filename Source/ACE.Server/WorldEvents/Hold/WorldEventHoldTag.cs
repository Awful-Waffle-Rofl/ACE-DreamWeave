using ACE.Server.WorldEvents.Defs;

namespace ACE.Server.WorldEvents.Hold
{
    /// <summary>What a Hold creature is to its run.</summary>
    public enum HoldUnitKind
    {
        /// <summary>A protected target (a knight / envoy). The run fails when too few of them stand.</summary>
        Knight,

        /// <summary>A wave monster.</summary>
        Monster,

        /// <summary>The finale boss that leads the last counted wave.</summary>
        FinaleBoss
    }

    /// <summary>
    /// The role a Hold wave monster is tuned by (world_events_hold_&lt;role&gt;_*). Melee covers the breach squad, the
    /// seekers and the hunters; Caster is a ranged monster whose ranged attack is spells (no launcher or thrown weapon).
    /// None is every creature that is not a tuned wave monster (a knight, the finale boss).
    /// </summary>
    public enum HoldDamageRole { None, Melee, Runner, Ranged, Caster, Corrupted }

    /// <summary>
    /// The one immutable tag a Hold creature carries (Creature.WorldEventHold). Built by the spawner and set exactly once,
    /// before EnterWorld, with Creature.SetWorldEventHoldTag; never changed afterwards and never persisted.
    ///
    /// THREADING: every member is fixed at construction (init-only), so the tag is safe to read from any thread. Anything a
    /// creature needs that changes over its life is per-creature state touched only on its own landblock thread, or run
    /// state read through <see cref="Run"/>.
    ///
    /// IDENTITY: "is a Hold knight" is Kind == Knight; "same run" is attacker.WorldEventHold.RunId ==
    /// knight.WorldEventHold.RunId and Run.IsActive. Never compare the creature's P_WorldEvent back-reference, and never read
    /// PropertyInt.WorldEventId in engine files.
    /// </summary>
    public sealed class WorldEventHoldTag
    {
        /// <summary>The owning run's id (WorldEvent.RunId).</summary>
        public uint RunId { get; init; }

        public HoldUnitKind Kind { get; init; }

        /// <summary>The job a wave monster has in its wave (protect.waveMix). Seeker for every non-monster.</summary>
        public HoldRole WaveRole { get; init; }

        /// <summary>The settings role it is tuned by. None for a knight and the finale boss.</summary>
        public HoldDamageRole Role { get; init; }

        /// <summary>Goes for the knights.</summary>
        public bool Seeker { get; init; }

        /// <summary>Goes for players, a recent healer first.</summary>
        public bool Hunter { get; init; }

        /// <summary>A marked priority target that empowers the wave monsters near it.</summary>
        public bool Corrupted { get; init; }

        /// <summary>Carries the ranged ward (protect.ward).</summary>
        public bool Warded { get; init; }

        /// <summary>The one knight this monster must go for (breach squad, corrupted assault), or 0 for none.</summary>
        public uint ForcedTargetGuid { get; init; }

        /// <summary>A knight this monster must not pick (spreads the seekers), or 0 for none.</summary>
        public uint AvoidTargetGuid { get; init; }

        /// <summary>Run speed multiplier (runners). 1 = unchanged.</summary>
        public float RunRateMult { get; init; } = 1f;

        /// <summary>Melee swing speed multiplier. 1 = unchanged.</summary>
        public float AttackSpeedMult { get; init; } = 1f;

        /// <summary>Metres a ranged or caster monster stops at from its target, or 0 to close in as normal.</summary>
        public float HoldDistance { get; init; }

        /// <summary>The client palette the creature is tinted with at spawn, or 0 for none.</summary>
        public uint TintPalette { get; init; }

        /// <summary>A knight shows its health bar to players.</summary>
        public bool ShowHealthBar { get; init; }

        /// <summary>The owning run, as the read-only view every engine hook may consult from any thread.</summary>
        public IWorldEventHoldRunView Run { get; init; }
    }
}
