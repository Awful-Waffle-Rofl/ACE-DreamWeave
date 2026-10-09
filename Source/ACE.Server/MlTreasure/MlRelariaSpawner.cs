using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.MlDigsite;
using ACE.Server.MlShared;
using ACE.Server.WorldObjects;

using log4net;

using Position = ACE.Entity.Position;

namespace ACE.Server.MlTreasure
{
    /// <summary>
    /// The Aun Relaria boss spawn (Docs/Marae-Lassel/TREASURE-HUNT-PLAN.md section 6 "Boss siting",
    /// step 10). A treasure map carrying PropertyInt 9066 TreasureMapBossWcid greater than 0 is the rare
    /// Relaria variant: its final dig step summons the named creature at the dig site instead of paying
    /// treasure currency.
    ///
    /// Four things this spawn must guarantee, each of which is a separate mechanism:
    ///
    ///   * CONFINEMENT. The creature is stamped with PropertyFloat 9009 TetherRadius and a HomeRadius of
    ///     twice that before it enters the world, which is the same recipe WorldEventSpawner.ApplyBossTether
    ///     uses (WorldEventSpawner.cs:1970-1995). Home is stamped from Location by WorldObject.AddPhysicsObj
    ///     (WorldObject.cs:246) as the creature enters, so the tether is measured from where it was dug up
    ///     with no extra write here.
    ///
    ///   * A TTL. TimeToRot is set to ml_treasure_boss_ttl_seconds, so the landblock heartbeat's decay pass
    ///     (Landblock.cs:1155) destroys an abandoned boss. This is the OPPOSITE of what the two other
    ///     hand-spawners in this fork do - WorldEventSpawner.TryPlace and Player_WaveChallenge.SpawnWave
    ///     both set TimeToRot = -1 because they own an explicit cleanup pass and must not lose a creature
    ///     an objective is counting. Nothing owns this boss, so the engine's own decay IS the cleanup, and
    ///     -1 here would leave it standing until the landblock unloaded.
    ///
    ///   * NO SHARD ROWS. See <see cref="BossMarker"/>. The landblock this spawns on is an ordinary
    ///     persistent realm-1 landblock, NOT an ephemeral instance, so Landblock.SaveDB's IsEphemeral early
    ///     return (Landblock.cs:1945) does not apply and cannot be relied on.
    ///
    ///   * boss_ok SITING. <see cref="CanSpawn"/> refuses unless the map's stored dig site is a boss_ok row
    ///     in the catalogue, which is what keeps the boss out of a town plaza.
    ///
    /// No new WorldObject subclass, and no wcid branch: the boss identity travels in PropertyInt 9066 on
    /// the map (TREASURE-HUNT-PLAN.md invariant 4), so a second boss variant is pure content.
    /// </summary>
    public static class MlRelariaSpawner
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// The property stamped on the spawned creature so it never reaches the shard:
        /// WorldObject.IsDynamicThatShouldPersistToShard (WorldObject_Database.cs) refuses any creature
        /// carrying it, alongside the existing WorldEventId and ThreadDungeonRunId exclusions.
        ///
        /// This stamp is LOAD-BEARING and is not decoration. Marae Lassel in realm 1 is an ordinary
        /// persistent landblock - IsEphemeral is true only for a temporary instanced realm copy
        /// (Landblock.cs:75-81) - so Landblock.SaveDB would otherwise write this creature's biota on
        /// unload, Landblock.Unload would then take its RemoveWorldObjectInternal arm rather than Destroy
        /// (Landblock.cs:1905), and the boss would respawn from the shard on every later activation of that
        /// landblock, forever, with no map and no player involved.
        ///
        /// 9066 is the id the plan reserved for "the Relaria boss wcid" (TREASURE-HUNT-PLAN.md section 3).
        /// On a MAP it names the boss to summon; on a CREATURE it names which boss this is, which is the
        /// same fact read from the other end, and it is also what <see cref="MlRelariaTrophy"/> keys the
        /// trophy drop on. No new property id is allocated for either use.
        ///
        /// Because BOTH carriers exist, the persistence exclusion tests `this is Creature` as well as the
        /// property. A map is a Gem, and a map DROPPED ON THE GROUND is a top-level landblock object that
        /// SaveDB does test - so a property-only exclusion would have made dropped Relaria maps vanish on
        /// unload. That narrowing is not tidiness; it is the bug it prevents.
        /// </summary>
        public const PropertyInt BossMarker = PropertyInt.TreasureMapBossWcid;

