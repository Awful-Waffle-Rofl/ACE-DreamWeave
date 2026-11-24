using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

using log4net;

using ACE.Server.ClassAbilities;

namespace ACE.Server.Managers.Market
{
    // The web class ability panel's wire shapes and its PURE half (Docs/Market/market-api-v1.yaml, schemas
    // ClassAbilityPanel*). Serialized with MarketApiHost.Json (snake_case), so CharacterGuid is character_guid.
    // The nullable refusal fields are written as an explicit null rather than omitted (the host omits nulls by
    // default), because the contract documents them as "null otherwise".

    public sealed class ClassAbilityPanelSnapshot
    {
        public uint CharacterGuid { get; set; }
        public string Name { get; set; }
        public int Level { get; set; }
        public bool Online { get; set; }
        public string Revision { get; set; }
        public bool Writable { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public string WriteRefusal { get; set; }

        public int PointsAvailable { get; set; }
        public int PointsEarned { get; set; }
        public List<ClassAbilityPanelClassRow> Classes { get; set; } = new();
        public List<ClassAbilityPanelAbilityRow> Abilities { get; set; } = new();
    }

    public sealed class ClassAbilityPanelClassRow
    {
        public string Class { get; set; }
        public int PointsSpent { get; set; }
        public List<ClassAbilityPanelTierRow> Tiers { get; set; } = new();
    }

    public sealed class ClassAbilityPanelTierRow
    {
        public int Tier { get; set; }
        public bool Unlocked { get; set; }

        /// <summary>Lifetime class ability points earned this tier needs (class_ability_tier{N}_cap_required); 0 for tier 1.</summary>
        public int EarnedRequired { get; set; }

        /// <summary>Points spent in this class this tier needs (class_ability_tier{N}_spent_required); 0 for tier 1.</summary>
        public int SpentRequired { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public string Requirement { get; set; }
    }

