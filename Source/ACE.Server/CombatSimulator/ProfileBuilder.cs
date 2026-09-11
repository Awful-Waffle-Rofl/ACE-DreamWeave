using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using log4net;

using ACE.Database;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

namespace ACE.Server.CombatSimulator
{
    /// <summary>
    /// The only part of the simulator that touches the database. Hydrates a throwaway Player
    /// from persisted biota, walks the production mitigation functions to fill a
    /// DefenderProfile, then drops the Player.
    ///
    /// NOTHING IS PERSISTED. A biota reaches the shard database only through an explicit
    /// SaveBiotaToDatabase call, or by being reachable from a landblock's worldObjects
    /// dictionary or PlayerManager's offlinePlayers dictionary when their save intervals fire.
    /// A Player constructed here and dropped is in none of those.
    ///
    /// Do not point this at a character who is online, and note precisely why. The hydrated copy
    /// itself is never at risk: ConvertToEntityBiota builds a fresh runtime biota graph and
    /// WorldObjectFactory.CreateWorldObject does the same per possession, so GetCreatureSkill's
    /// write-on-read - it inserts missing PropertiesSkill rows and sets ChangesDetected - lands
    /// only on the throwaway. The reason to refuse is that a profile of a logged-in character
    /// would silently disagree with live state, since the persisted biota lags whatever that
    /// player is doing right now. The guard is also TOCTOU: it runs on the caller's thread while
    /// the load runs later on the shard worker, so it cannot exclude a character who logs in
    /// mid-flight. It is a good-faith check, not an interlock.
    ///
    /// WHAT A DefenderProfile DOES NOT MODEL
    ///
    /// It is a PvE profile. Both the armor and the shield tables resolve the ignore-magic-armor
    /// and ignore-magic-resist terms for a NON-PLAYER attacker, because the engine gates those on
    /// the attacker (Monster_Melee.cs:463 and :476 open with "if (!(this is Player)) return 0",
    /// and the call site is attacker.GetArmorMod(playerDefender, ...) at
    /// Entity\DamageEvent.cs:358; Creature_Combat.cs:735 and :747 branch on "attacker is Player"
    /// directly). Applying a profile to a player attacker would need a third axis on both keys.
    /// The two resolutions coincide at stock configuration anyway, because both pvp scalars
    /// default to 1.0 (Managers\PropertyManager.cs:1270-1271) and the scaled helpers return zero
    /// at 1.0 even for a player attacker.
    ///
    /// AttackerSpec.AttackerIsPlayer = true is therefore OUTSIDE what a DefenderProfile models.
    /// The flag exists so the Battle Hardened PvE gate can be honoured, not so a PvP fight can be
    /// simulated; do not reach for it expecting correct PvP numbers. Two known divergences under
    /// it, both in the resistance term:
    ///
    /// MitigationMath.ResistanceMod omits the additive rating-space rescale at
    /// Creature_Properties.cs:150-162. That branch runs only when ignore-magic-resist is set AND
    /// both sides are players AND ignore_magic_resist_pvp_scalar is not 1.0, so a PvE profile
    /// excludes it by construction. It would have to be reinstated alongside the player-attacker
    /// axis above.
    ///
    /// MitigationMath.ResistanceMod also short-circuits to weaponResistanceMod only when the
    /// attacker is NOT a player, whereas Creature_Properties.cs:118-122 returns it flat for ANY
    /// attacker once ignore-magic-resist is set and ignore_magic_resist_pvp_scalar is 1.0 - which
    /// is the default. So a spec carrying both IgnoreMagicResist and AttackerIsPlayer gets
    /// P0 * max(V0, weaponResistanceMod) where the engine gives weaponResistanceMod alone. Inert
    /// for every PvE spec, which is every spec this profile is valid for.
    ///
    /// GetShieldMod's "if (CombatMode == CombatMode.NonCombat) return 1.0f" early return at
    /// Creature_Combat.cs:672-673 is deliberately NOT honoured. A Player hydrated here is always
    /// NonCombat - the restore constructor's SetEphemeralValues calls
    /// SetStance(MotionStance.NonCombat, false) at Player.cs:205 - so honouring it would zero
    /// every shield row of every character ever profiled. The profile therefore describes a
    /// defender in COMBAT STANCE. This does not put the shield out of step with the defense
    /// skills: GetEffectiveDefenseSkill's stance term, Player.GetDefenseStanceMod
    /// (Player_Combat.cs:291-317), already returns 1.0f for a hydrated player - it is neither
    /// jumping nor logging out, and the default CurrentMovementData
    /// (WorldObject_Properties.cs:866) has a null Invalid section, so the forward command is
    /// MotionCommand.Invalid and the switch falls to "default: return 1.0f". Both terms are
    /// already combat-stance values; do not reopen this.
    ///
    /// Cost note: the Player restore constructor makes a synchronous auth-database call,
    /// DatabaseManager.Authentication.GetAccountById at Player.cs:177, so every build stalls the
    /// single shard worker thread on one auth round trip on top of its shard reads. Accepted for
    /// an admin command over a handful of slots.
    /// </summary>
    public static class ProfileBuilder
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private static readonly DamageType[] PhysicalDamageTypes =
        {
            DamageType.Slash, DamageType.Pierce, DamageType.Bludgeon,
        };

