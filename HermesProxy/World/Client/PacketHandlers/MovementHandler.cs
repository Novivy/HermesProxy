using Framework.GameMath;
using Framework.Logging;
using HermesProxy.Enums;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using HermesProxy.World.Server.Packets;
using System;

namespace HermesProxy.World.Client
{
    public partial class WorldClient
    {
        // True if we've seen this creature swimming. Registry seeded in
        // UpdateHandler.ReadMovementUpdateBlock when a creature carries MovementFlag.Swimming.
        private bool IsSwimmingMob(WowGuid128 guid)
        {
            return GetSession().GameState.KnownSwimmingMobs.Contains(guid);
        }

        // Merely water-CAPABLE (UNIT_FLAG_SWIMMING). Animation-shaped state only - this covers most
        // land NPCs and every flying boss, so it must never drive gravity or ground-snap decisions.
        private bool IsWaterCapableMob(WowGuid128 guid)
        {
            return GetSession().GameState.WaterCapableMobs.Contains(guid);
        }

        // True while the server has this creature in hover/flight mode (SMSG_SPLINE_MOVE_SET_HOVER,
        // i.e. cmangos Unit::SetHover) - Onyxia's air phase, Sapphiron, and friends.
        //
        // This is the discriminator the swim synthesis was missing. UNIT_FLAG_SWIMMING is set on EVERY
        // InhabitType=3 creature (Creature.cpp:571) as a capability hint, not as "is in water right
        // now", so airborne dragons were registered as swimmers too. Forcing swim state on them costs
        // the client its fly/hover anim tier and it falls back to the ground walk cycle: Onyxia visibly
        // walking through the air. An airborne creature is never swimming, so hover always wins.
        private bool IsAirborneMob(WowGuid128 guid)
        {
            return GetSession().GameState.HoveringUnits.Contains(guid);
        }

        // Fix up a swimming creature's (already Modern-format) movement flags before they reach
        // the modern client. The vanilla protocol never sent gravity/anim-tier state for aquatic
        // NPCs, so without this the 1.14 client ground-snaps the model to the water surface and
        // plays the walk anim. Ensure the Swimming bit is set, drop the falling bits, and disable
        // gravity so the client renders the swim moving anim. Call AFTER the WotLK->Modern cast.
        //
        // Physics-shaped, so it keys off KnownSwimmingMobs (observed IN WATER) and never off
        // WaterCapableMobs - baking DisableGravity into a land creature's create block leaves it
        // hovering off the ground.
        private bool ApplySwimOverrideIfNeeded(WowGuid128 guid, MovementInfo moveInfo)
        {
            if (!IsSwimmingMob(guid) || IsAirborneMob(guid))
                return false;

            moveInfo.Flags &= ~(uint)(MovementFlagModern.Falling | MovementFlagModern.FallingFar);
            moveInfo.Flags |= (uint)(MovementFlagModern.Swimming | MovementFlagModern.DisableGravity);
            moveInfo.FallTime = 0;
            moveInfo.JumpVerticalSpeed = 0.0f;
            moveInfo.JumpHorizontalSpeed = 0.0f;
            return true;
        }

        // The modern spline flags carry the anim tier as a small VALUE in the low 3 bits (0 Ground,
        // 1 Swim, 2 Hover, 3 Fly, 4 Submerged), not as independent bits - so a tier has to be masked
        // out before a new one is OR'd in, or e.g. Swim|Fly silently reads back as something else.
        private const SplineFlagModern SPLINE_ANIM_TIER_MASK = (SplineFlagModern)0x7;

        // Vanilla stand states. Only the two "lying flat on the floor" ones matter here; the sit
        // variants anchor the model to a chair the client already places correctly.
        private const byte UNIT_STAND_STATE_SLEEP = 3;
        private const byte UNIT_STAND_STATE_DEAD  = 7;

        // Keep a lying NPC at the exact Z the legacy server sent. The 1.12 client positioned units
        // verbatim; the 1.14 client ground-snaps them, and interior WMO doodads (beds, bedrolls,
        // tables) carry no unit collision - so a scripted NPC laid on a bunk falls through it and ends
        // up inside/below the floor mesh, invisible. Quest Triage's patients are the visible case.
        //
        // Creatures only - a lying player is corpse/AFK state and must keep normal physics.
        private void ApplyLyingGravityOverride(WowGuid128 guid, byte standState, ObjectUpdate updateData)
        {
            if (guid.GetHighType() != HighGuidType.Creature)
                return;

            // A real corpse also reports the DEAD stand state, and corpses must keep falling normally
            // (a mob killed mid-air would otherwise hang there). Only a LIVING unit lying down is the
            // scripted-prop case. Health is parsed earlier in the same block, so it is already set on a
            // create and on any update that changed it; otherwise fall back to the cached field.
            long health = updateData.UnitData.Health ??
                          GetSession().GameState.GetLegacyFieldValueUInt32(guid, UnitField.UNIT_FIELD_HEALTH);

            var registry = GetSession().GameState.LyingUnitsGravityOff;
            bool lying = health > 0 &&
                         (standState == UNIT_STAND_STATE_SLEEP || standState == UNIT_STAND_STATE_DEAD);

            if (lying)
            {
                // Add() is false when we already turned gravity off for this unit; BYTES_1 rides along
                // with plenty of unrelated field changes, so re-sending the packet every time would spam.
                if (!registry.Add(guid) && updateData.CreateData == null)
                    return;

                if (updateData.CreateData?.MoveInfo != null)
                {
                    // Create block: bake it into the movement info the client builds the unit from,
                    // so it never gets a frame where gravity applies.
                    updateData.CreateData.MoveInfo.Flags &= ~(uint)(MovementFlagModern.Falling | MovementFlagModern.FallingFar);
                    updateData.CreateData.MoveInfo.Flags |= (uint)MovementFlagModern.DisableGravity;
                    updateData.CreateData.MoveInfo.FallTime = 0;
                }
                else
                {
                    // Values update (an already-visible NPC lies down): movement flags only travel in
                    // create blocks, so the state change has to go out as its own spline message.
                    MoveSplineSetFlag gravityOff = new MoveSplineSetFlag(Opcode.SMSG_MOVE_SPLINE_DISABLE_GRAVITY);
                    gravityOff.MoverGUID = guid;
                    SendPacketToClient(gravityOff);
                }
            }
            else if (registry.Remove(guid))
            {
                // Stood back up - a healed Triage patient is about to run to the doctor, and it would
                // run that path through the air if we left gravity off. Hand it back to physics.
                MoveSplineSetFlag gravityOn = new MoveSplineSetFlag(Opcode.SMSG_MOVE_SPLINE_ENABLE_GRAVITY);
                gravityOn.MoverGUID = guid;
                SendPacketToClient(gravityOn);
            }
        }

