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

        public static bool TryGetPayload(out CardPayload payload)
        {
            lock (Gate)
            {
                if (_payload != null && DateTime.UtcNow <= _expiresAt)
                {
                    payload = _payload;
                    return true;
                }

                payload = null;
                return false;
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

                return GetAccessCode(_payload);
            }
        }

        public static string GetAccessCode(CardPayload payload)
        {
            var preferred = _config == null ? "private" : _config.PreferredAccessCode;
            if (preferred == "official")
            {
                return FirstNonEmpty(payload.OfficialAccessCode, payload.PrivateAccessCode);
            }

            return FirstNonEmpty(payload.PrivateAccessCode, payload.OfficialAccessCode);
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