        // Nether belongs here even though it is void magic rather than an element: every path this
        // builder walks resolves it through the same code as the other six. GetResistance maps it
        // to ArmorModVsNether (Monster_Melee.cs:558-559), GetImpenBaneKey to the same property
        // (Managers\EnchantmentManager.cs:794-795), and GetResistanceKey to ResistNether (:821-822).
        // Player.GetNaturalResistance returns a hardcoded 0.5f for it (Player_Combat.cs:1070-1071),
        // which is a distinct VALUE but not a distinct path - it still arrives through the same
        // min(protection, natural) arithmetic. Omitting it made a nether hit fall through all three
        // fail-soft lookups to fully unmitigated damage, against an engine floor of 0.5.
        private static readonly DamageType[] ElementalDamageTypes =
        {
            DamageType.Fire, DamageType.Cold, DamageType.Acid, DamageType.Electric, DamageType.Nether,
        };

        /// <summary>
        /// Normally returns immediately and fires the callback later, NEVER on a world thread.
        ///
        /// ONE EXCEPTION TO THAT, and it bites. When the character is online the refusal is
        /// SYNCHRONOUS: the callback runs on the caller's own thread, re-entering the caller
        /// before this method has returned. So do not publish state the callback needs AFTER the
        /// call - "BuildAsync(g, cb); pending[g] = ...;" hands the callback a table that does not
        /// have its row yet - and do not hold a non-reentrant lock across the call, because the
        /// callback will try to take it again on the same thread.
        ///
        /// THE CALLBACK IS INVOKED AT MOST ONCE, and on every path this class controls it is
        /// invoked exactly once, with null on any failure: an online refusal, a missing character,
        /// a missing biota, a load or conversion that threw, a build that threw, or a failure to
        /// queue either the possession load or the pool task. The consumer's delegate is never
        /// invoked from inside a guarded region, so a delegate that throws is not answered with a
        /// second callback.
        ///
        /// TWO SHAPES YIELD NO CALLBACK AT ALL, and a consumer has to survive both.
        ///
        /// The first is this method throwing before it returns, which the caller sees directly.
        ///
        /// The second is silent, and is the one that matters. GetCharacter and
        /// GetPossessedBiotasInParallel each queue work that the shard worker later runs through
        /// RunStandalone, whose catch only logs "[DATABASE] DoWork task failed" and swallows -
        /// its own comment reads "perhaps add failure callbacks? swallow for now"
        /// (Source\ACE.Database\SerializedShardDatabase.cs:248-261). If the underlying read throws
        /// inside that queued task - an EnableRetryOnFailure(10) budget exhausted by a database
        /// blip, say - the callback inside that task body never runs. The enqueue guards below
        /// cannot reach it: they wrap the enqueue, and this throw happens later, during the
        /// worker's execution. The consumer sees NO callback, not a null one, and waits forever;
        /// nothing on this path has a timeout.
        ///
        /// So do not gate anything irreversible on a completion count reaching zero. An
        /// aggregating consumer must be able to report a partial result, and should not, for
        /// example, hold a session's reply hostage to the last build arriving. This is
        /// pre-existing SerializedShardDatabase behaviour, not something this class introduces or
        /// should paper over with a watchdog.
        ///
        /// WHAT RUNS WHERE. Only the database work stays on the shard worker thread
        /// (Source\ACE.Database\SerializedShardDatabase.cs:556-563 shows the shape): the character
        /// load, the possession load, the biota read, and the conversion of that biota. The biota
        /// read must stay there because that is what serializes it against concurrent SaveBiota
        /// traffic, and the conversion stays with it because a cache hit hands back the very
        /// instance the rest of the server is sharing
        /// (Source\ACE.Database\ShardDatabaseWithCaching.cs:102-107) - a reference is not
        /// serialized just because the call that produced it was.
        ///
        /// Everything after that - the Player construction, the walk, and the callback - runs on a
        /// THREAD-POOL thread, because hydrating a full inventory builds one WorldObject per item
        /// and recursing through side containers, and doing that on the worker stalls every other
        /// player's shard read and write behind it. Login keeps the same construction off that
        /// thread for the same reason, by enqueuing it onto the world thread
        /// (Managers\WorldManager.cs:173-177).
        ///
        /// WHAT A CONSUMER MUST DO. The callback is not on a world thread, so it must not touch
        /// world state: no sending network messages to a session, no reading or mutating a live
        /// WorldObject, no landblock work. Capture the DefenderProfile - which holds no
        /// WorldObject and no database handle, by design - and enqueue everything else onto the
        /// owning object's action queue.
        ///
        /// The callback can also now run CONCURRENTLY with the callback of another BuildAsync
        /// call, which was impossible while it ran on the single worker. Any aggregation across
        /// several builds must be thread-safe by construction - Interlocked, a lock, or a
        /// concurrent collection - not by assuming serialization. Completion order was never
        /// guaranteed and still is not.
        ///
        /// Three different threads can invoke the callback, so it must be thread-agnostic: the
        /// caller's own thread when the character is refused for being online, the shard worker
        /// when the load itself fails or cannot be queued, and a pool thread on success.
        ///
        /// Never call this from the world thread and block on the result.
        /// </summary>
        public static void BuildAsync(uint characterGuid, Action<DefenderProfile> callback)
        {
            if (PlayerManager.GetOnlinePlayer(characterGuid) != null)
            {
                log.Warn($"ProfileBuilder refused character 0x{characterGuid:X8}: currently online.");
                callback?.Invoke(null);
                return;
            }

            DatabaseManager.Shard.GetCharacter(characterGuid, character =>
            {
                if (character == null)
                {
                    callback?.Invoke(null);
                    return;
                }

                // Queueing is guarded for the same reason the pool task below is: this runs on the
                // shard worker, so an exception escaping here reaches RunStandalone's generic
                // handler, which logs "[DATABASE] DoWork task failed" and swallows it
                // (SerializedShardDatabase.cs:250-260). The consumer would then wait forever,
                // because nothing on this path has a timeout.
                try
                {
                    DatabaseManager.Shard.GetPossessedBiotasInParallel(characterGuid, possessions =>
                        LoadAndDispatch(characterGuid, character, possessions, callback));
                }
                catch (Exception ex)
                {
                    log.Error($"ProfileBuilder could not queue the possession load for 0x{characterGuid:X8}", ex);
                    callback?.Invoke(null);
                }
            });
        }

