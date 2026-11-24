using System;

using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Hunter's Echo (Archer T3, one rank at 3 CAP). When one of the archer's Aetheria surges or their cloak fires
    /// its spell proc (the two sources Taunt rides, <see cref="TauntAbility.TriggersOn"/>), a translucent copy of
    /// the archer steps out where they stand, pulls every monster attacking the archer onto itself and fires the
    /// archer's own bow at them until it ends. A second trigger while it lives renews it; there is never more than
    /// one. When the trigger is a cloak spell proc, the Echo takes the hit that caused the proc instead of the
    /// archer (Player.TakeDamage and SpellProjectile.DamageTarget only). PvE only.
    ///
    /// AFFINITY: Assess Creature lengthens the Echo only - the base duration times the shared affinity multiplier,
    /// with the added seconds capped by class_ability_huntersecho_affinity_cap_seconds.
    ///
    /// The Echo object, its attack and its taunt live in CombatPet_HuntersEcho.cs; the owner's half (the live Echo,
    /// the cooldown, the absorb) in Player_ClassAbilityHuntersEcho.cs.
    /// </summary>
    public class HuntersEchoAbility : IItemProcAbility, ITestableClassAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.HuntersEcho,
            AbilityClass = ClassAbilityClass.Archer,
            Tier = 3,
            Name = "hunters_echo",
            DisplayName = "Hunter's Echo",
            // Live text: the lifetime is class_ability_huntersecho_duration_seconds, lengthened by Assess Creature by at
            // most class_ability_huntersecho_affinity_cap_seconds (HuntersEchoMath.Duration).
            DescriptionTemplate = "While you hold a missile weapon, when one of your Aetheria surges or your " +
                          "cloak fires its spell proc, a translucent echo of you steps out where you stand for " +
                          "{v:class_ability_huntersecho_duration_seconds|num} seconds. It draws every monster attacking you onto " +
                          "itself and shoots them with your own bow and arrows at your pace and accuracy, Multishot and Double Volley included; " +
                          "its arrows deal your damage and its kills are yours. It has your level, attributes, health and " +
                          "defenses and cannot be harmed by players. When the trigger is your cloak, the echo takes the hit that " +
                          "fired the proc instead of you. A new trigger while it lives renews its health and time. It keeps " +
                          "firing the bow you held when it appeared, even if you switch weapons. Higher Assess Creature lengthens it, by at most " +
                          "{v:class_ability_huntersecho_affinity_cap_seconds|num} seconds.",
            MaxRank = 1,
            CostPerRank = new[] { 3 },
            Implemented = true,
            AffinitySkill = Skill.AssessCreature,
            Ceilings = new[]
            {
                new ClassAbilityCeiling { Id = "affinity", Bounds = CeilingBounds.Affinity, Source = CeilingSource.Tunable, Key = "class_ability_huntersecho_affinity_cap_seconds", Label = "max seconds from affinity", Format = "num" },
            },
        };

        public void OnItemProc(Player wielder, int rank, WorldObject procSource)
        {
            // rides exactly Taunt's two sources: an aetheria surge or a cloak spell proc
            if (!TriggersOn(procSource))
                return;

            // the cloak absorb already summoned the Echo for this very proc at the roll site; only re-run the taunt,
            // since Taunt (earlier in registry order) may just have pulled more monsters onto the player
            if (wielder.ConsumeHuntersEchoAbsorb(procSource))
            {
                wielder.HuntersEcho?.TauntForHuntersEcho(wielder);
                return;
            }

            wielder.TriggerHuntersEcho(absorbed: false);
        }

        /// <summary>The trigger allowlist: exactly <see cref="TauntAbility.TriggersOn"/>.</summary>
        public static bool TriggersOn(WorldObject procSource) => TauntAbility.TriggersOn(procSource);

        /// <summary>
        /// The Echo's lifetime for <paramref name="player"/> in seconds: the base tunable, lengthened by the Assess
        /// Creature affinity multiplier with the added seconds capped (HuntersEchoMath.Duration).
        /// </summary>
        public static double DurationFor(Player player) =>
            HuntersEchoMath.Duration(
                PropertyManager.GetDouble("class_ability_huntersecho_duration_seconds").Item,
                player?.GetClassAbilityAffinityMultiplier(Skill.AssessCreature) ?? 1.0,
                PropertyManager.GetDouble("class_ability_huntersecho_affinity_cap_seconds").Item);

        public string Test(Player player)
        {
            // /testskill bypasses the trigger but not the summon gate (the ability must be held to summon at all)
            var echo = player.TriggerHuntersEcho(absorbed: false);

            return echo == null
                ? "Hunter's Echo test: no Echo (the ability is not held, is disabled, is on cooldown, or there was no spot to place it)."
                : $"Hunter's Echo test: Echo up for {echo.HuntersEchoSecondsLeft:0.0} seconds.";
        }

        /// <summary>
        /// The Echo's lifetime: the base duration as Skill, the capped seconds Assess Creature adds as Affinity, no gear.
        /// The null-conditional is for the readout unit tests (a null Player falls back to the neutral 1.0 factor).
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var baseSeconds = PropertyManager.GetDouble("class_ability_huntersecho_duration_seconds").Item;
            var cap = PropertyManager.GetDouble("class_ability_huntersecho_affinity_cap_seconds").Item;

            var multiplier = player?.GetClassAbilityAffinityMultiplier(Skill.AssessCreature) ?? 1.0;

            var rawAdded = Math.Max(0.0, baseSeconds * Math.Max(1.0, multiplier) - baseSeconds);
            var added = HuntersEchoMath.AddedSeconds(baseSeconds, multiplier, cap);

            return new ClassAbilityReadout
            {
                HasValue = rank > 0,
                Skill = rank > 0 ? baseSeconds : 0.0,
                Affinity = rank > 0 ? added : 0.0,
                Gear = 0.0,
                Effective = rank > 0 ? HuntersEchoMath.Duration(baseSeconds, multiplier, cap) : 0.0,
                Unit = "s",
                Label = "echo",
                Per = null,
                CapNote = cap > 0.0 && rawAdded > cap ? "affinity cap" : null,
                Ceilings = new[] { ClassAbilityCeilings.Read(Definition, "affinity", rank > 0 ? rawAdded : 0.0, rank, ClassAbilityText.Live) },
            };
        }
    }
}