    public sealed class ClassAbilityPanelAbilityRow
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Class { get; set; }
        public int Tier { get; set; }
        public string Category { get; set; }
        public int MaxRank { get; set; }
        public int Rank { get; set; }
        public int[] CostPerRank { get; set; }
        public bool CanRaise { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public string RaiseRefusal { get; set; }

        public bool CanLower { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public string LowerRefusal { get; set; }

        /// <summary>
        /// The live "what it does right now" numbers (IAbilityReadout), or null. Only an ONLINE snapshot of an owned
        /// rank whose handler reports a value carries one; null is omitted from the wire (the host's default), so the
        /// key is simply absent otherwise.
        /// </summary>
        public ClassAbilityPanelReadoutRow Readout { get; set; }

        /// <summary>
        /// The ability's declared ceilings at its max rank, from the tunable values the snapshot was built with (live in
        /// production). Static, so offline snapshots carry them too. Null (omitted) for an ability that declares none.
        /// </summary>
        public List<ClassAbilityPanelCeilingRow> Ceilings { get; set; }
    }

    /// <summary>One declared ceiling. <c>max</c> is in display units (42 for 42%) and null when the cap reads "no limit"; <c>unit</c> is "%", "x" or "".</summary>
    public sealed class ClassAbilityPanelCeilingRow
    {
        public string Id { get; set; }
        public string Bounds { get; set; }
        public string Label { get; set; }
        public double? Max { get; set; }
        public string Unit { get; set; }

        /// <summary>True when the max is a base the affinity multiplier raises; omitted otherwise.</summary>
        public bool? Scales { get; set; }
    }

    /// <summary>One ceiling reading on an online readout: the raw value against its cap, display units, <c>max</c> null when uncapped.</summary>
    public sealed class ClassAbilityPanelCeilingReadingRow
    {
        public string Id { get; set; }
        public double Current { get; set; }
        public double? Max { get; set; }
        public bool Binding { get; set; }
    }

    /// <summary>
    /// One ability's readout in display units (ClassAbilityReadout): base is the rank's own term (Skill), effective
    /// is what combat uses after every clamp (Effective). Secondary readouts are not carried.
    /// </summary>
    public sealed class ClassAbilityPanelReadoutRow
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public string Prefix { get; set; }

        public double Base { get; set; }
        public double Effective { get; set; }
        public string Unit { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public string Label { get; set; }

        public bool Capped { get; set; }

        /// <summary>The affinity share of the readout (ClassAbilityReadout.Affinity), display units.</summary>
        public double Affinity { get; set; }

        /// <summary>The gear share of the readout (ClassAbilityReadout.Gear), display units.</summary>
        public double Gear { get; set; }

        /// <summary>Readings of the ability's declared ceilings (ClassAbilityReadout.Ceilings); empty when it reports none.</summary>
        public List<ClassAbilityPanelCeilingReadingRow> Ceilings { get; set; } = new();
    }

    /// <summary>The ?since fast-path answer.</summary>
    public sealed class ClassAbilityPanelUnchanged
    {
        public bool Unchanged { get; set; } = true;
        public string Revision { get; set; }
    }

    /// <summary>What one panel change is, judged from the request alone (from_rank / to_rank).</summary>
    public enum ClassAbilityPanelChangeKind
    {
        Invalid,
        Raise,
        Lower,
        Unlearn,
    }

    /// <summary>The Tier 2/3 unlock thresholds (class_ability_tier{2,3}_{cap,spent}_required) as plain values.</summary>
    public readonly struct ClassAbilityPanelTierRequirements
    {
        public ClassAbilityPanelTierRequirements(int tier2Earned, int tier2Spent, int tier3Earned, int tier3Spent)
        {
            Tier2Earned = tier2Earned;
            Tier2Spent = tier2Spent;
            Tier3Earned = tier3Earned;
            Tier3Spent = tier3Spent;
        }

        public int Tier2Earned { get; }
        public int Tier2Spent { get; }
        public int Tier3Earned { get; }
        public int Tier3Spent { get; }

        /// <summary>The live values. Reads PropertyManager, so the request or world thread only - never a unit test that has not seeded them.</summary>
        public static ClassAbilityPanelTierRequirements Live() => new ClassAbilityPanelTierRequirements(
            (int)PropertyManager.GetLong(ClassAbilityTierGate.Tier2EarnedKey).Item,
            (int)PropertyManager.GetLong(ClassAbilityTierGate.Tier2SpentKey).Item,
            (int)PropertyManager.GetLong(ClassAbilityTierGate.Tier3EarnedKey).Item,
            (int)PropertyManager.GetLong(ClassAbilityTierGate.Tier3SpentKey).Item);
    }

    /// <summary>
    /// What the snapshot builder reads off a character - a live Player on the world thread, or an offline import
    /// block. The Can* answers come from the character's own validators (Player.CanRaiseClassAbility /
    /// CanLowerClassAbility); the builder never re-derives a rule.
    /// </summary>
    public interface IClassAbilityPanelSubject
    {
        uint CharacterGuid { get; }
        string Name { get; }
        int Level { get; }
        string Revision { get; }
        int PointsAvailable { get; }
        int PointsEarned { get; }

        /// <summary>Every owned rank, by ability id. Absent means 0.</summary>
        IReadOnlyDictionary<ClassAbilityId, int> Ranks { get; }

        bool CanRaise(ClassAbilityDefinition ability, out string refusal);
        bool CanLower(ClassAbilityDefinition ability, out string refusal);

        /// <summary>
        /// The ability's IAbilityReadout at an owned <paramref name="rank"/> (above 0), or null when its handler has
        /// none. The builder asks only for an online snapshot and catches a throw.
        /// </summary>
        ClassAbilityReadout? Readout(ClassAbilityDefinition ability, int rank);
    }

    /// <summary>
    /// The panel's pure rules: the ability set, the change classification, the write-refusal sentences and the
    /// snapshot projection. No Player, no PropertyManager (except <see cref="ClassAbilityPanelTierRequirements.Live"/>,
    /// which callers pass in), so all of it is unit-tested.
    /// </summary>
    public static class ClassAbilityPanelModel
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>The abilities whose readout has already thrown and been logged, so a broken handler logs once, not per read.</summary>
        private static readonly ConcurrentDictionary<ClassAbilityId, bool> readoutFailuresLogged = new ConcurrentDictionary<ClassAbilityId, bool>();

        /// <summary>How many failure log lines each ability has written, counted at the log call itself.</summary>
        private static readonly ConcurrentDictionary<ClassAbilityId, int> readoutFailureLogLines = new ConcurrentDictionary<ClassAbilityId, int>();

        /// <summary>Test seam (InternalsVisibleTo, ACE.Server.csproj:15): the failure log lines written for one ability so far.</summary>
        internal static int ReadoutFailureLogLines(ClassAbilityId id) => readoutFailureLogLines.TryGetValue(id, out var n) ? n : 0;

        /// <summary>The log line's prefix when an ability's readout throws.</summary>
        public const string ReadoutFailureLogPrefix = "[MARKET][PANEL] class ability readout threw for";

        /// <summary>The revision every OFFLINE snapshot answers. Never equal to an online "epoch:counter" revision.</summary>
        public const string OfflineRevision = "offline";

        public const string OfflineRefusal = "This character is not online. Log it in to change its class abilities.";

        public const string NotLaunchSessionRefusal = "Open this page with /weblogin from this character in game to change its class abilities.";

        /// <summary>The panel's timeout sentence on a change: the outcome is unknown, so the client must reload, never retry.</summary>
        public const string ChangeTimeoutMessage = "The server did not confirm that change. Reload the panel to see whether it applied.";

        /// <summary>The panel's timeout sentence on a read.</summary>
        public const string ReadTimeoutMessage = "The server did not answer in time. Try again in a moment.";

        /// <summary>
        /// The world-thread eligibility rule for a panel change or a live read: the Player is online in this process,
        /// has a session, that session belongs to <paramref name="accountId"/>, is WorldConnected and still holds this
        /// same Player, and the Player is not logging out. Pure, so each clause is unit-tested; the live service only
        /// gathers the inputs (ClassAbilityPanelService.FindEligible).
        /// </summary>
        public static bool IsEligible(uint accountId, bool playerFound, bool hasSession, uint sessionAccountId,
            bool worldConnected, bool sessionHoldsPlayer, bool loggingOut)
        {
            return playerFound && hasSession && sessionAccountId == accountId && worldConnected && sessionHoldsPlayer && !loggingOut;
        }

        /// <summary>The compare-and-set: a change is stale when the live rank is not the from_rank the client sent.</summary>
        public static bool IsStale(int liveRank, int fromRank) => liveRank != fromRank;

        /// <summary>
        /// The panel set: every ability homed to a class (AbilityClass not None), Implemented, and not retired.
        /// Retired abilities are unregistered (they are absent from ClassAbilityRegistry.Abilities and live only in
        /// RetiredClassAbilities), and the retired-name check below is belt and braces. Ordered by class (the
        /// enum's order), then tier, then id.
        /// </summary>
        public static readonly IReadOnlyList<ClassAbilityDefinition> AbilitySet = BuildAbilitySet();

        /// <summary>The classes, in the server's class order (the enum's, None excluded).</summary>
        public static readonly IReadOnlyList<ClassAbilityClass> ClassOrder =
            Enum.GetValues(typeof(ClassAbilityClass)).Cast<ClassAbilityClass>().Where(c => c != ClassAbilityClass.None).ToArray();

        private static readonly HashSet<ClassAbilityId> abilitySetIds = new HashSet<ClassAbilityId>(AbilitySet.Select(d => d.Id));

        private static IReadOnlyList<ClassAbilityDefinition> BuildAbilitySet()
        {
            var retired = new HashSet<string>(RetiredClassAbilities.All().Select(r => r.Name), StringComparer.OrdinalIgnoreCase);

            return ClassAbilityRegistry.Abilities.Values
                .Where(d => d.AbilityClass != ClassAbilityClass.None && d.Implemented && !retired.Contains(d.Name))
                .OrderBy(d => (int)d.AbilityClass)
                .ThenBy(d => d.Tier)
                .ThenBy(d => (int)d.Id)
                .ToArray();
        }

        public static bool InSet(ClassAbilityDefinition def) => def != null && abilitySetIds.Contains(def.Id);

        /// <summary>A panel ability by its wire id (the registry Name). False for an unknown id or one outside the set.</summary>
        public static bool TryGetAbility(string id, out ClassAbilityDefinition def)
        {
            def = null;

            if (string.IsNullOrEmpty(id) || id.Length > 64 || !ClassAbilityRegistry.TryGetByName(id, out var found) || !InSet(found))
                return false;

            def = found;
            return true;
        }

        /// <summary>
        /// from_rank + 1 is a raise, 0 (from a held rank) an unlearn of every rank, from_rank - 1 (above 0) a
        /// one-rank lower; anything else is invalid. Lowering rank 1 to 0 therefore classifies as an unlearn,
        /// which leaves exactly the state a one-rank lower would and refunds the same single rank.
        /// </summary>
        public static ClassAbilityPanelChangeKind Classify(int fromRank, int toRank)
        {
            if (fromRank < 0 || toRank < 0)
                return ClassAbilityPanelChangeKind.Invalid;

            if (toRank == fromRank + 1)
                return ClassAbilityPanelChangeKind.Raise;

            if (toRank == 0 && fromRank >= 1)
                return ClassAbilityPanelChangeKind.Unlearn;

            if (toRank == fromRank - 1 && toRank > 0)
                return ClassAbilityPanelChangeKind.Lower;

            return ClassAbilityPanelChangeKind.Invalid;
        }

        /// <summary>
        /// The chat line a COMMITTED panel change sends its player: the in-game wording for the same change
        /// (ClassAbilityChangeMessages, shared with the trainer, the vendor and /abilities unlearn) plus the web panel
        /// marker. <paramref name="rankAfter"/> is the live rank once the change applied, and
        /// <paramref name="refunded"/> the points it returned (0 for a raise). Throws for Invalid, which never applies.
        /// </summary>
        public static string ChangeChatLine(ClassAbilityPanelChangeKind kind, ClassAbilityDefinition ability, int rankAfter, int refunded,
            int pointsAvailable, int pointsEarned)
        {
            var line = kind switch
            {
                ClassAbilityPanelChangeKind.Raise => ClassAbilityChangeMessages.Learned(ability.DisplayName, rankAfter, pointsAvailable),
                ClassAbilityPanelChangeKind.Lower => ClassAbilityChangeMessages.Lowered(ability.DisplayName, rankAfter, refunded, pointsAvailable, pointsEarned),
                ClassAbilityPanelChangeKind.Unlearn => ClassAbilityChangeMessages.Unlearned(ability.DisplayName, refunded, pointsAvailable, pointsEarned),
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "only an applied change has a chat line"),
            };

            return ClassAbilityChangeMessages.FromWebPanel(line);
        }

