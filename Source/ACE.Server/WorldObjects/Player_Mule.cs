using System;
using System.Collections.Generic;

using ACE.Database.Models.Shard;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Command.Handlers;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Realms;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// Mule system (WaffleACE). A mule is an irreversible, storage-and-trade-only character: it carries a huge
    /// amount, can chat, trade, use vendors, bank and take portals, and is barred from every progression and
    /// combat path. There is no un-mule path anywhere in the fork.
    ///
    /// CARRY CAPACITY IS BOUGHT WITH STRENGTH ('mule_strength', default 3300), not with an override on
    /// <see cref="GetEncumbranceCapacity"/>. Both approaches have now been tried in game, in this order:
    ///   1. Strength 5000, no override. Worked, but drags Jump, melee damage and WeaponTinkering up with it.
    ///   2. Normal Strength plus a capacity override. Correct server-side and INVISIBLE TO THE CLIENT -
    ///      verified 2026-08-02, the burden bar stayed heavy on a mule carrying almost nothing, because the
    ///      client derives capacity from Strength itself and ignores a server-sent EncumbranceCapacity.
    ///   3. Current: Strength again, at 3300 rather than 5000, with the side effects handled directly.
    /// The client cannot be corrected, only agreed with - the skill wire format carries InitLevel + Ranks +
    /// SAC, never the server's computed Current, so the client's own predictions are authoritative for
    /// anything it derives from attributes. Do not reintroduce a capacity override.
    ///
    /// The three Strength side effects and where each is handled:
    ///   - Melee damage, and WeaponTinkering (the only tinkering skill Strength feeds, as (Focus+Str)/2):
    ///     both already unreachable, blocked by the MeleeAttack and Craft guards.
    ///   - Jump. Strength 3300 puts Jump near 1700 and a full-power flat jump within about 0.02 of the
    ///     hardcoded fall-damage threshold, so an ordinary hop would be lethal. Mules are therefore exempt
    ///     from fall damage (Player_Move.TakeDamage_Falling) rather than jump-clamped, because a clamp
    ///     cannot reach the client and would rubber-band every jump. Mules stay mortal to everything else.
    ///
    /// IMPORTANT - what 'mule_system_enabled' gates. That switch gates ONLY the conversion path
    /// (<see cref="MuleConversion.HandleMuleRequest"/> via <see cref="IsEligibleForMuleConversion(Player, out string)"/>).
    /// The restriction guards deliberately do NOT consult it: they key off the persisted
    /// <see cref="IsMule"/> flag alone. If the guards were gated on the switch, turning it off would unlock
    /// every existing mule at once - a level 180 character with no combat restrictions and an unbounded pack.
    /// Turning the switch off must only stop NEW conversions.
    /// </summary>
    partial class Player
    {
        /// <summary>
        /// TRUE if this character has been converted to a mule. Irreversible in practice - nothing in the fork
        /// clears it. Follows the RemoveProperty-on-false idiom used by IsAdmin so a normal character never
        /// carries a row for it.
        /// </summary>
        public bool IsMule
        {
            get => GetProperty(PropertyBool.IsMule) ?? false;
            set { if (!value) RemoveProperty(PropertyBool.IsMule); else SetProperty(PropertyBool.IsMule, value); }
        }

        /// <summary>
        /// The single refusal gate for every mule restriction. Returns FALSE immediately for a non-mule, so a
        /// guard call costs one property lookup on the normal path.
        /// </summary>
        /// <param name="notify">
        /// FALSE suppresses the chat refusal, for guard sites that can fire in bulk (emote scripts calling
        /// QuestManager) or that already surface their own error string to the player.
        /// </param>
        /// <returns>TRUE when the action must be refused.</returns>
        public bool MuleBlocked(MuleAction action, bool notify = true)
        {
            if (!IsMule)
                return false;

            if (notify && Session != null)
                Session.Network.EnqueueSend(new GameMessageSystemChat(GetMuleRefusal(action), ChatMessageType.Broadcast));

            return true;
        }

        /// <summary>
        /// Short, action-specific refusal text. Kept well under the client's chat line budget.
        /// </summary>
        private static string GetMuleRefusal(MuleAction action)
        {
            switch (action)
            {
                case MuleAction.GainExperience:    return "A mule cannot earn experience.";
                case MuleAction.GainLuminance:     return "A mule cannot earn luminance.";
                case MuleAction.AdvanceQuest:      return "A mule cannot make quest progress.";
                case MuleAction.TrainClassAbility: return "A mule cannot learn class abilities.";
                case MuleAction.GainClassAbilityPoints: return "A mule cannot earn class ability points.";
                case MuleAction.Craft:             return "A mule cannot craft or tinker.";
                case MuleAction.Salvage:           return "A mule cannot salvage.";
                case MuleAction.MeleeAttack:
                case MuleAction.MissileAttack:     return "A mule cannot fight.";
                case MuleAction.CastSpell:         return "A mule cannot cast spells.";
                case MuleAction.JoinFellowship:    return "A mule cannot join a fellowship.";
                case MuleAction.ChangePkStatus:    return "A mule cannot change its player killer status.";
                case MuleAction.AcceptContract:    return "A mule cannot accept contracts.";
                case MuleAction.GainTitle:         return "A mule cannot earn titles.";
                case MuleAction.BuyHouse:          return "A mule cannot buy a dwelling.";
                case MuleAction.StartChallenge:    return "A mule cannot enter a challenge.";
                case MuleAction.ChangeFacet:       return "A mule cannot change facets.";
                default:                           return "A mule cannot do that.";
            }
        }

        /// <summary>
        /// Precondition check for mule conversion, gathering this character's state and handing it to the pure
        /// <see cref="IsEligibleForMule"/> below. Re-run at execution time, never trusted from an earlier call.
        /// </summary>
        public static bool IsEligibleForMuleConversion(Player player, out string reason)
        {
            reason = null;

            if (player == null)
            {
                reason = "No character to convert.";
                return false;
            }

            return IsEligibleForMule(
                PropertyManager.GetBool("mule_system_enabled").Item,
                player.IsMule,
                player.Level ?? 1,
                PropertyManager.GetLong("mule_max_convert_level").Item,
                out reason);
        }

        /// <summary>
        /// Pure eligibility rules, extracted from <see cref="IsEligibleForMuleConversion(Player, out string)"/>
        /// so they are unit-testable without constructing a Player (whose static type initializer needs a live
        /// world database). Same extraction rationale as Player.CalcPayoutStackSizes.
        /// Reason explains the FIRST failure, in player-facing language.
        ///
        /// LEVEL IS THE ONLY PROGRESS GATE, deliberately. Three narrower checks were tried and removed:
        ///   - TotalExperience == 0. Subsumed by the level cap, and mutually exclusive with it: a character
        ///     is level 8+ the moment it leaves the training hall, so any cap above 1 makes this unsatisfiable.
        ///   - questRowCount == 0. UNSATISFIABLE BY ANY CHARACTER, not merely restrictive. Leaving the
        ///     training hall stamps at least one quest, and independently of that Player_QuestStamps'
        ///     SeedQuestStampLedger writes a LedgerSeededMarker row into the same registry on first login -
        ///     unconditionally, for every character. Counting registry rows therefore counts bookkeeping as
        ///     progress and refuses everyone, including a brand new level 1. Do not reintroduce this without
        ///     excluding the ledger rows, and prefer not to reintroduce it at all.
        ///   - classAbilityRankCount == 0. A character inside the level cap can legitimately hold ranks, and
        ///     they are inert on a mule (every path that would use them is guarded), so it only refused
        ///     otherwise-valid conversions.
        /// The level cap is what stops a developed character being laundered into an unkillable vault; it is
        /// not a security boundary, and conversion is irreversible either way.
        ///
        /// THERE IS NO PER-ACCOUNT MULE LIMIT. One was tried ('mule_max_per_account', default 2) and removed
        /// on 2026-08-31: it was read as a cap on how many characters an account could convert, when the only
        /// limit that actually constrains play is the separate simultaneous-login rule (IpLimitManager, whose
        /// 'mule_landblocks' key is unrelated to this system). Converting an extra character costs the account
        /// a character slot and is irreversible, and a mule can do nothing but carry, so the count is not worth
        /// gating. 'mule_system_enabled' remains the only switch that stops new conversions.
        /// </summary>
        public static bool IsEligibleForMule(bool systemEnabled, bool alreadyMule, int level, long maxConvertLevel,
            out string reason)
        {
            reason = null;

            if (!systemEnabled)
            {
                reason = "Mule conversion is not available on this world.";
                return false;
            }

            if (alreadyMule)
            {
                reason = "This character is already a mule.";
                return false;
            }

            if (level > maxConvertLevel)
            {
                reason = $"Only a character of level {maxConvertLevel} or below can become a mule. This one is level {level}.";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Applies the whole mule stat package. Callers MUST have just re-run
        /// <see cref="IsEligibleForMuleConversion(Player, out string)"/> - this method does not re-check.
        /// </summary>
        public void ApplyMuleConversion()
        {
            // 1. flag FIRST, so any guard that fires part way through the rest of this method already sees it
            IsMule = true;

            // 2. level, and the matching total XP. Both come off EnlightenmentXpCurve, the same table
            //    CheckForLevelup compares TotalExperience against, so Level and TotalExperience cannot
            //    desync and 'verify-experience' has nothing to reconcile. GetTotalXPRequiredForLevel is the
            //    bounds-checked accessor over ExtendedTotals (clamps negatives and past-end indexes).
            var muleLevel = (int)Math.Clamp(PropertyManager.GetLong("mule_level").Item, 1,
                EnlightenmentXpCurve.ExtendedTotals.Count - 1);

            Level = muleLevel;
            TotalExperience = (long)EnlightenmentXpCurve.GetTotalXPRequiredForLevel(muleLevel);
            AvailableExperience = 0;

            // 3. Strength. This is what buys the carrying capacity: GetEncumbranceCapacity is the retail
            //    150 * Strength formula and carries no mule override, because an override is invisible to
            //    the client (see that method, and the class summary). Only Strength is raised - Jump reads
            //    (Strength + Coordination) / 2, so raising Coordination as well would push the jump higher
            //    for no benefit.
            var muleStrength = (uint)Math.Clamp(PropertyManager.GetLong("mule_strength").Item, 1, 9999);
            Attributes[PropertyAttribute.Strength].StartingValue = muleStrength;

            Session?.Network.EnqueueSend(new GameMessagePrivateUpdateAttribute(this, Attributes[PropertyAttribute.Strength]));

            // 4. Untrain everything the engine permits, and zero skill credits. The six AlwaysTrained skills
            //    (ArcaneLore, Jump, Loyalty, MagicDefense, Run, Salvaging) cannot be untrained - the engine's
            //    own IsSkillUntrainable refuses - so they are skipped rather than fought. Harmless now that
            //    attributes stay normal: an AlwaysTrained skill on a mule is just a normal character's skill.
            //    This whole step is defence in depth. The live guards (MuleBlocked call sites) are the control.
            var skillUpdates = new List<GameMessage>();

            foreach (var skill in PlayerSkills)
            {
                if (!IsSkillUntrainable(skill))
                    continue;

                var creatureSkill = GetCreatureSkill(skill, false);

                if (creatureSkill == null || creatureSkill.AdvancementClass < SkillAdvancementClass.Trained)
                    continue;

                creatureSkill.AdvancementClass = SkillAdvancementClass.Untrained;
                creatureSkill.InitLevel = 0;
                creatureSkill.Ranks = 0;
                creatureSkill.ExperienceSpent = 0;

                skillUpdates.Add(new GameMessagePrivateUpdateSkill(this, creatureSkill));
            }

            AvailableSkillCredits = 0;
            TotalSkillCredits = 0;

            // 5. top the character up. Attributes are unchanged, so the maximums are unchanged too - this is
            //    just a courtesy heal, not a rebuild.
            SetMaxVitals();

            // 6. refresh the client so the character sheet is right without a relog. The Strength update is
            //    sent above with step 3, and the client recomputes capacity and the burden bar from it -
            //    there is deliberately no EncumbranceCapacity message here, because the client ignores one
            //    (see GetEncumbranceCapacity).
            if (Session != null)
            {
                Session.Network.EnqueueSend(
                    new GameMessagePrivateUpdatePropertyInt(this, PropertyInt.Level, Level ?? 1),
                    new GameMessagePrivateUpdatePropertyInt64(this, PropertyInt64.TotalExperience, TotalExperience ?? 0),
                    new GameMessagePrivateUpdatePropertyInt64(this, PropertyInt64.AvailableExperience, 0),
                    new GameMessagePrivateUpdatePropertyInt(this, PropertyInt.AvailableSkillCredits, 0),
                    new GameMessagePrivateUpdatePropertyInt(this, PropertyInt.TotalSkillCredits, 0));

                if (skillUpdates.Count > 0)
                    Session.Network.EnqueueSend(skillUpdates.ToArray());
            }

            // 7. bind to the mule lifestone, mirroring LifeStone.ActOnUse (player.Sanctuary = position, plus
            //    the same client message a real lifestone /use sends). 'mule_bind_position' is a config loc
            //    string (cell x y z qw qx qy qz, same token order as /teleloc) rather than a hardcoded
            //    coordinate, parsed via AdminCommands.TryParseLocPosition so this doesn't duplicate that
            //    parsing logic. A malformed config string must never break the conversion itself - log a
            //    warning and skip the bind rather than throwing.
            //    An optional trailing "@R" token names the realm (RealmLocString): the Marketplace lifestone
            //    lives in realm 1's copy of Aerfalle Keep, so the bind must land in realm 1's default instance
            //    whatever instance the converting player is standing in. Without the token the bind keeps the
            //    player's current instance, as it always did. An unregistered realm skips the bind (logged
            //    below) rather than binding into some other copy of the landblock.
            var bindPositionString = PropertyManager.GetString("mule_bind_position").Item;

            var bindParsed = RealmLocString.TryParse(bindPositionString, Location?.Instance ?? 0, RealmManager.GetRealm,
                out var bindPosition, out var bindError, out _);

            if (bindParsed)
            {
                Sanctuary = bindPosition;

                // verified 2026-08-02 against ace_world: wcid 509 (lifestone)'s PropertyString.UseMessage
                if (Session != null)
                    Session.Network.EnqueueSend(new GameMessageSystemChat(
                        "You have attuned your spirit to this Lifestone. You will resurrect here after you die.",
                        ChatMessageType.Magic));
            }
            else
            {
                log.Warn($"[MULE] {Name} (0x{Guid}) - failed to parse 'mule_bind_position' ('{bindPositionString}'): {bindError ?? "no tokens"}. Skipping lifestone bind.");
            }

            // 8. persist promptly - this is irreversible, so it must not be lost to a crash before the next
            //    scheduled save (same idiom the banking code uses)
            CharacterChangesDetected = true;
            ChangesDetected = true;
            RushNextPlayerSave(5);

            // 9. play the level-up visual - the character just jumped to level 180 in one shot, and
            //    Player_Xp.CheckForLevelup plays this same effect on an ordinary level-up (Player_Xp.cs:418).
            PlayParticleEffect(PlayScript.LevelUp, Guid);
        }
    }
}
