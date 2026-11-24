using System;
using System.IO;

namespace ACE.Server.BalanceTrials
{
    /// <summary>
    /// Resolves the directory that holds the trial scenario, room and archetype files. Stage only: it never walks up
    /// to the repo's tools/ folder, so a trial cannot start from a developer checkout by accident.
    /// </summary>
    public static class TrialDataDir
    {
        public const string SettingKey = "balance_trials_data_dir";

        /// <summary>The folder name under the external config directory (/ace/Config on stage).</summary>
        public const string FolderName = "BalanceTrials";

        /// <summary>
        /// A non-blank <paramref name="configured"/> value wins as written. Otherwise <c>&lt;externalConfigDir&gt;/BalanceTrials</c>
        /// when <paramref name="dirExists"/> says it exists. Otherwise null, with <paramref name="reason"/> naming the setting.
        /// The live call passes <c>ExternalConfigDirectory.Resolve(Program.IsRunningInContainer)</c>.
        /// </summary>
        public static string Resolve(string configured, string externalConfigDir, Func<string, bool> dirExists, out string reason)
        {
            ArgumentNullException.ThrowIfNull(dirExists);

            if (!string.IsNullOrWhiteSpace(configured))
            {
                reason = null;
                return configured.Trim();
            }

            if (string.IsNullOrWhiteSpace(externalConfigDir))
            {
                reason = $"no trial data directory: {SettingKey} is empty and there is no external config directory; set {SettingKey} to the directory holding the scenario, room and archetype files";
                return null;
            }

            var candidate = Path.Combine(externalConfigDir, FolderName);
            if (!dirExists(candidate))
            {
                reason = $"no trial data directory: {SettingKey} is empty and '{candidate}' does not exist; create it or set {SettingKey}";
                return null;
            }

            reason = null;
            return candidate;
        }
    }
}
