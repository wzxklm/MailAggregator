# Project Pitfalls & Conventions

> 开发过程中踩过的坑和项目特有约定。AI 编码前必读。

---

## EF Core / 数据库

- **DbContext 非线程安全**：禁止 `Task.Run(() => query.ToList())`，必须用 `ToListAsync()`。DbContext 不能跨线程共享，后台服务（如 `EmailSyncService`）必须用 `IDbContextFactory` 创建 scoped 实例，UI 层 scoped 服务可直接注入
- **并发冲突**：从一个 DbContext 查出的实体不能 Attach 到另一个，会抛 tracking 异常。`SaveChangesSafeAsync()` 处理此场景：分离旧实体并重试。也处理 SQLite UNIQUE 约束冲突（error code 19）：分离 Added 实体并重试
- **同一文件夹禁止并发同步**：`EmailSyncService` 使用 per-folder `SemaphoreSlim`（`_folderSyncLocks`）防止 SyncManager IDLE 循环与 UI 层同时对同一文件夹执行同步，否则会导致 UNIQUE 约束冲突。锁在公开方法（`SyncInitialAsync`/`SyncIncrementalAsync`）获取，内部 `*CoreAsync` 方法不加锁
- **UIDVALIDITY 分支须调用 CoreAsync 避免死锁**：`SyncIncrementalCoreAsync` 检测到 UIDVALIDITY 变化时，必须调用 `SyncInitialCoreAsync`（不加锁版本），不能调用公开的 `SyncInitialAsync`（会重入同一 `SemaphoreSlim` 导致死锁）。同时传入调用方的 `ImapClient` 复用连接，避免同一账户占满连接池（max 2）
- **同一 DbContext 内 Attach 前必须检查 ChangeTracker**：`FetchAndCacheMessagesAsync` 保存后实体仍被跟踪（`Unchanged` 状态），后续 `SyncFlagsAndDetectDeletionsAsync` 若对同 Id 实体调用 `Attach` 会抛 `InvalidOperationException`。修复模式：先查 `ChangeTracker.Entries<T>()` 构建字典，已跟踪则直接更新 tracked entity，否则 Attach 新桩对象。参见 `SetMessageReadAsync`、`FetchMessageBodyAsync`、`SyncFlagsAndDetectDeletionsAsync`
- **避免 `Update()` 级联图遍历**：`dbContext.Folders.Update(folder)` 会级联到 `Messages` 导航属性，若其中有已跟踪实体则冲突。改用 `Attach` + 逐个 `Property(...).IsModified = true` 仅标记需要更新的标量属性
- **批量保存**：大量消息同步时每 50 条保存一次，避免 pending changes 过多导致内存飙升
- **新增实体必须手动建表**：`EnsureCreatedAsync()` 仅在数据库文件不存在时创建全部表。对已有数据库新增 `DbSet` 不会自动建表。必须在 `DatabaseInitializer.InitializeAsync` 中追加 `ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS ...")` 手动建表。列类型须与 EF Core 约定一致：`DateTimeOffset` → `INTEGER`（UTC ticks，对应 `DateTimeOffsetToLongConverter`），枚举 → `INTEGER`
- **新增带时间戳实体须扩展 StampTimestamps**：`MailAggregatorDbContext.StampTimestamps()` 按实体类型逐个处理。新增含 `CreatedAt`/`UpdatedAt` 的实体时，必须在该方法中添加对应的 `ChangeTracker.Entries<T>()` 循环，否则时间戳不会自动填充

## MailKit / 邮件协议

