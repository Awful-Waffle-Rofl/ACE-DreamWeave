using System;
using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity
{
    /// <summary>
    /// Attuned Drift Prisms - eight single-use gems that permanently align an ORB to one damage
    /// type, giving orb casters the elemental identity the loot tables never roll for them.
    ///
    /// The attunement is one-way and free: it sets the same three properties an inherently-elemental
    /// caster ships with (DamageType, ElementalDamageMod, and the matching UiEffects glow flag) and
    /// deliberately touches nothing else. In particular it never writes NumTimesTinkered, TinkerLog,
    /// ImbuedEffect or Retained, so an attuned orb still reads as a completely untinkered item to
    /// every other system - see <see cref="CleanStateProperties"/>.
    ///
    /// Evidence for the property set (queried against the local ace_world, 425 Caster weenies):
    /// PropertyInt.DamageType (45) is present on all 154 elemental casters and on zero plain ones;
    /// PropertyFloat.ElementalDamageMod (152) accompanies it on all 154; PropertyInt.UiEffects (18)
    /// carries the matching element flag on the vendor/loot elemental families. Nothing else in the
    /// weenie shape separates an elemental caster from a plain one.
    /// </summary>
    public static class AttunedDriftPrism
    {
        // ------------------------------------------------------------------
        // the prisms
        // ------------------------------------------------------------------

        public const uint FlamePrism     = 1001650;
        public const uint FrostPrism     = 1001651;
        public const uint AcidPrism      = 1001652;
        public const uint LightningPrism = 1001653;
        public const uint BladePrism     = 1001654;
        public const uint ForcePrism     = 1001655;
        public const uint ShockPrism     = 1001656;
        public const uint VoidPrism      = 1001657;

        /// <summary>
        /// The wcid set this feature owns. Kept as one self-contained table so the Gem.cs dispatch
        /// branch is a single lookup and merges cleanly alongside other use-on-target features.
        /// Content: Content/sql/weenies/1001650..1001657, registry owner "drift-prisms"
        /// (block 1001650-1001699).
        /// </summary>
        private static readonly Dictionary<uint, DamageType> PrismElements = new Dictionary<uint, DamageType>
        {
            { FlamePrism,     DamageType.Fire     },
            { FrostPrism,     DamageType.Cold     },
            { AcidPrism,      DamageType.Acid     },
            { LightningPrism, DamageType.Electric },
            { BladePrism,     DamageType.Slash    },
            { ForcePrism,     DamageType.Pierce   },
            { ShockPrism,     DamageType.Bludgeon },
            { VoidPrism,      DamageType.Nether   },
        };

        /// <summary>
        /// The UiEffects glow flag an inherently-elemental caster carries for each damage type.
        /// Verified one-to-one against the retail elemental caster families in ace_world (e.g.
        /// wcid 27884 "Flaming Orb" DamageType 16 / UiEffects 32; wcid 43381 "Nether Sceptre"
        /// DamageType 1024 / UiEffects 4096).
        /// </summary>
        private static readonly Dictionary<DamageType, UiEffects> ElementGlow = new Dictionary<DamageType, UiEffects>
        {
            { DamageType.Slash,    UiEffects.Slashing    },
            { DamageType.Pierce,   UiEffects.Piercing    },
            { DamageType.Bludgeon, UiEffects.Bludgeoning },
            { DamageType.Cold,     UiEffects.Frost       },
            { DamageType.Fire,     UiEffects.Fire        },
            { DamageType.Acid,     UiEffects.Acid        },
            { DamageType.Electric, UiEffects.Lightning   },
            { DamageType.Nether,   UiEffects.Nether      },
        };

        /// <summary>The word the success message uses for each element, in world voice.</summary>
        private static readonly Dictionary<DamageType, string> ElementPhrase = new Dictionary<DamageType, string>
        {
            { DamageType.Fire,     "flame"           },
            { DamageType.Cold,     "frost"           },
            { DamageType.Acid,     "acid"            },
            { DamageType.Electric, "lightning"       },
            { DamageType.Slash,    "a keen edge"     },
            { DamageType.Pierce,   "focused force"   },
            { DamageType.Bludgeon, "crushing shock"  },
            { DamageType.Nether,   "the empty dark"  },
        };

        public static bool IsPrism(uint wcid) => PrismElements.ContainsKey(wcid);

        public static bool TryGetElement(uint wcid, out DamageType element) => PrismElements.TryGetValue(wcid, out element);

        // ------------------------------------------------------------------
        // orb discrimination
        // ------------------------------------------------------------------

        /// <summary>
        /// Setup DataIds (PropertyDataId.Setup, 1) belonging to orb-shaped casters.
        ///
        /// There is NO int or float property that separates an orb from a wand / staff / sceptre:
        /// across all 425 Caster weenies in ace_world, ItemType (32768), DefaultCombatStyle (512
        /// Magic) and ValidLocations (0x01000000 Held) are constant, WeaponType is NULL/0/12 with no
        /// correlation to shape, and UiEffects encodes the element rather than the shape. Neither
        /// name column is usable either - class_Name "orbweddingsteele" is a Staff of Aerfalle, and
        /// the whole retail per-element orb family is class_Name "caster&lt;element&gt;". The Setup
        /// mesh is the only discriminator that holds up.
        ///
        /// Derived by (read-only ace_ro on ace_world):
        ///   SELECT DISTINCT d.value FROM weenie w
        ///     JOIN weenie_properties_d_i_d d ON d.object_Id = w.class_Id AND d.type = 1
        ///     LEFT JOIN weenie_properties_string s ON s.object_Id = w.class_Id AND s.type = 1
        ///    WHERE w.type = 35 AND (LOWER(w.class_Name) LIKE '%orb%' OR LOWER(s.value) LIKE '%orb%')
        ///      AND d.value &lt;&gt; 0x020004C1;
        /// 0x020004C1 is excluded by hand: it is the Staff of Aerfalle mesh, reached only through the
        /// misnamed "orbwedding*" class names. Every remaining Setup here resolves exclusively to
        /// orb-shaped casters (spheres, pearls, crystals, buadren, skulls).
        ///
        /// ACE's own CasterSlotSpells.IsOrb is deliberately not reused: it is
        /// "WeenieClassId == W_ORB_CLASS" (the single lootgen orb, wcid 2366) and carries a
        /// "todo: any other wcids for orbs?" comment, so it would reject every orb a player owns.
        /// </summary>
        public static readonly HashSet<uint> OrbSetupIds = new HashSet<uint>
        {
            0x020000ED, 0x0200091F, 0x02000986, 0x020009AD, 0x020009C5, 0x020009C6,
            0x020009C7, 0x020009D1, 0x020009E5, 0x02000A3A, 0x02000A7B, 0x02000B31,
            0x02000B5A, 0x02000B69, 0x02000B6A, 0x02000B7E, 0x02000EC3, 0x02000EC9,
            0x02000EDF, 0x02000EE9, 0x02000EF3, 0x02000FAA, 0x0200101E, 0x02001026,
            0x02001072, 0x02001073, 0x0200108E, 0x020010D8, 0x020010D9, 0x020010DA,
            0x02001112, 0x020011EA, 0x020011EB, 0x020011EC, 0x020011ED, 0x020011EE,
            0x020011EF, 0x020011F0, 0x020012D5, 0x020012EF, 0x02001380, 0x020014D1,
            0x02001503, 0x0200152D, 0x02001661, 0x02001738, 0x02001739,
        };

        public static bool IsOrb(WorldObject wo)
        {
            return wo is Caster && OrbSetupIds.Contains(wo.SetupTableId);
        }

        // ------------------------------------------------------------------
        // the value table
        // ------------------------------------------------------------------

        /// <summary>
        /// The fixed ElementalDamageMod an attunement grants, by the orb's loot tier (index 0 = tier 1).
        ///
        /// Each entry is the 25th percentile of that tier's natural elemental-caster roll range:
        ///     value = tier_min + 0.25 * (tier_max - tier_min), rounded to the roll granularity (0.01)
        /// There is no RNG anywhere in the attunement - the whole point is that a prism is a known
        /// quantity, so it lands a quarter of the way up the band it is imitating.
        ///
        /// SOURCE TABLE: Source/ACE.Server/Entity/Mutations/Casters/caster_elemental.txt - the embedded
        /// GDLE-derived mutation script that LootGenerationFactory_Caster.MutateCaster runs for every
        /// elemental caster. Its per-tier ElementalDamageMod bands are:
        ///     tier 4:  1.01 .. 1.03   ->  1.01 + 0.25 * 0.02 = 1.015  -> 1.02
        ///     tier 5:  1.02 .. 1.06   ->  1.02 + 0.25 * 0.04 = 1.03   -> 1.03
        ///     tier 6:  1.02 .. 1.11   ->  1.02 + 0.25 * 0.09 = 1.0425 -> 1.04
        ///     tier 7:  1.10 .. 1.15   ->  1.10 + 0.25 * 0.05 = 1.1125 -> 1.11
        ///     tier 8:  1.10 .. 1.18   ->  1.10 + 0.25 * 0.08 = 1.12   -> 1.12
        /// The script defines NO mutation for tiers 1-3, so an elemental caster of those tiers keeps
        /// its base weenie value, which every retail elemental caster ships as exactly 1.0 (verified:
        /// wcid 29262 wandfire, 43381 nethersceptre, 37224 acidstaff all carry ElementalDamageMod 1).
        /// Tiers 1-3 therefore attune to 1.00 - an alignment with no damage bonus, matching a vendor
        /// elemental caster of the same tier.
        ///
        /// USER RULING 2026-07-26: tiers 1-3 stay at the faithful 1.00 and a floor was explicitly
        /// DECLINED. Flooring them at the tier 4 value would make a manufactured tier-1 orb strictly
        /// beat every natural tier-1 elemental caster, inverting the natural-beats-manufactured rule
        /// at exactly the starter band. Do not "fix" these three entries; a low-tier prism's value is
        /// the alignment itself plus enabling the companion Prismatic Drift Stone's rend.
        ///
        /// AttunedDriftPrismTests re-derives every one of these numbers from the parsed script and
        /// fails if the script and this table ever drift apart.
        /// </summary>
        public static readonly double[] ElementalDamageModByTier =
        {
            1.00,   // tier 1 - no mutation block in caster_elemental.txt; base weenie value. Floor DECLINED, see above
            1.00,   // tier 2 - as above
            1.00,   // tier 3 - as above
            1.02,   // tier 4
            1.03,   // tier 5
            1.04,   // tier 6
            1.11,   // tier 7
            1.12,   // tier 8
        };

        public const int MinTier = 1;
        public const int MaxTier = 8;

        /// <summary>
        /// The percentile of the natural roll band an attunement lands on. Fixed, never rolled.
        /// </summary>
        public const decimal AttunementPercentile = 0.25m;

        /// <summary>
        /// The loot tier of a caster, inverted from the wield requirement its lootgen mutation script
        /// would have written.
        ///
        /// SOURCE TABLES: Casters/caster_elemental.txt (the RawSkill ladder) and
        /// Casters/caster_non_elemental.txt (the Level ladder) - the only two scripts
        /// LootGenerationFactory_Caster.MutateCaster ever runs. Neither ladder is injective (290
        /// appears at tiers 4-6, 355 at tiers 6-8), so the value returned is the LOWEST tier whose
        /// mutation block can produce that (WieldRequirements, WieldDifficulty) pair - the
        /// conservative reading. Anything the ladders cannot produce, including no wield requirement
        /// at all (starter and vendor orbs, 60 of the 93 orbs in ace_world), is tier 1.
        ///
        /// Elemental ladder (RawSkill): 290 -> t4, 310 -> t5, 330 -> t6, 355 -> t6, 375 -> t7, 385 -> t8.
        /// Non-elemental ladder (Level): 150 -> t7, 180 -> t8.
        ///
        /// WieldRequirement.Skill is read on the same ladder as RawSkill. The mutation scripts only
        /// ever emit RawSkill, but hand-authored world content uses Skill for the identical
        /// skill-value gate (e.g. ace_world orbs at Skill / WarMagic / 330), and both are the same
        /// magnitude of requirement. This is an explicit extension of the ladder, not part of it.
        /// </summary>
        public static int GetCasterTier(WorldObject wo)
        {
            var difficulty = wo.WieldDifficulty ?? 0;

            switch (wo.WieldRequirements)
            {
                case WieldRequirement.RawSkill:
                case WieldRequirement.Skill:

                    if (difficulty >= 385) return 8;
                    if (difficulty >= 375) return 7;
                    if (difficulty >= 330) return 6;   // covers both the 330 and 355 rungs
                    if (difficulty >= 310) return 5;
                    if (difficulty >= 290) return 4;
                    return MinTier;

                case WieldRequirement.Level:

                    if (difficulty >= 180) return 8;
                    if (difficulty >= 150) return 7;
                    return MinTier;
            }

            return MinTier;
        }

        /// <summary>
        /// The ElementalDamageMod this orb will be attuned to.
        /// </summary>
        public static double GetAttunementMod(WorldObject orb)
        {
            var tier = Math.Clamp(GetCasterTier(orb), MinTier, MaxTier);

            return ElementalDamageModByTier[tier - 1];
        }

        /// <summary>
        /// True if the orb already carries an elemental alignment, natural or from a prior prism.
        /// </summary>
        public static bool IsAlreadyAttuned(WorldObject orb)
        {
            return orb.W_DamageType != DamageType.Undef || orb.ElementalDamageMod != null;
        }

        // ------------------------------------------------------------------
        // use flow
        // ------------------------------------------------------------------

        /// <summary>
        /// The player uses an Attuned Drift Prism on an orb in their pack.
        /// </summary>
        public static void UseObjectOnTarget(Player player, WorldObject source, WorldObject target, bool confirmed = false)
        {
            if (player.IsBusy || player.Teleporting || player.suicideInProgress)
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

            var useError = VerifyUseRequirements(player, source, target);
            if (useError != WeenieError.None)
            {
                player.SendUseDoneEvent(useError);
                return;
            }

            // attunement is permanent and one-way, so it is confirmed before anything is spent.
            // Replay safety: the callback re-enters this same method with confirmed = true and
            // re-runs every check, so a stale confirmation cannot apply to a changed world.
            if (!confirmed)
            {
                TryGetElement(source.WeenieClassId, out var previewElement);

                var prompt = $"Align {target.Name} to {DescribeElement(previewElement)} damage?\n\n"
                           + $"This is permanent and cannot be undone or changed. The {source.Name} is consumed.";

                if (!player.ConfirmationManager.EnqueueSend(new Confirmation_Custom(player.Guid, () => UseObjectOnTarget(player, source, target, true)), prompt))
                    player.SendUseDoneEvent(WeenieError.ConfirmationInProgress);

                return;
            }

            var motionCommand = MotionCommand.ClapHands;

            var actionChain = new ActionChain();
            var nextUseTime = 0.0f;

            player.IsBusy = true;

            if (allowCraftInCombat && player.CombatMode != CombatMode.NonCombat)
            {
                var stanceTime = player.SetCombatMode(CombatMode.NonCombat);
                actionChain.AddDelaySeconds(stanceTime);

                nextUseTime += stanceTime;
            }

            var currentStance = player.CurrentMotionState.Stance;
            var clapTime = Physics.Animation.MotionTable.GetAnimationLength(player.MotionTableId, currentStance, motionCommand);

            actionChain.AddAction(player, () => player.SendMotionAsCommands(motionCommand, currentStance));
            actionChain.AddDelaySeconds(clapTime);

            nextUseTime += clapTime;

            actionChain.AddAction(player, () =>
            {
                // re-verify: the pack can change during the animation
                var postError = VerifyUseRequirements(player, source, target);
                if (postError != WeenieError.None)
                {
                    player.SendUseDoneEvent(postError);
                    return;
                }

                Attune(player, source, target);
            });

            actionChain.AddAction(player, () => player.IsBusy = false);

            actionChain.EnqueueChain();

            player.NextUseTime = DateTime.UtcNow.AddSeconds(nextUseTime);
        }

        public static WeenieError VerifyUseRequirements(Player player, WorldObject source, WorldObject target)
        {
            if (source == target)
            {
                player.SendTransientError($"You can't use the {source.Name} on itself.");
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            if (!IsPrism(source.WeenieClassId))
                return WeenieError.YouDoNotPassCraftingRequirements;

            // both must be loose in the pack - an equipped orb is refused rather than silently unwielded
            if (player.FindObject(source.Guid.Full, Player.SearchLocations.MyInventory) == null)
            {
                player.SendTransientError($"Cannot find the {source.Name}.");
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            if (player.FindObject(target.Guid.Full, Player.SearchLocations.MyInventory) == null)
            {
                if (player.FindObject(target.Guid.Full, Player.SearchLocations.MyEquippedItems) != null)
                    player.SendTransientError($"You must unequip the {target.Name} before attuning it.");
                else
                    player.SendTransientError($"The {target.Name} must be in your pack.");

                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            if (!IsOrb(target))
            {
                player.SendTransientError($"The {source.Name} only answers to an orb.");
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            if (IsAlreadyAttuned(target))
            {
                player.SendTransientError($"The {target.Name} is already aligned to an element. An alignment cannot be changed.");
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            // defensive: a tinkered or imbued orb would leave the "clean afterward" guarantee unprovable
            if (target.NumTimesTinkered > 0 || target.ImbuedEffect != ImbuedEffectType.Undef)
            {
                player.SendTransientError($"The {target.Name} has already been worked on and will not take the alignment.");
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            return WeenieError.None;
        }

        /// <summary>
        /// Applies the alignment. Writes exactly three properties - the same three that separate a
        /// naturally elemental caster from a plain one - and nothing else. No tinker count, no
        /// tinker log, no imbue, no retained flag, and no change to the wield requirement (which
        /// would re-gate an orb the player can already hold).
        /// </summary>
        private static void Attune(Player player, WorldObject source, WorldObject target)
        {
            if (!TryGetElement(source.WeenieClassId, out var element))
            {
                player.SendUseDoneEvent(WeenieError.CraftGeneralErrorNoUiMsg);
                return;
            }

            var mod = GetAttunementMod(target);

            player.UpdateProperty(target, PropertyInt.DamageType, (int)element);
            player.UpdateProperty(target, PropertyFloat.ElementalDamageMod, mod);

            // the element glow is additive - a magical orb keeps its Magical flag
            var glow = ElementGlow[element];
            player.UpdateProperty(target, PropertyInt.UiEffects, (int)((target.UiEffects ?? UiEffects.Undef) | glow));

            target.SaveBiotaToDatabase();

            player.Session.Network.EnqueueSend(new GameMessageUpdateObject(target));

            player.TryConsumeFromInventoryWithNetworking(source, 1);

            player.Session.Network.EnqueueSend(new GameMessageSystemChat(
                $"The prism dissolves. The {target.Name} hums with {ElementPhrase[element]}.", ChatMessageType.Craft));

            player.SendUseDoneEvent();
        }

        private static string DescribeElement(DamageType element)
        {
            switch (element)
            {
                case DamageType.Fire:     return "flame";
                case DamageType.Cold:     return "frost";
                case DamageType.Acid:     return "acid";
                case DamageType.Electric: return "lightning";
                case DamageType.Slash:    return "slashing";
                case DamageType.Pierce:   return "piercing";
                case DamageType.Bludgeon: return "bludgeoning";
                case DamageType.Nether:   return "void";
            }
            return "unknown";
        }
    }
}
