using System;
using System.Reflection;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.MlShared;
using ACE.Server.WorldEvents;
using ACE.Server.WorldObjects;

using log4net;

using Position = ACE.Entity.Position;

namespace ACE.Server.MlDigsite
{
    /// <summary>
    /// THE one spawn helper for the whole digsite system (invariant 1). All three encounter types place
    /// every creature through <see cref="TrySpawn"/>; none of them has a placement path of its own, so a
    /// stamp added here is added for every creature in every encounter shape.
    ///
    /// The fixed order, and every step of it is load-bearing:
    ///   create -> fail closed on a non-Creature -> terrain snap in try/catch -> ALL stamps -> AddHeld ->
    ///   EnterWorld -> OnGeneration in try/catch.
    ///
    /// EVERY property write happens BEFORE EnterWorld. That is the rule all three hand-spawners in this fork
    /// already follow (MlRelariaSpawner, WorldEventSpawner.TryPlace, Player_WaveChallenge.SpawnWave): there
    /// must be no window in which a landblock save, or a client's create packet, sees this creature as an
    /// ordinary persistable monster.
    ///
    /// TimeToRot = -1 IS MANDATORY and is the opposite of what MlRelariaSpawner does. A Relaria boss is
    /// owned by nothing, so the landblock's decay pass IS its cleanup. A digsite creature is owned by its
    /// encounter, which destroys it at Finish - and the decay pass destroys with a bare Destroy(), with no
    /// Die(), so a creature that rotted mid-fight would never report its death, the wave would never empty
    /// and the encounter would hang until its TTL. The chest is the one digsite object that takes a FINITE
    /// TimeToRot instead, because it deliberately outlives the encounter (see MlDigsiteRewards).
    /// </summary>
    public static class MlDigsiteSpawner
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Places one creature of <paramref name="role"/> for this encounter and registers it with the
        /// encounter. Returns null on every refusal, having left the world untouched.
        ///
        /// Registration is done HERE rather than by the caller so no encounter type can place a creature and
        /// forget to track it - an untracked creature would never report its death, and the wave it belonged
        /// to would never empty.
        ///
        /// <paramref name="waveNumber"/> is the endless-waves wave this creature belongs to (1 for anything
        /// that is not part of a numbered wave). It drives the per-wave scaling applied below, before
        /// EnterWorld, composed with crowd scaling.
        /// </summary>
        public static Creature TrySpawn(MlDigsiteEncounter encounter, MlDigsiteRole role, Random rng, int waveNumber = 1)
        {
            if (encounter?.Anchor == null || !encounter.RunValid)
                return null;

            var entry = MlDigsiteRoster.Pick(role, encounter.Zone, rng);

            if (entry == null)
            {
                log.Error($"[ML_DIGSITE] {encounter} has no roster entry for role {role}; nothing spawned");
                return null;
            }

            // The Kept Siraluun rare miniboss: only the Boss Rush boss and a Waves encounter's checkpoint
            // mini-boss are eligible, and only ONE roll is ever taken per encounter (the encounter's own
            // latch) - so an endless run rolls at its FIRST checkpoint only, never once per checkpoint, which
            // would make the feather several times more common than the tuned chance says. Rolled and picked
            // through the same rng this whole spawn already carries, so the substitution consumes exactly one
            // more draw from it rather than a second, unseeded source.
            if (MlDigsiteRules.KeptSiraluunRollRole(role) && encounter.TryClaimKeptSiraluunRoll())
            {
                if (MlDigsiteRules.RollKeptSiraluun(MlDigsiteTunables.KeptSiraluunChance, rng.NextDouble()))
                {
                    var kept = MlDigsiteRules.PickKeptSiraluun(MlDigsiteRoster.KeptSiraluun, entry.Value.Level, rng);

                    log.Info($"[ML_DIGSITE] {encounter} substituted Kept Siraluun for the {role} draw: wcid {entry.Value.Wcid} ({entry.Value.Name}) -> wcid {kept.Wcid} ({kept.Name})");

                    entry = kept;
                }
            }

            var wcid = entry.Value.Wcid;
            var wo = WorldObjectFactory.CreateNewWorldObject(wcid);

            if (wo == null)
            {
                log.Error($"[ML_DIGSITE] {encounter} roster wcid {wcid} ({entry.Value.Name}) failed to create; is its weenie applied to this world database?");
                return null;
            }

            // Fail closed on a non-Creature. Only a Creature can be tethered, can die, and can report that
            // death back to the encounter - a non-Creature here would be an unkillable object standing on a
            // live landblock that no death hook could ever clear.
            if (!(wo is Creature creature))
            {
                log.Error($"[ML_DIGSITE] {encounter} roster wcid {wcid} is a {wo.WeenieType}, not a Creature; nothing spawned");
                wo.Destroy();
                return null;
            }

            var position = ScatterPoint(encounter.Anchor, rng);

            if (position == null)
            {
                creature.Destroy();
                return null;
            }

            creature.Location = position;

            WarnIfHeldCasterWithoutSpells(encounter, creature, role);

            // ---- every stamp, before EnterWorld ----------------------------------------------------------

            // The persistence exclusion AND the death hook's persisted key. Read by
            // WorldObject.IsDynamicThatShouldPersistToShard, by Creature.Die's two-key check, and by
            // MlDigsiteOrphanFilter as the backstop.
            creature.SetProperty(PropertyInt.MlDigsiteEncounterId, (int)encounter.EncounterId);

            // Mandatory - see the class remarks. The encounter owns this creature's cleanup; the decay pass
            // must never take it first, because decay destroys without ever running Die().
            creature.TimeToRot = -1;

            ApplyTether(creature);

            // RoZ round 19 owner ruling M1: map-event bosses resist magic too heavily. Boss/MiniBoss/
            // Checkpoint/Priority are the digsite's "boss-weight" roles (Wave and Add are ordinary trash
            // and stay at their authored MagicDefense); Priority is included because in a Corruption
            // encounter it IS the objective. Before EnterWorld like every other stamp here - and BEFORE the
            // level-spread defense floor below, so the floor measures what M1 left rather than M1 scaling
            // what the floor already solved.
            if (role == MlDigsiteRole.Boss || role == MlDigsiteRole.MiniBoss || role == MlDigsiteRole.Checkpoint || role == MlDigsiteRole.Priority)
                ApplyMapEventBossMagicDefenseScale(creature);

            // ONE audience scan per spawn, feeding both the level-spread scaling and the headcount below, so
            // the two can never size against different sets of players (MlDigsiteAudience.Count is this
            // sample's count).
            var profiles = MlDigsiteAudience.Sample(encounter.Anchor, MlDigsiteTunables.AudienceRadiusMetres);

            // RoZ round 19 level-spread scaling (MlMapEventScaling). BEFORE ApplySpawnScaling, so a headcount or
            // per-wave raise multiplies the downscaled creature instead of being undone by it. The trash key is
            // drawn from its OWN Random, never from rng: rng's draws are accounted for above (the Kept Siraluun
            // roll) and by the callers, and sampling a player must not shift any existing seeded sequence.
            var spreadSettings = MlMapEventTunables.Read();

            var roleClass = MlMapEventScaling.RoleClassFor(role);

            // Only trash samples a player, so only trash pays for a Random (and a ThreadSafeRandom draw).
            var keyRng = roleClass == MlMapEventRoleClass.Trash ? new Random(ThreadSafeRandom.Next(0, int.MaxValue - 1)) : null;

            var spread = MlMapEventScaling.ApplySpreadScaling(creature, roleClass, profiles, keyRng, spreadSettings, encounter.ToString());

            if (role == MlDigsiteRole.Boss && spread.BossHealthApplied)
                encounter.NoteBossHealthBase(spread.BossBaseStartingValue, spread.BossBaseMax, spread.BossHealthMult);

            // Crowd scaling for every digsite-spawned creature, on the Bluespire ladder D6 pattern but the
            // digsite system's OWN tunables - never D6's keys - COMPOSED with the endless-waves per-wave
            // scaling. Resolved at THIS spawn moment from the digsite audience scan, the same spawn-time
            // snapshot D6 takes; a creature spawned into a quiet dig keeps that snapshot's stats for its own
            // lifetime. Applied before EnterWorld, like every other stamp here. The Boss Rush boss leaves the
            // headcount HEALTH curve while spread scaling is on - its health is the power-sum curve above.
            ApplySpawnScaling(creature, profiles.Count, waveNumber, !(role == MlDigsiteRole.Boss && spreadSettings.Enabled));

            // Boss Rush mechanic adds: the per-SET health and speed scale, on top of the crowd scaling every
            // digsite creature already got. Applied here, before EnterWorld like every other stamp, and only
            // for the Add role - see ApplyAddScaling for why it is a second health write rather than a
            // parameter on ApplySpawnScaling.
            if (role == MlDigsiteRole.Add)
                ApplyAddScaling(encounter, creature);

            // A roster creature drawn from the island's own bestiary can carry any of the three retail
            // reasons for one monster to attack another. On a shared outdoor landblock that is worse than it
            // is in a private copy: a faction mob would pick a fight with the island's ambient wildlife
            // rather than with the players who dug it up. Cleared before EnterWorld, the same rule and the
            // same call WorldEventSpawner uses for its own roster draws.
            SpawnedCreatureHostility.MakeHostileToPlayers(creature);

            // Round 17 tester feedback: a digsite creature shares its wcid AND its name with the island's own
            // ambient wildlife, so nothing on radar told a player which blips were the fight. Stamped for
            // EVERY digsite spawn, not just a role - the wildlife it is being distinguished from is not
            // role-specific either. Before EnterWorld like every other stamp here (invariant: no CreateObject
            // packet is ever sent without it). A null tunable (ml_digsite_radar_color at or below zero) skips
            // the stamp and leaves the creature's own default (RadarColor.Creature, Gold).
            if (MlDigsiteTunables.RadarColor != null)
                creature.RadarColor = MlDigsiteTunables.RadarColor;

            if (SpawnedCreatureHostility.ReleaseImmobileWithoutRangedAttack(creature))
                log.Debug($"[ML_DIGSITE] {encounter} wcid {wcid} AiImmobile cleared: no ranged attack");

            // Priority mob visual tag (round 13 feedback item F): a script and scale bump so the Corruption
            // shape's objective reads as visibly distinct from the field around it, before EnterWorld like
            // every other stamp here so no CreateObject packet is ever sent without it. Re-broadcast on
            // every corruption meter tick (MlDigsiteManager.DriveCorruption) so a player who loses and
            // regains visual range still sees the tag.
            if (role == MlDigsiteRole.Priority)
            {
                creature.DefaultScriptId = MlDigsiteTunables.PriorityScriptId;
                creature.ObjScale = (creature.ObjScale ?? 1.0f) * MlDigsiteTunables.PriorityScaleMultiplier;

                // Round 16 redesign: in a Corruption encounter the Priority role is the "Corrupted" mob - one
                // alive at a time, tougher than the surrounding field, killed ml_digsite_corruption_kills_required
                // times to win. Name and extra health both mark it out; the script/scale bump above already did.
                if (encounter.Type == MlDigsiteType.CorruptionMeter)
                    ApplyCorruptedMarking(creature);
            }

            // Boss Rush mechanic: the boss carries ml_digsite_bossrush_mechanic on top of its authored monster
            // effects. Before EnterWorld, because the overlay allocates a fresh effect-state array and must not
            // discard state the heartbeat has already started accruing. An empty tunable means no overlay.
            //
            // AND, FOR A SET THAT RUNS AN IMMUNE MECHANIC, THE "immune" FILTER RECORD ITSELF. That record is
            // what ImmunePhasesMechanic's P_DigsiteImmune flag is read by; without it the flag is set and
            // cleared with nothing on the other end, and the phase is a chat line over a boss that takes full
            // damage throughout. It has to be composed HERE rather than attached when the threshold is
            // crossed, because ApplyMonsterEffectOverlay allocates a fresh state array and would reset every
            // other effect mid-fight. The rolled set is already attached at this point: OpenEncounter's
            // BossRush branch calls MlDigsiteBossMechanics.Roll, which attaches the state, BEFORE it places
            // the boss - deliberately, and this read is one of the two reasons it does (the other is the Add
            // scaling above).
            if (role == MlDigsiteRole.Boss)
            {
                var mechanic = MlDigsiteBossMechanicRules.ComposeBossOverlay(
                    MlDigsiteTunables.BossRushMechanic, encounter.BossMechanics?.Set);

                if (!string.IsNullOrWhiteSpace(mechanic) && !creature.ApplyMonsterEffectOverlay(mechanic))
                    log.Warn($"[ML_DIGSITE] {encounter} Boss Rush mechanic '{mechanic}' resolved to nothing on wcid {wcid}; the boss fights without it");
            }


            // The in-memory half of the two-key death check. Purely runtime, never persisted, and nulled by
            // the manager's cleanup so a creature that somehow outlives its encounter is inert.
            creature.P_DigsiteEncounter = encounter;

            // ---- adoption, then the world ----------------------------------------------------------------

            // BEFORE EnterWorld, so there is no instant at which a live encounter creature is standing in the
            // world without the encounter knowing it must destroy it. A false return means the encounter has
            // already closed its list and this creature would be orphaned - so it is destroyed here rather
            // than left standing with TimeToRot = -1 and nothing to clean it up (invariant 6).
            if (!encounter.AddHeld(creature))
            {
                log.Debug($"[ML_DIGSITE] {encounter} refused a {role} spawn: the held list is closed; destroying wcid {wcid}");
                creature.P_DigsiteEncounter = null;
                creature.Destroy();
                return null;
            }

            bool entered;

            try
            {
                entered = creature.EnterWorld();
            }
            catch (Exception ex)
            {
                log.Error($"[ML_DIGSITE] {encounter} EnterWorld threw for wcid {wcid} at {position.ToLOCString()}", ex);
                entered = false;
            }

            if (!entered)
            {
                log.Warn($"[ML_DIGSITE] {encounter} wcid {wcid} failed to enter the world at {position.ToLOCString()}");
                creature.P_DigsiteEncounter = null;
                creature.Destroy();
                return null;
            }

            if (role == MlDigsiteRole.Wave)
                encounter.TrackWaveCreature(creature);
            else if (role == MlDigsiteRole.Checkpoint)
                encounter.TrackCheckpoint(creature);
            else if (role == MlDigsiteRole.Add)
                encounter.TrackAdd(creature);
            else
                encounter.TrackObjective(creature);

            // A non-generator spawn never fires the weenie's Generation emote set - EmoteManager.OnGeneration
            // is reached only from a generator - so a named unit's intro line has to be fired by hand. Only
            // for the single-target roles: a wave of twelve would broadcast twelve intro lines at once, and
            // so would the three or four adds a Boss Rush mechanic places in one breath.
            if (role != MlDigsiteRole.Wave && role != MlDigsiteRole.Add)
            {
                try
                {
                    creature.EmoteManager.OnGeneration();
                }
                catch (Exception ex)
                {
                    log.Error($"[ML_DIGSITE] {encounter} intro emote threw for wcid {wcid} (0x{creature.Guid.Full:X8})", ex);
                }
            }

            log.Info($"[ML_DIGSITE] {encounter} spawned {role} wcid={wcid} ({entry.Value.Name}) guid=0x{creature.Guid.Full:X8} at {position.ToLOCString()}");

            return creature;
        }

