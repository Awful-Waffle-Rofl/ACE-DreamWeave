using System;
using System.Collections.Generic;
using System.Threading;

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
    ///
    /// A record can exist with Damage 0 and Kills 0: a COMBAT-CONTACT record (2026-10-04), written at hit
    /// time when the player hit, or was hurt by, a run creature (see
    /// <see cref="WorldEventParticipation.CreditCombatContact(uint, uint, string, string)"/>). Such a record
    /// earns the ordinary reward and counts as a participant, but carries no damage or kill and is
    /// skipped by <see cref="WorldEventParticipation.TopKiller"/> and
    /// <see cref="WorldEventParticipation.TopDamager"/>.
    /// </summary>
    public sealed class ParticipantRecord
    {
        public uint CharacterGuid;
        public uint AccountId;
        public string Ip;
        public string Name;
        public float Damage;
        public int Kills;

        /// <summary>
        /// Unix seconds of this player's first DAMAGE OR KILL credit on this run - the death-path credit,
        /// which is what the TopKiller tie-break orders on. 0 while the record is contact-only: a hit-time
        /// contact credit deliberately does not set it, so being hit early cannot win a kill-count tie.
        /// </summary>
        public double FirstCredit;

        /// <summary>
        /// True while this record holds neither damage nor a kill - it exists only because of a hit-time
        /// combat-contact credit. The death path can never leave a record in this state: CreditDamageCore
        /// skips a non-positive amount and CreditKillCore always increments.
        /// </summary>
        public bool IsContactOnly => Damage <= 0 && Kills == 0;

        public ParticipantRecord Clone() => (ParticipantRecord)MemberwiseClone();
    }

    /// <summary>
    /// The participation ledger (TECH-DESIGN 2.6). Feeds MVP attribution, the participant count, the
    /// abandon/wipe short-circuits, and (2026-09-07) the crate-versus-coal payout: a claimant with a record
    /// of ANY kind is paid the ordinary crate.
    ///
    /// WRITERS. There are two (2026-10-04):
    ///
    /// 1. The death path: Creature.Die (Creature_Death.cs) -> WorldEventManager.OnEventCreatureDied ->
    ///    WorldEvent.OnCreatureDied -> <see cref="CreditDamage"/> / <see cref="CreditKill"/>. This remains
    ///    the ONLY source of the Damage and Kills totals.
    /// 2. The hit-time contact path: DamageHistory.Add -> WorldEventCombatCreditHook.OnDamageRecorded ->
    ///    WorldEvent.CreditCombatContact -> <see cref="CreditCombatContact(uint, uint, string, string)"/>. It
    ///    only ENSURES a record exists and never adds damage or kills, so the death walk still accumulates
    ///    every point exactly once and nothing is double counted.
    ///
    /// THREADING. Both writers run on whatever thread applied the damage or the death, which is NOT
    /// uniformly one thread: a physics-driven hit or kill (a spell projectile collision) lands in
    /// Landblock.TickPhysics, a monster-driven one in Landblock.TickMultiThreadedWork - LandblockManager runs
    /// both under a Parallel.ForEach over landblock GROUPS - while an object heartbeat (hotspot, damage over
    /// time) and a player's own queued action chain land in Landblock.TickSingleThreadedWork on the world
    /// thread. Two players in two landblock groups hitting run creatures at the same instant therefore write
    /// here concurrently, and the claim handler (WorldEventCacheHandler) reads <see cref="TryGetRecord"/> from
    /// a landblock-group worker for a FastTick player. The old "no lock, because the death path is the only
    /// writer and Rewarding cuts it off" argument no longer holds for the contact writer, so:
    ///
    /// * every instance member takes <see cref="sync"/>, a private object nothing outside this class can
    ///   lock, so the dictionary itself is never torn;
    /// * every reader that hands a record OUT returns a <see cref="ParticipantRecord.Clone"/> taken under the
    ///   lock (<see cref="Records"/>, <see cref="TopKiller"/>, <see cref="TopDamager"/>,
    ///   <see cref="TryGetRecord"/>), because a writer mutates a record's fields in place and a caller
    ///   walking the live record would otherwise read a half-updated one;
    /// * nothing that can block or re-enter is called while the lock is held. CreditDamage and CreditKill
    ///   read the clock, DamageHistory, the Player and its Session first and build plain tuples, then take the
    ///   lock only to apply them. CreditCombatContact probes <see cref="Contains"/> under one brief
    ///   acquisition, captures the Player's identity (Session, IP, Name) OUTSIDE the lock, and then takes it
    ///   again only to insert. <see cref="AliveNear"/> copies the guid list under the lock and calls
    ///   PlayerManager only after releasing it. The critical sections are therefore pure
    ///   dictionary work, short and leaf-level: this lock is never held while another lock is taken, so it
    ///   cannot take part in a lock-order cycle.
    ///
    /// SECOND OWNER. The ML digsite encounter (MlDigsiteEncounter) keeps its own instance and serialises every
    /// call on it under the encounter's own lock. That stays correct and unchanged: the order is always
    /// encounter lock -> this lock, and since this lock never calls out while held, the reverse order cannot
    /// arise and the nesting cannot deadlock. The digsite never calls the contact writer, so its ledger
    /// holds exactly what it held before.
    ///
    /// CLAIM-WINDOW ORDERING still matters for CORRECTNESS, though no longer for memory safety. The contact
    /// writer credits only while the run is Active (WorldEvent.CreditCombatContact), and entering Rewarding
    /// nulls every held creature's P_WorldEvent back-reference (WorldEventSpawner.DestroyAll), which both
    /// writers require. So the ledger stops growing when the fight ends and a claimant's answer does not
    /// depend on being hit by a straggler during the claim window.
    /// </summary>
    public sealed class WorldEventParticipation
    {
        private readonly object sync = new object();

        private readonly Dictionary<uint, ParticipantRecord> records = new Dictionary<uint, ParticipantRecord>();

        private readonly Func<double> clock;

        public WorldEventParticipation(Func<double> clock = null)
        {
            this.clock = clock ?? Time.GetUnixTime;
        }

        /// <summary>A snapshot: a fresh list of cloned records, safe to walk while writers keep crediting.</summary>
        public IReadOnlyCollection<ParticipantRecord> Records
        {
            get
            {
                lock (sync)
                {
                    var copy = new List<ParticipantRecord>(records.Count);

                    foreach (var rec in records.Values)
                        copy.Add(rec.Clone());

                    return copy;
                }
            }
        }

        /// <summary>Every credited player, contact-only records included (TECH-DESIGN 2.6, 2026-10-04).</summary>
        public int Count
        {
            get
            {
                lock (sync)
                    return records.Count;
            }
        }

        /// <summary>
        /// Walks <c>dead.DamageHistory.TotalDamage</c> (modelled on Creature_Death.cs:414-461): skips a
        /// zeroed-out entry the same way the precedent does (Creature_Death.cs:379
        /// <c>if (kvp.Value.TotalDamage &lt;= 0) continue;</c> - DamageHistory.OnHealInternal,
        /// DamageHistory.cs:140-151, zeroes an entry in place on a heal-to-full rather than removing it, so
        /// an unguarded walk would credit a phantom zero-damage participant), resolves a pet's damage to
        /// its owner via <see cref="DamageHistoryInfo.TryGetPetOwnerOrAttacker"/>, keeps only live Players,
        /// and accumulates damage per character guid. Safe to call with a dead creature that has no
        /// damagers. The DamageHistory walk happens BEFORE the lock; only the dictionary update is inside it.
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

            var now = clock();

            lock (sync)
                CreditDamageCore(damagers, records, now);
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

            var guid = player.Guid.Full;
            var accountId = player.Session?.AccountId ?? 0;
            var ip = player.Session?.EndPointC2S?.Address?.ToString();
            var name = player.Name;
            var now = clock();

            lock (sync)
                CreditKillCore(guid, accountId, ip, name, records, now);
        }

        /// <summary>
        /// Hit-time combat-contact credit (owner ruling 2026-10-04): ensures <paramref name="player"/> has a
        /// record and changes NOTHING else - no damage, no kill, no FirstCredit. Returns true when this call
        /// created the record. The caller (WorldEvent.CreditCombatContact) owns the run-state gate.
        ///
        /// HOT PATH while a run is Active: every qualifying hit lands here, and almost all of them are from a
        /// player who already has a record. So the existing-record case is answered first, by
        /// <see cref="Contains"/> - one brief uncontended lock and a dictionary probe - BEFORE any identity
        /// is captured (no Session read, no IP string, no Name property read). Only a genuinely new guid pays
        /// for the capture. The probe and the insert are two separate lock acquisitions, which is safe: a
        /// racing first credit for the same guid from another thread just makes CreditContactCore's own
        /// ContainsKey refuse the second insert.
        /// </summary>
        public bool CreditCombatContact(Player player)
        {
            if (player == null)
                return false;

            var guid = player.Guid.Full;

            if (guid == 0 || Contains(guid))
                return false;

            return CreditCombatContact(guid, player.Session?.AccountId ?? 0,
                player.Session?.EndPointC2S?.Address?.ToString(), player.Name);
        }

        /// <summary>Guid-level form of <see cref="CreditCombatContact(Player)"/>, for callers and tests with no live Player.</summary>
        public bool CreditCombatContact(uint characterGuid, uint accountId, string ip, string name)
        {
            if (characterGuid == 0)
                return false;

            Interlocked.Increment(ref contactCapturesForTest);

            lock (sync)
                return CreditContactCore(characterGuid, accountId, ip, name, records);
        }

        /// <summary>True when <paramref name="characterGuid"/> already has a record of any kind.</summary>
        public bool Contains(uint characterGuid)
        {
            lock (sync)
                return records.ContainsKey(characterGuid);
        }

        private long contactCapturesForTest;

        /// <summary>
        /// Test seam (InternalsVisibleTo): how many contact credits got past the existing-record fast path and
        /// paid for an identity capture. One interlocked increment on the rare slow path only.
        /// </summary>
        internal long ContactCapturesForTest => Interlocked.Read(ref contactCapturesForTest);

        /// <summary>Most kills; tie broken by earliest FirstCredit; contact-only records skipped; null when none qualify. A clone.</summary>
        public ParticipantRecord TopKiller()
        {
            lock (sync)
                return TopKillerCore(records.Values)?.Clone();
        }

        /// <summary>Most damage; contact-only records skipped; null when none qualify. A clone.</summary>
        public ParticipantRecord TopDamager()
        {
            lock (sync)
                return TopDamagerCore(records.Values)?.Clone();
        }

        /// <summary>Hands back a clone taken under the lock, never the live record.</summary>
        public bool TryGetRecord(uint characterGuid, out ParticipantRecord rec)
        {
            lock (sync)
            {
                if (records.TryGetValue(characterGuid, out var live))
                {
                    rec = live.Clone();
                    return true;
                }
            }

            rec = null;
            return false;
        }

        /// <summary>
        /// Test seam (InternalsVisibleTo): credits <paramref name="damage"/> to a bare guid through the same
        /// <see cref="CreditDamageCore"/> a live <see cref="CreditDamage"/> ends in, for tests that cannot build
        /// a Player to stand in a DamageHistory.
        /// </summary>
        internal void CreditDamageForTest(uint characterGuid, float damage)
        {
            var now = clock();

            lock (sync)
                CreditDamageCore(new[] { (characterGuid, 0u, (string)null, (string)null, damage) }, records, now);
        }

        /// <summary>Test seam (InternalsVisibleTo): the kill credit by bare guid, through <see cref="CreditKillCore"/>.</summary>
        internal void CreditKillForTest(uint characterGuid, string name = null)
        {
            var now = clock();

            lock (sync)
                CreditKillCore(characterGuid, 0, null, name, records, now);
        }

        /// <summary>
        /// The credit question the 2026-09-07 payout gate asks, with the null-ledger fallback folded in so
        /// the whole rule is reachable from a unit test. A NULL ledger answers TRUE - "assume they fought" -
        /// which is the fail-open direction: an absent ledger must pay the ordinary crate, never the booby
        /// prize. That case should be unreachable in production (<see cref="WorldEvent"/> constructs a
        /// ledger unconditionally), but the payout must not depend on that staying true.
        ///
        /// Any record answers TRUE, a contact-only one included: since 2026-10-04 a player who dealt ANY
        /// damage to, or took ANY damage from, a run creature during the fight earns the ordinary reward.
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
        ///
        /// Contact-only records count, deliberately: they are participants. The online/alive/near filter
        /// is what keeps a player who was hit once and then left, or died, from holding a run open.
        /// The guid list is copied under the lock; PlayerManager is called only after it is released.
        /// </summary>
        public int AliveNear(Position anchor, float radius)
        {
            if (anchor == null || radius <= 0)
                return 0;

            List<uint> guids;

            lock (sync)
            {
                if (records.Count == 0)
                    return 0;

                guids = new List<uint>(records.Keys);
            }

            var count = 0;

            foreach (var guid in guids)
            {
                var player = PlayerManager.GetOnlinePlayer(guid);

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
        /// Metadata (AccountId, Ip, Name) is captured only when a record is created. FirstCredit is
        /// set on a record's first damage-or-kill credit, which for a record created by the contact path is
        /// the first credit here rather than its creation. A non-positive <c>damage</c> is skipped entirely -
        /// it creates no record and contributes nothing to an existing one - matching the CreditDamage guard
        /// against DamageHistory's zeroed-in-place entries. Not thread-safe on its own: the instance callers
        /// hold the ledger lock.
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

                var rec = GetOrCreate(records, damager.guid, damager.accountId, damager.ip, damager.name);

                if (rec.IsContactOnly)
                    rec.FirstCredit = now;

                rec.Damage += damager.damage;
            }
        }

        /// <summary>
        /// Pure core, unit-testable (TECH-DESIGN D6): the same guid/metadata/increment logic
        /// <see cref="CreditKill"/> performs against a live Player. A guid already in
        /// <paramref name="records"/> just gets +1 Kills - the identity fields are captured only the first
        /// time a guid is seen (from any core), and FirstCredit only on its first damage-or-kill credit, same
        /// rule as CreditDamageCore.
        /// </summary>
        public static void CreditKillCore(uint guid, uint accountId, string ip, string name,
            Dictionary<uint, ParticipantRecord> records, double now)
        {
            if (records == null || guid == 0)
                return;

            var rec = GetOrCreate(records, guid, accountId, ip, name);

            if (rec.IsContactOnly)
                rec.FirstCredit = now;

            rec.Kills++;
        }

        /// <summary>
        /// Pure core behind the hit-time contact credit (2026-10-04): creates a contact-only record (Damage 0,
        /// Kills 0, FirstCredit 0) when <paramref name="guid"/> has none, and touches nothing on an existing
        /// record. Returns true only when it created one.
        /// </summary>
        public static bool CreditContactCore(uint guid, uint accountId, string ip, string name,
            Dictionary<uint, ParticipantRecord> records)
        {
            if (records == null || guid == 0 || records.ContainsKey(guid))
                return false;

            records[guid] = new ParticipantRecord
            {
                CharacterGuid = guid,
                AccountId = accountId,
                Ip = ip,
                Name = name
            };

            return true;
        }

        /// <summary>
        /// Pure core behind <see cref="TopKiller"/>, unit-testable without a live ledger (D6). Contact-only
        /// records never qualify, so a ledger of nothing but contact credits answers null.
        /// </summary>
        public static ParticipantRecord TopKillerCore(IEnumerable<ParticipantRecord> records)
        {
            ParticipantRecord best = null;

            if (records == null)
                return null;

            foreach (var rec in records)
            {
                if (rec == null || rec.IsContactOnly)
                    continue;

                if (best == null || rec.Kills > best.Kills ||
                    (rec.Kills == best.Kills && rec.FirstCredit < best.FirstCredit))
                    best = rec;
            }

            return best;
        }

        /// <summary>Pure core behind <see cref="TopDamager"/>, unit-testable without a live ledger (D6). Contact-only records never qualify.</summary>
        public static ParticipantRecord TopDamagerCore(IEnumerable<ParticipantRecord> records)
        {
            ParticipantRecord best = null;

            if (records == null)
                return null;

            foreach (var rec in records)
            {
                if (rec == null || rec.IsContactOnly)
                    continue;

                if (best == null || rec.Damage > best.Damage)
                    best = rec;
            }

            return best;
        }

        private static ParticipantRecord GetOrCreate(Dictionary<uint, ParticipantRecord> records, uint guid,
            uint accountId, string ip, string name)
        {
            if (!records.TryGetValue(guid, out var rec))
            {
                rec = new ParticipantRecord
                {
                    CharacterGuid = guid,
                    AccountId = accountId,
                    Ip = ip,
                    Name = name
                };

                records[guid] = rec;
            }

            return rec;
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
