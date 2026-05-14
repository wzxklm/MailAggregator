using MailKit.Net.Imap;

namespace MailAggregator.Core.Services.Mail;

/// <summary>
/// A pooled IMAP connection. Disposing returns the connection to the pool
/// instead of disconnecting it.
/// </summary>
public sealed class PooledImapConnection : IDisposable
{
    public ImapClient Client { get; }

    private readonly Action<ImapClient> _returnAction;
    private bool _disposed;

    internal PooledImapConnection(ImapClient client, Action<ImapClient> returnAction)
    {
        Client = client;
        _returnAction = returnAction;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _returnAction(Client);
    }
}