        /// <summary>
        /// Round 15 content guard: warns - and deliberately does NOT fix - when a spawned creature is holding
        /// a Held caster but has no spellbook. That shape idles: a Held caster puts a monster in Magic stance,
        /// Monster_Melee.GetCombatManeuver finds no melee maneuver for it, and with no spell to cast the
        /// creature simply stands there (Tureia the Underbeat, 1003672, shipped that way until round 15).
        /// Silently swapping its weapon here would hide the content defect instead of surfacing it; the fix
        /// belongs in the weenie. The wield list is generated in the Creature constructor
        /// (Creature.cs, GenerateWieldList), so it is already populated when this runs.
        ///
        /// Never throws: a diagnostic must not cost the spawn.
        /// </summary>
        private static void WarnIfHeldCasterWithoutSpells(MlDigsiteEncounter encounter, Creature creature, MlDigsiteRole role)
        {
            try
            {
                var held = creature.GetEquippedWand();
                var hasSpells = creature.Biota.HasKnownSpell(creature.BiotaDatabaseLock);

                if (MlDigsiteRules.HeldCasterWithoutSpells(held is Caster, hasSpells))
                    log.Warn($"[ML_DIGSITE] {encounter} {role} wcid {creature.WeenieClassId} ({creature.Name}) holds Held caster wcid {held.WeenieClassId} ({held.Name}) but has no spellbook; it will enter Magic stance with nothing to cast and idle. Fix the weenie's wield list.");
            }
            catch (Exception ex)
            {
                log.Error($"[ML_DIGSITE] {encounter} held-caster check threw for wcid {creature.WeenieClassId}", ex);
            }
        }