        public const string DeadRefusal = "You cannot change class abilities while dead.";
        public const string BusyRefusal = "You cannot change class abilities while busy.";
        public const string TeleportingRefusal = "You cannot change class abilities while teleporting.";
        public const string LoggingOutRefusal = "You cannot change class abilities while logging out.";

        /// <summary>
        /// The character-state refusal for a panel write (owner ruling 2026-11-08), or null: dead, busy (an animation or
        /// another action holds the character), teleporting, or in the PK delayed-logout countdown (Player.PKLogout).
        /// Reads stay allowed; this only makes the snapshot read-only and refuses a change with rule_refused.
        /// </summary>
        public static string StateRefusal(bool dead, bool busy, bool teleporting, bool pkLogout)
        {
            if (dead)
                return DeadRefusal;

            if (pkLogout)
                return LoggingOutRefusal;

            if (teleporting)
                return TeleportingRefusal;

            if (busy)
                return BusyRefusal;

            return null;
        }

        /// <summary>
        /// Why a POST from this session could not apply a change right now, or null when it could. In order: a launch
        /// session bound to this character, online, the character's state (<paramref name="stateRefusal"/>,
        /// <see cref="StateRefusal"/>), then the location refusal (<paramref name="areaRefusal"/>,
        /// MuleSummonHandler.ClassAbilityPanelAreaRefusal, null when permitted).
        /// </summary>
        public static string WriteRefusal(bool launchBound, bool online, string stateRefusal, string areaRefusal)
        {
            if (!launchBound)
                return NotLaunchSessionRefusal;

            if (!online)
                return OfflineRefusal;

            return stateRefusal ?? areaRefusal;
        }

