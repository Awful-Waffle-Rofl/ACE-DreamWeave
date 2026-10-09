using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using ACE.Entity.Models;

namespace ACE.Database.Adapter
{
    /// <summary>
    /// Deep equality for two runtime <see cref="Weenie"/> instances, INCLUDING enumeration order, reporting where the
    /// first difference is. The bulk weenie loader's fidelity test and its publish-time self-check both use it to
    /// prove a bulk-loaded weenie is indistinguishable from a legacy per-id read.
    /// <para />
    /// The 25 members of Weenie (3 scalars and 22 collections) are walked by hand, one line each, so the member list is reviewable and a missing
    /// one is visible (ComparedMembers is held equal to Weenie's properties by a test). The leaf records inside the
    /// collections (PropertiesPosition, PropertiesBodyPart, PropertiesEmoteAction, ...) are compared property by
    /// property through reflection, so a field added to one of them later is compared without anyone editing this.
    /// Order matters everywhere, dictionaries included: server code enumerates these dictionaries (for example the
    /// create list, emotes and generator profiles) and would behave differently on a different order.
    /// </summary>
    public static class WeenieFidelity
    {
        /// <summary>Every Weenie member <see cref="FirstDifference"/> compares.</summary>
        public static readonly IReadOnlyList<string> ComparedMembers = new[]
        {
            nameof(Weenie.WeenieClassId), nameof(Weenie.ClassName), nameof(Weenie.WeenieType),
            nameof(Weenie.PropertiesBool), nameof(Weenie.PropertiesDID), nameof(Weenie.PropertiesFloat),
            nameof(Weenie.PropertiesIID), nameof(Weenie.PropertiesInt), nameof(Weenie.PropertiesInt64),
            nameof(Weenie.PropertiesString), nameof(Weenie.PropertiesPosition), nameof(Weenie.PropertiesSpellBook),
            nameof(Weenie.PropertiesAnimPart), nameof(Weenie.PropertiesPalette), nameof(Weenie.PropertiesTextureMap),
            nameof(Weenie.PropertiesCreateList), nameof(Weenie.PropertiesEmote), nameof(Weenie.PropertiesEventFilter),
            nameof(Weenie.PropertiesGenerator), nameof(Weenie.PropertiesAttribute), nameof(Weenie.PropertiesAttribute2nd),
            nameof(Weenie.PropertiesBodyPart), nameof(Weenie.PropertiesSkill), nameof(Weenie.PropertiesBook),
            nameof(Weenie.PropertiesBookPageData),
        };

        /// <summary>
        /// Null when <paramref name="a"/> and <paramref name="b"/> are equal, including order; otherwise the path of
        /// the first differing member with both values, e.g. "PropertiesInt[1]: key Level vs Value".
        /// </summary>
        public static string FirstDifference(Weenie a, Weenie b)
        {
            if (a == null || b == null)
                return a == null && b == null ? null : $"weenie: {(a == null ? "null" : "set")} vs {(b == null ? "null" : "set")}";

            return Scalar(nameof(a.WeenieClassId), a.WeenieClassId, b.WeenieClassId)
                ?? Scalar(nameof(a.ClassName), a.ClassName, b.ClassName)
                ?? Scalar(nameof(a.WeenieType), a.WeenieType, b.WeenieType)
                ?? Dictionary(nameof(a.PropertiesBool), a.PropertiesBool, b.PropertiesBool)
                ?? Dictionary(nameof(a.PropertiesDID), a.PropertiesDID, b.PropertiesDID)
                ?? Dictionary(nameof(a.PropertiesFloat), a.PropertiesFloat, b.PropertiesFloat)
                ?? Dictionary(nameof(a.PropertiesIID), a.PropertiesIID, b.PropertiesIID)
                ?? Dictionary(nameof(a.PropertiesInt), a.PropertiesInt, b.PropertiesInt)
                ?? Dictionary(nameof(a.PropertiesInt64), a.PropertiesInt64, b.PropertiesInt64)
                ?? Dictionary(nameof(a.PropertiesString), a.PropertiesString, b.PropertiesString)
                ?? Dictionary(nameof(a.PropertiesPosition), a.PropertiesPosition, b.PropertiesPosition)
                ?? Dictionary(nameof(a.PropertiesSpellBook), a.PropertiesSpellBook, b.PropertiesSpellBook)
                ?? Sequence(nameof(a.PropertiesAnimPart), a.PropertiesAnimPart, b.PropertiesAnimPart)
                ?? Sequence(nameof(a.PropertiesPalette), a.PropertiesPalette, b.PropertiesPalette)
                ?? Sequence(nameof(a.PropertiesTextureMap), a.PropertiesTextureMap, b.PropertiesTextureMap)
                ?? Sequence(nameof(a.PropertiesCreateList), a.PropertiesCreateList, b.PropertiesCreateList)
                ?? Emotes(nameof(a.PropertiesEmote), a.PropertiesEmote, b.PropertiesEmote)
                ?? Sequence(nameof(a.PropertiesEventFilter), a.PropertiesEventFilter, b.PropertiesEventFilter)
                ?? Sequence(nameof(a.PropertiesGenerator), a.PropertiesGenerator, b.PropertiesGenerator)
                ?? Dictionary(nameof(a.PropertiesAttribute), a.PropertiesAttribute, b.PropertiesAttribute)
                ?? Dictionary(nameof(a.PropertiesAttribute2nd), a.PropertiesAttribute2nd, b.PropertiesAttribute2nd)
                ?? Dictionary(nameof(a.PropertiesBodyPart), a.PropertiesBodyPart, b.PropertiesBodyPart)
                ?? Dictionary(nameof(a.PropertiesSkill), a.PropertiesSkill, b.PropertiesSkill)
                ?? Leaf(nameof(a.PropertiesBook), a.PropertiesBook, b.PropertiesBook)
                ?? Sequence(nameof(a.PropertiesBookPageData), a.PropertiesBookPageData, b.PropertiesBookPageData);
        }

