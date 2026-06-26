using System;

namespace NFCAiME.AimeIO.Mod
{
    internal static class CardCache
    {
        private static readonly object Gate = new object();
        private static CardPayload _payload;
        private static DateTime _expiresAt;
        private static ModConfig _config;

        public static void Configure(ModConfig config)
        {
            _config = config;
        }

        public static void Store(CardPayload payload)
        {
            if (payload == null)
            {
                return;
            }

            lock (Gate)
            {
                _payload = payload;
                _expiresAt = DateTime.UtcNow.AddMilliseconds((_config == null ? 5000 : _config.CardTtlMs));
            }
        }

        public static bool HasValidCard()
        {
            lock (Gate)
            {
                return _payload != null && DateTime.UtcNow <= _expiresAt;
            }
        }

        public static string GetAccessCode()
        {
            lock (Gate)
            {
                if (_payload == null || DateTime.UtcNow > _expiresAt)
                {
                    return "";
                }

                var preferred = _config == null ? "private" : _config.PreferredAccessCode;
                if (preferred == "official")
                {
                    return FirstNonEmpty(_payload.OfficialAccessCode, _payload.PrivateAccessCode);
                }

                return FirstNonEmpty(_payload.PrivateAccessCode, _payload.OfficialAccessCode);
            }
        }

        public static string GetOfflineId()
        {
            lock (Gate)
            {
                if (_payload == null || DateTime.UtcNow > _expiresAt)
                {
                    return "";
                }

                return FirstNonEmpty(GetAccessCode(), _payload.Idm);
            }
        }

        private static string FirstNonEmpty(string first, string second)
        {
            return !string.IsNullOrWhiteSpace(first) ? first.Trim() : ((second ?? "").Trim());
        }
    }
}
