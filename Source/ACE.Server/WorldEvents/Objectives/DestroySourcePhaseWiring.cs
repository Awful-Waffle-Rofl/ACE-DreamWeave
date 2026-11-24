using System;
using System.Reflection;

using ACE.Entity.Enum;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.WorldEvents.Objectives
{
    /// <summary>
    /// The engine side of the phased Destroy the Source: the live port (a source creature's guard, health and the local announcer) and the
    /// spawn guard. Nothing here decides anything; the rules are DestroySourcePhases.
    /// </summary>
    public static class DestroySourcePhaseWiring
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>The monster-effect record that makes ImmuneEffect read Creature.P_DigsiteImmune (the speed-course boss guard's record).</summary>
        public const string ImmuneRecord = "immune";

        /// <summary>
        /// The phase machine for a run being staged, or null when phases are switched off (the objective then behaves exactly as before).
        /// Settings are read once, here.
        /// </summary>
        public static DestroySourcePhases Create(WorldEvent evt)
        {
            var settings = DestroySourcePhaseSettings.Read();

            if (evt == null || !settings.Enabled)
                return null;

            return new DestroySourcePhases(settings, new LivePort(evt),
                () => evt.Composition?.Source?.ResolveWaveCount(Math.Max(0, evt.ResolvedAudienceCount)) ?? 0);
        }

        /// <summary>
        /// The sealed state a source spawns in: the immune overlay (attached before EnterWorld, which allocates a fresh effect state),
        /// health floor = max health, immune on. The overlay is inert until P_DigsiteImmune is set. World thread, before EnterWorld.
        /// </summary>
        public static void ApplySpawnGuard(Creature source)
        {
            if (source == null)
                return;

            if (!source.ApplyMonsterEffectOverlay(ImmuneRecord))
                log.Warn($"[WORLDEVENT] immune record resolved to nothing on source wcid {source.WeenieClassId}; the ward keeps only its health floor");

            source.P_HealthFloor = source.Health.MaxValue;
            source.P_DigsiteImmune = true;
        }

        private sealed class LivePort : IDestroySourcePhasePort
        {
            private readonly WorldEvent evt;

            public LivePort(WorldEvent evt)
            {
                this.evt = evt;
            }

            private Creature Live(uint guid)
            {
                var c = evt.Spawner?.TryGetLiveSource(guid);

                return c == null || c.IsDead || c.IsDestroyed ? null : c;
            }

            public void SetGuard(uint guid, bool immune, uint floorHp)
            {
                var c = Live(guid);

                if (c == null)
                    return;

                var changed = c.P_DigsiteImmune != immune;

                c.P_DigsiteImmune = immune;
                c.P_HealthFloor = floorHp;

                // The grey shield cue the immune boss phases use: up on every shield, down only on a real change.
                if (immune || changed)
                    ACE.Server.MlDigsite.MlDigsiteProps.PlayScriptOn(c, immune ? PlayScript.ShieldUpGrey : PlayScript.ShieldDownGrey);
            }

            public bool TryGetHealth(uint guid, out uint current, out uint max)
            {
                var c = Live(guid);

                current = c?.Health.Current ?? 0;
                max = c?.Health.MaxValue ?? 0;

                return c != null && max > 0;
            }

            public void Announce(string line) => evt.AnnounceLocal(line);
        }
    }
}
