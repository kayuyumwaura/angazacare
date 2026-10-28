using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;

namespace AngazaCare.Services;

/// <summary>
/// Mothers' phone numbers are encrypted at rest (ASP.NET Data Protection) and looked up
/// by a keyed HMAC, so a database dump alone does not reveal who is enrolled.
/// </summary>
public sealed partial class PhoneProtector
{
    private readonly IDataProtector _protector;
    private readonly byte[] _hashKey;

    public PhoneProtector(IDataProtectionProvider provider, IConfiguration config)
    {
        _protector = provider.CreateProtector("AngazaCare.MotherPhone.v1");
        var key = config["Security:PhoneHashKey"];
        if (string.IsNullOrWhiteSpace(key) || key.Length < 32)
            throw new InvalidOperationException("Security:PhoneHashKey must be set to a random string of at least 32 characters.");
        _hashKey = Encoding.UTF8.GetBytes(key);
    }

    public string Encrypt(string e164) => _protector.Protect(e164);
    public string Decrypt(string cipher) => _protector.Unprotect(cipher);

    public string Hash(string e164)
    {
        using var hmac = new HMACSHA256(_hashKey);
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(e164)));
    }

    /// <summary>Normalises Kenyan numbers (07.., 01.., 2547.., +2547..) to E.164. Returns null if invalid.</summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var digits = NonDigits().Replace(raw, "");
        if (digits.StartsWith("254") && digits.Length == 12) return "+" + digits;
        if (digits.StartsWith('0') && digits.Length == 10) return "+254" + digits[1..];
        if ((digits.StartsWith('7') || digits.StartsWith('1')) && digits.Length == 9) return "+254" + digits;
        return null;
    }

    [GeneratedRegex(@"\D")]
    private static partial Regex NonDigits();
}

/// <summary>PBKDF2-SHA256 password hashing with no external dependencies.</summary>
public static class PasswordHasher
{
    private const int Iterations = 210_000;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256") return false;
        var iterations = int.Parse(parts[1]);
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
