using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ACE.DatLoader;
using ACE.DatLoader.Entity;
using ACE.DatLoader.FileTypes;
using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Network.Structure;

namespace ACE.Server.WorldObjects
{
    partial class Creature
    {
        public void HandleActionWorldBroadcast(string message, ChatMessageType messageType)
        {
            ActionChain chain = new ActionChain();
            chain.AddAction(this, () => DoWorldBroadcast(message, messageType));
            chain.EnqueueChain();
        }

        public void DoWorldBroadcast(string message, ChatMessageType messageType)
        {
            GameMessageSystemChat sysMessage = new GameMessageSystemChat(message, messageType);

            PlayerManager.BroadcastToAll(sysMessage);
            PlayerManager.LogBroadcastChat(Channel.AllBroadcast, this, message);
        }

        /// <summary>
        /// WaffleACE fork: true when an equipped object actually COVERS the creature model, and so
        /// takes over the look from the creature's own grafted objdesc rows
        /// (Biota.PropertiesAnimPart / PropertiesPalette / PropertiesTextureMap).
        ///
        /// This is the same mask the equipment loop in CalculateObjDesc uses to decide what it will
        /// draw, so the two can never disagree. Anything else a creature holds - a weapon, a shield, a
        /// quiver - is equipped but paints nothing, and must not suppress the graft.
        ///
        /// KNOWN GAP: the graft is applied INSTEAD OF equipment, never as a base layer underneath it,
        /// so a grafted creature wearing real coverage armor still loses the graft on the parts that
        /// armor does not cover. Out of scope here.
        ///
        /// A NULL wieldedLocation returns true, which looks wrong and is deliberate: C#'s lifted !=
        /// makes `(null & mask) != 0` true, so this is exactly what the inline expression in the loop
        /// below has always evaluated to. Keeping the two identical is the point of extracting this;
        /// changing the null case would silently alter which equipped items the loop draws. Nothing in
        /// EquippedObjects reaches here with a null location anyway - TryEquipObject always sets it.
        /// </summary>
        public static bool SuppressesBiotaObjDesc(EquipMask? wieldedLocation)
        {
            return (wieldedLocation & (EquipMask.Clothing | EquipMask.Armor | EquipMask.Cloak)) != 0;
        }

