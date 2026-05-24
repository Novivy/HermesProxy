using HermesProxy.World.Enums;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Framework;
using Framework.Logging;

namespace HermesProxy.World.Server
{
    public class AccountMetaDataManager
    {
        private const string LAST_CHARACTER_FILE = "last_character.txt";
        private const string CHARACTERS_ORDER_FILE = "characters_order.txt";
        private const string COMPLETED_QUESTS_FILE = "completed_quests.csv";
        private const string SETTINGS_FILE = "settings.json";

        private readonly string _accountName;

        private string GetAccountMetaDataDirectory()
        {
            string path = Path.GetFullPath(Path.Combine("AccountData", _accountName));

            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);

            return path;
        }

        private string GetAccountCharacterMetaDataDirectory(string realm, string characterName, ulong charLowerGuid)
        {
            string newPath = Path.GetFullPath(Path.Combine("AccountData", _accountName, realm, $"{characterName}-{charLowerGuid}"));

            if (!Directory.Exists(newPath))
            {
                // Migrate old name-only directory if it exists (upgrade path for existing chars)
                string oldPath = Path.GetFullPath(Path.Combine("AccountData", _accountName, realm, characterName));
                if (Directory.Exists(oldPath))
                    Directory.Move(oldPath, newPath);
                else
                    Directory.CreateDirectory(newPath);
            }

            return newPath;
        }
        
        public AccountMetaDataManager(string accountName)
        {
            _accountName = accountName;
        }

        public (string realmName, string charName, ulong charLowerGuid, long lastLoginUnixSec)? GetLastSelectedCharacter()
        {
            var path = Path.Combine(GetAccountMetaDataDirectory(), LAST_CHARACTER_FILE);
            if (!File.Exists(path))
                return null;

            var rawContent = File.ReadAllText(path, Encoding.UTF8);
            var content = rawContent.Split(',');
            if (content.Length != 4)
            {
                Log.Print(LogType.Error, $"Invalid split size in 'GetLastSelectedCharacter' for account '{_accountName}'");
                return null;
            }
            
            return (content[0], content[1], ulong.Parse(content[3]), long.Parse(content[2]));
        }

        public void SaveLastSelectedCharacter(string realmName, string charName, ulong charLowerGuid, long lastLoginUnixSec)
        {
            var dir = GetAccountMetaDataDirectory();
            var path = Path.Combine(dir, LAST_CHARACTER_FILE);

            File.WriteAllText(path, $"{realmName},{charName},{charLowerGuid},{lastLoginUnixSec}", Encoding.UTF8);
            Log.Print(LogType.Debug, $"Saved last selected char in '{path}'");
        }

        public byte GetCharacterListPosition(string realmName, WowGuid128 charGuid)
        {
            var dir = GetAccountMetaDataDirectory();
            var path = Path.Combine(dir, CHARACTERS_ORDER_FILE);

            if (!File.Exists(path))
                return 0;

            List<string> charOrderList = File.ReadAllLines(path).ToList();
            foreach (var entry in charOrderList)
            {
                var charOrderInfo = entry.Split(',');
                string savedRealm = charOrderInfo[0];

                if (savedRealm != realmName)
                    continue;

                byte position = byte.Parse(charOrderInfo[1]);
                ulong guidLow = ulong.Parse(charOrderInfo[2]);
                if (guidLow == charGuid.Low)
                    return position;
            }
            return 0;
        }

