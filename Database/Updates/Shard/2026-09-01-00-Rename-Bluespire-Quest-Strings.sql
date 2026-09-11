/* Bluespire quest strings - rename the 46 arc quest names in every character's quest registry.
 *
 * Players see quest strings raw: the first registry row prints "You've earned a quest stamp: <name>"
 * (Player_QuestStamps.HandleQuestStampRowCreated) and /myquests prints the name and the quest table's
 * message. The Bluespire arc shipped with builder labels (q1_kills, c2_billets), so on 2026-09-01 all
 * 46 `quest` rows in ace_world were renamed to Bluespire<Objective> (Content/sql/quests/*.sql, unit
 * ids unchanged). That covers the world template row; this script moves the SHARD side so characters
 * already mid-quest keep their progress instead of restarting Q1 from nothing.
 *
 * Two rows per quest can exist in character_properties_quest_registry: the plain progress row and
 * the fork's account stamp ledger twin, QuestStampSeen_<name> (QuestStamps.LedgerPrefix). Both are
 * renamed. Emote scripts, KillQuest tags and InqQuest checks all resolve by name, so a row left under
 * the old name would be invisible to the renamed content.
 *
 * Idempotent: every UPDATE matches on the OLD name only, so a second run (a deploy that resets
 * applied_updates.txt, a container without a persisted Config volume) finds nothing to change. Plain
 * UPDATE statements only - no user variables, no procedures - so the patcher runs it as one
 * multi-statement batch. No DDL.
 *
 * Order relative to the world content does not matter: the world `quest` rows and these shard rows
 * are independent tables keyed by the same string, and the server reads both by the new name after
 * restart.
 */

