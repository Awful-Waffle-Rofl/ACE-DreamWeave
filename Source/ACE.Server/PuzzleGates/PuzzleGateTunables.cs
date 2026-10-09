using System;

namespace ACE.Server.PuzzleGates
{
    /// <summary>
    /// Constants for the puzzle gate. Deliberately constants, not PropertyManager reads: the pure core must
    /// run in unit tests, where PropertyManager reads throw. Layout frame: x = right, y = forward, z = up,
    /// relative to the admin's position and heading.
    /// </summary>
    public static class PuzzleGateTunables
    {
        public const int MinN = 2;
        public const int MaxN = 5;
        public const int DefaultN = 3;

        public const int MinRounds = 1;
        public const int MaxRounds = 5;
        public const int DefaultRounds = 3;

        public const int MinBeams = 1;

        /// <summary>Beams carry distinct colours, so the palette bounds them.</summary>
        public static int MaxBeams => PuzzleGateColours.Palette.Length;

        /// <summary>
        /// Lever count for the pick-one types (sigil and odd): n is 4 or 5, and when n= is omitted the
        /// generator draws 4 or 5 per placement. Live test 2026-10-06: 2-3 levers made the answer a coin flip.
        /// </summary>
        public const int MinPickN = 4;
        public const int MaxPickN = 5;

        /// <summary>
        /// Sigil lever count. NOT palette-bound: only the ANSWER colour must be unique among the levers, so
        /// decoys reuse the other Palette.Length - 1 colours (one may repeat) and n can exceed the palette.
        /// Needs Palette.Length >= 2 (one answer colour plus at least one decoy colour).
        /// </summary>
        public const int MaxSigilN = MaxPickN;

        public const int MinSpots = 2;
        public const int MaxSpots = 8;
        public const int DefaultSpots = 4;

        public const float MinRadius = 2.0f;
        public const float MaxRadius = 30.0f;
        public const float DefaultRadius = 8.0f;

        public const int MinLockoutSeconds = 0;
        public const int MaxLockoutSeconds = 60;
        public const int DefaultLockoutSeconds = 5;

        public const int MinAmbushCount = 1;
        public const int MaxAmbushCount = 5;

        /// <summary>Forward distance of the lever row from the anchor, metres.</summary>
        public const float LeverRowDistance = 3.0f;

        public const float LeverSpacing = 1.5f;

        /// <summary>Forward distance of the gate from the anchor, metres (the admin default; a site may override it within the Min/Max below).</summary>
        public const float GateDistance = 7.0f;

        // Per-site layout limits (PuzzleLayoutParams). The defaults above and below stay what an admin /puzzlegate placement uses.
        public const float MinGateDistance = 4.0f;
        public const float MaxGateDistance = 14.0f;
        public const float MinLightHeight = 2.2f;
        public const float MaxLightHeight = 6.0f;
        public const float MinIndicatorHeight = 2.2f;
        public const float MaxIndicatorHeight = 8.0f;
        public const float MinHubHeight = 2.6f;
        public const float MaxHubHeight = 10.0f;

        /// <summary>Height of the indicator lights above the gate origin.</summary>
        public const float IndicatorHeight = 4.0f;

        /// <summary>Indicator beams per round, all the target colour (the door hides a single one).</summary>
        public const int IndicatorCount = 3;

        /// <summary>Spacing between indicator beams across the door face, metres (x = -1, 0, +1).</summary>
        public const float IndicatorSpacing = 1.0f;

        /// <summary>How far in front of the gate line (toward the lever row) the indicators stand, metres.</summary>
        public const float IndicatorForward = 1.0f;

        /// <summary>Height of a sigil light above its lever origin.</summary>
        public const float SigilLightHeight = 2.5f;

        /// <summary>Beam hub: height above the lever row centre.</summary>
        public const float HubHeight = 5.0f;

        /// <summary>Beam hub: offset from the lever row centre toward the gate.</summary>
        public const float HubForward = 1.5f;

        /// <summary>Beam length L (unverified in render): a host sits min(L, distance to hub) from its target.</summary>
        public const float BeamLength = 5.0f;

        /// <summary>Height above the lever origin that a beam aims at.</summary>
        public const float BeamTargetHeight = 0.0f;

        /// <summary>Odd lever: scale multipliers (high or low).</summary>
        public const float OddScaleHigh = 1.4f;
        public const float OddScaleLow = 0.65f;

        /// <summary>
        /// Odd lever: yaw offset range, degrees (sign drawn per round). Pinned to a quarter turn: the lever
        /// model's handle leans, so a half turn reads as a mirrored or pulled lever, while a quarter turn
        /// swings the base blocks in line with the row, which no other lever shares (live test 2026-10-06).
        /// </summary>
        public const float OddYawMinDeg = 90.0f;
        public const float OddYawMaxDeg = 90.0f;

        /// <summary>Noise applied to every odd-type lever: scale +/- fraction, yaw +/- degrees.</summary>
        public const float NoiseScale = 0.03f;
        public const float NoiseYawDeg = 4.0f;

        /// <summary>Shuffle ring fallback: every ring point stays at least this far in front of the gate line (metres).</summary>
        public const float ShuffleGateClearance = 1.0f;

