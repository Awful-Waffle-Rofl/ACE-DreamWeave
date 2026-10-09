using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using log4net;

using ACE.Database;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Pvp;
using ACE.Server.Pvp.Templates;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// PvP Template Facets admin command (Docs/Pvp/TEMPLATES.md "Commands"): /pvptemplate list | inspect | snapshot |
    /// name | enable | disable | modes | restore. AccessLevel.Admin, like /arenaadmin: it changes what every arena match is
    /// fought on. CommandHandlerFlag.None so it also works from the server console (every reply goes through
    /// CommandHandlerHelper, which handles a null session).
    ///
    /// Every database call goes through the shard queue; its callback runs off the world thread, so each reply and each
    /// catalog reload is marshalled back with WorldManager.EnqueueAction. Each write that succeeds reloads the
    /// coordinator's catalog, so the change reaches the next join without a restart. A match already dispatched keeps
    /// the definition frozen onto its bindings.
    /// </summary>
    public static class PvpTemplateAdminCommands
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        internal const string Usage =
            "[PvpTemplate] Usage: /pvptemplate <list|inspect <key>|snapshot <key> <character> [| <display name>]|name <key> <display name>|enable <key>|disable <key>|modes <key> <1v1,2v2,tugak,koth|none>|restore <character>>";

        /// <summary>The longest display name the pvp_template.display_Name column holds.</summary>
        internal const int MaxDisplayNameLength = 64;

        [CommandHandler("pvptemplate", AccessLevel.Admin, CommandHandlerFlag.None, 0,
            "PvP arena templates: list, inspect, snapshot, enable/disable, set modes, and restore a templated character.",
            "list                          - every template: key, name, version, enabled, modes, source character\n" +
            "inspect <key>                 - one template's stored build in summary\n" +
            "snapshot <key> <character> [| <name>] - capture an OFFLINE character on pvp_template_account as <key> (new keys start disabled, no modes; an existing key keeps its name unless one is given)\n" +
            "name <key> <display name>     - set the name shown beside players on results, the announcement and /top (64 characters at most)\n" +
            "enable <key> | disable <key>  - offer or withdraw a template\n" +
            "modes <key> <1v1,2v2,tugak,koth|none> - which modes offer it (koth = King of the Hill; every arena and battleground mode is templated)\n" +
            "restore <character>           - take a template off an ONLINE character now (offline: restored at their next login)")]
        public static void HandlePvpTemplate(Session session, params string[] parameters)
        {
            if (parameters == null || parameters.Length == 0)
            {
                Reply(session, Usage);
                return;
            }

            var sub = parameters[0].ToLowerInvariant();
            var rest = parameters.Skip(1).ToArray();
            var adminName = session?.Player?.Name ?? "console";

            switch (sub)
            {
                case "list":
                    HandleList(session);
                    break;

                case "inspect":
                    if (rest.Length != 1) { Reply(session, Usage); break; }
                    HandleInspect(session, rest[0]);
                    break;

                case "snapshot":
                {
                    if (rest.Length < 2) { Reply(session, Usage); break; }

                    if (!TrySplitSnapshotArgs(string.Join(" ", rest.Skip(1)), out var character, out var rawName))
                    {
                        Reply(session, Usage);
                        break;
                    }

                    string name = null;

                    if (rawName != null)
                    {
                        name = SanitizeDisplayName(rawName, out var nameProblem);

                        if (name == null)
                        {
                            Reply(session, "[PvpTemplate] " + nameProblem);
                            break;
                        }
                    }

                    HandleSnapshot(session, rest[0], character, name, adminName);
                    break;
                }

                case "name":
                    if (rest.Length < 2) { Reply(session, Usage); break; }
                    HandleName(session, rest[0], string.Join(" ", rest.Skip(1)), adminName);
                    break;

                case "enable":
                case "disable":
                    if (rest.Length != 1) { Reply(session, Usage); break; }
                    HandleEnable(session, rest[0], sub == "enable", adminName);
                    break;

                case "modes":
                    if (rest.Length < 2) { Reply(session, Usage); break; }
                    HandleModes(session, rest[0], string.Join(",", rest.Skip(1)), adminName);
                    break;

                case "restore":
                    if (rest.Length < 1) { Reply(session, Usage); break; }
                    HandleRestore(session, string.Join(" ", rest), adminName);
                    break;

                default:
                    Reply(session, Usage);
                    break;
            }
        }

        // ---- list / inspect ----

        private static void HandleList(Session session)
        {
            DatabaseManager.Shard.GetAllPvpTemplates((rows, status) => OnWorld(() =>
            {
                PvpMatchManager.ReloadTemplates();

                if (rows == null)
                {
                    Reply(session, $"[PvpTemplate] The template read failed ({status}). See the server log.");
                    return;
                }

                Reply(session, ListText(rows, status));
            }));
        }

        /// <summary>The /pvptemplate list block. Pure.</summary>
        internal static string ListText(IReadOnlyList<PvpTemplateRecord> rows, PvpTemplateStoreStatus status)
        {
            if (status == PvpTemplateStoreStatus.TablesMissing)
                return "[PvpTemplate] The template tables do not exist yet (Database/Updates/Shard/2026-10-03-00-Add-Pvp-Templates.sql has not been applied).";

            if (rows.Count == 0)
                return "[PvpTemplate] No templates. Create one with /pvptemplate snapshot <key> <character>.";

            var sb = new StringBuilder();
            sb.AppendLine($"[PvpTemplate] {rows.Count} template(s):");

            foreach (var r in rows.OrderBy(r => r.TemplateKey, StringComparer.Ordinal))
            {
                var parsed = PvpTemplateSnapshotService.TryParse(r, out _, out var error);
                var modes = PvpTemplateCatalog.ParseModes(r.Modes);

                sb.AppendLine($"  {r.TemplateKey} \"{r.DisplayName}\" v{r.Version} {(r.Enabled ? "ENABLED" : "disabled")}, modes {PvpTemplateCatalog.ModesDisplay(modes)}, from {r.SourceCharacterName} (0x{r.SourceCharacterId:X8}), snapshot {r.SnapshotAt:yyyy-MM-dd HH:mm} UTC by {r.SnapshotBy}{(parsed ? "" : $", DOES NOT PARSE ({error}) and is never offered")}");
            }

            return sb.ToString().TrimEnd();
        }

        private static void HandleInspect(Session session, string rawKey)
        {
            var key = PvpMatchCoordinator.NormalizeTemplateKey(rawKey);
            var keyProblem = PvpTemplateSnapshotService.ValidateKey(key);

            if (keyProblem != null)
            {
                Reply(session, "[PvpTemplate] " + keyProblem);
                return;
            }

            DatabaseManager.Shard.GetPvpTemplate(key, (row, status) => OnWorld(() =>
            {
                if (row == null)
                {
                    Reply(session, status == PvpTemplateStoreStatus.NotFound ? $"[PvpTemplate] No template {key}." : $"[PvpTemplate] Reading {key} returned {status}.");
                    return;
                }

                Reply(session, InspectText(row));
            }));
        }

        /// <summary>The /pvptemplate inspect block for one stored row. Pure.</summary>
        internal static string InspectText(PvpTemplateRecord row)
        {
            if (!PvpTemplateSnapshotService.TryParse(row, out var d, out var error))
                return $"[PvpTemplate] {row.TemplateKey} v{row.Version} does not parse ({error}); it is never offered. Re-snapshot it.";

            var modes = PvpTemplateCatalog.ParseModes(row.Modes);
            var sb = new StringBuilder();

            sb.AppendLine($"[PvpTemplate] {d.Key} \"{d.DisplayName}\" v{d.Version} {(row.Enabled ? "ENABLED" : "disabled")}, modes {PvpTemplateCatalog.ModesDisplay(modes)}; from {row.SourceCharacterName}, snapshot {row.SnapshotAt:yyyy-MM-dd HH:mm} UTC by {row.SnapshotBy}");
            sb.AppendLine($"  Attributes: {string.Join(", ", d.Attributes.OrderBy(a => a.Key).Select(a => $"{(PropertyAttribute)a.Key} {a.Value.InitLevel}+{a.Value.Ranks}"))}");
            sb.AppendLine($"  Vitals: {string.Join(", ", d.Vitals.OrderBy(v => v.Key).Select(v => $"{(PropertyAttribute2nd)v.Key} +{v.Value.Ranks}"))}");

            var trained = d.Skills.Where(s => s.Sac != SkillAdvancementClass.Untrained && s.Sac != SkillAdvancementClass.Inactive).ToList();
            sb.AppendLine($"  Skills ({trained.Count} trained or specialized): {string.Join(", ", trained.OrderBy(s => s.Skill.ToString(), StringComparer.Ordinal).Select(s => $"{s.Skill} {(s.Sac == SkillAdvancementClass.Specialized ? "spec" : "trained")} {s.Ranks}"))}");
            sb.AppendLine($"  Power properties: {d.PowerInts.Count}; buffs: {d.Buffs.Count}; spells: {d.Spells.Count}");
            sb.Append($"  Kit ({d.Kit.Count}): {string.Join(", ", d.Kit.Select(k => $"wcid {k.WeenieClassId}{(k.WieldLocation.HasValue ? $" worn {(EquipMask)k.WieldLocation.Value}" : " pack")}"))}");

            return sb.ToString();
        }

        // ---- snapshot ----

        /// <summary>
        /// Splits "character [| display name]". Character names may contain spaces, so the optional display name is
        /// separated by a "|". False when the character part is empty or the "|" has nothing after it. Pure.
        /// </summary>
        internal static bool TrySplitSnapshotArgs(string text, out string character, out string displayName)
        {
            character = null;
            displayName = null;

            if (string.IsNullOrWhiteSpace(text))
                return false;

            var bar = text.IndexOf('|');

            if (bar < 0)
            {
                character = text.Trim();
                return true;
            }

            character = text.Substring(0, bar).Trim();
            displayName = text.Substring(bar + 1).Trim();

            return character.Length > 0 && displayName.Length > 0;
        }

        /// <summary>
        /// A display name safe to show in chat and store: control characters and anything outside letters, digits,
        /// spaces and plain punctuation are removed (so no chat-formatting markup, tags or escapes survive), runs of
        /// spaces collapse to one, and the result must be 1 to <see cref="MaxDisplayNameLength"/> characters. Null with
        /// <paramref name="problem"/> set when nothing usable is left or it is too long. Pure.
        /// </summary>
        internal static string SanitizeDisplayName(string raw, out string problem)
        {
            problem = null;

            var sb = new StringBuilder();
            var lastWasSpace = true;

            foreach (var c in raw ?? string.Empty)
            {
                if (char.IsWhiteSpace(c))
                {
                    if (!lastWasSpace)
                        sb.Append(' ');

                    lastWasSpace = true;
                    continue;
                }

                if (char.IsControl(c) || !(char.IsLetterOrDigit(c) || "-'.,&()!?:+/#".IndexOf(c) >= 0))
                    continue;

                sb.Append(c);
                lastWasSpace = false;
            }

            var name = sb.ToString().Trim();

            if (name.Length == 0)
            {
                problem = "The display name is empty once control and formatting characters are removed.";
                return null;
            }

            if (name.Length > MaxDisplayNameLength)
            {
                problem = $"The display name is {name.Length} characters; {MaxDisplayNameLength} at most.";
                return null;
            }

            return name;
        }

        private static void HandleSnapshot(Session session, string rawKey, string characterName, string displayName, string adminName)
        {
            var key = PvpMatchCoordinator.NormalizeTemplateKey(rawKey);
            var keyProblem = PvpTemplateSnapshotService.ValidateKey(key);

            if (keyProblem != null)
            {
                Reply(session, "[PvpTemplate] " + keyProblem);
                return;
            }

            // Every reply comes back on the world thread; the catalog is reloaded with it, so a successful snapshot
            // (a new version of an enabled template) reaches the next join at once.
            void Snapshot(string name) => PvpTemplateSnapshotService.TrySnapshot(key, name, characterName, adminName, line =>
            {
                Reply(session, "[PvpTemplate] " + line);
                PvpMatchManager.ReloadTemplates();
            });

            if (displayName != null)
            {
                Snapshot(displayName);
                return;
            }

            // No name given: a re-snapshot keeps the key's current name (the capture would otherwise default it to the
            // key and the save would overwrite a name set with /pvptemplate name). A new key starts named after itself.
            DatabaseManager.Shard.GetPvpTemplate(key, (row, status) => OnWorld(() => Snapshot(row?.DisplayName)));
        }

        // ---- name ----

        private static void HandleName(Session session, string rawKey, string rawName, string adminName)
        {
            var key = PvpMatchCoordinator.NormalizeTemplateKey(rawKey);
            var keyProblem = PvpTemplateSnapshotService.ValidateKey(key);

            if (keyProblem != null)
            {
                Reply(session, "[PvpTemplate] " + keyProblem);
                return;
            }

            var name = SanitizeDisplayName(rawName, out var nameProblem);

            if (name == null)
            {
                Reply(session, "[PvpTemplate] " + nameProblem);
                return;
            }

            DatabaseManager.Shard.SetPvpTemplateDisplayName(key, name, status => OnWorld(() =>
            {
                Reply(session, WriteResultText(key, status, $"is now shown as \"{name}\" (matches already dispatched keep the name they started with)"));

                if (status == PvpTemplateStoreStatus.Ok)
                {
                    log.Info($"[PVPTEMPLATE] {adminName} renamed template {key} to '{name}'");
                    PvpMatchManager.ReloadTemplates();
                }
            }));
        }

        // ---- enable / disable / modes ----

        private static void HandleEnable(Session session, string rawKey, bool enabled, string adminName)
        {
            var key = PvpMatchCoordinator.NormalizeTemplateKey(rawKey);
            var keyProblem = PvpTemplateSnapshotService.ValidateKey(key);

            if (keyProblem != null)
            {
                Reply(session, "[PvpTemplate] " + keyProblem);
                return;
            }

            DatabaseManager.Shard.SetPvpTemplateEnabled(key, enabled, status => OnWorld(() =>
            {
                Reply(session, WriteResultText(key, status, enabled ? "is now ENABLED (offered in its modes)" : "is now disabled (offered nowhere; matches already dispatched keep it)"));

                if (status == PvpTemplateStoreStatus.Ok)
                {
                    log.Info($"[PVPTEMPLATE] {adminName} {(enabled ? "enabled" : "disabled")} template {key}");
                    PvpMatchManager.ReloadTemplates();
                }
            }));
        }

        private static void HandleModes(Session session, string rawKey, string rawModes, string adminName)
        {
            var key = PvpMatchCoordinator.NormalizeTemplateKey(rawKey);
            var keyProblem = PvpTemplateSnapshotService.ValidateKey(key);

            if (keyProblem != null)
            {
                Reply(session, "[PvpTemplate] " + keyProblem);
                return;
            }

            var modes = PvpTemplateCatalog.NormalizeModes(rawModes, out var modeProblem);

            if (modes == null)
            {
                Reply(session, "[PvpTemplate] " + modeProblem);
                return;
            }

            DatabaseManager.Shard.SetPvpTemplateModes(key, modes, status => OnWorld(() =>
            {
                Reply(session, WriteResultText(key, status, $"is now offered in: {(modes.Length > 0 ? PvpTemplateCatalog.ModesDisplay(modes) : "no modes")}"));

                if (status == PvpTemplateStoreStatus.Ok)
                {
                    log.Info($"[PVPTEMPLATE] {adminName} set template {key} modes to '{modes}'");
                    PvpMatchManager.ReloadTemplates();
                }
            }));
        }

        /// <summary>The reply for an enable/disable/modes write. Pure.</summary>
        internal static string WriteResultText(string key, PvpTemplateStoreStatus status, string successTail)
        {
            switch (status)
            {
                case PvpTemplateStoreStatus.Ok: return $"[PvpTemplate] {key} {successTail}.";
                case PvpTemplateStoreStatus.NotFound: return $"[PvpTemplate] No template {key}. Nothing was changed.";
                case PvpTemplateStoreStatus.TablesMissing: return "[PvpTemplate] The template tables do not exist yet. Nothing was changed.";
                default: return $"[PvpTemplate] Changing {key} failed ({status}). Nothing was changed; see the server log.";
            }
        }

        // ---- restore ----

        /// <summary>
        /// Takes a template off a character (TEMPLATES.md "Rulings": a failed restore leaves the player locked until staff
        /// run this). ONLINE: refused while the character is in a live match (cancel the match with /arenaadmin cancel,
        /// whose exit restores them); otherwise the idempotent restore runs on the character's own queue and the result
        /// comes back here. OFFLINE: nothing can be done to an offline biota safely from here - the login restore runs
        /// at their next login, before they enter the world, and destroys every issued item; if that restore fails they
        /// log in locked and this command is run again once they are online.
        /// </summary>
        private static void HandleRestore(Session session, string characterName, string adminName)
        {
            var online = PlayerManager.GetOnlinePlayer(characterName);

            if (online == null)
            {
                var offline = PlayerManager.GetOfflinePlayer(characterName);

                if (offline == null)
                {
                    Reply(session, $"[PvpTemplate] No character named {characterName}.");
                    return;
                }

                var hasRecord = !string.IsNullOrEmpty(offline.GetProperty(PropertyString.PvpTemplateRestore));

                Reply(session, hasRecord
                    ? $"[PvpTemplate] {offline.Name} is offline and still carries a template restore record. It is restored automatically at their next login (before they enter the world, issued items destroyed). If that restore fails they log in locked: run this again once they are online."
                    : $"[PvpTemplate] {offline.Name} is offline and carries no template restore record. Nothing to do.");
                return;
            }

            if (!online.IsPvpTemplated)
            {
                Reply(session, $"[PvpTemplate] {online.Name} is not wearing a template. Nothing to do.");
                return;
            }

            if (online.IsInPvpMatch)
            {
                Reply(session, $"[PvpTemplate] {online.Name} is in a live arena match. End it with /arenaadmin cancel; its exit restores them.");
                return;
            }

            online.EnqueueAction(new ActionEventDelegate(() =>
            {
                bool restored;

                try
                {
                    restored = online.RestorePvpTemplate($"staff restore by {adminName}");
                }
                catch (Exception ex)
                {
                    log.Error($"[PVPTEMPLATE] staff restore of {online.Name} by {adminName} threw", ex);
                    restored = false;
                }

                var still = online.IsPvpTemplated;

                log.Warn($"[PVPTEMPLATE] {adminName} ran /pvptemplate restore on {online.Name} (0x{online.Guid.Full:X8}): {(still ? "the record is STILL present" : "restored")}");

                OnWorld(() => Reply(session, still
                    ? $"[PvpTemplate] The restore of {online.Name} did not clear the record (see the server log). They stay locked."
                    : $"[PvpTemplate] {online.Name} {(restored ? "has been restored" : "is no longer templated")}."));
            }));

            Reply(session, $"[PvpTemplate] Restoring {online.Name}...");
        }

        // ---- shared ----

        private static void OnWorld(Action action)
        {
            WorldManager.EnqueueAction(new ActionEventDelegate(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    log.Error("[PVPTEMPLATE] a /pvptemplate reply threw", ex);
                }
            }));
        }

        private static void Reply(Session session, string text) => CommandHandlerHelper.WriteOutputInfo(session, text, ChatMessageType.Broadcast);
    }
}
