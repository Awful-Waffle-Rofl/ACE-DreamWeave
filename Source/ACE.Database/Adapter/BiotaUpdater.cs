using System;
using System.Collections.Generic;
using System.Linq;

using log4net;

using Microsoft.EntityFrameworkCore;

using ACE.Database.Models.Shard;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;

namespace ACE.Database.Adapter
{
    public static class BiotaUpdater
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Marks every element of one of targetBiota's navigation collections that shouldRemove accepts as
        /// Deleted on the context.
        ///
        /// The removals are collected first and applied afterwards, and that is the whole point of this
        /// helper: DbSet.Remove CAN mutate the navigation collection it was handed an element of, so removing
        /// while enumerating that collection throws "Collection was modified; enumeration operation may not
        /// execute". It happens only for a dependent in the ADDED state - Remove detaches that one instead of
        /// deleting it, and EF Core's navigation fixup takes a detached dependent out of its principal's
        /// navigation collection immediately. Removing an Unchanged dependent only marks it Deleted and leaves
        /// it in the collection, which is why the plain foreach this replaces worked for years and then failed
        /// (2026-09-07/08 prod) once a save had left Added rows on a retained ShardDbContext.
        /// BiotaUpdaterCollectionMutationTests pins both halves of that against the real EF model.
        ///
        /// Nothing is allocated when nothing needs removing, which is the common case for most of the ~24
        /// property tables on any one save.
        /// </summary>
        private static void RemoveWhere<T>(DbSet<T> set, ICollection<T> values, Func<T, bool> shouldRemove) where T : class
        {
            if (values == null || values.Count == 0)
                return;

            List<T> removals = null;

            foreach (var value in values)
            {
                if (!shouldRemove(value))
                    continue;

                removals ??= new List<T>();
                removals.Add(value);
            }

            if (removals == null)
                return;

            foreach (var value in removals)
                set.Remove(value);
        }

