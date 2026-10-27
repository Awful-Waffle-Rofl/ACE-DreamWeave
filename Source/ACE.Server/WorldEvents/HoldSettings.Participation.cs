using System.Collections.Generic;

namespace ACE.Server.WorldEvents
{
    /// <summary>Who counts as a participant of a Hold run or chain stage.</summary>
    public static partial class HoldSettings
    {
        static partial void RegisterParticipation(List<HoldSetting> r)
        {
            r.Add(Num(GroupParticipation, HoldKeys.ParticipantRadius, 60, 10, 300, HoldApplies.Live, null, false,
                "Players within this many metres of the anchor count: presence, audience scaling, local lines and the cache.",
                otherFile: "sources.json"));
            r.Add(Num(GroupParticipation, HoldKeys.MinPresenceSeconds, 0, 0, 1800, HoldApplies.Live, null, true,
                "A claimant needs this much (active) time inside the radius during the stage for the crate; less gets the coal prize (0 = off). Counted from when it is set."));
            r.Add(Num(GroupParticipation, HoldKeys.AfkSeconds, 0, 0, 1800, HoldApplies.Live, null, true,
                "A player who has not moved or dealt damage for this long (or is /afk) stops counting: not in the audience scaling, and idle time is not presence (0 = off)."));
            r.Add(Tog(GroupParticipation, HoldKeys.AdminsCount, false, HoldApplies.Live, null, true,
                "Admins and other staff count toward the audience scaling and presence (off: never, as today; --level still makes an empty field count as one)."));
        }

        /// <summary>The minimum presence rule for a Hold run or chain stage (true when off or for any other run).</summary>
        public static bool MeetsMinPresence(WorldEvent evt, uint characterGuid)
        {
            if (evt == null || !evt.IsHoldOrChain)
                return true;

            var min = Number(HoldKeys.MinPresenceSeconds, 0);

            return !(min > 0) || evt.PresenceSecondsOf(characterGuid) >= min - 1e-9;
        }
    }
}
