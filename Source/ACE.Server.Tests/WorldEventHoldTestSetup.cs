using System.Runtime.CompilerServices;

using ACE.Server.WorldEvents;

namespace ACE.Server.Tests
{
    /// <summary>
    /// world_events_hold_enabled ships off, and a unit test has no property cache, so every test would see Hold goals
    /// refused. The Hold tests are about the feature itself, so the whole test assembly runs with the master toggle on;
    /// the tests of the toggle (WorldEventHoldSettingsTests) swap the seam themselves and put it back.
    /// </summary>
    internal static class WorldEventHoldTestSetup
    {
        internal static readonly System.Func<bool> HoldOn = () => true;

        [ModuleInitializer]
        internal static void EnableHoldForTests() => HoldSettings.EnabledSource = HoldOn;
    }
}
