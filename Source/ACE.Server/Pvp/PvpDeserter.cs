using System;

using ACE.Server.Network.Enum;
using ACE.Server.Pvp.Battlegrounds;

namespace ACE.Server.Pvp
{
    /// <summary>
    /// One character's deserter state (Docs/Pvp/BATTLEGROUNDS.md "Deserter lockout"), exactly as it is stored: PropertyInt
    /// PvpDeserterStrikes (9089), PropertyInt64 PvpDeserterLastStrikeAt (9026) and PvpDeserterLockoutUntil (9025). Both times are unix
    /// seconds. Always written as a whole, as absolute values, so a repeated or late write can never double-count.
    /// </summary>
    public sealed record PvpDeserterRecord(int Strikes, long LastStrikeAt, long LockoutUntil)
    {
        public static readonly PvpDeserterRecord None = new PvpDeserterRecord(0, 0, 0);
    }

    /// <summary>
    /// What /arenaadmin deserter shows: the effective record, the strike count after decay as of now (what the next strike builds on), and
    /// the lockout seconds left (0 when none, or when the switch is off).
    /// </summary>
    public sealed record PvpDeserterStatus(PvpDeserterRecord Record, int CurrentStrikes, long SecondsRemaining);

    /// <summary>
    /// /arenaadmin deserter clear: the zeroed record, and whether it was handed to the conduct seam to store (false when the player gateway
    /// does not implement <see cref="IPvpConductGateway"/>: cleared in memory only).
    /// </summary>
    public sealed record PvpDeserterClearResult(PvpDeserterRecord Record, bool Stored);

    /// <summary>
    /// Every way a seat can leave a match, for the deserter table. The coordinator maps each <see cref="ParticipantExit"/> to one of these
    /// (<see cref="PvpDeserterRules.DepartureOf"/>) and overrides it where the exit is the server's doing, not the player's. The match-level
    /// ends (admin cancel, a cancel before Live, a space failure) never reach the per-seat exit at all; they are listed so the table names
    /// every case. Appending a value fails <see cref="PvpDeserterRules.Classify"/> (it throws) until it is classified.
    /// </summary>
    public enum PvpDeparture
    {
        /// <summary>Eliminated (a death that takes the seat out). Never a desertion.</summary>
        Died,

        /// <summary>/arena leave, confirmed (ParticipantExit.ForfeitCommand).</summary>
        LeaveCommand,

        /// <summary>Logged out, or dropped and timed out (ParticipantExit.ForfeitLogout from the logout hook or the walkout poll).</summary>
        Logout,

        /// <summary>Walked, recalled or portaled out of the match instance (ParticipantExit.ForfeitLeft).</summary>
        WalkedOut,

        /// <summary>Removed for idling (ParticipantExit.ForfeitInactive).</summary>
        IdleRemoval,

        /// <summary>Removed for feeding (ParticipantExit.ForfeitFeeding).</summary>
        FeedingRemoval,

        /// <summary>Offline when the coordinator placed the seat (Staging): recorded as a logout forfeit, but before Live.</summary>
        OfflineAtPlacement,

        /// <summary>Ejected because the template backstop could not move personal items off them: a server gate miss.</summary>
        TemplateBackstopEject,

        /// <summary>Any exit while the server is shutting down (the shutdown logs every player off).</summary>
        ServerShutdown,

        /// <summary>/arenaadmin cancel. Match-level: the seats are exited by the cancel, never through the per-seat exit.</summary>
        AdminCancel,

        /// <summary>A match canceled before Live (declines, too few players, staging timeout, denylist). Match-level.</summary>
        CanceledBeforeLive,

        /// <summary>The map or instance failed (allocation, readiness, fixtures, template entry). Match-level, or before Live.</summary>
        SpaceFailure,

        /// <summary>
        /// A logout the server or staff forced (a boot, a forced logoff, an IP-limit eviction, a failed save, a ban, the world closing):
        /// see <see cref="PvpDeserterRules.IsServerCausedTermination"/>. Appended last.
        /// </summary>
        StaffOrServerLogoff
    }

    /// <summary>Whether a departure is a deserter strike.</summary>
    public enum PvpDeserterVerdict
    {
        NoStrike,
        Strike
    }

