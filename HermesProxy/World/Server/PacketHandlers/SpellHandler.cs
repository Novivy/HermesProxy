using Framework;
using Framework.Constants;
using HermesProxy.Enums;
using HermesProxy.World;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using HermesProxy.World.Server.Packets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Framework.Logging;

namespace HermesProxy.World.Server
{
    public partial class WorldSocket
    {
        // Handlers for CMSG opcodes coming from the modern client
        SpellCastTargetFlags ConvertSpellTargetFlags(SpellTargetData target)
        {
            SpellCastTargetFlags targetFlags = SpellCastTargetFlags.None;
            if (target.Unit != null && !target.Unit.IsEmpty())
            {
                if (target.Flags.HasFlag(SpellCastTargetFlags.Unit))
                    targetFlags |= SpellCastTargetFlags.Unit;
                if (target.Flags.HasFlag(SpellCastTargetFlags.CorpseEnemy))
                    targetFlags |= SpellCastTargetFlags.CorpseEnemy;
                if (target.Flags.HasFlag(SpellCastTargetFlags.GameObject))
                    targetFlags |= SpellCastTargetFlags.GameObject;
                if (target.Flags.HasFlag(SpellCastTargetFlags.CorpseAlly))
                    targetFlags |= SpellCastTargetFlags.CorpseAlly;
                if (target.Flags.HasFlag(SpellCastTargetFlags.UnitMinipet))
                    targetFlags |= SpellCastTargetFlags.UnitMinipet;
            }
            if (target.Item != null & !target.Item.IsEmpty())
            {
                if (target.Flags.HasFlag(SpellCastTargetFlags.Item))
                    targetFlags |= SpellCastTargetFlags.Item;
                if (target.Flags.HasFlag(SpellCastTargetFlags.TradeItem))
                    targetFlags |= SpellCastTargetFlags.TradeItem;
            }
            if (target.SrcLocation != null)
                targetFlags |= SpellCastTargetFlags.SourceLocation;
            if (target.DstLocation != null)
                targetFlags |= SpellCastTargetFlags.DestLocation;
            if (!String.IsNullOrEmpty(target.Name))
                targetFlags |= SpellCastTargetFlags.String;
            return targetFlags;
        }
        void WriteSpellTargets(SpellTargetData target, SpellCastTargetFlags targetFlags, WorldPacket packet)
        {
            if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180))
                packet.WriteUInt16((ushort)targetFlags);
            else
                packet.WriteUInt32((uint)targetFlags);

            if (targetFlags.HasAnyFlag(SpellCastTargetFlags.Unit | SpellCastTargetFlags.CorpseEnemy | SpellCastTargetFlags.GameObject |
                SpellCastTargetFlags.CorpseAlly | SpellCastTargetFlags.UnitMinipet))
                packet.WritePackedGuid(target.Unit.To64());

            // Check if the user wants to target the "Will not be traded" slot
            if (targetFlags.HasFlag(SpellCastTargetFlags.TradeItem) && target.Item == WowGuid128.Create(HighGuidType703.Uniq, 10))
                packet.WritePackedGuid(new WowGuid64((ulong) TradeSlots.NonTraded));
            else if (targetFlags.HasFlag(SpellCastTargetFlags.Item))
                packet.WritePackedGuid(target.Item.To64());

