using System;
using System.Security.Cryptography;

namespace MediaMonitor.Protocol.New.Codec
{
    /// <summary>
    /// 资源身份摘要：SHA-256 截断到 64 bit（取前 8 字节，按大端解释为 ulong）。
    ///
    /// 只用于“资源是否同一份”的缓存判断，不用于安全，也不替代资源 CRC32：
    /// * 资源 BEGIN/DATA/END 仍用 CRC32 校验传输完整性；
    /// * SHA64 参与 REQUEST 的 KNOWN_SHA64 与 BEGIN 的 SHA64 字段。
    /// </summary>
    public static class ResourceSha64
    {
        /// <summary>SHA-256 前 8 字节（大端）→ ulong。</summary>
        public static ulong Compute(ReadOnlySpan<byte> data)
        {
            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(data, hash);
            return NpReader.ReadU64(hash);
        }
    }
}