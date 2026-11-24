using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Reflection;

using ACE.Entity;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldObjects;

using log4net;

using Position = ACE.Entity.Position;

namespace ACE.Server.Avatars.Gauntlet;

/// <summary>
/// The pure rules of a finished run's rewards (cache count, ring, tier, gates) and the constants the content must match. The live
/// hooks are <see cref="LiveGauntletRewardHooks"/>.
///
/// The crate a run pays is World Event reward crate wcid <see cref="CrateWcid"/>, stamped with a rewards.json profile
/// (gauntlet_t0 to gauntlet_t6, chosen by <see cref="GauntletCrateTier.For"/>) and a multiplier in [1, 2]. The composer only ever
/// picks WorldEventComposer.DefaultRewardId, so the gauntlet_tN profiles never reach a World Event.
/// </summary>
public static class GauntletRewards
{
    /// <summary>The cache weenie (Task 14 content): a Generic object carrying PropertyBool.AllegianceGauntletCache.</summary>
    public const uint CacheWcid = 1008506;

    /// <summary>The crate weenie (Task 14 content): a World Event reward crate, opened by WorldEventRewardCrate.</summary>
    public const uint CrateWcid = 1008507;

    /// <summary>Metres from the arena centre to each cache on the ring.</summary>
    public const float RingRadius = 3.0f;

    public const int DefaultParticipantsPerCache = 8;

    /// <summary>max(1, ceil(eligible / participantsPerCache)), at most <see cref="WorldEventRewardDelivery.MaxCachesPerRun"/>.</summary>
    public static int CacheCount(int eligible, int participantsPerCache)
    {
        var per = participantsPerCache > 0 ? participantsPerCache : DefaultParticipantsPerCache;
        var count = (int)Math.Max(1L, ((long)Math.Max(0, eligible) + per - 1) / per);
        return Math.Min(count, WorldEventRewardDelivery.MaxCachesPerRun);
    }

    /// <summary>
    /// <paramref name="count"/> offsets on a ring of <paramref name="radius"/> metres. A single cache has no ring and stands at the
    /// centre, as a lone World Event cache does.
    /// </summary>
    public static IReadOnlyList<Vector3> RingOffsets(int count, float radius = RingRadius)
    {
        var offsets = new List<Vector3>(Math.Max(1, count));

        if (count <= 1)
        {
            offsets.Add(Vector3.Zero);
            return offsets;
        }

        for (var i = 0; i < count; i++)
        {
            var angle = 2.0 * Math.PI * i / count;
            offsets.Add(new Vector3((float)(radius * Math.Cos(angle)), (float)(radius * Math.Sin(angle)), 0f));
        }

        return offsets;
    }

    /// <summary>
    /// The name a paid crate carries: "Gauntlet Crate (Tier {t}, Wave {score:0.00})", the tier read from the profile's
    /// gauntlet_t suffix and the score the run's centi-waves as waves. Under the 255-character chat limit for any input.
    /// </summary>
    public static string CrateName(string profile, long scoreCenti)
    {
        var tier = profile != null && profile.StartsWith(GauntletCrateTier.ProfilePrefix, StringComparison.Ordinal)
            && int.TryParse(profile.Substring(GauntletCrateTier.ProfilePrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var t) ? t : 0;
        var score = Math.Max(0L, scoreCenti);
        return string.Format(CultureInfo.InvariantCulture, "Gauntlet Crate (Tier {0}, Wave {1}.{2:D2})", tier, score / 100, score % 100);
    }

    /// <summary>The tier of a finished run: its score is centi-waves, so cleared = score / 100 and the partial is the remainder.</summary>
    public static (string Profile, double Multiplier) TierFor(long scoreCenti, long maxTier)
    {
        var cleared = (int)Math.Clamp(scoreCenti / 100, 0, int.MaxValue);
        var partial = (int)Math.Clamp(scoreCenti % 100, 0, 99);
        return GauntletCrateTier.For(cleared, partial, (int)Math.Clamp(maxTier, 0, GauntletTuning.CrateMaxTierCeiling));
    }

    /// <summary>
    /// Resolves the gates a window runs under. Character and account come from the profile (copied from standard). The household
    /// rule needs the profile AND the operator's world_events_ip_gate_enabled; the exempt addresses are world_events_ip_exempt.
    /// </summary>
    public static (bool Character, bool Account, bool Ip, ISet<string> Exempt) ResolveGates(RewardDef reward, bool ipGateEnabled, string exemptCsv)
        => (reward?.GateByCharacter ?? true, reward?.GateByAccount ?? true, (reward?.GateByIp ?? true) && ipGateEnabled, WorldEventClaimRules.ParseExemptIps(exemptCsv));
}

/// <summary>A <see cref="GauntletRun"/> seen by the claim pipeline.</summary>
internal sealed class GauntletRunClaimAdapter : IGauntletClaimRun
{
    private readonly GauntletRun run;

