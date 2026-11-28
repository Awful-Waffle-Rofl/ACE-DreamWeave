using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.Avatars;

/// <summary>Reads a look off a player or a bare WorldObject. No world, no database.</summary>
public static class AvatarLookCapture
{
    // The body lists are what AddBaseModelData (WorldObject_Networking.cs) reads for head, hair, skin, eyes, nose and mouth,
    // plus the model identity the dressed NPC needs. Heritage is PropertyInt.HeritageGroup (188). Pinned by a test.
    public static readonly PropertyInt[] BodyInts =
    {
        PropertyInt.HeritageGroup, PropertyInt.Gender, PropertyInt.Hairstyle,
    };

    public static readonly PropertyFloat[] BodyFloats =
    {
        PropertyFloat.DefaultScale,
    };

    public static readonly PropertyDataId[] BodyDataIds =
    {
        PropertyDataId.HeadObject, PropertyDataId.HairPalette, PropertyDataId.SkinPalette,
        PropertyDataId.EyesPalette, PropertyDataId.EyesTexture, PropertyDataId.DefaultEyesTexture,
        PropertyDataId.NoseTexture, PropertyDataId.DefaultNoseTexture,
        PropertyDataId.MouthTexture, PropertyDataId.DefaultMouthTexture,
        PropertyDataId.PaletteBase, PropertyDataId.Setup, PropertyDataId.MotionTable, PropertyDataId.SoundTable,
    };

    public static readonly PropertyString[] BodyStrings =
    {
        PropertyString.Sex, PropertyString.HeritageGroup,
    };

    // The piece lists mirror Tailoring.SetCommonProperties (Tailoring.cs) less Name/LongDesc, plus CopyWornModelProperties.
    // Duplicated rather than shared so Tailoring's behaviour cannot move. UiEffects is left out on purpose: Tailoring copies it
    // only when tailoring_intermediate_uieffects is on (default off), and it is an icon highlight, not a model look.
    public static readonly PropertyInt[] PieceInts =
    {
        PropertyInt.PaletteTemplate, PropertyInt.MaterialType,
        // armor reduction rewrites ClothingPriority (Tailoring.TailorReduceArmor), and the clash check in
        // Creature.GetEquippedItems reads it, so a reduced coat rebuilt at full coverage would block its sleeves
        PropertyInt.ClothingPriority,
    };

    public static readonly PropertyFloat[] PieceFloats =
    {
        PropertyFloat.DefaultScale, PropertyFloat.Shade, PropertyFloat.Shade2, PropertyFloat.Shade3, PropertyFloat.Shade4,
        // Perennial dyes: the shade of the second colour layer (Tailoring copies it next to Shade)
        PropertyFloat.DyeShellShade,
        PropertyFloat.Translucency,
    };

    public static readonly PropertyDataId[] PieceDataIds =
    {
        PropertyDataId.Setup, PropertyDataId.PaletteBase, PropertyDataId.ClothingBase,
        PropertyDataId.PhysicsEffectTable, PropertyDataId.SoundTable, PropertyDataId.Icon,
    };

    public static readonly PropertyBool[] PieceBools =
    {
        PropertyBool.LightsStatus, PropertyBool.IgnoreCloIcons,
        // armor layering (Tailoring.TailorLayerArmor) decides which piece's model wins a shared part
        PropertyBool.TopLayerPriority,
    };

    public static readonly PropertyString[] PieceStrings =
    {
        PropertyString.WornModelOverrides, PropertyString.ClothingPartExclusions,
    };

    private const EquipMask WieldedSlots =
        EquipMask.MeleeWeapon | EquipMask.MissileWeapon | EquipMask.Held | EquipMask.Shield | EquipMask.TwoHanded;

    public static AvatarLook FromPlayer(Player p, DateTime nowUtc)
    {
        var body = CaptureBody(p, p.Character?.DefaultHairTexture, p.Character?.HairTexture);

        var pieces = p.EquippedObjects.Values
            .Where(IsLookPiece)
            .OrderBy(i => (uint)(i.CurrentWieldedLocation ?? 0))
            .ThenBy(i => i.WeenieClassId)
            .Select(CapturePiece)
            .ToList();

        return new AvatarLook(AvatarLookJson.CurrentVersion, nowUtc, body, pieces);
    }

    public static AvatarLookBody CaptureBody(Creature source, uint? defaultHairTexture, uint? hairTexture)
    {
        // The stance is taken from the live motion state; a creature with no motion table has none, and a motion table
        // that has not been driven yet reads Invalid, which no client can show, so both mean NonCombat.
        var stance = source.CurrentMotionState?.Stance;
        if (stance == null || stance == MotionStance.Invalid)
            stance = MotionStance.NonCombat;

        return new AvatarLookBody(
            Pick(source, BodyInts, (w, k) => w.GetProperty(k)),
            Pick(source, BodyFloats, (w, k) => w.GetProperty(k)),
            Pick(source, BodyDataIds, (w, k) => w.GetProperty(k)),
            Pick(source, BodyStrings, (w, k) => w.GetProperty(k)),
            defaultHairTexture,
            hairTexture,
            stance,
            CapturePose(source));
    }

