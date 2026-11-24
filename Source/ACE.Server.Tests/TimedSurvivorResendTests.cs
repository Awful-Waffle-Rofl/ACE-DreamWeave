using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.WorldObjects;
using ACE.Server.WorldObjects.Managers;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Discord report 2026-10-29, "Tinkerer's Inspiration Removes Self-Buffs": a player self-casts the level-8
    /// Incantation of ... Tinkering Expertise Self spells (2 hours), uses the pedestal, and the 60 s Inspiration
    /// entries replace the self-casts on the client, which then show nothing for those spells once the minute is up.
    ///
    /// The server registry was right all along: the pedestal's entry sits at its own layer and the player's cast is
    /// never touched (EnchantmentLayerAllocationTests). The loss is in the client. Its
    /// CEnchantmentRegistry::AttemptToReplaceSpellInList (acclient 0x00593D00) runs whenever an UpdateEnchantment
    /// arrives for a (spell id, layer) the client does not already hold, and overwrites ANY timed entry whose spell id
    /// matches, comparing only the low 16 bits of the layered id. So the client holds one timed entry per spell id;
    /// the second layer replaced the first there, and removing it left the client with neither.
    ///
    /// The fix: after removing a timed entry, the server re-sends the best timed entry still registered for that
    /// spell id (EnchantmentManager.GetTimedSurvivorToResend). No Player is constructed here (it fails in this test
    /// host's static initializer), so the network side is pinned by source scan and the client by a small model
    /// of the three client functions involved.
    /// </summary>
    [TestClass]
    public class TimedSurvivorResendTests
    {
        /// <summary>Incantation of Armor Tinkering Expertise Self, one of the four spells the pedestal reuses.</summary>
        private const uint ArmorTinkeringSpellId = 4512;

        private const SpellCategory Category = SpellCategory.CraftingArmorTinkeringRaising;

        private const EnchantmentTypeFlags SkillBuff =
            EnchantmentTypeFlags.Skill | EnchantmentTypeFlags.Additive | EnchantmentTypeFlags.Beneficial;

        private static WorldObject CreateBareWorldObject(uint id)
        {
            var biota = new Biota
            {
                Id = id,
                WeenieClassId = 1,
                WeenieType = WeenieType.Generic,
            };

            return new GenericObject(biota);
        }

        private static PropertiesEnchantmentRegistry AddEntry(WorldObject wo, uint spellId, ushort layer, uint powerLevel, double duration, double startTime, uint caster, float value, SpellCategory category = Category)
        {
            var entry = new PropertiesEnchantmentRegistry
            {
                SpellId = (int)spellId,
                SpellCategory = category,
                LayerId = layer,
                PowerLevel = powerLevel,
                StartTime = startTime,
                Duration = duration,
                CasterObjectId = caster,
                StatModType = SkillBuff,
                StatModKey = (uint)Skill.ArmorTinkering,
                StatModValue = value,
                EnchantmentCategory = (uint)SpellType.Enchantment,
            };

            wo.Biota.PropertiesEnchantmentRegistry.AddEnchantment(entry, wo.BiotaDatabaseLock);

            return entry;
        }

        /// <summary>
        /// The parts of the client's CEnchantmentRegistry that decide what survives an add or a remove, for timed and
        /// untimed entries alike. Entries are identified by the layered id, (layer &lt;&lt; 16) | spell id.
        ///   - UpdateEnchantment first tries ReplaceEnchantmentInList (0x00593BB0): overwrite the entry with the SAME
        ///     layered id (ContentProfile::operator== compares _id).
        ///   - Failing that, AddEnchantmentToList (0x00593DF0) calls AttemptToReplaceSpellInList (0x00593D00): when the
        ///     incoming duration is positive, overwrite the first entry whose SPELL id matches and whose duration is
        ///     positive. Only failing that is the entry inserted.
        ///   - RemoveEnchantment (0x005944E0) removes by the exact layered id.
        /// </summary>
        private sealed class ClientRegistryModel
        {
            public readonly List<PropertiesEnchantmentRegistry> Entries = new List<PropertiesEnchantmentRegistry>();

            public void Update(PropertiesEnchantmentRegistry sent)
            {
                var copy = Copy(sent);

                var exact = Entries.FindIndex(e => e.SpellId == sent.SpellId && e.LayerId == sent.LayerId);
                if (exact >= 0)
                {
                    Entries[exact] = copy;
                    return;
                }

                if (sent.Duration > 0)
                {
                    var sameSpell = Entries.FindIndex(e => e.SpellId == sent.SpellId && e.Duration > 0);
                    if (sameSpell >= 0)
                    {
                        Entries[sameSpell] = copy;
                        return;
                    }
                }

                Entries.Insert(0, copy);
            }

            public void Remove(PropertiesEnchantmentRegistry sent)
            {
                Entries.RemoveAll(e => e.SpellId == sent.SpellId && e.LayerId == sent.LayerId);
            }

            private static PropertiesEnchantmentRegistry Copy(PropertiesEnchantmentRegistry e)
            {
                return new PropertiesEnchantmentRegistry
                {
                    SpellId = e.SpellId,
                    LayerId = e.LayerId,
                    SpellCategory = e.SpellCategory,
                    PowerLevel = e.PowerLevel,
                    StartTime = e.StartTime,
                    Duration = e.Duration,
                    CasterObjectId = e.CasterObjectId,
                    StatModType = e.StatModType,
                    StatModKey = e.StatModKey,
                    StatModValue = e.StatModValue,
                };
            }
        }

        /// <summary>
        /// The reported sequence end to end: own 2-hour cast, pedestal, the pedestal's layer expires. The server must
        /// keep the player's entry, and what it sends must leave the client holding that entry again.
        /// </summary>
        [TestMethod]
        public void InspirationLayerExpiring_ResendsThePlayersOwnCast_SoTheClientShowsItAgain()
        {
            var player = CreateBareWorldObject(0x50000101);
            var station = CreateBareWorldObject(0x70000101);
            var client = new ClientRegistryModel();

            var own = AddEntry(player, ArmorTinkeringSpellId, 1, 400, 7200, -600, player.Guid.Full, 45f);
            client.Update(own);

            var inspiration = player.EnchantmentManager.AddClassAbilityDebuff(ArmorTinkeringSpellId, 401, station,
                Category, SkillBuff, (uint)Skill.ArmorTinkering, 60f, 60.0, refreshOnlyOwnCaster: true);
            client.Update(inspiration);

            Assert.AreEqual(2, player.Biota.PropertiesEnchantmentRegistry.Count, "the server keeps both layers");
            Assert.AreEqual(1, client.Entries.Count, "precondition: the client collapses two timed layers of one spell into one slot");
            Assert.AreEqual(60f, client.Entries[0].StatModValue, "precondition: the pedestal's layer overwrote the player's own on the client");

            // the pedestal's layer expires: HeartBeat hands it to Remove
            player.EnchantmentManager.Remove(inspiration);
            client.Remove(inspiration);

            Assert.AreEqual(0, client.Entries.Count, "precondition: without a re-send the client now shows nothing for the spell");

            var resend = player.EnchantmentManager.GetTimedSurvivorToResend(inspiration);

            Assert.AreSame(own, resend, "the player's own cast must be re-sent after the pedestal's layer goes");

            client.Update(resend);

            Assert.AreEqual(1, client.Entries.Count);
            Assert.AreEqual((ushort)1, client.Entries[0].LayerId);
            Assert.AreEqual(45f, client.Entries[0].StatModValue);
            Assert.AreEqual(player.Guid.Full, client.Entries[0].CasterObjectId);

            Assert.AreEqual(1, player.Biota.PropertiesEnchantmentRegistry.Count);
            Assert.AreSame(own, player.Biota.PropertiesEnchantmentRegistry.Single(), "the server's own entry is untouched");
            Assert.AreEqual(7200, own.Duration);
            Assert.AreEqual(-600, own.StartTime);
        }

        /// <summary>
        /// The other order: the player's own cast expires first while the pedestal's layer is still up. Whichever slot
        /// the client was holding, the pedestal's layer has to be what it holds afterwards.
        /// </summary>
        [TestMethod]
        public void OwnCastExpiringUnderInspiration_ResendsTheInspirationLayer()
        {
            var player = CreateBareWorldObject(0x50000102);
            var station = CreateBareWorldObject(0x70000102);

            var own = AddEntry(player, ArmorTinkeringSpellId, 1, 400, 7200, -7190, player.Guid.Full, 45f);

            var inspiration = player.EnchantmentManager.AddClassAbilityDebuff(ArmorTinkeringSpellId, 401, station,
                Category, SkillBuff, (uint)Skill.ArmorTinkering, 60f, 60.0, refreshOnlyOwnCaster: true);

            // the player recast during the minute, so the client's one slot holds the player's layer, not the pedestal's
            var client = new ClientRegistryModel();
            client.Update(inspiration);
            client.Update(own);

            player.EnchantmentManager.Remove(own);
            client.Remove(own);

            var resend = player.EnchantmentManager.GetTimedSurvivorToResend(own);

            Assert.AreSame(inspiration, resend);

            client.Update(resend);

            Assert.AreEqual(1, client.Entries.Count);
            Assert.AreEqual(inspiration.LayerId, client.Entries[0].LayerId);
            Assert.AreEqual(60f, client.Entries[0].StatModValue);
        }

        [TestMethod]
        public void GetTimedSurvivorToResend_WithNoOtherLayerOfThatSpell_ReturnsNull()
        {
            var player = CreateBareWorldObject(0x50000103);

            var own = AddEntry(player, ArmorTinkeringSpellId, 1, 400, 7200, 0, player.Guid.Full, 45f);
            AddEntry(player, 4640, 1, 400, 7200, 0, player.Guid.Full, 45f, SpellCategory.CraftingWeaponTinkeringRaising);

            player.EnchantmentManager.Remove(own);

            Assert.IsNull(player.EnchantmentManager.GetTimedSurvivorToResend(own), "another spell id is never re-sent");
        }

        [TestMethod]
        public void GetTimedSurvivorToResend_ForAnUntimedRemovedEntry_ReturnsNull()
        {
            // an item spell (Duration -1) never displaced anything on the client: AttemptToReplaceSpellInList only runs
            // for a positive incoming duration, and only matches a positive existing one
            var player = CreateBareWorldObject(0x50000104);
            var item = CreateBareWorldObject(0x80000104);

            AddEntry(player, ArmorTinkeringSpellId, 1, 400, 7200, 0, player.Guid.Full, 45f);
            var fromItem = AddEntry(player, ArmorTinkeringSpellId, 2, 400, -1, 0, item.Guid.Full, 45f);

            player.EnchantmentManager.Remove(fromItem);

            Assert.IsNull(player.EnchantmentManager.GetTimedSurvivorToResend(fromItem));
        }

        [TestMethod]
        public void GetTimedSurvivorToResend_IgnoresAnUntimedSurvivor()
        {
            var player = CreateBareWorldObject(0x50000105);
            var item = CreateBareWorldObject(0x80000105);

            var timed = AddEntry(player, ArmorTinkeringSpellId, 1, 400, 60, 0, player.Guid.Full, 60f);
            AddEntry(player, ArmorTinkeringSpellId, 2, 400, -1, 0, item.Guid.Full, 45f);

            player.EnchantmentManager.Remove(timed);

            Assert.IsNull(player.EnchantmentManager.GetTimedSurvivorToResend(timed), "the client never lost the untimed layer, so there is nothing to re-send");
        }

        [TestMethod]
        public void GetTimedSurvivorToResend_ForACooldown_ReturnsNull()
        {
            var player = CreateBareWorldObject(0x50000106);
            var cooldownCategory = (SpellCategory)EnchantmentManager.SpellCategory_Cooldown;

            var first = AddEntry(player, 0x8001, 1, 0, 30, 0, 0x80000106, 0f, cooldownCategory);
            AddEntry(player, 0x8001, 2, 0, 30, 0, 0x80000107, 0f, cooldownCategory);

            Assert.IsNull(player.EnchantmentManager.GetTimedSurvivorToResend(first));
        }

        [TestMethod]
        public void GetTopTimedEntryForSpell_PrefersPowerLevelThenTheMostRecentStart()
        {
            var player = CreateBareWorldObject(0x50000107);

            AddEntry(player, ArmorTinkeringSpellId, 1, 400, 7200, -10, 0x50000107, 45f);
            var newerSamePower = AddEntry(player, ArmorTinkeringSpellId, 2, 400, 7200, -5, 0x70000001, 45f);

            var registry = player.Biota.PropertiesEnchantmentRegistry;

            Assert.AreSame(newerSamePower, registry.GetTopTimedEntryForSpell((int)ArmorTinkeringSpellId, player.BiotaDatabaseLock));

            var stronger = AddEntry(player, ArmorTinkeringSpellId, 3, 401, 60, -50, 0x70000002, 60f);

            Assert.AreSame(stronger, registry.GetTopTimedEntryForSpell((int)ArmorTinkeringSpellId, player.BiotaDatabaseLock));
        }

        /// <summary>
        /// The network half cannot run here (no Player), so pin that every removal path re-sends, and where. Each
        /// anchor is the exact method head; the body is matched with comments stripped and whitespace collapsed, and
        /// the pattern fixes the call as a plain statement right after the removal message, inside the
        /// Player != null block. A commented-out or conditionally skipped call does not match.
        /// </summary>
        [TestMethod]
        [DataRow("public virtual void Remove(PropertiesEnchantmentRegistry entry, bool sound = true)",
            @"if \(Player != null\) \{ var layer = [^{}]*; Player\.Session\.Network\.EnqueueSend\(new GameEventMagicRemoveEnchantment\([^{}]*; ResendTimedSurvivor\(entry\); \} else \{")]
        [DataRow("public virtual void Dispel(PropertiesEnchantmentRegistry entry)",
            @"if \(Player != null\) \{ Player\.Session\.Network\.EnqueueSend\(new GameEventMagicDispelEnchantment\(Player\.Session, \(ushort\)entry\.SpellId, entry\.LayerId\)\); ResendTimedSurvivor\(entry\); \} \}$")]
        [DataRow("public virtual void Dispel(List<PropertiesEnchantmentRegistry> entries)",
            @"if \(Player != null\) \{ Player\.Session\.Network\.EnqueueSend\(new GameEventMagicDispelMultipleEnchantments\(Player\.Session, entries\)\); var resent = new HashSet<int>\(\); foreach \(var entry in entries\) \{ if \(!resent\.Contains\(entry\.SpellId\) && ResendTimedSurvivor\(entry\)\) resent\.Add\(entry\.SpellId\); \} \} \}$")]
        public void RemovalPaths_ResendTheTimedSurvivor(string methodHead, string pattern)
        {
            var body = Normalize(MethodBody(ReadEnchantmentManager(), methodHead));

            Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(body, pattern),
                $"{methodHead} must re-send the timed survivor right after its removal message.\nbody: {body}");
        }

        /// <summary>
        /// ResendTimedSurvivor must actually send what GetTimedSurvivorToResend picked, as an UpdateEnchantment.
        /// </summary>
        [TestMethod]
        public void ResendTimedSurvivor_SendsTheSelectedSurvivorAsAnUpdate()
        {
            var body = Normalize(MethodBody(ReadEnchantmentManager(), "private bool ResendTimedSurvivor(PropertiesEnchantmentRegistry removed)"));

            const string expected =
                "{ if (Player == null) return false; " +
                "var survivor = GetTimedSurvivorToResend(removed); " +
                "if (survivor == null) return false; " +
                "Player.Session.Network.EnqueueSend(new GameEventMagicUpdateEnchantment(Player.Session, new Enchantment(Player, survivor))); " +
                "return true; }";

            Assert.AreEqual(expected, body);
        }

        /// <summary>Drops // and /// comments, then collapses all whitespace runs to one space.</summary>
        private static string Normalize(string code)
        {
            var noComments = System.Text.RegularExpressions.Regex.Replace(code, @"//[^\r\n]*", "");

            return System.Text.RegularExpressions.Regex.Replace(noComments, @"\s+", " ").Trim();
        }

        private static string MethodBody(string text, string methodHead)
        {
            var head = text.IndexOf(methodHead, StringComparison.Ordinal);
            Assert.IsTrue(head >= 0, $"method head not found: {methodHead}");

            var open = text.IndexOf('{', head);
            var depth = 0;

            for (var i = open; i < text.Length; i++)
            {
                if (text[i] == '{')
                    depth++;
                else if (text[i] == '}' && --depth == 0)
                    return text.Substring(open, i - open + 1);
            }

            Assert.Fail($"unbalanced braces after {methodHead}");
            return null;
        }

        private static string ReadEnchantmentManager()
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = System.IO.Path.Combine(dir.FullName, "ACE.Server", "WorldObjects", "Managers", "EnchantmentManager.cs");
                if (System.IO.File.Exists(candidate))
                    return System.IO.File.ReadAllText(candidate);
                dir = dir.Parent;
            }

            Assert.Fail("Could not find ACE.Server/WorldObjects/Managers/EnchantmentManager.cs by walking up from " + AppContext.BaseDirectory);
            return null;
        }
    }
}
