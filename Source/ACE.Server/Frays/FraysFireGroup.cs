using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Frays
{
    /// <summary>
    /// A group camp (the Sky Fray): fellowship based, with one shared Heat. The lighter's own fellowship holds the fire: it needs group_min_size members online
    /// on different connections to light it, and every member is an owner (they fight and loot its creatures). The group has
    /// ONE Heat: it lives on one member's camp record (the keeper, at first the lighter), and every other member's record
    /// mirrors it, so their Pattern slots, Embers, rating and rewards all follow the shared Heat. The keeper's camp alone
    /// cools, opens gates, takes boons and fields the creature stream, scaled by how many members stand in the zone. When
    /// the keeper steps out, a member who is present takes the Heat over. A member's death costs the group group_death_drop
    /// levels.
    /// </summary>
    public sealed partial class FraysFire
    {
        /// <summary>The member whose camp record holds the group's shared Heat (0: none yet, or not a group camp).</summary>
        private uint groupLead;

        /// <summary>When the fellowship first fell under group_min_size members (0: it has enough).</summary>
        private double groupBelowMinSince;

        /// <summary>The member keeping the group's shared Heat (0 when none).</summary>
        public uint GroupLead => groupLead;

        /// <summary>A group member's camp that only mirrors the shared Heat (not its keeper).</summary>
        private bool IsFollower(FraysCamper c) => Def.Group && c != null && groupLead != 0 && c.Guid != groupLead && campers.ContainsKey(groupLead);

        /// <summary>Where a camper's Heat lives: their own camp, or a group camp's shared keeper.</summary>
        private FraysCamper HeatCamper(FraysCamper c) =>
            Def.Group && groupLead != 0 && campers.TryGetValue(groupLead, out var lead) ? lead : c;

        // ---- scaling by the members present ----------------------------------------------------------------------

        private int GroupPresent => Def.Group ? Math.Max(1, PresentCount) : 1;

        private double GroupHealthMult => Def.Group ? FraysGroup.HealthMult(GroupPresent, FraysSettings.Number(FraysSettings.GroupHealthPerMember)) : 1;

        private int GroupPackExtra => Def.Group ? FraysGroup.PackExtra(GroupPresent, FraysSettings.Number(FraysSettings.GroupPackPerMember)) : 0;

        private double GroupRateMult => Def.Group ? FraysGroup.RateMult(GroupPresent, FraysSettings.Number(FraysSettings.GroupRatePerMember)) : 1;

        // ---- lighting ----------------------------------------------------------------------------------------------

        // The middle camp (Sky Hearth) needs a bigger fellowship: group_min_size_middle (5).
        private int MinSize => Math.Max(1, FraysSettings.Int(Def.BigGroup ? FraysSettings.GroupMinSizeMiddle : FraysSettings.GroupMinSize));

        /// <summary>A fellowship's members who are online.</summary>
        private static List<Player> OnlineMembers(Fellowship fellowship)
        {
            if (fellowship?.FellowshipMembers == null)
                return new List<Player>();

            return fellowship.FellowshipMembers.Keys
                .Select(g => PlayerManager.GetOnlinePlayer(g))
                .Where(p => p != null && p.Fellowship == fellowship)
                .ToList();
        }

        /// <summary>How many different connections a set of players plays from.</summary>
        private static int DistinctConnections(IEnumerable<Player> players) =>
            players.Select(p => p.Session?.EndPointC2S?.Address?.ToString() ?? $"guid:{p.Guid.Full}").Distinct().Count();

        /// <summary>Why this player cannot light this group fire now, or null when they can.</summary>
        private string RefuseGroupLight(Player player)
        {
            var min = MinSize;

            if (Lit)
                return IsOwner(player)
                    ? $"This is your fellowship's fire. The group's Heat here is {LevelOf(player)}."
                    : $"This group fire belongs to {OwnerName}. Find a cold one, or join their fellowship.";

            if (player.Fellowship == null || FraysManager.IsDungeonFellowship(player.Fellowship))
                return $"This is a group campfire. Only a fellowship of {min} or more can light it: make a fellowship first.";

            if (FraysManager.FireOwnedBy(player.Fellowship) is FraysFire held)
                return $"Your fellowship already holds a group fire ({held.Def.RoomName}). One fire per fellowship.";

            if (RetakeRefusal(player, ACE.Common.Time.GetUnixTime()) is string retake)
                return retake;

            var minLevel = FraysSettings.Int(FraysSettings.MinLevel);

            if ((player.Level ?? 1) < minLevel && player.Session?.AccessLevel < AccessLevel.Advocate)
                return $"The fire will not take for you yet. Campfires answer only to level {minLevel} and above.";

            var members = OnlineMembers(player.Fellowship);
            var onePerIp = FraysSettings.Toggle(FraysSettings.OnePerIp) && player.Session?.AccessLevel < AccessLevel.Advocate;

            if (!FraysGroup.MeetsMinSize(members.Count, DistinctConnections(members), min, onePerIp))
                return onePerIp && members.Count >= min
                    ? $"A group fire needs {min} players: your fellowship has {members.Count} characters but fewer than {min} players."
                    : $"A group fire needs a fellowship of {min} or more online. Yours has {members.Count}.";

            return null;
        }

        /// <summary>The fire's owner line: the fellowship's name and its leader.</summary>
        private static string GroupOwnerName(Player player)
        {
            var f = player.Fellowship;

            if (f == null)
                return player.Name;

            var leader = PlayerManager.GetOnlinePlayer(f.FellowshipLeaderGuid)?.Name ?? player.Name;
            return string.IsNullOrWhiteSpace(f.FellowshipName) ? $"{leader}'s fellowship" : $"{f.FellowshipName} ({leader}'s fellowship)";
        }

        private string GroupLitLine(Player player)
        {
            var members = OnlineMembers(OwnerFellowship).Count;

            return $"You light the group campfire. It belongs to your fellowship ({members} members online), and the group shares one Heat. "
                + $"Each member standing in the zone makes its creatures tougher and its packs bigger. A death costs the group {FraysGroup.DeathDrop(FraysSettings.Int(FraysSettings.GroupDeathDrop))} Heat. "
                + $"Leaving the fellowship takes you out of the camp; under {MinSize} members for {FraysSettings.Number(FraysSettings.AwaySeconds) / 60:0} minutes, the fire goes out.";
        }

        // ---- owners --------------------------------------------------------------------------------------------

        /// <summary>The group's members who are online and still in its fellowship.</summary>
        private List<Player> OnlineGroupOwners() => OnlineMembers(OwnerFellowship);

        /// <summary>
        /// The owners: every member of the fellowship who is online and in it. A member who logs off is remembered for the
        /// grace time (the fire waits while anyone of the group is remembered); one who leaves the fellowship stops being
        /// an owner at once.
        /// </summary>
        private bool RefreshGroupOwners(double now)
        {
            var next = new HashSet<uint>();

            foreach (var p in OnlineGroupOwners())
            {
                next.Add(p.Guid.Full);
                remembered[p.Guid.Full] = true;
            }

            foreach (var guid in remembered.Keys.ToList())
            {
                if (next.Contains(guid))
                    continue;

                var p = PlayerManager.GetOnlinePlayer(guid);

                if (p == null)
                    remembered[guid] = false;   // logged off: the camp waits for them for the grace time
                else
                {
                    remembered.Remove(guid);    // online but out of the fellowship: out of the camp
                    FraysManager.Tell(p, $"You are no longer in the fellowship that holds the group fire in {Def.RoomName}, so you leave its camp.");
                }
            }

            if (next.SetEquals(ownerGuids))
                return false;

            ownerGuids = next;
            return true;
        }

        /// <summary>Why the group fire goes out now (the fellowship is gone, or too small for too long), or null.</summary>
        private string GroupOutReason(double now)
        {
            var fellowship = OwnerFellowship;

            if (fellowship?.FellowshipMembers == null || fellowship.FellowshipMembers.Count == 0)
                return "the fellowship disbanded";

            var count = fellowship.FellowshipMembers.Count;

            if (count >= MinSize)
            {
                groupBelowMinSince = 0;
                return null;
            }

            if (groupBelowMinSince <= 0)
            {
                groupBelowMinSince = now;

                foreach (var p in OnlineGroupOwners())
                    FraysManager.Tell(p, $"Your fellowship is down to {count}. A group fire needs {MinSize}: bring the fellowship back up within {FraysSettings.Number(FraysSettings.AwaySeconds) / 60:0} minutes or it goes out.");

                return null;
            }

            return now - groupBelowMinSince >= FraysSettings.Number(FraysSettings.AwaySeconds) ? $"the fellowship stayed under {MinSize} members" : null;
        }

        // ---- the shared Heat ---------------------------------------------------------------------------------------

        /// <summary>
        /// The group's starting Heat for a new camp record: the keeper's Heat when there is one, else the lowest start of
        /// the members present (so the group starts where its least-rated member may).
        /// </summary>
        private int GroupStartFor(Player p)
        {
            if (groupLead != 0 && groupLead != p.Guid.Full && campers.TryGetValue(groupLead, out var lead))
                return lead.Level;

            var floor = FraysSettings.Int(FraysSettings.HeatStart);
            var starts = OnlineGroupOwners().Where(m => InZone(m.Location, Margin, 0)).DefaultIfEmpty(p).Select(m =>
            {
                var rec = FraysManager.Record(m);
                return Math.Max(floor, FraysRules.StartLevel(rec.StartHeat, rec.GroupRating, FraysPerks.CapFor(rec)));
            });

            return FraysGroup.StartLevel(starts);
        }

        /// <summary>
        /// Before the camp tick: the keeper must be a member who is present. With none yet, the first member present takes
        /// it; when the keeper has stepped out, a present member takes over the shared Heat.
        /// </summary>
        private void EnsureGroupLead(List<Player> present, double now)
        {
            if (present.Count == 0)
                return;

            if (present.Any(p => p.Guid.Full == groupLead) && campers.ContainsKey(groupLead))
                return;

            campers.TryGetValue(groupLead, out var old);

            // The next keeper: a present member who already has a camp record, else the first present.
            var next = present.FirstOrDefault(p => campers.ContainsKey(p.Guid.Full)) ?? present[0];

            if (old == null)
            {
                groupLead = next.Guid.Full;
                CamperFor(next, now);
                return;
            }

            var nc = CamperFor(next, now);   // joins as a follower at the shared Heat
            MoveLead(old, nc);
            groupLead = nc.Guid;

            foreach (var p in OnlineGroupOwners())
                FraysManager.Tell(p, $"{next.Name} now keeps the group's Heat at {Def.RoomName}.");
        }

        /// <summary>Moves the shared Heat and everything that goes with it from one member's camp record to another's.</summary>
        private static void MoveLead(FraysCamper from, FraysCamper to)
        {
            to.Level = from.Level;
            to.Progress = from.Progress;
            to.PointFraction = from.PointFraction;
            to.DecayAccumulator = from.DecayAccumulator;
            to.LastKillAt = Math.Max(to.LastKillAt, from.LastKillAt);
            to.CoolStage = from.CoolStage;
            to.PeakLevel = Math.Max(to.PeakLevel, from.PeakLevel);

            to.Boons.Clear();
            to.Boons.AddRange(from.Boons);
            to.Offer = from.Offer;
            to.OfferAt = from.OfferAt;
            to.OfferShoutAt = from.OfferShoutAt;
            to.OffersOwed = from.OffersOwed;
            from.Offer = new List<string>();
            from.OffersOwed = 0;

            to.SnarlBar = from.SnarlBar;
            to.SnarlAlive = from.SnarlAlive;
            to.SnarlSpawnedAt = from.SnarlSpawnedAt;
            to.SnarlAddsBar = from.SnarlAddsBar;
            to.SnarlAdds = from.SnarlAdds;
            to.SnarlAddsSpawnedAt = from.SnarlAddsSpawnedAt;
            from.SnarlAddsBar = 0;
            from.SnarlAdds = new List<Creature>();
            from.SnarlBar = 0;
            from.SnarlAlive = null;

            to.PointReadyAt.Clear();
            foreach (var kv in from.PointReadyAt)
                to.PointReadyAt[kv.Key] = kv.Value;

            to.NextSpawnAt = from.NextSpawnAt;
        }

        /// <summary>Every member's camp record follows the keeper's: the shared Heat, its peak and the group's boons.</summary>
        private void MirrorGroupHeat()
        {
            if (!Def.Group || groupLead == 0 || !campers.TryGetValue(groupLead, out var lead))
                return;

            foreach (var c in campers.Values)
            {
                if (c == lead)
                    continue;

                c.Level = lead.Level;
                c.Progress = lead.Progress;
                c.PeakLevel = Math.Max(c.PeakLevel, lead.Level);

                if (!c.Boons.SequenceEqual(lead.Boons))
                {
                    c.Boons.Clear();
                    c.Boons.AddRange(lead.Boons);
                }
            }
        }

        // ---- telling the group ---------------------------------------------------------------------------------------

        /// <summary>The players a camp event is for: a group camp's online members, else just <paramref name="p"/>.</summary>
        private List<Player> CampPlayers(Player p)
        {
            if (!Def.Group)
                return p == null ? new List<Player>() : new List<Player> { p };

            var list = OnlineGroupOwners();

            if (p != null && !list.Contains(p))
                list.Add(p);

            return list;
        }

        /// <summary>A line for the camp: the whole group at a group camp, else the one camper.</summary>
        private void TellCamp(Player p, string text)
        {
            foreach (var who in CampPlayers(p))
                FraysManager.Tell(who, text);
        }

        /// <summary>A centre-screen line for the camp: the whole group at a group camp, else the one camper.</summary>
        private void ShoutCamp(Player p, string text)
        {
            foreach (var who in CampPlayers(p))
                FraysManager.Shout(who, text);
        }

        /// <summary>A line to the group's other members (not <paramref name="except"/>).</summary>
        private void TellGroup(Player except, string text)
        {
            if (!Def.Group)
                return;

            foreach (var who in OnlineGroupOwners())
                if (who != except)
                    FraysManager.Tell(who, text);
        }
    }
}
