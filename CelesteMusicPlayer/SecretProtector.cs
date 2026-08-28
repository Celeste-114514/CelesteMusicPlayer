using System;
using System.Security.Cryptography;
using System.Text;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 凭据保护：用 Windows DPAPI（<see cref="DataProtectionScope.CurrentUser"/>，绑定当前用户+本机）
    /// 对本地存储的登录 Cookie 做透明加解密。明文永不入盘——磁盘上的值是带 "dpapi:" 前缀的密文；
    /// 旧版无前缀明文在读取时按明文兼容（下次保存时自动加密迁移）。
    /// </summary>
    internal static class SecretProtector
    {
        private const string Prefix = "dpapi:";
        private static readonly Encoding Enc = Encoding.UTF8;

        /// <summary>加密明文；空值原样返回；加密异常时降级返回明文（保证可用，仅丧失保密）。</summary>
        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain))
            {
                return plain;
            }

            try
            {
                byte[] cipher = ProtectedData.Protect(Enc.GetBytes(plain), null, DataProtectionScope.CurrentUser);
                return Prefix + Convert.ToBase64String(cipher);
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("SecretProtector.Protect", ex);
                return plain;
            }
        }

        /// <summary>解密；空值原样返回；无前缀视为旧版明文原样返回；解密失败（跨用户/损坏）视为无效凭据返回空。</summary>
        public static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored))
            {
                return stored;
            }

            if (!stored.StartsWith(Prefix, StringComparison.Ordinal))
            {
                return stored; // 旧版明文兼容
            }

            try
            {
                byte[] cipher = Convert.FromBase64String(stored.Substring(Prefix.Length));
                byte[] plain = ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser);
                return Enc.GetString(plain);
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("SecretProtector.Unprotect", ex);
                return string.Empty;
            }
        }
    }
}
