using System;
using System.Security.Cryptography;

namespace DeviceMonitor.Core.Security
{

    /// <summary>
    /// Hash 生成密码的保存信息，Verify 检查输入密码是否正确，不保存明文密码。
    /// </summary>
    public static class PasswordHasher
    {
        private const int SaltSize = 16;
        private const int HashSize = 32;
        private const int Iterations = 600_000;
        private const int MaxVerificationIterations = 2_000_000;

        /// <summary>
        /// 由 UserStore 创建账户或修改密码的方法调用。
        /// 生成随机盐、密码哈希和计算次数，供数据库保存。
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

            // 把这三样一起返回，不返回明文密码。
            return (salt, hash, Iterations);
        }

        /// <summary>
        /// 由 UserStore.CheckLogin 和 TryChangePassword 检查密码时调用。
        /// 用原来的盐和次数计算输入密码，再比较哈希是否一致。
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
