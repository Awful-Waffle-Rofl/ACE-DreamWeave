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
        int CharacterLevel,
        int MinLevel,
        bool IsDenylisted = false,
        bool IsTemplateAccount = false,
        bool IsPvpTemplated = false,
        bool RequiresPkFacet = false,
        bool OnPkFacet = true);

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

            // Owner ruling 2026-10-03 (Docs/Pvp/TEMPLATES.md "Rulings"): the PK facet is not required for a templated
            // mode, which is every mode by default; any character may queue from any facet. Only a room whose modes all
            // opted out of templates (none today) still requires it, in the position this check always had.
            if (r.RequiresPkFacet && !r.OnPkFacet)
                return PvpAdmissionRefusal.NotOnPkFacet;

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

            if (r.CharacterLevel < r.MinLevel)
                return PvpAdmissionRefusal.BelowMinLevel;

            return PvpAdmissionRefusal.None;
        }
    }
}
