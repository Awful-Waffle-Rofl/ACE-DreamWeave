using System;
using System.Collections.Generic;

using log4net;

using ACE.Entity.Enum;
using ACE.Server.ClassAbilities;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers.CharacterSheets;
using ACE.Server.Network.Enum;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.Managers.Market
{
    /// <summary>A validated panel change: a panel-set ability, its compare-and-set rank, and what the request asks for.</summary>
    public sealed class ClassAbilityPanelChange
    {
        public ClassAbilityPanelChange(ClassAbilityDefinition ability, int fromRank, ClassAbilityPanelChangeKind kind)
        {
            Ability = ability;
            FromRank = fromRank;
            Kind = kind;
        }

        public ClassAbilityDefinition Ability { get; }
        public int FromRank { get; }
        public ClassAbilityPanelChangeKind Kind { get; }
    }

    /// <summary>A panel read's answer. Error None means 200 with <see cref="Panel"/>.</summary>
    public sealed class ClassAbilityPanelReadResult
    {
        public ClassAbilityPanelReadResult(MarketError error, ClassAbilityPanelSnapshot panel)
        {
            Error = error;
            Panel = panel;
        }

        public MarketError Error { get; }
        public ClassAbilityPanelSnapshot Panel { get; }
    }

    /// <summary>
    /// A panel change's answer. Error None means applied (200). <see cref="Message"/> overrides the envelope's
    /// stock sentence (not_in_vault_area and rule_refused carry the server's own sentence); <see cref="Panel"/>
    /// is the fresh snapshot carried by applied, stale and rule_refused.
    /// </summary>
    public sealed class ClassAbilityPanelChangeResult
    {
        public ClassAbilityPanelChangeResult(MarketError error, string message = null, ClassAbilityPanelSnapshot panel = null)
        {
            Error = error;
            Message = message;
            Panel = panel;
        }

        public MarketError Error { get; }
        public string Message { get; }
        public ClassAbilityPanelSnapshot Panel { get; }
    }

    /// <summary>
    /// Everything the panel routes need from the running server, behind a seam so the route tests need no live
    /// Player and no world loop. The route has already checked the key, the session, the switches, the launch
    /// binding (for a write) and the roster before calling in.
    /// </summary>
    public interface IClassAbilityPanelService
    {
        /// <summary>
        /// The character's current revision WITHOUT a world-thread hop: the online Player's counter, or
        /// <see cref="ClassAbilityPanelModel.OfflineRevision"/> when it is not online in this process.
        /// </summary>
        string PeekRevision(uint characterGuid);

        /// <summary>A full snapshot. <paramref name="launchBound"/>: the session was launched by this character (only then can it be writable).</summary>
        ClassAbilityPanelReadResult Read(uint accountId, uint characterGuid, bool launchBound);

        /// <summary>One change, from a launch session bound to <paramref name="characterGuid"/>.</summary>
        ClassAbilityPanelChangeResult Apply(uint accountId, uint characterGuid, ClassAbilityPanelChange change);
    }

    /// <summary>
    /// The live panel service. World work runs on the WorldManager action queue (not Player.EnqueueAction, which
    /// never runs while the player is in portal space) through <see cref="AdminWorldCall"/>, waited on for at most
    /// <see cref="worldTimeout"/>: a timeout answers timeout and the outcome is UNKNOWN (the queued action skips
    /// itself only if it had not started). Offline reads go through CharacterSheetService.GetOwnerProfile, the
    /// import route's offline path, so sheet_busy is possible there.
    /// </summary>
    internal sealed class LiveClassAbilityPanelService : IClassAbilityPanelService
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>The fault-log prefixes for the panel's world calls (AdminWorldCall keeps [MARKET][ADMIN] for its admin callers).</summary>
        internal const string PanelReadLogPrefix = "[MARKET][PANEL] a queued class ability panel read";
        internal const string PanelChangeLogPrefix = "[MARKET][PANEL] a queued class ability panel change";

        private readonly Action<IAction> enqueueWorld;
        private readonly Func<ICharacterSheetService> sheets;
        private readonly TimeSpan worldTimeout;
        private readonly Func<bool> panelEnabled;

        /// <param name="panelEnabled">
        /// The two switches (class_abilities_enabled and class_ability_web_panel_enabled), re-read on the world thread
        /// at apply time so a change queued before an operator turned the panel off does not run. Null means on.
        /// </param>
        public LiveClassAbilityPanelService(Action<IAction> enqueueWorld, Func<ICharacterSheetService> sheets, int worldTimeoutMs,
            Func<bool> panelEnabled = null)
        {
            this.enqueueWorld = enqueueWorld ?? throw new ArgumentNullException(nameof(enqueueWorld));
            this.sheets = sheets ?? (() => null);
            worldTimeout = TimeSpan.FromMilliseconds(Math.Max(1, worldTimeoutMs));
            this.panelEnabled = panelEnabled ?? (() => true);
        }

        public string PeekRevision(uint characterGuid)
        {
            // PlayerManager takes its own read lock; the counter is Interlocked. No world hop.
            var player = PlayerManager.GetOnlinePlayer(characterGuid);
            return player?.ClassAbilityPanelRevision ?? ClassAbilityPanelModel.OfflineRevision;
        }

        public ClassAbilityPanelReadResult Read(uint accountId, uint characterGuid, bool launchBound)
        {
            if (PlayerManager.GetOnlinePlayer(characterGuid) != null)
            {
                var status = AdminWorldCall.Run(enqueueWorld, () =>
                {
                    var player = FindEligible(accountId, characterGuid);

                    // went offline between the check and the world thread: answer from the offline path below
                    if (player == null)
                        return null;

                    return Snapshot(player, launchBound);
                }, worldTimeout, out ClassAbilityPanelSnapshot online, PanelReadLogPrefix);

                if (status == AdminWorldCall.Status.TimedOut)
                    return new ClassAbilityPanelReadResult(MarketError.Timeout, null);

                if (status == AdminWorldCall.Status.Faulted)
                    return new ClassAbilityPanelReadResult(MarketError.ServerError, null);

                if (online != null)
                    return new ClassAbilityPanelReadResult(MarketError.None, online);
            }

            return ReadOffline(characterGuid, launchBound);
        }

        private ClassAbilityPanelReadResult ReadOffline(uint characterGuid, bool launchBound)
        {
            var service = sheets();

            if (service == null)
                return new ClassAbilityPanelReadResult(MarketError.SheetsDisabled, null);

            var result = service.GetOwnerProfile(characterGuid);

            if (result.Outcome != SheetOutcome.Ok || result.Profile == null)
                return new ClassAbilityPanelReadResult(SheetErrorFor(result.Outcome), null);

            var profile = result.Profile;

            // The character came online while the owner profile was built: its ranks are now live, so this
            // offline answer would carry a revision that never matches. Busy; the client's next poll reads it live.
            if (profile.Online)
                return new ClassAbilityPanelReadResult(MarketError.SheetBusy, null);

            var block = profile.ClassAbilityImport;
            if (block == null)
                return new ClassAbilityPanelReadResult(MarketError.ServerError, null);

            var subject = new OfflineSubject(characterGuid, profile.Name, profile.Level, block);
            var refusal = ClassAbilityPanelModel.WriteRefusal(launchBound, online: false, stateRefusal: null, areaRefusal: null);

            return new ClassAbilityPanelReadResult(MarketError.None,
                ClassAbilityPanelModel.Build(subject, online: false, refusal, ClassAbilityPanelTierRequirements.Live(), ClassAbilityText.Live));
        }

        public ClassAbilityPanelChangeResult Apply(uint accountId, uint characterGuid, ClassAbilityPanelChange change)
        {
            var status = AdminWorldCall.Run(enqueueWorld, () => ApplyOnWorld(accountId, characterGuid, change, panelEnabled),
                worldTimeout, out ClassAbilityPanelChangeResult result, PanelChangeLogPrefix);

            // A fault may have come after the change applied (AdminWorldCall logs it), so its outcome is as unknown as a
            // timeout's: the same answer, which the client already handles by reloading, never retrying.
            if (status == AdminWorldCall.Status.TimedOut || status == AdminWorldCall.Status.Faulted)
                return new ClassAbilityPanelChangeResult(MarketError.Timeout, ClassAbilityPanelModel.ChangeTimeoutMessage);

            if (result == null)
                return new ClassAbilityPanelChangeResult(MarketError.ServerError);

            // Offline: the world thread cannot build the read-only snapshot (it reads the shard), so it is taken here,
            // on the request thread, through the same offline path as the GET. Omitted (no panel) when that read
            // itself fails or answers busy; the client then refetches in full.
            if (result.Error == MarketError.CharacterOffline && result.Panel == null)
            {
                var offline = ReadOffline(characterGuid, launchBound: true);
                return new ClassAbilityPanelChangeResult(MarketError.CharacterOffline, ClassAbilityPanelModel.OfflineRefusal,
                    offline.Error == MarketError.None ? offline.Panel : null);
            }

            return result;
        }

        /// <summary>World thread. Contract steps 3 and 4, then compare-and-set, then the rules.</summary>
        private static ClassAbilityPanelChangeResult ApplyOnWorld(uint accountId, uint characterGuid, ClassAbilityPanelChange change,
            Func<bool> panelEnabled)
        {
            // the switches, re-read now: the route checked them when the request arrived, not when the world ran it
            if (!panelEnabled())
            {
                var disabledText = MarketErrorCodes.ToMessage(MarketError.PanelDisabled);
                var current = FindEligible(accountId, characterGuid);
                return new ClassAbilityPanelChangeResult(MarketError.PanelDisabled, disabledText,
                    current == null ? null : Snapshot(current, launchBound: true, writeRefusalOverride: disabledText));
            }

            // 3. online in this process, on this account, not logging out
            var player = FindEligible(accountId, characterGuid);
            if (player == null)
                return new ClassAbilityPanelChangeResult(MarketError.CharacterOffline, ClassAbilityPanelModel.OfflineRefusal);

            // dead, busy, teleporting or in the PK logout countdown (owner ruling 2026-11-08)
            var state = StateRefusal(player);
            if (state != null)
                return new ClassAbilityPanelChangeResult(MarketError.RuleRefused, state, Snapshot(player, launchBound: true));

            // 4. where the mule vendor could be summoned
            var area = MuleSummonHandler.ClassAbilityPanelAreaRefusal(player.Location);
            if (area != null)
                return new ClassAbilityPanelChangeResult(MarketError.NotInVaultArea, area, Snapshot(player, launchBound: true));

            var skill = change.Ability;

            // compare-and-set on the live rank
            if (ClassAbilityPanelModel.IsStale(player.GetClassAbilityRank(skill.Id), change.FromRank))
                return new ClassAbilityPanelChangeResult(MarketError.Stale, null, Snapshot(player, launchBound: true));

            string error;
            bool applied;
            var refunded = 0;

            switch (change.Kind)
            {
                case ClassAbilityPanelChangeKind.Raise:
                    // the panel's CanRaise first (the trainer's voucher refusal), then the trainer's own learn
                    applied = player.CanRaiseClassAbility(skill, out error) && player.LearnClassAbility(skill, out error);
                    break;

                case ClassAbilityPanelChangeKind.Lower:
                    applied = player.CanLowerClassAbility(skill, out error) && player.LowerClassAbilityRank(skill, out error, out refunded);
                    break;

                case ClassAbilityPanelChangeKind.Unlearn:
                    // CanUnlearn, so a refusal says "unlearn". A lower that is refused is never offered as an unlearn (the
                    // contract) without a separate gate: an unlearn removes more ranks, so it refuses whenever a lower does.
                    applied = player.CanUnlearnClassAbility(skill, out error) && player.UnlearnClassAbility(skill, out error, out refunded);
                    break;

                default:
                    return new ClassAbilityPanelChangeResult(MarketError.InvalidChange);
            }

            var after = Snapshot(player, launchBound: true);

            if (!applied)
                return new ClassAbilityPanelChangeResult(MarketError.RuleRefused, error, after);

            // Committed: the player's own chat line, the in-game wording plus the web panel marker. Only here, after
            // every refusal above has returned, so a stale, refused or out-of-area change never says anything.
            player.Session.Network.EnqueueSend(new GameMessageSystemChat(
                ClassAbilityPanelModel.ChangeChatLine(change.Kind, skill, player.GetClassAbilityRank(skill.Id), refunded,
                    player.AvailableClassAbilityPoints, player.TotalClassAbilityPointsEarned),
                ChatMessageType.Broadcast));

            log.Info($"[MARKET] class ability panel: {player.Name} (0x{characterGuid:X8}) {change.Kind} {skill.Name} from rank {change.FromRank}.");

            return new ClassAbilityPanelChangeResult(MarketError.None, null, after);
        }

        /// <summary>
        /// World thread. The online Player for <paramref name="characterGuid"/> when it is logged in on
        /// <paramref name="accountId"/>'s WorldConnected session and not logging out; otherwise null. By guid
        /// through PlayerManager, never the NetworkManager lookup by account id, which throws with two sessions on one account.
        /// </summary>
        private static Player FindEligible(uint accountId, uint characterGuid)
        {
            var player = PlayerManager.GetOnlinePlayer(characterGuid);
            var session = player?.Session;

            // the rule is ClassAbilityPanelModel.IsEligible (unit-tested); this only gathers its inputs
            var eligible = ClassAbilityPanelModel.IsEligible(accountId,
                playerFound: player != null,
                hasSession: session != null,
                sessionAccountId: session?.AccountId ?? 0,
                worldConnected: session?.State == SessionState.WorldConnected,
                sessionHoldsPlayer: session != null && ReferenceEquals(session.Player, player),
                loggingOut: player?.IsLoggingOut ?? true);

            return eligible ? player : null;
        }

        /// <summary>World thread. The live snapshot of an eligible player.</summary>
        private static ClassAbilityPanelSnapshot Snapshot(Player player, bool launchBound, string writeRefusalOverride = null)
        {
            var refusal = writeRefusalOverride;

            if (refusal == null)
            {
                // the area is asked only when nothing before it in WriteRefusal's order already refuses
                var state = StateRefusal(player);
                var area = launchBound && state == null ? MuleSummonHandler.ClassAbilityPanelAreaRefusal(player.Location) : null;
                refusal = ClassAbilityPanelModel.WriteRefusal(launchBound, online: true, state, area);
            }

            return ClassAbilityPanelModel.Build(new PlayerSubject(player), online: true, refusal, ClassAbilityPanelTierRequirements.Live(), ClassAbilityText.Live);
        }

        /// <summary>World thread. The character-state inputs of <see cref="ClassAbilityPanelModel.StateRefusal"/>.</summary>
        private static string StateRefusal(Player player) =>
            ClassAbilityPanelModel.StateRefusal(dead: player.IsDead, busy: player.IsBusy, teleporting: player.Teleporting, pkLogout: player.PKLogout);

        /// <summary>World thread. <see cref="FindEligible"/> for the Character Details read (LiveCharacterDetailsService), the same rule.</summary>
        internal static Player FindEligibleForDetails(uint accountId, uint characterGuid) => FindEligible(accountId, characterGuid);

        private static MarketError SheetErrorFor(SheetOutcome outcome) => outcome switch
        {
            SheetOutcome.NotFound => MarketError.SheetNotFound,
            SheetOutcome.Disabled => MarketError.SheetsDisabled,
            SheetOutcome.Busy     => MarketError.SheetBusy,
            _                     => MarketError.ServerError,
        };

        /// <summary>A live Player on the world thread. Every answer is the Player's own.</summary>
        private sealed class PlayerSubject : IClassAbilityPanelSubject
        {
            private readonly Player player;
            private readonly HashSet<int> heldVouchers;

            public PlayerSubject(Player player)
            {
                this.player = player;
                heldVouchers = player.ClassAbilityVoucherIdsHeld();
                Ranks = player.ClassAbilityRanksSnapshot();
            }

            public uint CharacterGuid => player.Guid.Full;
            public string Name => player.Name;
            public int Level => player.Level ?? 0;
            public string Revision => player.ClassAbilityPanelRevision;
            public int PointsAvailable => player.AvailableClassAbilityPoints;
            public int PointsEarned => player.TotalClassAbilityPointsEarned;
            public IReadOnlyDictionary<ClassAbilityId, int> Ranks { get; }

            public bool CanRaise(ClassAbilityDefinition ability, out string refusal) => player.CanRaiseClassAbility(ability, out refusal, heldVouchers);
            public bool CanLower(ClassAbilityDefinition ability, out string refusal) => player.CanLowerClassAbility(ability, out refusal);

            // The same call /abilities list makes (ClassAbilityCommands), on the world thread like it.
            public ClassAbilityReadout? Readout(ClassAbilityDefinition ability, int rank) =>
                ClassAbilityRegistry.GetHandler(ability.Id) is IAbilityReadout source ? source.GetReadout(player, rank) : null;
        }

        /// <summary>An offline character's import block. Never writable, so the Can* answers are never asked.</summary>
        private sealed class OfflineSubject : IClassAbilityPanelSubject
        {
            public OfflineSubject(uint characterGuid, string name, int level, ClassAbilityImportBlock block)
            {
                CharacterGuid = characterGuid;
                Name = name;
                Level = level;
                PointsAvailable = block.PointsAvailable;
                PointsEarned = block.PointsEarned;

                var ranks = new Dictionary<ClassAbilityId, int>();
                foreach (var r in block.Ranks)
                {
                    if (ClassAbilityRegistry.TryGetByName(r.Id, out var def))
                        ranks[def.Id] = r.Rank;
                }
                Ranks = ranks;
            }

            public uint CharacterGuid { get; }
            public string Name { get; }
            public int Level { get; }
            public string Revision => ClassAbilityPanelModel.OfflineRevision;
            public int PointsAvailable { get; }
            public int PointsEarned { get; }
            public IReadOnlyDictionary<ClassAbilityId, int> Ranks { get; }

            public bool CanRaise(ClassAbilityDefinition ability, out string refusal) { refusal = ClassAbilityPanelModel.OfflineRefusal; return false; }
            public bool CanLower(ClassAbilityDefinition ability, out string refusal) { refusal = ClassAbilityPanelModel.OfflineRefusal; return false; }

            // No Player offline, so no live number.
            public ClassAbilityReadout? Readout(ClassAbilityDefinition ability, int rank) => null;
        }
    }
}
