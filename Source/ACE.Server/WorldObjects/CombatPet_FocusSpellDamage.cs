using ACE.Server.Managers;

namespace ACE.Server.WorldObjects
{
    partial class CombatPet
    {
        /// <summary>
        /// FORK: Focus spell damage for CASTER SUMMONS (owner ruling 2026-11-02). A caster pet (IsCasterPet: the
        /// Ossuary line and the Revenant Lich) earns magic_damage_per_focus_point from its OWN buffed Focus, under the
        /// same gates a player's cast passes at each call site (a direct, non-proc damage spell against a non-player).
        /// Every other creature - a melee or missile pet, and every ordinary monster caster - earns nothing: the call
        /// sites ask <see cref="FocusSpellDamageModFor"/>, which is 1.0 for anything that is not a caster pet.
        /// </summary>
        public float GetFocusSpellDamageMod()
        {
            return Player.GetFocusSpellDamageMod(Focus.Current, PropertyManager.GetDouble("magic_damage_per_focus_point").Item);
        }

        /// <summary>
        /// The caster-summon half of the Focus spell damage bonus at a damage site: the pet's multiplier when
        /// <paramref name="source"/> is a caster pet, else exactly 1.0. The caller still owns the gates (direct hit,
        /// non-player target, not a proc), as it does for a player caster.
        /// </summary>
        internal static float FocusSpellDamageModFor(Creature source)
        {
            return source is CombatPet pet && pet.IsCasterPet ? pet.GetFocusSpellDamageMod() : 1.0f;
        }
    }
}
