using System;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Entity;
using ACE.Server.Pvp.Rules;
using ACE.Server.WeaponMods;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// The creature-level bodies of the Siphon and Cleanse weapon mods, so a Summoner focus can run them for its owner's
    /// combat pets (SummonerFocus.TryPetSiphon, SummonerFocus.ApplyPetKillUtility) as well as for the wielder. Siphon's
    /// body was moved here verbatim from Player.TryWeaponModSiphon, which now wraps it and adds the player's own client
    /// messages. The player's Cleanse (Player.CleanseOneHarmfulEnchantment) is NOT routed through here: it carries the PvP
    /// dispel vulnerability lock (D2), pinned to that file, and stays byte-identical; the pet's Cleanse below uses the same
    /// selection primitives without the lock, because a pet is never in a PvP match the focus transfer allows.
    /// </summary>
    partial class Creature
    {
        /// <summary>
        /// Resolves a registry entry's spell id for the creature-level Siphon and Cleanse. Production always uses the dat
        /// and world spell (<c>new Spell(id)</c>); a test sets a resolver for faked spells, because this assembly's tests
        /// load neither, and resets it in a finally. The server never sets it.
        /// </summary>
        internal static Func<int, Spell> WeaponModSpellResolver = DefaultWeaponModSpellResolver;

        internal static Spell DefaultWeaponModSpellResolver(int spellId) => new Spell(spellId);

        /// <summary>
        /// Siphon: a landed hit has a chance to STRIP one of the target's beneficial enchantments and apply the same
        /// spell to this creature, at the spell's own full duration (EnchantmentManager.Add). Returns the stolen spell
        /// and the Add result, or (null, null) when nothing was stolen; the caller owns any message. Every rule of the
        /// player's version, unchanged: magnitude first, then the self / PvP / Pet / dead target filter, one roll per
        /// call, beneficial-only top-layer selection through DispellingEdgeAbility.SelectDispellable, exactly one entry
        /// at random, the item-sourced aura exclusion re-asserted, and the spell resolved before the Dispel. Lock
        /// discipline as on the player: the target's read, the target's Dispel and this creature's Add are three
        /// separate registry calls, each under its own object's lock, never two at once.
        /// </summary>
        internal (Spell Spell, AddEnchantmentResult Added) SiphonOneBeneficialEnchantment(Creature target, double chance)
        {
            // magnitude first: nothing below this line runs on a weapon without Siphon, or with the gate off
            if (chance <= 0.0)
                return (null, null);

            if (target == null || target == this || PvpClassifier.IsPvp(this, target) || target is Pet || target.IsDead)
                return (null, null);

            if (!WeaponModCombat.RollsFree(chance, ThreadSafeRandom.Next(0f, 1f)))
                return (null, null);

            // Top layer per spell category, so a stack of the same buff counts once and the strongest is taken.
            var stealable = DispellingEdgeAbility.SelectDispellable(target.EnchantmentManager.GetEnchantments_TopLayer(EnchantmentTypeFlags.Beneficial));

            if (stealable.Count == 0)
                return (null, null);

            // Exactly ONE, chosen at random. Deliberately not a loop: one hit steals one enchantment even when
            // the hit came from a multishot volley or an AOE, and each hit rolls separately.
            var entry = stealable[ThreadSafeRandom.Next(0, stealable.Count - 1)];

            // never resurrect an item-sourced aura on the thief
            if (entry == null || entry.Duration == -1 || entry.SpellId > short.MaxValue)
                return (null, null);

            var spell = WeaponModSpellResolver(entry.SpellId);

            if (spell.NotFound)
                return (null, null);

            target.EnchantmentManager.Dispel(entry);

            return (spell, EnchantmentManager.Add(spell, this, null));
        }

        /// <summary>
        /// Strips ONE harmful enchantment off this creature (every caster's layer of it) and returns the spell, or null:
        /// Player.CleanseOneHarmfulEnchantment's selection (SelectDispellableHarmful on the top layer, one entry at
        /// random, SelectAllLayers re-expansion re-filtered) WITHOUT the PvP dispel lock, which is player-only. For a
        /// Summoner focus pet's killing blow (SummonerFocus.ApplyPetKillUtility). No roll; the caller owns the chance.
        /// </summary>
        internal Spell CleanseOneHarmfulEnchantmentNoPvpLock()
        {
            var harmful = WeaponModCombat.SelectDispellableHarmful(
                EnchantmentManager.GetEnchantments_TopLayer(EnchantmentTypeFlags.Undef), IsHarmfulWeaponModSpell);

            if (harmful.Count == 0)
                return null;

            var entry = harmful[ThreadSafeRandom.Next(0, harmful.Count - 1)];

            var spell = WeaponModSpellResolver(entry.SpellId);

            var layers = WeaponModCombat.SelectDispellableHarmful(
                DispellingEdgeAbility.SelectAllLayers(entry, EnchantmentManager.GetEnchantments(entry.SpellCategory)), IsHarmfulWeaponModSpell);

            EnchantmentManager.Dispel(layers);

            return spell;
        }

        /// <summary>TRUE when a registry entry's spell resolves and is harmful; an unresolvable id is left alone (the player's IsHarmfulSpell rule).</summary>
        private static bool IsHarmfulWeaponModSpell(int spellId)
        {
            var spell = WeaponModSpellResolver(spellId);

            return !spell.NotFound && spell.IsHarmful;
        }
    }
}