        /// <summary>
        /// Runs on the shard worker. Does the last of the database work - the biota read and its
        /// conversion - then hands the rest to the thread pool. Answers the callback exactly once
        /// on every path through it.
        /// </summary>
        private static void LoadAndDispatch(
            uint characterGuid,
            ACE.Database.Models.Shard.Character character,
            ACE.Database.Entity.PossessedBiotas possessions,
            Action<DefenderProfile> callback)
        {
            // STILL ON THE SHARD WORKER, deliberately. GetBiota has to be serialized against
            // concurrent SaveBiota traffic, and so does the walk of the graph it returns: on a
            // cache hit that is the shared instance, not a private copy.
            ACE.Entity.Models.Biota entityBiota = null;

            try
            {
                var shardBiota = DatabaseManager.Shard.BaseDatabase.GetBiota(character.Id, true);

                if (shardBiota != null)
                    entityBiota = ACE.Database.Adapter.BiotaConverter.ConvertToEntityBiota(shardBiota);
                else
                    log.Warn($"ProfileBuilder found no biota for character 0x{characterGuid:X8}.");
            }
            catch (Exception ex)
            {
                log.Error($"ProfileBuilder failed to load 0x{characterGuid:X8}", ex);
                entityBiota = null;
            }

            // deliberately OUTSIDE the try above: a consumer delegate that throws must not be
            // caught by a handler that answers with a second callback
            if (entityBiota == null)
            {
                callback?.Invoke(null);
                return;
            }

            // OFF THE SHARD WORKER from here on. Nothing below touches the SHARD database, and
            // the possession biotas are safe to carry across: GetPossessionsCore loads them
            // NoTracking through its own context and deliberately never puts them in the biota
            // cache (ShardDatabase.cs:862-880), so nothing else holds a reference. Login hands the
            // same PossessedBiotas across a thread boundary the same way
            // (Managers\WorldManager.cs:173-177).
            try
            {
                Task.Run(() =>
                {
                    DefenderProfile profile;

                    try
                    {
                        profile = BuildFrom(entityBiota, character, possessions);
                    }
                    catch (Exception ex)
                    {
                        log.Error($"ProfileBuilder failed for 0x{characterGuid:X8}", ex);
                        profile = null;
                    }

                    // outside the try for the same reason as above: compute first, invoke after,
                    // so the consumer's delegate is never inside a guarded region. The
                    // null-conditional matches the surrounding SerializedShardDatabase wrappers
                    // (SerializedShardDatabase.cs:561, :599).
                    callback?.Invoke(profile);
                });
            }
            catch (Exception ex)
            {
                log.Error($"ProfileBuilder could not queue the profile build for 0x{characterGuid:X8}", ex);
                callback?.Invoke(null);
            }
        }