- **命名冲突 — MailFolder**：`MailKit.MailFolder` 与 `Models.MailFolder` 冲突 → 必须用 `using LocalMailFolder = MailAggregator.Core.Models.MailFolder`
- **命名冲突 — Account**：`Services.AccountManagement` 命名空间导致 `Account` 模型歧义 → 用 `using LocalAccount = MailAggregator.Core.Models.Account`
- **IMAP ID 命令（RFC 2971）须在认证前发送**：163.com（Coremail）等服务器要求客户端先 `IdentifyAsync` 声明身份，否则拒绝登录（"Unsafe Login"）。仅在 `ImapCapabilities.Id` 存在时发送，失败时 log + 吞掉异常继续认证流程，不影响不支持 ID 的服务器
- **IMAP IDLE 必须独立连接**：IDLE 占用连接，不能复用同步连接。连接池 per-account 最多 2 个连接
- **IDLE 超时**：RFC 2177 要求 < 30 分钟，项目设 29 分钟。超时后必须重新打开文件夹再进入 IDLE
- **连接复用前必须验证**：从池中取出的连接必须检查 `IsConnected && IsAuthenticated`，失效则释放
- **Microsoft "User is authenticated but not connected" 是非瞬态错误**：Microsoft IMAP 服务器可能返回此错误，表示账户虽已认证但无法连接（通常是账户配置/许可问题）。`IsNonTransientAuthError` 通过 "not connected" 关键字检测此错误。`ImapConnectionService` 的重试循环捕获此类错误后立即抛出，不浪费重试次数
- **MimeKit TryParse 不验证地址格式**：`InternetAddressList.TryParse` 对纯数字等无效输入（如 `1`）仍返回成功并构造无域名的 `MailboxAddress`。发送前必须调用 `ParseAndValidateAddresses` 检查每个地址包含 `@` 且 local/domain 非空，否则无效地址直达 SMTP 服务器返回不友好的 5xx 错误

## OAuth

- **PKCE 不是所有提供商都支持**：Google 要求 PKCE，但部分提供商不支持 → `oauth-providers.json` 中 `usePKCE` 是 per-provider 配置，不要硬编码
- **RedirectionEndpoint 因提供商而异**：部分提供商（Yahoo/AOL）需要固定 redirect URI，不能用动态端口 → 用 `redirectionEndpoint` 字段覆盖
- **RefreshToken 刷新后旧 token 立即失效**：刷新后必须立即持久化新 token 到数据库（`PersistRefreshedTokenAsync`），否则下次启动会用过期 token
- **令牌刷新宽限期**：令牌过期前 60 秒就触发刷新（`TokenRefreshGracePeriod`），避免请求时恰好过期
- **令牌刷新并发保护**：IMAP 和 SMTP 可能同时刷新同一账户 token，Google 等提供商会使旧 refresh token 失效。`MailConnectionHelper` 使用 per-account `SemaphoreSlim` + double-check pattern 防止并发刷新。删除账户时必须调用 `RemoveTokenRefreshLock` 清理信号量
- **invalid_grant 不可重试**：`OAuthService` 检测 `invalid_grant` 后抛出 `OAuthReauthenticationRequiredException`，`SyncManager` 捕获后直接 break 退出同步循环（不进入重连退避），用户必须重新授权
- **OAuth 检测必须由 ViewModel 驱动，AccountService 不应覆盖**：`AddAccountAsync` 接受显式 `authType` 参数，由 ViewModel 传入。ViewModel 通过 `UpdateOAuthAvailability(imapHost)` 在发现成功或用户手动修改 IMAP host 时统一检测 OAuth 可用性。禁止在 AccountService 中独立覆盖调用方的 auth type 决定，否则会出现「账户标记为 OAuth2 但无 token」的 bug

## WPF / UI

- **样式统一放 `Resources/Styles.xaml`**：app-level 资源字典，不要在单个 Window 里定义样式或转换器
- **ViewModel 必须退订单例事件**：订阅 `SyncManager.NewEmailsReceived` 等单例事件的 ViewModel 必须实现 `IDisposable` 并在 Dispose 中退订，否则内存泄漏
- **UI 线程更新**：后台事件回调中修改 ObservableCollection 必须用 `Dispatcher.Invoke()` 切到 UI 线程
- **多账户并发操作须逐个隔离错误**：`LoadAccountsAsync` 中每个账户的文件夹同步用独立 try/catch 包裹，单个账户的 IMAP 错误（如认证失败、服务器不可达）不阻塞其他账户加载。任何新增的多账户循环操作都应遵循此模式
- **Desktop 项目需要 `UseWindowsForms=true`**：NotifyIcon（Toast 通知）依赖 WinForms，csproj 中必须启用

