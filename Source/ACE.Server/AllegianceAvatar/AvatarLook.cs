using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using log4net;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;

namespace ACE.Server.Avatars;

/// <summary>
/// One look for the Allegiance Avatar NPC: a body (head, hair, skin, eyes, stance...) and the worn pieces. Body is null
/// for the reset look (a stored look that means "back to the default"). Records holding Dictionaries do not get content
/// equality from <c>record</c>, so each one implements Equals by content. Stored as JSON in AvatarState.LookJson.
/// </summary>
public sealed record AvatarLook(int Version, DateTime DressedAtUtc, AvatarLookBody Body, IReadOnlyList<AvatarLookPiece> Pieces)
{
    public bool Equals(AvatarLook other) =>
        other != null
        && Version == other.Version
        && DressedAtUtc == other.DressedAtUtc
        && Equals(Body, other.Body)
        && AvatarLookEq.Seq(Pieces, other.Pieces);

    public override int GetHashCode() => HashCode.Combine(Version, DressedAtUtc, Body);
}

/// <summary>
/// The head and body model data AddBaseModelData reads, plus the hair texture pair a Player keeps on its Character, the
/// stance, and the held emote Pose (a SubState command such as ATOYOT; null when none). Pose is last and optional so a look
/// stored before it existed still parses.
/// </summary>
public sealed record AvatarLookBody(
    Dictionary<PropertyInt, int> Ints,
    Dictionary<PropertyFloat, double> Floats,
    Dictionary<PropertyDataId, uint> DataIds,
    Dictionary<PropertyString, string> Strings,
    uint? DefaultHairTexture,
    uint? HairTexture,
    MotionStance? Stance,
    MotionCommand? Pose = null)
{
    public bool Equals(AvatarLookBody other) =>
        other != null
        && AvatarLookEq.Dict(Ints, other.Ints)
        && AvatarLookEq.Dict(Floats, other.Floats)
        && AvatarLookEq.Dict(DataIds, other.DataIds)
        && AvatarLookEq.Dict(Strings, other.Strings)
        && DefaultHairTexture == other.DefaultHairTexture
        && HairTexture == other.HairTexture
        && Stance == other.Stance
        && Pose == other.Pose;

    public override int GetHashCode() => HashCode.Combine(DefaultHairTexture, HairTexture, Stance, Pose, Ints?.Count, DataIds?.Count);
}

/// <summary>
/// One worn piece. The look fields are what Tailoring.SetCommonProperties copies (less Name/LongDesc) plus the worn-model
/// strings; Name is kept for display only (AvatarLookCapture.Describe), it is not part of what is copied onto an NPC.
/// </summary>
public sealed record AvatarLookPiece(
    uint Wcid,
    EquipMask Slot,
    Dictionary<PropertyInt, int> Ints,
    Dictionary<PropertyFloat, double> Floats,
    Dictionary<PropertyDataId, uint> DataIds,
    Dictionary<PropertyBool, bool> Bools,
    List<AvatarLookObjDescRow> ObjDescRows,
    string Name,
    Dictionary<PropertyString, string> Strings)
{
    public bool Equals(AvatarLookPiece other) =>
        other != null
        && Wcid == other.Wcid
        && Slot == other.Slot
        && AvatarLookEq.Dict(Ints, other.Ints)
        && AvatarLookEq.Dict(Floats, other.Floats)
        && AvatarLookEq.Dict(DataIds, other.DataIds)
        && AvatarLookEq.Dict(Bools, other.Bools)
        && AvatarLookEq.Seq(ObjDescRows, other.ObjDescRows)
        && Name == other.Name
        && AvatarLookEq.Dict(Strings, other.Strings);

    public override int GetHashCode() => HashCode.Combine(Wcid, Slot, Name);
}

public enum AvatarLookRowKind
{
    /// <summary>A PropertiesTextureMap row: A = PartIndex, B = OldTexture, C = NewTexture.</summary>
    Texture = 0,

    /// <summary>A PropertiesPalette row: A = SubPaletteId, B = Offset, C = Length.</summary>
    Palette = 1,
}

/// <summary>One instance-level texture-map or palette row, the two kinds Tailoring.CopyObjDescRows copies.</summary>
public sealed record AvatarLookObjDescRow(AvatarLookRowKind Kind, uint A, uint B, uint C);

