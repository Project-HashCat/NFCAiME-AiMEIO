using System;
using System.Security.Cryptography;

namespace NFCAiME.AimeIO.Mod
{
    internal static class AesGcmDecryptor
    {
        private const string KeyHex = "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f";

        public static byte[] Decrypt(byte[] nonce, byte[] ciphertext, byte[] tag)
        {
            if (nonce == null || nonce.Length != 12)
            {
                throw new ArgumentException("nonce must be 12 bytes");
            }

            if (tag == null || tag.Length != 16)
            {
                throw new ArgumentException("tag must be 16 bytes");
            }

            var key = Hex.ToBytes(KeyHex);
            var h = EncryptBlock(key, new byte[16]);
            var j0 = new byte[16];
            Buffer.BlockCopy(nonce, 0, j0, 0, 12);
            j0[15] = 1;

            var plain = new byte[ciphertext.Length];
            var counter = (byte[])j0.Clone();
            for (var offset = 0; offset < ciphertext.Length; offset += 16)
            {
                Increment32(counter);
                var mask = EncryptBlock(key, counter);
                var len = Math.Min(16, ciphertext.Length - offset);
                for (var i = 0; i < len; i++)
                {
                    plain[offset + i] = (byte)(ciphertext[offset + i] ^ mask[i]);
                }
            }

            var expectedTag = ComputeTag(key, h, j0, ciphertext);
            if (!FixedEquals(tag, expectedTag))
            {
                throw new CryptographicException("AES-GCM tag mismatch");
            }

            return plain;
        }

        private static byte[] ComputeTag(byte[] key, byte[] h, byte[] j0, byte[] ciphertext)
        {
            var y = new byte[16];
            GHashBlocks(h, y, ciphertext);

            var lengthBlock = new byte[16];
            WriteUInt64BigEndian(lengthBlock, 8, (ulong)ciphertext.Length * 8UL);
            XorInPlace(y, lengthBlock);
            MultiplyInPlace(y, h);

            var s = EncryptBlock(key, j0);
            XorInPlace(s, y);
            return s;
        }

        private static void GHashBlocks(byte[] h, byte[] y, byte[] data)
        {
            for (var offset = 0; offset < data.Length; offset += 16)
            {
                var block = new byte[16];
                Buffer.BlockCopy(data, offset, block, 0, Math.Min(16, data.Length - offset));
                XorInPlace(y, block);
                MultiplyInPlace(y, h);
            }
        }

        private static byte[] EncryptBlock(byte[] key, byte[] block)
        {
            using (var aes = new AesManaged())
            {
                aes.Key = key;
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.None;
                using (var encryptor = aes.CreateEncryptor())
                {
                    return encryptor.TransformFinalBlock(block, 0, 16);
                }
            }
        }

        private static void MultiplyInPlace(byte[] x, byte[] y)
        {
            var z = new byte[16];
            var v = (byte[])y.Clone();

            for (var i = 0; i < 128; i++)
            {
                if ((x[i / 8] & (0x80 >> (i % 8))) != 0)
                {
                    XorInPlace(z, v);
                }

                var lsb = (v[15] & 1) != 0;
                ShiftRightOne(v);
                if (lsb)
                {
                    v[0] ^= 0xe1;
                }
            }

            Buffer.BlockCopy(z, 0, x, 0, 16);
        }

        private static void ShiftRightOne(byte[] value)
        {
            var carry = 0;
            for (var i = 0; i < value.Length; i++)
            {
                var nextCarry = value[i] & 1;
                value[i] = (byte)((value[i] >> 1) | (carry << 7));
                carry = nextCarry;
            }
        }

        private static void Increment32(byte[] counter)
        {
            for (var i = 15; i >= 12; i--)
            {
                counter[i]++;
                if (counter[i] != 0)
                {
                    return;
                }
            }
        }

        private static void XorInPlace(byte[] left, byte[] right)
        {
            for (var i = 0; i < left.Length; i++)
            {
                left[i] ^= right[i];
            }
        }

        private static void WriteUInt64BigEndian(byte[] dest, int offset, ulong value)
        {
            for (var i = 7; i >= 0; i--)
            {
                dest[offset + i] = (byte)value;
                value >>= 8;
            }
        }

        private static bool FixedEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
            {
                return false;
            }

            var diff = 0;
            for (var i = 0; i < a.Length; i++)
            {
                diff |= a[i] ^ b[i];
            }

            return diff == 0;
        }
    }
}
