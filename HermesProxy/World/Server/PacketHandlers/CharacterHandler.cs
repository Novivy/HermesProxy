using System;
using System.Linq;
using Framework.Constants;
using Framework.Logging;
using HermesProxy.Auth;
using HermesProxy.Enums;
using HermesProxy.World;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using HermesProxy.World.Server.Packets;

namespace HermesProxy.World.Server
{
    public partial class WorldSocket
    {
        // Handlers for CMSG opcodes coming from the modern client
        [PacketHandler(Opcode.CMSG_ENUM_CHARACTERS)]
        void HandleEnumCharacters(EnumCharacters charEnum)
        {
            WorldPacket packet = new WorldPacket(Opcode.CMSG_ENUM_CHARACTERS);
            SendPacketToServer(packet);
        }

        [PacketHandler(Opcode.CMSG_GET_ACCOUNT_CHARACTER_LIST)]
        void HandleGetAccountCharacterList(GetAccountCharacterListRequest request)
        {
            GetAccountCharacterListResult response = new();
            response.Token = request.Token;

            foreach (var ownCharacter in GetSession().GameState.OwnCharacters)
            {
                response.CharacterList.Add(new AccountCharacterListEntry
                {
                    AccountId = WowGuid128.Create(HighGuidType703.WowAccount, GetSession().GameAccountInfo.Id),
                    CharacterGuid = ownCharacter.CharacterGuid,
                    RealmVirtualAddress = GetSession().RealmId.GetAddress(),
                    RealmName = "", // If empty the realm name will not be displayed
                    LastLoginUnixSec = ownCharacter.LastLoginUnixSec,

                    Name = ownCharacter.Name,
                    Race = ownCharacter.RaceId,
                    Class = ownCharacter.ClassId,
                    Sex = ownCharacter.SexId,
                    Level = ownCharacter.Level,
                });
            }

            SendPacket(response);
        }

        [PacketHandler(Opcode.CMSG_GENERATE_RANDOM_CHARACTER_NAME)]
        void HandleGenerateRandomCharacterNameRequest(GenerateRandomCharacterNameRequest randomCharacterName)
        {
            GenerateRandomCharacterNameResult result = new();

            // The client can generate the name itself
            result.Success = false;

            SendPacket(result);
        }

        [PacketHandler(Opcode.CMSG_CREATE_CHARACTER)]
        void HandleCreateCharacter(CreateCharacter charCreate)
        {
            WorldPacket packet = new WorldPacket(Opcode.CMSG_CREATE_CHARACTER);
            packet.WriteCString(charCreate.CreateInfo.Name);
            packet.WriteUInt8((byte)charCreate.CreateInfo.RaceId);
            packet.WriteUInt8((byte)charCreate.CreateInfo.ClassId);
            packet.WriteUInt8((byte)charCreate.CreateInfo.Sex);

            CharacterCustomizations.ConvertModernCustomizationsToLegacy(charCreate.CreateInfo.Customizations, out byte skin, out byte face, out byte hairStyle, out byte hairColor, out byte facialhair);
            packet.WriteUInt8(skin);
            packet.WriteUInt8(face);
            packet.WriteUInt8(hairStyle);
            packet.WriteUInt8(hairColor);
            packet.WriteUInt8(facialhair);
            packet.WriteUInt8(0); // outfit
            SendPacketToServer(packet);
        }

        [PacketHandler(Opcode.CMSG_CHAR_DELETE)]
        void HandleCharDelete(CharDelete charDelete)
        {
            WorldPacket packet = new WorldPacket(Opcode.CMSG_CHAR_DELETE);
            packet.WriteGuid(charDelete.Guid.To64());
            SendPacketToServer(packet);
        }

        [PacketHandler(Opcode.CMSG_LOADING_SCREEN_NOTIFY)]
        void HandleLoadScreen(LoadingScreenNotify loadingScreenNotify)
        {
            if (loadingScreenNotify.MapID >= 0)
                GetSession().GameState.CurrentMapId = loadingScreenNotify.MapID;
        }

        [PacketHandler(Opcode.CMSG_QUERY_PLAYER_NAME)]
        void HandleNameQueryRequest(QueryPlayerName queryPlayerName)
        {
            WorldPacket packet = new WorldPacket(Opcode.CMSG_NAME_QUERY);
            packet.WriteGuid(queryPlayerName.Player.To64());
            SendPacketToServer(packet, GetSession().GameState.IsInWorld ? Opcode.MSG_NULL_ACTION : Opcode.SMSG_LOGIN_VERIFY_WORLD);
        }

        [PacketHandler(Opcode.CMSG_QUERY_PLAYER_NAMES)]
        void HandleNamesQueryRequest(QueryPlayerNames queryPlayerNames)
        {
            foreach (var guid in queryPlayerNames.Players)
            {
                WorldPacket packet = new WorldPacket(Opcode.CMSG_NAME_QUERY);
                packet.WriteGuid(guid.To64());
                SendPacketToServer(packet, GetSession().GameState.IsInWorld ? Opcode.MSG_NULL_ACTION : Opcode.SMSG_LOGIN_VERIFY_WORLD);
            }
        }

