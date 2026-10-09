using System;

namespace ACE.Common
{
    /// <summary>
    /// Resolves the external directory (outside the exe directory) that Config.js and
    /// log4net.config should be read from and scaffolded into, if any.
    ///
    /// This directory serves two purposes that used to be conflated into one container-only
    /// mechanism: a container mounts its config at a fixed path, but a bare-metal/worktree
    /// checkout has nowhere to keep a gitignored Config.js that survives across worktrees. The
    /// ACE_CONFIG_DIR environment variable generalizes the container mechanism so any checkout
    /// can point at a persistent, per-machine config directory.
    /// </summary>
    public static class ExternalConfigDirectory
    {
        /// <summary>
        /// The environment variable that, if set to a non-blank value, names the external config
        /// directory to use. Takes priority over the container default, even inside a container.
        /// </summary>
        public const string EnvironmentVariableName = "ACE_CONFIG_DIR";

        /// <summary>
        /// The fixed config directory used inside a container, when ACE_CONFIG_DIR is not set.
        /// </summary>
        public const string ContainerConfigDirectory = "/ace/Config";

        /// <summary>
        /// Resolves the external config directory to use, or null if none applies (the caller
        /// should fall back to the exe directory, as before this mechanism existed).
        ///
        /// Priority order:
        ///   1. ACE_CONFIG_DIR environment variable, if set to a non-blank value - wins even
        ///      inside a container.
        ///   2. The container default (/ace/Config), if isRunningInContainer is true.
        ///   3. null - no external directory.
        /// </summary>
        public static string Resolve(bool isRunningInContainer)
        {
            var envDir = Environment.GetEnvironmentVariable(EnvironmentVariableName);

            if (!string.IsNullOrWhiteSpace(envDir))
                return envDir;

            if (isRunningInContainer)
                return ContainerConfigDirectory;

            return null;
        }
    }
}
