using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;

using ACE.Common;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldEvents;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.Frays
{
    /// <summary>
    /// What a whole Fray shares (version 1): the Great Snarl (the Fray's combined burn fills one bar; when it is full a
    /// Great Snarl walks into a central room and everyone who hits it gets a cache; every fire holder is told), the weekly
    /// fellowship goal (the Fray's combined burn for the week; once met, every camper of that week gets a Vault Key),
    /// matching flame colours (fires lit together share a coloured sparkle), and each Fray's best combined Heat this week.
    /// None of it changes anyone's XP, lum or Ember rate.
    /// </summary>
    public static class FraysFrayWide
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(FraysFrayWide));

        private sealed class FrayState
        {
            public double GreatBar;
            public Creature GreatSnarl;
            public double WeekBurn;
            public int BestCombinedHeat;
            public bool GoalAnnounced;
            public readonly HashSet<uint> Campers = new HashSet<uint>();

            /// <summary>Who fed the Great Snarl's bar this fill (not saved: a restart starts the list again).</summary>
            public readonly Dictionary<uint, (string Name, double Work)> BarBy = new Dictionary<uint, (string, double)>();
            /// <summary>The bar is full and the Great Snarl walks in at GreatDueAt, near this fire (null: none due).</summary>
            public FraysFire GreatDueFire;
            public double GreatDueAt;
            public bool GreatHalfTold;
            /// <summary>The last full bar's feeders and their shares, for the Great Snarl's caches (feeders who never struck it still get one).</summary>
            public Dictionary<uint, double> Feeders = new Dictionary<uint, double>();
            /// <summary>The Great Snarl's landblock, held loaded from the countdown till it dies (LoadGreatSpot).</summary>
            public Landblock GreatPinned;
            /// <summary>When the last Great Snarl was called (unix seconds; saved), for great_snarl_gap_hours.</summary>
            public double LastGreatAt;
            /// <summary>The bar is full but the gap is not over: the Great Snarl is called near this fire when it is (Tick).</summary>
            public FraysFire GreatWaitFire;
            /// <summary>Camps whose owners went to the Great Snarl are held till then (GreatHoldsCamps).</summary>
            public double GreatHoldUntil;
            /// <summary>When the Great Snarl walked in (unix seconds), for great_timeout_minutes.</summary>
            public double GreatSpawnedAt;
        }

        private static readonly Dictionary<ushort, FrayState> frays = new Dictionary<ushort, FrayState>();
        private static int stateWeek = int.MinValue;
        private static bool loaded;
        private static double nextSave;

        /// <summary>Where each Fray's Great Snarl walks in: a big room with no campfire.</summary>
        public static readonly Dictionary<ushort, (ushort Landblock, ushort Cell, Vector3 Pos)> GreatSnarlSpots = new Dictionary<ushort, (ushort, ushort, Vector3)>
        {
            // Vissidal: flat landblocks with no campfire, between each Fray's zones (outdoors: put on the terrain at spawn).
            // The Coast's Great Snarl walks in by the inland pond, central to the Coast camps (was
            // 0xD2DF on the east side). The pond's middle (0xCEE0 96, 96) is water, so it is the nearest flat dry shore:
            // 0xCEE1 168, 8, about 120 m from the /fray great arrival and 154 m from vc12's zone centre.
            [FraysDefs.VissidalCoast] = (0xCEE1, 0x39, new Vector3(168f, 8f, 0f)),
            [FraysDefs.VissidalHeights] = (0xD5DF, 0x25, new Vector3(108f, 108f, 0f)),
            // The Sky Fray: the walkway between the middle square and the south arm, outside every camp (the middle square
            // holds a camp). A set height: the plateau floor, not the terrain far below.
            [FraysDefs.VissidalSky] = (FraysDefs.VissidalSky, 0x24, new Vector3(108f, 72f, FraysDefs.PlateauFloor)),
        };

        /// <summary>
        /// Where /fray great lands a camper, when the Fray has a set spot (else 12 m off the Great Snarl or its spawn spot).
        /// The Coast's is a hand-picked spot by the vc12 camp (0xCFE10100, /loc), with a hand-picked facing.
        /// </summary>
        public static readonly Dictionary<ushort, (uint Cell, Vector3 Pos, Quaternion Facing)> GreatArrivals = new Dictionary<ushort, (uint, Vector3, Quaternion)>
        {
            [FraysDefs.VissidalCoast] = (0xCFE10100, new Vector3(81.927391f, 58.012199f, 2.271926f), new Quaternion(0f, 0f, -0.919651f, -0.392736f)),
        };

        /// <summary>
        /// There is one Great Snarl, not one a Fray. Every Fray's burn fills its one bar, it walks in at the Coast's spot by the inland pond, and its
        /// state lives under this key. So does the weekly goal: every camp together.
        /// </summary>
        public const ushort GreatKey = FraysDefs.VissidalCoast;

        private static FrayState Great => State(GreatKey);

        /// <summary>The landblock copy the Great Snarl walks into: the Coast's (the open world), else the caller's.</summary>
        private static uint GreatInstance(uint fallback) =>
            FraysManager.Fires.FirstOrDefault(f => f.Def.FrayKey == GreatKey)?.Instance ?? fallback;

        private static FrayState State(ushort landblock)
        {
            lock (frays)
            {
                if (!loaded)
                {
                    loaded = true;
                    Load();
                }

                if (stateWeek != FraysWeek.Current)
                {
                    stateWeek = FraysWeek.Current;

                    foreach (var s in frays.Values)
                    {
                        s.WeekBurn = 0;
                        s.BestCombinedHeat = 0;
                        s.GoalAnnounced = false;
                        s.Campers.Clear();
                    }
                }

                if (!frays.TryGetValue(landblock, out var st))
                    frays[landblock] = st = new FrayState();

                return st;
            }
        }

        // ---- reports in --------------------------------------------------------------------------------------

        /// <summary>Health removed at a fire (in reference creatures): the Great Snarl's bar and the weekly goal.</summary>
        public static void AddBurn(FraysFire fire, double work, IEnumerable<(uint Guid, string Name, double Work)> by = null)
        {
            if (fire == null || work <= 0)
                return;

            var week = Great;   // one weekly goal for every camp together
            var st = Great;
            var need = GreatNeed(GreatKey);
            var spawn = false;
            var goalMet = false;
            string stirLine = null;

            lock (frays)
            {
                week.WeekBurn += work;

                if (!week.GoalAnnounced && week.WeekBurn >= FraysSettings.Number(FraysSettings.FrayGoalWork))
                {
                    week.GoalAnnounced = true;
                    goalMet = true;
                }

                if (st.GreatSnarl != null && (st.GreatSnarl.IsDestroyed || st.GreatSnarl.IsDead))
                    st.GreatSnarl = null;

                // One bar the whole Fray fills, sized to how many camp now; told to the Fray every 10%, with who
                // is feeding it most. Full, it walks in after great_snarl_warn_seconds (see Tick).
                if (st.GreatSnarl == null && st.GreatDueFire == null)
                {
                    var before = st.GreatBar / need;
                    // a full bar waiting out the gap stays full; who feeds it still counts for the cache
                    st.GreatBar = st.GreatWaitFire != null ? need : st.GreatBar + work;
                    var after = st.GreatBar / need;

                    if (by != null)
                        foreach (var (guid, name, w) in by)
                        {
                            st.BarBy.TryGetValue(guid, out var had);
                            st.BarBy[guid] = (name, had.Work + w);
                        }

                    if (st.GreatWaitFire != null)
                    {
                        // waiting: Tick calls it when great_snarl_gap_hours is over
                    }
                    else if (after >= 1)
                    {
                        var left = FraysRules.GreatGapLeft(st.LastGreatAt, FraysSettings.Number(FraysSettings.GreatSnarlGapHours), ACE.Common.Time.GetUnixTime());

                        if (left > 0)
                        {
                            st.GreatBar = need;
                            st.GreatWaitFire = fire;
                            stirLine = $"The Great Snarl's bar is full. It can come again in {Math.Ceiling(left / 60):0} minutes{TopFeeders(st, 2)}";
                        }
                        else
                        {
                            st.GreatBar = 0;
                            spawn = true;
                        }
                    }
                    else if (FraysRules.BarStep(after, 10) > FraysRules.BarStep(before, 10))
                        stirLine = $"The Great Snarl stirs {FraysRules.Bar(after)}{TopFeeders(st, 2)}";
                }
            }

            if (stirLine != null)
                FraysManager.Announce(fire, stirLine, everyone: true);

            if (goalMet)
            {
                FraysManager.Feed(fire, "The Frays' weekly goal is met! Every camper of this week is paid: Vault Keys, a cache, experience and luminance (anyone who camps later this week is paid too).", everyone: true);
                PayGoal();
            }

            if (spawn)
                CallGreatSnarl(fire);
        }

        /// <summary>
        /// The Great Snarl's bar size now: great_snarl_work plus great_snarl_work_per_camper per camper. There is one Great
        /// Snarl for every Fray, so every camper in every Fray counts; the landblock is kept for callers.
        /// </summary>
        public static double GreatNeed(ushort landblock)
        {
            var campers = FraysManager.Fires.Sum(f => f.CampersSnapshot().Count);
            return FraysRules.GreatSnarlNeed(FraysSettings.Number(FraysSettings.GreatSnarlWork),
                FraysSettings.Number(FraysSettings.GreatSnarlWorkPerCamper), campers);
        }

        /// <summary>" - most from Ann 40%, Bob 30%." for the bar's top feeders this fill (empty when none). Caller holds the lock.</summary>
        private static string TopFeeders(FrayState st, int count)
        {
            var total = st.BarBy.Values.Sum(v => v.Work);

            if (total <= 0)
                return "";

            return " - most from " + string.Join(", ", st.BarBy.Values.OrderByDescending(v => v.Work).Take(count)
                .Select(v => $"{v.Name} {Math.Round(v.Work / total * 100):0}%")) + ".";
        }

        /// <summary>The bar is full: tell the Fray, and bring the Great Snarl in when the warning runs out.</summary>
        /// <summary>Staff testing aid: calls the Fray's Great Snarl now, as if its bar had filled (warning first).</summary>
        public static string AdminGreatSnarl(FraysFire fire)
        {
            var st = Great;

            lock (frays)
            {
                if (st.GreatSnarl != null && !st.GreatSnarl.IsDestroyed && !st.GreatSnarl.IsDead)
                    return "The Great Snarl is already out.";

                if (st.GreatDueFire != null)
                    return "The Great Snarl is already on its way.";

                st.GreatBar = 0;
            }

            CallGreatSnarl(fire);
            return $"Calling the Great Snarl {GreatSpotText(GreatKey)} (it walks in after {FraysSettings.Number(FraysSettings.GreatSnarlWarnSeconds):0} s).";
        }

        private static void CallGreatSnarl(FraysFire fire)
        {
            var warn = FraysSettings.Number(FraysSettings.GreatSnarlWarnSeconds);
            var st = Great;
            string feeders;

            lock (frays)
            {
                feeders = TopFeeders(st, 3);
                st.LastGreatAt = ACE.Common.Time.GetUnixTime();   // great_snarl_gap_hours runs from here
                st.GreatWaitFire = null;
                st.GreatBar = 0;
                var fed = st.BarBy.Values.Sum(v => v.Work);
                st.Feeders = fed <= 0 ? new Dictionary<uint, double>() : st.BarBy.ToDictionary(kv => kv.Key, kv => kv.Value.Work / fed);
                st.BarBy.Clear();

                if (warn > 0)
                {
                    st.GreatDueFire = fire;
                    st.GreatDueAt = ACE.Common.Time.GetUnixTime() + warn;
                    st.GreatHalfTold = warn < 20;
                }
            }

            if (warn <= 0)
            {
                FraysManager.Feed(fire, $"The Great Snarl's bar is full{feeders}", everyone: true);
                SpawnGreatSnarl(fire);
                return;
            }

            LoadGreatSpot(fire);
            FraysManager.Announce(fire, $"The Great Snarl's bar is full{feeders} It arrives in {warn:0} seconds {GreatSpotText(GreatKey)} - gather there (/fray great takes a camper straight there).",
                $"The Great Snarl arrives in {warn:0} seconds {GreatSpotText(GreatKey)} - gather there!", everyone: true);
        }

        /// <summary>Where the Great Snarl walks in, for chat: map coordinates outdoors, "in the Fray's great room" in a dungeon.</summary>
        public static string GreatSpotText(ushort landblock)
        {
            if (!GreatSnarlSpots.TryGetValue(landblock, out var spot))
                return "in the Fray";

            if (spot.Cell >= 0x100)
                return "in the Fray's great room";

            var pos = new Position(((uint)spot.Landblock << 16) | spot.Cell, spot.Pos.X, spot.Pos.Y, spot.Pos.Z, 0, 0, 0, 1, 0);
            return "at " + FraysManager.MapCoordText(pos);
        }

        /// <summary>A player joined a camp: they count for the week's goal (and are paid at once if it is already met).</summary>
        public static void OnCamp(Player player, FraysFire fire)
        {
            var st = Great;
            bool met;

            lock (frays)
            {
                st.Campers.Add(player.Guid.Full);
                met = st.GoalAnnounced;
            }

            if (met)
                PayGoalTo(player);
        }

        private static void PayGoal()
        {
            List<uint> guids;

            lock (frays)
                guids = Great.Campers.ToList();

            foreach (var g in guids)
            {
                if (PlayerManager.GetOnlinePlayer(g) is Player p)
                    PayGoalTo(p);
            }
        }

        private static void PayGoalTo(Player player)
        {
            var rec = FraysManager.Record(player);
            rec.RollWeek(FraysWeek.Current);

            if (rec.WeekGoalPaid)
                return;

            // One key only matched a single Snarl, so the week's goal pays more: 15 keys, a 10-item cache, a Slayer Stone,
            // a Kindling and three tasks' XP and luminance.
            var keys = FraysSettings.Int(FraysSettings.FrayGoalKeys);
            var items = FraysSettings.Int(FraysSettings.FrayGoalCacheItems);
            var stones = FraysSettings.Int(FraysSettings.FrayGoalSlayerStones);
            var kindling = FraysSettings.Int(FraysSettings.FrayGoalKindling);
            var taskMult = FraysSettings.Number(FraysSettings.FrayGoalTaskMult);

            // The cache's items cannot wait at the Fray-Warden (they are rolled), so a full pack waits for the next camp.
            if (items > 0 && player.GetFreeInventorySlots() < items)
            {
                FraysManager.Tell(player, $"The Frays' weekly goal has a reward for you, but your pack needs {items} free slots for its cache. Make room and camp again.");
                return;
            }

            rec.WeekGoalPaid = true;
            FraysManager.SaveRecord(player, rec);

            var got = items > 0 ? GiveCache(player, items) : 0;

            if (FraysSettings.Toggle(FraysSettings.FrayGoalTaskReward) && taskMult > 0)
                FraysHunts.Reward(player, taskMult);

            if (keys > 0)
                FraysLongTerm.Give(player, FraysLongTerm.DreamKey, keys, null);

            if (stones > 0)
                FraysLongTerm.Give(player, FraysLongTerm.BaneStone, stones, null);

            if (kindling > 0)
                FraysLongTerm.Give(player, FraysLongTerm.Kindling, kindling, null);

            var parts = new List<string>();

            if (keys > 0)
                parts.Add(keys == 1 ? "a Vault Key" : $"{keys} Vault Keys");

            if (got > 0)
                parts.Add($"a cache of {got} item{(got == 1 ? "" : "s")} (the best one attuned)");

            if (stones > 0)
                parts.Add(stones == 1 ? "a Slayer Stone" : $"{stones} Slayer Stones");

            if (kindling > 0)
                parts.Add($"{kindling} Kindling");

            if (FraysSettings.Toggle(FraysSettings.FrayGoalTaskReward) && taskMult > 0)
                parts.Add("experience and luminance");

            if (parts.Count > 0)
                FraysManager.Tell(player, "The Frays' weekly goal pays you: " + string.Join(", ", parts) + ".");
        }

        /// <summary>
        /// A cache into <paramref name="player"/>'s pack: <paramref name="count"/> T8 magic items, the best attuned and
        /// bonded. Returns how many fit. The Great Snarl and the weekly goal both pay this.
        /// </summary>
        private static int GiveCache(Player player, int count)
        {
            if (count <= 0)
                return 0;

            var loot = LootGenerationFactory.CreateRandomLootObjects(new ACE.Database.Models.World.TreasureDeath
            {
                Tier = 8, LootQualityMod = 0, MagicItemChance = 100, MagicItemMinAmount = count, MagicItemMaxAmount = count,
                MagicItemTreasureTypeSelectionChances = 8, ItemChance = 0, MundaneItemChance = 0, UnknownChances = 19,
            }) ?? new List<WorldObject>();

            var best = loot.OrderByDescending(i => i.Value ?? 0).FirstOrDefault();

            if (best != null)
            {
                best.SetProperty(ACE.Entity.Enum.Properties.PropertyInt.Attuned, (int)AttunedStatus.Attuned);
                best.SetProperty(ACE.Entity.Enum.Properties.PropertyInt.Bonded, (int)BondedStatus.Bonded);
            }

            var got = 0;

            foreach (var item in loot)
            {
                if (player.TryCreateInInventoryWithNetworking(item))
                    got++;
                else
                    item.Destroy();
            }

            return got;
        }

        // ---- the Great Snarl ---------------------------------------------------------------------------------

        /// <summary>
        /// While a Fray's Great Snarl is due or up, and for away_seconds after it dies (the walk back), its camps never end
        /// for their owners being away, so a camp is saved while its owner goes to kill the Great Snarl.
        /// </summary>
        public static bool GreatHoldsCamps(ushort frayKey, double now)
        {
            // One Great Snarl for every Fray: while it is coming or up, every camp is held.
            lock (frays)
            {
                var st = Great;
                return st.GreatDueFire != null || (st.GreatSnarl != null && !st.GreatSnarl.IsDead && !st.GreatSnarl.IsDestroyed) || now < st.GreatHoldUntil;
            }
        }

        /// <summary>
        /// Loads the Great Snarl's landblock and holds it loaded (permaload) from the countdown until it dies, so it can walk
        /// in with no one near and stays while campers travel to it. Undone in <see cref="OnGreatSnarlDied"/>.
        /// </summary>
        private static void LoadGreatSpot(FraysFire fire)
        {
            if (!GreatSnarlSpots.TryGetValue(GreatKey, out var spot))
                return;

            try
            {
                var block = LandblockManager.GetLandblock(new LandblockId(((uint)spot.Landblock << 16) | 0xFFFF), GreatInstance(fire.Instance), false);

                if (block != null && !block.Permaload)
                {
                    block.Permaload = true;
                    lock (frays)
                        Great.GreatPinned = block;
                }
            }
            catch (Exception ex)
            {
                log.Error($"[FRAYS] loading the Great Snarl's landblock 0x{spot.Landblock:X4} threw", ex);
            }
        }

        private static void SpawnGreatSnarl(FraysFire near)
        {
            try
            {
                // One Great Snarl for every Fray, always at the Coast's spot.
                var lb = GreatKey;
                var instance = GreatInstance(near.Instance);

                if (!GreatSnarlSpots.TryGetValue(lb, out var spot))
                    return;

                LoadGreatSpot(near);

                var family = FraysFamilies.For(lb);
                var mix = FraysManager.Fires.FirstOrDefault(f => f.Def.FrayKey == lb)?.Def.Mix ?? near.Def.Mix;
                var wcid = family?.GreatSnarlWcid() ?? mix.OrderByDescending(m => FraysFire.BaseHealthOf(m.Wcid)).First().Wcid;

                if (WorldObjectFactory.CreateNewWorldObject(wcid) is not Creature c)
                    return;

                // Matched to every Fray's campers: their average level, plus their average Heat's extra levels.
                var campers = FraysManager.Fires.SelectMany(f => f.CampersSnapshot()).ToList();
                var level = campers.Count == 0 ? 300 : (int)Math.Round(campers.Average(x => (double)x.PlayerLevel));
                var heat = campers.Count == 0 ? 0 : (int)Math.Round(campers.Average(x => (double)x.Level));

                c.Location = new Position(((uint)spot.Landblock << 16) | spot.Cell, spot.Pos.X, spot.Pos.Y, spot.Pos.Z + 0.05f, 0, 0, 0, 1, instance);

                if (spot.Cell < 0x100 && spot.Pos.Z <= 0)   // outdoors on the terrain (a set height, like the plateau, stays)
                    c.Location.PositionZ = PositionExtensions.GetTerrainZ(c.Location) + 0.05f;
                c.Name = "Great Snarl " + c.Name;
                c.ObjScale = (c.ObjScale ?? 1) * (float)FraysSettings.Number(FraysSettings.GreatSnarlScale);
                c.FraysTransient = true;
                c.FraysGreatSnarl = lb;
                c.XpOverride = 0;
                c.LuminanceAward = 0;
                c.TimeToRot = -1;

                // Lifted to the campers' level and Heat, with Threads' health curve at that level times great_snarl_health.
                FraysLevelScale.Apply(c, FraysLevelScale.TargetLevel(level, heat));
                FraysLevelScale.ApplyDefenseCap(c);
                var curveHealth = FraysLevelScale.CurveHealth(FraysLevelScale.HealthLevel(c.Level ?? level, level, heat,
                    FraysSettings.Int(FraysSettings.HealthCamperLevelCap)));

                if (curveHealth > 0)
                    FraysLevelScale.SetMaxHealth(c, curveHealth * FraysSettings.Number(FraysSettings.GreatSnarlHealth));
                else
                {
                    var scaled = WorldEventSpawner.ScaledStartingValue(c.Health.StartingValue, c.Health.MaxValue, FraysSettings.Number(FraysSettings.GreatSnarlHealth));

                    if (scaled != c.Health.StartingValue)
                        c.Health.StartingValue = scaled;
                }

                c.Health.Current = c.Health.MaxValue;

                if (!c.EnterWorld())
                {
                    // Its landblock may not be loaded yet (no one near), so the spawn fails and nothing comes, though the
                    // Fray has been told it would. Try again in 5 seconds, its landblock loading meanwhile.
                    c.Destroy();
                    log.Warn($"[FRAYS] Great Snarl could not enter 0x{spot.Landblock:X4}{spot.Cell:X4}; trying again in 5 seconds");

                    lock (frays)
                    {
                        var st = State(lb);
                        st.GreatDueFire = near;
                        st.GreatDueAt = ACE.Common.Time.GetUnixTime() + 5;
                        st.GreatHalfTold = true;
                    }

                    return;
                }

                lock (frays)
                {
                    State(lb).GreatSnarl = c;
                    State(lb).GreatSpawnedAt = ACE.Common.Time.GetUnixTime();
                }

                FraysManager.Announce(near, $"The Great Snarl tears into the Fray ({c.Name}). Everyone who strikes it before it falls earns a cache.", $"The Great Snarl is here: {c.Name}!", everyone: true);
                FraysManager.Effect(c, PlayScript.SpecialStateRed);
                log.Info($"[FRAYS] Great Snarl {c.Name} in 0x{lb:X4} at level {c.Level}");
            }
            catch (Exception ex)
            {
                log.Error("[FRAYS] Great Snarl spawn threw", ex);
            }
        }

        /// <summary>The Great Snarl died: every Fray camper who damaged it gets a cache in their pack.</summary>
        /// <summary>
        /// On a Great Snarl kill a portal back to town opens. A portal (great_portal_wcid) opens where it
        /// fell for great_portal_seconds.
        /// </summary>
        /// <summary>
        /// The Great Snarl was not beaten in great_timeout_minutes: it leaves. No one is paid, its bar
        /// keeps great_leave_keep of its progress, and every camp is released (after great_leave_hold_minutes).
        /// </summary>
        private static void GreatSnarlLeaves(Creature creature, double now)
        {
            try
            {
                var lb = creature.FraysGreatSnarl;
                var keep = Math.Clamp(FraysSettings.Number(FraysSettings.GreatLeaveKeep), 0, 1);

                lock (frays)
                {
                    var st = State(lb);

                    if (st.GreatSnarl != creature)
                        return;

                    st.GreatSnarl = null;
                    st.GreatSpawnedAt = 0;
                    st.GreatHoldUntil = now + FraysSettings.Number(FraysSettings.GreatLeaveHoldMinutes) * 60;
                    st.Feeders = new Dictionary<uint, double>();

                    var great = Great;
                    great.GreatBar = Math.Max(great.GreatBar, GreatNeed(GreatKey) * keep);

                    var pinned = st.GreatPinned;
                    st.GreatPinned = null;

                    if (pinned != null && !FraysManager.Fires.Any(f => f.Lit && f.Landblock == pinned))
                        pinned.Permaload = false;
                }

                var anyFire = FraysManager.Fires.FirstOrDefault(f => f.Lit) ?? FraysManager.Fires.FirstOrDefault();

                if (anyFire != null)
                    FraysManager.Announce(anyFire, $"The Great Snarl was not beaten in time and leaves the Fray. No one is paid; its bar keeps {keep * 100:0}% for the next one.",
                        "The Great Snarl leaves, unbeaten.", everyone: true);

                creature.Destroy();
                log.Info($"[FRAYS] Great Snarl {creature.Name} left 0x{lb:X4} unbeaten");
            }
            catch (Exception ex)
            {
                log.Error("[FRAYS] Great Snarl leaving threw", ex);
            }
        }

        private static void OpenPortalHome(Creature creature)
        {
            try
            {
                var seconds = FraysSettings.Number(FraysSettings.GreatPortalSeconds);
                var wcid = (uint)FraysSettings.Int(FraysSettings.GreatPortalWcid);

                if (seconds <= 0 || wcid == 0 || creature.Location == null)
                    return;

                var portal = WorldObjectFactory.CreateNewWorldObject(wcid);

                if (portal == null)
                    return;

                portal.Location = new Position(creature.Location);
                portal.Location.PositionX += 3;
                portal.FraysTransient = true;

                if (!portal.EnterWorld())
                {
                    portal.Destroy();
                    return;
                }

                var chain = new ActionChain();
                chain.AddDelaySeconds(seconds);
                chain.AddAction(portal, () => { if (!portal.IsDestroyed) portal.Destroy(); });
                chain.EnqueueChain();
                FraysManager.Effect(portal, PlayScript.PortalEntry);
            }
            catch (Exception ex)
            {
                log.Error("[FRAYS] Great Snarl portal home threw", ex);
            }
        }

        /// <summary>
        /// Quick travel to the Great Snarl. /fray great: a player who holds a campfire in
        /// the Fray goes straight to its Great Snarl's spot while it is coming or up. Their camp is held meanwhile, as it is
        /// for anyone walking there.
        /// </summary>
        public static string TravelToGreat(Player player)
        {
            var fire = FraysManager.Fires.FirstOrDefault(f => f.Lit && f.IsOwner(player));

            if (fire == null)
                return "You hold no campfire. The Great Snarl's quick travel is for anyone who holds a campfire.";

            // Any fire holder, solo or group, in any Fray: there is one Great Snarl for every Fray.
            var key = GreatKey;
            Creature great;
            bool due;
            uint instance;

            lock (frays)
            {
                var st = Great;
                great = st.GreatSnarl != null && !st.GreatSnarl.IsDead && !st.GreatSnarl.IsDestroyed ? st.GreatSnarl : null;
                due = st.GreatDueFire != null;
                instance = great?.Location?.Instance ?? GreatInstance(fire.Instance);
            }

            if (great == null && !due)
                return "No Great Snarl is coming. It comes when the Great Snarl's bar fills (/fp).";

            if (!GreatSnarlSpots.TryGetValue(key, out var spot))
                return "The Great Snarl has no place to walk in.";

            if (player.IsDead || player.PKTimerActive)
                return "You cannot travel right now.";

            Position pos;

            if (GreatArrivals.TryGetValue(key, out var arrival))
            {
                pos = new Position(arrival.Cell, arrival.Pos.X, arrival.Pos.Y, arrival.Pos.Z + 0.05f, arrival.Facing.X, arrival.Facing.Y, arrival.Facing.Z, arrival.Facing.W, instance);
                player.Teleport(pos);
                return great != null ? "You are pulled to the Great Snarl's gathering spot. Strike it for a cache." : "You are pulled to the gathering spot for the Great Snarl.";
            }

            if (great?.Location != null)
                pos = new Position(great.Location);
            else
            {
                pos = new Position(((uint)spot.Landblock << 16) | spot.Cell, spot.Pos.X, spot.Pos.Y, spot.Pos.Z + 0.05f, 0, 0, 0, 1, instance);

                if (spot.Cell < 0x100 && spot.Pos.Z <= 0)
                    pos.PositionZ = PositionExtensions.GetTerrainZ(pos) + 0.5f;
            }

            pos.PositionX += 12;   // a little way off, not in its arms
            player.Teleport(pos);
            return great != null ? "You are pulled to the Great Snarl. Strike it for a cache." : "You are pulled to where the Great Snarl will walk in.";
        }

        public static void OnGreatSnarlDied(Creature creature)
        {
            try
            {
                var lb = creature.FraysGreatSnarl;

                // Its landblock may unload again (unless a lit fire holds it).
                lock (frays)
                {
                    var pinned = State(lb).GreatPinned;
                    State(lb).GreatPinned = null;
                    State(lb).GreatHoldUntil = ACE.Common.Time.GetUnixTime() + FraysSettings.Number(FraysSettings.GreatHoldMinutes) * 60;   // time to walk back (20 min)

                    if (pinned != null && !FraysManager.Fires.Any(f => f.Lit && f.Landblock == pinned))
                        pinned.Permaload = false;
                }
                OpenPortalHome(creature);

                var hitters = creature.DamageHistory.TotalDamage.Values
                    .Select(d => d.TryGetPetOwnerOrAttacker() as Player).Where(p => p != null).Distinct().ToList();

                // Lucid Night counts Great Snarls slain by players.
                if (hitters.Count > 0)
                    FraysLongTerm.GreatSnarlKilled();

                // The weekly task "Kill the Great Snarl": everyone who struck it.
                foreach (var p in hitters)
                    FraysLongTerm.Bounty(p, "w_great", 1);

                var items = FraysSettings.Int(FraysSettings.GreatSnarlCacheItems);
                // Feeders who never struck it get the cache too, at great_snarl_feeder_share or more of the bar; the top 3
                // feeders get one more item.
                Dictionary<uint, double> feeders;

                lock (frays)
                    feeders = new Dictionary<uint, double>(State(lb).Feeders);

                var minShare = FraysSettings.Number(FraysSettings.GreatSnarlFeederShare);
                var top = feeders.OrderByDescending(kv => kv.Value).Take(3).Select(kv => kv.Key).ToHashSet();
                var paid = new List<Player>(hitters);

                if (minShare > 0)
                    foreach (var (guid, share) in feeders)
                        if (share >= minShare && PlayerManager.GetOnlinePlayer(guid) is Player fp && !paid.Contains(fp))
                            paid.Add(fp);


                // Emberblessed: everyone it pays gets the buff, one Heat at once and a short pause in cooling.
                foreach (var p in paid)
                    FraysManager.Bless(p);

                // No Fray Effigy from the Great Snarl: pets come only from Snarls, as rare drops
                // (effigy_snarl_chance). It used to give one the first time each player was paid by a family's Great Snarl.

                foreach (var p in paid)
                {
                    var got = GiveCache(p, items + (top.Contains(p.Guid.Full) ? 1 : 0));
                    FraysManager.Tell(p, $"The Great Snarl falls. Your share of its cache: {got} item{(got == 1 ? "" : "s")} (the best one attuned){(top.Contains(p.Guid.Full) ? ", one more as a top feeder of its bar" : "")}.");

                    // An augmentation gem token or a Stipend, for each player apart (Great Snarl only, 5%).
                    FraysLongTerm.GreatSnarlAug(p);

                    // Sky Seals for a group camper.
                    FraysSky.OnGreatSnarl(p);
                }

                lock (frays)
                    State(lb).GreatSnarl = null;

                var anyFire = FraysManager.Fires.FirstOrDefault(f => f.Lit) ?? FraysManager.Fires.FirstOrDefault();

                if (anyFire != null)
                    FraysManager.Feed(anyFire, $"The Great Snarl is slain by {hitters.Count} camper{(hitters.Count == 1 ? "" : "s")}; {paid.Count} share its cache.", everyone: true);
            }
            catch (Exception ex)
            {
                log.Error("[FRAYS] Great Snarl death threw", ex);
            }
        }

        // ---- matching flame colours --------------------------------------------------------------------------

        private static readonly PlayScript[] Colours =
        {
            PlayScript.EnchantUpBlue, PlayScript.EnchantUpGreen, PlayScript.EnchantUpPurple,
            PlayScript.EnchantUpRed, PlayScript.EnchantUpOrange, PlayScript.EnchantUpYellow,
        };

        private static int nextColour;

        /// <summary>A fire was lit: if another fire of this Fray was lit by someone else in the last minute, both share a colour.</summary>
        public static void OnLit(FraysFire fire, double now)
        {
            var partner = FraysManager.Fires.Where(f => f != fire && f.Lit && f.Def.FrayKey == fire.Def.FrayKey && f.Instance == fire.Instance
                    && f.SoloOwner != fire.SoloOwner && now - f.LitAt <= 60)
                .OrderByDescending(f => f.LitAt).FirstOrDefault();

            if (partner == null)
                return;

            var colour = partner.KindredColour ?? Colours[nextColour++ % Colours.Length];
            partner.KindredColour = colour;
            fire.KindredColour = colour;
            FraysManager.Feed(fire, $"Kindred flames: {fire.OwnerName} and {partner.OwnerName} lit their fires together.");
        }

        /// <summary>Called from a fire's own tick: its shared colour sparkles every 30 seconds.</summary>
        public static void Sparkle(FraysFire fire, double now)
        {
            if (fire.SparkColour is not PlayScript colour || now < fire.NextSparkleAt || fire.FlameObject == null)
                return;

            fire.NextSparkleAt = now + (fire.KindredColour != null ? 30 : 8);   // an owner's chosen sparks burst every 8 s
            fire.FlameObject.EnqueueBroadcast(new GameMessageScript(fire.FlameObject.Guid, colour));
        }

        // ---- combined Heat, the tick, saving ------------------------------------------------------------------

        private static double nextGreatPulse;

        public static void Tick(double now)
        {
            // The Great Snarl glows purple every 4 seconds (told apart from the Snarl's red and the gates' gold).
            if (now >= nextGreatPulse)
            {
                nextGreatPulse = now + 4;
                List<Creature> greats;

                lock (frays)
                    greats = frays.Values.Select(s => s.GreatSnarl).Where(g => g != null && !g.IsDead && !g.IsDestroyed).ToList();

                foreach (var g in greats)
                    FraysManager.Effect(g, ACE.Entity.Enum.PlayScript.SpecialStatePurple);
            }

            foreach (var group in FraysManager.Fires.GroupBy(f => f.Def.FrayKey))
            {
                var heat = group.SelectMany(f => f.CampersSnapshot()).Sum(c => c.Level);
                var st = State(group.Key);

                lock (frays)
                {
                    if (heat > st.BestCombinedHeat)
                        st.BestCombinedHeat = heat;
                }
            }

            // The one Great Snarl's countdown: a reminder at 10 seconds, then it walks in.
            {
                var st = Great;
                FraysFire due = null;
                FraysFire told = null;

                lock (frays)
                {
                    if (st.GreatDueFire != null)
                    {
                        if (now >= st.GreatDueAt)
                        {
                            due = st.GreatDueFire;
                            st.GreatDueFire = null;
                        }
                        else if (!st.GreatHalfTold && st.GreatDueAt - now <= 10)
                        {
                            st.GreatHalfTold = true;
                            told = st.GreatDueFire;
                        }
                    }
                }

                // a full bar that waited out great_snarl_gap_hours: call it now
                FraysFire waited = null;

                lock (frays)
                {
                    if (st.GreatWaitFire != null && st.GreatDueFire == null && (st.GreatSnarl == null || st.GreatSnarl.IsDead || st.GreatSnarl.IsDestroyed)
                        && FraysRules.GreatGapLeft(st.LastGreatAt, FraysSettings.Number(FraysSettings.GreatSnarlGapHours), now) <= 0)
                    {
                        waited = st.GreatWaitFire.Lit ? st.GreatWaitFire : FraysManager.Fires.FirstOrDefault(f => f.Lit) ?? st.GreatWaitFire;
                        st.GreatWaitFire = null;
                    }
                }

                if (waited != null)
                    CallGreatSnarl(waited);

                // Not beaten in great_timeout_minutes: it leaves.
                Creature leaving = null;

                lock (frays)
                {
                    if (st.GreatSnarl != null && !st.GreatSnarl.IsDead && !st.GreatSnarl.IsDestroyed
                        && FraysRules.GreatTimedOut(st.GreatSpawnedAt, FraysSettings.Number(FraysSettings.GreatTimeoutMinutes), now))
                        leaving = st.GreatSnarl;
                }

                if (leaving != null)
                    GreatSnarlLeaves(leaving, now);

                if (told != null)
                    FraysManager.Announce(told, $"The Great Snarl arrives in 10 seconds {GreatSpotText(GreatKey)}.", everyone: true);

                if (due != null)
                    SpawnGreatSnarl(due);
            }

            if (now >= nextSave)
            {
                nextSave = now + 60;
                Save();
            }
        }

        /// <summary>/top fray combined: each Fray's combined Heat now and its best this week.</summary>
        public static string CombinedText()
        {
            var lines = new List<string> { "=== The Frays' combined Heat (this week) ===" };

            foreach (var group in FraysManager.Fires.GroupBy(f => f.Def.FrayKey).OrderBy(g => g.Key))
            {
                var now = group.SelectMany(f => f.CampersSnapshot()).Sum(c => c.Level);
                var st = State(group.Key);
                lines.Add($"{FraysManager.FrayName(group.Key)}: best {st.BestCombinedHeat}, now {now}.");
            }

            if (lines.Count == 1)
                return "No Fray is loaded right now.";

            lines.Add($"The Great Snarl (every Fray): {GreatPercent(GreatKey, Great):0}%; the weekly goal (every camp): {Math.Min(100, Great.WeekBurn / FraysSettings.Number(FraysSettings.FrayGoalWork) * 100):0}%.");
            return string.Join("\n", lines);
        }

        /// <summary>The Great Snarl's bar as a percent (100 while it is due or up).</summary>
        private static double GreatPercent(ushort landblock, FrayState st)
        {
            lock (frays)
            {
                if (st.GreatDueFire != null || (st.GreatSnarl != null && !st.GreatSnarl.IsDestroyed && !st.GreatSnarl.IsDead))
                    return 100;
            }

            return Math.Min(100, st.GreatBar / GreatNeed(landblock) * 100);
        }

        /// <summary>/frays bar: the Great Snarl's bar, who feeds it most, and the weekly goal, for one Fray.</summary>
        public static List<string> BarLines(ushort landblock, double now)
        {
            var week = Great;
            var st = Great;
            var lines = new List<string>();
            var campers = FraysManager.Fires.Sum(f => f.CampersSnapshot().Count);
            var need = GreatNeed(GreatKey);
            string great;

            lock (frays)
            {
                if (st.GreatSnarl != null && !st.GreatSnarl.IsDestroyed && !st.GreatSnarl.IsDead)
                    great = $"Great Snarl: up now {GreatSpotText(GreatKey)} - strike it for a cache.";
                else if (st.GreatDueFire != null)
                    great = $"Great Snarl: arrives in {Math.Max(0, st.GreatDueAt - now):0} seconds {GreatSpotText(GreatKey)}.";
                else
                    great = $"Great Snarl (every Fray, {campers} camping): {FraysRules.Bar(st.GreatBar / need)}{TopFeeders(st, 3)}";
            }

            lines.Add(great);
            lines.Add($"The Frays' weekly goal (every camp): {FraysRules.Bar(week.WeekBurn / FraysSettings.Number(FraysSettings.FrayGoalWork))}");
            return lines;
        }

        /// <summary>One Fray's bars for /frays status: Great Snarl bar and weekly goal, as percentages.</summary>
        public static (double Great, double Goal) Bars(ushort landblock)
        {
            var st = State(landblock);
            return (GreatPercent(GreatKey, Great),
                Math.Min(100, Great.WeekBurn / FraysSettings.Number(FraysSettings.FrayGoalWork) * 100));
        }

        private static void Save()
        {
            string text;

            lock (frays)
            {
                if (stateWeek == int.MinValue)
                    return;

                text = stateWeek.ToString(CultureInfo.InvariantCulture) + "|" + string.Join(";", frays.Select(kv =>
                    string.Join(",", kv.Key.ToString("X4"), (kv.Value.GreatDueFire != null ? 1e9 : kv.Value.GreatBar).ToString("0.##", CultureInfo.InvariantCulture),
                        kv.Value.WeekBurn.ToString("0.##", CultureInfo.InvariantCulture), kv.Value.BestCombinedHeat,
                        kv.Value.GoalAnnounced ? 1 : 0, string.Join(" ", kv.Value.Campers), kv.Value.LastGreatAt.ToString("0", CultureInfo.InvariantCulture))));
            }

            if (text != FraysSettings.Text(FraysSettings.FrayState))
                PropertyManager.ModifyString(FraysSettings.Prefix + FraysSettings.FrayState, text);
        }

        private static void Load()
        {
            try
            {
                var raw = FraysSettings.Text(FraysSettings.FrayState);
                var bar = raw.IndexOf('|');

                if (bar < 0 || !int.TryParse(raw.Substring(0, bar), out var week))
                    return;

                stateWeek = week;

                foreach (var part in raw.Substring(bar + 1).Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var f = part.Split(',');

                    if (f.Length < 6 || !ushort.TryParse(f[0], NumberStyles.HexNumber, null, out var lb))
                        continue;

                    var st = new FrayState
                    {
                        GreatBar = double.Parse(f[1], CultureInfo.InvariantCulture),
                        WeekBurn = double.Parse(f[2], CultureInfo.InvariantCulture),
                        BestCombinedHeat = int.Parse(f[3], CultureInfo.InvariantCulture),
                        GoalAnnounced = f[4] == "1",
                        LastGreatAt = f.Length > 6 && double.TryParse(f[6], NumberStyles.Float, CultureInfo.InvariantCulture, out var lastGreat) ? lastGreat : 0,
                    };

                    foreach (var g in f[5].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (uint.TryParse(g, out var guid))
                            st.Campers.Add(guid);
                    }

                    frays[lb] = st;
                }
            }
            catch (Exception ex)
            {
                log.Warn("[FRAYS] could not read the saved Fray state; starting fresh", ex);
            }
        }
    }
}
