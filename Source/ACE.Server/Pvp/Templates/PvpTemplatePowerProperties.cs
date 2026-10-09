using System.Collections.Generic;

using ACE.Entity.Enum.Properties;

namespace ACE.Server.Pvp.Templates
{
    /// <summary>
    /// The power-source table of Docs/Pvp/TEMPLATES.md, as data. Every PropertyInt the overlay REPLACES is
    /// listed in <see cref="ReplacedInts"/>, and nothing outside it is ever written by a template apply or
    /// restore. Attributes, vitals, skills, enchantments and the spellbook are replaced through their own
    /// tables (see PvpTemplateOverlay); this class only governs the PropertyInt rows.
    ///
    /// THE TABLE, ROW BY ROW (TEMPLATES.md "Power-source table"):
    ///   - Retail augmentations (AugmentationDevice.AugProps): Replace, EXCEPT AugmentationExtraPackSlot (229)
    ///     and AugmentationIncreasedCarryingCapacity (230), which are structural (Ignore).
    ///   - AugmentationResistanceNether (327), not in AugProps: Replace.
    ///   - The family counters AugmentationInnateFamily (217), AugmentationResistanceFamily (239) and
    ///     AugmentationSpecializeGearcraft (293): Replace, which keeps the client panel consistent.
    ///   - Luminance augmentations 333-345 and 365: Replace.
    ///   - Fork custom augs: 9064 (AugmentationSpellDurationCustom) Replace; 9063 (AugmentationPickupSpeed)
    ///     Existing (the arena pick-up mask already covers it); 9062 (AugmentationMuleSpace) Ignore (vault space).
    ///   - Base ratings on the player: 307, 308, 313-317, 323, 350, 351, 381, 382: Replace.
    ///
    /// AugProps is NOT read at runtime to build this set, deliberately: a future custom augmentation added to
    /// AugProps must be classified against the table by a person, not silently folded into the overlay.
    /// PvpTemplateCoreTests pins every AugProps entry to exactly one of <see cref="ReplacedInts"/> or
    /// <see cref="IgnoredAugProps"/>, so an unclassified addition fails a test instead.
    ///
    /// NEVER IN THIS SET, and a test proves it: XP, skill credits, class ability points, AvailableExperience,
    /// TotalExperience or any other spendable pool. The six Critical defects of the original facet build all
    /// came from that accounting (TEMPLATES.md "Model").
    /// </summary>
    public static class PvpTemplatePowerProperties
    {
        public static readonly IReadOnlyCollection<PropertyInt> ReplacedInts = new HashSet<PropertyInt>
        {
            // ---- retail augmentations (AugProps, minus the two structural ones) ----
            PropertyInt.AugmentationInnateStrength,              // 218
            PropertyInt.AugmentationInnateEndurance,             // 219
            PropertyInt.AugmentationInnateCoordination,          // 220
            PropertyInt.AugmentationInnateQuickness,             // 221
            PropertyInt.AugmentationInnateFocus,                 // 222
            PropertyInt.AugmentationInnateSelf,                  // 223
            PropertyInt.AugmentationSpecializeSalvaging,         // 224
            PropertyInt.AugmentationSpecializeItemTinkering,     // 225
            PropertyInt.AugmentationSpecializeArmorTinkering,    // 226
            PropertyInt.AugmentationSpecializeMagicItemTinkering,// 227
            PropertyInt.AugmentationSpecializeWeaponTinkering,   // 228
            PropertyInt.AugmentationLessDeathItemLoss,           // 231
            PropertyInt.AugmentationSpellsRemainPastDeath,       // 232
            PropertyInt.AugmentationCriticalDefense,             // 233
            PropertyInt.AugmentationBonusXp,                     // 234
            PropertyInt.AugmentationBonusSalvage,                // 235
            PropertyInt.AugmentationBonusImbueChance,            // 236
            PropertyInt.AugmentationFasterRegen,                 // 237
            PropertyInt.AugmentationIncreasedSpellDuration,      // 238
            PropertyInt.AugmentationResistanceSlash,             // 240
            PropertyInt.AugmentationResistancePierce,            // 241
            PropertyInt.AugmentationResistanceBlunt,             // 242
            PropertyInt.AugmentationResistanceAcid,              // 243
            PropertyInt.AugmentationResistanceFire,              // 244
            PropertyInt.AugmentationResistanceFrost,             // 245
            PropertyInt.AugmentationResistanceLightning,         // 246
            PropertyInt.AugmentationInfusedCreatureMagic,        // 294
            PropertyInt.AugmentationInfusedItemMagic,            // 295
            PropertyInt.AugmentationInfusedLifeMagic,            // 296
            PropertyInt.AugmentationInfusedWarMagic,             // 297
            PropertyInt.AugmentationCriticalExpertise,           // 298
            PropertyInt.AugmentationCriticalPower,               // 299
            PropertyInt.AugmentationSkilledMelee,                // 300
            PropertyInt.AugmentationSkilledMissile,              // 301
            PropertyInt.AugmentationSkilledMagic,                // 302
            PropertyInt.AugmentationDamageBonus,                 // 309
            PropertyInt.AugmentationDamageReduction,             // 310
            PropertyInt.AugmentationJackOfAllTrades,             // 326
            PropertyInt.AugmentationInfusedVoidMagic,            // 328

            // ---- not in AugProps ----
            PropertyInt.AugmentationResistanceNether,            // 327

            // ---- family counters ----
            PropertyInt.AugmentationInnateFamily,                // 217
            PropertyInt.AugmentationResistanceFamily,            // 239
            PropertyInt.AugmentationSpecializeGearcraft,         // 293

            // ---- luminance augmentations ----
            PropertyInt.LumAugDamageRating,                      // 333
            PropertyInt.LumAugDamageReductionRating,             // 334
            PropertyInt.LumAugCritDamageRating,                  // 335
            PropertyInt.LumAugCritReductionRating,               // 336
            PropertyInt.LumAugSurgeEffectRating,                 // 337
            PropertyInt.LumAugSurgeChanceRating,                 // 338
            PropertyInt.LumAugItemManaUsage,                     // 339
            PropertyInt.LumAugItemManaGain,                      // 340
            PropertyInt.LumAugVitality,                          // 341
            PropertyInt.LumAugHealingRating,                     // 342
            PropertyInt.LumAugSkilledCraft,                      // 343
            PropertyInt.LumAugSkilledSpec,                       // 344
            PropertyInt.LumAugNoDestroyCraft,                    // 345
            PropertyInt.LumAugAllSkills,                         // 365

            // ---- fork custom augs ----
            PropertyInt.AugmentationSpellDurationCustom,         // 9064

            // ---- base ratings on the player ----
            PropertyInt.DamageRating,                            // 307
            PropertyInt.DamageResistRating,                      // 308
            PropertyInt.CritRating,                              // 313
            PropertyInt.CritDamageRating,                        // 314
            PropertyInt.CritResistRating,                        // 315
            PropertyInt.CritDamageResistRating,                  // 316
            PropertyInt.HealingResistRating,                     // 317
            PropertyInt.HealingBoostRating,                      // 323
            PropertyInt.DotResistRating,                         // 350
            PropertyInt.LifeResistRating,                        // 351
            PropertyInt.PKDamageRating,                          // 381
            PropertyInt.PKDamageResistRating,                    // 382
        };