## 邮件发现 & 同步

- **MX 域名提取须处理 ccSLD**：`co.uk`、`com.au` 等 country-code second-level domain 需要取 3 级域名（如 `yahoo.co.uk`），否则会提取到无意义的 `co.uk`。参见 `TwoLevelTlds` HashSet
- **DNS 查询使用 DnsClient.NET**：不再通过 `nslookup` 进程解析 MX/SRV 记录，改用 `ILookupClient`（DnsClient 库）。构造函数接受 `ILookupClient` 便于测试注入。DNS 超时通过 `LookupClientOptions.Timeout` 配置（10s）
- **RFC 6186 SRV 记录发现（Level 5）**：自动发现 fallback 链新增 SRV 查询（`_imaps._tcp`→`_imap._tcp`→`_submission._tcp`），SMTP SRV 查询与 IMAP 并行执行以减少延迟
- **添加账户时须传 `manualConfig` 跳过冗余发现**：`AddAccountViewModel` 在调用 `AddAccountAsync` 前已通过自动发现或手动输入获得了服务器配置，UI 字段已填好。调用时必须将 UI 字段构建为 `ServerConfiguration` 并通过 `manualConfig` 参数传入，否则 `AccountService` 会再次执行自动发现（重复网络请求、延迟增加，且手动修改的配置会被覆盖）
- **删除账户须清理运行时资源**：先停后台同步（`StopAccountSyncAsync`）、释放连接池（`RemoveAccount`）、清理 token 刷新锁（`RemoveTokenRefreshLock`）、删磁盘附件，最后才删数据库记录
- **Account 实体操作前须 Detach 已跟踪实例**：长生命周期的 root-scoped DbContext 可能已跟踪同 Id 的 Account 实体（如 `AddAccountAsync` 保存后仍为 `Unchanged`）。`UpdateAccountAsync` 在调用 `Update()` 前必须查 `ChangeTracker.Entries<Account>()` 并 Detach 已跟踪实例，否则抛 "already being tracked" 异常。`DeleteAccountAsync` 同理但更复杂：sync stop 和资源清理后实体可能已过期（sync loop 的 DbContext 通过 factory 创建，可能修改了同一行，如 OAuth token 刷新），修复：`Entry(account).State = Detached` → `FirstOrDefaultAsync` 重新取 → `Remove(freshAccount)`
- **更新账户须重启同步**：`AccountService.UpdateAccountAsync` 校验 host/port 后，若账户正在同步则自动 stop → remove pool → start，否则 SyncManager 继续用旧配置连接
- **IMAP IDLE 不是所有服务器都支持**：进入监视循环前必须检查 `ImapCapabilities.Idle`，不支持时降级为 2 分钟轮询 + `NoOpAsync`。即使宣称支持，服务器也可能以 BAD/NO 拒绝 IDLE 命令，`IdleWaitAsync` 捕获 `ImapCommandException` 并降级为当次轮询
- **合并 IMAP 操作减少往返**：`SyncFlagsAndDetectDeletionsAsync` 用一次 FETCH 同时完成标志同步和删除检测（服务器仅返回仍存在的 UID，缺失的即为已删除）。避免分别查询浪费带宽
- **连接池需定时清理**：`ImapConnectionPool` 使用 `Timer`（5 分钟）后台清理断开的僵尸连接。NAT/移动网络可能静默断开 TCP 而 `IsConnected` 仍返回 true
- **连接池大小原子跟踪**：`_poolCounts` ConcurrentDictionary 通过 `AddOrUpdate` 原子递增/递减，防止并发入池导致超限
- **指数退避须含抖动**：`CalculateBackoffDelay` 在退避延迟上添加 ±25% 随机抖动（`JitterFactor=0.25`），防止多账户同时重连（thundering herd）
- **网络感知重连**：`SyncManager` 订阅 `NetworkChange.NetworkAvailabilityChanged`，网络断开时暂停退避循环（`ManualResetEventSlim`），恢复时立即重连（重置 `reconnectAttempt=0`）。不要用冗余的 volatile bool，直接用 `_networkAvailable.IsSet`
- **AutoDiscovery 并行请求须取消剩余**：Level 1-3 并行发起后，首个成功结果通过 `CancellationTokenSource.CancelAsync()` 取消其他正在进行的 HTTP 请求，避免浪费资源

