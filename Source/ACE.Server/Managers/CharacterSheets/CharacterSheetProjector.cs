using System;
using System.Collections.Generic;
using System.Linq;

using log4net;

using ACE.Common.Extensions;
using ACE.Database.Models.Shard;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.ClassAbilities;
using ACE.Server.Managers.Market;
using ACE.Server.Managers.Market.Suit;
using ACE.Server.WorldObjects;

namespace ACE.Server.Managers.CharacterSheets
{
    /// <summary>
    /// Copies a Player into a <see cref="CharacterSheet"/>. Ranks are NOT filled here; the service
    /// attaches them afterwards.
    ///
    /// THREADING: Project must run on the thread that owns the player - the online player's own action
    /// queue, or the hydration pool thread for an offline Player from ProfileBuilder.LoadOfflinePlayerAsync.
    /// EquippedObjects and Creature.Skills are plain Dictionaries with no lock of their own.
    ///
    /// NEVER MUTATES THE PLAYER, because the online caller passes a live one: skills are read with
    /// GetCreatureSkill(skill, add: false), which never creates a biota skill row, and items go through
    /// MarketSnapshot.FromItem, which avoids the write-on-read WorldObject.Workmanship getter. The
    /// in-memory read caches these getters fill (Creature.Skills wrappers, the class ability rank cache)
    /// are memoization, not persisted state. FromItem takes each item's own BiotaDatabaseLock, so this
    /// must never be called while holding one. Attribute and vital getters (CreatureAttribute.Base/Current,
    /// CreatureVital.Base/MaxValue) are computed reads over the player's existing dictionaries; they do
    /// not write anything back.
    /// </summary>
    public static class CharacterSheetProjector
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private const string Specialized = "specialized";
        private const string Trained = "trained";
        private const string UnknownSlot = "Unknown";

        internal static readonly (PropertyAttribute key, string name)[] AttributeOrder =
        {
            (PropertyAttribute.Strength, "Strength"),
            (PropertyAttribute.Endurance, "Endurance"),
            (PropertyAttribute.Coordination, "Coordination"),
            (PropertyAttribute.Quickness, "Quickness"),
            (PropertyAttribute.Focus, "Focus"),
            (PropertyAttribute.Self, "Self"),
        };

        internal static readonly (PropertyAttribute2nd key, string name)[] VitalOrder =
        {
            (PropertyAttribute2nd.MaxHealth, "Health"),
            (PropertyAttribute2nd.MaxStamina, "Stamina"),
            (PropertyAttribute2nd.MaxMana, "Mana"),
        };

        /// <summary>
        /// One equipped item's sheet entry. Its snapshot deliberately carries NO suit projection
        /// (MarketSnapshot.FromItem's includeSuit stays false): the suit builder reads equipped gear
        /// through ProjectEquippedForSuit, and a second copy here would be labelled source "vault".
        /// </summary>
        internal static SheetItem ProjectEquippedItem(WorldObject item)
            => new SheetItem
            {
                ItemGuid = item.Guid.Full,
                Name = item.Name,
                Slot = SlotName(item.CurrentWieldedLocation),
                Snapshot = MarketSnapshot.FromItem(item),
            };

