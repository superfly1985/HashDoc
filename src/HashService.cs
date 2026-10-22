using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace HashDoc
{
    /// <summary>文件哈希计算（SHA-256，1MB 分块流式读取，支持取消与进度）。</summary>
    public static class HashService
    {
        public static string Sha256(string path, Action<long, long> progress = null, CancellationToken ct = default(CancellationToken))
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.SequentialScan))
            using (var sha = SHA256.Create())
            {
                long total = fs.Length;
                long read = 0;
                var buf = new byte[1024 * 1024];
                int n;
                while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    sha.TransformBlock(buf, 0, n, null, 0);
                    read += n;
                    if (progress != null) progress(read, total);
                }
                sha.TransformFinalBlock(buf, 0, 0);
                return Hex(sha.Hash);
            }
        }

        public static string Hex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        /// <summary>列表展示用的短哈希。</summary>
        public static string Short(string hash)
        {
            return hash.Length <= 16 ? hash : hash.Substring(0, 16) + "…";
        }
    }
}