        /// <summary>
        /// Metres between a boss_ok dig site and the nearest landblock whose boss_ok flag is 0. The
        /// catalogue clears boss_ok for the five town landblocks AND their 8 neighbours
        /// (TREASURE-HUNT-PLAN.md section 6), so a boss_ok cell is at least one whole landblock away from
        /// any town block, and one landblock is 192 metres (ACE.Entity.Position.BlockLength).
        ///
        /// This is why <see cref="CanSpawn"/> can place the boss at the PLAYER rather than at the stored
        /// site coordinates: the player has been classified as digging, so they stand within
        /// ml_treasure_near_metres of a boss_ok site, and while that distance stays under this margin they
        /// cannot possibly be standing in an excluded landblock.
        /// </summary>
        public const double TownExclusionMarginMetres = 192.0;

        /// <summary>
        /// Map-unit tolerance when matching a map's stored 9010/9011 pair back to a catalogue row. The
        /// values make a float -> double -> float round trip through the biota (PropertyFloat is double in
        /// this codebase) which is exact, so this only absorbs a future change in how the pair is written.
        /// </summary>
        public const float SiteMatchToleranceMapUnits = 0.001f;

        // ---- pure decisions ------------------------------------------------------------------------

        /// <summary>
        /// The siteIsBossOk value <see cref="TrySpawn"/> feeds to <see cref="CanSpawn"/>: the real
        /// catalogue match, UNLESS <paramref name="skipBossSiteCheck"/> is set, which always answers true.
        /// Set only for a map carrying PropertyBool.TreasureMapAdminTest (/testtreasuremap relaria): that
        /// map's site was stamped at the admin's own position, never rolled from the catalogue, so the
        /// catalogue match would refuse every test relaria dig. Every OTHER CanSpawn clause - enabled,
        /// realm/landblock, indoors, bossWcid, the town-exclusion margin - is unaffected: this bypasses only
        /// the one check that exists solely to keep a REAL dig's boss out of an uncatalogued cell.
        /// </summary>
        public static bool EffectiveSiteIsBossOk(bool catalogueMatch, bool skipBossSiteCheck) => skipBossSiteCheck || catalogueMatch;

        /// <summary>
        /// The complete boss-spawn eligibility decision over already-read values, so every clause is
        /// testable without a live Player, a landblock, or PropertyManager (whose reads throw under unit
        /// test). This is what the runtime path actually calls - it is not a parallel copy of the rules.
        /// </summary>
        /// <param name="landblock">raw 16-bit landblock id the player is standing on</param>
        /// <param name="realm">realm id of that position; ML is realm 1 only, and retail Marae Lassel
        /// exists in realm 0 at identical physical coordinates (TREASURE-HUNT-PLAN.md section 4)</param>
        /// <param name="indoors">true when the player is in a dungeon/building cell; the spawn needs a
        /// terrain snap (Position.AdjustMapCoords) and a dig site is an outdoor terrain cell</param>
        /// <param name="bossWcid">PropertyInt 9066 off the map; 0 or less means this is an ordinary map</param>
        /// <param name="siteIsBossOk">whether the map's stored dig site matched a boss_ok catalogue row</param>
        /// <param name="enabled">ml_treasure_enabled</param>
        /// <param name="nearMetres">ml_treasure_near_metres - how far from the site a player may stand and
        /// still be digging, which is therefore the furthest the boss can land from a boss_ok cell</param>
        public static bool CanSpawn(ushort landblock, ushort realm, bool indoors, int bossWcid,
            bool siteIsBossOk, bool enabled, double nearMetres)
        {
            if (!enabled)
                return false;

            // NOT optional, and not redundant with the caller's own gate: retail Marae Lassel exists in
            // realm 0 at the same landblocks, and realm 1 also carries non-ML landblocks, so neither half
            // is sufficient alone (TREASURE-HUNT-PLAN.md section 4).
            if (!MlTreasureLandblock.IsMaraeLassel(landblock, realm))
                return false;

            if (indoors)
                return false;

            if (bossWcid <= 0)
                return false;

            if (!siteIsBossOk)
                return false;

            // The boss lands at the player, not at the stored coordinates, so the town exclusion only
            // survives while a digging player is necessarily still inside the boss_ok cell's landblock
            // margin. Raising ml_treasure_near_metres past a landblock length would silently break "the
            // boss can never appear in a town plaza"; refusing here fails closed instead.
            if (!(nearMetres >= 0.0) || nearMetres >= TownExclusionMarginMetres)
                return false;

            return true;
        }

