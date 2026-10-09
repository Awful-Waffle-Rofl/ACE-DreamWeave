using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

using ACE.Database;
using ACE.Database.Adapter;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;

namespace ACE.Server.Pvp.Templates
{
    /// <summary>
    /// The live half of /pvptemplate snapshot (TEMPLATES.md "Authoring"), for Phase C's admin command to call, and
    /// the one place a stored row becomes a definition.
    ///
    /// Snapshot flow: validate (key shape, pvp_template_account set, the character exists, is OFFLINE, and is on
    /// that account) -> read the character's possessed biotas through the shard queue (EF entities) -> convert each
    /// with BiotaConverter.ConvertToEntityBiota on that same callback, so no EF entity ever crosses into the capture
    /// -> hop to the world thread -> PvpTemplateKit.CaptureDefinition against the offline character's in-memory
    /// runtime biota under its own lock -> SavePvpTemplateSnapshot (row + history, version bumped by the store).
    /// Every reply is delivered on the world thread.
    /// </summary>
    public static class PvpTemplateSnapshotService
    {
        private static readonly Regex KeyShape = new Regex("^[a-z0-9_-]{1,32}$", RegexOptions.Compiled);

        /// <summary>
        /// The words /arena join reads as flags ("duo", "group"), so a template keyed with one could never be chosen
        /// at join. Reserved: no template may take one as its key.
        /// </summary>
        public static readonly IReadOnlyCollection<string> ReservedKeys = new[] { "duo", "group" };

        /// <summary>Null when <paramref name="key"/> is a usable template key, else the refusal.</summary>
        public static string ValidateKey(string key)
        {
            if (string.IsNullOrEmpty(key) || !KeyShape.IsMatch(key))
                return "A template key is 1 to 32 characters of lowercase letters, digits, '-' and '_'.";

            if (ReservedKeys.Contains(key))
                return $"'{key}' is reserved (/arena join reads it as a flag), so it cannot be a template key.";

            return null;
        }

        /// <summary>
        /// Starts a snapshot. <paramref name="reply"/> receives each admin-facing line, on the world thread. Returns
        /// false (having replied) when the request was refused before anything was queued.
        /// </summary>
        public static bool TrySnapshot(string key, string displayName, string characterName, string adminName, Action<string> reply)
        {
            reply ??= _ => { };

            var keyProblem = ValidateKey(key);

            if (keyProblem != null)
            {
                reply(keyProblem);
                return false;
            }

            var account = PvpTemplateSettings.AccountSource();

            if (string.IsNullOrWhiteSpace(account))
            {
                reply("pvp_template_account is empty, so no character can be snapshotted. Set it to the template account's name first.");
                return false;
            }

            if (PlayerManager.GetOnlinePlayer(characterName) != null)
            {
                reply($"{characterName} is online. Log the character out before snapshotting it.");
                return false;
            }

            var offline = PlayerManager.GetOfflinePlayer(characterName);

            if (offline == null)
            {
                reply($"No character named {characterName} was found.");
                return false;
            }

            if (offline.Account == null || !string.Equals(offline.Account.AccountName, account, StringComparison.OrdinalIgnoreCase))
            {
                reply($"{offline.Name} is not on the template account ({account}), so it cannot be snapshotted.");
                return false;
            }

            var characterId = offline.Guid.Full;

            DatabaseManager.Shard.GetPossessedBiotasInParallel(characterId, possessed =>
            {
                // Shard worker thread: convert here, so only runtime biotas leave this callback.
                List<ACE.Entity.Models.Biota> wielded = null, inventory = null;
                string conversionError = null;

                try
                {
                    wielded = possessed?.WieldedItems.Select(b => BiotaConverter.ConvertToEntityBiota(b)).ToList() ?? new List<ACE.Entity.Models.Biota>();
                    inventory = possessed?.Inventory.Select(b => BiotaConverter.ConvertToEntityBiota(b)).ToList() ?? new List<ACE.Entity.Models.Biota>();
                }
                catch (Exception ex)
                {
                    conversionError = ex.Message;
                    PvpTemplateSettings.Log.Error($"[PVPTEMPLATE] snapshot {key}: converting the possessions of 0x{characterId:X8} threw.", ex);
                }

                WorldManager.EnqueueAction(new ActionEventDelegate(() =>
                {
                    if (conversionError != null)
                    {
                        reply($"Snapshot of {key} failed reading {offline.Name}'s possessions: {conversionError}");
                        return;
                    }

                    // Re-check on the world thread: the character may have logged in while the read was queued.
                    if (PlayerManager.GetOnlinePlayer(characterId) != null)
                    {
                        reply($"{offline.Name} logged in during the snapshot. Log it out and try again.");
                        return;
                    }

                    PvpTemplateDefinition definition;
                    List<string> skipped;

                    offline.BiotaDatabaseLock.EnterReadLock();
                    try
                    {
                        definition = PvpTemplateKit.CaptureDefinition(key, displayName, offline.Biota, wielded, inventory, out skipped);
                    }
                    finally
                    {
                        offline.BiotaDatabaseLock.ExitReadLock();
                    }

                    var record = new PvpTemplateRecord
                    {
                        TemplateKey = key,
                        DisplayName = definition.DisplayName,
                        SourceCharacterId = characterId,
                        SourceCharacterName = offline.Name,
                        Enabled = false,
                        Modes = string.Empty,
                        DefinitionJson = PvpTemplateJson.SerializeDefinition(definition),
                        SnapshotAt = DateTime.UtcNow,
                        SnapshotBy = adminName ?? string.Empty,
                    };

                    DatabaseManager.Shard.SavePvpTemplateSnapshot(record, (status, version) =>
                    {
                        WorldManager.EnqueueAction(new ActionEventDelegate(() =>
                        {
                            switch (status)
                            {
                                case PvpTemplateStoreStatus.Ok:
                                    reply($"Snapshotted {offline.Name} as template {key} v{version}: {definition.Skills.Count} skills, {definition.PowerInts.Count} power properties, {definition.Buffs.Count} buffs, {definition.Spells.Count} spells, {definition.Kit.Count} kit items." +
                                        (skipped.Count > 0 ? $" Left out: {string.Join(", ", skipped)}." : "") +
                                        (version == 1 ? " New templates start disabled with no modes." : ""));
                                    PvpTemplateSettings.Log.Info($"[PVPTEMPLATE] {adminName} snapshotted {offline.Name} (0x{characterId:X8}) as {key} v{version}.");
                                    break;
                                case PvpTemplateStoreStatus.TablesMissing:
                                    reply("The template tables do not exist yet (Database/Updates/Shard/2026-10-03-00-Add-Pvp-Templates.sql has not been applied). Nothing was saved.");
                                    break;
                                case PvpTemplateStoreStatus.Ambiguous:
                                    reply($"The snapshot of {key} hit a duplicate key and may already be saved. Check /pvptemplate inspect {key} before retrying.");
                                    break;
                                default:
                                    reply($"The snapshot of {key} failed to save. See the server log.");
                                    break;
                            }
                        }));
                    });
                }));
            });

            return true;
        }

