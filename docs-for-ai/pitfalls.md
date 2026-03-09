# Project Pitfalls & Conventions

> 开发过程中踩过的坑和项目特有约定。AI 编码前必读。

---

## EF Core / 数据库

- **DbContext 非线程安全**：禁止 `Task.Run(() => query.ToList())`，必须用 `ToListAsync()`。DbContext 不能跨线程共享，后台服务（如 `EmailSyncService`）必须用 `IDbContextFactory` 创建 scoped 实例，UI 层 scoped 服务可直接注入
- **并发冲突**：从一个 DbContext 查出的实体不能 Attach 到另一个，会抛 tracking 异常。`SaveChangesSafeAsync()` 处理此场景：分离旧实体并重试
- **批量保存**：大量消息同步时每 50 条保存一次，避免 pending changes 过多导致内存飙升

## MailKit / 邮件协议

- **命名冲突 — MailFolder**：`MailKit.MailFolder` 与 `Models.MailFolder` 冲突 → 必须用 `using LocalMailFolder = MailAggregator.Core.Models.MailFolder`
- **命名冲突 — Account**：`Services.AccountManagement` 命名空间导致 `Account` 模型歧义 → 用 `using LocalAccount = MailAggregator.Core.Models.Account`
- **IMAP IDLE 必须独立连接**：IDLE 占用连接，不能复用同步连接。连接池 per-account 最多 2 个连接
- **IDLE 超时**：RFC 2177 要求 < 30 分钟，项目设 29 分钟。超时后必须重新打开文件夹再进入 IDLE
- **连接复用前必须验证**：从池中取出的连接必须检查 `IsConnected && IsAuthenticated`，失效则释放

## OAuth

- **PKCE 不是所有提供商都支持**：Google 要求 PKCE，但部分提供商不支持 → `oauth-providers.json` 中 `usePKCE` 是 per-provider 配置，不要硬编码
- **RedirectionEndpoint 因提供商而异**：部分提供商（Yahoo/AOL）需要固定 redirect URI，不能用动态端口 → 用 `redirectionEndpoint` 字段覆盖
- **RefreshToken 刷新后旧 token 立即失效**：刷新后必须立即持久化新 token 到数据库（`PersistRefreshedTokenAsync`），否则下次启动会用过期 token
- **令牌刷新宽限期**：令牌过期前 60 秒就触发刷新（`TokenRefreshGracePeriod`），避免请求时恰好过期

## WPF / UI

- **样式统一放 `Resources/Styles.xaml`**：app-level 资源字典，不要在单个 Window 里定义样式或转换器
- **ViewModel 必须退订单例事件**：订阅 `SyncManager.NewEmailsReceived` 等单例事件的 ViewModel 必须实现 `IDisposable` 并在 Dispose 中退订，否则内存泄漏
- **UI 线程更新**：后台事件回调中修改 ObservableCollection 必须用 `Dispatcher.Invoke()` 切到 UI 线程
- **Desktop 项目需要 `UseWindowsForms=true`**：NotifyIcon（Toast 通知）依赖 WinForms，csproj 中必须启用

## 邮件发现 & 同步

- **MX 域名提取须处理 ccSLD**：`co.uk`、`com.au` 等 country-code second-level domain 需要取 3 级域名（如 `yahoo.co.uk`），否则会提取到无意义的 `co.uk`。参见 `TwoLevelTlds` HashSet
- **nslookup 必须有超时**：使用 `CancellationTokenSource.CreateLinkedTokenSource` + `CancelAfter(10s)`，超时后 `Kill()` 进程
- **删除账户须清理运行时资源**：先停后台同步（`StopAccountSyncAsync`）、释放连接池（`RemoveAccount`）、删磁盘附件，最后才删数据库记录

## 架构约定

- **共享连接逻辑放 `MailConnectionHelper`**：认证、代理配置、加密类型映射等逻辑集中在此 internal static 类，不要在 ImapConnectionService / SmtpConnectionService 中重复实现
- **新服务必须注册 DI + 定义接口**：在 `App.xaml.cs` 的 `ServiceCollection` 中注册，且必须有对应的 `I` 前缀接口
- **测试文件镜像 Core 目录结构**：`MailAggregator.Tests/Services/` 下的目录结构与 `MailAggregator.Core/Services/` 一一对应

## 安全

- **敏感数据必须加密存储**：密码、AccessToken、RefreshToken 存入数据库前必须经过 `CredentialEncryptionService`（AES-256-GCM）加密，绝不存明文
- **加密密钥通过 DPAPI 保护**：生产环境用 `DpapiKeyProtector`（Windows CurrentUser 作用域），开发/测试环境用 `DevKeyProtector`（直通，不安全）
- **WebView2 安全配置**：禁用脚本、禁用右键菜单、阻止外部导航和资源加载（防追踪）
- **OAuth state 参数**：必须用 `RandomNumberGenerator` 生成随机 state 并在回调中验证，防 CSRF（RFC 6749 §10.12）
- **外部命令注入防护**：传给 `nslookup` 等外部进程的域名必须用 `ValidDomainRegex` 校验（仅允许字母数字、连字符、点号）
- **HttpListener 资源清理**：`_pendingListeners` 中的 HttpListener 必须在新 OAuth 流开始时清理，防止abandoned flow 导致端口泄漏
- **端口绑定 TOCTOU**：发现空闲端口后必须立即绑定（`StartListenerOnFreePort` 带重试），不能先查后绑

## 构建 & 测试

- **Core 项目 `net8.0`**：跨平台，Linux 可构建和测试
- **Desktop 项目 `net8.0-windows`**：Windows only，Linux 无法构建
- **测试命令**：`dotnet test src/MailAggregator.Tests/` — 每次代码变更后必须运行