        /// <summary>
        /// HomeRadius for a given tether, matching WorldEventSpawner.BossHomeRadiusFor: twice the tether,
        /// so the ordinary 192 m leash still sits outside the tether and only catches a boss the tether's
        /// forced retarget could not pull back.
        /// </summary>
        public static double HomeRadiusFor(double tetherRadius) => tetherRadius * 2.0;

        /// <summary>
        /// Whether the tether stamp is applied at all. A non-finite or non-positive radius means "no
        /// tether", matching how PropertyFloat 9009 is defined (absent or &lt;= 0 leaves targeting
        /// unchanged) and how the world-event spawner reads its own tunable.
        /// </summary>
        public static bool AppliesTether(double tetherRadius)
        {
            return tetherRadius > 0.0 && !double.IsNaN(tetherRadius) && !double.IsInfinity(tetherRadius);
        }

        /// <summary>
        /// The TimeToRot to stamp, clamped to something the decay pass can actually count down. 0 and -1
        /// are both meaningful to WorldObject_Decay (instant rot / never rot) and neither is a TTL, so a
        /// misconfigured tunable falls back to <paramref name="fallbackSeconds"/> rather than producing an
        /// immortal boss.
        /// </summary>
        public static double TimeToRotFor(long ttlSeconds, double fallbackSeconds)
        {
            return ttlSeconds > 0 ? ttlSeconds : fallbackSeconds;
        }

        // ---- runtime -------------------------------------------------------------------------------