    public GauntletRunClaimAdapter(GauntletRun run) => this.run = run;

    public uint ClaimRunId => run.AnnounceKey;
    public bool IsMember(Player player) => run.IsMember(player);
    public bool IsEligible(uint characterGuid) => run.IsEligible(characterGuid);
    public (int Counted, int Required) WaveCredit(uint characterGuid) => run.WaveCredit(characterGuid);
    public void NoteCrateClaimed() => run.NoteCrateClaimed();
}

/// <summary>
/// The live reward hooks (plan Task 12). At Rewarding: tier the run, build its claim window, ring the caches around the arena centre
/// on the arena landblock, and answer the claim window length. At the end of Rewarding: close the window and remove the caches.
/// When the instance is lost: pay eligible online members straight to their packs through the same claim pipeline.
/// </summary>
internal sealed class LiveGauntletRewardHooks : IGauntletRewardHooks
{
    private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

    private sealed class RunRewards
    {
        public GauntletClaimWindow Window;
        public readonly List<WorldObject> Caches = new List<WorldObject>();
        public readonly object Sync = new object();
    }

    private readonly ConcurrentDictionary<uint, RunRewards> runs = new ConcurrentDictionary<uint, RunRewards>();

    private static RewardDef Profile(string id) => WorldEventHooks.TryGetRewardProfile(id);

    private static GauntletClaimWindow BuildWindow(GauntletRun run, int secondsOverride, out RewardDef reward)
    {
        var (profile, multiplier) = GauntletRewards.TierFor(run.ScoreCenti, run.Tuning.CrateMaxTier);
        reward = Profile(profile);

        if (reward == null)
            log.Error($"[GAUNTLET] run {run.RunKey}: reward profile '{profile}' is not in rewards.json (store loaded={WorldEventHooks.RewardStoreLoaded}); the crate will not open");

        var window = reward?.ClaimWindowSeconds > 0 ? reward.ClaimWindowSeconds : NoGauntletHooks.DefaultClaimWindowSeconds;
        var gates = GauntletRewards.ResolveGates(reward, ReadBool("world_events_ip_gate_enabled"), ReadString("world_events_ip_exempt"));

        return new GauntletClaimWindow(new GauntletRunClaimAdapter(run), profile, multiplier, reward?.SuccessCrateWcid > 0 ? reward.SuccessCrateWcid : GauntletRewards.CrateWcid,
            GauntletClaims.UtcNow().AddSeconds(secondsOverride > 0 ? secondsOverride : window))
        {
            CrateName = GauntletRewards.CrateName(profile, run.ScoreCenti),
            GateByCharacter = gates.Character,
            GateByAccount = gates.Account,
            GateByIp = gates.Ip,
            ExemptIps = gates.Exempt,
        };
    }

    private static bool ReadBool(string key)
    {
        try { return PropertyManager.GetBool(key, true).Item; }
        catch (Exception ex) { log.Warn($"[GAUNTLET] could not read {key}; its rule stays on", ex); return true; }
    }

    private static string ReadString(string key)
    {
        try { return PropertyManager.GetString(key).Item; }
        catch (Exception ex) { log.Warn($"[GAUNTLET] could not read {key}; no exempt addresses", ex); return null; }
    }

    public int BeginRewarding(GauntletRun run)
    {
        var window = BuildWindow(run, 0, out var reward);
        var seconds = Math.Max(1, (int)Math.Ceiling((window.EndsAtUtc - GauntletClaims.UtcNow()).TotalSeconds));
        var eligible = run.EligibleCharacters().Count;
        var count = GauntletRewards.CacheCount(eligible, reward?.ParticipantsPerCache ?? 0);
        var state = new RunRewards { Window = window };
        runs[run.RunKey] = state;

        log.Info($"[GAUNTLET] run {run.RunKey} rewarding: {eligible} eligible, {count} cache(s), profile {window.Profile} x{window.Multiplier:0.###}, window {seconds} s");

        var centre = run.CentrePosition;
        var lb = centre == null || run.Instance == 0 ? null : LandblockManager.GetLoadedLandblock(new LandblockId(centre.Cell & 0xFFFF0000u), run.Instance);

        if (lb == null)
        {
            log.Error($"[GAUNTLET] run {run.RunKey}: the arena landblock is gone at Rewarding; no caches placed");
            return seconds;
        }

        lb.EnqueueAction(new ActionEventDelegate(() =>
        {
            try
            {
                PlaceCaches(run, state, centre, count);
            }
            catch (Exception ex)
            {
                log.Error($"[GAUNTLET] run {run.RunKey} placing caches threw", ex);
            }
        }));

        return seconds;
    }

