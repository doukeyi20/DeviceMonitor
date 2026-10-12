using System;
using System.Security.Cryptography;

namespace DeviceMonitor.Core.Security
{

    /// <summary>
    /// 保存密码前生成随机盐和 PBKDF2 哈希；验证时用同样的盐、次数重新计算，再安全比较。
    /// 这里只处理密码，不连数据库。第一遍只需知道 Hash 生成保存信息，Verify 返回是否匹配。
    /// </summary>
    public static class PasswordHasher
    {
        private const int SaltSize = 16;
        private const int HashSize = 32;
        private const int Iterations = 600_000;
        private const int MaxVerificationIterations = 2_000_000;

        /// <summary>
        /// UserStore 创建账户、修改密码、重置密码时调用。返回盐、哈希和迭代次数，保存这些而不是明文。
        /// </summary>
        public static (byte[] Salt, byte[] Hash, int Iterations)
            Hash(string password)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(password);

            // 每次创建密码记录都生成新的随机盐。
            byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);

            // PBKDF2 多次计算，生成用于保存和验证的哈希值。
            byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                Iterations,
                HashAlgorithmName.SHA256,
                HashSize);

            // 一次返回建表时预留的三个密码字段。
            return (salt, hash, Iterations);
        }

        /// <summary>
        /// UserStore 登录或修改自己的密码时调用。根据原来保存的盐和次数计算输入密码的哈希，再比较是否相同。
        /// </summary>
        public static bool Verify(
            string password,
            byte[] salt,
            byte[] storedHash,
            int iterations)
        {
            // 数据不完整时验证失败，也避免异常次数让计算耗时过长。
            if (string.IsNullOrWhiteSpace(password) ||
                salt is null || salt.Length != SaltSize ||
                storedHash is null || storedHash.Length != HashSize ||
                iterations <= 0 || iterations > MaxVerificationIterations)
            {
                return false;
            }

            // 使用保存时的盐和次数，把本次输入的密码再计算一次。
            byte[] currentHash = Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                HashSize);

            return CryptographicOperations.FixedTimeEquals(currentHash, storedHash);
        }
    }
}
