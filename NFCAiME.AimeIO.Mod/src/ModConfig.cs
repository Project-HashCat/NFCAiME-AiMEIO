using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace NFCAiME.AimeIO.Mod
{
    internal sealed class ModConfig
    {
        public bool Enabled = true;
        public string ServerUrl = "https://card.segasb.me/";
        public string SessionKey = "";
        public int CardTtlMs = 5000;
        public string PreferredAccessCode = "private";
        public bool AimeDbResolve = true;
        public string AimeDbHost = "";
        public int AimeDbPort = 22345;
        public string AimeDbGameId = "";
        public string AimeDbKeychip = "";
        public string AimeDbStoreIdHex = "bc310000";
        public int AimeDbTimeoutMs = 3000;
        private const string ConfigFileName = "NFCAiME.AimeIO.Mod.toml";

        public static ModConfig Load()
        {
            var root = AppDomain.CurrentDomain.BaseDirectory;
            var config = new ModConfig();

            ReadToml(Path.Combine(root, ConfigFileName), config);
            ReadSegatoolsIni(Path.Combine(root, "segatools.ini"), config);

            if (string.IsNullOrWhiteSpace(config.SessionKey))
            {
                WriteDefaultToml(Path.Combine(root, ConfigFileName), config);
            }

            config.ServerUrl = NormalizeServerUrl(config.ServerUrl);
            config.SessionKey = (config.SessionKey ?? "").Trim().Trim('/');
            config.PreferredAccessCode = (config.PreferredAccessCode ?? "private").Trim().ToLowerInvariant();
            if (config.CardTtlMs < 1000)
            {
                config.CardTtlMs = 5000;
            }
            if (config.AimeDbPort <= 0)
            {
                config.AimeDbPort = 22345;
            }
            if (config.AimeDbTimeoutMs < 500)
            {
                config.AimeDbTimeoutMs = 3000;
            }
            config.AimeDbHost = (config.AimeDbHost ?? "").Trim();
            config.AimeDbGameId = (config.AimeDbGameId ?? "").Trim();
            config.AimeDbKeychip = (config.AimeDbKeychip ?? "").Trim();
            config.AimeDbStoreIdHex = NormalizeStoreId(config.AimeDbStoreIdHex);

            return config;
        }

        public string BuildWebSocketUrl()
        {
            var url = NormalizeServerUrl(ServerUrl);
            if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                url = "wss://" + url.Substring("https://".Length);
            }
            else if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                url = "ws://" + url.Substring("http://".Length);
            }
            else if (!url.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) &&
                     !url.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            {
                url = "wss://" + url;
            }

            return url.TrimEnd('/') + "/" + SessionKey;
        }

        private static void ReadToml(string path, ModConfig config)
        {
            if (!File.Exists(path))
            {
                return;
            }

            foreach (var kv in ReadKeyValues(path))
            {
                Apply(kv.Key, kv.Value, config);
            }
        }

        private static Dictionary<string, string> ReadKeyValues(string path)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = StripComment(raw).Trim();
                var idx = line.IndexOf('=');
                if (idx <= 0)
                {
                    continue;
                }

                result[line.Substring(0, idx).Trim()] = Unquote(line.Substring(idx + 1).Trim());
            }

            return result;
        }

        private static void Apply(string key, string value, ModConfig config)
        {
            switch ((key ?? "").Trim().ToLowerInvariant())
            {
                case "enabled":
                    config.Enabled = !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
                    break;
                case "serverurl":
                case "relayurl":
                    config.ServerUrl = value;
                    break;
                case "session-key":
                case "sessionkey":
                case "instanceid":
                    config.SessionKey = value;
                    break;
                case "cardttlms":
                    int ttl;
                    if (int.TryParse(value, out ttl))
                    {
                        config.CardTtlMs = ttl;
                    }
                    break;
                case "preferredaccesscode":
                    config.PreferredAccessCode = value;
                    break;
                case "aimedbresolve":
                    config.AimeDbResolve = !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
                    break;
                case "aimedbhost":
                    config.AimeDbHost = value;
                    break;
                case "aimedbport":
                    int port;
                    if (int.TryParse(value, out port))
                    {
                        config.AimeDbPort = port;
                    }
                    break;
                case "aimedbgameid":
                    config.AimeDbGameId = value;
                    break;
                case "aimedbkeychip":
                    config.AimeDbKeychip = value;
                    break;
                case "aimedbstoreidhex":
                    config.AimeDbStoreIdHex = value;
                    break;
                case "aimedbtimeoutms":
                    int timeout;
                    if (int.TryParse(value, out timeout))
                    {
                        config.AimeDbTimeoutMs = timeout;
                    }
                    break;
            }
        }

        private static void ReadSegatoolsIni(string path, ModConfig config)
        {
            if (!File.Exists(path))
            {
                return;
            }

            var section = "";
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = StripIniComment(raw).Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    section = line.Substring(1, line.Length - 2).Trim().ToLowerInvariant();
                    continue;
                }

                var idx = line.IndexOf('=');
                if (idx <= 0)
                {
                    continue;
                }

                var key = line.Substring(0, idx).Trim().ToLowerInvariant();
                var value = line.Substring(idx + 1).Trim();

                if (section == "dns" && key == "aimedb" && string.IsNullOrWhiteSpace(config.AimeDbHost))
                {
                    config.AimeDbHost = value;
                }
                else if (section == "keychip" && key == "gameid" && string.IsNullOrWhiteSpace(config.AimeDbGameId))
                {
                    config.AimeDbGameId = value;
                }
                else if (section == "keychip" && key == "id" && string.IsNullOrWhiteSpace(config.AimeDbKeychip))
                {
                    config.AimeDbKeychip = value;
                }
            }
        }

        private static void WriteDefaultToml(string path, ModConfig config)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (File.Exists(path))
            {
                return;
            }

            File.WriteAllText(path,
                "enabled = true\r\n" +
                "serverUrl = \"" + config.ServerUrl + "\"\r\n" +
                "session-key = \"\"\r\n" +
                "cardTtlMs = 5000\r\n" +
                "preferredAccessCode = \"private\"\r\n" +
                "aimeDbResolve = true\r\n" +
                "aimeDbHost = \"\"\r\n" +
                "aimeDbPort = 22345\r\n" +
                "aimeDbGameId = \"\"\r\n" +
                "aimeDbKeychip = \"\"\r\n" +
                "aimeDbStoreIdHex = \"bc310000\"\r\n" +
                "aimeDbTimeoutMs = 3000\r\n");
        }

        private static string NormalizeServerUrl(string value)
        {
            value = (value ?? "").Trim().Trim('"').TrimEnd('/');
            return value.Length == 0 ? "https://card.segasb.me" : value;
        }

        private static string StripComment(string line)
        {
            var idx = line.IndexOf('#');
            return idx >= 0 ? line.Substring(0, idx) : line;
        }

        private static string StripIniComment(string line)
        {
            var semicolon = line.IndexOf(';');
            var hash = line.IndexOf('#');
            var idx = semicolon >= 0 && hash >= 0 ? Math.Min(semicolon, hash) : Math.Max(semicolon, hash);
            return idx >= 0 ? line.Substring(0, idx) : line;
        }

        private static string NormalizeStoreId(string value)
        {
            value = Regex.Replace(value ?? "", "[^0-9a-fA-F]", "").ToLowerInvariant();
            return value.Length == 8 ? value : "bc310000";
        }

        private static string Unquote(string value)
        {
            value = (value ?? "").Trim();
            if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
            {
                return value.Substring(1, value.Length - 2);
            }

            return value;
        }
    }
}
