using System;
using System.Collections.Generic;
using System.Linq;

using log4net;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.WorldObjects;

namespace ACE.Server.WeaponMods
{
    /// <summary>How a layer 1 material combines its delta with the property's current value.</summary>
    public enum WeaponTinkerOp
    {
        Add,
        Multiply,
    }

    /// <summary>
    /// One layer 1 (retail tinker) material: the property it moves, how, and by how much. Every delta below was
    /// read off the LIVE DAT mutation scripts under Source/ACE.Server/Entity/Mutations/Recipes/, not off
    /// RecipeManager.TryMutateNative, which is dead code carrying a stale table.
    ///
    /// This system NEVER reproduces the retail scripts' "set" branch. Several of them read
    ///
    ///     ElementalDamageMod (>= 0.01 ? add : set) 0.01
    ///
    /// which writes 0.01 onto a weapon that has no row - and the engine reads an absent ElementalDamageMod as
    /// 1.0 (WorldObject_Weapon.cs:438), so the retail script turns "no elemental bonus" into "99% elemental
    /// damage LOST". ACE already carries a workaround for the damage that bug did to retail data
    /// (GetWeaponDefenseModifier's ImbueStackingBits fixup). Instead, every material here combines against
    /// <see cref="EngineDefault"/>, the literal the engine's own "??" falls back to, and a reversal that lands
    /// back on that default REMOVES the row.
    ///
    /// OAK IS NOT IN ANY POOL, deliberately. Its script is "WeaponTime -= 50" followed by a floor at 0, and 75%
    /// of the 4009 weapon weenies carrying WeaponTime are already below 50, so the first Oak floors them and
    /// nominal subtraction does not reverse it - it INFLATES WeaponTime without bound, making the weapon
    /// permanently slower on every reroll. There is no upper clamp at the combat read (WorldObject_Weapon.cs:325-330).
    /// Pool sizes after that drop: melee 4, missile 3, caster 4.
    ///
    /// THE "BELOW THE DEFAULT, THEREFORE REMOVE" RECOVERY IS PER-MATERIAL, NOT GLOBAL (HARD). See
    /// <see cref="RecoverSetBranch"/>. An earlier revision applied it to every material with a multiplier-floor
    /// default, on the premise that a below-default value could only be the retail "set 0.01" artifact. That
    /// premise is FALSE and the row counts are in the brief's section 5: 11 weapon weenies ship DamageMod below
    /// 1.0 and 26 ship WeaponDefense below 1.0, none of which came from a set branch, and deleting their row is a
    /// straight damage or defense buff that nothing afterwards can audit.
    /// </summary>
    public class WeaponTinkerMaterial
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public MaterialType Material;

        /// <summary>Player-facing material name, for the appraisal and craft lines.</summary>
        public string DisplayName;

        /// <summary>The native integer property this material moves. Exactly one of this and <see cref="FloatProperty"/> is set.</summary>
        public PropertyInt? IntProperty;

        /// <summary>The native float property this material moves. Exactly one of this and <see cref="IntProperty"/> is set.</summary>
        public PropertyFloat? FloatProperty;

        public WeaponTinkerOp Op;

        /// <summary>The per-application delta: an addend for <see cref="WeaponTinkerOp.Add"/>, a factor for <see cref="WeaponTinkerOp.Multiply"/>.</summary>
        public double Delta;

        /// <summary>What the engine reads when the property is ABSENT. Both directions combine against this.</summary>
        public double EngineDefault;

        /// <summary>
        /// TRUE only for a material whose LIVE DAT mutation script actually carries the broken "set" branch
        ///
        ///     ElementalDamageMod (>= 0.01 ? add : set) 0.01
        ///
        /// On those, and ONLY those, a reversal that lands below <see cref="EngineDefault"/> means the weapon's
        /// row was written by that branch onto a property that was absent, so absent is the correct restore and
        /// the row is REMOVED.
        ///
        /// It must stay false everywhere else. A below-default value on a plain "+=" material is a legitimate
        /// loot or weenie value, and removing its row hands the weapon the engine default for free. Verified
        /// against ace_world on 2026-07-30, weapon weenies only (ItemType 1/256/32768): DamageMod 63 sits below
        /// 1.0 on 11 weenies (0.1 to 0.6, including the phantom launchers 21963-21965 and the newbie launchers
        /// 518/521/531/537/23109) and WeaponDefense 29 sits below 1.0 on 26 weenies (0.8 x23, 0.92 x2, 0.96).
        /// Deleting a 0.5 DamageMod row is a 100% damage gain, because the engine reads absent as 1.0
        /// (Entity/BaseDamageMod.cs:52, Network/Structure/WeaponProfile.cs:104).
        ///
        /// Audited 2026-07-30 across every script in Source/ACE.Server/Entity/Mutations/Recipes/: of the seven
        /// materials in this table only Green Garnet (0x3800004B, ElementalDamageMod) and Opal (0x3800002E,
        /// ManaConversionMod) carry the branch. Iron, Mahogany, Granite, Brass and Velvet are plain "+=" / "*=".
        /// </summary>
        public bool RecoverSetBranch;

