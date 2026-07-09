using HermesProxy.Enums;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using HermesProxy.World.Server.Packets;
using System;
using System.Collections.Generic;
using System.Threading;

namespace HermesProxy.World.Client
{
    public partial class WorldClient
    {
        // Legacy server page size for the owned-items list (MAX_AUCTION_ITEMS_CLIENT_UI_PAGE).
        // A full page of this size means more items may follow.
        private const int AuctionOwnerPageSize = 50;
        // Safety cap so a misbehaving server can never make us loop forever.
        private const int AuctionOwnerWalkMaxItems = 1000;
        // Drop late/duplicate owner pages that arrive shortly after a walk finalized, so they
        // can't overwrite the combined list the modern client just received with a partial page.
        private const long AuctionOwnerWalkPostFinalizeSuppressMs = 2000;
        // Pace successive owner-list page requests to cmangos so the walk doesn't fire them
        // back-to-back. Applied on the WorldClient thread OUTSIDE the walk lock.
        private const int AuctionOwnerWalkPageDelayMs = 200;

        // Handlers for SMSG opcodes coming the legacy world server
        [PacketHandler(Opcode.MSG_AUCTION_HELLO)]
        void HandleAuctionHello(WorldPacket packet)
        {
            AuctionHelloResponse auction = new AuctionHelloResponse();
            auction.Guid = packet.ReadGuid().To128(GetSession().GameState);
            GetSession().GameState.CurrentInteractedWithNPC = auction.Guid;
            auction.AuctionHouseID = packet.ReadUInt32();
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_3_0_10958))
                auction.OpenForBusiness = packet.ReadBool();

            // A fresh AH open starts the owner-list walk from scratch: force-arm (discarding any
            // stale in-progress walk from a previously-closed AH) so the reply and its following
            // pages get combined into a single result instead of stopping at the first 50 items.
            // Arm BEFORE sending the hello response, so the client's own owned-items request (which
            // it fires on receiving the response) sees the walk already in progress and is swallowed.
            if (LegacyVersion.ExpansionVersion <= 1)
            {
                var gs = GetSession().GameState;
                lock (gs.AuctionOwnerWalkLock)
                {
                    gs.AuctionOwnerWalkInProgress = true;
                    gs.AuctionOwnerWalkAuctioneer = auction.Guid;
                    gs.AuctionOwnerWalkAccumulator.Clear();
                    gs.AuctionOwnerWalkLastFinalizedTickMs = 0; // no stale suppression window
                }
            }

            SendPacketToClient(auction);

