using System;
using System.Collections.Generic;

namespace ACE.Server.PuzzleGates
{
    /// <summary>What stands where the puzzle's gate goes.</summary>
    public enum PuzzleGateForm
    {
        /// <summary>One Door weenie at a scale. The admin /puzzlegate default (the Warded Gate).</summary>
        Door,

        /// <summary>N Door panels laid side by side across a doorway too wide for any single door model.</summary>
        Barrier,

        /// <summary>
        /// A non-Door focal object: nothing physical is held shut, the puzzle's solve is consumed by its host
        /// instead (the Thread reward scene). Never pickable, never a Door.
        /// </summary>
        Focal,
    }

    /// <summary>What a solve does to the gate objects.</summary>
    public enum PuzzleGateSolveAction
    {
        /// <summary>Unlock and open the Door through WorldObject.OpenObjectiveGate.</summary>
        Open,

        /// <summary>
        /// Destroy every gate object with a visible fade. For models whose MotionTable has a ZERO-length open
        /// animation (they would "open" without moving and keep blocking), for barriers, and for focal objects.
        /// </summary>
        Destroy,
    }

    /// <summary>
    /// The gate-model indirection: which wcid(s) the gate role spawns, at what scale, and how a solve removes
    /// them. Immutable and pure, so the layout and the open-vs-destroy rule are unit-tested without a world.
    /// The pure generator still plans ONE gate spec; this expands it into the objects to spawn.
    /// </summary>
    public sealed class PuzzleGateModel
    {
        /// <summary>The admin /puzzlegate gate: one Warded Gate Door at scale 1. Exactly today's behaviour.</summary>
        public static readonly PuzzleGateModel Default = new PuzzleGateModel(PuzzleGateForm.Door, PuzzleGateTunables.GateWcid, 1.0f, 1, 0f, 0);

        public PuzzleGateForm Form { get; }

        public uint Wcid { get; }

        public float Scale { get; }

        /// <summary>Barrier panel count (1 for every other kind).</summary>
        public int Panels { get; }

        /// <summary>Measured usable doorway width, metres; 0 when unknown (the barrier then butts panels edge to edge).</summary>
        public float DoorwayWidth { get; }

        /// <summary>VisualEffectScript stamped on every gate object; 0 = none.</summary>
        public uint Script { get; }

        public PuzzleGateModel(PuzzleGateForm form, uint wcid, float scale, int panels, float doorwayWidth, uint script)
        {
            Form = form;
            Wcid = wcid;
            Scale = float.IsNaN(scale) || scale <= 0f ? 1.0f : scale;
            Panels = form == PuzzleGateForm.Barrier ? Math.Max(1, panels) : 1;
            DoorwayWidth = float.IsNaN(doorwayWidth) || doorwayWidth < 0f ? 0f : doorwayWidth;
            Script = script;
        }

        public static PuzzleGateModel Door(uint wcid, float scale) => new PuzzleGateModel(PuzzleGateForm.Door, wcid, scale, 1, 0f, 0);

        public static PuzzleGateModel Barrier(uint wcid, float scale, int panels, float doorwayWidth) => new PuzzleGateModel(PuzzleGateForm.Barrier, wcid, scale, panels, doorwayWidth, 0);

        public static PuzzleGateModel Focal(uint wcid, uint script) => new PuzzleGateModel(PuzzleGateForm.Focal, wcid, 1.0f, 1, 0f, script);

        /// <summary>A Door or Barrier gate must be a Door weenie (PrepareGate refuses anything else); a Focal must not need to be.</summary>
        public bool RequiresDoor => Form != PuzzleGateForm.Focal;

        /// <summary>
        /// True when a solve removes the WHOLE scene - gate, levers, lights, indicator and beam hosts - and the
        /// placement with it: a focal object guards nothing physical, so once its seal breaks nothing of the scene has
        /// a job left (the Thread reward scene, run or admin /puzzlegate site). A door or barrier gate keeps its levers,
        /// which answer with the "spent" line.
        /// </summary>
        public bool TearsDownOnSolve => Form == PuzzleGateForm.Focal;

        /// <summary>The solve rule for this model. See <see cref="PuzzleGateTunables.SolveActionFor"/>.</summary>
        public PuzzleGateSolveAction SolveAction => PuzzleGateTunables.SolveActionFor(Form, Wcid);

        /// <summary>
        /// Centre-to-centre spacing of barrier panels, metres. Ported from the site tool's fit rule
        /// (ACE.Content.Tools PuzzleSiteFit.Choose, barrier branch): panel width = BarrierPanelWidth * scale, and
        /// n panels are spread so the outer panels' edges sit BarrierJambInset inside the jambs:
        ///   pitch = (W - 2 * inset - panelWidth) / (n - 1)        for n &gt; 1
        /// A single panel, or a doorway width the file did not carry (0), falls back to edge-to-edge panels
        /// (pitch = panelWidth). Never negative.
        /// </summary>
        public static float PanelPitch(int panels, float scale, float doorwayWidth)
        {
            var panelWidth = PuzzleGateTunables.BarrierPanelWidth * scale;

            if (panels <= 1)
                return 0f;

            if (!(doorwayWidth > 0f))
                return panelWidth;

            var pitch = (doorwayWidth - 2f * PuzzleGateTunables.BarrierJambInset - panelWidth) / (panels - 1);
            return Math.Max(0f, pitch);
        }

        /// <summary>
        /// The anchor-frame positions of every gate object, given the generator's single gate position. A door
        /// is that point; a barrier is <see cref="Panels"/> points across the opening (local x, centred on it);
        /// a focal object is lifted <see cref="PuzzleGateTunables.FocalHeight"/> off the floor.
        /// </summary>
        public IReadOnlyList<System.Numerics.Vector3> PartLocals(System.Numerics.Vector3 gateLocal)
        {
            switch (Form)
            {
                case PuzzleGateForm.Barrier:
                {
                    var pitch = PanelPitch(Panels, Scale, DoorwayWidth);
                    var list = new System.Numerics.Vector3[Panels];

                    for (var i = 0; i < Panels; i++)
                        list[i] = gateLocal + new System.Numerics.Vector3((i - (Panels - 1) / 2.0f) * pitch, 0f, 0f);

                    return list;
                }

                case PuzzleGateForm.Focal:
                    return new[] { gateLocal + new System.Numerics.Vector3(0f, 0f, PuzzleGateTunables.FocalHeight) };

                default:
                    return new[] { gateLocal };
            }
        }

        public override string ToString() => Form == PuzzleGateForm.Barrier
            ? $"barrier wcid={Wcid} x{Panels} scale={Scale:0.###}"
            : $"{Form.ToString().ToLowerInvariant()} wcid={Wcid} scale={Scale:0.###}";
    }
}
