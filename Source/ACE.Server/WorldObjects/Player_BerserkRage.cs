using System;
using System.Collections.Concurrent;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Managers;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// Berserk Rage (Berserker T3, 2026-10-24) per-player state: the immunity / enrage window and the
    /// process-lifetime cooldown map. The trigger and the immunity themselves are the two pre-write handlers
    /// in BerserkRageAbility; this file only owns the state they read and write.
    /// </summary>
    partial class Player
    {
        /// <summary>
        /// Unix time the current rage window ends; 0 (the initial value) means not raging. Transient: a relog
        /// or a PK-facet switch (ResetClassAbilityTransientState) ends the window.
        /// </summary>
        private double berserkRageWindowEndTime;

        /// <summary>
        /// Character guid -> unix time the next rage becomes available. STATIC ON PURPOSE (owner ruling): the
        /// cooldown must survive a relog within the same server process, which a per-instance field would not
        /// (a relog builds a fresh Player). A process restart clears it, which is accepted - the ruling asked
        /// for relog survival without a new persisted property. Entries are tiny and keyed by character, so
        /// the map is bounded by the number of characters that ever raged this process lifetime.
        /// </summary>
        private static readonly ConcurrentDictionary<uint, double> berserkRageReadyTimes = new ConcurrentDictionary<uint, double>();

        /// <summary>TRUE while the rage window is open.</summary>
        public bool IsBerserkRaging => berserkRageWindowEndTime > 0 && Time.GetUnixTime() < berserkRageWindowEndTime;

        /// <summary>TRUE when this character's rage cooldown has elapsed (or it has never raged this process).</summary>
        public bool IsBerserkRageReady(double now)
        {
            return !berserkRageReadyTimes.TryGetValue(Guid.Full, out var ready) || now >= ready;
        }

        /// <summary>
        /// The outgoing-damage multiplier while raging (class_ability_berserkrage_damage_multiplier, 1.5 by
        /// default), else exactly 1.0. Read by BerserkRageAbility.ModifyOutgoingDamage for weapon hits and by
        /// the magic damage sites beside Hemomancy; every caller sits behind a not-a-Player-target gate, so
        /// this does no PvP check of its own.
        /// </summary>
        public float GetBerserkRageDamageMod()
        {
            if (!IsBerserkRaging)
                return 1.0f;

            var mult = PropertyManager.GetDouble(BerserkRageAbility.DamageMultiplierKey).Item;

            return mult > 1.0 ? (float)mult : 1.0f;
        }

        /// <summary>
        /// Opens the rage: starts the window and the cooldown, announces it and plays the Strength Up effect
        /// (PlayScript.AttribUpRed - the TargetEffect every Strength Self/Other spell carries in the client
        /// dat). Called by the trigger handler AFTER it has already clamped the lethal blow to leave 1 HP.
        /// </summary>
        public void StartBerserkRage(double now, double durationSeconds, double cooldownSeconds)
        {
            berserkRageWindowEndTime = now + Math.Max(0.0, durationSeconds);
            berserkRageReadyTimes[Guid.Full] = now + Math.Max(0.0, cooldownSeconds);

            SendClassAbilityBuffMessage($"You fly into a berserk rage! You are immune to damage for {durationSeconds:0.#} seconds.");
            ApplyVisualEffects(PlayScript.AttribUpRed);
        }

        /// <summary>
        /// Heartbeat sweep (ClassAbilityBuffsHeartbeat): announces the end of the window once and clears it.
        /// </summary>
        private void TickBerserkRageExpiry(double now)
        {
            if (berserkRageWindowEndTime > 0 && now >= berserkRageWindowEndTime)
            {
                berserkRageWindowEndTime = 0;
                SendClassAbilityBuffMessage("Your berserk rage subsides.");
                ApplyVisualEffects(PlayScript.EnchantDownRed);
            }
        }
    }
}
