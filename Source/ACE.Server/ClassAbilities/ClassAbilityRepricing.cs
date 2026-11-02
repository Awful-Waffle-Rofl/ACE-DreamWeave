using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Database.Models.Shard;
using ACE.Server.Entity.Facets;

namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// What a reprice wave does to an owner of one of its abilities.
    /// </summary>
    public enum RepriceAction
    {
        /// <summary>
        /// The owner KEEPS the rank they hold. The ability's per-rank costs may have changed; the points
        /// recompute (<see cref="ClassAbilityRepricing.RecomputePoints"/>) prices the kept rank at the new
        /// ladder, so a price drop is returned without any per-ability refund arithmetic. Carried tokens are
        /// taken back only for a tier whose price changed.
        /// </summary>
        Keep,

        /// <summary>
        /// The ability was FUNCTIONALLY CHANGED, so the owner's ranks are removed outright (rank 0) and the
        /// points recompute returns what they cost. The ability is also stripped from every stored facet
        /// build, and every carried token for it (any tier the old ladder had) is taken back, whatever its
        /// price did. The player re-learns it, at the new price, if they still want it.
        /// </summary>
        Unlearn,

        /// <summary>
        /// The owner is RAISED to the highest rank the NEW ladder sells for what their owned rank cost at the
        /// OLD prices (<see cref="ClassAbilityRepricing.PromotedRank"/>): never lowered, never above the new
        /// ladder's top. For a single expensive rank split into several cheap ones (Taunt: one rank at 3 ->
        /// three at 1/1/1) that is the full new ladder at exactly the price already paid, so the points
        /// recompute moves nothing. Otherwise it behaves as <see cref="Keep"/>: no facet strip, and carried
        /// tokens taken back only for a tier whose price changed. Stored facet builds are NOT promoted -
        /// a stored build holds ranks but no points (the swap model released them), so its rank swaps in
        /// at the live price exactly as a Keep row's does.
        /// </summary>
        PromoteToPaid,
    }

    /// <summary>
    /// One owned ability a pending wave PROMOTES (<see cref="RepriceAction.PromoteToPaid"/>): the raw rank
    /// the character logged in with and the rank it is raised to.
    /// </summary>
    public readonly struct PromoteStep
    {
        public int Wave { get; init; }
        public ClassAbilityId Ability { get; init; }
        public int OldRank { get; init; }
        public int NewRank { get; init; }

        /// <summary>What the old rank cost at the wave's OLD prices (for the ledger detail).</summary>
        public int OldPaid { get; init; }
    }

    /// <summary>
    /// One ability in one wave. <see cref="OldCosts"/> are PINNED VERBATIM - the prices in force before the
    /// wave, which no longer exist anywhere else once the definition is edited; they decide which carried
    /// tokens were bought at a changed price and what a token was paid. <see cref="NewCosts"/> are the
    /// prices the wave moves to; the newest wave naming an ability must match its live
    /// ClassAbilityDefinition.CostPerRank (ClassAbilityRepricingTests pins that).
    /// </summary>
    public sealed class RepriceRow
    {
        public ClassAbilityId Ability { get; }
        public IReadOnlyList<int> OldCosts { get; }
        public IReadOnlyList<int> NewCosts { get; }
        public RepriceAction Action { get; }

        public RepriceRow(ClassAbilityId ability, int[] oldCosts, int[] newCosts, RepriceAction action)
        {
            Ability = ability;
            OldCosts = oldCosts;
            NewCosts = newCosts;
            Action = action;
        }
    }

    /// <summary>
    /// One reprice wave: a number (the value stamped into PropertyInt.ClassAbilityRepriceWave once a
    /// character has been migrated through it), a label for logs and ledger rows, the player-facing subject
    /// and trainer phrases for the login chat line, and its rows.
    /// </summary>
    public sealed class RepriceWave
    {
        public int Number { get; }
        public string Label { get; }

        /// <summary>The chat line's subject, e.g. "Blood Mage abilities".</summary>
        public string Subject { get; }

        /// <summary>Where to re-learn, e.g. "the Blood Mage trainer".</summary>
        public string Trainer { get; }

        public IReadOnlyList<RepriceRow> Rows { get; }

        public RepriceWave(int number, string label, string subject, string trainer, params RepriceRow[] rows)
        {
            Number = number;
            Label = label;
            Subject = subject;
            Trainer = trainer;
            Rows = rows;
        }
    }

    /// <summary>
    /// One owned ability a pending wave unlearns: the rank removed and what it cost at the wave's OLD
    /// prices (for the ledger detail and the expected-delta diagnostic only - the points themselves come
    /// from <see cref="ClassAbilityRepricing.RecomputePoints"/>).
    /// </summary>
    public readonly struct UnlearnStep
    {
        public int Wave { get; init; }
        public ClassAbilityId Ability { get; init; }
        public int OldRank { get; init; }
        public int OldPaid { get; init; }
    }

    /// <summary>
    /// The result of the points recompute for one character. <see cref="NewAvailable"/> is what Available
    /// is set to; <see cref="Delta"/> = NewAvailable - CurrentAvailable is the single ledger row's
    /// delta_Available and the N in the chat line, and is never negative.
    /// </summary>
    public readonly struct RepricePoints
    {
        public int Recomputed { get; init; }
        public int CurrentAvailable { get; init; }
        public int NewAvailable { get; init; }
        public int Delta { get; init; }

        /// <summary>The floor: currentAvailable + the old-price refund (never below currentAvailable).</summary>
        public int Floor { get; init; }

        /// <summary>The old-price refund the floor guarantees (<see cref="ClassAbilityRepricing.ExpectedDelta"/>, clamped at 0).</summary>
        public int OldPriceRefund { get; init; }

        /// <summary>TRUE when the recompute came out BELOW the floor, so the floor term won.</summary>
        public bool FloorBinds { get; init; }
    }

    /// <summary>
    /// The CLASS ABILITY REPRICE MIGRATION as pure data and pure functions, so the arithmetic is
    /// unit-testable without a live Player (Player's static initializer cannot run under the test host).
    /// The Player-bound half - the facet read and strip, erasing ranks, the token sweep, the ledger rows,
    /// the guard stamp and the saves - is Player.ApplyClassAbilityRepricing (Player_ClassAbilities.cs), run
    /// at login after the overhaul respec and before the CAP audit.
    ///
    /// DATA-DRIVEN, SO A FUTURE REPRICE OR REWORK IS A NEW WAVE, NOT NEW CODE. Append a
    /// <see cref="RepriceWave"/> with the next number to <see cref="Waves"/>, pin the costs each ability had
    /// BEFORE it (verbatim) and the costs it moves to, choose Keep, Unlearn or PromoteToPaid per ability, and edit the
    /// definitions. Never edit a shipped wave: characters stamped past it were migrated against exactly
    /// those rows.
    ///
    /// THE POINTS ARE RECOMPUTED, NOT REFUNDED PER ABILITY. After the ranks are removed and the tokens are
    /// taken back, Available is set to
    ///
    ///     max( TotalEarned - ownedCostAtLivePrices - outstandingCarriedTokenValue ,
    ///          currentAvailable + oldPriceRefund )
    ///
    /// (<see cref="RecomputePoints"/>). Every class ability point a character ever earned is either still
    /// available, sunk in a rank it owns, or sunk in a prepaid token it has not applied yet: those are the
    /// only things CAP buys (a trainer's alternate-currency purchase is an instant-learn rank, and a voucher
    /// is a token until applied, then a rank). Pricing the owned ranks at the LIVE ladder is what returns a
    /// Keep row's price drop, and leaving the unlearned ranks and the taken-back tokens out of both terms is
    /// what returns them, all without needing to know what anything was paid.
    ///
    /// WHY NOT THE CAP AUDIT'S LEDGER SINK TERM. CapLedger.Unexplained subtracts the ledger's recorded
    /// VoucherBuy and VendorDebit spend, but an APPLIED voucher and a vendor instant-learn also sit in owned
    /// cost as a rank, so that identity counts them twice; it also cannot see spend from before the ledger
    /// existed, and needs an asynchronous read. The sink here is the token value still OUTSTANDING (what the
    /// exchanger would refund for every token the character still carries), which is exactly the spend not
    /// already represented by an owned rank.
    ///
    /// THE RECOMPUTE IS ABSOLUTE, SO IT IS CRASH-SAFE. Nothing in it depends on whether an earlier attempt
    /// partly persisted: a re-run after a crash sees whichever ranks and tokens survived and computes the
    /// same recompute from them. The FLOOR (current + the old-price refund, see <see cref="OldPriceRefund"/>)
    /// takes its unlearned part from the PENDING-REFUND MARKER (<see cref="MarkerRefundKey"/>), written in the
    /// same character save as the erase, so a re-run after a crash that persisted the erase but not the points
    /// still pays it. The stamp and the points share one biota save, so a lost stamp never re-pays a floor,
    /// and a marker found with the stamp present is stale and erased unpaid.
    /// </summary>
    public static class ClassAbilityRepricing
    {
        /// <summary>
        /// Every reprice wave ever shipped, in ascending <see cref="RepriceWave.Number"/> order.
        /// </summary>
        public static readonly IReadOnlyList<RepriceWave> Waves = new[]
        {
            // Wave 1 - Blood Mage buff pass (2026-10-17). Old costs are the CostPerRank values on master
            // before this pass (ff32e4480). The five functionally changed abilities are UNLEARNED; the three
            // whose behaviour only shifted (Sanguine Ward's and Blood Price's price drop, Sanguine Reserve's
            // affinity skill) keep their ranks.
            new RepriceWave(1, "Blood Mage buff pass", "Blood Mage abilities", "the Blood Mage trainer",
                new RepriceRow(ClassAbilityId.Hemomancy,       new[] { 1, 1, 1, 1, 1 }, new[] { 1, 1, 1, 1, 1 }, RepriceAction.Unlearn),
                new RepriceRow(ClassAbilityId.WeakenedBlood,   new[] { 1, 1, 1 },       new[] { 1, 1, 1 },       RepriceAction.Unlearn),
                new RepriceRow(ClassAbilityId.Exsanguinate,    new[] { 3 },             new[] { 1, 1, 1 },       RepriceAction.Unlearn),
                new RepriceRow(ClassAbilityId.CrimsonHarvest,  new[] { 3 },             new[] { 1, 1, 1 },       RepriceAction.Unlearn),
                new RepriceRow(ClassAbilityId.HealBoostRating, new[] { 1, 1, 1 },       new[] { 1, 1, 1 },       RepriceAction.Unlearn),
                new RepriceRow(ClassAbilityId.SanguineWard,    new[] { 1, 2, 3 },       new[] { 1, 1, 1 },       RepriceAction.Keep),
                new RepriceRow(ClassAbilityId.BloodPrice,      new[] { 2, 2, 2 },       new[] { 1, 1, 1 },       RepriceAction.Keep),
                new RepriceRow(ClassAbilityId.SanguineReserve, new[] { 1, 1, 1 },       new[] { 1, 1, 1 },       RepriceAction.Keep)),

            // Wave 2 - Vanguard pass (2026-10-17). Taunt was a single rank at 3 and is now three ranks at
            // 1/1/1 whose rank buys RADIUS (15/30/65 m). Owner ruling: an owner paid 3, so they end at rank 3 -
            // the widest radius - with no net point change, and nobody loses points. PromoteToPaid raises
            // rank 1 (paid 3) to rank 3 (costs 3 at the new prices), so the recompute moves nothing; an
            // unused old Taunt token (tier 1, bought at 3) is taken back and its 3 points returned.
            new RepriceWave(2, "Vanguard Taunt ladder", "Vanguard abilities", "the Vanguard trainer",
                new RepriceRow(ClassAbilityId.Taunt, new[] { 3 }, new[] { 1, 1, 1 }, RepriceAction.PromoteToPaid)),

            // Wave 3 - Berserker pass (2026-10-24). Old costs are the CostPerRank values on master before this
            // pass (8fd18f5d3). Damage Rating drops 2/2/2 -> 1/1/1 and owners keep their ranks (the recompute
            // returns the difference). Whirlwind was a single rank at 3 and is now three ranks at 1/1/1: like
            // Taunt in wave 2, an owner paid 3 and so ends at rank 3 with no net point change. Frenzy, Bloodlust
            // (T3 -> T1), Adrenaline and Break Armor changed function at UNCHANGED prices and deliberately have
            // NO row: an unchanged-price Keep row moves no points and produces no notice, but would force a full
            // recompute and lock those abilities until the character migrates. Savage Blows, Vengeance and
            // Enhanced Stamina were RETIRED, not repriced:
            // RetiredClassAbilities refunds them at login (SweepRetiredClassAbilities), so they have no row.
            new RepriceWave(3, "Berserker pass", "Berserker abilities", "the Berserker trainer",
                new RepriceRow(ClassAbilityId.DamageRating, new[] { 2, 2, 2 },       new[] { 1, 1, 1 },       RepriceAction.Keep),
                new RepriceRow(ClassAbilityId.Whirlwind,    new[] { 3 },             new[] { 1, 1, 1 },       RepriceAction.PromoteToPaid)),

            // Wave 4 was built in parallel with wave 3 above. They must not REACH A LIVE SHARD in the wrong
            // order: a character stamped at 4 has appliedWave >= 3, so a wave 3 shipped after wave 4 would
            // never run for it. Deploy wave 3 with or before wave 4.
            //
            // Wave 4 - Spellsword pass (2026-10-24). Old costs are the CostPerRank values on master before
            // this pass (8fd18f5d3). The FIRST wave to mix actions: Sundermark and Dispelling Dance (internal
            // name DispellingEdge) only drop in price, so they are Keep - ranks kept, tokens taken back where
            // a tier's price changed, the difference returned by the recompute. Spellsurge, Spellstorm and
            // Cascade were one rank at 3 and are now three ranks at 1/1/1, so they are PromoteToPaid exactly
            // like Taunt in wave 2: an owner paid 3 and ends at rank 3 with no net point change.
            new RepriceWave(4, "Spellsword pass", "Spellsword abilities", "the Spellsword trainer",
                new RepriceRow(ClassAbilityId.Sundermark,     new[] { 2, 2, 2 }, new[] { 1, 1, 1 }, RepriceAction.Keep),
                new RepriceRow(ClassAbilityId.Spellsurge,     new[] { 3 },       new[] { 1, 1, 1 }, RepriceAction.PromoteToPaid),
                new RepriceRow(ClassAbilityId.Spellstorm,     new[] { 3 },       new[] { 1, 1, 1 }, RepriceAction.PromoteToPaid),
                new RepriceRow(ClassAbilityId.Cascade,        new[] { 3 },       new[] { 1, 1, 1 }, RepriceAction.PromoteToPaid),
                new RepriceRow(ClassAbilityId.DispellingEdge, new[] { 2, 2, 2 }, new[] { 1, 1, 1 }, RepriceAction.Keep)),

            // Wave 5 - Archer pass. Numbered 5 on purpose: waves 3 and 4 belong to the parallel Berserker and
            // Spellsword passes, which land first. Crit Rating drops from 2/2/2 to 1/1/1; owners keep their
            // ranks and are refunded the difference by the login recompute. The other Archer changes in this
            // pass (Hunter's Mark, Pinning Shot, Double Volley, Long Draw) are tunable and affinity changes
            // that leave every price alone, so they need no row.
            new RepriceWave(5, "Archer pass", "Archer abilities", "the Archer trainer",
                new RepriceRow(ClassAbilityId.CritRating, new[] { 2, 2, 2 }, new[] { 1, 1, 1 }, RepriceAction.Keep)),

            // Wave 6 - Archmage pass (2026-10-24). Built in parallel with waves 4 and 5 (Spellsword, Archer),
            // which landed first. Old costs are the CostPerRank values on master before this pass. Overchannel
            // and Echo Cast drop 2/2/2 -> 1/1/1 and Mana Barrier 1/2/3 -> 1/1/1; owners keep their ranks and the
            // recompute returns the difference. Abilities whose price did not change get no row. Quickened
            // Casting and Flat Cast Speed were RETIRED, not repriced: RetiredClassAbilities refunds them at
            // login, so they have no row.
            new RepriceWave(6, "Archmage pass", "Archmage abilities", "the Archmage trainer",
                new RepriceRow(ClassAbilityId.Overchannel, new[] { 2, 2, 2 }, new[] { 1, 1, 1 }, RepriceAction.Keep),
                new RepriceRow(ClassAbilityId.EchoCast,    new[] { 2, 2, 2 }, new[] { 1, 1, 1 }, RepriceAction.Keep),
                new RepriceRow(ClassAbilityId.ManaBarrier, new[] { 1, 2, 3 }, new[] { 1, 1, 1 }, RepriceAction.Keep)),
        };

        /// <summary>The highest wave number shipped (0 when there are none). A character stamped at this
        /// value has nothing left to migrate.</summary>
        public static int LatestWave => LatestWaveOf(Waves);

        public static int LatestWaveOf(IReadOnlyList<RepriceWave> waves) =>
            waves == null || waves.Count == 0 ? 0 : waves.Max(w => w.Number);

        /// <summary>The waves above <paramref name="appliedWave"/>, in ascending order.</summary>
        public static IEnumerable<RepriceWave> PendingWaves(IReadOnlyList<RepriceWave> waves, int appliedWave) =>
            waves == null ? Enumerable.Empty<RepriceWave>() : waves.Where(w => w.Number > appliedWave).OrderBy(w => w.Number);

        /// <summary>
        /// The cumulative cost of ranks 1..<paramref name="rank"/> on a cost ladder. A rank above the ladder
        /// clamps to its top; rank 0 or below costs nothing.
        /// </summary>
        public static int Cumulative(IReadOnlyList<int> costs, int rank)
        {
            if (costs == null || rank <= 0)
                return 0;

            var top = Math.Min(rank, costs.Count);

            var total = 0;
            for (var i = 0; i < top; i++)
                total += costs[i];

            return total;
        }

        // ---- pending-wave refusal -------------------------------------------------------------------

        /// <summary>
        /// The player-facing refusal for a learn, voucher purchase, voucher apply or voucher refund of an
        /// ability a pending wave names (<see cref="IsAbilityRepricePending(int, ClassAbilityId)"/>).
        /// </summary>
        public const string PendingRefusal = "Your class ability price update is still being applied - try again in a moment, or log in again if this persists.";

        /// <summary>
        /// TRUE when a wave above <paramref name="appliedWave"/> names <paramref name="ability"/>, i.e. this
        /// character has not yet been migrated through it.
        ///
        /// WHY THE RANK AND TOKEN PATHS MUST REFUSE WHILE THIS IS TRUE. A character left unmigrated - the kill
        /// switch is off, so login skipped the sweep - could otherwise learn a rank of an ability the wave
        /// will unlearn (and be refunded for it as an old-price purchase), buy a token at the new price that
        /// the sweep later takes back at the old one (an arbitrage), or refund an old-price token at the
        /// exchanger for the new price (a loss). Refusing until the next migrated login closes all three.
        /// Abilities no pending wave names are unaffected.
        /// </summary>
        public static bool IsAbilityRepricePending(int appliedWave, ClassAbilityId ability) =>
            IsAbilityRepricePending(Waves, appliedWave, ability);

        /// <summary><see cref="IsAbilityRepricePending(int, ClassAbilityId)"/> over an explicit wave list, for tests.</summary>
        public static bool IsAbilityRepricePending(IReadOnlyList<RepriceWave> waves, int appliedWave, ClassAbilityId ability) =>
            PendingWaves(waves, appliedWave).Any(w => w.Rows.Any(r => r.Ability == ability));

        // ---- unlearns -------------------------------------------------------------------------------

        /// <summary>
        /// Every ability a pending wave UNLEARNS that this character owns (raw rank above 0), in wave order,
        /// each named once. <paramref name="rankOf"/> must return the RAW owned rank (the rank cache, not the
        /// PK-facet / PvP-arena masked TryGetClassAbility answer): a suppressed rank is still a paid-for rank.
        /// A character stamped at or past the latest wave gets an empty list.
        /// </summary>
        public static List<UnlearnStep> PlanUnlearns(int appliedWave, Func<ClassAbilityId, int> rankOf) =>
            PlanUnlearns(Waves, appliedWave, rankOf);

        /// <summary><see cref="PlanUnlearns(int, Func{ClassAbilityId, int})"/> over an explicit wave list, for tests.</summary>
        public static List<UnlearnStep> PlanUnlearns(IReadOnlyList<RepriceWave> waves, int appliedWave, Func<ClassAbilityId, int> rankOf)
        {
            var steps = new List<UnlearnStep>();

            if (rankOf == null)
                return steps;

            var done = new HashSet<ClassAbilityId>();

            foreach (var wave in PendingWaves(waves, appliedWave))
            {
                foreach (var row in wave.Rows)
                {
                    if (row.Action != RepriceAction.Unlearn || done.Contains(row.Ability))
                        continue;

                    var rank = rankOf(row.Ability);

                    if (rank <= 0)
                        continue;

                    done.Add(row.Ability);

                    steps.Add(new UnlearnStep
                    {
                        Wave = wave.Number,
                        Ability = row.Ability,
                        OldRank = rank,
                        OldPaid = Cumulative(row.OldCosts, rank),
                    });
                }
            }

            return steps;
        }

        // ---- promotions -----------------------------------------------------------------------------

        /// <summary>
        /// The rank a <see cref="RepriceAction.PromoteToPaid"/> owner ends at: the highest rank on
        /// <paramref name="newCosts"/> whose cumulative cost is no more than what <paramref name="rank"/> cost
        /// on <paramref name="oldCosts"/>. Never below <paramref name="rank"/> (a promotion never takes a rank
        /// away) and never above the new ladder's top; rank 0 or below is returned unchanged.
        ///
        /// A RE-RUN after a crash that persisted the promotion reads the promoted rank back as the "rank
        /// before". It promotes to itself (this never lowers a rank), and the expected delta prices it as
        /// Cumulative(old, promoted) - Cumulative(new, promoted). That equals the first run's figure whenever
        /// the old ladder's top costs exactly what the promoted owner paid, which holds for every ladder that
        /// was a SINGLE old rank (Taunt's {3}); a future multi-rank PromoteToPaid row must re-check it.
        /// </summary>
        public static int PromotedRank(IReadOnlyList<int> oldCosts, IReadOnlyList<int> newCosts, int rank)
        {
            if (rank <= 0 || newCosts == null)
                return rank;

            var paid = Cumulative(oldCosts, rank);
            var best = rank;

            for (var k = rank + 1; k <= newCosts.Count; k++)
            {
                if (Cumulative(newCosts, k) <= paid)
                    best = k;
            }

            return best;
        }

        /// <summary>
        /// The rank an owned, NOT-unlearned ability ends at after every pending wave: the promoted rank when
        /// any pending row for it is <see cref="RepriceAction.PromoteToPaid"/> (priced from the FIRST pending
        /// row's old ladder to the LAST pending row's new one, the same pairing the expected delta uses),
        /// otherwise the rank unchanged.
        /// </summary>
        public static int RankAfterPendingWaves(IReadOnlyList<RepriceWave> waves, int appliedWave, ClassAbilityId ability, int rank)
        {
            var rows = PendingWaves(waves, appliedWave).SelectMany(w => w.Rows).Where(r => r.Ability == ability).ToList();

            if (rows.Count == 0 || rank <= 0 || rows.Any(r => r.Action == RepriceAction.Unlearn))
                return rank;

            if (!rows.Any(r => r.Action == RepriceAction.PromoteToPaid))
                return rank;

            return PromotedRank(rows[0].OldCosts, rows[rows.Count - 1].NewCosts, rank);
        }

        /// <summary>
        /// Every owned ability a pending wave PROMOTES whose rank actually rises, in wave order, each named
        /// once. An ability any pending wave unlearns is never promoted (the unlearn wins). <paramref name="rankOf"/>
        /// is the RAW owned rank, as for <see cref="PlanUnlearns(int, Func{ClassAbilityId, int})"/>.
        /// </summary>
        public static List<PromoteStep> PlanPromotions(IReadOnlyList<RepriceWave> waves, int appliedWave, Func<ClassAbilityId, int> rankOf)
        {
            var steps = new List<PromoteStep>();

            if (rankOf == null)
                return steps;

            var done = new HashSet<ClassAbilityId>();

            foreach (var wave in PendingWaves(waves, appliedWave))
            {
                foreach (var row in wave.Rows)
                {
                    if (row.Action != RepriceAction.PromoteToPaid || done.Contains(row.Ability))
                        continue;

                    done.Add(row.Ability);

                    var rank = rankOf(row.Ability);
                    var newRank = RankAfterPendingWaves(waves, appliedWave, row.Ability, rank);

                    if (newRank <= rank)
                        continue;

                    steps.Add(new PromoteStep
                    {
                        Wave = wave.Number,
                        Ability = row.Ability,
                        OldRank = rank,
                        NewRank = newRank,
                        OldPaid = Cumulative(row.OldCosts, rank),
                    });
                }
            }

            return steps;
        }

        /// <summary>
        /// The wave whose subject and trainer the login chat line names: the first pending wave with a row for
        /// any ability in <paramref name="touched"/>, else the last pending wave. With more than one wave
        /// pending (a character that missed the Blood Mage wave logging in after the Vanguard one shipped)
        /// this keeps "Blood Mage abilities were reworked ... visit the Blood Mage trainer" from being worded
        /// as the Vanguard wave.
        /// </summary>
        public static RepriceWave NoticeWave(IReadOnlyList<RepriceWave> waves, int appliedWave, IEnumerable<ClassAbilityId> touched)
        {
            var pending = PendingWaves(waves, appliedWave).ToList();

            if (pending.Count == 0)
                return null;

            var ids = new HashSet<ClassAbilityId>(touched ?? Enumerable.Empty<ClassAbilityId>());

            return pending.FirstOrDefault(w => w.Rows.Any(r => ids.Contains(r.Ability))) ?? pending[pending.Count - 1];
        }

        /// <summary>
        /// The pending wave that UNLEARNS <paramref name="ability"/> (the first, in wave order), else the first
        /// pending wave naming it at all, else null. Used to label a ledger row for an ability erased at an
        /// earlier interrupted login, which carries no wave of its own.
        /// </summary>
        public static RepriceWave WaveNaming(IReadOnlyList<RepriceWave> waves, int appliedWave, ClassAbilityId ability)
        {
            var pending = PendingWaves(waves, appliedWave).ToList();

            return pending.FirstOrDefault(w => w.Rows.Any(r => r.Ability == ability && r.Action == RepriceAction.Unlearn))
                ?? pending.FirstOrDefault(w => w.Rows.Any(r => r.Ability == ability));
        }

        /// <summary>The generic subject of the login line when more than one pending wave touched the character.</summary>
        public const string MultiWaveSubject = "Some class abilities";

        /// <summary>The generic trainer phrase when the unlearned abilities came from more than one wave.</summary>
        public const string MultiWaveTrainer = "your class trainer";

        /// <summary>
        /// The subject and trainer the login chat line uses. With ONE contributing pending wave (a wave with a
        /// row for any ability in <paramref name="touched"/>) it is that wave's own wording, exactly as
        /// <see cref="NoticeWave"/> picks it. With MORE THAN ONE, the points returned are the sum over all of
        /// them, so the line must not attribute that total to one wave: the subject becomes
        /// <see cref="MultiWaveSubject"/>, and the trainer is the one wave that unlearned something (the only
        /// place the player has to go), or <see cref="MultiWaveTrainer"/> when the unlearns span waves. Both
        /// waves are commonly pending together at release, which is why this matters.
        /// </summary>
        public static (string Subject, string Trainer) NoticeWording(IReadOnlyList<RepriceWave> waves, int appliedWave,
            IEnumerable<ClassAbilityId> touched, IEnumerable<ClassAbilityId> unlearned)
        {
            var pending = PendingWaves(waves, appliedWave).ToList();

            if (pending.Count == 0)
                return (MultiWaveSubject, MultiWaveTrainer);

            var touchedIds = new HashSet<ClassAbilityId>(touched ?? Enumerable.Empty<ClassAbilityId>());
            var contributing = pending.Where(w => w.Rows.Any(r => touchedIds.Contains(r.Ability))).ToList();

            if (contributing.Count <= 1)
            {
                var wave = contributing.FirstOrDefault() ?? pending[pending.Count - 1];
                return (wave.Subject, wave.Trainer);
            }

            var unlearnedIds = new HashSet<ClassAbilityId>(unlearned ?? Enumerable.Empty<ClassAbilityId>());
            var unlearnWaves = pending.Where(w => w.Rows.Any(r => r.Action == RepriceAction.Unlearn && unlearnedIds.Contains(r.Ability))).ToList();

            return (MultiWaveSubject, unlearnWaves.Count == 1 ? unlearnWaves[0].Trainer : MultiWaveTrainer);
        }

        /// <summary>
        /// The def.Name of every ability any pending wave unlearns, whether or not this character owns it -
        /// the keys <see cref="TryStripStoredAbilities"/> removes from stored facet builds (abilities_Json is
        /// keyed by def.Name, Player_Facets.cs CaptureFacetAbilities). An id the live registry cannot resolve
        /// is skipped.
        /// </summary>
        public static HashSet<string> UnlearnedAbilityNames(IReadOnlyList<RepriceWave> waves, int appliedWave)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);

            foreach (var row in PendingWaves(waves, appliedWave).SelectMany(w => w.Rows))
            {
                if (row.Action == RepriceAction.Unlearn && ClassAbilityRegistry.Abilities.TryGetValue(row.Ability, out var def))
                    names.Add(def.Name);
            }

            return names;
        }

        /// <summary>
        /// TRUE when the character owns (raw rank above 0) any ability a pending wave names, Keep or Unlearn.
        /// Only such a character - or one whose token sweep took a token back - gets the points recompute;
        /// everyone else is simply stamped, so a wave never re-prices an unrelated character's balance.
        /// </summary>
        public static bool OwnsAnyPendingAbility(IReadOnlyList<RepriceWave> waves, int appliedWave, Func<ClassAbilityId, int> rankOf) =>
            rankOf != null && PendingWaves(waves, appliedWave).SelectMany(w => w.Rows).Any(r => rankOf(r.Ability) > 0);

        // ---- the pending-refund marker ----------------------------------------------------------------

        /// <summary>
        /// Character quest-registry key of the PENDING-REFUND MARKER's refund row for one wave: its
        /// NumTimesCompleted is the old-price cost of every rank the migration has erased but not yet paid
        /// for. Deliberately NOT under <see cref="ClassAbilityRegistry.QuestKeyPrefix"/> ("ClassAbility_"), so
        /// the rank cache, the retired sweep, the overhaul respec and the CAP audit's orphan counter never
        /// read it as a rank.
        ///
        /// WHY IT EXISTS. The erase is saved first, and the points and stamp only after that save confirms.
        /// A logout, client crash or server stop in between commits the erased ranks with no points and no
        /// stamp, and the next login's ranks are then 0: without a record it would see nothing owned and
        /// just stamp, losing the refund for good. The marker rides the SAME character save as the erase, so
        /// either both persist or neither does, and the next run pays from it.
        /// </summary>
        public static string MarkerRefundKey(int wave) => $"ClassAbilityReprice_w{wave}_refund";

        /// <summary>The marker's second row: NumTimesCompleted is <see cref="EncodeUnlearnMask"/> of the erased abilities, so the chat line can still name them.</summary>
        public static string MarkerMaskKey(int wave) => $"ClassAbilityReprice_w{wave}_mask";

        /// <summary>Every ability any wave unlearns, in wave and row order, each once: the bit order of <see cref="EncodeUnlearnMask"/>. Append-only by construction (waves are never edited), so a stored mask keeps its meaning.</summary>
        public static List<ClassAbilityId> UnlearnBitOrder(IReadOnlyList<RepriceWave> waves)
        {
            var order = new List<ClassAbilityId>();

            if (waves == null)
                return order;

            foreach (var row in waves.OrderBy(w => w.Number).SelectMany(w => w.Rows))
            {
                if (row.Action == RepriceAction.Unlearn && !order.Contains(row.Ability))
                    order.Add(row.Ability);
            }

            return order;
        }

        public static int EncodeUnlearnMask(IReadOnlyList<RepriceWave> waves, IEnumerable<ClassAbilityId> abilities)
        {
            var order = UnlearnBitOrder(waves);
            var mask = 0;

            foreach (var id in abilities ?? Enumerable.Empty<ClassAbilityId>())
            {
                var bit = order.IndexOf(id);
                if (bit >= 0 && bit < 31)
                    mask |= 1 << bit;
            }

            return mask;
        }

        public static List<ClassAbilityId> DecodeUnlearnMask(IReadOnlyList<RepriceWave> waves, int mask)
        {
            var order = UnlearnBitOrder(waves);
            var ids = new List<ClassAbilityId>();

            for (var bit = 0; bit < order.Count && bit < 31; bit++)
            {
                if ((mask & (1 << bit)) != 0)
                    ids.Add(order[bit]);
            }

            return ids;
        }

        /// <summary>
        /// THE FLOOR'S OLD-PRICE REFUND, resumable. The unlearned part comes ONLY from the marker
        /// (<paramref name="markerRefund"/>, the old cost of every rank erased and not yet paid for, written
        /// in the same character save as the erase), never from the ranks, which are 0 on a re-run after the
        /// erase persisted. The kept part (each kept rank's price drop) and the tokens come from
        /// <paramref name="rankOfBeforeErase"/> and <paramref name="tokensTakenBackAtOldPrice"/> as in
        /// <see cref="ExpectedDelta"/>, with every unlearned ability read as rank 0 so nothing is counted twice.
        /// </summary>
        public static int OldPriceRefund(IReadOnlyList<RepriceWave> waves, int appliedWave, Func<ClassAbilityId, int> rankOfBeforeErase,
            int tokensTakenBackAtOldPrice, int markerRefund)
        {
            var unlearnIds = new HashSet<ClassAbilityId>(PendingWaves(waves, appliedWave).SelectMany(w => w.Rows)
                .Where(r => r.Action == RepriceAction.Unlearn).Select(r => r.Ability));

            Func<ClassAbilityId, int> keptOnly = id => unlearnIds.Contains(id) || rankOfBeforeErase == null ? 0 : rankOfBeforeErase(id);

            return Math.Max(0, markerRefund) + ExpectedDelta(waves, appliedWave, keptOnly, tokensTakenBackAtOldPrice);
        }

        // ---- the points recompute -------------------------------------------------------------------

        /// <summary>
        /// THE POINTS RECOMPUTE. Recomputed = <paramref name="totalEarned"/> - <paramref name="ownedCostAfter"/>
        /// - <paramref name="outstandingTokenValue"/>, all measured AFTER the unlearns and the token sweep:
        /// ownedCostAfter is every rank still owned priced at the live ladder (Player.ClassAbilityPointsSpentOnOwnedRanks),
        /// and outstandingTokenValue is what the exchanger would refund for every token still carried
        /// (<see cref="OutstandingTokenValue"/>).
        ///
        /// THE FLOOR. NewAvailable = max(Recomputed, <paramref name="currentAvailable"/> + <paramref name="oldPriceRefund"/>):
        /// nobody loses the refund the pinned old prices owe them (the old cost of every unlearned rank, each
        /// kept rank's price drop, and the old price of every token taken back - <see cref="ExpectedDelta"/>).
        /// The refund is clamped at 0, so the migration never lowers a balance either.
        ///
        /// For a character whose balance was consistent before the wave the two terms are EQUAL. The recompute
        /// wins (comes out higher) when the derivation finds points the balance does not hold: a past price
        /// drop that was never migrated, or a token the sweep cannot see (a house chest, another character),
        /// which is not subtracted and so is returned while the token stays usable. The floor wins when the
        /// character already held more than its earnings explain (a positive admin credit, a past price rise
        /// never migrated). When the floor wins the caller logs a warning naming the character and both terms -
        /// that is for a human to read, not for a login to claw back.
        ///
        /// THE RECOMPUTE IS A FULL DERIVATION, NOT A DELTA, so for a touched character it also returns points
        /// lost to a destroyed or lost token and reverses any earlier NEGATIVE admin adjustment (/capadjust
        /// docks Available under reason admin_correct). Subtracting those docks would need the ledger, which is
        /// only readable asynchronously on the shard worker (Player_CapAudit's GetCapLedger callback) and is
        /// capped at ShardDatabase.MaxCapLedgerRows, so the synchronous login sweep does not; an admin dock on a
        /// Blood Mage owner has to be re-applied by hand after the migration.
        /// </summary>
        public static RepricePoints RecomputePoints(int totalEarned, int currentAvailable, int ownedCostAfter, int outstandingTokenValue, int oldPriceRefund)
        {
            var recomputed = totalEarned - ownedCostAfter - outstandingTokenValue;
            var refund = Math.Max(0, oldPriceRefund);
            var floor = currentAvailable + refund;
            var newAvailable = Math.Max(recomputed, floor);

            return new RepricePoints
            {
                Recomputed = recomputed,
                CurrentAvailable = currentAvailable,
                OldPriceRefund = refund,
                Floor = floor,
                NewAvailable = newAvailable,
                Delta = newAvailable - currentAvailable,
                FloorBinds = recomputed < floor,
            };
        }

        /// <summary>
        /// The delta of the migration's single net ledger row: the whole change in Available minus what the
        /// per-token voucher_refund rows (each crediting its token's OLD price) already moved, so the rows sum
        /// to exactly <see cref="RepricePoints.Delta"/>. Negative only when a kept rank's price ROSE by more than
        /// the other refunds (no wave does that today); Available still ends at NewAvailable.
        /// </summary>
        public static int NetRowDelta(RepricePoints points, int tokenRefundsAtOldPrice) => points.Delta - tokenRefundsAtOldPrice;

        /// <summary>
        /// The refund value of ONE carried token stack, i.e. the class ability points it still represents:
        /// the live CostPerRank of its tier for a registered ability (what RefundUnusedVoucher pays), the
        /// RetiredClassAbilities price for a retired one (what TryRefundRetiredVoucher pays), times the stack.
        /// 0 for anything that is not a token, a tier off the ladder, or an id neither table prices - those
        /// are unrefundable, so they hold no points.
        /// </summary>
        public static int TokenValue(int tokenId, int tier, int stackSize)
        {
            if (tokenId <= 0 || tier <= 0)
                return 0;

            var stack = Math.Max(1, stackSize);

            if (ClassAbilityRegistry.Abilities.TryGetValue((ClassAbilityId)tokenId, out var def))
                return tier <= def.CostPerRank.Length ? def.CostPerRank[tier - 1] * stack : 0;

            if (RetiredClassAbilities.TryGetRefund((ClassAbilityId)tokenId, tier, out var points, out _))
                return points * stack;

            return 0;
        }

        /// <summary>Sum of <see cref="TokenValue"/> over (tokenId, tier, stack) triples.</summary>
        public static int OutstandingTokenValue(IEnumerable<(int TokenId, int Tier, int Stack)> tokens) =>
            tokens == null ? 0 : tokens.Sum(t => TokenValue(t.TokenId, t.Tier, t.Stack));

        // ---- carried tokens -------------------------------------------------------------------------

        /// <summary>
        /// THE CARRIED-TOKEN DECISION for one inventory item: whether the sweep takes the whole stack back,
        /// the per-token price it was bought at (the FIRST pending wave's OLD cost for its tier - every login
        /// migrates before the character can buy anything, so a token held at wave N was bought at the prices
        /// after wave N), and that price times the stack.
        ///
        ///  - an ability a pending wave UNLEARNS: every tier the old ladder had is taken back, whatever its
        ///    price did - the ability was reworked, so the player chooses again;
        ///  - an ability pending waves only KEEP: taken back only when that tier's price changed (old price
        ///    versus the LAST pending wave's new price; a tier the new ladder no longer has counts as changed);
        ///  - a tier beyond the old ladder could not have been bought at the old prices, so it is left alone;
        ///  - not a token (<paramref name="tokenId"/> 0 or below), or no pending wave names the ability: left
        ///    alone. A stack below 1 counts as 1.
        ///
        /// The price is reported for the ledger detail and the expected-delta diagnostic; the points the token
        /// represented come back through <see cref="RecomputePoints"/>, because a token taken back is no longer
        /// in the outstanding token value.
        /// </summary>
        public static TokenRefundDecision DecideTokenRefund(int appliedWave, int tokenId, int tier, int stackSize) =>
            DecideTokenRefund(Waves, appliedWave, tokenId, tier, stackSize);

        /// <summary><see cref="DecideTokenRefund(int, int, int, int)"/> over an explicit wave list, for tests.</summary>
        public static TokenRefundDecision DecideTokenRefund(IReadOnlyList<RepriceWave> waves, int appliedWave, int tokenId, int tier, int stackSize)
        {
            if (tokenId <= 0 || tier <= 0)
                return default;

            var ability = (ClassAbilityId)tokenId;

            var rows = PendingWaves(waves, appliedWave)
                .SelectMany(w => w.Rows.Where(r => r.Ability == ability))
                .ToList();

            if (rows.Count == 0)
                return default;

            var first = rows[0];
            var last = rows[rows.Count - 1];

            if (tier > first.OldCosts.Count)
                return default;

            var oldPrice = first.OldCosts[tier - 1];

            var unlearned = rows.Any(r => r.Action == RepriceAction.Unlearn);
            var repriced = tier > last.NewCosts.Count || last.NewCosts[tier - 1] != oldPrice;

            if (!unlearned && !repriced)
                return default;

            var stack = Math.Max(1, stackSize);

            return new TokenRefundDecision { Refund = true, Unlearned = unlearned, UnitPrice = oldPrice, Stack = stack, Total = oldPrice * stack };
        }

        // ---- the expected delta (diagnostic) --------------------------------------------------------

        /// <summary>
        /// What the migration would return if the character's current balance was exactly right: the old
        /// cost of every unlearned rank, plus each kept rank's old-minus-new cumulative cost (signed; a price
        /// rise would be negative; a promoted rank is priced at the rank it is raised to), plus the old price
        /// of every token taken back. The recompute equals
        /// currentAvailable + this whenever the balance was consistent before the wave, and currentAvailable +
        /// this is the FLOOR <see cref="RecomputePoints"/> guarantees, so nobody loses the refund the old
        /// prices owe them.
        ///
        /// IT MUST BE MEASURED BEFORE THE UNLEARN ERASES ANY RANK. <paramref name="rankOfBeforeUnlearn"/> is the
        /// raw rank as the character logged in; a re-run after a crash that persisted the erase sees rank 0 for
        /// the unlearned abilities, which is why the live floor uses <see cref="OldPriceRefund"/> (unlearned part
        /// from the marker) rather than this directly.
        /// </summary>
        public static int ExpectedDelta(IReadOnlyList<RepriceWave> waves, int appliedWave, Func<ClassAbilityId, int> rankOfBeforeUnlearn, int tokensTakenBackAtOldPrice)
        {
            if (rankOfBeforeUnlearn == null)
                return tokensTakenBackAtOldPrice;

            var total = tokensTakenBackAtOldPrice;

            total += PlanUnlearns(waves, appliedWave, rankOfBeforeUnlearn).Sum(s => s.OldPaid);

            var unlearned = new HashSet<ClassAbilityId>(PendingWaves(waves, appliedWave).SelectMany(w => w.Rows)
                .Where(r => r.Action == RepriceAction.Unlearn).Select(r => r.Ability));

            foreach (var group in PendingWaves(waves, appliedWave).SelectMany(w => w.Rows).Where(r => !unlearned.Contains(r.Ability)).GroupBy(r => r.Ability))
            {
                var rank = rankOfBeforeUnlearn(group.Key);
                if (rank <= 0)
                    continue;

                var first = group.First();
                var last = group.Last();

                // A PromoteToPaid owner ends at a HIGHER rank, priced at the new ladder: what they paid minus
                // what the promoted rank costs now (0 for Taunt's 3 -> rank 3 at 1/1/1).
                var rankAfter = RankAfterPendingWaves(waves, appliedWave, group.Key, rank);

                total += Cumulative(first.OldCosts, rank) - Cumulative(last.NewCosts, rankAfter);
            }

            return total;
        }

        // ---- stored facet builds --------------------------------------------------------------------

        /// <summary>
        /// Removes every key in <paramref name="names"/> from one abilities_Json blob. Returns TRUE, with the
        /// rewritten blob and the removed names, only when something was actually removed; a blob that does
        /// not parse reads as empty (FacetSnapshot.DeserializeAbilities' fail-open shape) and so is never
        /// rewritten - an unreadable blob is left exactly as it is rather than replaced by "{}".
        /// </summary>
        public static bool TryStripAbilities(string abilitiesJson, IReadOnlyCollection<string> names, out string strippedJson, out List<string> removed)
        {
            strippedJson = abilitiesJson;
            removed = new List<string>();

            if (names == null || names.Count == 0 || string.IsNullOrWhiteSpace(abilitiesJson))
                return false;

            var abilities = FacetSnapshot.DeserializeAbilities(abilitiesJson);

            foreach (var name in names)
            {
                if (abilities.Remove(name))
                    removed.Add(name);
            }

            if (removed.Count == 0)
                return false;

            strippedJson = FacetSnapshot.SerializeAbilities(abilities);
            return true;
        }

        /// <summary>
        /// Strips the unlearned abilities from every stored facet build and waits for every write to CONFIRM
        /// (FacetRowFetch.TrySaveAll). Returns FALSE when any write did not confirm, in which case the caller
        /// must abandon the migration WITHOUT stamping its guard - the shape and the reasoning of
        /// ClassAbilityOverhaulRespec.TryClearStoredAbilities.
        ///
        /// WHY STRIP AND NOT LEAVE THEM. A stored build is a second copy of a character's ranks. Left in
        /// place, the first facet switch would silently re-apply a reworked ability the player never chose
        /// under its new behaviour, defeating the unlearn.
        ///
        /// WHY NO POINTS MOVE. The facet swap model (FacetPools.AvailableClassAbilityPointsAfterSwap:
        /// current + outgoingSpent - incomingSpent) RELEASED a build's points into Available when it was
        /// stored. A stored build therefore holds ranks but no points, so removing a rank from it refunds
        /// nothing - the points are already in Available, and the next swap-in simply debits nothing for the
        /// removed ability. skills_Json, equip_Json and attrs_Json are left exactly as read.
        /// </summary>
        public static bool TryStripStoredAbilities(
            IReadOnlyList<CharacterFacet> rows,
            IReadOnlyCollection<string> names,
            int timeoutMs,
            Action<CharacterFacet, Action<bool>> saver,
            out int strippedRows,
            out HashSet<string> strippedNames)
        {
            strippedRows = 0;
            strippedNames = new HashSet<string>(StringComparer.Ordinal);

            if (rows == null || rows.Count == 0 || names == null || names.Count == 0)
                return true;

            var dirty = new List<CharacterFacet>();
            var removedNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (var row in rows)
            {
                if (row == null || !TryStripAbilities(row.AbilitiesJson, names, out var stripped, out var removed))
                    continue;

                row.AbilitiesJson = stripped;
                row.UpdatedAt = DateTime.UtcNow;

                dirty.Add(row);
                removedNames.UnionWith(removed);
            }

            if (dirty.Count == 0)
                return true;

            if (!FacetRowFetch.TrySaveAll(dirty, timeoutMs, saver))
                return false;

            strippedRows = dirty.Count;
            strippedNames = removedNames;
            return true;
        }

        // ---- the login chat line --------------------------------------------------------------------

        /// <summary>
        /// The login chat lines. The first is the owner-specified line: the subject, the UNLEARNED abilities
        /// the character actually owned (display names, "A, B and C"), N points returned, and where to
        /// re-learn. A second, short line is added only when tokens were taken back or stored facet builds
        /// were stripped, kept separate so each stays under the client's 255-character chat line limit.
        /// Returns no lines when nothing visible happened (nothing unlearned, nothing stripped, N 0, no token).
        /// </summary>
        public static List<string> BuildNoticeLines(string subject, string trainer, IReadOnlyList<string> unlearnedDisplayNames,
            int pointsReturned, int tokensTakenBack, IReadOnlyList<string> strippedFacetDisplayNames,
            IReadOnlyList<(string DisplayName, int NewRank)> promoted = null)
        {
            var lines = BuildNoticeLinesCore(subject, trainer, unlearnedDisplayNames, pointsReturned, tokensTakenBack, strippedFacetDisplayNames);

            // A PromoteToPaid ability gets its own short line, after the others: "Taunt now has more ranks:
            // you were raised to rank 3 at no cost." Nothing else on the first line says a rank went UP.
            if (promoted != null && promoted.Count > 0)
            {
                var sentences = promoted.Select(p => $"{p.DisplayName} now has more ranks: you were raised to rank {p.NewRank} at no cost.");
                lines.Add(string.Join(" ", sentences));
            }

            return lines;
        }

        private static List<string> BuildNoticeLinesCore(string subject, string trainer, IReadOnlyList<string> unlearnedDisplayNames,
            int pointsReturned, int tokensTakenBack, IReadOnlyList<string> strippedFacetDisplayNames)
        {
            var lines = new List<string>();

            var unlearned = unlearnedDisplayNames ?? Array.Empty<string>();
            var stripped = strippedFacetDisplayNames ?? Array.Empty<string>();

            var points = $"{pointsReturned:N0} class ability point{(pointsReturned == 1 ? "" : "s")}";

            string first = null;

            if (unlearned.Count > 0)
            {
                var verb = unlearned.Count == 1 ? "has" : "have";
                var pronoun = unlearned.Count == 1 ? "it" : "them";

                first = pointsReturned > 0
                    ? $"{subject} were reworked: {JoinNames(unlearned)} {verb} been unlearned and {points} returned. Visit {trainer} to re-learn {pronoun}."
                    : $"{subject} were reworked: {JoinNames(unlearned)} {verb} been unlearned. Visit {trainer} to re-learn {pronoun}.";
            }
            else if (pointsReturned > 0)
            {
                first = $"{subject} were reworked and repriced: {points} returned.";
            }
            else if (stripped.Count > 0)
            {
                first = $"{subject} were reworked: {JoinNames(stripped)} {(stripped.Count == 1 ? "has" : "have")} been removed from your stored facet builds. Visit {trainer} to re-learn {(stripped.Count == 1 ? "it" : "them")}.";
                stripped = Array.Empty<string>();
            }

            if (first != null)
                lines.Add(first);

            var extras = new List<string>();

            if (tokensTakenBack > 0)
                extras.Add(tokensTakenBack == 1
                    ? "One unused training token was taken back; its points are included above."
                    : $"{tokensTakenBack:N0} unused training tokens were taken back; their points are included above.");

            if (stripped.Count > 0)
                extras.Add($"Your stored facet builds no longer hold {JoinNames(stripped)}.");

            if (extras.Count > 0)
                lines.Add(string.Join(" ", extras));

            return lines;
        }

        /// <summary>"A", "A and B", "A, B and C".</summary>
        public static string JoinNames(IReadOnlyList<string> names)
        {
            if (names == null || names.Count == 0)
                return string.Empty;

            if (names.Count == 1)
                return names[0];

            return string.Join(", ", names.Take(names.Count - 1)) + " and " + names[names.Count - 1];
        }
    }

    /// <summary>
    /// What the reprice token sweep does with one carried item: <see cref="Refund"/> FALSE (the default)
    /// leaves it alone; TRUE takes the whole stack back. <see cref="UnitPrice"/> is the price it was bought
    /// at and <see cref="Total"/> = UnitPrice x <see cref="Stack"/>; <see cref="Unlearned"/> says whether
    /// the reason is an unlearn (any price) rather than a changed price.
    /// </summary>
    public readonly struct TokenRefundDecision
    {
        public bool Refund { get; init; }
        public bool Unlearned { get; init; }
        public int UnitPrice { get; init; }
        public int Stack { get; init; }
        public int Total { get; init; }
    }
}
