using System.Security.Cryptography;
using System.Text;

namespace ExLlamaSharp.Server.Auth;

/// <summary>
/// Password hashing for UI login / setup / admin user APIs.
/// Format: <c>{saltHex}:{hashHex}</c> (SHA-256). Also verifies legacy plain SHA-256 hex and plaintext seed.
/// </summary>
public static class PasswordHasher
{
    public static string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Convert.ToHexString(salt) + password));
        return $"{Convert.ToHexString(salt)}:{Convert.ToHexString(hash)}";
    }

    /// <summary>Deterministic hash for first-run seed (same password always yields same stored value).</summary>
    public static string HashDeterministic(string password, ReadOnlySpan<byte> salt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Convert.ToHexString(salt.ToArray()) + password));
        return $"{Convert.ToHexString(salt)}:{Convert.ToHexString(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(stored))
        {
            return false;
        }

        // Current format salt:hash
        var colon = stored.IndexOf(':');
        if (colon > 0)
        {
            var saltHex = stored[..colon];
            var expected = stored[(colon + 1)..];
            try
            {
                var salt = Convert.FromHexString(saltHex);
                var actual = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(Convert.ToHexString(salt) + password)));
                return CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(actual),
                    Encoding.UTF8.GetBytes(expected));
            }
            catch
            {
                return false;
            }
        }

        // Legacy: raw SHA-256 hex of password (Setup / old Login)
        var legacy = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password)));
        if (string.Equals(legacy, stored, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Legacy seed plaintext
        return string.Equals(password, stored, StringComparison.Ordinal);
    }
}
