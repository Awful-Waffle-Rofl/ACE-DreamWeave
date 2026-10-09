using System;
using System.Collections.Generic;

using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;

namespace ACE.Server.Entity.AccountVault
{
    /// <summary>
    /// Mule Form Token: builds the in-memory weenie a summoned mule is created from when the account
    /// has earned a look (Docs/MuleVendor/2026-09-01-mule-form-token-design.md section 6).
    ///
    /// Pure by construction. The setup-height lookup is injected as a delegate rather than read from
    /// DatManager here, so every rule below is unit-testable without a dat file, and so the one place
    /// that touches the dat is the caller.
    ///
    /// WHY A CLONE AND NOT A PROPERTY WRITE. WorldObject's constructor runs InitPhysicsObj, which
    /// builds the physics object from SetupTableId and MotionTableId. A DID written after
    /// construction leaves physics on the old body: the vendor would LOOK like the donor and still
    /// collide, animate and idle like a Lugian. So the swap has to be in the Weenie handed to
    /// WorldObjectFactory.CreateNewWorldObject(Weenie), which is what this produces.
    ///
    /// WHY NOTHING HERE MAY MUTATE ITS INPUTS. Both weenies come from
    /// WorldDatabaseWithEntityCache.GetCachedWeenie, which returns the SHARED cached instance for that
    /// wcid. Writing into either one corrupts every subsequent spawn of that wcid for the process
    /// lifetime and raises nothing. Every scalar dictionary below is therefore a NEW Dictionary, built
    /// by copying, and no code path here ever indexes into the sources for a write.
    /// </summary>
    public static class MuleFormBody
    {
        /// <summary>
        /// A body will never be scaled below this. Purely a guard against a degenerate donor height
        /// producing an invisible vendor.
        /// </summary>
        public const float MinScale = 0.05f;

        /// <summary>
        /// A body will never be scaled above this. The scale formula normalises every donor to the
        /// mule's own height, so this is only reachable for a donor whose model is tiny.
        /// </summary>
        public const float MaxScale = 4.0f;

        /// <summary>
        /// The DataId properties that MOVE with the body, and the whole list of them.
        ///
        /// AN ALLOW-LIST, NEVER A COPY-ALL LOOP, and that is the load-bearing part. A "copy the donor's
        /// DIDs" loop would carry WieldedTreasureType (32) and DeathTreasureType (35) onto a vendor,
        /// which would then spawn armed and drop loot on death. The clone starts from the mule weenie,
        /// which carries neither, so the only way either can appear is if somebody replaces this list
        /// with a loop.
        ///
        /// The set itself is the fork's Serpentine Colossus body-swap rule: Setup moves together with
        /// MotionTable, SoundTable, CombatTable, Icon, PaletteBase, ClothingBase and
        /// PhysicsEffectTable, or the body animates and sounds wrong.
        /// </summary>
        private static readonly PropertyDataId[] BodyDataIds =
        {
            PropertyDataId.Setup,
            PropertyDataId.MotionTable,
            PropertyDataId.SoundTable,
            PropertyDataId.CombatTable,
            PropertyDataId.PaletteBase,
            PropertyDataId.ClothingBase,
            PropertyDataId.Icon,
            PropertyDataId.PhysicsEffectTable,
        };

