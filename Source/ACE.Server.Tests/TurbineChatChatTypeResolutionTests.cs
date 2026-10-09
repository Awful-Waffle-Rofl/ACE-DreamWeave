using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Network.Handlers;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Covers TurbineChatHandler.TryResolveChatType, the pure helper that decides what chat type a
    /// TurbineChat packet is delivered, relayed to Discord, and filed in analytics under - and
    /// whether it should be delivered at all. Accessed via InternalsVisibleTo
    /// (ACE.Server.csproj:15); ACE.Server.Tests cannot construct a live Player or Session, which is
    /// why this logic lives in a pure static helper instead of being tested end-to-end through
    /// TurbineChatReceived.
    ///
    /// Two bugs this guards against, both the same class (trusting the raw client chatType instead
    /// of deriving it from the trusted channel id):
    ///
    /// 1. Only ASYNCMETHOD_SENDTOROOMBYNAME and ASYNCMETHOD_SENDTOROOMBYID used to re-derive
    ///    chatType from trusted data. Every other dispatch type left the raw, client-supplied
    ///    chatType untouched, so a crafted packet with channelID = Allegiance and chatType =
    ///    General was delivered to the allegiance but relayed to the public Discord General
    ///    webhook and filed in analytics as "general".
    ///
    /// 2. After fixing (1) by folding every non-BYNAME dispatch type through the BYID rules, those
    ///    rules had no final "else": channelId 0 matches none of the known rooms, so
    ///    adjustedChatType stayed at its client-supplied initial value for BYID and for every other
    ///    non-BYNAME dispatch type. Delivery for channel 0 falls into the "available to all
    ///    players" branch, which no per-channel gate filters, so this reproduced bug (1) for
    ///    channel 0 specifically. TryResolveChatType now refuses (returns false for) any channelId
    ///    that does not name a known room - channelId 0 is the only such value today - rather than
    ///    resolving it from the client value.
    /// </summary>
    [TestClass]
    public class TurbineChatChatTypeResolutionTests
    {
        // ----- BYNAME: every ChatType row in the existing switch, plus the default arm -----

        [TestMethod]
        public void ByName_MapsEveryChatTypeToItsChannelAndBack()
        {
            var cases = new (ChatType clientChatType, uint expectedChannel, ChatType expectedChatType)[]
            {
                (ChatType.Allegiance,    TurbineChatChannel.Allegiance,   (ChatType)TurbineChatChannel.Allegiance),
                (ChatType.General,       TurbineChatChannel.General,      (ChatType)TurbineChatChannel.General),
                (ChatType.Trade,         TurbineChatChannel.Trade,        (ChatType)TurbineChatChannel.Trade),
                (ChatType.LFG,           TurbineChatChannel.LFG,          (ChatType)TurbineChatChannel.LFG),
                (ChatType.Roleplay,      TurbineChatChannel.Roleplay,     (ChatType)TurbineChatChannel.Roleplay),
                (ChatType.Society,       TurbineChatChannel.Society,      (ChatType)TurbineChatChannel.Society),
                (ChatType.SocietyCelHan, TurbineChatChannel.Society,      (ChatType)TurbineChatChannel.Society),
                (ChatType.SocietyEldWeb, TurbineChatChannel.Society,      (ChatType)TurbineChatChannel.Society),
                (ChatType.SocietyRadBlo, TurbineChatChannel.Society,      (ChatType)TurbineChatChannel.Society),
                (ChatType.Olthoi,        TurbineChatChannel.Olthoi,       (ChatType)TurbineChatChannel.Olthoi),
            };

            foreach (var (clientChatType, expectedChannel, expectedChatType) in cases)
            {
                var success = TurbineChatHandler.TryResolveChatType(ChatNetworkBlobDispatchType.ASYNCMETHOD_SENDTOROOMBYNAME, channelId: 0, clientChatType, out var resolved, out var adjustedChannelId);

                Assert.IsTrue(success, $"BYNAME must always resolve (clientChatType={clientChatType})");
                Assert.AreEqual(expectedChannel, adjustedChannelId, $"channel mismatch for clientChatType={clientChatType}");
                Assert.AreEqual(expectedChatType, resolved, $"chatType mismatch for clientChatType={clientChatType}");
            }
        }

        [TestMethod]
        public void ByName_DefaultArm_UnmappedChatTypeFallsBackToGeneral()
        {
            var success = TurbineChatHandler.TryResolveChatType(ChatNetworkBlobDispatchType.ASYNCMETHOD_SENDTOROOMBYNAME, channelId: 0, (ChatType)999999, out var resolved, out var adjustedChannelId);

            Assert.IsTrue(success);
            Assert.AreEqual(TurbineChatChannel.General, adjustedChannelId);
            Assert.AreEqual((ChatType)TurbineChatChannel.General, resolved);
        }

        // ----- BYID: General/Trade/LFG/Roleplay/Society/Olthoi/Allegiance, plus an allegiance id above Olthoi -----

        [TestMethod]
        public void ById_MapsEveryKnownChannelToItsChatType()
        {
            var cases = new (uint channelId, ChatType expectedChatType)[]
            {
                (TurbineChatChannel.General,   ChatType.General),
                (TurbineChatChannel.Trade,     ChatType.Trade),
                (TurbineChatChannel.LFG,       ChatType.LFG),
                (TurbineChatChannel.Roleplay,  ChatType.Roleplay),
                (TurbineChatChannel.Society,   ChatType.Society),
                (TurbineChatChannel.Olthoi,    ChatType.Olthoi),
                (TurbineChatChannel.Allegiance, ChatType.Allegiance),
            };

            foreach (var (channelId, expectedChatType) in cases)
            {
                // client-supplied chatType is deliberately wrong here to prove it is ignored
                var success = TurbineChatHandler.TryResolveChatType(ChatNetworkBlobDispatchType.ASYNCMETHOD_SENDTOROOMBYID, channelId, ChatType.General, out var resolved, out var adjustedChannelId);

                Assert.IsTrue(success, $"channelId={channelId}");
                Assert.AreEqual(channelId, adjustedChannelId, $"BYID must never adjust the channel id (channelId={channelId})");
                Assert.AreEqual(expectedChatType, resolved, $"chatType mismatch for channelId={channelId}");
            }
        }

        [TestMethod]
        public void ById_AllegianceIdAboveOlthoi_ResolvesToAllegiance()
        {
            var allegianceChannelId = TurbineChatChannel.Olthoi + 1;

            var success = TurbineChatHandler.TryResolveChatType(ChatNetworkBlobDispatchType.ASYNCMETHOD_SENDTOROOMBYID, allegianceChannelId, ChatType.General, out var resolved, out var adjustedChannelId);

            Assert.IsTrue(success);
            Assert.AreEqual(allegianceChannelId, adjustedChannelId);
            Assert.AreEqual(ChatType.Allegiance, resolved);
        }

        [TestMethod]
        public void ById_ChannelZero_IsRefused()
        {
            foreach (var clientChatType in new[] { ChatType.General, ChatType.Allegiance })
            {
                var success = TurbineChatHandler.TryResolveChatType(ChatNetworkBlobDispatchType.ASYNCMETHOD_SENDTOROOMBYID, channelId: 0, clientChatType, out _, out _);

                Assert.IsFalse(success, $"channelId 0 is not a known room (clientChatType={clientChatType})");
            }
        }

        // ----- The spoof case: every dispatch type that is neither BYNAME nor BYID must derive
        // ----- chatType purely from the channel id, never from the client-supplied chatType.

        private static IEnumerable<ChatNetworkBlobDispatchType> NonByNameNonByIdDispatchTypes()
        {
            foreach (ChatNetworkBlobDispatchType dispatchType in Enum.GetValues(typeof(ChatNetworkBlobDispatchType)))
            {
                if (dispatchType == ChatNetworkBlobDispatchType.ASYNCMETHOD_SENDTOROOMBYNAME || dispatchType == ChatNetworkBlobDispatchType.ASYNCMETHOD_SENDTOROOMBYID)
                    continue;

                yield return dispatchType;
            }
        }

        [TestMethod]
        public void Spoof_NonByNameNonByIdDispatchTypes_AreEnumerated()
        {
            // Guards the enumeration itself: if a new ChatNetworkBlobDispatchType value is ever
            // added, this fails loudly instead of the spoof tests below silently covering fewer
            // values than the enum actually has.
            var found = new List<ChatNetworkBlobDispatchType>(NonByNameNonByIdDispatchTypes());

            CollectionAssert.Contains(found, ChatNetworkBlobDispatchType.ASYNCMETHOD_UNKNOWN);
            CollectionAssert.Contains(found, ChatNetworkBlobDispatchType.ASYNCMETHOD_CREATEROOM);
            CollectionAssert.Contains(found, ChatNetworkBlobDispatchType.ASYNCMETHOD_INVITECLIENTTOROOMBYID);
            CollectionAssert.Contains(found, ChatNetworkBlobDispatchType.ASYNCMETHOD_EJECTCLIENTFROMROOMBYID);
            Assert.AreEqual(4, found.Count, "unexpected number of non-BYNAME/BYID dispatch types - update this test for the new value");
        }

        [TestMethod]
        public void Spoof_AllegianceChannelWithClientGeneral_ResolvesToAllegiance_ForEveryOtherDispatchType()
        {
            foreach (var dispatchType in NonByNameNonByIdDispatchTypes())
            {
                var success = TurbineChatHandler.TryResolveChatType(dispatchType, TurbineChatChannel.Allegiance, ChatType.General, out var resolved, out var adjustedChannelId);

                Assert.IsTrue(success, $"dispatchType={dispatchType}");
                Assert.AreEqual(TurbineChatChannel.Allegiance, adjustedChannelId, $"dispatchType={dispatchType}");
                Assert.AreEqual(ChatType.Allegiance, resolved, $"spoof not blocked for dispatchType={dispatchType}: client chatType=General leaked through for an Allegiance channel");
            }
        }

        [TestMethod]
        public void Spoof_SocietyChannelWithClientGeneral_ResolvesToSociety_ForEveryOtherDispatchType()
        {
            foreach (var dispatchType in NonByNameNonByIdDispatchTypes())
            {
                var success = TurbineChatHandler.TryResolveChatType(dispatchType, TurbineChatChannel.Society, ChatType.General, out var resolved, out var adjustedChannelId);

                Assert.IsTrue(success, $"dispatchType={dispatchType}");
                Assert.AreEqual(TurbineChatChannel.Society, adjustedChannelId, $"dispatchType={dispatchType}");
                Assert.AreEqual(ChatType.Society, resolved, $"spoof not blocked for dispatchType={dispatchType}: client chatType=General leaked through for a Society channel");
            }
        }

        [TestMethod]
        public void Spoof_ChannelZero_IsRefused_ForEveryOtherDispatchType()
        {
            foreach (var dispatchType in NonByNameNonByIdDispatchTypes())
            {
                foreach (var clientChatType in new[] { ChatType.General, ChatType.Allegiance })
                {
                    var success = TurbineChatHandler.TryResolveChatType(dispatchType, channelId: 0, clientChatType, out _, out _);

                    Assert.IsFalse(success, $"channelId 0 is not a known room (dispatchType={dispatchType}, clientChatType={clientChatType})");
                }
            }
        }

        /// <summary>
        /// Reproduces the ResolveChatType logic as it existed at PR #1147's initial commit
        /// (bec3702b4) - after fixing bug (1) above but before this refusal fix - to prove the
        /// channel-0 refusal tests above would have failed against it. That logic folded BYID and
        /// every other non-BYNAME dispatch type into a single "else" branch with no final else arm,
        /// so channelId 0 fell through every check and adjustedChatType stayed at its
        /// client-supplied initial value.
        /// </summary>
        private static ChatType PreRefusalFixResolveChatType(ChatNetworkBlobDispatchType dispatchType, uint channelId, ChatType clientChatType, out uint adjustedChannelId)
        {
            adjustedChannelId = channelId;
            var adjustedChatType = clientChatType;

            if (dispatchType == ChatNetworkBlobDispatchType.ASYNCMETHOD_SENDTOROOMBYNAME)
            {
                adjustedChannelId = clientChatType switch
                {
                    ChatType.Allegiance     => TurbineChatChannel.Allegiance,
                    ChatType.General        => TurbineChatChannel.General,
                    ChatType.Trade          => TurbineChatChannel.Trade,
                    ChatType.LFG            => TurbineChatChannel.LFG,
                    ChatType.Roleplay       => TurbineChatChannel.Roleplay,
                    ChatType.Society        => TurbineChatChannel.Society,
                    ChatType.SocietyCelHan  => TurbineChatChannel.Society,
                    ChatType.SocietyEldWeb  => TurbineChatChannel.Society,
                    ChatType.SocietyRadBlo  => TurbineChatChannel.Society,
                    ChatType.Olthoi         => TurbineChatChannel.Olthoi,
                    _                       => TurbineChatChannel.General
                };

                adjustedChatType = (ChatType)adjustedChannelId;
            }
            else // BYID, and every other dispatch type
            {
                if (channelId > TurbineChatChannel.Olthoi || channelId == TurbineChatChannel.Allegiance)
                    adjustedChatType = ChatType.Allegiance;
                else if (channelId == TurbineChatChannel.Olthoi)
                    adjustedChatType = ChatType.Olthoi;
                else if (channelId >= TurbineChatChannel.Society)
                    adjustedChatType = ChatType.Society;
                else
                {
                    if (channelId == TurbineChatChannel.General)
                        adjustedChatType = ChatType.General;
                    else if (channelId == TurbineChatChannel.Trade)
                        adjustedChatType = ChatType.Trade;
                    else if (channelId == TurbineChatChannel.LFG)
                        adjustedChatType = ChatType.LFG;
                    else if (channelId == TurbineChatChannel.Roleplay)
                        adjustedChatType = ChatType.Roleplay;
                    // else: adjustedChatType is left at its client-supplied initial value - the bug.
                }
            }

            return adjustedChatType;
        }

        [TestMethod]
        public void Spoof_ChannelZero_LeaksClientChatType_UnderThePreRefusalFixLogic()
        {
            // This is the failure the channel-0 refusal tests above guard against: run the exact
            // same inputs through the pre-fix logic and show the client-supplied chatType survives
            // untouched for channel 0, across BYID and every other non-BYNAME dispatch type.
            var allNonByNameDispatchTypes = new[] { ChatNetworkBlobDispatchType.ASYNCMETHOD_SENDTOROOMBYID }
                .Concat(NonByNameNonByIdDispatchTypes());

            foreach (var dispatchType in allNonByNameDispatchTypes)
            {
                foreach (var clientChatType in new[] { ChatType.General, ChatType.Allegiance })
                {
                    var leaked = PreRefusalFixResolveChatType(dispatchType, channelId: 0, clientChatType, out var adjustedChannelId);

                    Assert.AreEqual(0u, adjustedChannelId, $"dispatchType={dispatchType}");
                    Assert.AreEqual(clientChatType, leaked, $"dispatchType={dispatchType}: pre-fix logic was expected to leak the raw client chatType for channel 0");
                }
            }
        }

        // ----- Consistency: for every enum value, the resolved chatType for a global channel id
        // ----- matches that channel, regardless of the client-supplied chatType. -----

        [TestMethod]
        public void Consistency_GlobalChannelIds_ResolveToTheSameChatType_AcrossEveryDispatchType()
        {
            var globalChannels = new (uint channelId, ChatType expectedChatType)[]
            {
                (TurbineChatChannel.General,  ChatType.General),
                (TurbineChatChannel.Trade,    ChatType.Trade),
                (TurbineChatChannel.LFG,      ChatType.LFG),
                (TurbineChatChannel.Roleplay, ChatType.Roleplay),
            };

            foreach (ChatNetworkBlobDispatchType dispatchType in Enum.GetValues(typeof(ChatNetworkBlobDispatchType)))
            {
                if (dispatchType == ChatNetworkBlobDispatchType.ASYNCMETHOD_SENDTOROOMBYNAME)
                    continue; // BYNAME derives the channel id FROM chatType, so it is not channel-id-driven the same way

                foreach (var (channelId, expectedChatType) in globalChannels)
                {
                    // deliberately spoof a mismatched client chatType (Allegiance) to prove it is never trusted
                    var success = TurbineChatHandler.TryResolveChatType(dispatchType, channelId, ChatType.Allegiance, out var resolved, out var adjustedChannelId);

                    Assert.IsTrue(success, $"dispatchType={dispatchType}, channelId={channelId}");
                    Assert.AreEqual(channelId, adjustedChannelId, $"dispatchType={dispatchType}, channelId={channelId}");
                    Assert.AreEqual(expectedChatType, resolved, $"dispatchType={dispatchType}, channelId={channelId}");
                }
            }
        }
    }
}
