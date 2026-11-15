using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.WorldObjects;

namespace ACE.Server.Frays
{
    /// <summary>
    /// The group boon vote. At a group camp an Ember Boon offer is a vote: each member
    /// who fought in the last boon_vote_active_seconds (2 minutes) votes with /fp 1, 2 or 3, so an away member never holds
    /// it up. It closes when every such member has voted, or after boon_vote_seconds (20). The most votes wins; a tie, or no
    /// votes, goes to the keeper's boon plan.
    /// </summary>
    public sealed partial class FraysFire
    {
        /// <summary>Pure: the offer number with the most votes, or 0 on a tie or with no votes.</summary>
        public static int Tally(IEnumerable<int> votes, int offerCount)
        {
            var counts = (votes ?? Enumerable.Empty<int>()).Where(v => v >= 1 && v <= offerCount).GroupBy(v => v)
                .Select(g => (Choice: g.Key, Count: g.Count())).OrderByDescending(g => g.Count).ToList();

            if (counts.Count == 0 || counts.Count > 1 && counts[0].Count == counts[1].Count)
                return 0;

            return counts[0].Choice;
        }

        /// <summary>The members who vote: online, in this camp, and fighting in the last boon_vote_active_seconds.</summary>
        private List<Player> Voters(Player p, double now)
        {
            var window = FraysSettings.Number(FraysSettings.BoonVoteActiveSeconds);
            return CampPlayers(p).Where(m => campers.TryGetValue(m.Guid.Full, out var mc) && FraysRules.IsActive(mc.LastActiveAt, now, window)).ToList();
        }

        /// <summary>/fp n at a group camp: a vote. Returns what to tell the voter (null: the vote closed and was said).</summary>
        private string Vote(FraysCamper lead, Player player, int choice, double now)
        {
            var voters = Voters(player, now);

            if (voters.Count > 0 && !voters.Contains(player))
                return "Only members who fought in the last 2 minutes vote on this boon.";

            lead.Votes[player.Guid.Full] = choice;
            var name = FraysBoons.Find(lead.Offer[choice - 1])?.Name ?? "?";
            var voted = voters.Count(v => lead.Votes.ContainsKey(v.Guid.Full));
            TellCamp(player, $"{player.Name} votes for {name} ({voted} of {Math.Max(1, voters.Count)} voted).");

            if (voters.Count == 0 || voted >= voters.Count)
            {
                CloseVote(lead, player, now);
                return null;
            }

            return "";
        }

        /// <summary>The vote ends: the winner, or the keeper's plan on a tie or no votes.</summary>
        private void CloseVote(FraysCamper lead, Player p, double now)
        {
            var win = Tally(lead.Votes.Values, lead.Offer.Count);
            lead.Votes.Clear();

            if (win > 0)
            {
                TakeBoon(lead, p, lead.Offer[win - 1], "the group's vote");
                return;
            }

            var keeper = Managers.PlayerManager.GetOnlinePlayer(lead.Guid) ?? p;
            TakeBoon(lead, p, FraysBoons.AutoPick(lead.Offer, FraysManager.Record(keeper).BoonOrder, FraysManager.Record(keeper).BoonPlan, keeper.GetClassAbilityRank), "no clear winner, so the keeper's boon plan");
        }
    }
}