        /// <summary>
        /// Builds the mule weenie wearing the donor's body, or null when the donor cannot be used - in
        /// which case the CALLER falls back to the plain mule weenie. Null is never an error the player
        /// can see: a stale or unmeasurable form must not be able to refuse a summon.
        ///
        /// <paramref name="targetHeight"/> is the height the finished body must stand at, in the same
        /// units <paramref name="setupHeight"/> returns. The caller derives it from the mule's own
        /// Setup and DefaultScale, so retuning the default mule retunes every earned form with it and
        /// nothing about the Lugian is hard-coded here.
        ///
        /// <paramref name="setupHeight"/> answers the model height for a Setup id, and must answer 0
        /// (or less) for a Setup it cannot measure.
        /// </summary>
        public static Weenie CloneWithDonorBody(Weenie mule, Weenie donor, float targetHeight, Func<uint, float> setupHeight)
        {
            if (mule == null || donor == null || setupHeight == null)
                return null;

            if (donor.PropertiesDID == null || !donor.PropertiesDID.TryGetValue(PropertyDataId.Setup, out var donorSetup))
                return null;

            if (!TryComputeScale(targetHeight, setupHeight(donorSetup), out var scale))
                return null;

            var clone = new Weenie
            {
                // Still the MULE's identity. The factory keys PersonalVendor off bool 9049 rather than
                // off the wcid, but the wcid is what every log line and every admin tool will name, and
                // this object is a mule wearing a costume, not a copy of the donor.
                WeenieClassId = mule.WeenieClassId,
                ClassName = mule.ClassName,
                WeenieType = mule.WeenieType,

                // The seven scalar dictionaries are COPIED into new instances, because the swap below
                // writes into them.
                PropertiesBool = Copy(mule.PropertiesBool),
                PropertiesDID = Copy(mule.PropertiesDID),
                PropertiesFloat = Copy(mule.PropertiesFloat),
                PropertiesIID = Copy(mule.PropertiesIID),
                PropertiesInt = Copy(mule.PropertiesInt),
                PropertiesInt64 = Copy(mule.PropertiesInt64),
                PropertiesString = Copy(mule.PropertiesString),

                // Everything below is assigned BY REFERENCE and never written to.
                //
                // Five of them are already shared this way in production: WeenieConverter.ConvertToBiota
                // reference-shares PropertiesCreateList, PropertiesEmote, PropertiesEventFilter and
                // PropertiesGenerator (WeenieConverter.cs:77-83) and PropertiesBodyPart
                // (WeenieConverter.cs:133-136) straight out of the cached weenie whenever
                // referenceWeenieCollectionsForCommonProperties is set, which WorldObject's constructor
                // always sets (WorldObject.cs:124). For those five, sharing them from a clone changes
                // nothing that was not already true of every ordinary spawn.
                //
                // The remaining seven - Position, SpellBook, Attribute, Attribute2nd, Skill, Book and
                // BookPageData - are NOT reference-shared there; ConvertToBiota deep-clones every one of
                // them regardless of that flag. They are assigned by reference HERE for a different and
                // narrower reason: this clone is read-only. Nothing in this feature writes into it, and
                // nothing writes into WorldObject.Weenie, which is the only place the clone survives.
                PropertiesPosition = mule.PropertiesPosition,
                PropertiesSpellBook = mule.PropertiesSpellBook,
                PropertiesCreateList = mule.PropertiesCreateList,
                PropertiesEmote = mule.PropertiesEmote,
                PropertiesEventFilter = mule.PropertiesEventFilter,
                PropertiesGenerator = mule.PropertiesGenerator,
                PropertiesAttribute = mule.PropertiesAttribute,
                PropertiesAttribute2nd = mule.PropertiesAttribute2nd,
                PropertiesBodyPart = mule.PropertiesBodyPart,
                PropertiesSkill = mule.PropertiesSkill,
                PropertiesBook = mule.PropertiesBook,
                PropertiesBookPageData = mule.PropertiesBookPageData,

                // The three appearance lists come from the DONOR, by reference (orchestrator ruling,
                // 2026-09-01). They are body-tied - an AnimPart index is meaningless off its own Setup,
                // and the fork's recolour doctrine makes raw palette rows the colour authority for a
                // recoloured creature - so they travel with the body or not at all. Taking the donor's
                // value even when it is null is what stops the mule's own rows leaking onto a donor
                // body; the mule weenie carries none of the three today, and this keeps that true even
                // if it ever gains some.
                //
                // These three ALIAS THE DONOR'S CACHED WEENIE for the whole life of the vendor, because
                // WorldObject keeps the weenie it was built from (WorldObject.cs:122-125). The vendor's
                // Biota does not: ConvertToBiota deep-clones all three (WeenieConverter.cs:52-71)
                // regardless of the reference-sharing flag, so the Biota copy is independent. Any future
                // code that needs to change a summoned mule's palette, texture map or anim parts must
                // therefore go through Biota and never through Weenie - a write through Weenie would
                // recolour every future spawn of the DONOR wcid, process-wide.
                PropertiesPalette = donor.PropertiesPalette,
                PropertiesTextureMap = donor.PropertiesTextureMap,
                PropertiesAnimPart = donor.PropertiesAnimPart,
            };

            // The eight body DataIds. REPLACED where the donor carries one and REMOVED where it does
            // not, never left at the mule's value: a donor with no ClothingBase must not end up wearing
            // the Lugian's.
            foreach (var did in BodyDataIds)
            {
                if (donor.PropertiesDID.TryGetValue(did, out var value))
                    clone.PropertiesDID[did] = value;
                else
                    clone.PropertiesDID.Remove(did);
            }

            CopyOrRemove(donor.PropertiesInt, clone.PropertiesInt, PropertyInt.PaletteTemplate);
            CopyOrRemove(donor.PropertiesFloat, clone.PropertiesFloat, PropertyFloat.Shade);

            // CreatureType is what the appraisal panel prints as the body's family, so it travels with
            // the body like PaletteTemplate and Shade above; a donor carrying none leaves the clone
            // with none rather than the mule's Lugian. Cosmetic on the mule either way - Attackable
            // False and Ethereal True mean nothing CreatureType gates (bane weapons, faction/AI checks)
            // is reachable on it - but a stock mule's Lugian showing through a donor's costume is a
            // player-visible tell the swap missed a property, per the 1003150 SQL row's own comment.
            CopyOrRemove(donor.PropertiesInt, clone.PropertiesInt, PropertyInt.CreatureType);

            // Every earned form stands as tall as the default mule, so nothing grows into a doorway in
            // the Marketplace and the collision cylinder scales with the body.
            clone.PropertiesFloat[PropertyFloat.DefaultScale] = scale;

            return clone;
        }