        /// <summary>Which weapon classes may draw this material.</summary>
        public WeaponClass Classes;

        /// <summary>The recipe id of the retail tinker this mirrors. Documentation only.</summary>
        public uint Recipe;

        public bool AppliesTo(WeaponClass weaponClass) => weaponClass != WeaponClass.None && (Classes & weaponClass) != 0;

        // ---------------- pure apply / reverse arithmetic ----------------

        /// <summary>
        /// The property value after applying this material <paramref name="count"/> times to a current value of
        /// <paramref name="current"/> (null = absent). Pure - no item, no player, no database.
        ///
        /// The multiply is done ONCE through Math.Pow rather than count times in a loop, so float drift does not
        /// accumulate across rerolls.
        /// </summary>
        public double ApplyValue(double? current, int count)
        {
            var value = current ?? EngineDefault;

            if (count <= 0)
                return value;

            return Op == WeaponTinkerOp.Add
                ? value + count * Delta
                : value * Math.Pow(Delta, count);
        }

        /// <summary>
        /// The property value after taking <paramref name="count"/> applications back off, or NULL when the row
        /// should be REMOVED. Pure.
        ///
        /// Two removal cases, both restoring "absent", which is what the engine's own "??" default means:
        ///   - the result landed within <see cref="WeaponModRegistry.Epsilon"/> of <see cref="EngineDefault"/>;
        ///   - the result landed BELOW <see cref="EngineDefault"/> AND this material carries
        ///     <see cref="RecoverSetBranch"/>. That is the recovery path for a weapon whose retail tinkering took
        ///     the "set 0.01" branch: its original value was absent, so absent is the correct restore. It is
        ///     PER-MATERIAL because a below-default value on any other material is a real, shipped weenie value.
        ///
        /// A reversal is floored at 0 for every material - no native this table touches has a meaningful negative
        /// value, and a negative would be written straight onto the item. Hitting the floor is logged, because it
        /// can only happen when the tinker log claims more applications than the item actually carries; the
        /// integrity gate exists to keep that from reaching here.
        /// </summary>
        public double? ReverseValue(double? current, int count)
        {
            var value = current ?? EngineDefault;

            if (count <= 0)
                return current;

            var result = Op == WeaponTinkerOp.Add
                ? value - count * Delta
                : value / Math.Pow(Delta, count);

            if (result < 0.0)
            {
                log.Warn($"WeaponTinkerMaterial.ReverseValue({Material}): reversing {count} application(s) from {value} produced {result}. " +
                         "A reversal can only go negative when the tinker log disagrees with the item. Flooring at 0.");

                result = 0.0;
            }

            if (Math.Abs(result - EngineDefault) <= WeaponModRegistry.Epsilon)
                return null;

            if (RecoverSetBranch && result < EngineDefault)
                return null;

            return result;
        }

        // ---------------- item access ----------------

        public double? ReadValue(WorldObject weapon)
        {
            if (weapon == null)
                return null;

            if (IntProperty != null)
            {
                var raw = weapon.GetProperty(IntProperty.Value);
                return raw == null ? (double?)null : raw.Value;
            }

            return weapon.GetProperty(FloatProperty.Value);
        }

        public void WriteValue(WorldObject weapon, double? value)
        {
            if (weapon == null)
                return;

            if (IntProperty != null)
            {
                if (value == null)
                    weapon.RemoveProperty(IntProperty.Value);
                else
                    weapon.SetProperty(IntProperty.Value, (int)Math.Round(value.Value, MidpointRounding.AwayFromZero));

                return;
            }

            if (value == null)
                weapon.RemoveProperty(FloatProperty.Value);
            else
                weapon.SetProperty(FloatProperty.Value, value.Value);
        }