        /// <summary>
        /// Places <paramref name="count"/> wave creatures and reports how many actually landed. A partial
        /// wave is a real outcome, not an error: an anchor against a cliff can refuse placements, and a wave
        /// of four where five were asked for is still a wave.
        /// </summary>
        public static int SpawnWave(MlDigsiteEncounter encounter, int count, Random rng, int waveNumber)
        {
            var placed = 0;

            for (var i = 0; i < count; i++)
            {
                if (!encounter.RunValid)
                    break;

                if (TrySpawn(encounter, MlDigsiteRole.Wave, rng, waveNumber) != null)
                    placed++;
            }

            if (placed < count)
                log.Warn($"[ML_DIGSITE] {encounter} wave asked for {count} and placed {placed}");

            return placed;
        }

        /// <summary>
        /// A point to stand a creature on: sampled uniformly by area inside the spawn disc about the anchor,
        /// then snapped to the terrain.
        ///
        /// The snap is NOT optional and is not tidiness. A creature spawned in mid-air FALLS
        /// (Creature_SkyDrop.cs is an entire feature built on that), and the anchor carries the digging
        /// player's Z, which is only correct where they were standing. AdjustMapCoords overwrites Z outright
        /// with the ground height under the sampled point - the same thing every other outdoor creature
        /// placement in this fork does.
        ///
        /// Returns null rather than throwing when the snap fails, so one bad point costs one creature instead
        /// of taking down the landblock tick that asked for it.
        /// </summary>
        private static Position ScatterPoint(Position anchor, Random rng)
        {
            var radius = MlDigsiteTunables.SpawnRadiusMetres;

            var sampled = WorldEventGeometry.Disc(anchor, radius, 1, rng);

            var candidate = sampled.Count > 0 ? sampled[0] : new Position(anchor);

            if (candidate.Indoors)
            {
                // A dig site is an outdoor terrain cell by construction (TreasureMapHandler refuses to read a
                // map indoors), so this is unreachable today. Refusing rather than snapping keeps it that
                // way: AdjustMapCoords has no terrain to consult for an indoor cell.
                log.Warn($"[ML_DIGSITE] refusing an indoor spawn point at {candidate.ToLOCString()}");
                return null;
            }

            try
            {
                candidate.AdjustMapCoords();
            }
            catch (Exception ex)
            {
                log.Error($"[ML_DIGSITE] terrain snap failed at {candidate.ToLOCString()}", ex);
                return null;
            }

            return candidate;
        }

