using System;
using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Vanguard T3, two halves on one entry:
    ///
    ///  - THE BOUNCE. Incoming PROJECTILES of every kind - arrows, crossbow bolts, thrown weapons, war bolts
    ///    and void projectiles - have a 6/12/18% chance by rank to bounce straight back at whoever fired
    ///    them. The Vanguard takes nothing on a bounce, riders included; the projectile resolves against the
    ///    firer instead - the firer's own rolled attack against the firer's own defenses (owner ruling
    ///    2026-10-23), landing when a cosmetic copy flown back at it arrives. The shot's DoT and debuff
    ///    riders land on the firer, a leech is mirrored onto the Vanguard, and every other rider is dropped
    ///    (Player.LaunchReflectedMissile / LaunchReflectedSpell / ApplyReflectBounce).
    ///  - THE IMMUNITY WINDOW (owner ruling 2026-10-17). After a creature's SPELL damages the Vanguard, they
    ///    are immune to creature spell damage for 0.15/0.30/0.45 seconds by rank. "Spell damage" is every
    ///    spell route that reaches Health: spell projectiles, Harm, Drain Health and DoT ticks.
    ///
    /// Magic Defense multiplies both: the bounce chance with its affinity added amount capped by
    /// class_ability_affinity_chance_cap, exactly as before, and the window at the natural rate with NO cap.
    ///
    /// THE BOUNCE IS PROJECTILES ONLY, AND THAT IS A HARD BOUNDARY: a melee swing, a non-projectile spell and
    /// a damage-over-time tick never bounce. Reflecting anything that is not a projectile would give the
    /// entry a second, much larger coverage area on the same roll. The boundary is enforced twice, on
    /// purpose. The two bounce sites are projectile collision handlers and reach no melee path at all, which
    /// is the structural half; but a call-site list is only correct until someone adds a site, so
    /// <see cref="IsReflectableCombatType"/> re-decides it from the attack's own CombatType inside
    /// Player.TryReflectProjectile. The bounce is still dispatched BY HAND from there: the two sites a
    /// projectile can reach a player (ProjectileCollisionHelper for arrows and bolts,
    /// SpellProjectile.OnCollideObject for war and void) share no common sink and neither is a damage hook.
    ///
    /// THE WINDOW RIDES THE PRE-WRITE HOOK, at <see cref="DamageMitigationOrder.Immunity"/>, because that is
    /// the one dispatch all four spell routes already reach (see IPreWriteDamageAbility). That makes this a
    /// hooked entry, so it no longer carries IPassiveStatAbility: ClassAbilityRegistryTests forbids a
    /// passive marker on a handler that implements a combat hook. The hook also fires on melee hits and on
    /// PvP damage, and is gated to creature spell damage purely through
    /// PreWriteDamageContext.SpellDamageFromEnemyCreature, which is 0 for both.
    ///
    /// The window's rules, all in <see cref="DecideImmunity"/> so they are unit tested rather than read off
    /// the handler:
    ///  - the hit that ARMS the window is not blocked;
    ///  - inside a live window only the enemy-creature spell share of a hit is removed (a DoT tick that
    ///    mixes a creature's enchantment with a player's keeps the player's part);
    ///  - a hit blocked inside a live window neither re-arms nor extends it, so a stream of fast creature
    ///    spells cannot chain the window into permanent immunity;
    ///  - every creature DoT tick in ONE DoT heartbeat counts as ONE hit (<see cref="DecideDotPassTick"/>):
    ///    the heartbeat's ticks never block each other and arm the window at most once, while a window
    ///    armed before the heartbeat still blocks them all;
    ///  - a hit blocked to 0 still runs everything downstream of the dispatch (Thorns, the hit message, the
    ///    cloak proc roll). Nothing is suppressed here, and the cloak spell proc rolls on the hit as it stood
    ///    BEFORE the window removed it, so a blocked hit still counts for proc chance.
    ///
    /// NOTE THE NAME ASYMMETRY, which is deliberate and comes from the design table: the enum member is
    /// ReflectMagic and the canonical token is "reflect_magic", but it DISPLAYS to players as "Reflect".
    /// Do not "fix" any of the three to match the others.
    ///
    /// THE BOUNCE NO LONGER HOLDS THE REFLECT LATCH (owner ruling 2026-10-23), so the firer's wards and
    /// immunities apply to it and to its Thorns answer. It stays bounded because both reach the firer through
    /// its normal TakeDamage as SECONDARY damage, and the only monster incoming-damage effect that hits back -
    /// "reflect" on=hit - answers a DirectHit only; the latch itself (Creature.TryEnterReflect / ExitReflect,
    /// held across the monster incoming-damage dispatch in Creature_MonsterEffects.cs) still bounds that
    /// effect's own answer. Player.TryReflectProjectile reads Creature.IsReflectResolving and refuses a bounce
    /// that would start inside another reflect's resolution.
    /// </summary>
    public class ReflectMagicAbility : IClassAbility, IPreWriteDamageAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.ReflectMagic,
            AbilityClass = ClassAbilityClass.Vanguard,
            Tier = 3,
            Name = "reflect_magic",
            DisplayName = "Reflect",
            Description = "Incoming projectile hits that would land, of every kind - arrows, bolts, thrown weapons, war bolts, " +
                          "and void projectiles - have a 6/12/18% chance (by rank) to bounce straight back " +
                          "at whoever fired them. You take nothing; the projectile resolves against them " +
                          "instead. In addition, after a creature's spell damages you, you are immune to " +
                          "creature spell damage for 0.15/0.3/0.45 seconds (by rank). Higher Magic Defense " +
                          "multiplies both the chance and the immunity window.",
            MaxRank = 3,
            CostPerRank = new[] { 1, 1, 1 },
            Implemented = true,
            AffinitySkill = Skill.MagicDefense,
        };

        /// <summary>
        /// Immunity band: after the observers (so Adrenaline and Kinetic Charge still see a blocked hit as
        /// having reached the Vanguard) and ahead of every absorb pool (so a hit the window cancels never
        /// drains a ward or charges Mana Barrier). See DamageMitigationOrder.Immunity.
        /// </summary>
        public int MitigationOrder => DamageMitigationOrder.Immunity;

        /// <summary>
        /// FALSE. The window is a timestamp recomputed from the live rank each time it arms, and a window
        /// left open after an unlearn lapses on its own within a fraction of a second - nothing is stranded.
        /// </summary>
        public bool RunsWithoutLearnedRank => false;

        /// <summary>
        /// The "projectiles only" rule as a pure decision, so it can be unit tested and so every future
        /// dispatch site inherits it instead of re-deciding it.
        ///
        /// Missile is arrows / bolts / thrown weapons; Magic is a spell PROJECTILE, and the only magic that
        /// reaches the one site that passes it is SpellProjectile.OnCollideObject - a flying, collidable
        /// projectile. Melee is excluded outright and is the case this method exists to refuse: a melee swing
        /// carries no projectile and must never be reflected no matter where this is called from.
        /// </summary>
        public static bool IsReflectableCombatType(CombatType combatType)
        {
            return combatType == CombatType.Missile || combatType == CombatType.Magic;
        }

        /// <summary>
        /// The reflect chance at a given rank: the ability's OWN rank bonus (base + step*(rank-1)) SCALED by
        /// the Magic Defense affinity multiplier, with the added amount CAPPED. Pure for testability,
        /// matching PocketSandAbility.Chance / SundermarkAbility.Chance.
        ///
        /// <paramref name="affinityMultiplier"/> is what
        /// Player.GetClassAbilityAffinityMultiplier(Skill.MagicDefense) returns: a factor >= 1.0 that is
        /// EXACTLY 1.0 at zero effective Magic Defense, leaving the chance bit-identical to rank alone.
        /// Floored at 1.0 here as well, so a caller that hands over 0.0 - the neutral value of the OLD
        /// additive primitive, and an easy mistake because the two have identical shapes - degrades to
        /// rank-only rather than silently multiplying the whole bonus away.
        ///
        /// <paramref name="affinityCap"/> bounds the AMOUNT the affinity multiply ADDS
        /// (rankBonus * multiplier - rankBonus), never the rank ladder and never the factor itself
        /// (class_ability_affinity_chance_cap). It applies here because this IS a proc chance, which is what
        /// that tunable's own doc comment scopes it to, and because the affinity is linear in a skill value
        /// the server does not constrain - without the clamp a high Magic Defense would walk a full
        /// negation of every incoming projectile toward certainty. A cap of 0 means uncapped. There is no
        /// equipment mod for this entry, so no gear term sits outside the clamp.
        /// </summary>
        public static double ReflectChance(int rank, double chanceBase, double chanceStep, double affinityMultiplier, double affinityCap = 0.0)
        {
            if (rank <= 0)
                return 0.0;

            var rankBonus = chanceBase + (rank - 1) * chanceStep;

            // The multiplier scales this ability's OWN rank bonus. What the cap bounds is the AMOUNT that
            // multiply ADDS, not the factor itself - the factor is a bare number like 1.51 and clamping it
            // against a tunable expressed in percentage points would be a unit error.
            var added = rankBonus * Math.Max(1.0, affinityMultiplier) - rankBonus;

            if (affinityCap > 0.0)
                added = Math.Min(added, affinityCap);

            return Math.Max(0.0, rankBonus + added);
        }

        /// <summary>
        /// The live bounce chance for <paramref name="player"/> at <paramref name="rank"/>, read from the
        /// tunables and the player's Magic Defense affinity. The single place the bounce's affinity is read,
        /// so Player.TryReflectProjectile carries no affinity call of its own (ClassAbilityAffinityDeclarationTests
        /// scans this file for it). A null player is treated as the neutral 1.0 factor.
        /// </summary>
        public static double ChanceFor(Player player, int rank)
        {
            return ReflectChance(rank,
                PropertyManager.GetDouble("class_ability_reflectmagic_chance_base").Item,
                PropertyManager.GetDouble("class_ability_reflectmagic_chance_step").Item,
                player?.GetClassAbilityAffinityMultiplier(Skill.MagicDefense) ?? 1.0,
                PropertyManager.GetDouble("class_ability_affinity_chance_cap").Item);
        }

        /// <summary>
        /// The immunity window in seconds: (base + step*(rank-1)) multiplied by the Magic Defense affinity
        /// factor, floored at 1.0 so a 0.0 factor degrades to rank-only. UNCAPPED BY OWNER RULING: neither
        /// class_ability_affinity_chance_cap nor any other clamp applies to the window. 0 at rank 0 or below,
        /// and never negative.
        /// </summary>
        public static double ImmunityWindowSeconds(int rank, double secondsBase, double secondsStep, double affinityMultiplier)
        {
            if (rank <= 0)
                return 0.0;

            var rankSeconds = secondsBase + (rank - 1) * secondsStep;

            return Math.Max(0.0, rankSeconds * Math.Max(1.0, affinityMultiplier));
        }

        /// <summary>The outcome of one hit against the immunity window. See <see cref="DecideImmunity"/>.</summary>
        public readonly struct ImmunityDecision
        {
            public ImmunityDecision(uint damage, double windowEnd, uint blocked)
            {
                Damage = damage;
                WindowEnd = windowEnd;
                Blocked = blocked;
            }

            /// <summary>What is left of the hit for the handlers after this one.</summary>
            public uint Damage { get; }

            /// <summary>The Unix time the window now closes at; unchanged when the hit neither armed nor reset it.</summary>
            public double WindowEnd { get; }

            /// <summary>How much this hit lost to a live window; 0 when nothing was blocked.</summary>
            public uint Blocked { get; }
        }

        /// <summary>
        /// One hit against the window. Pure, so every rule is a unit test:
        ///  - LIVE WINDOW (now strictly before <paramref name="windowEnd"/>): the enemy-creature spell share,
        ///    clamped to the hit, is removed and reported as Blocked. The window is NOT re-armed or extended.
        ///    A hit with no such share (a melee swing, a PvP spell) passes through untouched.
        ///  - NO LIVE WINDOW: a hit with a positive share ARMS it at now + <paramref name="seconds"/> and is
        ///    itself NOT blocked. A hit with no share, or a non-positive window length, changes nothing.
        /// </summary>
        public static ImmunityDecision DecideImmunity(double now, double windowEnd, uint enemySpellShare, uint damage, double seconds)
        {
            var share = Math.Min(enemySpellShare, damage);

            if (windowEnd > 0.0 && now < windowEnd)
                return new ImmunityDecision(damage - share, windowEnd, share);

            if (share > 0 && seconds > 0.0)
                return new ImmunityDecision(damage, now + seconds, 0);

            return new ImmunityDecision(damage, windowEnd, 0);
        }

        /// <summary>
        /// One DoT tick inside a DoT HEARTBEAT PASS (owner ruling 2026-10-17: every creature DoT tick applied
        /// in the same EnchantmentManager.HeartBeat_DamageOverTime counts as ONE hit). Pure, for the tests.
        ///  - The tick is decided against <paramref name="passStartWindowEnd"/>, the window as it stood when
        ///    the heartbeat began, never against a window an earlier tick of the SAME heartbeat armed. So a
        ///    window armed before the heartbeat (a bolt a moment earlier) still blocks every tick, while the
        ///    pass's own ticks never block each other.
        ///  - The window arms at most once per pass: once <paramref name="armedThisPass"/> is true, a later
        ///    creature tick lands in full and leaves <paramref name="currentWindowEnd"/> alone.
        /// The returned WindowEnd is what the player's window should now be; a caller detects "this tick
        /// armed" as WindowEnd differing from <paramref name="currentWindowEnd"/>.
        /// </summary>
        public static ImmunityDecision DecideDotPassTick(double now, double passStartWindowEnd, double currentWindowEnd, bool armedThisPass,
            uint enemySpellShare, uint damage, double seconds)
        {
            var decision = DecideImmunity(now, passStartWindowEnd, enemySpellShare, damage, seconds);

            if (decision.Blocked > 0)
                return new ImmunityDecision(decision.Damage, currentWindowEnd, decision.Blocked);

            var armed = decision.WindowEnd != passStartWindowEnd;

            if (armed && !armedThisPass)
                return decision;

            return new ImmunityDecision(damage, currentWindowEnd, 0);
        }

        /// <summary>
        /// RANK-GATED (reached only while a rank is held). Does nothing for a hit with no enemy-creature
        /// spell share, which is every melee hit, every PvP or self-inflicted spell, and every hit with no
        /// creature behind it - so the affinity read below is paid only on creature spell damage.
        /// </summary>
        public void ModifyIncomingDamage(Player defender, int rank, PreWriteDamageContext context)
        {
            // a hazard-library hit never opens or spends the immunity window (owner ruling 2026-10-19: hazards bypass
            // every ward). The physical TakeDamage path carries no enemy-creature spell share, so today this is a
            // second guard; it keeps a later change to that share from letting a ward back in.
            if (context.IsHazard)
                return;

            if (defender == null || rank <= 0 || context.SpellDamageFromEnemyCreature == 0 || context.Damage == 0)
                return;

            var seconds = ImmunityWindowSeconds(rank,
                PropertyManager.GetDouble("class_ability_reflectmagic_immunity_seconds_base").Item,
                PropertyManager.GetDouble("class_ability_reflectmagic_immunity_seconds_step").Item,
                defender.GetClassAbilityAffinityMultiplier(Skill.MagicDefense));

            context.Damage = defender.ApplyReflectImmunityWindow(context.Damage, context.SpellDamageFromEnemyCreature, seconds, context.IsDamageOverTime);
        }

        /// <summary>
        /// The PRIMARY line reports the bounce chance. Affinity is the CAPPED amount the Magic Defense
        /// multiply ADDS (clamped the same way ReflectChance clamps it), so Skill + Affinity + Gear still
        /// sums to Effective exactly, which ClassAbilityAffinityCapReadoutInvariantTests pins for this
        /// readout family.
        ///
        /// The SECONDARY line reports the immunity window in MILLISECONDS: Skill is the rank-only window,
        /// Affinity is what the uncapped multiply adds, Effective is their sum. It never carries a CapNote,
        /// because nothing caps it. Omitted at rank 0, per ClassAbilityReadout.Secondary's contract.
        ///
        /// The null-conditional on the affinity read is for the readout unit tests, which call this with a
        /// null Player because Player's static initializer cannot run under the test host - and it falls
        /// back to 1.0, the NEUTRAL factor, never 0.0.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var chanceBase = PropertyManager.GetDouble("class_ability_reflectmagic_chance_base").Item;
            var chanceStep = PropertyManager.GetDouble("class_ability_reflectmagic_chance_step").Item;
            var affinityCap = PropertyManager.GetDouble("class_ability_affinity_chance_cap").Item;

            var rankBonus = rank <= 0 ? 0.0 : chanceBase + (rank - 1) * chanceStep;

            // NEUTRAL IS 1.0, NOT 0.0. The null-Player fallback must be the identity factor: 0.0 would not
            // omit the rider, it would multiply this ability's entire rank bonus away.
            var multiplier = player?.GetClassAbilityAffinityMultiplier(Skill.MagicDefense) ?? 1.0;

            // The RAW (pre-clamp) amount the multiply adds. This is the quantity the affinity cap bounds,
            // so it is also the quantity the cap must be compared against.
            var rawAdded = Math.Max(0.0, rankBonus * multiplier - rankBonus);

            // ReflectChance applies the clamp itself; this only records whether it bit, for CapNote.
            var affinityCapBites = affinityCap > 0.0 && rawAdded > affinityCap;

            // The CAPPED added amount, so Affinity below matches what ReflectChance actually folds in.
            var clampedAdded = affinityCap > 0.0 ? Math.Min(rawAdded, affinityCap) : rawAdded;

            var chance = ReflectChance(rank, chanceBase, chanceStep, multiplier, affinityCap);

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = rankBonus * 100.0,
                Affinity = clampedAdded * 100.0,
                Gear = 0.0,
                Effective = chance * 100.0,
                Unit = "%",
                Label = "projectile reflect",
                Per = null,
                CapNote = affinityCapBites ? "affinity cap" : null,
                Secondary = rank > 0 ? new List<ClassAbilityReadout> { ImmunitySecondary(rank, multiplier) } : null,
            };
        }

        /// <summary>
        /// The immunity-window readout line, mirroring <see cref="ImmunityWindowSeconds"/> exactly. Public
        /// and static so the readout tests can pin it at a non-neutral multiplier without a live Player.
        /// </summary>
        public static ClassAbilityReadout ImmunitySecondary(int rank, double affinityMultiplier)
        {
            var secondsBase = PropertyManager.GetDouble("class_ability_reflectmagic_immunity_seconds_base").Item;
            var secondsStep = PropertyManager.GetDouble("class_ability_reflectmagic_immunity_seconds_step").Item;

            // MILLISECONDS, not seconds: /abilities formats every readout number with one decimal place, which
            // would show 0.15/0.30/0.45s as 0.2/0.3/0.5. Every term is x1000 so they still sum to Effective.
            var rankMs = ImmunityWindowSeconds(rank, secondsBase, secondsStep, 1.0) * 1000.0;
            var affinityMs = ImmunityWindowSeconds(rank, secondsBase, secondsStep, affinityMultiplier) * 1000.0 - rankMs;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = rankMs,
                Affinity = affinityMs,
                Gear = 0.0,
                Effective = rankMs + affinityMs,
                Unit = "ms",
                Label = "spell immunity",
                Per = null,
                CapNote = null,
            };
        }
    }
}