        public override ACE.Entity.ObjDesc CalculateObjDesc()
        {
            ACE.Entity.ObjDesc objDesc = new ACE.Entity.ObjDesc();
            ClothingTable item;

            AddBaseModelData(objDesc);

            var coverage = new List<uint>();

            uint thisSetupId = SetupTableId;
            bool showHelm = true;
            bool showCloak = true;
            if (this is Player player)
            {
                showHelm = player.GetCharacterOption(CharacterOption.ShowYourHelmOrHeadGear);
                showCloak = player.GetCharacterOption(CharacterOption.ShowYourCloak);
            }

            // Some player races use an AlternateSetupDid, either at creation or via Barber options.
            // BUT -- those values do not correspond with entries in the Clothing Table.
            // So, we need to make some adjustments to look up something that DOES exist and is appropriate for the AlternateSetup model.
            switch (thisSetupId)
            {
                //case (uint)SetupConst.UmbraenMaleCrown:
                case (uint)SetupConst.UmbraenMaleCrownGen:
                case (uint)SetupConst.UmbraenMaleNoCrown:
                case (uint)SetupConst.UmbraenMaleVoid:
                    thisSetupId = (uint)SetupConst.UmbraenMaleCrown;
                    break;

                //case (uint)SetupConst.UmbraenFemaleCrown:
                //case (uint)SetupConst.UmbraenFemaleCrownGen:
                case (uint)SetupConst.UmbraenFemaleNoCrown:
                case (uint)SetupConst.UmbraenFemaleVoid:
                    thisSetupId = (uint)SetupConst.UmbraenFemaleCrown;
                    break;

                //case (uint)SetupConst.PenumbraenMaleCrown:
                case (uint)SetupConst.PenumbraenMaleCrownGen:
                case (uint)SetupConst.PenumbraenMaleNoCrown:
                case (uint)SetupConst.PenumbraenMaleVoid:
                    thisSetupId = (uint)SetupConst.PenumbraenMaleCrown;
                    break;

                //case (uint)SetupConst.PenumbraenFemaleCrown:
                //case (uint)SetupConst.PenumbraenFemaleCrownGen:
                case (uint)SetupConst.PenumbraenFemaleNoCrown:
                case (uint)SetupConst.PenumbraenFemaleVoid:
                    thisSetupId = (uint)SetupConst.PenumbraenFemaleCrown;
                    break;

                case (uint)SetupConst.UndeadMaleUndeadGen:
                case (uint)SetupConst.UndeadMaleSkeleton:
                case (uint)SetupConst.UndeadMaleSkeletonNoFlame:
                case (uint)SetupConst.UndeadMaleZombie:
                case (uint)SetupConst.UndeadMaleZombieNoFlame:
                    thisSetupId = (uint)SetupConst.UndeadMaleUndead;
                    break;

                case (uint)SetupConst.UndeadFemaleUndeadGen:
                case (uint)SetupConst.UndeadFemaleSkeleton:
                case (uint)SetupConst.UndeadFemaleSkeletonNoFlame:
                case (uint)SetupConst.UndeadFemaleZombie:
                case (uint)SetupConst.UndeadFemaleZombieNoFlame:
                    thisSetupId = (uint)SetupConst.UndeadFemaleUndead;
                    break;

                case (uint)SetupConst.AnakshayMale:
                    thisSetupId = (uint)SetupConst.HumanMale;
                    break;

                case (uint)SetupConst.AnakshayFemale:
                    thisSetupId = (uint)SetupConst.HumanFemale;
                    break;
            }

            // get all the Armor Items, and any Clothing items that might be equipped (robes, slippers, gloves, kasa, etc) so we can calculate their priority
            var armorItems = EquippedObjects.Values.Where(x => (x.ItemType == ItemType.Armor || (x.CurrentWieldedLocation & (EquipMask.Armor | EquipMask.Extremity)) != 0)).ToList();
            foreach (var w in armorItems)
                w.setVisualClothingPriority();

            // sort the armor into the proper order... TopLayerPriority first, then no priority, then TopLayerPriority=false.
            // Secondary sort field is the calculated "VisualClothingPriority"
            var top = armorItems.Where(x => x.TopLayerPriority == true).OrderBy(x => x.VisualClothingPriority);
            var noLayer = armorItems.Where(x => x.TopLayerPriority == null).OrderBy(x => x.VisualClothingPriority);
            var bottom = armorItems.Where(x => x.TopLayerPriority == false).OrderBy(x => x.VisualClothingPriority);
            var sortedArmorItems = bottom.Concat(noLayer).Concat(top).ToList();

            var clothesAndCloaks = EquippedObjects.Values
                                .Where(x => (x.ItemType == ItemType.Clothing) && (x.CurrentWieldedLocation & (EquipMask.Armor | EquipMask.Extremity)) == 0) // Extremity, Head/Foot/Hands, is included in the ArmorItems above
                                .OrderBy(x => x.ClothingPriority);

            var eo = clothesAndCloaks.Concat(sortedArmorItems).ToList();

            // WaffleACE fork: decide "nothing is covering the model" from what actually PAINTS the
            // model, not from what happens to be equipped. `eo` above pulls in anything with
            // ItemType.Armor, and a SHIELD is ItemType.Armor - so Legate Vessaryn (wcid 1002623)
            // holding a Kite Shield made this list non-empty, skipped the graft branch, and then lost
            // his whole Diforsa armor graft to the naked-part fill at the end of this method, because
            // the equipment loop below ignores a shield anyway (its wield location is not in
            // Clothing|Armor|Cloak, so it contributes nothing to `coverage`).
            var covering = eo.Where(w => SuppressesBiotaObjDesc(w.CurrentWieldedLocation)).ToList();

            if (covering.Count == 0)
            {
                // Check if there is any defined ObjDesc in the Biota and, if so, apply them
                if (Biota.PropertiesAnimPart.GetCount(BiotaDatabaseLock) > 0 || Biota.PropertiesPalette.GetCount(BiotaDatabaseLock) > 0 || Biota.PropertiesTextureMap.GetCount(BiotaDatabaseLock) > 0)
                {
                    Biota.PropertiesAnimPart.CopyTo(objDesc.AnimPartChanges, BiotaDatabaseLock);

                    Biota.PropertiesPalette.CopyTo(objDesc.SubPalettes, BiotaDatabaseLock);

                    Biota.PropertiesTextureMap.CopyTo(objDesc.TextureChanges, BiotaDatabaseLock);

                    return objDesc;
                }
            }

            foreach (var w in eo)
            {
                if ((w.CurrentWieldedLocation == EquipMask.HeadWear) && !showHelm && (this is Player))
                    continue;

                if ((w.CurrentWieldedLocation == EquipMask.Cloak) && !showCloak && (this is Player))
                    continue;

                // We can wield things that are not part of our model, only use those items that can cover our model.
                if (SuppressesBiotaObjDesc(w.CurrentWieldedLocation))
                {
                    if (w.ClothingBase.HasValue)
                        item = DatManager.PortalDat.ReadFromDat<ClothingTable>((uint)w.ClothingBase);
                    else
                    {
                        objDesc = AddSetupAsClothingBase(objDesc, w);
                        // Add any potentially added parts back into the coverage list
                        foreach(var a in objDesc.AnimPartChanges)
                            if (!coverage.Contains(a.Index))
                                coverage.Add(a.Index);
                        continue;
                    }

                    if (item.ClothingBaseEffects.ContainsKey(SetupTableId) || item.ClothingBaseEffects.ContainsKey(thisSetupId))
                    // Check if the player model has data. Gear Knights, this is usually you.
                    {
                        // Add the model and texture(s). Check if the original model has AnimParts defined, otherwise use the fallback if different
                        ClothingBaseEffect clothingBaseEffect;
                        if (item.ClothingBaseEffects.ContainsKey(SetupTableId))
                            clothingBaseEffect = item.ClothingBaseEffects[SetupTableId];
                        else
                            clothingBaseEffect = item.ClothingBaseEffects[thisSetupId];

                        // Where this item's own ClothingTable texture entries begin. The item-level
                        // merge below chains only onto entries at or after this point, so a row can
                        // never splice itself onto a DIFFERENT equipped piece's swap that happens to
                        // share a part index and end at the same texture.
                        var firstTextureChange = objDesc.TextureChanges.Count;

                        foreach (CloObjectEffect t in clothingBaseEffect.CloObjectEffects)
                        {
                            byte partNum = (byte)t.Index;
                            coverage.Add(partNum);

                            objDesc.AddAnimPartChange(new PropertiesAnimPart { Index = (byte)t.Index, AnimationId = t.ModelId });

                            foreach (CloTextureEffect t1 in t.CloTextureEffects)
                                objDesc.AddTextureChange(new PropertiesTextureMap { PartIndex = (byte)t.Index, OldTexture = t1.OldTexture, NewTexture = t1.NewTexture });
                        }

                        if (item.ClothingSubPalEffects.Count > 0)
                        {
                            int size = item.ClothingSubPalEffects.Count;
                            int palCount = size;

                            CloSubPalEffect itemSubPal;
                            int palOption = 0;
                            if (w.PaletteTemplate.HasValue)
                                palOption = (int)w.PaletteTemplate;
                            if (item.ClothingSubPalEffects.ContainsKey((uint)palOption))
                            {
                                itemSubPal = item.ClothingSubPalEffects[(uint)palOption];
                            }
                            else
                            {
                                itemSubPal = item.ClothingSubPalEffects[item.ClothingSubPalEffects.Keys.ElementAt(0)];
                            }

                            float shade = 0;
                            if (w.Shade.HasValue)
                                shade = (float)w.Shade;
                            for (int i = 0; i < itemSubPal.CloSubPalettes.Count; i++)
                            {
                                var itemPalSet = DatManager.PortalDat.ReadFromDat<PaletteSet>(itemSubPal.CloSubPalettes[i].PaletteSet);
                                ushort itemPal = (ushort)itemPalSet.GetPaletteID(shade);

                                for (int j = 0; j < itemSubPal.CloSubPalettes[i].Ranges.Count; j++)
                                {
                                    ushort palOffset = (ushort)(itemSubPal.CloSubPalettes[i].Ranges[j].Offset / 8);
                                    ushort numColors = (ushort)(itemSubPal.CloSubPalettes[i].Ranges[j].NumColors / 8);
                                    objDesc.SubPalettes.Add(new PropertiesPalette { SubPaletteId = itemPal, Offset = palOffset, Length = numColors });
                                }
                            }
                        }

                        // Merge the equipped item's OWN texture_map / palette rows into the wearer.
                        //
                        // Why this exists: retail composed every worn look purely from the item's
                        // ClothingTable, which is all the loop above reads. Fork content instead
                        // authors some looks as texture_map rows directly on the item (a recolour no
                        // ClothingTable entry covers). Those rows already render when the item is its
                        // own object - WorldObject.CalculateObjDesc applies them - so the piece looks
                        // right on the ground and in the pack, and then silently loses its look the
                        // moment it is worn. That asymmetry is the bug; this closes it.
                        //
                        // Deliberately INSIDE the ClothingBaseEffects branch: if this item's clothing
                        // table has no entry for the wearer's model it is dressing nothing here, and
                        // its raw rows would be swaps against a body the item never covers.
                        //
                        // Rows are read under the item's biota lock and copied, never handed to the
                        // ObjDesc by reference. AnimPart rows are deliberately NOT merged: model swaps
                        // on a wearer are what the coverage list above tracks, and injecting parts
                        // here would desync it from the naked-part fill below.
                        var itemTextures = w.Biota.PropertiesTextureMap.Clone(w.BiotaDatabaseLock);
                        if (itemTextures != null)
                        {
                            foreach (var row in itemTextures)
                                objDesc.MergeItemTextureChange(new PropertiesTextureMap { PartIndex = row.PartIndex, OldTexture = row.OldTexture, NewTexture = row.NewTexture }, firstTextureChange);
                        }

                        // Appended after this item's own ClothingTable sub-palettes, because later
                        // entries win client-side and an item-level row is the more specific
                        // statement of intent.
                        var itemPalettes = w.Biota.PropertiesPalette.Clone(w.BiotaDatabaseLock);
                        if (itemPalettes != null)
                        {
                            foreach (var row in itemPalettes)
                                objDesc.SubPalettes.Add(new PropertiesPalette { SubPaletteId = row.SubPaletteId, Offset = row.Offset, Length = row.Length });
                        }
                    }
                }
            }

            // Add the "naked" body parts. These are the ones not already covered.
            // Note that this is the original SetupTableId, not thisSetupId.
            if (SetupTableId > 0)
            {
                var baseSetup = DatManager.PortalDat.ReadFromDat<SetupModel>(SetupTableId);
                for (byte i = 0; i < baseSetup.Parts.Count; i++)
                {
                    if (!coverage.Contains(i) && i != 0x10) // Don't add body parts for those that are already covered. Also don't add the head, that was already covered by AddCharacterBaseModelData()
                        objDesc.AnimPartChanges.Add(new PropertiesAnimPart { Index = i, AnimationId = baseSetup.Parts[i] });
                    //AddModel(i, baseSetup.Parts[i]);
                }
            }

            if (coverage.Count == 0 && ClothingBase.HasValue)
                return base.CalculateObjDesc();

            return objDesc;
        }

        /// <summary>
        /// Certain items do not contain a ClothingBase. Ursuin Guise, WCID 32155 is one of them. This function will use the Setup of the weenie as a pseudo-ClothingBase.
        /// </summary>
        protected ACE.Entity.ObjDesc AddSetupAsClothingBase(ACE.Entity.ObjDesc objDesc, WorldObject wo)
        {
            // Loop over the parts in the Setup of the WorldObject
            for (var i = 0; i < wo.CSetup.Parts.Count; i++)
            {
                if(wo.CSetup.Parts[i] != 0x010001EC || i != 16) // This is essentially a "null" part, so do not add it for the head
                    objDesc.AnimPartChanges.Add(new PropertiesAnimPart { Index = (byte)i, AnimationId = wo.CSetup.Parts[i] });
            }

            return objDesc;
        }


        protected static void WriteIdentifyObjectCreatureProfile(BinaryWriter writer, Creature creature, bool success)
        {
            var creatureProfile = new CreatureProfile(creature, success);
            writer.Write(creatureProfile);
        }
    }
}
