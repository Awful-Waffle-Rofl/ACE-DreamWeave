using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.WorldEvents.Defs;

namespace ACE.Server.WorldEvents
{
    /// <summary>The chain: whether a won Hold leads on, where, and how the stage change is paced.</summary>
    public static partial class HoldSettings
    {
        static partial void RegisterChain(List<HoldSetting> r)
        {
            r.Add(Tog(GroupChain, HoldKeys.Chain, true, HoldApplies.StageEnd, null, true,
                "On: a won stage leads on to the next. Off: one and done (the chain ends there, with its closing lines)."));
            r.Add(Num(GroupChain, HoldKeys.ChainChance, 1, 0, 1, HoldApplies.StageEnd, null, true,
                "The chance a won stage leads on (1 = always, as today). Rolled once per stage."));
            r.Add(Int(GroupChain, HoldKeys.ChainMaxLength, 2, 2, 5, HoldApplies.StageEnd, "chain.maxLength", false,
                "The most stages in one chain, the first included.", g => F(g.Chain?.MaxLength ?? 0)));

            r.Add(TextSetting(GroupChain, HoldKeys.ChainNextSource, ChainDef.DefaultNextSource, HoldApplies.StageEnd, "chain.nextSource", false,
                "The next stage's source (a sources.json id) or random.", g => g.Chain?.NextSource));
            r.Add(TextSetting(GroupChain, HoldKeys.ChainNextGoal, ChainDef.DefaultNextGoal, HoldApplies.StageEnd, "chain.nextGoal", false,
                "The next stage's goal (a goals.json id the source supports) or random.", g => g.Chain?.NextGoal));
            r.Add(TextSetting(GroupChain, HoldKeys.ChainNextBoss, ChainDef.DefaultNextBoss, HoldApplies.StageEnd, "chain.nextBoss", false,
                "The champion pool or a forced boss: auto (a named boss of the family), a bosses.json id, family_champion, none or random.",
                g => g.Chain?.NextBoss));
            r.Add(TextSetting(GroupChain, HoldKeys.ChainNextFamily, "random", HoldApplies.StageEnd, null, true,
                "The next stage's monster family (a families.json id). Not set or random: it rolls, as today."));

            r.Add(Tog(GroupChain, HoldKeys.ChainTownNetwork, true, HoldApplies.StageEnd, "chain.toAnotherTown", false,
                "Town Network only: the next stage goes to another town of the Town Network (off: 250 to 500 m away).",
                g => F(g.Chain?.ToAnotherTown ?? false)));
            r.Add(Num(GroupChain, HoldKeys.ChainTownMinDistance, 800, 200, 5000, HoldApplies.StageEnd, "chain.townMinDistance", false,
                "The next town is at least this far away (m).", g => F(g.Chain?.TownMinDistance ?? 0)));
            r.Add(Num(GroupChain, HoldKeys.ChainTownMaxDistance, 8000, 1000, 30000, HoldApplies.StageEnd, "chain.townMaxDistance", false,
                "... and at most this far (m).", g => F(g.Chain?.TownMaxDistance ?? 0)));
            r.Add(Int(GroupChain, HoldKeys.ChainTownChoices, 4, 1, 10, HoldApplies.StageEnd, "chain.townChoices", false,
                "It picks at random among this many nearest towns in range.", g => F(g.Chain?.TownChoices ?? 0)));
            r.Add(Num(GroupChain, HoldKeys.ChainSpotMin, 60, 20, 400, HoldApplies.StageEnd, "chain.townSpotMinDistance", false,
                "The next stage stands at least this far from the town's portal drop (m).", g => F(g.Chain?.TownSpotMinDistance ?? 0)));
            r.Add(Num(GroupChain, HoldKeys.ChainSpotMax, 160, 30, 600, HoldApplies.StageEnd, "chain.townSpotMaxDistance", false,
                "... and at most this far (m).", g => F(g.Chain?.TownSpotMaxDistance ?? 0)));
            r.Add(Num(GroupChain, HoldKeys.TownReach, 250, 200, 1000, HoldApplies.NextEvent, "townReach.maxWalk", false,
                "Every stage must stand within this many metres of a Town Network portal drop (else it is moved to a town's edge).",
                g => F(g.TownReach?.MaxWalk ?? 0)));

            r.Add(Int(GroupChain, HoldKeys.ChainCountdownSeconds, 120, 60, 600, HoldApplies.StageEnd, "chain.countdownSeconds", false,
                "The travel countdown: the next stage goes live this long after its announcement.", g => F(g.Chain?.CountdownSeconds ?? 0)));
            r.Add(Int(GroupChain, HoldKeys.ChainArrivalSeconds, WorldEvent.DefaultMaxDurationSeconds, 120, 3600, HoldApplies.StageEnd, null, true,
                "The arrival time limit: the next stage's own time limit once live. Not set: stage 1's, as today."));

            r.Add(Num(GroupChain, HoldKeys.ChainThanksSeconds, 2, 0, 60, HoldApplies.StageEnd, "chain.pacing.thanksSeconds", false,
                "After the win line, the envoy's thanks that points to the cache (the cache reminder).", g => F(g.Chain?.Pacing?.ThanksSeconds ?? 0)));
            r.Add(Num(GroupChain, HoldKeys.ChainStorySeconds, 5, 0, 60, HoldApplies.StageEnd, "chain.pacing.storySeconds", false,
                "After the win line, the story beat.", g => F(g.Chain?.Pacing?.StorySeconds ?? 0)));
            r.Add(Num(GroupChain, HoldKeys.ChainAnnounceSeconds, 8, 0, 60, HoldApplies.StageEnd, "chain.pacing.announceSeconds", false,
                "After the win line, the next event's announcement (the countdown starts there).", g => F(g.Chain?.Pacing?.AnnounceSeconds ?? 0)));
            r.Add(Num(GroupChain, HoldKeys.ChainTieBackSeconds, 3, 0, 60, HoldApplies.StageEnd, "chain.pacing.tieBackSeconds", false,
                "After stage 2 goes live, the line that ties the champion back to stage 1.", g => F(g.Chain?.Pacing?.TieBackSeconds ?? 0)));
            r.Add(Num(GroupChain, HoldKeys.ChainClosingSeconds, 3, 0, 60, HoldApplies.StageEnd, "chain.pacing.closingSeconds", false,
                "After the chain's last outcome, the closing story line.", g => F(g.Chain?.Pacing?.ClosingSeconds ?? 0)));
            r.Add(Num(GroupChain, HoldKeys.ChainAsheronSeconds, 6, 0, 60, HoldApplies.StageEnd, "chain.pacing.asheronSeconds", false,
                "After the chain's last outcome, Asheron's closing line.", g => F(g.Chain?.Pacing?.AsheronSeconds ?? 0)));

            r.Add(Int(GroupInternals, HoldKeys.ChainAnnounceLeadSeconds, WorldEvent.DefaultAnnounceLeadSeconds, 0, 120, HoldApplies.StageEnd, null, true,
                "How much of the travel countdown is the next stage's announce phase (the rest is its teaser)."));
        }

