using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NFCAiME.AimeIO.Mod
{
    internal static class FlatJson
    {
        public static Dictionary<string, string> Parse(string json)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var i = 0;
            Skip(json, ref i);
            if (i >= json.Length || json[i] != '{')
            {
                throw new FormatException("JSON object expected");
            }

            i++;
            while (i < json.Length)
            {
                Skip(json, ref i);
                if (i < json.Length && json[i] == '}')
                {
                    return result;
                }

                var key = ReadString(json, ref i);
                Skip(json, ref i);
                if (i >= json.Length || json[i] != ':')
                {
                    throw new FormatException("JSON ':' expected");
                }

                i++;
                Skip(json, ref i);
                result[key] = ReadValue(json, ref i);
                Skip(json, ref i);
                if (i < json.Length && json[i] == ',')
                {
                    i++;
                }
            }

            return result;
        }

        public static bool Bool(Dictionary<string, string> obj, string key)
        {
            string value;
            return obj.TryGetValue(key, out value) && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }

        public static uint UInt(Dictionary<string, string> obj, params string[] keys)
        {
            foreach (var key in keys)
            {
                string value;
                uint parsed;
                if (obj.TryGetValue(key, out value) && uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                {
                    return parsed;
                }
            }

            return 0;
        }

        private static string ReadValue(string json, ref int i)
        {
            if (i < json.Length && json[i] == '"')
            {
                return ReadString(json, ref i);
            }

            var start = i;
            while (i < json.Length && json[i] != ',' && json[i] != '}')
            {
                i++;
            }

            return json.Substring(start, i - start).Trim();
        }

        private static string ReadString(string json, ref int i)
        {
            if (i >= json.Length || json[i] != '"')
            {
                throw new FormatException("JSON string expected");
            }

            i++;
            var sb = new StringBuilder();
            while (i < json.Length)
            {
                var c = json[i++];
                if (c == '"')
                {
                    return sb.ToString();
                }

                if (c != '\\' || i >= json.Length)
                {
                    sb.Append(c);
                    continue;
                }

                var escaped = json[i++];
                switch (escaped)
                {
                    case '"':
                    case '\\':
                    case '/':
                        sb.Append(escaped);
                        break;
                    case 'b':
                        sb.Append('\b');
                        break;
                    case 'f':
                        sb.Append('\f');
                        break;
                    case 'n':
                        sb.Append('\n');
                        break;
                    case 'r':
                        sb.Append('\r');
                        break;
                    case 't':
                        sb.Append('\t');
                        break;
                    case 'u':
                        if (i + 4 > json.Length)
                        {
                            throw new FormatException("invalid unicode escape");
                        }

                        sb.Append((char)int.Parse(json.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                }
            }

            throw new FormatException("unterminated JSON string");
        }

        private static void Skip(string json, ref int i)
        {
            while (i < json.Length && char.IsWhiteSpace(json, i))
            {
                i++;
            }
        }
    }
}
