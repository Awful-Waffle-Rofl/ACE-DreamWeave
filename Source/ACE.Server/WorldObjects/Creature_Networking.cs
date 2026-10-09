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
                    // PropertyString.ClothingPartExclusions: fork content reusing a ClothingTable
                    // built for something else (an NPC-only outfit's own head/face, a baked prop)
                    // can carry parts that should never land on a player. An excluded part is
                    // skipped entirely - not added to `coverage` either - so it hands the part back
                    // to the wearer's own body/customization and to any other equipped item that
                    // also covers it, instead of this item painting it.
                    var exclusions = w.GetProperty(ACE.Entity.Enum.Properties.PropertyString.ClothingPartExclusions) is string exclusionsValue
                        ? ACE.Server.Entity.ClothingPartExclusions.Parse(exclusionsValue)
                        : null;

                    // PropertyString.WornModelOverrides: per-part MODEL swaps the item applies to its
                    // wearer (e.g. a creature's baked meshes no ClothingTable carries). Reduced to the
                    // pairs that will actually be applied: excluded parts and model ids missing from
                    // client_portal.dat are dropped (the latter warned once per wcid and id). Null for
                    // every item without the property, which then takes exactly the old path, and
                    // hasModelOverrides is false whenever nothing survives, so an item whose pairs are
                    // all excluded or invalid also looks exactly as it did before this feature.
                    var modelOverrides = w.GetProperty(ACE.Entity.Enum.Properties.PropertyString.WornModelOverrides) is string modelOverridesValue
                        ? ACE.Server.Entity.WornModelOverrides.ParseApplicable(modelOverridesValue, exclusions, w.WeenieClassId)
                        : null;
                    var hasModelOverrides = modelOverrides != null && modelOverrides.Count > 0;

                    if (!w.ClothingBase.HasValue && hasModelOverrides)
                    {
                        // No ClothingBase: the overrides ARE the item's worn look. The
                        // AddSetupAsClothingBase fallback below is deliberately NOT used - it would
                        // dress the wearer in the item's own ground Setup - and the overrides replace
                        // it outright. hasModelOverrides guarantees at least one pair is applied here.
                        // Only the item's texture_map rows for override parts are merged; its palette
                        // rows are not emitted on this path.
                        ApplyWornModelOverrides(objDesc, w, modelOverrides, exclusions, coverage, mergeTextureRowsForOverrideParts: true);
                        continue;
                    }

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

                    // WaffleACE fork: a table with only human entries still fits a wearer whose parts 0-15 are mesh-identical to a human setup.
                    // Players only: retail humanoid NPCs share the human meshes and must behave as before.
                    var pickedSetupId = ACE.Server.Entity.ClothingHumanFallback.PickSetupId(SetupTableId, thisSetupId, item.ClothingBaseEffects.ContainsKey,
                        this is Player ? (Func<uint, uint?>)(id => ACE.Server.Entity.ClothingHumanFallback.Resolve(id, item.ClothingBaseEffects.ContainsKey)) : (id => null));

                    if (pickedSetupId.HasValue)
                    // Check if the player model has data. Gear Knights, this is usually you.
                    {
                        // Add the model and texture(s). Check if the original model has AnimParts defined, otherwise use the fallback if different
                        ClothingBaseEffect clothingBaseEffect;
                        uint clothingBaseEffectSetupId = pickedSetupId.Value;
                        clothingBaseEffect = item.ClothingBaseEffects[clothingBaseEffectSetupId];

                        // Where this item's own ClothingTable texture entries begin. The item-level
                        // merge below chains only onto entries at or after this point, so a row can
                        // never splice itself onto a DIFFERENT equipped piece's swap that happens to
                        // share a part index and end at the same texture.
                        var firstTextureChange = objDesc.TextureChanges.Count;

                        foreach (CloObjectEffect t in clothingBaseEffect.CloObjectEffects)
                        {
                            byte partNum = (byte)t.Index;

                            // Excluded parts: see `exclusions` above.
                            if (exclusions != null && exclusions.Contains(partNum))
                                continue;

                            // A part the item's WornModelOverrides replaces: the table's model AND its
                            // texture effects for that part are dropped, since those swaps were
                            // authored against the table's mesh, not the override's. The override
                            // itself is applied right after this loop.
                            if (hasModelOverrides && modelOverrides.ContainsKey(partNum))
                                continue;

                            coverage.Add(partNum);

                            objDesc.AddAnimPartChange(new PropertiesAnimPart { Index = (byte)t.Index, AnimationId = t.ModelId });

                            foreach (CloTextureEffect t1 in t.CloTextureEffects)
                                objDesc.AddTextureChange(new PropertiesTextureMap { PartIndex = (byte)t.Index, OldTexture = t1.OldTexture, NewTexture = t1.NewTexture });
                        }

                        // WornModelOverrides: after the table's effects, before the naked-part fill.
                        // The item's texture_map rows for override parts are merged by the general
                        // item-row merge below, so they are not merged here.
                        if (hasModelOverrides)
                            ApplyWornModelOverrides(objDesc, w, modelOverrides, exclusions, coverage, mergeTextureRowsForOverrideParts: false);

                        // The item's own texture_map rows, read once: merged into the wearer below, and
                        // for an exclusion item also an input to which palette units it still needs.
                        var itemTextures = w.Biota.PropertiesTextureMap.Clone(w.BiotaDatabaseLock);

                        // ClothingPartExclusions, palette half: the item's ClothingTable sub-palettes (and
                        // its weenie palette rows) were authored for the WHOLE outfit, excluded parts
                        // included. A range that only colored an excluded part would otherwise recolor
                        // whatever the wearer puts there instead - a helm on an excluded head. So every
                        // palette entry this item emits is clipped to the /8 units its visible textures
                        // actually index. Null means the units could not be computed from the dats, and
                        // the item's palettes are then emitted unclipped (fail open), exactly as before.
                        // Items without exclusions never get here and are unchanged.
                        HashSet<int> neededPaletteUnits = null;
                        if (exclusions != null && exclusions.Count > 0)
                            neededPaletteUnits = ACE.Server.Entity.ClothingVisiblePaletteUnits.Get((uint)w.ClothingBase, clothingBaseEffectSetupId, clothingBaseEffect, exclusions, itemTextures, hasModelOverrides ? modelOverrides : null);

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

                                    // ClothingPartExclusions: an item that hands body parts back to the
                                    // wearer must hand their colors back too. Skin/hair/eyes live in
                                    // [0, BaseAppearancePaletteEnd) - clip any range that overlaps it
                                    // rather than dropping the whole range, so a range that legitimately
                                    // extends past the region still recolors what it covers there.
                                    if (exclusions != null && exclusions.Count > 0)
                                    {
                                        if (!ACE.Server.Entity.ClothingPartExclusions.TryClipBaseAppearance(palOffset, numColors, out palOffset, out numColors))
                                            continue;
                                    }

                                    if (neededPaletteUnits != null)
                                    {
                                        foreach (var (runOffset, runLength) in ACE.Server.Entity.ClothingPartExclusions.ClipToUnits(palOffset, numColors, neededPaletteUnits))
                                            objDesc.SubPalettes.Add(new PropertiesPalette { SubPaletteId = itemPal, Offset = runOffset, Length = runLength });
                                        continue;
                                    }

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
                        // here would desync it from the naked-part fill below. Those rows also belong
                        // to the item's OWN ground/pack model (WorldObject.CalculateObjDesc applies
                        // them there). The one sanctioned way for a worn item to swap wearer models is
                        // PropertyString.WornModelOverrides (ApplyWornModelOverrides), which adds every
                        // overridden part to `coverage` in the same step, so the two stay in sync.
                        if (itemTextures != null)
                        {
                            foreach (var row in itemTextures)
                            {
                                // Same exclusion set as the ClothingTable loop above: the item's own
                                // texture_map rows are just as capable of carrying a part that should
                                // not reach the wearer.
                                if (exclusions != null && exclusions.Contains(row.PartIndex))
                                    continue;

                                objDesc.MergeItemTextureChange(new PropertiesTextureMap { PartIndex = row.PartIndex, OldTexture = row.OldTexture, NewTexture = row.NewTexture }, firstTextureChange);
                            }
                        }

                        // Appended after this item's own ClothingTable sub-palettes, because later
                        // entries win client-side and an item-level row is the more specific
                        // statement of intent.
                        var itemPalettes = w.Biota.PropertiesPalette.Clone(w.BiotaDatabaseLock);
                        if (itemPalettes != null)
                        {
                            foreach (var row in itemPalettes)
                            {
                                var rowOffset = row.Offset;
                                var rowLength = row.Length;

                                // Same base-appearance clip as the ClothingTable sub-palette loop
                                // above: the item's own weenie palette rows are just as capable of
                                // recoloring the wearer's skin/hair/eyes.
                                if (exclusions != null && exclusions.Count > 0)
                                {
                                    if (!ACE.Server.Entity.ClothingPartExclusions.TryClipBaseAppearance(rowOffset, rowLength, out rowOffset, out rowLength))
                                        continue;
                                }

                                // Same visible-unit clip as the ClothingTable sub-palettes above.
                                if (neededPaletteUnits != null)
                                {
                                    foreach (var (runOffset, runLength) in ACE.Server.Entity.ClothingPartExclusions.ClipToUnits(rowOffset, rowLength, neededPaletteUnits))
                                        objDesc.SubPalettes.Add(new PropertiesPalette { SubPaletteId = row.SubPaletteId, Offset = runOffset, Length = runLength });
                                    continue;
                                }

                                objDesc.SubPalettes.Add(new PropertiesPalette { SubPaletteId = row.SubPaletteId, Offset = rowOffset, Length = rowLength });
                            }
                        }
                    }
                    else if (hasModelOverrides)
                    {
                        // The item has a ClothingBase, but its table has no entry for this wearer's
                        // setup, so the table dresses nothing here. The overrides still apply, the
                        // same way as for an item with no ClothingBase at all.
                        ApplyWornModelOverrides(objDesc, w, modelOverrides, exclusions, coverage, mergeTextureRowsForOverrideParts: true);
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
        /// Applies an equipped item's PropertyString.WornModelOverrides to the wearer: each pair not in
        /// the item's ClothingPartExclusions becomes an AnimPartChange and a covered part, in the same
        /// step, so the naked-part fill never paints the wearer's own mesh back over it. Draw order is
        /// the equip sort's, so a later (e.g. TopLayerPriority) item's model for the same part wins.
        ///
        /// mergeTextureRowsForOverrideParts is true on the paths where no ClothingTable effect ran for
        /// this item (no ClothingBase, or none for this setup): the item's own texture_map rows are then
        /// merged for its override parts only, since every other part is one the item does not dress.
        /// On the ClothingTable path it is false, because that path's general item-row merge already
        /// covers every non-excluded part.
        /// </summary>
        private static void ApplyWornModelOverrides(ACE.Entity.ObjDesc objDesc, WorldObject w, SortedDictionary<byte, uint> modelOverrides, HashSet<byte> exclusions, List<uint> coverage, bool mergeTextureRowsForOverrideParts)
        {
            var applied = new HashSet<byte>();

            foreach (var pair in modelOverrides)
            {
                if (exclusions != null && exclusions.Contains(pair.Key))
                    continue;

                if (!coverage.Contains(pair.Key))
                    coverage.Add(pair.Key);

                objDesc.AddAnimPartChange(new PropertiesAnimPart { Index = pair.Key, AnimationId = pair.Value });
                applied.Add(pair.Key);
            }

            if (!mergeTextureRowsForOverrideParts || applied.Count == 0)
                return;

            var firstTextureChange = objDesc.TextureChanges.Count;
            var itemTextures = w.Biota.PropertiesTextureMap.Clone(w.BiotaDatabaseLock);

            if (itemTextures == null)
                return;

            foreach (var row in itemTextures)
            {
                if (!applied.Contains(row.PartIndex))
                    continue;

                objDesc.MergeItemTextureChange(new PropertiesTextureMap { PartIndex = row.PartIndex, OldTexture = row.OldTexture, NewTexture = row.NewTexture }, firstTextureChange);
            }
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
