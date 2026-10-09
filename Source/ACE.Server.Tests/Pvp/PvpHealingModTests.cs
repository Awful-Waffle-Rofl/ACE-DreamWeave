using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.ClassAbilities;
using ACE.Server.Pvp;
using ACE.Server.Pvp.Rules;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// The PvP healing mod (Docs/Pvp/DESIGN.md "PvP rules (levers)"): pvp_healing_mod scales Health heals RECEIVED
    /// by a player in a Live arena match (ARENA ONLY - owner ruling 2026-09-27; a PK-timer-only player is never
    /// scaled) at HL1-HL5F, all routed through PvpRules.ApplyHealingMod.
    ///
    /// The scaling rule is exercised through PvpRules' pure functions and its choke-point entry on reflection-seeded
    /// Players. The five sites cannot be driven live here - a heal spell reads its Spell from the client dat, a kit or
    /// food needs a Player with vitals and skills, which this test host cannot construct - so which site passes which
    /// point, the Health-only gates, and the ordering against HK2 / C4 / the Drain cloak proc / the fellow split are
    /// SOURCE-ORDER PLACEMENT CHECKS (matched on CODE only, comments stripped), not live heal computations.
    ///
    /// SEEDING: no PropertyManager key is read - the dials come from a swapped PvpRuleTunables.DialSource, and
    /// the arena-only gate reads only the healed player's arena binding (SetPvpBindingForTests), never pk_timer. A
    /// non-arena player is an NPK with no binding, or a PK with a recent PvP hit (PkTimerOnlyPlayer). Every seam
    /// (DialSource, Observer) is saved and restored per test.
    /// </summary>
    [TestClass]
    public class PvpHealingModTests
    {
        private Func<PvpRuleDials> savedDialSource;
        private Action<PvpChokePoint, double, double> savedObserver;

        private List<(PvpChokePoint Point, double Before, double After)> observed;
        private int dialReads;

        private static readonly PvpChokePoint[] HealPoints = { PvpChokePoint.HL1, PvpChokePoint.HL2, PvpChokePoint.HL3, PvpChokePoint.HL4, PvpChokePoint.HL5, PvpChokePoint.HL5F };

        [TestInitialize]
        public void Setup()
        {
            savedDialSource = PvpRuleTunables.DialSource;
            savedObserver = PvpRules.Observer;

            observed = new List<(PvpChokePoint, double, double)>();
            dialReads = 0;

            PvpRules.Observer = (p, b, a) => observed.Add((p, b, a));
            UseHealing(1.0);
        }

        [TestCleanup]
        public void Cleanup()
        {
            PvpRuleTunables.DialSource = savedDialSource;
            PvpRules.Observer = savedObserver;
        }

        // ================= fixtures =================

        private void UseHealing(double mod, bool enabled = true)
        {
            var dials = PvpRuleTunables.Defaults with { Enabled = enabled, HealingMod = mod };
            PvpRuleTunables.DialSource = () => { dialReads++; return dials; };
        }

        private static Player Seeded()
        {
            var player = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));

            SetInherited(player, "<Biota>k__BackingField", new ACE.Entity.Models.Biota());
            SetInherited(player, "BiotaDatabaseLock", new ReaderWriterLockSlim());

            return player;
        }

        private static void SetInherited(WorldObject wo, string name, object value)
        {
            var field = typeof(WorldObject).GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(field, $"WorldObject.{name} was not found by reflection - has it been renamed?");
            field.SetValue(wo, value);
        }

        private static PvpMatch NewMatch(string mode = "arena_1v1")
        {
            var teams = new List<PvpTeam>
            {
                new PvpTeam(0, new List<PvpParticipant> { new PvpParticipant(1, 1500) }),
                new PvpTeam(1, new List<PvpParticipant> { new PvpParticipant(2, 1500) })
            };

            return new PvpMatch(Guid.NewGuid(), mode, teams, DateTime.UtcNow);
        }

        /// <summary>Bound to a Live arena match - the ONLY state the arena-only healing lever scales. Never touches pk_timer.</summary>
        private static Player ArenaBoundPlayer()
        {
            var player = Seeded();
            player.PlayerKillerStatus = PlayerKillerStatus.NPK;
            player.SetPvpBindingForTests(new PvpPlayerBinding(NewMatch(), 0, PvpMatchState.Live, false, false, false, false, false));
            return player;
        }

        /// <summary>NPK with no binding: not in a Live arena match.</summary>
        private static Player UnboundPlayer()
        {
            var player = Seeded();
            player.PlayerKillerStatus = PlayerKillerStatus.NPK;
            return player;
        }

        /// <summary>
        /// A PK who just made a PvP hit (LastPkAttackTimestamp = now, so their PK timer runs) but has NO arena binding:
        /// the open-world case the arena-only ruling excludes. Built without reading PKTimerActive, which would read
        /// pk_timer; PvpRulesPveInvarianceTests.PkTimerOnly_HealingMod_IsUnchanged_AndReadsNoLever asserts the timer
        /// really is active for this shape.
        /// </summary>
        private static Player PkTimerOnlyPlayer()
        {
            var player = Seeded();
            player.PlayerKillerStatus = PlayerKillerStatus.PK;
            player.LastPkAttackTimestamp = ACE.Common.Time.GetUnixTime();
            return player;
        }

        // ================= pure =================

        [TestMethod]
        public void ScaleHealing_Pure_FloorsAndKeepsTheIdentityExact()
        {
            Assert.AreEqual(250.0, PvpRules.ScaleHealing(500.0, 0.5));
            Assert.AreEqual(61.0, PvpRules.ScaleHealing(123.0, 0.5), "61.5 is floored, never rounded up to 62");
            Assert.AreEqual(1000.0, PvpRules.ScaleHealing(500.0, 2.0));
            Assert.AreEqual(0.0, PvpRules.ScaleHealing(500.0, 0.0), "0 is legal: the heal lands for 0");
            Assert.AreEqual(0.0, PvpRules.ScaleHealing(1.0, 0.5), "a 1-point heal at 0.5 floors to 0, not up to 1");
            Assert.AreEqual(123.456, PvpRules.ScaleHealing(123.456, 1.0), "the identity returns the value exactly, unfloored");
        }

        [TestMethod]
        public void HealsHealth_IsHealthOnly()
        {
            Assert.IsTrue(PvpRules.HealsHealth(PropertyAttribute2nd.Health));
            Assert.IsFalse(PvpRules.HealsHealth(PropertyAttribute2nd.Stamina), "a Stamina kit or food must not be scaled");
            Assert.IsFalse(PvpRules.HealsHealth(PropertyAttribute2nd.Mana), "a Mana kit or food must not be scaled");
            Assert.IsFalse(PvpRules.HealsHealth(PropertyAttribute2nd.MaxHealth), "HL2/HL3/HL5 compare the BOOSTER enum, which is Health, never MaxHealth");
        }

        // ================= choke-point entry =================

        [TestMethod]
        public void ArenaBound_HealIsScaled_AtEverySite_AndReported()
        {
            UseHealing(0.5);

            foreach (var point in HealPoints)
            {
                observed.Clear();

                Assert.AreEqual(250u, PvpRules.ApplyHealingMod(point, ArenaBoundPlayer(), 500u), $"{point} uint");
                Assert.AreEqual(1, observed.Count, $"{point}: one report");
                Assert.AreEqual(point, observed[0].Point);
                Assert.AreEqual(500.0, observed[0].Before);
                Assert.AreEqual(250.0, observed[0].After);
            }

            Assert.AreEqual(250, PvpRules.ApplyHealingMod(PvpChokePoint.HL1, ArenaBoundPlayer(), 500));
            Assert.AreEqual(61.0f, PvpRules.ApplyHealingMod(PvpChokePoint.HL2, ArenaBoundPlayer(), 123.0f), "floored so the site's Math.Round cannot lift it to 62");
            Assert.AreEqual(250.0, PvpRules.ApplyHealingMod(PvpChokePoint.HL4, ArenaBoundPlayer(), 500.0));
        }

        [TestMethod]
        public void Unbound_IsUnchanged_AndReadsNothing()
        {
            UseHealing(0.5);

            foreach (var point in HealPoints)
            {
                Assert.AreEqual(500u, PvpRules.ApplyHealingMod(point, UnboundPlayer(), 500u), point.ToString());
                Assert.AreEqual(500u, PvpRules.ApplyHealingMod(point, null, 500u), $"{point} null");
            }

            Assert.AreEqual(0, dialReads, "a unbound target must not read pvp_healing_mod");
            Assert.AreEqual(0, observed.Count);
        }

        /// <summary>
        /// The gate is on the HEALED player, so an ARENA-BOUND player's self-heal IS scaled - a PvP (source, target) pair
        /// would have excluded it. (This test always used an arena binding, never a PK timer; it stays positive.)
        /// </summary>
        [TestMethod]
        public void ArenaSelfHeal_IsScaled()
        {
            UseHealing(0.5);

            Assert.AreEqual(250, PvpRules.ApplyHealingMod(PvpChokePoint.HL1, ArenaBoundPlayer(), 500));
        }

        /// <summary>ARENA ONLY: a PK-timer-only player's heal (self-heal, kit, any site) is never scaled and reads nothing.</summary>
        [TestMethod]
        public void PkTimerOnly_HealIsUnchanged_AtEverySite_AndReadsNothing()
        {
            UseHealing(0.5);

            foreach (var point in HealPoints)
            {
                Assert.AreEqual(500u, PvpRules.ApplyHealingMod(point, PkTimerOnlyPlayer(), 500u), point.ToString());
                Assert.AreEqual(500, PvpRules.ApplyHealingMod(point, PkTimerOnlyPlayer(), 500), $"{point} int");
                Assert.AreEqual(500u, PvpRules.ApplyHealingModToRecipient(point, PkTimerOnlyPlayer(), 500u), $"{point} recipient");
            }

            Assert.AreEqual(0, dialReads, "a PK-timer-only player must not read pvp_healing_mod");
            Assert.AreEqual(0, observed.Count);
        }

        [TestMethod]
        public void ZeroMod_HealLandsForZero()
        {
            UseHealing(0.0);

            Assert.AreEqual(0u, PvpRules.ApplyHealingMod(PvpChokePoint.HL2, ArenaBoundPlayer(), 500u));
        }

        [TestMethod]
        public void BadValues_AreTheIdentity()
        {
            foreach (var bad in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -0.5 })
            {
                UseHealing(bad);

                Assert.AreEqual(500u, PvpRules.ApplyHealingMod(PvpChokePoint.HL2, ArenaBoundPlayer(), 500u), bad.ToString());
            }

            Assert.AreEqual(0, observed.Count);
        }

        [TestMethod]
        public void MasterSwitchOff_IsUnchanged()
        {
            UseHealing(0.5, enabled: false);

            Assert.AreEqual(500u, PvpRules.ApplyHealingMod(PvpChokePoint.HL2, ArenaBoundPlayer(), 500u));
            Assert.AreEqual(0, observed.Count);
        }

        [TestMethod]
        public void Defaults_AreTheIdentity_AndReportNothing()
        {
            foreach (var point in HealPoints)
            {
                Assert.AreEqual(123.456f, PvpRules.ApplyHealingMod(point, ArenaBoundPlayer(), 123.456f), $"{point}: the identity is exact, unfloored");
                Assert.AreEqual(500, PvpRules.ApplyHealingMod(point, ArenaBoundPlayer(), 500));
            }

            Assert.AreEqual(0, observed.Count);
        }

        [TestMethod]
        public void NonPositiveValues_AreUnchanged_AndReadNothing()
        {
            UseHealing(0.5);

            Assert.AreEqual(0, PvpRules.ApplyHealingMod(PvpChokePoint.HL3, ArenaBoundPlayer(), 0));
            Assert.AreEqual(-40, PvpRules.ApplyHealingMod(PvpChokePoint.HL3, ArenaBoundPlayer(), -40), "a harmful boost is not a heal");
            Assert.AreEqual(0, dialReads);
        }

        /// <summary>Read per call, never cached: a changed setting takes effect on the very next heal.</summary>
        [TestMethod]
        public void Dials_AreReadPerCall()
        {
            UseHealing(0.5);
            Assert.AreEqual(250u, PvpRules.ApplyHealingMod(PvpChokePoint.HL2, ArenaBoundPlayer(), 500u));

            UseHealing(2.0);
            Assert.AreEqual(1000u, PvpRules.ApplyHealingMod(PvpChokePoint.HL2, ArenaBoundPlayer(), 500u));
        }

        /// <summary>
        /// HL2 composes MULTIPLICATIVELY with the HK2 arena kit cap: GetHealAmount builds healBase from the HK2-capped
        /// kit mod, and HL2 then scales the finished heal. A 1v1 kit with HealkitMod 2.0 is capped to 1.5 by HK2, and
        /// pvp_healing_mod 0.5 halves the result: 400 skill x 1.5 x 0.5 = 300. The order of the two sites in
        /// Healer.cs is pinned in <see cref="HealingSites_ArePinned"/>.
        /// </summary>
        [TestMethod]
        public void HK2Cap_ThenHealingMod_ComposeMultiplicatively()
        {
            UseHealing(0.5);

            var cappedKitMod = PvpArenaOneVOneRules.CapHealkitRestorationBonus(2.0, 1.5);
            Assert.AreEqual(1.5, cappedKitMod, "HK2 caps the kit mod");

            var healAmount = 400.0f * (float)cappedKitMod;
            var healed = PvpRules.ApplyHealingMod(PvpChokePoint.HL2, ArenaBoundPlayer(), healAmount);

            Assert.AreEqual(400.0f * 1.5f * 0.5f, healed, "the capped kit heal is then halved - the two multiply");
        }

        /// <summary>
        /// HL5 scales the Drain GAIN, never the source's LOSS: the one HL5 call takes and assigns destVitalChange, and
        /// no line in WorldObject_Magic.cs hands srcVitalChange to ApplyHealingMod. Behaviorally: a 300-point loss that
        /// paid a 210-point gain pays 105 at pvp_healing_mod 0.5 while the 300 stays 300 (C4 would be the only lever on
        /// the loss).
        /// </summary>
        [TestMethod]
        public void DrainGain_IsScaled_SourceLossIsNot()
        {
            UseHealing(0.5);

            uint srcVitalChange = 300;
            uint destVitalChange = 210;

            destVitalChange = PvpRules.ApplyHealingMod(PvpChokePoint.HL5, ArenaBoundPlayer(), destVitalChange);

            Assert.AreEqual(105u, destVitalChange);
            Assert.AreEqual(300u, srcVitalChange);

            var lines = File.ReadAllLines(Path.Combine(FindSourceRoot(), "ACE.Server", "WorldObjects", "WorldObject_Magic.cs")).Select(CodeOnly).ToArray();

            Assert.AreEqual(0, lines.Count(l => l.Contains("ApplyHealingMod") && l.Contains("srcVitalChange")), "the drained source's loss must never go through the healing mod");
            Assert.AreEqual(1, lines.Count(l => l.Contains("PvpChokePoint.HL5,")), "exactly one HL5 (caster) site");
            Assert.AreEqual(1, lines.Count(l => l.Contains("PvpChokePoint.HL5F,")), "exactly one HL5F (per-fellow) site");
        }

        // ================= Drain surplus split: per-recipient scaling (HL5 / HL5F) =================

        /// <summary>
        /// Runs the SAME split HandleCastSpell_Transfer runs (DrainSurplusDistribution.ApplyShare then Distribute, on
        /// the UNSCALED pool), then pays each recipient through PvpRules.ApplyHealingModToRecipient keyed on that
        /// recipient, exactly as the HL5 / HL5F lines do. Returns (caster gain, fellow gain, the fellow's unscaled share).
        /// </summary>
        private static (uint Caster, uint Fellow, uint FellowUnscaled) SplitAndPay(Player caster, Player fellow)
        {
            uint pool = 300;          // destVitalChange after C4 and the cloak proc
            uint missingDest = 100;   // the caster's own missing health
            var fellowMissing = new List<uint> { 500 };

            var deliveredSurplus = DrainSurplusDistribution.ApplyShare(pool - missingDest, 1.0);
            var fellowShares = DrainSurplusDistribution.Distribute(deliveredSurplus, fellowMissing);
            var casterShare = missingDest;

            var casterGain = PvpRules.ApplyHealingModToRecipient(PvpChokePoint.HL5, caster, casterShare);
            var fellowGain = PvpRules.ApplyHealingModToRecipient(PvpChokePoint.HL5F, fellow, fellowShares[0]);

            return (casterGain, fellowGain, fellowShares[0]);
        }

        /// <summary>Case 1 (the review's bug): an ARENA-BOUND fellow of an UNBOUND caster is scaled; the caster is not.</summary>
        [TestMethod]
        public void DrainSplit_UnboundCaster_ArenaBoundFellow_FellowIsScaled()
        {
            UseHealing(0.5);

            var (caster, fellow, fellowUnscaled) = SplitAndPay(UnboundPlayer(), ArenaBoundPlayer());

            Assert.AreEqual(200u, fellowUnscaled, "the split ran on the unscaled pool: the whole 200 surplus fits the fellow's 500 missing");
            Assert.AreEqual(100u, caster, "the unbound caster's own share is untouched");
            Assert.AreEqual(100u, fellow, "the arena-bound fellow's share is halved");
            Assert.AreEqual(1, dialReads, "only the arena-bound fellow reads the setting");
            Assert.AreEqual(1, observed.Count);
            Assert.AreEqual(PvpChokePoint.HL5F, observed[0].Point);
        }

        /// <summary>A PK-timer-only fellow (outside the arena) of an arena-bound caster is untouched and reads nothing.</summary>
        [TestMethod]
        public void DrainSplit_ArenaCaster_PkTimerOnlyFellow_FellowIsUnchanged()
        {
            UseHealing(0.5);

            var (caster, fellow, fellowUnscaled) = SplitAndPay(ArenaBoundPlayer(), PkTimerOnlyPlayer());

            Assert.AreEqual(50u, caster);
            Assert.AreEqual(fellowUnscaled, fellow, "a fellow outside the arena gets the full share, PK timer or not");
            Assert.AreEqual(1, dialReads, "only the arena-bound caster reads the setting");
        }

        /// <summary>Case 2 (the review's bug): an UNBOUND fellow of an ARENA-BOUND caster is untouched and reads nothing; the caster is scaled.</summary>
        [TestMethod]
        public void DrainSplit_ArenaBoundCaster_UnboundFellow_FellowIsUnchanged()
        {
            UseHealing(0.5);

            var (caster, fellow, fellowUnscaled) = SplitAndPay(ArenaBoundPlayer(), UnboundPlayer());

            Assert.AreEqual(50u, caster, "the arena-bound caster's own share is halved");
            Assert.AreEqual(fellowUnscaled, fellow, "the unbound fellow gets the full, unscaled share");
            Assert.AreEqual(200u, fellow);
            Assert.AreEqual(1, dialReads, "the caster's read is the only read - the unbound fellow reads nothing");
            Assert.AreEqual(1, observed.Count);
            Assert.AreEqual(PvpChokePoint.HL5, observed[0].Point);
        }

        [TestMethod]
        public void Recipient_NonPlayer_IsUnchanged_AndReadsNothing()
        {
            UseHealing(0.5);

            var pet = (Pet)RuntimeHelpers.GetUninitializedObject(typeof(Pet));

            Assert.AreEqual(200u, PvpRules.ApplyHealingModToRecipient(PvpChokePoint.HL5F, pet, 200u), "a summon's share is never scaled");
            Assert.AreEqual(200u, PvpRules.ApplyHealingModToRecipient(PvpChokePoint.HL5F, null, 200u));
            Assert.AreEqual(0, dialReads);
        }

        // ================= call-site pins (source order; placement checks, not live heals) =================

        [TestMethod]
        public void HealingSites_ArePinned()
        {
            var root = FindSourceRoot();

            const string hl1 = "tryBoost = boost = PvpRules.ApplyHealingMod(PvpChokePoint.HL1, boostHealedPlayer, tryBoost);";
            const string hl1Gate = "if (spell.VitalDamageType == DamageType.Health && tryBoost > 0 && targetCreature is Player boostHealedPlayer)";
            const string hl2 = "healAmount = PvpRules.ApplyHealingMod(PvpChokePoint.HL2, target, healAmount);";
            const string hl3 = "boostValue = PvpRules.ApplyHealingMod(PvpChokePoint.HL3, player, boostValue);";
            const string hl4 = "tickAmountTotal = PvpRules.ApplyHealingMod(PvpChokePoint.HL4, tickHealedPlayer, tickAmountTotal);";
            const string hl5 = "destVitalChange = PvpRules.ApplyHealingModToRecipient(PvpChokePoint.HL5, destination, destVitalChange);";
            const string hl5f = "var fellowShare = PvpRules.ApplyHealingModToRecipient(PvpChokePoint.HL5F, fellow, fellowShares[i]);";

            // HL1: gated to a positive Health boost, before the Health vital write
            AssertOrder(root, "ACE.Server/WorldObjects/WorldObject_Magic.cs", hl1Gate, hl1);
            AssertOrder(root, "ACE.Server/WorldObjects/WorldObject_Magic.cs", hl1, "boost = targetCreature.UpdateVitalDelta(targetCreature.Health, tryBoost);");

            // HL2: Health kits only, AFTER the HK2 arena cap and the healing rating, before the final round
            AssertOrder(root, "ACE.Server/WorldObjects/Healer.cs", "if (PvpRules.HealsHealth(BoosterEnum))", hl2);
            AssertOrder(root, "ACE.Server/WorldObjects/Healer.cs", "var healkitMod = Pvp.Rules.PvpArenaOneVOneRules.ApplyHealkitRestorationCap1v1(target, (double)HealkitMod.Value);", hl2);
            AssertOrder(root, "ACE.Server/WorldObjects/Healer.cs", "healAmount *= ratingMod;", hl2);
            AssertOrder(root, "ACE.Server/WorldObjects/Healer.cs", hl2, "return (uint)Math.Round(healAmount);");

            // HL3: Health, positive boosts only, before the vital write
            AssertOrder(root, "ACE.Server/WorldObjects/Food.cs", "if (PvpRules.HealsHealth(BoosterEnum) && boostValue > 0)", hl3);
            AssertOrder(root, "ACE.Server/WorldObjects/Food.cs", hl3, "var vitalChange = (uint)Math.Abs(player.UpdateVitalDelta(vital, boostValue));");

            // HL4: positive ticks, after the healing rating, before the round and the vital write
            AssertOrder(root, "ACE.Server/WorldObjects/Managers/EnchantmentManager.cs", "tickAmountTotal *= creature.GetHealingRatingMod();", hl4);
            AssertOrder(root, "ACE.Server/WorldObjects/Managers/EnchantmentManager.cs", "if (tickAmountTotal > 0 && creature is Player tickHealedPlayer)", hl4);
            AssertOrder(root, "ACE.Server/WorldObjects/Managers/EnchantmentManager.cs", hl4, "var healAmount = creature.UpdateVitalDelta(creature.Health, (int)Math.Round(tickAmountTotal));");

            // HL5: the CASTER's Health gain only, AFTER C4 and the Drain cloak recompute (both rewrite destVitalChange
            // from the loss and would undo it) and AFTER the fellow split (which must run on the UNSCALED pool),
            // before the caster's vital write
            AssertOrder(root, "ACE.Server/WorldObjects/WorldObject_Magic.cs", "if (PvpRules.HealsHealth(spell.Destination))", hl5);
            AssertOrder(root, "ACE.Server/WorldObjects/WorldObject_Magic.cs", "var cappedDrain = PvpRules.ApplyDamageCap(PvpChokePoint.C4, this, transferSource, srcVitalChange);", hl5);
            AssertOrder(root, "ACE.Server/WorldObjects/WorldObject_Magic.cs", "Cloak.ShowMessage(targetCreature, this, srcVitalChange, reduced);", hl5);
            AssertOrder(root, "ACE.Server/WorldObjects/WorldObject_Magic.cs", "fellowShares = DrainSurplusDistribution.Distribute(deliveredSurplus, fellowMissing);", hl5);
            AssertOrder(root, "ACE.Server/WorldObjects/WorldObject_Magic.cs", hl5, "destVitalChange = (uint)destination.UpdateVitalDelta(destination.Health, destVitalChange);");

            // HL5F: each fellow's share, keyed on that fellow, inside the payout loop and before its vital write; the
            // write uses the SCALED share, so fellowGain (and the fellow's chat line) is the scaled amount
            AssertOrder(root, "ACE.Server/WorldObjects/WorldObject_Magic.cs", "var fellow = drainFellows[i];", hl5f);
            AssertOrder(root, "ACE.Server/WorldObjects/WorldObject_Magic.cs", hl5f, "var fellowGain = (uint)fellow.UpdateVitalDelta(fellow.Health, fellowShare);");
            AssertOrder(root, "ACE.Server/WorldObjects/WorldObject_Magic.cs", "var fellowGain = (uint)fellow.UpdateVitalDelta(fellow.Health, fellowShare);",
                "fellowPlayer.SendChatMessage(this, $\"You gain {fellowGain} points of health due to {Name} casting {spell.Name} on {targetCreature.Name}\", ChatMessageType.Magic);");
        }

        private static string CodeOnly(string raw)
        {
            var commentAt = raw.IndexOf("//", StringComparison.Ordinal);
            return (commentAt >= 0 ? raw.Substring(0, commentAt) : raw).Trim();
        }

        private static void AssertOrder(string root, string file, string first, string second)
        {
            var path = Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar));
            Assert.IsTrue(File.Exists(path), $"missing {path}");

            var lines = File.ReadAllLines(path).Select(CodeOnly).ToArray();

            var a = Enumerable.Range(0, lines.Length).Where(i => lines[i] == first).ToList();
            var b = Enumerable.Range(0, lines.Length).Where(i => lines[i] == second).ToList();

            Assert.AreEqual(1, a.Count, $"{file}: expected exactly one code line `{first}`");
            Assert.AreEqual(1, b.Count, $"{file}: expected exactly one code line `{second}`");
            Assert.IsTrue(a[0] < b[0], $"{file}: `{first}` (line {a[0] + 1}) must come before `{second}` (line {b[0] + 1})");
        }

        private static string FindSourceRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ACE.Server", "Entity", "DamageEvent.cs")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find ACE.Server/Entity/DamageEvent.cs by walking up from {AppContext.BaseDirectory}");
            return dir.FullName;
        }
    }
}
