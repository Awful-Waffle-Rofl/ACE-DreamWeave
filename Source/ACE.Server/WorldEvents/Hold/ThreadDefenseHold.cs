using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldObjects;

namespace ACE.Server.WorldEvents.Hold
{
    /// <summary>
    /// A Hold run view owned by something other than a World Event: the Threads Defense encounter
    /// (Docs/Threads/ENCOUNTERS.md "Defense"). It lets that encounter reuse the Hold engine (only same-run tagged monsters
    /// may hurt the protected target, every such hit lands and is sized by undefended_seconds, the seeker targeting)
    /// through the engine's own interface, without a WorldEvent, a WorldEventHoldRun or the World Events
    /// runtime. It references no Threads type, so the engine never learns who hosts it.
    ///
    /// What it does not have, by design: no waves, no healing (player heal off, no shrine), no finale boss, no defend pose,
    /// one participant. The healer, finale and shrine members are no-ops.
    ///
    /// THREADING: every member is safe from any thread (the IWorldEventHoldRunView contract). Single values are volatile,
    /// the lists are immutable arrays swapped whole, and nothing here writes a creature.
    /// </summary>
    public sealed class ThreadDefenseHoldView : IWorldEventHoldRunView
    {
        /// <summary>
        /// The first run id this host hands out. World Event run ids are small, so a high-bit range keeps a view's id from
        /// ever equalling a live World Event run's (identity is the run id, WorldEventHoldRules.MayDamageKnight).
        /// </summary>
        public const uint FirstRunId = 0x80000000u;

        private static long issued = -1;

        /// <summary>The next view run id: FirstRunId, FirstRunId + 1, ..., wrapping inside the high-bit range and never 0. Any thread.</summary>
        internal static uint NextRunId()
        {
            var n = (ulong)Interlocked.Increment(ref issued);

            return FirstRunId | (uint)(n & 0x7FFFFFFFul);
        }

        private volatile bool active;
        private volatile Creature[] knights = Array.Empty<Creature>();
        private volatile Creature[] monsters = Array.Empty<Creature>();

        public ThreadDefenseHoldView(WorldEventHoldTuning tuning)
            : this(NextRunId(), tuning)
        {
        }

        internal ThreadDefenseHoldView(uint runId, WorldEventHoldTuning tuning)
        {
            RunId = runId;
            Tuning = tuning ?? WorldEventHoldTuning.Defaults;
        }

        /// <summary>
        /// The tuning a Defense pillar is paced by: the code defaults with the encounter's pacing and every heal source off.
        /// A run monster's hit is maxHealth x interval / <paramref name="undefendedSeconds"/>, and all of them together are
        /// held to one pillar's worth per <paramref name="minDeathSeconds"/> (0 = no floor). Pure.
        /// </summary>
        public static WorldEventHoldTuning BuildTuning(double undefendedSeconds, double minDeathSeconds)
            => WorldEventHoldTuning.Defaults with
            {
                UndefendedSeconds = undefendedSeconds,
                UndefendedSecondsBase = undefendedSeconds,
                UndefendedSecondsPerParticipant = 0,
                UndefendedSecondsFloor = 0,
                MinDeathSeconds = minDeathSeconds,
                PlayerHealEnabled = false,
                ShrineEnabled = false,
                DefendPoseEnabled = false,
                RegenPerSecond = 0,
            };

        public uint RunId { get; }

        public bool IsActive => active;

        /// <summary>The defense started: run monsters may now hurt the pillar. Any thread.</summary>
        public void Activate() => active = true;

        /// <summary>The defense is over (an outcome, or the run ended): no hit lands on the pillar again and every seeker falls back to ordinary targeting. Any thread.</summary>
        public void Deactivate() => active = false;

        /// <summary>No waves: the heal budget key never moves (and every heal is off anyway).</summary>
        public int WaveNumber => 0;

        public WorldEventHoldTuning Tuning { get; }

        public int ParticipantCount => 1;

        public IReadOnlyList<Creature> Knights => knights;

        public IReadOnlyList<Creature> WaveMonsters => monsters;