    private static void PlaceCaches(GauntletRun run, RunRewards state, Position centre, int count)
    {
        var placed = 0;

        foreach (var offset in GauntletRewards.RingOffsets(count))
        {
            var at = new Position(centre);

            if (offset != Vector3.Zero)
            {
                at.SetPosition(centre.Pos + offset);

                if (at.InstancedLandblock != centre.InstancedLandblock)
                    at = new Position(centre);
                else if (!at.Indoors)
                {
                    try { at.AdjustMapCoords(); }
                    catch (Exception ex) { log.Debug($"[GAUNTLET] run {run.RunKey}: a cache point could not be snapped to terrain", ex); }
                }
            }

            var cache = TryPlace(run, at) ?? (offset != Vector3.Zero ? TryPlace(run, new Position(centre)) : null);

            if (cache == null)
                continue;

            lock (state.Sync)
            {
                // The window may already be over (a slow landblock thread): such a cache would never be removed.
                if (state.Window.Closed || run.IsTerminal)
                {
                    cache.Destroy();
                    continue;
                }

                state.Caches.Add(cache);
            }

            GauntletCacheHandler.Register(cache.Guid.Full, state.Window);
            placed++;
        }

        if (placed == 0)
            log.Error($"[GAUNTLET] run {run.RunKey}: no cache entered the world; nobody can claim");
        else
            log.Info($"[GAUNTLET] run {run.RunKey}: {placed} cache(s) placed");
    }

    private static WorldObject TryPlace(GauntletRun run, Position at)
    {
        var cache = WorldObjectFactory.CreateNewWorldObject(GauntletRewards.CacheWcid);

        if (cache == null)
        {
            log.Error($"[GAUNTLET] run {run.RunKey}: cache weenie {GauntletRewards.CacheWcid} is missing");
            return null;
        }

        cache.SetProperty(PropertyBool.AllegianceGauntletCache, true);
        cache.Location = at;

        if (!cache.EnterWorld())
        {
            cache.Destroy();
            return null;
        }

        // A hand-spawned object has a dynamic guid and no generator, so the landblock heartbeat would rot it mid-window.
        cache.TimeToRot = -1;
        return cache;
    }

    public void DeliverDirect(GauntletRun run)
    {
        var window = BuildWindow(run, secondsOverride: 60, out _);
        var eligible = run.EligibleCharacters();
        var offline = 0;

        foreach (var guid in eligible)
        {
            var player = PlayerManager.GetOnlinePlayer(guid);

            if (player == null)
            {
                offline++;
                continue;
            }

            var (accountId, ip, household) = GauntletCacheHandler.Identify(player);

            GauntletClaims.Claim(window, null, player, accountId, ip, household);
        }

        log.Info($"[GAUNTLET] run {run.RunKey} lost its instance: {eligible.Count} eligible, {offline} offline (paid nothing), claims submitted for {eligible.Count - offline}; profile {window.Profile} x{window.Multiplier:0.###}");

        // The claims finish as the database answers; the window stays open for them and shuts itself after its length.
    }

    public void EndRewarding(GauntletRun run)
    {
        if (!runs.TryRemove(run.RunKey, out var state))
            return;

        List<WorldObject> caches;

        lock (state.Sync)
        {
            state.Window.Close();
            caches = new List<WorldObject>(state.Caches);
            state.Caches.Clear();
        }

        foreach (var cache in caches)
            GauntletCacheHandler.Unregister(cache.Guid.Full);

        log.Info($"[GAUNTLET] run {run.RunKey} rewarding over: {state.Window.Paid} paid, {caches.Count} cache(s) removed");

        if (caches.Count == 0)
            return;

        var centre = run.CentrePosition;
        var lb = centre == null || run.Instance == 0 ? null : LandblockManager.GetLoadedLandblock(new LandblockId(centre.Cell & 0xFFFF0000u), run.Instance);

        if (lb == null)
            return;

        lb.EnqueueAction(new ActionEventDelegate(() =>
        {
            foreach (var cache in caches)
            {
                try
                {
                    if (!cache.IsDestroyed)
                        cache.Destroy();
                }
                catch (Exception ex)
                {
                    log.Debug($"[GAUNTLET] run {run.RunKey}: a cache could not be destroyed", ex);
                }
            }
        }));
    }
}
