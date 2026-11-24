namespace ACE.Server.ThreadDungeons
{
    /// <summary>The Jump encounter's one run setting (Docs/Threads/ENCOUNTERS.md "Jump"), stamped by TryStart.</summary>
    public sealed partial class ThreadDungeonRun
    {
        public const long DefaultJumpTimeSeconds = 60;
        public const long JumpTimeSecondsMin = 15;
        public const long JumpTimeSecondsMax = 300;

        /// <summary>dynamic_dungeons_jump_time_seconds as TryStart stamps it: clamped to [15, 300]. Pure.</summary>
        public static long ClampJumpTimeSeconds(long configured) => System.Math.Clamp(configured, JumpTimeSecondsMin, JumpTimeSecondsMax);

        /// <summary>dynamic_dungeons_jump_time_seconds, read once by TryStart and stamped before publication. A run nobody stamped (tests) uses the shipped default.</summary>
        public long JumpTimeSeconds { get; set; } = DefaultJumpTimeSeconds;
    }
}
