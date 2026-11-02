using System;

using ACE.Common;
using ACE.Database;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity.Actions;
using ACE.Server.Knots;
using ACE.Server.HazardMechanics;
using ACE.Server.Managers;
using ACE.Server.Realms;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using log4net;

using Position = ACE.Entity.Position;

namespace ACE.Server.Pinnacles
{
    /// <summary>
    /// The Knot-side encounter for one destination kind. It charges at the head talk, not at the seam
    /// (<see cref="SeamChargesEntry"/> false), and the Knot lives until the attempt ends. Every RETURN into a running,
    /// held fight spends one more entry, here in <see cref="TryEnter"/> (owner ruling R1, 2026-10-19); a
    /// return to collect a delivered cache is free (C1). Entry is always realm 0 and closed to the fellowship.
    /// </summary>
    public sealed class PinnacleEncounter : IPinnacleEncounter
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public const string DisabledLine = "The trial will not open right now.";
        public const string NotReadyLine = "The trial is not ready. Try again in a moment.";
        public const string ClosedLine = "That trial has already closed.";
        public const string NotYoursLine = "That trial is not yours to enter.";
        public const string AdminInsideInstanceLine = "Leave your current instance before opening an admin copy.";

        public PinnacleEncounter(string kind)
        {
            Kind = kind;
        }

        public string Kind { get; }

        public bool SeamChargesEntry => false;

        public KnotResume ResumableAttempt(Player player, WorldObject knot)
            => player != null && knot != null ? PinnacleFightManager.ResumableAttempt(player.Guid.Full, knot.Guid.Full) : KnotResume.None;

        public void ForfeitAttempt(Player player, WorldObject knot)
        {
            if (player != null && knot != null)
                PinnacleFightManager.Forfeit(player.Guid.Full, knot.Guid.Full, "no Knot entries left to return");
        }

        /// <summary>
        /// Called by the Knot seam on the OWNER's thread (their own Knot use), so a paid return spends its entry here
        /// directly through <see cref="KnotEncounterEvents.SpendEntry"/>, after the teleport has been issued.
        /// </summary>
        public bool TryEnter(Player player, WorldObject knot, PinnacleDef def, int knotLevel, int phase, out string refusal)
        {
            refusal = null;

            if (!PinnacleTunables.Enabled)
            {
                refusal = DisabledLine;
                return false;
            }

            PinnacleFightManager.TryGetFight(player.Guid.Full, out var lf);

            var exists = lf != null && !lf.Fight.IsEnded;
            var action = PinnacleEntryRules.Decide(exists,
                exists ? lf.Fight.State : PinnacleFightState.NotStarted,
                exists && lf.Fight.AwaitingDelivery,
                exists && lf.Fight.Collecting,
                exists && lf.IsCopyLive,
                exists && lf.Fight.KnotGuid == knot.Guid.Full,
                out refusal);

            switch (action)
            {
                case PinnacleEntryAction.CreateCopy:
                    // An uncharged fight left over from another pull is ended first: Reset on its own landblock, no Knot
                    // report because nothing was charged. A fight whose copy is gone is ended by PinnacleFightManager.Add
                    // when the new fight replaces it in the registry (that end reports a charged loss).
                    // A fresh copy costs an entry at the head talk, so a Knot with none left cannot open one. The seam
                    // already refuses that, except in one race: it read a Free return (a fight being collected) that
                    // ended before this call, leaving nothing to return to and a zero-entry Knot at the door.
                    if (NoEntriesLeft(knot.GetProperty(PropertyInt.Structure)))
                    {
                        refusal = KnotItems.KnotHasNoEntries;
                        return false;
                    }

                    if (exists && lf.Fight.State == PinnacleFightState.NotStarted && lf.IsCopyLive)
                        lf.Landblock.EnqueueAction(new ActionEventDelegate(() => lf.Fight.Reset("replaced by a new entry")));

                    return CreateCopy(player, knot.Guid.Full, knot, knotLevel, def, adminEntry: false, null, out refusal);

                case PinnacleEntryAction.ReuseArrival:
                    return Reenter(player, lf, lf.Fight.Script.Data.ArrivalPoint, arm: false, out refusal);

                case PinnacleEntryAction.ResumeHeld:
                    if (!Reenter(player, lf, lf.Fight.Script.Data.FightEntryPoint, arm: true, out refusal))
                        return false;

                    // R1: every return into a running or held fight spends one entry (the seam refused the
                    // use already when there was none left to spend)
                    var left = KnotEncounterEvents.SpendEntry(player, knot.Guid.Full);
                    log.Info($"[PINNACLE] {player.Name} returned to their {lf.Fight.State} attempt; entry spent, {left} left");
                    return true;

                case PinnacleEntryAction.ReturnToCollect:
                    // back to a won fight (its cache owed or being collected): always free (owner ruling 2026-10-19); armed
                    // so a collecting fight does not close while they travel
                    return Reenter(player, lf, lf.Fight.Script.Data.FightEntryPoint, arm: true, out refusal);

                default:
                    return false;
            }
        }

