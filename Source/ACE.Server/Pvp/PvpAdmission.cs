namespace ACE.Server.Pvp
{
    /// <summary>
    /// The queue-join preflight, as a plain input record (Docs/Pvp/DESIGN.md "Joining the queue" >
    /// "Refused when the player:"). No live-server type is read here - every fact the check needs is a
    /// field on this record, supplied by the (PR C) caller.
    /// </summary>
    public sealed record PvpAdmissionRequest(
        bool ArenaEnabled,
        bool ModeEnabled,
        bool AlreadyInInstance,
        bool InRespite,
        bool HasActivePkTimer,
        bool IsOlthoi,
        bool IsMule,
        bool IsDead,
        bool IsTeleporting,
        bool AlreadyQueued,
        bool AlreadyInMatch,
        bool HasClearedThread,
        bool IsDenylisted = false,
        bool IsTemplateAccount = false,
        bool IsPvpTemplated = false);

    public static class PvpAdmission
    {
        public static PvpAdmissionRefusal Evaluate(PvpAdmissionRequest r)
        {
            if (!r.ArenaEnabled)
                return PvpAdmissionRefusal.ArenaDisabled;

            if (!r.ModeEnabled)
                return PvpAdmissionRefusal.ModeDisabled;

            // pvp_arena_denylist (Docs/Pvp/DESIGN.md "Joining the queue"), ported from Doctide
            // `arenas_blacklist`: refused ahead of every other per-character check, so a denylisted
            // character's refusal never depends on which other admission check would otherwise fire first.
            if (r.IsDenylisted)
                return PvpAdmissionRefusal.Denylisted;

            // PvP Template Facets (Docs/Pvp/TEMPLATES.md "Freezing"): a character on the template account is an
            // authoring source, never a fighter.
            if (r.IsTemplateAccount)
                return PvpAdmissionRefusal.TemplateAccount;

            if (r.AlreadyInMatch)
                return PvpAdmissionRefusal.AlreadyInMatch;

            if (r.AlreadyQueued)
                return PvpAdmissionRefusal.AlreadyQueued;

            if (r.AlreadyInInstance)
                return PvpAdmissionRefusal.AlreadyInInstance;

            // The PK facet is never checked here: owner ruling 2026-10-03 (Docs/Pvp/TEMPLATES.md "Rulings") dropped it for every
            // templated mode, and owner ruling 2026-10-15 (one Thread clear replaces the level and PK facet requirements) dropped
            // it for the rest, so any character may queue from any facet.

            // A character still carrying a template restore record (a restore that failed) is inert until staff
            // restore it, and a second apply would refuse anyway.
            if (r.IsPvpTemplated)
                return PvpAdmissionRefusal.TemplateLocked;

            if (r.InRespite)
                return PvpAdmissionRefusal.InRespite;

            if (r.HasActivePkTimer)
                return PvpAdmissionRefusal.ActivePkTimer;

            if (r.IsOlthoi)
                return PvpAdmissionRefusal.IsOlthoi;

            if (r.IsMule)
                return PvpAdmissionRefusal.IsMule;

            if (r.IsDead)
                return PvpAdmissionRefusal.IsDead;

            if (r.IsTeleporting)
                return PvpAdmissionRefusal.IsTeleporting;

            // Owner ruling 2026-10-15: one Thread clear replaces the level and PK facet requirements. HasClearedThread is
            // PvpEligibility.HasClearedAThread, the same predicate the one-time eligibility notice uses.
            if (!r.HasClearedThread)
                return PvpAdmissionRefusal.NoThreadCleared;

            return PvpAdmissionRefusal.None;
        }
    }
}
