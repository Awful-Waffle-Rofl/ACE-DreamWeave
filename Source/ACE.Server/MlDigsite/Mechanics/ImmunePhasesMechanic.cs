using System;
using System.Collections.Generic;
using System.Reflection;

using ACE.Entity.Enum;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.MlDigsite.Mechanics
{
    /// <summary>
    /// IMMUNE PHASES WITH ADDS. "At 85%, 50% and 25% health the boss goes immune and adds spawn. Killing the
    /// adds ends the immunity." (RoZ round 13.)
    ///
    /// THIS IS THE CROSS-OBJECT MECHANIC, and the driver owns the relationship rather than the boss: the boss
    /// carries only a FILTER (the "immune" monster effect), and the driver decides when it is on, which adds
    /// end it, and what happens if one of them is never killed.
    ///
    /// WHY NOT PropertyBool.Invincible. It does not block spell damage on a monster: both spell-side checks
    /// gate on the target being a PLAYER while the melee check does not, so an Invincible boss would still be
    /// killed by war magic during an "immune" phase. The immune effect rides IMonsterIncomingDamage, which is
    /// wired at all THREE damage sinks. See ImmuneEffect's own doc comment.
    ///
    /// WHY THE RECORD IS NOT ATTACHED AT THE THRESHOLD. Creature.ApplyMonsterEffectOverlay allocates a fresh
    /// effect-state array, so bolting the record on at 85% health would silently reset every other effect the
    /// boss carries. It ships inert with the boss (MlDigsiteSpawner composes it alongside
    /// ml_digsite_bossrush_mechanic) and this module switches it on in place.
    ///
    /// THE FAILSAFE IS NOT OPTIONAL. An add that falls through terrain, or is lost when a landblock unloads,
    /// would otherwise hold the boss immortal until the encounter's own TTL. timeout= ends the phase anyway.
    ///
    /// IMMUNE50 IS THIS MODULE with its threshold list narrowed to 0.50 alone - the tester's set 5 secondary -
    /// not a sixth implementation. MlDigsiteBossMechanicRules.ImmuneThresholds is what narrows it.
    /// </summary>
    public sealed class ImmunePhasesMechanic : IMlDigsiteMechanic
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public MlDigsiteMechanic Kind => MlDigsiteMechanic.ImmunePhases;

        // Documented defaults, applied for any arg the tunable string leaves out. Never 0, never blank.
        private const int DefaultAdds = 4;
        private const double DefaultTimeout = 45.0;

        public void Tick(MlDigsiteMechanicContext ctx)
        {
            // A running phase is watched, never re-triggered. The failsafe is the only thing that can end one
            // from here; every other end comes through the add death hook.
            if (ctx.State.ImmunePhaseActive)
            {
                if (!ctx.State.TryTimeOutImmunePhase(ctx.Now))
                    return;

                EndPhase(ctx.Encounter, ctx.Boss, "Whatever it called for does not come. The shell dulls and cracks on its own.");

                log.Info($"[ML_DIGSITE] {ctx.Encounter} immune phase ended on its failsafe timeout");

                return;
            }

            var max = ctx.Boss.Health.MaxValue;

            if (max == 0)
                return;

            // The identical read MlDigsiteManager.SampleBossHealth already makes on this same tick, and safe
            // for the same reason: the world thread runs after the landblock tick has joined.
            var fraction = (double)ctx.Boss.Health.Current / max;

            foreach (var threshold in MlDigsiteBossMechanicRules.ImmuneThresholds(ctx.Mechanic, ctx.Args))
            {
                if (fraction > threshold)
                    continue;

                // Latched per threshold, so a boss healed back over a threshold (by its own leech, or by the
                // tether heal) does not go immune at the same one twice.
                if (!ctx.State.TryLatchImmuneThreshold(threshold))
                    continue;

                BeginPhase(ctx, threshold);

                return;
            }
        }

        /// <summary>
        /// Opens a phase: place the adds, record their guids, switch the filter on, announce.
        ///
        /// A phase with NO adds is never opened. An immunity with nothing to kill is an unwinnable damage
        /// race that only the failsafe can end, which is strictly worse for the player than the threshold
        /// passing unremarked - so a spawn that placed nothing leaves the boss killable and says so in the
        /// log.
        /// </summary>
        private static void BeginPhase(MlDigsiteMechanicContext ctx, double threshold)
        {
            var wanted = Math.Clamp(ctx.Args.GetInt("adds", DefaultAdds), 1, 12);
            var timeout = Math.Clamp(ctx.Args.GetDouble("timeout", DefaultTimeout), 5.0, 600.0);

            var guids = new List<uint>();

            for (var i = 0; i < wanted; i++)
            {
                if (!ctx.Encounter.RunValid)
                    break;

                var add = MlDigsiteSpawner.TrySpawn(ctx.Encounter, MlDigsiteRole.Add, ctx.Rng);

                if (add != null)
                    guids.Add(add.Guid.Full);
            }

            if (!ctx.State.BeginImmunePhase(guids, ctx.Now + TimeSpan.FromSeconds(timeout)))
            {
                log.Warn($"[ML_DIGSITE] {ctx.Encounter} immune phase at {threshold:0.##} placed no adds; the boss stays killable rather than being made immune with nothing to kill");
                return;
            }

            // A PLAIN BOOL WRITE from the world thread, read on landblock threads by ImmuneEffect. Atomic in
            // the CLR memory model for a bool, and deliberately NOT queued onto the boss's landblock: a queued
            // write would land after the announcement, leaving a window in which the chat line says the boss
            // is immune and it is not.
            ctx.Boss.P_DigsiteImmune = true;

            MlDigsiteProps.PlayScriptOn(ctx.Boss, PlayScript.ShieldUpGrey);

            ctx.Say($"{ctx.Boss.Name} hardens over. Kill the ones it called!", ChatMessageType.WorldBroadcast);

            log.Info($"[ML_DIGSITE] {ctx.Encounter} immune phase opened at {threshold:0.##} with {guids.Count} add(s), failsafe {timeout}s (slot {ctx.Slot})");
        }

        /// <summary>
        /// One add has died. Runs on the LANDBLOCK thread inside Creature.Die. Only the death that empties the
        /// phase's set does anything, and the state object's own lock is what makes "empties it" true for
        /// exactly one caller even when two landblock threads kill the last two adds at once.
        /// </summary>
        public static void OnAddDied(MlDigsiteEncounter encounter, MlDigsiteBossMechanicState state, Creature add)
        {
            if (!state.NoteImmunePhaseAddDeath(add.Guid.Full, out var phaseCleared) || !phaseCleared)
                return;

            var boss = encounter.ObjectiveCreature;

            EndPhase(encounter, boss, boss == null ? null : $"{boss.Name}'s shell cracks open!");

            log.Info($"[ML_DIGSITE] {encounter} immune phase ended: every add it called for is dead");
        }

        /// <summary>
        /// Closes a phase however it ended. Clearing the flag is what actually ends the immunity; the script
        /// and the line only report it, so a missed broadcast can never leave a boss immortal.
        /// </summary>
        private static void EndPhase(MlDigsiteEncounter encounter, Creature boss, string line)
        {
            if (boss == null || boss.IsDestroyed)
                return;

            boss.P_DigsiteImmune = false;

            MlDigsiteProps.PlayScriptOn(boss, PlayScript.ShieldDownGrey);

            if (!string.IsNullOrEmpty(line))
                MlDigsiteManager.Announce(encounter, line, ChatMessageType.WorldBroadcast);
        }
    }
}