        /// <summary>
        /// True when the Knot's stored entry count is zero or less. A Knot with no Structure yet holds its full
        /// definition count (the seam reads it the same way), so a missing value is never "none left". Pure.
        /// </summary>
        internal static bool NoEntriesLeft(int? structure) => structure.HasValue && structure.Value <= 0;

        // ================================================================ fresh copy

        /// <summary>
        /// The world-DB rows a fresh copy needs before anything is created or charged: the head, the Pinnacle
        /// Cache and every wcid the script lists in RequiredWcids. Returns the first missing wcid, or 0 when every one resolves. Pure over <paramref name="exists"/>.
        /// </summary>
        internal static uint FirstMissingWeenie(uint headWcid, Func<uint, bool> exists, System.Collections.Generic.IReadOnlyList<uint> required = null)
        {
            if (headWcid != 0 && !exists(headWcid))
                return headWcid;

            if (!exists(KnotItems.PinnacleCacheWcid))
                return KnotItems.PinnacleCacheWcid;

            if (required != null)
                foreach (var wcid in required)
                    if (wcid != 0 && !exists(wcid))
                        return wcid;

            return 0;
        }

        // ================================================================ admin entry

        /// <summary>
        /// The pure routing of an admin (playtest) entry: refused inside an instance, else the ordinary
        /// <see cref="PinnacleEntryRules.Decide"/> with "same Knot" meaning "this fight was itself an admin entry
        /// (KnotGuid 0) of the SAME kind". A Knot-bound fight, or an admin fight of another kind, therefore routes as
        /// "another trial": NotStarted -> CreateCopy (the caller resets the uncharged old one), live -> Refuse.
        /// </summary>
        internal static PinnacleEntryAction DecideAdmin(bool insideInstance, bool fightExists, PinnacleFightState state, bool awaiting, bool collecting,
            bool copyLive, uint fightKnotGuid, bool sameKind, out string refusal)
        {
            if (insideInstance)
            {
                refusal = AdminInsideInstanceLine;
                return PinnacleEntryAction.Refuse;
            }

            return PinnacleEntryRules.Decide(fightExists, state, awaiting, collecting, copyLive, fightKnotGuid == 0 && sameKind, out refusal);
        }

        /// <summary>
        /// Admin entry for /pinnacle enter: opens a fresh admin copy of <paramref name="kind"/> at <paramref name="level"/>,
        /// or returns the admin to their held admin copy. Ignores the row's Enabled flag and the PinnacleTunables kill
        /// switch. Never charges a Knot (knotGuid 0: the Knot calls are logged no-ops), never spends an entry on a
        /// return, and the fight is marked so its win records no clear. Must run on the admin's own thread
        /// (admin.EnqueueAction), like the Knot seam.
        /// </summary>
        internal static bool TryEnterAdmin(Player admin, string kind, int level, out string refusal)
            => TryEnterAdmin(admin, kind, level, null, out refusal);