UPDATE `character_properties_quest_registry` SET `quest_Name` = 'BluespireThinTheRunners' WHERE `quest_Name` = 'q1_kills';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'QuestStampSeen_BluespireThinTheRunners' WHERE `quest_Name` = 'QuestStampSeen_q1_kills';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'BluespireStandAtTheRoadCairns' WHERE `quest_Name` = 'q1_cairns';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'QuestStampSeen_BluespireStandAtTheRoadCairns' WHERE `quest_Name` = 'QuestStampSeen_q1_cairns';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'BluespireThereWereNine' WHERE `quest_Name` = 'q1_complete';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'QuestStampSeen_BluespireThereWereNine' WHERE `quest_Name` = 'QuestStampSeen_q1_complete';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'BluespireRiketurasBounty' WHERE `quest_Name` = 'q2_terms';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'QuestStampSeen_BluespireRiketurasBounty' WHERE `quest_Name` = 'QuestStampSeen_q2_terms';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'BluespireAunKitSold' WHERE `quest_Name` = 'q2_turnins';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'QuestStampSeen_BluespireAunKitSold' WHERE `quest_Name` = 'QuestStampSeen_q2_turnins';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'BluespireTimiteasStandingOrder' WHERE `quest_Name` = 'q3_accept';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'QuestStampSeen_BluespireTimiteasStandingOrder' WHERE `quest_Name` = 'QuestStampSeen_q3_accept';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'BluespireTimiteaHasPaid' WHERE `quest_Name` = 'q3_paid';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'QuestStampSeen_BluespireTimiteaHasPaid' WHERE `quest_Name` = 'QuestStampSeen_q3_paid';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'BluespireHuntingMaterialSold' WHERE `quest_Name` = 'q3_turnins';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'QuestStampSeen_BluespireHuntingMaterialSold' WHERE `quest_Name` = 'QuestStampSeen_q3_turnins';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'BluespireCamaurisShelf' WHERE `quest_Name` = 'q4_shelf';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'QuestStampSeen_BluespireCamaurisShelf' WHERE `quest_Name` = 'QuestStampSeen_q4_shelf';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'BluespireCamauriHasPaid' WHERE `quest_Name` = 'q4_paid';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'QuestStampSeen_BluespireCamauriHasPaid' WHERE `quest_Name` = 'QuestStampSeen_q4_paid';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'BluespireHoldStoneAhurenga' WHERE `quest_Name` = 'q4_ahurenga';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'QuestStampSeen_BluespireHoldStoneAhurenga' WHERE `quest_Name` = 'QuestStampSeen_q4_ahurenga';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'BluespireHoldStoneHikirenga' WHERE `quest_Name` = 'q4_hikirenga';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'QuestStampSeen_BluespireHoldStoneHikirenga' WHERE `quest_Name` = 'QuestStampSeen_q4_hikirenga';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'BluespireHoldStoneIwarenga' WHERE `quest_Name` = 'q4_iwarenga';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'QuestStampSeen_BluespireHoldStoneIwarenga' WHERE `quest_Name` = 'QuestStampSeen_q4_iwarenga';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'BluespireHoldStoneKaurenga' WHERE `quest_Name` = 'q4_kaurenga';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'QuestStampSeen_BluespireHoldStoneKaurenga' WHERE `quest_Name` = 'QuestStampSeen_q4_kaurenga';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'BluespireHoldStoneMotuenga' WHERE `quest_Name` = 'q4_motuenga';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'QuestStampSeen_BluespireHoldStoneMotuenga' WHERE `quest_Name` = 'QuestStampSeen_q4_motuenga';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'BluespireHoldStoneNukurenga' WHERE `quest_Name` = 'q4_nukurenga';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'QuestStampSeen_BluespireHoldStoneNukurenga' WHERE `quest_Name` = 'QuestStampSeen_q4_nukurenga';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'BluespireHoldStoneOterenga' WHERE `quest_Name` = 'q4_oterenga';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'QuestStampSeen_BluespireHoldStoneOterenga' WHERE `quest_Name` = 'QuestStampSeen_q4_oterenga';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'BluespireHoldStoneTuraenga' WHERE `quest_Name` = 'q4_turaenga';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'QuestStampSeen_BluespireHoldStoneTuraenga' WHERE `quest_Name` = 'QuestStampSeen_q4_turaenga';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'BluespireHoldStoneWharenga' WHERE `quest_Name` = 'q4_wharenga';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'QuestStampSeen_BluespireHoldStoneWharenga' WHERE `quest_Name` = 'QuestStampSeen_q4_wharenga';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'BluespireLookUnderTheWatchTower' WHERE `quest_Name` = 'q5_hook';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'QuestStampSeen_BluespireLookUnderTheWatchTower' WHERE `quest_Name` = 'QuestStampSeen_q5_hook';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'BluespireLaboratoryStairUnsealed' WHERE `quest_Name` = 'q5_door';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'QuestStampSeen_BluespireLaboratoryStairUnsealed' WHERE `quest_Name` = 'QuestStampSeen_q5_door';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'BluespireSiberiteAccretionDestroyed' WHERE `quest_Name` = 'q5_accretion';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'QuestStampSeen_BluespireSiberiteAccretionDestroyed' WHERE `quest_Name` = 'QuestStampSeen_q5_accretion';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'BluespireNothingComesBackUp' WHERE `quest_Name` = 'q5_done';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'QuestStampSeen_BluespireNothingComesBackUp' WHERE `quest_Name` = 'QuestStampSeen_q5_done';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'BluespireReadTheSkyMarks' WHERE `quest_Name` = 'q6_marks';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'QuestStampSeen_BluespireReadTheSkyMarks' WHERE `quest_Name` = 'QuestStampSeen_q6_marks';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'BluespireItSitsLowerThanItDid' WHERE `quest_Name` = 'q6_complete';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'QuestStampSeen_BluespireItSitsLowerThanItDid' WHERE `quest_Name` = 'QuestStampSeen_q6_complete';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'BluespireTemenuasCharge' WHERE `quest_Name` = 'q7_accept';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'QuestStampSeen_BluespireTemenuasCharge' WHERE `quest_Name` = 'QuestStampSeen_q7_accept';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'BluespireAunPriestOfRaeta' WHERE `quest_Name` = 'q7_priest_kill';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'QuestStampSeen_BluespireAunPriestOfRaeta' WHERE `quest_Name` = 'QuestStampSeen_q7_priest_kill';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'BluespireShrineTaken' WHERE `quest_Name` = 'q7_shrine';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'QuestStampSeen_BluespireShrineTaken' WHERE `quest_Name` = 'QuestStampSeen_q7_shrine';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'BluespireAhurengaRuinExamined' WHERE `quest_Name` = 'q7_ruin';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'QuestStampSeen_BluespireAhurengaRuinExamined' WHERE `quest_Name` = 'QuestStampSeen_q7_ruin';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'BluespireKanokehsCharge' WHERE `quest_Name` = 'q7_ring_accept';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'QuestStampSeen_BluespireKanokehsCharge' WHERE `quest_Name` = 'QuestStampSeen_q7_ring_accept';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'BluespireMenhirDrummer' WHERE `quest_Name` = 'q7_drummer_kill';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'QuestStampSeen_BluespireMenhirDrummer' WHERE `quest_Name` = 'QuestStampSeen_q7_drummer_kill';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'BluespireMenhirRingSilenced' WHERE `quest_Name` = 'q7_ring';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'QuestStampSeen_BluespireMenhirRingSilenced' WHERE `quest_Name` = 'QuestStampSeen_q7_ring';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'BluespireTheNearGround' WHERE `quest_Name` = 'q7_done';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'QuestStampSeen_BluespireTheNearGround' WHERE `quest_Name` = 'QuestStampSeen_q7_done';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'BluespireLeaveRunnersMarks' WHERE `quest_Name` = 'c1_marks';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'QuestStampSeen_BluespireLeaveRunnersMarks' WHERE `quest_Name` = 'QuestStampSeen_c1_marks';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'BluespireTheBackDoor' WHERE `quest_Name` = 'c1_complete';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'QuestStampSeen_BluespireTheBackDoor' WHERE `quest_Name` = 'QuestStampSeen_c1_complete';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'BluespireNyrinuasHideOrder' WHERE `quest_Name` = 'c2_hide_order';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'QuestStampSeen_BluespireNyrinuasHideOrder' WHERE `quest_Name` = 'QuestStampSeen_c2_hide_order';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'BluespireWarOrderCord' WHERE `quest_Name` = 'c2_cord';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'QuestStampSeen_BluespireWarOrderCord' WHERE `quest_Name` = 'QuestStampSeen_c2_cord';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'BluespireWarOrderBillets' WHERE `quest_Name` = 'c2_billets';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'QuestStampSeen_BluespireWarOrderBillets' WHERE `quest_Name` = 'QuestStampSeen_c2_billets';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'BluespireWarOrderHides' WHERE `quest_Name` = 'c2_hide';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'QuestStampSeen_BluespireWarOrderHides' WHERE `quest_Name` = 'QuestStampSeen_c2_hide';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'BluespireTenDaysOfRope' WHERE `quest_Name` = 'c2_complete';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'QuestStampSeen_BluespireTenDaysOfRope' WHERE `quest_Name` = 'QuestStampSeen_c2_complete';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'BluespireThinTheHeadland' WHERE `quest_Name` = 'c4_kills';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'QuestStampSeen_BluespireThinTheHeadland' WHERE `quest_Name` = 'QuestStampSeen_c4_kills';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'BluespireItDoesNotStop' WHERE `quest_Name` = 'c4_complete';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'QuestStampSeen_BluespireItDoesNotStop' WHERE `quest_Name` = 'QuestStampSeen_c4_complete';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'BluespireTheFirstDrumAccepted' WHERE `quest_Name` = 'c5_accept';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'QuestStampSeen_BluespireTheFirstDrumAccepted' WHERE `quest_Name` = 'QuestStampSeen_c5_accept';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'BluespireDrumRackShown' WHERE `quest_Name` = 'c5_rack';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'QuestStampSeen_BluespireDrumRackShown' WHERE `quest_Name` = 'QuestStampSeen_c5_rack';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'BluespireHeadlandDrumSilenced' WHERE `quest_Name` = 'c5_drum_active';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'QuestStampSeen_BluespireHeadlandDrumSilenced' WHERE `quest_Name` = 'QuestStampSeen_c5_drum_active';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'BluespireTheFirstDrum' WHERE `quest_Name` = 'c5_complete';
UPDATE `character_properties_quest_registry` SET `quest_Name` = 'QuestStampSeen_BluespireTheFirstDrum' WHERE `quest_Name` = 'QuestStampSeen_c5_complete';
