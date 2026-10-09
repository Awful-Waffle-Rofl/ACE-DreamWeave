using System;
using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// When one of the player's equipped Aetheria surges, OR their equipped cloak fires its spell proc, all
    /// nearby monsters are taunted into attacking the player for a short time. Rides along with the proc
    /// ROLL, regardless of which spell procs or whether it resists/fails. Higher Assess Person extends the
    /// hold duration, up to a hard cap (2026-09-12 overhaul, replacing Loyalty - reading a target down to
    /// keep it locked onto you is an Assess Person trick, not a Loyalty one).
    ///
    /// THE CLOAK TRIGGER WAS ADDED 2026-09-29 AND REUSES THE EXISTING IItemProcAbility HOOK rather than a new
    /// one. Cloak procs resolve entirely inside ACE.Server.Entity.Cloak and never touched
    /// WorldObject.TryProcItem, so Cloak.TryProcSpell now calls Player.OnClassAbilityItemProc(cloak) on its
    /// success path. Routing it through the existing hook is safe precisely because this class is the ONLY
    /// implementor of IItemProcAbility, so no other handler can pick up the new source; the filter in
    /// <see cref="TriggersOn"/> is what keeps it honest if that ever stops being true.
    /// </summary>
    public class TauntAbility : IItemProcAbility, ITestableClassAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.Taunt,
            AbilityClass = ClassAbilityClass.Vanguard,
            Tier = 2,
            Name = "taunt",
            DisplayName = "Taunt",
            Description = "When one of your equipped Aetheria surges, or your equipped cloak fires its spell " +
                          "proc, all nearby monsters are taunted into attacking you for a short time. Higher " +
                          "Assess Person increases the hold duration.",
            MaxRank = 1,
            CostPerRank = new[] { 3 },
            Implemented = true,
            AffinitySkill = Skill.AssessPerson,
        };

        public void OnItemProc(Player wielder, int rank, WorldObject procSource)
        {
            // taunt only rides along with an aetheria surge or a cloak spell proc, not any other item proc
            if (!TriggersOn(procSource))
                return;

            var taunted = Activate(wielder);

            if (taunted > 0)
                wielder.Session.Network.EnqueueSend(new GameMessageSystemChat(
                    $"{SourceClause(procSource)} taunts {taunted} nearby monster{(taunted == 1 ? "" : "s")} into attacking you!", ChatMessageType.CombatSelf));
        }

        /// <summary>
        /// The two proc sources Taunt rides: an equipped Aetheria's surge and an equipped cloak's spell proc.
        /// Pure so the filter is unit-testable and so it stays a positive allowlist - anything else reaching
        /// the IItemProcAbility hook (a weapon's cast-on-strike, say) must NOT taunt, and a new proc source
        /// wired into the hook later is excluded by default rather than included by accident.
        ///
        /// A null source is refused rather than throwing: both dispatch paths pass a real item today, and a
        /// third that did not should be inert here rather than killing the hit.
        /// </summary>
        public static bool TriggersOn(WorldObject procSource)
        {
            if (procSource == null)
                return false;

            return Aetheria.IsAetheria(procSource.WeenieClassId) || Cloak.IsCloak(procSource);
        }

        /// <summary>
        /// The subject of the taunt chat line, so an aetheria surge and a cloak proc do not both claim to be
        /// a "surging aetheria". Pure, and only ever called for a source <see cref="TriggersOn"/> accepted.
        /// </summary>
        public static string SourceClause(WorldObject procSource) =>
            procSource != null && Cloak.IsCloak(procSource) ? "Your cloak's woven magic" : "Your surging aetheria";

        /// <summary>
        /// The Taunt effect itself: forces every attackable monster within the taunt radius and
        /// line of sight to attack the player for the configured duration, returning how many
        /// were taunted. No learned-skill check - callers gate (the surge proc requires the
        /// skill; the /testskill developer command deliberately doesn't).
        ///
        /// THE TWO EQUIPMENT MODS ARE READ THROUGH THE MACHINERY GATE PRECISELY BECAUSE THIS METHOD DOES
        /// NOT PROVE OWNERSHIP. Its two callers disagree: OnItemProc is dispatched from the LEARNED
        /// IItemProcAbility hook cache, so an owner is guaranteed there, but Test(player) is reached from
        /// /testskill, which the command's own help text says "bypasses its normal trigger and the learned
        /// requirement" - so a developer without Taunt can run this. Provoke and Bellow are machinery mods
        /// and must stay inert for that non-owner, which is what Player.GetMachineryEquipmentModValue
        /// enforces (it returns 0 unless the linked ability is live). A plain GetEquippedModValue here would
        /// have let a /testskill taunt from an unowning character pick up gear magnitudes.
        /// </summary>
        public static int Activate(Player player)
        {
            // Bellow equipment mod (MACHINERY): +m of taunt radius, added to the raw radius read. There is
            // no clamp anywhere on this value, so this is a plain summand.
            var radius = EffectiveRadius(PropertyManager.GetDouble("class_ability_taunt_radius").Item,
                player.GetMachineryEquipmentModValue(EquipmentModId.Bellow));

            // Taunt's Assess Person affinity (2026-09-12 overhaul, replacing Loyalty): MULTIPLIES the base
            // duration rather than adding beside it. Untrained Assess Person reproduces the base duration
            // exactly (multiplier = 1.0). What EffectiveDuration clamps is the AMOUNT the multiply adds
            // (baseDuration * multiplier - baseDuration), not the raw factor.
            var baseDuration = PropertyManager.GetDouble("class_ability_taunt_duration").Item;
            var affinityMultiplier = player.GetClassAbilityAffinityMultiplier(Skill.AssessPerson);
            var affinityBonus = baseDuration * affinityMultiplier - baseDuration;
            var bonusCap = PropertyManager.GetDouble("class_ability_taunt_loyalty_bonus_cap_seconds").Item;

            // Provoke equipment mod (MACHINERY): +s of hold duration, a further summand on the SUM and
            // therefore OUTSIDE the affinity clamp - see EffectiveDuration.
            var duration = EffectiveDuration(baseDuration, affinityBonus, bonusCap,
                player.GetMachineryEquipmentModValue(EquipmentModId.Provoke));

            var taunted = 0;

            // same visible-objects source and monster filters as GetMultiShotTargets
            var visible = player.PhysicsObj.ObjMaint.GetVisibleObjectsValuesWhere(o => o.WeenieObj.WorldObject != null);

            foreach (var obj in visible)
            {
                var creature = obj.WeenieObj.WorldObject as Creature;

                if (creature == null || creature is Player || creature.IsDead || creature.Teleporting)
                    continue;

                if (!creature.Attackable && creature.TargetingTactic == TargetingTactic.None)
                    continue;

                if (creature is CombatPet)
                    continue;

                if (player.Location.DistanceTo(creature.Location) > radius)
                    continue;

                if (!player.IsDirectVisible(creature))
                    continue;

                creature.ApplyTaunt(player, duration);
                taunted++;
            }

            return taunted;
        }

        /// <summary>
        /// The effective Taunt hold duration in seconds: the base duration plus the affinity bonus (the
        /// AMOUNT the Assess Person multiplier adds - see Activate above), clamped to
        /// <paramref name="bonusCapSeconds"/> (0 reproduces <paramref name="baseDuration"/> exactly). Pure
        /// for testability, and unchanged by the 2026-09-12 affinity-primitive migration: this helper only
        /// ever clamps whatever bonus its caller hands it, and never cared whether that bonus came from an
        /// additive Loyalty quotient or a multiplicative Assess Person factor.
        ///
        /// <paramref name="gearSeconds"/> is the PROVOKE equipment mod (EquipmentModId.Provoke), a MACHINERY
        /// mod added as a further summand ON THE SUM, deliberately OUTSIDE the affinity clamp. Only the
        /// BONUS is clamped here, and the clamp exists to bound an unbounded skill multiply; the gear term is
        /// already bounded by its registry MaxMagnitude, so folding it inside the clamp would let a
        /// fully-capped affinity silently eat the mod - the Resonance failure recorded in DESIGN.md 2.2.
        /// Provoke is therefore live at any Assess Person. Last and defaulted to 0, so an unmodded build is
        /// bit-identical.
        /// </summary>
        public static double EffectiveDuration(double baseDuration, double affinityBonusSeconds, double bonusCapSeconds, double gearSeconds = 0.0)
        {
            var bonus = Math.Clamp(affinityBonusSeconds, 0.0, bonusCapSeconds);
            return baseDuration + bonus + Math.Max(0.0, gearSeconds);
        }

        /// <summary>
        /// The effective Taunt radius in metres: the base radius tunable (rank-invariant, like Taunt's own
        /// MaxRank of 1) plus the BELLOW equipment mod, a plain summand with no clamp of its own. Pure for
        /// testability - shared by Activate, Test and the GetReadout radius Secondary so all three cannot
        /// drift apart.
        /// </summary>
        public static double EffectiveRadius(double baseRadius, double gearRadius = 0.0) =>
            baseRadius + Math.Max(0.0, gearRadius);

        public string Test(Player player)
        {
            var taunted = Activate(player);

            // Quotes the radius Activate actually used, Bellow included, through the same machinery gate -
            // so a non-owner running /testskill sees the unmodded number, matching what just happened.
            var radius = EffectiveRadius(PropertyManager.GetDouble("class_ability_taunt_radius").Item,
                player.GetMachineryEquipmentModValue(EquipmentModId.Bellow));

            return $"Taunt test: taunted {taunted} monster{(taunted == 1 ? "" : "s")} within {radius:0} units.";
        }

        /// <summary>
        /// Mirrors the base duration + Assess Person affinity bonus terms fed into EffectiveDuration in
        /// Activate above - the two must stay in step. Already in seconds ("s" unit, no x100). Affinity is
        /// the CAPPED bonus (clamped the same way EffectiveDuration clamps it), so the three displayed terms
        /// still sum to Effective. CapNote is set only when the raw bonus actually exceeds the cap this call.
        ///
        /// GEAR IS PROVOKE ONLY, not Provoke plus Bellow, ON THIS PRIMARY SEGMENT. Taunt carries two mods
        /// and they are in different units - Provoke is seconds of hold, Bellow is metres of radius - so
        /// only the one matching this segment's own Unit ("s") can be summed into it. Bellow's own
        /// gear-scaled radius bonus is reported as a Secondary below now that the contract supports more
        /// than one scalar - previously this was a documented display gap (Bellow live in Activate, no
        /// readout line for it); it no longer is.
        ///
        /// A plain GetEquippedModValue is correct HERE, unlike in Activate: /abilities list only renders a
        /// readout for abilities the player already owns at rank > 0, so ownership is proven by the caller.
        /// The null-conditional is for the readout unit tests, which pass a null Player because Player's
        /// static initializer cannot run under the test host.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var baseDuration = PropertyManager.GetDouble("class_ability_taunt_duration").Item;

            var affinityMultiplier = player.GetClassAbilityAffinityMultiplier(Skill.AssessPerson);
            var affinityBonus = baseDuration * affinityMultiplier - baseDuration;

            var bonusCap = PropertyManager.GetDouble("class_ability_taunt_loyalty_bonus_cap_seconds").Item;

            // Rank-gated - a machinery mod reports nothing without its ability.
            var gear = rank <= 0
                ? 0.0
                : Math.Max(0.0, player?.GetEquippedModValue(EquipmentModId.Provoke) ?? 0.0);

            var clampedAffinityBonus = Math.Clamp(affinityBonus, 0.0, bonusCap);
            var capBit = clampedAffinityBonus < affinityBonus;

            var effective = EffectiveDuration(baseDuration, affinityBonus, bonusCap, gear);

            // RADIUS secondary: mirrors the radius term fed into Activate above exactly - the base radius
            // tunable (rank-invariant, like Taunt's own MaxRank of 1) plus the BELLOW machinery mod, a
            // plain summand with no clamp of its own (see Activate's own comment - "no clamp anywhere on
            // this value").
            var baseRadius = PropertyManager.GetDouble("class_ability_taunt_radius").Item;

            var radiusGear = rank <= 0
                ? 0.0
                : Math.Max(0.0, player?.GetEquippedModValue(EquipmentModId.Bellow) ?? 0.0);

            var radiusSecondary = RadiusSecondary(baseRadius, radiusGear);

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = baseDuration,
                Affinity = clampedAffinityBonus,
                Gear = gear,
                Effective = effective,
                Unit = "s",
                Label = "taunt",
                Per = null,
                CapNote = capBit ? "loyalty cap" : null,
                Secondary = new List<ClassAbilityReadout> { radiusSecondary },
            };
        }

        /// <summary>
        /// The radius secondary readout for a given base radius and Bellow gear term - always present
        /// (the base radius tunable is never 0 in shipped configuration, unlike the zero-omission secondaries
        /// on other abilities). Pure for testability, built on <see cref="EffectiveRadius"/> so GetReadout
        /// and this segment cannot drift from Activate's own radius computation.
        /// </summary>
        public static ClassAbilityReadout RadiusSecondary(double baseRadius, double gearRadius = 0.0)
        {
            var gear = Math.Max(0.0, gearRadius);

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = baseRadius,
                Affinity = 0.0,
                Gear = gear,
                Effective = EffectiveRadius(baseRadius, gear),
                Unit = "m",
                Label = "radius",
                Per = null,
                CapNote = null,
            };
        }
    }
}