        /// <summary>
        /// A stored row as a definition. The row's version and display name are authoritative over whatever the
        /// JSON body carries (the store assigns the version after the body is written).
        /// </summary>
        public static bool TryParse(PvpTemplateRecord row, out PvpTemplateDefinition definition, out string error)
        {
            definition = null;

            if (row == null)
            {
                error = "no row";
                return false;
            }

            if (!PvpTemplateJson.TryDeserializeDefinition(row.DefinitionJson, out definition, out error))
                return false;

            definition.Key = row.TemplateKey;
            definition.Version = row.Version;

            if (!string.IsNullOrWhiteSpace(row.DisplayName))
                definition.DisplayName = row.DisplayName;

            // No kit warm here: TryParse also runs for /pvptemplate list and inspect and for every catalog load. The one
            // warm path is the coordinator's, when a match forms (PvpMatchCoordinator.WarmKit).
            return true;
        }

        /// <summary>
        /// Loads every kit wcid into the world weenie cache on a thread-pool thread, so the apply (which runs on the
        /// world thread) finds them cached instead of doing a synchronous world-database read in the tick.
        /// GetCachedWeenie is safe off the world thread (a ConcurrentDictionary cache, a fresh context per read).
        /// Called from ONE place: the coordinator, when a match forms (PvpMatchCoordinator.WarmKit), which is the
        /// accept window plus staging ahead of the apply. Fire and forget: a failure only means the apply reads the
        /// weenie itself, as it always could. Never throws.
        /// </summary>
        public static System.Threading.Tasks.Task WarmKitWeeniesAsync(PvpTemplateDefinition definition)
        {
            var wcids = definition?.Kit?.Select(k => k.WeenieClassId).Distinct().ToList();

            if (wcids == null || wcids.Count == 0)
                return System.Threading.Tasks.Task.CompletedTask;

            return System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var world = DatabaseManager.World;

                    if (world == null)
                        return;

                    foreach (var wcid in wcids)
                        world.GetCachedWeenie(wcid);
                }
                catch (Exception ex)
                {
                    PvpTemplateSettings.Log.Warn($"[PVPTEMPLATE] warming the kit weenies of {definition.Key} failed; the apply will read them itself: {ex.Message}");
                }
            });
        }
    }
}