        /// <summary>
        /// Runs on a thread-pool thread. No SHARD database access happens here - the biota read
        /// and its conversion are already done, by the caller, on the shard worker - but this is
        /// NOT database-free: the Player restore constructor issues one blocking AUTH read,
        /// Account = DatabaseManager.Authentication.GetAccountById(Character.AccountId)
        /// (WorldObjects\Player.cs:177). That is safe off-thread because GetAccountById opens its
        /// own AuthDbContext and reads AsNoTracking
        /// (ACE.Database\AuthenticationDatabase.cs:79-87), so it shares no context and tracks no
        /// entity - but it is a synchronous round trip, and it is what makes this method worth
        /// keeping off the shard worker rather than merely nice to.
        /// </summary>
        private static DefenderProfile BuildFrom(
            ACE.Entity.Models.Biota entityBiota,
            ACE.Database.Models.Shard.Character character,
            ACE.Database.Entity.PossessedBiotas possessions)
        {
            // The real inventory is load-bearing even though no armor comes off it. Container's
            // SortWorldObjectsIntoInventory accumulates each item's burden into EncumbranceVal
            // (Container.cs:167, :191), GetEffectiveDefenseSkill multiplies the skill by
            // GetBurdenMod (Creature_Combat.cs:492, :502), and that is 1.0f only while burden is
            // under 1.0 (Physics\Common\EncumbranceSystem.cs:40-48, reached through
            // Player.GetBurdenMod at Player.cs:1175). Passing an empty list left every defender at
            // full defense skill, so an overburdened character - a mule or a heavy-tinker build,
            // which is exactly what this tool gets pointed at - profiled up to 2x its live
            // defense, and hit chance is derived from that skill.
            //
            // This is the same list, from the same query, that login feeds through the same
            // method (Managers\WorldManager.cs:266).
            //
            // FIRST argument is the ENTITY biota; inventory and wielded are SHARD biotas. The two
            // types are not interchangeable and both namespaces are spelled out in this file for
            // that reason.
            var player = new Player(
                entityBiota,
                possessions.Inventory,
                possessions.WieldedItems,
                character,
                null);

            var profile = Walk(player);

            // the Player is dropped here and is never saved
            return profile;
        }