        /// <summary>
        /// Applies this material <paramref name="count"/> times to the weapon.
        ///
        /// NO JUNK ROW ON A DEAD ROLL. Granite is "DamageVariance *= 0.8", so on a weapon carrying no
        /// DamageVariance row it computes 0.0 * 0.8^n = 0.0 and would SetProperty a 0.0 where no row existed:
        /// a row that reads identically to absent, survives every reversal (it lands on the engine default and
        /// gets removed, so it is not even stable), and clutters biota_properties_float for nothing.
        ///
        /// THAT GRANITE IS A DEAD ROLL ON A ZERO-VARIANCE WEAPON IS FAITHFUL TO RETAIL AND IS NOT A BUG. The
        /// live script really is a bare multiply, and 0 * 0.8 really is 0. Do NOT "fix" the dead roll by giving
        /// Granite a floor, a fallback base, or an additive branch - only the junk row was ever the defect.
        /// </summary>
        public void Apply(WorldObject weapon, int count)
        {
            if (weapon == null || count <= 0)
                return;

            var current = ReadValue(weapon);
            var result = ApplyValue(current, count);

            if (current == null && Math.Abs(result - EngineDefault) <= WeaponModRegistry.Epsilon)
                return;

            WriteValue(weapon, result);
        }

        public void Reverse(WorldObject weapon, int count)
        {
            if (weapon == null || count <= 0)
                return;

            WriteValue(weapon, ReverseValue(ReadValue(weapon), count));
        }
    }

    /// <summary>
    /// The layer 1 material table and the per-class pools drawn from it. Uniform, with replacement, no focus
    /// material and no weighting - the mushy spread is the whole point of the reroll being the budget path
    /// rather than the optimal one (design section 1).
    /// </summary>
    public static class WeaponTinkerTable
    {
        private static readonly WeaponTinkerMaterial[] materials =
        {
            new WeaponTinkerMaterial
            {
                Material = MaterialType.Iron,
                DisplayName = "Iron",
                Recipe = 0x3800001A,
                IntProperty = PropertyInt.Damage,
                Op = WeaponTinkerOp.Add,
                Delta = 1.0,
                // DamageEvent.cs:449 reads "Weapon.Damage ?? 0"
                EngineDefault = 0.0,
                // plain "Damage += 1" (0x3800001A) - no set branch, so no below-default recovery
                Classes = WeaponClass.Melee,
            },
            new WeaponTinkerMaterial
            {
                Material = MaterialType.Mahogany,
                DisplayName = "Mahogany",
                Recipe = 0x3800001B,
                FloatProperty = PropertyFloat.DamageMod,
                Op = WeaponTinkerOp.Add,
                Delta = 0.04,
                // VERIFIED: both reads of PropertyFloat.DamageMod in ACE.Server fall back to 1.0 -
                // Entity/BaseDamageMod.cs:52 and Network/Structure/WeaponProfile.cs:104. A multiplier floor.
                EngineDefault = 1.0,
                // NO RecoverSetBranch. The script (0x3800001B) is a plain "DamageMod += 0.04", and 11 weapon
                // weenies ship DamageMod below 1.0 outright (0.1 to 0.6 - bowphantom 21964, atlatlphantom 21963,
                // crossbowphantom 21965, 31705, 7310, 7315, and the newbie launchers 518/521/531/537/23109).
                // Removing their row would read as 1.0 and hand the weapon up to a 100% damage gain.
                Classes = WeaponClass.Missile,
            },
            new WeaponTinkerMaterial
            {
                Material = MaterialType.GreenGarnet,
                DisplayName = "Green Garnet",
                Recipe = 0x3800004B,
                FloatProperty = PropertyFloat.ElementalDamageMod,
                Op = WeaponTinkerOp.Add,
                Delta = 0.01,
                // WorldObject_Weapon.cs:438 - "weapon.ElementalDamageMod ?? 1.0f". A multiplier floor, and the
                // reason this system never copies the retail script's "set 0.01" branch.
                EngineDefault = 1.0,
                // The ONLY material in this table where the flag does real work. Its script (0x3800004B) is
                // "ElementalDamageMod (>= 0.01 ? add : set) 0.01", so a caster below 1.0 really did have no row
                // before retail tinkering wrote one, and VERIFIED against ace_world: zero weapon weenies ship
                // ElementalDamageMod below 1.0, so nothing legitimate can be caught by this.
                RecoverSetBranch = true,
                Classes = WeaponClass.Caster,
            },
            new WeaponTinkerMaterial
            {
                Material = MaterialType.Opal,
                DisplayName = "Opal",
                Recipe = 0x3800002E,
                FloatProperty = PropertyFloat.ManaConversionMod,
                Op = WeaponTinkerOp.Add,
                Delta = 0.01,
                EngineDefault = 0.0,
                // Opal's script (0x3800002E) DOES carry the set branch - "ManaConversionMod (>= 0.01 ? add :
                // set) 0.01" - but RecoverSetBranch is deliberately NOT set, because here it would be dead
                // configuration: this property's engine default is 0.0, so a set-branch artifact of 0.01
                // reverses to 0.0 and is already removed by the "within epsilon of the default" rule above, and
                // anything further below is floored to 0.0 and removed by the same rule. Set the flag only if
                // ManaConversionMod's engine default ever becomes a positive multiplier floor.
                Classes = WeaponClass.Caster,
            },
            new WeaponTinkerMaterial
            {
                Material = MaterialType.Granite,
                DisplayName = "Granite",
                Recipe = 0x3800001C,
                FloatProperty = PropertyFloat.DamageVariance,
                Op = WeaponTinkerOp.Multiply,
                Delta = 0.8,
                EngineDefault = 0.0,
                // plain "DamageVariance *= 0.8" (0x3800001C) - no set branch. See Apply() for why a weapon with
                // no DamageVariance row gets NO row written rather than a junk 0.0.
                Classes = WeaponClass.Melee | WeaponClass.Missile,
            },
            new WeaponTinkerMaterial
            {
                Material = MaterialType.Brass,
                DisplayName = "Brass",
                Recipe = 0x38000020,
                FloatProperty = PropertyFloat.WeaponDefense,
                Op = WeaponTinkerOp.Add,
                Delta = 0.01,
                EngineDefault = 1.0,
                // NO RecoverSetBranch. The script (0x38000020) is a plain "WeaponDefense += 0.01", and 26 weapon
                // weenies ship WeaponDefense below 1.0 outright (0.8 x23, 0.92 x2, 0.96). Those are real values,
                // not set-branch artifacts, and deleting their row would silently buff the weapon.
                Classes = WeaponClass.All,
            },
            new WeaponTinkerMaterial
            {
                Material = MaterialType.Velvet,
                DisplayName = "Velvet",
                Recipe = 0x38000021,
                FloatProperty = PropertyFloat.WeaponOffense,
                Op = WeaponTinkerOp.Add,
                Delta = 0.01,
                EngineDefault = 1.0,
                // NO RecoverSetBranch. The script (0x38000021) is a plain "WeaponOffense += 0.01". VERIFIED
                // against ace_world: zero weapon weenies ship WeaponOffense below 1.0 today, so the flag would
                // be harmless here - it stays false anyway, because the rule is "the script has the branch",
                // not "no shipped data happens to be caught by it".
                // NOT missile on purpose: GetWeaponOffenseModifier returns the default unconditionally when
                // weapon.IsRanged (WorldObject_Weapon.cs:259-271), so WeaponOffense is a dead roll on bows,
                // crossbows and atlatls.
                Classes = WeaponClass.Melee | WeaponClass.Caster,
            },
        };

        public static readonly IReadOnlyList<WeaponTinkerMaterial> AllMaterials = materials;

        private static readonly Dictionary<MaterialType, WeaponTinkerMaterial> byMaterial =
            materials.ToDictionary(m => m.Material);

        private static readonly Dictionary<WeaponClass, WeaponTinkerMaterial[]> pools = new Dictionary<WeaponClass, WeaponTinkerMaterial[]>
        {
            { WeaponClass.Melee,   materials.Where(m => m.AppliesTo(WeaponClass.Melee)).ToArray() },
            { WeaponClass.Missile, materials.Where(m => m.AppliesTo(WeaponClass.Missile)).ToArray() },
            { WeaponClass.Caster,  materials.Where(m => m.AppliesTo(WeaponClass.Caster)).ToArray() },
        };

        private static readonly WeaponTinkerMaterial[] emptyPool = new WeaponTinkerMaterial[0];

        /// <summary>The materials a given weapon class draws from. Melee 4, missile 3, caster 4.</summary>
        public static IReadOnlyList<WeaponTinkerMaterial> Pool(WeaponClass weaponClass) =>
            pools.TryGetValue(weaponClass, out var pool) ? pool : emptyPool;

        public static bool TryGet(MaterialType material, out WeaponTinkerMaterial definition) =>
            byMaterial.TryGetValue(material, out definition);

        /// <summary>
        /// TRUE when this system knows how to apply and reverse the material. A retail log can carry entries
        /// this table does not own - imbue salvage, Oak, or (when a bag has no MaterialType) a raw wcid - and
        /// those are left strictly alone rather than guessed at.
        /// </summary>
        public static bool IsKnown(MaterialType material) => byMaterial.ContainsKey(material);
    }
}
