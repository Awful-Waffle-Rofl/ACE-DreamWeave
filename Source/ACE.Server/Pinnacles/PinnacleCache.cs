using System;
using System.Collections.Generic;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Knots;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using log4net;

using Position = ACE.Entity.Position;

namespace ACE.Server.Pinnacles
{
    /// <summary>
    /// Forms the won fight's cache at the script's fixed point (<see cref="TryPlaceAt"/>), or else beside the owner: the Thread cache's candidate chain
    /// (<see cref="ThreadCachePlacement.BuildCandidates"/>, no curated points, no exclusions) and its landing checks
    /// (<c>ThreadCachePlacer.TryPlaceEmpty</c> minus the run: same landblock, direct visibility, resting height within
    /// <see cref="ThreadCachePlacement.MaxZDelta"/>), through the run-free cache factory. Runs on the fight's landblock thread.
    /// </summary>
    public static class PinnacleCache
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public const string AnnounceLine = "A Pinnacle Cache has formed beside you.";

        /// <summary>
        /// True once a cache is standing and filled from <paramref name="rewards"/> (empty is fine). False when no
        /// candidate accepted a chest: nothing is left behind and the caller retries on a later tick. Items the chest
        /// could not take are destroyed here. <paramref name="logFailure"/> false silences the placement-failure warning
        /// (the caller retries every tick and logs the first failure only).
        /// </summary>
        public static bool TryPlace(Player owner, Landblock landblock, uint instance, IReadOnlyList<WorldObject> rewards, bool logFailure = true)
        {
            if (owner?.Location == null || landblock == null)
                return false;

            var at = owner.Location;
            var candidates = ThreadCachePlacement.BuildCandidates(at.Cell, at.PositionX, at.PositionY, at.PositionZ, at.RotationZ, at.RotationW,
                null, Array.Empty<(float X, float Y, float Z)>());

            foreach (var candidate in candidates)
            {
                var position = new Position(candidate.Cell, candidate.X, candidate.Y, candidate.Z, 0f, 0f, candidate.QZ, candidate.QW, instance);

                if (candidate.RecomputeCell)
                    position.LandblockId = new LandblockId(position.GetCell());

                if (position.LandblockId.Landblock != landblock.Id.Landblock || !owner.IsDirectVisible(position))
                    continue;

                var chest = TryPlaceEmpty(owner, landblock, position, owner.Location.PositionZ);

                if (chest == null)
                    continue;

                Formed(owner, chest, rewards, AnnounceLine);
                return true;
            }

            if (logFailure)
                log.Warn($"[PINNACLE] no cache placement accepted for {owner.Name}; {candidates.Count} candidate(s) tried");
            return false;
        }

        public const string AnnounceAtPointLine = "A Pinnacle Cache has formed where the head fell.";

        /// <summary>
        /// <see cref="TryPlace"/> at one fixed point the script chose (PinnacleScriptData.CachePoint) instead of beside the
        /// owner: no candidate chain and no line-of-sight check (the owner may be anywhere in the copy); the landing height
        /// is checked against the point. Same contract otherwise.
        /// </summary>
        public static bool TryPlaceAt(Player owner, Landblock landblock, Position at, IReadOnlyList<WorldObject> rewards, bool logFailure = true)
        {
            if (owner == null || landblock == null || at == null)
                return false;

            var chest = TryPlaceEmpty(owner, landblock, at, at.PositionZ);

            if (chest == null)
            {
                if (logFailure)
                    log.Warn($"[PINNACLE] the cache could not be placed for {owner.Name} at {at.ToLOCString()}");
                return false;
            }

            Formed(owner, chest, rewards, AnnounceAtPointLine);
            return true;
        }

        private static void Formed(Player owner, Chest chest, IReadOnlyList<WorldObject> rewards, string announce)
        {
            Fill(chest, rewards);
            chest.ApplyVisualEffects(PlayScript.Create);
            owner.Session?.Network.EnqueueSend(new GameMessageSystemChat(announce, ChatMessageType.Broadcast));
            log.Info($"[PINNACLE] cache 0x{chest.Guid.Full:X8} formed for {owner.Name} at {chest.Location.ToLOCString()} holding {chest.Inventory.Count} item(s)");
        }

        /// <summary>The chest entered at <paramref name="position"/>, or null (nothing left behind) when it did not land within <see cref="ThreadCachePlacement.MaxZDelta"/> of <paramref name="referenceZ"/> on this landblock.</summary>
        private static Chest TryPlaceEmpty(Player owner, Landblock landblock, Position position, float referenceZ)
        {
            if (position.LandblockId.Landblock != landblock.Id.Landblock)
                return null;

            var chest = ThreadDungeonRewardSpawner.CreateCacheChest(owner.Guid.Full, owner.Name, KnotItems.PinnacleCacheWcid, null, "pinnacle cache");

            if (chest == null)
                return null;

            chest.Location = position;

            // Walk-through, like a pooled Thread cache, so it can never block the way out; a rejected attempt must not flash.
            chest.Ethereal = true;
            chest.SuppressGenerateEffect = true;

            if (!chest.EnterWorld())
            {
                chest.Destroy();
                return null;
            }

            var landed = chest.Location;

            if (landed == null || landed.LandblockId.Landblock != landblock.Id.Landblock
                || Math.Abs(landed.PositionZ - referenceZ) > ThreadCachePlacement.MaxZDelta)
            {
                chest.Destroy();
                return null;
            }

            return chest;
        }

        private static void Fill(Chest chest, IReadOnlyList<WorldObject> rewards)
        {
            if (rewards == null)
                return;

            var next = 0;

            foreach (var item in rewards)
            {
                if (item == null)
                    continue;

                if (chest.TryAddToInventory(item, next))
                    next++;
                else
                {
                    log.Warn($"[PINNACLE] cache 0x{chest.Guid.Full:X8} could not take {item.Name}; destroyed");
                    item.Destroy();
                }
            }
        }
    }
}
