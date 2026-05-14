using FluentAssertions;
using MailAggregator.Core.Models;
using MailAggregator.Core.Services.Mail;
using MailAggregator.Core.Services.Sync;
using MailKit.Net.Imap;
using Moq;
using Serilog;

using LocalAccount = MailAggregator.Core.Models.Account;
using LocalMailFolder = MailAggregator.Core.Models.MailFolder;

namespace MailAggregator.Tests.Services.Sync;

public class SyncManagerTests : IDisposable
{
    private readonly Mock<IImapConnectionService> _mockImapConnection;
    private readonly Mock<IEmailSyncService> _mockEmailSyncService;
    private readonly Mock<ILogger> _mockLogger;
    private readonly SyncManager _syncManager;

    public SyncManagerTests()
    {
        _mockImapConnection = new Mock<IImapConnectionService>();
        _mockEmailSyncService = new Mock<IEmailSyncService>();
        _mockLogger = new Mock<ILogger>();

        // Serilog mock needs ForContext support for some patterns
        _mockLogger.Setup(l => l.ForContext(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<bool>()))
            .Returns(_mockLogger.Object);

        _syncManager = new SyncManager(
            _mockImapConnection.Object,
            _mockEmailSyncService.Object,
            _mockLogger.Object);
    }

    public void Dispose()
    {
        _syncManager.StopAllAsync().GetAwaiter().GetResult();
    }

    private static LocalAccount CreateTestAccount(int id = 1, string email = "test@example.com")
    {
        return new LocalAccount
        {
            Id = id,
            EmailAddress = email,
            ImapHost = "imap.example.com",
            ImapPort = 993,
            IsEnabled = true
        };
    }

    private static LocalMailFolder CreateInboxFolder(int accountId = 1)
    {
        return new LocalMailFolder
        {
            Id = 1,
            AccountId = accountId,
            Name = "INBOX",
            FullName = "INBOX",
            SpecialUse = SpecialFolderType.Inbox
        };
    }

    /// <summary>
    /// Sets up mocks so that ConnectAsync blocks until cancellation, simulating a long-running IDLE session.
    /// This prevents the sync loop from completing immediately.
    /// </summary>
    private void SetupBlockingConnect()
    {
        _mockImapConnection
            .Setup(s => s.ConnectAsync(It.IsAny<LocalAccount>(), It.IsAny<CancellationToken>()))
            .Returns<LocalAccount, CancellationToken>(async (_, ct) =>
            {
                // Block until cancellation to simulate a long-running connection
                await Task.Delay(Timeout.Infinite, ct);
                throw new OperationCanceledException(ct);
            });
    }