        public static void UpdateDatabaseBiota(ShardDbContext context, ACE.Entity.Models.Biota sourceBiota, ACE.Database.Models.Shard.Biota targetBiota)
        {
            targetBiota.WeenieClassId = sourceBiota.WeenieClassId;
            targetBiota.WeenieType = (int)sourceBiota.WeenieType;


            if (sourceBiota.PropertiesBool != null)
            {
                foreach (var kvp in sourceBiota.PropertiesBool)
                    targetBiota.SetProperty(kvp.Key, kvp.Value);
            }
            RemoveWhere(context.BiotaPropertiesBool, targetBiota.BiotaPropertiesBool,
                value => sourceBiota.PropertiesBool == null || !sourceBiota.PropertiesBool.ContainsKey((PropertyBool)value.Type));

            if (sourceBiota.PropertiesDID != null)
            {
                foreach (var kvp in sourceBiota.PropertiesDID)
                    targetBiota.SetProperty(kvp.Key, kvp.Value);
            }
            RemoveWhere(context.BiotaPropertiesDID, targetBiota.BiotaPropertiesDID,
                value => sourceBiota.PropertiesDID == null || !sourceBiota.PropertiesDID.ContainsKey((PropertyDataId)value.Type));

            if (sourceBiota.PropertiesFloat != null)
            {
                foreach (var kvp in sourceBiota.PropertiesFloat)
                    targetBiota.SetProperty(kvp.Key, kvp.Value);
            }
            RemoveWhere(context.BiotaPropertiesFloat, targetBiota.BiotaPropertiesFloat,
                value => sourceBiota.PropertiesFloat == null || !sourceBiota.PropertiesFloat.ContainsKey((PropertyFloat)value.Type));

            if (sourceBiota.PropertiesIID != null)
            {
                foreach (var kvp in sourceBiota.PropertiesIID)
                    targetBiota.SetProperty(kvp.Key, kvp.Value);
            }
            RemoveWhere(context.BiotaPropertiesIID, targetBiota.BiotaPropertiesIID,
                value => sourceBiota.PropertiesIID == null || !sourceBiota.PropertiesIID.ContainsKey((PropertyInstanceId)value.Type));

            if (sourceBiota.PropertiesInt != null)
            {
                foreach (var kvp in sourceBiota.PropertiesInt)
                    targetBiota.SetProperty(kvp.Key, kvp.Value);
            }
            RemoveWhere(context.BiotaPropertiesInt, targetBiota.BiotaPropertiesInt,
                value => sourceBiota.PropertiesInt == null || !sourceBiota.PropertiesInt.ContainsKey((PropertyInt)value.Type));

            if (sourceBiota.PropertiesInt64 != null)
            {
                foreach (var kvp in sourceBiota.PropertiesInt64)
                    targetBiota.SetProperty(kvp.Key, kvp.Value);
            }
            RemoveWhere(context.BiotaPropertiesInt64, targetBiota.BiotaPropertiesInt64,
                value => sourceBiota.PropertiesInt64 == null || !sourceBiota.PropertiesInt64.ContainsKey((PropertyInt64)value.Type));

            if (sourceBiota.PropertiesString != null)
            {
                foreach (var kvp in sourceBiota.PropertiesString)
                    targetBiota.SetProperty(kvp.Key, kvp.Value);
            }
            RemoveWhere(context.BiotaPropertiesString, targetBiota.BiotaPropertiesString,
                value => sourceBiota.PropertiesString == null || !sourceBiota.PropertiesString.ContainsKey((PropertyString)value.Type));


            if (sourceBiota.PropertiesPosition != null)
            {
                foreach (var kvp in sourceBiota.PropertiesPosition)
                {
                    BiotaPropertiesPosition existingValue = targetBiota.BiotaPropertiesPosition.FirstOrDefault(r => r.PositionType == (ushort)kvp.Key);

                    if (existingValue == null)
                    {
                        existingValue = new BiotaPropertiesPosition { ObjectId = sourceBiota.Id };

                        targetBiota.BiotaPropertiesPosition.Add(existingValue);
                    }

                    existingValue.PositionType = (ushort)kvp.Key;
                    existingValue.ObjCellId = kvp.Value.ObjCellId;
                    existingValue.Instance = kvp.Value.Instance;
                    existingValue.OriginX = kvp.Value.PositionX;
                    existingValue.OriginY = kvp.Value.PositionY;
                    existingValue.OriginZ = kvp.Value.PositionZ;
                    existingValue.AnglesW = kvp.Value.RotationW;
                    existingValue.AnglesX = kvp.Value.RotationX;
                    existingValue.AnglesY = kvp.Value.RotationY;
                    existingValue.AnglesZ = kvp.Value.RotationZ;

                    // Entity Framework is unable to store NaN floats in the database and results in an error of:
                    // ERROR 1054: Unknown column 'NaN' in 'field list'
                    if (float.IsNaN(existingValue.AnglesX) || float.IsNaN(existingValue.AnglesY) || float.IsNaN(existingValue.AnglesZ) || float.IsNaN(existingValue.AnglesW))
                    {
                        existingValue.AnglesW = 1;
                        existingValue.AnglesX = 0;
                        existingValue.AnglesY = 0;
                        existingValue.AnglesZ = 0;
                    }
                }
            }
            RemoveWhere(context.BiotaPropertiesPosition, targetBiota.BiotaPropertiesPosition,
                value => sourceBiota.PropertiesPosition == null || !sourceBiota.PropertiesPosition.ContainsKey((PositionType)value.PositionType));


            if (sourceBiota.PropertiesSpellBook != null)
            {
                // Optimization to help characters with very large spell books and avoid full iterations inside the foreach
                var existingValues = targetBiota.BiotaPropertiesSpellBook.ToDictionary(r => r.Spell, r => r);

                foreach (var kvp in sourceBiota.PropertiesSpellBook)
                {
                    if (!existingValues.TryGetValue(kvp.Key, out var existingValue))
                    {
                        existingValue = new BiotaPropertiesSpellBook { ObjectId = sourceBiota.Id };

                        targetBiota.BiotaPropertiesSpellBook.Add(existingValue);
                    }

                    existingValue.Spell = kvp.Key;
                    existingValue.Probability = kvp.Value;
                }
            }
            RemoveWhere(context.BiotaPropertiesSpellBook, targetBiota.BiotaPropertiesSpellBook,
                value => sourceBiota.PropertiesSpellBook == null || !sourceBiota.PropertiesSpellBook.ContainsKey(value.Spell));


            if (sourceBiota.PropertiesAnimPart != null)
            {
                for (int i = 0; i < sourceBiota.PropertiesAnimPart.Count; i++)
                {
                    var value = sourceBiota.PropertiesAnimPart[i];

                    BiotaPropertiesAnimPart existingValue = targetBiota.BiotaPropertiesAnimPart.FirstOrDefault(r => r.Order == i);

                    if (existingValue == null)
                    {
                        existingValue = new BiotaPropertiesAnimPart { ObjectId = sourceBiota.Id };

                        targetBiota.BiotaPropertiesAnimPart.Add(existingValue);
                    }

                    existingValue.Index = value.Index;
                    existingValue.AnimationId = value.AnimationId;
                    existingValue.Order = (byte)i;
                }
            }
            RemoveWhere(context.BiotaPropertiesAnimPart, targetBiota.BiotaPropertiesAnimPart,
                value => sourceBiota.PropertiesAnimPart == null || value.Order == null || value.Order >= sourceBiota.PropertiesAnimPart.Count);

            if (sourceBiota.PropertiesPalette != null)
            {
                for (int i = 0; i < sourceBiota.PropertiesPalette.Count; i++)
                {
                    var value = sourceBiota.PropertiesPalette[i];

                    BiotaPropertiesPalette existingValue = targetBiota.BiotaPropertiesPalette.FirstOrDefault(r => r.Order == i);

                    if (existingValue == null)
                    {
                        existingValue = new BiotaPropertiesPalette { ObjectId = sourceBiota.Id };

                        targetBiota.BiotaPropertiesPalette.Add(existingValue);
                    }

                    existingValue.SubPaletteId = value.SubPaletteId;
                    existingValue.Offset = value.Offset;
                    existingValue.Length = value.Length;
                    existingValue.Order = (byte)i;
                }
            }
            RemoveWhere(context.BiotaPropertiesPalette, targetBiota.BiotaPropertiesPalette,
                value => sourceBiota.PropertiesPalette == null || value.Order == null || value.Order >= sourceBiota.PropertiesPalette.Count);

            if (sourceBiota.PropertiesTextureMap != null)
            {
                for (int i = 0; i < sourceBiota.PropertiesTextureMap.Count; i++)
                {
                    var value = sourceBiota.PropertiesTextureMap[i];

                    BiotaPropertiesTextureMap existingValue = targetBiota.BiotaPropertiesTextureMap.FirstOrDefault(r => r.Order == i);

                    if (existingValue == null)
                    {
                        existingValue = new BiotaPropertiesTextureMap { ObjectId = sourceBiota.Id };

                        targetBiota.BiotaPropertiesTextureMap.Add(existingValue);
                    }

                    existingValue.Index = value.PartIndex;
                    existingValue.OldId = value.OldTexture;
                    existingValue.NewId = value.NewTexture;
                    existingValue.Order = (byte)i;
                }
            }
            RemoveWhere(context.BiotaPropertiesTextureMap, targetBiota.BiotaPropertiesTextureMap,
                value => sourceBiota.PropertiesTextureMap == null || value.Order == null || value.Order >= sourceBiota.PropertiesTextureMap.Count);


            // Properties for all world objects that typically aren't modified over the original Biota

            // This is a cluster... because there is no key per record, just the record id.
            // That poses a problem because when we add a new record to be saved, we don't know what the record id is yet.
            // It's not until we try to save the record a second time that we will then have the database persisted record (with a valid id), and the entity record (that still has a DatabaseRecordId of 0)
            // We then need to match up the record that was saved with it's entity counterpart
            var processedSourceCreateList = new HashSet<ACE.Entity.Models.PropertiesCreateList>();
            var usedTargetCreateList = new HashSet<BiotaPropertiesCreateList>();
            if (sourceBiota.PropertiesCreateList != null)
            {
                // Process matched up records first
                foreach (var value in sourceBiota.PropertiesCreateList)
                {
                    if (value.DatabaseRecordId == 0)
                        continue;

                    // Source record should already exist in the target
                    BiotaPropertiesCreateList existingValue = targetBiota.BiotaPropertiesCreateList.FirstOrDefault(r => r.Id == value.DatabaseRecordId);

                    // If the existingValue was not found, the database was likely modified outside of ACE after our last save
                    if (existingValue == null)
                        continue;

                    CopyValueInto(value, existingValue);

                    processedSourceCreateList.Add(value);
                    usedTargetCreateList.Add(existingValue);
                }
                foreach (var value in sourceBiota.PropertiesCreateList)
                {
                    if (processedSourceCreateList.Contains(value))
                        continue;

                    // For simplicity, just find the first unused target
                    BiotaPropertiesCreateList existingValue = targetBiota.BiotaPropertiesCreateList.FirstOrDefault(r => !usedTargetCreateList.Contains(r));

                    if (existingValue == null)
                    {
                        existingValue = new BiotaPropertiesCreateList { ObjectId = sourceBiota.Id };

                        targetBiota.BiotaPropertiesCreateList.Add(existingValue);
                    }

                    value.DatabaseRecordId = existingValue.Id;

                    CopyValueInto(value, existingValue);

                    //processedSourceCreateList.Add(value);
                    usedTargetCreateList.Add(existingValue);
                }
            }
            RemoveWhere(context.BiotaPropertiesCreateList, targetBiota.BiotaPropertiesCreateList,
                value => !usedTargetCreateList.Contains(value));

            // This is a cluster... because there is no key per record, just the record id.
            // That poses a problem because when we add a new record to be saved, we don't know what the record id is yet.
            // It's not until we try to save the record a second time that we will then have the database persisted record (with a valid id), and the entity record (that still has a DatabaseRecordId of 0)
            // We then need to match up the record that was saved with it's entity counterpart
            var emoteMap = new Dictionary<ACE.Entity.Models.PropertiesEmote, BiotaPropertiesEmote>();
            if (sourceBiota.PropertiesEmote != null)
            {
                // Process matched up records first
                foreach (var value in sourceBiota.PropertiesEmote)
                {
                    if (value.DatabaseRecordId == 0)
                        continue;

                    // Source record should already exist in the target
                    BiotaPropertiesEmote existingValue = targetBiota.BiotaPropertiesEmote.FirstOrDefault(r => r.Id == value.DatabaseRecordId);

                    // If the existingValue was not found, the database was likely modified outside of ACE after our last save
                    if (existingValue == null)
                        continue;

                    CopyValueInto(value, existingValue);

                    emoteMap[value] = existingValue;
                }
                foreach (var value in sourceBiota.PropertiesEmote)
                {
                    if (emoteMap.Keys.Contains(value))
                        continue;

                    // For simplicity, just find the first unused target
                    BiotaPropertiesEmote existingValue = targetBiota.BiotaPropertiesEmote.FirstOrDefault(r => !emoteMap.Values.Contains(r));

                    if (existingValue == null)
                    {
                        existingValue = new BiotaPropertiesEmote { ObjectId = sourceBiota.Id };

                        targetBiota.BiotaPropertiesEmote.Add(existingValue);
                    }

                    value.DatabaseRecordId = existingValue.Id;

                    CopyValueInto(value, existingValue);

                    emoteMap[value] = existingValue;
                }
            }
            RemoveWhere(context.BiotaPropertiesEmote, targetBiota.BiotaPropertiesEmote,
                value => !emoteMap.Values.Contains(value));
            // Now process the emote actions
            foreach (var kvp in emoteMap)
            {
                for (int i = 0; i < kvp.Key.PropertiesEmoteAction.Count; i++)
                {
                    BiotaPropertiesEmoteAction existingValue = kvp.Value.BiotaPropertiesEmoteAction.FirstOrDefault(r => r.Order == i);

                    if (existingValue == null)
                    {
                        existingValue = new BiotaPropertiesEmoteAction { EmoteId = kvp.Value.Id };

                        kvp.Value.BiotaPropertiesEmoteAction.Add(existingValue);
                    }

                    CopyValueInto(kvp.Key.PropertiesEmoteAction[i], existingValue, (uint)i);
                }
                RemoveWhere(context.BiotaPropertiesEmoteAction, kvp.Value.BiotaPropertiesEmoteAction,
                    value => value.Order >= kvp.Key.PropertiesEmoteAction.Count);
            }

            if (sourceBiota.PropertiesEventFilter != null)
            {
                foreach (var value in sourceBiota.PropertiesEventFilter)
                {
                    BiotaPropertiesEventFilter existingValue = targetBiota.BiotaPropertiesEventFilter.FirstOrDefault(r => r.Event == value);

                    if (existingValue == null)
                    {
                        var entity = new BiotaPropertiesEventFilter { ObjectId = sourceBiota.Id, Event = value };

                        targetBiota.BiotaPropertiesEventFilter.Add(entity);
                    }
                }
            }
            RemoveWhere(context.BiotaPropertiesEventFilter, targetBiota.BiotaPropertiesEventFilter,
                value => sourceBiota.PropertiesEventFilter == null || !sourceBiota.PropertiesEventFilter.Any(p => p == value.Event));

            // This is a cluster... because there is no key per record, just the record id.
            // That poses a problem because when we add a new record to be saved, we don't know what the record id is yet.
            // It's not until we try to save the record a second time that we will then have the database persisted record (with a valid id), and the entity record (that still has a DatabaseRecordId of 0)
            // We then need to match up the record that was saved with it's entity counterpart
            var processedSourceGenerators = new HashSet<ACE.Entity.Models.PropertiesGenerator>();
            var usedTargetGenerators = new HashSet<BiotaPropertiesGenerator>();
            if (sourceBiota.PropertiesGenerator != null)
            {
                // Process matched up records first
                foreach (var value in sourceBiota.PropertiesGenerator)
                {
                    if (value.DatabaseRecordId == 0)
                        continue;

                    // Source record should already exist in the target
                    BiotaPropertiesGenerator existingValue = targetBiota.BiotaPropertiesGenerator.FirstOrDefault(r => r.Id == value.DatabaseRecordId);

                    // If the existingValue was not found, the database was likely modified outside of ACE after our last save
                    if (existingValue == null)
                        continue;

                    CopyValueInto(value, existingValue);

                    processedSourceGenerators.Add(value);
                    usedTargetGenerators.Add(existingValue);
                }
                foreach (var value in sourceBiota.PropertiesGenerator)
                {
                    if (processedSourceGenerators.Contains(value))
                        continue;

                    // For simplicity, just find the first unused target
                    BiotaPropertiesGenerator existingValue = targetBiota.BiotaPropertiesGenerator.FirstOrDefault(r => !usedTargetGenerators.Contains(r));

                    if (existingValue == null)
                    {
                        existingValue = new BiotaPropertiesGenerator { ObjectId = sourceBiota.Id };

                        targetBiota.BiotaPropertiesGenerator.Add(existingValue);
                    }

                    value.DatabaseRecordId = existingValue.Id;

                    CopyValueInto(value, existingValue);

                    //processedSourceGenerators.Add(value);
                    usedTargetGenerators.Add(existingValue);
                }
            }
            RemoveWhere(context.BiotaPropertiesGenerator, targetBiota.BiotaPropertiesGenerator,
                value => !usedTargetGenerators.Contains(value));


            // Properties for creatures

            if (sourceBiota.PropertiesAttribute != null)
            {
                foreach (var kvp in sourceBiota.PropertiesAttribute)
                {
                    BiotaPropertiesAttribute existingValue = targetBiota.BiotaPropertiesAttribute.FirstOrDefault(r => r.Type == (ushort)kvp.Key);

                    if (existingValue == null)
                    {
                        existingValue = new BiotaPropertiesAttribute { ObjectId = sourceBiota.Id };

                        targetBiota.BiotaPropertiesAttribute.Add(existingValue);
                    }

                    existingValue.Type = (ushort)kvp.Key;
                    existingValue.InitLevel = kvp.Value.InitLevel;
                    existingValue.LevelFromCP = kvp.Value.LevelFromCP;
                    existingValue.CPSpent = kvp.Value.CPSpent;
                }
            }
            RemoveWhere(context.BiotaPropertiesAttribute, targetBiota.BiotaPropertiesAttribute,
                value => sourceBiota.PropertiesAttribute == null || !sourceBiota.PropertiesAttribute.ContainsKey((PropertyAttribute)value.Type));

            if (sourceBiota.PropertiesAttribute2nd != null)
            {
                foreach (var kvp in sourceBiota.PropertiesAttribute2nd)
                {
                    BiotaPropertiesAttribute2nd existingValue = targetBiota.BiotaPropertiesAttribute2nd.FirstOrDefault(r => r.Type == (ushort)kvp.Key);

                    if (existingValue == null)
                    {
                        existingValue = new BiotaPropertiesAttribute2nd { ObjectId = sourceBiota.Id };

                        targetBiota.BiotaPropertiesAttribute2nd.Add(existingValue);
                    }

                    existingValue.Type = (ushort)kvp.Key;
                    existingValue.InitLevel = kvp.Value.InitLevel;
                    existingValue.LevelFromCP = kvp.Value.LevelFromCP;
                    existingValue.CPSpent = kvp.Value.CPSpent;
                    existingValue.CurrentLevel = kvp.Value.CurrentLevel;
                }

            }
            RemoveWhere(context.BiotaPropertiesAttribute2nd, targetBiota.BiotaPropertiesAttribute2nd,
                value => sourceBiota.PropertiesAttribute2nd == null || !sourceBiota.PropertiesAttribute2nd.ContainsKey((PropertyAttribute2nd)value.Type));

            if (sourceBiota.PropertiesBodyPart != null)
            {
                foreach (var kvp in sourceBiota.PropertiesBodyPart)
                {
                    BiotaPropertiesBodyPart existingValue = targetBiota.BiotaPropertiesBodyPart.FirstOrDefault(r => r.Key == (uint)kvp.Key);

                    if (existingValue == null)
                    {
                        existingValue = new BiotaPropertiesBodyPart { ObjectId = sourceBiota.Id };

                        targetBiota.BiotaPropertiesBodyPart.Add(existingValue);
                    }

                    existingValue.Key = (ushort)kvp.Key;
                    existingValue.DType = (int)kvp.Value.DType;
                    existingValue.DVal = kvp.Value.DVal;
                    existingValue.DVar = kvp.Value.DVar;
                    existingValue.BaseArmor = kvp.Value.BaseArmor;
                    existingValue.ArmorVsSlash = kvp.Value.ArmorVsSlash;
                    existingValue.ArmorVsPierce = kvp.Value.ArmorVsPierce;
                    existingValue.ArmorVsBludgeon = kvp.Value.ArmorVsBludgeon;
                    existingValue.ArmorVsCold = kvp.Value.ArmorVsCold;
                    existingValue.ArmorVsFire = kvp.Value.ArmorVsFire;
                    existingValue.ArmorVsAcid = kvp.Value.ArmorVsAcid;
                    existingValue.ArmorVsElectric = kvp.Value.ArmorVsElectric;
                    existingValue.ArmorVsNether = kvp.Value.ArmorVsNether;
                    existingValue.BH = kvp.Value.BH;
                    existingValue.HLF = kvp.Value.HLF;
                    existingValue.MLF = kvp.Value.MLF;
                    existingValue.LLF = kvp.Value.LLF;
                    existingValue.HRF = kvp.Value.HRF;
                    existingValue.MRF = kvp.Value.MRF;
                    existingValue.LRF = kvp.Value.LRF;
                    existingValue.HLB = kvp.Value.HLB;
                    existingValue.MLB = kvp.Value.MLB;
                    existingValue.LLB = kvp.Value.LLB;
                    existingValue.HRB = kvp.Value.HRB;
                    existingValue.MRB = kvp.Value.MRB;
                    existingValue.LRB = kvp.Value.LRB;
                }
            }
            RemoveWhere(context.BiotaPropertiesBodyPart, targetBiota.BiotaPropertiesBodyPart,
                value => sourceBiota.PropertiesBodyPart == null || !sourceBiota.PropertiesBodyPart.ContainsKey((CombatBodyPart)value.Key));

            if (sourceBiota.PropertiesSkill != null)
            {
                foreach (var kvp in sourceBiota.PropertiesSkill)
                {
                    BiotaPropertiesSkill existingValue = targetBiota.BiotaPropertiesSkill.FirstOrDefault(r => r.Type == (ushort)kvp.Key);

                    if (existingValue == null)
                    {
                        existingValue = new BiotaPropertiesSkill { ObjectId = sourceBiota.Id };

                        targetBiota.BiotaPropertiesSkill.Add(existingValue);
                    }

                    existingValue.Type = (ushort)kvp.Key;
                    existingValue.LevelFromPP = kvp.Value.LevelFromPP;
                    existingValue.SAC = (uint)kvp.Value.SAC;
                    existingValue.PP = kvp.Value.PP;
                    existingValue.InitLevel = kvp.Value.InitLevel;
                    existingValue.ResistanceAtLastCheck = kvp.Value.ResistanceAtLastCheck;
                    existingValue.LastUsedTime = kvp.Value.LastUsedTime;
                }
            }
            RemoveWhere(context.BiotaPropertiesSkill, targetBiota.BiotaPropertiesSkill,
                value => sourceBiota.PropertiesSkill == null || !sourceBiota.PropertiesSkill.ContainsKey((Skill)value.Type));


            // Properties for books

            if (sourceBiota.PropertiesBook != null)
            {
                if (targetBiota.BiotaPropertiesBook == null)
                    targetBiota.BiotaPropertiesBook = new BiotaPropertiesBook { ObjectId = sourceBiota.Id, };

                targetBiota.BiotaPropertiesBook.MaxNumPages = sourceBiota.PropertiesBook.MaxNumPages;
                targetBiota.BiotaPropertiesBook.MaxNumCharsPerPage = sourceBiota.PropertiesBook.MaxNumCharsPerPage;
            }
            else
            {
                if (targetBiota.BiotaPropertiesBook != null)
                    context.BiotaPropertiesBook.Remove(targetBiota.BiotaPropertiesBook);
            }

            if (sourceBiota.PropertiesBookPageData != null)
            {
                for (int i = 0; i < sourceBiota.PropertiesBookPageData.Count; i++)
                {
                    var value = sourceBiota.PropertiesBookPageData[i];

                    BiotaPropertiesBookPageData existingValue = targetBiota.BiotaPropertiesBookPageData.FirstOrDefault(r => r.PageId == i);

                    if (existingValue == null)
                    {
                        existingValue = new BiotaPropertiesBookPageData { ObjectId = sourceBiota.Id };

                        targetBiota.BiotaPropertiesBookPageData.Add(existingValue);
                    }

                    existingValue.PageId = (uint)i;
                    existingValue.AuthorId = value.AuthorId;
                    existingValue.AuthorName = value.AuthorName;
                    existingValue.AuthorAccount = value.AuthorAccount;
                    existingValue.IgnoreAuthor = value.IgnoreAuthor;
                    existingValue.PageText = value.PageText;
                }
            }
            RemoveWhere(context.BiotaPropertiesBookPageData, targetBiota.BiotaPropertiesBookPageData,
                value => sourceBiota.PropertiesBookPageData == null || value.PageId >= sourceBiota.PropertiesBookPageData.Count);


            // Biota additions over Weenie

            if (sourceBiota.PropertiesAllegiance != null)
            {
                foreach (var kvp in sourceBiota.PropertiesAllegiance)
                {
                    BiotaPropertiesAllegiance existingValue = targetBiota.BiotaPropertiesAllegiance.FirstOrDefault(r => r.CharacterId == kvp.Key);

                    if (existingValue == null)
                    {
                        existingValue = new BiotaPropertiesAllegiance { AllegianceId = sourceBiota.Id };

                        targetBiota.BiotaPropertiesAllegiance.Add(existingValue);
                    }

                    existingValue.CharacterId = kvp.Key;
                    existingValue.Banned = kvp.Value.Banned;
                    existingValue.ApprovedVassal = kvp.Value.ApprovedVassal;
                }
            }
            RemoveWhere(context.BiotaPropertiesAllegiance, targetBiota.BiotaPropertiesAllegiance,
                value => sourceBiota.PropertiesAllegiance == null || !sourceBiota.PropertiesAllegiance.ContainsKey(value.CharacterId));

            // The shard schema permits at most ONE row per (object, spell, layer). The primary key is
            // (object_Id, spell_Id, caster_Object_Id, layer_Id), but there is ALSO a narrower unique index
            // wcid_enchantmentregistry_objectId_spellId_layerId_uidx over (object_Id, spell_Id, layer_Id)
            // (Database/Base/ShardBase.sql:361, mapped at Models/Shard/ShardDbContext.cs:584). Both are
            // upstream ACE, and they disagree: the runtime registry is keyed by the wider PK, so it can hold
            // two entries for one spell at one layer as long as their casters differ, and the database cannot
            // store that. Staging both produced MySQL error 1062 "Duplicate entry '<objectId>-<spellId>-1'",
            // which failed the whole save on both attempts, set Player.BiotaSaveFailed and disconnected the
            // player with CharacterError.AccountLogin (Player_Tick.cs). Observed in prod on 2026-09-06/07/08
            // for four players, always on one of the Tinkerer's Inspiration spell ids at layer 1.
            //
            // The producer of that state is fixed at source (EnchantmentManager.AddClassAbilityDebuff now
            // allocates a free layer), so this is the persistence-layer backstop: a registry that cannot be
            // represented must cost the extra layer, never the player's session. Any entry beyond the first
            // for a given (spell, layer) is dropped with a warning.
            var stagedEnchantmentLayers = new HashSet<(int SpellId, ushort LayerId)>();
            var stagedEnchantments = new HashSet<BiotaPropertiesEnchantmentRegistry>();

            if (sourceBiota.PropertiesEnchantmentRegistry != null)
            {
                foreach (var value in sourceBiota.PropertiesEnchantmentRegistry)
                {
                    if (!stagedEnchantmentLayers.Add((value.SpellId, value.LayerId)))
                    {
                        log.Warn($"[DATABASE] Biota 0x{sourceBiota.Id:X8} holds more than one enchantment for spell {value.SpellId} at layer {value.LayerId}; the shard schema allows only one, so the entry cast by 0x{value.CasterObjectId:X8} is not being persisted.");
                        continue;
                    }

                    BiotaPropertiesEnchantmentRegistry existingValue = targetBiota.BiotaPropertiesEnchantmentRegistry.FirstOrDefault(r => r.SpellId == value.SpellId && r.LayerId == value.LayerId && r.CasterObjectId == value.CasterObjectId);

                    if (existingValue == null)
                    {
                        existingValue = new BiotaPropertiesEnchantmentRegistry { ObjectId = sourceBiota.Id };

                        targetBiota.BiotaPropertiesEnchantmentRegistry.Add(existingValue);
                    }

                    stagedEnchantments.Add(existingValue);

                    existingValue.EnchantmentCategory = value.EnchantmentCategory;
                    existingValue.SpellId = value.SpellId;
                    existingValue.LayerId = value.LayerId;
                    existingValue.HasSpellSetId = value.HasSpellSetId;
                    existingValue.SpellCategory = (ushort)value.SpellCategory;
                    existingValue.PowerLevel = value.PowerLevel;
                    existingValue.StartTime = value.StartTime;
                    existingValue.Duration = value.Duration;
                    existingValue.CasterObjectId = value.CasterObjectId;
                    existingValue.DegradeModifier = value.DegradeModifier;
                    existingValue.DegradeLimit = value.DegradeLimit;
                    existingValue.LastTimeDegraded = value.LastTimeDegraded;
                    existingValue.StatModType = (uint)value.StatModType;
                    existingValue.StatModKey = value.StatModKey;
                    existingValue.StatModValue = value.StatModValue;
                    existingValue.SpellSetId = (uint)value.SpellSetId;
                }
            }
            // Keyed on what was actually STAGED above rather than re-matching the source list, which is what
            // makes the drop above safe: a target row that no staged source entry claimed is removed even if
            // some dropped source entry would have matched it. Re-matching the source would have kept a
            // second row alive at the same (spell, layer) and put the duplicate insert straight back.
            RemoveWhere(context.BiotaPropertiesEnchantmentRegistry, targetBiota.BiotaPropertiesEnchantmentRegistry,
                value => !stagedEnchantments.Contains(value));

            if (sourceBiota.HousePermissions != null)
            {
                foreach (var kvp in sourceBiota.HousePermissions)
                {
                    HousePermission existingValue = targetBiota.HousePermission.FirstOrDefault(r => r.PlayerGuid == kvp.Key);

                    if (existingValue == null)
                    {
                        existingValue = new HousePermission { HouseId = sourceBiota.Id };

                        targetBiota.HousePermission.Add(existingValue);
                    }

                    existingValue.PlayerGuid = kvp.Key;
                    existingValue.Storage = kvp.Value;
                }
            }
            RemoveWhere(context.HousePermission, targetBiota.HousePermission,
                value => sourceBiota.HousePermissions == null || !sourceBiota.HousePermissions.ContainsKey(value.PlayerGuid));
        }