        // Handlers for SMSG opcodes coming the legacy world server
        [PacketHandler(Opcode.MSG_MOVE_START_FORWARD)]
        [PacketHandler(Opcode.MSG_MOVE_START_BACKWARD)]
        [PacketHandler(Opcode.MSG_MOVE_STOP)]
        [PacketHandler(Opcode.MSG_MOVE_START_STRAFE_LEFT)]
        [PacketHandler(Opcode.MSG_MOVE_START_STRAFE_RIGHT)]
        [PacketHandler(Opcode.MSG_MOVE_STOP_STRAFE)]
        [PacketHandler(Opcode.MSG_MOVE_START_ASCEND)]
        [PacketHandler(Opcode.MSG_MOVE_START_DESCEND)]
        [PacketHandler(Opcode.MSG_MOVE_STOP_ASCEND)]
        [PacketHandler(Opcode.MSG_MOVE_JUMP)]
        [PacketHandler(Opcode.MSG_MOVE_START_TURN_LEFT)]
        [PacketHandler(Opcode.MSG_MOVE_START_TURN_RIGHT)]
        [PacketHandler(Opcode.MSG_MOVE_STOP_TURN)]
        [PacketHandler(Opcode.MSG_MOVE_START_PITCH_UP)]
        [PacketHandler(Opcode.MSG_MOVE_START_PITCH_DOWN)]
        [PacketHandler(Opcode.MSG_MOVE_STOP_PITCH)]
        [PacketHandler(Opcode.MSG_MOVE_SET_RUN_MODE)]
        [PacketHandler(Opcode.MSG_MOVE_SET_WALK_MODE)]
        [PacketHandler(Opcode.MSG_MOVE_TELEPORT)]
        [PacketHandler(Opcode.MSG_MOVE_SET_FACING)]
        [PacketHandler(Opcode.MSG_MOVE_SET_PITCH)]
        [PacketHandler(Opcode.MSG_MOVE_TOGGLE_COLLISION_CHEAT)]
        [PacketHandler(Opcode.MSG_MOVE_GRAVITY_CHNG)]
        [PacketHandler(Opcode.MSG_MOVE_ROOT)]
        [PacketHandler(Opcode.MSG_MOVE_UNROOT)]
        [PacketHandler(Opcode.MSG_MOVE_START_SWIM)]
        [PacketHandler(Opcode.MSG_MOVE_STOP_SWIM)]
        [PacketHandler(Opcode.MSG_MOVE_START_SWIM_CHEAT)]
        [PacketHandler(Opcode.MSG_MOVE_STOP_SWIM_CHEAT)]
        [PacketHandler(Opcode.MSG_MOVE_HEARTBEAT)]
        [PacketHandler(Opcode.MSG_MOVE_FALL_LAND)]
        [PacketHandler(Opcode.MSG_MOVE_UPDATE_CAN_FLY)]
        [PacketHandler(Opcode.MSG_MOVE_UPDATE_CAN_TRANSITION_BETWEEN_SWIM_AND_FLY)]
        [PacketHandler(Opcode.MSG_MOVE_HOVER)]
        [PacketHandler(Opcode.MSG_MOVE_FEATHER_FALL)]
        [PacketHandler(Opcode.MSG_MOVE_WATER_WALK)]
        void HandleMovementMessages(WorldPacket packet)
        {
            MoveUpdate moveUpdate = new MoveUpdate();
            moveUpdate.MoverGUID = packet.ReadPackedGuid().To128(GetSession().GameState);
            moveUpdate.MoveInfo = new();
            moveUpdate.MoveInfo.ReadMovementInfoLegacy(packet, GetSession().GameState);
            moveUpdate.MoveInfo.Flags = (uint)(((MovementFlagWotLK)moveUpdate.MoveInfo.Flags).CastFlags<MovementFlagModern>());
            ApplySwimOverrideIfNeeded(moveUpdate.MoverGUID, moveUpdate.MoveInfo);
            moveUpdate.MoveInfo.ValidateMovementInfo();
            SendPacketToClient(moveUpdate);
        }

