using System.Collections.Generic;
using System.Linq;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.Knots
{
    /// <summary>The outcome of <see cref="KnotLowering.Decide"/>.</summary>
    public enum LowerVerdict
    {
        Ok,
        /// <summary>The requested level is below <see cref="KnotLowering.MinLevel"/>.</summary>
        BelowFloor,
        /// <summary>The Knot is already at or below the floor, so there is nothing to lower to.</summary>
        AlreadyAtFloor,
        /// <summary>The requested level is not strictly below the Knot's current level (raising is never allowed).</summary>
        NotLower,
        /// <summary>The Knot has a recorded run checkpoint (non-zero KnotPhase).</summary>
        RunInProgress,
    }

    /// <summary>
    /// /knot lower &lt;level&gt;: players may DOWNGRADE a carried Knot's level, never raise it, and never below
    /// <see cref="MinLevel"/>. Irreversible, so it asks a Yes/No first and re-validates on Yes.
    /// </summary>
    public static class KnotLowering
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// The lowest level a Knot can be lowered to. Owner ruling (2026-10-26): players may run lower Knots without
        /// grinding low Fibers, but nothing below 200. A code constant on purpose, not a server setting.
        /// </summary>
        public const int MinLevel = 200;

        /// <summary>
        /// The pure rule. <paramref name="current"/> is the Knot's KnotLevel, <paramref name="phase"/> its KnotPhase
        /// <paramref name="liveAttempt"/>: a pinnacle attempt is open on this Knot (PinnacleFightManager.ResumableAttempt is not None).
        /// (KnotEncounterEvents.OnPhaseReached records the furthest phase a run reached, so non-zero means a run has
        /// started on this Knot and its checkpoint was calibrated at the current level).
        /// </summary>
        public static LowerVerdict Decide(int current, int? phase, int requested, bool liveAttempt = false)
        {
            if (current <= MinLevel)
                return LowerVerdict.AlreadyAtFloor;

            if (requested < MinLevel)
                return LowerVerdict.BelowFloor;

            if (requested >= current)
                return LowerVerdict.NotLower;

            // A live attempt keeps the level its fight copy was created with, so lowering mid-attempt would desync Knot and fight.
            if ((phase ?? 0) != 0 || liveAttempt)
                return LowerVerdict.RunInProgress;

            return LowerVerdict.Ok;
        }

        public static string Refusal(LowerVerdict verdict, int current) => verdict switch
        {
            LowerVerdict.AlreadyAtFloor when current <= 0 => "That Knot has no level, so it cannot be lowered.",
            LowerVerdict.AlreadyAtFloor => $"That Knot is already at Level {current}; Knots cannot be lowered below Level {MinLevel}.",
            LowerVerdict.BelowFloor => $"Knots cannot be lowered below Level {MinLevel}.",
            LowerVerdict.NotLower => $"That Knot is Level {current}. You can only lower it, to a level below {current}.",
            LowerVerdict.RunInProgress => "That Knot has a run in progress and cannot be lowered.",
            _ => null,
        };

        public static string Prompt(int from, int to) => $"Lower this Knot from Level {from} to Level {to}? This cannot be undone.";

        /// <summary>The Knot's display name at <paramref name="level"/>: the bound form when it is bound to a pinnacle.</summary>
        internal static string NameFor(string pinnacleKey, int level)
            => string.IsNullOrEmpty(pinnacleKey)
                ? KnotItems.KnotName(level)
                : KnotItems.BoundKnotName(KnotClearRecords.KnotName(pinnacleKey), level);

        /// <summary>Every Knot the player carries (main pack and side packs).</summary>
        internal static List<WorldObject> CarriedKnots(Container pack)
        {
            var found = new List<WorldObject>();

            foreach (var item in pack.Inventory.Values)
            {
                if (KnotItems.IsKnot(item))
                    found.Add(item);

                if (item.WeenieType == WeenieType.Container && item is Container side)
                    found.AddRange(CarriedKnots(side));
            }

            return found;
        }

        /// <summary>
        /// Resolves the Knot to lower: the player's currently selected (appraised) item when it is a Knot in their own
        /// inventory, else the one Knot they carry. Null (with a message sent) when neither resolves.
        /// </summary>
        private static WorldObject ResolveKnot(Player player)
        {
            if (player.CurrentAppraisalTarget.HasValue)
            {
                var selected = player.FindObject(player.CurrentAppraisalTarget.Value, Player.SearchLocations.MyInventory);

                if (selected != null && KnotItems.IsKnot(selected))
                    return selected;
            }

            var carried = CarriedKnots(player);

            if (carried.Count == 1)
                return carried[0];

            if (carried.Count == 0)
                Say(player, "You are not carrying a Knot.");
            else
                Say(player, $"Select the Knot you want to lower first. You carry Knots of Level {string.Join(", ", carried.Select(k => k.GetProperty(PropertyInt.KnotLevel) ?? 0).OrderBy(l => l))}.");

            return null;
        }

        private static void Say(Player player, string message)
            => player.Session?.Network.EnqueueSend(new GameMessageSystemChat(message, ChatMessageType.Broadcast));

        private static bool HasLiveAttempt(Player player, WorldObject knot)
            => ACE.Server.Pinnacles.PinnacleFightManager.ResumableAttempt(player.Guid.Full, knot.Guid.Full) != KnotResume.None;

        /// <summary>Entry point for /knot lower: resolve the Knot, validate, then ask.</summary>
        public static void Begin(Player player, int requested)
        {
            var knot = ResolveKnot(player);

            if (knot == null)
                return;

            var current = knot.GetProperty(PropertyInt.KnotLevel) ?? 0;
            var verdict = Decide(current, knot.GetProperty(PropertyInt.KnotPhase), requested, HasLiveAttempt(player, knot));

            if (verdict != LowerVerdict.Ok)
            {
                Say(player, Refusal(verdict, current));
                return;
            }

            if (!player.ConfirmationManager.EnqueueSend(new Confirmation_KnotLower(player.Guid, knot.Guid.Full, requested), Prompt(current, requested)))
                player.SendWeenieError(WeenieError.ConfirmationInProgress);
        }

        /// <summary>The Yes half: re-validate against the Knot as it is now, then apply.</summary>
        internal static void Confirm(Player player, uint knotGuid, int requested)
        {
            var knot = player.FindObject(knotGuid, Player.SearchLocations.MyInventory);

            if (knot == null || !KnotItems.IsKnot(knot))
            {
                Say(player, "That Knot is no longer in your pack.");
                return;
            }

            var current = knot.GetProperty(PropertyInt.KnotLevel) ?? 0;
            var verdict = Decide(current, knot.GetProperty(PropertyInt.KnotPhase), requested, HasLiveAttempt(player, knot));

            if (verdict != LowerVerdict.Ok)
            {
                Say(player, Refusal(verdict, current));
                return;
            }

            player.UpdateProperty(knot, PropertyInt.KnotLevel, requested);
            player.UpdateProperty(knot, PropertyString.Name, NameFor(knot.GetProperty(PropertyString.PinnacleKey), requested));
            player.EnqueueBroadcast(new GameMessageUpdateObject(knot));

            log.Info($"[KNOTS] {player.Name} (0x{player.Guid.Full:X8}) lowered Knot 0x{knot.Guid.Full:X8} from Level {current} to Level {requested}");
            Say(player, $"The Knot settles to Level {requested}.");
        }
    }

    /// <summary>The /knot lower question. Carries the Knot and the target level so Yes can re-validate.</summary>
    public class Confirmation_KnotLower : Confirmation
    {
        public readonly uint KnotGuid;
        public readonly int Level;

        public Confirmation_KnotLower(ObjectGuid playerGuid, uint knotGuid, int level)
            : base(playerGuid, ConfirmationType.Yes_No)
        {
            KnotGuid = knotGuid;
            Level = level;
        }

        public override void ProcessConfirmation(bool response, bool timeout = false)
        {
            if (!response || timeout)
                return;

            var player = Player;
            if (player == null)
                return;

            var knotGuid = KnotGuid;
            var level = Level;

            // Hop onto the player's own queue before touching the Knot, as Confirmation_KnotUse does.
            player.EnqueueAction(new ActionEventDelegate(() => KnotLowering.Confirm(player, knotGuid, level)));
        }
    }
}
