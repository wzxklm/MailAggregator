using System.Collections;
using System.Reflection;
using MailKit;
using MailKit.Net.Imap;
using Serilog;

namespace MailAggregator.Core.Services.Mail;

/// <summary>
/// Thunderbird-style defensive folder discovery. Tries multiple strategies:
/// 1. Standard: use PersonalNamespaces[0] (works for compliant servers)
/// 2. Fallback: construct default namespace ("", "/") and enumerate (for servers with empty NAMESPACE)
/// 3. Root folder: get root via GetFolder("") and collect subfolders recursively
/// 4. Last resort: INBOX only (guaranteed by RFC 3501 §5.1)
/// Always ensures INBOX is included in results.
/// </summary>
internal class ImapFolderDiscovery
{
    private const int MaxFolderDepth = 10;

    private readonly ILogger _logger;

    public ImapFolderDiscovery(ILogger logger)
    {
        _logger = logger;
    }

    public async Task<IList<IMailFolder>> DiscoverFoldersAsync(Models.Account account, ImapClient client, CancellationToken cancellationToken)
    {
        IList<IMailFolder>? folders = null;

        // Strategy 1: Standard namespace-based enumeration
        if (client.PersonalNamespaces.Count > 0)
        {
            try
            {
                folders = await client.GetFoldersAsync(client.PersonalNamespaces[0], cancellationToken: cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Warning(ex, "Standard namespace enumeration failed for {Email}, trying defensive discovery", account.EmailAddress);
            }
        }
        else
        {
            _logger.Warning("IMAP server for {Email} has no personal namespaces, trying defensive discovery", account.EmailAddress);

            // Inject root folder into MailKit's internal FolderCache via reflection.
            // MailKit's GetFoldersAsync checks the cache before sending LIST — when NAMESPACE
            // returns NIL, the cache is never populated, so LIST "" "*" is never sent.
            // This simulates what would happen if the server returned NAMESPACE (("" "/")).
            TryInjectRootFolderCache(client);
        }

        // Strategy 2: Construct a default namespace (empty prefix, "/" separator)
        // like Thunderbird's pre-set default namespace
        if (folders == null || folders.Count == 0)
        {
            folders = await TryGetFoldersWithDefaultNamespaceAsync(client, cancellationToken);
        }

        // Strategy 3: Try root folder enumeration
        if (folders == null || folders.Count == 0)
        {
            folders = await TryGetFoldersFromRootAsync(client, cancellationToken);
        }

        // Strategy 4: Last resort — INBOX only (guaranteed by RFC 3501 §5.1)
        if (folders == null || folders.Count == 0)
        {
            _logger.Warning("All folder discovery strategies failed for {Email}, using INBOX only", account.EmailAddress);
            folders = new List<IMailFolder> { client.Inbox };
        }

        // Always ensure INBOX is included (like Thunderbird's hardcoded LIST "" "INBOX")
        EnsureInboxIncluded(client, folders);

        return folders;
    }

    internal static char GetDirectorySeparator(ImapClient client)
    {
        return client.Inbox.DirectorySeparator != '\0'
            ? client.Inbox.DirectorySeparator
            : '/';
    }

    private async Task<IList<IMailFolder>?> TryGetFoldersWithDefaultNamespaceAsync(ImapClient client, CancellationToken cancellationToken)
    {
        try
        {
            var defaultNamespace = new FolderNamespace(GetDirectorySeparator(client), "");
            var folders = await client.GetFoldersAsync(defaultNamespace, cancellationToken: cancellationToken);
            if (folders.Count > 0)
            {
                _logger.Information("Default namespace discovery found {Count} folders", folders.Count);
            }
            return folders;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Debug(ex, "Default namespace discovery failed, will try next strategy");
            return null;
        }
    }

    private async Task<IList<IMailFolder>?> TryGetFoldersFromRootAsync(ImapClient client, CancellationToken cancellationToken)
    {
        try
        {
            var root = await client.GetFolderAsync("", cancellationToken);
            var subfolders = await root.GetSubfoldersAsync(cancellationToken: cancellationToken);
            if (subfolders.Count == 0)
                return null;

            // Recursively collect all subfolders
            var allFolders = new List<IMailFolder>();
            await CollectSubfoldersAsync(subfolders, allFolders, cancellationToken);
            if (allFolders.Count > 0)
            {
                _logger.Information("Root folder discovery found {Count} folders", allFolders.Count);
            }
            return allFolders;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Debug(ex, "Root folder discovery failed, will try next strategy");
            return null;
        }
    }

    private async Task CollectSubfoldersAsync(IEnumerable<IMailFolder> folders, List<IMailFolder> result, CancellationToken cancellationToken, int depth = 0)
    {
        if (depth >= MaxFolderDepth)
            return;

        foreach (var folder in folders)
        {
            result.Add(folder);
            try
            {
                var children = await folder.GetSubfoldersAsync(cancellationToken: cancellationToken);
                if (children.Count > 0)
                    await CollectSubfoldersAsync(children, result, cancellationToken, depth + 1);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Debug(ex, "Could not list subfolders of {Folder}, skipping", folder.FullName);
            }
        }
    }

    private static void EnsureInboxIncluded(ImapClient client, IList<IMailFolder> folders)
    {
        var hasInbox = false;
        foreach (var f in folders)
        {
            if (string.Equals(f.FullName, "INBOX", StringComparison.OrdinalIgnoreCase))
            {
                hasInbox = true;
                break;
            }
        }
        if (!hasInbox)
        {
            folders.Add(client.Inbox);
        }
    }

    /// <summary>
    /// Injects a root folder ("") into MailKit's internal FolderCache via reflection,
    /// simulating the effect of NAMESPACE returning (("" "/")).
    /// This allows GetFoldersAsync to send LIST "" "*" instead of throwing
    /// FolderNotFoundException at the cache lookup stage.
    /// Falls back silently on any reflection failure.
    /// Reflection targets are based on MailKit 4.15.1 internals (ImapEngine).
    /// </summary>
    internal void TryInjectRootFolderCache(ImapClient client)
    {
        const BindingFlags instanceAll = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        try
        {
            var separator = GetDirectorySeparator(client);

            // Access the internal ImapEngine via the "engine" field
            var engineField = typeof(ImapClient).GetField("engine", instanceAll);
            if (engineField == null)
            {
                _logger.Debug("Cannot find ImapClient.engine field, skipping root folder cache injection");
                return;
            }

            var engine = engineField.GetValue(client);
            if (engine == null)
            {
                _logger.Debug("ImapClient.engine is null, skipping root folder cache injection");
                return;
            }

            var engineType = engine.GetType();

            // Check if FolderCache already has the "" key
            var cacheField = engineType.GetField("FolderCache", instanceAll);
            if (cacheField == null)
            {
                _logger.Debug("Cannot find ImapEngine.FolderCache field, skipping root folder cache injection");
                return;
            }

            var cache = cacheField.GetValue(engine) as IDictionary;
            if (cache == null)
            {
                _logger.Debug("FolderCache is null or not IDictionary, skipping root folder cache injection");
                return;
            }

            if (cache.Contains(""))
            {
                _logger.Debug("Root folder already in FolderCache, skipping injection");
                return;
            }

            // Create root folder: CreateImapFolder("", FolderAttributes.None, separator)
            var createMethod = engineType.GetMethod("CreateImapFolder", instanceAll);
            if (createMethod == null)
            {
                _logger.Debug("Cannot find ImapEngine.CreateImapFolder method, skipping root folder cache injection");
                return;
            }

            var rootFolder = createMethod.Invoke(engine,
                new object[] { "", FolderAttributes.None, separator });
            if (rootFolder == null)
            {
                _logger.Debug("CreateImapFolder returned null, skipping root folder cache injection");
                return;
            }

            // Register in cache: CacheFolder(rootFolder)
            var cacheFolderMethod = engineType.GetMethod("CacheFolder", instanceAll);
            if (cacheFolderMethod == null)
            {
                _logger.Debug("Cannot find ImapEngine.CacheFolder method, skipping root folder cache injection");
                return;
            }

            cacheFolderMethod.Invoke(engine, new object[] { rootFolder });

            // Also populate PersonalNamespaces so that downstream code (SyncManager,
            // reconnection logic) that reads this collection finds the synthetic namespace.
            client.PersonalNamespaces.Add(new FolderNamespace(separator, ""));

            _logger.Information("Injected root folder cache and default namespace for non-compliant IMAP server");
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Root folder cache injection failed, will continue with existing strategies");
        }
    }
}