        /// <summary>Publishes the protected target. Any thread.</summary>
        public void SetKnight(Creature knight) => knights = knight == null ? Array.Empty<Creature>() : new[] { knight };

        /// <summary>Republishes the run monsters whole (an immutable array). Any thread.</summary>
        public void PublishMonsters(IEnumerable<Creature> current)
            => monsters = (current ?? Enumerable.Empty<Creature>()).Where(c => c != null).ToArray();

        public void NoteKnightHealer(Player healer, double now) { }

        public IReadOnlyList<uint> RecentHealers(double now) => Array.Empty<uint>();

        public void NoteFinaleBossHit(Creature boss, Creature knight, float dealt, double now) { }

        public double FinaleEnrageMult(Creature boss, double now) => 1.0;

        public bool TryUseShrine(WorldObject shrine, Player player, double now, out string reply)
        {
            reply = null;
            return false;
        }
    }

    /// <summary>
    /// The Hold-tag side of the Threads Defense encounter, kept here so the encounter reaches the Hold engine through this
    /// one small surface (the WorldEventHooksCallSiteTests rule: engine code names no Hold tag type directly).
    ///
    /// The tag is write-once (Creature.SetWorldEventHoldTag), so a Defense enemy carries NO tag while it fights players and
    /// is tagged exactly once, as a seeker, when it turns on the pillar (<see cref="TagAsSeeker"/>). The physics layer caches
    /// a creature's Hold run ids when its WeenieObject is built during EnterWorld (WorldEventHooks.HoldPhysicsRunIds), which
    /// for a then-untagged enemy is (0, 0). <see cref="TagAsSeeker"/> therefore re-caches the monster run id with the same
    /// pure rule the constructor uses, so the visibility filter (ObjectMaint) lets the new seeker keep the pillar as a target.
    /// The knight run id and IsMonster are unchanged by a Monster tag, so nothing else that constructor cached moves.
    /// </summary>
    public static class ThreadDefenseHold
    {
        /// <summary>
        /// The pillar's pre-EnterWorld writes: its max health, the protected appearance (not attackable, RubberGlue), and
        /// its Knight tag for <paramref name="view"/> (<see cref="PillarTag"/>), which is then published as the view's one
        /// knight. False (nothing tagged) when the creature already carries a tag. Construction thread, before EnterWorld.
        /// </summary>
        public static bool PreparePillar(Creature pillar, ThreadDefenseHoldView view, int maxHealth)
        {
            if (pillar == null || view == null)
                return false;

            WorldEventSpawner.ApplyHealth(pillar, Math.Max(1, maxHealth));
            WorldEventSpawner.ApplyProtectedAppearance(pillar);

            if (!pillar.SetWorldEventHoldTag(PillarTag(view)))
                return false;

            view.SetKnight(pillar);
            return true;
        }

        /// <summary>
        /// The pillar's knight tag (pure): the engine's KnightTag shape for <paramref name="view"/>, with two opt-ins that keep
        /// it a friendly NPC to every client and quiet in chat (owner playtest ruling 2026-11-11):
        /// - ShowHealthBar false: the Attackable bit goes to the client exactly as the server holds it (false), so the client
        ///   never presents the pillar as an attackable creature (WorldEventHooks.HoldClientAttackable);
        /// - NoAssessHealthLine true: assessing it never says "... is at N% health." (once every 3 s while it stays selected);
        ///   its health is told only by the encounter's once-each 75/50/25 milestones.
        /// </summary>
        public static WorldEventHoldTag PillarTag(ThreadDefenseHoldView view)
            => new WorldEventHoldTag
            {
                RunId = view.RunId,
                Run = view,
                Kind = HoldUnitKind.Knight,
                WaveRole = HoldRole.Seeker,
                Role = HoldDamageRole.None,
                ShowHealthBar = false,
                NoAssessHealthLine = true,
            };

        /// <summary>The pillar's max health: the Hold time-scaled value, large enough that a paced hit never rounds away.</summary>
        public const int PillarMaxHealth = ProtectDef.TimeScaledMaxHealth;

