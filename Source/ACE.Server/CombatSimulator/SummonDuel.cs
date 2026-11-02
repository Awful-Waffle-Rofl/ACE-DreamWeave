using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;

namespace ACE.Server.CombatSimulator
{
    /// <summary>
    /// A seeded random stream that DELEGATES to an inner <see cref="Random"/> instead of deriving from it. A derived
    /// Random routes Next(int) through the virtual Sample() on net10.0, so a counter placed in an override either
    /// counts integer draws too or recurses; a separate field has neither problem and keeps the stream byte-identical
    /// to new Random(seed). Draws counts every double taken, so a test can pin how many random numbers a duel used.
    /// </summary>
    public sealed class DuelRandom
    {
        private readonly Random inner;

        public DuelRandom(int seed)
        {
            inner = new Random(seed);
        }

        /// <summary>How many doubles have been drawn from this stream.</summary>
        public long Draws { get; private set; }

        public double NextDouble()
        {
            Draws++;
            return inner.NextDouble();
        }

        /// <summary>True with probability p (p outside [0, 1] clamps).</summary>
        public bool Chance(double p)
        {
            return NextDouble() < p;
        }
    }

    /// <summary>One animation option inside an attack mode (one combat maneuver, or one aim/reload sequence).</summary>
    public sealed class DuelAttackCycle
    {
        /// <summary>Relative weight of this option among the mode's cycles.</summary>
        public double Weight = 1.0;

        /// <summary>The animation length in seconds at attack-speed 1.0 (the weapon's own animation speed already folded in).</summary>
        public double AnimSeconds;

        /// <summary>Where each strike of the swing lands, as a fraction of the (speed-scaled) AnimSeconds (the motion table's attack frames), after LeadSeconds.</summary>
        public double[] StrikeFractions = { 1.0 / 3.0 };

        /// <summary>
        /// Seconds of the cycle that attack speed does NOT shorten (a missile's aim and its Reload-to-Ready link play at 1.0; only the
        /// reload runs at the attack speed, Monster_Missile.LaunchMissile). Added to the cycle length after the scaled AnimSeconds.
        /// </summary>
        public double FixedSeconds;

        /// <summary>Unscaled seconds before the scaled part starts, which the strikes follow (a missile's aim: the arrow leaves when it ends).</summary>
        public double LeadSeconds;
    }

    /// <summary>
    /// One way a combatant attacks (melee, missile or one spell), with per-strike numbers taken from the analytic combat
    /// simulator: hit chance, crit chance and the damage of a normal and of a critical landed hit.
    /// </summary>
    public sealed class DuelAttackMode
    {
        public string Name = "";

        /// <summary>Share of this combatant's attacks that use the mode (normalised across the combatant's modes).</summary>
        public double Mix = 1.0;

        public double HitChance;
        public double CritChance;
        public double NormalDamage;
        public double CritDamage;

        public DuelAttackCycle[] Cycles = Array.Empty<DuelAttackCycle>();

        /// <summary>Uniform(0, RecoveryMax) seconds after the animation (the engine's PowerupTime recovery).</summary>
        public double RecoveryMax;

        /// <summary>A fixed delay after the animation (missile: Monster.MissileDelay), not shortened by attack speed.</summary>
        public double FixedDelay;

        /// <summary>True for a launcher shot: Multishot arrows apply to this mode only.</summary>
        public bool IsMissile;

        /// <summary>The mean damage of one landed hit.</summary>
        public double MeanLandedDamage => (1.0 - CritChance) * NormalDamage + CritChance * CritDamage;

        internal double TotalWeight => Cycles.Sum(c => c.Weight);
    }

    /// <summary>
    /// A stacking attack-speed ramp (Frenzy), read from a pet's MonsterCombatEffects "ramp axis=attackspeed" record: one
    /// stack per landed hit up to MaxStacks, each adding PerStack to the speed multiplier (1 + stacks * per, the
    /// FrenzyAbility.AttackSpeedMultiplier shape), the whole pool lapsing when WindowSeconds pass with no landed hit,
    /// the product clamped to SpeedCap (monster_effect_speed_cap).
    /// </summary>
    public sealed class DuelRamp
    {
        public double PerStack;
        public int MaxStacks;
        public double WindowSeconds;
        public double SpeedCap = double.PositiveInfinity;