    /// <summary>The deserter exit table and scope (Docs/Pvp/BATTLEGROUNDS.md "Deserter lockout"). Pure.</summary>
    public static class PvpDeserterRules
    {
        /// <summary>
        /// THE EXIT TABLE. A strike is a departure that is the PLAYER's doing; anything the server or staff caused is not. Throws for a value
        /// not listed, so a new departure cannot ship unclassified (PvpDeserterTests enumerates every value).
        /// </summary>
        public static PvpDeserterVerdict Classify(PvpDeparture departure)
        {
            switch (departure)
            {
                case PvpDeparture.LeaveCommand:
                case PvpDeparture.Logout:
                case PvpDeparture.WalkedOut:
                case PvpDeparture.IdleRemoval:
                case PvpDeparture.FeedingRemoval:
                case PvpDeparture.OfflineAtPlacement:
                    return PvpDeserterVerdict.Strike;

                case PvpDeparture.Died:
                case PvpDeparture.TemplateBackstopEject:
                case PvpDeparture.ServerShutdown:
                case PvpDeparture.AdminCancel:
                case PvpDeparture.CanceledBeforeLive:
                case PvpDeparture.SpaceFailure:
                case PvpDeparture.StaffOrServerLogoff:
                    return PvpDeserterVerdict.NoStrike;

                default:
                    throw new ArgumentOutOfRangeException(nameof(departure), departure, "unclassified deserter departure");
            }
        }

        /// <summary>The departure an ordinary exit stands for. Throws for an unmapped <see cref="ParticipantExit"/>.</summary>
        public static PvpDeparture DepartureOf(ParticipantExit exit)
        {
            switch (exit)
            {
                case ParticipantExit.Died: return PvpDeparture.Died;
                case ParticipantExit.ForfeitCommand: return PvpDeparture.LeaveCommand;
                case ParticipantExit.ForfeitLogout: return PvpDeparture.Logout;
                case ParticipantExit.ForfeitLeft: return PvpDeparture.WalkedOut;
                case ParticipantExit.ForfeitInactive: return PvpDeparture.IdleRemoval;
                case ParticipantExit.ForfeitFeeding: return PvpDeparture.FeedingRemoval;

                default:
                    throw new ArgumentOutOfRangeException(nameof(exit), exit, "ParticipantExit with no deserter departure");
            }
        }

        /// <summary>
        /// Whether a session termination reason means the logout was forced by the server or staff, so a match forfeit it causes is NOT a
        /// desertion: AccountBooted, ForcedLogOffRequested, AutoForcedLogOff, WorldClosed, ServerShuttingDown, CharacterSaveFailed,
        /// BiotaSaveFailed and AccountBanned. Everything else is the player's (or their connection's) doing, AccountLoggedIn included: a
        /// second login on the same account kicking the character is the player's own act (owner ruling). Null (no termination pending,
        /// an ordinary logout) is not server-caused. Throws for an unlisted value, so a new reason cannot ship unclassified. Pure.
        /// </summary>
        public static bool IsServerCausedTermination(SessionTerminationReason? reason)
        {
            if (reason == null)
                return false;

            switch (reason.Value)
            {
                case SessionTerminationReason.AccountBooted:
                case SessionTerminationReason.ForcedLogOffRequested:
                case SessionTerminationReason.AutoForcedLogOff:
                case SessionTerminationReason.WorldClosed:
                case SessionTerminationReason.ServerShuttingDown:
                case SessionTerminationReason.CharacterSaveFailed:
                case SessionTerminationReason.BiotaSaveFailed:
                case SessionTerminationReason.AccountBanned:
                    return true;

                case SessionTerminationReason.None:
                case SessionTerminationReason.PacketHeaderDisconnect:
                case SessionTerminationReason.AccountInformationInvalid:
                case SessionTerminationReason.AccountSelectCallbackException:
                case SessionTerminationReason.NetworkTimeout:
                case SessionTerminationReason.ClientSentNetworkErrorDisconnect:
                case SessionTerminationReason.BadHandshake:
                case SessionTerminationReason.PongSentClosingConnection:
                case SessionTerminationReason.NotAuthorizedNoPasswordOrGlsTicketIncludedInLoginReq:
                case SessionTerminationReason.NotAuthorizedAccountNotFound:
                case SessionTerminationReason.AccountInUse:
                case SessionTerminationReason.NotAuthorizedPasswordMismatch:
                case SessionTerminationReason.NotAuthorizedGlsTicketNotImplementedToProcLoginReq:
                case SessionTerminationReason.ClientConnectionFailure:
                case SessionTerminationReason.SendToSocketException:
                case SessionTerminationReason.AbnormalSequenceReceived:
                case SessionTerminationReason.AccountLoggedIn:
                case SessionTerminationReason.ClientVersionIncorrect:
                case SessionTerminationReason.DATsPatchingDisabled:
                case SessionTerminationReason.DATsNewerThanServer:
                    return false;

                default:
                    throw new ArgumentOutOfRangeException(nameof(reason), reason, "unclassified session termination reason");
            }
        }
        /// <summary>
        /// The queues the lockout covers: the battleground room and Tugak Brawl (mode key "ffa"). 1v1 and 2v2 are never struck and never
        /// refused. Takes the room key (a match's or a join's).
        /// </summary>
        public static bool InScope(string roomKey) =>
            string.Equals(roomKey, BattlegroundModes.RoomKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(roomKey, ArenaMapCatalog.FfaKey, StringComparison.OrdinalIgnoreCase);

        /// <summary>A UTC clock reading as unix seconds (a DateTime of Unspecified kind is taken as UTC, as the coordinator's clock is).</summary>
        public static long ToUnix(DateTime utc) =>
            new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();
    }

