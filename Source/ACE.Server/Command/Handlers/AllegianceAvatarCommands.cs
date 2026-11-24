using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.Avatars;
using ACE.Server.Avatars.Gauntlet;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.WorldObjects;

namespace ACE.Server.Command.Handlers
{
    internal enum AvatarCommandVerb
    {
        Status,
        Summon,
        Seat,
        Heir,
        Gauntlet,
        GauntletEnter,
        Dress,
        DressReset,
    }

    /// <summary>
    /// /avatar: the Allegiance Avatar's player commands. Handlers run on the world thread (inbound queue), so the summon
    /// and the seat request are called directly. The heir popup's Yes hands the write back to the world thread.
    /// </summary>
    public static class AllegianceAvatarCommands
    {
        /// <summary>Test seam: a character by name as (guid, exact name), or null when none matches.</summary>
        internal static Func<string, (uint Guid, string Name)?> FindCharacter = name =>
        {
            var found = PlayerManager.FindByName(name);
            return found == null ? null : ((uint, string)?)(found.Guid.Full, found.Name);
        };

        internal static void ResetSeams()
        {
            FindCharacter = name =>
            {
                var found = PlayerManager.FindByName(name);
                return found == null ? null : ((uint, string)?)(found.Guid.Full, found.Name);
            };
            GauntletStatusLines = LiveGauntletStatus;
            GauntletTargetFor = avatarId => GauntletManager.ForAvatar(avatarId);
        }

        internal const string UsageText = "Usage: /avatar [summon | seat | heir <name> | dress [reset] | gauntlet [enter]]";

        /// <summary>Pure parse: no argument is the status, then summon, seat, or heir with the rest of the line as the name.</summary>
        internal static (AvatarCommandVerb Verb, string Arg, string Error) Parse(string[] parameters)
        {
            if (parameters == null || parameters.Length == 0)
                return (AvatarCommandVerb.Status, null, null);

            var verb = parameters[0].ToLowerInvariant();
            switch (verb)
            {
                case "summon":
                    return parameters.Length > 1 ? (AvatarCommandVerb.Summon, null, UsageText) : (AvatarCommandVerb.Summon, null, null);
                case "seat":
                    return parameters.Length > 1 ? (AvatarCommandVerb.Seat, null, UsageText) : (AvatarCommandVerb.Seat, null, null);
                case "gauntlet":
                    if (parameters.Length == 1)
                        return (AvatarCommandVerb.Gauntlet, null, null);
                    if (parameters.Length == 2 && string.Equals(parameters[1], "enter", StringComparison.OrdinalIgnoreCase))
                        return (AvatarCommandVerb.GauntletEnter, null, null);
                    return (AvatarCommandVerb.Gauntlet, null, UsageText);
                case "heir":
                    var name = string.Join(" ", parameters.Skip(1)).Trim();
                    if (name.Length == 0)
                        return (AvatarCommandVerb.Heir, null, "Usage: /avatar heir <name>");
                    return (AvatarCommandVerb.Heir, name, null);
                case "dress":
                    if (parameters.Length == 1)
                        return (AvatarCommandVerb.Dress, null, null);
                    if (parameters.Length == 2 && parameters[1].Equals("reset", StringComparison.OrdinalIgnoreCase))
                        return (AvatarCommandVerb.DressReset, null, null);
                    return (AvatarCommandVerb.Dress, null, UsageText);
                default:
                    return (AvatarCommandVerb.Status, null, UsageText);
            }
        }

        /// <summary>The Avatar bound to the player's monarch (their own guid when they lead or are solo), or null.</summary>
        internal static AvatarState AvatarOf(Player player)
        {
            if (player == null)
                return null;

            var monarchGuid = AvatarRights.AllegianceFacts(player).MonarchGuid;
            return AvatarManager.ForMonarch(monarchGuid);
        }

        internal static string NoAvatarText(AvatarTuning t) =>
            string.Format(CultureInfo.InvariantCulture,
                "Your allegiance has no Avatar yet. It appears once {0} or more accounts in it have played in the last {1} days.",
                t.MinActiveAccounts, t.ActiveWindowDays);

        [CommandHandler("avatar", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
            "Allegiance Avatar: status, summon, seat, heir, dress, gauntlet.", "[summon | seat | heir <name> | dress [reset] | gauntlet [enter]]")]
        public static void HandleAvatar(Session session, params string[] parameters)
        {
            var player = session?.Player;
            if (player == null)
                return;

            Run(player, parameters);
        }

        internal static void Run(Player player, string[] parameters)
        {
            var (verb, arg, error) = Parse(parameters);
            if (error != null)
            {
                AvatarNpcs.Tell(player, error);
                return;
            }

            var avatar = AvatarOf(player);
            if (avatar == null)
            {
                AvatarNpcs.Tell(player, NoAvatarText(AvatarManager.CurrentTuning()));
                return;
            }

            if (verb != AvatarCommandVerb.Status && !AvatarManager.CurrentTuning().Enabled)
            {
                AvatarNpcs.Tell(player, AvatarSeatRules.SwitchedOffMessage);
                return;
            }

            switch (verb)
            {
                case AvatarCommandVerb.Status:
                    foreach (var line in AvatarStatusText.Lines(avatar, AvatarManager.CurrentTuning(), AvatarVendor.AccountDisplayName))
                        AvatarNpcs.Tell(player, line);
                    break;

                case AvatarCommandVerb.Summon:
                    // TrySummon checks the spending rights and the Keeper itself.
                    AvatarNpcs.Tell(player, AvatarNpcs.TrySummon(player, avatar, out var refusal) ? "The Avatar appears before you." : refusal);
                    break;

                case AvatarCommandVerb.Seat:
                    // RequestSeat owns the popup and the world-thread re-check.
                    AvatarNpcs.RequestSeat(player, avatar);
                    break;

                case AvatarCommandVerb.Heir:
                    RequestHeir(player, avatar, arg);
                    break;

                case AvatarCommandVerb.Gauntlet:
                    foreach (var line in GauntletStatusLines(player, avatar))
                        AvatarNpcs.Tell(player, line);
                    break;

                case AvatarCommandVerb.GauntletEnter:
                    EnterGauntlet(player, avatar.AvatarId);
                    break;

                case AvatarCommandVerb.Dress:
                case AvatarCommandVerb.DressReset:
                    // TryBegin owns the checks and the popup; the Yes handler re-checks on the world thread.
                    if (!AvatarDress.TryBegin(player, avatar, verb == AvatarCommandVerb.DressReset, DateTime.UtcNow, out var dressRefusal))
                        AvatarNpcs.Tell(player, dressRefusal);
                    break;
            }
        }

