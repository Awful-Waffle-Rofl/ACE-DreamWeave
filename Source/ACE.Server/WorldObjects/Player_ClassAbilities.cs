using System;
using System.Collections.Generic;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Database;
using ACE.Database.Models.Shard;
using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Entity;
using ACE.Server.Entity.Facets;
using ACE.Server.EquipmentMods;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Pvp;
using ACE.Server.Pvp.Rules;
using ACE.Server.Pvp.Templates;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        // Class ability system - see ACE.Server.ClassAbilities for the definitions, hook interfaces,
        // and per-skill handler classes, and ClassAbilityCommands for the /abilities command surface.
        //
        // Ownership/rank is stored as one quest registry row per owned skill (ClassAbility_<name>,
        // NumTimesCompleted = rank), written through the Character extensions directly - NOT through
        // QuestManager, whose SetQuestCompletions clamps any quest missing from the world quest table
        // to 0 solves. Point balances are custom PropertyInts on the biota.

        /// <summary>
        /// In-memory rank cache so combat hot paths pay one dictionary lookup per hit,
        /// never a string-keyed quest registry scan. Built lazily, kept in sync by Learn/Unlearn.
        /// </summary>
        private Dictionary<ClassAbilityId, int> classAbilityCache;

        private Dictionary<ClassAbilityId, int> GetClassAbilityCache()
        {
            if (classAbilityCache == null)
            {
                classAbilityCache = new Dictionary<ClassAbilityId, int>();

                foreach (var quest in Character.GetQuests(CharacterDatabaseLock))
                {
                    if (ClassAbilityRegistry.TryGetByQuestKey(quest.QuestName, out var skill))
                        classAbilityCache[skill.Id] = Math.Clamp(quest.NumTimesCompleted, 0, skill.MaxRank);
                }
            }
            return classAbilityCache;
        }

        /// <summary>
        /// Returns the player's current rank in a class ability, 0 if not learned
        /// </summary>
        public int GetClassAbilityRank(ClassAbilityId id)
        {
            return GetClassAbilityCache().TryGetValue(id, out var rank) ? rank : 0;
        }

        /// <summary>
        /// Total class ability points spent (cumulative, all owned ranks) on skills belonging to the given
        /// class - the "points spent in class" half of the Tier 2/3 unlock. Ignores the untiered
        /// Enhanced-stat family (AbilityClass.None).
        /// </summary>
        public int PointsSpentInClass(ClassAbilityClass abilityClass)
        {
            if (abilityClass == ClassAbilityClass.None)
                return 0;

            var spent = 0;
            foreach (var (id, rank) in GetClassAbilityCache())
            {
                if (rank <= 0 || !ClassAbilityRegistry.Abilities.TryGetValue(id, out var def))
                    continue;
                if (def.AbilityClass == abilityClass)
                    spent += def.CumulativeCost(rank);
            }
            return spent;
        }

        /// <summary>
        /// Whether the player meets a Tier 2/3 skill's unlock prerequisites: a minimum lifetime-earned CAP
        /// count AND a minimum number of points already spent within that skill's class. Tier &lt;= 1 and
        /// the untiered Enhanced-stat family (Tier 0) are always unlocked. Returns FALSE with a
        /// player-facing reason on a lock.
        /// </summary>
        public bool MeetsClassAbilityTierUnlock(ClassAbilityDefinition skill, out string error)
        {
            error = null;

            if (skill.Tier <= 1 || skill.AbilityClass == ClassAbilityClass.None)
                return true;

            var (cspKey, spentKey) = skill.Tier >= 3
                ? ("class_ability_tier3_cap_required", "class_ability_tier3_spent_required")
                : ("class_ability_tier2_cap_required", "class_ability_tier2_spent_required");

            var cspRequired = (int)PropertyManager.GetLong(cspKey).Item;
            var spentRequired = (int)PropertyManager.GetLong(spentKey).Item;

            if (TotalClassAbilityPointsEarned < cspRequired)
            {
                error = $"{skill.DisplayName} is a Tier {skill.Tier} skill - you must have earned at least {cspRequired:N0} class ability points to unlock it (you have earned {TotalClassAbilityPointsEarned:N0}).";
                return false;
            }

            var spent = PointsSpentInClass(skill.AbilityClass);
            if (spent < spentRequired)
            {
                error = $"{skill.DisplayName} is a Tier {skill.Tier} {skill.AbilityClass} skill - you must have spent at least {spentRequired:N0} points on {skill.AbilityClass} skills to unlock it (you have spent {spent:N0}).";
                return false;
            }

            return true;
        }

        // Passive "Enhanced X" stat bonuses. These are read from the same combat-cold hot paths that
        // compute a skill/attribute/vital's Base and Current (CreatureSkill/CreatureAttribute/
        // CreatureVital), so they lead with the cheapest possible early-out: a player with no class
        // skills at all pays a single dictionary-count check. The bonus is a flat base-value increase
        // (+10 / +25 / +50 by rank), so it flows into wield-requirement checks and, for attributes,
        // cascades through AttributeFormula into every derived skill and vital.

        /// <summary>
        /// Flat base bonus to a skill from an "Enhanced &lt;skill&gt;" class ability, 0 if none / disabled.
        /// </summary>
        public int GetEnhancedSkillBonus(Skill skill)
        {
            // Character can be unset while a player object is still being constructed, and these
            // helpers ride the fundamental Base/Current getters - don't build the cache until it exists
            if (Character == null)
                return 0;

            var cache = GetClassAbilityCache();
            if (cache.Count == 0 || !PropertyManager.GetBool("class_abilities_enabled").Item)
                return 0;

            // PK facet rule or PvP arena mask (checked after the free early-outs, so a player with no class
            // abilities pays nothing extra on this hot path)
            if (ClassAbilitySuppressed)
                return 0;

            cache.TryGetValue(EnhancedStatAbility.ClassIdForSkill(skill), out var rank);
            var bonus = EnhancedStatAbility.BonusForRank(rank);

            // multi-skill bundles (Training / Advanced Weaponry / Questionable Tactics) stack additively
            // on top of the single Enhanced skill for any legacy skill they cover
            foreach (var bundle in ClassAbilityRegistry.StatBundleAbilities)
            {
                if (!cache.TryGetValue(bundle.Definition.Id, out var bundleRank) || bundleRank <= 0)
                    continue;

                var skills = bundle.BundledSkills;
                for (var i = 0; i < skills.Count; i++)
                {
                    if (skills[i] == skill)
                    {
                        bonus += EnhancedStatAbility.BonusForRank(bundleRank);
                        break;
                    }
                }
            }

            return bonus;
        }

        /// <summary>
        /// Flat base bonus to a primary attribute from an "Enhanced &lt;attribute&gt;" class ability,
        /// 0 if none / disabled. Cascades into derived skills and vitals via AttributeFormula.
        /// </summary>
        public int GetEnhancedAttributeBonus(PropertyAttribute attribute)
        {
            if (Character == null)
                return 0;

            var cache = GetClassAbilityCache();
            if (cache.Count == 0 || !PropertyManager.GetBool("class_abilities_enabled").Item)
                return 0;

            if (ClassAbilitySuppressed)
                return 0;

            cache.TryGetValue(EnhancedStatAbility.ClassIdForAttribute(attribute), out var rank);
            return EnhancedStatAbility.BonusForRank(rank);
        }

        /// <summary>
        /// Flat base bonus to a vital (max health/stamina/mana) from an "Enhanced &lt;vital&gt;"
        /// class ability, 0 if none / disabled.
        /// </summary>
        public int GetEnhancedVitalBonus(PropertyAttribute2nd vital)
        {
            if (Character == null)
                return 0;

            var cache = GetClassAbilityCache();
            if (cache.Count == 0 || !PropertyManager.GetBool("class_abilities_enabled").Item)
                return 0;

            if (ClassAbilitySuppressed)
                return 0;

            cache.TryGetValue(EnhancedStatAbility.ClassIdForVital(vital), out var rank);
            return EnhancedStatAbility.BonusForRank(rank);
        }

        /// <summary>
        /// Flat additive bonus into an ACE rating pool from a rating class ability (+3/6/10 by rank),
        /// 0 if not learned / disabled. Read at the matching GetXRating choke point in Creature_Rating.
        /// </summary>
        public int GetClassAbilityRating(ClassAbilityId id)
        {
            if (Character == null)
                return 0;

            var cache = GetClassAbilityCache();
            if (cache.Count == 0 || !PropertyManager.GetBool("class_abilities_enabled").Item)
                return 0;

            if (ClassAbilitySuppressed)
                return 0;

            cache.TryGetValue(id, out var rank);
            return RatingAbility.BonusForRank(rank);
        }

        /// <summary>
        /// Multiplicative damage-resist factor from the Battle Hardened class ability (1.0 = none),
        /// applied silently at the incoming-damage mitigation choke point (GetDamageResistRatingMod).
        /// Scales with the player's current (buffed) Strength, capped by a server tunable.
        /// </summary>
        public float GetBattleHardenedDamageResistMod()
        {
            // Bulwark equipment mod (STANDALONE): a flat damage reduction summed with Battle Hardened's
            // Strength-scaled reduction and clamped TOGETHER by the ability's own max-reduction ceiling, so
            // the mod can never lift total mitigation past that wall. Reachable at rank 0, where the
            // Strength term is zeroed by passing a per-Strength rate of 0 and the mod stands alone.
            var gearMod = GetEquippedModValue(EquipmentModId.Bulwark);

            // TryGetClassAbility already gates on class_abilities_enabled and a learned rank
            var owned = TryGetClassAbility(ClassAbilityId.BattleHardened, out _);

            if (!owned && gearMod <= 0.0)
                return 1.0f;

            var reductionPerStrength = owned ? PropertyManager.GetDouble("class_ability_battlehardened_reduction_per_strength").Item : 0.0;
            var maxReduction = PropertyManager.GetDouble("class_ability_battlehardened_max_reduction").Item;

            return BattleHardenedAbility.GetDamageResistMod(Strength.Current, reductionPerStrength, maxReduction, gearMod);
        }

        /// <summary>
        /// Peripheral-skill scaling for a class-ability hook (SKILL-TABLES-PREVIEW cross-class notes):
        /// the source legacy skill's effective (buffed / geared) value, gated on Trained/Specialized
        /// (an Untrained or Inactive source contributes 0), fed through the dual-ratio model. Returns a
        /// raw quotient ("+1 per divisor points above threshold"); the caller applies its own unit (flat
        /// damage vs. per-point percent). 0 when class abilities are disabled.
        /// </summary>
        public double GetClassAbilityScaling(Skill source, double perTrained, double perSpec, double threshold = 0.0)
        {
            if (Character == null || !PropertyManager.GetBool("class_abilities_enabled").Item)
                return 0.0;

            var cs = GetCreatureSkill(source);
            if (cs == null)
                return 0.0;

            var sac = cs.AdvancementClass;
            if (sac != SkillAdvancementClass.Trained && sac != SkillAdvancementClass.Specialized)
                return 0.0;

            return ClassAbilityScaling.Compute(cs.Current, sac == SkillAdvancementClass.Specialized, perTrained, perSpec, threshold);
        }

        /// <summary>
        /// MULTIPLICATIVE affinity for a class-ability hook: the source legacy skill's effective (buffed /
        /// geared) value, gated on Trained/Specialized, turned into a factor the caller MULTIPLIES the
        /// ability's own rank bonus by ("+rate of the bonus per 100 points of the skill").
        ///
        /// Returns the NEUTRAL value 1.0 - not 0.0 - wherever <see cref="GetClassAbilityScaling"/> returns
        /// 0.0: no character, class abilities disabled, no such skill, or an Untrained/Inactive source. That
        /// difference is the whole reason this is a separate method: 0.0 is "no rider" for an additive
        /// quotient and "delete the ability's bonus entirely" for a multiplicative factor.
        ///
        /// The gear term of a migrated ability stays ADDITIVE beside the multiplied rank bonus, so an
        /// equipment mod never compounds with affinity.
        /// </summary>
        public double GetClassAbilityAffinityMultiplier(Skill source, double ratePerTrained, double ratePerSpec)
        {
            if (Character == null || !PropertyManager.GetBool("class_abilities_enabled").Item)
                return 1.0;

            var cs = GetCreatureSkill(source);
            if (cs == null)
                return 1.0;

            var sac = cs.AdvancementClass;
            if (sac != SkillAdvancementClass.Trained && sac != SkillAdvancementClass.Specialized)
                return 1.0;

            return ClassAbilityAffinity.Multiplier(cs.Current, sac == SkillAdvancementClass.Specialized, ratePerTrained, ratePerSpec);
        }

        /// <summary>
        /// The standard-rate overload, reading the two SHARED affinity tunables. Every migrated ability
        /// calls this one except Bloodlust, which passes its own rates via the three-argument overload
        /// and has its own tunable pair rather than a field on the definition - it is off-standard in
        /// SHAPE, its two rates pinned EQUAL so that specializing Salvaging pays no more than training
        /// it. Void Damage and Soul Jump used to be off-standard in MAGNITUDE with their own pairs too;
        /// the 2026-10-02 owner ruling retired those two pairs and moved both abilities onto this
        /// overload, so a future change to the shared pair reaches them with no per-ability follow-up.
        /// </summary>
        public double GetClassAbilityAffinityMultiplier(Skill source)
        {
            return GetClassAbilityAffinityMultiplier(source,
                PropertyManager.GetDouble("class_ability_affinity_rate_per_trained").Item,
                PropertyManager.GetDouble("class_ability_affinity_rate_per_spec").Item);
        }

        /// <summary>
        /// Returns TRUE with the current rank if the player has learned this class ability and the
        /// system is enabled. Hook-based effects don't need this - it's for bespoke integrations
        /// (mechanics too entangled with core code to fit a hook) and command handlers.
        /// </summary>
        public bool TryGetClassAbility(ClassAbilityId id, out int rank)
        {
            rank = 0;

            if (!PropertyManager.GetBool("class_abilities_enabled").Item)
                return false;

            rank = GetClassAbilityRank(id);

            // PK facet rule or PvP arena mask: the ability reads as not held (rank 0). Checked only once a rank
            // is actually held, so the common not-learned lookup pays nothing extra.
            if (rank > 0 && ClassAbilitySuppressed)
                rank = 0;

            return rank > 0;
        }

        // Per-hook caches of this player's *learned* skill handlers, so the combat hot paths
        // iterate a tiny (usually empty) array rather than scanning the registry or the rank
        // dictionary. Built lazily from ClassAbilityRegistry's per-hook buckets, invalidated by
        // Learn/Unlearn.
        private (IOutgoingDamageAbility skill, int rank)[] outgoingDamageClassAbilities;
        private (IIncomingDamageAbility skill, int rank)[] incomingDamageClassAbilities;
        private (IPreWriteDamageAbility skill, int rank)[] preWriteDamageClassAbilities;
        private (IMissileVolleyAbility skill, int rank)[] missileVolleyClassAbilities;
        private (IItemProcAbility skill, int rank)[] itemProcClassAbilities;
        private (ISpellHitAbility skill, int rank)[] spellHitClassAbilities;
        private (ICreatureDeathAbility skill, int rank)[] creatureDeathClassAbilities;

        /// <summary>
        /// Filters a hook's registry bucket down to the handlers this player should actually be dispatched,
        /// PRESERVING THE BUCKET'S ORDER (which for the pre-write hook is MitigationOrder, not registration
        /// order, and is live combat arithmetic).
        ///
        /// The rule itself is <see cref="ClassAbilityRegistry.IsHookCacheEligible"/>, not inline here, so it
        /// is testable without a live Player: rank > 0, OR the handler declares that it runs without a
        /// learned rank. That second clause exists because a rank filter strands a pool that was granted
        /// while the ability WAS held - see IPreWriteDamageAbility.RunsWithoutLearnedRank. A rank-independent
        /// handler is cached with rank 0 and must tolerate that.
        /// </summary>
        private (T skill, int rank)[] BuildClassAbilityHookCache<T>(IReadOnlyList<T> handlers) where T : IClassAbility
        {
            // The PK facet runs NO class ability handler at all while the facet PK rule is active - not even
            // the rank-independent ones (RunsWithoutLearnedRank), which would otherwise be cached at rank 0
            // here and keep draining a ward on a facet where class abilities do not work. The result is
            // cached, so it is rebuilt whenever the answer can change: every switch invalidates these caches
            // (ApplyFacetAbilities, and ApplyFacetPkTransition after the slot is set), and FacetPkHeartbeat
            // invalidates them within one heartbeat of the rule itself being switched on or off. The PvP arena
            // mask joins through the same combined predicate; EnterPvpMatch and ExitPvpMatch invalidate these
            // caches themselves, and the heartbeat's refresh tracks the combined answer as a backstop.
            if (ClassAbilitySuppressed)
                return Array.Empty<(T, int)>();

            var learned = new List<(T, int)>();

            foreach (var handler in handlers)
            {
                var rank = GetClassAbilityRank(handler.Definition.Id);
                if (ClassAbilityRegistry.IsHookCacheEligible(handler, rank))
                    learned.Add((handler, rank));
            }
            return learned.ToArray();
        }

        private void InvalidateClassAbilityHookCaches()
        {
            outgoingDamageClassAbilities = null;
            incomingDamageClassAbilities = null;
            preWriteDamageClassAbilities = null;
            missileVolleyClassAbilities = null;
            itemProcClassAbilities = null;
            spellHitClassAbilities = null;
            creatureDeathClassAbilities = null;
        }

        /// <summary>
        /// IOutgoingDamageAbility dispatch - called from DamageTarget after damage calculation,
        /// before the hit is applied/reported. No-op on a miss or against players (PvP excluded,
        /// consistent across all class abilities).
        /// </summary>
        public void ApplyOutgoingDamageClassAbilities(Creature target, DamageEvent damageEvent)
        {
            var learned = outgoingDamageClassAbilities ??= BuildClassAbilityHookCache(ClassAbilityRegistry.OutgoingDamageAbilities);

            if (learned.Length == 0 || !damageEvent.HasDamage || target is Player)
                return;

            if (!PropertyManager.GetBool("class_abilities_enabled").Item)
                return;

            foreach (var (skill, rank) in learned)
                skill.ModifyOutgoingDamage(this, rank, target, damageEvent);
        }

        /// <summary>
        /// IIncomingDamageAbility dispatch - called from TakeDamage after health is updated, before the
        /// death check (so effects still fire on a killing blow). Fires on any landed hit (enemy contact),
        /// INCLUDING one fully mitigated to 0 damage - the hit still connected, so contact-reactive skills
        /// like Thorns should fire (user ruling 2026-07-16). Only misses/evades (which never reach
        /// TakeDamage) are excluded. Monster attackers only: PvP, self-damage, and dead attackers are
        /// filtered here. A handler that only cares about damage magnitude can check damageTaken itself.
        ///
        /// THIS HOOK IS PHYSICAL-ONLY. Player.TakeDamage is the melee/missile/hotspot entry point; magic
        /// damage (spell projectiles, Harm, Drain Health) and DoT ticks write Health straight to the vital
        /// and never arrive here, so anything registered as an IIncomingDamageAbility fires on physical
        /// damage and nothing else unless it is separately wired at those sites. Two handlers ride it:
        /// Vengeance, which deliberately stays physical-only on its tracking side, and Thorns, which is NOT
        /// physical-only any more (user ruling 2026-10-07) - it is separately wired by name at the three
        /// direct magic sites through Player.ApplyThornsOnMagicHit. Never call this whole dispatch from a
        /// magic site to "give Thorns magic": that would silently widen Vengeance with it. Any new
        /// incoming-damage ability inherits the physical-only default; decide which one it wants and say so
        /// in its description.
        ///
        /// MANA BARRIER NO LONGER RIDES THIS HOOK AT ALL, as of 2026-09-08. It is now a pre-write reduction
        /// called by name at five sites (Player.AbsorbWithManaBarrier, and AbsorbWithManaBarrierDot for the
        /// DoT tick that carries no attacker), exactly like Sanguine Ward below. It had to leave: this
        /// dispatch runs AFTER the health write, and the write clamps at zero, so on an overkill hit the
        /// barrier was handed the victim's remaining health rather than the damage thrown - it then refunded
        /// a share of that and averted a death it had not paid for. A hook that fires after the write cannot
        /// carry a mitigation that must apply before it. Do not re-register it here.
        ///
        /// The same trap catches defences that ride no hook at all. Sanguine Ward is read directly off the
        /// player rather than dispatched, and it was called from Player.TakeDamage only, so it had the
        /// identical physical-only gap and needed the identical five-site wiring (see
        /// Player.AbsorbWithSanguineWard). "Does it fire on magic?" is a question to ask of anything that
        /// reacts to a player taking damage, hooked or not.
        /// </summary>
        public void ApplyIncomingDamageClassAbilities(WorldObject source, DamageType damageType, uint damageTaken)
        {
            var learned = incomingDamageClassAbilities ??= BuildClassAbilityHookCache(ClassAbilityRegistry.IncomingDamageAbilities);

            if (learned.Length == 0)
                return;

            if (source is not Creature attacker || PvpClassifier.IsPvp(attacker, this) || attacker == this || attacker.IsDead)
                return;

            if (!PropertyManager.GetBool("class_abilities_enabled").Item)
                return;

            foreach (var (skill, rank) in learned)
                skill.OnDamageTaken(this, rank, attacker, damageType, damageTaken);
        }

        /// <summary>
        /// IPreWriteDamageAbility dispatch - the mitigation half of the pair, and the one that runs BEFORE
        /// the health write. Returns what is left of <paramref name="incomingDamage"/> after every learned
        /// mitigation; the caller assigns that back over its own damage figure and writes THAT to the vital.
        /// Called at all five sites through which a player can lose health (Player.TakeDamage,
        /// SpellProjectile.DamageTarget, WorldObject_Magic.HandleCastSpell_Boost and HandleCastSpell_Transfer,
        /// EnchantmentManager.ApplyDamageTick), because there is no single choke point: magic damage writes
        /// Health straight to the vital and never passes through TakeDamage.
        ///
        /// READ ApplyIncomingDamageClassAbilities ABOVE FOR WHY BOTH EXIST. That one fires after the write,
        /// so it can only react - it takes damage by value, returns void, and the write it follows has
        /// already clamped at zero. This one fires before the write and can reduce the damage, zero it, or
        /// leave a lethal blow survivable. A new incoming-damage ability picks its hook on that question
        /// alone.
        ///
        /// THE SHARED PRECONDITIONS ARE COMPUTED HERE BUT APPLIED PER HANDLER, which is a deliberate
        /// divergence from the hook file's "shared preconditions live in the Player dispatch" rule. The two
        /// abilities on this hook demonstrably disagree about the attacker filter and always have: Mana
        /// Barrier reproduces the old post-write hook's gates (monsters only, never PvP, self-damage or a
        /// dead attacker), while Sanguine Ward is called unconditionally and deliberately DOES absorb PvP
        /// and self-damage, so that it is never weaker on one damage path than on another. Early-returning
        /// on the filter here would silently nerf the ward; dropping the filter would silently widen the
        /// barrier. So it is evaluated exactly once, into PreWriteDamageContext.Attacker, and each handler
        /// reads it or ignores it. Only the class_abilities_enabled kill switch is a hard gate, matching
        /// every other dispatch in this file.
        ///
        /// A ZERO HIT NO LONGER SKIPS THIS DISPATCH. It used to early-out entirely, which silently starved
        /// Kinetic Charge and Adrenaline - both Observe-band handlers whose whole promise is "every attack
        /// that reaches you" - of a hit fully soaked by armor. The mitigations still treat 0 damage as
        /// nothing to absorb, so nothing changes for them: DamageMitigationOrder.ShouldDispatchPreWriteHandler
        /// dispatches ONLY the Observe band when incomingDamage is 0 and every other band exactly as before
        /// when it is not, so a finite ward is still never consumed by a hit that was fully mitigated
        /// upstream.
        ///
        /// THE learned.Length EARLY-OUT NO LONGER FIRES FOR THIS HOOK, because Sanguine Ward declares
        /// RunsWithoutLearnedRank and is therefore in every player's cache at rank 0 (its ward outlives the
        /// rank that granted it, so a rank filter would strand a live pool). Every damaging hit on a player
        /// therefore allocates one small context. That is the deliberate price of the correctness fix, and
        /// it is bounded: master already called both absorb methods unconditionally at all five sites, and
        /// the ward's own first line returns on an empty pool.
        /// </summary>
        /// <param name="isDamageOverTime">
        /// TRUE only from the accumulated DoT tick, which carries no source whatsoever. Deliberately
        /// distinct from a null attacker, which also covers PvP and self-damage - a handler that skips
        /// unattributed damage and a handler that skips PvP want different answers.
        /// </param>
        public uint ApplyPreWriteDamageClassAbilities(WorldObject source, DamageType damageType, uint incomingDamage, bool isDamageOverTime = false)
        {
            var learned = preWriteDamageClassAbilities ??= BuildClassAbilityHookCache(ClassAbilityRegistry.PreWriteDamageAbilities);

            if (learned.Length == 0)
                return incomingDamage;

            if (!PropertyManager.GetBool("class_abilities_enabled").Item)
                return incomingDamage;

            // the same attacker filter ApplyIncomingDamageClassAbilities applies, evaluated once and carried
            // on the context rather than early-returning on it - see the summary for why
            var attacker = source is Creature creature && !PvpClassifier.IsPvp(creature, this) && creature != this && !creature.IsDead ? creature : null;

            var context = new PreWriteDamageContext
            {
                Source = source,
                Attacker = attacker,
                DamageType = damageType,
                IsDamageOverTime = isDamageOverTime,
                Damage = incomingDamage,
            };

            foreach (var (skill, rank) in learned)
            {
                if (DamageMitigationOrder.ShouldDispatchPreWriteHandler(skill.MitigationOrder, incomingDamage))
                    skill.ModifyIncomingDamage(this, rank, context);
            }

            return context.Damage;
        }

        /// <summary>
        /// IMissileVolleyAbility dispatch - called from the missile launch path. Appends one damage
        /// multiplier per skill-granted extra shot; the caller owns targeting and projectiles.
        /// </summary>
        public void AddClassAbilityMissileShots(List<float> shotDamageMultipliers)
        {
            var learned = missileVolleyClassAbilities ??= BuildClassAbilityHookCache(ClassAbilityRegistry.MissileVolleyAbilities);

            if (learned.Length == 0 || !PropertyManager.GetBool("class_abilities_enabled").Item)
                return;

            foreach (var (skill, rank) in learned)
                skill.AddExtraShots(this, rank, shotDamageMultipliers);
        }

        /// <summary>
        /// Rolls the Double Volley class ability: TRUE when the player has it learned and this volley wins
        /// its per-rank re-fire chance (6/12/18%). Bespoke - the second volley is fired by the caller
        /// (Player.LaunchMissile) rather than by a hook. FALSE when unlearned or the system is disabled.
        /// </summary>
        public bool TryClassAbilityDoubleVolley()
        {
            if (!TryGetClassAbility(ClassAbilityId.DoubleVolley, out var rank))
                return false;

            // Double Volley equipment mod (MACHINERY): +pp on the ability's own re-fire roll, unreachable
            // without the ability because TryGetClassAbility already returned above. Run is MULTIPLICATIVE
            // on the ability's own rank bonus (2026-09-29), UNCAPPED - the 0.0 affinity-cap argument is
            // DoubleVolleyAbility.Chance's own convention for "no ceiling".
            var affinityMultiplier = GetClassAbilityAffinityMultiplier(Skill.Run);

            var chance = DoubleVolleyAbility.Chance(rank,
                PropertyManager.GetDouble("class_ability_doublevolley_chance_base").Item,
                PropertyManager.GetDouble("class_ability_doublevolley_chance_step").Item,
                affinityMultiplier,
                0.0,
                GetEquippedModValue(EquipmentModId.DoubleVolley));

            return ThreadSafeRandom.Next(0.0f, 1.0f) <= chance;
        }

        /// <summary>
        /// IItemProcAbility dispatch - called from WorldObject.TryProcItem when an equipped item's
        /// proc roll succeeds, regardless of whether the proc spell itself lands.
        /// </summary>
        public void OnClassAbilityItemProc(WorldObject procSource)
        {
            var learned = itemProcClassAbilities ??= BuildClassAbilityHookCache(ClassAbilityRegistry.ItemProcAbilities);

            if (learned.Length == 0 || !PropertyManager.GetBool("class_abilities_enabled").Item)
                return;

            foreach (var (skill, rank) in learned)
                skill.OnItemProc(this, rank, procSource);
        }

        /// <summary>
        /// ISpellHitAbility dispatch - called from SpellProjectile.OnCollideObject when one of this
        /// player's offensive damaging spell projectiles successfully damages a monster, in ANY school
        /// (the call site's War Magic test came off on 2026-09-13). Monster targets only (PvP excluded
        /// here). The core site no longer gates on school; individual handlers own their own filters,
        /// including skipping class-ability-spawned child projectiles so effects can't cascade.
        /// </summary>
        public void ApplySpellHitClassAbilities(Creature target, SpellProjectile projectile)
        {
            var learned = spellHitClassAbilities ??= BuildClassAbilityHookCache(ClassAbilityRegistry.SpellHitAbilities);

            // NB: no IsDead guard - the hit that triggers this frequently kills the struck target
            // (a nuke against weaker monsters), and the blast still legitimately radiates from where
            // that target stood to its surviving neighbors. The dying target's Location/PhysicsObj are
            // still valid here (removal is deferred), and SpawnClassAbilityAoeChild spawns from a copy of it.
            if (learned.Length == 0 || target == null || target is Player)
                return;

            if (!PropertyManager.GetBool("class_abilities_enabled").Item)
                return;

            foreach (var (skill, rank) in learned)
                skill.OnSpellHit(this, rank, target, projectile);
        }

        /// <summary>
        /// ICreatureDeathAbility dispatch - called from Creature.OnDeath when this player landed the last
        /// blow. Monster victims only: players (PvP), the player's own pets, and self-kills are filtered
        /// here, so handlers never have to re-check them. The victim is dead by definition, so there is
        /// deliberately no IsDead guard; its Location/PhysicsObj are still valid (removal is deferred).
        /// </summary>
        public void ApplyCreatureDeathClassAbilities(Creature victim)
        {
            var learned = creatureDeathClassAbilities ??= BuildClassAbilityHookCache(ClassAbilityRegistry.CreatureDeathAbilities);

            if (learned.Length == 0 || victim == null || victim == this)
                return;

            if (victim is Player || victim is Pet)
                return;

            if (!PropertyManager.GetBool("class_abilities_enabled").Item)
                return;

            foreach (var (skill, rank) in learned)
                skill.OnCreatureKilled(this, rank, victim);
        }

        // ---- THE CAP FUNNEL (Class Ability Point audit ledger, round 2) ----------------------------
        //
        // Both point counters are GET-ONLY, and that is the guard, not a style choice. Two prod
        // characters hold fewer AvailableClassAbilityPoints than the ledger identity
        // (total earned - owned rank cost - vendor/voucher sinks) allows, and the losing event is
        // unrecoverable because NOTHING recorded a single CAP mutation anywhere. Making these two
        // properties read-only means every existing write site stopped compiling and had to be routed
        // through AdjustClassAbilityPoints, and every FUTURE write site will stop compiling too.
        //
        // A `private set` would have bought nothing: every write site already lives in a
        // `partial class Player` declaration, so private access reaches all of them. Read-only is the
        // only accessibility that actually forces the funnel.
        //
        // The remove-at-zero behaviour is preserved verbatim in the two raw setters below. It is
        // load-bearing rather than tidy: ABSENT MEANS ZERO for these properties, so writing an
        // explicit 0 row and removing the row are the same state to every reader, and the codebase
        // (and the prod diagnosis) treats a missing 9017 row as zero available points.

        /// <summary>
        /// Spendable class ability points (PropertyInt 9017). Read-only - mutate through
        /// <see cref="AdjustClassAbilityPoints"/> so the change is recorded in `character_cap_ledger`.
        /// </summary>
        public int AvailableClassAbilityPoints => GetProperty(PropertyInt.AvailableClassAbilityPoints) ?? 0;

        /// <summary>
        /// Lifetime class ability points earned (PropertyInt 9018). Read-only - mutate through
        /// <see cref="AdjustClassAbilityPoints"/>. This counter never decreases in normal play and
        /// gates Tier 2/3 access via <see cref="MeetsClassAbilityTierUnlock"/>, so a stray write here
        /// silently loosens those gates - another reason it is not settable from outside.
        /// </summary>
        public int TotalClassAbilityPointsEarned => GetProperty(PropertyInt.TotalClassAbilityPointsEarned) ?? 0;

        /// <summary>
        /// The raw property write for <see cref="AvailableClassAbilityPoints"/>, with the
        /// remove-at-zero behaviour the old setter had. Called ONLY by
        /// <see cref="AdjustClassAbilityPoints"/>.
        /// </summary>
        private void SetAvailableClassAbilityPointsRaw(int value)
        {
            if (value == 0)
                RemoveProperty(PropertyInt.AvailableClassAbilityPoints);
            else
                SetProperty(PropertyInt.AvailableClassAbilityPoints, value);
        }

        /// <summary>
        /// The raw property write for <see cref="TotalClassAbilityPointsEarned"/>, with the
        /// remove-at-zero behaviour the old setter had. Called ONLY by
        /// <see cref="AdjustClassAbilityPoints"/>.
        /// </summary>
        private void SetTotalClassAbilityPointsEarnedRaw(int value)
        {
            if (value == 0)
                RemoveProperty(PropertyInt.TotalClassAbilityPointsEarned);
            else
                SetProperty(PropertyInt.TotalClassAbilityPointsEarned, value);
        }

        /// <summary>
        /// THE ONE PLACE either class-ability-point counter changes. Applies both deltas and appends a
        /// row to `character_cap_ledger` describing the change, so a later shortfall can be traced to
        /// the operation that caused it.
        ///
        /// Both deltas may be zero: <see cref="CapLedgerReason.VoucherApply"/> is a real, recordable
        /// event (a prepaid rank being applied) that legitimately moves no points, and the ledger row
        /// is the only evidence it happened.
        ///
        /// The entire ledger half is wrapped in try/catch. The DAO already logs and swallows its own
        /// failures, so this is belt and braces for ROW CONSTRUCTION - a null Character, a name lookup,
        /// a rank-cache read. Nothing about writing a forensic row may be allowed to escape into the
        /// player-facing operation it describes, which by this point has already been applied.
        /// </summary>
        /// <param name="availableDelta">Signed change to <see cref="AvailableClassAbilityPoints"/>.</param>
        /// <param name="totalDelta">Signed change to <see cref="TotalClassAbilityPointsEarned"/>.</param>
        /// <param name="reason">Which write site this is - the ledger's `reason` column.</param>
        /// <param name="ability">Ability Name for an ability-scoped reason, null otherwise.</param>
        /// <param name="rankAfter">
        /// The ability's rank AFTER the caller's own rank write, when the caller knows it. This is the
        /// column that makes the leading structural hypothesis falsifiable: if a learn's point debit
        /// persists while its rank write does not, the ledger row's rank_After will disagree with the
        /// character's quest registry.
        /// </param>
        /// <param name="detail">Free text naming the incident or source. Truncated to the column width.</param>
        /// <param name="batchId">
        /// Groups the N rows one full respec emits. Null for every non-batched reason.
        /// </param>
        /// <param name="ownedCostDelta">
        /// How much the character's owned-rank cost is about to change by, for the ONE case where the
        /// rank write has to follow the point write: a learn (paid or prepaid) debits the point first,
        /// because the biota save that persists the debit lives at the bottom of
        /// <see cref="ApplyClassAbilityRankCore"/>. Without this the row's `owned_Cost_After` would be
        /// measured before the rank existed and would understate the true figure by exactly the rank
        /// cost, which makes the row read as an unexplained shortfall on its own numbers. Every other
        /// reason leaves it at 0, because the cache is already up to date by the time they call in.
        /// </param>
        private void AdjustClassAbilityPoints(int availableDelta, int totalDelta, CapLedgerReason reason,
            string ability = null, int? rankAfter = null, string detail = null, string batchId = null,
            int ownedCostDelta = 0)
        {
            if (availableDelta != 0)
                SetAvailableClassAbilityPointsRaw(AvailableClassAbilityPoints + availableDelta);

            if (totalDelta != 0)
                SetTotalClassAbilityPointsEarnedRaw(TotalClassAbilityPointsEarned + totalDelta);

            try
            {
                var row = new CharacterCapLedger
                {
                    CharacterId = Guid.Full,
                    CharacterName = Name,
                    Ts = DateTime.UtcNow,
                    Reason = reason.ToCode(),
                    BatchId = batchId,
                    DeltaAvailable = availableDelta,
                    DeltaTotal = totalDelta,
                    AvailableAfter = AvailableClassAbilityPoints,
                    TotalAfter = TotalClassAbilityPointsEarned,
                    OwnedCostAfter = ClassAbilityPointsSpentOnOwnedRanks() + ownedCostDelta,
                    Ability = ability,
                    RankAfter = rankAfter,
                    Detail = detail != null && detail.Length > 255 ? detail.Substring(0, 255) : detail,
                };

                // Fire and forget: the write is queued onto SerializedShardDatabase's worker, so this
                // costs an enqueue and never a MySQL round trip on the calling (world) thread. A null
                // callback is correct - there is nothing a caller could usefully do about a failed
                // forensic row, and the DAO logs it.
                DatabaseManager.Shard.AddCapLedgerRow(row, null);
            }
            catch (Exception ex)
            {
                log.Error($"[CAPLEDGER] {Name} (0x{Guid.Full:X8}): failed to record a '{reason}' CAP mutation (deltaAvailable {availableDelta}, deltaTotal {totalDelta}). The point change itself HAS been applied; only the ledger row is missing.", ex);
            }
        }

        /// <summary>
        /// THE ONLY PUBLIC ROUTE onto <see cref="AdjustClassAbilityPoints"/>, for the CAP audit ledger's
        /// admin remediation command (/capadjust, ClassAbilityCommands.cs) - the durable tool for a
        /// future CAP-shortfall incident, and the manual fallback if the one-off correction migration
        /// has not run yet.
        ///
        /// <see cref="GrantClassAbilityPoints"/> is NOT usable for this: it raises BOTH
        /// AvailableClassAbilityPoints AND TotalClassAbilityPointsEarned, and Total is what
        /// <see cref="MeetsClassAbilityTierUnlock"/> reads (the `TotalClassAbilityPointsEarned &lt;
        /// cspRequired` check) to gate Tier 2/3 class-ability access - crediting a correction through it
        /// would silently hand out tier access along with the point. This method only ever touches
        /// Available, via the same mutator (and the same `character_cap_ledger` row, reason
        /// <see cref="CapLedgerReason.AdminCorrect"/>) every other CAP write site uses, so the correction
        /// stays ledgered without ever writing Total.
        ///
        /// Refuses a zero delta and refuses any delta that would take Available below zero - both
        /// refusals leave every counter untouched and write no ledger row. On success, returns the
        /// resulting Available balance so the caller can report it without a second property read.
        /// </summary>
        public bool TryAdminAdjustClassAbilityPoints(int availableDelta, string detail, out int availableAfter)
        {
            availableAfter = AvailableClassAbilityPoints;

            if (availableDelta == 0)
                return false;

            if (AvailableClassAbilityPoints + availableDelta < 0)
                return false;

            AdjustClassAbilityPoints(availableDelta, 0, CapLedgerReason.AdminCorrect, detail: detail);

            availableAfter = AvailableClassAbilityPoints;
            return true;
        }

        /// <summary>
        /// The cumulative class ability point cost of every rank this character currently owns, across
        /// every class. This is <see cref="PointsSpentInClass"/> without the class filter and without
        /// its AbilityClass.None early-out, which is what makes it the `owned_Cost` term of the ledger
        /// identity rather than a tier-unlock input.
        ///
        /// IT CANNOT SEE AN UNRESOLVABLE-AND-UNRETIRED `ClassAbility_*` QUEST ROW. The rank cache is
        /// built by <see cref="GetClassAbilityCache"/>, which silently skips any row
        /// <see cref="ClassAbilityRegistry.TryGetByQuestKey"/> cannot resolve, so points spent on such
        /// a row are invisible here and surface as `unexplained` instead. That is deliberate and the
        /// cache must NOT be changed to paper over it: round 3 counts those rows separately as
        /// `orphan_Rows` on `character_cap_audit`, which distinguishes "spent on a retired ability we
        /// can still see" from a genuine unexplained shortfall.
        /// </summary>
        public int ClassAbilityPointsSpentOnOwnedRanks()
        {
            var spent = 0;

            foreach (var (id, rank) in GetClassAbilityCache())
            {
                if (rank <= 0 || !ClassAbilityRegistry.Abilities.TryGetValue(id, out var def))
                    continue;

                spent += def.CumulativeCost(rank);
            }

            return spent;
        }

        public int ClassAbilityPointsPurchasedWithLum
        {
            get => GetProperty(PropertyInt.ClassAbilityPointsPurchasedWithLum) ?? 0;
            set { if (value == 0) RemoveProperty(PropertyInt.ClassAbilityPointsPurchasedWithLum); else SetProperty(PropertyInt.ClassAbilityPointsPurchasedWithLum, value); }
        }

        /// <summary>
        /// How many class ability points this character has bought with EXPERIENCE. This is the xp curve's
        /// POSITION, not a balance - it only ever grows, and resetting it would hand the character the base
        /// price again. Deliberately separate from <see cref="ClassAbilityPointsPurchasedWithLum"/> so the two
        /// lanes escalate independently (XP-LANE-SPEC sec 3.3).
        /// </summary>
        public int ClassAbilityPointsPurchasedWithXp
        {
            get => GetProperty(PropertyInt.ClassAbilityPointsPurchasedWithXp) ?? 0;
            set { if (value == 0) RemoveProperty(PropertyInt.ClassAbilityPointsPurchasedWithXp); else SetProperty(PropertyInt.ClassAbilityPointsPurchasedWithXp, value); }
        }

        /// <summary>
        /// Grants class ability points from any source (level milestone, quest reward, Luminance purchase,
        /// admin grant), in one place. There is NO lifetime cap (the no-caps redesign, DESIGN.md sec 1:
        /// power is limited by the Luminance price curve, not a wall), so this always succeeds for a
        /// positive amount. Returns FALSE only for a non-positive amount.
        ///
        /// <paramref name="reason"/> is threaded in because this ONE method serves several distinct
        /// ledger reasons (a level milestone, an enlightenment milestone, a Luminance purchase, an XP
        /// purchase, an admin grant), and the ledger has to tell them apart. Every caller names its
        /// own; there is deliberately no default, so a new grant avenue cannot be added without
        /// deciding which reason it is. <paramref name="sourceText"/> is carried into the ledger row's
        /// `detail` as well as into the player's chat line, so a coarse reason code is still readable
        /// at a MySQL prompt (e.g. reason 'grant_admin', detail 'a stage test grant').
        /// </summary>
        public bool GrantClassAbilityPoints(int amount, string sourceText, CapLedgerReason reason)
        {
            if (amount <= 0)
                return false;

            // Mule (WaffleACE). Guarded here rather than at each caller because this method's own summary
            // names it the choke point for every CAP-gain avenue. Silent (notify: false) because the
            // milestone grant retries on EVERY login - a chat refusal would fire each time. Returning false
            // also leaves MilestoneClassAbilityPointsGranted un-advanced and suppresses the
            // first-class-ability-point notice, which is what a level 180 mule would otherwise be shown.
            if (MuleBlocked(MuleAction.GainClassAbilityPoints, notify: false))
                return false;

            // PvP template (progression lock): a templated player earns no class ability points. Silent for the same
            // reason as the mule guard - the grant is retried from several automatic paths.
            if (PvpTemplateBlocked(PvpTemplateAction.ClassAbilityPoints) != null)
                return false;

            AdjustClassAbilityPoints(amount, amount, reason, detail: sourceText);

            Session.Network.EnqueueSend(new GameMessageSystemChat(
                $"You gain {amount:N0} class ability point{(amount == 1 ? "" : "s")} from {sourceText}. Available: {AvailableClassAbilityPoints:N0}. Use /abilities to spend them.", ChatMessageType.Advancement));

            // Item level-up burst (deliberately distinct from PlayScript.LevelUp, the PLAYER level-up).
            // This is the choke point for every CAP-gain avenue, so putting it here means all of them get
            // the burst for free. Known soft edge: PlayParticleEffect -> EnqueueBroadcast returns early
            // when CurrentLandblock is null, so the two login-time retroactive catch-up grants (called
            // from Player_Networking.cs inside PlayerEnterWorld) may not show the burst - the chat line
            // still arrives regardless.
            PlayParticleEffect(PlayScript.AetheriaLevelUp, Guid);

            SaveBiotaToDatabase();

            return true;
        }

        /// <summary>
        /// How many level-milestone class ability points (DESIGN.md sec 2a) have already been paid to this
        /// character. Compared against <see cref="ClassAbilityMilestones.EntitledCount"/> to compute the
        /// idempotent catch-up in <see cref="GrantMilestoneClassAbilityPoints"/>.
        /// </summary>
        public int MilestoneClassAbilityPointsGranted
        {
            get => GetProperty(PropertyInt.MilestoneClassAbilityPointsGranted) ?? 0;
            set { if (value == 0) RemoveProperty(PropertyInt.MilestoneClassAbilityPointsGranted); else SetProperty(PropertyInt.MilestoneClassAbilityPointsGranted, value); }
        }

        /// <summary>
        /// Grants the level-milestone class ability points this character has earned but not yet been paid
        /// (DESIGN.md sec 2a: one point at each of levels 20, 40, 60, 80, 100, 125, 150, 175, 200, 225,
        /// 250, 275). Entitlement is derived from Level alone and the paid-out count is persisted, so this
        /// is safe to call on every level-up and on login: it is idempotent, retroactive for existing
        /// characters, and immune to missed level-ups (a multi-level jump pays every milestone crossed).
        /// Points funnel through <see cref="GrantClassAbilityPoints"/> (which has no cap), so the whole owed
        /// difference is paid in one grant and the persisted counter advances to the new entitlement.
        ///
        /// ENLIGHTENMENT SAFETY: the paid-out counter (<see cref="MilestoneClassAbilityPointsGranted"/>) is a
        /// LIFETIME total that must never be reset. ACE's enlightenment (<see cref="Entity.Enlightenment"/>)
        /// sets Level back to 1 but does NOT touch this counter (nor the earned points), so a re-leveling
        /// enlightened character computes owed = entitled - counter &lt;= 0 and is never paid a milestone
        /// twice. Any future enlightenment rework must preserve this counter for that guarantee to hold.
        /// </summary>
        public void GrantMilestoneClassAbilityPoints()
        {
            if (!PropertyManager.GetBool("class_abilities_enabled").Item)
                return;

            var entitled = ClassAbilityMilestones.EntitledCount(Level ?? 1);
            var alreadyGranted = MilestoneClassAbilityPointsGranted;
            var owed = entitled - alreadyGranted;

            if (owed <= 0)
                return;

            var source = owed == 1
                ? $"reaching level {ClassAbilityMilestones.Levels[entitled - 1]}"
                : "reaching level milestones";

            if (GrantClassAbilityPoints(owed, source, CapLedgerReason.GrantMilestone))
            {
                MilestoneClassAbilityPointsGranted = entitled;
                SaveBiotaToDatabase();

                // The FIRST class ability point a character ever earns (0 -> first): congratulate them and
                // point them at the Drift Network trainers. Because the counter is lifetime and survives
                // enlightenment, this fires exactly once per character, not once per enlightenment cycle.
                if (alreadyGranted == 0)
                    SendFirstClassAbilityPointNotice();
            }
        }

        /// <summary>
        /// How many enlightenment-milestone class ability points (DESIGN.md sec 2c) have already been paid to
        /// this character. Compared against <see cref="EnlightenmentCapMilestones.EntitledCount"/> to compute
        /// the idempotent catch-up in <see cref="GrantEnlightenmentClassAbilityPoints"/>.
        /// </summary>
        public int EnlightenmentClassAbilityPointsGranted
        {
            get => GetProperty(PropertyInt.EnlightenmentClassAbilityPointsGranted) ?? 0;
            set { if (value == 0) RemoveProperty(PropertyInt.EnlightenmentClassAbilityPointsGranted); else SetProperty(PropertyInt.EnlightenmentClassAbilityPointsGranted, value); }
        }

        /// <summary>
        /// Grants the enlightenment-milestone class ability points this character has earned but not yet been
        /// paid (DESIGN.md sec 2c: one point at ENL 1, 2, 3, 4, 5, 7, 10, 15, 20, 25, 30, 35, 40, 45, 50, then
        /// every 10 beyond 50). Entitlement is derived from the Enlightenment count alone and the paid-out
        /// count is persisted, so this is safe to call on every enlightenment and on login: it is idempotent,
        /// retroactive for characters enlightened before this lane existed, and immune to missed grants (it
        /// pays the whole owed difference at once). Points funnel through <see cref="GrantClassAbilityPoints"/>
        /// (which has no cap), gated behind <c>class_abilities_enabled</c>.
        ///
        /// ENLIGHTENMENT SAFETY: the paid-out counter (<see cref="EnlightenmentClassAbilityPointsGranted"/>) is
        /// a LIFETIME total that must never be reset. Enlightenment (<see cref="Entity.Enlightenment"/>) raises
        /// the Enlightenment count and resets Level to 1 but does NOT touch this counter, so a character is
        /// never paid the same enlightenment milestone twice. Any future enlightenment rework must preserve
        /// this counter for that guarantee to hold.
        /// </summary>
        public void GrantEnlightenmentClassAbilityPoints()
        {
            if (!PropertyManager.GetBool("class_abilities_enabled").Item)
                return;

            var entitled = EnlightenmentCapMilestones.EntitledCount(Enlightenment);
            var alreadyGranted = EnlightenmentClassAbilityPointsGranted;
            var owed = entitled - alreadyGranted;

            if (owed <= 0)
                return;

            if (GrantClassAbilityPoints(owed, "your enlightenment", CapLedgerReason.GrantEnlightenment))
            {
                EnlightenmentClassAbilityPointsGranted = entitled;
                SaveBiotaToDatabase();
            }
        }

        /// <summary>
        /// The one-time congratulation shown when a character earns its very first class ability point (the
        /// level 20 milestone on a first playthrough). Delivers a modal popup (the same client dialog as the
        /// training-hall "Welcome to Asheron's Call" tutorial, via <see cref="GameEventPopupString"/>) so the
        /// milestone stands out, and mirrors it to the chat log so the direction persists after the popup is
        /// dismissed.
        /// </summary>
        private void SendFirstClassAbilityPointNotice()
        {
            const string popup =
                "You have earned your first Class Ability Point!\n\n" +
                "This is a milestone in your growing power. Class Ability Points let you learn powerful new " +
                "abilities suited to your fighting style.\n\n" +
                "Spend it with the trainers in the Drift Network. To get there, use the portal in the " +
                "Marketplace, or type /dn from anywhere to recall directly.";

            Session.Network.EnqueueSend(new GameEventPopupString(Session, popup));

            Session.Network.EnqueueSend(new GameMessageSystemChat(
                "You have earned your first class ability point, a milestone in your growing power!", ChatMessageType.Advancement));
            Session.Network.EnqueueSend(new GameMessageSystemChat(
                "Spend it with the trainers in the Drift Network: take the portal in the Marketplace, or type /dn to recall there from anywhere.", ChatMessageType.Advancement));
        }

        /// <summary>
        /// The Luminance cost to buy <paramref name="count"/> class ability points when
        /// <paramref name="alreadyPurchased"/> have already been bought with Luminance, per the piecewise
        /// curve (DESIGN.md sec 2b). Reads the live tunables; the math lives in <see cref="ClassAbilityLumCurve"/>.
        /// </summary>
        public static long LumCostForClassAbilityPoints(int alreadyPurchased, int count)
        {
            return ClassAbilityLumCurve.CostForRange(
                alreadyPurchased, count,
                PropertyManager.GetLong("class_ability_lum_base_cost").Item,
                PropertyManager.GetDouble("class_ability_lum_ratio_1").Item,
                PropertyManager.GetDouble("class_ability_lum_ratio_2").Item,
                PropertyManager.GetDouble("class_ability_lum_ratio_3").Item,
                (int)PropertyManager.GetLong("class_ability_lum_breakpoint_1").Item,
                (int)PropertyManager.GetLong("class_ability_lum_breakpoint_2").Item);
        }

        /// <summary>
        /// Buys class ability points with Luminance (/abilities buy) at the piecewise curve price (DESIGN.md
        /// sec 2b). There is NO cap on how many points can be bought - each successive point simply costs
        /// more. All-or-nothing: the full cost of the requested points is computed and charged (from
        /// available Luminance first, then the uncapped bank, since prices climb past the available-lum
        /// cap quickly) before any point is granted.
        /// </summary>
        public bool TryBuyClassAbilityPoints(int count, out string error)
        {
            error = null;

            if (count < 1)
            {
                error = "You must buy at least 1 point.";
                return false;
            }

            var purchased = ClassAbilityPointsPurchasedWithLum;
            var cost = LumCostForClassAbilityPoints(purchased, count);

            var totalLum = GetSpendableLuminance();
            if (totalLum < cost)
            {
                error = $"Buying {count:N0} point{(count == 1 ? "" : "s")} costs {cost:N0} Luminance; you have {totalLum:N0} ({AvailableLuminance ?? 0:N0} available + {BankedLuminance:N0} banked).";
                return false;
            }

            if (!TrySpendLuminanceIncludingBank(cost))
            {
                error = $"Buying {count:N0} point{(count == 1 ? "" : "s")} costs {cost:N0} Luminance; you have {GetSpendableLuminance():N0}.";
                return false;
            }

            ClassAbilityPointsPurchasedWithLum = purchased + count;

            // no cap - GrantClassAbilityPoints succeeds for a positive amount and saves the biota
            GrantClassAbilityPoints(count, $"spending {cost:N0} Luminance", CapLedgerReason.BuyLum);

            return true;
        }

        /// <summary>
        /// The minimum character level at which experience may be exchanged for class ability points.
        /// Below it the exchange competes directly with skill and attribute training for the same
        /// <see cref="AvailableExperience"/> pool; at and above it skills are effectively maxed and that
        /// pool is otherwise idle, which is the role this lane took over from enlightenment.
        /// </summary>
        public static int ClassAbilityXpMinLevel => (int)PropertyManager.GetLong("class_ability_xp_min_level").Item;

        /// <summary>
        /// The EXPERIENCE cost to buy <paramref name="count"/> class ability points when
        /// <paramref name="alreadyPurchased"/> have already been bought with experience
        /// (Docs/ClassAbilities/XP-LANE-SPEC.md sec 3.1). Reads the live tunables; the math is the SAME
        /// piecewise curve the Luminance lane uses - with all three xp ratios equal it collapses to
        /// base * ratio^(n-1), which is why the breakpoints are inert at their defaults.
        /// </summary>
        public static long XpCostForClassAbilityPoints(int alreadyPurchased, int count)
        {
            return ClassAbilityLumCurve.CostForRange(
                alreadyPurchased, count,
                PropertyManager.GetLong("class_ability_xp_base_cost").Item,
                PropertyManager.GetDouble("class_ability_xp_ratio_1").Item,
                PropertyManager.GetDouble("class_ability_xp_ratio_2").Item,
                PropertyManager.GetDouble("class_ability_xp_ratio_3").Item,
                (int)PropertyManager.GetLong("class_ability_xp_breakpoint_1").Item,
                (int)PropertyManager.GetLong("class_ability_xp_breakpoint_2").Item);
        }

        /// <summary>
        /// Buys class ability points with EXPERIENCE (/abilities buyxp, and the Drift Network exchange
        /// stone). There is no cap on how many can be bought - each successive point simply costs more.
        /// All-or-nothing: the full price is computed and charged before any point is granted.
        ///
        /// Spends <see cref="AvailableExperience"/> ONLY, via <see cref="SpendXP"/>. TotalExperience is
        /// deliberately untouched, so a purchase never costs a level and never desyncs the level the
        /// character has already earned - see XP-LANE-SPEC sec 3.4 for why deducting TotalExperience
        /// would not downlevel but would corrupt GetRemainingXP, fellowship splits and vitae thresholds.
        /// </summary>
        public bool TryBuyClassAbilityPointsWithXp(int count, out string error)
        {
            error = null;

            if (count < 1)
            {
                error = "You must buy at least 1 point.";
                return false;
            }

            var minLevel = ClassAbilityXpMinLevel;
            if ((Level ?? 1) < minLevel)
            {
                error = $"You must be level {minLevel:N0} to exchange experience for class ability points. You are level {Level ?? 1:N0}.";
                return false;
            }

            var purchased = ClassAbilityPointsPurchasedWithXp;
            var cost = XpCostForClassAbilityPoints(purchased, count);

            var available = AvailableExperience ?? 0;
            if (available < cost)
            {
                error = $"Buying {count:N0} point{(count == 1 ? "" : "s")} costs {cost:N0} experience; you have {available:N0} unassigned.";
                return false;
            }

            // SpendXP re-checks affordability and deducts AvailableExperience only
            if (!SpendXP(cost))
            {
                error = $"Buying {count:N0} point{(count == 1 ? "" : "s")} costs {cost:N0} experience; you have {AvailableExperience ?? 0:N0} unassigned.";
                return false;
            }

            ClassAbilityPointsPurchasedWithXp = purchased + count;

            // no cap - GrantClassAbilityPoints succeeds for a positive amount and saves the biota
            GrantClassAbilityPoints(count, $"spending {cost:N0} experience", CapLedgerReason.BuyXp);

            return true;
        }

        /// <summary>
        /// DEVELOPER TOOLING ONLY - the /abilities token buy caller is gated to AccessLevel.Developer. Every class
        /// ability token is prepaid (it applies its rank for FREE on use), so this Luminance-only purchase bypasses
        /// the class ability point economy entirely; it exists to spawn a usable token for testing without farming
        /// points. The player-facing purchase is the Drift Network trainer vendor, which charges class ability
        /// points through the vendor currency system.
        ///
        /// All-or-nothing: Luminance and inventory room are validated before any Luminance is spent. Returns
        /// FALSE with a player-facing error and no state change on any failure.
        /// </summary>
        public bool TryBuyClassAbilityToken(ClassAbilityTokenCatalog.Offering offering, out string error)
        {
            error = null;

            if (!PropertyManager.GetBool("class_abilities_enabled").Item)
            {
                error = "Class abilities are not currently enabled on this server.";
                return false;
            }

            // PK facet: this developer path has no mule check to sit behind, so the freeze goes here, before
            // any Luminance is spent or token created.
            if (!FacetPk.CanSpendClassAbilityPoints(IsPkFacetRuleActive))
            {
                error = FacetPk.PkFacetClassAbilityRefusal;
                return false;
            }

            // PvP arena (Docs/Pvp/DESIGN.md H10): spend gates REFUSE in a match rather than mask
            if (IsInPvpMatch)
            {
                error = PvpArenaText.AbilitySpendRefused;
                return false;
            }

            var cost = ClassAbilityTokenCatalog.LumCost(offering.Definition, offering.Tier);

            // Luminance is spent from available (earned) Luminance first, then the uncapped bank - so a token
            // priced above the MaximumLuminance cap is still buyable out of banked Luminance.
            var totalLum = GetSpendableLuminance();
            if (totalLum < cost)
            {
                error = $"That token costs {cost:N0} Luminance; you have {totalLum:N0} ({AvailableLuminance ?? 0:N0} available + {BankedLuminance:N0} banked).";
                return false;
            }

            var token = WorldObjectFactory.CreateNewWorldObject(offering.Wcid);
            if (token == null)
            {
                error = $"The token for {offering.Definition.DisplayName} tier {offering.Tier} (wcid {offering.Wcid}) is not in the world database - its content has not been imported yet.";
                return false;
            }

            if (!TryCreateInInventoryWithNetworking(token))
            {
                token.Destroy();
                error = "You don't have room in your pack for that token.";
                return false;
            }

            // Luminance was checked above and landblock access is single-threaded, so this spend does not fail
            // in practice; the defensive branch pulls the token back out if it somehow does, so no free token.
            if (!TrySpendLuminanceIncludingBank(cost))
            {
                TryConsumeFromInventoryWithNetworking(token, 1);
                error = $"That token costs {cost:N0} Luminance; you have {GetSpendableLuminance():N0}.";
                return false;
            }

            Session.Network.EnqueueSend(new GameMessageSystemChat(
                $"You buy {token.Name} for {cost:N0} Luminance. Use it from your pack to learn {offering.Definition.DisplayName} rank {offering.Tier} - it is already paid for.", ChatMessageType.Broadcast));

            return true;
        }

        /// <summary>
        /// EVERY non-currency prerequisite for gaining the next rank of a class ability, in the exact
        /// order the pre-split LearnClassAbility ran them and with the exact same error strings: the
        /// Mule block, <see cref="ClassAbilityDefinition.Implemented"/>, the Tier 2/3 unlock gate
        /// (first rank only), and the max-rank ceiling. On success <paramref name="rank"/> is the
        /// rank the character holds NOW, so the rank being bought is rank + 1.
        ///
        /// Factored out rather than duplicated so the paid and prepaid paths cannot drift: there is
        /// one copy of the validation set and one copy of each message, and neither caller can
        /// accidentally skip or reorder a check. See <see cref="ApplyClassAbilityRankPrepaid"/> for
        /// the equivalence argument this factoring exists to make checkable.
        /// </summary>
        private bool ValidateClassAbilityRankPrerequisites(ClassAbilityDefinition skill, out int rank, out string error)
        {
            error = null;
            rank = 0;

            // Mule (WaffleACE): class ability ranks are written straight to the quest registry below via
            // Character.GetOrCreateQuest, bypassing QuestManager entirely, so the AdvanceQuest guard in
            // QuestManager does NOT cover this path. Silent refusal - the caller surfaces 'error' itself.
            if (MuleBlocked(MuleAction.TrainClassAbility, notify: false))
            {
                error = "A mule cannot learn class abilities.";
                return false;
            }

            // PvP template (progression lock): no class ability rank is bought or applied while templated, on both
            // rank-gain paths this gate serves.
            var templateRefusal = PvpTemplateBlocked(PvpTemplateAction.ClassAbilityPoints);
            if (templateRefusal != null)
            {
                error = templateRefusal;
                return false;
            }

            // PK facet: no class abilities and no class ability point spending of any kind. Here, the shared
            // gate of BOTH rank-gain paths (paid LearnClassAbility, which runs this before its debit, and the
            // prepaid voucher ApplyClassAbilityRankPrepaid, which runs it before the rank write).
            if (!FacetPk.CanSpendClassAbilityPoints(IsPkFacetRuleActive))
            {
                error = FacetPk.PkFacetClassAbilityRefusal;
                return false;
            }

            // PvP arena (H10): refused in a match, on both rank-gain paths this gate serves
            if (IsInPvpMatch)
            {
                error = PvpArenaText.AbilitySpendRefused;
                return false;
            }

            if (!skill.Implemented)
            {
                error = $"{skill.DisplayName} is coming soon and cannot be learned yet.";
                return false;
            }

            // Tier 2/3 unlock gate (only checked when first learning the skill - subsequent ranks of an
            // already-owned skill can't lock, since owning rank 1 proves the tier was already unlocked)
            if (GetClassAbilityRank(skill.Id) == 0 && !MeetsClassAbilityTierUnlock(skill, out error))
                return false;

            rank = GetClassAbilityRank(skill.Id);

            if (rank >= skill.MaxRank)
            {
                error = $"{skill.DisplayName} is already at max rank ({rank}/{skill.MaxRank}).";
                return false;
            }

            return true;
        }

        /// <summary>
        /// The shared rank-gain path behind BOTH <see cref="LearnClassAbility"/> (paid) and
        /// <see cref="ApplyClassAbilityRankPrepaid"/> (prepaid voucher): the validations, then the
        /// quest-registry rank write, the rank-cache update, the hook-cache invalidation, the live
        /// stat update, and the two saves.
        ///
        /// IT DOES NOT TOUCH EITHER POINT COUNTER. The caller has already applied its own point
        /// mutation through <see cref="AdjustClassAbilityPoints"/> before calling this, which is
        /// deliberate and ordered: the biota save at the bottom of this method is what persists that
        /// mutation, so a debit applied afterwards would not be written by it.
        ///
        /// THE TWO SAVES ARE INDEPENDENT AND THAT IS THE POINT OF THE LEDGER. SaveBiotaToDatabase
        /// persists the point counters; SaveCharacterToDatabase persists the rank. Losing the second
        /// while the first succeeds leaves the point spent with no rank to show for it, which is the
        /// leading structural hypothesis for the two prod shortfalls. This method does not try to fix
        /// that (making the two atomic is a much larger change); it makes it VISIBLE, because the
        /// caller's ledger row carries the rank it intended to write and a later read can compare
        /// that against the character's actual quest registry.
        ///
        /// <paramref name="reason"/> is not used to alter behaviour. It names the calling path in the
        /// error log below, which fires only in the case that matters: a validation refusal reached
        /// AFTER the caller moved points. Both callers validate first, so that is unreachable in
        /// practice - if it is ever logged, the log line is the record of a point spent for nothing.
        /// </summary>
        private bool ApplyClassAbilityRankCore(ClassAbilityDefinition skill, CapLedgerReason reason, out string error)
        {
            if (!ValidateClassAbilityRankPrerequisites(skill, out var rank, out error))
            {
                log.Error($"[CAPLEDGER] {Name} (0x{Guid.Full:X8}): ApplyClassAbilityRankCore refused '{skill.Name}' for reason '{reason}' AFTER the point mutation was applied ({error}). The caller validates first, so this should be unreachable; the character may now hold a spend with no rank.");
                return false;
            }

            var newRank = rank + 1;

            var quest = Character.GetOrCreateQuest(ClassAbilityRegistry.QuestKey(skill), CharacterDatabaseLock, out var created);
            if (created)
                quest.CharacterId = Guid.Full;   // same value QuestManager.IDtoUseForQuestRegistry uses for players
            quest.NumTimesCompleted = newRank;
            quest.LastTimeCompleted = (uint)Time.GetUnixTime();
            CharacterChangesDetected = true;

            GetClassAbilityCache()[skill.Id] = newRank;
            InvalidateClassAbilityHookCaches();
            SendEnhancedStatUpdate(skill);

            SaveBiotaToDatabase();
            SaveCharacterToDatabase();

            return true;
        }

        /// <summary>
        /// Learns the next rank of a class ability, spending available points.
        /// Returns FALSE with a player-facing error and no state change on any validation failure.
        /// </summary>
        public bool LearnClassAbility(ClassAbilityDefinition skill, out string error)
        {
            if (!ValidateClassAbilityRankPrerequisites(skill, out var rank, out error))
                return false;

            var cost = skill.CostPerRank[rank];

            if (AvailableClassAbilityPoints < cost)
            {
                error = $"Rank {rank + 1} of {skill.DisplayName} requires {cost:N0} class ability point{(cost == 1 ? "" : "s")}; you have {AvailableClassAbilityPoints:N0}.";
                return false;
            }

            AdjustClassAbilityPoints(-cost, 0, CapLedgerReason.Learn,
                ability: skill.Name, rankAfter: rank + 1, ownedCostDelta: cost,
                detail: $"learned rank {rank + 1} for {cost}");

            return ApplyClassAbilityRankCore(skill, CapLedgerReason.Learn, out error);
        }

        /// <summary>
        /// Pushes a live stat update to the client when an Enhanced &lt;stat&gt; class ability, or a
        /// multi-skill bundle ability (Training / Advanced Weaponry / Questionable Tactics - anything
        /// implementing IStatBundleAbility) is learned or unlearned, so the attribute/skill panel
        /// reflects the new bonus without a relog. The bonus is getter-only, so the update carries
        /// NetworkStartingValue/NetworkInitLevel.
        /// Vital maximums are rebuilt client-side, so the vital message carries NetworkStartingValue -
        /// the same term the login description packet writes - rather than the raw StartingValue.
        /// A bundle only ever contributes skill bonuses (see GetEnhancedSkillBonus), so its branch sends
        /// one GameMessagePrivateUpdateSkill per skill in BundledSkills and never touches attributes/vitals.
        /// </summary>
        private void SendEnhancedStatUpdate(ClassAbilityDefinition skill)
        {
            if (Session == null)
                return;

            var handler = ClassAbilityRegistry.GetHandler(skill.Id);

            if (handler is EnhancedStatAbility enhanced)
            {
                switch (enhanced.Kind)
                {
                    case EnhancedStatKind.Attribute:
                        Session.Network.EnqueueSend(new GameMessagePrivateUpdateAttribute(this, Attributes[enhanced.TargetAttribute]));
                        break;
                    case EnhancedStatKind.Skill:
                        Session.Network.EnqueueSend(new GameMessagePrivateUpdateSkill(this, GetCreatureSkill(enhanced.TargetSkill)));
                        break;
                    case EnhancedStatKind.Vital:
                        // The client rebuilds max vitals itself from terms the server pushes. The live vital
                        // message carries NetworkStartingValue (the value with this bonus folded in), which is
                        // the same term GameEventPlayerDescription writes at login, so the two agree.
                        Session.Network.EnqueueSend(new GameMessagePrivateUpdateVital(this, Vitals[enhanced.TargetVital]));
                        break;
                }
            }
            else if (handler is IStatBundleAbility bundle)
            {
                var skills = bundle.BundledSkills;
                for (var i = 0; i < skills.Count; i++)
                    Session.Network.EnqueueSend(new GameMessagePrivateUpdateSkill(this, GetCreatureSkill(skills[i])));
            }
        }

        /// <summary>
        /// Sweeps the character's quest registry for orphaned class-ability rows - rows whose name starts
        /// with <see cref="ClassAbilityRegistry.QuestKeyPrefix"/> but which <see cref="ClassAbilityRegistry.TryGetByQuestKey"/>
        /// can no longer resolve, because the ability they name has since been retired (its handler
        /// unregistered, its id kept reserved - see ClassAbilityDefinition.cs). Left alone, such a row is
        /// silently skipped by <see cref="GetClassAbilityCache"/> forever: the character gets no benefit from
        /// the rank they hold, and the class ability points they spent on it are never refunded.
        ///
        /// Looks each orphaned row up in <see cref="RetiredClassAbilities"/> to price the refund, grants the
        /// points, and erases the row (erasing is what makes this idempotent - a second sweep finds nothing
        /// left to refund). Runs regardless of <c>class_abilities_enabled</c>, unlike the milestone grants
        /// next to it, so orphaned rows are still cleaned up if the gate is later switched off.
        ///
        /// A row that is unresolvable AND not in the retired table is left completely alone (not erased) and
        /// logged at warn level - erasing rank data this method cannot price would be worse than leaving it.
        /// </summary>
        public void SweepRetiredClassAbilities()
        {
            var refundedTotal = 0;
            var refundedNames = new List<string>();

            foreach (var quest in Character.GetQuests(CharacterDatabaseLock))
            {
                if (!quest.QuestName.StartsWith(ClassAbilityRegistry.QuestKeyPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Still resolves to a live ability - not orphaned, nothing to do.
                if (ClassAbilityRegistry.TryGetByQuestKey(quest.QuestName, out _))
                    continue;

                var suffix = quest.QuestName.Substring(ClassAbilityRegistry.QuestKeyPrefix.Length);

                if (!RetiredClassAbilities.TryGetRefund(suffix, quest.NumTimesCompleted, out var points, out var displayName))
                {
                    log.WarnFormat(
                        "Player {0} (0x{1}) has an unresolvable class ability quest registry row '{2}' (rank {3}) that is not in the retired-abilities table. Leaving it alone.",
                        Name, Guid, quest.QuestName, quest.NumTimesCompleted);
                    continue;
                }

                if (Character.EraseQuest(quest.QuestName, CharacterDatabaseLock))
                    CharacterChangesDetected = true;

                if (points > 0)
                {
                    // rankAfter 0: the row was just erased above, so the character owns no rank of
                    // this retired ability afterwards. `ability` carries the retired display name
                    // rather than a live registry name, which is the only name that still exists.
                    AdjustClassAbilityPoints(points, 0, CapLedgerReason.SweepRetired,
                        ability: displayName, rankAfter: 0,
                        detail: $"retired row '{quest.QuestName}' rank {quest.NumTimesCompleted} refunded");

                    refundedTotal += points;
                    refundedNames.Add($"{displayName} (rank {quest.NumTimesCompleted}, {points:N0} point{(points == 1 ? "" : "s")})");
                }
            }

            if (refundedNames.Count == 0)
                return;

            // A retired row can affect the rank cache / hook caches exactly like Learn/Unlearn does, so
            // invalidate the same way UnlearnClassAbility does rather than trust the sweep didn't touch them.
            classAbilityCache = null;
            InvalidateClassAbilityHookCaches();

            Session.Network.EnqueueSend(new GameMessageSystemChat(
                $"Some class abilities you had learned have been retired and refunded: {string.Join(", ", refundedNames)}. " +
                $"Total refunded: {refundedTotal:N0} class ability point{(refundedTotal == 1 ? "" : "s")}. Available: {AvailableClassAbilityPoints:N0}.",
                ChatMessageType.Advancement));

            SaveBiotaToDatabase();
            SaveCharacterToDatabase();
        }

        /// <summary>
        /// THE CLASS ABILITY OVERHAUL DEPLOYMENT MIGRATION: a one-shot, per-character forced full
        /// respec, run as a login sweep. Every learned class ability is unlearned, every point is
        /// returned, every stored facet build's ability set is emptied, every unused training token is
        /// consumed, and the player is told at login that their points have been reset.
        ///
        /// THE RULE IS ABSOLUTE AND DELIBERATELY NOT PER-ABILITY: clear every owned rank, then set
        /// AvailableClassAbilityPoints := TotalClassAbilityPointsEarned. There is no historical price
        /// table and no per-ability refund arithmetic. This is NOT routed through
        /// <see cref="UnlearnClassAbility"/> (nor ClassAbilityTrainer.HandleRespec, which is
        /// interactive and charges Luminance) precisely because that path refunds
        /// skill.CumulativeCost(rank) at the CURRENT price: the overhaul makes 26 abilities cheaper,
        /// so refunding at the new price would silently confiscate the difference - a player who paid
        /// 9 for a 3/3/3 ability would get 3 back. Setting Available to the lifetime earned total makes
        /// every owner whole regardless of what they actually paid.
        ///
        /// TotalClassAbilityPointsEarned IS NEVER TOUCHED. It is lifetime, it is what the level-
        /// milestone and enlightenment grants compare against (so resetting it would re-fire those
        /// grants), and it is half of the Tier 2/3 gate.
        ///
        /// TIER ACCESS RESETTING IS INTENDED, NOT A BUG. <see cref="MeetsClassAbilityTierUnlock"/>
        /// reads <see cref="PointsSpentInClass"/>, computed from currently-owned ranks, so T2/T3
        /// re-lock on their own until the player reinvests. Nothing to preserve, nothing to strip.
        ///
        /// It deliberately OVER-CREDITS by the sinks. The alternate-currency vendor and prepaid
        /// training vouchers both debit Available without ever touching Total, so setting Available to
        /// Total hands those spends back. That is accepted. The one consequence that is NOT acceptable
        /// is a bought-but-unapplied voucher surviving as a usable item after its points have been
        /// returned, which is why the sweep consumes them - see
        /// <see cref="ConsumeClassAbilityTokensForOverhaulRespec"/> for the bound over-credit that
        /// still survives.
        ///
        /// CROSS-BRANCH NOTE, recorded because it is invisible from this file and this method must NOT
        /// special-case it: a parallel branch moves Sanguine Ward and Mana Barrier onto a pre-write
        /// damage hook whose handler list is filtered to LEARNED ranks. This sweep clears every rank, so
        /// a character swept while holding a live Sanguine Ward or Mana Barrier pool loses the handler
        /// that would otherwise have drained it. Whatever that branch decides to do about an orphaned
        /// pool belongs there; behaviour here is deliberately unchanged for it.
        ///
        /// Modelled on <see cref="SweepRetiredClassAbilities"/> and ordered after it in
        /// PlayerEnterWorld: per-character, independently guarded, so an abort part-way through a shard
        /// is benign and the next login simply continues. Two independent guards - the per-character
        /// PropertyInt 9067 stamp, and the server-wide
        /// <c>class_ability_overhaul_respec_sweep_enabled</c> kill switch (default FALSE) that stops
        /// the migration live with no redeploy.
        /// </summary>
        public void ApplyClassAbilityOverhaulRespec()
        {
            // Already swept. The stamp is never cleared, so this is the permanent end state.
            if ((GetProperty(PropertyInt.ClassAbilityOverhaulRespecDone) ?? 0) != 0)
                return;

            if (!PropertyManager.GetBool("class_ability_overhaul_respec_sweep_enabled").Item)
                return;

            // THE FACET READ RUNS FIRST, AND A FAILURE REFUSES THE WHOLE SWEEP. TryGetCharacterFacetRows
            // returns false on a timeout, which must NEVER be read as "this character has no facets":
            // clearing the live build while a stored facet keeps its own copy of the ranks is strictly
            // worse than doing nothing, because the first facet switch would then re-apply a build the
            // player no longer paid for while the swap math subtracted its cost. Nothing is cleared and
            // the one-shot guard is NOT stamped, so the next login retries.
            if (!TryGetCharacterFacetRows(out var facetRows))
            {
                log.Warn($"[CARESPEC] {Name} (0x{Guid.Full:X8}): the character_facet read did not complete, so the class ability overhaul respec is SKIPPED this login. Nothing was cleared and the one-shot guard was not stamped - the next login retries.");
                return;
            }

            // THE STORED FACET BUILDS ARE CLEARED FIRST, BEFORE ANY LIVE STATE IS TOUCHED, and their
            // writes are CONFIRMED rather than fire-and-forget. This is ordering, not tidiness: the facet
            // write is the ONLY step of this sweep that can fail, so running it first makes an abandoned
            // run a true no-op - no ranks erased, no points moved, no tokens consumed, and no
            // CharacterChangesDetected set by this method - which is what stops the routine player save
            // from persisting half a sweep.
            //
            // A stored facet build is a SECOND copy of the character's ranks, so clearing the live build
            // while a stored slot keeps its own copy is strictly worse than doing nothing: the first
            // switch would re-apply a build the player no longer paid for while the swap math subtracted
            // its cost. Stamping the one-shot guard over a facet write that never landed would make that
            // state permanent - which is exactly what this ordering, plus the refusal below, prevents.
            //
            // THE IN-WINDOW STATE THIS ORDERING LEAVES IS BENIGN, and that was walked rather than
            // assumed. If a later step aborts, the character has empty stored sets and an intact live
            // build. A facet switch then computes outgoingSpent from the LIVE ranks (Player_Facets.cs's
            // CaptureFacetAbilities, which reads the rank cache) and incomingSpent 0 from the emptied
            // row, so FacetPools.AvailableClassAbilityPointsAfterSwap returns current + outgoingSpent
            // with no shortfall: the player is CREDITED their build's cost, never debited. The next
            // login's retry ends at Available == Total regardless, which makes them whole.
            if (!ClassAbilityOverhaulRespec.TryClearStoredAbilities(
                    facetRows, FacetDbTimeoutMs, DatabaseManager.Shard.SaveCharacterFacet, out var clearedFacets))
            {
                log.Error($"[CARESPEC] {Name} (0x{Guid.Full:X8}): one or more character_facet ability writes did not confirm, so the class ability overhaul respec is ABANDONED this login and the one-shot guard was NOT stamped. NOTHING was mutated - the live ranks, both point counters and any training tokens are all exactly as they were - so the next login simply re-runs the whole sweep.");
                return;
            }

            var clearedAbilities = 0;

            // GetQuests hands back a ToList copy (CharacterExtensions), so erasing inside this loop is
            // safe. Only rows the live registry can still resolve are erased; an unresolvable row
            // belongs to SweepRetiredClassAbilities, which has already run this login and prices those
            // from the retired table. See ClassAbilityOverhaulRespec.IsSweepableQuestRow.
            foreach (var quest in Character.GetQuests(CharacterDatabaseLock))
            {
                if (!ClassAbilityOverhaulRespec.IsSweepableQuestRow(quest.QuestName))
                    continue;

                if (Character.EraseQuest(quest.QuestName, CharacterDatabaseLock))
                {
                    CharacterChangesDetected = true;
                    clearedAbilities++;
                }
            }

            // BEFORE the point write, deliberately, and for the reason UnlearnClassAbility gives for
            // its own ordering: AdjustClassAbilityPoints snapshots ClassAbilityPointsSpentOnOwnedRanks()
            // into the ledger row's owned_Cost_After, and that term is only honest once the erased rows
            // are out of the cache. Nulling it forces a rebuild from the (now erased) quest registry.
            classAbilityCache = null;

            var restored = TotalClassAbilityPointsEarned - AvailableClassAbilityPoints;

            if (restored != 0)
            {
                if (restored < 0)
                {
                    // Available exceeded the lifetime total, which no ordinary play path produces - it
                    // needs an admin-only credit (TryAdminAdjustClassAbilityPoints moves Available
                    // without Total). The migration rule is absolute, so the counter is still set to
                    // Total, which REMOVES that excess; logged rather than silently applied so an
                    // affected character is identifiable afterwards.
                    log.Warn($"[CARESPEC] {Name} (0x{Guid.Full:X8}): available class ability points ({AvailableClassAbilityPoints}) exceeded the lifetime earned total ({TotalClassAbilityPointsEarned}) before the overhaul respec; setting available to the earned total removes {-restored} point(s). Reachable only through an admin credit that moved available without total.");
                }

                AdjustClassAbilityPoints(restored, 0, CapLedgerReason.OverhaulRespec,
                    detail: $"class ability overhaul: cleared {clearedAbilities} learned ability row(s), available set to the lifetime earned total ({TotalClassAbilityPointsEarned})");
            }

            var consumedTokens = ConsumeClassAbilityTokensForOverhaulRespec();

            // Stamped only once every mutation above has been applied. The retry story is simple now that
            // the only step which can fail runs FIRST: nothing here mutates the character until the facet
            // writes have confirmed, so an abandoned run leaves them exactly as it found them and the next
            // login re-runs the whole sweep from the top. Past that point every remaining step is also
            // absolute rather than additive, so even a repeat is harmless - erasing an already-erased row
            // is a no-op, a second Available := Total is a zero delta that writes no ledger row, and a
            // retry finds no tokens left to consume because their points were already returned by
            // Available := Total.
            SetProperty(PropertyInt.ClassAbilityOverhaulRespecDone, 1);

            // Only a character the sweep actually changed is told about it. A character that owned
            // nothing, was already holding all of its points and carried no tokens has nothing to
            // explain, and a modal popup at every such login would be noise.
            if (clearedAbilities > 0 || restored != 0 || clearedFacets > 0 || consumedTokens.Count > 0)
                SendClassAbilityOverhaulRespecNotice(consumedTokens);

            // Same closing shape as SweepRetiredClassAbilities: the rank cache and the per-hook caches
            // are both invalidated, then the biota (the point counters and the stamp) and the character
            // (the erased quest rows) are persisted through their two independent save paths.
            classAbilityCache = null;
            InvalidateClassAbilityHookCaches();

            SaveBiotaToDatabase();
            SaveCharacterToDatabase();

            log.Info($"[CARESPEC] {Name} (0x{Guid.Full:X8}): class ability overhaul respec applied - cleared {clearedAbilities} ability row(s), {clearedFacets} stored facet build(s), consumed {consumedTokens.Count} training token(s); available {AvailableClassAbilityPoints} of {TotalClassAbilityPointsEarned} lifetime earned.");
        }

        /// <summary>
        /// Consumes every unapplied class ability training token this character is carrying, and returns
        /// their item names for the login notice.
        ///
        /// WHY THEY ARE DESTROYED RATHER THAN REFUNDED: a token is prepaid, so its points were debited
        /// from Available at purchase and never touched Total. Setting Available to Total has therefore
        /// ALREADY returned them. Leaving the token in the pack would hand the player both the points
        /// and a usable token. The token is spent, so it goes.
        ///
        /// Walks the full owned container tree (main pack, side packs and equipped) via
        /// GetAllPossessions, the same walk TryFindClassAbilityVoucher uses, and identifies a token the
        /// same way every other path does - by PropertyInt.ClassAbilityTokenId, never by wcid, so a
        /// retired ability's token is caught too. Consumption reuses
        /// TryConsumeFromInventoryWithNetworking, the identical call RefundUnusedVoucher makes, rather
        /// than a raw Destroy: it removes the item, tells the client, and updates encumbrance.
        ///
        /// THE OVER-CREDIT THAT SURVIVES, stated plainly because it is a known and accepted gap: a token
        /// stored anywhere this walk cannot see - a house chest, a storage container the character does
        /// not carry, or another character on the account - is NOT consumed, while the points it was
        /// bought with are still returned on the character being swept. That is bounded (one token per
        /// ability at a time, by the one-voucher-per-skill rule) and accepted.
        /// </summary>
        private List<string> ConsumeClassAbilityTokensForOverhaulRespec()
        {
            var consumed = new List<string>();

            foreach (var item in GetAllPossessions())
            {
                if ((item.GetProperty(PropertyInt.ClassAbilityTokenId) ?? 0) <= 0)
                    continue;

                // Captured before the consume: the object is destroyed by the call below.
                var name = item.Name;

                if (TryConsumeFromInventoryWithNetworking(item, item.StackSize ?? 1))
                    consumed.Add(name);
                else
                    log.Warn($"[CARESPEC] {Name} (0x{Guid.Full:X8}): could not consume the class ability training token '{name}' (0x{item.Guid.Full:X8}) during the overhaul respec. Its points have still been returned, so this character now holds both the points and a usable token.");
            }

            return consumed;
        }

        /// <summary>
        /// The login notice for a character the overhaul respec actually changed. A build that silently
        /// vanished has to be explained in the same session, so this is NOT optional: it is delivered as
        /// a modal popup (<see cref="GameEventPopupString"/>, the same client dialog
        /// <see cref="SendFirstClassAbilityPointNotice"/> uses) and mirrored to the chat log so the
        /// explanation survives dismissing the modal. Both deliveries render the same paragraphs from
        /// ClassAbilityOverhaulRespec.BuildNoticeParagraphs, so they cannot drift apart.
        /// </summary>
        private void SendClassAbilityOverhaulRespecNotice(IReadOnlyList<string> consumedTokenNames)
        {
            if (Session == null)
                return;

            var paragraphs = ClassAbilityOverhaulRespec.BuildNoticeParagraphs(AvailableClassAbilityPoints, consumedTokenNames);

            Session.Network.EnqueueSend(new GameEventPopupString(Session, string.Join("\n\n", paragraphs)));

            foreach (var paragraph in paragraphs)
                Session.Network.EnqueueSend(new GameMessageSystemChat(paragraph, ChatMessageType.Advancement));
        }

        /// <summary>
        /// Unlearns a class ability entirely (all ranks), refunding its cumulative point cost and
        /// charging the respec luminance fee. Returns FALSE with a player-facing error and no
        /// state change on any validation failure. ignoreFee skips the luminance charge (developer
        /// testing path only), so rapid respec isn't blocked by the economy.
        ///
        /// <paramref name="reason"/> distinguishes a single player-chosen unlearn
        /// (<see cref="CapLedgerReason.Unlearn"/>, the default) from one iteration of the trainer's
        /// full-respec loop (<see cref="CapLedgerReason.Respec"/>, which also passes a shared
        /// <paramref name="batchId"/> so the ledger can tell one respec that touched N abilities from
        /// N separate unlearns).
        /// </summary>
        public bool UnlearnClassAbility(ClassAbilityDefinition skill, out string error, out int refunded, bool ignoreFee = false,
            CapLedgerReason reason = CapLedgerReason.Unlearn, string batchId = null)
        {
            error = null;
            refunded = 0;

            // PvP arena (S6): refused in a match, before any rank, refund or fee moves
            if (IsInPvpMatch)
            {
                error = PvpArenaText.AbilitySpendRefused;
                return false;
            }

            var rank = GetClassAbilityRank(skill.Id);

            if (rank <= 0)
            {
                error = $"You have not learned {skill.DisplayName}.";
                return false;
            }

            if (!ignoreFee)
            {
                var fee = PropertyManager.GetLong("class_ability_respec_lum_cost").Item;

                if (!TrySpendLuminanceIncludingBank(fee))
                {
                    error = $"Unlearning a class ability costs {fee:N0} Luminance; you have {GetSpendableLuminance():N0}.";
                    return false;
                }
            }

            refunded = skill.CumulativeCost(rank);

            if (Character.EraseQuest(ClassAbilityRegistry.QuestKey(skill), CharacterDatabaseLock))
                CharacterChangesDetected = true;

            GetClassAbilityCache().Remove(skill.Id);

            // AFTER the cache removal, deliberately: AdjustClassAbilityPoints snapshots
            // ClassAbilityPointsSpentOnOwnedRanks() into the row's owned_Cost_After, and that term is
            // only correct once this ability's ranks are out of the cache. The rank write and the
            // point write are not atomic with each other either way (that is the whole reason this
            // ledger exists), so ordering it this way makes the recorded owned cost match the state
            // the character is actually left in.
            AdjustClassAbilityPoints(refunded, 0, reason,
                ability: skill.Name, rankAfter: 0, batchId: batchId,
                detail: $"unlearned rank {rank} (cumulative {refunded})");

            InvalidateClassAbilityHookCaches();
            SendEnhancedStatUpdate(skill);

            SaveBiotaToDatabase();
            SaveCharacterToDatabase();

            return true;
        }
    }
}