        public double Multiplier(int stacks) => Math.Min(1.0 + Math.Max(0, stacks) * PerStack, Math.Max(1.0, SpeedCap));
    }

    /// <summary>Extra arrows per missile shot: each is its own hit roll at ArrowMultiplier of the primary's damage.</summary>
    public sealed class DuelMultishot
    {
        public int ExtraArrows;
        public double ArrowMultiplier = 1.0;
    }

    /// <summary>One side's data for a duel. Pure numbers - no WorldObject, no dat.</summary>
    public sealed class DuelCombatant
    {
        public string Name = "";
        public double MaxHealth;

        /// <summary>Multiplies every point of damage this combatant takes (Soul Tether: 1 - reduction). 1.0 = none.</summary>
        public double IncomingMultiplier = 1.0;

        /// <summary>Fraction of the damage this combatant deals that heals it (Umbral Siphon plus the Empowered Summons leech).</summary>
        public double LeechFraction;

        public DuelAttackMode[] Modes = Array.Empty<DuelAttackMode>();
        public DuelRamp Ramp;
        public DuelMultishot Multishot;

        /// <summary>True: the modes are used in turn, one per attack, in order (a dual wielder alternating hands) instead of drawn by Mix.</summary>
        public bool Alternate;

        /// <summary>The seconds the combatant stays out (a summon's Lifespan). Infinity = no expiry.</summary>
        public double LifespanSeconds = double.PositiveInfinity;
    }

    /// <summary>The totals of one set of duels (one pet against N copies of one monster).</summary>
    public sealed class DuelSummary
    {
        public int Trials;
        public int Wins;
        public int Deaths;
        public int Expired;

        /// <summary>Wins / Trials.</summary>
        public double WinRate;

        /// <summary>The pet's health fraction at the end of the trial, averaged over every trial (a death counts 0).</summary>
        public double MeanRemainingHpAll;

        /// <summary>The same, over the trials the pet won (NaN when it never won).</summary>
        public double MeanRemainingHpWins = double.NaN;

        /// <summary>Mean seconds until the pet died, over the trials it died (NaN when it never did).</summary>
        public double MeanTimeToDeath = double.NaN;

        /// <summary>Mean seconds until the last monster fell, over the trials the pet won (NaN when it never won).</summary>
        public double MeanTimeToKill = double.NaN;

        /// <summary>Total damage the pet dealt over total seconds it fought, all trials (damage on a monster is capped at its remaining health).</summary>
        public double DpsDealt;

        /// <summary>Total random doubles drawn.</summary>
        public long Draws;
    }

    /// <summary>
    /// The duel model behind summon-duel: an event-driven, seeded simulation of one summoned pet against one to three
    /// monsters that fight it simultaneously while the pet kills them in order. Each combatant repeats attack cycles - a
    /// mode and an animation option are drawn, the strikes land at the motion table's attack frames, the next cycle
    /// starts after the animation plus the recovery - and every strike rolls its own hit and crit from the numbers the
    /// analytic simulator produced. Pure arithmetic, so the dat-free tests pin it; the dat-dependent cadence is built
    /// elsewhere (SummonDuelBuilder).
    ///
    /// MODELLED: the incoming multiplier (Soul Tether), the leech (Umbral Siphon, Empowered Summons), Multishot
    /// extra arrows with the owner's overflow rule (an arrow with no secondary target lands on the primary at 1/3 of its
    /// multiplier), the Frenzy ramp, the pet's lifespan. NOT MODELLED: taunt, movement and range, projectile flight,
    /// the within-hit damage spread (a hit deals the analytic mean, crit or not), monster combat effects, healing or
    /// debuff spells, the pet being targeted by only some of the monsters.
    /// </summary>
    public static class SummonDuel
    {
        /// <summary>The longest a duel runs when the pet has no lifespan, in seconds.</summary>
        public const double HardTimeLimit = 600.0;

        // ---- analytic expectations (no randomness) ----

