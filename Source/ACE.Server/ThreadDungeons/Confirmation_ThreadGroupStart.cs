using System;
using System.Collections.Generic;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// Either Thread start confirmation: the group offer (spec 4.1 step 5, rulings R4-R6) and, since the explicit
    /// consent ruling of 2026-09-17, the solo fallback offered when no fellow survived formation.
    ///
    /// WHY THIS IS NOT Confirmation_Custom. The original reason - "Custom returns early on No, and here No must open
    /// a solo run" - is OBSOLETE: No now opens nothing, which is exactly what Custom does. Three reasons survive it,
    /// and all three still apply:
    ///
    ///   1. Custom invokes its action inline on whichever thread answered. This class instead hops onto the PLAYER's
    ///      own action queue, which is where the R5 re-validation has to run: it reads and re-parses an item in the
    ///      player's inventory.
    ///   2. Custom carries no state. This one carries the gem guid, the send time, the exclusions already shown, and
    ///      which of the two offers was sent, all of which the answer needs.
    ///   3. Custom has no timeout rule, and No is not inert for us the way it is for Custom: a No SAYS something.
    ///      Without the <see cref="IsLate"/> check below, an unanswered dialog would tell the player their Thread
    ///      stays closed as though they had refused it.
    ///
    /// Nothing is consumed or bound when either dialog is sent, so a refusal, a timeout and a dropped connection all
    /// leave the gem exactly as it was.
    ///
    /// Timeout (R6). For Yes_No, ConfirmationManager's 30 s abort only sends a message and leaves the entry; the
    /// client's automatic reply then arrives as a plain No. So an answer flagged as a timeout, or arriving at or
    /// after <see cref="LateAfter"/>, does nothing and says nothing.
    /// </summary>
    public class Confirmation_ThreadGroupStart : Confirmation
    {
        /// <summary>Half a second inside ConfirmationManager's 30 s timeout.</summary>
        public static readonly TimeSpan LateAfter = TimeSpan.FromSeconds(29.5);

        public uint GemGuid { get; }

        public DateTime SentUtc { get; }

        /// <summary>The exclusion lines already shown with the offer, so a Yes re-form reports only new ones. Never null.</summary>
        public IReadOnlyList<(string Name, RosterExclusionReason Reason)> OfferedExclusions { get; }

        /// <summary>
        /// True for the solo fallback ("no one can join right now - open it alone?"), false for the group offer.
        /// The answer handler branches on it: a Yes here is consent to a SOLO run and nothing else, so the roster is
        /// not re-formed, and a Yes on the group offer opens a group run or nothing.
        /// </summary>
        public bool SoloOffer { get; }

        public Confirmation_ThreadGroupStart(ObjectGuid playerGuid, uint gemGuid, DateTime sentUtc)
            : this(playerGuid, gemGuid, sentUtc, null, false)
        {
        }

        public Confirmation_ThreadGroupStart(ObjectGuid playerGuid, uint gemGuid, DateTime sentUtc,
            IReadOnlyList<(string Name, RosterExclusionReason Reason)> offeredExclusions)
            : this(playerGuid, gemGuid, sentUtc, offeredExclusions, false)
        {
        }

        public Confirmation_ThreadGroupStart(ObjectGuid playerGuid, uint gemGuid, DateTime sentUtc,
            IReadOnlyList<(string Name, RosterExclusionReason Reason)> offeredExclusions, bool soloOffer)
            : base(playerGuid, ConfirmationType.Yes_No)
        {
            GemGuid = gemGuid;
            SentUtc = sentUtc;
            OfferedExclusions = offeredExclusions ?? Array.Empty<(string Name, RosterExclusionReason Reason)>();
            SoloOffer = soloOffer;
        }

        public override void ProcessConfirmation(bool response, bool timeout = false)
        {
            var player = Player;
            if (player == null)
                return;

            if (IsLate(SentUtc, DateTime.UtcNow, timeout))
                return;

            var gemGuid = GemGuid;
            var offered = OfferedExclusions;
            var solo = SoloOffer;

            player.EnqueueAction(new ActionEventDelegate(() => ThreadDungeonGemHandler.OnGroupStartAnswered(player, gemGuid, response, offered, solo)));
        }

        /// <summary>True for a timeout, or for an answer at or after <see cref="LateAfter"/> from sending.</summary>
        internal static bool IsLate(DateTime sentUtc, DateTime nowUtc, bool timeout)
            => timeout || nowUtc - sentUtc >= LateAfter;
    }
}
