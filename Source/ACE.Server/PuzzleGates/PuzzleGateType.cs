namespace ACE.Server.PuzzleGates
{
    /// <summary>The four puzzle-gate experiences. All share one gate, one round counter and one wrong-answer path.</summary>
    public enum PuzzleGateType
    {
        /// <summary>N levers, a coloured light above each; indicator beams in front of the gate show the target colour.</summary>
        Sigil,

        /// <summary>N levers, k coloured beams each aimed at a distinct lever; the indicator picks the beam.</summary>
        Beam,

        /// <summary>N near-identical levers; exactly one differs on one channel.</summary>
        Odd,

        /// <summary>One lever that hops between spots each round.</summary>
        Shuffle,
    }

    /// <summary>Which single channel the odd lever differs on (the only allowed answer leak for <see cref="PuzzleGateType.Odd"/>).</summary>
    public enum PuzzleOddChannel
    {
        /// <summary>Not specified: the layout draws one per placement.</summary>
        Auto,
        Scale,
        Yaw,
        Glow,
    }

    /// <summary>Which host-local axis the beam effect is assumed to run along. Dev switch until render-verified.</summary>
    public enum PuzzleBeamAxis
    {
        /// <summary>Beam runs along host local -Z (default).</summary>
        Down,

        /// <summary>Beam runs along host local +Z.</summary>
        Up,
    }

    /// <summary>What a spec is. The live layer maps role to a wcid; the pure core never sees a wcid.</summary>
    public enum PuzzleRole
    {
        Gate,
        /// <summary>A lever the player can pull. Candidates carry the answer only via the type's one allowed channel.</summary>
        Candidate,
        /// <summary>A Puzzle Light host (sigil light, or a beam host).</summary>
        Light,
        /// <summary>The light above the gate that shows the target colour.</summary>
        Indicator,
    }
}