        /// <summary>
        /// The pet's expected damage per second against a target that never dies, at a fixed ramp stack count: the
        /// mix-weighted damage per attack over the mix-weighted seconds per attack (the renewal-reward ratio).
        /// Multishot is left out: it depends on the number of targets.
        /// </summary>
        public static double ExpectedDps(DuelCombatant combatant, int rampStacks = 0, bool includeRecovery = true)
        {
            var speed = combatant.Ramp == null ? 1.0 : combatant.Ramp.Multiplier(rampStacks);

            double damage = 0.0, seconds = 0.0, mixTotal = combatant.Modes.Sum(m => m.Mix);

            if (mixTotal <= 0.0)
                return 0.0;

            foreach (var mode in combatant.Modes)
            {
                var share = mode.Mix / mixTotal;
                var weight = mode.TotalWeight;

                if (weight <= 0.0)
                    continue;

                foreach (var cycle in mode.Cycles)
                {
                    var w = share * cycle.Weight / weight;

                    damage += w * cycle.StrikeFractions.Length * mode.HitChance * mode.MeanLandedDamage;
                    seconds += w * (cycle.LeadSeconds + cycle.AnimSeconds / speed + cycle.FixedSeconds + (includeRecovery ? mode.RecoveryMax / 2.0 : 0.0) + mode.FixedDelay);
                }
            }

            return seconds > 0.0 ? damage / seconds : 0.0;
        }

        // ---- the stochastic duel ----

        private enum EventKind { Cycle, Strike, Expire }

        private readonly struct Event
        {
            public readonly EventKind Kind;
            public readonly int Who;
            public readonly DuelAttackMode Mode;

            /// <summary>The combatant the swing was aimed at when it began; a strike whose target has died meanwhile is dropped.</summary>
            public readonly int Target;

            public Event(EventKind kind, int who, DuelAttackMode mode, int target = -1)
            {
                Kind = kind;
                Who = who;
                Mode = mode;
                Target = target;
            }
        }

        /// <summary>
        /// Runs <paramref name="trials"/> duels of the pet against monsters.Count monsters and totals them. Deterministic
        /// for a given (seed, inputs): one DuelRandom stream is consumed in a fixed order.
        /// </summary>
        public static DuelSummary Run(DuelCombatant pet, IReadOnlyList<DuelCombatant> monsters, int trials, int seed)
        {
            if (pet == null)
                throw new ArgumentNullException(nameof(pet));

            if (monsters == null || monsters.Count == 0)
                throw new ArgumentException("at least one monster is required", nameof(monsters));

            var rng = new DuelRandom(seed);

            var summary = new DuelSummary { Trials = trials };

            double hpAll = 0.0, hpWins = 0.0, deathTime = 0.0, killTime = 0.0, damage = 0.0, fought = 0.0;

            for (var i = 0; i < trials; i++)
            {
                var t = RunOne(pet, monsters, rng);

                damage += t.PetDamage;
                fought += t.Duration;
                hpAll += t.PetHpFraction;

                if (t.PetWon)
                {
                    summary.Wins++;
                    hpWins += t.PetHpFraction;
                    killTime += t.Duration;
                }
                else if (t.PetDied)
                {
                    summary.Deaths++;
                    deathTime += t.Duration;
                }
                else
                {
                    summary.Expired++;
                }
            }

            summary.WinRate = trials > 0 ? (double)summary.Wins / trials : 0.0;
            summary.MeanRemainingHpAll = trials > 0 ? hpAll / trials : 0.0;

            if (summary.Wins > 0)
            {
                summary.MeanRemainingHpWins = hpWins / summary.Wins;
                summary.MeanTimeToKill = killTime / summary.Wins;
            }

            if (summary.Deaths > 0)
                summary.MeanTimeToDeath = deathTime / summary.Deaths;

            summary.DpsDealt = fought > 0.0 ? damage / fought : 0.0;
            summary.Draws = rng.Draws;

            return summary;
        }

        private struct Trial
        {
            public bool PetWon, PetDied;
            public double PetHpFraction, Duration, PetDamage;
        }