        /// <summary>
        /// The scale that makes a body of <paramref name="donorHeight"/> stand at
        /// <paramref name="targetHeight"/>, clamped to [<see cref="MinScale"/>, <see cref="MaxScale"/>].
        ///
        /// Returns false, with <paramref name="scale"/> at 0, for any height that is not strictly
        /// positive on either side. That is the whole reason this is a Try method rather than an
        /// expression: a donor whose model cannot be measured would otherwise divide by zero and hand
        /// back an infinity or a NaN, and a NaN scale reaches the client as a broken object rather than
        /// as an error anybody notices.
        /// </summary>
        public static bool TryComputeScale(float targetHeight, float donorHeight, out float scale)
        {
            scale = 0f;

            if (!(targetHeight > 0f) || !(donorHeight > 0f))
                return false;

            var raw = targetHeight / donorHeight;

            if (float.IsNaN(raw) || float.IsInfinity(raw))
                return false;

            scale = Math.Min(MaxScale, Math.Max(MinScale, raw));
            return true;
        }

        /// <summary>
        /// A NEW dictionary with the source's contents, or a new empty one when the source is null.
        /// Never the source instance itself - see the class remarks on the shared weenie cache.
        /// </summary>
        private static Dictionary<TKey, TValue> Copy<TKey, TValue>(IDictionary<TKey, TValue> source)
        {
            return source == null ? new Dictionary<TKey, TValue>() : new Dictionary<TKey, TValue>(source);
        }

        private static void CopyOrRemove<TKey, TValue>(IDictionary<TKey, TValue> donor, IDictionary<TKey, TValue> clone, TKey key)
        {
            if (donor != null && donor.TryGetValue(key, out var value))
                clone[key] = value;
            else
                clone.Remove(key);
        }
    }
}
