using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;

using ACE.Server.WorldObjects;

namespace ACE.Server.Avatars.Gauntlet;

/// <summary>
/// The read side of the Avatar fighter's aura (owner direction 2026-11-25: "a general boon to the team while alive"): members
/// standing within allegiance_gauntlet_fighter_aura_radius of the LIVING fighter (alive, not held in the death pen, in the
/// run's copy) gain allegiance_gauntlet_fighter_aura_damage_rating and allegiance_gauntlet_fighter_aura_resist_rating, read
/// at the moment the rating is (Creature_Rating, beside the Thread Shrine blessings, players only). There is no enchantment
/// and nothing persisted, so nothing can be dispelled, outlive the copy, or need cleaning up.
///
/// The fighter decides membership on its own landblock thread, every <see cref="PublishSeconds"/>, and publishes an
/// immutable <see cref="Snapshot"/> keyed by the copy's instance: the whole map is replaced with a compare-exchange, so a
/// rating read on any other landblock thread sees either the old map or the new one, never a half-built set. A snapshot
/// older than <see cref="StaleSeconds"/> counts for nothing, so a fighter that stops publishing (left the world, its tick
/// stalled) takes its aura with it; a death or the run's removal clears it at once.
///
/// Only a Gauntlet copy ever holds a snapshot, so the aura applies only there. PvP IS possible inside a copy: nothing in the
/// Gauntlet changes a member's PlayerKillerStatus (the only PK write under AllegianceAvatar/Gauntlet is the fighter's own
/// RubberGlue, GauntletAvatarFighter.BuildFighterWeenie), and Player.CheckPKStatusVsTarget (Player_Combat.cs) has no
/// Gauntlet gate, so two PK members can hurt each other there. The aura is therefore held to the class-abilities-never-PvP
/// rule the way GetClassAbilityRating is: a member whose class abilities are suppressed (Player.ClassAbilitySuppressed, the
/// PK facet rule or the arena mask) gets none of it (<see cref="IsSuppressed"/>). Hot path: every read first checks whether any snapshot exists at all, one
/// volatile read when no Gauntlet fighter is up.
/// </summary>
public static class GauntletAura
{
    /// <summary>Seconds between a fighter's membership publishes.</summary>
    public const double PublishSeconds = 1.0;

    /// <summary>A snapshot older than this is ignored (three missed publishes).</summary>
    public const double StaleSeconds = 3.0;

    /// <summary>One fighter's published aura: who is inside it, how much it gives, and when it was decided. Immutable.</summary>
    public sealed class Snapshot
    {
        public Snapshot(uint runKey, uint instance, object source, double publishedAt, int damageRating, int resistRating, IEnumerable<uint> members)
        {
            RunKey = runKey;
            Instance = instance;
            Source = source;
            PublishedAt = publishedAt;
            DamageRating = Math.Max(0, damageRating);
            ResistRating = Math.Max(0, resistRating);
            Members = members == null ? ImmutableHashSet<uint>.Empty : ImmutableHashSet.CreateRange(members);
        }

        public uint RunKey { get; }
        public uint Instance { get; }

        /// <summary>The fighter that published it: its death clears only its own snapshot.</summary>
        public object Source { get; }

        /// <summary>Unix seconds (<see cref="Now"/>) when the membership was decided.</summary>
        public double PublishedAt { get; }

        public int DamageRating { get; }
        public int ResistRating { get; }
        public ImmutableHashSet<uint> Members { get; }
    }

    private static ImmutableDictionary<uint, Snapshot> published = ImmutableDictionary<uint, Snapshot>.Empty;

    /// <summary>Test seam: the clock snapshots are stamped and judged with (unix seconds).</summary>
    internal static Func<double> Now = ACE.Common.Time.GetUnixTime;

    /// <summary>The fast gate every read takes first: true while any fighter has a snapshot up.</summary>
    public static bool AnyLive => !Volatile.Read(ref published).IsEmpty;

    /// <summary>Test seam: the snapshot published for an instance, or null.</summary>
    internal static Snapshot For(uint instance) => Volatile.Read(ref published).TryGetValue(instance, out var s) ? s : null;

