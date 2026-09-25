using System;
using System.Security.Cryptography;

namespace DeviceMonitor.Core.Security
{
    // 生成密码哈希，并验证登录时输入的密码。
    public static class PasswordHasher
    {
        private const int SaltSize = 16;
        private const int HashSize = 32;
        private const int Iterations = 600_000;
        private const int MaxVerificationIterations = 2_000_000;

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