        private static Trial RunOne(DuelCombatant pet, IReadOnlyList<DuelCombatant> monsters, DuelRandom rng)
        {
            // index 0 is the pet, 1..N the monsters
            var all = new List<DuelCombatant> { pet };
            all.AddRange(monsters);

            var n = all.Count;
            var hp = all.Select(c => c.MaxHealth).ToArray();
            var alive = Enumerable.Repeat(true, n).ToArray();
            var stacks = new int[n];
            var lastTrigger = new double[n];
            var swings = new int[n];

            var queue = new PriorityQueue<Event, (double Time, long Seq)>();
            long seq = 0;

            void Push(double time, Event e) => queue.Enqueue(e, (time, seq++));

            for (var who = 0; who < n; who++)
                Push(0.0, new Event(EventKind.Cycle, who, null));

            var limit = Math.Min(HardTimeLimit, pet.LifespanSeconds);

            if (!double.IsPositiveInfinity(pet.LifespanSeconds) && pet.LifespanSeconds <= HardTimeLimit)
                Push(pet.LifespanSeconds, new Event(EventKind.Expire, 0, null));

            var petDamage = 0.0;
            var now = 0.0;
            var monstersAlive = n - 1;

            while (queue.TryDequeue(out var e, out var key))
            {
                now = key.Time;

                if (now > limit)
                {
                    now = limit;
                    break;
                }

                if (e.Kind == EventKind.Expire)
                    break;

                if (!alive[e.Who])
                    continue;

                var me = all[e.Who];

                if (e.Kind == EventKind.Cycle)
                {
                    if (me.Ramp != null && stacks[e.Who] > 0 && now - lastTrigger[e.Who] >= me.Ramp.WindowSeconds)
                        stacks[e.Who] = 0;

                    var speed = me.Ramp == null ? 1.0 : me.Ramp.Multiplier(stacks[e.Who]);

                    // the swing is aimed at a target when it begins (Monster_Melee.MeleeAttack); with none left there is no swing
                    var aimed = e.Who == 0 ? FirstAlive(alive, 1) : 0;

                    if (aimed < 0)
                        continue;

                    var mode = PickMode(me, rng, swings[e.Who]++);

                    if (mode == null)
                        continue;

                    var cycle = PickCycle(mode, rng);

                    var anim = cycle.AnimSeconds / speed;

                    foreach (var f in cycle.StrikeFractions)
                        Push(now + cycle.LeadSeconds + f * anim, new Event(EventKind.Strike, e.Who, mode, aimed));

                    var recovery = mode.RecoveryMax > 0.0 ? rng.NextDouble() * mode.RecoveryMax : 0.0;

                    // a guard against a zero-length cycle spinning the queue
                    var next = Math.Max(cycle.LeadSeconds + anim + cycle.FixedSeconds + recovery + mode.FixedDelay, 0.05);

                    Push(now + next, new Event(EventKind.Cycle, e.Who, null));

                    continue;
                }

                // a strike
                if (e.Who == 0)
                {
                    // a melee strike lands on the target the swing was aimed at and is dropped if that one has died meanwhile
                    // (Monster_Melee.cs: "target.IsDead" returns); a launched arrow flies at whoever is the target at launch
                    var target = e.Mode.IsMissile ? FirstAlive(alive, 1) : e.Target;

                    if (target < 0 || !alive[target])
                        continue;

                    var arrows = new List<(int Target, double Mult)> { (target, 1.0) };

                    if (e.Mode.IsMissile && me.Multishot != null && me.Multishot.ExtraArrows > 0)
                    {
                        var secondaries = new List<int>();

                        for (var j = 1; j < n; j++)
                            if (alive[j] && j != target)
                                secondaries.Add(j);

                        var plan = MultiShotOverflow.Plan(Enumerable.Repeat((float)me.Multishot.ArrowMultiplier, me.Multishot.ExtraArrows).ToList(), secondaries.Count);

                        foreach (var shot in plan)
                            arrows.Add((shot.IsOverflow ? target : secondaries[shot.ExtraTargetIndex], shot.DamageMultiplier));
                    }

                    foreach (var (t, mult) in arrows)
                    {
                        if (!alive[t])
                            continue;

                        var dealt = Strike(me, e.Mode, all[t], mult, rng, ref hp[t]);

                        if (dealt.Landed)
                        {
                            petDamage += dealt.Applied;

                            Heal(me, ref hp[0], dealt.Applied);
                            Trigger(me, ref stacks[0], ref lastTrigger[0], now);
                        }

                        if (hp[t] <= 0.0)
                        {
                            alive[t] = false;
                            monstersAlive--;
                        }
                    }

                    if (monstersAlive == 0)
                        return new Trial { PetWon = true, PetHpFraction = Math.Max(0.0, hp[0]) / pet.MaxHealth, Duration = now, PetDamage = petDamage };
                }
                else
                {
                    var dealt = Strike(me, e.Mode, pet, 1.0, rng, ref hp[0]);

                    if (dealt.Landed)
                    {
                        Heal(me, ref hp[e.Who], dealt.Applied);
                        Trigger(me, ref stacks[e.Who], ref lastTrigger[e.Who], now);
                    }

                    if (hp[0] <= 0.0)
                        return new Trial { PetDied = true, PetHpFraction = 0.0, Duration = now, PetDamage = petDamage };
                }
            }

            // out of events or out of time: the pet's lifespan ran out (or the hard limit), with monsters still standing
            return new Trial { PetHpFraction = Math.Max(0.0, hp[0]) / pet.MaxHealth, Duration = now, PetDamage = petDamage };
        }

