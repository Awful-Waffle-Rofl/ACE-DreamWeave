using System;
using System.Collections.Generic;
using System.Linq;

using log4net;

using ACE.Database;
using ACE.DatLoader;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Avatars;

/// <summary>
/// A creature that wears the Avatar's look: the selling NPC (<see cref="AvatarVendor"/>) and the in-fight body
/// (<see cref="Gauntlet.GauntletAvatarFighter"/>). Carries the dressed hair texture pair, which WorldObject.AddBaseModelData
/// reads the way a Player reads its Character's.
/// </summary>
internal interface IAvatarFigure
{
    (uint, uint)? HairTextureOverride { get; set; }
}

/// <summary>
/// Dresses an Avatar figure from a stored <see cref="AvatarLook"/>: sets the body, replaces what it wears with
/// cosmetic copies of the look's pieces, sets the stance and pose. A null look, or one with a null
/// Body (the reset look), puts back the default weenie's body and create_list clothing.
///
/// Two figures: the vendor (<see cref="Apply"/>) takes the whole look; the fighter (<see cref="ApplyFighter"/>) takes the
/// body, the scale and the worn (not held) pieces, keeps its own weapon and shield (scaled with the body, as a vendor's held
/// piece is), and keeps its combat stance.
///
/// A dress never leaves the Avatar half-built: a piece whose wcid is gone or which will not equip is destroyed, logged and
/// skipped; a Heritage/Gender pair that is not in CharGen keeps the default body. Every outside read (the piece factory,
/// the default weenie, CharGen, the face roll, the scale factor) goes through a seam so the whole class runs in a unit test.
/// </summary>
public static class AvatarLookApplier
{
    private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    /// <summary>The part index a Player's hair texture change targets (WorldObject_Networking.AddBaseModelData).</summary>
    internal const uint HairPartIndex = 0x10;

    /// <summary>Test seam: builds one piece from its wcid, or null when the weenie does not exist.</summary>
    internal static Func<uint, WorldObject> PieceBuilder = WorldObjectFactory.CreateNewWorldObject;

    /// <summary>Test seam: the default Avatar weenie (body properties and create_list). Null when it is not cached.</summary>
    internal static Func<Weenie> DefaultWeenie = DefaultWeenieFromDatabase;

    /// <summary>Test seam: whether a Heritage/Gender pair is in the client's CharGen table.</summary>
    internal static Func<int, int, bool> HeritageGenderValid = HeritageGenderInCharGen;

    /// <summary>
    /// Test seam: rolls a face for the default body. The constructor does this for every non-Player creature with a heritage
    /// and gender (Creature.GenerateNewFace), and clearing the body removes that face, so the default look rolls a new one.
    /// </summary>
    internal static Action<Creature> FaceGenerator = npc => npc.GenerateNewFace();

    /// <summary>Test seam: the live body-scale multiplier (allegiance_avatar_scale; 1.0 = player size).</summary>
    internal static Func<double> ScaleFactor = ScaleFactorFromPropertyManager;

    /// <summary>The largest body-scale multiplier Apply accepts; a bigger tuned value is clamped to this.</summary>
    internal const double MaxScaleFactor = 5.0;

    private static double ScaleFactorFromPropertyManager() => PropertyManager.GetDouble("allegiance_avatar_scale").Item;

    internal static void ResetSeams()
    {
        PieceBuilder = WorldObjectFactory.CreateNewWorldObject;
        DefaultWeenie = DefaultWeenieFromDatabase;
        HeritageGenderValid = HeritageGenderInCharGen;
        FaceGenerator = npc => npc.GenerateNewFace();
        ScaleFactor = ScaleFactorFromPropertyManager;
    }

    private static Weenie DefaultWeenieFromDatabase() => DatabaseManager.World.GetCachedWeenie(AvatarVendor.AvatarWcid);

    private static bool HeritageGenderInCharGen(int heritage, int gender)
    {
        var groups = DatManager.PortalDat?.CharGen?.HeritageGroups;
        return groups != null
            && groups.TryGetValue((uint)heritage, out var group)
            && group.Genders.ContainsKey(gender);
    }

