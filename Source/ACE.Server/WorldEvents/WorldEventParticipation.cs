using System;
using System.Collections.Generic;

using ACE.Common;
using ACE.Entity;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// One credited participant of a run (TECH-DESIGN 2.6). A repeat damager updates the same record -
    /// de-duplicated by character guid - rather than creating a second entry.
    /// </summary>
    public sealed class ParticipantRecord
    {
        public uint CharacterGuid;
        public uint AccountId;
        public string Ip;
        public string Name;
        public float Damage;
        public int Kills;

        /// <summary>Unix seconds of this player's first credit on this run.</summary>
        public double FirstCredit;
    }

    /// <summary>
    /// The participation ledger (TECH-DESIGN 2.6). Per decision C13 this narrowly serves MVP attribution,
    /// the audience/participant count, and the abandon/wipe short-circuits - it never decides who may claim
    /// a reward (2.7's claim gate is presence-based, not damage-based).
    ///
    /// THREADING. Every write arrives through exactly one path: Creature.Die (Creature_Death.cs:153) ->
    /// WorldEventManager.OnEventCreatureDied -> WorldEvent.OnCreatureDied -> CreditDamage/CreditKill
    /// (WorldEvent.cs:2652-2653). Which THREAD that is depends on what ticked the dying creature, and it is
    /// NOT uniformly "a landblock-group thread": a physics-driven kill (a spell projectile collision) lands
    /// in Landblock.TickPhysics (Landblock.cs:897,910), a monster-driven one in
    /// Landblock.TickMultiThreadedWork (Landblock.cs:925,974) - LandblockManager runs both of those under a
    /// Parallel.ForEach over landblock GROUPS (LandblockManager.cs:371,477) - while an object heartbeat
    /// (hotspot, damage-over-time) and a player's own queued action chain land in
    /// Landblock.TickSingleThreadedWork (Landblock.cs:1141,1173,1200), which LandblockManager runs as a
    /// plain nested foreach directly on the world thread (LandblockManager.cs:557-577).
    ///
    /// No lock is taken. TWO separate arguments carry that, and both are load-bearing:
    ///
    /// 1. THE MANAGER'S OWN READS never overlap a write. LandblockManager.Tick (LandblockManager.cs:317-335)
    ///    joins both Parallel.ForEach passes before it returns, and WorldManager calls it
    ///    (WorldManager.cs:719) BEFORE WorldEventManager.Tick (WorldManager.cs:742). So no landblock work of
    ///    any kind is in flight while a WorldEvent method - Tick, Finish, the announcer's TopKiller/Count
    ///    reads - runs on the world thread.
    ///
    /// 2. THE CLAIM-TIME READ (WorldEventCacheHandler's TryGetRecord, which decides crate vs coal) does NOT
    ///    inherit argument 1: it runs on a landblock thread, not the world thread's manager tick. Which one
    ///    depends on Player.FastTick - an ordinary player's use callback lands in the player's OWN action
    ///    queue (Player_Tick.cs:1004 -> :66) and so runs on the world thread inside TickSingleThreadedWork,
    ///    but a FastTick player's callback fires from UpdateObjectPhysics (Player_Tick.cs:340, Player_Move.cs:210)
    ///    and so runs on a landblock-group Parallel.ForEach worker. It is safe anyway, because a claim is
    ///    only legal while the run is Rewarding (WorldEventCacheHandler.cs:109) and NO write can happen in
    ///    that state.
    ///
    ///    Be precise about WHY, because the obvious reason is false: OnEventCreatureDied does NOT check for
    ///    Rewarding, it refuses only Done (WorldEventManager.cs:533). The real reason is the back-reference.
    ///    Entering Rewarding runs DestroyEventCreatures SYNCHRONOUSLY (WorldEvent.cs:2798), which nulls
    ///    creature.P_WorldEvent on every held creature (WorldEventSpawner.cs:1332) and latches
    ///    creaturePassRan so the spawner refuses every later adopt (WorldEventSpawner.cs:3022-3024) - and
    ///    BOTH the Creature.Die hook (Creature_Death.cs:153) and OnEventCreatureDied's identity check
    ///    (WorldEventManager.cs:539) require that back-reference. A run creature killed during the claim
    ///    window therefore reaches nothing here. Finish itself always runs on the world thread with the
    ///    landblock passes joined, so there is no window between the state store and the nulling.
    ///
    /// Two residual hazards, neither of them reachable by the current code:
    ///   * a throw part-way through Spawner.DestroyAll's loop would leave the creatures after the throw
    ///     point holding a live back-reference while creaturePassRan is already latched. Their deaths WOULD
    ///     then write here while a claim reads. Nothing added to that loop may throw.
    ///   * a command handler reading Records/Count/TopKiller off a landblock thread can still race a write
    ///     while the run is Active. A torn read there is benign (nothing outside the death path mutates the
    ///     ledger), but no command handler may WRITE to it.
    ///
    /// If either invariant changes - a straggler creature deliberately left alive through the claim window,
    /// or any second writer - this dictionary needs a lock, and Records must then hand back a snapshot
    /// rather than the live records.Values view it returns today.
    /// </summary>
    public sealed class WorldEventParticipation
    {
        private readonly Dictionary<uint, ParticipantRecord> records = new Dictionary<uint, ParticipantRecord>();

        private readonly Func<double> clock;

        public WorldEventParticipation(Func<double> clock = null)
        {
            this.clock = clock ?? Time.GetUnixTime;
        }

        public IReadOnlyCollection<ParticipantRecord> Records => records.Values;

        public int Count => records.Count;

        /// <summary>
        /// Walks <c>dead.DamageHistory.TotalDamage</c> (modelled on Creature_Death.cs:414-461): skips a
        /// zeroed-out entry the same way the precedent does (Creature_Death.cs:379
        /// <c>if (kvp.Value.TotalDamage &lt;= 0) continue;</c> - DamageHistory.OnHealInternal,
        /// DamageHistory.cs:140-151, zeroes an entry in place on a heal-to-full rather than removing it, so
        /// an unguarded walk would credit a phantom zero-damage participant), resolves a pet's damage to
        /// its owner via <see cref="DamageHistoryInfo.TryGetPetOwnerOrAttacker"/>, keeps only live Players,
        /// and accumulates damage per character guid. Safe to call with a dead creature that has no
        /// damagers.
        /// </summary>
        public void CreditDamage(Creature dead)
        {
            if (dead?.DamageHistory == null)
                return;

            var damagers = new List<(uint guid, uint accountId, string ip, string name, float damage)>();

            foreach (var info in dead.DamageHistory.TotalDamage.Values)
            {
                if (info == null || info.TotalDamage <= 0)
                    continue;

                var player = ResolvePlayer(info);

                if (player == null)
                    continue;

                damagers.Add((player.Guid.Full, player.Session?.AccountId ?? 0,
                    player.Session?.EndPointC2S?.Address?.ToString(), player.Name, info.TotalDamage));
            }

            CreditDamageCore(damagers, records, clock());
        }

        /// <summary>
        /// +1 Kills to the resolved player (pet damage resolves to its owner, same rule as CreditDamage).
        /// A player who is credited for the first time here - a killing blow with no prior damage credit on
        /// this run - gets a fresh record, same as CreditDamage would create.
        /// </summary>
        public void CreditKill(DamageHistoryInfo lastDamager)
        {
            var player = ResolvePlayer(lastDamager);

            if (player == null)
                return;

            CreditKillCore(player.Guid.Full, player.Session?.AccountId ?? 0,
                player.Session?.EndPointC2S?.Address?.ToString(), player.Name, records, clock());
        }

        /// <summary>Most kills; tie broken by earliest FirstCredit; null when the ledger is empty.</summary>
        public ParticipantRecord TopKiller() => TopKillerCore(records.Values);

        /// <summary>Most damage; null when the ledger is empty.</summary>
        public ParticipantRecord TopDamager() => TopDamagerCore(records.Values);

        public bool TryGetRecord(uint characterGuid, out ParticipantRecord rec) => records.TryGetValue(characterGuid, out rec);

        /// <summary>
        /// The credit question the 2026-09-07 payout gate asks, with the null-ledger fallback folded in so
        /// the whole rule is reachable from a unit test. A NULL ledger answers TRUE - "assume they fought" -
        /// which is the fail-open direction: an absent ledger must pay the ordinary crate, never the booby
        /// prize. That case should be unreachable in production (<see cref="WorldEvent"/> constructs a
        /// ledger unconditionally), but the payout must not depend on that staying true.
        ///
        /// Static, and takes the ledger as an argument rather than being an instance member, precisely so a
        /// test can pass null - which an instance method cannot be called on. It is the seam that makes the
        /// caller in <c>WorldEventCacheHandler</c> a one-liner with no logic of its own left to go untested.
        /// </summary>
        public static bool HasCreditOrUnknown(WorldEventParticipation ledger, uint characterGuid)
        {
            return ledger == null || ledger.TryGetRecord(characterGuid, out _);
        }

        /// <summary>
        /// Number of credited players who are currently online, not dead, in the same landblock instance as
        /// <paramref name="anchor"/>, and within <paramref name="radius"/> of it. Feeds
        /// <see cref="WorldEvent.LastAliveParticipantAt"/> (TECH-DESIGN 2.2). Copies
        /// WorldEventAudienceSampler's instance-then-distance idiom. Tolerates a null anchor (returns 0)
        /// so a directly constructed WorldEvent (no landblock bridge, no anchor) never throws here.
        /// </summary>
        public int AliveNear(Position anchor, float radius)
        {
            if (anchor == null || radius <= 0 || records.Count == 0)
                return 0;

            var count = 0;

            foreach (var rec in records.Values)
            {
                var player = PlayerManager.GetOnlinePlayer(rec.CharacterGuid);

                if (player == null || player.IsDead)
                    continue;

                var loc = player.Location;

                if (loc == null || loc.Instance != anchor.Instance)
                    continue;

                if (loc.DistanceTo(anchor) > radius)
                    continue;

                count++;
            }

            return count;
        }

        /// <summary>
        /// Pure core, unit-testable (TECH-DESIGN D6): the same walk over plain tuples that
        /// <see cref="CreditDamage"/> performs against a live Creature. A repeat guid in
        /// <paramref name="damagers"/> accumulates onto the same record rather than creating a second one.
        /// Metadata (AccountId, Ip, Name, FirstCredit) is captured only on a record's first credit. A
        /// non-positive <c>damage</c> is skipped entirely - it creates no record and contributes nothing to
        /// an existing one - matching the CreditDamage guard against DamageHistory's zeroed-in-place entries.
        /// </summary>
        public static void CreditDamageCore(IEnumerable<(uint guid, uint accountId, string ip, string name, float damage)> damagers,
            Dictionary<uint, ParticipantRecord> records, double now)
        {
            if (damagers == null || records == null)
                return;

            foreach (var damager in damagers)
            {
                if (damager.guid == 0 || damager.damage <= 0)
                    continue;

                if (!records.TryGetValue(damager.guid, out var rec))
                {
                    rec = new ParticipantRecord
                    {
                        CharacterGuid = damager.guid,
                        AccountId = damager.accountId,
                        Ip = damager.ip,
                        Name = damager.name,
                        FirstCredit = now
                    };

                    records[damager.guid] = rec;
                }

                rec.Damage += damager.damage;
            }
        }

        /// <summary>
        /// Pure core, unit-testable (TECH-DESIGN D6): the same guid/metadata/increment logic
        /// <see cref="CreditKill"/> performs against a live Player. A guid already in
        /// <paramref name="records"/> just gets +1 Kills - FirstCredit and the identity fields are
        /// captured only the first time a guid is seen (from either core), same rule as CreditDamageCore.
        /// </summary>
        public static void CreditKillCore(uint guid, uint accountId, string ip, string name,
            Dictionary<uint, ParticipantRecord> records, double now)
        {
            if (records == null || guid == 0)
                return;

            if (!records.TryGetValue(guid, out var rec))
            {
                rec = new ParticipantRecord
                {
                    CharacterGuid = guid,
                    AccountId = accountId,
                    Ip = ip,
                    Name = name,
                    FirstCredit = now
                };

                records[guid] = rec;
            }

            rec.Kills++;
        }

        /// <summary>Pure core behind <see cref="TopKiller"/>, unit-testable without a live ledger (D6).</summary>
        public static ParticipantRecord TopKillerCore(IEnumerable<ParticipantRecord> records)
        {
            ParticipantRecord best = null;

            if (records == null)
                return null;

            foreach (var rec in records)
            {
                if (best == null || rec.Kills > best.Kills ||
                    (rec.Kills == best.Kills && rec.FirstCredit < best.FirstCredit))
                    best = rec;
            }

            return best;
        }

        /// <summary>Pure core behind <see cref="TopDamager"/>, unit-testable without a live ledger (D6).</summary>
        public static ParticipantRecord TopDamagerCore(IEnumerable<ParticipantRecord> records)
        {
            ParticipantRecord best = null;

            if (records == null)
                return null;

            foreach (var rec in records)
            {
                if (best == null || rec.Damage > best.Damage)
                    best = rec;
            }

            return best;
        }

        /// <summary>
        /// A pet's damage/kill resolves to its owner, else the direct attacker - exactly
        /// <see cref="DamageHistoryInfo.TryGetPetOwnerOrAttacker"/> (DamageHistoryInfo.cs:52-58), which
        /// already takes the guard Creature_Death.cs:373-378 takes before touching PetOwner (a null
        /// WeakReference field throws on TryGetTarget) - kept only when it resolves to a live Player.
        /// </summary>
        private static Player ResolvePlayer(DamageHistoryInfo info)
        {
            return info?.TryGetPetOwnerOrAttacker() as Player;
        }
    }
}