        /// <summary>
        /// The pre-EnterWorld setup <see cref="TrySpawn"/> applies to the boss, factored out so a second
        /// caller (the /relariaspawn admin command, MlRelariaCommands.cs) cannot drift from the real dig
        /// path: both callers must stamp the exact same marker/TTL/tether, in the exact same order, or the
        /// admin spawn would be a plausible-looking creature that behaves differently under the hood.
        ///
        /// Must be called AFTER <paramref name="boss"/>.Location is set and BEFORE boss.EnterWorld() - the
        /// same ordering rule TrySpawn itself follows and documents at its call site: there must be no
        /// window in which a landblock save or a client sees this creature as an ordinary persistable
        /// monster. The tether this stamps (when applied) anchors from wherever boss.Location already
        /// points, via WorldObject.AddPhysicsObj on EnterWorld (WorldObject.cs:246) - not from any
        /// dig-site coordinate - so a caller that places the boss somewhere other than a dig site still
        /// gets a tether centred on that spawn position with no extra write here.
        /// </summary>
        /// <param name="boss">the not-yet-entered creature; Location must already be set</param>
        /// <param name="bossWcid">the wcid to stamp as <see cref="BossMarker"/> - normally
        /// <paramref name="boss"/>'s own wcid, but taken as a separate parameter to match TrySpawn's
        /// original call, which stamps the map's PropertyInt 9066 value rather than re-deriving it</param>
        public static void PrepareBoss(Creature boss, int bossWcid)
        {
            // Every property write below happens BEFORE EnterWorld, the rule both other hand-spawners in
            // this fork follow: there must be no window in which a landblock save or a client sees this
            // creature as an ordinary persistable monster.
            boss.SetProperty(BossMarker, bossWcid);

            var ttl = TimeToRotFor(PropertyManager.GetLong("ml_treasure_boss_ttl_seconds").Item, DefaultTtlSeconds);
            boss.TimeToRot = ttl;

            var tether = PropertyManager.GetDouble("ml_treasure_boss_tether_radius").Item;

            if (AppliesTether(tether))
            {
                boss.TetherRadius = tether;
                boss.HomeRadius = HomeRadiusFor(tether);
                boss.DisableSticky = true;
            }

            // RoZ round 19 owner ruling M1: map-event bosses resist magic too heavily. Shared with
            // MlDigsiteSpawner's own boss-weight roles via MlMapEventBossMagicDefense.ScaleInitLevel, which
            // is the pure math both callers apply the same way; only the PropertyManager read differs.
            var magicDefenseScale = Math.Clamp(PropertyManager.GetDouble("ml_mapevent_boss_magic_defense_scale", 0.90).Item, 0.0, 1.0);

            var magicDefense = boss.GetCreatureSkill(Skill.MagicDefense);

            magicDefense.InitLevel = MlMapEventBossMagicDefense.ScaleInitLevel(magicDefense.InitLevel, magicDefense.Current, magicDefenseScale);

            // RoZ round 19 level-spread scaling: the SAME MlMapEventScaling.ApplySpreadScaling the digsite spawner
            // calls, as a Boss (defenses floored for the weakest present player, health on the power-sum curve),
            // sampled at the spawn anchor with the digsite presence rule. AFTER M1, so the magic-defense floor
            // measures what M1 left. Spawn-time only: nothing owns this boss, so there is no tick to re-check it
            // or to run a measured damage controller.
            var spreadSettings = MlMapEventTunables.Read();

            var profiles = spreadSettings.Enabled
                ? MlDigsiteAudience.Sample(boss.Location, MlDigsiteTunables.AudienceRadiusMetres)
                : new List<MlMapEventProfile>();

            MlMapEventScaling.ApplySpreadScaling(boss, MlMapEventRoleClass.Boss, profiles,
                new Random(ThreadSafeRandom.Next(0, int.MaxValue - 1)), spreadSettings, $"relaria wcid={bossWcid}");
        }

