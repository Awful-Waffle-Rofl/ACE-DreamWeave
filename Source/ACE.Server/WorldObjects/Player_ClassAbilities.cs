using System;
using System.Collections.Generic;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Database.Models.Shard;
using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Entity;
using ACE.Server.EquipmentMods;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;

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
            return rank > 0;
        }

        // Per-hook caches of this player's *learned* skill handlers, so the combat hot paths
        // iterate a tiny (usually empty) array rather than scanning the registry or the rank
        // dictionary. Built lazily from ClassAbilityRegistry's per-hook buckets, invalidated by
        // Learn/Unlearn.
        private (IOutgoingDamageAbility skill, int rank)[] outgoingDamageClassAbilities;
        private (IIncomingDamageAbility skill, int rank)[] incomingDamageClassAbilities;
        private (IMissileVolleyAbility skill, int rank)[] missileVolleyClassAbilities;
        private (IItemProcAbility skill, int rank)[] itemProcClassAbilities;
        private (ISpellHitAbility skill, int rank)[] spellHitClassAbilities;
        private (ICreatureDeathAbility skill, int rank)[] creatureDeathClassAbilities;

        private (T skill, int rank)[] BuildClassAbilityHookCache<T>(IReadOnlyList<T> handlers) where T : IClassAbility
        {
            var learned = new List<(T, int)>();

            foreach (var handler in handlers)
            {
                var rank = GetClassAbilityRank(handler.Definition.Id);
                if (rank > 0)
                    learned.Add((handler, rank));
            }
            return learned.ToArray();
        }

        private void InvalidateClassAbilityHookCaches()
        {
            outgoingDamageClassAbilities = null;
            incomingDamageClassAbilities = null;
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
        /// </summary>
        public void ApplyIncomingDamageClassAbilities(WorldObject source, DamageType damageType, uint damageTaken)
        {
            var learned = incomingDamageClassAbilities ??= BuildClassAbilityHookCache(ClassAbilityRegistry.IncomingDamageAbilities);

            if (learned.Length == 0)
                return;

            if (source is not Creature attacker || attacker is Player || attacker == this || attacker.IsDead)
                return;

            if (!PropertyManager.GetBool("class_abilities_enabled").Item)
                return;

            foreach (var (skill, rank) in learned)
                skill.OnDamageTaken(this, rank, attacker, damageType, damageTaken);
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
            // without the ability because TryGetClassAbility already returned above.
            var chance = DoubleVolleyAbility.Chance(rank,
                PropertyManager.GetDouble("class_ability_doublevolley_chance_base").Item,
                PropertyManager.GetDouble("class_ability_doublevolley_chance_step").Item,
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
        /// player's offensive war-magic projectiles successfully damages a monster. Monster targets
        /// only (PvP excluded here); the core site additionally gates on war magic and skips
        /// class-ability-spawned child projectiles so effects can't cascade.
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

        public int AvailableClassAbilityPoints
        {
            get => GetProperty(PropertyInt.AvailableClassAbilityPoints) ?? 0;
            set { if (value == 0) RemoveProperty(PropertyInt.AvailableClassAbilityPoints); else SetProperty(PropertyInt.AvailableClassAbilityPoints, value); }
        }

        public int TotalClassAbilityPointsEarned
        {
            get => GetProperty(PropertyInt.TotalClassAbilityPointsEarned) ?? 0;
            set { if (value == 0) RemoveProperty(PropertyInt.TotalClassAbilityPointsEarned); else SetProperty(PropertyInt.TotalClassAbilityPointsEarned, value); }
        }

        public int ClassAbilityPointsPurchasedWithLum
        {
            get => GetProperty(PropertyInt.ClassAbilityPointsPurchasedWithLum) ?? 0;
            set { if (value == 0) RemoveProperty(PropertyInt.ClassAbilityPointsPurchasedWithLum); else SetProperty(PropertyInt.ClassAbilityPointsPurchasedWithLum, value); }
        }

        /// <summary>
        /// Grants class ability points from any source (level milestone, quest reward, Luminance purchase,
        /// admin grant), in one place. There is NO lifetime cap (the no-caps redesign, DESIGN.md sec 1:
        /// power is limited by the Luminance price curve, not a wall), so this always succeeds for a
        /// positive amount. Returns FALSE only for a non-positive amount.
        /// </summary>
        public bool GrantClassAbilityPoints(int amount, string sourceText)
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

            TotalClassAbilityPointsEarned += amount;
            AvailableClassAbilityPoints += amount;

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

            if (GrantClassAbilityPoints(owed, source))
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

            if (GrantClassAbilityPoints(owed, "your enlightenment"))
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

            var totalLum = (AvailableLuminance ?? 0) + BankedLuminance;
            if (totalLum < cost)
            {
                error = $"Buying {count:N0} point{(count == 1 ? "" : "s")} costs {cost:N0} Luminance; you have {totalLum:N0} ({AvailableLuminance ?? 0:N0} available + {BankedLuminance:N0} banked).";
                return false;
            }

            if (!TrySpendLuminanceIncludingBank(cost))
            {
                error = $"Buying {count:N0} point{(count == 1 ? "" : "s")} costs {cost:N0} Luminance; you have {(AvailableLuminance ?? 0) + BankedLuminance:N0}.";
                return false;
            }

            ClassAbilityPointsPurchasedWithLum = purchased + count;

            // no cap - GrantClassAbilityPoints succeeds for a positive amount and saves the biota
            GrantClassAbilityPoints(count, $"spending {cost:N0} Luminance");

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

            var cost = ClassAbilityTokenCatalog.LumCost(offering.Definition, offering.Tier);

            // Luminance is spent from available (earned) Luminance first, then the uncapped bank - so a token
            // priced above the MaximumLuminance cap is still buyable out of banked Luminance.
            var totalLum = (AvailableLuminance ?? 0) + BankedLuminance;
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
                error = $"That token costs {cost:N0} Luminance; you have {(AvailableLuminance ?? 0) + BankedLuminance:N0}.";
                return false;
            }

            Session.Network.EnqueueSend(new GameMessageSystemChat(
                $"You buy {token.Name} for {cost:N0} Luminance. Use it from your pack to learn {offering.Definition.DisplayName} rank {offering.Tier} - it is already paid for.", ChatMessageType.Broadcast));

            return true;
        }

        /// <summary>
        /// Learns the next rank of a class ability, spending available points.
        /// Returns FALSE with a player-facing error and no state change on any validation failure.
        /// </summary>
        public bool LearnClassAbility(ClassAbilityDefinition skill, out string error)
        {
            error = null;

            // Mule (WaffleACE): class ability ranks are written straight to the quest registry below via
            // Character.GetOrCreateQuest, bypassing QuestManager entirely, so the AdvanceQuest guard in
            // QuestManager does NOT cover this path. Silent refusal - the caller surfaces 'error' itself.
            if (MuleBlocked(MuleAction.TrainClassAbility, notify: false))
            {
                error = "A mule cannot learn class abilities.";
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

            var rank = GetClassAbilityRank(skill.Id);

            if (rank >= skill.MaxRank)
            {
                error = $"{skill.DisplayName} is already at max rank ({rank}/{skill.MaxRank}).";
                return false;
            }

            var cost = skill.CostPerRank[rank];

            if (AvailableClassAbilityPoints < cost)
            {
                error = $"Rank {rank + 1} of {skill.DisplayName} requires {cost:N0} class ability point{(cost == 1 ? "" : "s")}; you have {AvailableClassAbilityPoints:N0}.";
                return false;
            }

            AvailableClassAbilityPoints -= cost;

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
        /// Pushes a live stat update to the client when an Enhanced &lt;stat&gt; class ability is learned or
        /// unlearned, so the attribute/skill panel reflects the new bonus without a relog. The bonus is
        /// getter-only, so the update carries NetworkStartingValue/NetworkInitLevel. Vital maximums are
        /// recomputed client-side from components a live vital message doesn't resend, and an attribute
        /// change cascades into derived skills/vitals - both fully reconcile on the next relog.
        /// </summary>
        private void SendEnhancedStatUpdate(ClassAbilityDefinition skill)
        {
            if (Session == null || ClassAbilityRegistry.GetHandler(skill.Id) is not EnhancedStatAbility enhanced)
                return;

            switch (enhanced.Kind)
            {
                case EnhancedStatKind.Attribute:
                    Session.Network.EnqueueSend(new GameMessagePrivateUpdateAttribute(this, Attributes[enhanced.TargetAttribute]));
                    break;
                case EnhancedStatKind.Skill:
                    Session.Network.EnqueueSend(new GameMessagePrivateUpdateSkill(this, GetCreatureSkill(enhanced.TargetSkill)));
                    break;
                case EnhancedStatKind.Vital:
                    // max vital is rebuilt client-side from components not carried by the live vital
                    // message; it refreshes on relog (the description packet sends NetworkStartingValue)
                    break;
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
                    AvailableClassAbilityPoints += points;
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
        /// Unlearns a class ability entirely (all ranks), refunding its cumulative point cost and
        /// charging the respec luminance fee. Returns FALSE with a player-facing error and no
        /// state change on any validation failure. ignoreFee skips the luminance charge (developer
        /// testing path only), so rapid respec isn't blocked by the economy.
        /// </summary>
        public bool UnlearnClassAbility(ClassAbilityDefinition skill, out string error, out int refunded, bool ignoreFee = false)
        {
            error = null;
            refunded = 0;

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
                    error = $"Unlearning a class ability costs {fee:N0} Luminance; you have {(AvailableLuminance ?? 0) + BankedLuminance:N0}.";
                    return false;
                }
            }

            refunded = skill.CumulativeCost(rank);
            AvailableClassAbilityPoints += refunded;

            if (Character.EraseQuest(ClassAbilityRegistry.QuestKey(skill), CharacterDatabaseLock))
                CharacterChangesDetected = true;

            GetClassAbilityCache().Remove(skill.Id);
            InvalidateClassAbilityHookCaches();
            SendEnhancedStatUpdate(skill);

            SaveBiotaToDatabase();
            SaveCharacterToDatabase();

            return true;
        }
    }
}
