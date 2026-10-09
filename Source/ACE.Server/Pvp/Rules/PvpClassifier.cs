using System;

using ACE.Server.Pvp.Battlegrounds;
using ACE.Server.WorldObjects;

namespace ACE.Server.Pvp.Rules
{
    /// <summary>
    /// Where a player-vs-player interaction happens. None means the interaction is NOT PvP, and every PvP
    /// lever must hand its input back unchanged.
    /// </summary>
    public enum PvpScope
    {
        None,
        OpenWorld,
        Arena,
        /// <summary>
        /// Both players bound to the same Live BATTLEGROUND match (mode key starts with bg_). Before this value existed
        /// such a pair classified as <see cref="Arena"/>; every site that compared against Arena keeps matching it
        /// (see <see cref="PvpInteraction.IsMatchScope"/>), and only the per-context tuning tells the two apart.
        /// </summary>
        Battleground
    }

    /// <summary>
    /// The result of <see cref="PvpClassifier.Classify"/>. Attacker and Defender are set only when the
    /// interaction is PvP; for <see cref="PvpScope.None"/> both are null.
    /// </summary>
    public readonly struct PvpInteraction
    {
        public static readonly PvpInteraction None = default;

        public Player Attacker { get; }

        public Player Defender { get; }

        public PvpScope Scope { get; }

        public bool IsPvp => Scope != PvpScope.None;

        /// <summary>TRUE for a pair in a Live match of either kind (Arena or Battleground): what a bare Arena test meant before Battleground existed.</summary>
        public bool IsMatchScope => Scope == PvpScope.Arena || Scope == PvpScope.Battleground;

        public PvpInteraction(Player attacker, Player defender, PvpScope scope)
        {
            Attacker = attacker;
            Defender = defender;
            Scope = scope;
        }
    }

    /// <summary>
    /// THE player-vs-player classifier. Every PvP decision in combat, magic and enchantment code goes through
    /// here instead of an inline `x is Player && y is Player` test, so PvP levers and PvE code can be proven
    /// apart in one place.
    ///
    /// An interaction is PvP when the source and the target are BOTH players and are NOT the same object:
    ///  - a null source or target is not PvP;
    ///  - a player acting on themself is not PvP;
    ///  - a pet or combat pet is not a Player, so a pet's damage is not PvP (the same answer the old inline
    ///    `attacker is Player` tests gave it);
    ///  - a cleave or riposte hit is classified per target, by whoever calls this for that hit.
    ///
    /// SELF IS NOT PvP HERE, BUT IT WAS IN SOME OLD INLINE TESTS. `sourcePlayer != null && targetPlayer != null`
    /// is true for a player acting on themself. Sites where a self interaction can reach the test use
    /// <see cref="IsPlayerPairIncludingSelf"/>, which keeps that old answer, so the L0 migration changed no
    /// behavior.
    ///
    /// Arena scope comes from <see cref="ArenaScopeSource"/>: both players bound to the same Live match.
    /// Until the arena coordinator binds a player no binding exists, so every PvP interaction is OpenWorld.
    /// </summary>
    public static class PvpClassifier
    {
        /// <summary>
        /// Decides whether a PvP pair is an arena interaction. A seam: tests and the arena coordinator can swap
        /// it. The default reads the merged arena bindings (<see cref="PvpPlayerRules.InSameLiveMatch"/>).
        /// Only consulted for a pair that is already PvP, so it can never turn a PvE interaction into PvP.
        /// </summary>
        internal static Func<Player, Player, bool> ArenaScopeSource = DefaultArenaScope;

        /// <summary>The shipped <see cref="ArenaScopeSource"/>: both players bound to the same match, and it is Live for both.</summary>
        internal static bool DefaultArenaScope(Player attacker, Player defender)
            => PvpPlayerRules.InSameLiveMatch(attacker.PvpBinding, defender.PvpBinding);

        /// <summary>
        /// TRUE when source and target are both players and not the same object. Never reads a setting and never
        /// consults the arena seam, so it is safe on every hot path.
        /// </summary>
        public static bool IsPvp(WorldObject source, WorldObject target)
            => source is Player && target is Player && !ReferenceEquals(source, target);

        /// <summary>
        /// TRUE when source and target are both players, INCLUDING a player acting on themself. This is the
        /// semantics of the pre-classifier inline tests (`sourcePlayer != null && targetPlayer != null`), kept
        /// for the retail PvP-branch sites a self interaction can reach (self-cast DoT, self-Harm), so moving
        /// them onto the classifier changed no behavior. New PvP levers use <see cref="Classify"/> or
        /// <see cref="IsPvp"/>, where self is NOT PvP.
        /// </summary>
        public static bool IsPlayerPairIncludingSelf(WorldObject source, WorldObject target)
            => source is Player && target is Player;

        /// <summary>
        /// Classifies one interaction. Returns <see cref="PvpInteraction.None"/> for anything that is not PvP,
        /// before any setting or arena state is read.
        /// </summary>
        public static PvpInteraction Classify(WorldObject source, Creature target)
        {
            if (!IsPvp(source, target))
                return PvpInteraction.None;

            var attacker = (Player)source;
            var defender = (Player)target;

            return new PvpInteraction(attacker, defender, ResolveScope(attacker, defender));
        }

        private static PvpScope ResolveScope(Player attacker, Player defender)
        {
            var source = ArenaScopeSource;

            if (source == null)
                return PvpScope.OpenWorld;

            try
            {
                if (!source(attacker, defender))
                    return PvpScope.OpenWorld;

                // a battleground match uses the same binding as an arena match; its mode key tells them apart. A
                // pair with no readable binding (a swapped seam in a test) stays Arena, what it was before.
                return BattlegroundModes.IsBattlegroundModeKey(defender.PvpBinding?.Match?.ModeKey) ? PvpScope.Battleground : PvpScope.Arena;
            }
            catch (Exception ex)
            {
                PvpArenaHookSettings.Log.Error($"[PVP] PvpClassifier arena scope threw for 0x{attacker.Guid.Full:X8} vs 0x{defender.Guid.Full:X8}; treating as OpenWorld.", ex);
                return PvpScope.OpenWorld;
            }
        }
    }
}
