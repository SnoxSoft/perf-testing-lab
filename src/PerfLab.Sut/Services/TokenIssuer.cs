using System.Security.Cryptography;
using System.Text;
using PerfLab.Sut.Configuration;

namespace PerfLab.Sut.Services;

/// <summary>
/// Issues and validates bearer tokens.
///
/// Issuance is deliberately expensive and validation deliberately cheap, which
/// is the shape of every real authentication system: a password hash is costly
/// on purpose, while verifying a signature is not.
///
/// That asymmetry is the whole reason this exists in a load testing repository.
/// A test that authenticates on every iteration spends most of its budget
/// measuring the login path, and reports the endpoint under test as far slower
/// than it is. The cost here is large enough to make that mistake visible in
/// the numbers rather than merely arguable.
///
/// Tokens are stateless: the payload carries its own expiry and signature, so
/// there is no server-side store to grow. That is a deliberate contrast with the
/// unbounded report cache — not every cache has to leak.
/// </summary>
public sealed class TokenIssuer(PathologyOptions options)
{
    // Fixed key. This is a load testing target, not a security boundary, and a
    // random per-start key would make tokens useless across a container restart
    // mid-run.
    private static readonly byte[] SigningKey =
        Encoding.UTF8.GetBytes("perflab-signing-key-not-a-secret");

    public async Task<(string Token, int ExpiresInSeconds)> IssueAsync(string user)
    {
        // Stands in for password hashing. Real work, not a timer, so it costs
        // actual CPU the way bcrypt or Argon2 would.
        await Task.Delay(options.TokenIssuanceCost);

        DateTimeOffset expiresAt = DateTimeOffset.UtcNow + options.TokenLifetime;
        string payload = $"{user}|{expiresAt.ToUnixTimeMilliseconds()}";
        string signature = Sign(payload);

        string token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{payload}|{signature}"));
        return (token, (int)options.TokenLifetime.TotalSeconds);
    }

    /// <summary>
    /// Returns the authenticated user, or null when the token is missing,
    /// malformed, expired or badly signed.
    ///
    /// Expiry is checked before the signature deliberately: an expired token is
    /// the common case in a long run and should not cost a hash to reject.
    /// </summary>
    public string? Validate(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(token));
        }
        catch (FormatException)
        {
            return null;
        }

        string[] parts = decoded.Split('|');
        if (parts.Length != 3)
        {
            return null;
        }

        if (!long.TryParse(parts[1], out long expiresAtUnixMs))
        {
            return null;
        }

        if (DateTimeOffset.FromUnixTimeMilliseconds(expiresAtUnixMs) <= DateTimeOffset.UtcNow)
        {
            return null;
        }

        string expected = Sign($"{parts[0]}|{parts[1]}");

        // Fixed-time comparison. Habit rather than necessity here, but a
        // signature comparison written the lazy way in a sample gets copied.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(parts[2]))
            ? parts[0]
            : null;
    }

    private static string Sign(string payload) =>
        Convert.ToBase64String(
            HMACSHA256.HashData(SigningKey, Encoding.UTF8.GetBytes(payload)));
}
