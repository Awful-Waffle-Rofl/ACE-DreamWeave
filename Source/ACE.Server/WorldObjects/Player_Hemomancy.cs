using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Managers;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        // ---- Hemomancy (Blood Mage T1): healing arms a short damage buff ------------------------------
        //
        // Transient, never persisted, mutated only on this player's landblock thread (every heal site and
        // every damage site that reads the buff runs there) - the same contract as the rest of the class
        // ability buff family. Allocated lazily on the first heal while the ability is OWNED, so a player
        // without Hemomancy carries no state and is never sent a message.
        //
        // The rules (threshold, leech window, refresh-not-stack) live in ClassAbilities/HemomancyMath.cs;
        // the damage multiplier, including its Healing affinity call, lives in HemomancyAbility so the
        // handler file carries the ability's one affinity skill (ClassAbilityAffinityDeclarationTests).
        //
        // The lapse is announced once ("Hemomancy fades.") by TickHemomancyExpiry
        // (Player_HemomancyTick.cs), called from ClassAbilityBuffsHeartbeat.

        private HemomancyState hemomancyState;

        /// <summary>
        /// Hemomancy's heal-armed damage multiplier (1.0 = none): 1 + rank * per-rank * Healing multiplier
        /// while the buff is live. Read at the weapon leg (HemomancyAbility.ModifyOutgoingDamage), the
        /// DoT leg (EnchantmentManager.ApplyDamageTick) and the magic damage sites; never at Drain. Every
        /// caller sits behind a not-a-Player-target gate, so this does no PvP check of its own.
        /// </summary>
        public float GetHemomancyDamageMod()
        {
            var state = hemomancyState;

            if (state == null || !state.IsActive(Time.GetUnixTime()))
                return 1.0f;

            return HemomancyAbility.ActiveDamageMultiplier(this);
        }

        /// <summary>
        /// Called at every real heal site with the APPLIED health delta (never the requested amount).
        /// <paramref name="isLeech"/> marks Drain returns, Transfusion shares and Bloodlust lifesteal, which
        /// accumulate in the rolling leech window instead of each having to reach the threshold alone.
        /// Announces only on the inactive -> active transition; a refresh is silent.
        /// </summary>
        public void OnHealthRestored(uint applied, bool isLeech)
        {
            if (applied == 0)
                return;

            if (!TryGetClassAbility(ClassAbilityId.Hemomancy, out var rank) || rank <= 0)
            {
                // a rank lost mid-buff takes the buff and the half-filled window with it
                hemomancyState?.Reset();
                return;
            }

            hemomancyState ??= new HemomancyState();

            var result = hemomancyState.OnHealthRestored(
                Time.GetUnixTime(),
                applied,
                isLeech,
                Health.MaxValue,
                PropertyManager.GetDouble("class_ability_hemomancy_heal_threshold_fraction").Item,
                PropertyManager.GetDouble("class_ability_hemomancy_leech_window_seconds").Item,
                PropertyManager.GetDouble("class_ability_hemomancy_duration_seconds").Item);

            // announced only on the arming that OPENS the buff, never on a refresh inside it: a Drain-heavy
            // Blood Mage re-arms every couple of seconds, and a line per refresh would be unreadable
            if (result == HemomancyArmResult.Armed)
            {
                SendClassAbilityBuffMessage("Hemomancy quickens - the blood you mend sharpens your attacks.");
                ApplyVisualEffects(PlayScript.EnchantUpRed);
            }
        }
    }
}
