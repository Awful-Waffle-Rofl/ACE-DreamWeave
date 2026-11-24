using System.Collections.Generic;

using log4net;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity
{
    /// <summary>
    /// Marketplace "Tinkerer's Inspiration" buff - a free, no-cooldown pedestal prop (wcid 1002761) and its hookable inventory twin (wcid 1002770).
    /// Use-dispatched from <see cref="WorldObjects.GenericObject.ActOnUse"/>, gated on
    /// <see cref="PropertyBool.TinkerersInspiration"/> (9037) being true on the station.
    /// Also dispatched from <see cref="WorldObjects.Hooker.ActOnUse"/> for the hookable variant (wcid 1002770),
    /// a type-64 Hooker that can hang on a house floor or yard hook and be used there.
    ///
    /// On use, it lays down five buffs on the player for 60 seconds: retail Brilliance (2348, +50 Focus, value
    /// untouched - only the duration is ours) and the four level-8 "Incantation of ... Tinkering Expertise Self"
    /// spells (4512/4640/4566/4592) raised from +45 to +60, rendered on the enchantment bar under their REAL
    /// retail spell ids/icons/categories rather than a synthetic class-ability category.
    /// That is deliberate: it is meant to read as "a stronger version of a spell you already know", not a
    /// separate buff track.
    ///
    /// Using the real spell ids and categories means the normal top-layer-by-power-level selection
    /// (<see cref="Managers.PropertiesEnchantmentRegistryExtensions.GetEnchantmentsTopLayerByStatModType"/>)
    /// applies against whatever the player already has running in that same category - most commonly a
    /// player's own 2-hour self-cast Incantation (+45) in the same category. Two things follow from that:
    ///  - <c>spell.Power + 1</c> is passed as the PowerLevel so the pedestal's entry always outranks a real
    ///    cast in the same category while it lasts.
    ///  - <see cref="Managers.EnchantmentManager.AddClassAbilityDebuff"/> is called with
    ///    <c>refreshOnlyOwnCaster: true</c> and the STATION as caster, so the refresh-lookup only ever
    ///    matches a previous application of Tinkerer's Inspiration (caster = this station), never the
    ///    player's own cast (caster = the player). The player's own entry is therefore never touched: it
    ///    keeps ticking its own clock as an independent layer for the same (category, spell id), and simply
    ///    resumes being the visible top layer once our higher-power entry expires.
    ///
    /// That last part holds on the SERVER only by itself. The client keeps one timed entry per spell id, so our
    /// layer overwrites the player's own cast there (CEnchantmentRegistry::AttemptToReplaceSpellInList), and when
    /// ours expires the client would be left with nothing for the spell. <see cref="Managers.EnchantmentManager.Remove"/>
    /// re-sends the surviving timed layer after every removal to cover that
    /// (<see cref="Managers.EnchantmentManager.GetTimedSurvivorToResend"/>), so the player's own cast reappears when
    /// the minute is up.
    /// </summary>
    public static class TinkerersInspiration
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// (spell id, magnitude) pairs. A null magnitude means "apply the spell at its own retail value";
        /// only the duration is changed.
        /// </summary>
        private static readonly (uint SpellId, float? Magnitude)[] Buffs =
        {
            (2348, null), // Brilliance - retail +50 Focus, untouched; only the duration is ours
            (4512, 60f),  // Incantation of Armor Tinkering Expertise Self - retail +45, raised to +60
            (4640, 60f),  // Incantation of Weapon Tinkering Expertise Self
            (4566, 60f),  // Incantation of Item Tinkering Expertise Self
            (4592, 60f),  // Incantation of Magic Item Tinkering Expertise Self
        };

        private const double DurationSeconds = 60.0;

        /// <summary>
        /// If <paramref name="station"/> is flagged as a Tinkerer's Inspiration pedestal, applies the five
        /// buffs to <paramref name="player"/> and returns true; otherwise returns false so normal Generic use
        /// behavior is left untouched.
        /// </summary>
        public static bool TryHandleUse(WorldObject station, Player player)
        {
            if (station == null || player == null)
                return false;

            if (station.GetProperty(PropertyBool.TinkerersInspiration) != true)
                return false;

            // the chat line names every buff actually applied, so players can tell what this stacks with
            var applied = new List<string>();

            foreach (var (id, magnitudeOverride) in Buffs)
            {
                var spell = new Spell(id);

                if (spell.NotFound)
                {
                    log.Warn($"TinkerersInspiration.TryHandleUse: spell id {id} not found in the spell table, skipping");
                    continue;
                }

                var statModType = spell.StatModType | (spell.IsBeneficial ? EnchantmentTypeFlags.Beneficial : 0);
                var magnitude = magnitudeOverride ?? spell.StatModVal;

                player.EnchantmentManager.AddClassAbilityDebuff(spell.Id, spell.Power + 1, station, spell.Category, statModType, spell.StatModKey, magnitude, DurationSeconds, refreshOnlyOwnCaster: true);

                applied.Add($"{spell.Name} (+{magnitude:0})");
            }

            player.EnqueueBroadcast(new GameMessageScript(player.Guid, PlayScript.SkillUpBlue));

            player.Session.Network.EnqueueSend(new GameMessageSystemChat(
                $"Inspiration sharpens your hands and your mind for one minute: {string.Join(", ", applied)}.",
                ChatMessageType.Craft));

            return true;
        }
    }
}
