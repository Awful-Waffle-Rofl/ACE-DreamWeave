using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// The use-on-target half of a Raw Fragment (PHASE-2-IMPLEMENTATION-PLAN.md task D4b): the player
    /// double-clicks the fragment and clicks either a stack of spell components (load one dose) or the
    /// Fragment Press (resolve every loaded dose into a Thread Gem).
    ///
    /// Deliberately thin - every decision belongs to RawFragmentRules, which is pure and unit-tested, and
    /// every player-facing string here is one of that class's constants or one of this class's own. Nothing
    /// in this file can be unit-tested: Player.UpdateProperty's SendNetwork dereferences Session with no
    /// null check, the same reason Gem.TryHandleMuleFormTokenUse gives.
    ///
    /// A finished Thread Gem carries the same PropertyString.DungeonGemSpec that Gem.HandleActionUseOnTarget
    /// dispatches on, but its ItemUseable is Contained, so the client never sends a use-on for one. If one
    /// ever arrives anyway (a data error), it is refused by name here rather than being loaded.
    /// </summary>
    public static class RawFragment
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public const string UnreadableMessage = "The marks on this fragment do not read. Take it to the Fragment-Clerk.";
        public const string AlreadyFinishedMessage = "That gem is finished. Use it to open a dungeon.";

        public static string ComposeTakesLine(string componentName) => $"The fragment takes the {componentName}.";

        public static void UseObjectOnTarget(Player player, Gem fragment, WorldObject target)
        {
            if (player == null || fragment == null || target == null)
                return;

            var text = fragment.GetProperty(PropertyString.DungeonGemSpec);

            if (!DungeonGemSpec.TryParse(text, out var spec, out var parseError))
            {
                log.Error($"[DYNDUNGEON] fragment 0x{fragment.Guid.Full:X8} held by {player.Name} has an unparseable spec '{text}': {parseError}");
                Say(player, UnreadableMessage);
                return;
            }

            // A pressed spec is a finished gem, whatever its ItemUseable says. Never load one, and never
            // press one again: the press rolls the seed exactly once, and DungeonGemFactory refuses either
            // direction on the same invariant.
            if (spec.Seed != 0)
            {
                Say(player, AlreadyFinishedMessage);
                return;
            }

            // The press is the only non-component target a fragment answers. Checked before CanLoad, which
            // would otherwise refuse the station as "does not answer that" - it is a Creature, not a
            // component. Every tunable the pressing needs is read inside FragmentPressStation, never here.
            if (target.GetProperty(PropertyBool.DungeonGemPress) == true)
            {
                FragmentPressStation.Press(player, fragment, target);
                return;
            }

            var store = ThreadDungeonManager.Store;

            var refusalKind = RawFragmentRules.CanLoad(spec, store.Attunement, target.WeenieClassId, target.StackSize ?? 1, out var component, out var refusal);

            if (refusalKind != LoadRefusal.None)
            {
                // The one refusal CanLoad cannot produce: the target was never a candidate at all. A spell
                // component the file simply does not map still gets "does not take that", which is the more
                // informative answer; anything else - a creature, a weapon, a herb - gets "does not answer
                // that". ItemType is a flag set, so test the bit rather than comparing the whole value.
                if (refusalKind == LoadRefusal.NotAComponent && (target.ItemType & ItemType.SpellComponents) == ItemType.None)
                    Say(player, RawFragmentRules.RefuseNotAValidTarget);
                else
                    Say(player, refusal);

                return;
            }

            // The dose leaves the stack BEFORE the fragment records it, so a failed consume cannot leave a
            // fragment claiming a component the player still holds.
            if (!player.TryConsumeFromInventoryWithNetworking(target, component.Dose))
            {
                Say(player, RawFragmentRules.RefuseNotAValidTarget);
                return;
            }

            var loaded = RawFragmentRules.Load(spec, component);

            // Same three-step shape ThreadDungeonGemHandler uses for a gem: write the spec, rewrite the
            // description from it, then push one full object refresh so the appraisal panel updates without
            // a relog. UpdateProperty, never SetProperty - SetProperty writes the biota and sends nothing.
            player.UpdateProperty(fragment, PropertyString.DungeonGemSpec, loaded.Serialize());
            player.UpdateProperty(fragment, PropertyString.LongDesc,
                ThreadDungeonGemHandler.ComposeLongDesc(loaded, DungeonGemFactory.ResolveDungeonName(loaded), fragment.Structure ?? 0, fragment.MaxStructure ?? 0));
            player.EnqueueBroadcast(new GameMessageUpdateObject(fragment));

            Say(player, DungeonGemNarrator.ComposeLoadMessage(component, loaded, store.Attunement, store.Modifiers));
        }

        private static void Say(Player player, string message)
            => player.Session?.Network.EnqueueSend(new GameMessageSystemChat(message, ChatMessageType.Broadcast));
    }
}