        private static DefenderProfile Walk(Player player)
        {
            var armor = new Dictionary<ArmorKey, ArmorRow>();
            var resistance = new Dictionary<DamageType, ResistancePair>();
            var shield = new Dictionary<ShieldKey, ShieldRow>();

            var allTypes = PhysicalDamageTypes.Concat(ElementalDamageTypes).ToList();

            // enumerate body parts rather than rolling one: BodyParts.GetFlags decomposes a
            // height band's combined mask into its individual parts
            var bodyParts = BodyParts.GetFlags(BodyParts.Upper)
                .Concat(BodyParts.GetFlags(BodyParts.Mid))
                .Concat(BodyParts.GetFlags(BodyParts.Lower))
                .Distinct()
                .ToList();

            foreach (var damageType in allTypes)
            {
                foreach (var bodyPart in bodyParts)
                {
                    foreach (var ignoreMagicArmor in new[] { false, true })
                    {
                        foreach (var ignoreMagicResist in new[] { false, true })
                        {
                            var effectiveAl = SumEffectiveArmorLevel(player, bodyPart, damageType, ignoreMagicArmor, ignoreMagicResist);

                            armor[new ArmorKey(bodyPart, damageType, ignoreMagicArmor, ignoreMagicResist)] = new ArmorRow(effectiveAl);
                        }
                    }
                }

                resistance[damageType] = BuildResistancePair(player, damageType);

                foreach (var ignoreMagicArmor in new[] { false, true })
                    shield[new ShieldKey(damageType, ignoreMagicArmor)] = BuildShieldRow(player, damageType, ignoreMagicArmor);
            }

            // THE MAGIC ENTRY COMES FROM A DIFFERENT ENGINE FUNCTION, deliberately, and it is not
            // interchangeable with the other two. GetEffectiveDefenseSkill chooses its skill with a
            // two-way ternary - "combatType == CombatType.Missile ? Skill.MissileDefense :
            // Skill.MeleeDefense" (Creature_Combat.cs:490) - so CombatType.Magic falls into the
            // MELEE arm silently. The engine's spell-resist path never calls it for magic; it calls
            // GetEffectiveMagicDefense (Creature_Magic.cs:172), which reads Skill.MagicDefense with
            // its own weapon-modifier and imbue terms.
            //
            // Filling the Magic row from GetEffectiveDefenseSkill stored melee defense under the
            // Magic key, and HitChance.For reads it straight. For a character at MeleeDefense 500
            // and MagicDefense 300 against attack skill 400 the reported magic hit chance was the
            // melee one, and SwingsToKill divides by it. See DefenderProfile.DefenseSkills.
            var defenseSkills = new Dictionary<CombatType, uint>
            {
                { CombatType.Melee, player.GetEffectiveDefenseSkill(CombatType.Melee) },
                { CombatType.Missile, player.GetEffectiveDefenseSkill(CombatType.Missile) },
                { CombatType.Magic, player.GetEffectiveMagicDefense() },
            };

            // per combat type, because GetSpecDefenseBonus (Creature_Rating.cs:285-307) is folded
            // into the rating and returns 0 for a null combat type. The third argument is the
            // profiled player itself, which makes "attacker is not Player" false at
            // Creature_Rating.cs:277 and so suppresses the PvE-only Battle Hardened term; that
            // term is stored separately in BattleHardenedMultiplier and reapplied by
            // MitigationMath.DamageResistRatingMod under the same gate.
            var damageResistRatingBase = new Dictionary<CombatType, float>
            {
                { CombatType.Melee, player.GetDamageResistRatingMod(CombatType.Melee, true, player) },
                { CombatType.Missile, player.GetDamageResistRatingMod(CombatType.Missile, true, player) },
                { CombatType.Magic, player.GetDamageResistRatingMod(CombatType.Magic, true, player) },
            };

            return new DefenderProfile
            {
                CharacterGuid = player.Guid.Full,
                Label = player.Name,
                Level = player.Level ?? 0,
                SnapshotUtc = DateTime.UtcNow,
                MaxHealth = player.Health.MaxValue,
                DefenseSkills = defenseSkills,
                Armor = armor,
                Resistance = resistance,
                Shield = shield,
                DamageResistRatingBase = damageResistRatingBase,
                BattleHardenedMultiplier = player.GetBattleHardenedDamageResistMod(),
            };
        }

