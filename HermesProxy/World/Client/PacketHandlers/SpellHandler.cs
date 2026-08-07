using Framework;
using HermesProxy.Enums;
using HermesProxy.World.Enums;
using HermesProxy.World.Server.Packets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace HermesProxy.World.Client
{
    public partial class WorldClient
    {
        // Nature's Grasp (druid talent 761) is a 1-point talent whose spell auto-upgrades with the
        // player's Entangling Roots rank: 16689 (r1) -> 16810 -> 16811 -> 16812 -> 16813 -> 17329,
        // each upgrade superceding the previous rank. The modern client marks a talent as taken by
        // matching a KNOWN spell against the talent's SpellRank[] list, which contains only rank 1
        // (16689). Once NG has upgraded past rank 1 the client no longer knows 16689, so it shows the
        // talent as un-taken (with a wrong pip count if higher ranks are listed). Keeping rank 1 in
        // the client's known-spell set lets the 1-rank talent record match -> correct 1/1 taken.
        private const uint NaturesGraspRank1 = 16689;
        private static readonly uint[] NaturesGraspUpgradedRanks = { 16810, 16811, 16812, 16813, 17329 };

        // Handlers for SMSG opcodes coming the legacy world server
        [PacketHandler(Opcode.SMSG_SEND_KNOWN_SPELLS)]
        void HandleSendKnownSpells(WorldPacket packet)
        {
            SendKnownSpells spells = new SendKnownSpells();
            spells.InitialLogin = packet.ReadBool();
            ushort spellCount = packet.ReadUInt16();
            for (ushort i = 0; i < spellCount; i++)
            {
                uint spellId;
                if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_1_0_9767))
                    spellId = packet.ReadUInt32();
                else
                    spellId = packet.ReadUInt16();
                spells.KnownSpells.Add(spellId);
                packet.ReadInt16();
            }

            // Keep Nature's Grasp rank 1 known so its talent stays marked taken (see class comment).
            if (NaturesGraspUpgradedRanks.Any(r => spells.KnownSpells.Contains(r)) &&
                !spells.KnownSpells.Contains(NaturesGraspRank1))
                spells.KnownSpells.Add(NaturesGraspRank1);

            SendPacketToClient(spells);

            // The legacy server only sends SMSG_SET_PROFICIENCY when a new proficiency is
            // learned (never at login). The modern client needs an explicit proficiency packet
            // at login, otherwise it defaults to showing all items as equippable in tooltips.
            uint weaponProf = 0;
            uint armorProf = 0;
            foreach (uint sid in spells.KnownSpells)
            {
                if (GameData.WeaponProficiencySpells.TryGetValue(sid, out uint wMask))
                    weaponProf |= wMask;
                if (GameData.ArmorProficiencySpells.TryGetValue(sid, out uint aMask))
                    armorProf |= aMask;
            }
            if (weaponProf != 0)
                SendPacketToClient(new SetProficiency { ProficiencyClass = 2, ProficiencyMask = weaponProf });
            if (armorProf != 0)
                SendPacketToClient(new SetProficiency { ProficiencyClass = 4, ProficiencyMask = armorProf });

            ushort cooldownCount = packet.ReadUInt16();
            if (cooldownCount != 0)
            {
                SendSpellHistory histories = new SendSpellHistory();
                for (ushort i = 0; i < cooldownCount; i++)
                {
                    SpellHistoryEntry history = new SpellHistoryEntry();

                    uint spellId;
                    if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_1_0_9767))
                        spellId = packet.ReadUInt32();
                    else
                        spellId = packet.ReadUInt16();
                    history.SpellID = spellId;

                    uint itemId;
                    if (LegacyVersion.AddedInVersion(ClientVersionBuild.V4_2_2_14545))
                        itemId = packet.ReadUInt32();
                    else
                        itemId = packet.ReadUInt16();
                    history.ItemID = itemId;

                    history.Category = packet.ReadUInt16();
                    history.RecoveryTime = packet.ReadInt32();
                    history.CategoryRecoveryTime = packet.ReadInt32();

                    histories.Entries.Add(history);
                }
                SendPacketToClient(histories, Opcode.SMSG_SEND_UNLEARN_SPELLS);
            }

            // These packets don't exist in Vanilla.
            if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180))
            {
                SendPacketToClient(new SendUnlearnSpells());
                SendPacketToClient(new SendSpellCharges());
            }
        }

        [PacketHandler(Opcode.SMSG_SUPERCEDED_SPELLS)]
        void HandleSupercededSpells(WorldPacket packet)
        {
            SupercededSpells spells = new SupercededSpells();
            uint spellId;
            uint supercededId;
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
            {
                supercededId = packet.ReadUInt32();
                spellId = packet.ReadUInt32();
            }
            else
            {
                supercededId = packet.ReadUInt16();
                spellId = packet.ReadUInt16();
            }
            // When Nature's Grasp auto-upgrades (16689 -> 16810) the legacy server supercedes rank 1;
            // forward it as a plain learn of the new rank so the client keeps 16689 known and the
            // talent stays marked taken (see class comment).
            if (supercededId == NaturesGraspRank1)
            {
                LearnedSpells learnedInstead = new LearnedSpells();
                learnedInstead.Spells.Add(spellId);
                SendPacketToClient(learnedInstead);
                return;
            }

            spells.SpellID.Add(spellId);
            spells.Superceded.Add(supercededId);
            SendPacketToClient(spells);
        }

        [PacketHandler(Opcode.SMSG_LEARNED_SPELL)]
        void HandleLearnedSpell(WorldPacket packet)
        {
            LearnedSpells spells = new LearnedSpells();
            uint spellId = packet.ReadUInt32();
            spells.Spells.Add(spellId);
            SendPacketToClient(spells);
        }

        [PacketHandler(Opcode.SMSG_SEND_UNLEARN_SPELLS)]
        void HandleSendUnlearnSpells(WorldPacket packet)
        {
            SendUnlearnSpells spells = new SendUnlearnSpells();
            uint spellCount = packet.ReadUInt32();
            for (uint i = 0; i < spellCount; i++)
            {
                uint spellId = packet.ReadUInt32();
                spells.Spells.Add(spellId);
            }
            SendPacketToClient(spells);
        }

        [PacketHandler(Opcode.SMSG_UNLEARNED_SPELLS)]
        void HandleUnlearnedSpells(WorldPacket packet)
        {
            UnlearnedSpells spells = new UnlearnedSpells();
            uint spellId;
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_1_0_9767))
                spellId = packet.ReadUInt32();
            else
                spellId = packet.ReadUInt16();
            spells.Spells.Add(spellId);
            SendPacketToClient(spells);
        }

        [PacketHandler(Opcode.SMSG_CAST_FAILED)]
        void HandleCastFailed(WorldPacket packet)
        {
            // Artificial lag is needed for spell packets,
            // or spells will bug out and glow if spammed.
            if (Settings.ClientSpellDelay > 0)
                Thread.Sleep(Settings.ClientSpellDelay);

            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
                packet.ReadUInt8(); // cast count

            uint spellId = packet.ReadUInt32();
            if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180))
            {
                var status = packet.ReadUInt8();
                if (status != 2)
                    return;
            }

            uint reason = packet.ReadUInt8();
            if (LegacyVersion.InVersion(ClientVersionBuild.V2_0_1_6180, ClientVersionBuild.V3_0_2_9056))
                packet.ReadUInt8(); // cast count
            int arg1 = 0;
            int arg2 = 0;
            if (packet.CanRead())
                arg1 = packet.ReadInt32();
            if (packet.CanRead())
                arg2 = packet.ReadInt32();

            ClientCastRequest failedSpecial;
            ClientCastRequest failedNormal;
            lock (GetSession().GameState.SpellCastLock)
            {
                failedSpecial = GetSession().GameState.CurrentClientSpecialCast;
                failedNormal = GetSession().GameState.CurrentClientNormalCast;
            }

            if (failedSpecial != null && failedSpecial.SpellId == spellId)
            {
                CastFailed failed = new();
                failed.SpellID = failedSpecial.SpellId;
                failed.SpellXSpellVisualID = failedSpecial.SpellXSpellVisualId;
                failed.Reason = LegacyVersion.ConvertSpellCastResult(reason);
                failed.CastID = failedSpecial.ServerGUID;
                failed.FailedArg1 = arg1;
                failed.FailedArg2 = arg2;
                SendPacketToClient(failed);
                lock (GetSession().GameState.SpellCastLock)
                {
                    if (ReferenceEquals(GetSession().GameState.CurrentClientSpecialCast, failedSpecial))
                        GetSession().GameState.CurrentClientSpecialCast = null;
                }
            }
            else if (failedNormal != null && failedNormal.SpellId == spellId)
            {
                if (!failedNormal.HasStarted)
                {
                    SpellPrepare prepare2 = new SpellPrepare();
                    prepare2.ClientCastID = failedNormal.ClientGUID;
                    prepare2.ServerCastID = failedNormal.ServerGUID;
                    SendPacketToClient(prepare2);
                }

                CastFailed failed = new();
                failed.SpellID = failedNormal.SpellId;
                failed.SpellXSpellVisualID = failedNormal.SpellXSpellVisualId;
                failed.Reason = LegacyVersion.ConvertSpellCastResult(reason);
                failed.CastID = failedNormal.ServerGUID;
                failed.FailedArg1 = arg1;
                failed.FailedArg2 = arg2;
                SendPacketToClient(failed);

                List<ClientCastRequest> toFail;
                WorldPacket pendingSpecialPacket = null;
                lock (GetSession().GameState.SpellCastLock)
                {
                    if (ReferenceEquals(GetSession().GameState.CurrentClientNormalCast, failedNormal))
                        GetSession().GameState.CurrentClientNormalCast = null;
                    toFail = GetSession().GameState.PendingClientCasts.ToList();
                    GetSession().GameState.PendingClientCasts.Clear();
                    if (GetSession().GameState.PendingSpecialCast != null)
                    {
                        // The cast that was blocking an auto-repeat (wand/shot) failed or was
                        // interrupted, so the cast slot is free - fire the deferred shot now
                        // rather than leaving it stuck pending forever.
                        pendingSpecialPacket = GetSession().GameState.PendingSpecialCast.PendingLegacyPacket;
                        GetSession().GameState.PendingSpecialCast = null;
                    }
                }
                foreach (var pending in toFail)
                    GetSession().InstanceSocket.SendCastRequestFailed(pending, false);
                if (pendingSpecialPacket != null)
                    SendPacketToServer(pendingSpecialPacket);
            }
        }

        [PacketHandler(Opcode.SMSG_PET_CAST_FAILED, ClientVersionBuild.Zero, ClientVersionBuild.V2_0_1_6180)]
        void HandlePetCastFailed(WorldPacket packet)
        {
            // Artificial lag is needed for spell packets,
            // or spells will bug out and glow if spammed.
            if (Settings.ClientSpellDelay > 0)
                Thread.Sleep(Settings.ClientSpellDelay);

            uint spellId = packet.ReadUInt32();
            var status = packet.ReadUInt8();
            if (status != 2)
                return;

            if (GetSession().GameState.CurrentClientPetCast == null ||
                GetSession().GameState.CurrentClientPetCast.SpellId != spellId)
                return;

            if (!GetSession().GameState.CurrentClientPetCast.HasStarted)
            {
                SpellPrepare prepare2 = new SpellPrepare();
                prepare2.ClientCastID = GetSession().GameState.CurrentClientPetCast.ClientGUID;
                prepare2.ServerCastID = GetSession().GameState.CurrentClientPetCast.ServerGUID;
                SendPacketToClient(prepare2);
            }

            PetCastFailed spell = new PetCastFailed();
            spell.SpellID = spellId;
            uint reason = packet.ReadUInt8();
            spell.Reason = LegacyVersion.ConvertSpellCastResult(reason);
            spell.CastID = GetSession().GameState.CurrentClientPetCast.ServerGUID;
            SendPacketToClient(spell);

            List<ClientCastRequest> toFailPet1;
            lock (GetSession().GameState.SpellCastLock)
            {
                toFailPet1 = GetSession().GameState.PendingClientPetCasts.ToList();
                GetSession().GameState.PendingClientPetCasts.Clear();
            }
            foreach (var pending in toFailPet1)
                GetSession().InstanceSocket.SendCastRequestFailed(pending, true);
        }

        [PacketHandler(Opcode.SMSG_PET_CAST_FAILED, ClientVersionBuild.V2_0_1_6180)]
        void HandlePetCastFailedTBC(WorldPacket packet)
        {
            // Artificial lag is needed for spell packets,
            // or spells will bug out and glow if spammed.
            if (Settings.ClientSpellDelay > 0)
                Thread.Sleep(Settings.ClientSpellDelay);

            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
                packet.ReadUInt8(); // cast count

            uint spellId = packet.ReadUInt32();

            if (GetSession().GameState.CurrentClientPetCast == null ||
                GetSession().GameState.CurrentClientPetCast.SpellId != spellId)
                return;

            if (!GetSession().GameState.CurrentClientPetCast.HasStarted)
            {
                SpellPrepare prepare2 = new SpellPrepare();
                prepare2.ClientCastID = GetSession().GameState.CurrentClientPetCast.ClientGUID;
                prepare2.ServerCastID = GetSession().GameState.CurrentClientPetCast.ServerGUID;
                SendPacketToClient(prepare2);
            }

            PetCastFailed failed = new PetCastFailed();
            failed.SpellID = spellId;
            uint reason = packet.ReadUInt8();
            failed.Reason = LegacyVersion.ConvertSpellCastResult(reason);
            failed.CastID = GetSession().GameState.CurrentClientPetCast.ServerGUID;

            if (packet.CanRead())
                failed.FailedArg1 = packet.ReadInt32();
            if (packet.CanRead())
                failed.FailedArg2 = packet.ReadInt32();

            SendPacketToClient(failed);

            List<ClientCastRequest> toFailPet2;
            lock (GetSession().GameState.SpellCastLock)
            {
                toFailPet2 = GetSession().GameState.PendingClientPetCasts.ToList();
                GetSession().GameState.PendingClientPetCasts.Clear();
            }
            foreach (var pending in toFailPet2)
                GetSession().InstanceSocket.SendCastRequestFailed(pending, true);
        }

        [PacketHandler(Opcode.SMSG_SPELL_FAILED_OTHER)]
        void HandleSpellFailedOther(WorldPacket packet)
        {
            WowGuid128 casterUnit;
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
                casterUnit = packet.ReadPackedGuid().To128(GetSession().GameState);
            else
                casterUnit = packet.ReadGuid().To128(GetSession().GameState);

            if (casterUnit == GetSession().GameState.CurrentPlayerGuid)
            {
                // Artificial lag is needed for spell packets,
                // or spells will bug out and glow if spammed.
                if (Settings.ClientSpellDelay > 0)
                    Thread.Sleep(Settings.ClientSpellDelay);
            }

            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
                packet.ReadUInt8(); // Cast Count

            uint spellId = packet.ReadUInt32();
            byte reason = 61;
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
                reason = (byte)LegacyVersion.ConvertSpellCastResult(packet.ReadUInt8());

            WowGuid128 castId;
            uint spellVisual;
            List<ClientCastRequest> toFail = null;
            List<ClientCastRequest> toFailPet = null;
            WorldPacket pendingSpecialPacket = null;
            lock (GetSession().GameState.SpellCastLock)
            {
                var normalCast = GetSession().GameState.CurrentClientNormalCast;
                var petCast = GetSession().GameState.CurrentClientPetCast;
                if (GetSession().GameState.CurrentPlayerGuid == casterUnit &&
                    normalCast != null && normalCast.SpellId == spellId)
                {
                    castId = normalCast.ServerGUID;
                    spellVisual = normalCast.SpellXSpellVisualId;
                    GetSession().GameState.CurrentClientNormalCast = null;
                    toFail = GetSession().GameState.PendingClientCasts.ToList();
                    GetSession().GameState.PendingClientCasts.Clear();
                    if (GetSession().GameState.PendingSpecialCast != null)
                    {
                        // The cast that was blocking an auto-repeat (wand/shot) was interrupted, so
                        // the cast slot is free - fire the deferred shot now rather than leaving it
                        // stuck pending forever (which would also block all future auto-repeat casts).
                        pendingSpecialPacket = GetSession().GameState.PendingSpecialCast.PendingLegacyPacket;
                        GetSession().GameState.PendingSpecialCast = null;
                    }
                }
                else if (GetSession().GameState.CurrentPetGuid == casterUnit &&
                         petCast != null && petCast.SpellId == spellId)
                {
                    castId = petCast.ServerGUID;
                    spellVisual = petCast.SpellXSpellVisualId;
                    GetSession().GameState.CurrentClientPetCast = null;
                    toFailPet = GetSession().GameState.PendingClientPetCasts.ToList();
                    GetSession().GameState.PendingClientPetCasts.Clear();
                }
                else
                {
                    castId = WowGuid128.Create(HighGuidType703.Cast, SpellCastSource.Normal, (uint)GetSession().GameState.CurrentMapId, spellId, spellId + casterUnit.GetCounter());
                    spellVisual = GameData.GetSpellVisual(spellId);
                }
            }
            if (toFail != null)
                foreach (var pending in toFail)
                    GetSession().InstanceSocket.SendCastRequestFailed(pending, false);
            if (toFailPet != null)
                foreach (var pending in toFailPet)
                    GetSession().InstanceSocket.SendCastRequestFailed(pending, true);
            if (pendingSpecialPacket != null)
                SendPacketToServer(pendingSpecialPacket);

            SpellFailure spell = new SpellFailure();
            spell.CasterUnit = casterUnit;
            spell.CastID = castId;
            spell.SpellID = spellId;
            spell.SpellXSpellVisualID = spellVisual;
            spell.Reason = reason;
            SendPacketToClient(spell);

            SpellFailedOther spell2 = new SpellFailedOther();
            spell2.CasterUnit = casterUnit;
            spell2.CastID = castId;
            spell2.SpellID = spellId;
            spell2.SpellXSpellVisualID = spellVisual;
            spell2.Reason = reason;
            SendPacketToClient(spell2);

            // For an interrupted mob cast (kick/counterspell/Earth Shock), vanilla only sends
            // SMSG_SPELL_FAILED_OTHER, which on its own does not dismiss the modern nameplate
            // cast bar nor fire COMBAT_LOG_EVENT SPELL_INTERRUPT (so interrupt announces and
            // Plater's "Interrupted" never trigger). Synthesize the modern interrupt log here.
            // reason 61 = Interrupted (vanilla SPELL_FAILED_OTHER is only sent on interrupt, so
            // the default reason is 61). Credit the actual interrupter recorded from the interrupt
            // spell's SMSG_SPELL_GO on this victim; fall back to the local player if none is known.
            if (reason == 61 &&
                casterUnit != GetSession().GameState.CurrentPlayerGuid &&
                casterUnit != GetSession().GameState.CurrentPetGuid)
            {
                WowGuid128 interrupter = GetSession().GameState.CurrentPlayerGuid;
                if (GetSession().GameState.RecentInterrupts.TryGetValue(casterUnit, out var record))
                {
                    GetSession().GameState.RecentInterrupts.Remove(casterUnit);
                    // Guard against a stale record from an earlier, unrelated kick on the same unit.
                    if (Environment.TickCount - record.Tick <= 3000 && !record.Interrupter.IsEmpty())
                        interrupter = record.Interrupter;
                }

                SpellInterruptLog interruptLog = new SpellInterruptLog();
                interruptLog.Caster = interrupter;
                interruptLog.Victim = casterUnit;
                interruptLog.InterruptedSpellID = (int)spellId;
                interruptLog.BackfireSpellID = (int)spellId;
                SendPacketToClient(interruptLog);
            }

            // Fast-path retract: when the server broadcasts a failure for an
            // auto-repeat spell from a remote unit, we know the volley ended, so
            // skip the timer and retract the bow immediately.
            if (casterUnit != GetSession().GameState.CurrentPlayerGuid &&
                GameData.AutoRepeatSpells.Contains(spellId))
            {
                RetractOtherAutoShotNow(casterUnit);
            }
        }

        [PacketHandler(Opcode.SMSG_SPELL_START)]
        void HandleSpellStart(WorldPacket packet)
        {
            if (GetSession().GameState.CurrentMapId == null)
                return;

            SpellStart spell = new SpellStart();
            spell.Cast = HandleSpellStartOrGo(packet, false);

            // Hovering casters (Sapphiron air phase): the modern client's cast animation overrides
            // the hover idle and grounds the model; strip the visual, the castbar is unaffected
            if (GetSession().GameState.HoveringUnits.Contains(spell.Cast.CasterUnit))
                spell.Cast.SpellXSpellVisualID = 0;

            byte failPending = 0;
            ClientCastRequest startedNormal;
            ClientCastRequest startedPet;
            lock (GetSession().GameState.SpellCastLock)
            {
                startedNormal = GetSession().GameState.CurrentClientNormalCast;
                startedPet = GetSession().GameState.CurrentClientPetCast;
            }
            if (GetSession().GameState.CurrentPlayerGuid == spell.Cast.CasterUnit &&
                startedNormal != null &&
                startedNormal.SpellId == spell.Cast.SpellID)
            {
                spell.Cast.CastID = startedNormal.ServerGUID;
                spell.Cast.SpellXSpellVisualID = startedNormal.SpellXSpellVisualId;
                startedNormal.HasStarted = true;
                startedNormal.SpellStartTimestamp = Environment.TickCount;
                startedNormal.CastDuration = (uint)spell.Cast.CastTime;

                SpellPrepare prepare = new();
                prepare.ClientCastID = startedNormal.ClientGUID;
                prepare.ServerCastID = spell.Cast.CastID;
                SendPacketToClient(prepare);
                failPending = 1;
            }
            else if (GetSession().GameState.CurrentPetGuid == spell.Cast.CasterUnit &&
                     startedPet != null &&
                     startedPet.SpellId == spell.Cast.SpellID)
            {
                spell.Cast.CastID = startedPet.ServerGUID;
                spell.Cast.SpellXSpellVisualID = startedPet.SpellXSpellVisualId;
                startedPet.HasStarted = true;

                SpellPrepare prepare = new();
                prepare.ClientCastID = startedPet.ClientGUID;
                prepare.ServerCastID = spell.Cast.CastID;
                SendPacketToClient(prepare);
                failPending = 2;
            }

            if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180))
            {
                // We need spell id for SMSG_SPELL_DISPELL_LOG since its not sent by server
                if (GameData.DispellSpells.Contains((uint)spell.Cast.SpellID))
                    GetSession().GameState.LastDispellSpellId = (uint)spell.Cast.SpellID;
            }

            SendPacketToClient(spell);

            if (failPending == 1)
            {
                List<ClientCastRequest> toFail;
                lock (GetSession().GameState.SpellCastLock)
                {
                    toFail = GetSession().GameState.PendingClientCasts.ToList();
                    GetSession().GameState.PendingClientCasts.Clear();
                }
                foreach (var pending in toFail)
                    GetSession().InstanceSocket.SendCastRequestFailed(pending, false);
            }
            else if (failPending == 2)
            {
                List<ClientCastRequest> toFail;
                lock (GetSession().GameState.SpellCastLock)
                {
                    toFail = GetSession().GameState.PendingClientPetCasts.ToList();
                    GetSession().GameState.PendingClientPetCasts.Clear();
                }
                foreach (var pending in toFail)
                    GetSession().InstanceSocket.SendCastRequestFailed(pending, true);
            }
        }

        [PacketHandler(Opcode.SMSG_SPELL_GO)]
        void HandleSpellGo(WorldPacket packet)
        {
            if (GetSession().GameState.CurrentMapId == null)
                return;

            SpellGo spell = new SpellGo();
            spell.Cast = HandleSpellStartOrGo(packet, true);

            // Record the caster of an interrupt spell (Kick/Counterspell/Earth Shock/...) against each
            // unit it lands on. Vanilla's later SMSG_SPELL_FAILED_OTHER (interrupted) does not name the
            // interrupter, so we read this back to credit the real kicker in the synthesized interrupt log.
            if (GameData.InterruptSpells.Contains((uint)spell.Cast.SpellID))
            {
                int now = Environment.TickCount;
                foreach (var hit in spell.Cast.HitTargets)
                    GetSession().GameState.RecentInterrupts[hit] = (spell.Cast.CasterUnit, now);
                if (!spell.Cast.Target.Unit.IsEmpty())
                    GetSession().GameState.RecentInterrupts[spell.Cast.Target.Unit] = (spell.Cast.CasterUnit, now);
            }

            // (hovering casters keep their GO visuals: the release anim does not ground the
            // model — observed on Sapphiron — and stripping it would lose missile visuals)
            WorldPacket pendingGoPacket = null;
            WorldPacket pendingSpecialPacket = null;
            lock (GetSession().GameState.SpellCastLock)
            {
                if (GetSession().GameState.CurrentPlayerGuid == spell.Cast.CasterUnit &&
                    GetSession().GameState.CurrentClientNormalCast != null &&
                    GetSession().GameState.CurrentClientNormalCast.SpellId == spell.Cast.SpellID)
                {
                    spell.Cast.CastID = GetSession().GameState.CurrentClientNormalCast.ServerGUID;
                    spell.Cast.SpellXSpellVisualID = GetSession().GameState.CurrentClientNormalCast.SpellXSpellVisualId;
                    GetSession().GameState.CurrentClientNormalCast = null;

                    if (GetSession().GameState.PendingClientCasts.Count > 0)
                    {
                        var queued = GetSession().GameState.PendingClientCasts[0];
                        GetSession().GameState.PendingClientCasts.RemoveAt(0);
                        GetSession().GameState.CurrentClientNormalCast = queued;
                        pendingGoPacket = queued.PendingLegacyPacket;
                    }
                    else if (GetSession().GameState.PendingSpecialCast != null)
                    {
                        // The cast that was blocking an auto-repeat (wand/shot) just finished and
                        // no further normal cast was queued behind it, so fire the deferred shot now.
                        pendingSpecialPacket = GetSession().GameState.PendingSpecialCast.PendingLegacyPacket;
                        GetSession().GameState.PendingSpecialCast = null;
                    }
                }
                else if (GetSession().GameState.CurrentPlayerGuid == spell.Cast.CasterUnit &&
                    GetSession().GameState.CurrentClientSpecialCast != null &&
                    GetSession().GameState.CurrentClientSpecialCast.SpellId == spell.Cast.SpellID)
                {
                    spell.Cast.CastID = GetSession().GameState.CurrentClientSpecialCast.ServerGUID;
                    spell.Cast.SpellXSpellVisualID = GetSession().GameState.CurrentClientSpecialCast.SpellXSpellVisualId;
                    GetSession().GameState.CurrentClientSpecialCast = null;
                }
                else if (GetSession().GameState.CurrentPetGuid == spell.Cast.CasterUnit &&
                         GetSession().GameState.CurrentClientPetCast != null &&
                         GetSession().GameState.CurrentClientPetCast.SpellId == spell.Cast.SpellID)
                {
                    spell.Cast.CastID = GetSession().GameState.CurrentClientPetCast.ServerGUID;
                    spell.Cast.SpellXSpellVisualID = GetSession().GameState.CurrentClientPetCast.SpellXSpellVisualId;
                    GetSession().GameState.CurrentClientPetCast = null;
                }
            }
            if (pendingGoPacket != null)
                SendPacketToServer(pendingGoPacket);
            if (pendingSpecialPacket != null)
                SendPacketToServer(pendingSpecialPacket);
            if (!spell.Cast.CasterUnit.IsEmpty() && GameData.AuraSpells.Contains((uint)spell.Cast.SpellID))
            {
                uint spellId = (uint)spell.Cast.SpellID;
                foreach (WowGuid128 target in spell.Cast.HitTargets)
                {
                    var updateFields = GetSession().GameState.GetCachedObjectFieldsLegacy(target);
                    if (updateFields != null)
                    {
                        int existingSlot = FindAuraSlotBySpellId(target, spellId, updateFields);
                        if (existingSlot >= 0)
                            SendAuraRefreshUpdate(target, spellId, spell.Cast.CasterUnit, (byte)existingSlot, updateFields);
                    }

                    GetSession().GameState.StoreLastAuraCasterOnTarget(target, spellId, spell.Cast.CasterUnit);
                }
            }

            // The 1.14 client keeps the bow/wand aim pose drawn for OTHER observed
            // units until it receives SMSG_CANCEL_AUTO_REPEAT for that unit. The
            // 1.12 server only sends that packet to the caster's own session, so
            // the proxy never sees it for remote hunters. Schedule a synthetic
            // cancel; each new auto-repeat SPELL_GO from the same unit pushes the
            // timer forward, so continuous shooting keeps the bow visible.
            if (!spell.Cast.CasterUnit.IsEmpty() &&
                spell.Cast.CasterUnit != GetSession().GameState.CurrentPlayerGuid &&
                GameData.AutoRepeatSpells.Contains((uint)spell.Cast.SpellID))
            {
                ScheduleOtherAutoShotRetract(spell.Cast.CasterUnit);
            }

            SendPacketToClient(spell);
        }

        // Vanilla bows fire at most every ~3.3s unhasted. 5s leaves margin for one
        // skipped shot (haste-jittered or out-of-range retry) without retracting too
        // early during a continuous volley.
        private const int OtherAutoShotRetractDelayMs = 5000;

        private void ScheduleOtherAutoShotRetract(WowGuid128 casterUnit)
        {
            var gameState = GetSession().GameState;
            var newCts = new CancellationTokenSource();
            lock (gameState.OtherAutoShotTimersLock)
            {
                if (gameState.OtherAutoShotTimers.TryGetValue(casterUnit, out var oldCts))
                {
                    oldCts.Cancel();
                    oldCts.Dispose();
                }
                gameState.OtherAutoShotTimers[casterUnit] = newCts;
            }

            var token = newCts.Token;
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await System.Threading.Tasks.Task.Delay(OtherAutoShotRetractDelayMs, token);
                }
                catch (System.OperationCanceledException)
                {
                    return;
                }

                lock (gameState.OtherAutoShotTimersLock)
                {
                    if (!gameState.OtherAutoShotTimers.TryGetValue(casterUnit, out var current) || current != newCts)
                        return;
                    gameState.OtherAutoShotTimers.Remove(casterUnit);
                }
                newCts.Dispose();

                CancelAutoRepeat cancel = new CancelAutoRepeat();
                cancel.Guid = casterUnit;
                SendPacketToClient(cancel);
            });
        }

        private void RetractOtherAutoShotNow(WowGuid128 casterUnit)
        {
            var gameState = GetSession().GameState;
            lock (gameState.OtherAutoShotTimersLock)
            {
                if (gameState.OtherAutoShotTimers.TryGetValue(casterUnit, out var oldCts))
                {
                    oldCts.Cancel();
                    oldCts.Dispose();
                    gameState.OtherAutoShotTimers.Remove(casterUnit);
                }
            }

            CancelAutoRepeat cancel = new CancelAutoRepeat();
            cancel.Guid = casterUnit;
            SendPacketToClient(cancel);
        }

        // A bolt is dropped from the in-flight list after this long without a damage log, so a shot
        // that produces none (immune target, target despawned, log lost) cannot shift the pairing of
        // every later shot forever. Comfortably above the longest flight time (30 yd at 20 yd/s = 1.5s).
        private const int AutoRepeatShotInFlightTimeoutMs = 4000;

        // Auto-repeat shots overlap near max range: a wand bolt spends 1.5s crossing 30 yd, longer
        // than the wand swing, so the next shot is fired while the previous bolt is still flying.
        // The cast id synthesized in HandleSpellStartOrGo only depends on spell + caster, so both
        // bolts shared one id and the modern client - which tracks in-flight casts by id - held the
        // second one back until the first resolved: the character stayed in the aim pose, the bolt
        // left late, and the damage log (delayed server-side by the flight time) landed before it.
        // Numbering the shots gives every bolt its own id. Called on SMSG_SPELL_START.
        private WowGuid128 OpenAutoRepeatShot(WowGuid128 casterUnit, uint spellId)
        {
            var gameState = GetSession().GameState;

            // The shot that answers the client's own Shoot request must keep the ServerGUID already
            // announced to it in SpellPrepare (HandleSpellGo forces that id on this shot), so restart
            // the numbering at 0 there - sequence 0 reproduces the plain spell + caster id.
            bool startsVolley = false;
            if (casterUnit == gameState.CurrentPlayerGuid)
            {
                lock (gameState.SpellCastLock)
                    startsVolley = gameState.CurrentClientSpecialCast != null &&
                                   gameState.CurrentClientSpecialCast.SpellId == spellId;
            }

            byte sequence;
            lock (gameState.AutoRepeatShotsLock)
            {
                var tracker = GetAutoRepeatShotTracker(gameState, casterUnit, spellId);
                // Restarting the numbering deliberately keeps the in-flight list: a bolt from the
                // previous volley may still be flying and its damage log lands before this volley's.
                tracker.Sequence = startsVolley ? (byte)0 : (byte)(tracker.Sequence + 1);
                sequence = tracker.Sequence;
            }

            return MakeAutoRepeatShotCastId(casterUnit, spellId, sequence);
        }

        // Called on SMSG_SPELL_GO: reuses the id opened by this shot's SMSG_SPELL_START and, when the
        // shot connected, queues it as in flight so the delayed damage log can be matched back to it.
        private WowGuid128 ReleaseAutoRepeatShot(WowGuid128 casterUnit, uint spellId, bool hasHitTargets)
        {
            var gameState = GetSession().GameState;

            byte sequence;
            lock (gameState.AutoRepeatShotsLock)
            {
                var tracker = GetAutoRepeatShotTracker(gameState, casterUnit, spellId);
                sequence = tracker.Sequence;

                int now = Environment.TickCount;
                tracker.InFlight.RemoveAll(shot => now - shot.Tick > AutoRepeatShotInFlightTimeoutMs);
                if (hasHitTargets)
                    tracker.InFlight.Add((sequence, now));
            }

            return MakeAutoRepeatShotCastId(casterUnit, spellId, sequence);
        }

        // Called on the damage log, which the legacy server delays by the bolt's flight time and sends
        // without any cast id: bolts land in the order they were fired, so take the oldest pending one.
        private WowGuid128 TakeAutoRepeatShotCastId(WowGuid128 casterUnit, uint spellId)
        {
            var gameState = GetSession().GameState;

            byte sequence = 0;
            lock (gameState.AutoRepeatShotsLock)
            {
                if (gameState.AutoRepeatShots.TryGetValue(casterUnit, out var tracker) && tracker.SpellId == spellId)
                {
                    int now = Environment.TickCount;
                    tracker.InFlight.RemoveAll(shot => now - shot.Tick > AutoRepeatShotInFlightTimeoutMs);
                    if (tracker.InFlight.Count > 0)
                    {
                        sequence = tracker.InFlight[0].Sequence;
                        tracker.InFlight.RemoveAt(0);
                    }
                    else
                        sequence = tracker.Sequence;
                }
            }

            return MakeAutoRepeatShotCastId(casterUnit, spellId, sequence);
        }

        // Must be called under AutoRepeatShotsLock.
        private static AutoRepeatShotTracker GetAutoRepeatShotTracker(GameSessionData gameState, WowGuid128 casterUnit, uint spellId)
        {
            if (!gameState.AutoRepeatShots.TryGetValue(casterUnit, out var tracker) || tracker.SpellId != spellId)
            {
                tracker = new AutoRepeatShotTracker { SpellId = spellId };
                gameState.AutoRepeatShots[casterUnit] = tracker;
            }
            return tracker;
        }

        // Sequence 0 yields exactly the plain spell + caster id used for every other cast, so the
        // first shot of a volley still matches the SpellPrepare handed to the client. The counter
        // part of a cast guid is 40 bits wide, so the shot number sits above the spell/caster sum.
        private WowGuid128 MakeAutoRepeatShotCastId(WowGuid128 casterUnit, uint spellId, byte sequence)
        {
            return WowGuid128.Create(HighGuidType703.Cast, SpellCastSource.Normal, (uint)GetSession().GameState.CurrentMapId,
                                     spellId, (ulong)spellId + casterUnit.GetCounter() + ((ulong)sequence << 32));
        }

        SpellCastData HandleSpellStartOrGo(WorldPacket packet, bool isSpellGo)
        {
            SpellCastData dbdata = new SpellCastData();

            dbdata.CasterGUID = packet.ReadPackedGuid().To128(GetSession().GameState);
            dbdata.CasterUnit = packet.ReadPackedGuid().To128(GetSession().GameState);

            if (dbdata.CasterUnit == GetSession().GameState.CurrentPlayerGuid)
            {
                // Artificial lag is needed for spell packets,
                // or spells will bug out and glow if spammed.
                if (Settings.ClientSpellDelay > 0)
                    Thread.Sleep(Settings.ClientSpellDelay);
            }

            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
                packet.ReadUInt8(); // cast count

            dbdata.SpellID = packet.ReadInt32();
            dbdata.SpellXSpellVisualID = GameData.GetSpellVisual((uint)dbdata.SpellID);
            dbdata.CastID = WowGuid128.Create(HighGuidType703.Cast, SpellCastSource.Normal, (uint)GetSession().GameState.CurrentMapId, (uint)dbdata.SpellID, (ulong)dbdata.SpellID + dbdata.CasterUnit.GetCounter());
            bool isAutoRepeatShot = GameData.AutoRepeatSpells.Contains((uint)dbdata.SpellID);
            if (isAutoRepeatShot && !isSpellGo)
                dbdata.CastID = OpenAutoRepeatShot(dbdata.CasterUnit, (uint)dbdata.SpellID);

            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180) && LegacyVersion.RemovedInVersion(ClientVersionBuild.V3_0_2_9056) && !isSpellGo)
                packet.ReadUInt8(); // cast count

            uint flags;
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
                flags = packet.ReadUInt32();
            else
                flags = packet.ReadUInt16();
            dbdata.CastFlags = flags;

            if (!isSpellGo || LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
                dbdata.CastTime = packet.ReadUInt32();

            if (isSpellGo)
            {
                var hitCount = packet.ReadUInt8();
                for (var i = 0; i < hitCount; i++)
                {
                    WowGuid128 hitTarget = packet.ReadGuid().To128(GetSession().GameState);
                    dbdata.HitTargets.Add(hitTarget);
                }

                var missCount = packet.ReadUInt8();
                for (var i = 0; i < missCount; i++)
                {
                    WowGuid128 missTarget = packet.ReadGuid().To128(GetSession().GameState);
                    SpellMissInfo missType = (SpellMissInfo)packet.ReadUInt8();
                    SpellMissInfo reflectType = SpellMissInfo.None;
                    if (missType == SpellMissInfo.Reflect)
                        reflectType = (SpellMissInfo)packet.ReadUInt8();

                    dbdata.MissTargets.Add(missTarget);
                    dbdata.MissStatus.Add(new SpellMissStatus(missType, reflectType));
                }

                // Same shot as the SMSG_SPELL_START above (cmangos sends one start per auto-repeat
                // tick); a shot that connects also gets its damage log queued against this id.
                if (isAutoRepeatShot)
                    dbdata.CastID = ReleaseAutoRepeatShot(dbdata.CasterUnit, (uint)dbdata.SpellID, dbdata.HitTargets.Count > 0);
            }

            var targetFlags = LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180) ?
                (SpellCastTargetFlags)packet.ReadUInt32() : (SpellCastTargetFlags)packet.ReadUInt16();
            dbdata.Target.Flags = targetFlags;

            WowGuid128 unitTarget = WowGuid128.Empty;
            if (targetFlags.HasAnyFlag(SpellCastTargetFlags.Unit | SpellCastTargetFlags.CorpseEnemy | SpellCastTargetFlags.GameObject |
                SpellCastTargetFlags.CorpseAlly | SpellCastTargetFlags.UnitMinipet))
                unitTarget = packet.ReadPackedGuid().To128(GetSession().GameState);
            dbdata.Target.Unit = unitTarget;

            WowGuid128 itemTarget = WowGuid128.Empty;
            if (targetFlags.HasAnyFlag(SpellCastTargetFlags.Item | SpellCastTargetFlags.TradeItem))
                itemTarget = packet.ReadPackedGuid().To128(GetSession().GameState);
            dbdata.Target.Item = itemTarget;

            if (targetFlags.HasAnyFlag(SpellCastTargetFlags.SourceLocation))
            {
                dbdata.Target.SrcLocation = new TargetLocation();
                dbdata.Target.SrcLocation.Transport = WowGuid128.Empty;
                if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_2_0_10192))
                    dbdata.Target.SrcLocation.Transport = packet.ReadPackedGuid().To128(GetSession().GameState);

                dbdata.Target.SrcLocation.Location = packet.ReadVector3();
            }

            if (targetFlags.HasAnyFlag(SpellCastTargetFlags.DestLocation))
            {
                dbdata.Target.DstLocation = new TargetLocation();
                dbdata.Target.DstLocation.Transport = WowGuid128.Empty;
                if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_8_9464))
                    dbdata.Target.DstLocation.Transport = packet.ReadPackedGuid().To128(GetSession().GameState);

                dbdata.Target.DstLocation.Location = packet.ReadVector3();
            }

            if (targetFlags.HasAnyFlag(SpellCastTargetFlags.String))
                dbdata.Target.Name = packet.ReadCString();

            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
            {
                if (flags.HasAnyFlag(CastFlag.PredictedPower))
                {
                    packet.ReadInt32(); // Rune Cooldown
                }

                if (flags.HasAnyFlag(CastFlag.RuneInfo))
                {
                    var spellRuneState = packet.ReadUInt8();
                    var playerRuneState = packet.ReadUInt8();

                    for (var i = 0; i < 6; i++)
                    {
                        var mask = 1 << i;
                        if ((mask & spellRuneState) == 0)
                            continue;

                        if ((mask & playerRuneState) != 0)
                            continue;

                        packet.ReadUInt8(); // Rune Cooldown Passed
                    }
                }

                if (isSpellGo)
                {
                    if (flags.HasAnyFlag(CastFlag.AdjustMissile))
                    {
                        dbdata.MissileTrajectory.Pitch = packet.ReadFloat(); // Elevation
                        dbdata.MissileTrajectory.TravelTime = packet.ReadUInt32(); // Delay time
                    }
                }
            }

            if (flags.HasAnyFlag(CastFlag.Projectile))
            {
                dbdata.AmmoDisplayId = packet.ReadInt32();
                dbdata.AmmoInventoryType = packet.ReadInt32();
            }

            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
            {
                if (isSpellGo)
                {
                    if (flags.HasAnyFlag(CastFlag.VisualChain))
                    {
                        packet.ReadInt32();
                        packet.ReadInt32();
                    }

                    if (targetFlags.HasAnyFlag(SpellCastTargetFlags.DestLocation))
                        packet.ReadInt8(); // Some count

                    if (targetFlags.HasAnyFlag(SpellCastTargetFlags.ExtraTargets))
                    {
                        var targetCount = packet.ReadInt32();
                        if (targetCount > 0)
                        {
                            TargetLocation location = new();
                            for (var i = 0; i < targetCount; i++)
                            {
                                location.Location = packet.ReadVector3();
                                location.Transport = packet.ReadGuid().To128(GetSession().GameState);
                            }
                            dbdata.TargetPoints.Add(location);
                        }
                    }
                }
                else
                {
                    if (flags.HasAnyFlag(CastFlag.Immunity))
                    {
                        dbdata.Immunities.School = packet.ReadUInt32();
                        dbdata.Immunities.Value = packet.ReadUInt32();
                    }

                    if (flags.HasAnyFlag(CastFlag.HealPrediction))
                    {
                        packet.ReadInt32(); // Predicted Spell ID

                        if (packet.ReadUInt8() == 2)
                            packet.ReadPackedGuid();
                    }
                }
            }

            return dbdata;
        }

        [PacketHandler(Opcode.SMSG_CANCEL_AUTO_REPEAT)]
        void HandleCancelAutoRepeat(WorldPacket packet)
        {
            // Artificial lag is needed for spell packets,
            // or spells will bug out and glow if spammed.
            if (Settings.ClientSpellDelay > 0)
                Thread.Sleep(Settings.ClientSpellDelay);

            bool wasActiveWand = false;
            lock (GetSession().GameState.SpellCastLock)
            {
                // Clear the first-tick translation slot if the wand was cancelled before its first shot.
                if (GetSession().GameState.CurrentClientSpecialCast != null &&
                    GameData.AutoRepeatSpells.Contains(GetSession().GameState.CurrentClientSpecialCast.SpellId))
                {
                    GetSession().GameState.CurrentClientSpecialCast = null;
                }

                // Remember the toggled-on wand (ActiveAutoRepeatCast survives continuous firing, so this
                // fires even when the stun lands mid-wanding). cmangos sends SMSG_CANCEL_AUTO_REPEAT right
                // before it sets UNIT_FLAG_STUNNED, so if the player's stun flag turns on within a short
                // window we know the wand was stopped by a stun and re-fire it when the stun fades (see
                // UpdateWandStunResume). A non-stun cancel (moving, target change, toggle) is never
                // followed by the stun flag, so this stash simply expires unused.
                var activeWand = GetSession().GameState.ActiveAutoRepeatCast;
                if (activeWand != null)
                {
                    GetSession().GameState.RecentAutoRepeatCancel = activeWand;
                    GetSession().GameState.RecentAutoRepeatCancelTime = Environment.TickCount;
                    GetSession().GameState.ActiveAutoRepeatCast = null;
                    wasActiveWand = true;
                }
            }

            CancelAutoRepeat cancel = new CancelAutoRepeat();
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
                cancel.Guid = packet.ReadPackedGuid().To128(GetSession().GameState);
            else
                cancel.Guid = GetSession().GameState.CurrentPlayerGuid;

            // If this cancel stopped the player's own wand, don't tell the client yet: the very next
            // packet may carry the stun flag that caused it, in which case we suppress the cancel so the
            // client stays in auto-repeat mode and the Shoot button stays lit through stun + resume. If
            // no stun follows within the hold window, the cancel is forwarded normally (button un-lights).
            if (wasActiveWand)
                ScheduleWandCancelToClient(cancel);
            else
                SendPacketToClient(cancel);
        }

        // Hold window for a wand cancel before it is forwarded to the client. cmangos emits the cancel
        // and the UNIT_FLAG_STUNNED update in the same end-of-tick flush, so the stun flag is only a few
        // ms behind; 150ms covers jitter while staying imperceptible on an ordinary move/target cancel.
        private const int WandCancelHoldMs = 150;

        private void ScheduleWandCancelToClient(CancelAutoRepeat cancel)
        {
            var gameState = GetSession().GameState;
            var newCts = new System.Threading.CancellationTokenSource();
            lock (gameState.SpellCastLock)
            {
                gameState.PendingWandCancelCts?.Cancel();
                gameState.PendingWandCancelCts?.Dispose();
                gameState.PendingWandCancelCts = newCts;
            }

            var token = newCts.Token;
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await System.Threading.Tasks.Task.Delay(WandCancelHoldMs, token);
                }
                catch (System.OperationCanceledException)
                {
                    return; // suppressed (stun) or superseded
                }

                lock (gameState.SpellCastLock)
                {
                    if (gameState.PendingWandCancelCts != newCts)
                        return; // superseded
                    gameState.PendingWandCancelCts = null;
                }
                newCts.Dispose();

                // No stun arrived: this was an ordinary cancel, so let the client stop the wand.
                SendPacketToClient(cancel);
            });
        }

        // How long after a wand cancel the stun flag may arrive and still count as the same event.
        // cmangos queues the cancel and the UNIT_FLAG_STUNNED update into the same end-of-tick flush
        // (cancel first), so the client reads them back-to-back and the real gap is sub-millisecond.
        // 1s comfortably covers tick/network jitter while keeping the false-positive window (a move-off
        // cancel followed by an unrelated stun) small.
        private const int WandStunResumeWindowMs = 1000;

        // Driven from the player's UNIT_FIELD_FLAGS updates (see UpdateHandler.AfterStoreObjectUpdateHook).
        // A stun interrupts an active wand on the legacy server without ever resuming it; this restores
        // the pre-stun behavior by re-firing the wand once the stun fades.
        void UpdateWandStunResume(WowGuid128 playerGuid)
        {
            uint flags = GetSession().GameState.GetLegacyFieldValueUInt32(playerGuid, UnitField.UNIT_FIELD_FLAGS);
            bool nowStunned = (flags & (uint)UnitFlagsVanilla.Stunned) != 0;
            if (nowStunned == GetSession().GameState.PlayerStunnedForWand)
                return;
            GetSession().GameState.PlayerStunnedForWand = nowStunned;

            if (nowStunned)
            {
                // Stun just landed: if a wand was cancelled moments ago, it was this stun that stopped it.
                lock (GetSession().GameState.SpellCastLock)
                {
                    var cancelledWand = GetSession().GameState.RecentAutoRepeatCancel;
                    if (cancelledWand != null &&
                        Environment.TickCount - GetSession().GameState.RecentAutoRepeatCancelTime <= WandStunResumeWindowMs)
                    {
                        GetSession().GameState.WandToResumeAfterStun = cancelledWand;
                        // Suppress the held cancel so the client never leaves auto-repeat mode: the Shoot
                        // button stays lit through the stun and the resumed volley.
                        GetSession().GameState.PendingWandCancelCts?.Cancel();
                        GetSession().GameState.PendingWandCancelCts?.Dispose();
                        GetSession().GameState.PendingWandCancelCts = null;
                    }
                    GetSession().GameState.RecentAutoRepeatCancel = null;
                }
                return;
            }

            // Stun faded: re-fire the wand it interrupted.
            ClientCastRequest resume;
            lock (GetSession().GameState.SpellCastLock)
            {
                resume = GetSession().GameState.WandToResumeAfterStun;
                GetSession().GameState.WandToResumeAfterStun = null;
                if (resume != null)
                    GetSession().GameState.ActiveAutoRepeatCast = resume; // so a further stun during the resumed firing re-suspends it
            }
            if (resume == null)
                return;

            // The client stayed in auto-repeat mode (the stun cancel was suppressed), so from its view the
            // wand never stopped. Just restart the server-side loop; the resumed SMSG_SPELL_GO ticks flow
            // through as ordinary continuation shots (raw cast id, no SpellPrepare) - exactly like ticks
            // during uninterrupted wanding. Sending a SpellPrepare / translating the first tick here would
            // read as a discrete one-shot cast and drop the button glow.
            if (resume.PendingLegacyPacket != null)
                SendPacketToServer(resume.PendingLegacyPacket);
        }

        [PacketHandler(Opcode.SMSG_SPELL_COOLDOWN)]
        void HandleSpellCooldown(WorldPacket packet)
        {
            SpellCooldownPkt cooldown = new();
            try
            {
                cooldown.Caster = packet.ReadGuid().To128(GetSession().GameState);
                if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
                    cooldown.Flags = packet.ReadUInt8();
                while (packet.CanRead())
                {
                    SpellCooldownStruct cd = new();
                    cd.SpellID = packet.ReadUInt32();
                    cd.ForcedCooldown = packet.ReadUInt32();
                    cooldown.SpellCooldowns.Add(cd);
                }
            }
            catch (ArgumentOutOfRangeException ex)
            {
                // wrong structure from arcemu
                // https://github.com/arcemu/arcemu/blob/2_4_3/src/arcemu-world/Spell.cpp#L1554
                packet.ResetReadPos();
                SpellCooldownStruct cd = new();
                cd.SpellID = packet.ReadUInt32();
                cooldown.Caster = packet.ReadPackedGuid().To128(GetSession().GameState);
                cd.ForcedCooldown = packet.ReadUInt32();
                cooldown.SpellCooldowns.Add(cd);
            }
            SendPacketToClient(cooldown);
        }

        [PacketHandler(Opcode.SMSG_COOLDOWN_EVENT)]
        void HandleCooldownEvent(WorldPacket packet)
        {
            CooldownEvent cooldown = new();
            cooldown.SpellID = packet.ReadUInt32();
            WowGuid guid = packet.ReadGuid();
            cooldown.IsPet = guid.GetHighType() == HighGuidType.Pet;
            SendPacketToClient(cooldown);
        }

        [PacketHandler(Opcode.SMSG_CLEAR_COOLDOWN)]
        void HandleClearCooldown(WorldPacket packet)
        {
            ClearCooldown cooldown = new();
            cooldown.SpellID = packet.ReadUInt32();
            WowGuid guid = packet.ReadGuid();
            cooldown.IsPet = guid.GetHighType() == HighGuidType.Pet;
            SendPacketToClient(cooldown);
        }

        [PacketHandler(Opcode.SMSG_COOLDOWN_CHEAT)]
        void HandleCooldownCheat(WorldPacket packet)
        {
            CooldownCheat cooldown = new();
            cooldown.Guid = packet.ReadGuid().To128(GetSession().GameState);
            SendPacketToClient(cooldown);
        }

        [PacketHandler(Opcode.SMSG_SPELL_NON_MELEE_DAMAGE_LOG)]
        void HandleSpellNonMeleeDamageLog(WorldPacket packet)
        {
            SpellNonMeleeDamageLog spell = new();
            spell.TargetGUID = packet.ReadPackedGuid().To128(GetSession().GameState);
            spell.CasterGUID = packet.ReadPackedGuid().To128(GetSession().GameState);
            spell.SpellID = packet.ReadUInt32();
            spell.SpellXSpellVisualID = GameData.GetSpellVisual(spell.SpellID);
            spell.CastID = GameData.AutoRepeatSpells.Contains(spell.SpellID)
                ? TakeAutoRepeatShotCastId(spell.CasterGUID, spell.SpellID)
                : WowGuid128.Create(HighGuidType703.Cast, SpellCastSource.Normal, (uint)GetSession().GameState.CurrentMapId, spell.SpellID, spell.SpellID + spell.CasterGUID.GetCounter());
            spell.Damage = packet.ReadInt32();
            spell.OriginalDamage = spell.Damage;

            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_3_9183))
                spell.Overkill = packet.ReadInt32();
            else
                spell.Overkill = -1;

            byte school = packet.ReadUInt8();
            if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180))
                school = (byte)(1u << school);

            spell.SchoolMask = school;
            spell.Absorbed = packet.ReadInt32();
            spell.Resisted = packet.ReadInt32();
            spell.Periodic = packet.ReadBool();
            packet.ReadUInt8(); // unused
            spell.ShieldBlock = packet.ReadInt32();
            spell.Flags = (SpellHitType)packet.ReadUInt32();

            bool debugOutput = packet.ReadBool();
            if (debugOutput)
            {
                if (!spell.Flags.HasAnyFlag(SpellHitType.Split))
                {
                    if (spell.Flags.HasAnyFlag(SpellHitType.CritDebug))
                    {
                        packet.ReadFloat(); // roll
                        packet.ReadFloat(); // needed
                    }

                    if (spell.Flags.HasAnyFlag(SpellHitType.HitDebug))
                    {
                        packet.ReadFloat(); // roll
                        packet.ReadFloat(); // needed
                    }

                    if (spell.Flags.HasAnyFlag(SpellHitType.AttackTableDebug))
                    {
                        packet.ReadFloat(); // miss chance
                        packet.ReadFloat(); // dodge chance
                        packet.ReadFloat(); // parry chance
                        packet.ReadFloat(); // block chance
                        packet.ReadFloat(); // glance chance
                        packet.ReadFloat(); // crush chance
                    }
                }
            }

            SendPacketToClient(spell);
        }

        [PacketHandler(Opcode.SMSG_SPELL_HEAL_LOG)]
        void HandleSpellHealLog(WorldPacket packet)
        {
            SpellHealLog spell = new();
            spell.TargetGUID = packet.ReadPackedGuid().To128(GetSession().GameState);
            spell.CasterGUID = packet.ReadPackedGuid().To128(GetSession().GameState);
            spell.SpellID = packet.ReadUInt32();
            spell.HealAmount = packet.ReadInt32();
            spell.OriginalHealAmount = spell.HealAmount;

            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_3_9183))
                spell.OverHeal = packet.ReadUInt32();

            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_2_0_10192))
                spell.Absorbed = packet.ReadUInt32();

            spell.Crit = packet.ReadBool();

            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
            {
                bool debugOutput = packet.ReadBool();
                if (debugOutput)
                {
                    spell.CritRollMade = packet.ReadFloat();
                    spell.CritRollNeeded = packet.ReadFloat();
                }
            }

            SendPacketToClient(spell);
        }

        [PacketHandler(Opcode.SMSG_SPELL_PERIODIC_AURA_LOG)]
        void HandleSpellPeriodicAuraLog(WorldPacket packet)
        {
            SpellPeriodicAuraLog spell = new();
            spell.TargetGUID = packet.ReadPackedGuid().To128(GetSession().GameState);
            spell.CasterGUID = packet.ReadPackedGuid().To128(GetSession().GameState);
            spell.SpellID = packet.ReadUInt32();

            var count = packet.ReadInt32();
            for (var i = 0; i < count; i++)
            {
                var aura = (AuraType)packet.ReadUInt32();
                switch (aura)
                {
                    case AuraType.PeriodicDamage:
                    case AuraType.PeriodicDamagePercent:
                    {
                        SpellPeriodicAuraLog.SpellLogEffect effect = new();
                        effect.Effect = (uint)aura;
                        effect.Amount = packet.ReadInt32();
                        effect.OriginalDamage = effect.Amount;

                        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
                            effect.OverHealOrKill = packet.ReadUInt32();

                        uint school = packet.ReadUInt32();
                        if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180))
                            school = (1u << (byte)school);

                        effect.SchoolMaskOrPower = school;
                        effect.AbsorbedOrAmplitude = packet.ReadUInt32();
                        effect.Resisted = packet.ReadUInt32();

                        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_1_2_9901))
                            effect.Crit = packet.ReadBool();

                        spell.Effects.Add(effect);
                        break;
                    }
                    case AuraType.PeriodicHeal:
                    case AuraType.ObsModHealth:
                    {
                        SpellPeriodicAuraLog.SpellLogEffect effect = new();
                        effect.Effect = (uint)aura;
                        effect.Amount = packet.ReadInt32();
                        effect.OriginalDamage = effect.Amount;

                        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
                            effect.OverHealOrKill = packet.ReadUInt32();

                        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_3_0_10958))
                            // no idea when this was added exactly
                            effect.AbsorbedOrAmplitude = packet.ReadUInt32();

                        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_1_2_9901))
                            effect.Crit = packet.ReadBool();

                        spell.Effects.Add(effect);
                        break;
                    }
                    case AuraType.ObsModPower:
                    case AuraType.PeriodicEnergize:
                    {
                        SpellPeriodicAuraLog.SpellLogEffect effect = new();
                        effect.Effect = (uint)aura;
                        effect.SchoolMaskOrPower = packet.ReadUInt32();
                        effect.Amount = packet.ReadInt32();
                        spell.Effects.Add(effect);
                        break;
                    }
                    case AuraType.PeriodicManaLeech:
                    {
                        SpellPeriodicAuraLog.SpellLogEffect effect = new();
                        effect.Effect = (uint)aura;
                        effect.SchoolMaskOrPower = packet.ReadUInt32();
                        effect.Amount = packet.ReadInt32();
                        packet.ReadFloat(); // Gain multiplier
                        spell.Effects.Add(effect);
                        break;
                    }
                }
            }
            SendPacketToClient(spell);
        }

        [PacketHandler(Opcode.SMSG_SPELL_ENERGIZE_LOG)]
        void HandleSpellEnergizeLog(WorldPacket packet)
        {
            SpellEnergizeLog spell = new();
            spell.TargetGUID = packet.ReadPackedGuid().To128(GetSession().GameState);
            spell.CasterGUID = packet.ReadPackedGuid().To128(GetSession().GameState);
            spell.SpellID = packet.ReadUInt32();
            spell.Type = (PowerType)packet.ReadUInt32();
            spell.Amount = packet.ReadInt32();
            SendPacketToClient(spell);
        }

        [PacketHandler(Opcode.SMSG_SPELL_DELAYED)]
        void HandleSpellDelayed(WorldPacket packet)
        {
            SpellDelayed delay = new();
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
                delay.CasterGUID = packet.ReadPackedGuid().To128(GetSession().GameState);
            else
                delay.CasterGUID = packet.ReadGuid().To128(GetSession().GameState);
            delay.Delay = packet.ReadInt32();
            SendPacketToClient(delay);
        }

        [PacketHandler(Opcode.MSG_CHANNEL_START)]
        void HandleSpellChannelStart(WorldPacket packet)
        {
            SpellChannelStart channel = new();
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
                channel.CasterGUID = packet.ReadPackedGuid().To128(GetSession().GameState);
            else
                channel.CasterGUID = GetSession().GameState.CurrentPlayerGuid;
            channel.SpellID = packet.ReadUInt32();
            channel.SpellXSpellVisualID = GameData.GetSpellVisual(channel.SpellID);
            channel.Duration = packet.ReadUInt32();
            SendPacketToClient(channel);
        }

        [PacketHandler(Opcode.MSG_CHANNEL_UPDATE)]
        void HandleSpellChannelUpdate(WorldPacket packet)
        {
            SpellChannelUpdate channel = new();
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
                channel.CasterGUID = packet.ReadPackedGuid().To128(GetSession().GameState);
            else
                channel.CasterGUID = GetSession().GameState.CurrentPlayerGuid;
            channel.TimeRemaining = packet.ReadInt32();
            SendPacketToClient(channel);
        }

        [PacketHandler(Opcode.SMSG_SPELL_DAMAGE_SHIELD)]
        void HandleSpellDamageShield(WorldPacket packet)
        {
            SpellDamageShield spell = new();
            spell.VictimGUID = packet.ReadGuid().To128(GetSession().GameState);
            spell.CasterGUID = packet.ReadGuid().To128(GetSession().GameState);

            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
                spell.SpellID = packet.ReadUInt32();
            else
                spell.SpellID = 7294; // Retribution Aura

            spell.Damage = packet.ReadInt32();
            spell.OriginalDamage = spell.Damage;

            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
                spell.OverKill = packet.ReadUInt32();

            uint school = packet.ReadUInt32();
            if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180))
                school = (1u << (byte)school);

            spell.SchoolMask = school;
            SendPacketToClient(spell);
        }

        [PacketHandler(Opcode.SMSG_ENVIRONMENTAL_DAMAGE_LOG)]
        void HandleEnvironmentalDamageLog(WorldPacket packet)
        {
            EnvironmentalDamageLog damage = new();
            damage.Victim = packet.ReadGuid().To128(GetSession().GameState);
            damage.Type = (EnvironmentalDamage)packet.ReadUInt8();
            damage.Amount = packet.ReadInt32();
            damage.Absorbed = packet.ReadInt32();
            damage.Resisted = packet.ReadInt32();
            SendPacketToClient(damage);
        }

        [PacketHandler(Opcode.SMSG_SPELL_INSTAKILL_LOG)]
        void HandleSpellInstakillLog(WorldPacket packet)
        {
            SpellInstakillLog spell = new();
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
            {
                spell.CasterGUID = packet.ReadGuid().To128(GetSession().GameState);
                spell.TargetGUID = packet.ReadGuid().To128(GetSession().GameState);
            }
            else
                spell.CasterGUID = spell.TargetGUID = packet.ReadGuid().To128(GetSession().GameState);
            spell.SpellID = packet.ReadUInt32();
            SendPacketToClient(spell);
        }

        [PacketHandler(Opcode.SMSG_SPELL_DISPELL_LOG)]
        void HandleSpellDispellLog(WorldPacket packet)
        {
            SpellDispellLog spell = new();
            spell.TargetGUID = packet.ReadPackedGuid().To128(GetSession().GameState);
            spell.CasterGUID = packet.ReadPackedGuid().To128(GetSession().GameState);

            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
                spell.DispelledBySpellID = packet.ReadUInt32();
            else
                spell.DispelledBySpellID = GetSession().GameState.LastDispellSpellId;

            bool hasDebug;
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
                hasDebug = packet.ReadBool();
            else
                hasDebug = false;

            int count = packet.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                SpellDispellData dispel = new SpellDispellData();
                dispel.SpellID = packet.ReadUInt32();
                if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
                    dispel.Harmful = packet.ReadBool();
                spell.DispellData.Add(dispel);
            }

            if (hasDebug)
            {
                packet.ReadInt32(); // unk
                packet.ReadInt32(); // unk
            }

            SendPacketToClient(spell);
        }

        [PacketHandler(Opcode.SMSG_PLAY_SPELL_VISUAL)]
        void HandlePlaySpellVisualKit(WorldPacket packet)
        {
            PlaySpellVisualKit spell = new();
            spell.Unit = packet.ReadGuid().To128(GetSession().GameState);
            spell.KitRecID = packet.ReadUInt32();
            SendPacketToClient(spell);
        }

        [PacketHandler(Opcode.SMSG_UPDATE_AURA_DURATION)]
        void HandleUpdateAuraDuration(WorldPacket packet)
        {
            byte slot = packet.ReadUInt8();
            int duration = packet.ReadInt32();

            WowGuid128 guid = GetSession().GameState.CurrentPlayerGuid;
            if (guid == null)
                return;

            GetSession().GameState.StoreAuraDurationLeft(guid, slot, duration, (int)packet.GetReceivedTime());
            if (duration <= 0)
                return;

            var updateFields = GetSession().GameState.GetCachedObjectFieldsLegacy(guid);
            if (updateFields == null)
                return;

            AuraInfo aura = new AuraInfo();
            aura.Slot = slot;
            aura.AuraData = ReadAuraSlot(slot, guid, updateFields);
            if (aura.AuraData == null)
                return;

            aura.AuraData.Flags |= AuraFlagsModern.Duration;
            aura.AuraData.Duration = duration;
            aura.AuraData.Remaining = duration;

            AuraUpdate update = new AuraUpdate(guid, false);
            update.Auras.Add(aura);
            SendPacketToClient(update);
        }

        [PacketHandler(Opcode.SMSG_SET_EXTRA_AURA_INFO)]
        [PacketHandler(Opcode.SMSG_SET_EXTRA_AURA_INFO_NEED_UPDATE)]
        void HandleSetExtraAuraInfo(WorldPacket packet)
        {
            WowGuid128 guid = packet.ReadPackedGuid().To128(GetSession().GameState);
            if (!packet.CanRead())
                return;

            byte slot = packet.ReadUInt8();
            uint spellId = packet.ReadUInt32();
            int durationFull = packet.ReadInt32();
            int durationLeft = packet.ReadInt32();

            GetSession().GameState.StoreAuraDurationFull(guid, slot, durationFull);
            GetSession().GameState.StoreAuraDurationLeft(guid, slot, durationLeft, (int)packet.GetReceivedTime());

            if (packet.GetUniversalOpcode(false) == Opcode.SMSG_SET_EXTRA_AURA_INFO_NEED_UPDATE)
                GetSession().GameState.StoreAuraCaster(guid, slot, GetSession().GameState.CurrentPlayerGuid);

            if (durationFull <= 0 && durationLeft <= 0)
                return;

            var updateFields = GetSession().GameState.GetCachedObjectFieldsLegacy(guid);
            if (updateFields == null)
                return;

            AuraInfo aura = new AuraInfo();
            aura.Slot = slot;
            aura.AuraData = ReadAuraSlot(slot, guid, updateFields);
            if (aura.AuraData == null)
                return;
            if (aura.AuraData.SpellID != spellId)
                return;

            aura.AuraData.CastUnit = GetSession().GameState.GetAuraCaster(guid, slot, spellId);
            aura.AuraData.Flags |= AuraFlagsModern.Duration;
            aura.AuraData.Duration = durationFull;
            aura.AuraData.Remaining = durationLeft;

            AuraUpdate update = new AuraUpdate(guid, false);
            update.Auras.Add(aura);
            SendPacketToClient(update);
        }

        [PacketHandler(Opcode.SMSG_RESURRECT_REQUEST)]
        void HandleResurrectRequest(WorldPacket packet)
        {
            ResurrectRequest revive = new();
            revive.CasterGUID = packet.ReadGuid().To128(GetSession().GameState);
            revive.CasterVirtualRealmAddress = GetSession().RealmId.GetAddress();
            packet.ReadUInt32(); // Name Length
            revive.Name = packet.ReadCString();
            revive.Sickness = packet.ReadBool();
            revive.UseTimer = packet.ReadBool();
            SendPacketToClient(revive);
        }

        [PacketHandler(Opcode.SMSG_TOTEM_CREATED)]
        void HandleTotemCreated(WorldPacket packet)
        {
            TotemCreated totem = new();
            totem.Slot = packet.ReadUInt8();
            totem.Totem = packet.ReadGuid().To128(GetSession().GameState);
            totem.Duration = packet.ReadUInt32();
            totem.SpellId = packet.ReadUInt32();
            SendPacketToClient(totem);
        }

        [PacketHandler(Opcode.SMSG_SET_FLAT_SPELL_MODIFIER)]
        [PacketHandler(Opcode.SMSG_SET_PCT_SPELL_MODIFIER)]
        void HandleSetSpellModifier(WorldPacket packet)
        {
            byte classIndex = packet.ReadUInt8();
            byte modIndex = packet.ReadUInt8();
            int modValue = packet.ReadInt32();

            if (GetSession().GameState.CurrentPlayerCreateTime != 0)
            {
                SetSpellModifier spell = new SetSpellModifier(packet.GetUniversalOpcode(false));
                SpellModifierInfo mod = new SpellModifierInfo();
                SpellModifierData data = new SpellModifierData();
                data.ClassIndex = classIndex;
                mod.ModIndex = modIndex;
                data.ModifierValue = modValue;
                mod.ModifierData.Add(data);
                spell.Modifiers.Add(mod);
                SendPacketToClient(spell);
            }

            if (packet.GetUniversalOpcode(false) == Opcode.SMSG_SET_FLAT_SPELL_MODIFIER)
                GetSession().GameState.SetFlatSpellMod(modIndex, classIndex, modValue);
            else
                GetSession().GameState.SetPctSpellMod(modIndex, classIndex, modValue);
        }

        private int FindAuraSlotBySpellId(WowGuid128 target, uint spellId, Dictionary<int, UpdateField> updateFields)
        {
            int UNIT_FIELD_AURA = LegacyVersion.GetUpdateField(UnitField.UNIT_FIELD_AURA);
            if (UNIT_FIELD_AURA < 0)
                return -1;

            int aurasCount = LegacyVersion.GetAuraSlotsCount();
            for (int i = 0; i < aurasCount; i++)
            {
                if (updateFields.TryGetValue(UNIT_FIELD_AURA + i, out var field) && field.UInt32Value == spellId)
                    return i;
            }

            return -1;
        }

        private void SendAuraRefreshUpdate(WowGuid128 target, uint spellId, WowGuid128 caster, byte slot, Dictionary<int, UpdateField> updateFields)
        {
            AuraDataInfo auraData = ReadAuraSlot(slot, target, updateFields);
            if (auraData == null || auraData.SpellID != spellId)
                return;

            auraData.CastUnit = caster;

            GetSession().GameState.GetAuraDuration(target, slot, out int durationLeft, out int durationFull);

            if (durationFull <= 0)
                durationFull = GameData.GetAuraSpellDuration(spellId);

            if (durationFull > 0)
            {
                auraData.Flags |= AuraFlagsModern.Duration;
                auraData.Duration = durationFull;
                auraData.Remaining = durationFull;

                GetSession().GameState.StoreAuraDurationLeft(target, slot, durationFull, Environment.TickCount);
                GetSession().GameState.StoreAuraDurationFull(target, slot, durationFull);
            }

            AuraInfo aura = new AuraInfo();
            aura.Slot = slot;
            aura.AuraData = auraData;

            AuraUpdate clearUpdate = new AuraUpdate(target, false);
            AuraInfo clearAura = new AuraInfo();
            clearAura.Slot = slot;
            clearUpdate.Auras.Add(clearAura);
            SendPacketToClient(clearUpdate);

            AuraUpdate update = new AuraUpdate(target, false);
            update.Auras.Add(aura);
            SendPacketToClient(update);
        }
    }
}