    /// <summary>Replaces the instance's snapshot (any thread). A null or instance-0 snapshot is ignored.</summary>
    public static void Publish(Snapshot snapshot)
    {
        if (snapshot == null || snapshot.Instance == 0)
            return;

        Swap(map => map.SetItem(snapshot.Instance, snapshot));
    }

    /// <summary>Removes the snapshot <paramref name="source"/> published, if it is still the one up. True when one went.</summary>
    public static bool ClearFrom(object source)
    {
        if (source == null)
            return false;

        return RemoveWhere(s => ReferenceEquals(s.Source, source));
    }

    /// <summary>Removes every snapshot of a run (its removal). True when one went.</summary>
    public static bool ClearRun(uint runKey) => RemoveWhere(s => s.RunKey == runKey);

    /// <summary>Test seam: removes every snapshot.</summary>
    internal static void ResetForTests() => Interlocked.Exchange(ref published, ImmutableDictionary<uint, Snapshot>.Empty);

    // ---- the reads (hot path) ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The PvP gate, the class-ability one: true when <paramref name="player"/>'s class abilities are suppressed (the PK facet
    /// rule or the arena mask, Player.ClassAbilitySuppressed). Test seam; read only after <see cref="AnyLive"/>.
    /// </summary>
    internal static Func<Player, bool> IsSuppressed = p => p.ClassAbilitySuppressed;

    /// <summary>Damage rating <paramref name="player"/> gains from a fighter's aura right now (0 outside one, or while suppressed).</summary>
    public static int DamageRatingFor(Player player)
    {
        if (!AnyLive || player == null)
            return 0;

        var instance = player.Location?.Instance ?? 0;

        if (Member(player.Guid.Full, instance) == null || IsSuppressed(player))
            return 0;

        return DamageRatingFor(player.Guid.Full, instance);
    }

    /// <summary>Damage resist rating <paramref name="player"/> gains from a fighter's aura right now (0 outside one, or while suppressed).</summary>
    public static int DamageResistRatingFor(Player player)
    {
        if (!AnyLive || player == null)
            return 0;

        var instance = player.Location?.Instance ?? 0;

        if (Member(player.Guid.Full, instance) == null || IsSuppressed(player))
            return 0;

        return DamageResistRatingFor(player.Guid.Full, instance);
    }

    /// <summary>The Player-free core of <see cref="DamageRatingFor(Player)"/>.</summary>
    internal static int DamageRatingFor(uint guid, uint instance) => Member(guid, instance)?.DamageRating ?? 0;

    /// <summary>The Player-free core of <see cref="DamageResistRatingFor(Player)"/>.</summary>
    internal static int DamageResistRatingFor(uint guid, uint instance) => Member(guid, instance)?.ResistRating ?? 0;

    /// <summary>The snapshot that covers <paramref name="guid"/> in <paramref name="instance"/> now, or null.</summary>
    private static Snapshot Member(uint guid, uint instance)
    {
        if (instance == 0)
            return null;

        var map = Volatile.Read(ref published);

        if (map.IsEmpty || !map.TryGetValue(instance, out var s))
            return null;

        return Covers(s, guid, Now()) ? s : null;
    }

    /// <summary>Pure: true when <paramref name="s"/> is fresh at <paramref name="now"/> and lists <paramref name="guid"/>.</summary>
    internal static bool Covers(Snapshot s, uint guid, double now)
    {
        if (s == null || !IsFresh(s.PublishedAt, now))
            return false;

        return s.Members.Contains(guid);
    }

    /// <summary>Pure: a snapshot counts for <see cref="StaleSeconds"/> after it was published (a clock step back counts as fresh).</summary>
    internal static bool IsFresh(double publishedAt, double now) => double.IsFinite(publishedAt) && now - publishedAt <= StaleSeconds;

    // ---- membership (pure) ----------------------------------------------------------------------------------------------------