        /// <summary>
        /// Mirrors the player-defender armor sum, Creature.GetArmorMod(Creature, DamageType,
        /// List&lt;WorldObject&gt;, WorldObject, float) at
        /// Source\ACE.Server\WorldObjects\Monster_Melee.cs:427-461, with armorRendingMod fixed
        /// at 1.0 and stopping short of SkillFormula.CalcArmorMod. MitigationMath.ArmorMod
        /// applies the attacker's rending and then runs the curve, which is the engine's order.
        ///
        /// This is NOT the Creature_BodyPart.cs:65 overload. That one is the monster-defender
        /// mirror and zeroes the ignore-magic terms flatly rather than through the scaled
        /// helpers; our defender is always a player.
        ///
        /// ATTACKER RESOLUTION OF THE IGNORE-MAGIC TERMS. In the engine the two scaled helpers
        /// (Monster_Melee.cs:463-487) are called on the ATTACKER - the call site is
        /// attacker.GetArmorMod(playerDefender, ...) at Entity\DamageEvent.cs:358 - and both
        /// return a flat zero unless the attacker is a Player AND the matching pvp scalar is
        /// something other than 1.0. Both scalars default to 1.0
        /// (Managers\PropertyManager.cs:1270-1271), so the two resolutions are numerically
        /// identical at stock configuration. ArmorKey carries no attacker-is-player axis, so a
        /// row can only hold one of them; we build the non-player-attacker resolution, which is
        /// the PvE case the simulator exists for.
        /// </summary>
        private static float SumEffectiveArmorLevel(Player player, BodyPart bodyPart, DamageType damageType, bool ignoreMagicArmor, bool ignoreMagicResist)
        {
            // Monster_Melee.cs:411. Declared on Creature but reads nothing off the instance -
            // it is a pure function of the target's equipped clothing - so calling it on the
            // defender returns exactly what the attacker's call would.
            var armors = player.GetArmorLayers(player, bodyPart);

            var effectiveAL = 0.0f;

            foreach (var armor in armors)
                effectiveAL += PieceEffectiveArmorLevel(player, armor, damageType, ignoreMagicArmor);

            // life spells
            // additive: armor/imperil
            var bodyArmorMod = player.EnchantmentManager.GetBodyArmorMod();

            // IgnoreMagicResistScaled for a non-player attacker (Monster_Melee.cs:476-487)
            if (ignoreMagicResist)
                bodyArmorMod = 0;

            effectiveAL += bodyArmorMod;

            // the engine's "if (effectiveAL > 0) effectiveAL *= armorRendingMod" is the
            // identity at armorRendingMod 1.0, and MitigationMath.ArmorMod reapplies both the
            // sign guard and the real rending value against this stored sum
            return effectiveAL;
        }

        /// <summary>
        /// Mirrors the per-piece term, Creature.GetArmorMod(WorldObject, DamageType, bool) at
        /// Source\ACE.Server\WorldObjects\Monster_Melee.cs:493-534. The engine reads
        /// PropertyInt.ArmorType into a local and never uses it; that dead read is not
        /// reproduced. The ignore-magic-armor rescale is resolved as described on
        /// SumEffectiveArmorLevel.
        /// </summary>
        private static float PieceEffectiveArmorLevel(Player player, WorldObject armor, DamageType damageType, bool ignoreMagicArmor)
        {
            // get base armor/resistance level
            var baseArmor = armor.GetProperty(PropertyInt.ArmorLevel) ?? 0;
            var resistance = player.GetResistance(armor, damageType);

            // armor level additives
            var armorMod = armor.EnchantmentManager.GetArmorMod();

            // resistance additives
            // banes, lures
            var armorBane = armor.EnchantmentManager.GetArmorModVsType(damageType);

            return ComposePieceArmorLevel(baseArmor, armorMod, resistance, armorBane, ignoreMagicArmor);
        }