## 2FA / TOTP

- **PK 查找用 `FindAsync` 而非 `FirstOrDefaultAsync`**：`FindAsync` 先查 ChangeTracker 后查数据库，适合单主键查找且避免与已跟踪实体冲突。`TwoFactorAccountService.UpdateAsync`/`DeleteAsync` 均使用此模式
- **Secret 存储前须 `ToUpperInvariant()` 标准化**：Base32 不区分大小写，但用户输入可能混合大小写。在验证和加密前统一转换为大写，确保解密后 TOTP 生成一致。`ParseOtpAuthUri` 同样对 secret 参数做大写标准化
- **TOTP 密钥字节须零化清理**：`Base32Encoding.ToBytes` 解码后的密钥字节数组用完后必须调用 `CryptographicOperations.ZeroMemory` 清理，防止内存中残留敏感数据。使用 `try/finally` 确保异常路径也能清理

## 架构约定

- **共享连接逻辑放 `MailConnectionHelper`**：认证、代理配置、加密类型映射等逻辑集中在此 internal static 类，不要在 ImapConnectionService / SmtpConnectionService 中重复实现。非瞬态认证错误检测（`IsNonTransientAuthError`）也集中在此类，`SyncManager` 和 `EmailSyncService` 统一调用，不要在各处内联关键字匹配
- **新服务必须注册 DI + 定义接口**：在 `App.xaml.cs` 的 `ServiceCollection` 中注册，且必须有对应的 `I` 前缀接口
- **测试文件镜像 Core 目录结构**：`MailAggregator.Tests/Services/` 下的目录结构与 `MailAggregator.Core/Services/` 一一对应

## 安全

- **敏感数据必须加密存储**：密码、AccessToken、RefreshToken 存入数据库前必须经过 `CredentialEncryptionService`（AES-256-GCM）加密，绝不存明文
- **加密密钥通过 DPAPI 保护**：生产环境用 `DpapiKeyProtector`（Windows CurrentUser 作用域），开发/测试环境用 `DevKeyProtector`（直通，不安全）
- **WebView2 安全配置**：禁用脚本、禁用右键菜单、阻止外部导航和资源加载（防追踪）
- **OAuth state 参数**：必须用 `RandomNumberGenerator` 生成随机 state 并在回调中验证，防 CSRF（RFC 6749 §10.12）
- **不使用外部进程做 DNS 查询**：已从 nslookup 进程迁移到 DnsClient.NET 库，消除了命令注入风险和 `ValidDomainRegex` 依赖
- **HttpListener 取消须捕获两种异常**：`listener.Stop()`（通过 `cancellationToken.Register` 触发）可能使 `GetContextAsync()` 抛出 `ObjectDisposedException` 或 `HttpListenerException`（Windows 错误码 995），取决于时序。catch 块必须同时处理两者并转换为 `OperationCanceledException`，否则 `HttpListenerException` 会作为未处理异常向上传播
- **HttpListener 资源清理**：`_pendingListeners` 中的 HttpListener 必须在新 OAuth 流开始时清理，防止abandoned flow 导致端口泄漏
- **端口绑定 TOCTOU**：发现空闲端口后必须立即绑定（`StartListenerOnFreePort` 带重试），不能先查后绑

## 构建 & 测试

- **Core 项目 `net8.0`**：跨平台，Linux 可构建和测试
- **Desktop 项目 `net8.0-windows`**：Windows only，Linux 无法构建
- **测试命令**：`dotnet test src/MailAggregator.Tests/` — 每次代码变更后必须运行