        [PacketHandler(Opcode.CMSG_PLAYER_LOGIN)]
        void HandlePlayerLogin(PlayerLogin playerLogin)
        {
            if (!GetSession().GameState.CachedPlayers.TryGetValue(playerLogin.Guid, out var selectedChar))
            {
                Log.Print(LogType.Error, $"Player tried to log in with unknown char id: {playerLogin.Guid}");
                return;
            }

            var realm = GetSession().RealmManager.GetRealm(GetSession().RealmId);
            if (realm == null)
            {
                Log.Print(LogType.Error, $"Player tried to log in to unknown realm id: {GetSession().RealmId}");
                return;
            }

            GetSession().AccountMetaDataMgr.SaveLastSelectedCharacter(realm.Name, selectedChar.Name, playerLogin.Guid.Low, Time.UnixTime);

            if (GetSession().WorldClient == null || !GetSession().WorldClient.IsConnected())
            {
                Log.Print(LogType.Error, $"(DISCONNECT) Player '{selectedChar.Name}' tried to log in but WorldClient is null or disconnected, login blocked and client will be disconnected");
                return;
            }

            if (GetSession().AuthClient != null)
                GetSession().AuthClient.Disconnect();

            SendConnectToInstance(ConnectToSerial.WorldAttempt1);
            GetSession().GameState.IsConnectedToInstance = true;
            GetSession().GameState.IsFirstEnterWorld = true;
            GetSession().GameState.CurrentPlayerGuid = playerLogin.Guid;
            GetSession().GameState.CurrentPlayerInfo = GetSession().GameState.OwnCharacters.Single(x => x.CharacterGuid == playerLogin.Guid);
            GetSession().GameState.CurrentPlayerStorage.LoadCurrentPlayer();

            WorldPacket packet = new WorldPacket(Opcode.CMSG_PLAYER_LOGIN);
            packet.WriteGuid(playerLogin.Guid.To64());
            SendPacketToServer(packet);


            // Send HermesProxy version to the server via dedicated custom opcode
          /*  WorldPacket versionPacket = new WorldPacket(Opcode.CMSG_HERMES_VERSION);
            versionPacket.WriteCString(GitVersionInformation.MajorMinorPatch);
            SendPacketToServer(versionPacket);*/
        }

        [PacketHandler(Opcode.CMSG_LOGOUT_REQUEST)]
        void HandleLogoutRequest(LogoutRequest logoutRequest)
        {
            WorldPacket packet = new WorldPacket(Opcode.CMSG_LOGOUT_REQUEST);
            SendPacketToServer(packet);
        }

        [PacketHandler(Opcode.CMSG_LOGOUT_CANCEL)]
        void HandleLogoutCancel(LogoutCancel logoutCancel)
        {
            WorldPacket packet = new WorldPacket(Opcode.CMSG_LOGOUT_CANCEL);
            SendPacketToServer(packet);
        }

        [PacketHandler(Opcode.CMSG_REQUEST_PLAYED_TIME)]
        void HandleRequestPlayedTime(RequestPlayedTime played)
        {
            WorldPacket packet = new WorldPacket(Opcode.CMSG_REQUEST_PLAYED_TIME);
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
                packet.WriteBool(played.TriggerScriptEvent);
            SendPacketToServer(packet);
            GetSession().GameState.ShowPlayedTime = played.TriggerScriptEvent;
        }

        [PacketHandler(Opcode.CMSG_SET_TITLE)]
        void HandleTogglePvP(SetTitle title)
        {
            WorldPacket packet = new WorldPacket(Opcode.CMSG_SET_TITLE);
            packet.WriteInt32(title.TitleID);
            SendPacketToServer(packet);
        }

        [PacketHandler(Opcode.CMSG_TOGGLE_PVP)]
        void HandleTogglePvP(TogglePvP pvp)
        {
            WorldPacket packet = new WorldPacket(Opcode.CMSG_TOGGLE_PVP);
            SendPacketToServer(packet);
        }

        [PacketHandler(Opcode.CMSG_SET_PVP)]
        void HandleTogglePvP(SetPvP pvp)
        {
            WorldPacket packet = new WorldPacket(Opcode.CMSG_TOGGLE_PVP);
            packet.WriteBool(pvp.Enable);
            SendPacketToServer(packet);
        }

        [PacketHandler(Opcode.CMSG_SET_ACTION_BUTTON)]
        void HandleSetActionButton(SetActionButton button)
        {
            WorldPacket packet = new WorldPacket(Opcode.CMSG_SET_ACTION_BUTTON);
            packet.WriteUInt8(button.Index);
            packet.WriteUInt16(button.Action);
            packet.WriteUInt16(button.Type);
            SendPacketToServer(packet);
        }