        /// <summary>
        /// The AugProps entries the table classifies as NOT replaced: 229/230 Ignore (structural), 9062 Ignore
        /// (vault space), 9063 Existing (the arena pick-up mask). Exists so a test can prove every AugProps entry
        /// is classified exactly once.
        /// </summary>
        public static readonly IReadOnlyCollection<PropertyInt> IgnoredAugProps = new HashSet<PropertyInt>
        {
            PropertyInt.AugmentationExtraPackSlot,               // 229
            PropertyInt.AugmentationIncreasedCarryingCapacity,   // 230
            PropertyInt.AugmentationMuleSpace,                   // 9062
            PropertyInt.AugmentationPickupSpeed,                 // 9063
        };

        /// <summary>
        /// The spendable pools and progression ledgers the overlay must NEVER write. Not consulted by the overlay
        /// (it writes only <see cref="ReplacedInts"/>); a test asserts the two sets are disjoint and that a round
        /// trip leaves every one of these byte-identical.
        /// </summary>
        public static readonly IReadOnlyCollection<PropertyInt> NeverWrittenInts = new HashSet<PropertyInt>
        {
            PropertyInt.AvailableSkillCredits,
            PropertyInt.TotalSkillCredits,
            PropertyInt.AvailableClassAbilityPoints,
            PropertyInt.TotalClassAbilityPointsEarned,
            PropertyInt.Level,
            PropertyInt.Enlightenment,
        };

        public static readonly IReadOnlyCollection<PropertyInt64> NeverWrittenInt64s = new HashSet<PropertyInt64>
        {
            PropertyInt64.AvailableExperience,
            PropertyInt64.TotalExperience,
            PropertyInt64.AvailableLuminance,
            PropertyInt64.MaximumLuminance,
        };
    }
}