        /// <summary>
        /// As above, with per-copy tuning overrides (plan T9): "key=value" settings that apply to the FRESH copy only.
        /// A refused override (unknown key, unparsable value, a kind with no overrides) is reported before anything is
        /// created, and overrides asked of an entry that would only return to a held copy are refused, not ignored.
        /// </summary>
        internal static bool TryEnterAdmin(Player admin, string kind, int level, HazardArgs overrides, out string refusal)
        {
            refusal = null;

            if (admin == null || admin.Location == null)
            {
                refusal = NotReadyLine;
                return false;
            }

            if (!PinnacleEncounters.TryGet(kind, out var impl) || !(impl is PinnacleEncounter encounter))
            {
                refusal = $"No pinnacle kind '{kind}' is registered.";
                return false;
            }

            var def = ThreadDungeonManager.Store?.GetPinnacle(kind);

            if (def == null)
            {
                refusal = $"No pinnacles.json row with key '{kind}'.";
                return false;
            }

            // Overrides are validated before any state is read or created: a bad key means no copy.
            IPinnacleBossScript script = null;

            if (overrides != null && overrides.Count > 0)
            {
                if (!string.Equals(kind, Matron.MatronData.Kind, StringComparison.Ordinal))
                {
                    refusal = $"Per-copy overrides are only supported for {Matron.MatronData.Kind}.";
                    return false;
                }

                if (!Matron.MatronOverrides.TryCreateScript(PinnacleTunables.Matron(), overrides, out var tuned, out var overrideError))
                {
                    refusal = overrideError;
                    return false;
                }

                script = tuned;
            }

            PinnacleFightManager.TryGetFight(admin.Guid.Full, out var lf);

            var exists = lf != null && !lf.Fight.IsEnded;
            var action = DecideAdmin(admin.Location.IsEphemeralRealm,
                exists,
                exists ? lf.Fight.State : PinnacleFightState.NotStarted,
                exists && lf.Fight.AwaitingDelivery,
                exists && lf.Fight.Collecting,
                exists && lf.IsCopyLive,
                exists ? lf.Fight.KnotGuid : 0u,
                exists && string.Equals(lf.Fight.Script?.Kind, kind, StringComparison.Ordinal),
                out refusal);

            if (script != null && action != PinnacleEntryAction.CreateCopy && action != PinnacleEntryAction.Refuse)
            {
                refusal = "You already have an admin copy open; overrides apply only to a fresh one. Use /pinnacle reset first.";
                return false;
            }

            switch (action)
            {
                case PinnacleEntryAction.CreateCopy:
                    if (exists && lf.Fight.State == PinnacleFightState.NotStarted && lf.IsCopyLive)
                        lf.Landblock.EnqueueAction(new ActionEventDelegate(() => lf.Fight.Reset("replaced by a new entry")));

                    return encounter.CreateCopy(admin, 0u, null, level, def, adminEntry: true, script, out refusal);

                case PinnacleEntryAction.ReuseArrival:
                    return Reenter(admin, lf, lf.Fight.Script.Data.ArrivalPoint, arm: false, out refusal);

                case PinnacleEntryAction.ResumeHeld:
                case PinnacleEntryAction.ReturnToCollect:
                    // a return costs nothing: an admin entry has no Knot
                    return Reenter(admin, lf, lf.Fight.Script.Data.FightEntryPoint, arm: true, out refusal);

                default:
                    return false;
            }
        }

        private bool CreateCopy(Player player, uint knotGuid, WorldObject knotOrNull, int knotLevel, PinnacleDef def, bool adminEntry, IPinnacleBossScript scriptOverride, out string refusal)
        {
            refusal = null;

            var rewardTable = def?.RewardTable;

            var script = scriptOverride ?? PinnacleFightManager.CreateScript(Kind);

            if (script?.Data == null)
            {
                refusal = NotReadyLine;
                return false;
            }

            var data = script.Data;

            // Readiness (C3): a world without the head or the cache weenie would take the Knot and then fail in the copy.
            // Refused here with nothing created and nothing charged.
            var missing = FirstMissingWeenie(data.HeadWcid, wcid => DatabaseManager.World.GetCachedWeenie(wcid) != null, data.RequiredWcids);

            if (missing != 0)
            {
                log.Error($"[PINNACLE] wcid {missing} is not in the world database; refusing a '{Kind}' entry for {player.Name}");
                refusal = NotReadyLine;
                return false;
            }

            // Player thread, under the scaling gate: the band memo and its DB reads are built here, not on the fight tick.
            PinnacleScaling.Warm(knotLevel);

            // Realm 0 always: the copy defaults to the owner's HOME realm, and a realm-1 character would get an empty one.
            var landblock = RealmManager.GetNewEphemeralLandblock(new LandblockId(((uint)data.Landblock << 16) | 0xFFFF), player, realmId: 0, openToFellowship: false, excludedRows: data.ExcludeRows);

            if (landblock == null)
            {
                refusal = NotReadyLine;
                return false;
            }

            if (!landblock.IsDungeon)
            {
                log.Error($"[PINNACLE] landblock 0x{data.Landblock:X4} is not a dungeon; refusing entry for {player.Name}");
                LandblockManager.AddToDestructionQueue(landblock);
                refusal = NotReadyLine;
                return false;
            }

            var arrival = ToPosition(data.ArrivalPoint, landblock.Instance);

            // Prove the copy resolves with the check the teleport will run, BEFORE any side effect; refuse rather than
            // send the player to the shared-world copy of the landblock.
            arrival.ValidateInstanceDestination(player, out var rejection);

            if (rejection != InstanceRejection.None)
            {
                log.Error($"[PINNACLE] the instance 0x{landblock.Instance:X8} just created does not validate ({rejection}); refusing entry for {player.Name}");
                LandblockManager.AddToDestructionQueue(landblock);
                refusal = NotReadyLine;
                return false;
            }

            WorldObject.AdjustDungeon(arrival);

            // Who opened the copy and when, for the attempt's telemetry rows; one id per copy.
            var attempt = new PinnacleAttemptContext(def?.Key, player.Name, player.Account?.AccountId ?? 0, player.Level, landblock.Instance);

            var world = new LivePinnacleWorld(landblock, landblock.Instance, player.Guid.Full, knotGuid, knotLevel, data.HazardRegionPreset, data.HeadFaceYawOffset, data.HeadCastHeight, rewardTable, attempt,
                data.CachePoint, data.ReturnPortalPoint, data.PlateFloor);
            var fight = new PinnacleFight(script, world, PinnacleTunables.FightConfig(), player.Guid.Full, knotGuid, landblock.Instance,
                knotLevel: knotLevel, seed: ThreadSafeRandom.Next(1, int.MaxValue - 1));

            if (adminEntry)
                fight.MarkAdminEntry();

            world.Bind(fight);

            // Registered (and the hold attached, head spawning on the first tick) before the teleport.
            PinnacleFightManager.Add(new LiveFight(fight, world, landblock));

            if (knotOrNull != null)
                KnotTelemetry.RecordCopyOpen(player, knotOrNull, knotLevel, def, attempt.AttemptId, landblock.Instance);

            Teleport(player, arrival);
            return true;
        }

