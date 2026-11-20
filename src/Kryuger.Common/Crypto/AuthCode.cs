using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Kryuger.Common.Crypto
{
    public static class AuthCode
    {
        public const string DefaultKey = "yundoudou";
        private const long DefaultTtl = 3600;

        public static string Md5(string input)
        {
            return Md5(Encoding.UTF8.GetBytes(input));
        }

        public static string Md5(byte[] input)
        {
            using var md5 = System.Security.Cryptography.MD5.Create();
            byte[] hash = md5.ComputeHash(input);
            var sb = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        public static byte[] TransformRc4(byte[] data, string key)
        {
            byte[] pwd = Encoding.UTF8.GetBytes(key);
            int[] box = new int[256];
            for (int i = 0; i < 256; i++)
                box[i] = i;

            int j = 0;
            for (int i = 0; i < 256; i++)
            {
                j = (j + box[i] + pwd[i % pwd.Length]) % 256;
                (box[i], box[j]) = (box[j], box[i]);
            }

            byte[] output = new byte[data.Length];
            int a = 0;
            j = 0;
            for (int i = 0; i < data.Length; i++)
            {
                a = (a + 1) % 256;
                j = (j + box[a]) % 256;
                (box[a], box[j]) = (box[j], box[a]);

                int k = box[(box[a] + box[j]) % 256];
                output[i] = (byte)(data[i] ^ k);
            }

            return output;
        }

        public static byte[] GzipCompress(byte[] data)
        {
            using var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionLevel.Optimal, true))
            {
                gz.Write(data, 0, data.Length);
            }
            return ms.ToArray();
        }

        public static byte[] GzipDecompress(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var gz = new GZipStream(ms, CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            gz.CopyTo(outMs);
            return outMs.ToArray();
        }

        public static byte[] EncodeRaw(string body, string key, long ttlSeconds = DefaultTtl)
        {
            return EncodeRaw(Encoding.UTF8.GetBytes(body), key, ttlSeconds);
        }

        public static byte[] EncodeRaw(byte[] bodyBytes, string key, long ttlSeconds = DefaultTtl)
        {
            string hash = Md5(key);
            string x = Md5(hash[..16]);
            string realKeyX = Md5(hash[16..]);
            string realKey = realKeyX + Md5(realKeyX);

            long expiryUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + ttlSeconds;
            string expiry = expiryUnix.ToString("D10");

            byte[] xBytes = Encoding.UTF8.GetBytes(x);
            byte[] macInput = new byte[bodyBytes.Length + xBytes.Length];
            Buffer.BlockCopy(bodyBytes, 0, macInput, 0, bodyBytes.Length);
            Buffer.BlockCopy(xBytes, 0, macInput, bodyBytes.Length, xBytes.Length);
            string mac = Md5(macInput)[..16];

            byte[] header = Encoding.ASCII.GetBytes(expiry + mac);
            byte[] packet = new byte[header.Length + bodyBytes.Length];
            Buffer.BlockCopy(header, 0, packet, 0, header.Length);
            Buffer.BlockCopy(bodyBytes, 0, packet, header.Length, bodyBytes.Length);
            return TransformRc4(packet, realKey);
        }

        public static string Encode(string body, string key, bool compress = false, long ttlSeconds = DefaultTtl)
        {
            if (compress)
            {
                byte[] compressed = GzipCompress(Encoding.UTF8.GetBytes(body));
                byte[] enc = EncodeRaw(compressed, key, ttlSeconds);
                return "#" + Convert.ToBase64String(enc);
            }
            else
            {
                byte[] enc = EncodeRaw(Encoding.UTF8.GetBytes(body), key, ttlSeconds);
                return Convert.ToBase64String(enc);
            }
        }

        public static string Decode(string data, string key = DefaultKey)
        {
            if (string.IsNullOrEmpty(data))
                return string.Empty;

            if (string.IsNullOrEmpty(key))
                key = DefaultKey;

            data = data.Trim('\n', '\r', ' ');
            bool compressed = false;

            if (data.StartsWith("#"))
            {
                compressed = true;
                data = data[1..];
            }
            else
            {
                data = Uri.UnescapeDataString(data);
            }

            // Adjust base64 padding if needed
            int pad = data.Length % 4;
            if (pad > 0)
                data += new string('=', 4 - pad);

            byte[] enc = Convert.FromBase64String(data);

            string hash = Md5(key);
            string realKeyX = Md5(hash[16..]);
            string realKey = realKeyX + Md5(realKeyX);

            byte[] decrypted = TransformRc4(enc, realKey);
            if (decrypted.Length < 26)
                throw new InvalidOperationException("Decrypted packet too short");

            // Expiry is first 10 bytes, mac is next 16 bytes
            byte[] bodyBytes = decrypted[26..];

            if (compressed)
            {
                byte[] decompressed = GzipDecompress(bodyBytes);
                return Encoding.UTF8.GetString(decompressed);
            }

            return Encoding.UTF8.GetString(bodyBytes);
        }
    }
}

