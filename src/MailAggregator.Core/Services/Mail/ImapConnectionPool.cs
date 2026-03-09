using System.Collections.Concurrent;
using MailAggregator.Core.Models;
using MailKit.Net.Imap;
using Serilog;

namespace MailAggregator.Core.Services.Mail;

public class ImapConnectionPool : IImapConnectionPool
{
    private const int MaxPoolSizePerAccount = 2;

    /// <summary>
    /// Interval for the background cleanup timer that removes stale/zombie connections.
    /// NAT and mobile networks can silently drop TCP connections, leaving them
    /// in a state where IsConnected is true but the connection is dead.
    /// </summary>
    internal static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<int, ConcurrentQueue<ImapClient>> _pool = new();
    private readonly ConcurrentDictionary<int, int> _poolCounts = new();
    private readonly IImapConnectionService _connectionService;
    private readonly ILogger _logger;
    private readonly Timer _cleanupTimer;
    private bool _disposed;

    public ImapConnectionPool(IImapConnectionService connectionService, ILogger logger)
    {
        _connectionService = connectionService ?? throw new ArgumentNullException(nameof(connectionService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Start background timer to periodically clean up stale connections
        _cleanupTimer = new Timer(_ => CleanupStaleConnections(), null, CleanupInterval, CleanupInterval);
    }

    public async Task<PooledImapConnection> GetConnectionAsync(Account account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        var queue = _pool.GetOrAdd(account.Id, _ => new ConcurrentQueue<ImapClient>());

        while (queue.TryDequeue(out var client))
        {
            _poolCounts.AddOrUpdate(account.Id, 0, (_, current) => Math.Max(0, current - 1));

            if (client.IsConnected && client.IsAuthenticated)
            {
                _logger.Debug("Reusing pooled IMAP connection for {Email}", account.EmailAddress);
                return new PooledImapConnection(client, c => ReturnToPool(account.Id, c));
            }

            // Stale connection
            DisposeClient(client);
        }

        // No pooled connection available, create a new one
        var newClient = await _connectionService.ConnectAsync(account, cancellationToken);
        return new PooledImapConnection(newClient, c => ReturnToPool(account.Id, c));
    }

    private void ReturnToPool(int accountId, ImapClient client)
    {
        if (_disposed || !client.IsConnected || !client.IsAuthenticated)
        {
            DisposeClient(client);
            return;
        }

        var queue = _pool.GetOrAdd(accountId, _ => new ConcurrentQueue<ImapClient>());

        // Use atomic increment to prevent the ConcurrentQueue.Count TOCTOU race
        // that could allow more connections than MaxPoolSizePerAccount.
        var newCount = _poolCounts.AddOrUpdate(accountId, 1, (_, current) => current + 1);
        if (newCount <= MaxPoolSizePerAccount)
        {
            queue.Enqueue(client);
        }
        else
        {
            // Over limit — decrement back and dispose
            _poolCounts.AddOrUpdate(accountId, 0, (_, current) => Math.Max(0, current - 1));
            DisposeClient(client);
        }
    }

    /// <summary>
    /// Removes stale connections from all account queues.
    /// A connection is stale if it is no longer connected or authenticated.
    /// </summary>
    internal void CleanupStaleConnections()
    {
        if (_disposed) return;

        var totalRemoved = 0;
        foreach (var kvp in _pool)
        {
            var accountId = kvp.Key;
            var queue = kvp.Value;
            var count = queue.Count;

            for (int i = 0; i < count; i++)
            {
                if (!queue.TryDequeue(out var client))
                    break;

                if (client.IsConnected && client.IsAuthenticated)
                {
                    queue.Enqueue(client);
                }
                else
                {
                    _poolCounts.AddOrUpdate(accountId, 0, (_, current) => Math.Max(0, current - 1));
                    DisposeClient(client);
                    totalRemoved++;
                }
            }
        }

        if (totalRemoved > 0)
        {
            _logger.Debug("Connection pool cleanup removed {Count} stale connection(s)", totalRemoved);
        }
    }

    public void RemoveAccount(int accountId)
    {
        _poolCounts.TryRemove(accountId, out _);
        if (_pool.TryRemove(accountId, out var queue))
        {
            while (queue.TryDequeue(out var client))
                DisposeClient(client);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _cleanupTimer.Dispose();
        foreach (var kvp in _pool)
        {
            while (kvp.Value.TryDequeue(out var client))
                DisposeClient(client);
        }
        _pool.Clear();
    }

    private static void DisposeClient(ImapClient client)
    {
        try { if (client.IsConnected) client.Disconnect(true); } catch { }
        client.Dispose();
    }
}