        public static CharacterSheet Project(Player player, bool includeClassAbilities, DateTime nowUtc, bool ownerView = false)
        {
            if (player == null)
                throw new ArgumentNullException(nameof(player));

            var sheet = new CharacterSheet
            {
                Name = player.Name,
                Level = player.Level ?? 0,
                GeneratedAt = nowUtc,
            };

            sheet.Attributes = ProjectAttributes(player);
            sheet.Vitals = ProjectVitals(player);

            var items = new List<SheetItem>();
            foreach (var item in player.EquippedObjects.Values.ToList())
            {
                try
                {
                    items.Add(ProjectEquippedItem(item));
                }
                catch (Exception ex)
                {
                    // One bad item is skipped, never the whole sheet.
                    log.Error($"[CHARSHEET] could not project equipped item 0x{item?.Guid.Full:X8} of {player.Name}; skipping it: {ex.GetFullMessage()}");
                }
            }
            sheet.Equipped = OrderItems(items);

            sheet.Skills = ProjectSkills(player);

            if (includeClassAbilities)
            {
                var abilities = new List<SheetAbility>();
                foreach (var def in ClassAbilityRegistry.Abilities.Values)
                {
                    if (!def.Implemented)
                        continue;

                    var rank = player.GetClassAbilityRank(def.Id);
                    if (rank <= 0)
                        continue;

                    abilities.Add(new SheetAbility
                    {
                        Name = def.Name,
                        DisplayName = def.DisplayName,
                        Class = def.AbilityClass.ToString(),
                        Tier = def.Tier,
                        Rank = rank,
                        MaxRank = def.MaxRank,
                    });
                }

                sheet.ClassAbilities = abilities
                    .OrderBy(a => a.Class, StringComparer.Ordinal)
                    .ThenBy(a => a.Tier)
                    .ThenBy(a => a.DisplayName, StringComparer.Ordinal)
                    .ToList();
            }

            if (ownerView)
                sheet.Owner = ProjectOwner(player, sheet);

            return sheet;
        }

        private static List<SheetStat> ProjectAttributes(Player player)
        {
            var stats = new List<SheetStat>();

            foreach (var (key, name) in AttributeOrder)
            {
                if (player.Attributes.TryGetValue(key, out var attr))
                    stats.Add(new SheetStat { Name = name, Base = attr.Base, Current = attr.Current });
            }

            return stats;
        }

        private static List<SheetStat> ProjectVitals(Player player)
        {
            var stats = new List<SheetStat>();

            foreach (var (key, name) in VitalOrder)
            {
                if (player.Vitals.TryGetValue(key, out var vital))
                    stats.Add(new SheetStat { Name = name, Base = vital.Base, Current = vital.MaxValue });
            }

            return stats;
        }

        private static List<SheetSkill> ProjectSkills(Player player)
        {
            var skills = new List<SheetSkill>();
            foreach (Skill s in Enum.GetValues(typeof(Skill)))
            {
                if (s == Skill.None)
                    continue;

                // add: false - never create a skill row on a live Player.
                var cs = player.GetCreatureSkill(s, add: false);
                if (cs == null)
                    continue;

                var advancement = AdvancementName(cs.AdvancementClass);
                if (advancement == null)
                    continue;

                skills.Add(new SheetSkill
                {
                    Name = s.ToSentence(),
                    Advancement = advancement,
                    Base = cs.Base,
                    Current = cs.Current,
                });
            }
            return OrderSkills(skills);
        }

        /// <summary>
        /// The equipped items that matter to a suit (<see cref="SuitItemProjector.IsSuitRelevant(WorldObject)"/>
        /// - shields, weapons, aetheria and sigils are dropped), each as an "equipped" SuitItem. The filter
        /// runs BEFORE the projection. One bad item is skipped, never the profile. Split out so the filter is
        /// testable without a live Player.
        /// </summary>
        internal static List<SuitItem> ProjectEquippedForSuit(IEnumerable<WorldObject> items, string ownerName, uint? holderGuid = null)
        {
            var equipped = new List<SuitItem>();

            foreach (var item in items)
            {
                try
                {
                    if (!SuitItemProjector.IsSuitRelevant(item))
                        continue;

                    equipped.Add(SuitItemProjector.FromItem(item, SuitItem.SourceEquipped)
                        with { CurrentWieldedLocation = item.GetProperty(PropertyInt.CurrentWieldedLocation), HolderGuid = holderGuid });
                }
                catch (Exception ex)
                {
                    log.Error($"[CHARSHEET] could not project equipped item 0x{item?.Guid.Full:X8} of {ownerName} for the suit profile; skipping it: {ex.GetFullMessage()}");
                }
            }

            return equipped;
        }

        /// <summary>Most suit-relevant pack items one profile will project; the rest are dropped with a log line.</summary>
        internal const int MaxPackSuitItems = 300;