    // What a worn piece must not carry: every stat a wielded item contributes. Removing a key is the same as zeroing it for
    // every reader, and leaves nothing for a later "is it > 0" check to trip on.
    private static readonly PropertyInt[] StrippedInts =
    {
        PropertyInt.Damage, PropertyInt.DamageType, PropertyInt.ArmorLevel,
        PropertyInt.ImbuedEffect, PropertyInt.ImbuedEffect2, PropertyInt.ImbuedEffect3, PropertyInt.ImbuedEffect4, PropertyInt.ImbuedEffect5,
        PropertyInt.WieldRequirements, PropertyInt.WieldSkillType, PropertyInt.WieldDifficulty,
        PropertyInt.WieldRequirements2, PropertyInt.WieldSkillType2, PropertyInt.WieldDifficulty2,
        PropertyInt.WieldRequirements3, PropertyInt.WieldSkillType3, PropertyInt.WieldDifficulty3,
        PropertyInt.WieldRequirements4, PropertyInt.WieldSkillType4, PropertyInt.WieldDifficulty4,
        PropertyInt.ItemSpellcraft, PropertyInt.ItemCurMana, PropertyInt.ItemMaxMana, PropertyInt.ItemManaCost,
        PropertyInt.ItemDifficulty, PropertyInt.ItemSkillLevelLimit,
        // the ratings Creature.AddItemToEquippedItemsRatingCache reads, so a worn piece never feeds the NPC's rating cache
        PropertyInt.GearDamage, PropertyInt.GearDamageResist, PropertyInt.GearCrit, PropertyInt.GearCritResist,
        PropertyInt.GearCritDamage, PropertyInt.GearCritDamageResist, PropertyInt.GearHealingBoost, PropertyInt.GearMaxHealth,
        PropertyInt.GearPKDamageRating, PropertyInt.GearPKDamageResistRating,
    };

    private static readonly PropertyFloat[] StrippedFloats =
    {
        PropertyFloat.DamageVariance, PropertyFloat.WeaponOffense, PropertyFloat.WeaponDefense,
        PropertyFloat.WeaponMissileDefense, PropertyFloat.WeaponMagicDefense, PropertyFloat.ManaConversionMod,
        PropertyFloat.ElementalDamageMod, PropertyFloat.SlayerDamageBonus, PropertyFloat.ProcSpellRate,
        PropertyFloat.CriticalFrequency, PropertyFloat.CriticalMultiplier, PropertyFloat.ManaRate,
        PropertyFloat.ArmorModVsSlash, PropertyFloat.ArmorModVsPierce, PropertyFloat.ArmorModVsBludgeon,
        PropertyFloat.ArmorModVsCold, PropertyFloat.ArmorModVsFire, PropertyFloat.ArmorModVsAcid,
        PropertyFloat.ArmorModVsElectric, PropertyFloat.ArmorModVsNether,
    };

    private static readonly PropertyDataId[] StrippedDataIds =
    {
        PropertyDataId.ProcSpell, PropertyDataId.Spell,
    };

    /// <summary>
    /// Dresses an NPC that is NOT yet in the world. It sets properties and equips pieces but sends nothing: the ObjDesc
    /// event carries no scale, Setup or wielded child objects, so a viewer who already has the NPC would see a half-updated
    /// figure. To re-dress a live Avatar, despawn it and spawn a fresh NPC through this method, so every viewer gets the
    /// whole figure from CreateObject. Called on an NPC that is already in a landblock it still applies, and logs a warning.
    /// </summary>
    public static void Apply(AvatarVendor npc, AvatarLook lookOrNull)
    {
        if (npc == null)
            throw new ArgumentNullException(nameof(npc));

        ApplyFigure(npc, npc, lookOrNull, keepArms: false);
    }

    /// <summary>
    /// Dresses the Avatar fighter BEFORE it enters the world, as <see cref="Apply"/> dresses the vendor, except that its own
    /// held items (the weapon and shield its weenie wields) stay on and take the scale factor, a look's held pieces are
    /// skipped (the fighter fights with its own arms), and its motion is left alone (placement sets combat mode).
    /// </summary>
    public static void ApplyFighter(Gauntlet.GauntletAvatarFighter fighter, AvatarLook lookOrNull)
    {
        if (fighter == null)
            throw new ArgumentNullException(nameof(fighter));

        ApplyFigure(fighter, fighter, lookOrNull, keepArms: true);
    }

