using System;

namespace NFCAiME.AimeIO.Mod
{
    internal static class Hex
    {
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
    }
}