        /// <summary>
        /// The pure arithmetic half of PieceEffectiveArmorLevel, split out so it can be tested
        /// without a Player. Every argument is a value the caller has already read off the engine;
        /// this composes them exactly as Monster_Melee.cs:493-534 does, including the resistance
        /// clamp at :524, and reads nothing itself.
        ///
        /// Internal rather than private ONLY so ACE.Server.Tests can pin it (InternalsVisibleTo,
        /// ACE.Server.csproj:15). It exists because no Player can be constructed in that harness
        /// at all: both Player constructors call DatabaseManager.Authentication.GetAccountById
        /// (Player.cs:149 and :177), and AuthDbContext.OnConfiguring dereferences a null
        /// ConfigManager.Config with no database configured (Models\Auth\AuthDbContext.cs:27). So
        /// the walk itself is untestable headless, and its transcribed arithmetic is the largest
        /// part of it that is not.
        /// </summary>
        internal static float ComposePieceArmorLevel(int baseArmor, float armorModEnchantment, double resistance, float armorBane, bool ignoreMagicArmor)
        {
            // IgnoreMagicArmorScaled for a non-player attacker (Monster_Melee.cs:463-474) drops
            // BOTH the armor-level enchantment and the per-damage-type bane
            if (ignoreMagicArmor)
            {
                armorModEnchantment = 0.0f;
                armorBane = 0.0f;
            }

            var effectiveAL = baseArmor + armorModEnchantment;

            var effectiveRL = (float)(resistance + armorBane);

            // resistance clamp
            effectiveRL = Math.Clamp(effectiveRL, -2.0f, 2.0f);

            return effectiveAL * effectiveRL;
        }

        /// <summary>
        /// Mirrors Creature.GetResistanceMod at
        /// Source\ACE.Server\WorldObjects\Creature_Properties.cs:113-165, keeping only the
        /// defender-side half.
        ///
        /// P0 is the protection mod floored against natural resistance with the player's
        /// resistance augmentation folded in. V0 is the RAW vulnerability mod: the engine's
        /// "if (vulnMod &lt; weaponResistanceMod) vulnMod = weaponResistanceMod" floor is an
        /// attacker term and MitigationMath.ResistanceMod applies it at call time.
        ///
        /// Three lines of the engine function are deliberately not reproduced, all of them
        /// attacker-dependent: the ignore-magic-resist bypass at :118-122, the
        /// weaponResistanceMod floor at :147-148, and the additive rating-space rescale at
        /// :150-162 which runs only when both sides are players.
        /// </summary>
        private static ResistancePair BuildResistancePair(Player player, DamageType damageType)
        {
            var protMod = player.EnchantmentManager.GetProtectionResistanceMod(damageType);
            var vulnMod = player.EnchantmentManager.GetVulnerabilityResistanceMod(damageType);

            var naturalResistMod = player.GetNaturalResistance(damageType);

            // the engine guards this on "this is Player"; our defender always is
            var resistAug = player.GetAugmentationResistance(damageType);

            return ComposeResistancePair(protMod, vulnMod, naturalResistMod, resistAug);
        }

        /// <summary>
        /// The pure arithmetic half of BuildResistancePair, split out so it can be tested without a
        /// Player - see ComposePieceArmorLevel for why that is necessary. Composes the four engine
        /// reads exactly as Creature_Properties.cs:129-143 does.
        /// </summary>
        internal static ResistancePair ComposeResistancePair(float protMod, float vulnMod, float naturalResistMod, int resistAug)
        {
            // protection mod becomes either life protection or natural resistance,
            // whichever is more powerful (more powerful = lower value here)
            if (protMod > naturalResistMod)
                protMod = naturalResistMod;

            if (resistAug > 0)
            {
                var augFactor = Math.Min(1.0f, resistAug * 0.1f);
                protMod *= 1.0f - augFactor;
            }

            return new ResistancePair(protMod, vulnMod);
        }

