using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.ClassAbilities;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Managers;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// Interim command front end for the Drift Network trainer economy (buy a class ability token for class ability
    /// points, refund an unused one). The intended front end is the 6 trainer NPCs + 1 exchange NPC in the Drift
    /// Network; those NPCs call the exact same Player engine methods this command does
    /// (<see cref="ACE.Server.WorldObjects.Player.TryPurchaseClassAbilityVoucher"/> /
    /// <see cref="ACE.Server.WorldObjects.Player.RefundUnusedVoucher"/>). This command exists so the engine is
    /// exercisable in-game before that content ships, and can be retired once the NPCs are wired.
    /// </summary>
    public static class ClassAbilityTrainerCommands
    {
        [CommandHandler("cavoucher", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
            "Drift Network class ability trainer (interim): buy a class ability token for class ability points, or refund an unused one",
            "list | buy <skill> [tier] | refund <skill>")]
        public static void HandleCsVoucher(Session session, params string[] parameters)
        {
            if (!PropertyManager.GetBool("class_abilities_enabled").Item)
            {
                Reply(session, "Class abilities are not currently enabled on this server.");
                return;
            }

            var sub = parameters.Length > 0 ? parameters[0].ToLowerInvariant() : "list";

            switch (sub)
            {
                case "list":
                    HandleList(session);
                    break;
                case "buy":
                    HandleBuy(session, parameters);
                    break;
                case "refund":
                    HandleRefund(session, parameters);
                    break;
                default:
                    Reply(session, "Usage: /cavoucher list | buy <skill> [tier] | refund <skill>");
                    break;
            }
        }

        private static void Reply(Session session, string message)
        {
            session.Network.EnqueueSend(new GameMessageSystemChat(message, ChatMessageType.Broadcast));
        }

        private static void HandleList(Session session)
        {
            var player = session.Player;

            Reply(session, "Drift Network trainer - buy a class ability token for class ability points, then use it from your pack (free):");

            ClassAbilityId? last = null;
            foreach (var o in ClassAbilityTokenCatalog.AllOfferings())
            {
                if (last != o.SkillId)
                {
                    Reply(session, $"  {o.Definition.DisplayName} ({o.Definition.Name}) - your rank {player.GetClassAbilityRank(o.SkillId)}/{o.Definition.MaxRank}");
                    last = o.SkillId;
                }

                var pts = o.Definition.CostPerRank[o.Tier - 1];
                Reply(session, $"      rank {o.Tier}: {pts:N0} class ability point{(pts == 1 ? "" : "s")}");
            }

            Reply(session, "Buy the next rank with /cavoucher buy <skill>, e.g. /cavoucher buy multishot. Refund an unused token with /cavoucher refund <skill>.");
            Reply(session, $"Points: {player.AvailableClassAbilityPoints:N0} available.");
        }

        private static void HandleBuy(Session session, string[] parameters)
        {
            if (parameters.Length < 2)
            {
                Reply(session, "Usage: /cavoucher buy <skill> [tier], e.g. /cavoucher buy multishot. Tier defaults to your next rank.");
                return;
            }

            if (!ClassAbilityRegistry.TryGetByName(parameters[1], out var skill))
            {
                Reply(session, $"Unknown class ability '{parameters[1]}'. Use /cavoucher list to see buyable skills.");
                return;
            }

            // Tier defaults to the next rank the player can learn.
            var tier = session.Player.GetClassAbilityRank(skill.Id) + 1;
            if (parameters.Length > 2 && (!int.TryParse(parameters[2], out tier) || tier < 1))
            {
                Reply(session, "Tier must be a positive number, e.g. /cavoucher buy multishot 1.");
                return;
            }

            if (!ClassAbilityTokenCatalog.TryResolve(skill.Id, tier, out var offering))
            {
                Reply(session, $"There is no rank {tier} token for {skill.DisplayName} (it has {skill.MaxRank} rank{(skill.MaxRank == 1 ? "" : "s")}). Use /cavoucher list.");
                return;
            }

            if (!session.Player.TryPurchaseClassAbilityVoucher(offering, out var error))
                Reply(session, error);
            // success message comes from TryPurchaseClassAbilityVoucher
        }

        private static void HandleRefund(Session session, string[] parameters)
        {
            if (parameters.Length < 2)
            {
                Reply(session, "Usage: /cavoucher refund <skill> - returns an unused class ability token and refunds its points.");
                return;
            }

            if (!ClassAbilityRegistry.TryGetByName(parameters[1], out var skill))
            {
                Reply(session, $"Unknown class ability '{parameters[1]}'. Use /cavoucher list to see skills.");
                return;
            }

            var voucher = session.Player.GetAllPossessions().FirstOrDefault(wo =>
                (wo.GetProperty(PropertyInt.ClassAbilityTokenId) ?? 0) == (int)skill.Id);

            if (voucher == null)
            {
                Reply(session, $"You do not hold an unused {skill.DisplayName} training token.");
                return;
            }

            if (!session.Player.RefundUnusedVoucher(voucher, out var error))
                Reply(session, error);
            // success message comes from RefundUnusedVoucher
        }
    }
}
