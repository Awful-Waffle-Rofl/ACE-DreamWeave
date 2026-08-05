/* Retune augmentation gem cooldown timers.
   These template quests ("blank" is substituted with the actual augmentation
   name at runtime) gate how often a player can re-acquire an augmentation.
   - AugmentationBlankGemAcquired: 6d (518400) -> 20h (72000)
   - BlankAugLuminanceTimer_0511:  14d (1209600) -> 6d 20h (590400)
   Idempotent: sets fixed min_Delta values, safe to re-run. */

UPDATE `quest` SET `min_Delta` = 72000  WHERE `name` = 'AugmentationBlankGemAcquired';
UPDATE `quest` SET `min_Delta` = 590400 WHERE `name` = 'BlankAugLuminanceTimer_0511';
