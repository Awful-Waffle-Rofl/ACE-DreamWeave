using System;
using System.Threading;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.MlTreasure;

// The shared, feature-agnostic action-chain scheduler below (ScheduleQuestPulse) is used by this aura AND
// by Player_RelariaTallyGlow.cs's distinct 100-charge glow. Each feature keeps its own run-id field,
// eligibility test and PlayScript - only the "queue a delayed self-check-and-reschedule" plumbing is
// shared, per the generalize-rather-than-copy-paste instruction that produced Player_RelariaTallyGlow.cs.

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// The Aun Relaria repeat-kill aura (MlRelariaTrophy.TryAwardRepeatKillRewards). A character who
    /// rolls the rare ml_relaria_repeat_aura_chance outcome is stamped with
    /// MlRelariaTrophy.RepeatAuraQuestName permanently, and from then on - while the preference below is
    /// on - carries a looping visual: a GameMessageScript re-broadcast on the player every
    /// ml_relaria_aura_pulse_seconds, exactly the WorldEventRewardDelivery ScheduleCacheEffect/
    /// ArmCacheEffect pattern but scoped to the player's own action chain instead of a landblock's.
    ///
    /// Nothing here is persisted except the quest stamp and the on/off preference - the pulse itself is a
    /// plain in-memory action chain, re-armed at every login (PlayerEnterWorld) and invalidated on
    /// logout, toggle-off, or a fresh arm by bumping <see cref="relariaAuraRunId"/>. A queued pulse
    /// compares its captured id against the current value before it plays or reschedules itself, so a
    /// chain left over from a previous login (or a preference just toggled off) silently stops instead of
    /// continuing to fire - and re-resolving by id rather than holding a captured reference is what keeps
    /// login/logout/login from ever running two pulses at once.
    ///
    /// Both this aura and Player_RelariaTallyGlow.cs's glow are additionally gated server-wide by
    /// ml_relaria_pulse_effects_enabled (owner ruling 2026-09-24: off by default, the pulses are judged
    /// too visually intrusive). <see cref="RelariaPulseEffectsEnabled"/> below is the single read shared
    /// by both files' Arm/Fire methods.
    /// </summary>
    partial class Player
    {
        /// <summary>
        /// Server-wide gate for both the repeat-kill aura pulse and the Tally of the Unburied glow pulse
        /// (ml_relaria_pulse_effects_enabled). Shared by both files so the two pulses are gated
        /// identically. Off flips a live pulse off at its next scheduled tick (the Fire methods re-check
        /// this before rescheduling); on takes effect at each player's next arm (login, or the moment the
        /// aura/glow is granted) rather than immediately for players already online.
        /// </summary>
        private static bool RelariaPulseEffectsEnabled => PropertyManager.GetBool("ml_relaria_pulse_effects_enabled").Item;
        private int relariaAuraRunId;

        /// <summary>ml_relaria_aura_pulse_seconds default: seconds between pulses.</summary>
        private const long DefaultRelariaAuraPulseSeconds = 10;

        /// <summary>
        /// ml_relaria_aura_script default: PlayScript.LayingofHands, an Aun/Empyrean radiant-light burst
        /// already shipped in the client - the same family of "holy light" effect the Aun Relaria lore
        /// calls for. Other Aun/light-themed candidates considered (see the PR description for the full
        /// list and how to compare them live with /relariaprobe): WeddingBliss (already the
        /// WorldEventRewardDelivery cache-sparkle default), VisionUpWhite, EnchantUpWhite,
        /// SpecialStateWhite.
        /// </summary>
        private const long DefaultRelariaAuraScript = (long)PlayScript.LayingofHands;

        /// <summary>
        /// Per-character preference set by /relaria aura on|off (PropertyBool.MlRelariaAuraEnabled).
        /// ABSENT means ON, matching this fork's other per-character preference toggles (PropertyBool
        /// 9059 ShowDotDamage, 9062 SummonAttackOnTarget).
        /// </summary>
        public bool RelariaAuraEnabled
        {
            get => GetProperty(PropertyBool.MlRelariaAuraEnabled) ?? true;
            set
            {
                if (value)
                    RemoveProperty(PropertyBool.MlRelariaAuraEnabled);
                else
                    SetProperty(PropertyBool.MlRelariaAuraEnabled, false);
            }
        }

        /// <summary>True once this character has ever rolled the aura. Never cleared.</summary>
        public bool HasRelariaAura => QuestManager.HasQuest(MlRelariaTrophy.RepeatAuraQuestName);

        /// <summary>
        /// Arms the pulse if this character has earned the aura and has not opted out. Safe to call any
        /// number of times (login, /relaria aura on, the moment the aura is granted) - each call bumps
        /// the run id first, so at most one pulse chain from this player is ever live regardless of how
        /// many times this has been called.
        /// </summary>
        public void ArmRelariaAuraPulseIfEligible()
        {
            var runId = Interlocked.Increment(ref relariaAuraRunId);

            if (!HasRelariaAura || !RelariaAuraEnabled || !RelariaPulseEffectsEnabled)
                return;

            ScheduleQuestPulse(runId, 0.0, FireRelariaAuraPulse);
        }

        /// <summary>
        /// Invalidates any pulse chain currently queued for this player without arming a new one. Called
        /// at logout - cheaper than ArmRelariaAuraPulseIfEligible for that call site because it never
        /// needs to read the quest stamp or the preference, which run on every logout in the game whether
        /// or not this character has ever touched Relaria.
        /// </summary>
        public void DisarmRelariaAuraPulse()
        {
            Interlocked.Increment(ref relariaAuraRunId);
        }

        /// <summary>
        /// Queues one delayed pulse callback on this player's own action chain. Shared by every
        /// quest-gated cosmetic pulse on Player (this aura and Player_RelariaTallyGlow's glow) - the
        /// caller owns its own run-id field and re-checks it (and its own eligibility) inside
        /// <paramref name="fire"/> before doing anything, exactly as <see cref="FireRelariaAuraPulse"/>
        /// does, since a stale chain from a previous login or a superseding re-arm must stop silently
        /// rather than continue to fire.
        /// </summary>
        private void ScheduleQuestPulse(int runId, double delaySeconds, Action<int> fire)
        {
            var chain = new ActionChain();
            chain.AddDelaySeconds(delaySeconds);
            chain.AddAction(this, () => fire(runId));
            chain.EnqueueChain();
        }

        private void FireRelariaAuraPulse(int runId)
        {
            // Stale chain from a previous login, a toggle-off, or a re-arm that has since superseded
            // this one - stop silently rather than continuing to fire.
            if (runId != relariaAuraRunId)
                return;

            if (!HasRelariaAura || !RelariaAuraEnabled || !RelariaPulseEffectsEnabled)
                return;

            if (CurrentLandblock != null)
                ApplyVisualEffects(ReadRelariaAuraScript());

            var repeatSeconds = Math.Max(1, PropertyManager.GetLong("ml_relaria_aura_pulse_seconds", DefaultRelariaAuraPulseSeconds).Item);

            ScheduleQuestPulse(runId, repeatSeconds, FireRelariaAuraPulse);
        }

        private static bool relariaAuraScriptWarned;

        /// <summary>Reads ml_relaria_aura_script and resolves it through <see cref="ResolveRelariaAuraScript"/>.</summary>
        private static PlayScript ReadRelariaAuraScript()
        {
            var configured = PropertyManager.GetLong("ml_relaria_aura_script", DefaultRelariaAuraScript).Item;

            var script = ResolveRelariaAuraScript(configured, out var valid);

            if (!valid && !relariaAuraScriptWarned)
            {
                relariaAuraScriptWarned = true;
                log.Warn($"ml_relaria_aura_script {configured} is not a defined PlayScript; using {(uint)script} ({script})");
            }

            return script;
        }

        /// <summary>
        /// The pure half of resolving ml_relaria_aura_script: a value that does not name a defined
        /// PlayScript member is refused rather than cast blindly, and falls back to
        /// <see cref="DefaultRelariaAuraScript"/>. Same shape as
        /// WorldEventRewardDelivery.ResolveCacheEffectScript. Testable without PropertyManager, unlike
        /// <see cref="ReadRelariaAuraScript"/> which reads it.
        /// </summary>
        public static PlayScript ResolveRelariaAuraScript(long configured, out bool valid)
        {
            valid = configured > 0 && configured <= uint.MaxValue
                    && Enum.IsDefined(typeof(PlayScript), (PlayScript)(uint)configured);

            return valid ? (PlayScript)(uint)configured : (PlayScript)DefaultRelariaAuraScript;
        }
    }
}