        private static int FirstAlive(bool[] alive, int from)
        {
            for (var i = from; i < alive.Length; i++)
                if (alive[i])
                    return i;

            return -1;
        }

        private static DuelAttackMode PickMode(DuelCombatant c, DuelRandom rng, int swing)
        {
            if (c.Alternate && c.Modes.Length > 0)
                return c.Modes[swing % c.Modes.Length];

            var total = c.Modes.Sum(m => m.Mix);

            if (total <= 0.0 || c.Modes.Length == 0)
                return null;

            if (c.Modes.Length == 1)
                return c.Modes[0];

            var roll = rng.NextDouble() * total;

            foreach (var m in c.Modes)
            {
                roll -= m.Mix;

                if (roll < 0.0)
                    return m;
            }

            return c.Modes[c.Modes.Length - 1];
        }

        private static DuelAttackCycle PickCycle(DuelAttackMode mode, DuelRandom rng)
        {
            if (mode.Cycles.Length == 1)
                return mode.Cycles[0];

            var roll = rng.NextDouble() * mode.TotalWeight;

            foreach (var c in mode.Cycles)
            {
                roll -= c.Weight;

                if (roll < 0.0)
                    return c;
            }

            return mode.Cycles[mode.Cycles.Length - 1];
        }

        private readonly struct StrikeResult
        {
            public readonly bool Landed;
            public readonly double Applied;

            public StrikeResult(bool landed, double applied)
            {
                Landed = landed;
                Applied = applied;
            }
        }

        /// <summary>One hit roll, one crit roll, the target's incoming multiplier, then the damage taken off its health (capped at what it had).</summary>
        private static StrikeResult Strike(DuelCombatant attacker, DuelAttackMode mode, DuelCombatant target, double mult, DuelRandom rng, ref double targetHp)
        {
            if (!rng.Chance(mode.HitChance))
                return new StrikeResult(false, 0.0);

            var raw = (rng.Chance(mode.CritChance) ? mode.CritDamage : mode.NormalDamage) * mult * target.IncomingMultiplier;

            var applied = Math.Min(raw, Math.Max(0.0, targetHp));

            targetHp -= raw;

            return new StrikeResult(true, applied);
        }

        private static void Heal(DuelCombatant c, ref double hp, double dealt)
        {
            if (c.LeechFraction > 0.0 && hp > 0.0)
                hp = Math.Min(c.MaxHealth, hp + dealt * c.LeechFraction);
        }

        private static void Trigger(DuelCombatant c, ref int stacks, ref double last, double now)
        {
            if (c.Ramp == null)
                return;

            stacks = Math.Min(stacks + 1, Math.Max(1, c.Ramp.MaxStacks));
            last = now;
        }
    }

    /// <summary>
    /// The class-ability arithmetic of the sizing build, from the same pure functions the live abilities call
    /// (EmpoweredSummonsAbility.StatMultiplier / LeechFraction / DurationMultiplier, SoulTetherAbility.DamageMultiplier,
    /// UmbralSiphonAbility.LeechFraction, ClassAbilityAffinity.Multiplier) with the Player left out: a synthetic
    /// build is a set of ranks plus the three skills' effective values and whether each is specialized. Tunables arrive
    /// as a record so the arithmetic is testable without PropertyManager.
    /// </summary>
    public static class SummonBuild
    {
        public sealed record Tunables(
            double AffinityRatePerTrained,
            double AffinityRatePerSpec,
            double EmpoweredPercentPerRank,
            double EmpoweredLeechPerRank,
            double EmpoweredLoyaltyPerTrained,
            double EmpoweredLoyaltyPerSpec,
            double EmpoweredMaxDurationBonus,
            double SoulTetherBase,
            double SoulTetherStep,
            double SoulTetherMaxReduction,
            double UmbralSiphonPerRank);