        /// <summary>The tag a Defense enemy takes when it turns on the pillar: a seeker Monster of <paramref name="view"/>, no role. Pure.</summary>
        public static WorldEventHoldTag SeekerTag(ThreadDefenseHoldView view)
            => new WorldEventHoldTag
            {
                RunId = view.RunId,
                Run = view,
                Kind = HoldUnitKind.Monster,
                WaveRole = HoldRole.Seeker,
                Role = HoldDamageRole.None,
                Seeker = true,
            };

        /// <summary>
        /// Tags <paramref name="enemy"/> as a seeker of <paramref name="view"/> (its one and only tag) and re-caches its
        /// physics monster run id. False, changing nothing, when it already carries a tag. The enemy's own landblock thread.
        /// </summary>
        public static bool TagAsSeeker(Creature enemy, ThreadDefenseHoldView view)
        {
            if (enemy == null || view == null)
                return false;

            if (!enemy.SetWorldEventHoldTag(SeekerTag(view)))
                return false;

            RecacheMonsterRun(enemy.PhysicsObj?.WeenieObj, enemy);

            return true;
        }

        /// <summary>
        /// Re-caches <paramref name="weenie"/>'s monster run id from <paramref name="enemy"/>'s tag with the rule its
        /// constructor used (WorldEventHooks.HoldPhysicsRunIds). Only the monster run id is written: the knight run id and
        /// IsMonster are what the constructor computed and a Monster tag leaves them as they were. Null-safe.
        /// </summary>
        internal static void RecacheMonsterRun(ACE.Server.Physics.Common.WeenieObject weenie, Creature enemy)
        {
            if (weenie == null || enemy == null)
                return;

            weenie.HoldMonsterRunId = WorldEventHooks.HoldPhysicsRunIds(enemy).MonsterRun;
        }

        /// <summary>
        /// The switch: tags the enemy (<see cref="TagAsSeeker"/>) and aims it at the pillar now through the engine's seeker
        /// lock, which also wakes it. True when it was tagged (it then seeks on every later target pick even if the pillar
        /// was out of reach this moment). The enemy's own landblock thread.
        /// </summary>
        public static bool SwitchToSeeker(Creature enemy, ThreadDefenseHoldView view)
        {
            if (enemy == null || view == null || enemy.IsDead || enemy.IsDestroyed || !view.IsActive)
                return false;

            if (!TagAsSeeker(enemy, view))
                return false;

            if (enemy.PhysicsObj != null)
                WorldEventHooks.KeepsHoldSeekerLock(enemy, null);

            return true;
        }

        /// <summary>
        /// One portal's layers, the Hold portal recipe's own layout (WorldEventHoldRules.ScalePortalParts, then
        /// PlaceTargetDecor): <paramref name="parts"/> scaled whole by <paramref name="scale"/> (offsets, heights and sizes
        /// together, snapped to the ground outdoors), in the frame of a portal that stands at compass bearing
        /// <paramref name="bearingDegrees"/> from what it surrounds and faces away from it, as the Hold ring's portals do.
        /// Offsets are metres east/north of the portal point. Pure.
        /// </summary>
        public static List<DecorDef> PortalLayers(IReadOnlyList<DecorDef> parts, double scale, double bearingDegrees)
            => WorldEventHoldRules.PlaceTargetDecor(
                new[] { new WorldEventHoldRules.RingOffset(0f, 0f, WorldEventHoldRules.OutwardYaw(bearingDegrees)) },
                WorldEventHoldRules.ScalePortalParts(parts, scale));

        /// <summary>True when <paramref name="creature"/> carries a Hold tag of <paramref name="view"/> (a Defense seeker, or the pillar). Any thread.</summary>
        public static bool IsTaggedFor(Creature creature, ThreadDefenseHoldView view)
            => creature != null && view != null && creature.WorldEventHold?.RunId == view.RunId;

        /// <summary>True when <paramref name="creature"/> carries any Hold tag at all. Any thread.</summary>
        public static bool IsTagged(Creature creature) => creature?.WorldEventHold != null;
    }
}
