using System;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// Admin tooling for live verification of Paragon weapon mods without buying gems. It writes through the same
    /// <see cref="ParagonModManager.ApplyGemTo"/> and <see cref="ParagonModManager.ClearGems"/> mutations the
    /// player flows use and runs the same weapon-state rules, but skips the gem and the reset item. Like the
    /// real flow it works on a weapon in the PACK only: changing a wielded weapon's item level would leave its
    /// level-gated set spells stale (they are only recomputed on equip and level-up). It costs and refunds nothing.
    /// </summary>
    public static class ParagonModTestCommands
    {
        [CommandHandler("paragonmod", AccessLevel.Admin, CommandHandlerFlag.RequiresWorld, 1,
            "Applies a Paragon mod slot to, or clears the Paragon mods from, the last appraised weapon, which must be in your pack.",
            "apply <WeaponModId name or number> [tokenCost] | clear\n" +
            "  The weapon must be in your inventory, not wielded.\n" +
            "  apply - runs the real gem rules (Paragon set, level 50-54, fully leveled, slots in step, class applies) then writes the next slot\n" +
            "          (bypasses the player overflow confirmation: it never prompts when the gem would pass a perfect roll)\n" +
            "  clear - removes every Paragon slot and returns the weapon to level 50. No tokens are refunded.")]
        public static void HandleParagonMod(Session session, params string[] parameters)
        {
            var player = session.Player;
            var weapon = CommandHandlerHelper.GetLastAppraisedObject(session);

            if (weapon == null || !WeaponModParagon.IsCarrier(weapon))
            {
                Send(session, "No weapon: appraise a weapon in your pack first.");
                return;
            }

            if (player.FindObject(weapon.Guid.Full, Player.SearchLocations.MyInventory) == null)
            {
                Send(session, $"The {weapon.Name} is not in your pack. Unwield it first: changing a wielded weapon's level would leave its set spells stale.");
                return;
            }

            switch (parameters[0].ToLowerInvariant())
            {
                case "apply":
                    Apply(session, weapon, parameters);
                    return;

                case "clear":
                    Clear(session, weapon);
                    return;

                default:
                    Send(session, "Usage: @paragonmod apply <WeaponModId name or number> [tokenCost] | clear");
                    return;
            }
        }

        private static void Apply(Session session, WorldObject weapon, string[] parameters)
        {
            if (parameters.Length < 2)
            {
                Send(session, "Usage: @paragonmod apply <WeaponModId name or number> [tokenCost]");
                return;
            }

            if (!TryParseModId(parameters[1], out var modId))
            {
                Send(session, $"'{parameters[1]}' is not a WeaponModId.");
                return;
            }

            var cost = 0;

            if (parameters.Length > 2 && !int.TryParse(parameters[2], out cost))
            {
                Send(session, $"'{parameters[2]}' is not a token cost.");
                return;
            }

            var weaponClass = WeaponClassifier.Classify(weapon);
            var setId = weapon.GetProperty(PropertyInt.EquipmentSetId);
            var level = weaponClass != WeaponClass.None && weaponClass != WeaponClass.Shield && ParagonModManager.IsParagonSet(setId) ? ParagonModManager.CurrentLevel(weapon) : null;

            var refusal = ParagonModManager.ResolveGemRefusal(WeaponModRegistry.Enabled(), true, true, weaponClass,
                setId, weapon.ItemMaxLevel, level,
                ParagonModManager.ReadFilledSlots(weapon), (int)modId, weapon.ItemXpStyle,
                level != null && ParagonModManager.NextTotalReadsBack(weapon));

            if (refusal != ParagonModManager.ParagonModRefusal.None)
            {
                Send(session, ParagonModManager.RefusalMessage(refusal, "gem", weapon.Name) + $" ({refusal})");
                return;
            }

            if (!ParagonModManager.ApplyGemTo(weapon, (int)modId, cost))
            {
                Send(session, "Nothing was written.");
                return;
            }

            Save(session, weapon);

            Send(session, $"{weapon.Name}: {modId} written. Level {weapon.ItemMaxLevel}/{weapon.ItemMaxLevel}, tokens spent {weapon.GetProperty(PropertyInt.ParagonModTokensSpent) ?? 0}.");
        }

        private static void Clear(Session session, WorldObject weapon)
        {
            if (ParagonModManager.ReadFilledSlots(weapon) == 0)
            {
                Send(session, $"{weapon.Name} has no Paragon mods.");
                return;
            }

            var spent = ParagonModManager.ClearGems(weapon);

            Save(session, weapon);

            var refund = ParagonModManager.RefundFor(spent, PropertyManager.GetDouble(ParagonModManager.RefundFractionKey).Item);

            Send(session, $"{weapon.Name}: Paragon mods cleared, back to level {weapon.ItemMaxLevel}. Tokens spent was {spent}; a real reset would refund {refund}. Nothing was refunded.");
        }

        private static void Save(Session session, WorldObject weapon)
        {
            weapon.ChangesDetected = true;
            weapon.SaveBiotaToDatabase();

            ParagonModManager.UpdateObj(session.Player, weapon);
        }

        private static bool TryParseModId(string text, out WeaponModId modId)
        {
            if (Enum.TryParse(text, true, out modId) && Enum.IsDefined(typeof(WeaponModId), modId))
                return true;

            modId = default;
            return false;
        }

        private static void Send(Session session, string message)
        {
            session.Network.EnqueueSend(new GameMessageSystemChat(message, ChatMessageType.Broadcast));
        }
    }
}