        /// <summary>
        /// The two thresholds a tier needs, from the same values the tier gate reads (Player.MeetsClassAbilityTierUnlock
        /// reads the same four keys; <see cref="ClassAbilityPanelTierRequirements.Live"/> carries them here). Tier 1 needs
        /// nothing: (0, 0).
        /// </summary>
        public static (int EarnedRequired, int SpentRequired) TierThresholds(int tier, ClassAbilityPanelTierRequirements req)
        {
            if (tier <= 1)
                return (0, 0);

            return tier >= 3 ? (req.Tier3Earned, req.Tier3Spent) : (req.Tier2Earned, req.Tier2Spent);
        }

        /// <summary>The tier row's requirement sentence for a locked tier, or null when the tier is unlocked.</summary>
        public static string TierRequirement(ClassAbilityClass cls, int tier, int totalEarned, int spentInClass, ClassAbilityPanelTierRequirements req)
        {
            if (tier <= 1)
                return null;

            var (earnedRequired, spentRequired) = TierThresholds(tier, req);

            switch (ClassAbilityTierGate.CheckTierUnlock(totalEarned, spentInClass, earnedRequired, spentRequired))
            {
                case ClassAbilityTierGate.TierUnlockShortfall.Earned:
                    return $"Tier {tier} {cls} abilities unlock once you have earned at least {earnedRequired:N0} class ability points (you have earned {totalEarned:N0}).";

                case ClassAbilityTierGate.TierUnlockShortfall.Spent:
                    return $"Tier {tier} {cls} abilities unlock once you have spent at least {spentRequired:N0} points on {cls} abilities (you have spent {spentInClass:N0}).";

                default:
                    return null;
            }
        }

