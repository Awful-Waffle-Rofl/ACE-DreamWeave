using System;

using log4net;

using ACE.Server.Managers;

namespace ACE.Server.Pvp
{
    /// <summary>
    /// Static state the player-side hooks (Player_PvpArena.cs) need, kept OFF the Player type on purpose.
    ///
    /// Player's own static field initializers (Player_Location.cs) read the World database, so the first touch
    /// of any static field Player declares runs that type initializer - and in ACE.Server.Tests, with no World
    /// database, it throws once and poisons Player for the rest of the run (see MuleSummonTests'
    /// EnsureVendorConstructible). Keeping the hook's logger and seams here lets the gate, the predicates and
    /// ExitPvpMatchNow be driven on a seeded Player without ever touching a Player static.
    /// </summary>
    public static class PvpArenaHookSettings
    {
        /// <summary>The [PVP] logger for every player-side hook.</summary>
        public static readonly ILog Log = LogManager.GetLogger(typeof(PvpArenaHookSettings));

        /// <summary>pvp_arena_friendly_fire, read per gated check. A seam so tests can set it without PropertyManager.</summary>
        internal static Func<bool> FriendlyFireSource = ReadFriendlyFire;

        /// <summary>
        /// The clock the player-side hooks read a battleground spawn-protection window against (the coordinator publishes the
        /// window's end from its own injected clock, and production wires both to UtcNow). A seam so a test can move time.
        /// </summary>
        internal static Func<DateTime> UtcNow = () => DateTime.UtcNow;

        /// <summary>How often the friendly-fire line may repeat: an AoE or cleave checks every target in range at once.</summary>
        public const double FriendlyFireNoticeIntervalSeconds = 3.0;

        /// <summary>How often the "is protected" line may repeat for one target: an AoE or cleave checks every target in range at once.</summary>
        public const double SpawnProtectionNoticeIntervalSeconds = 3.0;

        /// <summary>The most targets one attacker's "is protected" throttle remembers; expired entries are dropped first.</summary>
        public const int SpawnProtectionNoticeMaxTracked = 8;

        /// <summary>
        /// The refusal a spawn-protected target returns from Player.CheckPKStatusVsTarget. A distinct instance (its two entries are
        /// placeholders no caller may send) so the three call sites that report a PK error to the attacker and the target
        /// (Player_Combat DamageTarget, Player_Magic cast, SpellProjectile impact) recognise it by reference and send NOTHING: the
        /// attacker already got the throttled "is protected" line, and the protected player is told nothing.
        /// </summary>
        internal static readonly System.Collections.Generic.List<ACE.Entity.Enum.WeenieErrorWithString> SpawnProtectedRefusal =
            new System.Collections.Generic.List<ACE.Entity.Enum.WeenieErrorWithString>
            {
                ACE.Entity.Enum.WeenieErrorWithString.YouFailToAffect_YouCannotAffectAnyone,
                ACE.Entity.Enum.WeenieErrorWithString._FailsToAffectYou_TheyCannotAffectAnyone
            };

        /// <summary>True when <paramref name="refusal"/> is <see cref="SpawnProtectedRefusal"/>: report nothing.</summary>
        internal static bool IsSpawnProtectedRefusal(System.Collections.Generic.List<ACE.Entity.Enum.WeenieErrorWithString> refusal)
            => ReferenceEquals(refusal, SpawnProtectedRefusal);

        private static bool ReadFriendlyFire()
        {
            try
            {
                return PropertyManager.GetBool("pvp_arena_friendly_fire", PvpTunables.Defaults.FriendlyFire).Item;
            }
            catch (Exception)
            {
                return PvpTunables.Defaults.FriendlyFire;
            }
        }
    }
}
