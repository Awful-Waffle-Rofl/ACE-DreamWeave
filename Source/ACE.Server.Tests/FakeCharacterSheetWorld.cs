using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

using ACE.Server.Managers.CharacterSheets;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Stand-in for the live world: presence per character, canned sheets, call counters, and gates that
    /// hold an offline build open so a second request can be driven through the window deterministically
    /// (never a timed sleep - a starved pool can finish a "slow" call before the probe lands).
    /// </summary>
    internal sealed class FakeCharacterSheetWorld : ICharacterSheetWorld
    {
        /// <summary>A character absent from this map is Missing.</summary>
        public ConcurrentDictionary<uint, CharacterPresence> PresenceOf { get; } = new ConcurrentDictionary<uint, CharacterPresence>();

        public volatile bool BlockOfflineBuilds;
        public volatile bool OnlineReturnsNull;
        public volatile bool OfflineReturnsNull;
        public volatile bool ThrowOnPresence;
        public volatile bool ThrowOnOfflineBuild;
        public volatile bool ThrowOnRankBuild;

        /// <summary>Runs inside every online/offline build, e.g. to advance the test's monotonic clock.</summary>
        public Action DuringBuild;

        /// <summary>What BuildRankSnapshot answers; null = not built in time. Defaults to a real, empty snapshot.</summary>
        public IReadOnlyDictionary<uint, List<SheetRank>> RankSnapshot = new Dictionary<uint, List<SheetRank>>();

        public readonly ManualResetEventSlim OfflineBuildStarted = new ManualResetEventSlim(false);
        private readonly ManualResetEventSlim offlineBuildGate = new ManualResetEventSlim(false);

        public int PresenceCount;
        public int OnlineBuildCount;
        public int OfflineBuildCount;
        public int RankBuildCount;

        public bool? LastIncludeClassAbilities;
        public bool? LastOwnerView;
        public TimeSpan LastBuildTimeout;
        public TimeSpan LastRankTimeout;

        public void ReleaseOfflineBuilds() => offlineBuildGate.Set();

        public CharacterPresence Presence(uint characterGuid)
        {
            Interlocked.Increment(ref PresenceCount);

            if (ThrowOnPresence)
                throw new InvalidOperationException("presence lookup failed");

            return PresenceOf.TryGetValue(characterGuid, out var presence) ? presence : CharacterPresence.Missing;
        }

        public CharacterSheet BuildOnline(uint characterGuid, bool includeClassAbilities, TimeSpan timeout, bool ownerView = false)
        {
            Interlocked.Increment(ref OnlineBuildCount);
            LastIncludeClassAbilities = includeClassAbilities;
            LastBuildTimeout = timeout;
            DuringBuild?.Invoke();

            LastOwnerView = ownerView;

            return OnlineReturnsNull ? null : SheetFor(characterGuid, ownerView);
        }

        public CharacterSheet BuildOffline(uint characterGuid, bool includeClassAbilities, TimeSpan timeout, bool ownerView = false)
        {
            Interlocked.Increment(ref OfflineBuildCount);
            LastIncludeClassAbilities = includeClassAbilities;
            LastBuildTimeout = timeout;

            if (BlockOfflineBuilds)
            {
                OfflineBuildStarted.Set();
                offlineBuildGate.Wait(TimeSpan.FromSeconds(10));
            }

            DuringBuild?.Invoke();

            if (ThrowOnOfflineBuild)
                throw new InvalidOperationException("offline build failed");

            LastOwnerView = ownerView;

            return OfflineReturnsNull ? null : SheetFor(characterGuid, ownerView);
        }

        public IReadOnlyDictionary<uint, List<SheetRank>> BuildRankSnapshot(TimeSpan timeout)
        {
            Interlocked.Increment(ref RankBuildCount);
            LastRankTimeout = timeout;

            if (ThrowOnRankBuild)
                throw new InvalidOperationException("rank build failed");

            return RankSnapshot;
        }

        // a fresh instance per build, so ranks attached to one sheet can never bleed into another
        private static CharacterSheet SheetFor(uint characterGuid, bool ownerView = false)
        {
            var sheet = new CharacterSheet { Name = $"Character{characterGuid:X8}", Level = 200, GeneratedAt = new DateTime(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc) };

            if (ownerView)
                sheet.Owner = new OwnerProfile { Name = sheet.Name, Level = sheet.Level, Heritage = "Aluvian" };

            return sheet;
        }
    }
}
