# Bug Report — v1.0.8 Runtime Errors

> 基于 2026-03-09 07:01–07:10 运行日志分析。

---

## ~~BUG-001: Microsoft OAuth scope 域名错误~~ [已修复]

**状态**: 已关闭 — 2026-03-09 手动修正 `oauth-providers.json` 中 scope 域名 `office365.com` → `office.com`

---

## ~~BUG-002: 并发同步导致 UNIQUE 约束冲突~~ [已修复]

**状态**: 已关闭 — 2026-03-09 修复：per-folder `SemaphoreSlim` 锁 + `SaveChangesSafeAsync` 增加 UNIQUE 约束冲突处理 + UIDVALIDITY 分支复用调用方连接

---

## ~~BUG-003: EF Core 实体跟踪冲突~~ [已修复]

**状态**: 已关闭 — 2026-03-09 修复：两处改动消除 EF Core 实体跟踪冲突
1. `SyncFlagsAndDetectDeletionsAsync`: 在 Attach 桩实体前，通过 `ChangeTracker.Entries<EmailMessage>()` 预建字典检查是否已跟踪。已跟踪则直接更新已有实体，未跟踪则 Attach 新桩对象
2. `SyncIncrementalCoreAsync`: 将 `dbContext.Folders.Update(folder)` 替换为 `Attach` + 仅标记 `MaxUid`/`MessageCount`/`UnreadCount` 为 Modified，避免 EF Core `Update` 图遍历级联到 Messages 导航属性中已跟踪的 EmailMessage 实体

---

## ~~BUG-004: OAuth 回调 HttpListenerException 未捕获~~ [已修复]

**状态**: 已关闭 — 2026-03-09 修复：在 `WaitForAuthorizationCodeAsync` 中将 `catch (ObjectDisposedException)` 改为 `catch (Exception ex) when ((ex is ObjectDisposedException or HttpListenerException) && cancellationToken.IsCancellationRequested)`，统一将取消期间的两种异常转换为 `OperationCanceledException`

---

## ~~BUG-005: 发送邮件缺少收件人地址校验~~ [已修复]

**状态**: 已关闭 — 2026-03-09 修复：在 `EmailSendService` 中新增 `ParseAndValidateAddresses` 和 `IsValidMailboxAddress` 方法，校验邮件地址必须包含 `local@domain` 格式。`SetRecipients` 改用 `ParseAndValidateAddresses`（单次解析+校验，无重复解析），无效地址在发送前即抛出 `ArgumentException`。原 `ParseAddresses` 保留供 `ReplyAllAsync` 解析已存储的原始消息地址
