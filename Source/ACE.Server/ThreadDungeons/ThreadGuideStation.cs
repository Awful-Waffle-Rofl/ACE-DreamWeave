using System;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// The ONE guide fragment builder, shared by the NPC grant and the fail regrant (both reach it through
    /// IGuideHolder.TryGiveFragment), so the two cannot drift on how a guide fragment is shaped.
    /// </summary>
    public static class ThreadGuideItems
    {
        /// <summary>
        /// Builds the guide fragment for <paramref name="tag"/>: DungeonGemFactory.CreateFragment over
        /// ThreadGuideFlow.BuildFragmentSpec (rung level, the ladder's tier, the tag), with
        /// ThreadGuideFlow.FragmentEntries entries, renamed "Guide Fragment (Level N)", then Attuned and Bonded.
        /// Null with <paramref name="error"/> set when the factory refuses.
        /// </summary>
        public static WorldObject CreateFragment(GuideTag tag, out string error)
        {
            var spec = ThreadGuideFlow.BuildFragmentSpec(tag);

            var fragment = DungeonGemFactory.CreateFragment(spec, ThreadGuideFlow.FragmentEntries, ThreadGuideFlow.FragmentEntries, out error);
            if (fragment == null)
                return null;

            fragment.SetProperty(PropertyString.Name, ThreadGuideText.ComposeFragmentName(tag.Rung));
            fragment.SetProperty(PropertyString.PluralName, ThreadGuideText.ComposeFragmentPluralName(tag.Rung));
            Bind(fragment);

            return fragment;
        }

        /// <summary>
        /// Attuned and Bonded: the item cannot leave its owner's hands. Applied to the guide fragment here and to
        /// the gem the Press makes from it (FragmentPressStation.Press), so the gem is bound like the fragment.
        /// </summary>
        public static void Bind(WorldObject item)
        {
            if (item == null)
                return;

            item.Attuned = AttunedStatus.Attuned;
            item.Bonded = BondedStatus.Bonded;
        }
    }

    /// <summary>
    /// The Thread-Guide NPC (wcid 1003605, authored separately as content), marked by PropertyBool.ThreadGuideNpc
    /// and dispatched from Creature.ActOnUse beside the Survey-Archivist. MIRRORS SurveyArchivistStation: it
    /// carries NO Use emote set, because WorldObject.OnActivate runs EmoteManager.OnUse BEFORE ActOnUse and an
    /// emote rig alongside this code would double-fire.
    ///
    /// Every decision is ThreadGuideFlow's (pure, unit tested); this file reads the tunable, resolves the runs
    /// and adapts the Player through <see cref="PlayerGuideHolder"/>. The serial is stamped only on a confirmed
    /// give, exactly like SurveyArchivistStation.TryGiveGuide, and before any tell goes out.
    /// </summary>
    public static class ThreadGuideStation
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public const string EnabledProperty = "dynamic_dungeons_guide_enabled";
        public const string RewardPercentProperty = "dynamic_dungeons_guide_reward_percent";

        /// <summary>The Thread-Guide NPC weenie. The level-50 notice waits until it exists in the world database.</summary>
        public const uint ThreadGuideNpcWcid = 1003605;

        private static readonly ThreadGuideFlow.PresenceCache npcPresence = new ThreadGuideFlow.PresenceCache();

        /// <summary>
        /// Whether the Thread-Guide weenie is in the world database, through <see cref="ThreadGuideFlow.PresenceCache"/>
        /// (positive forever, negative for five minutes), so the notice gate never makes a synchronous world-DB read
        /// per login or level-up while the content is missing.
        /// </summary>
        public static bool NpcWeenieExists()
            => npcPresence.Check(() => ACE.Database.DatabaseManager.World.GetCachedWeenie(ThreadGuideNpcWcid) != null, DateTime.UtcNow);

        /// <summary>ThreadGuideFlow.GuideAvailable over the three live switches: Threads, the Press, the guide.</summary>
        public static bool ReadAvailable()
            => ThreadGuideFlow.GuideAvailable(
                ThreadDungeonManager.IsEnabled,
                PropertyManager.GetBool(FragmentPressStation.EnabledProperty).Item,
                PropertyManager.GetBool(EnabledProperty).Item);

        /// <summary>Dispatch gate: false at the cost of one property miss for every NPC that is not the guide.</summary>
        public static bool TryHandleUse(WorldObject npc, Player player)
        {
            if (npc == null || player == null)
                return false;

            if (!(npc.GetProperty(PropertyBool.ThreadGuideNpc) ?? false))
                return false;

            var holder = new PlayerGuideHolder(player);
            var hasLiveGuideRun = ThreadGuideFlow.HasLiveGuideRun(ThreadDungeonManager.LiveRuns, player.Guid.Full);

            var decision = ThreadGuideFlow.HandleNpcUse(holder, ReadAvailable(), player.Level ?? 0, hasLiveGuideRun,
                text => Tell(npc, player, text),
                text => Popup(player, text));

            log.Info($"[DYNDUNGEON] guide {player.Name} (0x{player.Guid.Full:X8}) level={player.ThreadGuideLevel} serial={player.ThreadGuideGrantSerial} -> {decision}");

            return true;
        }

        /// <summary>
        /// The clear seam body (ThreadDungeonManager.GuideRecorder's production target). Guide runs only, owner
        /// only, online only (an offline owner gets no credit, as with the survey), on the owner's action queue.
        /// Applies the survey's nothing-spawned gate as well: a run that placed nothing cleared without a fight and
        /// must not pay a rung; the owner simply gets the same rung again from the NPC.
        /// </summary>
        public static void RecordGuideClear(ThreadDungeonRun run)
        {
            if (run == null || !ThreadGuideRules.IsGuide(run.Spec))
                return;

            // An owner the puzzle fail policy removed earns no guide credit (ThreadPuzzleFailPolicy).
            if (run.IsPuzzleRemoved(run.OwnerGuid))
                return;

            var owner = PlayerManager.GetOnlinePlayer(run.OwnerGuid);

            switch (ThreadGuideFlow.DecideClearSeam(run.Spec, owner != null, run.Spawned))
            {
                case ThreadGuideFlow.ClearSeamAction.Skip:
                    return;

                case ThreadGuideFlow.ClearSeamAction.Empty:
                    log.Info($"[DYNDUNGEON] guide clear not credited for {run}: nothing spawned");
                    Say(owner, ThreadGuideText.EmptyThread);
                    return;
            }

            var spec = run.Spec;
            var runText = run.ToString();

            owner.EnqueueAction(new ACE.Server.Entity.Actions.ActionEventDelegate(() =>
            {
                var percent = ThreadGuideFlow.SanitizeRewardPercent(PropertyManager.GetDouble(RewardPercentProperty).Item);
                var holder = new PlayerGuideHolder(owner);

                var advanced = ThreadGuideFlow.ApplyClear(holder, spec, percent, rung => ThreadGuideLadder.RewardCap(rung),
                    text => Say(owner, text),
                    ex => log.Error($"[DYNDUNGEON] guide clear XP for {owner.Name} threw; the rung is latched anyway ({runText})", ex));

                log.Info($"[DYNDUNGEON] guide clear {owner.Name} rung={spec.Guide.Rung} advanced={advanced} level={owner.ThreadGuideLevel} for {runText}");
            }));
        }

        /// <summary>
        /// The fail seam body (ThreadDungeonManager.GuideFailRegranter's production target), called from EndRun
        /// after OnRunEnded for a guide run that never cleared. Online owner only, on their action queue - which
        /// runs after OnRunEnded's own gem consume on the same queue, so the dead gem is already gone.
        /// </summary>
        public static void RegrantAfterFail(ThreadDungeonRun run)
        {
            if (run == null || !ThreadGuideRules.IsGuide(run.Spec))
                return;

            var owner = PlayerManager.GetOnlinePlayer(run.OwnerGuid);
            if (owner == null)
                return;

            var rung = run.Spec.Guide.Rung;
            var runText = run.ToString();

            owner.EnqueueAction(new ACE.Server.Entity.Actions.ActionEventDelegate(() =>
            {
                // The kill switch is read HERE, inside the delegate, so a switch flipped between the run ending and
                // this delegate running is honoured.
                var hasLiveGuideRun = ThreadGuideFlow.HasLiveGuideRun(ThreadDungeonManager.LiveRuns, owner.Guid.Full);
                var outcome = ThreadGuideFlow.ApplyFailRegrant(new PlayerGuideHolder(owner), rung, hasLiveGuideRun, ReadAvailable(), text => Say(owner, text));

                log.Info($"[DYNDUNGEON] guide fail regrant {owner.Name} rung={rung} outcome={(outcome?.ToString() ?? "outstanding")} for {runText}");
            }));
        }

        /// <summary>Increments ThreadClearsLifetime on one player's own action queue.</summary>
        public static void IncrementClears(Player player)
        {
            player?.EnqueueAction(new ACE.Server.Entity.Actions.ActionEventDelegate(() =>
                player.ThreadClearsLifetime = player.ThreadClearsLifetime + 1));
        }

        /// <summary>The exact shape EmoteType.Tell sends, as SurveyArchivistStation.Tell does.</summary>
        private static void Tell(WorldObject npc, Player player, string message)
        {
            if (player?.Session == null || message == null)
                return;

            player.Session.Network.EnqueueSend(new GameEventTell(npc, message, player, ChatMessageType.Tell));
        }

        internal static void Popup(Player player, string message)
        {
            if (player?.Session == null || message == null)
                return;

            player.Session.Network.EnqueueSend(new GameEventPopupString(player.Session, message));
        }

        internal static void Say(Player player, string message)
        {
            if (player?.Session == null || message == null)
                return;

            player.Session.Network.EnqueueSend(new GameMessageSystemChat(message, ChatMessageType.Broadcast));
        }

        /// <summary>The live-Player side of <see cref="IGuideHolder"/>.</summary>
        internal sealed class PlayerGuideHolder : IGuideHolder
        {
            private readonly Player player;

            public PlayerGuideHolder(Player player) => this.player = player;

            public uint Guid => player.Guid.Full;

            public int GuideLevel
            {
                get => player.ThreadGuideLevel;
                set => player.ThreadGuideLevel = value;
            }

            public int GrantSerial
            {
                get => player.ThreadGuideGrantSerial;
                set => player.ThreadGuideGrantSerial = value;
            }

            public bool HasOutstandingGuideItem()
                => ThreadGuideFlow.HasOutstandingGuideItem(player.Inventory.Values, player.Guid.Full, player.ThreadGuideGrantSerial);

            /// <summary>
            /// The pre-flight, the same shape FragmentPressStation.HasRoomFor uses. Fails OPEN for a weenie missing
            /// from the world database (ItemsToReceive charges it 0 slots); the build then fails and gives nothing.
            /// </summary>
            public bool HasRoomForFragment()
            {
                var itemsToReceive = new ItemsToReceive(player);
                itemsToReceive.Add(DungeonGemFactory.RawFragmentBaseWcid, 1);
                return !itemsToReceive.PlayerExceedsLimits;
            }

            public bool TryGiveFragment(GuideTag tag)
            {
                var fragment = ThreadGuideItems.CreateFragment(tag, out var error);

                if (fragment == null)
                {
                    log.Error($"[DYNDUNGEON] guide fragment for {player.Name} ({tag}) could not be built: {error}");
                    return false;
                }

                if (!player.TryCreateInInventoryWithNetworking(fragment))
                {
                    fragment.Destroy();
                    return false;
                }

                return true;
            }

            public void GrantXp(double percent, long cap)
            {
                if (cap > 0)
                    player.GrantLevelProportionalXp(percent, 0, cap);
            }

            public void Save() => player.SaveBiotaToDatabase();

            /// <summary>Consumes each stale guide item (ThreadGuideFlow.FindStaleGuideItems) from the pack.</summary>
            public int RemoveStaleGuideItems()
            {
                var removed = 0;

                foreach (var item in ThreadGuideFlow.FindStaleGuideItems(player.Inventory.Values, player.Guid.Full, player.ThreadGuideGrantSerial))
                {
                    var itemGuid = item.Guid.Full;

                    if (player.TryConsumeFromInventoryWithNetworking(item))
                    {
                        removed++;
                        log.Info($"[DYNDUNGEON] guide took back stale item 0x{itemGuid:X8} from {player.Name}");
                    }
                }

                return removed;
            }
        }
    }
}
