using System;

namespace NFCAiME.AimeIO.Mod
{
    internal static class Hex
    {
        private static readonly char[] Lower = "0123456789abcdef".ToCharArray();

        public static byte[] ToBytes(string hex)
        {
            hex = (hex ?? "").Trim().Replace(":", "").Replace(" ", "");
            if (hex.Length % 2 != 0)
            {
                throw new FormatException("hex length must be even");
            }

            var bytes = new byte[hex.Length / 2];
            for (var i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }

            return bytes;
        }

        public static string FromBytes(byte[] bytes)
        {
            if (bytes == null)
            {
                return "";
            }

            return FromBytes(bytes, 0, bytes.Length);
        }

        public static string FromBytes(byte[] bytes, int offset, int count)
        {
            if (bytes == null || offset < 0 || count <= 0 || offset + count > bytes.Length)
            {
                return "";
            }

            var chars = new char[count * 2];
            for (var i = 0; i < count; i++)
            {
                var value = bytes[offset + i];
                chars[i * 2] = Lower[value >> 4];
                chars[i * 2 + 1] = Lower[value & 0x0f];
            }

            return new string(chars);
        }
    }
}
