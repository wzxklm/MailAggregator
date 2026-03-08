using System.Collections.Concurrent;
using MailAggregator.Core.Models;
using MailKit.Net.Imap;
using Serilog;

namespace MailAggregator.Core.Services.Mail;

public class ImapConnectionPool : IImapConnectionPool
{
    private const int MaxPoolSizePerAccount = 2;

    private readonly ConcurrentDictionary<int, ConcurrentQueue<ImapClient>> _pool = new();
    private readonly IImapConnectionService _connectionService;
    private readonly ILogger _logger;
    private bool _disposed;

    public ImapConnectionPool(IImapConnectionService connectionService, ILogger logger)
    {
        _connectionService = connectionService ?? throw new ArgumentNullException(nameof(connectionService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<PooledImapConnection> GetConnectionAsync(Account account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        var queue = _pool.GetOrAdd(account.Id, _ => new ConcurrentQueue<ImapClient>());

        while (queue.TryDequeue(out var client))
        {
            if (client.IsConnected && client.IsAuthenticated)
            {
                _logger.Debug("Reusing pooled IMAP connection for {Email}", account.EmailAddress);
                return new PooledImapConnection(client, c => ReturnToPool(account.Id, c));
            }

            // Stale connection
            client.Dispose();
        }

        // No pooled connection available, create a new one
        var newClient = await _connectionService.ConnectAsync(account, cancellationToken);
        return new PooledImapConnection(newClient, c => ReturnToPool(account.Id, c));
    }

    private void ReturnToPool(int accountId, ImapClient client)
    {
        if (_disposed || !client.IsConnected)
        {
            DisposeClient(client);
            return;
        }

        var queue = _pool.GetOrAdd(accountId, _ => new ConcurrentQueue<ImapClient>());
        if (queue.Count < MaxPoolSizePerAccount)
        {
            queue.Enqueue(client);
        }
        else
        {
            DisposeClient(client);
        }
    }

    public void RemoveAccount(int accountId)
    {
        if (_pool.TryRemove(accountId, out var queue))
        {
            while (queue.TryDequeue(out var client))
                DisposeClient(client);
        }
    }

    public void Dispose()
    {
        _disposed = true;
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
