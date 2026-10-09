using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Common;
using ACE.Database;
using ACE.Database.Models.World;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.MlTreasure;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.WorldObjects
{
    partial class Creature
    {
        /// <summary>
        /// The authored level to use for loot gating when a spawner RAISED <see cref="WorldObject.Level"/>
        /// (null for every creature that was never raised). Per instance and never persisted, in the same style as
        /// <see cref="DefenseSkillCeilings"/>. Set only by the World Events band uplift
        /// (WorldEventSpawner.ApplyBandUplift), to the level the creature had BEFORE the uplift; Threads never sets
        /// it and keeps its own loot handling. Read through <see cref="LootGateLevelFor"/> by the rare-drop
        /// eligibility check in CreateCorpse, which recomputes CanGenerateRare from the level at death, so
        /// presetting that flag would not work.
        /// </summary>
        public int? LootGateLevel;

        /// <summary>
        /// Threads (WaffleACE): DeathTreasureOverride, when set, replaces the weenie's DeathTreasureType
        /// for this one creature (PLAN 7.2). It is an in-memory TreasureDeath the spawner built for the run, so it
        /// is checked first and short-circuits the world-database lookup entirely.
        /// </summary>
        public TreasureDeath DeathTreasure
        {
            get
            {
                if (DeathTreasureOverride != null)
                    return DeathTreasureOverride;

                return DeathTreasureType.HasValue ? DatabaseManager.World.GetCachedDeathTreasure(DeathTreasureType.Value) : null;
            }
        }

        private bool onDeathEntered = false;

        /// <summary>
        /// Called when a monster or player dies, in conjunction with Die()
        /// </summary>
        /// <param name="lastDamager">The last damager that landed the death blow</param>
        /// <param name="damageType">The damage type for the death message</param>
        /// <param name="criticalHit">True if the death blow was a critical hit, generates a critical death message</param>
        public virtual DeathMessage OnDeath(DamageHistoryInfo lastDamager, DamageType damageType, bool criticalHit = false)
        {
            if (onDeathEntered)
                return GetDeathMessage(lastDamager, damageType, criticalHit);

            onDeathEntered = true;

            IsTurning = false;
            IsMoving = false;

            // puzzle-gate ambush (WaffleACE): a creature a wrong lever pull summoned grants nothing. Everything
            // below this line is an on-kill reward or a consequence the weenie authored - killer class-ability /
            // weapon-mod / vessel / mule-token hooks, the three kill-task slots, the death spawn, and XP plus
            // luminance (which EarnXP then shares to fellows and passes up the allegiance) - so returning here
            // closes all of them at once. Die() reads the same predicate for the corpse and the rest.
            if (IsRewardlessDeath(IsPuzzleAmbush))
                return GetDeathMessage(lastDamager, damageType, criticalHit);

            // Attack/Defend crystals (Docs/Pvp/ATTACK-DEFEND.md "Death"): a match objective is not a kill. It skips the killer
            // hooks (class abilities, weapon mods, kill-fill vessels, mule tokens), the kill quests, the death spawn and the XP
            // grant below; only the death message is kept. Untagged creatures answer false and take the full path unchanged.
            if (IsBattlegroundObjectiveCorpselessDeath(BattlegroundObjective))
                return GetDeathMessage(lastDamager, damageType, criticalHit);

            // class abilities that react to landing a killing blow (e.g. Nether Bloom). The onDeathEntered
            // guard above makes this fire exactly once per death; the Player dispatch owns the shared
            // preconditions (system enabled, PvP/pet/self exclusion).
            if (lastDamager?.TryGetAttacker() is Player killingPlayer)
            {
                killingPlayer.ApplyCreatureDeathClassAbilities(this);

                // Tier B weapon mods: Second Wind restores a fraction of the killer's maximum vitals. Same
                // once-per-death guarantee from onDeathEntered above, and the Player-side method owns the
                // shared preconditions (gate, PvP/pet/self exclusion). See Player_WeaponMods.cs.
                killingPlayer.ApplyWeaponModCreatureDeath(this);

                // Kill-fill vessels: a vessel in the killer's pack gains a charge. Same once-per-death
                // guarantee from onDeathEntered above, and the Player-side method owns the shared
                // preconditions. THE KILLING BLOW IS THE POINT: killingPlayer comes from
                // DamageHistory.LastDamager, not TopDamager and not the damage list, so a fellow who did
                // more damage gets nothing. That exclusion is the mechanic. See Player_KillFillVessel.cs.
                killingPlayer.ApplyKillFillVesselCreatureDeath(this);

                // Mule form tokens: an attuned token in the killer's pack records one kill toward a
                // mule appearance. Same once-per-death guarantee from onDeathEntered above, the same
                // killing-blow-only rule as the vessel line just above (killingPlayer is
                // DamageHistory.LastDamager, never TopDamager), and the Player-side method owns the
                // shared preconditions. Matches on WeenieClassId, not CreatureType. See
                // Player_MuleFormToken.cs.
                killingPlayer.ApplyMuleFormTokenCreatureDeath(this);
            }

            //QuestManager.OnDeath(lastDamager?.TryGetAttacker());

            if (KillQuest != null)
                OnDeath_HandleKillTask(KillQuest, isPrimarySlot: true);
            if (KillQuest2 != null)
                OnDeath_HandleKillTask(KillQuest2, isPrimarySlot: false);
            if (KillQuest3 != null)
                OnDeath_HandleKillTask(KillQuest3, isPrimarySlot: false);

            // Death spawn (PropertyInt 9070 DeathSpawnWcid): the generic "adds on death" primitive, and the
            // mechanism behind the Bluespire rung 5 shell swap - the first form dies here and the real boss
            // steps out. Beside the KillQuest block because this is the same kind of thing: a consequence
            // the dying weenie itself authored. The onDeathEntered latch above makes it exactly once per
            // death, and TrySpawn short-circuits after ONE property read for every creature that authors
            // none - it reads DeathSpawnWcid first and returns before touching count or radius.
            ACE.Server.Entity.DeathSpawner.TrySpawn(this);

            if (!IsOnNoDeathXPLandblock)
                OnDeath_GrantXP();

            return GetDeathMessage(lastDamager, damageType, criticalHit);
        }


        public DeathMessage GetDeathMessage(DamageHistoryInfo lastDamagerInfo, DamageType damageType, bool criticalHit = false)
        {
            var lastDamager = lastDamagerInfo?.TryGetAttacker();

            if (lastDamagerInfo == null || lastDamagerInfo.Guid == Guid || lastDamager is Hotspot || lastDamager is Food)   // !(lastDamager is Creature)?
                return Strings.General[1];

            var deathMessage = Strings.GetDeathMessage(damageType, criticalHit);

            // if killed by a player, send them a message
            if (lastDamagerInfo.IsPlayer)
            {
                if (criticalHit && this is Player)
                    deathMessage = Strings.PKCritical[0];

                var killerMsg = string.Format(deathMessage.Killer, Name);

                if (lastDamager is Player playerKiller)
                    playerKiller.Session.Network.EnqueueSend(new GameEventKillerNotification(playerKiller.Session, killerMsg));
            }
            return deathMessage;
        }

        /// <summary>
        /// Kills a player/creature and performs the full death sequence
        /// </summary>
        public void Die()
        {
            Die(DamageHistory.LastDamager, DamageHistory.TopDamager);
        }

        private bool dieEntered = false;

        /// <summary>
        /// Performs the full death sequence for non-Player creatures
        /// </summary>
        protected virtual void Die(DamageHistoryInfo lastDamager, DamageHistoryInfo topDamager)
        {
            if (dieEntered) return;

            dieEntered = true;

            // puzzle-gate ambush (WaffleACE): decided once, consumed by the corpse step and the reward-bearing
            // hooks below (see IsRewardlessDeath). The run / event / encounter hooks need back-references only
            // their own spawners set, so they are unreachable for an ambush creature and need no guard.
            var rewardless = IsRewardlessDeath(IsPuzzleAmbush);

            // wave challenge (WaffleACE): a wave-gauntlet creature reports its death to the run that spawned it,
            // which advances the gauntlet once every creature in the live wave is down. Sits after the
            // exactly-once dieEntered guard so a wave creature can never be counted twice.
            if (GetProperty(PropertyBool.WaveChallengeCreature) == true)
                P_WaveOwner?.OnWaveCreatureDied(this);

            // world events (WaffleACE): a creature a running world event spawned reports its death to that
            // run, which is what advances the objective, the alive count and the MVP ledger. Sits after the
            // exactly-once dieEntered guard for the same reason the wave hook does, and requires BOTH the
            // in-memory back-reference and the persisted stamp so a creature from a finished run is inert.
            if (P_WorldEvent != null && GetProperty(PropertyInt.WorldEventId) != null)
                ACE.Server.WorldEvents.WorldEventManager.OnEventCreatureDied(this, lastDamager, topDamager);

            // Threads pooled loot (Docs/Threads/POOLED-LOOT-CACHE-DESIGN.md sections 2-3): a creature of a pooled
            // run leaves no corpse and banks its loot on the run instead. Banked HERE, before the run kill hook just
            // below, so the kill that clears the run is already in the ledger when AnnounceCleared asks for
            // delivery. Decided once and captured by the corpse step at the end of this method.
            var pooledLoot = IsPooledLootDeath(P_DungeonRun, GetProperty(PropertyInt.ThreadDungeonRunId));

            // ML digsite encounters (WaffleACE): an encounter's creatures leave no corpse either. A SECOND
            // predicate OR-ed into the SAME local, so the corpse step at the end of this method stays the one
            // and only consumer - see IsDigsiteCorpselessDeath's own doc comment for why the engine's
            // corpse-suppression property is the wrong tool here and only skipping CreateCorpse works. Only
            // the corpse decision is shared; the pooled bank below stays keyed on pooledLoot alone, because
            // a digsite banks nothing.
            //
            // The predicate is named "Corpseless" on purpose. PooledLootWiringTests scans THIS method body
            // and asserts it never names that engine property, because naming it is how someone talks
            // themselves into using it. The explanation lives once, on the predicate; do not restate it here.
            // Attack/Defend crystals (Docs/Pvp/ATTACK-DEFEND.md "Death"): another predicate OR-ed into the same local. A tagged
            // crystal leaves no corpse and no treasure, and reports its destruction to the match exactly once (this point is
            // past the dieEntered guard). Untagged creatures answer false and are unchanged.
            var objectiveDeath = IsBattlegroundObjectiveCorpselessDeath(BattlegroundObjective);

            if (objectiveDeath)
                ReportBattlegroundObjectiveDestroyed(lastDamager);

            var suppressCorpse = pooledLoot || IsDigsiteCorpselessDeath(P_DigsiteEncounter, GetProperty(PropertyInt.MlDigsiteEncounterId)) || rewardless || objectiveDeath;

            // Guarded: a throw here (the rare roll creates a world object) must not abort Die() after dieEntered is
            // set, or the kill is never recorded and the creature is never destroyed. pooledLoot stays true, so the
            // corpse is still skipped and a failed bank loses its loot.
            if (pooledLoot)
            {
                try
                {
                    ACE.Server.ThreadDungeons.ThreadLootPool.BankKill(this, topDamager);
                }
                catch (Exception ex)
                {
                    log.Error($"[DYNDUNGEON] pooled bank failed for {Name} (0x{Guid}, wcid {WeenieClassId}); its loot is lost", ex);
                }
            }

            // Threads (WaffleACE): a run-owned creature reports its death to the run, which is what
            // advances the clear. Same post-dieEntered slot and the same two-key check as the world-event hook:
            // the in-memory back-reference AND the persisted stamp, so a creature from a dead run is inert.
            if (P_DungeonRun != null && GetProperty(PropertyInt.ThreadDungeonRunId) != null)
                ACE.Server.ThreadDungeons.ThreadDungeonManager.OnRunCreatureDied(this, lastDamager, topDamager);

            // ML digsite encounters (WaffleACE): a creature a dug-up encounter spawned reports its death to
            // that encounter, which is what clears a wave and what wins the fight when the death is the
            // objective's. Same post-dieEntered slot and the same TWO-KEY check as the hooks above - the
            // in-memory back-reference AND the persisted stamp - which here is not merely convention: the
            // digsite roster draws ORDINARY ISLAND WILDLIFE, so a wcid test would fire for every wild Carenzi
            // on Marae Lassel. Only a creature this encounter actually placed carries both keys.
            if (P_DigsiteEncounter != null && GetProperty(PropertyInt.MlDigsiteEncounterId) != null)
                ACE.Server.MlDigsite.MlDigsiteManager.OnEncounterCreatureDied(this);

            // Wave encounters (WaffleACE): a creature an object-anchored wave encounter spawned reports its
            // death to that encounter. Same post-dieEntered slot and the same TWO-KEY check as the digsite hook
            // above - the in-memory back-reference AND the persisted stamp - because the rosters draw ORDINARY
            // dungeon creatures (the D6 Tumeroks), so a wcid test would also fire for any hand-placed copy.
            // The hook only records: the next wave, the end and all cleanup run on the world-thread tick.
            if (P_WaveEncounter != null && GetProperty(PropertyInt.WaveEncounterId) != null)
                ACE.Server.WaveEncounters.WaveEncounterManager.OnEncounterCreatureDied(this);

            // speed challenge (WaffleACE): a boss flagged PropertyBool.SpeedChallengeBoss reports its death to
            // the runner, which is one of the two ways a timed Proving Grounds run finishes
            // (Docs/ProvingGroundsSpeed/DESIGN.md section 3.4). Sits in the same post-dieEntered cluster as the
            // two hooks above, for the same reason: dieEntered is what makes a death report exactly once.
            //
            // The one real divergence from the wave hook is how the player is found. Wave reads P_WaveOwner, an
            // in-memory back-reference stamped when the RUN spawned the creature (Player_WaveChallenge.cs:339).
            // A speed-dungeon boss is pre-placed content inside the ephemeral instance - nothing ever spawns it
            // on behalf of a run - so no back-reference exists to read and the runner must come from the damage
            // history instead. Top damager first, last damager as the fallback, resolved through
            // TryGetPetOwnerOrAttacker (not TryGetAttacker) so a kill landed by the runner's combat pet still
            // credits the runner - the same resolution WorldEventParticipation.ResolvePlayer uses
            // (WorldEventParticipation.cs:258-261), and it degrades to TryGetAttacker whenever no pet is involved.
            //
            // Resolving the WRONG player is not a scoring hazard: TryFinishSpeedChallenge re-validates through
            // IsSpeedRunValid (Player_SpeedChallenge.cs:86-92), which requires that player to have a run armed
            // AND to be standing in the exact ephemeral instance their own run is bound to, and it then gates
            // this creature's wcid against the season's declared ObjectiveWcid. A player who is not on a run in
            // this instance simply no-ops.
            if (!rewardless && GetProperty(PropertyBool.SpeedChallengeBoss) == true)
            {
                var speedRunner = topDamager?.TryGetPetOwnerOrAttacker() as Player
                    ?? lastDamager?.TryGetPetOwnerOrAttacker() as Player;

                if (speedRunner != null)
                    speedRunner.OnSpeedChallengeBossDied(this);
                else
                    log.Warn($"[SPEED] {Name} (0x{Guid}, wcid {WeenieClassId}) is flagged SpeedChallengeBoss but died with no resolvable player attacker - no run can be finished. Damage history holds no live player (killed by the environment, a non-player creature, or the killer logged out).");
            }

            // objective locks (WaffleACE): a creature carrying PropertyString.ObjectiveLockKey is a
            // CONTRIBUTOR to a count-based puzzle gate - this is the "the door opens when every creature in
            // the room is dead" case (see WorldObject_Objective.cs). Sits in the same post-dieEntered
            // cluster as the three hooks above, for the same reason they do: dieEntered is what makes a
            // death report exactly once, and here that matters for the wrong-answer path in particular -
            // ObjectiveLock absorbs a duplicate CONTRIBUTION (the same token key just replaces itself), but
            // a duplicate call on a creature flagged ObjectiveLockResets would wipe the room's progress a
            // second time.
            //
            // CurrentLandblock is NOT checked here and is not assumed to be non-null: nothing in Die()
            // reads it, so nothing in this path establishes it. ContributeToObjectiveLock does the
            // null-check and logs, which keeps that judgement in one place shared with the activation call
            // site.
            if (!rewardless && ObjectiveLockKey != null)
            {
                // Resolved only to address the progress message; the contribution itself needs no player.
                // Same resolution the speed hook above uses - top damager first, last damager as the
                // fallback, through TryGetPetOwnerOrAttacker so a kill landed by a combat pet still talks
                // to the pet's owner. A null result simply means nobody is told.
                var objectiveKiller = topDamager?.TryGetPetOwnerOrAttacker() as Player
                    ?? lastDamager?.TryGetPetOwnerOrAttacker() as Player;

                ContributeToObjectiveLock(objectiveKiller);
            }

            UpdateVital(Health, 0);

            if (topDamager != null)
            {
                KillerId = topDamager.Guid.Full;

                if (topDamager.IsPlayer && !rewardless)
                {
                    var topDamagerPlayer = topDamager.TryGetAttacker();

                    // A match objective is not a creature kill either (Docs/Pvp/ATTACK-DEFEND.md "Death").
                    if (topDamagerPlayer != null && !objectiveDeath)
                        topDamagerPlayer.CreatureKills = (topDamagerPlayer.CreatureKills ?? 0) + 1;
                }
            }

            CurrentMotionState = new Motion(MotionStance.NonCombat, MotionCommand.Ready);
            //IsMonster = false;

            PhysicsObj.StopCompletely(true);

            // broadcast death animation
            var motionDeath = new Motion(MotionStance.NonCombat, MotionCommand.Dead);
            var deathAnimLength = ExecuteMotion(motionDeath);

            // A Death emote can give items, XP or quest stamps; an ambush creature's never runs.
            if (!rewardless)
                EmoteManager.OnDeath(lastDamager);

            var dieChain = new ActionChain();

            // wait for death animation to finish
            //var deathAnimLength = DatManager.PortalDat.ReadFromDat<MotionTable>(MotionTableId).GetAnimationLength(MotionCommand.Dead);
            dieChain.AddDelaySeconds(deathAnimLength);

            dieChain.AddAction(this, () =>
            {
                // Suppressed corpse: no corpse, and therefore no GenerateTreasure. THE ONE call site, fed by
                // the ONE local decided at the top of this method, which ORs together the Threads pooled-loot
                // rule (the kill was banked above), the ML digsite rule (the encounter's chest is the
                // reward), the puzzle-gate ambush rule (a free summon pays nothing) and the Attack/Defend crystal
                // rule (a match objective is not a kill). Destroy() still waits
                // out the death animation exactly as before.
                if (!suppressCorpse)
                    CreateCorpse(topDamager);

                Destroy();
            });

            dieChain.EnqueueChain();
        }

        /// <summary>
        /// Called when an admin player uses the /smite command
        /// to instantly kill a creature
        /// </summary>
        public void Smite(WorldObject smiter, bool useTakeDamage = false)
        {
            if (useTakeDamage)
            {
                // deal remaining damage
                TakeDamage(smiter, DamageType.Bludgeon, Health.Current);
            }
            else
            {
                OnDeath();
                var smiterInfo = new DamageHistoryInfo(smiter);
                Die(smiterInfo, smiterInfo);
            }
        }

        public void OnDeath()
        {
            OnDeath(null, DamageType.Undef);
        }

        /// <summary>
        /// Grants XP to players in damage history
        /// </summary>
        public void OnDeath_GrantXP()
        {
            if (this is Player && PlayerKillerStatus == PlayerKillerStatus.PKLite)
                return;

            var totalHealth = DamageHistory.TotalHealth;

            if (totalHealth == 0)
                return;

            // Aggregate damage per awarded player first, so a player and their own pet(s) count as a
            // single contributor. Otherwise the owner could collect XP on their own damage slice AND
            // each pet slice; combined with an unclamped ratio that lets the owner exceed 100%.
            var damageByPlayer = new Dictionary<Player, float>();

            foreach (var kvp in DamageHistory.TotalDamage)
            {
                var damager = kvp.Value.TryGetAttacker();

                var playerDamager = damager as Player;

                if (playerDamager == null && kvp.Value.PetOwner != null)
                    playerDamager = kvp.Value.TryGetPetOwner();

                if (playerDamager == null)
                    continue;

                if (damageByPlayer.TryGetValue(playerDamager, out var existing))
                    damageByPlayer[playerDamager] = existing + kvp.Value.TotalDamage;
                else
                    damageByPlayer[playerDamager] = kvp.Value.TotalDamage;
            }

            foreach (var kvp in damageByPlayer)
            {
                var playerDamager = kvp.Key;

                // Clamp to 1.0 so overkill / heal-during-fight (recorded damage exceeding max health)
                // can never award a single contributor more than 100% of the creature's XP.
                var damagePercent = Math.Min(kvp.Value / totalHealth, 1.0f);

                var totalXP = (XpOverride ?? 0) * damagePercent;

                playerDamager.EarnXP((long)Math.Round(totalXP), XpType.Kill);

                // handle luminance
                if (LuminanceAward != null)
                {
                    var totalLuminance = (long)Math.Round(LuminanceAward.Value * damagePercent);
                    playerDamager.EarnLuminance(totalLuminance, XpType.Kill);
                }
            }
        }

        /// <summary>
        /// Handles the KillTask for a killed creature.
        /// </summary>
        /// <param name="isPrimarySlot">
        /// True only for the KillQuest call (PropertyString 45, slot 1). KillQuestCreatesRow (PropertyBool
        /// 9065) is a per-CREATURE property, not a per-slot one, so <see cref="ShouldBypassHasQuestGate"/>
        /// deliberately reads it only when this is true - see that method's doc comment for the incident
        /// (Menhir Drummer 1003262 + AunTumerokBountyCount on KillQuest2) that makes this matter.
        /// </param>
        public void OnDeath_HandleKillTask(string killQuest, bool isPrimarySlot)
        {
            /*var receivers = KillTask_GetEligibleReceivers(killQuest);

            foreach (var receiver in receivers)
            {
                var damager = receiver.Value.TryGetAttacker();

                var player = damager as Player;

                if (player == null && receiver.Value.PetOwner != null)
                    player = receiver.Value.TryGetPetOwner();

                if (player != null)
                    player.QuestManager.HandleKillTask(killQuest, this);
            }*/

            // new method

            // with full fellowship support and new config option for capping,
            // building a pre-flattened structure is no longer really necessary,
            // and we can do this more iteratively.

            // one caveat to do this, we need to keep track of player and summoning caps separately
            // this is to prevent ordering bugs, such as a player being processed after a summon,
            // and already being at the 1 cap for players

            var summon_credit_cap = (int)PropertyManager.GetLong("summoning_killtask_multicredit_cap").Item - 1;

            // Bluespire ladder (WaffleACE): the six rung-clear stamps, and ONLY those six, are allowed to
            // have their registry row CREATED by this kill instead of merely credited. See
            // BluespireLadderRewards.ShouldBootstrapClearRow for the bootstrap cycle that forces it - in
            // short, the ladder has no quest-giving NPC to arm the row, and arming it in content would be
            // the very row creation that pays the rung, so a pre-armed character would be paid on entry.
            //
            // The quest name is stripped of any @comment first, exactly as HasQuest and
            // KillTask_GetEligibleReceivers do, so an authored "Name@note" still matches the table.
            //
            // When this is false - which is every kill task in the game bar three bosses - every branch
            // below behaves exactly as it did before this line existed. The added cost is one dictionary
            // miss plus one PropertyManager read, in a method that already makes two of those.
            var ladderBootstrap = BluespireLadderRewards.ShouldBootstrapClearRow(
                QuestManager.GetQuestName(killQuest), BluespireLadder.Enabled);

            // WaffleACE fork (PropertyBool 9065, KillQuestCreatesRow): a general-purpose, per-creature version
            // of the same bootstrap bypass the Bluespire ladder's six rung names use above, for a kill task a
            // player can complete before its quest-giving NPC has armed the row (e.g. the Menhir Drummer:
            // Kanokeh arms BluespireMenhirDrummer with SetQuestCompletions at charge ACCEPTANCE, so a player
            // who kills it first holds no row yet). OR'd with ladderBootstrap (via ShouldBypassHasQuestGate)
            // so every branch below - the killer gate, the fellow gate, and TryHandleKillTask's own
            // bootstrapRow threading into QuestManager - shares one combined bypass instead of growing a
            // second parallel path. Absent or false (every creature in the game bar the ladder rungs and this
            // one) leaves every branch byte-identical to before this property existed. isPrimarySlot scopes
            // the per-creature flag to the KillQuest slot only - see ShouldBypassHasQuestGate.
            var killQuestCreatesRow = ShouldBypassHasQuestGate(ladderBootstrap, KillQuestCreatesRow, isPrimarySlot);

            var playerCredits = new Dictionary<ObjectGuid, int>();
            var summonCredits = new Dictionary<ObjectGuid, int>();

            // this option isn't really needed anymore, but keeping it around for compatibility
            // it is now synonymous with summoning_killtask_multicredit_cap <= 1
            if (!PropertyManager.GetBool("allow_summoning_killtask_multicredit").Item)
                summon_credit_cap = 0;

            foreach (var kvp in DamageHistory.TotalDamage)
            {
                if (kvp.Value.TotalDamage <= 0)
                    continue;

                var damager = kvp.Value.TryGetAttacker();

                var combatPet = false;

                var playerDamager = damager as Player;

                if (playerDamager == null && kvp.Value.PetOwner != null)
                {
                    playerDamager = kvp.Value.TryGetPetOwner();
                    combatPet = true;
                }

                if (playerDamager == null)
                    continue;

                var killTaskCredits = combatPet ? summonCredits : playerCredits;

                var cap = combatPet ? summon_credit_cap : 1;

                if (cap <= 0)
                {
                    // handle special case: use playerCredits
                    killTaskCredits = playerCredits;
                    cap = 1;
                }

                // killQuestCreatesRow (ladderBootstrap OR the per-creature KillQuestCreatesRow flag)
                // short-circuits the HasQuest gate for a bootstrap-eligible kill, and for nothing else. It
                // deliberately also short-circuits the fellow_kt_killer branch below: that option asks "did
                // the killer hold the kill task before passing it on", and under bootstrap the answer is yes
                // by construction, because this same kill creates the killer's row.
                if (killQuestCreatesRow || playerDamager.QuestManager.HasQuest(killQuest))
                {
                    TryHandleKillTask(playerDamager, killQuest, killTaskCredits, cap, killQuestCreatesRow);
                }
                // check option that requires killer to have killtask to pass to fellows
                else if (!PropertyManager.GetBool("fellow_kt_killer").Item)
                {
                    continue;
                }

                if (playerDamager.Fellowship == null)
                    continue;

                // share with fellows in kill task range
                var fellows = playerDamager.Fellowship.WithinRange(playerDamager);

                foreach (var fellow in fellows)
                {
                    // The SECOND half of the bootstrap, and the half that makes a group clear a group clear:
                    // on a first clear no fellow holds the row either, so gating them on HasQuest would credit
                    // the damagers and silently drop everybody else. The qualification rule is unchanged -
                    // Fellowship.WithinRange, which is instance-aware and indoors-aware, the same rule kill XP
                    // uses.
                    if (killQuestCreatesRow || fellow.QuestManager.HasQuest(killQuest))
                        TryHandleKillTask(fellow, killQuest, killTaskCredits, cap, killQuestCreatesRow);
                }
            }
        }

        /// <summary>
        /// True when a kill task's slot may bypass the ordinary HasQuest gate and CREATE the registry row
        /// instead of only crediting one that already exists. Pure, so the combination is unit testable
        /// without a live Creature/Player (see KillQuestCreatesRowTests).
        ///
        /// <paramref name="ladderBootstrap"/> is already quest-name-scoped by the caller
        /// (BluespireLadderRewards.ShouldBootstrapClearRow keys off the specific quest name passed for THIS
        /// slot), so it applies here regardless of slot.
        ///
        /// <paramref name="creatureKillQuestCreatesRow"/> (PropertyBool 9065, KillQuestCreatesRow) is a
        /// per-CREATURE flag, not a per-slot one - the same GetProperty read is returned for KillQuest,
        /// KillQuest2 and KillQuest3 alike. It is deliberately read only when <paramref name="isPrimarySlot"/>
        /// is true (the KillQuest slot, PropertyString 45). Real incident this closes (2026-09-25): the
        /// Menhir Drummer (1003262) carries this flag true for its own BluespireMenhirDrummer bootstrap in
        /// KillQuest, and also briefly carried an unrelated quest, 'AunTumerokBountyCount', in KillQuest2
        /// (Content/sql/patches/marae_lassel_killquest_auntumerok.sql) - because the old code OR'd the flag
        /// into every slot equally, that second tag let the same kill bootstrap Hea Piritaha's 50-kill bounty
        /// row for any killer, skipping her HasQuest gate entirely. KillQuest2/KillQuest3 must stay
        /// HasQuest-gated regardless of what the creature's own flag says, so a future re-tag of slot 2 or 3
        /// for an unrelated quest can never inherit a bypass authored for slot 1.
        /// </summary>
        public static bool ShouldBypassHasQuestGate(bool ladderBootstrap, bool creatureKillQuestCreatesRow, bool isPrimarySlot)
        {
            return ladderBootstrap || (isPrimarySlot && creatureKillQuestCreatesRow);
        }

        /// <param name="bootstrapRow">
        /// Bluespire ladder (WaffleACE): let this kill CREATE the player's registry row rather than only
        /// credit an existing one. Threaded through from OnDeath_HandleKillTask's single ladder test so the
        /// discriminator lives in exactly one place; every other caller gets the default and the historic
        /// behaviour.
        /// </param>
        public bool TryHandleKillTask(Player player, string killTask, Dictionary<ObjectGuid, int> killTaskCredits, int cap, bool bootstrapRow = false)
        {
            if (killTaskCredits.TryGetValue(player.Guid, out var currentCredits))
            {
                if (currentCredits >= cap)
                    return false;

                killTaskCredits[player.Guid]++;
            }
            else
                killTaskCredits[player.Guid] = 1;

            player.QuestManager.HandleKillTask(killTask, this, bootstrapRow);

            return true;
        }

        /// <summary>
        /// Returns a flattened structure of eligible Players, Fellows, and CombatPets
        /// </summary>
        public Dictionary<ObjectGuid, DamageHistoryInfo> KillTask_GetEligibleReceivers(string killQuest)
        {
            // http://acpedia.org/wiki/Announcements_-_2012/12_-_A_Growing_Twilight#Release_Notes

            var questName = QuestManager.GetQuestName(killQuest);

            // we are using DamageHistoryInfo here, instead of Creature or WorldObjectInfo
            // WeakReference<CombatPet> may be null for expired CombatPets, but we still need the WeakReference<PetOwner> references

            var receivers = new Dictionary<ObjectGuid, DamageHistoryInfo>();

            foreach (var kvp in DamageHistory.TotalDamage)
            {
                if (kvp.Value.TotalDamage <= 0)
                    continue;

                var damager = kvp.Value.TryGetAttacker();

                var playerDamager = damager as Player;

                if (playerDamager == null && kvp.Value.PetOwner != null)
                {
                    // handle combat pets
                    playerDamager = kvp.Value.TryGetPetOwner();

                    if (playerDamager != null && playerDamager.QuestManager.HasQuest(questName))
                    {
                        // only add combat pet to eligible receivers if player has quest, and allow_summoning_killtask_multicredit = true (default, retail)
                        if (DamageHistory.HasDamager(playerDamager, true) && PropertyManager.GetBool("allow_summoning_killtask_multicredit").Item)
                            receivers[kvp.Value.Guid] = kvp.Value;  // add CombatPet
                        else
                            receivers[playerDamager.Guid] = new DamageHistoryInfo(playerDamager);   // add dummy profile for PetOwner
                    }

                    // regardless if combat pet is eligible, we still want to continue traversing to the pet owner, and possibly fellows

                    // in a scenario where combat pet does 100% damage:

                    // - regardless if allow_summoning_killtask_multicredit is enabled/disabled, it should continue traversing into pet owner and possibly their fellows

                    // - if pet owner doesn't have kill task, and fellow_kt_killer=false, any fellows with the task should still receive 1 credit
                }

                if (playerDamager == null)
                    continue;

                // factors:
                // - has quest
                // - is killer (last damager, top damager, or any damager? in current context, considering it to be any damager)
                // - has fellowship
                // - server option: fellow_kt_killer
                // - is within Fellowship.GetDistanceScalar range of the killer (same rule kill XP/luminance uses)

                if (playerDamager.QuestManager.HasQuest(questName))
                {
                    // just add a fake DamageHistoryInfo for reference
                    receivers[playerDamager.Guid] = new DamageHistoryInfo(playerDamager);
                }
                else if (PropertyManager.GetBool("fellow_kt_killer").Item)
                {
                    // if this option is enabled (retail default), the killer is required to have kill task
                    // for it to share with fellowship
                    continue;
                }

                // we want to add fellowship members in a flattened structure
                // in this inner loop, instead of the outer loop

                // scenarios:

                // i am a summoner in a fellowship with 1 other player
                // we both have a killtask

                // - my combatpet does 100% damage to the monster
                // result: i get 1 killtask credit, and my fellow gets 1 killtask credit

                // - my combatpet does 50% damage to monster, and i do 50% damage
                // result: i get 2 killtask credits (1 if allow_summoning_killtask_multicredit server option is disabled), and my fellow gets 1 killtask credit
                // after update should be 2/2, instead of 2/1

                // - my combatpet does 33% damage to monster, i do 33% damage, and fellow does 33% damage
                // result: same as previous scenario
                // after update should be 2/2, instead of 2/1 again

                // 2 players not in a fellowship both have a killtask
                // they each do 50% damage to monster

                // result: both players receive killtask credit

                if (playerDamager.Fellowship == null)
                    continue;

                // share with fellows in kill task range
                var fellows = playerDamager.Fellowship.WithinRange(playerDamager);

                foreach (var fellow in fellows)
                {
                    if (fellow.QuestManager.HasQuest(questName))
                        receivers[fellow.Guid] = new DamageHistoryInfo(fellow);
                }
            }
            return receivers;
        }

        /// <summary>
        /// Create a corpse for both creatures and players currently
        /// </summary>
        protected void CreateCorpse(DamageHistoryInfo killer, bool hadVitae = false)
        {
            if (NoCorpse)
            {
                if (killer != null && killer.IsOlthoiPlayer) return;

                var loot = GenerateTreasure(killer, null);

                foreach(var item in loot)
                {
                    if (!string.IsNullOrEmpty(item.Quest)) // if the item has a Quest string, make the creature a "generator" of the item so that the pickup action applies the quest. 
                        item.GeneratorId = Guid.Full; 
                    item.Location = new Position(Location);
                    LandblockManager.AddObject(item);
                }
                return;
            }

            var cachedWeenie = DatabaseManager.World.GetCachedWeenie("corpse");

            var corpse = WorldObjectFactory.CreateNewWorldObject(cachedWeenie) as Corpse;

            var prefix = "Corpse";

            if (TreasureCorpse)
            {
                // Hardcoded values from PCAPs of Treasure Pile Corpses, everything else lines up exactly with existing corpse weenie
                corpse.SetupTableId  = 0x02000EC4;
                corpse.MotionTableId = 0x0900019B;
                corpse.SoundTableId  = 0x200000C2;
                corpse.ObjScale      = 0.4f;

                prefix = "Treasure";
            }
            else
            {
                corpse.SetupTableId = SetupTableId;
                corpse.MotionTableId = MotionTableId;
                //corpse.SoundTableId = SoundTableId; // Do not change sound table for corpses
                corpse.PaletteBaseDID = PaletteBaseDID;
                corpse.ClothingBase = ClothingBase;
                corpse.PhysicsTableId = PhysicsTableId;

                if (ObjScale.HasValue)
                    corpse.ObjScale = ObjScale;
                if (PaletteTemplate.HasValue)
                    corpse.PaletteTemplate = PaletteTemplate;
                if (Shade.HasValue)
                    corpse.Shade = Shade;
                //if (Translucency.HasValue) // Shadows have Translucency but their corpses do not, videographic evidence can be found on YouTube.
                //corpse.Translucency = Translucency;


                // Pull and save objdesc for correct corpse apperance at time of death
                var objDesc = CalculateObjDesc();

                corpse.Biota.PropertiesAnimPart = objDesc.AnimPartChanges.Clone(corpse.BiotaDatabaseLock);

                corpse.Biota.PropertiesPalette = objDesc.SubPalettes.Clone(corpse.BiotaDatabaseLock);

                corpse.Biota.PropertiesTextureMap = objDesc.TextureChanges.Clone(corpse.BiotaDatabaseLock);
            }

            // use the physics location for accuracy,
            // especially while jumping
            //
            // Held in a local as well as assigned, purely so the ML Treasure Hunt gate further down can read
            // the landblock and realm off it without going back through WorldObject.Location - that getter is
            // GetPosition(PositionType.Location), two dictionary probes, and it would run on EVERY creature
            // death server-wide. See the drop hook's comment below.
            var corpseLocation = PhysicsObj.Position.ACEPosition(PhysicsObj.CurInstance);

            corpse.Location = corpseLocation;

            corpse.VictimId = Guid.Full;
            corpse.Name = $"{prefix} of {Name}";

            // set 'killed by' for looting rights
            var killerName = "misadventure";
            if (killer != null)
            {
                if (!(Generator != null && Generator.Guid == killer.Guid) && Guid != killer.Guid)
                {
                    if (!string.IsNullOrWhiteSpace(killer.Name))
                        killerName = killer.Name.TrimStart('+');  // vtank requires + to be stripped for regex matching.

                    corpse.KillerId = killer.Guid.Full;

                    if (killer.PetOwner != null)
                    {
                        var petOwner = killer.TryGetPetOwner();
                        if (petOwner != null)
                            corpse.KillerId = petOwner.Guid.Full;
                    }
                }
            }

            corpse.LongDesc = $"Killed by {killerName}.";

            bool saveCorpse = false;

            var player = this as Player;

            // Rare/treasure-map eligibility only (set below in the monster branch via
            // DamageHistoryInfo.ResolvePetOwnerAsKiller); declared here so TryGenerateRare below can reach it
            // regardless of branch. Unused, and left as the raw killer, on the player-death branch - that
            // branch's behavior is unchanged.
            var lootKiller = killer;

            if (player != null)
            {
                corpse.SetPosition(PositionType.Location, corpse.Location);

                var killerIsOlthoiPlayer = killer != null && killer.IsOlthoiPlayer;
                var killerIsPkPlayer = killer != null && killer.IsPlayer && killer.Guid != Guid;

                //var dropped = killer != null && killer.IsOlthoiPlayer ? player.CalculateDeathItems_Olthoi(corpse, hadVitae) : player.CalculateDeathItems(corpse);

                if (killerIsOlthoiPlayer || player.IsOlthoiPlayer)
                {
                    var dropped = player.CalculateDeathItems_Olthoi(corpse, hadVitae, killerIsOlthoiPlayer, killerIsPkPlayer);

                    foreach (var wo in dropped)
                        DoCantripLogging(killer, wo);

                    corpse.RecalculateDecayTime(player);

                    if (dropped.Count > 0)
                        saveCorpse = true;

                    corpse.PkLevel = PKLevel.PK;
                }
                else
                {
                    var dropped = player.CalculateDeathItems(corpse);

                    corpse.RecalculateDecayTime(player);

                    if (dropped.Count > 0)
                        saveCorpse = true;

                    if ((player.Location.Cell & 0xFFFF) < 0x100)
                    {
                        player.SetPosition(PositionType.LastOutsideDeath, new Position(corpse.Location));
                        player.Session.Network.EnqueueSend(new GameMessagePrivateUpdatePosition(player, PositionType.LastOutsideDeath, corpse.Location));

                        if (dropped.Count > 0)
                            player.Session.Network.EnqueueSend(new GameMessageSystemChat($"Your corpse is located at ({corpse.Location.GetMapCoordStr()}).", ChatMessageType.Broadcast));
                    }

                    var isPKdeath = player.IsPKDeath(killer);
                    var isPKLdeath = player.IsPKLiteDeath(killer);

                    if (isPKdeath)
                        corpse.PkLevel = PKLevel.PK;

                    if (!isPKdeath && !isPKLdeath)
                    {
                        var miserAug = player.AugmentationLessDeathItemLoss * 5;
                        if (miserAug > 0)
                            player.Session.Network.EnqueueSend(new GameMessageSystemChat($"Your augmentation has reduced the number of items you can lose by {miserAug}!", ChatMessageType.Broadcast));
                    }

                    if (dropped.Count == 0 && !isPKLdeath)
                        player.Session.Network.EnqueueSend(new GameMessageSystemChat($"You have retained all your items. You do not need to recover your corpse!", ChatMessageType.Broadcast));
                }
            }
            else
            {
                corpse.IsMonster = true;

                // Rare/treasure-map eligibility only: a kill landed by a player's CombatPet is eligible
                // exactly as if the owner had made the kill (DamageHistoryInfo.ResolvePetOwnerAsKiller).
                // GenerateTreasure/GenerateTreasure_Olthoi just below, and everything else in this method,
                // still read the raw killer - only the gate, TryGenerateRare and TryDropTreasureMap use this.
                lootKiller = DamageHistoryInfo.ResolvePetOwnerAsKiller(killer);

                if (killer == null || !killer.IsOlthoiPlayer)
                    GenerateTreasure(killer, corpse);
                else
                    GenerateTreasure_Olthoi(killer, corpse);

                // ML Treasure Hunt (WaffleACE): a creature killed by a player on Marae Lassel rarely leaves a
                // treasure map on its corpse (Docs/Marae-Lassel/TREASURE-HUNT-PLAN.md section 6 "Drop", step 7).
                //
                // Beside GenerateTreasure in CreateCorpse, NOT in OnDeath, exactly as the plan specifies: the
                // map is loot, and CreateCorpse is the only path that owns a corpse to put loot in. Two of the
                // hook's required exclusions then come free from this position rather than from a check -
                // the enclosing else branch is the `player == null` branch, so a player death can never reach
                // here, and the NoCorpse early-return at the top of this method means a corpse-less creature
                // never reaches here either.
                //
                // THIS RUNS FOR EVERY CREATURE DEATH ON EVERY LANDBLOCK, FOREVER, ON THE TICK PATH. The entire
                // cost it adds to a kill that is NOT on Marae Lassel is the three lines below:
                //   - corpseLocation.LandblockId.Landblock: a struct field read (Position.landblockId, already
                //     populated, so the Raw == 0 fallback never runs) plus a shift and a mask;
                //   - corpseLocation.RealmID: Position.ParseInstanceID over the uint Instance field, a shift
                //     and two masks;
                //   - IsMaraeLassel: one comparison to reject a non-realm-1 death outright, and on realm 1 only
                //     a further shift, two masks and four range comparisons (MlTreasureLandblock.cs:26-30).
                // No allocation, no dictionary lookup, no property-table read, no PropertyManager read and no
                // RNG draw. All of those live behind this predicate inside MlTreasureDrop, which a non-ML
                // death never enters at all. Reading corpse.Location instead of the local would have added two
                // dictionary probes per death (WorldObject_Properties.cs:769), which is why the local exists.
                var corpseLandblock = corpseLocation.LandblockId.Landblock;
                var corpseRealm = corpseLocation.RealmID;

                if (MlTreasureLandblock.IsMaraeLassel(corpseLandblock, corpseRealm))
                {
                    MlTreasureDrop.TryDropTreasureMap(corpse, lootKiller, corpseLandblock, corpseRealm, Level ?? 0);

                    // ML Treasure Hunt step 11: a dug-up Aun Relaria leaves its CAP trophy on the corpse the
                    // first time a given character kills it. Inside the same realm/box gate as the map drop,
                    // so a death anywhere else never enters the file; the first thing it reads is the 9066
                    // marker only the dig spawn stamps, so an ordinary Marae Lassel kill costs one property
                    // read. There is still never a second trophy, and the boss weenie still carries no
                    // DeathTreasureType or create-list rows - XP and luminance still come from its own
                    // XpOverride/LuminanceAward, which the ordinary death path already pays. A repeat kill
                    // (the killer has already claimed the trophy) additionally pays a guaranteed handful of
                    // ML Doubloons and rolls a low chance at a permanent player aura, via
                    // MlRelariaTrophy.TryAwardRepeatKillRewards - see MlRelariaTrophy.cs's own header.
                    MlRelariaTrophy.TryDropTrophy(this, corpse, lootKiller);
                }

                // The rule itself lives in Creature_LootRolls.ResolveCanGenerateRare so the Threads pooled model
                // (ThreadLootPool.BankKill) applies the identical eligibility at kill time. The write pattern here is
                // the original one, exactly: a non-candidate killer clears the flag, a qualifying player killer sets
                // it, and a player killer that does not qualify writes nothing. Passing current: false makes the
                // rule return true only when that player killer qualifies. lootKiller (not killer) so a CombatPet's
                // kill reads as its owner's here, same as TryGenerateRare and TryDropTreasureMap below.
                if (!(lootKiller != null && lootKiller.IsPlayer && !lootKiller.IsOlthoiPlayer))
                    CanGenerateRare = false;
                else if (ResolveCanGenerateRare(false, LootGateLevelFor(LootGateLevel, Level), true, true, false, () => lootKiller.TryGetAttacker()?.Level))
                    CanGenerateRare = true;
            }

            corpse.RemoveProperty(PropertyInt.Value);

            if (CanGenerateRare && killer != null)
                corpse.TryGenerateRare(lootKiller);

            corpse.InitPhysicsObj();

            // persist the original creature velocity (only used for falling) to corpse
            corpse.PhysicsObj.Velocity = PhysicsObj.Velocity;

            corpse.EnterWorld();

            if (player != null)
            {
                if (corpse.PhysicsObj == null || corpse.PhysicsObj.Position == null)
                    log.InfoFormat("[CORPSE] {0}'s corpse (0x{1}) failed to spawn! Tried at {2}", Name, corpse.Guid, player.Location.ToLOCString());
                else
                    log.InfoFormat("[CORPSE] {0}'s corpse (0x{1}) is located at {2}", Name, corpse.Guid, corpse.PhysicsObj.Position);
            }

            if (saveCorpse)
            {
                corpse.SaveBiotaToDatabase();

                foreach (var item in corpse.Inventory.Values)
                    item.SaveBiotaToDatabase();
            }
        }

        public bool CanGenerateRare
        {
            get => GetProperty(PropertyBool.CanGenerateRare) ?? false;
            set { if (!value) RemoveProperty(PropertyBool.CanGenerateRare); else SetProperty(PropertyBool.CanGenerateRare, value); }
        }

        /// <summary>
        /// Transfers generated treasure from creature to corpse
        /// </summary>
        private List<WorldObject> GenerateTreasure(DamageHistoryInfo killer, Corpse corpse)
        {
            var droppedItems = new List<WorldObject>();

            // create death treasure from loot generation factory
            if (DeathTreasure != null)
            {
                List<WorldObject> items = RollDeathTreasureItems(DeathTreasure);
                foreach (WorldObject wo in items)
                {
                    if (corpse != null)
                        corpse.TryAddToInventory(wo);
                    else
                        droppedItems.Add(wo);

                    DoCantripLogging(killer, wo);
                }
            }

            // move wielded treasure over, which also should include Wielded objects not marked for destroy on death.
            // allow server operators to configure this behavior due to errors in createlist post 16py data
            var dropFlags = PropertyManager.GetBool("creatures_drop_createlist_wield").Item ? DestinationType.WieldTreasure : DestinationType.Treasure;

            var wieldedTreasure = Inventory.Values.Concat(EquippedObjects.Values).Where(i => (i.DestinationType & dropFlags) != 0);
            foreach (var item in wieldedTreasure.ToList())
            {
                if (item.Bonded == BondedStatus.Destroy)
                    continue;

                if (TryDequipObjectWithBroadcasting(item.Guid, out var wo, out var wieldedLocation))
                    EnqueueBroadcast(new GameMessagePublicUpdateInstanceID(item, PropertyInstanceId.Wielder, ObjectGuid.Invalid));

                // WaffleACE fork: undo a runtime-only scale (World Events boss scale) before the item can be
                // looted, so it does not keep the boss's size. No-op for every unscaled creature.
                RestoreRuntimeItemScale(item);

                if (corpse != null)
                {
                    corpse.TryAddToInventory(item);
                    EnqueueBroadcast(new GameMessagePublicUpdateInstanceID(item, PropertyInstanceId.Container, corpse.Guid), new GameMessagePickupEvent(item));
                }
                else
                    droppedItems.Add(item);
            }

            // contain and non-wielded treasure create
            if (Biota.PropertiesCreateList != null)
            {
                var createList = Biota.PropertiesCreateList.Where(i => (i.DestinationType & DestinationType.Contain) != 0 ||
                                (i.DestinationType & DestinationType.Treasure) != 0 && (i.DestinationType & DestinationType.Wield) == 0).ToList();

                var selected = CreateListSelect(createList);

                foreach (var item in selected)
                {
                    var wo = WorldObjectFactory.CreateNewWorldObject(item);

                    if (wo != null)
                    {
                        if (corpse != null)
                            corpse.TryAddToInventory(wo);
                        else
                            droppedItems.Add(wo);
                    }
                }
            }

            // Threads (WaffleACE): salvage affinity. A gem carrying one of the affinity modifiers
            // gives every creature in its run a per-KILL chance to leave one extra ordinary item made of that
            // material, which the player salvages themselves. Per kill rather than per rolled item, so the
            // magnitude the gem advertises is the observed rate and stays orthogonal to lootQuantityMult.
            //
            // Guarded on the SAME two keys as the run's death hook (Creature_Death.cs:158): the in-memory
            // back-reference AND the persisted stamp, so a creature left over from a dead run is inert.
            //
            // ThreadSafeRandom, never the gem's seeded Random - the population plan must stay bit-identical
            // to a gem with no affinity modifier, and there is a determinism test that says so.
            if (P_DungeonRun != null && GetProperty(PropertyInt.ThreadDungeonRunId) != null && P_DungeonSalvageAffinities != null)
            {
                // A run creature always leaves a corpse (ThreadDungeonSpawner clears NoCorpse), but the
                // dropped-items branch is handled anyway rather than assumed away.
                // The draws and creates are Creature_LootRolls.RollSalvageAffinityItems, shared with the pooled
                // model. It draws and creates in the same order this loop did; adding to the corpse afterwards
                // consumes no random draw and no guid, so the outcome is unchanged.
                foreach (var wo in RollSalvageAffinityItems(P_DungeonSalvageAffinities, DeathTreasure?.Tier ?? 1))
                {
                    if (corpse != null)
                        corpse.TryAddToInventory(wo);
                    else
                        droppedItems.Add(wo);
                }
            }

            return droppedItems;
        }

        /// <summary>
        /// Generates random amounts of slag on a corpse
        /// when an OlthoiPlayer is the killer
        /// </summary>
        private void GenerateTreasure_Olthoi(DamageHistoryInfo killer, Corpse corpse)
        {
            if (DeathTreasure == null) return;

            var slag = LootGenerationFactory.RollSlag(DeathTreasure);

            if (slag == null) return;

            corpse.TryAddToInventory(slag);
        }

        public void DoCantripLogging(DamageHistoryInfo killer, WorldObject wo)
        {
            var epicCantrips = wo.EpicCantrips;
            var legendaryCantrips = wo.LegendaryCantrips;

            if (epicCantrips.Count > 0 && log.IsDebugEnabled)
                log.Debug($"[LOOT][EPIC] {Name} ({Guid}) generated item with {epicCantrips.Count} epic{(epicCantrips.Count > 1 ? "s" : "")} - {wo.Name} ({wo.Guid}) - {GetSpellList(epicCantrips)} - killed by {killer?.Name} ({killer?.Guid})");

            if (legendaryCantrips.Count > 0 && log.IsDebugEnabled)
                log.Debug($"[LOOT][LEGENDARY] {Name} ({Guid}) generated item with {legendaryCantrips.Count} legendar{(legendaryCantrips.Count > 1 ? "ies" : "y")} - {wo.Name} ({wo.Guid}) - {GetSpellList(legendaryCantrips)} - killed by {killer?.Name} ({killer?.Guid})");
        }

        public static string GetSpellList(Dictionary<int, float> spellTable)
        {
            var spells = new List<Server.Entity.Spell>();

            foreach (var kvp in spellTable)
                spells.Add(new Server.Entity.Spell(kvp.Key, false));

            return string.Join(", ", spells.Select(i => i.Name));
        }

        public bool IsOnNoDeathXPLandblock => Location != null ? NoDeathXP_Landblocks.Contains(Location.LandblockId.Landblock) : false;

        /// <summary>
        /// A list of landblocks the player gains no xp from creature kills
        /// </summary>
        public static HashSet<ushort> NoDeathXP_Landblocks = new HashSet<ushort>()
        {
            0x00B0,     // Colosseum Arena One
            0x00B1,     // Colosseum Arena Two
            0x00B2,     // Colosseum Arena Three
            0x00B3,     // Colosseum Arena Four
            0x00B4,     // Colosseum Arena Five
            0x5960,     // Gauntlet Arena One (Celestial Hand)
            0x5961,     // Gauntlet Arena Two (Celestial Hand)
            0x5962,     // Gauntlet Arena One (Eldritch Web)
            0x5963,     // Gauntlet Arena Two (Eldritch Web)
            0x5964,     // Gauntlet Arena One (Radiant Blood)
            0x5965,     // Gauntlet Arena Two (Radiant Blood)
            0x596B,     // Gauntlet Staging Area (All Societies)
        };
    }
}