        /// <summary>
        /// Confines a creature to the dig site, the same three-property recipe MlRelariaSpawner and
        /// WorldEventSpawner.ApplyBossTether use. Home is stamped from Location by WorldObject.AddPhysicsObj
        /// as the creature enters, so the tether is measured from where it was dug up with no extra write.
        ///
        /// This matters MORE here than it does for a world event, because a digsite is on the open island
        /// rather than in an arena: without it, one player can walk an encounter's whole field away from the
        /// anchor, and the audience scan that sizes and pays the fight is anchored to the hole in the ground.
        /// A non-finite or non-positive radius stamps nothing and leaves the creature behaving like any other
        /// monster, which is how the tunable is turned off.
        /// </summary>
        private static void ApplyTether(Creature creature)
        {
            var tether = MlDigsiteTunables.TetherRadius;

            if (!(tether > 0.0) || double.IsNaN(tether) || double.IsInfinity(tether))
                return;

            creature.TetherRadius = tether;
            creature.HomeRadius = tether * 2.0;
            creature.DisableSticky = true;
        }

        /// <summary>
        /// Raises a digsite creature's health and DamageRating for the participant count at its own spawn
        /// moment AND for the endless-waves wave it belongs to.
        ///
        /// Crowd scaling is the same curve/addend the Bluespire ladder's D6 crowd scaling applies
        /// (<see cref="MlDigsiteRules.CrowdHealthMultiplier"/> / <see cref="MlDigsiteRules.CrowdDamageRatingAddend"/>),
        /// reading the digsite system's OWN tunables. Per-wave scaling is
        /// <see cref="MlDigsiteRules.WaveHealthMultiplier"/> / <see cref="MlDigsiteRules.WaveDamageRatingAddend"/>,
        /// both 1.0 / 0 at wave 1. The two health multipliers MULTIPLY and are applied in ONE write, through
        /// WorldEventSpawner.ScaledStartingValue - the same shared pure engine piece BluespireCrowdScaling uses
        /// - so a digsite creature's max-health arithmetic never drifts from the ladder's. The two DamageRating
        /// addends ADD.
        ///
        /// <paramref name="applyCrowdHealth"/> is false only for the Boss Rush boss while level-spread scaling is
        /// on (RoZ round 19): its health is sized by the power-sum curve in MlMapEventScaling instead, the World
        /// Event rule. It still takes the crowd DamageRating addend, which the measured damage controller
        /// (MlDigsiteBossDamage) then corrects from real hits.
        /// </summary>
        private static void ApplySpawnScaling(Creature creature, int participants, int waveNumber, bool applyCrowdHealth)
        {
            var crowdHealth = applyCrowdHealth
                ? MlDigsiteRules.CrowdHealthMultiplier(participants, MlDigsiteTunables.CrowdPerPlayer, MlDigsiteTunables.CrowdHealthCap)
                : 1.0;

            var waveHealth = MlDigsiteRules.WaveHealthMultiplier(waveNumber,
                MlDigsiteTunables.WaveHealthPerWave, MlDigsiteTunables.WaveHealthCap);

            var healthMult = crowdHealth * waveHealth;

            if (healthMult > 1.0)
            {
                var scaled = WorldEventSpawner.ScaledStartingValue(creature.Health.StartingValue, creature.Health.MaxValue, healthMult);

                if (scaled != creature.Health.StartingValue)
                {
                    creature.Health.StartingValue = scaled;
                    creature.Health.Current = creature.Health.MaxValue;
                }
            }

            var drAddend = MlDigsiteRules.CrowdDamageRatingAddend(participants,
                MlDigsiteTunables.CrowdDrPerPlayer, MlDigsiteTunables.CrowdDrCap)
                + MlDigsiteRules.WaveDamageRatingAddend(waveNumber, MlDigsiteTunables.WaveDrPerWave, MlDigsiteTunables.WaveDrCap);

            if (drAddend != 0)
                creature.DamageRating = (creature.DamageRating ?? 0) + drAddend;
        }