        [PacketHandler(Opcode.MSG_MOVE_KNOCK_BACK)]
        void HandleMoveKnockBack(WorldPacket packet)
        {
            MoveUpdateKnockBack knockback = new MoveUpdateKnockBack();
            knockback.MoverGUID = packet.ReadPackedGuid().To128(GetSession().GameState);
            knockback.MoveInfo = new();
            knockback.MoveInfo.ReadMovementInfoLegacy(packet, GetSession().GameState);
            knockback.MoveInfo.Flags = (uint)(((MovementFlagWotLK)knockback.MoveInfo.Flags).CastFlags<MovementFlagModern>());
            knockback.MoveInfo.JumpSinAngle = packet.ReadFloat();
            knockback.MoveInfo.JumpCosAngle = packet.ReadFloat();
            knockback.MoveInfo.JumpHorizontalSpeed = packet.ReadFloat();
            knockback.MoveInfo.JumpVerticalSpeed = packet.ReadFloat();
            knockback.MoveInfo.ValidateMovementInfo();
            SendPacketToClient(knockback);
        }

        [PacketHandler(Opcode.SMSG_MOVE_KNOCK_BACK)]
        void HandleMoveForceKnockBack(WorldPacket packet)
        {
            MoveKnockBack knockback = new MoveKnockBack();
            knockback.MoverGUID = packet.ReadPackedGuid().To128(GetSession().GameState);
            knockback.MoveCounter = packet.ReadUInt32();
            knockback.Direction = packet.ReadVector2();
            knockback.HorizontalSpeed = packet.ReadFloat();
            knockback.VerticalSpeed = packet.ReadFloat();
            SendPacketToClient(knockback);
        }

        [PacketHandler(Opcode.SMSG_CONTROL_UPDATE)]
        void HandleControlUpdate(WorldPacket packet)
        {
            ControlUpdate control = new ControlUpdate();
            control.Guid = packet.ReadPackedGuid().To128(GetSession().GameState);
            control.HasControl = packet.ReadBool();
            SendPacketToClient(control);
        }

        [PacketHandler(Opcode.MSG_MOVE_TELEPORT_ACK)]
        void HandleMoveTeleportAck(WorldPacket packet)
        {
            WowGuid128 guid = packet.ReadPackedGuid().To128(GetSession().GameState);

            if (GetSession().GameState.IsInTaxiFlight &&
                GetSession().GameState.CurrentPlayerGuid == guid)
            {
                ControlUpdate control = new ControlUpdate();
                control.Guid = guid;
                control.HasControl = true;
                SendPacketToClient(control);
                GetSession().GameState.IsInTaxiFlight = false;
            }

            MoveTeleport teleport = new MoveTeleport();
            teleport.MoverGUID = guid;
            teleport.MoveCounter = packet.ReadUInt32();
            MovementInfo moveInfo = new();
            moveInfo.ReadMovementInfoLegacy(packet, GetSession().GameState);
            moveInfo.Flags = (uint)(((MovementFlagWotLK)moveInfo.Flags).CastFlags<MovementFlagModern>());
            moveInfo.ValidateMovementInfo();
            teleport.Position = moveInfo.Position;
            teleport.Orientation = moveInfo.Orientation;
            teleport.TransportGUID = moveInfo.TransportGuid;
            if (moveInfo.TransportSeat > 0)
            {
                teleport.Vehicle = new();
                teleport.Vehicle.VehicleSeatIndex = moveInfo.TransportSeat;
            }
            SendPacketToClient(teleport);
        }

        [PacketHandler(Opcode.SMSG_TRANSFER_PENDING)]
        void HandleTransferPending(WorldPacket packet)
        {
            if (GetSession().GameState.IsWaitingForWorldPortAck)
            {
                Log.Print(LogType.Error, "Skipping SMSG_TRANSFER_PENDING, client is already being teleported.");
                return;
            }

            TransferPending transfer = new TransferPending();
            transfer.MapID = GetSession().GameState.PendingTransferMapId = packet.ReadUInt32();
            transfer.OldMapPosition = Vector3.Zero;
            SendPacketToClient(transfer);
            GetSession().GameState.IsFirstEnterWorld = false;
            GetSession().GameState.IsWaitingForNewWorld = true;

            SuspendToken suspend = new();
            suspend.SequenceIndex = 3;
            suspend.Reason = 1;
            SendPacketToClient(suspend);
        }

        [PacketHandler(Opcode.SMSG_TRANSFER_ABORTED)]
        void HandleTransferAborted(WorldPacket packet)
        {
            TransferAborted transfer = new TransferAborted();

            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
                transfer.MapID = packet.ReadUInt32();
            else
                transfer.MapID = GetSession().GameState.PendingTransferMapId;

            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
                transfer.Reason = (TransferAbortReasonModern)packet.ReadUInt8();
            else
            {
                TransferAbortReasonLegacy legacyReason = (TransferAbortReasonLegacy)packet.ReadUInt8();
                transfer.Reason = (TransferAbortReasonModern)Enum.Parse(typeof(TransferAbortReasonModern), legacyReason.ToString());
            }

            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
                transfer.Arg = packet.ReadUInt8();

            SendPacketToClient(transfer);
            GetSession().GameState.IsWaitingForNewWorld = false;
        }