    private static void ApplyFigure(Creature npc, IAvatarFigure figure, AvatarLook lookOrNull, bool keepArms)
    {
        if (npc.CurrentLandblock != null)
            log.Warn($"[AVATAR] {npc.Name}: dressed while in the world; existing viewers will not see the scale, Setup or held items change. Respawn the NPC instead.");

        var body = lookOrNull?.Body;
        var defaultWeenie = new Lazy<Weenie>(() => DefaultWeenie());

        ClearWorn(npc, keepArms);

        if (body == null)
            ApplyDefaultBody(npc, figure, defaultWeenie.Value);
        else if (BodyIsValid(body))
            ApplyBody(npc, figure, body);
        else
        {
            log.Warn($"[AVATAR] {npc.Name}: the look's heritage/gender pair is not in CharGen; keeping the default body.");
            ApplyDefaultBody(npc, figure, defaultWeenie.Value);
        }

        var factor = ScaleFactor();
        if (!(factor > 0) || double.IsInfinity(factor))
            factor = 1.0;
        else if (factor > MaxScaleFactor)
            factor = MaxScaleFactor;

        ApplyScale(npc, defaultWeenie, factor);

        if (keepArms)
            ScaleKeptArms(npc, factor);

        if (body == null)
            EquipDefaultPieces(npc, defaultWeenie.Value, factor, keepArms);
        else
            foreach (var piece in lookOrNull.Pieces)
            {
                if (keepArms && (piece.Slot & EquipMask.Selectable) != 0)
                    continue;

                EquipLookPiece(npc, piece, factor);
            }

        if (keepArms)
            return;

        var stance = ResolveStance(npc, body?.Stance);
        // the held pose is kept even when the stance fell back: a pose is a substate of the stance the NPC ends up in
        npc.CurrentMotionState = new Motion(stance, body?.Pose ?? MotionCommand.Ready);
    }

    /// <summary>
    /// Multiplies the body scale (the copied one, else the default weenie's, else 1.0) by the factor. Applied here and never
    /// stored in the look, so retuning the factor changes every Avatar without a re-dress.
    /// </summary>
    private static void ApplyScale(Creature npc, Lazy<Weenie> defaultWeenie, double factor)
    {
        var scale = npc.ObjScale;
        if (scale == null && defaultWeenie.Value?.PropertiesFloat != null
            && defaultWeenie.Value.PropertiesFloat.TryGetValue(PropertyFloat.DefaultScale, out var weenieScale))
            scale = (float)weenieScale;

        npc.ObjScale = (float)((scale ?? 1.0f) * factor);
    }

    private static bool BodyIsValid(AvatarLookBody body) =>
        body.Ints.TryGetValue(PropertyInt.HeritageGroup, out var heritage)
        && body.Ints.TryGetValue(PropertyInt.Gender, out var gender)
        && HeritageGenderValid(heritage, gender);

    /// <summary>
    /// Unequips and destroys everything the NPC wears. The pieces are cosmetic copies, so nothing here is worth keeping.
    /// With <paramref name="keepArms"/> (the fighter) its held items stay: they are its real weapon and shield.
    /// </summary>
    private static void ClearWorn(Creature npc, bool keepArms)
    {
        foreach (var kv in npc.EquippedObjects.ToList())
        {
            if (keepArms && IsHeld(kv.Value))
                continue;

            if (npc.TryDequipObject(kv.Key, out var item, out _))
                item.Destroy();
        }
    }

    private static bool IsHeld(WorldObject item) => ((item.CurrentWieldedLocation ?? EquipMask.None) & EquipMask.Selectable) != 0;

    /// <summary>The fighter's own held items take the factor, as a vendor's held piece does in <see cref="EquipCosmetic"/>.</summary>
    private static void ScaleKeptArms(Creature npc, double factor)
    {
        foreach (var item in npc.EquippedObjects.Values.Where(IsHeld))
            item.ObjScale = (item.ObjScale ?? 1.0f) * (float)factor;
    }

    private static void ClearBody(Creature npc, IAvatarFigure figure)
    {
        foreach (var k in AvatarLookCapture.BodyInts) npc.RemoveProperty(k);
        foreach (var k in AvatarLookCapture.BodyFloats) npc.RemoveProperty(k);
        foreach (var k in AvatarLookCapture.BodyDataIds) npc.RemoveProperty(k);
        foreach (var k in AvatarLookCapture.BodyStrings) npc.RemoveProperty(k);
        figure.HairTextureOverride = null;
    }