        /// <summary>
        /// The carried (not worn) items that matter to a suit, each as a "pack" SuitItem. Walks the main pack and
        /// each side container's Inventory, one level deep, deduped by guid like Player.GetAllPossessions; anything
        /// whose guid is in <paramref name="equippedGuids"/> is dropped. The suit-relevance filter runs BEFORE the
        /// projection, so a non-suit item costs one property read. At most <paramref name="maxItems"/> are projected,
        /// in a DETERMINISTIC order (main-pack items by guid, then side packs by PlacementPosition then guid, each
        /// with its items by guid), so which items survive the cap does not depend on dictionary order.
        /// <paramref name="truncated"/> is true when a suit-relevant item was dropped by the cap (also logged).
        /// One bad item is skipped, never the profile. Pure over its inputs, so it is testable without a live Player.
        /// </summary>
        internal static List<SuitItem> ProjectPackForSuit(IEnumerable<WorldObject> inventory, ISet<uint> equippedGuids, uint holderGuid, string ownerName, int maxItems, out bool truncated)
        {
            truncated = false;

            var pack = new List<SuitItem>();
            var seen = new HashSet<uint>();
            var mainPack = inventory.Where(i => i != null).OrderBy(i => i.Guid.Full).ToList();

            IEnumerable<WorldObject> Walk()
            {
                foreach (var item in mainPack)
                    yield return item;

                foreach (var container in mainPack.OfType<Container>().OrderBy(c => c.PlacementPosition ?? 0).ThenBy(c => c.Guid.Full))
                {
                    foreach (var sub in container.Inventory.Values.OrderBy(i => i.Guid.Full).ToList())
                        yield return sub;
                }
            }

            foreach (var item in Walk())
            {
                try
                {
                    if (!seen.Add(item.Guid.Full) || equippedGuids.Contains(item.Guid.Full))
                        continue;

                    if (!SuitItemProjector.IsSuitRelevant(item))
                        continue;

                    if (pack.Count >= maxItems)
                    {
                        truncated = true;
                        log.Warn($"[CHARSHEET] {ownerName} carries more than {maxItems} suit-relevant pack items; the suit profile drops the rest.");
                        break;
                    }

                    pack.Add(SuitItemProjector.FromItem(item, SuitItem.SourcePack) with { HolderGuid = holderGuid });
                }
                catch (Exception ex)
                {
                    log.Error($"[CHARSHEET] could not project pack item 0x{item?.Guid.Full:X8} of {ownerName} for the suit profile; skipping it: {ex.GetFullMessage()}");
                }
            }

            return pack;
        }

        internal static List<SuitItem> ProjectPackForSuit(IEnumerable<WorldObject> inventory, ISet<uint> equippedGuids, uint holderGuid, string ownerName, int maxItems = MaxPackSuitItems)
            => ProjectPackForSuit(inventory, equippedGuids, holderGuid, ownerName, maxItems, out _);

        /// <summary>
        /// The owner view's gear lists from the two collections ProjectOwner reads off the Player: the WORN items
        /// (EquippedObjects) become "equipped" rows and their appraisal, and the CARRIED items (Inventory) become
        /// "pack" rows minus anything worn. Split out so a test can pin which collection feeds which list.
        /// </summary>
        internal static (List<SuitItem> Equipped, List<SheetItem> Appraisal, List<SuitItem> Pack, bool PackTruncated) ProjectOwnerGear(
            IEnumerable<WorldObject> equippedObjects, IEnumerable<WorldObject> inventory, string ownerName, List<SheetItem> sheetEquipped, uint holderGuid)
        {
            var worn = equippedObjects.ToList();
            var (equipped, appraisal) = ProjectOwnerEquipment(worn, ownerName, sheetEquipped, holderGuid);
            var pack = ProjectPackForSuit(inventory, new HashSet<uint>(worn.Select(i => i.Guid.Full)), holderGuid, ownerName, MaxPackSuitItems, out var truncated);

            return (equipped, appraisal, pack, truncated);
        }