    /// <summary>
    /// The held emote pose: a forward command whose class byte is 0x41 (Crouch/Sitting/Sleeping), 0x42 (ATOYOT) or 0x43
    /// (the *State emotes), other than Ready. Walk and Run carry the SubState bit too (0x45/0x44), and the Aim motions
    /// are 0x40, so the class byte, not the bit, is what tells a pose from a movement. One-shot emotes are never captured.
    /// </summary>
    private static MotionCommand? CapturePose(Creature source)
    {
        var command = source.CurrentMotionState?.MotionState?.ForwardCommand;
        if (command == null || command == MotionCommand.Ready)
            return null;

        var cls = (uint)command.Value >> 24;
        return cls == 0x41 || cls == 0x42 || cls == 0x43 ? command : null;
    }

    public static AvatarLookPiece CapturePiece(WorldObject item)
    {
        var dataIds = Pick(item, PieceDataIds, (w, k) => w.GetProperty(k));

        // A weapon-quality aura belongs to the weapon it was earned on, not to the look (Tailoring.IntermediateScript).
        var script = Tailoring.IntermediateScript(item.VisualEffectScript);
        if (script.HasValue)
            dataIds[PropertyDataId.VisualEffectScript] = script.Value;

        var rows = new List<AvatarLookObjDescRow>();
        var textures = item.Biota.PropertiesTextureMap.Clone(item.BiotaDatabaseLock);
        if (textures != null)
            rows.AddRange(textures.Select(t => new AvatarLookObjDescRow(AvatarLookRowKind.Texture, t.PartIndex, t.OldTexture, t.NewTexture)));
        var palettes = item.Biota.PropertiesPalette.Clone(item.BiotaDatabaseLock);
        if (palettes != null)
            rows.AddRange(palettes.Select(p => new AvatarLookObjDescRow(AvatarLookRowKind.Palette, p.SubPaletteId, p.Offset, p.Length)));

        return new AvatarLookPiece(
            item.WeenieClassId,
            item.CurrentWieldedLocation ?? EquipMask.None,
            Pick(item, PieceInts, (w, k) => w.GetProperty(k)),
            Pick(item, PieceFloats, (w, k) => w.GetProperty(k)),
            dataIds,
            Pick(item, PieceBools, (w, k) => w.GetProperty(k)),
            rows,
            item.Name,
            Pick(item, PieceStrings, (w, k) => w.GetProperty(k)));
    }

    /// <summary>An equipped item that shows: anything with a ClothingBase, or a weapon, shield or held item. Ammunition is not.</summary>
    public static bool IsLookPiece(WorldObject item)
    {
        var loc = item.CurrentWieldedLocation ?? EquipMask.None;
        if (loc == EquipMask.None || loc == EquipMask.MissileAmmo)
            return false;

        return item.ClothingBase.HasValue || (loc & WieldedSlots) != 0;
    }

    /// <summary>The popup text body, for example "Dress the Avatar as a female Viamontian in: A, B and C? This replaces its current look."</summary>
    public static string Describe(AvatarLook look)
    {
        var body = look.Body;
        if (body == null)
            return "Return the Avatar to its default look? This replaces its current look.";

        body.Strings.TryGetValue(PropertyString.Sex, out var sex);
        body.Strings.TryGetValue(PropertyString.HeritageGroup, out var heritage);
        var who = string.Join(" ", new[] { sex?.ToLowerInvariant(), heritage }.Where(s => !string.IsNullOrEmpty(s)));
        who = who.Length == 0 ? "this figure" : "a " + who;
        var posed = body.Pose.HasValue;
        if (posed)
            who += ", posed " + PoseName(body.Pose.Value);

        var names = look.Pieces.Select(p => p.Name).Where(n => !string.IsNullOrEmpty(n)).ToList();
        string worn = names.Count == 0 ? "" : (posed ? ", in: " : " in: ") + (names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[names.Count - 1]);

        return $"Dress the Avatar as {who}{worn}? This replaces its current look.";
    }

    /// <summary>The pose as a player would say it: the enum name lower-cased, a trailing "State" removed.</summary>
    internal static string PoseName(MotionCommand pose)
    {
        var name = pose.ToString();
        if (name.EndsWith("State", StringComparison.Ordinal) && name.Length > 5)
            name = name.Substring(0, name.Length - 5);
        return name.ToLowerInvariant();
    }

    /// <summary>True when the two looks show the same figure (body and outfit); DressedAtUtc, Stance and Pose are ignored.</summary>
    public static bool SameFigure(AvatarLook a, AvatarLook b)
    {
        if (a == null || b == null)
            return a == b;
        return a.Version == b.Version && Equals(WithoutMotion(a.Body), WithoutMotion(b.Body)) && AvatarLookEq.Seq(a.Pieces, b.Pieces);
    }

    private static AvatarLookBody WithoutMotion(AvatarLookBody body) => body == null ? null : body with { Stance = null, Pose = null };

    private static Dictionary<TK, TV> Pick<TK, TV>(WorldObject wo, TK[] keys, Func<WorldObject, TK, TV?> read) where TV : struct
    {
        var d = new Dictionary<TK, TV>();
        foreach (var k in keys)
        {
            var v = read(wo, k);
            if (v.HasValue)
                d[k] = v.Value;
        }
        return d;
    }

    private static Dictionary<TK, string> Pick<TK>(WorldObject wo, TK[] keys, Func<WorldObject, TK, string> read)
    {
        var d = new Dictionary<TK, string>();
        foreach (var k in keys)
        {
            var v = read(wo, k);
            if (v != null)
                d[k] = v;
        }
        return d;
    }
}
