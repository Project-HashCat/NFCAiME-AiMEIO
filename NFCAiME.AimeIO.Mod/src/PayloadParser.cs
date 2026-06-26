using System.Text;

namespace NFCAiME.AimeIO.Mod
{
    internal static class PayloadParser
    {
        public static CardPayload Parse(string json)
        {
            var obj = FlatJson.Parse(json);
            var encrypted = FlatJson.Bool(obj, "encrypted") ||
                            string.Equals(Get(obj, "type"), "encrypted", System.StringComparison.OrdinalIgnoreCase);

            if (encrypted)
            {
                var plaintext = AesGcmDecryptor.Decrypt(
                    Hex.ToBytes(Get(obj, "nonce")),
                    Hex.ToBytes(Get(obj, "ciphertext")),
                    Hex.ToBytes(Get(obj, "tag")));
                obj = FlatJson.Parse(Encoding.UTF8.GetString(plaintext));
            }

            return new CardPayload
            {
                Type = Get(obj, "type"),
                PrivateAccessCode = NormalizeCode(Get(obj, "privateAccessCode")),
                OfficialAccessCode = NormalizeCode(Get(obj, "officialAccessCode")),
                Idm = NormalizeHex(Get(obj, "idm")),
                AimeId = FlatJson.UInt(obj, "aimeId", "userId", "accountId"),
                Encrypted = encrypted
            };
        }

        private static string Get(System.Collections.Generic.Dictionary<string, string> obj, string key)
        {
            string value;
            return obj.TryGetValue(key, out value) ? value : "";
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