        /// <summary>
        /// The owner-only extras: heritage, and the equipped items that matter to a suit (SuitItemProjector.
        /// IsSuitRelevant), each projected as a "equipped" SuitItem. Same threading rule and same no-mutation
        /// guarantee as <see cref="Project"/>: the filter and the projection both read through GetProperty.
        /// </summary>
        private static OwnerProfile ProjectOwner(Player player, CharacterSheet sheet)
        {
            var gear = ProjectOwnerGear(player.EquippedObjects.Values, player.Inventory.Values, player.Name, sheet.Equipped, player.Guid.Full);
            var heritage = ProjectHeritage(player.HeritageGroupName, player.HeritageGroup);

            return new OwnerProfile
            {
                CharacterGuid = player.Guid.Full,
                Name = sheet.Name,
                Level = sheet.Level,
                Heritage = heritage.Name,
                HeritageId = heritage.Id,
                Attributes = sheet.Attributes,
                Vitals = sheet.Vitals,
                Skills = sheet.Skills,
                Equipped = gear.Equipped,
                EquippedAppraisal = gear.Appraisal,
                Pack = gear.Pack,
                PackTruncated = gear.PackTruncated,
                ClassAbilityImport = GuardedClassAbilityImport(() => ProjectClassAbilityImport(player), player.Name),
            };
        }

        /// <summary>
        /// Gathers the import projection's inputs from the Player (quest registry rows, the bought-point and
        /// balance PropertyInts, the respec flags) and hands them to the pure <see cref="ClassAbilityImportProjection"/>.
        /// respec_pending mirrors Player.ApplyClassAbilityOverhaulRespec's two guards: PropertyInt 9067 unset AND
        /// class_ability_overhaul_respec_sweep_enabled on.
        /// </summary>
        private static ClassAbilityImportBlock ProjectClassAbilityImport(Player player)
        {
            return BuildClassAbilityImport(
                player.Character.GetQuests(player.CharacterDatabaseLock),
                player.ClassAbilityPointsPurchasedWithLum,
                player.ClassAbilityPointsPurchasedWithXp,
                player.TotalClassAbilityPointsEarned,
                player.AvailableClassAbilityPoints,
                player.GetProperty(PropertyInt.ClassAbilityOverhaulRespecDone) ?? 0,
                PropertyManager.GetBool("class_ability_overhaul_respec_sweep_enabled").Item);
        }

        /// <summary>
        /// The input-gathering seam, taking already-read values so a test can drive it without a Player.
        /// respec_pending is the stamp being unset AND the sweep switch on, the two guards of
        /// Player.ApplyClassAbilityOverhaulRespec.
        /// </summary>
        internal static ClassAbilityImportBlock BuildClassAbilityImport(
            IEnumerable<CharacterPropertiesQuestRegistry> quests, int lumBought, int xpBought, int earned, int available, int respecDone, bool sweepEnabled)
        {
            var rows = (quests ?? Enumerable.Empty<CharacterPropertiesQuestRegistry>())
                .Select(q => new ImportQuestRow(q.QuestName, q.NumTimesCompleted))
                .ToList();

            return ClassAbilityImportProjection.Project(rows, lumBought, xpBought, earned, available, respecDone == 0 && sweepEnabled);
        }

        /// <summary>
        /// Runs the import block build and swallows a failure to null, so a fault in this optional block can never
        /// take down the suit-profile (same owner build). The import route answers a null block with a server error.
        /// </summary>
        internal static ClassAbilityImportBlock GuardedClassAbilityImport(Func<ClassAbilityImportBlock> build, string characterName)
        {
            try
            {
                return build();
            }
            catch (Exception ex)
            {
                log.Error($"[CHARSHEET] could not project the class ability import block of {characterName}; leaving it null: {ex.GetFullMessage()}");
                return null;
            }
        }
        /// <summary>
        /// The owner view's two equipment lists: the suit-relevant items as "equipped" SuitItems, and the sheet
        /// snapshots of exactly those items. ProjectOwner calls only this, so a test can pin the wiring without a Player.
        /// </summary>
        internal static (List<SuitItem> Equipped, List<SheetItem> Appraisal) ProjectOwnerEquipment(IEnumerable<WorldObject> items, string ownerName, List<SheetItem> sheetEquipped, uint? holderGuid = null)
        {
            var equipped = ProjectEquippedForSuit(items, ownerName, holderGuid);
            return (equipped, JoinEquippedAppraisal(sheetEquipped, equipped));
        }

