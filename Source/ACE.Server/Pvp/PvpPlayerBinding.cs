using System;
using System.Collections.Generic;

using ACE.Server.Pvp.Templates;

namespace ACE.Server.Pvp
{
    /// <summary>
    /// The immutable, volatile-field snapshot a landblock thread reads to decide PvP rules for a player,
    /// with no locking on the hot path (Docs/Pvp/DESIGN.md "Reads from landblock threads"). A player who is
    /// not in a match has a null binding; every mask flag on it is only ever true while EnterPvpMatch has
    /// run and ExitPvpMatch has not (PR C wiring).
    /// </summary>
    public sealed class PvpPlayerBinding
    {
        public PvpMatch Match { get; }

        public int TeamIndex { get; }

        public PvpMatchState State { get; }

        public bool ClassAbilitiesSuppressed { get; }

        public bool EquipmentModsSuppressed { get; }

        public bool WeaponModsSuppressed { get; }

        public bool PickupBoonsSuppressed { get; }

        public bool TurnSpeedSuppressed { get; }

        /// <summary>
        /// Snapshot of pvp_arena_restrict_spells at match creation (PvpMatchCoordinator.Binding), the same shape
        /// as the five suppression flags above. Defaults true so every existing binding constructor call - test
        /// fixtures included - keeps the Doctide-ported arena spell rules ON without being touched.
        /// </summary>
        public bool RestrictSpells { get; }

        /// <summary>
        /// When the match went to overtime (Docs/Pvp/DESIGN.md "Overtime"), or null while it is still in regulation
        /// (and always, with pvp_arena_overtime_enabled off). Set by the coordinator's Live-to-Live republish at the
        /// regulation limit. The two overtime values below are snapshotted with it, so a setting edited mid-round
        /// never changes a round already in overtime. Defaults null so every existing constructor call is regulation.
        /// </summary>
        public DateTime? OvertimeSinceUtc { get; }

        /// <summary>pvp_arena_overtime_healing_mod as it stood when overtime started. Meaningful only with <see cref="OvertimeSinceUtc"/> set.</summary>
        public double OvertimeHealingMod { get; }

        /// <summary>pvp_arena_overtime_damage_ramp_per_minute as it stood when overtime started. Meaningful only with <see cref="OvertimeSinceUtc"/> set.</summary>
        public double OvertimeRampPerMinute { get; }

        /// <summary>
        /// PvP Template Facets (Docs/Pvp/TEMPLATES.md "Freezing"): the parsed template definition this participant
        /// fights on, copied from the coordinator's catalog at dispatch (where the masks are snapshotted) and carried
        /// unchanged by every later binding of the match, so a re-snapshot of the template never changes a match in
        /// progress. Null only for a mode that opted out (<see cref="Untemplated"/>) and on bindings built outside
        /// the coordinator (test fixtures); the live gateway refuses to place a participant whose binding carries none
        /// unless the binding is Untemplated.
        /// </summary>
        public PvpTemplateDefinition Template { get; }

        /// <summary>
        /// The position of this player's team pen, latched for the death teleport (Docs/Pvp/BATTLEGROUNDS.md "Respawn"),
        /// or null outside a battleground. Defaults null so every existing constructor call is unchanged.
        /// </summary>
        public ACE.Entity.Position RespawnPen { get; }

        /// <summary>True while the player is dead or waiting in the pen: unharmable, unable to cast, not counted on the zone. Defaults false.</summary>
        public bool Respawning { get; }

        /// <summary>
        /// PvP Template Facets: true only for a match whose mode explicitly opted out of templates
        /// (PvpModeDefinition.Templated false). Defaults false, so a binding that does not say otherwise requires a
        /// template and the live gateway refuses to place it without one (fail closed).
        /// </summary>
        public bool Untemplated { get; }

        /// <summary>
        /// Battlegrounds (Docs/Pvp/BATTLEGROUNDS.md "Team fellowships"): the match puts each team in a server-built
        /// fellowship, so the player-side fellowship gates refuse every player-driven change while this binding is held
        /// (PvpPlayerRules.RefusesFellowshipChange). DERIVED by the coordinator on every build from the match's own
        /// snapshot, never carried over from a previous binding. Defaults false, so every existing constructor call
        /// (arena modes, test fixtures) leaves fellowships alone.
        /// </summary>
        public bool TeamFellowship { get; }

        /// <summary>
        /// Battleground spawn protection (Docs/Pvp/BATTLEGROUNDS.md "Spawn protection"): until this instant no other player can
        /// hurt the player. Set by the coordinator when a respawn is confirmed at the spawn point (confirm time plus the
        /// snapshotted pvp_bg_spawn_protection_seconds), null otherwise, always null for an arena seat. It lapses by the clock
        /// alone (the gate compares it with now), so expiry needs no republish. Defaults null.
        /// </summary>
        public DateTime? ProtectedUntilUtc { get; }

        /// <summary>
        /// Room-based spawn protection (Docs/Pvp/BATTLEGROUNDS.md "Spawn protection"): the protection has no end time yet. The player is
        /// protected for as long as they stand in <see cref="SpawnRoom"/>; the first time they are seen outside, the player
        /// side swaps this binding for one with <see cref="ProtectedUntilUtc"/> set to that moment plus <see cref="ProtectionSeconds"/>
        /// (<see cref="WithRoomExit"/>), and re-entering the room does not bring it back. False for a plain window.
        /// </summary>
        public bool ProtectedInRoom { get; }

        /// <summary>The player's own team's spawn room (cells plus footprint); null when the layout has none.</summary>
        public SpawnRoomArea SpawnRoom { get; }