            if (targetFlags.HasAnyFlag(SpellCastTargetFlags.SourceLocation))
            {
                if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_2_0_10192))
                    packet.WritePackedGuid(target.SrcLocation.Transport.To64());
                packet.WriteVector3(target.SrcLocation.Location);
            }

            if (targetFlags.HasAnyFlag(SpellCastTargetFlags.DestLocation))
            {
                if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_8_9464))
                    packet.WritePackedGuid(target.DstLocation.Transport.To64());
                packet.WriteVector3(target.DstLocation.Location);
            }

            if (targetFlags.HasAnyFlag(SpellCastTargetFlags.String))
                packet.WriteCString(target.Name);
        }
        public void SendCastRequestFailed(ClientCastRequest castRequest, bool isPet, SpellCastResultClassic reason = SpellCastResultClassic.SpellInProgress)
        {
            if (castRequest == null || castRequest.ServerGUID == null)
                return;
            if (!castRequest.HasStarted)
            {
                SpellPrepare prepare2 = new SpellPrepare();
                prepare2.ClientCastID = castRequest.ClientGUID;
                prepare2.ServerCastID = castRequest.ServerGUID;
                SendPacket(prepare2);
            }

            if (isPet)
            {
                PetCastFailed failed = new();
                failed.SpellID = castRequest.SpellId;
                failed.Reason = (uint)reason;
                failed.CastID = castRequest.ServerGUID;
                SendPacket(failed);
            }
            else
            {
                CastFailed failed = new();
                failed.SpellID = castRequest.SpellId;
                failed.SpellXSpellVisualID = castRequest.SpellXSpellVisualId;
                failed.Reason = (uint)reason;
                failed.CastID = castRequest.ServerGUID;
                SendPacket(failed);
            }
        }

        private bool RejectIfOnTaxi(uint spellId, uint spellVisualId, WowGuid128 clientGuid, bool isPet)
        {
            if (!GetSession().GameState.IsInTaxiFlight)
                return false;
            ClientCastRequest castRequest = new ClientCastRequest();
            castRequest.Timestamp = Environment.TickCount;
            castRequest.SpellId = spellId;
            castRequest.SpellXSpellVisualId = spellVisualId;
            castRequest.ClientGUID = clientGuid;
            castRequest.ServerGUID = WowGuid128.Create(HighGuidType703.Cast, SpellCastSource.Normal, (uint)GetSession().GameState.CurrentMapId, spellId, 20000 + clientGuid.GetCounter());
            SendCastRequestFailed(castRequest, isPet, SpellCastResultClassic.NotOnTaxi);
            return true;
        }
        [PacketHandler(Opcode.CMSG_CAST_SPELL)]
        void HandleCastSpell(CastSpell cast)
        {
            // Modern client unlocks the cast UI during taxi (we strip TAXI_FLIGHT + restore
            // HasControl so bag/equip works). The legacy server reacts to a cast by removing
            // the temporary mount aura, which kills the wyvern/gryphon visual mid-flight.
            // Reject casts at the proxy before they reach the server.
            if (RejectIfOnTaxi(cast.Cast.SpellID, cast.Cast.SpellXSpellVisualID, cast.Cast.CastID, false))
                return;

            // Artificial lag is needed for spell packets,
            // or spells will bug out and glow if spammed.
            if (Settings.ServerSpellDelay > 0)
                Thread.Sleep(Settings.ServerSpellDelay);

            if (GameData.NextMeleeSpells.Contains(cast.Cast.SpellID) ||
                GameData.AutoRepeatSpells.Contains(cast.Cast.SpellID))
            {
                ClientCastRequest castRequest = new ClientCastRequest();
                castRequest.Timestamp = Environment.TickCount;
                castRequest.SpellId = cast.Cast.SpellID;
                castRequest.SpellXSpellVisualId = cast.Cast.SpellXSpellVisualID;
                castRequest.ClientGUID = cast.Cast.CastID;

                bool alreadyHasSpecial;
                lock (GetSession().GameState.SpellCastLock)
                {
                    alreadyHasSpecial = GetSession().GameState.CurrentClientSpecialCast != null;
                    if (alreadyHasSpecial)
                    {
                        castRequest.ServerGUID = WowGuid128.Create(HighGuidType703.Cast, SpellCastSource.Normal, (uint)GetSession().GameState.CurrentMapId, cast.Cast.SpellID, 10000 + cast.Cast.CastID.GetCounter());
                    }
                    else
                    {
                        castRequest.ServerGUID = WowGuid128.Create(HighGuidType703.Cast, SpellCastSource.Normal, (uint)GetSession().GameState.CurrentMapId, cast.Cast.SpellID, cast.Cast.SpellID + GetSession().GameState.CurrentPlayerGuid.GetCounter());
                        GetSession().GameState.CurrentClientSpecialCast = castRequest;
                    }
                }
                if (alreadyHasSpecial)
                {
                    SendCastRequestFailed(castRequest, false);
                    return;
                }
                SpellPrepare prepare = new SpellPrepare();
                prepare.ClientCastID = cast.Cast.CastID;
                prepare.ServerCastID = castRequest.ServerGUID;
                SendPacket(prepare);
            }
            else
            {
                ClientCastRequest castRequest = new ClientCastRequest();
                castRequest.Timestamp = Environment.TickCount;
                castRequest.SpellId = cast.Cast.SpellID;
                castRequest.SpellXSpellVisualId = cast.Cast.SpellXSpellVisualID;
                castRequest.ClientGUID = cast.Cast.CastID;
                castRequest.ServerGUID = WowGuid128.Create(HighGuidType703.Cast, SpellCastSource.Normal, (uint)GetSession().GameState.CurrentMapId, cast.Cast.SpellID, 10000 + cast.Cast.CastID.GetCounter());

                ClientCastRequest currentNormalCast;
                lock (GetSession().GameState.SpellCastLock)
                    currentNormalCast = GetSession().GameState.CurrentClientNormalCast;

                if (currentNormalCast != null)
                {
                    if (currentNormalCast.HasStarted)
                    {
                        long remainingMs = currentNormalCast.SpellStartTimestamp + currentNormalCast.CastDuration - Environment.TickCount;
                        if (Settings.SpellQueueWindow > 0 && currentNormalCast.CastDuration > 0 && remainingMs <= Settings.SpellQueueWindow)
                        {
                            castRequest.PendingLegacyPacket = BuildLegacyCastPacket(cast);
                            List<ClientCastRequest> toFail;
                            lock (GetSession().GameState.SpellCastLock)
                            {
                                toFail = GetSession().GameState.PendingClientCasts.ToList();
                                GetSession().GameState.PendingClientCasts.Clear();
                                GetSession().GameState.PendingClientCasts.Add(castRequest);
                            }
                            foreach (var old in toFail)
                                SendCastRequestFailed(old, false);
                        }
                        else
                        {
                            SendCastRequestFailed(castRequest, false);
                        }
                    }
                    else
                    {
                        // Sometimes we dont clear the CurrentCast when we dont get the correct SMSG_SPELL_GO
                        if (currentNormalCast.Timestamp + 10000 < castRequest.Timestamp)
                        {
                            Log.Print(LogType.Warn, $"Clearing CurrentClientNormalCast because of 10 sec timeout! (oldSpell:{currentNormalCast.SpellId} newSpell:{castRequest.SpellId})");
                            Log.Print(LogType.Warn, "Are you playing on a server with another patch?");
                            List<ClientCastRequest> toFail;
                            lock (GetSession().GameState.SpellCastLock)
                            {
                                if (ReferenceEquals(GetSession().GameState.CurrentClientNormalCast, currentNormalCast))
                                    GetSession().GameState.CurrentClientNormalCast = null;
                                toFail = GetSession().GameState.PendingClientCasts.ToList();
                                GetSession().GameState.PendingClientCasts.Clear();
                            }
                            SendCastRequestFailed(currentNormalCast, false);
                            foreach (var pending in toFail)
                                SendCastRequestFailed(pending, false);
                            SendCastRequestFailed(castRequest, false);
                        }
                        else
                        {
                            castRequest.PendingLegacyPacket = BuildLegacyCastPacket(cast);
                            lock (GetSession().GameState.SpellCastLock)
                                GetSession().GameState.PendingClientCasts.Add(castRequest);
                        }
                    }
                    return;
                }

                lock (GetSession().GameState.SpellCastLock)
                    GetSession().GameState.CurrentClientNormalCast = castRequest;
            }

            SendPacketToServer(BuildLegacyCastPacket(cast));
        }
        private WorldPacket BuildLegacyCastPacket(CastSpell cast)
        {
            SpellCastTargetFlags targetFlags = ConvertSpellTargetFlags(cast.Cast.Target);
            WorldPacket packet = new WorldPacket(Opcode.CMSG_CAST_SPELL);
            if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180))
            {
                packet.WriteUInt32(cast.Cast.SpellID);
            }
            else if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V3_0_2_9056))
            {
                packet.WriteUInt32(cast.Cast.SpellID);
                packet.WriteUInt8(0); // cast count
            }
            else
            {
                packet.WriteUInt8(0); // cast count
                packet.WriteUInt32(cast.Cast.SpellID);
                packet.WriteUInt8((byte)cast.Cast.SendCastFlags);
            }
            WriteSpellTargets(cast.Cast.Target, targetFlags, packet);
            return packet;
        }
        [PacketHandler(Opcode.CMSG_PET_CAST_SPELL)]
        void HandlePetCastSpell(PetCastSpell cast)
        {
            if (RejectIfOnTaxi(cast.Cast.SpellID, cast.Cast.SpellXSpellVisualID, cast.Cast.CastID, true))
                return;

            // Artificial lag is needed for spell packets,
            // or spells will bug out and glow if spammed.
            if (Settings.ServerSpellDelay > 0)
                Thread.Sleep(Settings.ServerSpellDelay);

            ClientCastRequest castRequest = new ClientCastRequest();
            castRequest.Timestamp = Environment.TickCount;
            castRequest.SpellId = cast.Cast.SpellID;
            castRequest.SpellXSpellVisualId = cast.Cast.SpellXSpellVisualID;
            castRequest.ClientGUID = cast.Cast.CastID;
            castRequest.ServerGUID = WowGuid128.Create(HighGuidType703.Cast, SpellCastSource.Normal, (uint)GetSession().GameState.CurrentMapId, cast.Cast.SpellID, 10000 + cast.Cast.CastID.GetCounter());

            ClientCastRequest currentPetCast;
            lock (GetSession().GameState.SpellCastLock)
                currentPetCast = GetSession().GameState.CurrentClientPetCast;

            if (currentPetCast != null)
            {
                if (currentPetCast.HasStarted)
                {
                    SendCastRequestFailed(castRequest, true);
                }
                else
                {
                    // Sometimes we dont clear the CurrentCast when we dont get the correct SMSG_SPELL_GO
                    if (currentPetCast.Timestamp + 10000 < castRequest.Timestamp)
                    {
                        Log.Print(LogType.Warn, $"Clearing CurrentClientPetCast because of 10 sec timeout! (oldSpell:{currentPetCast.SpellId} newSpell:{castRequest.SpellId})");
                        List<ClientCastRequest> toFail;
                        lock (GetSession().GameState.SpellCastLock)
                        {
                            if (ReferenceEquals(GetSession().GameState.CurrentClientPetCast, currentPetCast))
                                GetSession().GameState.CurrentClientPetCast = null;
                            toFail = GetSession().GameState.PendingClientPetCasts.ToList();
                            GetSession().GameState.PendingClientPetCasts.Clear();
                        }
                        SendCastRequestFailed(currentPetCast, true);
                        foreach (var pending in toFail)
                            SendCastRequestFailed(pending, true);
                        SendCastRequestFailed(castRequest, true);
                    }
                    else
                    {
                        lock (GetSession().GameState.SpellCastLock)
                            GetSession().GameState.PendingClientPetCasts.Add(castRequest);
                    }
                }
                return;
            }

            lock (GetSession().GameState.SpellCastLock)
                GetSession().GameState.CurrentClientPetCast = castRequest;

            SpellCastTargetFlags targetFlags = ConvertSpellTargetFlags(cast.Cast.Target);

            WorldPacket packet = new WorldPacket(Opcode.CMSG_PET_CAST_SPELL);
            packet.WriteGuid(cast.PetGUID.To64());
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
                packet.WriteUInt8(0); // cast count
            packet.WriteUInt32(cast.Cast.SpellID);
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
                packet.WriteUInt8((byte)cast.Cast.SendCastFlags);
            WriteSpellTargets(cast.Cast.Target, targetFlags, packet);
            SendPacketToServer(packet);
        }
        [PacketHandler(Opcode.CMSG_USE_ITEM)]
        void HandleUseItem(UseItem use)
        {
            if (RejectIfOnTaxi(use.Cast.SpellID, use.Cast.SpellXSpellVisualID, use.Cast.CastID, false))
                return;

            // Artificial lag is needed for spell packets,
            // or spells will bug out and glow if spammed.
            if (Settings.ServerSpellDelay > 0)
                Thread.Sleep(Settings.ServerSpellDelay);

            ClientCastRequest castRequest = new ClientCastRequest();
            castRequest.Timestamp = Environment.TickCount;
            castRequest.SpellId = use.Cast.SpellID;
            castRequest.SpellXSpellVisualId = use.Cast.SpellXSpellVisualID;
            castRequest.ClientGUID = use.Cast.CastID;
            castRequest.ServerGUID = WowGuid128.Create(HighGuidType703.Cast, SpellCastSource.Normal, (uint)GetSession().GameState.CurrentMapId, use.Cast.SpellID, 10000 + use.Cast.CastID.GetCounter());
            castRequest.ItemGUID = use.CastItem;

            ClientCastRequest currentNormalCastItem;
            lock (GetSession().GameState.SpellCastLock)
                currentNormalCastItem = GetSession().GameState.CurrentClientNormalCast;

            if (currentNormalCastItem != null)
            {
                if (currentNormalCastItem.HasStarted)
                {
                    SendCastRequestFailed(castRequest, false);
                }
                else
                {
                    // Sometimes we dont clear the CurrentCast when we dont get the correct SMSG_SPELL_GO
                    if (currentNormalCastItem.Timestamp + 10000 < castRequest.Timestamp)
                    {
                        Log.Print(LogType.Warn, $"Clearing CurrentClientNormalCast because of 10 sec timeout! (oldSpell:{currentNormalCastItem.SpellId} newSpell:{castRequest.SpellId})");
                        List<ClientCastRequest> toFail;
                        lock (GetSession().GameState.SpellCastLock)
                        {
                            if (ReferenceEquals(GetSession().GameState.CurrentClientNormalCast, currentNormalCastItem))
                                GetSession().GameState.CurrentClientNormalCast = null;
                            toFail = GetSession().GameState.PendingClientCasts.ToList();
                            GetSession().GameState.PendingClientCasts.Clear();
                        }
                        SendCastRequestFailed(currentNormalCastItem, false);
                        foreach (var pending in toFail)
                            SendCastRequestFailed(pending, false);
                        SendCastRequestFailed(castRequest, false);
                    }
                    else
                    {
                        castRequest.PendingLegacyPacket = BuildLegacyUseItemPacket(use);
                        lock (GetSession().GameState.SpellCastLock)
                            GetSession().GameState.PendingClientCasts.Add(castRequest);
                    }
                }
                return;
            }

            lock (GetSession().GameState.SpellCastLock)
                GetSession().GameState.CurrentClientNormalCast = castRequest;
            SendPacketToServer(BuildLegacyUseItemPacket(use));
        }
        private WorldPacket BuildLegacyUseItemPacket(UseItem use)
        {
            WorldPacket packet = new WorldPacket(Opcode.CMSG_USE_ITEM);
            byte containerSlot = use.PackSlot != Enums.Classic.InventorySlots.Bag0 ? ModernVersion.AdjustInventorySlot(use.PackSlot) : use.PackSlot;
            byte slot = use.PackSlot == Enums.Classic.InventorySlots.Bag0 ? ModernVersion.AdjustInventorySlot(use.Slot) : use.Slot;
            packet.WriteUInt8(containerSlot);
            packet.WriteUInt8(slot);
            packet.WriteUInt8(GetSession().GameState.GetItemSpellSlot(use.CastItem, use.Cast.SpellID));
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
            {
                packet.WriteUInt8(0); // cast count;
                packet.WriteGuid(use.CastItem.To64());
            }
            SpellCastTargetFlags targetFlags = ConvertSpellTargetFlags(use.Cast.Target);
            WriteSpellTargets(use.Cast.Target, targetFlags, packet);
            return packet;
        }
        [PacketHandler(Opcode.CMSG_CANCEL_CAST)]
        void HandleCancelCast(CancelCast cast)
        {
            // Artificial lag is needed for spell packets,
            // or spells will bug out and glow if spammed.
            if (Settings.ServerSpellDelay > 0)
                Thread.Sleep(Settings.ServerSpellDelay);

            WorldPacket packet = new WorldPacket(Opcode.CMSG_CANCEL_CAST);
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
                packet.WriteUInt8(0);
            packet.WriteUInt32(cast.SpellID);
            SendPacketToServer(packet);
        }
        [PacketHandler(Opcode.CMSG_CANCEL_CHANNELLING)]
        void HandleCancelChannelling(CancelChannelling cast)
        {
            // Artificial lag is needed for spell packets,
            // or spells will bug out and glow if spammed.
            if (Settings.ServerSpellDelay > 0)
                Thread.Sleep(Settings.ServerSpellDelay);

            WorldPacket packet = new WorldPacket(Opcode.CMSG_CANCEL_CHANNELLING);
            packet.WriteInt32(cast.SpellID);
            SendPacketToServer(packet);
        }
        [PacketHandler(Opcode.CMSG_CANCEL_AUTO_REPEAT_SPELL)]
        void HandleCancelAutoRepeatSpell(CancelAutoRepeatSpell spell)
        {
            // Artificial lag is needed for spell packets,
            // or spells will bug out and glow if spammed.
            if (Settings.ServerSpellDelay > 0)
                Thread.Sleep(Settings.ServerSpellDelay);

            WorldPacket packet = new WorldPacket(Opcode.CMSG_CANCEL_AUTO_REPEAT_SPELL);
            SendPacketToServer(packet);
        }
        [PacketHandler(Opcode.CMSG_CANCEL_AURA)]
        void HandleCancelAura(CancelAura aura)
        {
            // During taxi the modern client auto-fires this for the mount aura whenever the
            // player tries to cast or use an item, which makes the legacy server strip the
            // wyvern/gryphon visual mid-flight. Swallow the cancel for mount auras and
            // re-push the mount display so the client redraws it (the client locally clears
            // the visual before waiting for server confirmation).
            if (GetSession().GameState.IsInTaxiFlight && GameData.MountAuras.Contains(aura.SpellID))
            {
                RefreshTaxiMountDisplay();
                return;
            }

            WorldPacket packet = new WorldPacket(Opcode.CMSG_CANCEL_AURA);
            packet.WriteUInt32(aura.SpellID);
            SendPacketToServer(packet);
        }
        private void RefreshTaxiMountDisplay()
        {
            WowGuid128 guid = GetSession().GameState.CurrentPlayerGuid;
            int mountDisplayId = GetSession().GameState.GetLegacyFieldValueInt32(guid, UnitField.UNIT_FIELD_MOUNTDISPLAYID);
            if (mountDisplayId == 0)
                return;

            // SetUpdateField only marks the field dirty when the new value differs from the cached
            // modern value. The cache already holds the wyvern displayId, so a normal update would
            // be empty and the client wouldn't redraw. Force a diff by zeroing the cache slot first.
            var modernCache = GetSession().GameState.GetCachedObjectFieldsModern(guid);
            int modernMountIdx = ModernVersion.GetUpdateField(UnitField.UNIT_FIELD_MOUNTDISPLAYID);
            if (modernCache != null && modernMountIdx >= 0)
                modernCache.m_updateValues[modernMountIdx].SignedValue = 0;

            ObjectUpdate updateData = new ObjectUpdate(guid, UpdateTypeModern.Values, GetSession());
            updateData.UnitData.MountDisplayID = mountDisplayId;
            UpdateObject updatePacket = new UpdateObject(GetSession().GameState);
            updatePacket.ObjectUpdates.Add(updateData);
            SendPacket(updatePacket);
        }
        [PacketHandler(Opcode.CMSG_CANCEL_MOUNT_AURA)]
        void HandleCancelMountAura(EmptyClientPacket cancel)
        {
            // Same reasoning as CMSG_CANCEL_AURA: drop the dismount request while on taxi.
            if (GetSession().GameState.IsInTaxiFlight)
            {
                RefreshTaxiMountDisplay();
                return;
            }

            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
            {
                WorldPacket packet = new WorldPacket(Opcode.CMSG_CANCEL_MOUNT_AURA);
                SendPacketToServer(packet);
            }
            else
            {
                WowGuid128 guid = GetSession().GameState.CurrentPlayerGuid;
                var updateFields = GetSession().GameState.GetCachedObjectFieldsLegacy(guid);
                if (updateFields == null)
                    return;

                for (byte i = 0; i < 32; i++)
                {
                    var aura = GetSession().WorldClient.ReadAuraSlot(i, guid, updateFields);
                    if (aura == null)
                        continue;

                    if (GameData.MountAuras.Contains(aura.SpellID))
                    {
                        WorldPacket packet = new WorldPacket(Opcode.CMSG_CANCEL_AURA);
                        packet.WriteUInt32(aura.SpellID);
                        SendPacketToServer(packet);
                    }
                }
            }
        }
        [PacketHandler(Opcode.CMSG_LEARN_TALENT)]
        void HandleLearnTalent(LearnTalent talent)
        {
            WorldPacket packet = new WorldPacket(Opcode.CMSG_LEARN_TALENT);
            packet.WriteUInt32(talent.TalentID);
            packet.WriteUInt32(talent.Rank);
            SendPacketToServer(packet);
        }
        [PacketHandler(Opcode.CMSG_RESURRECT_RESPONSE)]
        void HandleResurrectResponse(ResurrectResponse revive)
        {
            WorldPacket packet = new WorldPacket(Opcode.CMSG_RESURRECT_RESPONSE);
            packet.WriteGuid(revive.CasterGUID.To64());
            packet.WriteUInt8((byte)(revive.Response != 0 ? 0 : 1));
            SendPacketToServer(packet);
        }
        [PacketHandler(Opcode.CMSG_SELF_RES)]
        void HandleSelfRes(SelfRes revive)
        {
            WorldPacket packet = new WorldPacket(Opcode.CMSG_SELF_RES);
            SendPacketToServer(packet);
        }

        [PacketHandler(Opcode.CMSG_TOTEM_DESTROYED)]
        void HandleTotemDestroyed(TotemDestroyed totem)
        {
            if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180))
                return;

            WorldPacket packet = new WorldPacket(Opcode.CMSG_TOTEM_DESTROYED);
            packet.WriteUInt8(totem.Slot);
            SendPacketToServer(packet);
        }
    }
}