        /// <summary>
        /// RoZ round 19 owner ruling M1: lowers a boss-weight creature's MagicDefense InitLevel so its
        /// current MagicDefense becomes round(authored * ml_mapevent_boss_magic_defense_scale). The only
        /// PropertyManager read for this feature; the arithmetic itself lives in
        /// MlMapEventBossMagicDefense.ScaleInitLevel, which is pure and shared with MlRelariaSpawner's own
        /// boss-prep path.
        /// </summary>
        private static void ApplyMapEventBossMagicDefenseScale(Creature creature)
        {
            var scale = Math.Clamp(PropertyManager.GetDouble("ml_mapevent_boss_magic_defense_scale", 0.90).Item, 0.0, 1.0);

            var magicDefense = creature.GetCreatureSkill(Skill.MagicDefense);

            magicDefense.InitLevel = MlMapEventBossMagicDefense.ScaleInitLevel(magicDefense.InitLevel, magicDefense.Current, scale);
        }

        /// <summary>
        /// Round 16: marks a Corruption encounter's Priority-role spawn as the "Corrupted" mob - the display
        /// name gets a "Corrupted " prefix, and its health is raised to ml_digsite_corrupted_health_multiplier
        /// times what the ordinary crowd/wave-scaled field around it has. Reuses
        /// MlDigsiteBossMechanicRules.ScaledAddHealth for the health scale - the same "scale to an absolute
        /// multiple of the current max" arithmetic ApplyAddScaling already applies to Boss Rush mechanic adds,
        /// so a second, near-identical health formula is not needed here.
        /// </summary>
        private static void ApplyCorruptedMarking(Creature creature)
        {
            creature.Name = $"Corrupted {creature.Name}";

            var mult = MlDigsiteTunables.CorruptedHealthMultiplier;

            if (Math.Abs(mult - 1.0) < 1e-9 || mult <= 0.0)
                return;

            var scaled = MlDigsiteBossMechanicRules.ScaledAddHealth(creature.Health.StartingValue, creature.Health.MaxValue, mult);

            if (scaled != creature.Health.StartingValue)
            {
                creature.Health.StartingValue = scaled;
                creature.Health.Current = creature.Health.MaxValue;
            }
        }