        [PacketHandler(Opcode.CMSG_SET_ACTION_BAR_TOGGLES)]
        void HandleSetActionBarToggles(SetActionBarToggles bars)
        {
            WorldPacket packet = new WorldPacket(Opcode.CMSG_SET_ACTION_BAR_TOGGLES);
            packet.WriteUInt8(bars.Mask);
            SendPacketToServer(packet);
        }

        [PacketHandler(Opcode.CMSG_UNLEARN_SKILL)]
        void HandleUnlearnSkill(UnlearnSkill skill)
        {
            WorldPacket packet = new WorldPacket(Opcode.CMSG_UNLEARN_SKILL);
            packet.WriteUInt32(skill.SkillLine);
            SendPacketToServer(packet);
        }

        [PacketHandler(Opcode.CMSG_PLAYER_SHOWING_CLOAK)]
        [PacketHandler(Opcode.CMSG_PLAYER_SHOWING_HELM)]
        void HandleShowHelmOrCloak(PlayerShowingHelmOrCloak show)
        {
            WorldPacket packet = new WorldPacket(show.GetUniversalOpcode());
            packet.WriteBool(show.Showing);
            SendPacketToServer(packet);
        }

        [PacketHandler(Opcode.CMSG_INSPECT)]
        void HandleInspect(Inspect inspect)
        {
            // The legacy server (see cmangos WorldSocket.cpp InitOpcodeCooldowns) silently drops
            // any CMSG_INSPECT within 5000ms of the previous one. The proxy already has all the
            // data needed to build the response (race/class/sex from CachedPlayers or UNIT_FIELD_BYTES_0,
            // items/guild/rank from the legacy object cache), so when we're inside that 5s window
            // we synthesize the SMSG_INSPECT_RESULT locally instead of letting the request vanish.
            const long cooldownMs = 5000;
            long nowTicks = System.DateTime.UtcNow.Ticks;
            long lastTicks = GetSession().GameState.LastInspectForwardTicks;
            long elapsedMs = (nowTicks - lastTicks) / System.TimeSpan.TicksPerMillisecond;

            if (lastTicks != 0 && elapsedMs < cooldownMs)
            {
                InspectResult result = GetSession().GameState.BuildInspectResultFromCache(inspect.Target);
                SendPacket(result);
                return;
            }

            // If the target isn't cached (e.g. SMSG_INVALIDATE_PLAYER wiped them after a relog,
            // and the modern client uses its own UI cache so it skips CMSG_QUERY_PLAYER_NAME),
            // proactively issue a legacy name query first. The legacy server processes packets in
            // order, so the SMSG_QUERY_PLAYER_NAME_RESPONSE arrives before SMSG_INSPECT and
            // repopulates CachedPlayers with name/race/class/sex before the inspect response runs.
            if (!GetSession().GameState.CachedPlayers.ContainsKey(inspect.Target))
            {
                WorldPacket nameQuery = new WorldPacket(Opcode.CMSG_NAME_QUERY);
                nameQuery.WriteGuid(inspect.Target.To64());
                SendPacketToServer(nameQuery);
            }

            WorldPacket packet = new WorldPacket(Opcode.CMSG_INSPECT);
            packet.WriteGuid(inspect.Target.To64());
            SendPacketToServer(packet);
            GetSession().GameState.LastInspectForwardTicks = nowTicks;
        }

        [PacketHandler(Opcode.CMSG_INSPECT_HONOR_STATS)]
        void HandleInspectHonorStats(Inspect inspect)
        {
            WorldPacket packet = new WorldPacket(Opcode.MSG_INSPECT_HONOR_STATS);
            packet.WriteGuid(inspect.Target.To64());
            SendPacketToServer(packet);
        }

        [PacketHandler(Opcode.CMSG_INSPECT_PVP)]
        void HandleInspectArenaTeams(Inspect inspect)
        {
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
            {
                WorldPacket packet = new WorldPacket(Opcode.MSG_INSPECT_ARENA_TEAMS);
                packet.WriteGuid(inspect.Target.To64());
                SendPacketToServer(packet);
            }
            else
            {
                InspectPvP pvp = new InspectPvP();
                pvp.PlayerGUID = inspect.Target;
                pvp.ArenaTeams.Add(new ArenaTeamInspectData());
                pvp.ArenaTeams.Add(new ArenaTeamInspectData());
                pvp.ArenaTeams.Add(new ArenaTeamInspectData());
                SendPacket(pvp);
            }
        }

        [PacketHandler(Opcode.CMSG_CHARACTER_RENAME_REQUEST)]
        void HandleCharacterRenameRequest(CharacterRenameRequest rename)
        {
            WorldPacket packet = new WorldPacket(Opcode.CMSG_CHARACTER_RENAME_REQUEST);
            packet.WriteGuid(rename.Guid.To64());
            packet.WriteCString(rename.NewName);
            SendPacketToServer(packet);
        }
    }
}
