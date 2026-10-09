using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// Cleanup layer (b) (Q10): a gem bound to a run that is no longer live is destroyed the next time it
    /// passes through the owner's hands - at login (whole inventory) and on vault withdraw (the withdrawn
    /// items). Never touches the shard offline; a gem sitting in a vault stays there until withdrawn.
    /// </summary>
    public static class ThreadDungeonSweeper
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public static bool IsDeadBoundGem(WorldObject item)
        {
            // With the feature off nothing populates the run registry, so EVERY bound gem would look dead and
            // be destroyed at login and on vault withdraw. A disabled feature must not eat players' items.
            if (!ThreadDungeonManager.IsEnabled)
                return false;

            var text = item?.GetProperty(PropertyString.DungeonGemSpec);
            if (string.IsNullOrEmpty(text) || !DungeonGemSpec.TryParse(text, out var spec, out _) || !spec.IsBound)
                return false;

            return IsDeadBoundGemCore(spec, item.Guid.Full, ThreadDungeonManager.GetRun);
        }

        /// <summary>
        /// The pure half: a bound gem is dead when the run resolved from ITS OWN run id is gone, or no longer knows the
        /// gem as its owner gem or a member key (Group Threads invariant 6). Never resolved by owner guid: an owner can
        /// hold a Cleared group run alongside a newer live one, and each gem belongs to exactly one of them.
        /// </summary>
        internal static bool IsDeadBoundGemCore(DungeonGemSpec spec, uint itemGuid, System.Func<uint, ThreadDungeonRun> getRun)
        {
            if (spec == null || !spec.IsBound)
                return false;

            var run = getRun?.Invoke(spec.RunId);
            return run == null || !run.IsRunGem(itemGuid);
        }

        public static int SweepPlayer(Player player)
        {
            var dead = player.GetAllPossessions().Where(IsDeadBoundGem).ToList();
            var destroyed = 0;
            foreach (var gem in dead)
            {
                if (player.TryConsumeFromInventoryWithNetworking(gem))
                    destroyed++;
            }
            if (destroyed > 0)
            {
                player.Session?.Network.EnqueueSend(new GameMessageSystemChat(destroyed == 1 ? "A spent dungeon gem crumbles to dust." : $"{destroyed} spent dungeon gems crumble to dust.", ChatMessageType.Broadcast));
                log.Info($"[DYNDUNGEON] login sweep destroyed {destroyed} dead gem(s) on {player.Name}");
            }
            return destroyed;
        }
    }
}
