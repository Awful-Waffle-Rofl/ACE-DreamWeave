using System;

using ACE.Entity.Enum;

namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// Pure math for WEAKENED BLOOD (Blood Mage T2 game-changer): a landed Harm or Drain leaves the TARGET
    /// carrying a life-magic vulnerability of 2.00 / 2.50 / 3.10 by the applier's rank, for 20 seconds, and
    /// applies the retail FESTER enchantment on that target so the mark is something the player can
    /// actually see land (<see cref="FesterSpell"/>).
    ///
    /// The state this backs lives on the TARGET (Creature_ClassAbilityDebuffs.cs), not on the caster,
    /// precisely so a second life caster in the fellowship reads the same debuff - BLOOD-MAGE-DESIGN sec 3.
    /// The value is fed into Creature.GetLifeVulnerabilityMod, which takes MAX against weapon rending, so
    /// life vulnerability stays one axis rather than becoming a product.
    ///
    /// THE FESTER ENCHANTMENT IS NOT WHERE THE VULNERABILITY LIVES. The two are deliberately separate: the
    /// vulnerability is still the transient target-side mark, and Fester is a real Life Magic enchantment
    /// with its own retail effect (a health-regeneration penalty) and its own visual. Reading the
    /// vulnerability out of the enchantment registry instead would put it at the mercy of
    /// EnchantmentManager.GetEnchantmentsTopLayer, which selects the surviving layer by PowerLevel and never
    /// by StatModValue - so a higher-tier Fester from any other source would silently take over a Blood
    /// Mage's mark. Keeping them apart is what makes that impossible.
    /// </summary>
    public static class WeakenedBloodMath
    {
        /// <summary>
        /// The life resistance modifier a given rank applies: 2.00 / 2.50 / 3.10 at ranks 1-3, the top three
        /// rungs of the retail vulnerability ladder (Vulnerability V, VI, and the Incantation). Rank 0 or
        /// below returns 1.0 (no vulnerability); a rank above 3 is clamped to rank 3 rather than rejected,
        /// matching how the other class abilities treat an out-of-range persisted rank.
        ///
        /// A configured value below 1.0 would be a RESISTANCE rather than a vulnerability, which this axis
        /// must never produce, so the result is floored at 1.0.
        ///
        /// <paramref name="gear"/> is the HEMORRHAGE equipment mod (EquipmentModId.Hemorrhage), added to
        /// the rank's table value on the same additive axis (DESIGN.md 3.3). WATCH THE UNIT: this method
        /// returns a BARE MULTIPLIER (2.00/2.50/3.10), not a fraction, so the gear term is in that same
        /// bare-multiplier unit - Hemorrhage's MaxMagnitude of 0.05 means +0.05 ON the multiplier, taking a
        /// rank-3 mark from 3.10 to 3.15. It defaults to 0 and adding 0.0 is exact, so an unmodded mark is
        /// bit-identical.
        ///
        /// The rank &lt;= 0 early return runs BEFORE the gear term, so a caster without Weakened Blood can
        /// never produce a vulnerability from gear alone - the mod is machinery.
        /// </summary>
        public static double ResistanceMod(int rank, double modR1, double modR2, double modR3, double gear = 0.0)
        {
            if (rank <= 0)
                return 1.0;

            var mod = (rank switch
            {
                1 => modR1,
                2 => modR2,
                _ => modR3,
            }) + gear;

            return mod < 1.0 ? 1.0 : mod;
        }

        /// <summary>
        /// The retail Fester Other tier a given rank applies, alongside the transient mark. Rank 0 or below
        /// returns <see cref="SpellId.Undef"/> (apply nothing); a rank above 3 is clamped to rank 3, matching
        /// how <see cref="ResistanceMod"/> treats an out-of-range persisted rank.
        ///
        /// HOW THE TIERS WERE CHOSEN: each rank's Fester tier sits on the SAME rung of the retail ladder as
        /// that rank's vulnerability magnitude already does. ResistanceMod's 2.00 / 2.50 / 3.10 are exactly
        /// the elemental Vulnerability Other V, VI and Incantation values (verified in ace_world.spell:
        /// Acid Vulnerability Other V = 2.0, VI = 2.5, and the Incantation = 3.1), so the mark's spell is
        /// Fester Other V, Fester Other VI, and the Incantation of Fester Other in turn. Rank 3 lands on the
        /// Incantation exactly, which is the same ceiling the resistance mod already states.
        ///
        /// Ids are the enum's, verified against ace_world.spell at write time: FesterOther5 = 175
        /// "Fester Other V", FesterOther6 = 176 "Fester Other VI", FesterOther8 = 4489 "Incantation of
        /// Fester Other". Note the rung between them is NOT named Fester: FesterOther7 = 2178 is
        /// "Decrepitude's Grasp", the retail tier-VII flavour name.
        /// </summary>
        public static SpellId FesterSpell(int rank) => rank switch
        {
            <= 0 => SpellId.Undef,
            1 => SpellId.FesterOther5,
            2 => SpellId.FesterOther6,
            _ => SpellId.FesterOther8,
        };

        /// <summary>
        /// TRUE while a debuff applied with the given expiry is still holding.
        /// </summary>
        public static bool IsActive(double now, double expireTime) => now < expireTime;

        /// <summary>
        /// Combines an incoming application with whatever the target already carries, returning the values
        /// the target should store.
        ///
        /// BOTH terms take the MAXIMUM, independently. A rank-1 blood mage refreshing a rank-3 debuff must
        /// not downgrade it to 2.00, and a rank-3 application must not shorten a longer-lived one. The
        /// existing values are ignored entirely once they have expired, so a lapsed rank-3 debuff cannot
        /// resurrect itself through a later rank-1 hit.
        /// </summary>
        public static (double Mod, double ExpireTime) Refresh(double now, double existingMod, double existingExpire, double incomingMod, double incomingExpire)
        {
            if (!IsActive(now, existingExpire))
                return (incomingMod, incomingExpire);

            return (existingMod > incomingMod ? existingMod : incomingMod,
                    existingExpire > incomingExpire ? existingExpire : incomingExpire);
        }

        /// <summary>
        /// Whether a life spell's DAMAGE reads the Weakened Blood mark. This is the single place the
        /// beneficiary ruling is encoded.
        ///
        /// APPLYING THE MARK AND BENEFITING FROM IT ARE DIFFERENT QUESTIONS. A landed Drain applies and
        /// refreshes Weakened Blood - that is unchanged and is not what this decides. What this decides is
        /// who the resulting vulnerability multiplies for.
        ///
        /// User ruling, 2026-08-02: "Lets exclude drains from the blood rend/vuln. I have thoughts for
        /// making drains better, but I think the multiplier is too much. Blood rend/vuln should apply to
        /// harm, heca, raven." Weakened Blood IS the blood vulnerability, so:
        ///
        ///  - <see cref="SpellType.Boost"/> on Health - HARM - benefits;
        ///  - <see cref="SpellType.LifeProjectile"/> - MARTYR'S HECATOMB and CURSE OF RAVEN FURY - benefits;
        ///  - <see cref="SpellType.Transfer"/> - DRAIN - does NOT.
        ///
        /// The mechanical reason Drain is the odd one out is spell.TransferCap: the cap binds against
        /// anything worth draining, so a vulnerability there is either invisible (scaling the roll) or turns
        /// a filler spell into a primary nuke that also heals (scaling the cap). Making Drain better is a
        /// design decision the user deliberately deferred, not an engineering gap to close here.
        ///
        /// Only the Drain answer is a live branch (WorldObject_Magic.HandleCastSpell_Transfer selects a
        /// resistance accessor on it). The other two are stated rather than gated, because Harm and the life
        /// projectiles read the axis inside Creature.GetResistanceMod with no branch to take - they are here
        /// so the full beneficiary list is one readable, testable thing.
        /// </summary>
        public static bool DamageBenefits(SpellType metaSpellType)
        {
            switch (metaSpellType)
            {
                case SpellType.Boost:
                case SpellType.FellowBoost:
                case SpellType.LifeProjectile:
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>
        /// The single life-vulnerability axis, as arithmetic: the MAXIMUM of the weapon rend/cleave term and
        /// any cast mark, floored at 1.0 so a rend below 1.0 (a resistance, not a vulnerability) is clamped
        /// out. Creature.GetLifeVulnerabilityMod is a one-line call to this.
        ///
        /// MAX, NEVER A PRODUCT. That is the whole guarantee the axis exists to provide - a blood mage's
        /// mark and a rending wand do not multiply, exactly as a war caster's rending wand and a cast Fire
        /// Vulnerability do not. A new contributor belongs in this call, not as a separate multiplier at a
        /// damage site.
        /// </summary>
        public static float VulnerabilityMod(double weaponResistanceMod, double castVulnerability)
        {
            return (float)Math.Max(1.0, Math.Max(weaponResistanceMod, castVulnerability));
        }
    }
}