    private static void ApplyBody(Creature npc, IAvatarFigure figure, AvatarLookBody body)
    {
        ClearBody(npc, figure);

        foreach (var kv in body.Ints) npc.SetProperty(kv.Key, kv.Value);
        foreach (var kv in body.Floats) npc.SetProperty(kv.Key, kv.Value);
        foreach (var kv in body.DataIds) npc.SetProperty(kv.Key, kv.Value);
        foreach (var kv in body.Strings) npc.SetProperty(kv.Key, kv.Value);

        figure.HairTextureOverride = body.DefaultHairTexture.HasValue && body.HairTexture.HasValue
            ? (body.DefaultHairTexture.Value, body.HairTexture.Value)
            : null;
    }

    private static void ApplyDefaultBody(Creature npc, IAvatarFigure figure, Weenie weenie)
    {
        ClearBody(npc, figure);

        if (weenie == null)
        {
            log.Error($"[AVATAR] {npc.Name}: the default weenie {AvatarVendor.AvatarWcid} is not available; the body is left empty of look properties.");
            return;
        }

        foreach (var k in AvatarLookCapture.BodyInts)
            if (weenie.PropertiesInt != null && weenie.PropertiesInt.TryGetValue(k, out var v)) npc.SetProperty(k, v);
        foreach (var k in AvatarLookCapture.BodyFloats)
            if (weenie.PropertiesFloat != null && weenie.PropertiesFloat.TryGetValue(k, out var v)) npc.SetProperty(k, v);
        foreach (var k in AvatarLookCapture.BodyDataIds)
            if (weenie.PropertiesDID != null && weenie.PropertiesDID.TryGetValue(k, out var v)) npc.SetProperty(k, v);
        foreach (var k in AvatarLookCapture.BodyStrings)
            if (weenie.PropertiesString != null && weenie.PropertiesString.TryGetValue(k, out var v)) npc.SetProperty(k, v);

        FaceGenerator(npc);
    }

    private static void EquipDefaultPieces(Creature npc, Weenie weenie, double factor, bool keepArms)
    {
        if (weenie?.PropertiesCreateList == null)
            return;

        foreach (var row in weenie.PropertiesCreateList.Where(r => (r.DestinationType & DestinationType.Wield) != 0))
        {
            var wo = PieceBuilder(row.WeenieClassId);
            if (wo == null)
            {
                log.Warn($"[AVATAR] {npc.Name}: default piece wcid {row.WeenieClassId} does not exist; skipped.");
                continue;
            }

            if (row.Palette > 0)
                wo.PaletteTemplate = row.Palette;
            wo.Shade = row.Shade;

            var slot = (EquipMask)(wo.ValidLocations ?? 0);

            if (keepArms && (slot & EquipMask.Selectable) != 0)
            {
                wo.Destroy();
                continue;
            }

            EquipCosmetic(npc, wo, slot, factor);
        }
    }

    private static void EquipLookPiece(Creature npc, AvatarLookPiece piece, double factor)
    {
        var wo = PieceBuilder(piece.Wcid);
        if (wo == null)
        {
            log.Warn($"[AVATAR] {npc.Name}: piece wcid {piece.Wcid} does not exist; skipped.");
            return;
        }

        Overlay(wo, piece);
        EquipCosmetic(npc, wo, piece.Slot, factor);
    }

    /// <summary>Strips the piece to cosmetic and equips it; destroys it and logs when it will not go on.</summary>
    private static void EquipCosmetic(Creature npc, WorldObject wo, EquipMask slot, double factor)
    {
        StripToCosmetic(wo);

        if (slot == EquipMask.None || !npc.TryEquipObject(wo, slot))
        {
            log.Warn($"[AVATAR] {npc.Name}: piece {wo.WeenieClassId} ({wo.Name}) would not equip in slot {slot}; skipped.");
            wo.Destroy();
            return;
        }

        // A held item (weapon, shield, held, missile) is its own object with its own scale and does not inherit the body's
        // (see Creature.ApplyWieldScale), so it takes the factor, not the body scale: the factor is what made the body big.
        if ((slot & EquipMask.Selectable) != 0)
            wo.ObjScale = (wo.ObjScale ?? 1.0f) * (float)factor;
    }

