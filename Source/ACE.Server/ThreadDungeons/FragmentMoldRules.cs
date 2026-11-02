using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using ACE.Server.ThreadDungeons.Defs;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>What a use of the Fragment Mold on a target resolves to.</summary>
    public enum MoldStep
    {
        /// <summary>Nothing changes; <see cref="MoldDecision.Message"/> is the refusal.</summary>
        Refuse,

        /// <summary>The target fragment holds a load: record it onto the mold. The fragment is untouched.</summary>
        TakeImprint,

        /// <summary>The target fragment is blank and the mold holds an imprint: load it, all or nothing.</summary>
        Apply,
    }

    public enum ImprintOutcome
    {
        /// <summary>Every dose of the imprint fits and every unit is held: <see cref="ImprintPlan.Needs"/> is the bill.</summary>
        Ready,

        /// <summary>The imprint fits but the player is short of at least one wcid: <see cref="ImprintPlan.Shortages"/> names them.</summary>
        Shortage,

        /// <summary>The imprint cannot be loaded at all (retired component, over a limit, fragment not blank).</summary>
        Refused,
    }

    /// <summary>The all-or-nothing plan for applying an imprint. Pure data; nothing here touches a player.</summary>
    public sealed class ImprintPlan
    {
        public ImprintOutcome Outcome { get; }

        /// <summary>The refusal text when <see cref="Outcome"/> is Refused, otherwise null.</summary>
        public string Refusal { get; }

        /// <summary>Units to consume per wcid, in first-use order. Populated only when Outcome is Ready.</summary>
        public IReadOnlyList<(uint Wcid, int Units)> Needs { get; }

        /// <summary>Units MISSING per wcid (need minus held), in first-use order. Populated only when Outcome is Shortage.</summary>
        public IReadOnlyList<(uint Wcid, int Missing)> Shortages { get; }

        /// <summary>The spec with the whole imprint loaded. Meaningful only when Outcome is Ready.</summary>
        public DungeonGemSpec Result { get; }

        public ImprintPlan(ImprintOutcome outcome, string refusal, IReadOnlyList<(uint Wcid, int Units)> needs,
            IReadOnlyList<(uint Wcid, int Missing)> shortages, DungeonGemSpec result)
        {
            Outcome = outcome;
            Refusal = refusal;
            Needs = needs ?? new List<(uint Wcid, int Units)>();
            Shortages = shortages ?? new List<(uint Wcid, int Missing)>();
            Result = result;
        }
    }

    public sealed class MoldDecision
    {
        public MoldStep Step { get; }

        /// <summary>The refusal text (Refuse) or the chat line for a taken imprint (TakeImprint); null for Apply.</summary>
        public string Message { get; }

        /// <summary>TakeImprint only: the serialized load to store on the mold.</summary>
        public string NewImprint { get; }

        /// <summary>TakeImprint only: the mold's new LongDesc.</summary>
        public string NewLongDesc { get; }

        /// <summary>Apply only: the parsed target spec (blank, unpressed).</summary>
        public DungeonGemSpec TargetSpec { get; }

        /// <summary>Apply only: the parsed imprint, in load order.</summary>
        public IReadOnlyList<(uint Wcid, int Doses)> Imprint { get; }

        /// <summary>Apply only: the plan. Its Outcome says whether anything may be consumed.</summary>
        public ImprintPlan Plan { get; }

        public MoldDecision(MoldStep step, string message, string newImprint = null, string newLongDesc = null,
            DungeonGemSpec targetSpec = null, IReadOnlyList<(uint Wcid, int Doses)> imprint = null, ImprintPlan plan = null)
        {
            Step = step;
            Message = message;
            NewImprint = newImprint;
            NewLongDesc = newLongDesc;
            TargetSpec = targetSpec;
            Imprint = imprint;
            Plan = plan;
        }
    }

    /// <summary>
    /// Every decision of the Fragment Mold (a Thread-dungeons crafting item that records the component load of
    /// a Raw Fragment and re-applies it to blank fragments). Pure and unit-tested; FragmentMold.cs is the thin
    /// engine half that consumes components and writes properties.
    ///
    /// VAGUE-FEEDBACK RULING (Docs/Threads/PRESS-V2-COMPONENT-DESIGN.md): nothing here may reveal a resulting
    /// modifier or magnitude. Every line names COMPONENTS only, in the same text as the Pressed: line.
    ///
    /// ALL OR NOTHING. PlanImprint replays every dose of the imprint against a SIMULATED spec with the same
    /// CanLoad + Load pair the hand-loading path uses, so the per-type limits and the retired-component check
    /// are the real ones, and totals up the units each wcid needs. Only a plan that is fully affordable and
    /// fully loadable (Ready) lets the caller consume anything.
    /// </summary>
    public static class FragmentMoldRules
    {
        public const string RefuseNotAFragment = "The mold does not answer that.";
        public const string RefuseNoImprint = "The mold holds no imprint.";
        public const string RefuseImprintUnreadable = "The imprint in this mold will not read.";
        public const string RefuseNotBlank = "The fragment already holds a load.";
        public const string RefuseTrading = "Finish your trade first.";
        public const string RefuseNotInPack = "Take the fragment into your pack first.";
        public const string RefuseNoTarget = "Double-click the mold, then click a Raw Fragment.";

        public static string ComposeImprintDesc(string line) => "Imprint: " + line;
        public static string ComposeTakesLine(string line) => $"The mold takes the imprint: {line}.";
        public static string ComposeAppliedLine(string line) => $"The fragment takes the imprint: {line}.";
        /// <summary>
        /// The line for a load that stopped partway. <paramref name="line"/> is the component list that DID load
        /// (null when nothing did); <paramref name="unitsLost"/> is units taken from the packs but not recorded
        /// on the fragment (a dose that failed partway), 0 when none.
        /// </summary>
        public static string ComposeInterruptedLine(string line, int unitsLost = 0)
        {
            var text = line == null ? "The imprint was interrupted. The fragment took nothing." : $"The imprint was interrupted. The fragment took: {line}.";
            if (unitsLost > 0)
                text += $" {unitsLost.ToString(CultureInfo.InvariantCulture)} unit{(unitsLost == 1 ? "" : "s")} taken from your packs did not load.";
            return text;
        }

        /// <summary>
        /// Units consumed from the packs that the loaded doses do not account for: total consumed minus the
        /// units of every dose recorded on <paramref name="loaded"/>. Non-zero only when a dose call failed partway.
        /// </summary>
        public static int UnitsLost(IReadOnlyList<(uint Wcid, int Doses)> loaded, IReadOnlyDictionary<uint, int> consumedUnits, AttunementDef defs)
        {
            var taken = consumedUnits.Values.Sum();
            var recorded = 0;
            foreach (var entry in loaded)
                if (defs.TryGet(entry.Wcid, out var component))
                    recorded += entry.Doses * component.Dose;
            return Math.Max(0, taken - recorded);
        }

        /// <summary>"You are short: Red Taper x1, Powdered Onyx x2." Counts are UNITS missing.</summary>
        public static string ComposeShortageLine(IReadOnlyList<(uint Wcid, int Missing)> shortages, AttunementDef defs)
            => "You are short: " + string.Join(", ", shortages.Select(s =>
                (defs?.DisplayName(s.Wcid) ?? $"wcid {s.Wcid}") + " x" + s.Missing.ToString(CultureInfo.InvariantCulture))) + ".";

        /// <summary>
        /// Resolves one use of the mold. <paramref name="targetSpecText"/> is the target's DungeonGemSpec
        /// property, or null when it has none (not a Raw Fragment). <paramref name="imprintText"/> is the mold's
        /// stored imprint, or null/empty when it holds none. <paramref name="held"/> counts what the player
        /// holds of a wcid across every pack.
        /// </summary>
        public static MoldDecision Decide(string targetSpecText, string imprintText, AttunementDef defs, Func<uint, int> held)
        {
            if (targetSpecText == null)
                return Refuse(RefuseNotAFragment);

            if (!DungeonGemSpec.TryParse(targetSpecText, out var spec, out _))
                return Refuse(RawFragment.UnreadableMessage);

            if (spec.Seed != 0)
                return Refuse(RawFragment.AlreadyFinishedMessage);

            if (spec.Load.Count > 0)
            {
                var line = RawFragmentRules.ComposePressedLine(spec.Load, defs);
                return new MoldDecision(MoldStep.TakeImprint, ComposeTakesLine(line),
                    newImprint: DungeonGemSpec.SerializeLoad(spec.Load), newLongDesc: ComposeImprintDesc(line));
            }

            if (string.IsNullOrEmpty(imprintText))
                return Refuse(RefuseNoImprint);

            if (!DungeonGemSpec.TryParseLoad(imprintText, out var imprint, out _))
                return Refuse(RefuseImprintUnreadable);

            var plan = PlanImprint(spec, imprint, defs, held);
            return new MoldDecision(MoldStep.Apply, null, targetSpec: spec, imprint: imprint, plan: plan);
        }

        /// <summary>
        /// Walks every dose of <paramref name="imprint"/>, in load order, against a simulated copy of
        /// <paramref name="spec"/>, accumulating the units each wcid needs (doses * component.Dose). The
        /// fragment must be blank. Any CanLoad refusal (component retired from attunement.json, a type over its
        /// limit) is returned as a Refused plan with nothing to consume. Otherwise a wcid whose need exceeds
        /// <paramref name="held"/> yields a Shortage plan listing every short wcid, again with nothing to consume.
        /// </summary>
        public static ImprintPlan PlanImprint(DungeonGemSpec spec, IReadOnlyList<(uint Wcid, int Doses)> imprint,
            AttunementDef defs, Func<uint, int> held)
        {
            if (spec == null || imprint == null || defs == null || held == null)
                return Refused(RawFragmentRules.RefuseNotAValidTarget);

            if (spec.Load.Count > 0)
                return Refused(RefuseNotBlank);

            var needs = new List<(uint Wcid, int Units)>();
            var sim = spec;

            foreach (var entry in imprint)
            {
                for (var i = 0; i < entry.Doses; i++)
                {
                    var kind = RawFragmentRules.CanLoad(sim, defs, entry.Wcid, int.MaxValue, out var component, out var refusal);
                    if (kind != LoadRefusal.None)
                        return Refused(refusal);

                    sim = RawFragmentRules.Load(sim, component);

                    var slot = needs.FindIndex(n => n.Wcid == entry.Wcid);
                    if (slot < 0) needs.Add((entry.Wcid, component.Dose));
                    else needs[slot] = (entry.Wcid, needs[slot].Units + component.Dose);
                }
            }

            var shortages = new List<(uint Wcid, int Missing)>();
            foreach (var need in needs)
            {
                var have = held(need.Wcid);
                if (have < need.Units)
                    shortages.Add((need.Wcid, need.Units - have));
            }

            if (shortages.Count > 0)
                return new ImprintPlan(ImprintOutcome.Shortage, null, null, shortages, null);

            return new ImprintPlan(ImprintOutcome.Ready, null, needs, null, sim);
        }

        /// <summary>
        /// The partial-failure half. Replays the imprint against <paramref name="spec"/> loading a dose only
        /// when <paramref name="consumedUnits"/> still covers it, so the fragment never records a component
        /// that was not actually taken from the player. When every unit was consumed the result equals the
        /// plan's Result. Returns the new spec and how many doses were loaded.
        /// </summary>
        public static DungeonGemSpec LoadConsumed(DungeonGemSpec spec, IReadOnlyList<(uint Wcid, int Doses)> imprint,
            AttunementDef defs, IReadOnlyDictionary<uint, int> consumedUnits, out int dosesLoaded)
        {
            dosesLoaded = 0;
            var remaining = new Dictionary<uint, int>();
            foreach (var kv in consumedUnits) remaining[kv.Key] = kv.Value;

            var sim = spec;
            foreach (var entry in imprint)
            {
                for (var i = 0; i < entry.Doses; i++)
                {
                    remaining.TryGetValue(entry.Wcid, out var left);

                    if (RawFragmentRules.CanLoad(sim, defs, entry.Wcid, left, out var component, out _) != LoadRefusal.None)
                        break;

                    remaining[entry.Wcid] = left - component.Dose;
                    sim = RawFragmentRules.Load(sim, component);
                    dosesLoaded++;
                }
            }

            return sim;
        }

        private static MoldDecision Refuse(string message) => new MoldDecision(MoldStep.Refuse, message);

        private static ImprintPlan Refused(string refusal) => new ImprintPlan(ImprintOutcome.Refused, refusal, null, null, null);
    }
}
