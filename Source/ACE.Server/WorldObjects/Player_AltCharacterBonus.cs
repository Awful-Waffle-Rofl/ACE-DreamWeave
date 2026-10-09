using System;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        /// <summary>
        /// The "alt character bonus" gives a catch-up boost to a character whose account already has a
        /// further-along character. While THIS character's progression (see <see cref="GetAltCharacterProgression"/>,
        /// which folds enlightenment and level together) trails the highest progression among all characters on
        /// the same account by at least alt_character_bonus_gap points, the leveling XP this character earns is
        /// scaled up by alt_character_bonus_multiplier.
        ///
        /// The boost is multiplicative with every other XP bonus - most notably it stacks on top of the offline
        /// bonus (a fresh alt with banked offline time earns 2x from the offline bonus and 2x again from this,
        /// i.e. 4x) as well as augmentation/gear XP modifiers. It applies to the character's own directly-earned
        /// leveling XP (its own kills and quest turn-ins, plus its share of a fellow's kill) and deliberately not
        /// to allegiance passup or item XP, neither of which ever takes this bonus. See Player_Xp.GrantXP for
        /// the wiring.
        ///
        /// The target the alt is catching up to is fixed for the duration of a login: only one character per
        /// account can be online at a time, so no other character on the account can gain level/enlightenment
        /// while this one is playing. The threshold is therefore computed once at login (<see cref="InitAltCharacterBonus"/>)
        /// and cached; only this character's own live progression is re-read, which is what lets the bonus switch
        /// off the instant this character levels up to the target.
        /// </summary>

        /// <summary>Highest account progression score at login - the catch-up target. 0 = no target / feature off.</summary>
        private long altCharacterBonusThreshold;

        /// <summary>Enlightenment of the furthest-along account character at login (for display).</summary>
        private int altCharacterBonusTargetEnlightenment;

        /// <summary>Level of the furthest-along account character at login (for display).</summary>
        private int altCharacterBonusTargetLevel;

        /// <summary>
        /// Computes and caches the catch-up target (the highest enlightenment+level progression among all
        /// non-deleted characters on this account, including this one) for the duration of this login. Call once
        /// at login, after the character is registered with PlayerManager. Because only one character per account
        /// is ever online, the other characters' cached values are their current persisted values, and the
        /// target cannot change until this character logs out. Including this character in the scan is harmless:
        /// it can only "win" when it is already the account's furthest-along character, in which case the bonus
        /// is inactive (self is never below itself) and nothing is displayed.
        /// </summary>
        public void InitAltCharacterBonus()
        {
            altCharacterBonusThreshold = 0;
            altCharacterBonusTargetEnlightenment = 0;
            altCharacterBonusTargetLevel = 0;

            var accountId = Account?.AccountId ?? 0;
            if (accountId == 0)
                return;

            // Use the fixed global cap (not the per-player enlightenment cap) so the cross-character
            // progression score folds enlightenment uniformly for every character on the account. A freshly
            // enlightened character therefore briefly qualifies for catch-up XP for its first ~5n levels.
            var maxLevel = EnlightenmentXpCurve.BaseMaxLevel;

            foreach (var character in PlayerManager.GetAccountPlayersSnapshot(accountId))
            {
                if (character.IsDeleted || character.IsPendingDeletion)
                    continue;

                // A mule's level 180 is granted by ApplyMuleConversion, not earned, so it is not a real
                // progression target - counting it would hand every other character on the account a
                // permanent catch-up bonus the moment one character is muled. Nothing caps how many of an
                // account's characters may be mules, so this skip has no upper bound to rely on.
                if (character.GetProperty(PropertyBool.IsMule) ?? false)
                    continue;

                var level = character.Level ?? 1;
                var enlightenment = character.GetProperty(PropertyInt.Enlightenment) ?? 0;

                var progression = AltCharacterBonus.GetProgression(level, enlightenment, maxLevel);

                if (progression > altCharacterBonusThreshold)
                {
                    altCharacterBonusThreshold = progression;
                    altCharacterBonusTargetEnlightenment = enlightenment;
                    altCharacterBonusTargetLevel = level;
                }
            }
        }

        /// <summary>
        /// This character's live progression score, folding its current enlightenment and level into one value.
        /// Read fresh every time (cheap) so the bonus cuts off the moment this character reaches the target.
        /// </summary>
        public long GetAltCharacterProgression()
        {
            return AltCharacterBonus.GetProgression(Level ?? 1, Enlightenment, EnlightenmentXpCurve.BaseMaxLevel);
        }

        /// <summary>Enlightenment of the character this alt is catching up to (0 when inactive).</summary>
        public int AltCharacterBonusTargetEnlightenment => altCharacterBonusTargetEnlightenment;

        /// <summary>Level of the character this alt is catching up to (0 when inactive).</summary>
        public int AltCharacterBonusTargetLevel => altCharacterBonusTargetLevel;

        /// <summary>
        /// TRUE if the feature is enabled and this character trails its account's login-time high-water mark by
        /// at least alt_character_bonus_gap progression points (i.e. it is far enough behind a further-along
        /// character to receive the catch-up boost). A character within that gap - or at/ahead of the mark -
        /// does not qualify.
        /// </summary>
        public bool IsAltCharacterBonusActive
        {
            get
            {
                if (!PropertyManager.GetBool("alt_character_bonus_enabled").Item)
                    return false;

                var gap = PropertyManager.GetLong("alt_character_bonus_gap").Item;

                return AltCharacterBonus.IsBelow(GetAltCharacterProgression(), altCharacterBonusThreshold, gap);
            }
        }

        /// <summary>
        /// Applies the alt character bonus multiplier to an amount of leveling XP landing on this character,
        /// if the feature is enabled and this character is an under-leveled alt. Returns the amount unchanged
        /// otherwise. The caller is responsible for only passing this character's own directly-earned leveling
        /// XP (its own kill/quest, or its share of a fellow's kill), never allegiance passup or item XP (see
        /// the caller in GrantXP).
        /// </summary>
        public long ApplyAltCharacterBonus(long amount)
        {
            if (amount <= 0 || !IsAltCharacterBonusActive)
                return amount;

            var multiplier = PropertyManager.GetDouble("alt_character_bonus_multiplier").Item;

            return AltCharacterBonus.Apply(amount, multiplier);
        }

        /// <summary>
        /// The alt character bonus line for the bank balance listing (/b). "none" when inactive, otherwise
        /// "until Enl #, Level #" naming the target this character is catching up to.
        /// </summary>
        public string AltCharacterBonusDisplay =>
            IsAltCharacterBonusActive ? $"until Enl {altCharacterBonusTargetEnlightenment}, Level {altCharacterBonusTargetLevel}" : "none";

        /// <summary>
        /// Sends a chat summary of the character's current alt character bonus status.
        /// </summary>
        public void ShowAltCharacterBonusStatus()
        {
            if (!PropertyManager.GetBool("alt_character_bonus_enabled").Item)
            {
                Session.Network.EnqueueSend(new GameMessageSystemChat("The alt character bonus is not enabled on this server.", ChatMessageType.Broadcast));
                return;
            }

            if (!IsAltCharacterBonusActive)
            {
                Session.Network.EnqueueSend(new GameMessageSystemChat("You have no alt character bonus - you are not far enough behind the furthest-along character on your account.", ChatMessageType.Broadcast));
                return;
            }

            var percent = (int)Math.Round(PropertyManager.GetDouble("alt_character_bonus_multiplier").Item * 100);

            Session.Network.EnqueueSend(new GameMessageSystemChat($"Alternate Character Bonus: +{percent}% experience until Enl {altCharacterBonusTargetEnlightenment}, Level {altCharacterBonusTargetLevel}.", ChatMessageType.Broadcast));
        }
    }
}
