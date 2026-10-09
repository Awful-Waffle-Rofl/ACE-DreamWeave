using System;

using log4net;

using ACE.Server.Managers;

namespace ACE.Server.Pvp.Templates
{
    /// <summary>
    /// The five pvp_template_* tunables (TEMPLATES.md "Settings"), each behind a static Func seam with a
    /// try/catch that falls back to the compiled default: PropertyManager reads throw in ACE.Server.Tests, which
    /// has no shard config. Tests swap a seam and put it back in their cleanup.
    ///
    /// Kept OFF the Player type, like PvpArenaHookSettings and for the same reason: touching a static field
    /// Player declares runs Player's World-database-reading type initializer, which poisons Player in tests.
    /// </summary>
    public static class PvpTemplateSettings
    {
        public static readonly ILog Log = LogManager.GetLogger(typeof(PvpTemplateSettings));

        public const bool DefaultEnabled = true;
        public const bool DefaultKeepOwnBuffs = true;
        public const bool DefaultSuppressHeritageBonus = true;
        public const long DefaultBackstopSeconds = 10;
        public const string DefaultAccount = "";

        public static Func<bool> EnabledSource = () => ReadBool("pvp_template_enabled", DefaultEnabled);

        public static Func<bool> KeepOwnBuffsSource = () => ReadBool("pvp_template_keep_own_buffs", DefaultKeepOwnBuffs);

        public static Func<bool> SuppressHeritageBonusSource = () => ReadBool("pvp_template_suppress_heritage_bonus", DefaultSuppressHeritageBonus);

        public static Func<long> BackstopSecondsSource = () => ReadLong("pvp_template_backstop_seconds", DefaultBackstopSeconds);

        public static Func<string> AccountSource = () => ReadString("pvp_template_account", DefaultAccount);

        /// <summary>runrate_add_hooks, the existing (non-template) tunable ApplyFacetAttributes also gates its run-rate push on.</summary>
        public static Func<bool> RunRateHooksSource = () => ReadBool("runrate_add_hooks", false);

        /// <summary>
        /// Test seams for the two side effects of a restore that need a live shard: saving the player's biota and
        /// destroying an issued item (WorldObject.Destroy removes its biota from the shard). Production never
        /// reassigns them; ACE.Server.Tests swaps them to record calls and puts them back in its cleanup.
        /// </summary>
        internal static Action<ACE.Server.WorldObjects.WorldObject> SaveBiota = wo => wo.SaveBiotaToDatabase();

        internal static Action<ACE.Server.WorldObjects.WorldObject> DestroyItem = wo => wo.Destroy();

        /// <summary>
        /// The one restore every exit hook calls (Phase C: ExitPvpMatchNow, the death arrival, and through
        /// ExitPvpMatch the logout path): Player.RestorePvpTemplate, idempotent, false when there is no record.
        /// A seam so ACE.Server.Tests can count which exit paths reach it on a seeded Player, where the real restore's
        /// client pushes and gear re-equip cannot run. Production never reassigns it.
        /// </summary>
        internal static Func<ACE.Server.WorldObjects.Player, string, bool> Restore = (player, reason) => player.RestorePvpTemplate(reason);

        /// <summary>
        /// Test seam: the silent (pre-world) dequip of a worn issued item. Creature.TryDequipObject needs the live
        /// rating cache, children list and spell hooks a seeded Player does not have. Returns the dequipped item, or
        /// null when it was not worn.
        /// </summary>
        internal static Func<ACE.Server.WorldObjects.Player, ACE.Entity.ObjectGuid, ACE.Server.WorldObjects.WorldObject> SilentDequip =
            (player, guid) => player.TryDequipObject(guid, out var item, out _) ? item : null;

        /// <summary>
        /// Test seam: the equipped-item backstop's move of one worn personal item to the pack. The backstop never
        /// trusts it - it recounts what is still worn afterwards - so a seam that does nothing is the full-pack case.
        /// </summary>
        internal static Action<ACE.Server.WorldObjects.Player, ACE.Server.WorldObjects.WorldObject> MoveWornItemToPack =
            (player, item) => player.HandleActionPutItemInContainer(item.Guid.Full, player.Guid.Full);

        /// <summary>How long the heartbeat backstop waits before retrying a restore that failed (the record was kept).</summary>
        public static readonly TimeSpan HeartbeatRetryBackoff = TimeSpan.FromSeconds(60);

        /// <summary>The backstop delay actually used: a zero or negative setting falls back to the default.</summary>
        public static TimeSpan BackstopDelay()
        {
            var seconds = BackstopSecondsSource();

            return TimeSpan.FromSeconds(seconds > 0 ? seconds : DefaultBackstopSeconds);
        }

        private static bool ReadBool(string key, bool fallback)
        {
            try { return PropertyManager.GetBool(key, fallback).Item; }
            catch (Exception) { return fallback; }
        }

        private static long ReadLong(string key, long fallback)
        {
            try { return PropertyManager.GetLong(key, fallback).Item; }
            catch (Exception) { return fallback; }
        }

        private static string ReadString(string key, string fallback)
        {
            try { return PropertyManager.GetString(key, fallback).Item ?? fallback; }
            catch (Exception) { return fallback; }
        }
    }
}