        public sealed record Ranks(int EmpoweredSummons, int SoulTether, int UmbralSiphon, bool Summon2x);

        public sealed record Skills(double Summoning, double Leadership, double Loyalty, bool LeadershipSpecialized, bool LoyaltySpecialized);

        /// <summary>The numbers the build contributes to a duel.</summary>
        public sealed record Result(float StatMod, float DurationMod, double EmpoweredLeech, double UmbralLeech, double SoulTetherMultiplier)
        {
            /// <summary>Both leeches heal the pet (Empowered Summons' recipient is the pet, see Pet.ApplyEmpoweredSummonsLeech).</summary>
            public double TotalLeech => EmpoweredLeech + UmbralLeech;
        }

        /// <param name="statModOverride">When set, replaces the computed Empowered Summons stat multiplier (a reference build pinned by its StatMod, not by the skills that would produce it).</param>
        public static Result Compute(Ranks ranks, Skills skills, Tunables t, double? statModOverride = null)
        {
            var leadership = ClassAbilityAffinity.Multiplier(skills.Leadership, skills.LeadershipSpecialized, t.AffinityRatePerTrained, t.AffinityRatePerSpec);
            var loyalty = ClassAbilityAffinity.Multiplier(skills.Loyalty, skills.LoyaltySpecialized, t.AffinityRatePerTrained, t.AffinityRatePerSpec);

            // Player.GetEmpoweredSummonsStatMod: the affinity MULTIPLIES the rank bonus; StatMultiplier wants the amount it adds
            var statRank = ranks.EmpoweredSummons * t.EmpoweredPercentPerRank;
            var statMod = EmpoweredSummonsAbility.StatMultiplier(ranks.EmpoweredSummons, t.EmpoweredPercentPerRank, statRank * leadership - statRank);

            // Player.GetEmpoweredSummonsDurationMod: Loyalty points per +1 percent, rank-gated
            var loyaltyFraction = ClassAbilityScaling.Compute(skills.Loyalty, skills.LoyaltySpecialized, t.EmpoweredLoyaltyPerTrained, t.EmpoweredLoyaltyPerSpec) * 0.01;
            var durationMod = EmpoweredSummonsAbility.DurationMultiplier(ranks.EmpoweredSummons, loyaltyFraction, t.EmpoweredMaxDurationBonus);

            // Player.GetEmpoweredSummonsLeechFraction (Leadership rider)
            var leechRank = ranks.EmpoweredSummons * t.EmpoweredLeechPerRank;
            var empoweredLeech = EmpoweredSummonsAbility.LeechFraction(ranks.EmpoweredSummons, t.EmpoweredLeechPerRank, leechRank * leadership - leechRank);

            // Player.GetUmbralSiphonLeechFraction (Loyalty rider)
            var umbralRank = ranks.UmbralSiphon * t.UmbralSiphonPerRank;
            var umbralLeech = UmbralSiphonAbility.LeechFraction(ranks.UmbralSiphon, t.UmbralSiphonPerRank, umbralRank * loyalty - umbralRank);

            // Player.GetSoulTetherDamageReductionMod (Loyalty rider, no gear term)
            var tetherRank = ranks.SoulTether <= 0 ? 0.0 : t.SoulTetherBase + (ranks.SoulTether - 1) * t.SoulTetherStep;
            var tether = ranks.SoulTether <= 0
                ? 1.0f
                : SoulTetherAbility.DamageMultiplier(ranks.SoulTether, t.SoulTetherBase, t.SoulTetherStep, tetherRank * loyalty - tetherRank, t.SoulTetherMaxReduction);

            if (statModOverride.HasValue)
                statMod = (float)statModOverride.Value;

            return new Result(statMod, durationMod, empoweredLeech, umbralLeech, tether);
        }
    }
}