        /// <summary>Test seam: the live run of an Avatar, as the gate sees it.</summary>
        internal static Func<uint, IGauntletGateTarget> GauntletTargetFor = avatarId => GauntletManager.ForAvatar(avatarId);

        /// <summary>
        /// /avatar gauntlet enter: the gate's own entry (GauntletGate.Enter, the same checks and the same exit stamp) for the
        /// player's Avatar's live run, so a member can enter with no gate object in reach.
        /// </summary>
        internal static void EnterGauntlet(Player player, uint avatarId)
        {
            try
            {
                GauntletGate.Enter(GauntletTargetFor(avatarId), player);
            }
            catch (Exception ex)
            {
                log4net.LogManager.GetLogger(typeof(AllegianceAvatarCommands)).Error($"[GAUNTLET] /avatar gauntlet enter by {player?.Name} threw", ex);
                GauntletGate.TellLine(player, GauntletGate.ClosedLine);
            }
        }

        /// <summary>Test seam: the status inputs (run, hold, cooldown, best) read from the live Gauntlet. Any member may ask.</summary>
        internal static Func<Player, AvatarState, IReadOnlyList<string>> GauntletStatusLines = LiveGauntletStatus;

        private static IReadOnlyList<string> LiveGauntletStatus(Player player, AvatarState avatar)
        {
            var run = GauntletManager.ForAvatar(avatar.AvatarId);

            return GauntletStatusText.Lines(
                GauntletManager.IsEnabled,
                run?.Info(DateTime.UtcNow),
                run?.DeathHoldSecondsLeft(player) ?? 0,
                GauntletManager.CooldownLeft(avatar.AvatarId),
                GauntletRecords.BestCentiOf(avatar.AvatarId));
        }

        /// <summary>
        /// Keeper only. Asks "Name X as the Avatar's heir?" and, on Yes, hands the write to the world thread, where it
        /// re-checks that the player is still the Keeper, the target is still a member, and the Avatar Version is the one
        /// read now. AvatarLifecycle.SetHeir announces the change to the allegiance.
        /// </summary>
        internal static void RequestHeir(Player keeper, AvatarState a, string name)
        {
            if (!AvatarRights.IsKeeper(keeper, a))
            {
                AvatarNpcs.Tell(keeper, "Only the Keeper can name the Avatar's heir.");
                return;
            }

            var target = FindCharacter(name);
            if (target != null && target.Value.Guid == keeper.Guid.Full)
            {
                AvatarNpcs.Tell(keeper, "You are already the Keeper.");
                return;
            }

            if (target == null || !IsMember(a, target.Value.Guid))
            {
                AvatarNpcs.Tell(keeper, "That character is not a member of your allegiance.");
                return;
            }

            var avatarId = a.AvatarId;
            var version = a.Version;
            var keeperGuid = keeper.Guid.Full;
            var targetGuid = target.Value.Guid;
            var targetName = target.Value.Name;

            var confirmation = new Confirmation_Choice(keeper.Guid, () => AvatarNpcs.WorldEnqueue(() =>
            {
                var k = AvatarNpcs.OnlinePlayer(keeperGuid);
                if (k == null)
                    return;

                if (!AvatarManager.CurrentTuning().Enabled)
                {
                    AvatarNpcs.Tell(k, AvatarSeatRules.SwitchedOffMessage);
                    return;
                }

                var current = AvatarManager.Get(avatarId);
                if (current == null || current.State != AvatarBindState.Bound || !AvatarRights.IsKeeper(k, current))
                {
                    AvatarNpcs.Tell(k, "The heir was not named: you are no longer the Keeper.");
                    return;
                }

                if (!IsMember(current, targetGuid))
                {
                    AvatarNpcs.Tell(k, "The heir was not named: that character is no longer a member of your allegiance.");
                    return;
                }

                if (current.Version != version)
                {
                    AvatarNpcs.Tell(k, "The heir was not named: the Avatar has changed since you were asked.");
                    return;
                }

                if (!AvatarLifecycle.SetHeir(current, targetGuid))
                    AvatarNpcs.Tell(k, "The heir was not named.");
            }), null, DateTime.UtcNow);

            if (!AvatarNpcs.SendConfirmation(keeper, confirmation, $"Name {targetName} as the Avatar's heir?"))
                AvatarNpcs.Tell(keeper, "You are already answering another question.");
        }

        private static bool IsMember(AvatarState a, uint guid)
        {
            if (!a.MonarchGuid.HasValue)
                return false;

            var roster = AvatarManager.RosterFor(a.MonarchGuid.Value);
            return roster?.Members != null && roster.Members.Any(m => m.Guid == guid);
        }
    }
}