        [PacketHandler(Opcode.SMSG_NEW_WORLD)]
        void HandleNewWorld(WorldPacket packet)
        {
            NewWorld teleport = new NewWorld();
            GetSession().GameState.CurrentMapId = teleport.MapID = packet.ReadUInt32();
            teleport.Position = packet.ReadVector3();
            teleport.Orientation = packet.ReadFloat();
            teleport.Reason = 4;
            GetSession().GameState.IsFirstEnterWorld = false;

            if (GetSession().GameState.IsWaitingForNewWorld)
            {
                GetSession().GameState.IsWaitingForNewWorld = false;
                GetSession().GameState.IsWaitingForWorldPortAck = true;

                // The client discards and rebuilds its world on this teleport. The legacy server
                // sends no per-object SMSG_DESTROY_OBJECT / out-of-range block for the old map's
                // objects, so our per-guid synthesized-state registries would otherwise keep stale
                // entries. On return to the old map the same-guid creature is re-created with stale
                // membership, which (e.g. KnownSwimmingMobs) bakes DisableGravity into its create
                // block and the 1.14 client renders it hovering ~2yd off the ground. Clear them; the
                // fresh creates on the new map re-seed whatever is actually true there. (Object
                // caches are left alone: a create block rebuilds its cache entry via ObjectUpdateBuilder.)
                GetSession().GameState.HoveringUnits.Clear();
                GetSession().GameState.KnownSwimmingMobs.Clear();
                GetSession().GameState.WaterCapableMobs.Clear();
                GetSession().GameState.LyingUnitsGravityOff.Clear();
                GetSession().GameState.ForcedStealthAnimUnits.Clear();

                SendPacketToClient(teleport);
                if (teleport.MapID > 1)
                {
                    UpdateLastInstance instance = new();
                    instance.MapID = teleport.MapID;
                    SendPacketToClient(instance);

                    if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180))
                        SendPacketToClient(new TimeSyncRequest());

                    ResumeToken resume = new();
                    resume.SequenceIndex = 3;
                    resume.Reason = 1;
                    SendPacketToClient(resume);
                }

