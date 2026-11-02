using System;

using log4net;

using ACE.Server.Managers;

namespace ACE.Server.Pvp
{
    /// <summary>
    /// The repeat-pairing settings (Docs/Pvp/DESIGN.md "Repeat-pairing cap"). A separate record from
    /// <see cref="PvpArenaDials"/> so the pairing feature's dials arrive without touching that record's many positional
    /// call sites. Only <see cref="PaidCap"/> is consumed by the cap itself; the other four are reserved here so the
    /// matchmaker and audit changes that read them need no further settings plumbing.
    /// </summary>
    /// <param name="PaidCap">pvp_arena_pair_paid_cap - paid 1v1 / 2v2 matches per opposing account pair per arena day. 0 or less = off.</param>
    /// <param name="MmRecentOpponents">pvp_arena_mm_recent_opponents - recent opponents the matchmaker avoids repeating. 0 = off.</param>
    /// <param name="AuditEnabled">pvp_arena_pair_audit_enabled - the win-trading audit switch.</param>
    /// <param name="AuditWindowHours">pvp_arena_pair_audit_window_hours - how far back the audit looks.</param>
    /// <param name="AuditMinSwaps">pvp_arena_pair_audit_min_swaps - alternating wins between a pair before the audit flags it.</param>
    public sealed record PvpPairingDials(
        long PaidCap,
        long MmRecentOpponents,
        bool AuditEnabled,
        long AuditWindowHours,
        long AuditMinSwaps);

    /// <summary>
    /// Reads the pairing settings. <see cref="Defaults"/> are the SAME values the PropertyManager entries ship with, and
    /// the fallback when a key cannot be read (ACE.Server.Tests throws on any uncached key), following
    /// <see cref="PvpTunables"/>.
    /// </summary>
    public static class PvpPairingTunables
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(PvpPairingTunables));

        public static Func<PvpPairingDials> DialSource = ReadFromProperties;

        public static readonly PvpPairingDials Defaults = new PvpPairingDials(
            PaidCap: 2,
            MmRecentOpponents: 3,
            AuditEnabled: true,
            AuditWindowHours: 24,
            AuditMinSwaps: 3);

        private static PvpPairingDials ReadFromProperties()
        {
            try
            {
                var d = Defaults;

                return new PvpPairingDials(
                    PaidCap: PropertyManager.GetLong("pvp_arena_pair_paid_cap", d.PaidCap).Item,
                    MmRecentOpponents: PropertyManager.GetLong("pvp_arena_mm_recent_opponents", d.MmRecentOpponents).Item,
                    AuditEnabled: PropertyManager.GetBool("pvp_arena_pair_audit_enabled", d.AuditEnabled).Item,
                    AuditWindowHours: PropertyManager.GetLong("pvp_arena_pair_audit_window_hours", d.AuditWindowHours).Item,
                    AuditMinSwaps: PropertyManager.GetLong("pvp_arena_pair_audit_min_swaps", d.AuditMinSwaps).Item);
            }
            catch (Exception ex)
            {
                log.Error("[PVP] could not read the pvp_arena_pair_* tunables; falling back to built-in defaults", ex);
                return Defaults;
            }
        }
    }
}