internal static class AvatarLookEq
{
    public static bool Dict<TK, TV>(Dictionary<TK, TV> a, Dictionary<TK, TV> b)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (a == null || b == null || a.Count != b.Count)
            return false;
        foreach (var kv in a)
            if (!b.TryGetValue(kv.Key, out var v) || !EqualityComparer<TV>.Default.Equals(kv.Value, v))
                return false;
        return true;
    }

    public static bool Seq<T>(IReadOnlyList<T> a, IReadOnlyList<T> b)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (a == null || b == null)
            return false;
        return a.SequenceEqual(b);
    }
}

/// <summary>Writes a Dictionary with an enum key using the numeric id, so a renamed enum member never breaks a stored look.</summary>
internal sealed class NumericEnumKeyDictionaryConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type t) =>
        t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Dictionary<,>) && t.GetGenericArguments()[0].IsEnum;

    public override JsonConverter CreateConverter(Type t, JsonSerializerOptions options)
    {
        var args = t.GetGenericArguments();
        return (JsonConverter)Activator.CreateInstance(typeof(Conv<,>).MakeGenericType(args[0], args[1]));
    }

    private sealed class Conv<TK, TV> : JsonConverter<Dictionary<TK, TV>> where TK : struct, Enum
    {
        public override Dictionary<TK, TV> Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException("expected an object");

            var result = new Dictionary<TK, TV>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName || !long.TryParse(reader.GetString(), out var id))
                    throw new JsonException("expected a numeric key");
                reader.Read();
                result[(TK)Enum.ToObject(typeof(TK), id)] = JsonSerializer.Deserialize<TV>(ref reader, options);
            }
            return result;
        }

        public override void Write(Utf8JsonWriter writer, Dictionary<TK, TV> value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            foreach (var kv in value.OrderBy(k => Convert.ToInt64(k.Key)))
            {
                writer.WritePropertyName(Convert.ToInt64(kv.Key).ToString());
                JsonSerializer.Serialize(writer, kv.Value, options);
            }
            writer.WriteEndObject();
        }
    }
}

public static class AvatarLookJson
{
    private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    public const int CurrentVersion = 1;

    /// <summary>The longest stored look, in UTF-8 bytes. A longer string parses as null.</summary>
    public const int MaxBytes = 16384;

    private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
    {
        Converters = { new NumericEnumKeyDictionaryConverterFactory() },
    };

    private static bool IsComplete(AvatarLook look)
    {
        var b = look.Body;
        if (b != null && (b.Ints == null || b.Floats == null || b.DataIds == null || b.Strings == null))
            return false;

        foreach (var p in look.Pieces)
            if (p == null || p.Ints == null || p.Floats == null || p.DataIds == null || p.Bools == null
                || p.Strings == null || p.ObjDescRows == null || p.ObjDescRows.Any(r => r == null))
                return false;

        return true;
    }

    public static string Serialize(AvatarLook look) => JsonSerializer.Serialize(look, Options);

    /// <summary>Null on null/empty, corrupt JSON, an unknown version, or more than MaxBytes (the last three log a warning).</summary>
    public static AvatarLook Parse(string json)
    {
        if (string.IsNullOrEmpty(json))
            return null;

        if (Encoding.UTF8.GetByteCount(json) > MaxBytes)
        {
            log.Warn($"AvatarLookJson: look is larger than {MaxBytes} bytes, ignoring it");
            return null;
        }

        try
        {
            var look = JsonSerializer.Deserialize<AvatarLook>(json, Options);
            if (look == null || look.Pieces == null)
            {
                log.Warn("AvatarLookJson: look has no piece list, ignoring it");
                return null;
            }

            if (!IsComplete(look))
            {
                log.Warn("AvatarLookJson: look is structurally incomplete, ignoring it");
                return null;
            }

            if (look.Version != CurrentVersion)
            {
                log.Warn($"AvatarLookJson: unknown look version {look.Version}, ignoring it");
                return null;
            }

            return look;
        }
        catch (Exception ex) when (ex is JsonException || ex is NotSupportedException || ex is InvalidOperationException)
        {
            log.Warn($"AvatarLookJson: corrupt look JSON, ignoring it: {ex.Message}");
            return null;
        }
    }
}
