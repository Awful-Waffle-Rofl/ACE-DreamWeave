using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ACE.Server.PuzzleGates
{
    /// <summary>
    /// Pure: a compact, admin- and log-facing description of what distinguishes each lever in a round, with the
    /// correct one starred. Built from the <see cref="PuzzlePlan"/> the round was actually spawned from (never
    /// re-randomised), so a log line answers "what did the player see and which lever was right" on its own.
    /// Never sent to a player.
    /// <list type="bullet">
    /// <item>sigil: the colour of each lever's light, then the gate colour: <c>red,blue*,green,purple gate=blue</c></item>
    /// <item>beam: each beam's colour and the lever it points at (one beam: <c>beam->3*</c>), then the gate colour when
    /// there is an indicator: <c>red->2,blue->4* gate=blue</c></item>
    /// <item>odd: the differing channel per lever: <c>scale:1,1,1.4*,1</c>, <c>yaw:3,-5,48*,2</c> (degrees off the
    /// facing) or <c>glow:-,-,on*,-</c></item>
    /// <item>shuffle: the spot the lever stands at: <c>spot2*</c></item>
    /// </list>
    /// </summary>
    public static class PuzzleGateCues
    {
        public static string Describe(PuzzlePlan plan)
        {
            if (plan == null)
                return "-";

            switch (plan.Type)
            {
                case PuzzleGateType.Sigil:
                    return DescribeSigil(plan);
                case PuzzleGateType.Beam:
                    return DescribeBeam(plan);
                case PuzzleGateType.Odd:
                    return DescribeOdd(plan);
                default:
                    return $"spot{(plan.AnswerSlot + 1).ToString(CultureInfo.InvariantCulture)}*";
            }
        }

        /// <summary>The palette name of a colour script, or its hex when it is not a palette colour.</summary>
        public static string ColourName(uint script)
        {
            foreach (var c in PuzzleGateColours.Palette)
                if (c.Script == script)
                    return c.Name;

            return $"0x{script:X8}";
        }

        private static string Mark(string cue, bool answer) => answer ? cue + "*" : cue;

        private static string GateColour(PuzzlePlan plan)
            => plan.AnswerColourIndex >= 0 && plan.AnswerColourIndex < PuzzleGateColours.Palette.Length
                ? " gate=" + PuzzleGateColours.ByIndex(plan.AnswerColourIndex).Name
                : "";

        private static List<PuzzleObjectSpec> Of(PuzzlePlan plan, PuzzleRole role)
            => plan.Specs.Where(s => s.Role == role).OrderBy(s => s.Slot).ToList();

        private static string DescribeSigil(PuzzlePlan plan)
        {
            var lights = Of(plan, PuzzleRole.Light);
            var parts = lights.Select(l => Mark(ColourName(l.Script), l.Slot == plan.AnswerSlot));
            return string.Join(",", parts) + GateColour(plan);
        }

        private static string DescribeBeam(PuzzlePlan plan)
        {
            var beams = Of(plan, PuzzleRole.Light);
            var targets = plan.BeamTargets;

            if (targets == null || targets.Count != beams.Count || beams.Count <= 1)
                return $"beam->{(plan.AnswerSlot + 1).ToString(CultureInfo.InvariantCulture)}*";

            var parts = new List<string>(beams.Count);

            for (var j = 0; j < beams.Count; j++)
                parts.Add(Mark($"{ColourName(beams[j].Script)}->{(targets[j] + 1).ToString(CultureInfo.InvariantCulture)}", targets[j] == plan.AnswerSlot));

            return string.Join(",", parts) + GateColour(plan);
        }

        private static string DescribeOdd(PuzzlePlan plan)
        {
            var levers = Of(plan, PuzzleRole.Candidate);
            var faceYaw = PuzzleGateGenerator.YawDegFromQuaternion(plan.Gate.Orientation);
            var sb = new StringBuilder();

            switch (plan.OddChannel)
            {
                case PuzzleOddChannel.Scale:
                    sb.Append("scale:");
                    Join(sb, levers, plan, l => Number(l.Scale));
                    break;
                case PuzzleOddChannel.Yaw:
                    sb.Append("yaw:");
                    Join(sb, levers, plan, l => Number(Wrap(PuzzleGateGenerator.YawDegFromQuaternion(l.Orientation) - faceYaw)));
                    break;
                default:
                    sb.Append("glow:");
                    Join(sb, levers, plan, l => l.Script != 0 ? "on" : "-");
                    break;
            }

            return sb.ToString();
        }

        private static void Join(StringBuilder sb, List<PuzzleObjectSpec> levers, PuzzlePlan plan, Func<PuzzleObjectSpec, string> cue)
        {
            for (var i = 0; i < levers.Count; i++)
            {
                if (i > 0)
                    sb.Append(',');

                sb.Append(Mark(cue(levers[i]), levers[i].Slot == plan.AnswerSlot));
            }
        }

        private static string Number(float value) => Math.Abs(value) < 0.005f ? "0" : Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);

        private static float Wrap(float deg)
        {
            while (deg > 180f)
                deg -= 360f;

            while (deg <= -180f)
                deg += 360f;

            return deg;
        }
    }
}