        /// <summary>The next stage's family (world_events_chain_next_family), or an empty list (it rolls, as before).</summary>
        public static List<string> ChainNextFamily()
        {
            var family = Text(HoldKeys.ChainNextFamily, "").Trim().ToLowerInvariant();

            return family.Length == 0 || family == ChainDef.RandomAxis ? new List<string>() : new List<string> { family };
        }

        /// <summary>The next stage's time limit: world_events_chain_arrival_seconds when set, else <paramref name="inherited"/>.</summary>
        public static int ChainArrivalLimit(int inherited) =>
            IsSet(HoldKeys.ChainArrivalSeconds) ? Integer(HoldKeys.ChainArrivalSeconds, inherited) : inherited;

        /// <summary>
        /// The check of a chain axis setting against the loaded store: an id that is not there (or a goal the next source
        /// does not support) is reported, and the stage falls back to random (as the loader does for goals.json).
        /// </summary>
        private static string CheckChainAxis(string key, string raw)
        {
            var store = WorldEventManager.Store;
            var id = ChainDef.Pinned(raw);

            if (store == null || store.IsEmpty || id == null)
                return null;

            switch (key)
            {
                case HoldKeys.ChainNextSource:
                    return store.Sources.ContainsKey(id) ? null : $"{key}='{raw}' is not in sources.json; the next stage rolls a source";
                case HoldKeys.ChainNextGoal:
                    return store.Goals.ContainsKey(id) ? null : $"{key}='{raw}' is not in goals.json; the next stage rolls a goal";
                case HoldKeys.ChainNextBoss:
                    return id == WorldEventComposer.AutoBossId || id == "family_champion" || id == WorldEventComposer.DefaultBossId || store.Bosses.ContainsKey(id)
                        ? null : $"{key}='{raw}' is not in bosses.json (or auto, family_champion, none, random)";
                case HoldKeys.ChainNextFamily:
                    return store.Families.ContainsKey(id) ? null : $"{key}='{raw}' is not in families.json; the next stage rolls a family";
                default:
                    return null;
            }
        }
    }
}