        /// <summary>Slots always drawn per round regardless of N, so the RNG stream never depends on options.</summary>
        public const int DrawSlots = 5;

        // ---- live layer ------------------------------------------------------------------------------

        /// <summary>Puzzle Gate (Door). Role -> wcid lives only here; the pure core never sees a wcid.</summary>
        public const uint GateWcid = 1006850;

        /// <summary>Puzzle Lever (Switch): every Candidate.</summary>
        public const uint LeverWcid = 1006851;

        /// <summary>Puzzle Light (Generic effect host): every Light and the Indicator.</summary>
        public const uint LightWcid = 1006852;

        /// <summary>Ambush creatures alive at once per placement; a wrong answer past this spawns nothing.</summary>
        public const int MaxLiveAmbush = 10;

        /// <summary>Ambush row: forward distance from the anchor (between the admin and the lever row).</summary>
        public const float AmbushRowDistance = 1.5f;

        /// <summary>A placement still Spawning after this long never ran its queued spawn (landblock unloaded).</summary>
        public const int SpawnTimeoutSeconds = 30;

        /// <summary>/puzzlegate clear and reveal with no id pick the nearest placement within this many metres.</summary>
        public const float NearestRange = 50.0f;

        /// <summary>Approach prompt: a player within this many metres of the lever-row centre gets the one hint line.</summary>
        public const float PromptRadius = 12.0f;

        /// <summary>Approach prompt: the hint re-arms for a player only once they are seen beyond this many metres.</summary>
        public const float PromptResetRadius = 25.0f;

        /// <summary>Approach prompt: seconds between scans of the placement's landblock players.</summary>
        public const float PromptScanSeconds = 1.0f;

        /// <summary>
        /// Approach prompt: a prompted player missing from this many consecutive scans (left the landblock,
        /// logged out) is re-armed. Not instant, so stepping over a landblock edge next to the puzzle cannot
        /// make the hint repeat.
        /// </summary>
        public const int PromptUnseenRearmScans = 30;

        // ---- gate models (PuzzleGateModel) -------------------------------------------------------------

        /// <summary>Garrison double door (Setup 0x0200037E). Opens normally.</summary>
        public const uint GarrisonGateWcid = 1006853;

        /// <summary>Reinforced gate (Setup 0x02000FB5). Its MotionTable 0x09000115 has a ZERO-length open animation.</summary>
        public const uint ReinforcedGateWcid = 1006854;

        /// <summary>Olthoi door (Setup 0x020005F2). Opens normally.</summary>
        public const uint OlthoiGateWcid = 1006855;

        /// <summary>Ward Barrier panel (Setup 0x020014FF). Its MotionTable 0x0900019B has a ZERO-length open animation.</summary>
        public const uint BarrierPanelWcid = 1006856;

        /// <summary>
        /// Barrier panel collision width at scale 1, metres: the 0x020014FF collision width the site tool MEASURED
        /// from the dat (3.333; its catalog quotes the rounded 3.33). The server has only this constant, so it must
        /// match the measured value the tool fitted the panels with, or the outer edges drift off the jambs.
        /// </summary>
        public const float BarrierPanelWidth = 3.333f;

        /// <summary>A barrier's outer panel edges sit this far inside the jambs (PuzzleSiteFit.BarrierJambInset).</summary>
        public const float BarrierJambInset = 0.05f;

        /// <summary>
        /// Gate wcids whose open animation is zero-length: "opening" one changes its state without moving it, so
        /// it would keep blocking the doorway. A solve DESTROYS these instead (PuzzleGateSolveAction.Destroy).
        /// </summary>
        public static readonly uint[] DestroyOnSolveWcids = { ReinforcedGateWcid, BarrierPanelWcid };

        /// <summary>
        /// The open-vs-destroy rule. A barrier and a focal object are always destroyed; a door is destroyed when
        /// its wcid is in <see cref="DestroyOnSolveWcids"/> and opened otherwise (1006850, 1006853, 1006855).
        /// </summary>
        public static PuzzleGateSolveAction SolveActionFor(PuzzleGateForm form, uint wcid)
        {
            if (form != PuzzleGateForm.Door)
                return PuzzleGateSolveAction.Destroy;

            return Array.IndexOf(DestroyOnSolveWcids, wcid) >= 0 ? PuzzleGateSolveAction.Destroy : PuzzleGateSolveAction.Open;
        }

        /// <summary>
        /// The Thread reward scene's focal object, standing in for a dedicated target weenie that does not exist
        /// yet: the Puzzle Light host (a Generic effect host, so never a Door and never pickable). Content replaces
        /// it by changing this one constant.
        /// </summary>
        public const uint RewardFocalWcid = LightWcid;

        /// <summary>The focal object's VisualEffectScript: the God-tier weapon aura (PuzzleGateColours.GlowScript), the one glow confirmed clearly visible in play.</summary>
        public const uint RewardFocalScript = PuzzleGateColours.GlowScript;

        /// <summary>A focal object hangs this far above the gate point, metres.</summary>
        public const float FocalHeight = 1.0f;

        public static uint WcidFor(PuzzleRole role)
        {
            switch (role)
            {
                case PuzzleRole.Gate: return GateWcid;
                case PuzzleRole.Candidate: return LeverWcid;
                default: return LightWcid;
            }
        }
    }
}