            // Have to send this again here, or server does not reply for some reason.
            WorldPacket packet2 = new WorldPacket(Opcode.CMSG_AUCTION_LIST_OWNED_ITEMS);
            packet2.WriteGuid(auction.Guid.To64());
            packet2.WriteUInt32(0);
            SendPacketToServer(packet2);
        }

        AuctionItem ReadAuctionItem(WorldPacket packet, uint index)
        {
            AuctionItem item = new AuctionItem();
            item.AuctionID = packet.ReadUInt32();
            item.Item = new();
            item.Item.ItemID = packet.ReadUInt32();

            byte enchantmentCount;
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
                enchantmentCount = 7;
            else if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
                enchantmentCount = 6;
            else
                enchantmentCount = 1;

            for (byte j = 0; j < enchantmentCount; ++j)
            {
                ItemEnchantData enchant = new ItemEnchantData();
                enchant.Slot = j;
                enchant.ID = packet.ReadUInt32();
                if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
                {
                    enchant.Expiration = packet.ReadUInt32();
                    enchant.Charges = packet.ReadInt32();
                }
                if (enchant.ID != 0)
                    item.Enchantments.Add(enchant);
            }

            item.Item.RandomPropertiesID = packet.ReadUInt32();
            item.Item.RandomPropertiesSeed = packet.ReadUInt32();
            item.Count = packet.ReadInt32();
            item.Charges = packet.ReadInt32();

            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
               item.Flags = packet.ReadUInt32();

            item.Owner = packet.ReadGuid().To128(GetSession().GameState);
            item.OwnerAccountID = GetSession().GetGameAccountGuidForPlayer(item.Owner);
            item.MinBid = packet.ReadUInt32();
            item.MinIncrement = packet.ReadUInt32();
            item.BuyoutPrice = packet.ReadUInt32();
            item.DurationLeft = packet.ReadInt32();
            item.Bidder = packet.ReadGuid().To128(GetSession().GameState);
            item.BidAmount = packet.ReadUInt32();

            if (item.Item.ItemID == 0)
                item.Item = null;

            return item;
        }

        [PacketHandler(Opcode.SMSG_AUCTION_LIST_BIDDED_ITEMS_RESULT)]
        [PacketHandler(Opcode.SMSG_AUCTION_LIST_OWNED_ITEMS_RESULT)]
        void HandleAuctionListMyItemsResult(WorldPacket packet)
        {
            Opcode universalOpcode = packet.GetUniversalOpcode(false);
            AuctionListMyItemsResult auction = new AuctionListMyItemsResult(universalOpcode);
            uint count = packet.ReadUInt32();
            for (uint i = 0; i < count; i++)
                auction.Items.Add(ReadAuctionItem(packet, i));
            auction.TotalItemsCount = packet.ReadInt32();
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_3_0_7561))
                auction.DesiredDelay = packet.ReadUInt32();

            // Combine all owned-auction pages into a single result so the modern client (which never
            // asks for the next page on the Auctions tab) sees every auction the player has posted.
            if (universalOpcode == Opcode.SMSG_AUCTION_LIST_OWNED_ITEMS_RESULT
                && HandleAuctionOwnerWalkPage(auction))
                return; // page absorbed / re-requested; nothing to forward yet

            SendPacketToClient(auction);
        }

        // Returns true when the page was consumed by the walk (accumulated and either a further page
        // was requested or the combined result was already sent). Returns false to let the caller
        // forward the packet unchanged (no walk active / suppression not applicable).
        bool HandleAuctionOwnerWalkPage(AuctionListMyItemsResult page)
        {
            var gs = GetSession().GameState;

            // Decide what to do under the lock, then act (send / sleep) outside it so the delay
            // never blocks the modern-client-facing handler that arms/swallows walk requests.
            AuctionListMyItemsResult finalized = null;
            bool requestNextPage = false;
            uint nextOffset = 0;
            WowGuid128 auctioneer = null;

            lock (gs.AuctionOwnerWalkLock)
            {
                if (!gs.AuctionOwnerWalkInProgress)
                {
                    // A late/duplicate page from a walk that just finalized would overwrite the
                    // combined list the client already has with a partial 50-item page. Drop it.
                    long sinceFinalize = Environment.TickCount64 - gs.AuctionOwnerWalkLastFinalizedTickMs;
                    if (gs.AuctionOwnerWalkLastFinalizedTickMs > 0
                        && sinceFinalize < AuctionOwnerWalkPostFinalizeSuppressMs)
                        return true;
                    return false; // not walking: forward as-is
                }

                // Accumulate this page, de-duplicating by AuctionID (cmangos resends the first page
                // as an extra result, which would otherwise inflate the combined list).
                int serverTotal = page.TotalItemsCount;
                var existingIds = new HashSet<uint>();
                foreach (var existing in gs.AuctionOwnerWalkAccumulator)
                    existingIds.Add(existing.AuctionID);
                foreach (var item in page.Items)
                {
                    if (existingIds.Add(item.AuctionID))
                        gs.AuctionOwnerWalkAccumulator.Add(item);
                }

                int gathered = gs.AuctionOwnerWalkAccumulator.Count;
                bool shortPage = page.Items.Count < AuctionOwnerPageSize;
                bool reachedTotal = serverTotal > 0 && gathered >= serverTotal;
                bool atCap = gathered >= AuctionOwnerWalkMaxItems;

                if (shortPage || reachedTotal || atCap)
                {
                    // Walk complete: forward everything as one result. TotalItemsCount must equal the
                    // item count so the modern client shows a single full page and no phantom "next".
                    page.Items = new List<AuctionItem>(gs.AuctionOwnerWalkAccumulator);
                    page.TotalItemsCount = page.Items.Count;
                    gs.AuctionOwnerWalkAccumulator.Clear();
                    gs.AuctionOwnerWalkInProgress = false;
                    gs.AuctionOwnerWalkLastFinalizedTickMs = Environment.TickCount64;
                    finalized = page;
                }
                else
                {
                    requestNextPage = true;
                    nextOffset = (uint)gathered;
                    auctioneer = gs.AuctionOwnerWalkAuctioneer;
                }
            }

            if (finalized != null)
            {
                SendPacketToClient(finalized);
                return true;
            }

            // More pages remain: pace the request to cmangos, then ask for the next one starting
            // past what we already have.
            Thread.Sleep(AuctionOwnerWalkPageDelayMs);
            WorldPacket nextPage = new WorldPacket(Opcode.CMSG_AUCTION_LIST_OWNED_ITEMS);
            nextPage.WriteGuid(auctioneer.To64());
            nextPage.WriteUInt32(nextOffset);
            SendPacketToServer(nextPage);
            return true;
        }

        [PacketHandler(Opcode.SMSG_AUCTION_LIST_ITEMS_RESULT)]
        void HandleAuctionListItemsResult(WorldPacket packet)
        {
            AuctionListItemsResult auction = new AuctionListItemsResult();
            uint count = packet.ReadUInt32();
            for (uint i = 0; i < count; i++)
            {
                AuctionItem item = ReadAuctionItem(packet, i);
                item.CensorServerSideInfo = true;
                auction.Items.Add(item);
            }
            auction.TotalItemsCount = packet.ReadInt32();
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_3_0_7561))
                auction.DesiredDelay = packet.ReadUInt32();
            SendPacketToClient(auction);
        }

        [PacketHandler(Opcode.SMSG_AUCTION_COMMAND_RESULT)]
        void HandleAuctionCommandResult(WorldPacket packet)
        {
            AuctionCommandResult auction = new AuctionCommandResult();
            auction.AuctionID = packet.ReadUInt32();
            auction.Command = (AuctionHouseAction)packet.ReadUInt32();
            auction.ErrorCode = (AuctionHouseError)packet.ReadUInt32();

            switch (auction.ErrorCode)
            {
                case AuctionHouseError.Ok:
                    if (auction.Command == AuctionHouseAction.Bid)
                        auction.MinIncrement = packet.ReadUInt32();
                    break;
                case AuctionHouseError.Inventory:
                    auction.BagResult = LegacyVersion.ConvertInventoryResult(packet.ReadUInt32());
                    break;
                case AuctionHouseError.HigherBid:
                    auction.Guid = packet.ReadGuid().To128(GetSession().GameState);
                    auction.Money = packet.ReadUInt32();
                    auction.MinIncrement = packet.ReadUInt32();
                    break;
            }

            SendPacketToClient(auction);
        }

        [PacketHandler(Opcode.SMSG_AUCTION_OWNER_NOTIFICATION)]
        void HandleAuctionOwnerNotification(WorldPacket packet)
        {
            AuctionOwnerNotification info = new AuctionOwnerNotification();
            info.AuctionID = packet.ReadUInt32();
            info.BidAmount = packet.ReadUInt32();
            uint minIncrement = packet.ReadUInt32();
            WowGuid buyer = packet.ReadGuid();
            info.Item.ItemID = packet.ReadUInt32();
            info.Item.RandomPropertiesID = packet.ReadUInt32();

            float mailDelay;
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
                mailDelay = packet.ReadFloat();
            else
                mailDelay = 3600;

            if (buyer.IsEmpty())
            {
                // BidAmount != 0 -> Your auction of X sold.
                // BidAmount == 0 -> Your auction of X has expired.
                AuctionClosedNotification auction = new AuctionClosedNotification();
                auction.Info = info;
                auction.Sold = info.BidAmount != 0;
                auction.ProceedsMailDelay = mailDelay;
                SendPacketToClient(auction);
            }
            else
            {
                // A buyer has been found for your auction of X.
                AuctionOwnerBidNotification auction = new AuctionOwnerBidNotification();
                auction.Info = info;
                auction.MinIncrement = minIncrement;
                auction.Bidder = buyer.To128(GetSession().GameState);
                SendPacketToClient(auction);
            }
        }

        [PacketHandler(Opcode.SMSG_AUCTION_BIDDER_NOTIFICATION)]
        void HandleAuctionBidderNotification(WorldPacket packet)
        {
            AuctionBidderNotification info = new AuctionBidderNotification();
            uint auctionHouseId = packet.ReadUInt32();
            info.AuctionID = packet.ReadUInt32();
            info.Bidder = packet.ReadGuid().To128(GetSession().GameState);
            uint bidAmount = packet.ReadUInt32();
            uint minIncrement = packet.ReadUInt32();
            info.Item.ItemID = packet.ReadUInt32();
            info.Item.RandomPropertiesID = packet.ReadUInt32();

            if (bidAmount == 0)
            {
                // You won an auction for X.
                AuctionWonNotification auction = new AuctionWonNotification();
                auction.Info = info;
                SendPacketToClient(auction);
            }
            else
            {
                // You have been outbid on X.
                AuctionOutbidNotification auction = new AuctionOutbidNotification();
                auction.Info = info;
                auction.BidAmount = bidAmount;
                auction.MinIncrement = minIncrement;
                SendPacketToClient(auction);
            }
        }
    }
}
