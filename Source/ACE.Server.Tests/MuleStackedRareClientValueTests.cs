using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Network.GameMessages;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Network.Structure;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Discord bug 1559583372206936125, "Mules won't take Converter's Crystal rares": a single
    /// Value-0 rare dragged into a mule's sell pane fine, a STACK of them was refused.
    ///
    /// The refusal is client-side. The decompiled client's VendorProfile::InqAcceptability
    /// (clientleak acclient-AI-RE, src/AC/acshop/VendorProfile.c) divides the item's Value by its
    /// StackSize in unsigned integer arithmetic and refuses the drag when the quotient is 0 ("That
    /// item has no value and cannot be sold"). The server's zero-value spoof (WorldObject.ClientValue)
    /// used to report a flat 1 per STACK, so 1 / 5 = 0 for a stack of five. It now reports 1 per
    /// UNIT, i.e. StackSize.
    ///
    /// Every test here checks the quantity the client actually tests - the per-unit quotient - on
    /// each of the four channels that carry a Value to the client, rather than the raw number.
    /// </summary>
    [TestClass]
    public class MuleStackedRareClientValueTests
    {
        private static uint nextGuid = 0x7F100000;

        private static uint NextGuid() => Interlocked.Increment(ref nextGuid);

        /// <summary>
        /// The shape of Converter's Crystal (retail wcid 30226, read from the local ace_world): a Gem
        /// with ItemType Gem, Value 0, StackUnitValue 0, MaxStackSize 100 and a RareId.
        /// </summary>
        private static Gem MakeZeroValueRareStack(int stackSize)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 30226,
                WeenieType = WeenieType.Gem,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.Gem },
                    { PropertyInt.StackSize, stackSize },
                    { PropertyInt.MaxStackSize, 100 },
                    { PropertyInt.StackUnitValue, 0 },
                    { PropertyInt.Value, 0 },
                    { PropertyInt.StackUnitEncumbrance, 5 },
                    { PropertyInt.EncumbranceVal, 5 * stackSize },
                    { PropertyInt.RareId, 31 },
                },
                PropertiesString = new Dictionary<PropertyString, string>
                {
                    { PropertyString.Name, "Converter's Crystal" },
                },
            };

            return new Gem(weenie, new ObjectGuid(NextGuid()));
        }

        /// <summary>
        /// The value half of the client's InqAcceptability: Value / StackSize when StackSize is
        /// non-zero, both unsigned, and a zero quotient refuses the drag.
        /// </summary>
        private static uint ClientPerUnitValue(uint value, uint stackSize)
        {
            return stackSize != 0 ? value / stackSize : value;
        }

        private static readonly int[] StackSizes = { 1, 2, 5, 37, 99, 100 };

        [TestMethod]
        public void ClientValue_IsOnePerUnit_ForEveryStackSizeOfAZeroValueRare()
        {
            foreach (var size in StackSizes)
            {
                var rare = MakeZeroValueRareStack(size);

                Assert.AreEqual(size, rare.StackSize, "fixture stack size");
                Assert.AreEqual(0, rare.Value ?? 0, "the REAL Value stays 0 - the spoof is client-only");

                var clientValue = rare.ClientValue;

                Assert.AreEqual(size, clientValue, $"a zero-value stack of {size} is told 1 per unit");
                Assert.AreEqual(1u, ClientPerUnitValue((uint)clientValue, (uint)size), $"the client's per-unit quotient for a stack of {size} must be exactly 1, never 0");
            }
        }

        /// <summary>
        /// The pre-fix behavior, pinned as the discriminating case: a flat 1 on a stack of 5 is the
        /// exact value the client refused. If ClientValue ever regresses to a per-stack 1 this fails.
        /// </summary>
        [TestMethod]
        public void ClientValue_ForAStackOfFive_DoesNotRoundToZeroOnTheClient()
        {
            var rare = MakeZeroValueRareStack(5);

            Assert.AreEqual(0u, ClientPerUnitValue(1, 5), "control: the old flat 1 on a stack of 5 IS refused by the client's arithmetic");
            Assert.AreNotEqual(0u, ClientPerUnitValue((uint)rare.ClientValue, 5), "the spoofed Value must survive the client's division");
        }

        [TestMethod]
        public void SetStackSizeMessage_CarriesAPerUnitClientValue()
        {
            var rare = MakeZeroValueRareStack(1);

            // A merge onto the stack: the server sends GameMessageSetStackSize with the new size and
            // the item's Value, and the client overwrites its local Value with what it receives.
            rare.SetProperty(PropertyInt.StackSize, 7);

            var bytes = new GameMessageSetStackSize(rare).Data.ToArray();

            // Layout: uint opcode, byte sequence, uint guid, uint stack size, uint value.
            var sentStack = BitConverter.ToUInt32(bytes, 4 + 1 + 4);
            var sentValue = BitConverter.ToUInt32(bytes, 4 + 1 + 4 + 4);

            Assert.AreEqual(7u, sentStack, "stack size");
            Assert.AreEqual(7u, sentValue, "value is 1 per unit");
            Assert.AreEqual(1u, ClientPerUnitValue(sentValue, sentStack), "the client's quotient after a merge is 1");
        }

        [TestMethod]
        public void MuleOpenOverride_CarriesAPerUnitClientValue()
        {
            var single = MakeZeroValueRareStack(1);
            var stack = MakeZeroValueRareStack(12);

            var messages = PersonalVendor.BuildZeroValueOverrideMessages(new List<WorldObject> { single, stack });

            Assert.AreEqual(2, messages.Count, "both zero-value items are nudged");

            foreach (var (item, msg) in new[] { single, stack }.Zip(messages))
            {
                Assert.AreEqual(GameMessageOpcode.PublicUpdatePropertyInt, msg.Opcode);

                var bytes = msg.Data.ToArray();

                // Layout: uint opcode, byte sequence, uint guid, uint property, int value.
                Assert.AreEqual(item.Guid.Full, BitConverter.ToUInt32(bytes, 4 + 1), "names the item");
                Assert.AreEqual((uint)PropertyInt.Value, BitConverter.ToUInt32(bytes, 4 + 1 + 4), "property is Value");

                var sentValue = BitConverter.ToInt32(bytes, 4 + 1 + 4 + 4);

                Assert.AreEqual(item.StackSize ?? 1, sentValue, "the nudge is 1 per unit, not a literal 1");
                Assert.AreEqual(1u, ClientPerUnitValue((uint)sentValue, (uint)(item.StackSize ?? 1)), "the client's quotient is 1");
            }
        }

        [TestMethod]
        public void AppraiseResponse_CarriesAPerUnitClientValue()
        {
            var stack = MakeZeroValueRareStack(9);
            var ints = new Dictionary<PropertyInt, int> { { PropertyInt.Value, 0 } };

            AppraiseInfo.ApplyClientValue(stack, ints);

            Assert.AreEqual(9, ints[PropertyInt.Value], "an appraised zero-value stack of 9 reports 9");
            Assert.AreEqual(1u, ClientPerUnitValue((uint)ints[PropertyInt.Value], 9), "the client's quotient after an appraisal is 1");
        }

        /// <summary>
        /// The clamp in GetClientValue: a spoofed item whose StackSize is absent, zero or negative must
        /// still be told exactly 1, never 0 or a negative number (which the client would read as a huge
        /// unsigned Value), and a non-stackable zero-value item is told 1 as before.
        /// </summary>
        [TestMethod]
        public void ClientValue_ClampsToOne_ForAbsentZeroOrNegativeStackSize_AndForANonStackable()
        {
            var absent = MakeZeroValueRareStack(1);
            absent.RemoveProperty(PropertyInt.StackSize);
            Assert.IsNull(absent.StackSize, "fixture: StackSize absent");
            Assert.AreEqual(1, WorldObject.GetClientValue(absent), "StackSize null is told 1");

            var zero = MakeZeroValueRareStack(1);
            zero.SetProperty(PropertyInt.StackSize, 0);
            Assert.AreEqual(0, zero.StackSize, "fixture: StackSize 0");
            Assert.AreEqual(1, WorldObject.GetClientValue(zero), "StackSize 0 is told 1");

            var negative = MakeZeroValueRareStack(1);
            negative.SetProperty(PropertyInt.StackSize, -3);
            Assert.AreEqual(-3, negative.StackSize, "fixture: StackSize -3");
            Assert.AreEqual(1, WorldObject.GetClientValue(negative), "StackSize -3 is told 1");

            var nonStackableWeenie = new Weenie
            {
                WeenieClassId = 30240,
                WeenieType = WeenieType.Generic,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.Value, 0 } },
            };
            var nonStackable = new GenericObject(nonStackableWeenie, new ObjectGuid(NextGuid()));
            Assert.IsNull(nonStackable.StackSize, "fixture: a non-stackable carries no StackSize");
            Assert.AreEqual(1, WorldObject.GetClientValue(nonStackable), "a non-stackable zero-value item is told 1");
        }

        /// <summary>
        /// The fix must not touch anything the spoof never covered: an item with a real Value is sent
        /// exactly that Value, stack or not, and the server-side Value every price and gate reads is
        /// never modified.
        /// </summary>
        [TestMethod]
        public void ClientValue_LeavesARealValueStackUntouched()
        {
            var stack = MakeZeroValueRareStack(4);
            stack.SetProperty(PropertyInt.Value, 400);

            Assert.AreEqual(400, stack.ClientValue, "a real Value passes through");

            var ints = new Dictionary<PropertyInt, int> { { PropertyInt.Value, 400 } };
            AppraiseInfo.ApplyClientValue(stack, ints);
            Assert.AreEqual(400, ints[PropertyInt.Value], "appraisal leaves a real Value alone");

            Assert.AreEqual(0, PersonalVendor.BuildZeroValueOverrideMessages(new List<WorldObject> { stack }).Count, "no nudge for a real Value");
        }
    }
}
