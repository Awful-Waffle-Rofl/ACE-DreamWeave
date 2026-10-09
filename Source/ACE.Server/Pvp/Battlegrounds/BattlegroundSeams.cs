using System;
using System.Collections.Generic;
using System.Threading;

using ACE.Entity;

namespace ACE.Server.Pvp.Battlegrounds
{
    /// <summary>
    /// The battleground half of the coordinator's player seam (Docs/Pvp/BATTLEGROUNDS.md "Respawn", "King of the Hill").
    /// A separate interface from <see cref="IPvpPlayerGateway"/> so every existing gateway (and test fake) is untouched;
    /// the coordinator enables the battleground room only when its gateway also implements this one. Every method is
    /// called on the WORLD thread; the live implementation queues each change on the player's own action queue, and an
    /// offline player is a logged no-op, never a throw.
    /// </summary>
    public interface IBattlegroundPlayerGateway
    {
        /// <summary>
        /// The control-zone sample for one player (instance, landblock-local position, dead, teleporting), read the way
        /// <see cref="IPvpPlayerGateway.GetPresence"/> reads. Null when offline. TeamIndex and IpKey are the caller's.
        /// </summary>
        BattlegroundZoneSample SampleZone(uint characterId, uint matchInstance);

        /// <summary>Queues the King of the Hill drain on the player (Player.DrainForBattlegroundZone).</summary>
        void DrainVitals(uint characterId, Guid matchId, uint matchInstance, int health, int stamina, int mana, bool lethal);

        /// <summary>Queues the respawn on the player (Player.RespawnInBattleground): shields off, full vitals, teleport to <paramref name="spawn"/>.</summary>
        void Respawn(uint characterId, Guid matchId, Position spawn);

        /// <summary>The death probe: true while the player's death sequence is still running (Player.IsInDeathProcess). False when offline.</summary>
        bool IsInDeathProcess(uint characterId);

        /// <summary>
        /// ExitPvpMatch for a seat the coordinator believes is still dying: never a coordinator teleport. The player side
        /// sends them home only if their death sequence already finished in the pen (Player.ExitPvpMatch(context, true)).
        /// </summary>
        void ExitMatchFromPen(uint characterId, string context);
    }

    /// <summary>The state of one match's pen-seal placement.</summary>
    public enum BattlegroundFixtureStatus
    {
        Pending,
        Placed,
        Failed
    }

    /// <summary>
    /// One queued fixture placement. Written by the instance landblock's thread, read by the world-thread coordinator,
    /// so the status is published with a volatile write AFTER the detail.
    /// </summary>
    public sealed class BattlegroundFixtureJob
    {
        private int status = (int)BattlegroundFixtureStatus.Pending;

        public BattlegroundFixtureStatus Status => (BattlegroundFixtureStatus)Volatile.Read(ref status);

        /// <summary>
        /// How many pieces entered the world, and the failure reason when Failed. A best-effort job (the zone markers)
        /// can be Placed with a Detail: the first refusal among the pieces it skipped.
        /// </summary>
        public int Placed { get; private set; }

        public string Detail { get; private set; }

        /// <summary>
        /// The live objects the placement created, so a later removal can destroy exactly them. Written only by the
        /// landblock thread while it runs the placement and read only by a removal queued behind it on the same queue.
        /// </summary>
        public System.Collections.Generic.List<object> PlacedObjects { get; } = new System.Collections.Generic.List<object>();

        public void Complete(int placed, string detail = null)
        {
            Detail = detail;
            Placed = placed;
            Volatile.Write(ref status, (int)BattlegroundFixtureStatus.Placed);
        }

        public void Fail(string detail, int placed = 0)
        {
            Detail = detail;
            Placed = placed;
            Volatile.Write(ref status, (int)BattlegroundFixtureStatus.Failed);
        }
    }

    /// <summary>
    /// The battleground half of the space seam: places the pen seals into the match's own instance (Docs/Pvp/BATTLEGROUNDS.md
    /// "Pen seals"). Separate from <see cref="IPvpMatchSpaces"/> for the same reason as <see cref="IBattlegroundPlayerGateway"/>.
    /// </summary>
    public interface IBattlegroundMatchSpaces
    {
        /// <summary>
        /// Queues creation of every piece on the instance landblock's OWN action queue and returns the job the
        /// coordinator polls. Never throws for a refusal: a space that no longer resolves comes back as a Failed job.
        /// </summary>
        BattlegroundFixtureJob SpawnFixtures(MatchSpace space, IReadOnlyList<BattlegroundSealPiece> pieces);

