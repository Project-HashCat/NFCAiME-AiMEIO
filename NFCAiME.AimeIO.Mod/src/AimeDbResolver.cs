using System;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace NFCAiME.AimeIO.Mod
{
    internal static class AimeDbResolver
    {
        private static readonly byte[] AesKey = Encoding.ASCII.GetBytes("Copyright(C)SEGA");

        public static bool TryResolve(CardPayload payload, ModConfig config, out uint aimeId, out string accessCode, out string reason)
        {
            aimeId = 0;
            accessCode = CardCache.GetAccessCode(payload);
            reason = "";

            if (payload == null || config == null || !config.AimeDbResolve)
            {
                reason = "disabled";
                return false;
            }

            if (string.IsNullOrWhiteSpace(config.AimeDbHost))
            {
                reason = "aimeDbHost is empty";
                return false;
            }

            if (IsOfficialAimeDb(config.AimeDbHost))
            {
                reason = "official AimeDB requires CMD17; skip CMD15";
                return false;
            }

            try
            {
                if (IsAccessCode(accessCode) && TryGetUserIdByAccessCode(config, accessCode, out aimeId))
                {
                    return true;
                }

                if (!string.IsNullOrWhiteSpace(payload.Idm) &&
                    TryConvertIdm(config, payload.Idm, out accessCode) &&
                    TryGetUserIdByAccessCode(config, accessCode, out aimeId))
                {
                    return true;
                }

                reason = "AimeDB returned no userId";
                return false;
            }
            catch (Exception ex)
            {
                reason = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        private static bool TryGetUserIdByAccessCode(ModConfig config, string accessCode, out uint aimeId)
        {
            aimeId = 0;
            if (!IsAccessCode(accessCode))
            {
                return false;
            }

            var response = SendCommand(config, "0f00", accessCode + "000201020304");
            return TryParseUserId(response, out aimeId);
        }

        private static bool TryConvertIdm(ModConfig config, string idm, out string accessCode)
        {
            accessCode = "";
            idm = NormalizeHex(idm);
            if (idm.Length != 16)
            {
                return false;
            }

            var response = SendCommand(config, "0100", idm + "00F1000000014300");
            if (response.Length >= 0x2e)
            {
                accessCode = Hex.FromBytes(response, 0x24, 10);
            }
            else if (response.Length >= 12)
            {
                accessCode = Hex.FromBytes(response, response.Length - 12, 10);
            }

            return IsAccessCode(accessCode);
        }

        private static byte[] SendCommand(ModConfig config, string commandIdHex, string bodyHex)
        {
            var header = new MemoryStream();
            WriteHex(header, "3ea18730");
            WriteHex(header, commandIdHex);
            WriteHex(header, "30000000");
            WriteFixedAscii(header, config.AimeDbGameId, 6, false);
            WriteHex(header, config.AimeDbStoreIdHex);
            WriteFixedAscii(header, config.AimeDbKeychip, 12, true);
            WriteHex(header, bodyHex);

            var request = Encrypt(PadZero(header.ToArray()));
            using (var client = new TcpClient())
            {
                var ar = client.BeginConnect(config.AimeDbHost, config.AimeDbPort, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(config.AimeDbTimeoutMs))
                {
                    client.Close();
                    throw new TimeoutException("connect timeout");
                }

                client.EndConnect(ar);
                client.SendTimeout = config.AimeDbTimeoutMs;
                client.ReceiveTimeout = config.AimeDbTimeoutMs;

                using (var stream = client.GetStream())
                {
                    stream.Write(request, 0, request.Length);
                    var buffer = new byte[1024];
                    var read = stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        return new byte[0];
                    }

                    var encrypted = new byte[read];
                    Buffer.BlockCopy(buffer, 0, encrypted, 0, read);
                    return Decrypt(encrypted);
                }
            }
        }

        private static bool TryParseUserId(byte[] response, out uint aimeId)
        {
            aimeId = 0;
            if (response == null || response.Length < 0x24)
            {
                return false;
            }

            var direct = BitConverter.ToUInt32(response, 0x20);
            if (direct != 0 && direct != uint.MaxValue)
            {
                aimeId = direct;
                return true;
            }

            var hex = Hex.FromBytes(response);
            var match = Regex.Match(hex, "[0-9a-f]{64}([0-9a-f]{6})");
            if (!match.Success)
            {
                return false;
            }

            var value = match.Groups[1].Value;
            var parsed = Convert.ToUInt32(value.Substring(4, 2) + value.Substring(2, 2) + value.Substring(0, 2), 16);
            if (parsed == 0 || parsed == 0xffffff)
            {
                return false;
            }

            aimeId = parsed;
            return true;
        }

        private static byte[] Encrypt(byte[] data)
        {
            using (var aes = new AesManaged())
            {
                aes.Key = AesKey;
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.None;
                using (var encryptor = aes.CreateEncryptor())
                {
                    return encryptor.TransformFinalBlock(data, 0, data.Length);
                }
            }
        }

        private static byte[] Decrypt(byte[] data)
        {
            if (data.Length % 16 != 0)
            {
                return new byte[0];
            }

            using (var aes = new AesManaged())
            {
                aes.Key = AesKey;
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.None;
                using (var decryptor = aes.CreateDecryptor())
                {
                    return decryptor.TransformFinalBlock(data, 0, data.Length);
                }
            }
        }

        private static byte[] PadZero(byte[] data)
        {
            if (data.Length % 16 == 0)
            {
                return data;
            }

            var padded = new byte[data.Length + 16 - data.Length % 16];
            Buffer.BlockCopy(data, 0, padded, 0, data.Length);
            return padded;
        }

        private static void WriteHex(Stream stream, string hex)
        {
            var bytes = Hex.ToBytes(hex);
            stream.Write(bytes, 0, bytes.Length);
        }

        private static void WriteFixedAscii(Stream stream, string value, int length, bool stripNonAlphaNumeric)
        {
            value = value ?? "";
            if (stripNonAlphaNumeric)
            {
                value = Regex.Replace(value, "[^0-9A-Za-z]", "");
            }

            value = value.ToUpperInvariant();
            var bytes = Encoding.ASCII.GetBytes(value);
            var fixedBytes = new byte[length];
            Buffer.BlockCopy(bytes, 0, fixedBytes, 0, Math.Min(length, bytes.Length));
            stream.Write(fixedBytes, 0, fixedBytes.Length);
        }

        private static bool IsOfficialAimeDb(string host)
        {
            host = (host ?? "").Trim().ToLowerInvariant();
            return host == "aime.naominet.jp";
        }

        private static bool IsAccessCode(string value)
        {
            return Regex.IsMatch(value ?? "", "^[0-9]{20}$");
        }

        private static string NormalizeHex(string value)
        {
            return Regex.Replace(value ?? "", "[^0-9a-fA-F]", "").ToLowerInvariant();
        }
    }
}
