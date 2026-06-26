using System.Text;
using Newtonsoft.Json.Linq;

namespace NFCAiME.AimeIO.Mod
{
    internal static class PayloadParser
    {
        public static CardPayload Parse(string json)
        {
            var obj = JObject.Parse(json);
            var encrypted = obj.Value<bool?>("encrypted") == true ||
                            string.Equals(obj.Value<string>("type"), "encrypted", System.StringComparison.OrdinalIgnoreCase);

            if (encrypted)
            {
                var plaintext = AesGcmDecryptor.Decrypt(
                    Hex.ToBytes(obj.Value<string>("nonce")),
                    Hex.ToBytes(obj.Value<string>("ciphertext")),
                    Hex.ToBytes(obj.Value<string>("tag")));
                obj = JObject.Parse(Encoding.UTF8.GetString(plaintext));
            }

            return new CardPayload
            {
                Type = obj.Value<string>("type"),
                PrivateAccessCode = NormalizeCode(obj.Value<string>("privateAccessCode")),
                OfficialAccessCode = NormalizeCode(obj.Value<string>("officialAccessCode")),
                Idm = NormalizeHex(obj.Value<string>("idm")),
                Encrypted = encrypted
            };
        }

        private static string NormalizeCode(string value)
        {
            value = (value ?? "").Trim().Replace(" ", "");
            return value;
        }

        private static string NormalizeHex(string value)
        {
            value = (value ?? "").Trim().Replace(":", "").Replace(" ", "").ToLowerInvariant();
            return value;
        }
    }
}