        /// <summary>
        /// Summons the boss named by <paramref name="bossWcid"/> where <paramref name="player"/> is
        /// standing. Returns false and leaves the world untouched on every refusal, which is what lets the
        /// caller keep the map in the player's pack so the dig can be retried.
        /// </summary>
        /// <param name="player">the digging player; the spawn anchor and the tether centre</param>
        /// <param name="bossWcid">PropertyInt 9066 off the map</param>
        /// <param name="siteMapCoords">the map's stored 9010/9011 pair as (X=EastWest, Y=NorthSouth),
        /// used only to confirm the site is a boss_ok catalogue row</param>
        /// <param name="skipBossSiteCheck">
        /// true only for a /testtreasuremap relaria map (PropertyBool.TreasureMapAdminTest): its site was
        /// stamped at the admin's own position, not rolled from the catalogue, so the catalogue match is
        /// bypassed via <see cref="EffectiveSiteIsBossOk"/> while every other CanSpawn clause still applies.
        /// </param>
        public static bool TrySpawn(Player player, int bossWcid, Vector2 siteMapCoords, bool skipBossSiteCheck = false)
        {
            if (player?.Location == null)
                return false;

            var siteIsBossOk = EffectiveSiteIsBossOk(
                MlTreasureSiteStore.Instance.IsBossSite(siteMapCoords, SiteMatchToleranceMapUnits), skipBossSiteCheck);

            var eligible = CanSpawn(player.Location.LandblockId.Landblock, player.Location.RealmID,
                player.Location.Indoors, bossWcid, siteIsBossOk,
                PropertyManager.GetBool("ml_treasure_enabled").Item,
                PropertyManager.GetDouble("ml_treasure_near_metres").Item);

            if (!eligible)
            {
                log.Warn($"[ML_TREASURE] {player.Name} completed a Relaria dig at {player.Location.ToLOCString()} but the boss spawn was refused (bossWcid={bossWcid}, siteBossOk={siteIsBossOk}, indoors={player.Location.Indoors}, store count {MlTreasureSiteStore.Instance.Count}); the map was NOT consumed");
                return false;
            }

            var wo = WorldObjectFactory.CreateNewWorldObject((uint)bossWcid);

            if (wo == null)
            {
                log.Error($"[ML_TREASURE] Relaria boss wcid {bossWcid} failed to create for {player.Name}; is Content/sql/weenies/1004120 Aun Relaria the Unburied.sql applied to this world database?");
                return false;
            }

            // Fail closed on a non-Creature. Only a Creature can be tethered, can die, and can leave the
            // corpse the trophy is placed on, so a non-Creature here would be an unkillable object standing
            // on the dig site until its TTL expired.
            if (!(wo is Creature boss))
            {
                log.Error($"[ML_TREASURE] Relaria boss wcid {bossWcid} is a {wo.WeenieType}, not a Creature; nothing spawned");
                wo.Destroy();
                return false;
            }

            var anchor = new Position(player.Location);

            try
            {
                // A creature spawned mid-air FALLS (Creature_SkyDrop.cs is the whole feature built on that),
                // and the player may have been a hand's breadth off the ground when the dig finished. The
                // terrain snap overwrites Z outright with the ground height under the anchor, which is the
                // same thing WorldEventSpawner.TryPlace does for every outdoor creature placement.
                anchor.AdjustMapCoords();
            }
            catch (Exception ex)
            {
                log.Error($"[ML_TREASURE] terrain snap failed for the Relaria spawn at {anchor.ToLOCString()}", ex);
                boss.Destroy();
                return false;
            }

            boss.Location = anchor;

            PrepareBoss(boss, bossWcid);

            if (!boss.EnterWorld())
            {
                log.Error($"[ML_TREASURE] Relaria boss wcid {bossWcid} failed to enter the world at {anchor.ToLOCString()} for {player.Name}");
                boss.Destroy();
                return false;
            }

            // A non-generator spawn never fires the weenie's Generation emote set - EmoteManager.OnGeneration
            // is called from WorldObject.OnGeneration, which only a generator reaches - so any intro the boss
            // weenie authors has to be fired by hand here. Same call, same try/catch, as
            // WorldEventSpawner's boss branch (WorldEventSpawner.cs:1792-1803).
            try
            {
                boss.EmoteManager.OnGeneration();
            }
            catch (Exception ex)
            {
                log.Error($"[ML_TREASURE] Relaria intro emote threw for wcid {bossWcid} (0x{boss.Guid.Full:X8})", ex);
            }

            log.Info($"[ML_TREASURE] {player.Name} dug up Relaria wcid {bossWcid} (0x{boss.Guid.Full:X8}) at {anchor.ToLOCString()}; ttl={boss.TimeToRot}s tether={(boss.TetherRadius.HasValue ? boss.TetherRadius.ToString() : "off")} homeRadius={boss.HomeRadius?.ToString() ?? "default"}");

            return true;
        }

        /// <summary>
        /// The TTL used when ml_treasure_boss_ttl_seconds is not a usable countdown. 30 minutes, chosen to
        /// sit far above any plausible length for an encounter the plan calls solo-beatable, because the
        /// decay pass does not know the boss is being fought: a TTL that elapsed mid-fight would Destroy()
        /// the creature outright, with no Die(), no corpse and no XP.
        /// </summary>
        public const double DefaultTtlSeconds = 1800.0;
    }
}