        private static string Scalar<T>(string path, T a, T b)
        {
            return EqualityComparer<T>.Default.Equals(a, b) ? null : $"{path}: {Show(a)} vs {Show(b)}";
        }

        private static string Presence(string path, object a, object b)
        {
            if (a == null && b == null)
                return null;

            if (a == null || b == null)
                return $"{path}: {(a == null ? "null" : "set")} vs {(b == null ? "null" : "set")}";

            return string.Empty; // both set: the caller compares contents
        }

        private static string Dictionary<TKey, TValue>(string path, IEnumerable<KeyValuePair<TKey, TValue>> a, IEnumerable<KeyValuePair<TKey, TValue>> b)
        {
            var presence = Presence(path, a, b);

            if (presence != string.Empty)
                return presence;

            var listA = a.ToList();
            var listB = b.ToList();

            if (listA.Count != listB.Count)
                return $"{path}.Count: {listA.Count} vs {listB.Count}";

            for (var i = 0; i < listA.Count; i++)
            {
                if (!EqualityComparer<TKey>.Default.Equals(listA[i].Key, listB[i].Key))
                    return $"{path}[{i}]: key {Show(listA[i].Key)} vs {Show(listB[i].Key)}";

                var valueDiff = Leaf($"{path}[{Show(listA[i].Key)}]", listA[i].Value, listB[i].Value);

                if (valueDiff != null)
                    return valueDiff;
            }

            return null;
        }

        private static string Sequence<T>(string path, IEnumerable<T> a, IEnumerable<T> b)
        {
            var presence = Presence(path, a, b);

            if (presence != string.Empty)
                return presence;

            var listA = a.ToList();
            var listB = b.ToList();

            if (listA.Count != listB.Count)
                return $"{path}.Count: {listA.Count} vs {listB.Count}";

            for (var i = 0; i < listA.Count; i++)
            {
                var diff = Leaf($"{path}[{i}]", listA[i], listB[i]);

                if (diff != null)
                    return diff;
            }

            return null;
        }

        private static string Emotes(string path, IEnumerable<PropertiesEmote> a, IEnumerable<PropertiesEmote> b)
        {
            var diff = Sequence(path, a, b);

            if (diff != null || a == null)
                return diff;

            var listA = a.ToList();
            var listB = b.ToList();

            for (var i = 0; i < listA.Count; i++)
            {
                diff = Sequence($"{path}[{i}].{nameof(PropertiesEmote.PropertiesEmoteAction)}", listA[i].PropertiesEmoteAction, listB[i].PropertiesEmoteAction);

                if (diff != null)
                    return diff;
            }

            return null;
        }

        private static readonly ConcurrentDictionary<Type, PropertyInfo[]> leafProperties = new ConcurrentDictionary<Type, PropertyInfo[]>();

        /// <summary>
        /// Compares a leaf value. Primitives, enums, strings and their nullables compare by value; any other object
        /// compares by each of its public readable properties of those kinds. Collection and reference-typed
        /// properties (PropertiesEmote.PropertiesEmoteAction, PropertiesEmote.Object) are skipped here: the emote
        /// actions are compared by <see cref="Emotes"/>, and Object is a back-reference.
        /// </summary>
        private static string Leaf(string path, object a, object b)
        {
            if (a == null || b == null)
                return a == null && b == null ? null : $"{path}: {Show(a)} vs {Show(b)}";

            var type = a.GetType();

            if (type != b.GetType())
                return $"{path}: type {type.Name} vs {b.GetType().Name}";

            if (IsSimple(type))
                return a.Equals(b) ? null : $"{path}: {Show(a)} vs {Show(b)}";

            var properties = leafProperties.GetOrAdd(type, t => t
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && IsSimple(p.PropertyType))
                .OrderBy(p => p.MetadataToken)
                .ToArray());

            foreach (var property in properties)
            {
                var valueA = property.GetValue(a);
                var valueB = property.GetValue(b);

                if (!Equals(valueA, valueB))
                    return $"{path}.{property.Name}: {Show(valueA)} vs {Show(valueB)}";
            }

            return null;
        }

        private static bool IsSimple(Type type)
        {
            var underlying = Nullable.GetUnderlyingType(type) ?? type;

            return underlying.IsPrimitive || underlying.IsEnum || underlying == typeof(string) || underlying == typeof(decimal);
        }

        private static string Show(object value)
        {
            if (value == null)
                return "null";

            if (value is string s)
                return s.Length > 60 ? $"\"{s.Substring(0, 60)}...\"" : $"\"{s}\"";

            return value.ToString();
        }
    }
}
