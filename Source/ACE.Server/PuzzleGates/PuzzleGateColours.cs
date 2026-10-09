using System;

namespace ACE.Server.PuzzleGates
{
    /// <summary>A colour effect: the display name is admin-facing only and never reaches a player.</summary>
    public readonly record struct PuzzleColour(string Name, uint Script);

    public static class PuzzleGateColours
    {
        /// <summary>Distinct colour scripts (VisualEffectScript ids). Only red is render-verified. White is deliberately absent: blue and white beams were near-indistinguishable live (2026-10-06). Every colour-bound limit derives from Palette.Length.</summary>
        public static readonly PuzzleColour[] Palette =
        {
            new PuzzleColour("red", 0x33000798),
            new PuzzleColour("blue", 0x3300079A),
            new PuzzleColour("green", 0x33000796),
            new PuzzleColour("purple", 0x33000797),
        };

        /// <summary>
        /// The glow used by the odd type's glow channel: the God-tier weapon aura (frost bloom, large and
        /// unmistakable; confirmed in play on weapons, commit 2aa25fcdc). It replaced the Elite orb
        /// 0x330011BE, which was barely visible on a lever in the 2026-10-06 live test. Not a beam script - a
        /// beam emits along the host's local -Z, so on a floor-standing lever it points into the ground and
        /// is never seen.
        /// </summary>
        public const uint GlowScript = 0x3300101B;

        public static PuzzleColour ByIndex(int index)
        {
            if (index < 0 || index >= Palette.Length)
                throw new ArgumentOutOfRangeException(nameof(index));

            return Palette[index];
        }
    }
}