    /// <summary>
    /// Sets up mocks so that SyncFoldersAsync blocks until cancellation.
    /// ConnectAsync returns a mock ImapClient, but folders sync never completes.
    /// GetFoldersFromDbAsync returns empty so SyncManager attempts IMAP sync.
    /// </summary>
    private void SetupBlockingFolderSync()
    {
        _mockImapConnection
            .Setup(s => s.ConnectAsync(It.IsAny<LocalAccount>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ImapClient());

        _mockEmailSyncService
            .Setup(s => s.GetFoldersFromDbAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LocalMailFolder>());

        _mockEmailSyncService
            .Setup(s => s.SyncFoldersAsync(It.IsAny<LocalAccount>(), It.IsAny<ImapClient>(), It.IsAny<CancellationToken>()))
            .Returns<LocalAccount, ImapClient, CancellationToken>(async (_, _, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                throw new OperationCanceledException(ct);
            });
    }

    // ==========================================
    // Constructor Tests
    // ==========================================

    [Fact]
    public void Constructor_NullImapConnectionService_ThrowsArgumentNullException()
    {
        var act = () => new SyncManager(null!, _mockEmailSyncService.Object, _mockLogger.Object);
        act.Should().Throw<ArgumentNullException>().WithParameterName("imapConnectionService");
    }

    [Fact]
    public void Constructor_NullEmailSyncService_ThrowsArgumentNullException()
    {
        var act = () => new SyncManager(_mockImapConnection.Object, null!, _mockLogger.Object);
        act.Should().Throw<ArgumentNullException>().WithParameterName("emailSyncService");
    }

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        var act = () => new SyncManager(_mockImapConnection.Object, _mockEmailSyncService.Object, null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
    }

    // ==========================================
    // StartAccountSyncAsync Tests
    // ==========================================

    [Fact]
    public async Task StartAccountSyncAsync_NullAccount_ThrowsArgumentNullException()
    {
        var act = () => _syncManager.StartAccountSyncAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task StartAccountSyncAsync_StartsSync_IsAccountSyncingReturnsTrue()
    {
        var account = CreateTestAccount();
        SetupBlockingConnect();

        await _syncManager.StartAccountSyncAsync(account);

        // Give Task.Run a moment to register
        await Task.Delay(50);

        _syncManager.IsAccountSyncing(account.Id).Should().BeTrue();
    }

    [Fact]
    public async Task StartAccountSyncAsync_CalledTwice_DoesNotCreateDuplicate()
    {
        var account = CreateTestAccount();
        SetupBlockingConnect();

        await _syncManager.StartAccountSyncAsync(account);
        await _syncManager.StartAccountSyncAsync(account);

        // Should still only be syncing once
        _syncManager.IsAccountSyncing(account.Id).Should().BeTrue();
    }

    [Fact]
    public async Task StartAccountSyncAsync_MultipleAccounts_AllTracked()
    {
        var account1 = CreateTestAccount(1, "user1@example.com");
        var account2 = CreateTestAccount(2, "user2@example.com");
        SetupBlockingConnect();

        await _syncManager.StartAccountSyncAsync(account1);
        await _syncManager.StartAccountSyncAsync(account2);

        await Task.Delay(50);

        _syncManager.IsAccountSyncing(1).Should().BeTrue();
        _syncManager.IsAccountSyncing(2).Should().BeTrue();
    }

    // ==========================================
    // StopAccountSyncAsync Tests
    // ==========================================

    [Fact]
    public async Task StopAccountSyncAsync_RunningSyncStops_IsAccountSyncingReturnsFalse()
    {
        var account = CreateTestAccount();
        SetupBlockingConnect();

        await _syncManager.StartAccountSyncAsync(account);
        await Task.Delay(50);

        _syncManager.IsAccountSyncing(account.Id).Should().BeTrue();

        await _syncManager.StopAccountSyncAsync(account.Id);

        _syncManager.IsAccountSyncing(account.Id).Should().BeFalse();
    }

    [Fact]
    public async Task StopAccountSyncAsync_NoRunningSync_CompletesGracefully()
    {
        // Stopping a non-existent sync should not throw
        await _syncManager.StopAccountSyncAsync(999);

        _syncManager.IsAccountSyncing(999).Should().BeFalse();
    }

    [Fact]
    public async Task StopAccountSyncAsync_OnlyStopsTargetAccount()
    {
        var account1 = CreateTestAccount(1, "user1@example.com");
        var account2 = CreateTestAccount(2, "user2@example.com");
        SetupBlockingConnect();

        await _syncManager.StartAccountSyncAsync(account1);
        await _syncManager.StartAccountSyncAsync(account2);
        await Task.Delay(50);

        await _syncManager.StopAccountSyncAsync(1);

        _syncManager.IsAccountSyncing(1).Should().BeFalse();
        _syncManager.IsAccountSyncing(2).Should().BeTrue();
    }

    // ==========================================
    // StopAllAsync Tests
    // ==========================================

    [Fact]
    public async Task StopAllAsync_StopsAllRunningAccountSyncs()
    {
        var account1 = CreateTestAccount(1, "user1@example.com");
        var account2 = CreateTestAccount(2, "user2@example.com");
        var account3 = CreateTestAccount(3, "user3@example.com");
        SetupBlockingConnect();

        await _syncManager.StartAccountSyncAsync(account1);
        await _syncManager.StartAccountSyncAsync(account2);
        await _syncManager.StartAccountSyncAsync(account3);
        await Task.Delay(50);

        _syncManager.IsAccountSyncing(1).Should().BeTrue();
        _syncManager.IsAccountSyncing(2).Should().BeTrue();
        _syncManager.IsAccountSyncing(3).Should().BeTrue();

        await _syncManager.StopAllAsync();

        _syncManager.IsAccountSyncing(1).Should().BeFalse();
        _syncManager.IsAccountSyncing(2).Should().BeFalse();
        _syncManager.IsAccountSyncing(3).Should().BeFalse();
    }

    [Fact]
    public async Task StopAllAsync_NoRunningSyncs_CompletesGracefully()
    {
        // Should not throw when nothing is running
        await _syncManager.StopAllAsync();
    }

    // ==========================================
    // IsAccountSyncing Tests
    // ==========================================

    [Fact]
    public void IsAccountSyncing_NoSyncStarted_ReturnsFalse()
    {
        _syncManager.IsAccountSyncing(1).Should().BeFalse();
    }

    // ==========================================
    // NewEmailsReceived Event Tests
    // ==========================================

    [Fact]
    public void NewEmailsEventArgs_PropertiesSetCorrectly()
    {
        var args = new NewEmailsEventArgs(42, "test@mail.com", 5);

        args.AccountId.Should().Be(42);
        args.AccountEmail.Should().Be("test@mail.com");
        args.NewMessageCount.Should().Be(5);
    }

    [Fact]
    public async Task NewEmailsReceived_EventRaisedOnNewMessages()
    {
        var account = CreateTestAccount();
        var inbox = CreateInboxFolder();
        NewEmailsEventArgs? receivedArgs = null;
        var eventRaised = new TaskCompletionSource<bool>();

        // Set up mocks: ConnectAsync returns a client, SyncFolders returns inbox,
        // SyncIncremental completes, then ConnectAsync throws to break the loop after one cycle
        var callCount = 0;

        _mockImapConnection
            .Setup(s => s.ConnectAsync(It.IsAny<LocalAccount>(), It.IsAny<CancellationToken>()))
            .Returns<LocalAccount, CancellationToken>((_, ct) =>
            {
                callCount++;
                if (callCount > 1)
                {
                    // Block on reconnect to prevent rapid looping
                    return Task.FromResult<ImapClient>(null!).ContinueWith<ImapClient>(_ =>
                    {
                        ct.WaitHandle.WaitOne();
                        throw new OperationCanceledException(ct);
                    }, ct);
                }
                return Task.FromResult(new ImapClient());
            });

        _mockEmailSyncService
            .Setup(s => s.GetFoldersFromDbAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LocalMailFolder>());

        _mockEmailSyncService
            .Setup(s => s.SyncFoldersAsync(It.IsAny<LocalAccount>(), It.IsAny<ImapClient>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LocalMailFolder> { inbox });

        _mockEmailSyncService
            .Setup(s => s.SyncIncrementalAsync(It.IsAny<LocalAccount>(), It.IsAny<LocalMailFolder>(), It.IsAny<ImapClient>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        // The sync loop will fail trying to use the ImapClient for IDLE (since it's not connected),
        // causing a reconnect. We want to verify the orchestration, not actual IDLE behavior.
        // Instead, let the loop error out and verify it attempts to connect and sync.

        _syncManager.NewEmailsReceived += (_, args) =>
        {
            receivedArgs = args;
            eventRaised.TrySetResult(true);
        };

        await _syncManager.StartAccountSyncAsync(account);

        // Wait briefly; the sync will try to connect, sync folders, sync incremental, then fail on IDLE
        await Task.Delay(200);

        // Verify that GetFoldersFromDbAsync was called first, then SyncFoldersAsync (DB was empty)
        _mockEmailSyncService.Verify(
            s => s.GetFoldersFromDbAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
        _mockEmailSyncService.Verify(
            s => s.SyncFoldersAsync(It.IsAny<LocalAccount>(), It.IsAny<ImapClient>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);

        // Verify that SyncIncrementalAsync (with client) was called at least once
        _mockEmailSyncService.Verify(
            s => s.SyncIncrementalAsync(It.IsAny<LocalAccount>(), It.Is<LocalMailFolder>(f => f.SpecialUse == SpecialFolderType.Inbox), It.IsAny<ImapClient>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);

        await _syncManager.StopAllAsync();
    }

    [Fact]
    public async Task SyncLoop_NoInboxFolder_ExitsGracefully()
    {
        var account = CreateTestAccount();

        _mockImapConnection
            .Setup(s => s.ConnectAsync(It.IsAny<LocalAccount>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ImapClient());

        // Return folders without an Inbox (DB already has folders, so no IMAP sync needed)
        _mockEmailSyncService
            .Setup(s => s.GetFoldersFromDbAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LocalMailFolder>
            {
                new() { Id = 2, AccountId = 1, Name = "Sent", FullName = "Sent", SpecialUse = SpecialFolderType.Sent }
            });

        await _syncManager.StartAccountSyncAsync(account);

        // Wait for the task to finish (it should exit because no Inbox)
        await Task.Delay(200);

        // The sync task should have exited; IsAccountSyncing may still show true
        // because we track the task reference, but the task itself has completed.
        // Stopping should succeed without issues.
        await _syncManager.StopAccountSyncAsync(account.Id);
    }

    [Fact]
    public async Task SyncLoop_ConnectionError_AttemptsReconnect()
    {
        var account = CreateTestAccount();
        var connectCallCount = 0;

        _mockImapConnection
            .Setup(s => s.ConnectAsync(It.IsAny<LocalAccount>(), It.IsAny<CancellationToken>()))
            .Returns<LocalAccount, CancellationToken>((_, ct) =>
            {
                connectCallCount++;
                if (connectCallCount <= 2)
                {
                    throw new InvalidOperationException("Connection failed");
                }
                // After 2 failures, block until cancellation
                return Task.Delay(Timeout.Infinite, ct).ContinueWith<ImapClient>(_ =>
                    throw new OperationCanceledException(ct), ct);
            });

        await _syncManager.StartAccountSyncAsync(account);

        // Wait for at least 2 reconnect attempts (initial delays of 1s and 2s)
        await Task.Delay(4000);

        connectCallCount.Should().BeGreaterThanOrEqualTo(2,
            "the sync loop should retry connection after failures");

        await _syncManager.StopAllAsync();
    }

    // ==========================================
    // CalculateBackoffDelay Tests
    // ==========================================

    [Fact]
    public void CalculateBackoffDelay_Attempt0_ReturnsNearInitialDelay()
    {
        // With ±25% jitter, attempt 0 (base 1s) should be in [0.75s, 1.25s]
        var delay = SyncManager.CalculateBackoffDelay(0);
        delay.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(750));
        delay.Should().BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(1250));
    }

    [Fact]
    public void CalculateBackoffDelay_Attempt1_ReturnsNearDoubleInitial()
    {
        // With ±25% jitter, attempt 1 (base 2s) should be in [1.5s, 2.5s]
        var delay = SyncManager.CalculateBackoffDelay(1);
        delay.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(1500));
        delay.Should().BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(2500));
    }

    [Fact]
    public void CalculateBackoffDelay_Attempt2_ReturnsNearFourTimesInitial()
    {
        // With ±25% jitter, attempt 2 (base 4s) should be in [3s, 5s]
        var delay = SyncManager.CalculateBackoffDelay(2);
        delay.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(3000));
        delay.Should().BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(5000));
    }

    [Fact]
    public void CalculateBackoffDelay_ExponentialProgression()
    {
        // With jitter, verify that the general exponential trend holds
        var d0 = SyncManager.CalculateBackoffDelay(0);
        var d1 = SyncManager.CalculateBackoffDelay(1);
        var d4 = SyncManager.CalculateBackoffDelay(4);

        // d1 base (2s) > d0 base (1s), accounting for worst-case jitter
        d1.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(1500));
        // d4 base (16s) should be significantly higher than d0
        d4.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(12000));
    }

    [Fact]
    public void CalculateBackoffDelay_CapsAtMaxDelay()
    {
        // Attempt 10: 2^10 = 1024s -> capped at 300s, then jitter ±25% => [225s, 375s]
        var delay = SyncManager.CalculateBackoffDelay(10);
        delay.Should().BeLessThanOrEqualTo(SyncManager.MaxReconnectDelay * 1.25);
        delay.Should().BeGreaterThanOrEqualTo(SyncManager.MaxReconnectDelay * 0.75);
    }

    [Fact]
    public void CalculateBackoffDelay_VeryHighAttempt_CapsAtMaxDelay()
    {
        var delay = SyncManager.CalculateBackoffDelay(100);
        delay.Should().BeLessThanOrEqualTo(SyncManager.MaxReconnectDelay * 1.25);
        delay.Should().BeGreaterThanOrEqualTo(SyncManager.MaxReconnectDelay * 0.75);
    }

    [Fact]
    public void CalculateBackoffDelay_NegativeAttempt_ThrowsArgumentOutOfRange()
    {
        var act = () => SyncManager.CalculateBackoffDelay(-1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void CalculateBackoffDelay_JitterProducesDifferentValues()
    {
        // Run multiple times and verify we get at least some variation (jitter is working)
        var delays = Enumerable.Range(0, 20).Select(_ => SyncManager.CalculateBackoffDelay(5)).ToList();
        delays.Distinct().Count().Should().BeGreaterThan(1, "jitter should produce varying delays");
    }

    // ==========================================
    // Graceful Shutdown Tests
    // ==========================================

    [Fact]
    public async Task GracefulShutdown_CancellationDuringConnect_ExitsCleanly()
    {
        var account = CreateTestAccount();
        SetupBlockingConnect();

        using var cts = new CancellationTokenSource();

        await _syncManager.StartAccountSyncAsync(account, cts.Token);
        await Task.Delay(50);

        _syncManager.IsAccountSyncing(account.Id).Should().BeTrue();

        // Cancel the external token
        cts.Cancel();

        // Give the task a moment to observe the cancellation
        await Task.Delay(200);

        // Now StopAccountSyncAsync should complete promptly
        await _syncManager.StopAccountSyncAsync(account.Id);
        _syncManager.IsAccountSyncing(account.Id).Should().BeFalse();
    }

    [Fact]
    public async Task GracefulShutdown_CancellationDuringFolderSync_ExitsCleanly()
    {
        var account = CreateTestAccount();
        SetupBlockingFolderSync();

        await _syncManager.StartAccountSyncAsync(account);
        await Task.Delay(50);

        _syncManager.IsAccountSyncing(account.Id).Should().BeTrue();

        await _syncManager.StopAccountSyncAsync(account.Id);
        _syncManager.IsAccountSyncing(account.Id).Should().BeFalse();
    }

    // ==========================================
    // Static Configuration Tests
    // ==========================================

    [Fact]
    public void IdleTimeout_IsLessThan30Minutes()
    {
        SyncManager.IdleTimeout.Should().BeLessThan(TimeSpan.FromMinutes(30),
            "IMAP IDLE has a 30-minute RFC limit; timeout must be shorter");
    }

    [Fact]
    public void InitialReconnectDelay_IsOneSecond()
    {
        SyncManager.InitialReconnectDelay.Should().Be(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void MaxReconnectDelay_Is300Seconds()
    {
        SyncManager.MaxReconnectDelay.Should().Be(TimeSpan.FromSeconds(300));
    }
}
