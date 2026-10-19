using System;
using System.Linq;

using ACE.Server.Entity.Actions;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldObjects;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// Hold, protect.defendPose: once a second, each standing target is handed the nearest of this run's living monsters
    /// within reach (edge to edge), and plays its cosmetic defense on its own landblock (Creature.TickWorldEventDefendPose).
    /// Only the run's wave monsters are offered, never a player or a pet.
    /// </summary>
    public sealed partial class WorldEvent
    {
        private readonly Random defendPoseRng = new Random();

        private void TickHoldDefendPose(double now)
        {
            try
            {
                var pose = Composition?.Goal?.TypeKind == GoalType.Hold ? Composition?.Goal?.Protect?.DefendPose : null;

                if (pose == null)
                    return;

                var monsters = Spawner.LiveWaveCreatures.Where(m => m != null && !m.IsDead && m.Location != null && !(m is Player)).ToList();

                foreach (var target in Spawner.LiveProtectedCreatures)
                {
                    if (target == null || target.IsDead || target.Location == null || target.CurrentLandblock == null)
                        continue;

                    var selfRadius = target.PhysicsObj?.GetRadius() ?? 0f;

                    var gaps = monsters.Select(m => (m.Guid.Full, (double)(target.Location.DistanceTo(m.Location) - selfRadius - (m.PhysicsObj?.GetRadius() ?? 0f))));
                    var foeGuid = WorldEventHoldDefense.PickFoe(gaps, pose.Reach, target.WorldEventDefendFoe);
                    var foe = foeGuid == 0 ? null : monsters.FirstOrDefault(m => m.Guid.Full == foeGuid);

                    var knight = target;
                    Random rng;

                    lock (defendPoseRng)
                        rng = new Random(defendPoseRng.Next());

                    target.CurrentLandblock.EnqueueAction(new ActionEventDelegate(() =>
                    {
                        try
                        {
                            var before = knight.WorldEventDefendSwings;
                            var action = knight.TickWorldEventDefendPose(foe, ACE.Common.Time.GetUnixTime(), pose, rng);

                            if (action == WorldEventHoldDefense.PoseAction.DrawWeapon)
                                log.Info($"[WORLDEVENT] run={RunId} defend pose: {knight.Name} draws its weapon ({knight.CurrentMotionState?.Stance})");
                            else if (action == WorldEventHoldDefense.PoseAction.Swing && before == 0)
                                log.Info($"[WORLDEVENT] run={RunId} defend pose: {knight.Name} swings at {foe?.Name} (cosmetic, no damage)");
                        }
                        catch (Exception ex)
                        {
                            log.Error($"[WORLDEVENT] run={RunId} defend pose threw", ex);
                        }
                    }));
                }
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={RunId} defend pose pass threw", ex);
            }
        }
    }
}
