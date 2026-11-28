using System.Collections.Generic;

using ACE.Server.Avatars;

namespace ACE.Server.Tests.Avatars
{
    /// <summary>
    /// Builds the two counts the scaled prices read: R, the Avatar's active accounts, and S, the server's. One roster of
    /// <c>r</c> members (the monarch, one named vassal, then synthetic vassals, one account each) plus <c>s - r</c> active
    /// accounts that belong to no roster, all touched at <c>now</c>, so a recompute reads R = r and S = s.
    /// </summary>
    internal static class AvatarScaleFixture
    {
        public static AvatarRoster Roster(AccountActivityIndex activity, int r, int s, uint monarch, uint monarchAcct, uint vassal, uint vassalAcct, double now)
        {
            var members = new List<KeeperCandidate> { new KeeperCandidate(monarch, monarchAcct, 1, 0, 0) };
            activity.Touch(monarchAcct, now);

            if (r >= 2)
            {
                members.Add(new KeeperCandidate(vassal, vassalAcct, 1, 0, 1));
                activity.Touch(vassalAcct, now);
            }

            for (var i = 0; members.Count < r; i++)
            {
                members.Add(new KeeperCandidate(50_000u + (uint)i, 60_000u + (uint)i, 1, 0, 1));
                activity.Touch(60_000u + (uint)i, now);
            }

            for (var i = 0; i < s - r; i++)
                activity.Touch(70_000u + (uint)i, now);

            return new AvatarRoster(monarch, monarchAcct, false, members);
        }
    }
}
