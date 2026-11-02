using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.ClassAbilities;
using ACE.Server.Entity;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        /// <summary>
        /// The cast number the last "takes hold" line was sent for (MagicState.CastNum), so a cast that marks
        /// several creatures - a Curse of Raven Fury ring, Spell AOE splashes, an Echo Cast recast, a Crimson
        /// Harvest fan - announces once. -1 so the very first cast (CastNum 1 after OnCastStart) is not
        /// mistaken for one already announced.
        /// </summary>
        private int weakenedBloodAnnouncedCastNum = -1;

        /// <summary>
        /// Which targets have already rolled for Weakened Blood during the current cast - see
        /// <see cref="WeakenedBloodCastRollLedger"/>.
        /// </summary>
        private readonly WeakenedBloodCastRollLedger weakenedBloodRollLedger = new WeakenedBloodCastRollLedger();

        /// <summary>
        /// ROLLS, and on a proc applies or refreshes, Weakened Blood on a target this player's damaging Life
        /// Magic spell just struck: Harm and every Drain strike (WorldObject_Magic, including each Crimson
        /// Harvest secondary) and every landed Health-damage life projectile (WeakenedBloodAbility.OnSpellHit,
        /// including Spell AOE and Cascade children and Echo Cast recasts). ONE ROLL PER TARGET PER CAST:
        /// the first call for a target in a cast (MagicState.CastNum) rolls, and every later call for that
        /// target in the same cast - a second Raven Fury projectile on a large monster, an AOE child, an Echo
        /// copy - returns without drawing a roll (<see cref="WeakenedBloodCastRollLedger"/>).
        ///
        /// Silently does nothing without the ability, so call sites need no rank lookup. The magnitude and
        /// the merge-with-existing rule live on the target (Creature_ClassAbilityDebuffs.cs) because every
        /// life caster in the fellowship reads the same mark, not just this one.
        ///
        /// THE SLOW rides the same application: rank slow x this player's Creature Enchantment affinity
        /// multiplier, floored by class_ability_weakenedblood_slow_floor (WeakenedBloodMath.SpeedFactor). The
        /// target refuses it for players, pets and chess pieces (Creature.CanBeWeakenedBloodSlowed).
        ///
        /// TWO THINGS LAND, NOT ONE (user, live test 2026-08-03: "the 'Fester' spell is what should be
        /// applied, and also multiply outgoing damage. The fester animation should be visible"):
        ///
        ///  - the transient target-side MARK, which is where the 2.00/2.50/3.10 life vulnerability lives and
        ///    is unchanged by this;
        ///  - the retail FESTER enchantment, which is what the player and everyone nearby can actually see.
        ///
        /// They are deliberately not the same object - see the note on <see cref="WeakenedBloodMath"/> for
        /// why reading the vulnerability out of the enchantment registry would hand it to
        /// GetEnchantmentsTopLayer's PowerLevel selection.
        /// </summary>
        public void TryApplyWeakenedBlood(Creature target)
        {
            if (target == null || target is Player)
                return;

            if (!TryGetClassAbility(ClassAbilityId.WeakenedBlood, out var rank))
                return;

            // ONE ROLL PER TARGET PER CAST: a second projectile (or AOE child, Echo copy, Crimson Harvest
            // secondary) of the same cast striking a target that already rolled draws no roll at all
            var castNum = MagicState?.CastNum ?? 0;

            if (!weakenedBloodRollLedger.TryClaimRoll(castNum, target.Guid.Full))
                return;

            // THE PROC (Blood Mage buff pass): strictly less than the chance.
            var chance = WeakenedBloodMath.ProcChance(rank,
                PropertyManager.GetDouble("class_ability_weakenedblood_proc_chance_r1").Item,
                PropertyManager.GetDouble("class_ability_weakenedblood_proc_chance_r2").Item,
                PropertyManager.GetDouble("class_ability_weakenedblood_proc_chance_r3").Item);

            if (!WeakenedBloodMath.Procs(ThreadSafeRandom.Next(0.0f, 1.0f), chance))
                return;

            // HEMORRHAGE (machinery on Weakened Blood, proven owned by the early return above). Added to the
            // rank's table value in the SAME BARE-MULTIPLIER UNIT the table is in - +0.05 takes a rank-3
            // mark from 3.10 to 3.15 - not as a percentage of it. The applier's gear is baked into the
            // magnitude STORED ON THE TARGET, which is deliberate: the mark is already shared with every
            // other attacker, so their damage reads whatever the applier's mark was worth.
            var mod = WeakenedBloodMath.ResistanceMod(rank,
                PropertyManager.GetDouble("class_ability_weakenedblood_resist_r1").Item,
                PropertyManager.GetDouble("class_ability_weakenedblood_resist_r2").Item,
                PropertyManager.GetDouble("class_ability_weakenedblood_resist_r3").Item,
                GetEquippedModValue(EquipmentModId.Hemorrhage));

            var duration = PropertyManager.GetDouble("class_ability_weakenedblood_duration_seconds").Item;

            var speedFactor = ComputeWeakenedBloodSpeedFactor(rank);

            var tookHold = target.ApplyWeakenedBlood(mod, duration, speedFactor);

            // at most ONE "takes hold" line per cast, however many creatures this cast marks
            // (a dead target gets no line - ApplyWeakenedBloodFester skips it - so it must not use up the slot)
            var announce = WeakenedBloodMath.ShouldAnnounce(tookHold && !target.IsDead, castNum, weakenedBloodAnnouncedCastNum);

            if (announce)
                weakenedBloodAnnouncedCastNum = castNum;

            ApplyWeakenedBloodFester(target, rank, mod, duration, announce, speedFactor);
        }

        /// <summary>
        /// The speed factor this player's Weakened Blood leaves a marked monster at (1.0 = no slow): the
        /// rank's slow, multiplied by this player's Creature Enchantment affinity multiplier (rank x
        /// multiplier, the affinity shape; no gear term), then max(1 - slow, floor).
        /// </summary>
        public double ComputeWeakenedBloodSpeedFactor(int rank)
        {
            var rankSlow = WeakenedBloodMath.RankSlow(rank,
                PropertyManager.GetDouble("class_ability_weakenedblood_slow_r1").Item,
                PropertyManager.GetDouble("class_ability_weakenedblood_slow_r2").Item,
                PropertyManager.GetDouble("class_ability_weakenedblood_slow_r3").Item);

            var slow = rankSlow * GetClassAbilityAffinityMultiplier(Skill.CreatureEnchantment);

            return WeakenedBloodMath.SpeedFactor(slow, PropertyManager.GetDouble("class_ability_weakenedblood_slow_floor").Item);
        }

        /// <summary>
        /// Lands the retail Fester enchantment behind Weakened Blood, plays that spell's own target effect
        /// on the creature IMMEDIATELY (so this cast's drain script lands on top of it), and tells the
        /// caster in chat when the mark first takes hold.
        ///
        /// THE CHAT LINE IS THE TELL, NOT THE SCRIPT - and that is a deliberate choice, made twice.
        /// User, live test 2026-08-03: "I can't tell that fester is applying visually when weakened blood
        /// fires. Perhaps the spell is being overridden by the drain." That reading was correct, and it is
        /// measurable rather than inferred. Probing the client's portal.dat SpellBase entries directly gives:
        ///
        ///   Fester Other V (175) / VI (176) / Incantation (4489): TargetEffect 0x26 (PlayScript.RegenDownREd)
        ///   Harm Other I (7) / VI (1176) / Heart Rend (2070):     TargetEffect 0x20 (PlayScript.HealthDownRed)
        ///   Drain Health Other I (1237) / VI (1242) / Inc (4643): TargetEffect 0x20 (PlayScript.HealthDownRed)
        ///
        /// Both scripts fire on the SAME target guid in the SAME tick, and Fester's goes first: this method
        /// runs inside HandleCastSpell_Boost / HandleCastSpell_Transfer, which return to HandleCastSpell,
        /// which then calls DoSpellEffects to broadcast the Harm or Drain script. So the subtle regen-down
        /// puff is enqueued immediately before the big red health-down burst on the same creature, and the
        /// drain is what the player actually sees. The enchantment registry write itself is unconditional
        /// inside EnchantmentManager.Add - nothing in that path can silently drop it.
        ///
        /// A ONE-TICK DELAY WAS TRIED HERE AND DELIBERATELY REVERTED. Deferring the Fester broadcast by a
        /// tick does make it the visible script - and that is exactly the problem, because it then displaces
        /// the drain. User, same day, on the delayed build: "it seems the fester is now applying on drain
        /// instead of the drain effect. Drain effect is better ... revert to show the drain as a priority
        /// over fester effect." The drain burst is the primary combat feedback and outranks the mark's tell.
        /// Do not re-introduce the delay to make Fester visible; the visibility need is already met below.
        ///
        /// BECAUSE A VISUAL WAS NEVER THE RIGHT ANSWER HERE. The actual need is confirming the mark landed,
        /// in a fight already full of particles, so the caster gets a chat line naming the target and quoting
        /// the magnitude. That line, not the script, is what makes Weakened Blood confirmable - which is why
        /// reverting the delay costs nothing. It fires only when the mark TAKES HOLD, not on every refresh
        /// (see Creature.ApplyWeakenedBlood), and at most ONCE PER CAST however many creatures that cast
        /// marks (<paramref name="announce"/>, decided in TryApplyWeakenedBlood by WeakenedBloodMath.ShouldAnnounce):
        /// a Curse of Raven Fury ring or a Crimson Harvest fan would otherwise print a line per creature.
        ///
        /// THE SCRIPT IS THE SPELL'S, NOT A GUESS. spell.TargetEffect is read straight out of portal.dat via
        /// SpellBase, exactly as DoSpellEffects does for a normal cast, so whatever the client already plays
        /// for a cast Fester is what plays here. Hardcoding a PlayScript would be a guess about a value the
        /// dat file already answers - and the neighbouring PlayScript values are the WRONG VITAL rather than
        /// a different colour (0x1F HealthUpRed is a heal, 0x20 HealthDownRed is the drain), so an
        /// off-by-one there is not a cosmetic slip.
        ///
        /// NOT RESISTED, DELIBERATELY. This is the visible half of a class-ability mark, not a cast spell:
        /// the mark itself has never been resistable and the two must not diverge, or a resisted Fester would
        /// leave a target silently carrying the vulnerability with no tell at all - the exact complaint this
        /// change answers.
        ///
        /// DURATION IS THE SPELL'S OWN, which is longer than the mark's 20 seconds. The two are not required
        /// to agree: Fester is a real Life Magic debuff doing its real retail work (a health-regeneration
        /// penalty) and outliving the vulnerability costs nothing, whereas clipping it would mean mutating a
        /// dat-backed Spell. What the player must not see is a vulnerability with no visual, and this is on
        /// the safe side of that.
        ///
        /// A dead target is skipped: the enchantment would be written onto a biota that is about to become a
        /// corpse, and the strike that killed it is feedback enough.
        /// </summary>
        private void ApplyWeakenedBloodFester(Creature target, int rank, double mod, double durationSeconds, bool announce, double speedFactor)
        {
            if (target.IsDead)
                return;

            var spellId = WeakenedBloodMath.FesterSpell(rank);

            if (spellId == SpellId.Undef)
                return;

            var spell = new Spell(spellId);

            if (spell.NotFound)
                return;

            target.EnchantmentManager.Add(spell, this, null);

            if (announce)
            {
                // the slow clause is omitted where no slow can land (a pet or chess piece target, or a slow
                // tuned to 0), so the line never claims a slow the target does not carry
                var slowNote = speedFactor < 1.0 && target.CanBeWeakenedBloodSlowed
                    ? $", attacks and casts {(1.0 - speedFactor) * 100.0:0}% slower"
                    : "";

                SendClassAbilityBuffMessage($"Weakened Blood takes hold on {target.Name}. ({spell.Name} lands, life damage x{mod:0.00}{slowNote} for {durationSeconds:0}s)");
            }

            if (spell.TargetEffect == 0)
                return;

            // IMMEDIATELY, and that ordering is the point: DoSpellEffects broadcasts this cast's own Harm or
            // Drain script on the same target a moment after this method returns, so the drain lands ON TOP
            // and stays the visible one. See the "deliberately reverted" note above before adding any delay.
            // BROADCAST, not a direct send - the same shape DoSpellEffects uses at its own TargetEffect site.
            target.EnqueueBroadcast(new GameMessageScript(target.Guid, spell.TargetEffect, spell.Formula.Scale));
        }
    }
}