        public void SaveCharacterOrder(string realmName, Dictionary<ulong, byte> changedPositionsList, List<OwnCharacterInfo> allCharList)
        {
            var dir = GetAccountMetaDataDirectory();
            var path = Path.Combine(dir, CHARACTERS_ORDER_FILE);
            Dictionary<string, Dictionary<ulong, byte>> characterPositionsList = new Dictionary<string, Dictionary<ulong, byte>>();

            if (File.Exists(path))
            {
                List<string> charOrderList = File.ReadAllLines(path).ToList();
                foreach (var entry in charOrderList)
                {
                    var charOrderInfo = entry.Split(',');
                    string savedRealm = charOrderInfo[0];
                    byte position = byte.Parse(charOrderInfo[1]);
                    ulong guidLow = ulong.Parse(charOrderInfo[2]);

                    if (!characterPositionsList.ContainsKey(savedRealm))
                    {
                        Dictionary<ulong, byte> realmChars = new Dictionary<ulong, byte>();
                        characterPositionsList.Add(savedRealm, realmChars);
                    }

                    if (savedRealm == realmName)
                    {
                        if (!allCharList.Any(own => own.CharacterGuid.Low == guidLow))
                            continue;

                        byte newPosition;
                        if (changedPositionsList.TryGetValue(guidLow, out newPosition))
                        {
                            position = newPosition;
                        }
                    }

                    if (characterPositionsList.ContainsKey(savedRealm))
                        characterPositionsList[savedRealm][guidLow] = position;
                }
            }

            if (!characterPositionsList.ContainsKey(realmName))
            {
                Dictionary<ulong, byte> realmChars = new Dictionary<ulong, byte>();
                characterPositionsList.Add(realmName, realmChars);
            }

            foreach (var changedPos in changedPositionsList)
            {
                if (characterPositionsList.ContainsKey(realmName))
                {
                    characterPositionsList[realmName][changedPos.Key] = changedPos.Value;
                }
            }

            File.WriteAllText(path, "");
            foreach (var posRealm in characterPositionsList)
            {
                foreach (var pos in posRealm.Value)
                {
                    File.AppendAllLines(path, new[] { $"{posRealm.Key},{pos.Value},{pos.Key}" }, Encoding.UTF8);
                }
            }
            Log.Print(LogType.Debug, $"Characters order saved in '{path}'");
        }

        public void InvalidateLastSelectedCharacter()
        {
            var dir = GetAccountMetaDataDirectory();
            var path = Path.Combine(dir, LAST_CHARACTER_FILE);

            if (!File.Exists(path))
                return;

            File.WriteAllText(path, "");
            Log.Print(LogType.Debug, $"Invalidated last selected character entry in '{path}'");
        }

        public List<uint> GetAllCompletedQuests(string realmName, string charName, ulong charLowerGuid)
        {
            var dir = GetAccountCharacterMetaDataDirectory(realmName, charName, charLowerGuid);
            var path = Path.Combine(dir, COMPLETED_QUESTS_FILE);

            if (!File.Exists(path))
                return new List<uint>();

            List<string> lines = File.ReadAllLines(path).ToList();

            var completedQuestIds = lines.Select(x => uint.Parse(x.Split(',').FirstOrDefault() ?? "0")).ToList();
            return completedQuestIds;
        }

        public void MarkQuestAsCompleted(string realmName, string charName, ulong charLowerGuid, uint questId)
        {
            var dir = GetAccountCharacterMetaDataDirectory(realmName, charName, charLowerGuid);
            var path = Path.Combine(dir, COMPLETED_QUESTS_FILE);

            var when = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            File.AppendAllLines(path, new[]{$"{questId},{when}"}, Encoding.UTF8);
        }

        public void MarkQuestAsNotCompleted(string realmName, string charName, ulong charLowerGuid, uint questId)
        {
            var dir = GetAccountCharacterMetaDataDirectory(realmName, charName, charLowerGuid);
            var path = Path.Combine(dir, COMPLETED_QUESTS_FILE);

            string needle = questId.ToString();
            List<string> lines = File.ReadAllLines(path).ToList();
            lines.RemoveAll(l => l.Split(',').FirstOrDefault()?.Equals(needle) ?? false);
            File.WriteAllLines(path, lines);
        }

        public void SaveCharacterSettingsStorage(string realmName, string charName, ulong charLowerGuid, PlayerSettings.InternalStorage settings)
        {
            var dir = GetAccountCharacterMetaDataDirectory(realmName, charName, charLowerGuid);
            var path = Path.Combine(dir, SETTINGS_FILE);

            var options = new JsonSerializerOptions { WriteIndented = true };
            var jsonString = JsonSerializer.Serialize(settings, options);
            File.WriteAllText(path, jsonString, Encoding.UTF8);
        }

