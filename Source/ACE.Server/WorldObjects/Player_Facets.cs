using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;

using log4net;

using ACE.Common;
using ACE.DatLoader;
using ACE.Database;
using ACE.Database.Models.Shard;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ClassAbilities;
using ACE.Server.Entity;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Entity.Facets;
using ACE.Server.Managers;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// Player Facets: additional configurable builds for one character. A facet owns how
    /// progression has been SPENT - skills, class ability ranks, the attribute REDISTRIBUTION, the
    /// remembered worn set. It does not own the progression itself: level, lifetime XP, attribute
    /// ranks and their CPSpent, vitals, augmentations and the spellbook are shared by every slot.
    /// See Docs/Facets/DESIGN.md.
    ///
    /// The attribute split is finer than the others and is worth stating precisely: only the six
    /// primary attributes' InitLevel (CreatureAttribute.StartingValue - the half
    /// AttributeTransferDevice moves) is per-facet. The XP-bought half of the same attribute stays
    /// global. That boundary is what keeps attributes out of FacetPools entirely, since InitLevel is
    /// never bought with experience. See FacetAttributes.
    /// </summary>
    partial class Player
    {
        /// <summary>
        /// The highest slot number DESIGN.md defines (section 2). Slot 1 is the base build and always
        /// exists; slots 2-4 unlock by level via facet_slot2_level/3/4.
        /// </summary>
        public const int MaxFacetSlot = 4;

        /// <summary>
        /// Which slot the character is standing on. Absent or 1 means slot 1, the base build.
        /// </summary>
        public int ActiveFacetSlot
        {
            get => GetProperty(PropertyInt.ActiveFacetSlot) ?? 1;
            set
            {
                if (value <= 1)
                    RemoveProperty(PropertyInt.ActiveFacetSlot);
                else
                    SetProperty(PropertyInt.ActiveFacetSlot, value);
            }
        }

        /// <summary>
        /// Whether the one-time facet slot-2 unlock notice (<see cref="SendFacetUnlockNoticeIfDue"/>)
        /// has already been shown to this character. Absent or false means not yet shown.
        /// </summary>
        public bool FacetUnlockNoticeShown
        {
            get => GetProperty(PropertyBool.FacetUnlockNoticeShown) ?? false;
            set { if (!value) RemoveProperty(PropertyBool.FacetUnlockNoticeShown); else SetProperty(PropertyBool.FacetUnlockNoticeShown, value); }
        }

        /// <summary>
        /// The pricing DECISION, split out from the dat read so it can be unit-tested: ACE.Server.Tests
        /// has no client dat files, so anything that touches DatManager.PortalDat is unreachable there.
        /// Visible to the test assembly via InternalsVisibleTo (ACE.Server.csproj:15).
        ///
        /// Only the UPGRADE is ever zeroed. The trained cost is passed through untouched, because the
        /// engine really does charge it: an augmentation grants the specialization, never the training.
        /// </summary>
        internal static SkillCreditCost ComposeSkillCreditCost(int trainedCost, int upgradeCost, bool specializedFreeByAugmentation)
        {
            return new SkillCreditCost(trainedCost, specializedFreeByAugmentation ? 0 : upgradeCost);
        }

        /// <summary>
        /// What one skill costs THIS CHARACTER in skill credits. Kept behind a named method so
        /// FacetPools can stay DatManager-free and therefore unit-testable.
        ///
        /// An INSTANCE method, not a static one, and that is the whole point of it: the price has to be
        /// what the engine actually charged this character, not what the dat generically lists. The one
        /// place those disagree in a way that matters is augmentation-specialized skills. The engine
        /// specializes those for ZERO - Player_Skills.TrainSkill auto-calls SpecializeSkill(skill, 0,
        /// false) when the aug is held, and AugmentationDevice.DoAugmentation sets SAC to Specialized
        /// directly and charges experience only, never credits - while the dat's
        /// UpgradeCostFromTrainedToSpecialized for all five AugSpecSkills is 999.
        ///
        /// Why a generic price is not merely cosmetic here: a swap prices the outgoing and the incoming
        /// build and takes the difference, so any per-skill error cancels ONLY when that skill has the
        /// same SkillAdvancementClass on both sides. SAC is per-build, so it frequently does not. A
        /// stored row that predates the augmentation purchase holds the skill Trained while the live
        /// character has it Specialized, and the generic price then makes the swap release a raw 999
        /// credits against an engine charge of zero - repeatable per aug-spec skill, against a lifetime
        /// budget of 98 credits (114 Olthoi). The same asymmetry bites the other way after
        /// Player_Skills.ResetSkill takes a tinkering skill Specialized -> Trained without refunding the
        /// upgrade, which would strand the player on a permanent "short 999 skill credits" refusal with
        /// nothing they could untrain to recover.
        ///
        /// Pricing from the augmentation is safe precisely BECAUSE the augmentation is a global character
        /// property rather than a per-build one: it is identical on both sides of any one switch, so it
        /// cannot itself introduce an asymmetry.
        ///
        /// COVERAGE, stated plainly because the previous version of this fix shipped with none and two
        /// comments claiming otherwise. <see cref="ComposeSkillCreditCost"/> above carries the decision
        /// and is unit-tested directly; FacetPoolsTests also pins by reflection that THIS method is an
        /// instance method, which is the structural half of "do not revert it to a static generic dat
        /// read". What no test in this repo can reach is the three lines of wiring in this method itself -
        /// the dat read and the augmentation query both need a live DatManager and a live Player - so a
        /// change that kept the shape but passed a constant false would pass the suite. A live check is
        /// queued for that.
        /// </summary>
        public SkillCreditCost LookupSkillCreditCost(Skill skill)
        {
            var skillBase = DatManager.PortalDat.SkillTable.SkillBaseHash[(uint)skill];

            var specializedFreeByAugmentation = IsSkillSpecializedViaAugmentation(skill, out var hasAugmentation) && hasAugmentation;

            return ComposeSkillCreditCost((int)skillBase.TrainedCost, (int)skillBase.UpgradeCostFromTrainedToSpecialized, specializedFreeByAugmentation);
        }

        /// <summary>Reads the character's current skill state into a storable snapshot.</summary>
        public List<FacetSkillEntry> CaptureFacetSkills()
        {
            var entries = new List<FacetSkillEntry>();

            foreach (var kvp in Biota.PropertiesSkill)
            {
                entries.Add(new FacetSkillEntry
                {
                    Skill = kvp.Key,
                    Sac = kvp.Value.SAC,
                    Ranks = kvp.Value.LevelFromPP,
                    Pp = kvp.Value.PP,
                    InitLevel = kvp.Value.InitLevel,
                });
            }

            return entries;
        }

        /// <summary>
        /// The build a never-visited slot starts from: every untrainable skill reset to Untrained with
        /// no ranks and no invested XP. AlwaysTrained skills stay Trained because they cannot be
        /// untrained by design, and augmentation-specialized skills stay Specialized, matching what
        /// UnspecializeSkill already does for the aug-spec case.
        /// </summary>
        public List<FacetSkillEntry> BuildFreshFacetSkills()
        {
            var entries = new List<FacetSkillEntry>();

            foreach (var kvp in Biota.PropertiesSkill)
            {
                var skill = kvp.Key;

                var keepSpecialized = AugSpecSkills.Contains(skill)
                    && IsSkillSpecializedViaAugmentation(skill, out var hasAug)
                    && hasAug;

                SkillAdvancementClass sac;

                if (keepSpecialized)
                    sac = SkillAdvancementClass.Specialized;
                else if (!IsSkillUntrainable(skill))
                    sac = SkillAdvancementClass.Trained;
                else
                    sac = SkillAdvancementClass.Untrained;

                entries.Add(new FacetSkillEntry
                {
                    Skill = skill,
                    Sac = sac,
                    Ranks = 0,
                    Pp = 0,
                    InitLevel = sac == SkillAdvancementClass.Specialized ? 10u : 0u,
                });
            }

            return entries;
        }

        /// <summary>
        /// Writes a stored skill set onto the live character. Does NOT touch the pools - the caller
        /// derives those, because it is the only thing that knows both the outgoing and incoming builds.
        ///
        /// AUTHORITATIVE, not additive: the incoming list defines the character's whole skill state, and
        /// any live skill it does not name is reset to Untrained with zero ranks, zero PP and zero init
        /// level. Writing only the named skills would be a fail-open XP hole one layer below the
        /// <see cref="TryGetCharacterFacetRows"/> one: the caller has ALREADY released every point of the
        /// outgoing build's PP into AvailableExperience by the time this runs, so a short incoming list
        /// leaves the character holding both the un-rewritten skills and the experience that paid for
        /// them. The switch path refuses an unreadable skills_Json outright
        /// (<see cref="FacetSnapshot.TryDeserializeSkills"/>), and this sweep is the second half of the
        /// same guarantee: whatever list does arrive is applied in full, not merged over what was there.
        ///
        /// It also closes a smaller, ordinary case: a stored row predates a skill the biota has since
        /// gained (Creature.GetCreatureSkill adds a PropertiesSkill row on first access), so the live
        /// character can legitimately carry a skill no stored build names.
        ///
        /// One enqueue pass, not one per skill (DESIGN.md section 7.1), matching Player_Mule's own skill
        /// sweep: roughly 50 sends in the same tick that already carries the pool updates, the ability
        /// updates and the gear report is worth batching.
        /// </summary>
        public void ApplyFacetSkills(List<FacetSkillEntry> incoming)
        {
            if (incoming == null)
                return;

            var updates = new List<GameMessage>();
            var named = new HashSet<Skill>();

            foreach (var entry in incoming)
            {
                var creatureSkill = GetCreatureSkill(entry.Skill);

                if (creatureSkill == null)
                    continue;

                named.Add(entry.Skill);

                creatureSkill.AdvancementClass = entry.Sac;
                creatureSkill.Ranks = entry.Ranks;
                creatureSkill.ExperienceSpent = entry.Pp;
                creatureSkill.InitLevel = entry.InitLevel;

                updates.Add(new GameMessagePrivateUpdateSkill(this, creatureSkill));
            }

            // Snapshotted AFTER the loop above, deliberately: GetCreatureSkill(add: true) can add a
            // PropertiesSkill row for a named skill the biota did not have yet, so a key list taken before
            // the loop would miss it - and taking it live would mutate the collection being enumerated.
            foreach (var skill in new List<Skill>(Biota.PropertiesSkill.Keys))
            {
                if (named.Contains(skill))
                    continue;

                var creatureSkill = GetCreatureSkill(skill);

                if (creatureSkill == null)
                    continue;

                creatureSkill.AdvancementClass = SkillAdvancementClass.Untrained;
                creatureSkill.Ranks = 0;
                creatureSkill.ExperienceSpent = 0;
                creatureSkill.InitLevel = 0;

                updates.Add(new GameMessagePrivateUpdateSkill(this, creatureSkill));
            }

            if (updates.Count > 0)
                Session?.Network.EnqueueSend(updates.ToArray());
        }

        /// <summary>
        /// The character's current attribute REDISTRIBUTION - the six primary attributes' InitLevel,
        /// read as CreatureAttribute.StartingValue. This is the half AttributeTransferDevice moves, and
        /// it is the only attribute state a facet stores; ranks and CPSpent (the XP-bought half) stay
        /// global, as do the vital records, augmentations, enlightenment and level.
        ///
        /// ITERATES THE SIX NAMED VALUES, never Biota.PropertiesAttribute. That collection can carry
        /// keys outside Strength..Self - DeveloperFixCommands' verify-attributes exists specifically to
        /// find and remove them - and a stray key folded into a stored arrangement would make the live
        /// and stored sums disagree for a reason that has nothing to do with the player's redistribution,
        /// which is what the conservation check would then report.
        ///
        /// Read through Attributes[...] rather than off the biota, so a character whose biota is missing
        /// an attribute row gets one created (CreatureAttribute's constructor does that) instead of the
        /// attribute silently dropping out of the arrangement. All six keys are populated
        /// unconditionally by Creature's constructor, so the indexer cannot miss.
        ///
        /// Because this copies the LIVE values, a capture can never break the conservation invariant:
        /// the row it writes sums, by construction, to exactly what the live character sums to. Only the
        /// apply side can drift - see FacetAttributes.
        /// </summary>
        public Dictionary<PropertyAttribute, uint> CaptureFacetAttributes()
        {
            var arrangement = new Dictionary<PropertyAttribute, uint>();

            foreach (var attribute in FacetAttributes.PrimaryAttributes)
                arrangement[attribute] = Attributes[attribute].StartingValue;

            return arrangement;
        }

        /// <summary>
        /// The attribute arrangement a never-visited slot starts from: the character's CURRENT live one.
        ///
        /// NOT a reset and NOT an even split, and that is a decision rather than a shortcut. Creation-time
        /// values are not stored anywhere on the character, so there is nothing to reset TO; and a uniform
        /// split would cripple a level-300 character the first time they stepped onto a fresh facet, for
        /// a redistribution they would then have to buy transfer devices to undo. Inheriting also makes
        /// conservation trivially true on a first visit, since the stored sum IS the live sum.
        ///
        /// Deliberately kept as its own named method rather than an alias, so the fresh-slot decision has
        /// somewhere to be documented and a future change to it cannot silently alter capture as well.
        /// </summary>
        public Dictionary<PropertyAttribute, uint> BuildFreshFacetAttributes()
        {
            return CaptureFacetAttributes();
        }

        /// <summary>
        /// Writes a reconciled attribute arrangement onto the live character and pushes everything the
        /// client needs to redraw itself. A null arrangement is a no-op, which is how the caller says
        /// "keep the live arrangement" for a row that predates this column or could not be read.
        ///
        /// The four sends, and why each is unconditional:
        ///
        ///   1. GameMessagePrivateUpdateAttribute x6. Precedent is AttributeTransferDevice, which sends
        ///      exactly these for the two attributes it moved and redistributes live with no relog. Sent
        ///      for all six rather than only the changed ones because a facet switch is not a
        ///      single-attribute edit and the cost of six UIQueue messages once per manual command is
        ///      nothing.
        ///
        ///   2. GameMessagePrivateUpdateVital x3 - Health, Stamina and Mana, ALL THREE. The conditional
        ///      in Player_Attributes.HandleActionRaiseAttribute (Endurance -> health, Self -> mana) was
        ///      written for raising ONE attribute by one rank; a facet switch can move all six at once,
        ///      and maximum stamina derives from Endurance as well. Max vitals are derived from
        ///      attributes at read time (AttributeFormula / CreatureVital.GetMaxValue), so no stored
        ///      vital record changes - but the CLIENT rebuilds the maximum itself from the terms these
        ///      messages carry, so it has to be told.
        ///
        ///   3. Clamp each current vital to its new maximum and push it. Do NOT lean on VitalHeartBeat's
        ///      clamp instead: it is up to ~5 seconds late, and in the meantime CreatureVital.Missing is
        ///      a uint subtraction (MaxValue - Current) that WRAPS to roughly four billion whenever
        ///      Current exceeds MaxValue. That value is read by Creature_Vitals.SetMaxVitals, Healer and
        ///      two sites in WorldObject_Magic, so the window is reachable by ordinary play, not just by
        ///      inspection. Player.UpdateVital enqueues the level message itself, so the clamp and the
        ///      push are one call. Precedent: HandleMaxHealthUpdate does exactly this clamp-and-push on
        ///      every equip and dequip.
        ///
        ///   4. HandleRunRateUpdate once, and only if Strength or Quickness actually changed, under the
        ///      same runrate_add_hooks gate HandleActionRaiseAttribute uses for the same hook.
        ///
        /// NOTHING IS NEEDED FOR BURDEN. PropertyManager's own remarks on the encumbrance tunable record
        /// (verified in game 2026-08-02) that the client derives the burden bar from Strength itself and
        /// ignores a server-sent EncumbranceCapacity, so the six attribute sends above already move it.
        ///
        /// ON ORDERING AGAINST ApplyFacetAbilities, which runs immediately after this: the attribute
        /// message carries NetworkStartingValue, which folds in the getter-only "Enhanced &lt;attribute&gt;"
        /// class ability bonus - and class abilities ARE per-facet, so at this instant that term is still
        /// the OUTGOING facet's. That is harmless in both directions. If the ability's rank is unchanged
        /// between the two facets the term is identical anyway; if it changed, TrySwitchFacet's own
        /// SendEnhancedStatUpdate loop - which runs after ApplyFacetAbilities, precisely so the live cache
        /// is already correct - re-sends GameMessagePrivateUpdateAttribute for that attribute, and the
        /// later message is the one the client keeps.
        /// </summary>
        public void ApplyFacetAttributes(Dictionary<PropertyAttribute, uint> incoming)
        {
            if (incoming == null)
                return;

            var updates = new List<GameMessage>();
            var runRateAffected = false;

            foreach (var attribute in FacetAttributes.PrimaryAttributes)
            {
                var creatureAttribute = Attributes[attribute];

                if (incoming.TryGetValue(attribute, out var value) && creatureAttribute.StartingValue != value)
                {
                    if (attribute == PropertyAttribute.Strength || attribute == PropertyAttribute.Quickness)
                        runRateAffected = true;

                    creatureAttribute.StartingValue = value;
                }

                updates.Add(new GameMessagePrivateUpdateAttribute(this, creatureAttribute));
            }

            updates.Add(new GameMessagePrivateUpdateVital(this, Health));
            updates.Add(new GameMessagePrivateUpdateVital(this, Stamina));
            updates.Add(new GameMessagePrivateUpdateVital(this, Mana));

            Session?.Network.EnqueueSend(updates.ToArray());

            // After the writes above, not before: MaxValue is derived from the attributes at read time,
            // so this is the first point at which the new maximum exists to clamp against.
            foreach (var vital in new[] { Health, Stamina, Mana })
            {
                if (vital.Current > vital.MaxValue)
                    UpdateVital(vital, vital.MaxValue);
            }

            if (runRateAffected && PropertyManager.GetBool("runrate_add_hooks").Item)
                HandleRunRateUpdate();
        }

        /// <summary>
        /// The character's learned class abilities, keyed by ability NAME. Names rather than enum
        /// values, so a future enum reordering cannot repoint a stored facet at a different ability -
        /// the same reason ClassAbilityRegistry.QuestKey uses the name.
        /// </summary>
        public Dictionary<string, int> CaptureFacetAbilities()
        {
            var abilities = new Dictionary<string, int>();

            foreach (var (id, rank) in GetClassAbilityCache())
            {
                if (rank > 0 && ClassAbilityRegistry.Abilities.TryGetValue(id, out var definition))
                    abilities[definition.Name] = rank;
            }

            return abilities;
        }

        /// <summary>
        /// What a stored ability set commits in class ability points. Recomputed from CumulativeCost
        /// rather than tracked incrementally, so it stays correct if per-rank costs are ever retuned.
        /// </summary>
        public int ClassAbilityPointsSpent(Dictionary<string, int> abilities)
        {
            if (abilities == null)
                return 0;

            var total = 0;

            foreach (var pair in abilities)
            {
                if (ClassAbilityRegistry.TryGetByName(pair.Key, out var definition))
                    total += definition.CumulativeCost(pair.Value);
            }

            return total;
        }

        /// <summary>
        /// Replaces the character's learned class abilities with a stored set. Quest rows are written
        /// directly through the Character extensions, bypassing QuestManager.SetQuestCompletions, which
        /// clamps unknown quests to zero solves - the same path the trainer already uses.
        ///
        /// Every currently-owned ability's row that is NOT also present in the incoming set is ERASED
        /// (Character.EraseQuest), not zeroed, matching how ClassAbilityTrainer.HandleRespec clears a
        /// full respec via Player.UnlearnClassAbility -> Character.EraseQuest. A zero-rank row left
        /// behind here would put a facet-switched character in a different database state than a
        /// respecced one for the same logical "unlearned" outcome.
        ///
        /// An ability that DOES appear in both the outgoing and incoming set (the ordinary case for two
        /// builds sharing an ability, not an edge case) is deliberately NOT erased-and-rewritten. Both
        /// EraseQuest and GetOrCreateQuest key off the same EF composite key
        /// (ShardDbContext: HasKey(CharacterId, QuestName) on CharacterPropertiesQuestRegistry), and
        /// this method's whole batch runs inside the ONE long-lived per-character ShardDbContext with no
        /// SaveChanges in between. Erasing an id and then recreating it in the same call would hand that
        /// context's identity map a Deleted instance and an Added instance carrying an identical
        /// composite key, which throws "cannot be tracked because another instance with the same key
        /// value is already being tracked" at the next SaveChanges. LearnClassAbility and
        /// UnlearnClassAbility never hit this because each flushes (SaveCharacterToDatabase)
        /// immediately after its own single mutation; batching a whole build swap into one context
        /// generation is what exposes it. So a retained ability is instead left alone in the erase pass
        /// and updated IN PLACE by GetOrCreateQuest in the write pass below - identical to what the
        /// single-ability Learn/Unlearn path already does for a rank change.
        ///
        /// Tier gating needs no explicit reset: MeetsClassAbilityTierUnlock reads PointsSpentInClass,
        /// which is recomputed from the live cache on every call, so a set with nothing learned
        /// re-locks Tier 2 and Tier 3 on its own.
        ///
        /// Does NOT touch AvailableClassAbilityPoints or TotalClassAbilityPointsEarned - the caller carries
        /// the available balance forward as a DELTA (release the outgoing build's spend, commit the
        /// incoming build's), because only the caller knows both builds. Deliberately NOT a recompute from
        /// the lifetime ledger: points are also a vendor currency and back prepaid training vouchers, so a
        /// ledger derivation refunds every such spend. See FacetPools.AvailableClassAbilityPointsAfterSwap.
        /// </summary>
        public void ApplyFacetAbilities(Dictionary<string, int> incoming)
        {
            incoming ??= new Dictionary<string, int>();

            var cache = GetClassAbilityCache();

            // Resolve the incoming set up front, with the SAME name lookup and rank clamp the write pass
            // below applies, so "retained" below means EXACTLY what the write pass is about to persist:
            // an incoming entry that resolves to an unknown name, or clamps to rank 0, is NOT retained
            // and falls through to the erase pass like any other dropped ability.
            var resolved = new Dictionary<ClassAbilityId, (ClassAbilityDefinition definition, int rank)>();

            foreach (var pair in incoming)
            {
                if (!ClassAbilityRegistry.TryGetByName(pair.Key, out var definition))
                    continue;   // an ability that no longer exists is dropped, not an error

                var rank = Math.Clamp(pair.Value, 0, definition.MaxRank);

                if (rank > 0)
                    resolved[definition.Id] = (definition, rank);
            }

            foreach (var existing in new List<ClassAbilityId>(cache.Keys))
            {
                if (resolved.ContainsKey(existing))
                    continue;   // retained - updated in place by GetOrCreateQuest below, never erased

                if (ClassAbilityRegistry.Abilities.TryGetValue(existing, out var definition)
                    && Character.EraseQuest(ClassAbilityRegistry.QuestKey(definition), CharacterDatabaseLock))
                {
                    CharacterChangesDetected = true;
                }
            }

            // Rebuilt from `resolved` below rather than merged, so the cache ends up EXACTLY equal to the
            // incoming set - no stale entry for an ability that was erased above, no missing entry for one
            // that was retained (GetOrCreateQuest returns the SAME tracked entity for a retained ability;
            // its rank is overwritten unconditionally just below, not only on the freshly-created path).
            cache.Clear();

            foreach (var (id, (definition, rank)) in resolved)
            {
                var quest = Character.GetOrCreateQuest(ClassAbilityRegistry.QuestKey(definition), CharacterDatabaseLock, out var created);

                if (created)
                    quest.CharacterId = Guid.Full;   // same value QuestManager.IDtoUseForQuestRegistry uses for players

                quest.NumTimesCompleted = rank;
                quest.LastTimeCompleted = (uint)Time.GetUnixTime();

                cache[id] = rank;
            }

            CharacterChangesDetected = true;
            InvalidateClassAbilityHookCaches();
        }

        /// <summary>The character's currently worn set, as a storable snapshot.</summary>
        public List<FacetEquipEntry> CaptureFacetEquip()
        {
            var entries = new List<FacetEquipEntry>();

            foreach (var item in EquippedObjects.Values)
            {
                entries.Add(new FacetEquipEntry
                {
                    Guid = item.Guid.Full,
                    Wcid = item.WeenieClassId,
                    Slot = (int)(item.CurrentWieldedLocation ?? 0),
                });
            }

            return entries;
        }

        /// <summary>
        /// Does the character have room for the whole worn set, in the slots THIS MOVE can actually reach?
        /// Returns the refusal to show, or null when there is room. Split out from
        /// <see cref="TryStripForFacetSwitch"/> the same way <see cref="ComposeSkillCreditCost"/> and
        /// <see cref="ShouldShowFacetUnlockNotice"/> are, because ACE.Server.Tests cannot construct a live
        /// Player and so cannot reach anything that reads a capacity off one.
        ///
        /// TWO POOLS, NOT ONE TOTAL. Container.TryAddToInventory charges an item with UseBackpackSlot (a
        /// container, or anything with RequiresPackSlot) against ContainerCapacity and everything else
        /// against the main pack's ItemCapacity. A single combined free-slot number cannot answer the
        /// question for a set that contains both kinds, so both are checked independently here.
        ///
        /// MAIN PACK ONLY. The caller passes GetFreeInventorySlots(includeSidePacks: false), not the
        /// default. Every item is stripped by HandleActionPutItemInContainer with the player as the
        /// container, and DoHandleActionPutItemInContainer forwards that as
        /// container.TryAddToInventory(item, placement, limitToMainPackOnly: TRUE, burdenCheck)
        /// (Player_Inventory.cs) - so a free SIDE-pack slot cannot take a stripped item at all, even though
        /// GetFreeInventorySlots() counts side packs by default (Container.cs). Before this split, the
        /// precheck used that default: a character with a full main pack and room in a side pack passed it,
        /// then failed partway through the loop and got the partial-strip refusal, which names no cause the
        /// player could act on. NOTE what this method therefore does NOT cover on its own - which overload
        /// the caller reads is a decision at the call site, outside these parameters.
        /// </summary>
        internal static string ComposeStripRoomRefusal(int mainPackItemCount, int packSlotItemCount, int freeMainPackSlots, int freeContainerSlots)
        {
            if (freeMainPackSlots < mainPackItemCount)
            {
                var needed = mainPackItemCount - freeMainPackSlots;

                return $"You need {needed} more free slot{(needed == 1 ? "" : "s")} in your MAIN pack before you can change facets. Gear is unequipped into the main pack only, so free space in a side pack does not count.";
            }

            if (freeContainerSlots < packSlotItemCount)
            {
                var needed = packSlotItemCount - freeContainerSlots;

                return $"You need {needed} more free pack slot{(needed == 1 ? "" : "s")} before you can change facets.";
            }

            return null;
        }

        /// <summary>
        /// Moves every equipped item into the character's own packs, after checking there is room for
        /// all of it. Refuses up front rather than stripping half a build and stopping.
        ///
        /// Uses HandleActionPutItemInContainer per item, which is what /myrespec does. Do NOT simplify
        /// this to TryDequipObjectWithNetworking plus TryCreateInInventoryWithNetworking: that pair
        /// issues a spurious GameMessageCreateObject for a guid the client already holds.
        ///
        /// There is no wired WeenieError for a full pack - YouAreTooEncumbered and FullInventoryLocation
        /// both exist in the enum with zero usages anywhere in the server - so the refusal is a plain
        /// system chat line, again matching /myrespec.
        ///
        /// PRECONDITION this method enforces itself, and why: HandleActionPutItemInContainer_Verify's
        /// very first real check is `if (IsBusy) { ...; return false; }` (Player_Inventory.cs:963-974) -
        /// a gate that fires before anything this method's own reasoning below is about, and that has
        /// nothing to do with combat stance. IsBusy covers far more than "mid-cast": teleporting, vendor
        /// commerce, death, doors, pet devices, healers, and the ~1 second post-cast recoil window
        /// FinishCast sets and only its own delayed ActionChain clears (Player_Magic.cs:998-1042) all set
        /// it too. If IsBusy is true when this method is called, EVERY item below would independently
        /// fail this same check and nothing would move (worse: the FIRST item can install a deferred
        /// NextPickup continuation for itself alone if PickupState is mid-Return, silently dropping every
        /// item after it) - so this method refuses up front rather than attempting moves that cannot
        /// land.
        ///
        /// Sequencing, once IsBusy is confirmed false above: HandleActionPutItemInContainer completes
        /// inline for an equip-to-own-pack move UNLESS RequiresStanceSwap is true for the item's wielded
        /// location while the player is in a combat stance, in which case the dequip is routed through
        /// HandleActionChangeCombatMode and can complete on a LATER tick via an ActionChain
        /// (RequiresStanceSwap short-circuits to false the moment CombatMode == NonCombat - see
        /// Player_Inventory.cs:605-624). This method closes that path itself rather than delegating to
        /// HandleActionChangeCombatMode (which would reintroduce its OWN possible ActionChain delay when
        /// NextUseTime has not yet elapsed - Player_Combat.cs:865-900 - defeating the point): it mirrors
        /// HandleActionChangeCombatMode_Inner's own cleanup (Player_Combat.cs:915-918) - failing an
        /// in-progress cast and cancelling an in-progress attack - before flipping CombatMode directly via
        /// SetCombatMode, so no cast or attack is left abandoned mid-flight by the forced stance change,
        /// and RequiresStanceSwap is false for every item in the loop below.
        ///
        /// HandleActionPutItemInContainer returns void, so a single item's failure inside the loop is NOT
        /// observable from that call - this is the same limit /myrespec lives with, which is why it calls
        /// its own count "movesRequested" rather than "completed". What actually verifies the strip landed
        /// is the EquippedObjects.Count check after the loop: given the preconditions enforced above hold
        /// (not IsBusy, no stale cast/attack), every item's move is expected to resolve synchronously with
        /// no ActionChain, so a nonzero remainder there is a genuine, observable failure signal - and also
        /// a live assertion that the synchronicity reasoning above still holds, not merely documentation
        /// of an assumption a future change to HandleActionPutItemInContainer could silently invalidate.
        /// </summary>
        public bool TryStripForFacetSwitch(out string refusal)
        {
            refusal = null;

            var equipped = new List<WorldObject>(EquippedObjects.Values);

            if (equipped.Count == 0)
                return true;

            if (IsBusy)
            {
                refusal = "You are too busy to change facets right now. Try again in a moment.";
                return false;
            }

            // includeSidePacks is FALSE deliberately, and it is the whole of this fix - see
            // ComposeStripRoomRefusal for why a side-pack slot cannot take a stripped item.
            var packSlotItemCount = equipped.Count(i => i.UseBackpackSlot);

            refusal = ComposeStripRoomRefusal(
                equipped.Count - packSlotItemCount,
                packSlotItemCount,
                GetFreeInventorySlots(includeSidePacks: false),
                GetFreeContainerSlots());

            if (refusal != null)
                return false;

            if (CombatMode != CombatMode.NonCombat)
            {
                // Mirrors HandleActionChangeCombatMode_Inner's own cleanup (Player_Combat.cs:915-918)
                // rather than calling HandleActionChangeCombatMode itself, which can enqueue its own
                // ActionChain delay when NextUseTime has not yet elapsed - reintroducing exactly the
                // asynchrony this method exists to close off.
                if (CombatMode == CombatMode.Magic && MagicState.IsCasting)
                    FailCast();

                HandleActionCancelAttack();

                SetCombatMode(CombatMode.NonCombat);
            }

            foreach (var item in equipped)
                HandleActionPutItemInContainer(item.Guid.Full, Guid.Full);

            if (EquippedObjects.Count > 0)
            {
                // Task 11 fix round 1, Finding 3: once the loop above has started, a single-item failure
                // leaves SOME items already moved to the pack and some still worn - this is data-safe (no
                // skills/pools/ActiveFacetSlot mutation follows a false return here) but the message
                // must say so explicitly, not just imply it via the remaining-equipped count.
                //
                // The earlier wording ("some gear may already be in your pack") was too weak for the state
                // this check actually catches most often. When an item is registered in BOTH Inventory and
                // EquippedObjects, HandleActionPutItemInContainer resolves it as an inventory item
                // (FindObject searches MyInventory before MyEquippedItems) and reorders it in place instead
                // of dequipping it, so the item is genuinely in the pack AND worn at the same time - not
                // "may be". Say that, because a player who reads "may already be in your pack" will go
                // looking for missing gear that is in fact right there.
                refusal = $"Something prevented your gear from fully stripping; {EquippedObjects.Count} item(s) are still equipped. Gear that did move is now in your pack, and an item that is still equipped may be taking up a pack slot at the same time. Your facet, skills and pools were not changed. Please try again, and contact staff if this repeats.";
                return false;
            }

            return true;
        }

        /// <summary>
        /// The account vault enumerated exactly once for one facet switch: the set of stored-item
        /// guids and the wcid-to-count ledger map <see cref="FacetGear.ResolveAll"/> needs, plus the
        /// actual <see cref="VaultEntry"/> objects a withdrawal will need. There is no guid-to-vault
        /// index anywhere in the schema (account_vault_log.item_Guid is write-only audit, never
        /// queried by value), so resolution is "enumerate my own vault", never "ask where an item
        /// went" - and it happens ONCE per switch, never once per remembered entry.
        /// </summary>
        private readonly struct FacetVaultLookup
        {
            public FacetVaultLookup(AccountVaultStore store, ISet<uint> itemGuids, IReadOnlyDictionary<uint, int> ledgerCounts,
                Dictionary<uint, VaultEntry> entriesByGuid, Dictionary<uint, VaultEntry> ledgerEntriesByWcid,
                bool notReady, string notReadyReason)
            {
                Store = store;
                ItemGuids = itemGuids;
                LedgerCounts = ledgerCounts;
                EntriesByGuid = entriesByGuid;
                LedgerEntriesByWcid = ledgerEntriesByWcid;
                NotReady = notReady;
                NotReadyReason = notReadyReason;
            }

            /// <summary>Null when the character has no resolvable account or the vault is not ready.</summary>
            public AccountVaultStore Store { get; }

            public ISet<uint> ItemGuids { get; }

            public IReadOnlyDictionary<uint, int> LedgerCounts { get; }

            public Dictionary<uint, VaultEntry> EntriesByGuid { get; }

            public Dictionary<uint, VaultEntry> LedgerEntriesByWcid { get; }

            /// <summary>
            /// True when the vault itself could not be consulted at all (no resolvable account, or
            /// AccountVaultStore.TryCheckReady refused - e.g. still loading). Distinguishes "your vault
            /// could not be checked" from a genuine "that item is gone" for every entry that falls
            /// through to FacetGearSource.NotFound, which would otherwise look identical to the player.
            /// </summary>
            public bool NotReady { get; }

            /// <summary>AccountVaultStore.TryCheckReady's own failReason, when NotReady is true.</summary>
            public string NotReadyReason { get; }
        }

        /// <summary>
        /// Builds <see cref="FacetVaultLookup"/> for this character's own account. A missing or
        /// not-ready store degrades to an empty lookup rather than throwing - every VaultItem/VaultLedger
        /// resolution then simply falls through to NotFound, which RestoreFacetEquip already reports
        /// by name, matching "a switch always completes".
        ///
        /// Group vault entries (several whole biotas bucketed onto one panel line) are deliberately
        /// skipped rather than resolved: grouping only ever applies to ItemType.TinkeringMaterial
        /// (AccountVaultStore.IsGroupCandidate), never to wearable gear, so a real facet item can
        /// only ever be a standalone stored item or a ledger stack. Treating a group's representative
        /// guid as a match here would risk withdrawing a DIFFERENT member than the one actually
        /// remembered, since AccountVaultStore.TryWithdraw takes a group's members from the front or
        /// back of the bucket, not by the specific guid asked for.
        /// </summary>
        private FacetVaultLookup BuildFacetVaultLookup()
        {
            var itemGuids = new HashSet<uint>();
            var ledgerCounts = new Dictionary<uint, int>();
            var entriesByGuid = new Dictionary<uint, VaultEntry>();
            var ledgerEntriesByWcid = new Dictionary<uint, VaultEntry>();

            var accountId = Account?.AccountId ?? 0;
            var store = AccountVaultManager.GetStore(accountId);

            if (store == null)
                return new FacetVaultLookup(null, itemGuids, ledgerCounts, entriesByGuid, ledgerEntriesByWcid, true, "no account vault could be resolved");

            if (!store.TryCheckReady(out var notReadyReason))
                return new FacetVaultLookup(null, itemGuids, ledgerCounts, entriesByGuid, ledgerEntriesByWcid, true, notReadyReason);

            foreach (var entry in store.GetEntries(0, -1))
            {
                if (entry.IsLedger)
                {
                    ledgerCounts[entry.Wcid] = ledgerCounts.TryGetValue(entry.Wcid, out var existing)
                        ? existing + (int)entry.Count
                        : (int)entry.Count;

                    ledgerEntriesByWcid[entry.Wcid] = entry;
                    continue;
                }

                if (entry.IsGroup)
                    continue;

                itemGuids.Add(entry.Guid.Full);
                entriesByGuid[entry.Guid.Full] = entry;
            }

            return new FacetVaultLookup(store, itemGuids, ledgerCounts, entriesByGuid, ledgerEntriesByWcid, false, null);
        }

        /// <summary>
        /// The report line for an entry that resolved to nothing usable (FacetGearSource.NotFound, or
        /// a VaultItem/VaultLedger resolution whose backing entry vanished between resolution and
        /// withdrawal). Distinguishes "your vault could not be checked" (lookup.NotReady) from a genuine
        /// "that item is gone" - see FacetVaultLookup.NotReady's remarks for why conflating the two
        /// would hide the one case the player can do something about (try the switch again once the
        /// vault has finished loading) behind the one they cannot.
        /// </summary>
        private static string ItemNotRestoredMessage(FacetVaultLookup lookup, FacetGearResolution resolution)
        {
            var label = FacetItemLabel(resolution.Wcid, null);

            if (lookup.NotReady)
                return $"Your vault could not be checked for {label}: {lookup.NotReadyReason ?? "vault unavailable"}. It was not restored this time.";

            return $"Could not find {label}.";
        }

        /// <summary>
        /// How a remembered worn item is named in a report line the player reads. DESIGN.md section 6
        /// requires an unresolved entry be listed BY NAME with its reason; a raw wcid is not a name, and
        /// the player has no way to turn one into an item.
        ///
        /// Three sources, in order of how specific they are:
        ///   - <paramref name="known"/>, when the caller is holding the actual object (a stored vault
        ///     entry's VaultEntry.WorldObject, or a freshly withdrawn item). NameWithMaterial is used
        ///     rather than Name so a "Silver Koujia Coat" does not report as a bare "Koujia Coat",
        ///     matching what the already-correct sites in this file print.
        ///   - the world weenie for the wcid, for a ledger row (whose biota was destroyed when the vault
        ///     collapsed it, so there is no object to name) and for an entry that resolved to nothing.
        ///   - the wcid itself, only when the world database cannot name the weenie at all - a wcid that
        ///     has since been removed from the world DB. Keeping the number in that last case is
        ///     deliberate: it is useless to the player but it is the only thing staff can act on, and the
        ///     alternative is a report line that names nothing.
        ///
        /// A ledger-sourced entry is named from its TEMPLATE, which is exactly right for it: the vault
        /// only collapses an item into a ledger row when it is provably identical to that template, so
        /// the template's name is the withdrawn object's name.
        /// </summary>
        private static string FacetItemLabel(uint wcid, WorldObject known)
        {
            if (known != null && !string.IsNullOrWhiteSpace(known.NameWithMaterial))
                return known.NameWithMaterial;

            var weenieName = DatabaseManager.World.GetCachedWeenie(wcid)?.GetName();

            return string.IsNullOrWhiteSpace(weenieName)
                ? $"the item you had in that slot (wcid {wcid})"
                : weenieName;
        }

        /// <summary>
        /// The three distinguishable outcomes of trying to return a withdrawn object to the vault. A
        /// bare bool cannot say all three, and collapsing Threw into Refused would assert a state
        /// (the item is in a known, safe place) that a throw specifically means is no longer known.
        /// </summary>
        private enum VaultReturnOutcome
        {
            /// <summary>The object landed back in the vault under a known, correct state.</summary>
            Returned,

            /// <summary>
            /// A clean refusal (or the store had already been retired) - the vault declined it, or the
            /// call never ran, but nothing threw and the item's state is fully known: still an
            /// unparented WorldObject the caller holds, never delivered anywhere.
            /// </summary>
            Refused,

            /// <summary>
            /// The underlying call threw. See TryReturnWithdrawnToVault's remarks - the vault's state
            /// may be INCONSISTENT (a credit or an inventory mutation may have landed before the throw),
            /// so this must be treated as "needs reconciliation", never as a plain failure, and the same
            /// item must NEVER be retried - see the remarks for the double-credit risk that creates.
            /// </summary>
            Threw,
        }

        /// <summary>
        /// Returns a withdrawn-but-undelivered object to the vault it came out of, through the path
        /// matching its SOURCE (TryReturnWithdrawnToLedger for a ledger withdrawal, TryReturnWithdrawn
        /// for a stored item). Uses the two-arg Enqueue(Action, out Exception) overload, not the one-arg
        /// convenience form - the same fix WithdrawAndRestoreFromVault's own withdraw call needed, and
        /// for the identical reason: the one-arg form leaves a thrown exception indistinguishable from a
        /// clean refusal, because Drain catches and swallows a work item's exception without rethrowing.
        ///
        /// A throw here is NOT equivalent to a refusal, and the two vault methods this dispatches to
        /// each document their own version of why. TryReturnWithdrawnToLedger's own doc comment names a
        /// throw from ApplyLedgerCount/InvalidateLedger landing AFTER the ledger credit but BEFORE the
        /// object is destroyed - a caller that cannot see the throw does not know the ledger was already
        /// credited, and a caller that then RETRIES the same item double-credits it. TryReturnWithdrawn
        /// (non-ledger) has the same shape: TryAddToInventory has already mutated the vault's in-memory
        /// inventory and set the item's ContainerId before the trailing SaveBiota call can throw, so a
        /// throw there leaves a runtime/DB split, not the clean "never delivered anywhere" state a
        /// Refused result asserts. Either way, the vault's state is unknown afterward, not merely
        /// unchanged - hence Threw is its own outcome rather than folded into Refused.
        ///
        /// The RETURN value itself is also never assumed on a clean call: TryReturnWithdrawn has several
        /// return-false guards (vault capacity chief among them), each commented "the caller still holds
        /// it and must not destroy it", so a caller that assumes success on every call is the Destroy()
        /// the vault's contract forbids, minus the audit trail.
        ///
        /// NO CODE PATH IN THIS FILE RETRIES A Threw ITEM. Both call sites (in WithdrawAndRestoreFromVault)
        /// report it and stop; RestoreFacetEquip processes each remembered entry exactly once per call
        /// and never loops back onto one. If a future change ever adds a retry of a failed restore, it
        /// MUST exclude a Threw item explicitly - retrying it risks double-crediting the ledger for a
        /// ledger-sourced withdrawal, per the doc comment above.
        /// </summary>
        private VaultReturnOutcome TryReturnWithdrawnToVault(FacetVaultLookup lookup, VaultEntry entry, bool isLedger, WorldObject item)
        {
            var actor = VaultActor.From(this);
            var returned = false;

            var ran = lookup.Store.Enqueue(() =>
            {
                returned = isLedger
                    ? lookup.Store.TryReturnWithdrawnToLedger(item, entry.Wcid, item.StackSize ?? 1, actor)
                    : lookup.Store.TryReturnWithdrawn(item, actor);
            }, out var thrown);

            if (!ran)
                return VaultReturnOutcome.Refused;   // store retired - nothing ran, nothing changed

            if (thrown != null)
            {
                log.Error($"[FACET] {Name} (0x{Guid.Full:X8}): AccountVaultStore.{(isLedger ? "TryReturnWithdrawnToLedger" : "TryReturnWithdrawn")} threw returning 0x{item.Guid.Full:X8} (wcid {entry.Wcid}) to the vault. Its state may be INCONSISTENT and needs manual reconciliation against account_vault_log. Do NOT retry this item - see TryReturnWithdrawnToVault's remarks for the double-credit risk. {thrown}");
                return VaultReturnOutcome.Threw;
            }

            return returned ? VaultReturnOutcome.Returned : VaultReturnOutcome.Refused;
        }

        /// <summary>
        /// Detaches an item from the pack it is sitting in and then equips it, which is the ONLY correct
        /// order and the whole of the fix this method exists for.
        ///
        /// SCOPE: the INVENTORY restore path only - an item the character already had in its own packs,
        /// and which the client therefore already knows about and already draws in a pack slot. The vault
        /// path does NOT come through here any more and must not be routed back through it; see
        /// <see cref="TryEquipWithdrawnForFacet"/> for why an object the client has never heard of needs
        /// the opposite treatment (no detach message at all, and one trailing GameMessageCreateObject).
        /// The two paths are deliberately different because the client's starting belief about the object
        /// is different, not because they drifted.
        ///
        /// WHY THE DETACH IS MANDATORY. TryEquipObjectWithNetworking -> Creature_Equipment.TryEquipObject
        /// sets CurrentWieldedLocation/WielderId and adds the object to EquippedObjects, and that is all it
        /// does: it never clears ContainerId and never removes the object from Container.Inventory. The
        /// player-facing wield path does the detach itself, in its CALLER, immediately before the same
        /// call - Player_Inventory.DoHandleActionGetAndWieldItem, whose own comment there says it plainly:
        /// "Fail closed. Equipping an item we failed to detach leaves it both wielded and still parented to
        /// its old container, which is a duplication on the next load." This method is that block, in the
        /// shape it has there, so the two read the same to anyone comparing them.
        ///
        /// "A duplication on the next load" is literal, not a worry. ShardDatabase loads a character's
        /// possessions with two independent queries - GetInventoryInParallel by PropertyInstanceId.Container
        /// and GetWieldedItemsInParallel by PropertyInstanceId.Wielder - and Player's ctor feeds the first
        /// to SortBiotasIntoInventory and the second to AddBiotasToEquippedObjects. One biota carrying both
        /// ids is therefore materialized as TWO separate WorldObject instances sharing one guid, one in the
        /// pack and one worn. Player_Inventory.LogDuplicatePossession already names this exact state as
        /// corrupt shard data ("type 2 (Container) and type 3 (Wielder) must not both be set").
        ///
        /// It also silently broke the NEXT strip, which is how the bug was found. FindObject searches
        /// MyInventory before MyEquippedItems, so a double-registered item resolves with wasEquipped FALSE;
        /// DoHandleActionPutItemInContainer then takes its "movement within the same pack" branch, removes
        /// the item from Inventory and adds it straight back, and never reaches TryDequipObjectWithNetworking.
        /// The strip reports N items still equipped and the player cannot switch again.
        ///
        /// ON FAILURE. A failed detach refuses before equipping, exactly as the reference block does - the
        /// point of the check is that a half-attached equip is worse than no equip. A failed EQUIP puts the
        /// item back in the pack, which the reference block leaves as a todo but Monster_Inventory's
        /// EquipInventoryItems already does ("if (!success) TryAddToInventory(item)"); that precedent is
        /// followed here rather than the todo, because a facet restore runs unattended over a whole worn
        /// set and an orphaned object would be a silent loss. Every branch adds a report line: a switch
        /// always completes, so the player has to be told which pieces did not go back on.
        ///
        /// TELLING THE CLIENT THE ITEM LEFT THE PACK, which the server-state fix does NOT do by itself.
        /// Container.TryRemoveFromInventory sends no network message at all - it mutates the dictionary and
        /// the ids, then calls OnRemoveItem, whose base is an empty virtual and whose only overrides are on
        /// Hook/SlumLord/Storage, never on a Player. So a bare detach is network-silent: the client's byte
        /// stream would be byte-for-byte identical with and without it, and the equipment panel would stay
        /// empty. That is a SEPARATE defect from the double-parenting one, not the remaining half of it -
        /// the server-state repair is complete without it, and this repair is invisible to the shard.
        ///
        /// The message goes HERE and never inside TryEquipObjectWithNetworking, which is shared with every
        /// client-initiated wield, where the client has already moved the item in its own panel and a second
        /// container update would be redundant at best.
        ///
        /// WHICH message: the detach runs through TryRemoveFromInventoryWithNetworking with
        /// RemoveFromInventoryAction.ToWieldedSlot rather than the bare Container.TryRemoveFromInventory.
        /// That enum member is upstream ACE's own name for exactly this transition and had ZERO call sites
        /// before this one - it is referenced only by the guard that suppresses the off-player extras. Under
        /// it, TryRemoveFromInventoryWithNetworking emits precisely one thing beyond the plain remove:
        /// GameMessagePublicUpdateInstanceID(item, PropertyInstanceId.Container, ObjectGuid.Invalid). No
        /// GameMessageInventoryRemoveObject (that is gated to GiveItem/SpendItem/ToCorpseOnDeath), no
        /// encumbrance update and no DeepSave (both gated to != ToWieldedSlot, and the encumbrance TryEquipObject
        /// is about to re-add would cancel out anyway). It is the exact mirror of the container update
        /// DoHandleActionPutItemInContainer sends on the way back, and picking the repo's own dormant idiom
        /// beats inventing a message set.
        ///
        /// STILL UNSETTLED, and the reason this is marked rather than asserted: whether that one update is
        /// SUFFICIENT for the client to draw the item in its equipment panel cannot be decided from server
        /// source. It is the necessary half - the client is currently never told the item left the pack -
        /// but the panel may also want something the mirror does not imply. This needs the live check; if
        /// the panel is still empty afterwards, the next thing to look at is the stock upstream
        /// Wielder = ObjectGuid.Invalid / CurrentWieldedLocation = 0 pair inside
        /// TryEquipObjectWithNetworking, which is deliberately NOT touched here because it is shared with
        /// every ordinary player wield.
        /// </summary>
        private bool TryDetachAndEquipForFacet(Container itemRootOwner, WorldObject item, EquipMask wieldedLocation, List<string> report)
        {
            // The one caller resolves items only within this character's own possessions -
            // RestoreFacetEquip scopes its FindObject to SearchLocations.MyInventory - so anything else is
            // a caller bug, not a runtime condition. Asserting it is what lets the detach below use the
            // Player-level networking form, which acts on this player rather than on an arbitrary container.
            if (itemRootOwner != this)
            {
                // Fail closed. Equipping an item we failed to detach leaves it both wielded and still
                // parented to its old container, which is a duplication on the next load.
                log.Error($"[FACET] {Name} (0x{Guid.Full:X8}): 0x{item.Guid.Full:X8} ({item.Name}, wcid {item.WeenieClassId}) resolved to root owner {itemRootOwner?.Name ?? "null"} rather than this player; refusing to equip it rather than detaching from a container this method does not own. The item is untouched.");

                report.Add($"{item.NameWithMaterial} could not be taken out of your pack, so it was not equipped. It is still in your pack.");
                return false;
            }

            if (!TryRemoveFromInventoryWithNetworking(item.Guid, out _, RemoveFromInventoryAction.ToWieldedSlot))
            {
                // Fail closed. Equipping an item we failed to detach leaves it both wielded and still
                // parented to its old container, which is a duplication on the next load.
                log.Error($"[FACET] {Name} (0x{Guid.Full:X8}): could not detach 0x{item.Guid.Full:X8} ({item.Name}, wcid {item.WeenieClassId}) from its container before equipping it; refusing to equip rather than leaving it both wielded and parented. The item is untouched and still in the pack.");

                report.Add($"{item.NameWithMaterial} could not be taken out of your pack, so it was not equipped. It is still in your pack.");
                return false;
            }

            // Caught rather than allowed to propagate, because the detach above has already told the client
            // the item has no container. TryEquipObjectWithNetworking is not throw-free - TryActivateSpells,
            // EquipItemFromSet and VisualEffectManager.SendTo all run inside it - and an escaping exception
            // would unwind past both the re-add and the retract below, leaving the item unparented on the
            // server AND invisible in the client's pack until relog. The pre-existing behaviour without the
            // container update was merely the unparenting; sending it makes swallowing this mandatory rather
            // than tidy. Treated exactly as a false return: back in the pack, retracted, reported by name.
            bool equipped;

            try
            {
                equipped = TryEquipObjectWithNetworking(item, wieldedLocation);
            }
            catch (Exception ex)
            {
                log.Error($"[FACET] {Name} (0x{Guid.Full:X8}): equipping 0x{item.Guid.Full:X8} ({item.Name}, wcid {item.WeenieClassId}) threw after it had already been detached from the pack.", ex);

                // NOT a flat `equipped = false`, and the difference is the double-parenting this whole
                // method exists to prevent. Creature_Equipment.TryEquipObject - which sets WielderId and
                // registers the item in EquippedObjects - is the FIRST thing TryEquipObjectWithNetworking
                // does, and every one of the throw sources named above (TryActivateSpells, EquipItemFromSet,
                // VisualEffectManager.SendTo, HandleMaxHealthUpdate) runs AFTER it. So a throw here usually
                // means the item IS equipped. Treating that as a failure and putting it back in the pack
                // sets ContainerId beside the WielderId that is still there, which is exactly the corrupt
                // both-ids row 2026-09-06-00-Repair-Loadout-Double-Parented-Items.sql exists to clean up -
                // this catch would have recreated the very bug it was added alongside.
                //
                // Believing EquippedObjects rather than unwinding is deliberate: TryDequipObject would undo
                // the registration but not any enchantment TryActivateSpells had already created, so the
                // unwind is itself partial. The item being genuinely worn is the closest-to-correct state
                // available, and it is also what the client was already told by the messages that had
                // already gone out before the throw.
                equipped = HasEquippedItem(item.Guid);
            }

            if (equipped)
                return true;

            // Put it back where it came from. burdenCheck is off deliberately: this item was in the pack
            // one call ago and the detach above already subtracted its burden, so a burden refusal here
            // could only be spurious, and the cost of one would be an unparented object.
            if (!TryAddToInventory(item, out var returnedTo, burdenCheck: false))
            {
                log.Error($"[FACET] {Name} (0x{Guid.Full:X8}): 0x{item.Guid.Full:X8} ({item.Name}, wcid {item.WeenieClassId}) failed to equip AND failed to go back into the pack it was just detached from. The object is now unparented and is in NEITHER the pack NOR the equipped set - manual recovery required.");

                report.Add($"{item.NameWithMaterial} could not be equipped and could not be put back in your pack. Please contact staff immediately.");
                return false;
            }

            // The detach above already told the client the item has no container. Having put it back, that
            // has to be retracted or the client is left holding an item it believes is nowhere - the same
            // pair DoHandleActionPutItemInContainer sends when an item lands in a container, aimed at
            // whichever pack TryAddToInventory actually chose.
            //
            // The null-conditional below is style, not a contract: Session cannot be null here. The detach
            // at the top of this method reaches an UNGUARDED Session.Network.EnqueueSend inside
            // TryRemoveFromInventoryWithNetworking, so a null session would already have thrown before this
            // line. What actually guarantees it is the entry point - /facet is registered RequiresWorld
            // and takes session.Player, and nothing else calls into the restore.
            Session?.Network.EnqueueSend(
                new GameMessagePublicUpdateInstanceID(item, PropertyInstanceId.Container, returnedTo.Guid),
                new GameEventItemServerSaysContainId(Session, item, returnedTo));

            report.Add($"{item.NameWithMaterial} could not be equipped and is in your pack.");
            return false;
        }

        /// <summary>
        /// Equips an object that has just come out of the account vault, straight onto the character,
        /// and only then tells the client the object exists at all. The vault path's counterpart to
        /// <see cref="TryDetachAndEquipForFacet"/>, and deliberately NOT the same shape.
        ///
        /// THE BUG THIS EXISTS FOR. The previous shape delivered the withdrawn object with
        /// TryCreateInInventoryWithNetworking and then ran it through TryDetachAndEquipForFacet, so the
        /// object's very first mention to the client was a GameMessageCreateObject describing it as a pack
        /// item, immediately followed by the detach and wield messages that move it out of the pack. Those
        /// two halves do not travel together. GameMessageCreateObject is built on
        /// GameMessageGroup.SmartboxQueue (GameMessageCreateObject.cs:8), while every message that follows
        /// it here is on GameMessageGroup.UIQueue - GameMessagePublicUpdateInstanceID's ctor, GameEventWieldItem.cs:8,
        /// GameEventItemServerSaysContainId.cs:8. NetworkSession.Update() walks currentBundles in ASCENDING
        /// group order (NetworkSession.cs:227-283) and UIQueue is 0x09 against SmartboxQueue's 0x0A, so the
        /// UIQueue bundle is flushed BEFORE the SmartboxQueue bundle carrying the create - and
        /// minimumTimeBetweenBundles can push the create out a further pass. Every detach/wield message
        /// therefore reached the client naming a guid the client had never been told about, and the create
        /// that finally introduced the object described it as sitting in the pack. Server: wielded. Client:
        /// a phantom pack entry that nothing ever retracts. The character model was still correct because
        /// GameMessageObjDescEvent describes the PLAYER, an object the client has always known.
        ///
        /// WHY THE OBVIOUS FIX DOES NOT WORK. Un-suppressing GameMessageInventoryRemoveObject on the detach
        /// (RemoveFromInventoryAction gates it away for ToWieldedSlot) fixes nothing: that message is UIQueue
        /// too (GameMessageInventoryRemoveObject.cs:9), so it is flushed in the same doomed bundle, ahead of
        /// the create it was meant to retract. Nothing sent before the create can survive.
        ///
        /// WHAT IT DOES INSTEAD. A withdrawn object comes back parented to nothing - AccountVaultStore's
        /// TryWithdraw contract says so, WithdrawItem clears ContainerId through Container.TryRemoveFromInventory,
        /// and WithdrawFromLedger builds a brand-new object - so there is no container to detach it from and
        /// no pack round trip to announce. It is equipped directly, and the client is then told about it with
        /// a single GameMessageCreateObject describing the finished, wielded state. That is not an invented
        /// message set: it is exactly what SendInventoryAndWieldedItems does for every equipped item at login
        /// (Player_Networking.cs:319-324), which is the proof that a create alone is sufficient for the client
        /// to draw an item in its equipment panel - no GameEventWieldItem needed.
        ///
        /// THE VISUAL EFFECT IS RE-SENT, and that is not belt-and-braces. TryEquipObjectWithNetworking calls
        /// VisualEffectManager.SendTo itself, which enqueues GameMessagePlayScriptId - also SmartboxQueue
        /// (GameMessagePlayScriptId.cs:23), and enqueued BEFORE the create below, so it lands ahead of the
        /// object's introduction and is lost the same way. SendTo is once-per-lifetime guarded, so that lost
        /// send would otherwise suppress the effect permanently. The rebuilt:true overload is the guard's own
        /// documented bypass and re-sends after the create. It cannot double the effect for the same reason
        /// the whole bug exists: the client cannot have applied a script for an object it did not yet have.
        ///
        /// ENCUMBRANCE IS SENT EXPLICITLY, unlike the inventory path. There the item was already on the
        /// character and TryRemoveFromInventoryWithNetworking's ToWieldedSlot suppression is right because
        /// the equip re-adds the same burden. Here the burden genuinely rises: the item was in the vault a
        /// moment ago and Creature_Equipment.TryEquipObject has just added it to EncumbranceVal.
        ///
        /// Returns false when the item is not now worn, in which case NOTHING has been said to the client
        /// about it and the caller must deliver it the ordinary way.
        /// </summary>
        private bool TryEquipWithdrawnForFacet(WorldObject item, EquipMask wieldedLocation)
        {
            bool equipped;

            try
            {
                equipped = TryEquipObjectWithNetworking(item, wieldedLocation);
            }
            catch (Exception ex)
            {
                log.Error($"[FACET] {Name} (0x{Guid.Full:X8}): equipping vault-withdrawn 0x{item.Guid.Full:X8} ({item.Name}, wcid {item.WeenieClassId}) threw.", ex);

                // See TryDetachAndEquipForFacet's catch for the full argument: TryEquipObject runs first
                // inside TryEquipObjectWithNetworking, so a throw from anything after it leaves the item
                // genuinely worn, and the caller's fallback delivery would then set ContainerId beside a
                // live WielderId - the double-parented row this feature already needed a repair migration
                // for. Believe EquippedObjects, not the exception.
                equipped = HasEquippedItem(item.Guid);
            }

            if (!equipped)
                return false;

            // Persisted immediately, discharging AccountVaultStore.TryWithdraw's caller contract ("add it to
            // the actor, then call SaveBiotaToDatabase() IMMEDIATELY"). The vault's row for this object still
            // reads ContainerId = the vault until this lands; leaving it unsaved is the deliver-and-forget
            // duplication that contract's rule 1 describes, where an idle-evicted store rehydrates and hands
            // back an item the player is already wearing.
            item.SaveBiotaToDatabase();

            // The null-conditional is style, not a contract, and the ENTRY POINT is the whole of the
            // guarantee: /facet is registered RequiresWorld and takes session.Player, and nothing else
            // calls into the restore. Do NOT restate that as "an earlier unguarded send would already have
            // thrown" - TryEquipObjectWithNetworking's unguarded Session.Network.EnqueueSend
            // (Player_Inventory.cs:436) sits INSIDE the try above, so its NullReferenceException would be
            // swallowed by this method's own catch and execution would arrive here regardless. The behaviour
            // is fine either way (this send and the SendTo below both no-op on a null session); the reasoning
            // is what had to be corrected.
            Session?.Network.EnqueueSend(
                new GameMessageCreateObject(item),
                new GameMessagePrivateUpdatePropertyInt(this, PropertyInt.EncumbranceVal, EncumbranceVal ?? 0));

            VisualEffectManager.SendTo(Session, item, rebuilt: true);

            return true;
        }

        /// <summary>
        /// Withdraws one vault-resolved entry (VaultItem or VaultLedger) and puts it back on the
        /// character - straight onto the body when it is going to be worn, into the pack otherwise -
        /// reporting every outcome by name; a withdrawal succeeding is reported
        /// just as much as one failing, because auto-withdrawing moves shared, account-scoped storage
        /// as a side effect of a per-character command.
        ///
        /// Runs the withdraw itself, and any return-on-failed-delivery (via
        /// <see cref="TryReturnWithdrawnToVault"/>), through
        /// <see cref="AccountVaultStore.Enqueue(Action, out Exception)"/> - AccountVaultStore.TryWithdraw/
        /// TryReturnWithdrawn/TryReturnWithdrawnToLedger all assert they are on the store's own
        /// serialized mutation queue. Enqueue does not return until the work has actually run (it is a
        /// drain-lock/re-entrancy guard around inline execution, not a separate worker thread the way
        /// SerializedShardDatabase is), so this whole withdraw-then-equip sequence completes
        /// synchronously within one call - no polling, no later tick, no action-chain bridge needed.
        ///
        /// BOTH the withdraw here and the return inside TryReturnWithdrawnToVault go through the
        /// two-arg <c>Enqueue(Action, out Exception)</c> overload, deliberately NOT the one-arg
        /// convenience form. AccountVaultStore.cs's own doc comment on that
        /// split names the exact trap the one-arg form sets for a closure of the shape used here
        /// (<c>() =&gt; ok = store.TryWithdraw(...)</c>): Drain catches and swallows a work item's
        /// exception without rethrowing, so if TryWithdraw throws partway through, <c>ok</c> is left at
        /// its default false while Enqueue still reports true - a THROW misread as a clean REFUSAL. This
        /// is not theoretical for a withdraw specifically: WithdrawFromLedger debits the ledger and
        /// builds the replacement object (populating the `withdrawn` out-parameter) BEFORE its trailing,
        /// purely opportunistic DeleteEmptyAccountVaultStacks cleanup call, so a throw in that tail can
        /// leave a real, ledger-already-debited object sitting in `withdrawn` with `ok` never set. Losing
        /// the only reference to that object here would be an invisible account-side loss with no
        /// recovery. So: a throw with a recovered object routes it through the SAME return-to-vault path
        /// a failed delivery uses; a throw with nothing recovered is reported as a distinct vault-error
        /// line (never folded into the generic "unknown reason" a clean refusal gets) and logged at
        /// Error, because a possible ledger-state loss must be visible, not silent.
        ///
        /// TWO DELIVERIES, AND ONLY ONE OF THEM RUNS. The wield check happens BEFORE anything is
        /// delivered now, because which delivery is correct depends on its answer and the two announce the
        /// object to the client in incompatible ways:
        ///
        ///   - It will be worn -> <see cref="TryEquipWithdrawnForFacet"/>. No pack round trip at all, and
        ///     therefore no free pack slot required to restore a worn item. Read that method's remarks for
        ///     why the previous "deliver into the pack, then detach and equip" shape produced a client that
        ///     showed the item in the pack forever while the server had it wielded.
        ///   - It will not be worn, or the equip refused -> TryCreateInInventoryWithNetworking, unchanged,
        ///     which also discharges AccountVaultStore.TryWithdraw's caller contract with its own
        ///     item.SaveBiotaToDatabase(). The item is left sitting in the pack rather than returned to the
        ///     vault: it has already been delivered per the vault's own contract, and this matches "the
        ///     switch always completes" - the player still has the item, just not worn.
        ///
        /// The equip is attempted FIRST and the pack delivery is its fallback, never both: an equip that
        /// refuses has told the client nothing whatsoever about the object, so the fallback's
        /// GameMessageCreateObject is still the client's first and only introduction to it and describes
        /// exactly where the item really is.
        ///
        /// If the fallback delivery itself fails (no free pack slot), the withdrawn object is returned to the vault
        /// through <see cref="TryReturnWithdrawnToVault"/>, which reports whether the return itself
        /// landed - TryReturnWithdrawn has several return-false guards (vault capacity chief among
        /// them), each commented "the caller still holds it and must not destroy it", so a caller that
        /// assumes success on every call is the Destroy() the contract forbids, minus the audit trail.
        /// </summary>
        private void WithdrawAndRestoreFromVault(
            FacetVaultLookup lookup,
            VaultEntry entry,
            int amount,
            bool isLedger,
            FacetGearResolution resolution,
            List<string> report)
        {
            var ok = false;
            List<WorldObject> withdrawn = null;
            string failReason = null;

            // Resolved BEFORE the withdraw, because every failure branch below needs it and most of them
            // have no object to name. entry.WorldObject is null for a ledger row, which falls through to
            // the weenie name - see FacetItemLabel.
            var label = FacetItemLabel(resolution.Wcid, entry.WorldObject);

            var ran = lookup.Store.Enqueue(() => ok = lookup.Store.TryWithdraw(entry, amount, this, out withdrawn, out failReason), out var thrown);

            if (!ran)
            {
                report.Add($"Your vault is unavailable right now, so {label} could not be restored.");
                return;
            }

            if (thrown != null)
            {
                if (withdrawn != null && withdrawn.Count > 0)
                {
                    // TryWithdraw got far enough to build (and, for a ledger withdraw, debit) a real
                    // object before it threw - `ok` never got set, but the object itself is real and
                    // must not be dropped on the floor. Route it back exactly like a failed delivery.
                    // NEVER retry this item after this call, regardless of outcome - see
                    // TryReturnWithdrawnToVault's remarks on the double-credit risk of retrying a Threw
                    // item, and note this whole branch is itself already a one-shot: nothing here loops.
                    var recovered = withdrawn[0];
                    var returnOutcome = TryReturnWithdrawnToVault(lookup, entry, isLedger, recovered);

                    log.Error($"[FACET] {Name} (0x{Guid.Full:X8}): AccountVaultStore.TryWithdraw threw for wcid {resolution.Wcid} but had already produced object 0x{recovered.Guid.Full:X8}; return-to-vault outcome: {returnOutcome}. {thrown}");

                    report.Add(returnOutcome switch
                    {
                        VaultReturnOutcome.Returned =>
                            $"A vault error interrupted restoring {label}; it was returned to your vault. Please try the switch again.",
                        VaultReturnOutcome.Refused =>
                            $"A vault error interrupted restoring {label} and it could NOT be returned to your vault. Please contact staff immediately.",
                        // Threw - the return attempt itself may have left the vault inconsistent, so this
                        // must NOT assert a known state (never "in neither the vault nor your
                        // possession") the way the Refused message does.
                        _ =>
                            $"A vault error interrupted restoring {label}, and the vault's state afterward could not be confirmed. Please contact staff immediately - this needs manual reconciliation.",
                    });
                }
                else
                {
                    log.Error($"[FACET] {Name} (0x{Guid.Full:X8}): AccountVaultStore.TryWithdraw threw for wcid {resolution.Wcid} with no object recovered - possible vault/ledger state loss, needs manual reconciliation against account_vault_log. {thrown}");

                    report.Add($"A vault error occurred while restoring {label}. Please contact staff - this may need manual correction.");
                }

                return;
            }

            if (!ok || withdrawn == null || withdrawn.Count == 0)
            {
                report.Add($"Could not withdraw {label} from your vault: {failReason ?? "unknown reason"}.");
                return;
            }

            var item = withdrawn[0];

            // BEFORE any delivery, because the answer picks which delivery is correct - see this method's
            // remarks. CheckWieldRequirements reads only item properties and this character's own skills,
            // attributes, vitals and level, so it does not care that the object is currently parented to
            // nothing; it was already being called on an item the caller had not yet equipped.
            var wieldError = CheckWieldRequirements(item);

            if (wieldError == WeenieError.None && TryEquipWithdrawnForFacet(item, (EquipMask)resolution.Slot))
            {
                report.Add($"{item.NameWithMaterial} was withdrawn from your vault.");
                return;
            }

            // Either it cannot be worn on this build, or the equip refused. TryEquipWithdrawnForFacet
            // sends the client nothing on a false return, so the create below is still the client's first
            // and only introduction to this object.
            if (!TryCreateInInventoryWithNetworking(item))
            {
                // Never delivered - the vault contract requires this go back exactly the way it came
                // out, never destroyed. The item is still fresh off TryWithdraw, so this call is
                // guaranteed to be off the mutation queue (RestoreFacetEquip is not itself queue
                // work), and Enqueue's re-entrancy guard would run it inline even if it weren't.
                // NEVER retry this item after this call, regardless of outcome - see
                // TryReturnWithdrawnToVault's remarks on the double-credit risk; this branch is itself a
                // one-shot and nothing here loops back onto the same item.
                var returnOutcome = TryReturnWithdrawnToVault(lookup, entry, isLedger, item);

                switch (returnOutcome)
                {
                    case VaultReturnOutcome.Returned:
                        report.Add($"Your pack had no room for {label}; it was left in your vault.");
                        break;

                    case VaultReturnOutcome.Refused:
                        log.Error($"[FACET] {Name} (0x{Guid.Full:X8}): could not return withdrawn item 0x{item.Guid.Full:X8} (wcid {resolution.Wcid}) to the vault after a failed pack delivery. The object is now unparented and is in NEITHER the vault NOR the player's possession - manual recovery required.");
                        report.Add($"Could not restore {label}, and it could not be returned to your vault. Please contact staff immediately.");
                        break;

                    default: // Threw
                        // Do NOT reuse the "in neither the vault nor your possession" wording above -
                        // that asserts a known state, and a throw specifically means the state is no
                        // longer known (TryReturnWithdrawnToVault already logged the store-level detail).
                        report.Add($"Could not restore {label}, and the vault's state afterward could not be confirmed. Please contact staff immediately - this needs manual reconciliation.");
                        break;
                }

                return;
            }

            report.Add($"{item.NameWithMaterial} was withdrawn from your vault.");

            report.Add(wieldError != WeenieError.None
                ? $"{item.NameWithMaterial} no longer meets its wield requirements on this facet ({wieldError}). It is in your pack."
                : $"{item.NameWithMaterial} could not be equipped and is in your pack.");
        }

        /// <summary>
        /// Re-equips a remembered set from the character's own packs or, failing that, its account
        /// vault - withdrawing a remembered item back out when it has been banked since it was last
        /// worn. Returns one human-readable line per entry that could not be restored AND one per vault
        /// withdrawal performed (see WithdrawAndRestoreFromVault), because auto-withdrawing moves
        /// shared, account-scoped storage as a side effect of a per-character command and the player
        /// has to be able to see that it happened. A switch ALWAYS completes: nothing here is a reason
        /// to abort, or one sold item would block a facet forever.
        ///
        /// Ownership is re-verified rather than trusted, for both sources: a remembered guid may belong
        /// to an item that has since been traded, sold or given away (inventory case), or the vault
        /// re-authorizes on every TryWithdraw call independent of anything checked earlier (vault
        /// case) - the account vault is looked up fresh off THIS character's own account, so nothing
        /// here can ever reach or move a different account's vault.
        ///
        /// Only items resolved out of this character's own inventory are equipped via the inventory
        /// path - the guid set fed to FacetGear.ResolveAll, and the FindObject call below, both use
        /// exactly the SearchLocations.MyInventory scope (main pack plus one level of side packs),
        /// deliberately excluding EquippedObjects: GetAllPossessions() would fold equipped items into
        /// the same set, but FindObject(..., SearchLocations.MyInventory, ...) cannot find them there,
        /// which would misreport an already-equipped item as "not found".
        /// </summary>
        public List<string> RestoreFacetEquip(List<FacetEquipEntry> remembered)
        {
            var report = new List<string>();

            if (remembered == null || remembered.Count == 0)
                return report;

            var inventoryGuids = new HashSet<uint>();

            foreach (var item in GetAllPossessions(Inventory.Values, Array.Empty<WorldObject>()))
                inventoryGuids.Add(item.Guid.Full);

            var vaultLookup = BuildFacetVaultLookup();

            var resolutions = FacetGear.ResolveAll(remembered, inventoryGuids, vaultLookup.ItemGuids, vaultLookup.LedgerCounts);

            for (var i = 0; i < resolutions.Count; i++)
            {
                var resolution = resolutions[i];

                switch (resolution.Source)
                {
                    case FacetGearSource.Inventory:
                    {
                        // rootOwner is captured rather than discarded: it is the container the item has to be
                        // detached from before it can be equipped (see TryDetachAndEquipForFacet). For a
                        // MyInventory hit FindObject always reports the player as rootOwner, including for an
                        // item sitting in a side pack, and TryRemoveFromInventory recurses to find it there.
                        var item = FindObject(resolution.Guid, SearchLocations.MyInventory, out _, out var itemRootOwner, out _);

                        if (item == null)
                        {
                            report.Add($"Could not find {FacetItemLabel(resolution.Wcid, null)}.");
                            continue;
                        }

                        // Wield requirements are checked ONLY at equip time - there is no periodic
                        // recheck of worn gear - so a build that no longer qualifies has to be caught
                        // right here.
                        var wieldError = CheckWieldRequirements(item);

                        if (wieldError != WeenieError.None)
                        {
                            report.Add($"{item.NameWithMaterial} no longer meets its wield requirements on this facet ({wieldError}).");
                            continue;
                        }

                        TryDetachAndEquipForFacet(itemRootOwner, item, (EquipMask)resolution.Slot, report);

                        break;
                    }

                    case FacetGearSource.VaultItem:
                    {
                        if (vaultLookup.Store == null || !vaultLookup.EntriesByGuid.TryGetValue(resolution.Guid, out var entry))
                        {
                            report.Add(ItemNotRestoredMessage(vaultLookup, resolution));
                            continue;
                        }

                        WithdrawAndRestoreFromVault(vaultLookup, entry, (int)entry.Count, false, resolution, report);
                        break;
                    }

                    case FacetGearSource.VaultLedger:
                    {
                        if (vaultLookup.Store == null || !vaultLookup.LedgerEntriesByWcid.TryGetValue(resolution.Wcid, out var entry))
                        {
                            report.Add(ItemNotRestoredMessage(vaultLookup, resolution));
                            continue;
                        }

                        WithdrawAndRestoreFromVault(vaultLookup, entry, 1, true, resolution, report);
                        break;
                    }

                    default:
                        report.Add(ItemNotRestoredMessage(vaultLookup, resolution));
                        break;
                }
            }

            return report;
        }

        // ==================================================================================
        // Orchestration: gates, tunables, storage I/O and the switch itself (Task 11).
        // ==================================================================================

        /// <summary>
        /// The switch-summary line for a reconciled attribute surplus, naming WHERE the points landed
        /// rather than only that there were some. Returns null when nothing was deposited.
        ///
        /// Where they landed is derived by diffing the applied arrangement against the stored one rather
        /// than being reported out of <see cref="FacetAttributes.Reconcile"/>: the deposit can legitimately
        /// span several attributes (the walk fills the lowest to the ceiling and then moves on), so a
        /// single "it went here" out-parameter would be wrong in exactly the case worth reporting.
        ///
        /// Split out from <see cref="TrySwitchFacet"/> the same way <see cref="ComposeSkillCreditCost"/>
        /// and <see cref="ComposeStripRoomRefusal"/> are, so it is unit-testable without a live Player.
        /// Attributes are listed in PropertyAttribute enum order, not in deposit order, so the line reads
        /// the same way the character sheet does.
        /// </summary>
        internal static string ComposeAttributeSurplusLine(
            IReadOnlyDictionary<PropertyAttribute, uint> stored,
            IReadOnlyDictionary<PropertyAttribute, uint> applied,
            uint surplus)
        {
            if (stored == null || applied == null || surplus == 0)
                return null;

            var landed = new List<string>();

            foreach (var attribute in FacetAttributes.PrimaryAttributes)
            {
                if (!stored.TryGetValue(attribute, out var before) || !applied.TryGetValue(attribute, out var after))
                    continue;

                if (after > before)
                    landed.Add($"{attribute} +{after - before:N0}");
            }

            if (landed.Count == 0)
                return null;

            return $"You had {surplus:N0} more innate attribute point(s) than this facet remembered; they were added to {string.Join(", ", landed)}.";
        }

        /// <summary>facet_slot2_level/3/4, matching the slot number. Slot 1 has no threshold - it always exists.</summary>
        private static long RequiredLevelForSlot(int slot, FacetDials dials)
        {
            switch (slot)
            {
                case 2: return dials.Slot2Level;
                case 3: return dials.Slot3Level;
                case 4: return dials.Slot4Level;
                default: return 0;
            }
        }

        /// <summary>
        /// Gates 1-4 of DESIGN.md sections 2/9: feature flag, slot range/active-slot/unlock level,
        /// landblock allowlist, and the busy-state gates, plus the fork's own mule guard. Pure and
        /// non-mutating, on purpose - the
        /// command layer calls this once to decide whether a confirmation prompt is even worth showing,
        /// and TrySwitchFacet calls it again as its own first step, so a switch that runs through a
        /// confirmation prompt RE-VALIDATES everything rather than trusting state from before the prompt
        /// was shown. ClassAbilityCommands' unlearn path documents exactly why this matters: the player
        /// can walk out of the allowlisted area, start a trade, or die while the prompt is up.
        /// </summary>
        internal bool CheckFacetGates(int targetSlot, out string refusal)
        {
            refusal = null;

            var dials = FacetTunables.DialSource();

            if (!dials.Enabled)
            {
                refusal = "Facets are not available on this server yet.";
                return false;
            }

            // Mule (WaffleACE): a mule is a storage character, not a progression one - ConvertToMule zeroes
            // TotalSkillCredits and forces every untrainable skill to Untrained, so a switch would capture
            // that zeroed state into a slot row as though it were a build the player had chosen. Silent
            // refusal (notify: false) because this method's own contract is to hand the reason back through
            // `refusal`, exactly as Player_ClassAbilities' TrainClassAbility guard does; notifying here
            // would send the same line twice.
            //
            // Unreachable at stock config - mule_level is 180 and facet_slot2_level is 300, so a mule can
            // never meet the unlock threshold - but both are live tunables and neither reads the other, so
            // this is a guard rather than an assertion.
            if (MuleBlocked(MuleAction.ChangeFacet, notify: false))
            {
                refusal = "A mule cannot change facets.";
                return false;
            }

            if (targetSlot < 1 || targetSlot > MaxFacetSlot)
            {
                refusal = $"There is no facet {targetSlot}. Valid facets are 1-{MaxFacetSlot}.";
                return false;
            }

            if (targetSlot == ActiveFacetSlot)
            {
                refusal = "You are already using that facet.";
                return false;
            }

            var requiredLevel = RequiredLevelForSlot(targetSlot, dials);

            if (requiredLevel > 0 && (Level ?? 0) < requiredLevel)
            {
                refusal = $"Facet {targetSlot} unlocks at level {requiredLevel:N0}. You are level {Level ?? 0:N0}.";
                return false;
            }

            var landblock = (ushort)(Location?.LandblockShort ?? 0);

            // Count == 0 is "no allowlist configured", NOT "nothing is allowed" - see facet_allowlist's
            // remarks. Inverting this would make a blanked config row silently disable the whole feature.
            if (dials.Allowlist.Count > 0 && !dials.Allowlist.Contains(landblock))
            {
                refusal = $"You can only change facets in {dials.AllowlistName}.";
                return false;
            }

            if (IsBusy || IsTrading || IsInDeathProcess || Teleporting)
            {
                refusal = "You are too busy to change facets right now. Try again in a moment.";
                return false;
            }

            // LastOpenedContainerId does NOT mean "a window is open", and treating it that way is what
            // made this gate refuse a player who had already closed the vendor they were standing at.
            // Vendor.ApproachVendor SETS it on approach and the ONLY thing that clears it is
            // Vendor.CheckClose, on its 1.5s poll, when the player has moved outside UseRadius. Closing
            // the panel sends the server nothing at all - there is no client-side close signal for a
            // vendor - so for a vendor the flag really means "still standing here", and gating on it is
            // gating on proximity. Real containers are different: Container.FinishClose clears the flag,
            // so for those it genuinely does track an open view.
            //
            // So resolve it and ask the object, using the same triple check Container.ActOnUse already
            // uses before it closes a previously-opened container.
            var lastOpened = LastOpenedContainerId != ObjectGuid.Invalid
                ? CurrentLandblock?.GetObject(LastOpenedContainerId)
                : null;

            if (lastOpened is Container openContainer && openContainer.IsOpen && openContainer.Viewer == Guid.Full)
            {
                refusal = "Close whatever you have open before changing facets.";
                return false;
            }

            // A deliberate carve-out, and NOT for the reason above. A PersonalVendor is a view onto the
            // account vault, and the gear restore can WITHDRAW from that vault mid-switch
            // (RestoreFacetEquip -> WithdrawAndRestoreFromVault). Two paths touching vault state at once is
            // an interaction that has not been traced end to end, and switching is Marketplace-only, which
            // is exactly where the fork's PersonalVendors stand - so this is the common case, not an edge
            // one. Refuse until someone traces it and can delete this block on evidence. A PersonalVendor is
            // not a Container, so it would otherwise fall straight through the check above.
            //
            // BE CLEAR ABOUT WHAT THIS TESTS, because it is the same latch the block above just stopped
            // trusting: for a PersonalVendor too, LastOpenedContainerId means PROXIMITY, not "the panel is
            // open" - PersonalVendor overrides neither CheckClose nor FinishClose, so nothing clears it any
            // earlier than walking out of UseRadius. Re-admitting the proximity latch here is the
            // conservative choice on purpose, not precision about panel state. That is also why the refusal
            // below tells the player to step away rather than to close anything: stepping out of range is
            // the only thing that actually clears it.
            if (lastOpened is PersonalVendor)
            {
                refusal = "Step away from the vendor before changing facets.";
                return false;
            }

            return true;
        }

        private const int FacetDbTimeoutMs = 5000;
        private const long FacetDbWarnElapsedMs = 200;

        /// <summary>
        /// Fetches every character_facet row for this character, blocking the calling (world tick)
        /// thread until the read lands or <see cref="FacetDbTimeoutMs"/> passes with no answer. This
        /// is the same affordable-because-rare blocking pattern AccountVaultStore.SaveAndWait documents:
        /// a switch happens once per manual player action, never per tick, so blocking here costs one
        /// player's tick, not the server's. Monitor rather than an event object, for the same reason
        /// SaveAndWait gives - a callback arriving after the timeout would call Set on a disposed
        /// ManualResetEventSlim and throw on the database thread, whereas a late Pulse against a lock
        /// nobody is waiting on is inert. The actual wait mechanism is
        /// <see cref="FacetRowFetch.TryFetch"/>, extracted to a pure static helper so it is
        /// unit-testable without a live Player.
        ///
        /// Returns FALSE, with rows == null, when the read did not complete in time. THIS IS LOAD-BEARING:
        /// Task 11 fix round 1's Finding 1 was exactly this method degrading a timeout to an empty list,
        /// which TrySwitchFacet then read as "slot never visited" and used to overwrite a real stored
        /// build with a synthesized fresh one - a silent, permanent loss of the player's actual build the
        /// very next time they saved that slot's row. Every caller for whom a wrong "no rows" answer would
        /// drive a MUTATION must check the return value and refuse rather than falling through. A caller
        /// for whom a wrong answer is merely advisory (nothing worse than an extra confirmation prompt or
        /// a stale display) may use <see cref="GetCharacterFacetRowsOrEmpty"/> instead.
        /// </summary>
        internal bool TryGetCharacterFacetRows(out List<CharacterFacet> rows)
        {
            var started = Stopwatch.StartNew();

            var ok = FacetRowFetch.TryFetch(Character.Id, FacetDbTimeoutMs, DatabaseManager.Shard.GetCharacterFacets, out rows);

            var elapsedMs = started.ElapsedMilliseconds;

            if (!ok)
            {
                log.Error($"[FACET] {Name} (0x{Guid.Full:X8}): no character_facet read confirmation after {elapsedMs} ms (timeout {FacetDbTimeoutMs} ms). The read did NOT complete - the caller must refuse rather than treat this as \"no stored rows\". This wait ran on the world tick thread.");
                return false;
            }

            if (elapsedMs > FacetDbWarnElapsedMs)
                log.Error($"[FACET] {Name} (0x{Guid.Full:X8}): blocked the world tick thread for {elapsedMs} ms waiting on character_facet. The shard read queue is backed up.");

            return true;
        }

        /// <summary>
        /// Advisory wrapper over <see cref="TryGetCharacterFacetRows"/> for callers where a false "no
        /// stored rows" answer is genuinely SAFE - it costs nothing beyond a stale display
        /// (FacetCommands.HandleList), never a
        /// mutation. Every load-bearing caller (a switch, a rename that must find an EXISTING row) must
        /// call <see cref="TryGetCharacterFacetRows"/> directly instead and refuse on a false return.
        /// </summary>
        internal List<CharacterFacet> GetCharacterFacetRowsOrEmpty()
        {
            return TryGetCharacterFacetRows(out var rows) ? rows : new List<CharacterFacet>();
        }

        /// <summary>
        /// Fire-and-forget, matching AddSpeedRun/SaveBiotaToDatabase - nothing in the same call needs
        /// this write to have landed before it continues. character_facet is a per-slot cache of a
        /// build that also lives, for the slot the player is actually standing on, directly on the
        /// character's own Biota/CharacterPropertiesQuestRegistry - so a lost write here is recoverable
        /// by switching away and back once the queue catches up, never a silent loss of live state.
        /// </summary>
        private void SaveFacetRowAsync(CharacterFacet row)
        {
            DatabaseManager.Shard.SaveCharacterFacet(row, success =>
            {
                if (!success)
                    log.Error($"[FACET] character 0x{row.CharacterId:X8} slot {row.Slot}: FAILED to persist a character_facet row. That table is this slot's only record of the build while the player is standing elsewhere, so it may now be stale until the next successful save from this slot.");
            });
        }

        /// <summary>
        /// Sets a slot's display label. A non-active slot must already have a stored row - naming an
        /// unvisited slot is refused rather than pre-creating a synthetic row, because a pre-created row
        /// would make that slot's FIRST real switch look like a "subsequent" one to
        /// <see cref="TrySwitchFacet"/>'s freshness check and silently skip the one-time confirmation
        /// DESIGN.md section 8 calls for. Naming the ACTIVE slot instead captures and saves the real, live
        /// build under its own slot number - exactly what a switch AWAY from this slot does anyway (see
        /// <see cref="TrySwitchFacet"/>), just done early and harmlessly.
        /// </summary>
        public bool TrySetFacetName(int slot, string name, out string refusal)
        {
            refusal = null;

            if (slot < 1 || slot > MaxFacetSlot)
            {
                refusal = $"There is no facet {slot}. Valid facets are 1-{MaxFacetSlot}.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                refusal = "That name is empty.";
                return false;
            }

            if (slot == ActiveFacetSlot)
            {
                var activeRow = new CharacterFacet
                {
                    CharacterId = Character.Id,
                    Slot = (byte)slot,
                    Name = name,
                    SkillsJson = FacetSnapshot.SerializeSkills(CaptureFacetSkills()),
                    AbilitiesJson = FacetSnapshot.SerializeAbilities(CaptureFacetAbilities()),
                    EquipJson = FacetSnapshot.SerializeEquip(CaptureFacetEquip()),
                    // Captured live along with the other three, and it has to be: this row REPLACES the
                    // slot's stored row, and ShardDatabase.SaveCharacterFacet's update branch copies
                    // every column, so leaving this null would blank a real stored arrangement just for
                    // naming the slot the player is standing on. Capturing live is also correct on its
                    // own terms - this is the active slot, so the live arrangement IS its arrangement.
                    AttrsJson = FacetSnapshot.SerializeAttributes(CaptureFacetAttributes()),
                    UpdatedAt = DateTime.UtcNow,
                };

                SaveFacetRowAsync(activeRow);
                return true;
            }

            // LOAD-BEARING read (see TryGetCharacterFacetRows's remarks): a failed/timed-out read must
            // never be misreported as "you have not visited that slot yet" - that message is reserved for
            // a GENUINE absence, and this branch does not mutate anything on refusal either way, so there
            // is no reason to guess.
            if (!TryGetCharacterFacetRows(out var rows))
            {
                refusal = "Cannot check your stored facets right now. Try again in a moment.";
                return false;
            }

            var existing = rows.Find(r => r.Slot == (byte)slot);

            if (existing == null)
            {
                refusal = $"You have not cut facet {slot} yet - switch to it first, then you can name it.";
                return false;
            }

            existing.Name = name;
            existing.UpdatedAt = DateTime.UtcNow;

            SaveFacetRowAsync(existing);
            return true;
        }

        /// <summary>
        /// The pure decision behind <see cref="SendFacetUnlockNoticeIfDue"/>: should the one-time
        /// slot-2 unlock notice fire, given the four inputs that gate it. Split out from the instance
        /// method (which owns reading the tunables/properties and sending the messages) so the decision
        /// itself is testable without a live Player - ACE.Server.Tests cannot construct one.
        /// </summary>
        internal static bool ShouldShowFacetUnlockNotice(bool facetsEnabled, bool noticeAlreadyShown, bool isMuleBlocked, long level, long requiredLevelForSlot2)
        {
            if (!facetsEnabled)
                return false;

            if (noticeAlreadyShown)
                return false;

            if (isMuleBlocked)
                return false;

            return level >= requiredLevelForSlot2;
        }

        /// <summary>
        /// The one-time notice shown when a character first becomes eligible for facet slot 2 (mirrors
        /// <see cref="Player_ClassAbilities.SendFirstClassAbilityPointNotice"/> exactly: a modal popup via
        /// <see cref="GameEventPopupString"/> for the same client dialog used by the training-hall
        /// tutorial, so the milestone stands out, plus a chat mirror so the direction persists in the log
        /// after the popup is dismissed).
        ///
        /// Gated on all of: facet_enabled (a notice for a command that refuses is worse than no notice),
        /// <see cref="PropertyBool.FacetUnlockNoticeShown"/> not already set, the character's Level
        /// meeting the live slot-2 threshold (read through <see cref="FacetTunables"/> and
        /// <see cref="RequiredLevelForSlot"/> so retuning the gate moves the notice with it, never
        /// hardcoded), and not being a mule (checked the same way <see cref="CheckFacetGates"/> does,
        /// silently - a mule must not be told about a feature it cannot use).
        ///
        /// <see cref="FacetUnlockNoticeShown"/> is set and persisted BEFORE the notice is sent, so a
        /// throw partway through delivery cannot cause the notice to fire again on a later login.
        /// </summary>
        public void SendFacetUnlockNoticeIfDue()
        {
            var dials = FacetTunables.DialSource();

            var due = ShouldShowFacetUnlockNotice(
                dials.Enabled,
                FacetUnlockNoticeShown,
                MuleBlocked(MuleAction.ChangeFacet, notify: false),
                Level ?? 0,
                RequiredLevelForSlot(2, dials));

            if (!due)
                return;

            FacetUnlockNoticeShown = true;
            SaveBiotaToDatabase();

            // Both the location clause and its name come from the SAME dials the gate itself reads, so the
            // notice cannot promise a rule the gate does not enforce. An empty allowlist means no location
            // restriction at all (see CheckFacetGates' remarks on Count == 0), in which case the notice
            // must not send the player somewhere they do not need to go.
            var restricted = dials.Allowlist.Count > 0;
            var popupWhere = restricted ? $", but must be done in {dials.AllowlistName}" : "";
            var chatWhere = restricted ? $" in {dials.AllowlistName}" : "";

            var popup =
                "You can now cut a second facet.\n\n" +
                "You are on facet 1, and nothing about it has changed - this is a new, " +
                "additional build you can switch to whenever you like.\n\n" +
                $"Switching costs nothing{popupWhere}, and you will be asked to confirm the first time you " +
                "enter a facet. A fresh facet starts with its skills untrained - except always-trained and " +
                "augmentation-specialized ones - and no class abilities learned, and all of its experience, " +
                "skill credits and class ability points are returned to you to spend differently. Your " +
                "augmentations, level and attribute ranks are shared across every facet and are never " +
                "reset. How you have arranged your innate attributes is remembered by each facet, so a " +
                "build that wants its points spread differently can keep them that way; a new facet " +
                "begins with the arrangement you have now. " +
                "Gear equipped on the facet you are leaving is unequipped into your pack, and the facet you " +
                "return to re-equips what it remembers, telling you about anything it could not.\n\n" +
                "Type /facet to see your facets, or /facet 2 to turn the stone.";

            Session.Network.EnqueueSend(new GameEventPopupString(Session, popup));

            Session.Network.EnqueueSend(new GameMessageSystemChat(
                $"You can now cut a second facet - turn the stone{chatWhere} with /facet 2.", ChatMessageType.Advancement));
            Session.Network.EnqueueSend(new GameMessageSystemChat(
                "A fresh facet starts with its skills untrained and no class abilities learned, and returns its experience, skill credits and class ability points for you to spend differently; your augmentations, level and attribute ranks are shared and never reset, while each facet remembers its own arrangement of innate attributes. Type /facet to see your facets.", ChatMessageType.Advancement));
        }

        /// <summary>
        /// The orchestrating switch. See Docs/Facets/DESIGN.md sections 2, 4, 8 and 9 for the full
        /// reasoning; this method composes everything Tasks 2-10 built.
        ///
        /// ORDER, and why it cannot be reordered: gates first (<see cref="CheckFacetGates"/>) -> peek
        /// the target slot's stored row (read-only - needed to know the incoming build's committed PP
        /// for the shortfall check, but nothing is applied to the live character yet) -> compute the
        /// three pool shortfalls and REFUSE if any is non-zero, before anything is mutated (the XP
        /// duplication hole FacetPools' doc comment describes, and the credit/point equivalents) ->
        /// only THEN capture and
        /// save the outgoing build's row -> strip gear (which refuses on its own free-slot precheck
        /// without moving anything) -> apply the incoming skills/abilities/pools and push the live update
        /// messages -> set ActiveFacetSlot -> save BOTH the biota and the character -> and only at the
        /// very end, restore the remembered gear, which is a "the switch always completes" step
        /// (DESIGN.md section 6) that reports failures by name rather than aborting.
        ///
        /// THE FIRST-VISIT CONFIRMATION IS DECIDED HERE, from this method's own single row read, rather
        /// than by a separate peek in the command layer. Both reads block the world tick thread for up to
        /// FacetDbTimeoutMs, and a shard stall on that thread is paid by every player in the landblock
        /// group, not just the one who typed the command - so a switch to an already-visited slot, the
        /// common case, must cost one blocking read and not two. When the target slot has no stored row
        /// and <paramref name="confirmed"/> is false, this returns false with
        /// <paramref name="needsConfirmation"/> true, having mutated NOTHING; the command layer prompts and
        /// re-enters with confirmed=true, and that re-entry runs this whole method again from the top -
        /// its own fresh read, its own full CheckFacetGates. A first visit therefore still costs two
        /// reads, which is once per slot ever, and NOTHING about the post-confirmation re-validation is
        /// weakened: the player can still walk out of the allowlisted area, start a trade or die while the
        /// prompt is up, and the confirmed pass catches all of it.
        ///
        /// On a false return, refusal names why - with exactly one exception: the needsConfirmation return,
        /// where refusal is NULL because the caller owns the prompt wording. Branch on needsConfirmation
        /// before reading refusal. On a true return, refusal carries the human-readable success report
        /// (pools, plus any gear that could not be restored) and is never null; the one out parameter is
        /// reused for both directions.
        ///
        /// WHAT A FALSE RETURN GUARANTEES, precisely, because "nothing has been mutated" is not literally
        /// true for every path: no pool, skill, class ability, ActiveFacetSlot or equipped item is
        /// changed on any refusal, which is the guarantee that matters. The two refusals AFTER the
        /// outgoing row is queued - the re-validation gate and the gear strip - have already enqueued a
        /// SaveCharacterFacet for the OUTGOING slot. That write is idempotent and self-directed: it
        /// stores the character's current, unchanged build into its own slot's row, which is where that
        /// build already belongs. It is deliberately not moved later, because capturing the outgoing build
        /// before the strip is what makes the row correct at all.
        /// </summary>
        public bool TrySwitchFacet(int targetSlot, bool confirmed, out string refusal, out bool needsConfirmation)
        {
            needsConfirmation = false;

            if (!CheckFacetGates(targetSlot, out refusal))
                return false;

            var outgoingSlot = ActiveFacetSlot;

            // LOAD-BEARING read (see TryGetCharacterFacetRows's remarks - this call site is the exact
            // one Task 11 fix round 1's Finding 1 named). A failed or timed-out read must refuse the
            // switch outright rather than falling through to "no row for the target slot", which would
            // otherwise synthesize a fresh build and overwrite the player's real one the next time this
            // slot's row is saved. Nothing has been mutated yet at this point, so refusing here is free.
            if (!TryGetCharacterFacetRows(out var rows))
            {
                refusal = "Cannot switch right now - could not read your stored facets. Try again in a moment.";
                return false;
            }

            var targetRow = rows.Find(r => r.Slot == (byte)targetSlot);
            var outgoingRow = rows.Find(r => r.Slot == (byte)outgoingSlot);

            // First visit to this slot: DESIGN.md section 8 gates it behind a one-time confirmation,
            // because the gear strip is disruptive and the slot starts from a fresh build. Decided from
            // the read above rather than a second blocking read in the command layer - see this method's
            // remarks. Nothing has been mutated at this point.
            if (targetRow == null && !confirmed)
            {
                // refusal is deliberately left null on THIS path alone: the caller owns the prompt wording
                // and must branch on needsConfirmation before it reads refusal. Assigning a string here
                // would only look like a message that is never shown.
                needsConfirmation = true;
                return false;
            }

            List<FacetSkillEntry> incomingSkills;
            Dictionary<string, int> incomingAbilities;
            List<FacetEquipEntry> incomingEquip;
            string incomingName;

            // What the target row stored for attributes, before reconciliation. Null means there is
            // nothing readable to apply.
            Dictionary<PropertyAttribute, uint> storedAttributes;

            // What actually gets written onto the character. Null means "keep the live arrangement",
            // which is what a pre-upgrade or unreadable attrs_Json degrades to - see below.
            Dictionary<PropertyAttribute, uint> incomingAttributes;

            if (targetRow != null)
            {
                // LOAD-BEARING deserialize, and the ONLY one of the three that is. An unreadable
                // skills_Json must refuse: the pool arithmetic below releases every point of the outgoing
                // build's PP and then commits the incoming build's back, so an empty incoming list from a
                // blank or corrupt column hands the player their whole build's experience as unspent XP
                // while ApplyFacetSkills writes nothing worth the difference. Abilities and equip keep
                // the fail-open form on purpose - see FacetSnapshot.TryDeserializeSkills' remarks for
                // why empty is self-consistent for those two and is not for skills.
                if (!FacetSnapshot.TryDeserializeSkills(targetRow.SkillsJson, out incomingSkills))
                {
                    log.Error($"[FACET] {Name} (0x{Guid.Full:X8}): slot {targetSlot}'s character_facet.skills_Json is blank or unparseable; refusing the switch rather than applying an empty build. Raw length {targetRow.SkillsJson?.Length ?? -1}.");

                    refusal = $"Cannot switch: your stored build for slot {targetSlot} could not be read. Contact staff.";
                    return false;
                }

                incomingAbilities = FacetSnapshot.DeserializeAbilities(targetRow.AbilitiesJson);
                incomingEquip = FacetSnapshot.DeserializeEquip(targetRow.EquipJson);
                incomingName = targetRow.Name;

                // A THIRD degrade rule, not a copy of either of the two above. An unreadable attrs_Json
                // must NOT refuse (attrs_Json is nullable precisely because every row written before the
                // column existed has NULL there, and refusing would strand all of them), and must NOT
                // degrade to empty (an empty arrangement would delete the player's redistribution). It
                // degrades to "keep the character's live arrangement", which conserves by construction
                // and which the next switch away from this slot rewrites correctly.
                //
                // The pre-upgrade case is silent and the malformed case is logged, so the two stay
                // distinguishable in the log: a NULL/blank column is expected on every row that predates
                // this feature, while unparseable JSON in a column this server wrote is a real fault.
                if (!FacetSnapshot.TryDeserializeAttributes(targetRow.AttrsJson, out storedAttributes))
                {
                    storedAttributes = null;

                    if (!string.IsNullOrWhiteSpace(targetRow.AttrsJson))
                    {
                        log.Error($"[FACET] {Name} (0x{Guid.Full:X8}): slot {targetSlot}'s character_facet.attrs_Json could not be read as a complete six-attribute arrangement; keeping the character's live attribute arrangement for this switch. Raw length {targetRow.AttrsJson.Length}.");
                    }
                }

                incomingAttributes = null;   // filled in by the reconciliation below, if there is anything to reconcile
            }
            else
            {
                incomingSkills = BuildFreshFacetSkills();
                incomingAbilities = new Dictionary<string, int>();
                incomingEquip = new List<FacetEquipEntry>();
                incomingName = null;

                // A fresh slot INHERITS the character's current arrangement, so its sum is the live sum
                // and conservation holds trivially - there is nothing to reconcile. See
                // BuildFreshFacetAttributes for why this is not a reset or an even split.
                storedAttributes = null;
                incomingAttributes = BuildFreshFacetAttributes();
            }

            var outgoingSkills = CaptureFacetSkills();
            var outgoingAbilities = CaptureFacetAbilities();
            var outgoingEquip = CaptureFacetEquip();
            var outgoingAttributes = CaptureFacetAttributes();

            var outgoingPp = FacetPools.TotalPp(outgoingSkills);
            var incomingPp = FacetPools.TotalPp(incomingSkills);

            var newAvailableXp = FacetPools.AvailableExperienceAfterSwap(AvailableExperience ?? 0, outgoingPp, incomingPp, out var shortfall);

            if (shortfall > 0)
            {
                refusal = $"Cannot switch: short {shortfall:N0} experience. XP spent on attributes or vitals while away from a build does not come back - free up experience or spend less elsewhere first.";
                return false;
            }

            // BOTH sides are priced, not just the incoming one, and both through the INSTANCE
            // LookupSkillCreditCost so the price is what the engine charged this character rather than
            // what the dat generically lists. Subtracting a lifetime total by a generic price zeroes an
            // aug-holding character's whole credit pool; subtracting the two builds' prices from each
            // other fixes that, but only for a skill whose SAC is the same on both sides - and SAC is
            // per-build, so an aug-spec skill Specialized live and Trained in the stored row would
            // otherwise release a raw 999 for free. See LookupSkillCreditCost and
            // FacetPools.AvailableSkillCreditsAfterSwap for the full argument.
            uint incomingCreditsSpent;
            uint outgoingCreditsSpent;

            try
            {
                incomingCreditsSpent = FacetPools.SkillCreditsSpent(incomingSkills, LookupSkillCreditCost);
                outgoingCreditsSpent = FacetPools.SkillCreditsSpent(outgoingSkills, LookupSkillCreditCost);
            }
            catch (Exception ex)
            {
                // LookupSkillCreditCost indexes SkillBaseHash directly and throws KeyNotFoundException
                // for a skill the dat does not recognize. That was survivable while only a Developer
                // command reached it (CommandManager wraps handlers in try/catch); /facet is
                // player-facing and must turn the same throw into a clean refusal instead of an
                // uncaught-looking crash.
                log.Error($"[FACET] {Name} (0x{Guid.Full:X8}): could not price the skill credits for a switch to slot {targetSlot}; refusing rather than switching with an unknown cost.", ex);

                refusal = "Cannot switch: that build contains a skill this server does not recognize. Contact staff.";
                return false;
            }

            var newAvailableCredits = FacetPools.AvailableSkillCreditsAfterSwap(
                AvailableSkillCredits ?? 0, outgoingCreditsSpent, incomingCreditsSpent, out var creditShortfall);

            if (creditShortfall > 0)
            {
                refusal = $"Cannot switch: short {creditShortfall:N0} skill credit(s) for that build. Untrain something on this facet first.";
                return false;
            }

            var outgoingAbilityPointsSpent = ClassAbilityPointsSpent(outgoingAbilities);
            var incomingAbilityPointsSpent = ClassAbilityPointsSpent(incomingAbilities);

            // Delta form, NOT a recompute from TotalClassAbilityPointsEarned. Learned abilities are not the
            // only sink: the Class Ability Point vendor currency (Player_Bank.DebitBankedAlternateCurrency)
            // and prepaid training vouchers (Player_ClassAbilityTokens) both debit
            // AvailableClassAbilityPoints without touching the lifetime ledger, so a recompute REFUNDED
            // every vendor spend on every switch - free, instant and repeatable, since switching has no
            // cooldown. See FacetPools.AvailableClassAbilityPointsAfterSwap.
            var newAvailableAbilityPoints = FacetPools.AvailableClassAbilityPointsAfterSwap(
                AvailableClassAbilityPoints, outgoingAbilityPointsSpent, incomingAbilityPointsSpent, out var abilityPointShortfall);

            if (abilityPointShortfall > 0)
            {
                refusal = $"Cannot switch: short {abilityPointShortfall:N0} class ability point(s) for that build. Points spent at a vendor do not come back - unlearn something on this facet first.";
                return false;
            }

            // THE ATTRIBUTE CONSERVATION CHECK - the fourth pre-mutation refusal, in the same shape and
            // the same position as the three above, and a real runtime check rather than a comment
            // because unlike the pool invariants this one is two sums and needs no dat read or cost
            // function. See FacetAttributes for the writer table this rests on and for why a surplus is
            // reconciled while a deficit is refused.
            string attributeSurplusLine = null;

            if (storedAttributes != null)
            {
                var reconciled = FacetAttributes.Reconcile(
                    storedAttributes, FacetAttributes.Sum(outgoingAttributes), out var attributeSurplus, out var attributeShortfall);

                if (attributeShortfall > 0)
                {
                    // Only reachable through an admin subtraction (@modifyattr with a negative delta) -
                    // no player action lowers the innate total. There is no safe automatic answer: every
                    // arrangement summing to the smaller live total is one the player did not choose, and
                    // silently deleting the difference is the exact failure this design exists to prevent.
                    log.Error($"[FACET] {Name} (0x{Guid.Full:X8}): slot {targetSlot}'s stored attribute arrangement sums to {attributeShortfall:N0} MORE than the character's live innate total ({FacetAttributes.Sum(outgoingAttributes):N0}); refusing the switch rather than applying an arrangement that would delete those points. Reachable only via an admin attribute subtraction - reconcile the character's innate attributes against the stored row before letting this switch through.");

                    refusal = $"Cannot switch: your stored attributes for facet {targetSlot} do not add up to your current innate total ({attributeShortfall:N0} short). This needs a staff fix - please contact staff rather than retrying.";
                    return false;
                }

                if (reconciled == null)
                {
                    // Guard, not an expected state: Reconcile only returns null with no shortfall for a
                    // partial arrangement, and TryDeserializeAttributes above has already rejected those.
                    // Keeping the live arrangement is the safe fallback either way.
                    log.Error($"[FACET] {Name} (0x{Guid.Full:X8}): slot {targetSlot}'s stored attribute arrangement could not be reconciled and named no shortfall; keeping the character's live attribute arrangement for this switch.");
                }
                else
                {
                    incomingAttributes = reconciled;

                    if (attributeSurplus > 0)
                        attributeSurplusLine = ComposeAttributeSurplusLine(storedAttributes, reconciled, attributeSurplus);
                }
            }

            // Observability only, and it must STAY observability only. This comparison used to DRIVE the
            // pool value, which is exactly how the vendor-refund hole got in: it is a ledger derivation,
            // and a ledger derivation cannot see a spend that never touched the lifetime total. Nothing
            // below reads derivedForOutgoing.
            //
            // A disagreement is therefore no longer even necessarily a fault - any character who has ever
            // bought a training voucher or spent points at a vendor shows one permanently - so the line is
            // a lead worth auditing, not an error, and the wording has to say so or it will be read as one.
            var derivedForOutgoing = (long)TotalClassAbilityPointsEarned - outgoingAbilityPointsSpent;

            if (derivedForOutgoing != AvailableClassAbilityPoints)
            {
                log.Warn($"[FACET] {Name} (0x{Guid.Full:X8}): AvailableClassAbilityPoints ({AvailableClassAbilityPoints}) does not match TotalClassAbilityPointsEarned minus the current build's spend ({derivedForOutgoing}) before a facet switch. Expected whenever points have been spent at a vendor or on a training voucher, since neither adjusts the lifetime total; the switch carries the stored value forward unchanged either way. Audit only if this character has no such purchases.");
            }

            // Everything above this line is read-only - nothing has been mutated yet, and every refusal
            // path above returns before this point.

            var outgoingFacetRow = new CharacterFacet
            {
                CharacterId = Character.Id,
                Slot = (byte)outgoingSlot,
                Name = outgoingRow?.Name,
                SkillsJson = FacetSnapshot.SerializeSkills(outgoingSkills),
                AbilitiesJson = FacetSnapshot.SerializeAbilities(outgoingAbilities),
                EquipJson = FacetSnapshot.SerializeEquip(outgoingEquip),
                AttrsJson = FacetSnapshot.SerializeAttributes(outgoingAttributes),
                UpdatedAt = DateTime.UtcNow,
            };

            SaveFacetRowAsync(outgoingFacetRow);

            // Task 11 fix round 1, Finding 2: the blocking read above (bounded at FacetDbTimeoutMs =
            // 5000ms) plus the shortfall/credit-pricing work in between is a real window during which
            // state can change, and TryStripForFacetSwitch's own precondition only re-checks IsBusy -
            // not IsTrading (set at Player_Trade.cs:88 WITHOUT setting IsBusy, so a trade invite accepted
            // during this window is NOT caught by the strip's own check), IsInDeathProcess, Teleporting,
            // or LastOpenedContainerId. Re-running the FULL gate list here - rather than re-checking the
            // individual flags a second time - means this can never drift out of sync with
            // CheckFacetGates' own list. Do NOT delete this as "redundant" with the check at the top of
            // this method: the whole point is that time (and a blocking wait) has passed since then.
            if (!CheckFacetGates(targetSlot, out refusal))
                return false;

            if (!TryStripForFacetSwitch(out refusal))
                return false;

            ApplyFacetSkills(incomingSkills);

            // BEFORE the gear restore at the bottom of this method, and that ordering is a hard
            // constraint rather than tidiness: CheckWieldRequirements reads attributes
            // (WieldRequirement.Attrib / RawAttrib / SecondaryAttrib) and runs at equip time, so gear
            // remembered on a facet whose arrangement is what qualifies for it would fail its own wield
            // check if the attributes were applied afterwards. A null argument is the "keep live" case.
            ApplyFacetAttributes(incomingAttributes);

            ApplyFacetAbilities(incomingAbilities);

            // Class abilities: push SendEnhancedStatUpdate once per ability whose rank actually changed
            // (DESIGN.md section 7.1), AFTER ApplyFacetAbilities so the live cache already reflects the
            // incoming state when the update message is built.
            var changedAbilityNames = new HashSet<string>(outgoingAbilities.Keys);

            foreach (var name in incomingAbilities.Keys)
                changedAbilityNames.Add(name);

            foreach (var name in changedAbilityNames)
            {
                var oldRank = outgoingAbilities.TryGetValue(name, out var o) ? o : 0;
                var newRank = incomingAbilities.TryGetValue(name, out var n) ? n : 0;

                if (oldRank == newRank)
                    continue;

                if (ClassAbilityRegistry.TryGetByName(name, out var definition))
                    SendEnhancedStatUpdate(definition);
            }

            AvailableExperience = newAvailableXp;
            AvailableSkillCredits = newAvailableCredits;

            // CAP funnel: the counter is read-only now, so the switch's computed absolute value goes in
            // as a delta through the one mutator that records it in `character_cap_ledger`. Wired for
            // completeness, not because facets are suspected - prod holds zero `character_facet` rows,
            // so this branch is currently unreachable there and the repo owner has ruled facets out of
            // scope for the incident. Nothing else in this file changes; in particular the observability
            // log.Warn above is deliberately left exactly as it is.
            AdjustClassAbilityPoints(newAvailableAbilityPoints - AvailableClassAbilityPoints, 0, CapLedgerReason.FacetSwitch,
                detail: $"facet switch to slot {targetSlot}");

            Session?.Network.EnqueueSend(
                new GameMessagePrivateUpdatePropertyInt64(this, PropertyInt64.AvailableExperience, AvailableExperience ?? 0),
                new GameMessagePrivateUpdatePropertyInt(this, PropertyInt.AvailableSkillCredits, AvailableSkillCredits ?? 0),
                new GameMessagePrivateUpdatePropertyInt(this, PropertyInt.AvailableClassAbilityPoints, AvailableClassAbilityPoints));

            ActiveFacetSlot = targetSlot;

            SaveBiotaToDatabase();
            SaveCharacterToDatabase();

            // The switch is COMMITTED by this point - slot, pools, biota and character are all written.
            // RestoreFacetEquip reaches into AccountVaultStore, TryCreateInInventoryWithNetworking,
            // CheckWieldRequirements and TryEquipObjectWithNetworking, any of which can throw; without this
            // catch the throw unwinds past the summary below, out of HandleSwitch, and is swallowed by
            // CommandManager - leaving the player with a silently changed character sheet, no chat line at
            // all, and "You are already using that facet" if they retype the command. Swallowing is the
            // right call HERE specifically, and only because the switch has already committed: there is
            // nothing left to abort, and losing the report is the only remaining harm.
            List<string> restoreReport;

            try
            {
                restoreReport = RestoreFacetEquip(incomingEquip);
            }
            catch (Exception ex)
            {
                log.Error($"[FACET] {Name} (0x{Guid.Full:X8}): the gear restore threw AFTER the switch to slot {targetSlot} had committed (slot, pools and both saves are already applied). Some gear may be unequipped, in the pack, or still in the account vault; this needs checking against account_vault_log if any vault withdrawal was involved.", ex);

                restoreReport = new List<string> { "Your build was switched, but some gear could not be restored - contact staff." };
            }

            var summary = new System.Text.StringBuilder();

            summary.Append($"You turn the stone to facet {targetSlot}");

            if (!string.IsNullOrWhiteSpace(incomingName))
                summary.Append($" (\"{incomingName}\")");

            summary.Append($". Available: {AvailableExperience ?? 0:N0} XP, {AvailableSkillCredits ?? 0:N0} skill credit(s), {AvailableClassAbilityPoints:N0} class ability point(s).");

            // Named rather than silent: the player's arrangement on this facet is now NOT the one the
            // slot remembered, and the difference is real points they earned (an innate augmentation)
            // after the row was written. Saying where they landed is the only way they can tell that
            // from a bug.
            if (attributeSurplusLine != null)
                summary.Append(" " + attributeSurplusLine);

            if (restoreReport.Count > 0)
                summary.Append(" " + string.Join(" ", restoreReport));

            refusal = summary.ToString();
            return true;
        }
    }

    /// <summary>
    /// Bundles the six facet_* tunables read together on every gate check, mirroring
    /// WorldEventRosterSelector.BandDials/DialSource: one seam swap covers every dial at once instead of
    /// one per property.
    /// </summary>
    internal readonly struct FacetDials
    {
        public FacetDials(bool enabled, long slot2Level, long slot3Level, long slot4Level, HashSet<ushort> allowlist, string allowlistName)
        {
            Enabled = enabled;
            Slot2Level = slot2Level;
            Slot3Level = slot3Level;
            Slot4Level = slot4Level;
            Allowlist = allowlist;
            AllowlistName = allowlistName;
        }

        public bool Enabled { get; }
        public long Slot2Level { get; }
        public long Slot3Level { get; }
        public long Slot4Level { get; }
        public HashSet<ushort> Allowlist { get; }
        public string AllowlistName { get; }
    }

    /// <summary>
    /// Test seam for the facet_* tunables, following WorldEventRosterSelector.DialSource exactly.
    /// PropertyManager.Get* throws in ACE.Server.Tests for any uncached key (there is no shard config at
    /// all in that environment), so every read here is wrapped and falls back to the compiled defaults -
    /// the SAME defaults the tunables ship with, so a test-environment read and a fresh, unconfigured
    /// shard read agree.
    /// </summary>
    internal static class FacetTunables
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(FacetTunables));

        internal static Func<FacetDials> DialSource = ReadFromProperties;

        private const string AllowlistNameFallback = "the permitted area";

        private static FacetDials ReadFromProperties()
        {
            try
            {
                var enabled = PropertyManager.GetBool("facet_enabled", false).Item;
                var slot2 = PropertyManager.GetLong("facet_slot2_level", 300).Item;
                var slot3 = PropertyManager.GetLong("facet_slot3_level", 400).Item;
                var slot4 = PropertyManager.GetLong("facet_slot4_level", 500).Item;
                var allowlist = ParseLandblockList(PropertyManager.GetString("facet_allowlist", "016C").Item);
                var allowlistNameRaw = PropertyManager.GetString("facet_allowlist_name", "the Marketplace").Item;
                var allowlistName = string.IsNullOrWhiteSpace(allowlistNameRaw) ? AllowlistNameFallback : allowlistNameRaw.Trim();

                return new FacetDials(enabled, slot2, slot3, slot4, allowlist, allowlistName);
            }
            catch (Exception ex)
            {
                log.Error("[FACET] could not read the facet_* tunables; falling back to built-in defaults (facet_enabled=false)", ex);

                return new FacetDials(false, 300, 400, 500, new HashSet<ushort> { 0x016C }, "the Marketplace");
            }
        }

        /// <summary>
        /// Parses facet_allowlist with the exact same rules MuleSummonHandler.ParseLandblockList uses
        /// for account_vault_allowlist: 0x016C or 016C, case-insensitive, whitespace-tolerant, empty
        /// entries skipped, and a malformed entry is skipped with a logged warning rather than thrown - a
        /// bad config string must never break a facet switch. An EMPTY result means no allowlist is
        /// configured and every landblock is permitted; it does NOT mean nowhere is.
        /// </summary>
        private static HashSet<ushort> ParseLandblockList(string raw)
        {
            var result = new HashSet<ushort>();

            if (string.IsNullOrWhiteSpace(raw))
                return result;

            foreach (var token in raw.Split(','))
            {
                var trimmed = token.Trim();

                if (trimmed.Length == 0)
                    continue;

                if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    trimmed = trimmed.Substring(2);

                if (ushort.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var landblock))
                    result.Add(landblock);
                else
                    log.Warn($"FacetTunables.ParseLandblockList: skipping malformed entry '{token}' in facet_allowlist.");
            }

            return result;
        }
    }
}
