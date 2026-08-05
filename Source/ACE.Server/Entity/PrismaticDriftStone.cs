using System;
using System.Collections.Generic;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity
{
    /// <summary>
    /// The Prismatic Drift Stone - a starter tinkering gem.
    ///
    /// Used on a clean (never tinkered, never imbued) melee weapon, missile launcher or caster
    /// carried in the player's pack. It always succeeds, is consumed, and permanently closes the
    /// weapon's tinkering future (NumTimesTinkered is set to <see cref="LockedTinkerCount"/>).
    ///
    /// It applies two things at once:
    ///   1. A rend imbue matched to the weapon's own damage type (see <see cref="GetRend"/>).
    ///   2. Between <see cref="MinBoosts"/> and <see cref="MaxBoosts"/> incremental damage tinkers,
    ///      class-appropriate (see <see cref="GetBoostClass"/>).
    ///
    /// The property changes mirror the retail imbue / weapon tinkering side effects. The readable
    /// reference for which retail material produces which effect is the dead legacy code in
    /// RecipeManager.TryMutateNative (Iron / Mahogany / Green Garnet at ~lines 597-602 and 561-563,
    /// the rend gems at ~lines 617-644, Black Opal / CriticalStrike at ~line 553); the live retail
    /// path is DAT mutation scripts, but this manager writes the property changes directly, which is
    /// exactly what that legacy mirror documents. RecipeManager.IconUnderlay supplies the icon
    /// underlay and RecipeManager.HandleTinkerLog the TinkerLog format (comma separated MaterialType
    /// ids), both reused here so there is one source of truth.
    /// </summary>
    public static class PrismaticDriftStone
    {
        /// <summary>
        /// The one wcid this manager answers to.
        ///
        /// A single item does not justify a custom property id. If this ever grows into a family of
        /// drift stone tiers, move the branch off the wcid and onto a dedicated property, registered
        /// in Source/property-registry.tsv per PR #231's flow, rather than growing this constant into
        /// a list.
        /// </summary>
        public const uint PrismaticDriftStoneWcid = 1001600;

        /// <summary>
        /// NumTimesTinkered written on success. The weapon is treated as fully tinkered no matter how
        /// many boosts actually landed - this is the permanent lock, not a count of what was applied.
        /// </summary>
        public const int LockedTinkerCount = 10;

        public const int MinBoosts = 1;
        public const int MaxBoosts = 5;

        /// <summary>Iron equivalent: PropertyInt.Damage +1 per boost.</summary>
        public const int MeleeDamagePerBoost = 1;

        /// <summary>Mahogany equivalent: PropertyFloat.DamageMod +0.04 per boost.</summary>
        public const double MissileDamageModPerBoost = 0.04;

        /// <summary>Green Garnet equivalent: PropertyFloat.ElementalDamageMod +0.01 per boost.</summary>
        public const double CasterElementalDamageModPerBoost = 0.01;

        /// <summary>Weapon classes this stone accepts. ItemType.WeaponOrCaster = MeleeWeapon | MissileWeapon | Caster.</summary>
        public const ItemType EligibleItemTypes = ItemType.WeaponOrCaster;

        public static bool IsPrismaticDriftStone(uint wcid)
        {
            return wcid == PrismaticDriftStoneWcid;
        }

        public static bool IsPrismaticDriftStone(WorldObject wo)
        {
            return wo != null && IsPrismaticDriftStone(wo.WeenieClassId);
        }

        /// <summary>
        /// Which incremental damage tinker a weapon gets, by weapon class.
        /// </summary>
        public enum BoostClass
        {
            None,

            /// <summary>Melee weapon: PropertyInt.Damage, Iron equivalent.</summary>
            MeleeDamage,

            /// <summary>Missile launcher: PropertyFloat.DamageMod, Mahogany equivalent.</summary>
            MissileDamageMod,

            /// <summary>Caster: PropertyFloat.ElementalDamageMod, Green Garnet equivalent.</summary>
            CasterElementalDamageMod,
        }

        /// <summary>
        /// What one application did. Returned by <see cref="ApplyToWeapon"/> so the caller can report
        /// it without re-deriving anything.
        /// </summary>
        public class Result
        {
            public ImbuedEffectType Rend;
            public BoostClass BoostClass;
            public int NumBoosts;

            /// <summary>The materials appended to the weapon's TinkerLog: NumBoosts boost materials, then the rend gem's.</summary>
            public List<MaterialType> Materials = new List<MaterialType>();
        }

        /// <summary>
        /// Maps a weapon's damage type to the rend it earns.
        ///
        /// Deterministic priority for multi-type weapons - elemental bits beat physical ones:
        ///   Fire > Cold > Acid > Electric > Nether > Slash > Pierce > Bludgeon.
        ///
        /// Nether has no rend in ImbuedEffectType's usable set (NetherRending exists but has no icon
        /// underlay and no combat handling), so nether wands get CriticalStrike instead.
        ///
        /// Fallback when the weapon carries no rendable damage bit at all: a missile launcher gets
        /// PierceRending (arrows are pierce - the elementless-bow default), everything else gets
        /// CriticalStrike (a wand with no element has nothing to rend, same reasoning as nether).
        /// </summary>
        public static ImbuedEffectType GetRend(DamageType damageType, ItemType itemType)
        {
            if ((damageType & DamageType.Fire) != 0)     return ImbuedEffectType.FireRending;
            if ((damageType & DamageType.Cold) != 0)     return ImbuedEffectType.ColdRending;
            if ((damageType & DamageType.Acid) != 0)     return ImbuedEffectType.AcidRending;
            if ((damageType & DamageType.Electric) != 0) return ImbuedEffectType.ElectricRending;

            if ((damageType & DamageType.Nether) != 0)   return ImbuedEffectType.CriticalStrike;

            if ((damageType & DamageType.Slash) != 0)    return ImbuedEffectType.SlashRending;
            if ((damageType & DamageType.Pierce) != 0)   return ImbuedEffectType.PierceRending;
            if ((damageType & DamageType.Bludgeon) != 0) return ImbuedEffectType.BludgeonRending;

            if ((itemType & ItemType.MissileWeapon) != 0) return ImbuedEffectType.PierceRending;

            return ImbuedEffectType.CriticalStrike;
        }

        public static ImbuedEffectType GetRend(WorldObject weapon)
        {
            return GetRend(weapon.W_DamageType, weapon.ItemType);
        }

        /// <summary>
        /// The retail imbue gem whose material stamps the TinkerLog for a given rend.
        /// Mirrors RecipeManager.TryMutateNative's material -> ImbuedEffectType cases.
        /// </summary>
        public static MaterialType GetRendMaterial(ImbuedEffectType rend)
        {
            switch (rend)
            {
                case ImbuedEffectType.SlashRending:    return MaterialType.ImperialTopaz;   // 0x38000040
                case ImbuedEffectType.PierceRending:   return MaterialType.BlackGarnet;     // 0x3800003F
                case ImbuedEffectType.BludgeonRending: return MaterialType.WhiteSapphire;   // 0x3800003B
                case ImbuedEffectType.ColdRending:     return MaterialType.Aquamarine;      // 0x3800003C
                case ImbuedEffectType.FireRending:     return MaterialType.RedGarnet;       // 0x3800003E
                case ImbuedEffectType.AcidRending:     return MaterialType.Emerald;         // 0x3800003A
                case ImbuedEffectType.ElectricRending: return MaterialType.Jet;             // 0x3800003D
                case ImbuedEffectType.CriticalStrike:  return MaterialType.BlackOpal;       // 0x38000023
            }

            return MaterialType.Unknown;
        }

        public static string GetRendName(ImbuedEffectType rend)
        {
            switch (rend)
            {
                case ImbuedEffectType.SlashRending:    return "Slash Rending";
                case ImbuedEffectType.PierceRending:   return "Pierce Rending";
                case ImbuedEffectType.BludgeonRending: return "Bludgeon Rending";
                case ImbuedEffectType.ColdRending:     return "Cold Rending";
                case ImbuedEffectType.FireRending:     return "Fire Rending";
                case ImbuedEffectType.AcidRending:     return "Acid Rending";
                case ImbuedEffectType.ElectricRending: return "Electric Rending";
                case ImbuedEffectType.CriticalStrike:  return "Critical Strike";
            }

            return rend.ToString();
        }

        /// <summary>
        /// Which damage tinker a weapon class gets. Keyed on ItemType, which is what the client's
        /// TargetType gate on the stone's weenie also uses.
        /// </summary>
        public static BoostClass GetBoostClass(ItemType itemType)
        {
            if ((itemType & ItemType.MeleeWeapon) != 0)   return BoostClass.MeleeDamage;
            if ((itemType & ItemType.MissileWeapon) != 0) return BoostClass.MissileDamageMod;
            if ((itemType & ItemType.Caster) != 0)        return BoostClass.CasterElementalDamageMod;

            return BoostClass.None;
        }

        public static BoostClass GetBoostClass(WorldObject weapon)
        {
            return GetBoostClass(weapon.ItemType);
        }

        /// <summary>The retail salvage material each boost stamps into the TinkerLog.</summary>
        public static MaterialType GetBoostMaterial(BoostClass boostClass)
        {
            switch (boostClass)
            {
                case BoostClass.MeleeDamage:              return MaterialType.Iron;         // 0x3800001A
                case BoostClass.MissileDamageMod:         return MaterialType.Mahogany;     // 0x3800001B
                case BoostClass.CasterElementalDamageMod: return MaterialType.GreenGarnet;  // 0x3800004B
            }

            return MaterialType.Unknown;
        }

        public static bool IsEligibleWeapon(WorldObject target)
        {
            return target != null && (target.ItemType & EligibleItemTypes) != 0;
        }

        /// <summary>
        /// A clean weapon has never been tinkered, imbued, or tinker-logged. The stone refuses
        /// anything else - it has no slot accounting of its own, it just consumes them all.
        /// </summary>
        public static bool IsCleanWeapon(WorldObject target)
        {
            if (target == null)
                return false;

            if (target.NumTimesTinkered != 0)
                return false;

            if (target.ImbuedEffect != ImbuedEffectType.Undef)
                return false;

            if (!string.IsNullOrEmpty(target.TinkerLog))
                return false;

            return true;
        }

        /// <summary>
        /// TRUE when a weapon's state matches what <see cref="ApplyToWeapon"/> leaves behind. Other systems that
        /// rewrite a weapon's tinker budget (ACE.Server.WeaponMods) call this to refuse the weapon outright,
        /// because this stone promises the player the weapon "can never be tinkered again" and that promise has
        /// to be honored by anything that could break it.
        ///
        /// WHY AN EXPLICIT CHECK RATHER THAN RELYING ON THE INTEGRITY GATE. A drift-stoned weapon carries
        /// NumTimesTinkered = <see cref="LockedTinkerCount"/> (10) but only <see cref="MinBoosts"/>+1 ..
        /// <see cref="MaxBoosts"/>+1 = 2..6 log entries, so a "log length must equal NumTimesTinkered" gate
        /// happens to refuse it today. That is a COINCIDENCE OF THE CURRENT TUNING, not a guarantee: raising
        /// MaxBoosts to 9 makes the log ten entries long and the gate would wave the weapon straight through.
        ///
        /// KNOWN LIMITATION - THIS IS A SIGNATURE, NOT A MARKER. The stone writes no dedicated property, so
        /// there is nothing on the weapon that says "a drift stone did this"; the signature below is the closest
        /// thing available and it can false-positive. A player who hand-tinkers N of the class boost material
        /// and then applies the rend matching their own damage type produces the same shape. Refusing that
        /// weapon costs the player one unavailable reroll and corrupts nothing, which is the right side to err
        /// on. The durable fix is a dedicated marker property stamped by <see cref="ApplyToWeapon"/>, which
        /// needs an id reserved in Source/property-registry.tsv and would not cover weapons already stamped on a
        /// live shard.
        /// </summary>
        public static bool MatchesAppliedSignature(WorldObject weapon)
        {
            if (weapon == null)
                return false;

            if (weapon.NumTimesTinkered != LockedTinkerCount)
                return false;

            var rend = GetRend(weapon);

            if (weapon.ImbuedEffect != rend)
                return false;

            var boostMaterial = GetBoostMaterial(GetBoostClass(weapon));

            if (boostMaterial == MaterialType.Unknown)
                return false;

            var rendMaterial = GetRendMaterial(rend);

            if (rendMaterial == MaterialType.Unknown)
                return false;

            var log = weapon.TinkerLog;

            if (string.IsNullOrWhiteSpace(log))
                return false;

            var entries = new List<MaterialType>();

            foreach (var raw in log.Split(','))
            {
                if (!uint.TryParse(raw.Trim(), out var value))
                    return false;

                entries.Add((MaterialType)value);
            }

            // NumBoosts boost materials, then the rend gem's material - see ApplyToWeapon
            if (entries.Count < MinBoosts + 1 || entries.Count > MaxBoosts + 1)
                return false;

            if (entries[entries.Count - 1] != rendMaterial)
                return false;

            for (var i = 0; i < entries.Count - 1; i++)
            {
                if (entries[i] != boostMaterial)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// The player uses a Prismatic Drift Stone on a weapon.
        /// Mirrors CorePlating.UseObjectOnTarget's busy/combat/motion shape and RecipeManager's
        /// confirm-then-act shape.
        /// </summary>
        public static void UseObjectOnTarget(Player player, WorldObject source, WorldObject target, bool confirmed = false)
        {
            if (player.IsBusy)
            {
                player.SendUseDoneEvent(WeenieError.YoureTooBusy);
                return;
            }

            var allowCraftInCombat = PropertyManager.GetBool("allow_combat_mode_crafting").Item;

            if (!allowCraftInCombat && player.CombatMode != CombatMode.NonCombat)
            {
                player.SendUseDoneEvent(WeenieError.YouMustBeInPeaceModeToTrade);
                return;
            }

            // verify use requirements
            var useError = VerifyUseRequirements(player, source, target);
            if (useError != WeenieError.None)
            {
                player.SendUseDoneEvent(useError);
                return;
            }

            // the application permanently closes this weapon's tinkering future - confirm first.
            if (!confirmed)
            {
                if (!player.ConfirmationManager.EnqueueSend(new Confirmation_PrismaticDriftStone(player.Guid, source.Guid, target.Guid), GetConfirmationText(target)))
                {
                    player.SendUseDoneEvent(WeenieError.ConfirmationInProgress);
                    return;
                }

                player.SendUseDoneEvent();
                return;
            }

            var motionCommand = MotionCommand.ClapHands;

            var actionChain = new ActionChain();
            var nextUseTime = 0.0f;

            player.IsBusy = true;

            if (allowCraftInCombat && player.CombatMode != CombatMode.NonCombat)
            {
                // Drop out of combat mode. This depends on the server property "allow_combat_mode_crafting"
                // being True. If not, this action would have aborted due to not being in NonCombat mode.
                var stanceTime = player.SetCombatMode(CombatMode.NonCombat);
                actionChain.AddDelaySeconds(stanceTime);

                nextUseTime += stanceTime;
            }

            var currentStance = player.CurrentMotionState.Stance; // expected to be MotionStance.NonCombat
            var clapTime = Physics.Animation.MotionTable.GetAnimationLength(player.MotionTableId, currentStance, motionCommand);

            actionChain.AddAction(player, () => player.SendMotionAsCommands(motionCommand, currentStance));
            actionChain.AddDelaySeconds(clapTime);

            nextUseTime += clapTime;

            actionChain.AddAction(player, () =>
            {
                // re-verify: the player had a dialog and an animation's worth of time to change the world
                var reverifyError = VerifyUseRequirements(player, source, target);
                if (reverifyError != WeenieError.None)
                {
                    player.SendUseDoneEvent(reverifyError);
                    return;
                }

                Apply(player, source, target);
            });

            actionChain.AddAction(player, () => player.IsBusy = false);

            actionChain.EnqueueChain();

            player.NextUseTime = DateTime.UtcNow.AddSeconds(nextUseTime);
        }

        /// <summary>
        /// A caster carrying no elemental alignment at all - no DamageType bit. These are the
        /// weapons that take the CriticalStrike fallback rather than a rend, and the ones an
        /// Attuned Drift Prism could otherwise have given an element to first.
        ///
        /// Scope note: the Attuned Drift Prism feature discriminates orbs specifically, via a table
        /// of orb-shaped Setup DataIds (AttunedDriftPrism.OrbSetupIds on its own branch). That table
        /// is deliberately NOT duplicated here - two copies of a mesh id list would drift the moment
        /// either side added an orb. So the warning is raised for every unaligned caster, not only
        /// orbs. The sentence stays true either way: what forecloses a later attunement is this
        /// stone's permanent tinker lock, which applies to wands, staves and sceptres exactly as it
        /// does to orbs, whether or not a prism exists for that shape today.
        /// </summary>
        public static bool IsUnalignedCaster(WorldObject target)
        {
            if (target == null)
                return false;

            return (target.ItemType & ItemType.Caster) != 0 && target.W_DamageType == DamageType.Undef;
        }

        /// <summary>Kept under the client confirmation panel's silent ~600 character clip.</summary>
        public static string GetConfirmationText(WorldObject target)
        {
            var text = $"Use the Prismatic Drift Stone on {target.Name}? The weapon gains a rending matched to its own damage type, plus between {MinBoosts} and {MaxBoosts} damage tinkers. It will then count as fully tinkered and can never be tinkered again. The stone is consumed.";

            if (IsUnalignedCaster(target))
                text += " This caster has no elemental alignment, and the lock is permanent, so it can never be attuned to an element afterward.";

            return text;
        }

        public static WeenieError VerifyUseRequirements(Player player, WorldObject source, WorldObject target)
        {
            if (source == null || target == null)
                return WeenieError.YouDoNotPassCraftingRequirements;

            if (source == target)
            {
                player.SendTransientError($"You can't use the {source.Name} on itself.");
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            if (!IsPrismaticDriftStone(source))
                return WeenieError.YouDoNotPassCraftingRequirements;

            // inventory only - both the stone and the weapon must be somewhere the player can move items
            if (player.FindObject(source.Guid.Full, Player.SearchLocations.LocationsICanMove) == null)
                return WeenieError.YouDoNotPassCraftingRequirements;

            if (player.FindObject(target.Guid.Full, Player.SearchLocations.LocationsICanMove) == null)
                return WeenieError.YouDoNotPassCraftingRequirements;

            if (!IsEligibleWeapon(target))
            {
                player.SendTransientError($"The {source.Name} only works on a melee weapon, a missile launcher, or a caster.");
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            if (!IsCleanWeapon(target))
            {
                player.SendTransientError($"The {source.Name} only works on a weapon that has never been tinkered or imbued.");
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            return WeenieError.None;
        }

        private static void Apply(Player player, WorldObject source, WorldObject target)
        {
            var result = ApplyToWeapon(target);

            target.SaveBiotaToDatabase();

            player.TryConsumeFromInventoryWithNetworking(source, 1);

            UpdateObj(player, target);

            var plural = result.NumBoosts == 1 ? "damage tinker" : "damage tinkers";

            player.Session.Network.EnqueueSend(new GameMessageSystemChat($"The Prismatic Drift Stone refracts: {GetRendName(result.Rend)} and {result.NumBoosts} {plural} flow into {target.Name}.", ChatMessageType.Craft));

            player.SendUseDoneEvent();
        }

        /// <summary>
        /// The whole mechanic, with no player, networking or database in it - the rend, the rolled
        /// boosts, the TinkerLog, and the permanent tinker lock. Split out so it is directly testable.
        ///
        /// Callers are responsible for having verified eligibility first (see
        /// <see cref="VerifyUseRequirements"/>); this method does not re-check.
        /// </summary>
        public static Result ApplyToWeapon(WorldObject target)
        {
            var result = new Result
            {
                Rend = GetRend(target),
                BoostClass = GetBoostClass(target),
                NumBoosts = ThreadSafeRandom.Next(MinBoosts, MaxBoosts),
            };

            var boostMaterial = GetBoostMaterial(result.BoostClass);

            for (var i = 0; i < result.NumBoosts; i++)
            {
                switch (result.BoostClass)
                {
                    case BoostClass.MeleeDamage:
                        target.Damage = (target.Damage ?? 0) + MeleeDamagePerBoost;
                        break;

                    case BoostClass.MissileDamageMod:
                        target.DamageMod = (target.DamageMod ?? 0.0) + MissileDamageModPerBoost;
                        break;

                    case BoostClass.CasterElementalDamageMod:
                        target.ElementalDamageMod = (target.ElementalDamageMod ?? 0.0) + CasterElementalDamageModPerBoost;
                        break;
                }

                result.Materials.Add(boostMaterial);
            }

            // the imbue, and its retail side effects
            target.ImbuedEffect = result.Rend;

            if (RecipeManager.IconUnderlay.TryGetValue(result.Rend, out var iconUnderlay))
                target.IconUnderlayId = iconUnderlay;

            result.Materials.Add(GetRendMaterial(result.Rend));

            AppendTinkerLog(target, result.Materials);

            // the permanent lock: fully tinkered regardless of how many boosts landed
            target.NumTimesTinkered = LockedTinkerCount;

            return result;
        }

        /// <summary>Same comma separated MaterialType id format as RecipeManager.HandleTinkerLog.</summary>
        private static void AppendTinkerLog(WorldObject target, IEnumerable<MaterialType> materials)
        {
            foreach (var material in materials)
            {
                if (target.TinkerLog != null)
                    target.TinkerLog += ",";

                target.TinkerLog += (uint)material;
            }
        }

        /// <summary>
        /// Sends an UpdateObj to the client for the modified weapon.
        /// Mirrors RecipeManager.UpdateObj - the client moves an updated item to the first container
        /// slot, so the server has to mimic that for persistence.
        /// </summary>
        private static void UpdateObj(Player player, WorldObject obj)
        {
            player.EnqueueBroadcast(new GameMessageUpdateObject(obj));

            if (obj.CurrentWieldedLocation != null)
            {
                player.EnqueueBroadcast(new GameMessageObjDescEvent(player));
                return;
            }

            var invObj = player.FindObject(obj.Guid.Full, Player.SearchLocations.MyInventory);

            if (invObj != null)
                player.MoveItemToFirstContainerSlot(obj);
        }
    }
}