        // ================================================================ re-entry

        /// <summary>
        /// Walks the owner back into the fight's copy. A relogged owner is a NEW Player object and fails
        /// EphemeralRealm.Accepts (it matches by reference), so the owner is re-admitted by guid before the teleport.
        /// </summary>
        private static bool Reenter(Player player, LiveFight lf, PinnaclePoint to, bool arm, out string refusal)
        {
            refusal = null;

            var realm = LandblockManager.GetEphemeralLandblock(lf.Fight.Instance)?.InnerRealmInfo;

            if (realm == null)
            {
                refusal = ClosedLine;
                return false;
            }

            if (!realm.Accepts(player))
            {
                if (lf.Fight.OwnerGuid != player.Guid.Full)
                {
                    refusal = NotYoursLine;
                    return false;
                }

                realm.Admit(player);
                log.Info($"[PINNACLE] re-admitted owner {player.Name} (0x{player.Guid.Full:X8}) after a stale Owner reference");
            }

            // The resume is armed in the teleport FOLLOW-UP, which WorldManager.ThreadSafeTeleport queues on the world
            // thread right after player.Teleport() starts the move: NOT on arrival (the owner is still in portal space).
            // That is safe because a Held fight only resumes once the owner is present for the grace, and nothing clears
            // the flag on a Held fight. A fight still Running at the Knot use (the owner outside for under a tick) holds
            // on its next tick for "owner absent", and that hold KEEPS the arm, so the owner's arrival still resumes it.
            Teleport(player, ToPosition(to, lf.Fight.Instance), () =>
            {
                if (arm)
                    lf.Fight.ArmResume();
            });
            return true;
        }

        private static Position ToPosition(PinnaclePoint p, uint instance)
            => new Position(p.Cell, p.X, p.Y, p.Z, p.QX, p.QY, p.QZ, p.QW, instance);

        private static void Teleport(Player player, Position to, Action onArrival = null)
        {
            player.SetPosition(PositionType.EphemeralRealmExitTo, new Position(player.Location));
            WorldManager.ThreadSafeTeleport(player, to, new ActionEventDelegate(() =>
            {
                player.SendWeenieError(WeenieError.ITeleported);

                // Owner ruling 2026-10-22: rare gems are disabled in Knot fights - stripped on entry (this follow-up
                // covers first entry and re-entry), refused inside (Gem.ActOnUse / UseGem).
                // Deliberately NOT gated on the player's instance having changed: a fog-delayed teleport (Player.Teleport
                // re-queues itself after clearing fog) runs this follow-up before the move, and a gate would skip the strip
                // for it entirely. The destination is validated before the teleport (ValidateInstanceDestination in Enter),
                // so a refused move is rare, and stripping then costs only buffs the player chose to give up by using the
                // Knot. The line is sent only when something was removed (the pre-check can false-positive on an item's
                // innate Duration -1 aura).
                if (player.HasAnyStrippableRareGemBuff(true) && player.StripRareGemBuffs(true))
                {
                    player.Session?.Network.EnqueueSend(new ACE.Server.Network.GameMessages.Messages.GameMessageSystemChat(PinnacleFightManager.RareGemsStrippedLine, ChatMessageType.Broadcast));
                }

                onArrival?.Invoke();
            }), true);
        }
    }
}
