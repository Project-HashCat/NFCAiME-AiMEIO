using System;
using System.Collections.Generic;
using System.IO;

namespace NFCAiME.AimeIO.Mod
{
    internal sealed class ModConfig
    {
        public bool Enabled = true;
        public string ServerUrl = "https://card.segasb.me/";
        public string SessionKey = "";
        public int CardTtlMs = 5000;
        public string PreferredAccessCode = "private";

        public static ModConfig Load()
        {
            var root = AppDomain.CurrentDomain.BaseDirectory;
            var config = new ModConfig();

            ReadToml(Path.Combine(root, "Mods", "NFCAiME.AimeIO.Mod.toml"), config);
            ReadSegatoolsIni(Path.Combine(root, "segatools.ini"), config);

            if (string.IsNullOrWhiteSpace(config.SessionKey))
            {
                WriteDefaultToml(Path.Combine(root, "Mods", "NFCAiME.AimeIO.Mod.toml"), config);
            }

            config.ServerUrl = NormalizeServerUrl(config.ServerUrl);
            config.SessionKey = (config.SessionKey ?? "").Trim().Trim('/');
            config.PreferredAccessCode = (config.PreferredAccessCode ?? "private").Trim().ToLowerInvariant();
            if (config.CardTtlMs < 1000)
            {
                config.CardTtlMs = 5000;
            }

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

        private static void ReadSegatoolsIni(string path, ModConfig config)
        {
            if (!File.Exists(path))
            {
                return;
            }

            var inAimeIo = false;
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = StripComment(raw).Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    inAimeIo = string.Equals(line, "[aimeio]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inAimeIo)
                {
                    continue;
                }

                var idx = line.IndexOf('=');
                if (idx <= 0)
                {
                    continue;
                }

                Apply(line.Substring(0, idx).Trim(), Unquote(line.Substring(idx + 1).Trim()), config);
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
                "preferredAccessCode = \"private\"\r\n");
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