    /// <summary>
    /// The deserter ladder (Docs/Pvp/BATTLEGROUNDS.md "Deserter lockout"). Pure: the caller supplies the stored record, the clock (unix
    /// seconds) and the pvp_conduct_deserter_* dials.
    /// </summary>
    public static class PvpDeserterLadder
    {
        public const int MaxStrikes = 3;

        private const long SecondsPerDay = 86400;

        /// <summary>
        /// The strike count after decay: one strike drops for every FULL pvp_conduct_deserter_decay_days since the last strike. A decay of 0
        /// days means a strike never outlives the moment (always 0). A clock that went backwards (now before the last strike) decays nothing.
        /// The stored count is clamped to 0..3 first.
        /// </summary>
        public static int Decayed(int strikes, long lastStrikeAt, long now, long decayDays)
        {
            var s = Math.Clamp(strikes, 0, MaxStrikes);

            if (s == 0)
                return 0;

            if (decayDays <= 0)
                return 0;

            var elapsed = now - lastStrikeAt;

            if (elapsed <= 0)
                return s;

            var steps = elapsed / (Math.Min(decayDays, PvpConductTunables.MaxDeserterDecayDays) * SecondsPerDay);

            return (int)Math.Max(0, s - Math.Min(steps, MaxStrikes));
        }

        /// <summary>The lockout length in minutes for a strike count (1, 2, 3 or more = lockout_1/2/3); 0 for no strikes.</summary>
        public static long LockoutMinutes(int strikes, PvpConductDials dials)
        {
            if (strikes <= 0 || dials == null)
                return 0;

            if (strikes == 1)
                return Math.Clamp(dials.DeserterLockout1Minutes, 0, PvpConductTunables.MaxDeserterLockoutMinutes);

            if (strikes == 2)
                return Math.Clamp(dials.DeserterLockout2Minutes, 0, PvpConductTunables.MaxDeserterLockoutMinutes);

            return Math.Clamp(dials.DeserterLockout3Minutes, 0, PvpConductTunables.MaxDeserterLockoutMinutes);
        }

        /// <summary>
        /// One new strike: decay the stored count, add one (capped at 3), and lock out for that count's length from now. The result is the
        /// whole record to store. Null when pvp_conduct_deserter_enabled is off: no strike and no lockout.
        /// </summary>
        public static PvpDeserterRecord Next(int strikes, long lastStrikeAt, long now, PvpConductDials dials)
        {
            if (dials == null || !dials.DeserterEnabled)
                return null;

            var decayed = Decayed(strikes, lastStrikeAt, now, dials.DeserterDecayDays);
            var next = Math.Min(MaxStrikes, decayed + 1);
            var minutes = LockoutMinutes(next, dials);

            return new PvpDeserterRecord(next, now, now + minutes * 60);
        }

        /// <summary>Seconds of lockout left at <paramref name="now"/>; 0 when none (or the switch is off).</summary>
        public static long SecondsRemaining(PvpDeserterRecord record, long now, PvpConductDials dials)
        {
            if (record == null || dials == null || !dials.DeserterEnabled)
                return 0;

            return Math.Max(0, record.LockoutUntil - now);
        }
    }
}