        /// <summary>
        /// Points spent in a class: the cumulative cost of every owned rank of every REGISTERED ability of that
        /// class, the figure Player.PointsSpentInClass computes for the tier gate.
        /// </summary>
        public static int PointsSpentInClass(IReadOnlyDictionary<ClassAbilityId, int> ranks, ClassAbilityClass cls)
        {
            var spent = 0;

            foreach (var (id, rank) in ranks)
            {
                if (rank <= 0 || !ClassAbilityRegistry.Abilities.TryGetValue(id, out var def) || def.AbilityClass != cls)
                    continue;

                spent += def.CumulativeCost(rank);
            }

            return spent;
        }

        /// <summary>
        /// The wire readout for one handler answer, or null when there is nothing to show: no handler readout, no value
        /// to show, or a non-finite number (System.Text.Json refuses NaN and infinity, which would fail the whole read).
        /// A primary with HasValue false but a Secondary list shows Secondary[0] in its place, because /abilities list
        /// prints the secondaries alone in that case (ClassAbilityCommands.FormatReadoutLine; a secondary's HasValue is
        /// implicitly true per ClassAbilityReadout.Secondary). Prefix and label are null when empty; unit is never null.
        /// </summary>
        public static ClassAbilityPanelReadoutRow ToReadoutRow(ClassAbilityReadout? readout, ClassAbilityDefinition def)
        {
            if (readout is not { } primary)
                return null;

            ClassAbilityReadout r;
            if (primary.HasValue)
                r = primary;
            else if (primary.Secondary is { Count: > 0 } secondaries)
                r = secondaries[0];
            else
                return null;

            if (!double.IsFinite(r.Skill) || !double.IsFinite(r.Effective) || !double.IsFinite(r.Affinity) || !double.IsFinite(r.Gear))
                return null;

            return new ClassAbilityPanelReadoutRow
            {
                Prefix = string.IsNullOrEmpty(r.Prefix) ? null : r.Prefix,
                Base = r.Skill,
                Effective = r.Effective,
                Unit = r.Unit ?? string.Empty,
                Label = string.IsNullOrEmpty(r.Label) ? null : r.Label,
                Capped = r.Capped,
                Affinity = r.Affinity,
                Gear = r.Gear,
                // The ceilings belong to the ability, not to a secondary segment standing in for a value-less primary.
                Ceilings = (primary.Ceilings ?? Array.Empty<ClassAbilityCeilingReading>())
                    .Where(c => double.IsFinite(c.Current) && double.IsFinite(c.Max))
                    .Select(c => new ClassAbilityPanelCeilingReadingRow
                    {
                        Id = c.Id,
                        Current = c.Current,
                        Max = ClassAbilityCeilingText.IsUnlimited(def, c) ? null : c.Max,
                        Binding = c.Binding,
                    })
                    .ToList(),
            };
        }

        /// <summary>The static ceiling rows for an ability at its max rank, or null when it declares none.</summary>
        public static List<ClassAbilityPanelCeilingRow> CeilingRows(ClassAbilityDefinition def, ITunableValues values)
        {
            if (def.Ceilings == null || def.Ceilings.Count == 0)
                return null;

            var rank = def.MaxRank > 0 ? def.MaxRank : 1;
            return def.Ceilings
                .Select(c => new ClassAbilityPanelCeilingRow
                {
                    Id = c.Id,
                    Bounds = c.Bounds.ToString().ToLowerInvariant(),
                    Label = c.Label,
                    Max = c.IsUncapped(values, rank) ? null : System.Math.Round(c.DisplayValue(values, rank), 4),
                    Unit = ClassAbilityCeilingText.UnitOf(c),
                    Scales = c.AffinityScalesMax ? true : null,
                })
                .ToList();
        }

