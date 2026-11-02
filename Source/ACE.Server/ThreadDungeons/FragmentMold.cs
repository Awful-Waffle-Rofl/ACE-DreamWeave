using System;
using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldObjects;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// The use-on-target half of the Fragment Mold: the player double-clicks the mold and clicks a Raw
    /// Fragment. A fragment that holds a load has its component load recorded onto the mold (the fragment is
    /// untouched); a blank fragment is loaded from the mold's imprint, consuming the components from the
    /// player's packs. The mold itself is never consumed.
    ///
    /// Deliberately thin, like RawFragment.cs: every decision belongs to FragmentMoldRules, which is pure and
    /// unit-tested. Nothing in this file can be unit-tested (Player.UpdateProperty's SendNetwork dereferences
    /// Session with no null check), so it only consumes, writes and refreshes.
    /// </summary>
    public static class FragmentMold
    {
        public static void UseObjectOnTarget(Player player, Gem mold, WorldObject target)
        {
            if (player == null || mold == null || target == null)
                return;

            // The mold spends components from the player's packs on its target, so the target must be in those
            // packs as well. Player_Use refuses a landblock target before the walk; this is the second, authoritative
            // check, ahead of every decision below.
            if (player.FindObject(target.Guid.Full, Player.SearchLocations.MyInventory) == null)
            {
                Say(player, FragmentMoldRules.RefuseNotInPack);
                return;
            }

            var store = ThreadDungeonManager.Store;
            var defs = store.Attunement;

            var decision = FragmentMoldRules.Decide(
                target.GetProperty(PropertyString.DungeonGemSpec),
                mold.GetProperty(PropertyString.FragmentMoldImprint),
                defs,
                wcid => player.GetNumInventoryItemsOfWCID(wcid));

            switch (decision.Step)
            {
                case MoldStep.TakeImprint:
                    // Same shape as the fragment refresh below: write the data, rewrite the description, push one
                    // full object refresh. UpdateProperty, never SetProperty - SetProperty sends nothing.
                    player.UpdateProperty(mold, PropertyString.FragmentMoldImprint, decision.NewImprint);
                    player.UpdateProperty(mold, PropertyString.LongDesc, decision.NewLongDesc);
                    player.EnqueueBroadcast(new GameMessageUpdateObject(mold));
                    Say(player, decision.Message);
                    return;

                case MoldStep.Apply:
                    Apply(player, target, decision, defs);
                    return;

                default:
                    Say(player, decision.Message);
                    return;
            }
        }

        private static void Apply(Player player, WorldObject fragment, MoldDecision decision, AttunementDef defs)
        {
            // Applying consumes by wcid and can shrink a stack the trade partner has already seen. Taking an
            // imprint consumes nothing, so it stays allowed.
            if (player.IsTrading)
            {
                Say(player, FragmentMoldRules.RefuseTrading);
                return;
            }

            var plan = decision.Plan;

            if (plan.Outcome == ImprintOutcome.Refused)
            {
                Say(player, plan.Refusal);
                return;
            }

            if (plan.Outcome == ImprintOutcome.Shortage)
            {
                Say(player, FragmentMoldRules.ComposeShortageLine(plan.Shortages, defs));
                return;
            }

            // The components leave the packs BEFORE the fragment records them, as RawFragment does, so a failed
            // consume cannot leave a fragment claiming a component the player still holds. They leave ONE DOSE
            // AT A TIME, so a failure can strand at most one partial dose (a Dose above 1 is several units);
            // the units each wcid actually lost are measured rather than assumed, and only the doses those
            // units paid for are loaded.
            var consumed = new Dictionary<uint, int>();
            var interrupted = false;

            foreach (var entry in decision.Imprint)
            {
                for (var i = 0; i < entry.Doses && !interrupted; i++)
                {
                    if (!defs.TryGet(entry.Wcid, out var component))
                    {
                        interrupted = true;
                        break;
                    }

                    var before = player.GetNumInventoryItemsOfWCID(entry.Wcid);
                    var ok = player.TryConsumeFromInventoryWithNetworking(entry.Wcid, component.Dose);
                    var taken = ok ? component.Dose : Math.Max(0, before - player.GetNumInventoryItemsOfWCID(entry.Wcid));

                    consumed.TryGetValue(entry.Wcid, out var sofar);
                    consumed[entry.Wcid] = sofar + taken;

                    if (!ok)
                        interrupted = true;
                }

                if (interrupted)
                    break;
            }

            var loaded = FragmentMoldRules.LoadConsumed(decision.TargetSpec, decision.Imprint, defs, consumed, out var dosesLoaded);
            var unitsLost = FragmentMoldRules.UnitsLost(loaded.Load, consumed, defs);

            if (dosesLoaded == 0)
            {
                Say(player, FragmentMoldRules.ComposeInterruptedLine(null, unitsLost));
                return;
            }

            // The same three-step refresh RawFragment uses: write the spec, rewrite the description from it, push
            // one full object refresh so the appraisal panel updates without a relog.
            player.UpdateProperty(fragment, PropertyString.DungeonGemSpec, loaded.Serialize());
            player.UpdateProperty(fragment, PropertyString.LongDesc,
                DungeonGemFactory.ComposeFragmentLongDesc(loaded, fragment.Structure ?? 0, fragment.MaxStructure ?? 0));
            player.EnqueueBroadcast(new GameMessageUpdateObject(fragment));

            var line = RawFragmentRules.ComposePressedLine(loaded.Load, defs);
            Say(player, interrupted ? FragmentMoldRules.ComposeInterruptedLine(line, unitsLost) : FragmentMoldRules.ComposeAppliedLine(line));
        }
        private static void Say(Player player, string message)
            => player.Session?.Network.EnqueueSend(new GameMessageSystemChat(message, ChatMessageType.Broadcast));
    }
}