        /// <summary>
        /// The King of the Hill zone markers (Docs/Pvp/BATTLEGROUNDS.md "Zone markers"): queued on the same instance
        /// landblock queue with the same live-instance check as <see cref="SpawnFixtures"/>, but placed BEST EFFORT, so a
        /// marker that cannot be placed is skipped and the job still completes with the count placed. Kept apart from
        /// <see cref="SpawnFixtures"/> because the coordinator never waits on this job and never cancels for it. Never
        /// throws for a refusal.
        /// </summary>
        BattlegroundFixtureJob SpawnZoneMarkers(MatchSpace space, IReadOnlyList<BattlegroundSealPiece> pieces);

        /// <summary>
        /// Removes the markers a <see cref="SpawnZoneMarkers"/> job placed (the moving hill replaces its ring). Queued on the
        /// instance landblock's own action queue AFTER the placement it undoes, so it removes exactly what that job placed
        /// whether or not the job had finished when this was called. Best effort: never throws, and an instance that is no
        /// longer live is a no-op (the markers went with it).
        /// </summary>
        void RemoveZoneMarkers(MatchSpace space, BattlegroundFixtureJob placed);

        /// <summary>
        /// The start gates (<see cref="BattlegroundLayout.StartGates"/>): queued exactly like <see cref="SpawnFixtures"/> (same landblock
        /// queue, same live-instance check, STRICT), but every placed object is recorded in the job's PlacedObjects so
        /// <see cref="RemoveStartGates"/> can destroy exactly them. A separate job from the pen seals, which are never removed.
        /// </summary>
        BattlegroundFixtureJob SpawnStartGates(MatchSpace space, IReadOnlyList<BattlegroundSealPiece> pieces);

        /// <summary>
        /// Destroys the gates a <see cref="SpawnStartGates"/> job placed, on the instance landblock's own queue behind that placement.
        /// Best effort: never throws, and an instance that is no longer live is a no-op (the gates went with it).
        /// </summary>
        void RemoveStartGates(MatchSpace space, BattlegroundFixtureJob placed);
    }

    /// <summary>
    /// The Attack/Defend half of the space seam (Docs/Pvp/ATTACK-DEFEND.md "Placement"): the crystals, their cosmetic health sample
    /// and their rescale when an attacker leaves. A separate interface from <see cref="IBattlegroundMatchSpaces"/> so every existing
    /// battleground fake is untouched; the coordinator offers Attack/Defend only when its spaces also implement this one. Every
    /// method is called on the WORLD thread and never throws for a refusal.
    /// </summary>
    public interface IObjectiveMatchSpaces
    {
        /// <summary>
        /// Queues one <paramref name="wcid"/> crystal per planned site on the instance landblock's OWN action queue, exactly like the
        /// pen seals: instance-stamped position, TimeToRot -1, transient, and the tag from <paramref name="tagFactory"/> and the
        /// plan's health set before EnterWorld. STRICT: any crystal that fails to place fails the job. A space that no longer
        /// resolves comes back as a Failed job.
        /// </summary>
        BattlegroundFixtureJob SpawnObjectives(MatchSpace space, uint wcid, AttackDefendPlan plan, Func<PlannedCrystal, BattlegroundObjectiveTag> tagFactory);

        /// <summary>
        /// Every standing crystal's current and maximum health, read from the objects <paramref name="job"/> placed. Cosmetic and
        /// torn-read tolerant (the landblock thread writes health); destruction is never read from here. Empty until the job is Placed.
        /// </summary>
        IReadOnlyList<CrystalHealth> SampleObjectives(MatchSpace space, BattlegroundFixtureJob job);

        /// <summary>
        /// Queues, on the instance landblock's own queue, the rescale of every standing crystal's current and maximum health by
        /// <see cref="CrystalHealthScaler.Scale"/> from <paramref name="sizedForAttackers"/> to <paramref name="currentAttackers"/>
        /// (Docs/Pvp/ATTACK-DEFEND.md ruling 12). Best effort: an instance that is no longer live is a no-op.
        /// </summary>
        void ScaleObjectives(MatchSpace space, BattlegroundFixtureJob job, int sizedForAttackers, int currentAttackers);

        /// <summary>
        /// Queues, on the instance landblock's own queue, one kill's chip or heal on the crystal at <see cref="CrystalKillEffect.CrystalIndex"/>
        /// (Docs/Pvp/ATTACK-DEFEND.md "Kill chip and heal"): <see cref="CrystalKillRules.Amount"/> of the crystal's CURRENT maximum health.
        /// A chip goes through the crystal's normal damage path (Creature.TakeDamage, credited to <paramref name="killerId"/> when that
        /// player is online), so a chip to 0 destroys it exactly as a hit would, report and all. A heal never exceeds the maximum. A crystal
        /// that has already fallen is left alone. Best effort: an instance that is no longer live is a no-op.
        /// </summary>
        void AdjustObjective(MatchSpace space, BattlegroundFixtureJob job, CrystalKillEffect effect, uint killerId);
    }
}