    /// <summary>
    /// Writes the stored look fields onto a freshly built piece. A key the capture lists but the piece did not carry is
    /// removed, so the piece shows what the captured one showed, not what its weenie happens to default to.
    /// </summary>
    private static void Overlay(WorldObject wo, AvatarLookPiece piece)
    {
        foreach (var k in AvatarLookCapture.PieceInts)
            if (piece.Ints.TryGetValue(k, out var v)) wo.SetProperty(k, v); else wo.RemoveProperty(k);
        foreach (var k in AvatarLookCapture.PieceFloats)
            if (piece.Floats.TryGetValue(k, out var v)) wo.SetProperty(k, v); else wo.RemoveProperty(k);
        foreach (var k in AvatarLookCapture.PieceDataIds)
            if (piece.DataIds.TryGetValue(k, out var v)) wo.SetProperty(k, v); else wo.RemoveProperty(k);
        foreach (var k in AvatarLookCapture.PieceBools)
            if (piece.Bools.TryGetValue(k, out var v)) wo.SetProperty(k, v); else wo.RemoveProperty(k);
        foreach (var k in AvatarLookCapture.PieceStrings)
            if (piece.Strings.TryGetValue(k, out var v)) wo.SetProperty(k, v); else wo.RemoveProperty(k);

        // keys the capture adds beyond the lists (the weapon-quality aura filter writes VisualEffectScript)
        foreach (var kv in piece.DataIds)
            wo.SetProperty(kv.Key, kv.Value);

        var textures = piece.ObjDescRows.Where(r => r.Kind == AvatarLookRowKind.Texture)
            .Select(r => new PropertiesTextureMap { PartIndex = (byte)r.A, OldTexture = r.B, NewTexture = r.C }).ToList();
        var palettes = piece.ObjDescRows.Where(r => r.Kind == AvatarLookRowKind.Palette)
            .Select(r => new PropertiesPalette { SubPaletteId = r.A, Offset = (ushort)r.B, Length = (ushort)r.C }).ToList();

        wo.Biota.PropertiesTextureMap = textures;
        wo.Biota.PropertiesPalette = palettes;
    }

    private static void StripToCosmetic(WorldObject wo)
    {
        wo.SetProperty(PropertyBool.AvatarCosmetic, true);
        wo.SetProperty(PropertyBool.AllowGive, false);

        foreach (var k in StrippedInts) wo.RemoveProperty(k);
        foreach (var k in StrippedFloats) wo.RemoveProperty(k);
        foreach (var k in StrippedDataIds) wo.RemoveProperty(k);

        wo.Biota.PropertiesSpellBook = null;
    }

    /// <summary>
    /// The stance the Avatar ends up in: the stored one when the pieces it needs are on, else NonCombat. HandCombat and
    /// NonCombat need nothing; a caster stance needs a held item; a missile stance a missile weapon; every other combat
    /// stance a melee weapon.
    /// </summary>
    internal static MotionStance ResolveStance(Creature npc, MotionStance? stored)
    {
        if (stored == null || stored == MotionStance.Invalid || stored == MotionStance.NonCombat || stored == MotionStance.HandCombat)
            return stored == MotionStance.HandCombat ? MotionStance.HandCombat : MotionStance.NonCombat;

        EquipMask needs;
        switch (stored.Value)
        {
            case MotionStance.Magic:
                needs = EquipMask.Held;
                break;
            case MotionStance.BowCombat:
            case MotionStance.CrossbowCombat:
            case MotionStance.SlingCombat:
            case MotionStance.BowNoAmmo:
            case MotionStance.CrossBowNoAmmo:
            case MotionStance.AtlatlCombat:
            case MotionStance.ThrownWeaponCombat:
            case MotionStance.ThrownShieldCombat:
                needs = EquipMask.MissileWeapon;
                break;
            case MotionStance.SwordCombat:
            case MotionStance.SwordShieldCombat:
            case MotionStance.TwoHandedSwordCombat:
            case MotionStance.TwoHandedStaffCombat:
            case MotionStance.DualWieldCombat:
            case MotionStance.UnusedCombat:
            case MotionStance.Graze:
                needs = EquipMask.MeleeWeapon | EquipMask.TwoHanded;
                break;
            default:
                return MotionStance.NonCombat;
        }

        var has = npc.EquippedObjects.Values.Any(i => ((i.CurrentWieldedLocation ?? EquipMask.None) & needs) != 0);
        return has ? stored.Value : MotionStance.NonCombat;
    }
}
