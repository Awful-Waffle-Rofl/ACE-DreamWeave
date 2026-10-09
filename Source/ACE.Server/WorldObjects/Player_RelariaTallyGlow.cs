using System;
using System.Threading;

using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.MlTreasure;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// The Tally of the Unburied's 100-charge glow (MlRelariaChargeTrophy.TryAddMapCharge). A character
    /// who fills a Tally of the Unburied to capacity is stamped with
    /// MlRelariaChargeTrophy.GlowQuestName permanently, and from then on carries a looping visual: a
    /// GameMessageScript re-broadcast on the player every ml_relaria_tally_glow_pulse_seconds.
    ///
    /// DISTINCT FROM Player_RelariaAura ON PURPOSE: its own quest stamp, its own PlayScript
    /// (ml_relaria_tally_glow_script defaults to PlayScript.VisionUpWhite - a bright ascending
    /// white-light burst, visibly different from the aura's warm LayingofHands radiance and, unlike
    /// LayingofHands/WeddingBliss/LevelUp/AetheriaLevelUp, not already used anywhere else in this fork
    /// for a one-shot burst that a repeating pulse could be confused for), and its own run-id field, so
    /// the two pulses arm, disarm and re-arm completely independently - a character who has BOTH glows
    /// running is a fully expected end state, not a collision.
    ///
    /// Unlike the aura, this glow carries no on/off preference: it is a one-time terminal reward with no
    /// documented opt-out in this branch's spec, so eligibility is exactly
    /// <see cref="HasRelariaTallyGlow"/> and nothing else. The scheduling plumbing itself
    /// (<c>ScheduleQuestPulse</c>) is shared with Player_RelariaAura.cs rather than copy-pasted; see that
    /// file's header for why sharing stops there.
    ///
    /// This glow's pulse IS additionally gated server-wide, alongside the aura's, by
    /// ml_relaria_pulse_effects_enabled (owner ruling 2026-09-24: off by default). The check is
    /// <see cref="RelariaPulseEffectsEnabled"/>, defined in Player_RelariaAura.cs and shared by both
    /// files.
    /// </summary>
    partial class Player
    {
        private int relariaTallyGlowRunId;

        /// <summary>ml_relaria_tally_glow_pulse_seconds default: seconds between pulses.</summary>
        private const long DefaultRelariaTallyGlowPulseSeconds = 10;

        /// <summary>
        /// ml_relaria_tally_glow_script default: PlayScript.VisionUpWhite. See this file's header for why
        /// it was chosen over the aura's LayingofHands and over the other candidates that file's own
        /// header lists (WeddingBliss, EnchantUpWhite, SpecialStateWhite) - none of those four is
        /// currently used for a REPEATING pulse elsewhere in this fork, but VisionUpWhite is additionally
        /// not used anywhere else in Source/ACE.Server at all, so it cannot be confused with any other
        /// effect a player might already know.
        /// </summary>
        private const long DefaultRelariaTallyGlowScript = (long)PlayScript.VisionUpWhite;

        /// <summary>True once this character has ever filled a Tally of the Unburied. Never cleared.</summary>
        public bool HasRelariaTallyGlow => QuestManager.HasQuest(MlRelariaChargeTrophy.GlowQuestName);

        /// <summary>
        /// Arms the pulse if this character has earned the glow. Safe to call any number of times (login,
        /// the moment the glow is granted) - each call bumps the run id first, so at most one pulse chain
        /// from this player is ever live for this glow regardless of how many times this has been called.
        /// </summary>
        public void ArmRelariaTallyGlowPulseIfEligible()
        {
            var runId = Interlocked.Increment(ref relariaTallyGlowRunId);

            if (!HasRelariaTallyGlow || !RelariaPulseEffectsEnabled)
                return;

            ScheduleQuestPulse(runId, 0.0, FireRelariaTallyGlowPulse);
        }

        /// <summary>
        /// Invalidates any pulse chain currently queued for this glow without arming a new one. Called at
        /// logout alongside <see cref="DisarmRelariaAuraPulse"/> - cheap, since it never reads the quest
        /// stamp.
        /// </summary>
        public void DisarmRelariaTallyGlowPulse()
        {
            Interlocked.Increment(ref relariaTallyGlowRunId);
        }

        private void FireRelariaTallyGlowPulse(int runId)
        {
            // Stale chain from a previous login or a re-arm that has since superseded this one - stop
            // silently rather than continuing to fire.
            if (runId != relariaTallyGlowRunId)
                return;

            if (!HasRelariaTallyGlow || !RelariaPulseEffectsEnabled)
                return;

            if (CurrentLandblock != null)
                ApplyVisualEffects(ReadRelariaTallyGlowScript());

            var repeatSeconds = Math.Max(1, PropertyManager.GetLong("ml_relaria_tally_glow_pulse_seconds", DefaultRelariaTallyGlowPulseSeconds).Item);

            ScheduleQuestPulse(runId, repeatSeconds, FireRelariaTallyGlowPulse);
        }

        private static bool relariaTallyGlowScriptWarned;

        /// <summary>Reads ml_relaria_tally_glow_script and resolves it through <see cref="ResolveRelariaTallyGlowScript"/>.</summary>
        private static PlayScript ReadRelariaTallyGlowScript()
        {
            var configured = PropertyManager.GetLong("ml_relaria_tally_glow_script", DefaultRelariaTallyGlowScript).Item;

            var script = ResolveRelariaTallyGlowScript(configured, out var valid);

            if (!valid && !relariaTallyGlowScriptWarned)
            {
                relariaTallyGlowScriptWarned = true;
                log.Warn($"ml_relaria_tally_glow_script {configured} is not a defined PlayScript; using {(uint)script} ({script})");
            }

            return script;
        }

        /// <summary>
        /// The pure half of resolving ml_relaria_tally_glow_script: a value that does not name a defined
        /// PlayScript member is refused rather than cast blindly, and falls back to
        /// <see cref="DefaultRelariaTallyGlowScript"/>. Same shape as
        /// Player_RelariaAura.ResolveRelariaAuraScript. Testable without PropertyManager, unlike
        /// <see cref="ReadRelariaTallyGlowScript"/> which reads it.
        /// </summary>
        public static PlayScript ResolveRelariaTallyGlowScript(long configured, out bool valid)
        {
            valid = configured > 0 && configured <= uint.MaxValue
                    && Enum.IsDefined(typeof(PlayScript), (PlayScript)(uint)configured);

            return valid ? (PlayScript)(uint)configured : (PlayScript)DefaultRelariaTallyGlowScript;
        }
    }
}