        /// <summary>The full cell ids (landblock in the high 16 bits) of <see cref="SpawnRoom"/>; null when there is no room.</summary>
        public IReadOnlySet<uint> SpawnRoomCells => SpawnRoom?.Cells;

        /// <summary>The snapshotted pvp_bg_spawn_protection_seconds: how long protection lasts after the player leaves the room. 0 when unset.</summary>
        public int ProtectionSeconds { get; }

        /// <summary>
        /// Identifies one grant of protection (the coordinator's clock ticks at the grant), carried unchanged through every state of it, so
        /// the player side can tell "the same protection" on a republish rebuilt from the seat. 0 means unstamped.
        /// </summary>
        public long ProtectionStamp { get; }

        /// <summary>The identity of this grant: the stamp, or for an unstamped plain window the window's end ticks; 0 when there is none.</summary>
        public long ProtectionId => ProtectionStamp != 0 ? ProtectionStamp : (ProtectedUntilUtc?.Ticks ?? 0);

        /// <summary>True while any protection state is held (a running or lapsed window, or room mode).</summary>
        public bool HasSpawnProtection => ProtectedInRoom || ProtectedUntilUtc.HasValue;

        /// <summary>True when <paramref name="at"/> is inside this player's own spawn room (<see cref="SpawnRoomArea.Contains"/>: reported cell AND footprint).</summary>
        public bool IsInSpawnRoom(ACE.Entity.Position at) => SpawnRoom != null && SpawnRoom.Contains(at);

        /// <summary>
        /// Protected right now. In room mode: while <paramref name="at"/> (the player's current position) is in the spawn room (a binding in room mode that is read
        /// from outside the room before the player side has swapped it is NOT protected: the player is already out). In window mode: until
        /// <see cref="ProtectedUntilUtc"/>. Pure; <paramref name="at"/> is only read in room mode.
        /// </summary>
        public bool IsSpawnProtected(DateTime nowUtc, ACE.Entity.Position at = null)
            => ProtectedInRoom ? IsInSpawnRoom(at) : ProtectedUntilUtc.HasValue && ProtectedUntilUtc.Value > nowUtc;

        /// <summary>This binding with the spawn protection cleared (identity kept), every other field carried over unchanged.</summary>
        public PvpPlayerBinding WithoutSpawnProtection() => Copy(false, null);

        /// <summary>This binding after the player left the spawn room: room mode off, the window running until <paramref name="untilUtc"/>.</summary>
        public PvpPlayerBinding WithRoomExit(DateTime untilUtc) => Copy(false, untilUtc);

        private PvpPlayerBinding Copy(bool inRoom, DateTime? until) => new PvpPlayerBinding(
            Match, TeamIndex, State, ClassAbilitiesSuppressed, EquipmentModsSuppressed, WeaponModsSuppressed, PickupBoonsSuppressed, TurnSpeedSuppressed,
            RestrictSpells, OvertimeSinceUtc, OvertimeHealingMod, OvertimeRampPerMinute, Template, RespawnPen, Respawning, Untemplated, TeamFellowship,
            protectedUntilUtc: until, spawnRoom: SpawnRoom, protectedInRoom: inRoom, protectionSeconds: ProtectionSeconds, protectionStamp: ProtectionStamp);

        public PvpPlayerBinding(
            PvpMatch match,
            int teamIndex,
            PvpMatchState state,
            bool classAbilitiesSuppressed,
            bool equipmentModsSuppressed,
            bool weaponModsSuppressed,
            bool pickupBoonsSuppressed,
            bool turnSpeedSuppressed,
            bool restrictSpells = true,
            DateTime? overtimeSinceUtc = null,
            double overtimeHealingMod = 1.0,
            double overtimeRampPerMinute = 0.0,
            PvpTemplateDefinition template = null,
            ACE.Entity.Position respawnPen = null,
            bool respawning = false,
            bool untemplated = false,
            bool teamFellowship = false,
            DateTime? protectedUntilUtc = null,
            SpawnRoomArea spawnRoom = null,
            bool protectedInRoom = false,
            int protectionSeconds = 0,
            long protectionStamp = 0)
        {
            Match = match;
            TeamIndex = teamIndex;
            State = state;
            ClassAbilitiesSuppressed = classAbilitiesSuppressed;
            EquipmentModsSuppressed = equipmentModsSuppressed;
            WeaponModsSuppressed = weaponModsSuppressed;
            PickupBoonsSuppressed = pickupBoonsSuppressed;
            TurnSpeedSuppressed = turnSpeedSuppressed;
            RestrictSpells = restrictSpells;
            OvertimeSinceUtc = overtimeSinceUtc;
            OvertimeHealingMod = overtimeHealingMod;
            OvertimeRampPerMinute = overtimeRampPerMinute;
            Template = template;
            RespawnPen = respawnPen;
            Respawning = respawning;
            Untemplated = untemplated;
            TeamFellowship = teamFellowship;
            ProtectedUntilUtc = protectedUntilUtc;
            SpawnRoom = spawnRoom;
            ProtectedInRoom = protectedInRoom;
            ProtectionSeconds = protectionSeconds;
            ProtectionStamp = protectionStamp;
        }
    }

    /// <summary>
    /// What a player has already done with one grant of spawn protection: ended it early, or left the room (the window then runs to
    /// <see cref="UntilUtc"/>). Kept by the Player so a coordinator republish rebuilt from the seat, which only learns of either later,
    /// cannot undo it. Immutable; the Player swaps the whole reference.
    /// </summary>
    internal sealed class PvpProtectionProgress
    {
        public long Id { get; }

        public bool Ended { get; }

        public DateTime UntilUtc { get; }

        public PvpProtectionProgress(long id, bool ended, DateTime untilUtc)
        {
            Id = id;
            Ended = ended;
            UntilUtc = untilUtc;
        }
    }
}
