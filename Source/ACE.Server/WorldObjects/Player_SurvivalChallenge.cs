using System;
using System.Linq;

using ACE.Common;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.WorldObjects
{
    // WaffleACE survival challenge ("The Proving Grounds (Defense)"): a portal (PropertyInt.SurvivalChallengeInterval
    // > 0) drops the player into a strictly single-player ephemeral arena full of unkillable enemies (content-side
    // 100M HP). Every interval the "storm quickens" - the escalation tier increments and every arena creature flagged
    // PropertyBool.SurvivalChallengeCreature is made stronger IN RAW POWER, in place, so the escalation rides the
    // standard combat flows rather than a bolt-on damage multiplier:
    //   - its six attributes and combat skills are multiplied by rampRate (this drives accuracy via attack skill,
    //     resist penetration via magic skill, and the melee attribute-damage mod - all native formulas);
    //   - its DamageRating (PropertyInt 307) is incremented by ratingPerTier, the retail-native (100+rating)/100
    //     lever that scales BOTH melee (DamageEvent) and spell-projectile damage - the uniform damage ramp, needed
    //     because monster war-spell base damage is flat and does not scale with caster stats;
    //   - its Level is raised so appraisal shows a visibly growing monster, and the golden level-up flash fires.
    // Score = whole seconds survived from arrival until death. Death in the run is penalty-free (see Player_Death).
    // Leaving via the exit portal or logging out mid-run forfeits with no score.
    //
    // This is the defensive twin of Player_DpsChallenge.cs and mirrors it deliberately: the same run-instance
    // binding to invalidate stale ticks, the same persisted-active-flag + login-clear forfeit model, and the same
    // server-record-before-personal-best scoring order.
    partial class Player
    {
        // The combat skills whose flat InitLevel component is scaled per tier (their attribute-derived component
        // already scales because the attributes themselves are raised). Only skills actually present on a creature
        // are touched; defenses are harmless to raise (arena creatures are unkillable), offense/magic drive the ramp.
        private static readonly System.Collections.Generic.HashSet<Skill> SurvivalCombatSkills = new()
        {
            Skill.MeleeDefense, Skill.MissileDefense, Skill.MagicDefense,
            Skill.HeavyWeapons, Skill.LightWeapons, Skill.FinesseWeapons, Skill.MissileWeapons, Skill.TwoHandedCombat,
            Skill.WarMagic, Skill.VoidMagic, Skill.LifeMagic, Skill.CreatureEnchantment,
        };

        // The six primary attributes raised each tier.
        private static readonly PropertyAttribute[] SurvivalPowerAttributes =
        {
            PropertyAttribute.Strength, PropertyAttribute.Endurance, PropertyAttribute.Coordination,
            PropertyAttribute.Quickness, PropertyAttribute.Focus, PropertyAttribute.Self,
        };

        // The offensive skills whose (scaled) Current value proxies a creature's combat power when deriving its
        // displayed Level - "the current melee attack skill, or the war/void skill for casters if higher".
        private static readonly Skill[] SurvivalAttackSkills =
        {
            Skill.HeavyWeapons, Skill.LightWeapons, Skill.FinesseWeapons, Skill.MissileWeapons,
            Skill.TwoHandedCombat, Skill.UnarmedCombat, Skill.WarMagic, Skill.VoidMagic,
        };

        // Displayed-Level fit: level = SurvivalLevelSlope * effectiveAttackSkill + SurvivalLevelIntercept.
        // Fitted by ordinary least squares over retail ace_world creatures (level = weenie PropertyInt 25;
        // effective attack skill = MAX weenie_properties_skill.init_level across the offensive weapon + war/void
        // skills), restricted to level 10..250 and attack skill 20..800 to drop non-combat rows and outliers:
        //   n = 4136, slope = 0.3045, intercept = 38.06, r = 0.67.
        // Sanity: retail L140's attack skill is ~320 (bucketed avg), and fit(320) ~= 136 ~= 140. This maps the
        // creatures' scaled skills onto a retail-scale Level so appraisal reflects their true power.
        private const double SurvivalLevelSlope = 0.3045;
        private const double SurvivalLevelIntercept = 38.06;
        private const int SurvivalLevelCap = 999;

        // Upper bound applied to raised attribute/skill values, to guard against uint overflow over a pathologically
        // long run (accuracy/resist saturate far below this, so it never affects real play).
        private const uint SurvivalPowerCap = 1_000_000;

        private static uint ScaleSurvivalPower(uint value, double rampRate)
        {
            if (value == 0 || value >= SurvivalPowerCap)
                return value;

            var scaled = (uint)Math.Round(value * rampRate);
            return Math.Min(scaled, SurvivalPowerCap);
        }
        // Instance id the current run is bound to; 0 = no active run. The escalation tick guards on this (plus the
        // player's live Location.Instance) so a run that was abandoned - the player left the instance, died out of
        // it, or logged out - simply stops escalating.
        private uint survivalChallengeRunInstance;

        // Unix time (seconds) the current run started, used to compute the survived-seconds score at death.
        private double survivalChallengeStartTime;

        // The current escalation tier (0 at arrival, +1 each interval).
        private int survivalChallengeTier;

        // Captured at the start of the death sequence: true iff this death is a valid in-arena survival death.
        // The death-penalty skips key off this rather than SurvivalChallengeActive, because scoring clears the
        // active flag up front (before CalculateDeathItems / ThreadSafeTeleportOnDeath run later in the chain).
        private bool survivalChallengeDeathInProgress;

        /// <summary>
        /// Best recorded seconds survived across the player's survival-challenge runs. Persisted on the character biota.
        /// </summary>
        public long BestSurvivalScore
        {
            get => GetProperty(PropertyInt64.BestSurvivalScore) ?? 0;
            set => SetProperty(PropertyInt64.BestSurvivalScore, value);
        }

        /// <summary>
        /// True while a survival-challenge run is armed/in progress. Persisted so a mid-run logout can be caught at
        /// next login (see WorldManager.DoPlayerEnterWorld) and the player re-homed to their lifestone.
        /// </summary>
        public bool SurvivalChallengeActive
        {
            get => GetProperty(PropertyBool.SurvivalChallengeActive) ?? false;
            set => SetProperty(PropertyBool.SurvivalChallengeActive, value);
        }

        private void SurvivalChallengeMsg(string message)
        {
            Session.Network.EnqueueSend(new GameMessageSystemChat(message, ChatMessageType.System));
        }

        /// <summary>
        /// True if the run bound to <paramref name="runInstance"/> is still valid for this player: the run must be
        /// armed for that exact instance and the player must still be standing in it.
        /// </summary>
        private bool SurvivalChallengeRunValid(uint runInstance)
        {
            return runInstance != 0
                && survivalChallengeRunInstance == runInstance
                && CurrentLandblock != null
                && Location != null
                && Location.Instance == runInstance;
        }

        /// <summary>
        /// Starts the survival-challenge run. Called on arrival in the arena instance (from Portal.ActOnUse's
        /// teleport-completion follow-up), so the survived-seconds clock effectively begins when the player lands.
        /// Binds the run to the player's current instance and schedules the first escalation tier.
        /// </summary>
        public void StartSurvivalChallenge(int intervalSeconds, double rampRate, int ratingPerTier)
        {
            // Mule (WaffleACE): a mule cannot fight, so it has no business in a Proving Grounds run.
            if (MuleBlocked(MuleAction.StartChallenge))
                return;

            if (Location == null || intervalSeconds <= 0)
                return;

            var runInstance = Location.Instance;
            survivalChallengeRunInstance = runInstance;
            survivalChallengeStartTime = Time.GetUnixTime();
            survivalChallengeTier = 0;
            SurvivalChallengeActive = true;
            RushNextPlayerSave(5);

            SurvivalChallengeMsg("The storm gathers. Survive as long as you can!");

            ScheduleSurvivalTier(runInstance, intervalSeconds, rampRate, ratingPerTier);
            ScheduleSurvivalElapsedPrompt(runInstance, 0);
        }

        // Cadence (seconds) of the elapsed-time survival prompts. Deliberately independent of the escalation
        // interval so the "seconds stood" feedback stays at a steady 30s beat regardless of how the storm is
        // configured to quicken.
        private const int SurvivalElapsedPromptInterval = 30;

        /// <summary>
        /// Schedules the next elapsed-time prompt via one self-rescheduling ActionChain, running alongside (and
        /// independently of) the escalation tick. Every <see cref="SurvivalElapsedPromptInterval"/> seconds it tells
        /// the player how long they have stood, then reschedules itself; the loop terminates the instant the run is
        /// no longer valid (the player left, died, or logged out), guarded by the same run-instance binding as the
        /// escalation tick so a stale run after re-entry never fires. <paramref name="elapsedSeconds"/> is the
        /// survived-seconds total already announced (0 at run start), carried forward so no wall-clock read is needed.
        /// </summary>
        private void ScheduleSurvivalElapsedPrompt(uint runInstance, int elapsedSeconds)
        {
            var chain = new ActionChain();
            chain.AddDelaySeconds(SurvivalElapsedPromptInterval);
            chain.AddAction(this, () =>
            {
                if (!SurvivalChallengeRunValid(runInstance))
                    return;

                var totalSeconds = elapsedSeconds + SurvivalElapsedPromptInterval;

                SurvivalChallengeMsg($"You have stood against the Squall for {totalSeconds:N0} seconds.");

                ScheduleSurvivalElapsedPrompt(runInstance, totalSeconds);
            });
            chain.EnqueueChain();
        }

        /// <summary>
        /// Schedules the next escalation tier via one self-rescheduling ActionChain. Each tick increments the tier,
        /// warns the player, and raises every arena creature's raw power - attributes, combat skills, DamageRating
        /// and Level - in place, then flashes the classic level-up effect on each; the loop terminates the instant
        /// the run is no longer valid (the player left, died, or logged out).
        /// </summary>
        private void ScheduleSurvivalTier(uint runInstance, int intervalSeconds, double rampRate, int ratingPerTier)
        {
            var chain = new ActionChain();
            chain.AddDelaySeconds(intervalSeconds);
            chain.AddAction(this, () =>
            {
                if (!SurvivalChallengeRunValid(runInstance))
                    return;

                survivalChallengeTier++;

                SurvivalChallengeMsg($"The storm quickens. [Tier {survivalChallengeTier}]");

                // Strengthen every arena creature in this landblock in raw power and flash the golden level-up
                // effect so the player sees the Squall strengthen. These are ephemeral-instance creatures, so the
                // in-place mutations are purely in-memory - no persistence concern. Attribute/skill values are read
                // fresh by the combat formulas on the next swing/cast, so no cache invalidation is needed.
                foreach (var creature in CurrentLandblock.GetAllWorldObjectsForDiagnostics().OfType<Creature>())
                {
                    if (creature.GetProperty(PropertyBool.SurvivalChallengeCreature) != true)
                        continue;

                    RaiseSurvivalCreaturePower(creature, rampRate, ratingPerTier);

                    // classic golden level-up particle flash (the effect players get on level-up)
                    creature.EnqueueBroadcast(new GameMessageScript(creature.Guid, PlayScript.LevelUp));
                }

                ScheduleSurvivalTier(runInstance, intervalSeconds, rampRate, ratingPerTier);
            });
            chain.EnqueueChain();
        }

        /// <summary>
        /// Raises one arena creature's raw power by one tier: multiplies its six attributes (StartingValue) and its
        /// combat skills' flat InitLevel by <paramref name="rampRate"/>, adds <paramref name="ratingPerTier"/> to its
        /// DamageRating, and bumps its Level. All standard combat flows read these live, so accuracy, resist
        /// penetration, the melee attribute-damage mod, and (via DamageRating) both melee and spell damage escalate
        /// through the native formulas.
        /// </summary>
        private static void RaiseSurvivalCreaturePower(Creature creature, double rampRate, int ratingPerTier)
        {
            // attributes (StartingValue == the biota InitLevel; Base/Current derive from it on read)
            foreach (var attr in SurvivalPowerAttributes)
            {
                if (creature.Attributes.TryGetValue(attr, out var attribute))
                    attribute.StartingValue = ScaleSurvivalPower(attribute.StartingValue, rampRate);
            }

            // combat skills' flat InitLevel component (the attribute-derived component already scaled above)
            foreach (var skill in SurvivalCombatSkills)
            {
                var creatureSkill = creature.GetCreatureSkill(skill, false);
                if (creatureSkill != null && creatureSkill.InitLevel > 0)
                    creatureSkill.InitLevel = ScaleSurvivalPower(creatureSkill.InitLevel, rampRate);
            }

            // the uniform damage lever: DamageRating feeds (100+rating)/100 in both the melee and spell paths
            creature.DamageRating = (creature.DamageRating ?? 0) + ratingPerTier;

            // presentation: derive the displayed Level from the just-scaled stats (via the retail-fitted skill->level
            // map) so appraisal reflects true power, rather than scaling Level directly (which read far too low).
            // effectiveSkill = the highest Current across the creature's offensive skills (melee, or war/void if
            // higher). Never decreases the shown level, and is capped.
            uint effectiveSkill = 0;
            foreach (var skill in SurvivalAttackSkills)
            {
                var creatureSkill = creature.GetCreatureSkill(skill, false);
                if (creatureSkill != null)
                    effectiveSkill = Math.Max(effectiveSkill, creatureSkill.Current);
            }

            var currentLevel = creature.Level ?? 1;
            var fitLevel = (int)Math.Round(SurvivalLevelSlope * effectiveSkill + SurvivalLevelIntercept);
            creature.Level = Math.Max(currentLevel, Math.Min(fitLevel, SurvivalLevelCap));
        }

        /// <summary>
        /// True iff this player is dying inside a valid, active survival run - the gate for scoring the run and
        /// waiving the normal death penalties. Requires the persisted active flag AND that the player is still in
        /// the bound run instance, so a stale flag (e.g. after some non-portal exit) cannot turn an ordinary death
        /// into a penalty-free one.
        /// </summary>
        private bool IsSurvivalChallengeDeath()
        {
            return SurvivalChallengeActive
                && survivalChallengeRunInstance != 0
                && Location != null
                && Location.Instance == survivalChallengeRunInstance;
        }

        /// <summary>
        /// Scores an in-arena survival death: announces the survived seconds, updates the personal best, and - if it
        /// beats every player's best - broadcasts a new server record. Called from Player.Die() BEFORE the death
        /// penalties are computed, so the server-record check reads the pre-update maxima (letting the reigning
        /// record-holder beat their own record). Consumes the run so nothing can re-score it.
        /// </summary>
        private void FinishSurvivalChallenge()
        {
            var score = (long)Math.Floor(Time.GetUnixTime() - survivalChallengeStartTime);
            if (score < 0)
                score = 0;

            SurvivalChallengeMsg($"You weathered the storm for {score:N0} seconds.");

            // Server-record check: read the current max across ALL players BEFORE persisting this player's new best
            // (same online+offline scan the /top command uses). Doing it first is what lets the reigning
            // record-holder beat their own record - their stored best is still the old value here.
            var currentServerMax = PlayerManager.GetAllPlayers()
                .Select(p => p.GetProperty(PropertyInt64.BestSurvivalScore) ?? 0)
                .DefaultIfEmpty(0)
                .Max();

            if (score > 0 && score > currentServerMax)
            {
                PlayerManager.BroadcastToAll(new GameMessageSystemChat(
                    $"[The Proving Grounds] {Name} has weathered the storm for {score:N0} seconds - a new record!",
                    ChatMessageType.WorldBroadcast));
            }

            if (score > BestSurvivalScore)
            {
                BestSurvivalScore = score;
                SurvivalChallengeMsg($"New personal best: {score:N0} seconds survived!");
            }

            // consume the run (the death-penalty skips continue to key off survivalChallengeDeathInProgress)
            survivalChallengeRunInstance = 0;
            SurvivalChallengeActive = false;
            RushNextPlayerSave(5);
        }

        /// <summary>
        /// Death-sequence entry point (called from Player.Die()). If this is a valid in-arena survival death, it
        /// scores the run immediately and returns true so the caller waives the death penalties for the rest of the
        /// sequence; otherwise it does nothing and returns false.
        /// </summary>
        public bool TryBeginSurvivalChallengeDeath()
        {
            survivalChallengeDeathInProgress = IsSurvivalChallengeDeath();

            if (survivalChallengeDeathInProgress)
                FinishSurvivalChallenge();

            return survivalChallengeDeathInProgress;
        }

        /// <summary>
        /// Exit-portal finish (called from Portal.ActOnUse when the in-arena exit portal is used mid-run). Using the
        /// exit portal is a SCORED finish, exactly like death: it records the seconds survived, updates the personal
        /// best, and fires the personal-best / server-record announcements - but with no death penalties, since
        /// nobody died. Marks the run finished (SurvivalChallengeActive cleared) BEFORE the portal's teleport runs,
        /// so the subsequent OnTeleportComplete instance-exit check sees an already-finished run and neither
        /// double-scores nor forfeits. No-ops for a non-survival exit portal, or if the player is not in their run.
        /// </summary>
        public void FinishSurvivalChallengeAtExit()
        {
            if (IsInSurvivalChallengeInstance)
                FinishSurvivalChallenge();
        }

        /// <summary>
        /// True while the current death sequence is a survival death - read by the various death-penalty skips in
        /// Player_Death.cs (vitae, enchantment purge, death items, teleport destination).
        /// </summary>
        public bool SurvivalChallengeDeathInProgress => survivalChallengeDeathInProgress;

        /// <summary>
        /// Clears the survival-death marker once the death sequence has fully completed.
        /// </summary>
        public void EndSurvivalChallengeDeath()
        {
            survivalChallengeDeathInProgress = false;
        }

        /// <summary>
        /// Strips rare-gem buffs from the player. Called on arrival through a Proving Grounds portal
        /// (PortalBlocksRareGems).
        /// <para/>
        /// THE RULE: every "Prodigal" spell is stripped; every Incantation and every "Aura of Incantation" spell
        /// survives. That is exactly the buffs a player could not have cast on themselves.
        /// <para/>
        /// The rare-gem spell-id set (WorldDatabaseWithEntityCache.GetRareGemSpellIds) is only the broad CANDIDATE
        /// set - every spell on a Gem-type weenie carrying a RareId, 138 ids in ace_world. Roughly half of those
        /// are the ordinary player-castable level-8 line (the Incantations and Auras), which ace_world ships Scroll
        /// weenies for, so matching on the candidate set ALONE destroyed the player's own self-buffs on arrival.
        /// <see cref="RareGemSpells.GetStrippableSpellIds"/> narrows the candidates to the ones with a rare
        /// SpellCategory in the client dat; a direct probe of all 138 showed that split is exact in both
        /// directions - all 67 Prodigal spells have a rare category, and zero of the other 71 do. Corroboration:
        /// 0 of the 67 Prodigal spells are teachable by any Scroll weenie, against 64 of 65 Incantations and 4 of
        /// 6 Auras.
        /// <para/>
        /// Because a player can never self-cast a Prodigal spell, id membership alone is sufficient AND complete -
        /// there is deliberately no test on the enchantment's caster here. A caster test would in fact be WRONG
        /// for the item-redirected half of the gem line: a Prodigal Bane goes through
        /// TryCastItemEnchantment_WithRedirects, which drops the itemCaster and records the PLAYER as caster, so
        /// a "not self-cast" filter would let exactly those Prodigal buffs survive.
        /// <para/>
        /// The one condition that stays is Duration != -1 on the equipped-item pass - see below.
        /// </summary>
        public void StripRareGemBuffs()
        {
            var strippableSpellIds = ACE.Server.Entity.RareGemSpells.GetStrippableSpellIds();
            if (strippableSpellIds.Count == 0)
                return;

            // (1) buffs that landed on the player's own enchantment registry
            var toDispel = Biota.PropertiesEnchantmentRegistry.Clone(BiotaDatabaseLock)
                .Where(e => strippableSpellIds.Contains((uint)e.SpellId))
                .ToList();

            if (toDispel.Count > 0)
                EnchantmentManager.Dispel(toDispel);

            // (2) item-redirectable rare-gem buffs (the Prodigal Impen/Bane family) land on the EQUIPPED ITEM's
            // registry, not the player's (see Gem.cs item-redirect handling), so scan each equipped item too.
            // One extra condition beyond the spell-id match: Duration == -1 marks an equip-sourced aura - the
            // item's OWN permanent spells, written with Duration -1 / StartTime 0 (EnchantmentManager.cs:231-236)
            // - which are part of the item and must never be stripped. A rare item whose own spellbook contains a
            // Prodigal spell therefore keeps its innate aura when the player walks through the portal.
            foreach (var item in EquippedObjects.Values)
            {
                var itemBuffs = item.Biota.PropertiesEnchantmentRegistry.Clone(item.BiotaDatabaseLock)
                    .Where(e => strippableSpellIds.Contains((uint)e.SpellId) && e.Duration != -1)
                    .ToList();

                foreach (var entry in itemBuffs)
                    item.EnchantmentManager.Remove(entry);
            }
        }

        /// <summary>
        /// True while the player is armed for a survival run AND still standing in the bound run instance. Used to
        /// gate the jump tax the same way the death path is gated, so a stale flag (after some non-portal exit that
        /// has not yet been reconciled) does not keep taxing jumps outside the arena.
        /// </summary>
        public bool IsInSurvivalChallengeInstance =>
            SurvivalChallengeActive
            && survivalChallengeRunInstance != 0
            && Location != null
            && Location.Instance == survivalChallengeRunInstance;

        /// <summary>
        /// Reconciles the persisted run flag against the player's current instance after a teleport completes. Only
        /// death, the exit portal, and login-clear reset SurvivalChallengeActive; every OTHER way out of the arena
        /// (lifestone/portal recall, /hometown, an admin teleport) leaves the flag set. Called from
        /// OnTeleportComplete: if the player is survival-active but no longer in their bound run instance, forfeit
        /// the run so the persisted state (and the jump tax) stop applying. Because the arrival-arming callback
        /// (StartSurvivalChallenge) runs before the client's teleport-complete ack, a genuine arena arrival already
        /// has survivalChallengeRunInstance == Location.Instance here and is left untouched; a flag left set by a
        /// StartSurvivalChallenge that no-opped (run instance still 0) is cleared silently.
        /// </summary>
        public void CheckSurvivalChallengeInstanceExit()
        {
            if (!SurvivalChallengeActive)
                return;

            // still inside the bound, started run - nothing to do
            if (survivalChallengeRunInstance != 0 && Location != null && Location.Instance == survivalChallengeRunInstance)
                return;

            // a started run whose instance we have now left - tell the player they gave it up
            if (survivalChallengeRunInstance != 0)
                SurvivalChallengeMsg("You have abandoned the trial.");

            ForfeitSurvivalChallenge();
        }

        /// <summary>
        /// Forfeit: ends an in-progress survival run with no score - used when the player leaves the arena via the
        /// exit portal, or when a teleport moves them out of the arena by any other means. The escalation loop already
        /// self-terminates via SurvivalChallengeRunValid; this additionally clears the persisted active flag so the
        /// jump tax and death-path scoring stop applying the moment the player leaves. A mid-run logout is handled
        /// instead by the login clear in WorldManager (mirrors DPS).
        /// </summary>
        public void ForfeitSurvivalChallenge()
        {
            if (!SurvivalChallengeActive)
                return;

            survivalChallengeRunInstance = 0;
            SurvivalChallengeActive = false;
            RushNextPlayerSave(5);
        }
    }
}