                WorldServerInfo info = new();
                if (teleport.MapID > 1)
                {
                    info.DifficultyID = 1;
                    info.InstanceGroupSize = 5;
                }
                SendPacketToClient(info);
            }
        }

        // for server controlled units
        [PacketHandler(Opcode.SMSG_MOVE_SPLINE_SET_FLIGHT_BACK_SPEED)]
        [PacketHandler(Opcode.SMSG_MOVE_SPLINE_SET_FLIGHT_SPEED)]
        [PacketHandler(Opcode.SMSG_MOVE_SPLINE_SET_PITCH_RATE)]
        [PacketHandler(Opcode.SMSG_MOVE_SPLINE_SET_RUN_BACK_SPEED)]
        [PacketHandler(Opcode.SMSG_MOVE_SPLINE_SET_RUN_SPEED)]
        [PacketHandler(Opcode.SMSG_MOVE_SPLINE_SET_SWIM_BACK_SPEED)]
        [PacketHandler(Opcode.SMSG_MOVE_SPLINE_SET_SWIM_SPEED)]
        [PacketHandler(Opcode.SMSG_MOVE_SPLINE_SET_TURN_RATE)]
        [PacketHandler(Opcode.SMSG_MOVE_SPLINE_SET_WALK_BACK_SPEED)]
        [PacketHandler(Opcode.SMSG_MOVE_SPLINE_SET_WALK_SPEED)]
        void HandleMoveSplineSetSpeed(WorldPacket packet)
        {
            MoveSplineSetSpeed speed = new MoveSplineSetSpeed(packet.GetUniversalOpcode(false));
            speed.MoverGUID = packet.ReadPackedGuid().To128(GetSession().GameState);
            speed.Speed = packet.ReadFloat();
            SendPacketToClient(speed);
        }

        // for own player
        [PacketHandler(Opcode.SMSG_FORCE_WALK_SPEED_CHANGE)]
        [PacketHandler(Opcode.SMSG_FORCE_RUN_SPEED_CHANGE)]
        [PacketHandler(Opcode.SMSG_FORCE_RUN_BACK_SPEED_CHANGE)]
        [PacketHandler(Opcode.SMSG_FORCE_SWIM_SPEED_CHANGE)]
        [PacketHandler(Opcode.SMSG_FORCE_SWIM_BACK_SPEED_CHANGE)]
        [PacketHandler(Opcode.SMSG_FORCE_TURN_RATE_CHANGE)]
        [PacketHandler(Opcode.SMSG_FORCE_FLIGHT_SPEED_CHANGE)]
        [PacketHandler(Opcode.SMSG_FORCE_FLIGHT_BACK_SPEED_CHANGE)]
        [PacketHandler(Opcode.SMSG_FORCE_PITCH_RATE_CHANGE)]
        void HandleMoveForceSpeedChange(WorldPacket packet)
        { // for own player
            string opcodeName = packet.GetUniversalOpcode(false).ToString().Replace("SMSG_FORCE_", "SMSG_MOVE_SET_").Replace("_CHANGE", "");
            Opcode universalOpcode = Opcodes.GetUniversalOpcode(opcodeName);

            MoveSetSpeed speed = new MoveSetSpeed(universalOpcode);
            speed.MoverGUID = packet.ReadPackedGuid().To128(GetSession().GameState);
            speed.MoveCounter = packet.ReadUInt32();

            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180) &&
                packet.GetUniversalOpcode(false) == Opcode.SMSG_FORCE_RUN_SPEED_CHANGE)
            {
                packet.ReadUInt8(); // unk byte
            }

            speed.Speed = packet.ReadFloat();
            SendPacketToClient(speed);

            // Convenience in vanilla to use SwimSpeed as FlySpeed
            if (universalOpcode is Opcode.SMSG_MOVE_SET_SWIM_SPEED
                                or Opcode.SMSG_MOVE_SET_SWIM_BACK_SPEED &&
                LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180))
            {
                var flyOpcode = (Opcode) Enum.Parse(typeof(Opcode), universalOpcode.ToString().Replace("SWIM", "FLIGHT"));
                MoveSetSpeed flySpeed = new MoveSetSpeed(flyOpcode);
                flySpeed.MoverGUID = speed.MoverGUID;
                flySpeed.MoveCounter = speed.MoveCounter;
                flySpeed.Speed = speed.Speed;
                SendPacketToClient(flySpeed);
            }
        }

        // for other players
        [PacketHandler(Opcode.MSG_MOVE_SET_FLIGHT_BACK_SPEED)]
        [PacketHandler(Opcode.MSG_MOVE_SET_FLIGHT_SPEED)]
        [PacketHandler(Opcode.MSG_MOVE_SET_PITCH_RATE)]
        [PacketHandler(Opcode.MSG_MOVE_SET_RUN_BACK_SPEED)]
        [PacketHandler(Opcode.MSG_MOVE_SET_RUN_SPEED)]
        [PacketHandler(Opcode.MSG_MOVE_SET_SWIM_BACK_SPEED)]
        [PacketHandler(Opcode.MSG_MOVE_SET_SWIM_SPEED)]
        [PacketHandler(Opcode.MSG_MOVE_SET_TURN_RATE)]
        [PacketHandler(Opcode.MSG_MOVE_SET_WALK_SPEED)]
        void HandleMoveUpdateSpeed(WorldPacket packet)
        { // for other players
            string opcodeName = packet.GetUniversalOpcode(false).ToString().Replace("MSG_MOVE_SET", "SMSG_MOVE_UPDATE");
            Opcode universalOpcode = Opcodes.GetUniversalOpcode(opcodeName);

            MoveUpdateSpeed speed = new MoveUpdateSpeed(universalOpcode);
            speed.MoverGUID = packet.ReadPackedGuid().To128(GetSession().GameState);
            speed.MoveInfo = new MovementInfo();
            speed.MoveInfo.ReadMovementInfoLegacy(packet, GetSession().GameState);
            var newFlags = ((MovementFlagWotLK)speed.MoveInfo.Flags).CastFlags<MovementFlagModern>();
            speed.MoveInfo.Flags = (uint)(newFlags);
            speed.MoveInfo.ValidateMovementInfo();
            speed.Speed = packet.ReadFloat();
            SendPacketToClient(speed);

            // Convenience in vanilla to use SwimSpeed as FlySpeed
            if (universalOpcode is Opcode.SMSG_MOVE_UPDATE_SWIM_SPEED
                                or Opcode.SMSG_MOVE_UPDATE_SWIM_BACK_SPEED &&
                LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180))
            {
                var flyOpcode = (Opcode) Enum.Parse(typeof(Opcode), universalOpcode.ToString().Replace("SWIM", "FLIGHT"));
                MoveUpdateSpeed flySpeed = new MoveUpdateSpeed(flyOpcode);
                flySpeed.MoverGUID = speed.MoverGUID;
                flySpeed.MoveInfo = speed.MoveInfo;
                flySpeed.Speed = speed.Speed;
                SendPacketToClient(flySpeed);
            }
        }

        [PacketHandler(Opcode.SMSG_MOVE_SPLINE_ROOT)]
        [PacketHandler(Opcode.SMSG_MOVE_SPLINE_UNROOT)]
        [PacketHandler(Opcode.SMSG_MOVE_SPLINE_ENABLE_GRAVITY)]
        [PacketHandler(Opcode.SMSG_MOVE_SPLINE_DISABLE_GRAVITY)]
        [PacketHandler(Opcode.SMSG_MOVE_SPLINE_SET_FEATHER_FALL)]
        [PacketHandler(Opcode.SMSG_MOVE_SPLINE_SET_NORMAL_FALL)]
        [PacketHandler(Opcode.SMSG_MOVE_SPLINE_SET_HOVER)]
        [PacketHandler(Opcode.SMSG_MOVE_SPLINE_UNSET_HOVER)]
        [PacketHandler(Opcode.SMSG_MOVE_SPLINE_SET_WATER_WALK)]
        [PacketHandler(Opcode.SMSG_MOVE_SPLINE_SET_LAND_WALK)]
        [PacketHandler(Opcode.SMSG_MOVE_SPLINE_START_SWIM)]
        [PacketHandler(Opcode.SMSG_MOVE_SPLINE_STOP_SWIM)]
        [PacketHandler(Opcode.SMSG_MOVE_SPLINE_SET_RUN_MODE)]
        [PacketHandler(Opcode.SMSG_MOVE_SPLINE_SET_WALK_MODE)]
        [PacketHandler(Opcode.SMSG_MOVE_SPLINE_SET_FLYING)]
        [PacketHandler(Opcode.SMSG_MOVE_SPLINE_UNSET_FLYING)]
        void HandleSplineMovementMessages(WorldPacket packet)
        {
            var universalOpcode = packet.GetUniversalOpcode(false);
            MoveSplineSetFlag spline = new MoveSplineSetFlag(universalOpcode);
            spline.MoverGUID = packet.ReadPackedGuid().To128(GetSession().GameState);
            SendPacketToClient(spline);

            // Vanilla has no gravity/anim-tier concept for creatures: a parked hovering unit
            // (Sapphiron air phase) reverts to grounded pose on modern clients as soon as the
            // last fly spline ends or a cast anim plays. Synthesize the two modern airborne
            // states from the hover toggle: spline gravity disable + UNIT_FIELD_BYTES_1 AnimTier.
            if (universalOpcode == Opcode.SMSG_MOVE_SPLINE_SET_HOVER ||
                universalOpcode == Opcode.SMSG_MOVE_SPLINE_UNSET_HOVER)
            {
                bool hoverOn = universalOpcode == Opcode.SMSG_MOVE_SPLINE_SET_HOVER;

                // Tracked so SpellStart/SpellGo can strip cast visuals of hovering casters:
                // the modern client's cast animation overrides the hover idle (grounded pose)
                if (hoverOn)
                    GetSession().GameState.HoveringUnits.Add(spline.MoverGUID);
                else
                    GetSession().GameState.HoveringUnits.Remove(spline.MoverGUID);

                MoveSplineSetFlag gravity = new MoveSplineSetFlag(hoverOn ? Opcode.SMSG_MOVE_SPLINE_DISABLE_GRAVITY : Opcode.SMSG_MOVE_SPLINE_ENABLE_GRAVITY);
                gravity.MoverGUID = spline.MoverGUID;
                SendPacketToClient(gravity);

                // The hover IDLE animation on modern clients is driven by this dedicated packet
                // (same thing the PlayHoverAnim create-bit does for units created mid-hover)
                SetPlayHoverAnim hoverAnim = new SetPlayHoverAnim();
                hoverAnim.UnitGUID = spline.MoverGUID;
                hoverAnim.PlayHoverAnim = hoverOn;
                SendPacketToClient(hoverAnim);

                ObjectUpdate updateData = new ObjectUpdate(spline.MoverGUID, UpdateTypeModern.Values, GetSession());
                if (updateData.UnitData != null) // spline movers are units by protocol, but stay safe
                {
                    UpdateObject updateObject = new UpdateObject(GetSession().GameState);
                    updateData.UnitData.AnimTier = (byte)(hoverOn ? 2 : 0); // 2 = Hover, 0 = Ground
                    updateObject.ObjectUpdates.Add(updateData);
                    SendPacketToClient(updateObject);
                }
            }
        }

        [PacketHandler(Opcode.SMSG_MOVE_ROOT)]
        [PacketHandler(Opcode.SMSG_MOVE_UNROOT)]
        [PacketHandler(Opcode.SMSG_MOVE_SET_WATER_WALK)]
        [PacketHandler(Opcode.SMSG_MOVE_SET_LAND_WALK)]
        [PacketHandler(Opcode.SMSG_MOVE_SET_HOVERING)]
        [PacketHandler(Opcode.SMSG_MOVE_UNSET_HOVERING)]
        [PacketHandler(Opcode.SMSG_MOVE_SET_CAN_FLY)]
        [PacketHandler(Opcode.SMSG_MOVE_UNSET_CAN_FLY)]
        [PacketHandler(Opcode.SMSG_MOVE_ENABLE_TRANSITION_BETWEEN_SWIM_AND_FLY)]
        [PacketHandler(Opcode.SMSG_MOVE_DISABLE_TRANSITION_BETWEEN_SWIM_AND_FLY)]
        [PacketHandler(Opcode.SMSG_MOVE_DISABLE_GRAVITY)]
        [PacketHandler(Opcode.SMSG_MOVE_ENABLE_GRAVITY)]
        [PacketHandler(Opcode.SMSG_MOVE_SET_FEATHER_FALL)]
        [PacketHandler(Opcode.SMSG_MOVE_SET_NORMAL_FALL)]
        void HandleMoveForceFlagChange(WorldPacket packet)
        {
            MoveSetFlag flag = new MoveSetFlag(packet.GetUniversalOpcode(false));
            flag.MoverGUID = packet.ReadPackedGuid().To128(GetSession().GameState);
            flag.MoveCounter = packet.ReadUInt32();
            SendPacketToClient(flag);
        }

        [PacketHandler(Opcode.SMSG_COMPRESSED_MOVES)]
        void HandleCompressedMoves(WorldPacket packet)
        {
            var uncompressedSize = packet.ReadInt32();

            WorldPacket pkt = packet.Inflate(uncompressedSize);

            while (pkt.CanRead())
            {
                var size = pkt.ReadUInt8();
                var opc = pkt.ReadUInt16();
                var data = pkt.ReadBytes((uint)(size - 2));

                var pkt2 = new WorldPacket(opc, data);
                pkt2.SetReceiveTime(pkt.GetReceivedTime());
                HandlePacket(pkt2);
            }
        }

        [PacketHandler(Opcode.SMSG_ON_MONSTER_MOVE)]
        [PacketHandler(Opcode.SMSG_MONSTER_MOVE_TRANSPORT)]
        void HandleMonsterMove(WorldPacket packet)
        {
            WowGuid128 guid = packet.ReadPackedGuid().To128(GetSession().GameState);
            ServerSideMovement moveSpline = new();

            if (packet.GetUniversalOpcode(false) == Opcode.SMSG_MONSTER_MOVE_TRANSPORT)
            {
                moveSpline.TransportGuid = packet.ReadPackedGuid().To128(GetSession().GameState);
                if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_1_0_9767))
                    moveSpline.TransportSeat = packet.ReadInt8();
            }

            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_1_0_9767)) // no idea when this was added exactly
                packet.ReadBool(); // "Toggle AnimTierInTrans"

            moveSpline.StartPosition = packet.ReadVector3();
            moveSpline.SplineId = packet.ReadUInt32();
            SplineTypeLegacy type = (SplineTypeLegacy)packet.ReadUInt8();
            switch (type)
            {
                case SplineTypeLegacy.FacingSpot:
                {
                    moveSpline.SplineType = SplineTypeModern.FacingSpot;
                    moveSpline.FinalFacingSpot = packet.ReadVector3();
                    break;
                }
                case SplineTypeLegacy.FacingTarget:
                {
                    moveSpline.SplineType = SplineTypeModern.FacingTarget;
                    moveSpline.FinalFacingGuid = packet.ReadGuid().To128(GetSession().GameState);
                    break;
                }
                case SplineTypeLegacy.FacingAngle:
                {
                    moveSpline.SplineType = SplineTypeModern.FacingAngle;
                    moveSpline.FinalOrientation = packet.ReadFloat();
                    MovementInfo.ClampOrientation(ref moveSpline.FinalOrientation);
                    break;
                }
                case SplineTypeLegacy.Stop:
                {
                    moveSpline.SplineType = SplineTypeModern.None;
                    // Keep the swim idle anim on stop; without AnimTierSwim the modern client
                    // reverts to ground anim and snaps the mob's Z to the water surface.
                    if (IsSwimmingMob(guid) && !IsAirborneMob(guid))
                        moveSpline.SplineFlags |= SplineFlagModern.AnimTierSwim | SplineFlagModern.CanSwim;
                    MonsterMove moveStop = new MonsterMove(guid, moveSpline);
                    SendPacketToClient(moveStop);
                    return;
                }
            }

            bool hasAnimTier;
            bool hasTrajectory;
            bool hasCatmullRom;
            bool hasTaxiFlightFlags;
            if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180))
            {
                var splineFlags = (SplineFlagVanilla)packet.ReadUInt32();
                hasAnimTier = false;
                hasTrajectory = false;
                hasCatmullRom = splineFlags.HasAnyFlag(SplineFlagVanilla.Flying);
                hasTaxiFlightFlags = splineFlags == (SplineFlagVanilla.Runmode | SplineFlagVanilla.Flying);

                if (splineFlags == SplineFlagVanilla.Runmode) // Default spline flags used by Vanilla and TBC servers
                {
                    moveSpline.SplineFlags = SplineFlagModern.Unknown5;
                    UnitFlagsVanilla unitFlags = (UnitFlagsVanilla)GetSession().GameState.GetLegacyFieldValueUInt32(guid, UnitField.UNIT_FIELD_FLAGS);
                    if (unitFlags.HasFlag(UnitFlagsVanilla.CanSwim))
                        moveSpline.SplineFlags |= SplineFlagModern.CanSwim;
                    if (type == SplineTypeLegacy.Normal && !unitFlags.HasFlag(UnitFlagsVanilla.InCombat))
                        moveSpline.SplineFlags |= SplineFlagModern.Steering | SplineFlagModern.Unknown10;
                }
                else
                    moveSpline.SplineFlags = splineFlags.CastFlags<SplineFlagModern>();
            }
            else if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V3_0_2_9056))
            {
                var splineFlags = (SplineFlagTBC)packet.ReadUInt32();
                hasAnimTier = false;
                hasTrajectory = false;
                hasCatmullRom = splineFlags.HasAnyFlag(SplineFlagTBC.Flying);
                hasTaxiFlightFlags = splineFlags == (SplineFlagTBC.Runmode | SplineFlagTBC.Flying);

                if (splineFlags == SplineFlagTBC.Runmode) // Default spline flags used by Vanilla and TBC servers
                {
                    moveSpline.SplineFlags = SplineFlagModern.Unknown5;
                    UnitFlags unitFlags = (UnitFlags)GetSession().GameState.GetLegacyFieldValueUInt32(guid, UnitField.UNIT_FIELD_FLAGS);
                    if (unitFlags.HasFlag(UnitFlags.CanSwim))
                        moveSpline.SplineFlags |= SplineFlagModern.CanSwim;
                    if (type == SplineTypeLegacy.Normal && !unitFlags.HasFlag(UnitFlags.InCombat))
                        moveSpline.SplineFlags |= SplineFlagModern.Steering | SplineFlagModern.Unknown10;
                }
                else
                    moveSpline.SplineFlags = splineFlags.CastFlags<SplineFlagModern>();
            }
            else
            {
                var splineFlags = (SplineFlagWotLK)packet.ReadUInt32();
                hasAnimTier = splineFlags.HasAnyFlag(SplineFlagWotLK.AnimationTier);
                hasTrajectory = splineFlags.HasAnyFlag(SplineFlagWotLK.Trajectory);
                hasCatmullRom = splineFlags.HasAnyFlag(SplineFlagWotLK.Flying | SplineFlagWotLK.CatmullRom);
                hasTaxiFlightFlags = splineFlags == (SplineFlagWotLK.WalkMode | SplineFlagWotLK.Flying);
                moveSpline.SplineFlags = splineFlags.CastFlags<SplineFlagModern>();
            }

            // Swimming creatures: synthesize the swim spline state so the modern client plays the swim
            // moving animation (AnimTierSwim + CanSwim) and does not ground-snap (strip SmoothGroundPath).
            // Read the unit flag DIRECTLY from the cached fields (same source the vanilla branch uses
            // above) so this does not depend on create-time seeding firing first. This core sets
            // UNIT_FLAG_SWIMMING (0x8000, read as UnitFlagsVanilla.CanSwim) on every water-capable
            // creature; the 1.14 client still selects walk-vs-swim by liquid, so this is a capability
            // hint, not a forced on-land swim. Restricted to creatures (never players/pets).
            uint cachedUnitFlags = GetSession().GameState.GetLegacyFieldValueUInt32(guid, UnitField.UNIT_FIELD_FLAGS);
            bool unitCanSwim = ((UnitFlagsVanilla)cachedUnitFlags).HasAnyFlag(UnitFlagsVanilla.CanSwim) ||
                               IsWaterCapableMob(guid);
            bool observedSwimming = IsSwimmingMob(guid);

            // ...but a spline the server explicitly flagged Flying is an authoritative airborne path:
            // cmangos FORCED_MOVEMENT_FLIGHT (Onyxia's air phase, Eranikus, the scourge invasion movers)
            // rides on it. Since UNIT_FLAG_SWIMMING is only a capability bit that this core sets on every
            // InhabitType=3 creature - Onyxia included - the swim synthesis must not claim those splines.
            // Only a creature we have actually seen in water (MOVEFLAG_SWIMMING) keeps the swim treatment
            // on a Flying spline, which is the underwater 3D roam the strip was written for.
            bool authoritativeFlyPath = guid.GetHighType() == HighGuidType.Creature &&
                                        moveSpline.SplineFlags.HasAnyFlag(SplineFlagModern.Flying) &&
                                        (IsAirborneMob(guid) || !observedSwimming);

            if (authoritativeFlyPath)
            {
                // Keep Flying (3D path, exact Z) and pin the anim tier to Fly. The vanilla protocol has
                // no anim tier at all, so without this the modern client animates the flight with the
                // ground run cycle - Onyxia visibly running through the air across her lair.
                moveSpline.SplineFlags &= ~(SplineFlagModern.SmoothGroundPath | SplineFlagModern.Falling | SplineFlagModern.FallingSlow);
                moveSpline.SplineFlags = (moveSpline.SplineFlags & ~SPLINE_ANIM_TIER_MASK) | SplineFlagModern.AnimTierFly;
            }
            else if (guid.GetHighType() == HighGuidType.Creature && (unitCanSwim || observedSwimming) &&
                     !IsAirborneMob(guid))
            {
                moveSpline.SplineFlags &= ~(SplineFlagModern.SmoothGroundPath | SplineFlagModern.Falling | SplineFlagModern.FallingSlow | SplineFlagModern.Flying);
                moveSpline.SplineFlags = (moveSpline.SplineFlags & ~SPLINE_ANIM_TIER_MASK) | SplineFlagModern.AnimTierSwim;
                moveSpline.SplineFlags |= SplineFlagModern.CanSwim;
            }

            if (hasAnimTier)
            {
                packet.ReadUInt8(); // Animation State
                packet.ReadInt32(); // Async-time in ms
            }

            moveSpline.SplineTimeFull = packet.ReadUInt32();

            if (hasTrajectory)
            {
                packet.ReadFloat(); // Vertical Speed
                packet.ReadInt32(); // Async-time in ms
            }

            moveSpline.SplineCount = packet.ReadUInt32();

            if (hasCatmullRom)
            {
                for (var i = 0; i < moveSpline.SplineCount; i++)
                {
                    Vector3 vec = packet.ReadVector3();

                    if (moveSpline != null)
                        moveSpline.SplinePoints.Add(vec);
                }
                moveSpline.SplineFlags |= SplineFlagModern.UncompressedPath;
            }
            else
            {
                moveSpline.EndPosition = packet.ReadVector3();

                Vector3 mid = (moveSpline.StartPosition + moveSpline.EndPosition) * 0.5f;

                for (var i = 1; i < moveSpline.SplineCount; i++)
                {
                    var vec = packet.ReadPackedVector3();

                    if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
                        vec = mid - vec;
                    else
                        vec = moveSpline.EndPosition - vec;

                    moveSpline.SplinePoints.Add(vec);
                }
            }

            bool isTaxiFlight = (hasTaxiFlightFlags &&
                                (GetSession().GameState.IsWaitingForTaxiStart ||
                                 Math.Abs(packet.GetReceivedTime() - GetSession().GameState.CurrentPlayerCreateTime) <= 1000) &&
                                 GetSession().GameState.CurrentPlayerGuid == guid);

            if (isTaxiFlight)
            {
                // Exact sequence of packets from sniff.
                // Client instantly teleports to destination if anything is left out.

                ServerSideMovement stopSpline = new();
                stopSpline.StartPosition = moveSpline.StartPosition;
                stopSpline.SplineId = moveSpline.SplineId - 2;
                MonsterMove moveStop = new MonsterMove(guid, stopSpline);
                SendPacketToClient(moveStop);

                ControlUpdate update = new();
                update.Guid = guid;
                update.HasControl = false;
                SendPacketToClient(update);

                stopSpline.SplineId = moveSpline.SplineId - 1;
                moveStop = new MonsterMove(guid, stopSpline);
                SendPacketToClient(moveStop);

                update = new();
                update.Guid = guid;
                update.HasControl = false;
                SendPacketToClient(update);

                moveSpline.SplineFlags = SplineFlagModern.Flying |
                                         SplineFlagModern.CatmullRom |
                                         SplineFlagModern.CanSwim |
                                         SplineFlagModern.UncompressedPath |
                                         SplineFlagModern.Unknown5 |
                                         SplineFlagModern.Steering |
                                         SplineFlagModern.Unknown10;

                if (!hasCatmullRom && moveSpline.EndPosition != Vector3.Zero)
                    moveSpline.SplinePoints.Add(moveSpline.EndPosition);
            }

            MonsterMove monsterMove = new MonsterMove(guid, moveSpline);
            SendPacketToClient(monsterMove);

            if (isTaxiFlight)
            {
                if (GetSession().GameState.IsWaitingForTaxiStart)
                {
                    ActivateTaxiReplyPkt taxi = new();
                    taxi.Reply = ActivateTaxiReply.Ok;
                    SendPacketToClient(taxi);
                    GetSession().GameState.IsWaitingForTaxiStart = false;
                }
                GetSession().GameState.IsInTaxiFlight = true;

                // Modern client (1.14/2.5) gates bag/equip UI on HasFullControlOfMyCharacter,
                // which is false while client control is removed. The taxi spline is
                // server-authoritative and ignores client input, so restoring control here
                // unlocks the bag UI without letting the player deviate from the path.
                ControlUpdate restoreControl = new();
                restoreControl.Guid = guid;
                restoreControl.HasControl = true;
                SendPacketToClient(restoreControl);
            }
        }
    }
}