        /// <summary>
        /// Mirrors Creature.GetShieldMod at
        /// Source\ACE.Server\WorldObjects\Creature_Combat.cs:669-780, stopping at
        /// "effectiveLevel = Math.Min(effectiveLevel, shieldCap)". The attacker's
        /// GetIgnoreShieldMod multiply and SkillFormula.CalcArmorMod are MitigationMath's, and
        /// the IgnoreAllArmor and 180 degree arc gates are binary gates it already applies.
        ///
        /// Three deliberate departures, each recorded because a reader will otherwise look for
        /// them:
        ///
        /// 1. The "CombatMode == NonCombat" early return at :672-673 is NOT applied. It is
        ///    defender state at the instant of the hit, and a hydrated offline Player is always
        ///    NonCombat (Player.SetEphemeralValues calls SetStance(MotionStance.NonCombat)), so
        ///    honouring it would zero every shield row for every character profiled. The
        ///    profile describes a defender in combat.
        /// 2. The no-shield Arcane Defender branch at :677-712 IS reproduced, and it factors
        ///    exactly: WeaponModCombat.ArcaneShieldMod is CalcArmorMod(armorLevel *
        ///    ignoreShieldMod) (WeaponMods\WeaponModCombat.cs:162-171), the same shape
        ///    MitigationMath.ShieldMod runs, and that branch takes no shield-skill cap. A
        ///    defender with neither a shield nor the modifier stores 0, and CalcArmorMod(0) is
        ///    1.0 (SkillFormula.cs:29-37), which is the engine's early return.
        /// 3. ignoreMagicArmor is an attacker term (:730), so it is a KEY axis rather than
        ///    something resolvable here: the caller stores one row per value of it. Both the
        ///    shield's armor-level enchantment (:734-735) and its bane/lure enchantment
        ///    (:746-747) drop out when it is set. Resolved for a non-player attacker, i.e. the
        ///    flat-zero arm of each of those two ternaries, matching the armor table and the
        ///    PvE-profile note on this class.
        ///
        /// The Arcane Defender branch reads no enchantment, so its two rows are identical; the
        /// boolean is still honoured as a key so every damage type has both entries and
        /// MitigationMath.ShieldMod never has to fall back.
        /// </summary>
        private static ShieldRow BuildShieldRow(Player player, DamageType damageType, bool ignoreMagicArmor)
        {
            var shield = player.GetEquippedShield();

            if (shield == null)
            {
                var arcaneDefender = player.GetCasterOnlyModValue(WeaponModId.ArcaneDefender);

                if (double.IsNaN(arcaneDefender) || arcaneDefender <= 0.0)
                    return new ShieldRow(0.0f);

                return new ShieldRow((float)arcaneDefender);
            }

            // get base shield AL
            var baseSL = shield.GetProperty(PropertyInt.ArmorLevel) ?? 0.0f;

            // shield AL item enchantment additives:
            // impenetrability, brittlemail
            var modSL = shield.EnchantmentManager.GetArmorMod();

            // get shield RL against damage type
            var baseRL = player.GetResistance(shield, damageType);

            // shield RL item enchantment additives:
            // banes, lures
            var modRL = shield.EnchantmentManager.GetArmorModVsType(damageType);

            // SL cap:
            // Trained / untrained: 1/2 shield skill
            // Spec: shield skill
            // SL cap is applied *after* item enchantments
            var shieldSkill = player.GetCreatureSkill(Skill.Shield);

            return new ShieldRow(ComposeShieldLevel(
                baseSL, modSL, baseRL, modRL, ignoreMagicArmor,
                shieldSkill.Current,
                shieldSkill.AdvancementClass == SkillAdvancementClass.Specialized));
        }

        /// <summary>
        /// The pure arithmetic half of BuildShieldRow's shielded branch, split out so it can be
        /// tested without a Player - see ComposePieceArmorLevel for why that is necessary. Composes
        /// the engine reads exactly as Creature_Combat.cs:726-769 does, and stops at the same place
        /// BuildShieldRow does: the attacker's ignore-shield multiply and SkillFormula.CalcArmorMod
        /// belong to MitigationMath.
        ///
        /// The shield-skill cap is applied to the level, NOT to the mod, and after the
        /// enchantments - that ordering is the part most likely to drift, so it is what this is
        /// here to pin.
        /// </summary>
        internal static float ComposeShieldLevel(float baseSL, float modSL, double baseRL, float modRL, bool ignoreMagicArmor, uint shieldSkillCurrent, bool shieldSpecialized)
        {
            // Creature_Combat.cs:734-735 and :746-747, non-player-attacker arm: BOTH the shield's
            // armor-level enchantment and its per-damage-type bane/lure drop out
            if (ignoreMagicArmor)
            {
                modSL = 0.0f;
                modRL = 0.0f;
            }

            var effectiveSL = baseSL + modSL;

            var effectiveRL = (float)(baseRL + modRL);

            // resistance clamp
            effectiveRL = Math.Clamp(effectiveRL, -2.0f, 2.0f);

            var effectiveLevel = effectiveSL * effectiveRL;

            var shieldCap = shieldSkillCurrent;

            if (!shieldSpecialized)
                shieldCap = (uint)Math.Round(shieldCap / 2.0f);

            return Math.Min(effectiveLevel, shieldCap);
        }
    }
}
