using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Entity.Facets;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// Player Facets: /facet, /facet &lt;N&gt;, /facet name &lt;N&gt; &lt;text&gt; and /facet help. All the actual
    /// gating, mutation and persistence lives on Player_Facets.cs (Player.CheckFacetGates,
    /// Player.TrySwitchFacet, Player.TrySetFacetName); this file is the thin command-layer dispatch
    /// plus the wording of the first-visit confirmation prompt. See Docs/Facets/DESIGN.md section 8.
    /// </summary>
    public static class FacetCommands
    {
        [CommandHandler("facet", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
            "Player Facets: switch between configurable builds for this character.",
            "[<N> | name <N> <text> | help]\n" +
            "  (no args)          - show your facets\n" +
            "  <N>                - turn to facet N, e.g. /facet 2\n" +
            "  name <N> <text>    - set facet N's display name, e.g. /facet name 1 Void\n" +
            "  help               - what a facet keeps, shares and costs")]
        public static void HandleFacet(Session session, params string[] parameters)
        {
            var player = session.Player;

            // facet_enabled gates the WHOLE command family - a plain refusal rather than being hidden,
            // so a player who was told about the feature gets an answer (PropertyManager tunable doc).
            if (!FacetTunables.DialSource().Enabled)
            {
                Msg(player, "Facets are not available on this server yet.");
                return;
            }

            if (parameters.Length == 0)
            {
                HandleList(player);
                return;
            }

            if (string.Equals(parameters[0], "name", StringComparison.OrdinalIgnoreCase))
            {
                HandleName(player, parameters.Skip(1).ToArray());
                return;
            }

            if (string.Equals(parameters[0], "help", StringComparison.OrdinalIgnoreCase))
            {
                Msg(player, ComposeHelp(FacetTunables.DialSource()));
                return;
            }

            if (!int.TryParse(parameters[0], out var targetSlot))
            {
                Msg(player, "Usage: /facet [<N> | name <N> <text> | help] - type /facet help for what a facet actually does.");
                return;
            }

            HandleSwitch(session, player, targetSlot, false);
        }

        /// <summary>
        /// /facet help. Everything variable in it - the slot count, the unlock levels, whether there is a
        /// location restriction at all and what it is called - is read from the SAME <see cref="FacetDials"/>
        /// the gates read, for the reason SendFacetUnlockNoticeIfDue gives about its own wording: help that
        /// hardcodes a threshold promises a rule the server is not enforcing the moment the tunable moves.
        /// An empty allowlist means no location restriction (CheckFacetGates' remarks on Count == 0), so the
        /// location line is dropped rather than named.
        ///
        /// Pure and static so it is unit-testable without a live Player, the same way
        /// Player.ComposeSkillCreditCost and Player.ComposeAttributeSurplusLine are.
        /// </summary>
        internal static string ComposeHelp(FacetDials dials)
        {
            var sb = new StringBuilder();

            sb.AppendLine("Facets - separate builds for one character.");
            sb.AppendLine();
            sb.AppendLine("  /facet                 - list your facets, with the active one marked");
            sb.AppendLine($"  /facet <N>             - turn to facet N (1-{Player.MaxFacetSlot})");
            sb.AppendLine("  /facet name <N> <text> - name facet N, up to 32 characters");
            sb.AppendLine("  /facet help            - this text");
            sb.AppendLine();
            sb.AppendLine("Examples:");
            sb.AppendLine("  /facet 2               - turn to your second facet");
            sb.AppendLine("  /facet name 1 Void     - names your first facet \"Void\"");
            sb.AppendLine();
            sb.AppendLine("Each facet keeps its own trained and specialized skills, its own class abilities, its own arrangement of your innate attribute points, and the gear it was wearing when you left it.");
            sb.AppendLine("Every facet shares your level, lifetime experience, attribute ranks, vitals, augmentations and spellbook. Switching costs nothing and never resets any of those.");
            sb.AppendLine();

            var unlocks = new List<string>();

            for (var slot = 2; slot <= Player.MaxFacetSlot; slot++)
            {
                var requiredLevel = slot switch
                {
                    2 => dials.Slot2Level,
                    3 => dials.Slot3Level,
                    4 => dials.Slot4Level,
                    _ => 0,
                };

                if (requiredLevel > 0)
                    unlocks.Add($"facet {slot} at level {requiredLevel:N0}");
            }

            sb.Append("Facet 1 always exists.");
            sb.AppendLine(unlocks.Count > 0 ? $" You unlock {string.Join(", ", unlocks)}." : "");

            if (dials.Allowlist.Count > 0)
                sb.AppendLine($"You can only change facets in {dials.AllowlistName}.");

            sb.AppendLine("The first time you turn to a facet you are asked to confirm: it has never been cut, so it starts with its skills untrained - except always-trained and augmentation-specialized ones - and no class abilities learned, and hands back all of its experience, skill credits and class ability points for you to spend differently.");
            sb.AppendLine("Gear you are wearing is unequipped into your pack when you leave a facet, and the facet you turn to re-equips whatever it remembers, telling you about anything it could not.");

            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// The switch entry point, re-entered with confirmed=true from the Confirmation_Custom callback.
        /// Gates are re-checked on EVERY call (Player.CheckFacetGates, and again inside
        /// Player.TrySwitchFacet), including the confirmed re-entry - ClassAbilityCommands' unlearn path
        /// documents why: the player can walk out of the allowlisted area, start a trade, or die while the
        /// confirmation prompt is up.
        ///
        /// WHETHER a confirmation is needed is decided by Player.TrySwitchFacet, from the single
        /// character_facet read it has to do anyway; only the prompt WORDING lives here. The switch used
        /// to answer that question with its own extra blocking read (Player.IsFacetSlotFresh, now gone),
        /// which meant every switch blocked the world tick thread twice - and a shard stall there is paid
        /// by every player in the landblock group, not by the one who typed the command. The confirmed
        /// re-entry runs the whole method again, so it still gets its own fresh read and its own full gate
        /// re-check; a first visit costs two reads, which is once per slot ever.
        /// </summary>
        private static void HandleSwitch(Session session, Player player, int targetSlot, bool confirmed)
        {
            if (!player.CheckFacetGates(targetSlot, out var gateRefusal))
            {
                Msg(player, gateRefusal);
                return;
            }

            if (player.TrySwitchFacet(targetSlot, confirmed, out var result, out var needsConfirmation))
            {
                Msg(player, result);
                return;
            }

            if (!needsConfirmation)
            {
                Msg(player, result);
                return;
            }

            // First visit to the target slot (DESIGN.md section 8). TrySwitchFacet mutated nothing on
            // this path - it refused before its first write - so re-entering it after the prompt is a
            // clean re-run, not a resume.
            var prompt = $"Turn to facet {targetSlot}? This face has never been cut: your current " +
                         "gear will be stripped to your pack, and the new facet starts fresh (skills untrained except " +
                         "always-trained and augmentation-specialized ones, no class abilities learned).";

            if (!player.ConfirmationManager.EnqueueSend(new Confirmation_Custom(player.Guid, () => HandleSwitch(session, player, targetSlot, true)), prompt))
                player.SendWeenieError(WeenieError.ConfirmationInProgress);
        }

        /// <summary>
        /// Bare /facet: number, name, unlock state, and a one-line specialized-skill summary for every
        /// slot, with the active one marked. The active slot's summary comes from the LIVE character
        /// (it may have no stored row yet, if it has never been switched away from); every other slot
        /// reads its stored row, or reports "not yet cut" when there is none.
        /// </summary>
        private static void HandleList(Player player)
        {
            // Read-only display: ADVISORY, per Player.GetCharacterFacetRowsOrEmpty's remarks - a failed
            // or timed-out read just shows a stale/empty listing here, never a mutation.
            var rows = player.GetCharacterFacetRowsOrEmpty();
            var dials = FacetTunables.DialSource();

            var sb = new StringBuilder();
            sb.AppendLine("Facets:");

            for (var slot = 1; slot <= Player.MaxFacetSlot; slot++)
            {
                var active = slot == player.ActiveFacetSlot;
                var row = rows.FirstOrDefault(r => r.Slot == (byte)slot);

                var requiredLevel = slot switch
                {
                    2 => dials.Slot2Level,
                    3 => dials.Slot3Level,
                    4 => dials.Slot4Level,
                    _ => 0,
                };

                var unlocked = requiredLevel <= 0 || (player.Level ?? 0) >= requiredLevel;

                var label = string.IsNullOrWhiteSpace(row?.Name) ? $"Facet {slot}" : $"Facet {slot} \"{row.Name}\"";

                if (active)
                    label += " (active)";

                // A grandfathered active slot (its threshold raised past the player's current level
                // while they stood on it - DESIGN.md section 2, "the player keeps the slot") must never
                // display as locked: they are not locked out of anything, they simply could not switch
                // back in if they left. Only a non-active, currently out-of-reach slot shows this line.
                if (!unlocked && !active)
                {
                    sb.AppendLine($"  {label} - locked until level {requiredLevel:N0}");
                    continue;
                }

                List<FacetSkillEntry> skills;

                if (active)
                    skills = player.CaptureFacetSkills();
                else if (row != null)
                    skills = FacetSnapshot.DeserializeSkills(row.SkillsJson);
                else
                    skills = null;

                string summary;

                if (skills == null)
                {
                    summary = "not yet cut";
                }
                else
                {
                    var specialized = skills
                        .Where(s => s.Sac == SkillAdvancementClass.Specialized)
                        .Select(s => s.Skill.ToSentence())
                        .ToList();

                    summary = specialized.Count > 0 ? string.Join(", ", specialized) : "no specialized skills";
                }

                sb.AppendLine($"  {label} - {summary}");
            }

            sb.AppendLine("Turn to one with /facet <N>, name one with /facet name <N> <text>. Type /facet help for what a facet keeps and what it shares.");

            Msg(player, sb.ToString().TrimEnd());
        }

        private static void HandleName(Player player, string[] args)
        {
            if (args.Length < 2 || !int.TryParse(args[0], out var slot))
            {
                Msg(player, "Usage: /facet name <N> <text> - for example, /facet name 1 Void names your first facet \"Void\".");
                return;
            }

            var name = string.Join(" ", args.Skip(1)).Trim();

            if (name.Length > 32)
                name = name.Substring(0, 32);

            if (!player.TrySetFacetName(slot, name, out var refusal))
            {
                Msg(player, refusal);
                return;
            }

            Msg(player, $"Facet {slot} is now named \"{name}\".");
        }

        private static void Msg(Player player, string text)
        {
            player.Session.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
        }
    }
}
