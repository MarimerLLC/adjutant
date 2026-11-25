using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Adjutant.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace Adjutant.Core.Security;

/// <summary>
/// <see cref="IAdminTokenStore"/> backed by <c>{data}/admin-token.json</c>, which holds only the
/// SHA-256 of the generated token. The configured token is read on every check, so a change to
/// <c>Adjutant:AdminToken</c> in appsettings.json applies without a restart.
/// </summary>
public sealed class AdminTokenStore : IAdminTokenStore
{
    /// <summary>Name of the file the generated token's hash is kept in, inside the data directory.</summary>
    public const string FileName = "admin-token.json";

    /// <summary>Prefix on every generated token, so a leaked string is recognizable as one.</summary>
    public const string SecretPrefix = "adjadm_";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly ILogger<AdminTokenStore> _logger;
    private readonly Func<string?> _configuredToken;
    private readonly string _filePath;
    private readonly Lock _gate = new();

    private byte[]? _storedHash;

    /// <param name="configuredToken">
    /// The operator's token, read on every call; null or blank means none is configured.
    /// </param>
    /// <param name="filePath">Overridable for tests; defaults to the shared data directory.</param>
    public AdminTokenStore(ILogger<AdminTokenStore> logger, Func<string?> configuredToken, string? filePath = null)
    {
        _logger = logger;
        _configuredToken = configuredToken;
        _filePath = filePath ?? Path.Combine(ConfigurationPaths.GetDataDirectory(), FileName);
        _storedHash = Load();
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_configuredToken());

    public bool HasToken
    {
        get
        {
            if (IsConfigured)
                return true;

            lock (_gate)
            {
                return _storedHash is not null;
            }
        }
    }

    public string? EnsureToken()
    {
        if (IsConfigured)
            return null;

        lock (_gate)
        {
            if (_storedHash is not null)
                return null;

            var secret = SecretPrefix + Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            var hash = ComputeHash(secret);
            _storedHash = hash;

            try
            {
                var directory = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                var record = new StoredToken(Convert.ToBase64String(hash), DateTimeOffset.UtcNow);
                File.WriteAllText(_filePath, JsonSerializer.Serialize(record, JsonOptions));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still protected: the hash is held in memory and the secret goes to the log. It
                // only lasts until the next start, which generates a new one.
                _logger.LogWarning(ex,
                    "Could not save the generated admin token to {Path}. It is valid until the server " +
                    "restarts; set ADJUTANT_ADMIN_TOKEN for one that lasts.", _filePath);
            }

            return secret;
        }
    }

    public bool Matches(string? presented)
    {
        if (string.IsNullOrEmpty(presented))
            return false;

        // Hashing both sides gives equal-length inputs, so the comparison leaks neither the
        // token's characters nor its length.
        var presentedHash = ComputeHash(presented);

        var configured = _configuredToken();
        if (!string.IsNullOrWhiteSpace(configured))
            return CryptographicOperations.FixedTimeEquals(presentedHash, ComputeHash(configured));

        lock (_gate)
        {
            return _storedHash is not null && CryptographicOperations.FixedTimeEquals(presentedHash, _storedHash);
        }
    }

    private byte[]? Load()
    {
        if (!File.Exists(_filePath))
            return null;

        try
        {
            var record = JsonSerializer.Deserialize<StoredToken>(File.ReadAllText(_filePath), JsonOptions);
            if (record?.Hash is { Length: > 0 } hash)
            {
                var bytes = Convert.FromBase64String(hash);
                if (bytes.Length == SHA256.HashSizeInBytes)
                    return bytes;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            _logger.LogWarning(ex, "Could not read the admin token file {Path}", _filePath);
        }

        // Unreadable or malformed: no stored token, so startup generates a new one rather than
        // leaving the console with a token nobody can match.
        _logger.LogWarning("Ignoring unusable admin token file {Path}; a new token will be generated.", _filePath);
        return null;
    }

    private static byte[] ComputeHash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record StoredToken(
        [property: JsonPropertyName("hash")] string Hash,
        [property: JsonPropertyName("createdUtc")] DateTimeOffset CreatedUtc);
}