        public PlayerSettings.InternalStorage LoadCharacterSettingsStorage(string realmName, string charName, ulong charLowerGuid)
        {
            var dir = GetAccountCharacterMetaDataDirectory(realmName, charName, charLowerGuid);
            var path = Path.Combine(dir, SETTINGS_FILE);

            if (!File.Exists(path))
            {
                var fallback = new PlayerSettings.InternalStorage();
                SaveCharacterSettingsStorage(realmName, charName, charLowerGuid, fallback);
                return fallback; // Default fallback
            }

            var jsonString = File.ReadAllText(path, Encoding.UTF8);
            var loadedJson = JsonSerializer.Deserialize<PlayerSettings.InternalStorage>(jsonString);

            return loadedJson;
        }
    }
    
    public class AccountData
    {
        public WowGuid128 Guid;
        public long Timestamp;
        public uint Type;
        public uint UncompressedSize;
        public byte[] CompressedData;
    }
    public class AccountDataManager
    {
        public AccountData[] Data;
        string _accountName;
        string _realmName;
        
        public AccountDataManager(string accountName, string realmName)
        {
            _accountName = accountName;
            _realmName = realmName.Trim();
        }

        public static bool IsGlobalDataType(uint type)
        {
            switch ((AccountDataType)type)
            {
                case AccountDataType.GlobalConfigCache:
                case AccountDataType.GlobalBindingsCache:
                case AccountDataType.GlobalMacrosCache:
                case AccountDataType.GlobalTTSCache:
                case AccountDataType.GlobalFlaggedCache:
                    return true;
            }
            return false;
        }

        public string GetAccountDataDirectory()
        {
            string path = Path.GetFullPath(Path.Combine("AccountData", _accountName, _realmName));

            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);

            return path;
        }

        public string GetFullFileName(WowGuid128 guid, uint type)
        {
            string file;
            if (IsGlobalDataType(type))
                file = $"data-{type}.bin";
            else
                file = $"data-{type}-{guid.GetLowValue()}-{guid.GetHighValue()}.bin";

            string path = GetAccountDataDirectory();
            path = Path.Combine(path, file);
            return path;
        }

        public void LoadAllData(WowGuid128 guid)
        {
            Data = new AccountData[ModernVersion.GetAccountDataCount()];

            for (uint i = 0; i < ModernVersion.GetAccountDataCount(); i++)
            {
                Data[i] = LoadData(guid, i);
            }
        }

        public AccountData LoadData(WowGuid128 guid, uint type)
        {
            AccountData data = null;
            string fileName = GetFullFileName(guid, type);

            if (File.Exists(fileName))
            {
                try
                {
                    using (BinaryReader reader = new BinaryReader(File.OpenRead(fileName)))
                    {
                        data = new();
                        ulong guidLow = reader.ReadUInt64();
                        ulong guidHigh = reader.ReadUInt64();
                        data.Guid = new WowGuid128(guidHigh, guidLow);

                        if (!IsGlobalDataType(type))
                            System.Diagnostics.Trace.Assert(guid == data.Guid);

                        data.Timestamp = reader.ReadInt64();
                        data.Type = reader.ReadUInt32();
                        System.Diagnostics.Trace.Assert(type == data.Type);
                        data.UncompressedSize = reader.ReadUInt32();

                        int compressedSize = reader.ReadInt32();
                        data.CompressedData = reader.ReadBytes(compressedSize);
                    }
                }
                catch (Exception ex)
                {
                    Log.Print(LogType.Error, $"Corrupt account data file '{fileName}' (type {type}): {ex.Message}. Deleting it.");
                    File.Delete(fileName);
                    data = null;
                }
            }
            
            return data;
        }

        public void SaveData(WowGuid128 guid, long timestamp, uint type, uint uncompressedSize, byte[] compressedData)
        {
            if (compressedData == null)
                return;
            if (Data[type] == null)
                Data[type] = new();

            Data[type].Guid = guid;
            Data[type].Timestamp = timestamp;
            Data[type].Type = type;
            Data[type].UncompressedSize = uncompressedSize;
            Data[type].CompressedData = compressedData;

            string finalPath = GetFullFileName(guid, type);
            string tempPath = finalPath + ".tmp";
            using (BinaryWriter writer = new BinaryWriter(File.Open(tempPath, FileMode.Create)))
            {
                writer.Write(guid.GetLowValue());
                writer.Write(guid.GetHighValue());
                writer.Write(timestamp);
                writer.Write(type);
                writer.Write(uncompressedSize);
                writer.Write(compressedData.Length);
                writer.Write(compressedData);
            }
            File.Move(tempPath, finalPath, overwrite: true);
        }

        public byte[] LoadCUFProfiles()
        {
            string fileName = Path.Combine(GetAccountDataDirectory(), "cuf.bin");

            if (File.Exists(fileName))
            {
                using (FileStream file = File.OpenRead(fileName))
                {
                    using (BinaryReader reader = new BinaryReader(file))
                    {
                        return File.ReadAllBytes(fileName);
                    }
                }
            }

            return new byte[4];
        }

        public void SaveCUFProfiles(byte[] data)
        {
            string fileName = Path.Combine(GetAccountDataDirectory(), "cuf.bin");

            using (BinaryWriter writer = new BinaryWriter(File.Open(fileName, FileMode.Create)))
            {
                writer.Write(data);
            }
        }
    }
}