        /// <summary>
        /// The per-SET health and speed scale for a Boss Rush mechanic add. One shared add weenie
        /// (MlDigsiteRoster.AddEntries, owner ruling 2026-09-20) reads as "fast and weak" in the volatile sets
        /// and as "a thing you must actually kill" in the immune-phase ones, purely from
        /// ml_digsite_bossrush_sets' addhp/addspeed tokens.
        ///
        /// A SECOND health write rather than a parameter on <see cref="ApplySpawnScaling"/>, deliberately:
        /// that method's arithmetic is pinned by test against source text, and it composes crowd and per-wave
        /// scaling through WorldEventSpawner.ScaledStartingValue, which is a no-op for any multiplier at or
        /// below 1.0 - so it cannot express a scale DOWN at all. The add scale reads the creature's CURRENT
        /// MaxValue, so the two compose rather than one overwriting the other (the same argument
        /// WorldEventSpawner.ApplyHealthMultiplier makes for its own second call).
        ///
        /// SPEED IS THE RUN SKILL, NOT RunRate. Creature.RunRate is recomputed from
        /// GetCreatureSkill(Skill.Run).Current by Monster_Navigation.GetMovementSpeed on every move decision,
        /// so a value stamped at spawn would be overwritten before the add took a step. InitLevel is the
        /// persisted base Current is built from, and scaling it is exactly what
        /// ThreadDungeonSpawner already does for its own run-speed multiplier (its plan.RunSpeedMult).
        ///
        /// A missing driver state (mechanics disabled, or an add somehow placed outside a Boss Rush) leaves
        /// the weenie's own numbers alone rather than guessing a scale.
        /// </summary>
        private static void ApplyAddScaling(MlDigsiteEncounter encounter, Creature creature)
        {
            var set = encounter?.BossMechanics?.Set;

            if (set == null)
                return;

            var health = set.Value.AddHealth;

            if (Math.Abs(health - 1.0) > 1e-9)
            {
                var scaled = MlDigsiteBossMechanicRules.ScaledAddHealth(creature.Health.StartingValue, creature.Health.MaxValue, health);

                if (scaled != creature.Health.StartingValue)
                {
                    creature.Health.StartingValue = scaled;
                    creature.Health.Current = creature.Health.MaxValue;
                }
            }

            var speed = set.Value.AddSpeed;

            if (Math.Abs(speed - 1.0) > 1e-9 && speed > 0.0)
            {
                var runSkill = creature.GetCreatureSkill(Skill.Run);

                if (runSkill != null)
                    runSkill.InitLevel = (uint)Math.Clamp(Math.Round(runSkill.InitLevel * speed), 0, uint.MaxValue);
            }
        }
    }
}