        private static void CopyValueInto(ACE.Entity.Models.PropertiesCreateList value, ACE.Database.Models.Shard.BiotaPropertiesCreateList existingValue)
        {
            existingValue.DestinationType = (sbyte)value.DestinationType;
            existingValue.WeenieClassId = value.WeenieClassId;
            existingValue.StackSize = value.StackSize;
            existingValue.Palette = value.Palette;
            existingValue.Shade = value.Shade;
            existingValue.TryToBond = value.TryToBond;
        }

        private static void CopyValueInto(ACE.Entity.Models.PropertiesEmote value, ACE.Database.Models.Shard.BiotaPropertiesEmote existingValue)
        {
            existingValue.Category = (uint)value.Category;
            existingValue.Probability = value.Probability;
            existingValue.WeenieClassId = value.WeenieClassId;
            existingValue.Style = (uint?)value.Style;
            existingValue.Substyle = (uint?)value.Substyle;
            existingValue.Quest = value.Quest;
            existingValue.VendorType = (int?)value.VendorType;
            existingValue.MinHealth = value.MinHealth;
            existingValue.MaxHealth = value.MaxHealth;
        }

        private static void CopyValueInto(ACE.Entity.Models.PropertiesEmoteAction value, ACE.Database.Models.Shard.BiotaPropertiesEmoteAction existingValue, uint order)
        {
            //existingValue.EmoteId = value.EmoteId;
            existingValue.Order = order;
            existingValue.Type = value.Type;
            existingValue.Delay = value.Delay;
            existingValue.Extent = value.Extent;
            existingValue.Motion = (uint?)value.Motion;
            existingValue.Message = value.Message;
            existingValue.TestString = value.TestString;
            existingValue.Min = value.Min;
            existingValue.Max = value.Max;
            existingValue.Min64 = value.Min64;
            existingValue.Max64 = value.Max64;
            existingValue.MinDbl = value.MinDbl;
            existingValue.MaxDbl = value.MaxDbl;
            existingValue.Stat = value.Stat;
            existingValue.Display = value.Display;
            existingValue.Amount = value.Amount;
            existingValue.Amount64 = value.Amount64;
            existingValue.HeroXP64 = value.HeroXP64;
            existingValue.Percent = value.Percent;
            existingValue.SpellId = value.SpellId;
            existingValue.WealthRating = value.WealthRating;
            existingValue.TreasureClass = value.TreasureClass;
            existingValue.TreasureType = value.TreasureType;
            existingValue.PScript = (int?)value.PScript;
            existingValue.Sound = (int?)value.Sound;
            existingValue.DestinationType = value.DestinationType;
            existingValue.WeenieClassId = value.WeenieClassId;
            existingValue.StackSize = value.StackSize;
            existingValue.Palette = value.Palette;
            existingValue.Shade = value.Shade;
            existingValue.TryToBond = value.TryToBond;
            existingValue.ObjCellId = value.ObjCellId;
            existingValue.OriginX = value.OriginX;
            existingValue.OriginY = value.OriginY;
            existingValue.OriginZ = value.OriginZ;
            existingValue.AnglesW = value.AnglesW;
            existingValue.AnglesX = value.AnglesX;
            existingValue.AnglesY = value.AnglesY;
            existingValue.AnglesZ = value.AnglesZ;
        }

        private static void CopyValueInto(ACE.Entity.Models.PropertiesGenerator value, ACE.Database.Models.Shard.BiotaPropertiesGenerator existingValue)
        {
            existingValue.Probability = value.Probability;
            existingValue.WeenieClassId = value.WeenieClassId;
            existingValue.Delay = value.Delay;
            existingValue.InitCreate = value.InitCreate;
            existingValue.MaxCreate = value.MaxCreate;
            existingValue.WhenCreate = (uint)value.WhenCreate;
            existingValue.WhereCreate = (uint)value.WhereCreate;
            existingValue.StackSize = value.StackSize;
            existingValue.PaletteId = value.PaletteId;
            existingValue.Shade = value.Shade;
            existingValue.ObjCellId = value.ObjCellId;
            existingValue.OriginX = value.OriginX;
            existingValue.OriginY = value.OriginY;
            existingValue.OriginZ = value.OriginZ;
            existingValue.AnglesW = value.AnglesW;
            existingValue.AnglesX = value.AnglesX;
            existingValue.AnglesY = value.AnglesY;
            existingValue.AnglesZ = value.AnglesZ;
        }
    }
}
