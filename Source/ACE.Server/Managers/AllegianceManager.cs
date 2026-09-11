using System;
using System.Collections.Generic;

using ACE.Database;
using ACE.Entity;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.Managers
{
    /// <summary>
    /// Allegiance helper methods
    /// </summary>
    public class AllegianceManager
    {
        /// <summary>
        /// A mapping of all loaded Allegiance GUIDs => their Allegiances
        /// </summary>
        public static readonly Dictionary<ObjectGuid, Allegiance> Allegiances = new Dictionary<ObjectGuid, Allegiance>();

        /// <summary>
        /// A mapping of all Players on the server => their AllegianceNodes
        /// </summary>
        public static readonly Dictionary<ObjectGuid, AllegianceNode> Players = new Dictionary<ObjectGuid, AllegianceNode>();

        /// <summary>
        /// Returns the monarch for a player
        /// </summary>
        public static IPlayer GetMonarch(IPlayer player)
        {
            if (player.MonarchId == null)
                return player;

            var monarch = PlayerManager.FindByGuid(player.MonarchId.Value);

            return monarch ?? player;
        }

        /// <summary>
        /// Returns the full allegiance structure for any player
        /// </summary>
        /// <param name="player">A player at any level of an allegiance</param>
        public static Allegiance GetAllegiance(IPlayer player)
        {
            if (player == null) return null;

            var monarch = GetMonarch(player);

            if (monarch == null) return null;

            // is this allegiance already loaded / cached?
            if (Players.ContainsKey(monarch.Guid))
                return Players[monarch.Guid].Allegiance;

            // try to load biota
            var allegianceID = DatabaseManager.Shard.BaseDatabase.GetAllegianceID(monarch.Guid.Full);
            var biota = allegianceID != null ? DatabaseManager.Shard.BaseDatabase.GetBiota(allegianceID.Value) : null;

            Allegiance allegiance;

            if (biota != null)
            {
                var entityBiota = ACE.Database.Adapter.BiotaConverter.ConvertToEntityBiota(biota);

                allegiance = new Allegiance(entityBiota);
            }
            else
                allegiance = new Allegiance(monarch.Guid);

            if (allegiance.TotalMembers == 1)
                return null;

            if (biota == null)
            {
                allegiance = WorldObjectFactory.CreateNewWorldObject("allegiance") as Allegiance;
                allegiance.MonarchId = monarch.Guid.Full;
                allegiance.Init(monarch.Guid);

                allegiance.SaveBiotaToDatabase();
            }

            AddPlayers(allegiance);

            //if (!Allegiances.ContainsKey(allegiance.Guid))
                //Allegiances.Add(allegiance.Guid, allegiance);
            Allegiances[allegiance.Guid] = allegiance;

            return allegiance;
        }

        /// <summary>
        /// Returns the AllegianceNode for a Player
        /// </summary>
        public static AllegianceNode GetAllegianceNode(IPlayer player)
        {
            Players.TryGetValue(player.Guid, out var allegianceNode);
            return allegianceNode;
        }

        /// <summary>
        /// Returns a list of all players under a monarch
        /// </summary>
        public static List<IPlayer> FindAllPlayers(ObjectGuid monarchGuid)
        {
            return PlayerManager.FindAllByMonarch(monarchGuid);
        }

        /// <summary>
        /// Loads the Allegiance and AllegianceNode for a Player
        /// </summary>
        public static void LoadPlayer(IPlayer player)
        {
            if (player == null) return;

            player.Allegiance = GetAllegiance(player);
            player.AllegianceNode = GetAllegianceNode(player);

            // TODO: update chat channels for online players here?
        }

        /// <summary>
        /// Called when a player joins/exits an Allegiance
        /// </summary>
        public static void Rebuild(Allegiance allegiance)
        {
            if (allegiance == null) return;

            RemoveCache(allegiance);

            // rebuild allegiance
            allegiance = GetAllegiance(allegiance.Monarch.Player);

            // relink players
            foreach (var member in allegiance.Members.Keys)
            {
                var player = PlayerManager.FindByGuid(member);
                if (player == null) continue;

                LoadPlayer(player);
            }

            // update dynamic properties
            allegiance.UpdateProperties();
        }

        /// <summary>
        /// Appends the Players lookup table with the members of an Allegiance
        /// </summary>
        public static void AddPlayers(Allegiance allegiance)
        {
            foreach (var member in allegiance.Members)
            {
                var player = member.Key;
                var allegianceNode = member.Value;

                if (!Players.ContainsKey(player))
                    Players.Add(player, allegianceNode);
                else
                    Players[player] = allegianceNode;
            }
        }

        /// <summary>
        /// Removes an Allegiance from the Players lookup table cache
        /// </summary>
        public static void RemoveCache(Allegiance allegiance)
        {
            foreach (var member in allegiance.Members)
                Players.Remove(member.Key);
        }

        /// <summary>
        /// DreamWeave: the maximum number of direct vassals a patron may hold.
        /// Enforced in Player_Allegiance.SwearAllegiance, and the point at which the passup
        /// curve below reaches its maximum combined rate.
        /// </summary>
        public const int MaxDirectVassals = 11;

        /// <summary>
        /// DreamWeave: default share of a vassal's earned XP passed up to their patron when that
        /// patron holds exactly one vassal. Compiled default for the allegiance_passup_first_vassal
        /// tunable.
        /// </summary>
        public const double PatronPassupRate = 0.25;

        /// <summary>
        /// DreamWeave: default combined share a patron receives across all of their vassals once
        /// they hold <see cref="MaxDirectVassals"/> of them. Compiled default for the
        /// allegiance_passup_max_total tunable.
        /// </summary>
        public const double MaxTotalPassupRate = 1.0;

        /// <summary>
        /// DreamWeave: the grandpatron receives this fraction of whatever the patron receives,
        /// which preserves the original 25% / 5% ratio at every point on the passup curve.
        /// Passup stops here - no layers beyond the grandpatron.
        /// </summary>
        public const double GrandPatronPassupShare = 0.2;

        /// <summary>
        /// DreamWeave: the share of ONE vassal's earned XP that their patron receives, given how
        /// many direct vassals that patron currently holds. Diminishing returns: the per-vassal
        /// share shrinks as vassals are added, so the patron's combined take across all of them
        /// climbs from <paramref name="firstVassalRate"/> at a single vassal to
        /// <paramref name="maxTotalRate"/> at <see cref="MaxDirectVassals"/>.
        ///
        /// Pure function so it is testable without a shard config; the tunable reads live in
        /// <see cref="GetPatronPassupRate(int)"/>.
        /// </summary>
        public static double CalculatePatronPassupRate(int vassalCount, double firstVassalRate, double maxTotalRate)
        {
            // Both endpoints are admin-set tunables, so every result below is clamped: a typo must
            // not be able to hand a patron more XP than the vassal actually earned. The ulong cast
            // in DoPassXP saturates rather than throwing, so an unclamped rate would silently mint
            // XP instead of failing loudly.
            if (double.IsNaN(firstVassalRate) || double.IsNaN(maxTotalRate))
                return 0.0;

            firstVassalRate = Math.Clamp(firstVassalRate, 0.0, 1.0);

            if (firstVassalRate <= 0.0)
                return 0.0;

            if (vassalCount <= 1)
                return firstVassalRate;

            // A combined take of MaxDirectVassals is the ceiling worth allowing: every vassal
            // passing up 100% of their XP.
            maxTotalRate = Math.Clamp(maxTotalRate, firstVassalRate, MaxDirectVassals);

            // combined take across n vassals is first * n^k, with k solved so that
            // first * MaxDirectVassals^k == maxTotal. Each individual vassal therefore
            // contributes that divided by n, ie. first * n^(k-1).
            var exponent = Math.Log(maxTotalRate / firstVassalRate) / Math.Log(MaxDirectVassals);

            return Math.Clamp(firstVassalRate * Math.Pow(vassalCount, exponent - 1.0), 0.0, 1.0);
        }

        /// <summary>
        /// DreamWeave: <see cref="CalculatePatronPassupRate"/> against the live tunables.
        /// </summary>
        public static double GetPatronPassupRate(int vassalCount)
        {
            return CalculatePatronPassupRate(vassalCount,
                GetPassupTunable("allegiance_passup_first_vassal", PatronPassupRate),
                GetPassupTunable("allegiance_passup_max_total", MaxTotalPassupRate));
        }

        /// <summary>
        /// Reads a server property, falling back to the compiled default when there is no shard
        /// config to read from. PropertyManager.GetDouble dereferences a null shard config on a
        /// cache miss, which is exactly the situation in ACE.Server.Tests; a real DB fault still
        /// propagates, like every other PropertyManager call site.
        /// </summary>
        private static double GetPassupTunable(string key, double fallback)
        {
            try
            {
                var value = PropertyManager.GetDouble(key, fallback).Item;

                return double.IsFinite(value) ? value : fallback;
            }
            catch (NullReferenceException)
            {
                return fallback;
            }
        }

        // This function can be called from multi-threaded operations
        // We must add thread safety to prevent AllegianceManager corruption
        // We must also protect against cross-thread operations on vassal/patron (non-concurrent collections)
        public static void PassXP(AllegianceNode vassalNode, ulong amount)
        {
            WorldManager.EnqueueAction(new ActionEventDelegate(() => DoPassXP(vassalNode, amount)));
        }

        private static void DoPassXP(AllegianceNode vassalNode, ulong amount)
        {
            // DreamWeave: diminishing-returns passup, replacing the retail loyalty / leadership /
            // time-sworn formula. A patron's per-vassal share shrinks as they take on more vassals,
            // so their combined take rises from 25% with one vassal to 100% at the 11-vassal cap.
            // The grandpatron receives a fifth of whatever the patron gets. No layers beyond that.

            var patronNode = vassalNode.Patron;
            if (patronNode == null)
                return;

            var vassal = vassalNode.Player;

            if (!vassal.ExistedBeforeAllegianceXpChanges)
                return;

            var passupRate = GetPatronPassupRate(patronNode.TotalVassals);

            var patronAmount = (ulong)(amount * passupRate);

            var grandPatronNode = patronNode.Patron;
            var grandPatronAmount = grandPatronNode != null ? (ulong)(amount * passupRate * GrandPatronPassupShare) : 0;

            vassal.AllegianceXPGenerated += patronAmount + grandPatronAmount;

            ReceivePassupXP(patronNode, patronAmount);

            if (grandPatronNode != null)
                ReceivePassupXP(grandPatronNode, grandPatronAmount);
        }

        private static void ReceivePassupXP(AllegianceNode recipientNode, ulong amount)
        {
            if (amount == 0)
                return;

            var recipient = recipientNode.Player;

            if (PropertyManager.GetBool("offline_xp_passup_limit").Item)
                recipient.AllegianceXPCached = Math.Min(recipient.AllegianceXPCached + amount, uint.MaxValue);
            else
                recipient.AllegianceXPCached += amount;

            var onlineRecipient = PlayerManager.GetOnlinePlayer(recipient.Guid);
            if (onlineRecipient != null)
                onlineRecipient.AddAllegianceXP();
        }

        /// <summary>
        /// Updates the Allegiance tree structure when a new player joins
        /// </summary>
        /// <param name="vassal">The vassal swearing into the Allegiance</param>
        public static void OnSwearAllegiance(Player vassal)
        {
            if (vassal == null) return;

            // was this vassal previously a Monarch?
            if (vassal.Allegiance != null)
                RemoveCache(vassal.Allegiance);

            // rebuild the new combined structure
            var allegiance = GetAllegiance(vassal);
            Rebuild(allegiance);

            LoadPlayer(vassal);

            // maintain approved vassals list
            if (allegiance != null && allegiance.HasApprovedVassal(vassal.Guid.Full))
                allegiance.RemoveApprovedVassal(vassal.Guid.Full);
        }

        /// <summary>
        /// Updates the Allegiance tree structure when a member leaves
        /// </summary>
        /// <param name="self">The player initiating the break request</param>
        /// <param name="target">The patron or vassal of the self player</param>
        public static void OnBreakAllegiance(IPlayer self, IPlayer target)
        {
            // remove the previous allegiance structure
            if (self != null)   // ??
                RemoveCache(self.Allegiance);

            // rebuild for self and target
            var selfAllegiance = GetAllegiance(self);
            var targetAllegiance = GetAllegiance(target);

            Rebuild(selfAllegiance);
            Rebuild(targetAllegiance);

            LoadPlayer(self);
            LoadPlayer(target);

            HandleNoAllegiance(self);
            HandleNoAllegiance(target);
        }

        public static void HandleNoAllegiance(IPlayer player)
        {
            if (player == null || player.Allegiance != null)
                return;

            var onlinePlayer = PlayerManager.GetOnlinePlayer(player.Guid);

            var updated = false;

            if (player.MonarchId != null)
            {
                player.UpdateProperty(PropertyInstanceId.Monarch, null, true);

                updated = true;
            }

            if (player.AllegianceRank != null)
            {
                player.AllegianceRank = null;

                if (onlinePlayer != null)
                    onlinePlayer.Session.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyInt(onlinePlayer, PropertyInt.AllegianceRank, 0));

                updated = true;
            }

            if (updated)
                player.SaveBiotaToDatabase();

            if (onlinePlayer != null)
                onlinePlayer.Session.Network.EnqueueSend(new GameEventAllegianceUpdate(onlinePlayer.Session, onlinePlayer.Allegiance, onlinePlayer.AllegianceNode), new GameEventAllegianceAllegianceUpdateDone(onlinePlayer.Session));
        }

        public static Allegiance FindAllegiance(uint allegianceID)
        {
            Allegiances.TryGetValue(new ObjectGuid(allegianceID), out var allegiance);
            return allegiance;
        }

        // This function is called from a database callback.
        // We must add thread safety to prevent AllegianceManager corruption
        public static void HandlePlayerDelete(uint playerGuid)
        {
            WorldManager.EnqueueAction(new ActionEventDelegate(() => DoHandlePlayerDelete(playerGuid)));
        }

        internal static void DoHandlePlayerDelete(uint playerGuid)
        {
            var player = PlayerManager.FindByGuid(playerGuid);
            if (player == null)
            {
                Console.WriteLine($"AllegianceManager.HandlePlayerDelete({playerGuid:X8}): couldn't find player guid");
                return;
            }
            var allegiance = GetAllegiance(player);

            if (allegiance == null) return;

            allegiance.Members.TryGetValue(player.Guid, out var allegianceNode);

            var players = new List<IPlayer>() { player };

            if (player.PatronId != null)
            {
                var patron = PlayerManager.FindByGuid(player.PatronId.Value);

                if (patron != null)
                    players.Add(patron);
            }

            player.PatronId = null;
            player.UpdateProperty(PropertyInstanceId.Monarch, null, true);

            // vassals now become monarchs...
            foreach (var vassalNode in allegianceNode.Vassals.Values)
            {
                var vassal = PlayerManager.FindByGuid(vassalNode.PlayerGuid);

                if (vassal == null) continue;

                vassal.PatronId = null;
                vassal.UpdateProperty(PropertyInstanceId.Monarch, null, true);

                // walk the allegiance tree from this node, update monarch ids
                vassalNode.Walk((node) =>
                {
                    node.Player.UpdateProperty(PropertyInstanceId.Monarch, vassalNode.PlayerGuid.Full, true);

                    node.Player.SaveBiotaToDatabase();

                }, false);

                players.Add(vassal);
            }

            RemoveCache(allegiance);

            // rebuild for those directly involved
            foreach (var p in players)
                Rebuild(GetAllegiance(p));

            foreach (var p in players)
                LoadPlayer(p);

            foreach (var p in players)
                HandleNoAllegiance(p);

            // save immediately?
            foreach (var p in players)
                p.SaveBiotaToDatabase();

            foreach (var p in players)
            {
                Player.CheckAllegianceHouse(p.Guid);

                var newAllegiance = GetAllegiance(p);
                if (newAllegiance != null)
                    newAllegiance.Monarch.Walk((node) => Player.CheckAllegianceHouse(node.PlayerGuid), false);
            }
        }
    }
}
