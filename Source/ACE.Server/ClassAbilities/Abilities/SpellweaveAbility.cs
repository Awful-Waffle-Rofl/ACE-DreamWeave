using System;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Spellsword T1 splash, and the class's thesis in one entry: casting any spell - INCLUDING A PROC -
    /// arms the next weapon hit for 3/6/9/12/15% more damage by rank, and landing a weapon hit arms the next
    /// spell for the same. The loop runs in both directions, which is what rewards alternating rather than
    /// committing to one half.
    ///
    /// COUNTING PROCS IS LOAD-BEARING, not incidental generosity: the class's own Spellblade, Runeblade and
    /// Spellstorm entries all cast war spells off a landed weapon hit, so a proc that did not arm the weapon
    /// half would leave the class's signature abilities feeding nothing. The mechanic slice must not
    /// "simplify" procs out.
    ///
    /// SHIPPED (2026-09-12): both halves are a symmetric ARM/CONSUME pair of one-shot charges, never a
    /// stacking ramp like Frenzy/Nether Rush/Resonance. The state lives on the Player
    /// (Player_ClassAbilityBuffs.cs: spellweaveWeaponChargeArmed, spellweaveSpellChargeArmed,
    /// spellweaveSpellDamageMod):
    ///
    ///  - CAST SIDE: Player.OnSpellweaveCast() runs once per committed cast, from exactly TWO sites, because
    ///    there are two cast paths and neither passes through the other:
    ///     1. WorldObject_Magic.HandleCastSpell - every ordinary cast, and every item cast-on-strike, which
    ///        arrives through WorldObject.TryProcItem -> TryCastSpell -> HandleCastSpell.
    ///     2. Player.CastClassAbilityProc - the Spellblade, Runeblade and Spellstorm procs. These call
    ///        CreateSpellProjectiles directly and NEVER reach HandleCastSpell, which is why they fed nothing
    ///        until 2026-09-14. HandleCastSpell skips its own call while the proc latch is armed, so a future
    ///        proc that does route through HandleCastSpell still fires the cast side exactly once.
    ///    That call first STAMPS whatever weapon-hit charge is pending into spellweaveSpellDamageMod, THEN
    ///    arms a fresh charge for the player's next landed weapon hit. Stamped once per cast rather than read
    ///    live, so a multi-projectile spell (an Arc, a Raven Fury ring) takes the one charge the CAST consumed
    ///    instead of each projectile racing to spend it. Every projectile then CAPTURES the stamp as it is
    ///    built (SpellProjectile.SpellweaveDamageMod, set in WorldObject_Magic.LaunchSpellProjectiles) and the
    ///    projectile damage sites read that capture, not the player field - so a later cast re-stamping the
    ///    player cannot change the bonus of a projectile already in flight. Harm has no projectile and reads
    ///    the player stamp synchronously inside the same HandleCastSpell call that set it.
    ///  - WEAPON-HIT SIDE: this class's ModifyOutgoingDamage (IOutgoingDamageAbility) consumes whatever
    ///    spell-armed charge is pending via Player.ConsumeSpellweaveWeaponHitMod, applies it to THIS landed
    ///    hit, then arms the player's next spell via Player.ArmSpellweaveSpellCharge. It runs BEFORE the war
    ///    procs on the same hit (IOutgoingDamageAbility.DispatchOrder), so a proc consumes the spell charge
    ///    its triggering hit armed and can never boost that hit.
    ///
    /// CHILD PROJECTILES are spawned from a spell-HIT hook, not by a cast, so they run neither cast-side
    /// site and consume no charge. Every one of them inherits its PARENT projectile's captured stamp, so one
    /// cast's bonus can never leak onto another cast's projectiles: Spell AOE radiated copies in
    /// SpellProjectile.SpawnClassAbilityAoeChild, and Echo Cast and Cascade children in their OnSpellHit
    /// handlers, which overwrite the live-player stamp CreateSpellProjectiles gave them.
    ///
    /// PvE ONLY FOR THE WEAPON-HIT HALF, and this needs no gate of its own: IOutgoingDamageAbility's own
    /// dispatch (Player.ApplyOutgoingDamageClassAbilities) already excludes a Player target and a miss, so
    /// this half can never fire in PvP. The cast-side half is not target-restricted - casting at anything,
    /// including another player, still arms the next weapon hit.
    ///
    /// AFFINITY: Item Enchantment MULTIPLIES this ability's own rank bonus (2026-09-12 affinity overhaul),
    /// applied identically to both halves since the mechanic is symmetric - one static helper
    /// (DamageMultiplier) and one PropertyManager tunable serve both directions. GetClassAbilityAffinityMultiplier
    /// is called in THIS file's own GetReadout (below), which is what
    /// ClassAbilityAffinityDeclarationTests.EveryHandlerScalingCall_IsDeclaredAsAffinitySkill checks.
    /// </summary>
    public class SpellweaveAbility : IOutgoingDamageAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.Spellweave,
            AbilityClass = ClassAbilityClass.Spellsword,
            Tier = 1,
            Name = "spellweave",
            DisplayName = "Spellweave",
            Description = "Casting any spell, including procs, makes your next weapon hit deal " +
                          "3/6/9/12/15% more damage (by rank). Landing a weapon hit makes your next spell " +
                          "deal 3/6/9/12/15% more. Higher Item Enchantment multiplies the bonus.",
            MaxRank = 5,
            CostPerRank = new[] { 1, 1, 1, 1, 1 },
            Implemented = true,
            AffinitySkill = Skill.ItemEnchantment,
        };

        /// <summary>
        /// The damage multiplier for EITHER half of the weave (1.0 = none): 1 + rank*percentPerRank + the
        /// affinity ADDED amount. Pure for testability, matching the other migrated abilities
        /// (SavageBlowsAbility, AttackSpeedAbility): <paramref name="affinityAddedFraction"/> is the AMOUNT
        /// the Item Enchantment multiplier adds to the rank term (rankBonus * multiplier - rankBonus), not
        /// the raw multiplier, so a caller with no affinity term can pass 0 and get the rank-only bonus
        /// bit-for-bit. Shared by both halves - the mechanic is symmetric, so one formula serves both.
        /// </summary>
        public static float DamageMultiplier(int rank, double percentPerRank, double affinityAddedFraction = 0.0)
        {
            if (rank <= 0)
                return 1.0f;

            var rankBonus = Math.Max(0, rank) * percentPerRank;
            return (float)(1.0 + rankBonus + Math.Max(0.0, affinityAddedFraction));
        }

        /// <summary>
        /// The weapon-hit half: consumes whatever charge a prior spell cast armed
        /// (Player.ConsumeSpellweaveWeaponHitMod), applies it to THIS landed weapon hit, then arms the
        /// player's NEXT spell to get the same bonus (Player.ArmSpellweaveSpellCharge). Dispatched via
        /// IOutgoingDamageAbility, whose own dispatch already excludes a miss and a Player target - see
        /// ClassAbilityHooks.IOutgoingDamageAbility and Player.ApplyOutgoingDamageClassAbilities.
        /// </summary>
        public void ModifyOutgoingDamage(Player attacker, int rank, Creature target, DamageEvent damageEvent)
        {
            if (attacker == null || target == null || damageEvent == null || !damageEvent.HasDamage)
                return;

            var mult = attacker.ConsumeSpellweaveWeaponHitMod(rank);
            if (mult > 1.0f)
                damageEvent.Damage *= mult;

            // Landing this hit arms the player's NEXT spell, whatever it turns out to be.
            attacker.ArmSpellweaveSpellCharge();
        }

        /// <summary>
        /// Mirrors DamageMultiplier's terms above (x100 for display) - both halves consume the SAME
        /// tunable and affinity, so one readout describes the ability regardless of which side a player
        /// happens to be charging. No equipment mod exists for this ability (no EquipmentModId.Spellweave
        /// entry), so Gear is 0, and nothing here clamps the affinity term, so CapNote is always null.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var perRank = PropertyManager.GetDouble("class_ability_spellweave_percent_per_rank").Item;

            var rankBonus = rank * perRank;

            // Affinity is a MULTIPLIER on the rank term, reported as the AMOUNT it adds so the displayed
            // terms stay in one unit and still sum to Effective.
            var multiplier = player.GetClassAbilityAffinityMultiplier(Skill.ItemEnchantment);

            var skill = rankBonus * 100.0;
            var affinity = (rankBonus * multiplier - rankBonus) * 100.0;
            var total = skill + affinity;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = affinity,
                Gear = 0.0,
                Effective = total,
                Unit = "%",
                Label = "spellweave dmg",
                Per = null,
                CapNote = null,
            };
        }
    }
}