        /// <summary>
        /// The readout row for an owned ability on an online snapshot, or null. A throwing handler costs only its own
        /// row's readout (logged once per ability per process); the read itself still answers.
        /// </summary>
        private static ClassAbilityPanelReadoutRow ReadoutFor(IClassAbilityPanelSubject subject, ClassAbilityDefinition def, int rank)
        {
            try
            {
                return ToReadoutRow(subject.Readout(def, rank), def);
            }
            catch (Exception ex)
            {
                if (readoutFailuresLogged.TryAdd(def.Id, true))
                {
                    log.Warn($"{ReadoutFailureLogPrefix} {def.Name} at rank {rank}; the panel omits it: {ex}");
                    readoutFailureLogLines.AddOrUpdate(def.Id, 1, (_, n) => n + 1);
                }

                return null;
            }
        }

        /// <summary>
        /// The full snapshot. <paramref name="writeRefusal"/> null means writable (it must then be online); when
        /// it is set every can_* is false and every *_refusal carries that same sentence, so a client that only
        /// displays the per-ability fields still shows the right reason.
        /// </summary>
        public static ClassAbilityPanelSnapshot Build(IClassAbilityPanelSubject subject, bool online, string writeRefusal, ClassAbilityPanelTierRequirements req, ITunableValues ceilingValues = null)
        {
            // The service passes ClassAbilityText.Live so the panel quotes what combat applies; a bare Build (the pure rules'
            // callers) uses the code defaults and never reads PropertyManager.
            ceilingValues ??= ClassAbilityText.Defaults;

            if (!online && writeRefusal == null)
                writeRefusal = OfflineRefusal;

            var writable = writeRefusal == null;
            var ranks = subject.Ranks ?? new Dictionary<ClassAbilityId, int>();

            var snapshot = new ClassAbilityPanelSnapshot
            {
                CharacterGuid = subject.CharacterGuid,
                Name = subject.Name ?? string.Empty,
                Level = subject.Level,
                Online = online,
                Revision = subject.Revision,
                Writable = writable,
                WriteRefusal = writeRefusal,
                PointsAvailable = subject.PointsAvailable,
                PointsEarned = subject.PointsEarned,
            };

            foreach (var cls in ClassOrder)
            {
                var spent = PointsSpentInClass(ranks, cls);
                var row = new ClassAbilityPanelClassRow { Class = cls.ToString(), PointsSpent = spent };

                for (var tier = 1; tier <= 3; tier++)
                {
                    var requirement = TierRequirement(cls, tier, subject.PointsEarned, spent, req);
                    var (earnedRequired, spentRequired) = TierThresholds(tier, req);
                    row.Tiers.Add(new ClassAbilityPanelTierRow
                    {
                        Tier = tier,
                        Unlocked = requirement == null,
                        EarnedRequired = earnedRequired,
                        SpentRequired = spentRequired,
                        Requirement = requirement,
                    });
                }

                snapshot.Classes.Add(row);
            }

            foreach (var def in AbilitySet)
            {
                ranks.TryGetValue(def.Id, out var rank);

                var abilityRow = new ClassAbilityPanelAbilityRow
                {
                    Id = def.Name,
                    Name = def.DisplayName,
                    Class = def.AbilityClass.ToString(),
                    Tier = def.Tier,
                    Category = def.Category,
                    MaxRank = def.MaxRank,
                    // The live rank, unclamped. Defensive only: the rank cache (Player.GetClassAbilityCache) and the
                    // offline import (ClassAbilityImportProjection) already clamp to max_rank at load, so the rank the
                    // compare-and-set reads and the rank sent here agree either way.
                    Rank = Math.Max(0, rank),
                    CostPerRank = def.CostPerRank.Take(def.MaxRank).ToArray(),
                    Ceilings = CeilingRows(def, ceilingValues),
                };

                // Online only: an offline import block has no Player to compute a live number from.
                if (online && abilityRow.Rank > 0)
                    abilityRow.Readout = ReadoutFor(subject, def, abilityRow.Rank);

                if (writable)
                {
                    abilityRow.CanRaise = subject.CanRaise(def, out var raiseRefusal);
                    abilityRow.RaiseRefusal = abilityRow.CanRaise ? null : raiseRefusal;

                    abilityRow.CanLower = subject.CanLower(def, out var lowerRefusal);
                    abilityRow.LowerRefusal = abilityRow.CanLower ? null : lowerRefusal;
                }
                else
                {
                    abilityRow.RaiseRefusal = writeRefusal;
                    abilityRow.LowerRefusal = writeRefusal;
                }

                snapshot.Abilities.Add(abilityRow);
            }

            return snapshot;
        }
    }
}
