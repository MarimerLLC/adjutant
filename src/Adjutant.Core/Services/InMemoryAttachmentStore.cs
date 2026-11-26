using System.Security.Cryptography;
using Adjutant.Core.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Adjutant.Core.Services;

/// <summary>
/// Single-process attachment store backed by a dictionary. Suitable for the
/// current single-pod deployment; switch to a distributed store if the
/// HttpServer is ever scaled out.
/// </summary>
public sealed class InMemoryAttachmentStore : IAttachmentStore
{
    private readonly Dictionary<string, StoredAttachment> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<(string OrganizationId, string Owner), long> _ownerBytes = new();
    private readonly Lock _gate = new();
    private readonly AttachmentStoreOptions _options;
    private readonly ILogger<InMemoryAttachmentStore> _logger;
    private readonly TimeProvider _time;
    private readonly IMcpCallerContextAccessor? _callerContext;
    private readonly IAccountRegistry? _accounts;
    private long _totalBytes;

    public InMemoryAttachmentStore(
        IOptions<AttachmentStoreOptions> options,
        ILogger<InMemoryAttachmentStore> logger,
        TimeProvider? timeProvider = null,
        IMcpCallerContextAccessor? callerContext = null,
        IAccountRegistry? accounts = null)
    {
        _options = options.Value;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        _callerContext = callerContext;
        _accounts = accounts;
    }

    private McpCallerContext Caller => _callerContext?.Current ?? McpCallerContext.Unrestricted;

    public StoredAttachment? Put(string name, string? contentType, byte[] bytes, string? sourceAccountId = null)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length > _options.MaxBytesPerAttachment)
        {
            return null;
        }

        var caller = Caller;
        var id = NewId();
        var entry = new StoredAttachment
        {
            Id = id,
            Owner = AdminEmailAllowList.Normalize(caller.UserId),
            OrganizationId = caller.OrganizationId,
            Name = string.IsNullOrWhiteSpace(name) ? "attachment" : name,
            ContentType = contentType,
            SourceAccountId = sourceAccountId,
            Bytes = bytes,
            ExpiresAt = _time.GetUtcNow().Add(_options.Ttl),
        };

        lock (_gate)
        {
            EvictExpiredCore();
            if (_totalBytes + bytes.Length > _options.MaxTotalBytes)
            {
                return null;
            }
            if (entry.Owner is { } owner && bytes.Length > 0)
            {
                var key = (entry.OrganizationId, owner);
                var ownerBytes = _ownerBytes.GetValueOrDefault(key) + bytes.Length;
                if (ownerBytes > _options.MaxBytesPerOwner)
                    return null;
                _ownerBytes[key] = ownerBytes;
            }
            _entries[id] = entry;
            _totalBytes += bytes.Length;
        }

        _logger.LogDebug("Stored attachment {Id} ({Name}, {Size} bytes), expires {ExpiresAt:O}",
            id, entry.Name, bytes.Length, entry.ExpiresAt);
        return entry;
    }

    public StoredAttachment? TryConsume(string attachmentId)
    {
        if (string.IsNullOrEmpty(attachmentId))
            return null;

        lock (_gate)
        {
            if (!_entries.TryGetValue(attachmentId, out var entry) || !CanAccess(entry))
                return null;

            Remove(entry);

            if (entry.ExpiresAt <= _time.GetUtcNow())
            {
                _logger.LogDebug("Attachment {Id} was past expiry on consume", attachmentId);
                return null;
            }

            return entry;
        }
    }

    public StoredAttachment? TryRead(string attachmentId)
    {
        if (string.IsNullOrEmpty(attachmentId))
            return null;

        lock (_gate)
        {
            if (!_entries.TryGetValue(attachmentId, out var entry) || !CanAccess(entry))
                return null;

            if (entry.ExpiresAt <= _time.GetUtcNow())
            {
                // Lazily clean up the expired entry while we hold the lock.
                Remove(entry);
                return null;
            }

            return entry;
        }
    }

    public bool TryDelete(string attachmentId)
    {
        if (string.IsNullOrEmpty(attachmentId))
            return false;

        lock (_gate)
        {
            if (!_entries.TryGetValue(attachmentId, out var entry) || !CanAccess(entry))
                return false;
            Remove(entry);
            return true;
        }
    }

    private bool CanAccess(StoredAttachment entry)
    {
        var caller = Caller;
        if (caller.IsUnrestricted)
            return true;
        if (!caller.Owns(entry.Owner, entry.OrganizationId))
            return false;

        return entry.SourceAccountId is not { } sourceAccountId ||
            (caller.AllowsAccount(sourceAccountId) &&
             (_accounts is null || _accounts.GetEnabledAccounts().Any(account =>
                 string.Equals(account.Id, sourceAccountId, StringComparison.OrdinalIgnoreCase) &&
                 caller.CanAccessAccount(account))));
    }

    // Caller must hold the cache lock.
    private void Remove(StoredAttachment entry)
    {
        _entries.Remove(entry.Id);
        _totalBytes -= entry.Bytes.Length;
        if (entry.Owner is { } owner && entry.Bytes.Length > 0)
        {
            var key = (entry.OrganizationId, owner);
            var bytes = _ownerBytes[key] - entry.Bytes.Length;
            if (bytes == 0)
                _ownerBytes.Remove(key);
            else
                _ownerBytes[key] = bytes;
        }
    }

    public void EvictExpired()
    {
        lock (_gate)
        {
            EvictExpiredCore();
        }
    }

    private void EvictExpiredCore()
    {
        var now = _time.GetUtcNow();
        List<StoredAttachment>? toRemove = null;
        foreach (var entry in _entries.Values)
        {
            if (entry.ExpiresAt <= now)
            {
                (toRemove ??= []).Add(entry);
            }
        }
        if (toRemove == null) return;
        foreach (var entry in toRemove)
            Remove(entry);
        _logger.LogDebug("Evicted {Count} expired attachments", toRemove.Count);
    }

    // 16 random bytes -> 22-char base64url id.
    private static string NewId()
    {
        Span<byte> buf = stackalloc byte[16];
        RandomNumberGenerator.Fill(buf);
        return Convert.ToBase64String(buf)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