        /// <summary>
        /// The sheet's equipped items (appraisal snapshots) that also appear in the suit's equipped list, matched
        /// on ItemGuid and kept in the sheet's order. Weapons, shields and aetheria have no suit entry and drop
        /// out; so does a suit entry without a guid. Pure, so it is testable without a Player.
        /// </summary>
        internal static List<SheetItem> JoinEquippedAppraisal(IEnumerable<SheetItem> sheetEquipped, IEnumerable<SuitItem> suitEquipped)
        {
            var guids = new HashSet<uint>(suitEquipped.Where(s => s.ItemGuid.HasValue).Select(s => s.ItemGuid.Value));
            return sheetEquipped.Where(i => guids.Contains(i.ItemGuid)).ToList();
        }

        /// <summary>
        /// The owner profile's two heritage fields. The display name is unchanged (the stored name, else the enum
        /// name); the numeric id is the HeritageGroup itself, because two heritages can share a display name
        /// (HeritageGroupExtensions.ToSentence renders OlthoiAcid as "Olthoi"), and armor restricted by
        /// HeritageSpecificArmor names the id, not the text.
        /// </summary>
        internal static (string Name, int Id) ProjectHeritage(string heritageGroupName, HeritageGroup heritageGroup)
            => (heritageGroupName ?? heritageGroup.ToString(), (int)heritageGroup);

        /// <summary>Specialized first, then trained; each group by Name, ordinal.</summary>
        internal static List<SheetSkill> OrderSkills(IEnumerable<SheetSkill> skills)
        {
            return skills
                .OrderBy(s => s.Advancement == Specialized ? 0 : 1)
                .ThenBy(s => s.Name, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>The wire name for a shown advancement class; null for everything the sheet hides.</summary>
        internal static string AdvancementName(SkillAdvancementClass sac)
        {
            switch (sac)
            {
                case SkillAdvancementClass.Specialized:
                    return Specialized;
                case SkillAdvancementClass.Trained:
                    return Trained;
                default:
                    return null;
            }
        }

        /// <summary>
        /// The item's PRIMARY slot as a single EquipMask flag name, or "Unknown" when the item carries no
        /// wielded location. EquipMask is [Flags] and multi-slot armor sets several bits, whose ToString reads
        /// "ChestArmor, AbdomenArmor"; the sheet shows ONE slot instead. Primary is the LOWEST set flag in
        /// EquipMask bit order, so chest plus abdomen is "ChestArmor" (0x200 before 0x400). A location with no
        /// named single-bit flag set is "Unknown".
        /// </summary>
        internal static string SlotName(EquipMask? location)
        {
            if (!location.HasValue || location.Value == 0)
                return UnknownSlot;

            var bits = (uint)location.Value;

            foreach (var (flag, name) in SingleSlotFlagsInDisplayOrder)
            {
                if ((bits & (uint)flag) != 0)
                    return name;
            }

            return UnknownSlot;
        }

        // every single-bit EquipMask member, lowest bit first, so the first match is the lowest set flag
        private static readonly (EquipMask flag, string name)[] SingleSlotFlagsInDisplayOrder =
            Enum.GetValues(typeof(EquipMask)).Cast<EquipMask>()
                .Where(m => m != 0 && ((uint)m & ((uint)m - 1)) == 0)
                .Select(m => (flag: m, name: m.ToString()))
                .OrderBy(t => (uint)t.flag)
                .ToArray();

        /// <summary>By Slot, then Name; both ordinal.</summary>
        internal static List<SheetItem> OrderItems(IEnumerable<SheetItem> items)
        {
            return items
                .OrderBy(i => i.Slot, StringComparer.Ordinal)
                .ThenBy(i => i.Name, StringComparer.Ordinal)
                .ToList();
        }
    }
}