    /// <summary>One member as the aura sees them: who, whether they are down (dead or held), and where.</summary>
    public readonly record struct AuraCandidate(uint Guid, bool Down, ACE.Entity.Position At);

    /// <summary>
    /// The aura's rule for one member: the fighter alive, the member not down, in the fighter's instance, and within
    /// <paramref name="radius"/> metres. A radius of 0 or less (or not a number) is the aura off.
    /// </summary>
    public static bool IsAuraMember(bool fighterAlive, bool memberDown, bool sameInstance, double distance, double radius)
    {
        if (!fighterAlive || memberDown || !sameInstance)
            return false;

        if (!double.IsFinite(radius) || !(radius > 0) || !double.IsFinite(distance))
            return false;

        return distance <= radius;
    }

    /// <summary>The members inside the aura of a fighter at <paramref name="fighterAt"/> (pure over its inputs).</summary>
    public static HashSet<uint> Members(IEnumerable<AuraCandidate> candidates, ACE.Entity.Position fighterAt, bool fighterAlive, double radius)
    {
        var inside = new HashSet<uint>();

        if (candidates == null || fighterAt == null)
            return inside;

        foreach (var c in candidates)
        {
            if (c.At == null)
                continue;

            var sameInstance = c.At.Instance == fighterAt.Instance;
            var distance = sameInstance ? fighterAt.DistanceTo(c.At) : double.PositiveInfinity;

            if (IsAuraMember(fighterAlive, c.Down, sameInstance, distance, radius))
                inside.Add(c.Guid);
        }

        return inside;
    }

    // ---- player-facing text (orchestrator-owned strings; every number from the run's tunables) ------------------------------

    /// <summary>True when an aura of this radius and these ratings gives anything (the text is shown only then).</summary>
    public static bool IsOn(double radius, long damageRating, long resistRating) =>
        double.IsFinite(radius) && radius > 0 && (damageRating > 0 || resistRating > 0);

    /// <summary>A radius as the text shows it: a whole number with no decimals, else one decimal (30 -> "30", 12.5 -> "12.5").</summary>
    public static string RadiusText(double radius) => radius.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Said to the fighter's audience each time the Gauntlet places it (the first spawn and every return).</summary>
    public static string PlacedLine(double radius, long damageRating, long resistRating) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "The Avatar stands with you. Fight within {0} meters of it for +{1} damage rating and +{2} damage resistance rating.",
            RadiusText(radius), damageRating, resistRating);

    /// <summary>Appended to the run's death line while the aura is on.</summary>
    public const string FallenSuffix = " Its blessing fades until it returns.";

    /// <summary>The status command's aura line while a run is live; with " (fallen - returns in Ns)" while the fighter is down.</summary>
    public static string StatusLine(GauntletAuraInfo aura)
    {
        if (aura == null || !IsOn(aura.Radius, aura.DamageRating, aura.ResistRating))
            return null;

        var line = string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "Avatar's blessing: +{0} damage, +{1} damage resistance within {2} m while it stands.",
            aura.DamageRating, aura.ResistRating, RadiusText(aura.Radius));

        if (aura.FallenSecondsLeft is { } left)
            line += string.Format(System.Globalization.CultureInfo.InvariantCulture, " (fallen - returns in {0}s)", Math.Max(0, left));

        return line;
    }

    // ---- the swap -----------------------------------------------------------------------------------------------------------

    private static void Swap(Func<ImmutableDictionary<uint, Snapshot>, ImmutableDictionary<uint, Snapshot>> change)
    {
        while (true)
        {
            var current = Volatile.Read(ref published);
            var next = change(current);

            if (ReferenceEquals(next, current) || ReferenceEquals(Interlocked.CompareExchange(ref published, next, current), current))
                return;
        }
    }

    private static bool RemoveWhere(Func<Snapshot, bool> match)
    {
        var removed = false;

        Swap(map =>
        {
            removed = false;
            var next = map;

            foreach (var kv in map)
            {
                if (match(kv.Value))
                {
                    next = next.Remove(kv.Key);
                    removed = true;
                }
            }

            return next;
        });

        return removed;
    }
}
