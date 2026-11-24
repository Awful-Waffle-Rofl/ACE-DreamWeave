using System;
using System.Collections.Generic;

using ACE.Server.Avatars;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// Who hears a run's announcements. An ordinary run is heard server-wide; a quiet private Avatar event (Avatar phase 1,
    /// Task 12) is heard only by the Avatar's allegiance. Payout, participation and rewards are not touched.
    /// </summary>
    public sealed partial class WorldEvent
    {
        /// <summary>Copied from the request: false, with <see cref="AvatarId"/> set, keeps this run's announcements to the Avatar's allegiance.</summary>
        public bool AnnounceServerWide { get; }

        private readonly ulong avatarFavorPrice;
        private bool avatarFavorSettled;

        /// <summary>
        /// Settles the Favor an Avatar's private event reserved, from <see cref="Transition"/> (the one place State is written):
        /// committed exactly once when the run first reaches Active, released exactly once if it ends (Resolved, Cleanup or Done)
        /// without ever having been Active, so an event that never started is free. Reservations are memory only, so a restart
        /// before Active releases them implicitly (ReservedFavor comes back as 0).
        /// </summary>
        private void SettleAvatarFavor(WorldEventState to)
        {
            if (AvatarId == null || avatarFavorPrice == 0 || avatarFavorSettled)
                return;

            bool commit;

            if (to == WorldEventState.Active)
                commit = true;
            else if (to == WorldEventState.Resolved || to == WorldEventState.Cleanup || to == WorldEventState.Done)
                commit = false;
            else
                return;

            avatarFavorSettled = true;

            try
            {
                WorldEventHooks.AvatarFavorSettleSink(AvatarId.Value, avatarFavorPrice, commit);
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] Run {RunId}: settling the Avatar's Favor ({(commit ? "commit" : "release")}) threw", ex);
            }
        }

        /// <summary>
        /// Send point for a run's HEADLINE lines (teaser, start, active, time-left, outcome, fail, death protection). A quiet
        /// Avatar event sends them to the Avatar's allegiance only, in the allegiance look, and never to Discord. Every other
        /// run - public, or a private run that was bought announced - goes through <see cref="WorldEventAnnouncer.Broadcast"/>
        /// unchanged, so the announced price still buys the server-wide headline and the Discord teaser. World thread.
        /// </summary>
        internal void Announce(string msg, bool relayToDiscord = false)
        {
            if (string.IsNullOrEmpty(msg))
                return;

            if (IsQuietPrivate)
            {
                SendToAllegiance(msg);
                return;
            }

            WorldEventAnnouncer.Broadcast(msg, relayToDiscord);
        }

        /// <summary>
        /// Send point for an IN-RUN line that is world-wide for a public run (boss arrived, boss health, objective lines): a
        /// public run broadcasts it as before; a private run sends it to the allegiance plus, when it was bought announced, the
        /// radius-local players (<see cref="AnnounceToAllegianceAndLocal"/>). World thread.
        /// </summary>
        internal void AnnounceInRunWide(string msg)
        {
            if (string.IsNullOrEmpty(msg))
                return;

            if (AvatarId != null)
                AnnounceToAllegianceAndLocal(msg);
            else
                WorldEventAnnouncer.Broadcast(msg);
        }

        /// <summary>
        /// Send point for an IN-RUN line meant for the people around the anchor (waves, local objective lines). A public run
        /// sends it radius-local exactly as before; a quiet private run sends it to the allegiance only; an announced private
        /// run sends it to the allegiance plus the radius-local players. The ONLY place outside WorldEventAnnouncer that calls
        /// <see cref="WorldEventAnnouncer.Local"/>. World thread.
        /// </summary>
        internal void AnnounceLocal(string msg)
        {
            if (string.IsNullOrEmpty(msg))
                return;

            if (AvatarId != null)
                AnnounceToAllegianceAndLocal(msg);
            else
                WorldEventAnnouncer.LocalAnnounceSink(this, msg, null);
        }

        /// <summary>The last monarch an allegiance send resolved (world thread only).</summary>
        private uint? lastMonarch;

        private bool IsQuietPrivate => AvatarId != null && !AnnounceServerWide;

        /// <summary>A private run's in-run line: the allegiance always, plus the radius-local copy only for an announced run (outsiders were invited).</summary>
        private void AnnounceToAllegianceAndLocal(string msg)
        {
            var reached = SendToAllegiance(msg);

            if (IsQuietPrivate)
                return;

            // Skip anyone the allegiance send already reached so a member near the anchor hears each line once.
            try
            {
                WorldEventAnnouncer.LocalAnnounceSink(this, msg, reached);
            }
            catch (Exception ex)
            {
                log.Warn($"[WORLDEVENT] Run {RunId}: local announcement failed", ex);
            }
        }

        /// <summary>
        /// Resolves the CURRENT monarch at send time (the monarch can change mid-event) and sends the line, in the allegiance
        /// look, through <see cref="WorldEventAnnouncer.AllegianceSink"/>. With no allegiance the line is logged only. Returns
        /// the guids reached (null when none).
        /// </summary>
        private ISet<uint> SendToAllegiance(string msg)
        {
            // Falls back to the last monarch this run resolved, so an Avatar unbound mid-run (absorbed or retired, MonarchGuid
            // cleared) does not leave the run's remaining lines with nobody to hear them.
            var monarch = AvatarManager.Get(AvatarId.Value)?.MonarchGuid ?? lastMonarch;

            if (monarch != null)
                lastMonarch = monarch;

            if (monarch == null)
            {
                log.Info($"[WORLDEVENT] Run {RunId}: private Avatar event line has no allegiance to send to, Avatar {AvatarId} has no monarch: {msg}");
                return null;
            }

            try
            {
                return WorldEventAnnouncer.AllegianceSink(monarch.Value, WorldEventAnnouncer.AllegianceLook(msg));
            }
            catch (Exception ex)
            {
                log.Warn($"[WORLDEVENT] Run {RunId}: allegiance announcement failed", ex);
                return null;
            }
        }
    }
}
