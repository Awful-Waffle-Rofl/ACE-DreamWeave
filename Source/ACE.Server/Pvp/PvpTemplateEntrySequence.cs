using System;

using ACE.Server.Pvp.Templates;
using ACE.Server.WorldObjects;

namespace ACE.Server.Pvp
{
    /// <summary>
    /// The order a templated participant is placed in (Docs/Pvp/TEMPLATES.md "Lifecycle"), as a pure sequence over
    /// injected steps so it is unit tested without a live Player:
    ///
    ///   apply the template (queued on the player; the strip and the kit issue happen inside it)
    ///     -> success: EnterPvpMatch (the binding, the 9075 marker, PK Lite), then the teleport, both from inside the
    ///        apply's completion callback, which already runs on the player's own queue
    ///     -> failure: nothing is bound and nothing is teleported; the coordinator is told (EntryFailed) and removes
    ///        the participant without fault.
    ///
    /// The old placement queued the teleport blindly behind EnterPvpMatch. That cannot be done for a template match:
    /// the apply can fail, and a teleport queued behind it would put an untemplated (or half-templated) player in the
    /// arena. The teleport is therefore issued ONLY from a successful apply's callback.
    ///
    /// The bind happens in the same callback as the successful apply, so the heartbeat backstop
    /// (pvp_template_backstop_seconds, "templated with no live match") never sees a templated player without a binding.
    /// </summary>
    public static class PvpTemplateEntrySequence
    {
        /// <param name="binding">The participant's Staging binding; its Template is what gets applied.</param>
        /// <param name="apply">Player.ApplyPvpTemplate: queues the apply and calls back (on the player's queue) with the result.</param>
        /// <param name="enter">Player.EnterPvpMatchNow, then whether the player is now bound to this match.</param>
        /// <param name="teleport">The teleport to the spawn.</param>
        /// <param name="restore">Player.RestorePvpTemplate: takes the template off again when the bind itself fails.</param>
        /// <param name="reportFailure">
        /// Tells the coordinator (a PvpIntentKind.EntryFailed intent): a reason for the log, whether the player caused it
        /// (locked out), and whether it was only a busy player (retried for a short window before it counts).
        /// </param>
        /// <param name="logError">Logs a thrown step.</param>
        public static void Run(
            PvpPlayerBinding binding,
            Action<PvpTemplateDefinition, Guid, Action<PvpTemplateApplyResult>> apply,
            Func<bool> enter,
            Action teleport,
            Action<string> restore,
            Action<string, bool, bool> reportFailure,
            Action<string, Exception> logError)
        {
            if (binding?.Match == null || binding.Template == null)
            {
                // Templates are mandatory (owner ruling 2026-10-03): a binding with no template is never placed.
                reportFailure("the binding carries no template", false, false);
                return;
            }

            var matchId = binding.Match.MatchId;

            apply(binding.Template, matchId, result =>
            {
                if (result == null || !result.Success)
                {
                    reportFailure($"template {binding.Template.Key} did not apply: {result?.Refusal ?? "no result"}{(result?.StillTemplated == true ? " (the player is STILL templated and inert)" : "")}",
                        result?.PlayerCaused == true, result?.Busy == true);
                    return;
                }

                bool entered;

                try
                {
                    entered = enter();
                }
                catch (Exception ex)
                {
                    logError($"EnterPvpMatch threw after template {binding.Template.Key} applied for match {matchId}", ex);
                    entered = false;
                }

                if (!entered)
                {
                    // Never leave a templated player unbound and unplaced: take the template off again here rather than
                    // waiting for the heartbeat backstop.
                    try
                    {
                        restore($"match {matchId} entry failed after the template applied");
                    }
                    catch (Exception ex)
                    {
                        logError($"the restore after a failed EnterPvpMatch threw for match {matchId}", ex);
                    }

                    reportFailure("EnterPvpMatch did not bind the player after the template applied", false, false);
                    return;
                }

                try
                {
                    teleport();
                }
                catch (Exception ex)
                {
                    // Bound and templated but not moved: the player never arrives, so the staging timeout cancels the
                    // match without fault and its exit restores them.
                    logError($"the teleport into match {matchId} threw", ex);
                }
            });
        }
    }
}
