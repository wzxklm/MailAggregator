# IMAP 协议兼容性问题报告 — mail.seek.li

## 概述

我们的邮件客户端（基于 MailKit/.NET）能够成功连接 `mail.seek.li:993`（SSL）并完成认证，但该服务器存在两个 IMAP 协议兼容性问题，导致无法正常枚举邮箱文件夹。

## 问题 1：NAMESPACE 命令返回空的 Personal Namespaces

认证成功后，服务器对 `NAMESPACE` 命令（RFC 2342）返回了空的 Personal Namespaces。

**期望的响应（根据 RFC 2342）：**
```
C: A01 NAMESPACE
S: * NAMESPACE (("" "/")) NIL NIL
S: A01 OK NAMESPACE completed
```

**实际行为：**
服务器对所有三个命名空间类别均返回 `NIL`，或不支持 `NAMESPACE` 命令。这导致客户端无法发现文件夹层级结构。

## 问题 2：LIST 根目录查询失败

当客户端尝试使用标准 IMAP LIST 命令（RFC 3501 §6.3.8）列出文件夹时：

```
C: A02 LIST "" "*"    -- 应返回所有文件夹
C: A03 LIST "" ""     -- 应返回根目录层级分隔符
```

服务器未能正确响应，导致客户端抛出 `FolderNotFoundException` 异常。根据 RFC 3501，`LIST "" "*"` 应返回所有可用文件夹，`LIST "" ""` 应返回层级分隔符信息。

## 影响

由于以上两个问题的叠加：
- 客户端无法枚举任何邮箱文件夹（已发送、草稿箱、回收站等）
- 仅能通过 `SELECT INBOX` 直接访问收件箱（RFC 3501 §5.1 保证 INBOX 始终存在）
- 用户只能收发收件箱中的邮件，无法访问其他文件夹

## 为什么 Thunderbird 能正常使用？

我们阅读了 Mozilla Thunderbird 的 IMAP 协议实现源码（`mozilla/releases-comm-central` 仓库，`mailnews/imap/src/` 目录），发现 Thunderbird 之所以能正常工作，**并非因为服务器响应正确，而是因为 Thunderbird 内置了针对不合规服务器的容错机制**。具体如下：

### Thunderbird 的容错策略

**1. 预设默认命名空间（绕过 NAMESPACE 问题）**

Thunderbird 在连接服务器**之前**，就在本地创建了一个默认的个人命名空间（空前缀 `""`，分隔符 `/`）。关键代码位于 `nsImapIncomingServer.cpp`：

```cpp
// 当所有命名空间偏好设置均为空时（新账户的默认情况）
if (personalNamespace.IsEmpty() && publicNamespace.IsEmpty() &&
    otherUsersNamespace.IsEmpty())
    personalNamespace.AssignLiteral("\"\"");  // 硬编码默认值: 空前缀
```

当服务器的 `NAMESPACE` 命令返回全 `NIL` 时，Thunderbird 的解析器（`nsImapServerResponseParser.cpp`）只是静默跳过，**不会清除这个预设的默认命名空间**。因此 Thunderbird 始终至少拥有一个 `""` 前缀的个人命名空间可用。

**2. 基于默认命名空间执行 LIST（绕过 LIST 问题）**

在文件夹发现阶段（`nsImapProtocol.cpp` 的 `DiscoverMailboxList()` 函数），Thunderbird 遍历所有命名空间，对每个命名空间拼接前缀并执行 LIST：

```cpp
for (uint32_t i = 0; i < count; i++) {
    nsImapNamespace* ns = nullptr;
    m_hostSessionList->GetNamespaceNumberForHost(GetImapServerKey(), i, ns);
    if (ns) {
        const char* prefix = ns->GetPrefix();
        nsCString pattern;
        pattern.Append(prefix);
        pattern += '*';           // 对于空前缀, pattern = "*"
        List(pattern.get(), ...); // 发送: LIST "" "*"
    }
}
```

由于默认命名空间的前缀为空字符串，实际发送的命令是 `LIST "" "*"`。如果该命令在贵服务器上能返回结果（即使 NAMESPACE 返回 NIL），Thunderbird 就能发现文件夹。

**3. INBOX 始终显式列出**

无论命名空间发现结果如何，Thunderbird 硬编码了始终显式执行 `LIST "" "INBOX"`：

```cpp
// GetShouldAlwaysListInboxForHost() 硬编码返回 true
if (!usingSubscription || listInboxForHost)
    List("INBOX", true);  // 始终执行
```

**4. 分隔符自动修正**

Thunderbird 先猜测分隔符为 `/`，然后在收到 LIST 响应后，从响应中提取实际的分隔符并修正本地记录：

```cpp
fHostSessionList->SetNamespaceHierarchyDelimiterFromMailboxForHost(
    serverKey, boxname.get(), boxSpec->mHierarchySeparator);
```

### 结论：Thunderbird 能用 ≠ 服务器没问题

Thunderbird 的成功连接依赖于以下**客户端侧的容错逻辑**，而非服务器的正确行为：

| 服务器问题 | Thunderbird 的容错方式 |
|-----------|----------------------|
| NAMESPACE 返回全 NIL | 使用本地预设的默认命名空间 `""` |
| LIST 依赖正确的命名空间前缀 | 默认命名空间前缀为空，拼接后仍为 `LIST "" "*"` |
| 分隔符未知 | 先猜 `/`，后从 LIST 响应中修正 |

**这些都是 Thunderbird 为了兼容各类不合规 IMAP 服务器而实现的防御性编程**，并不代表服务器的 NAMESPACE 和 LIST 行为符合 RFC 标准。其他严格遵循 RFC 的客户端（如 MailKit、Apple Mail 等）在遇到相同问题时可能无法正常工作。

## 当前防御性策略的实际表现（2026-03-12 诊断）

我们已在 `EmailSyncService.DiscoverFoldersAsync` 中实现了 Thunderbird 风格的4级策略级联，但在 mail.seek.li 上全部失败。通过反编译 MailKit 4.15.1 源码（`ImapEngine.cs`）并结合运行日志，定位到真正的故障原因：

### 策略执行日志

```
[WRN] IMAP server for wzxklm@seek.li has no personal namespaces, trying defensive discovery
[DBG] Default namespace discovery failed, will try next strategy
      → FolderNotFoundException at ImapEngine.QueueGetFoldersCommand(FolderNamespace namespace, ...)
[DBG] Root folder discovery failed, will try next strategy
      → FolderNotFoundException at ImapEngine.ProcessGetFolderResponse(...)
[WRN] All folder discovery strategies failed for wzxklm@seek.li, using INBOX only
[INF] Synced 1 folders for wzxklm@seek.li
```

### 逐策略故障分析

| 策略 | 我们的代码 | 实际发生了什么 | `LIST` 是否发出 |
|------|-----------|--------------|----------------|
| 1. 标准命名空间 | `GetFoldersAsync(PersonalNamespaces[0])` | `PersonalNamespaces.Count == 0`，跳过 | 否 |
| 2. 构造默认命名空间 | `new FolderNamespace('/', "")` → `GetFoldersAsync` | MailKit 在 `QueueGetFoldersCommand` 内部执行 `TryGetCachedFolder("") → false`，**未发命令即抛异常** | **否** |
| 3. 根文件夹枚举 | `GetFolderAsync("")` | MailKit 发送了 `LIST "" ""`，服务器响应中无 `""` 文件夹 | 是，但响应无效 |
| 4. 仅 INBOX | `client.Inbox` | 兜底命中 | — |

### 关键发现：`LIST "" "*"` 从未被发送

issue 报告中"问题 2"关于 `LIST "" "*"` 失败的描述**不准确** — 该命令从未发送到服务器。

策略2的 `FolderNotFoundException` 异常栈在 `ImapEngine.QueueGetFoldersCommand`（构造命令阶段），而非 `ProcessGetFolderResponse`（处理响应阶段）。MailKit 在发命令前先检查内部 `FolderCache`（`Dictionary<string, ImapFolder>`），找不到 `""` 键就直接抛异常。

MailKit `QueueGetFoldersCommand` 反编译代码（关键行）：

```csharp
private ImapCommand QueueGetFoldersCommand(FolderNamespace @namespace, ...) {
    string text = EncodeMailboxName(@namespace.Path);  // "" for empty prefix
    // ...
    if (!TryGetCachedFolder(text, out var _))          // FolderCache 中查找 ""
    {
        throw new FolderNotFoundException(@namespace.Path);  // ← 策略2死在这里
    }
    // ... 构造 LIST 命令（永远没执行到）
}
```

而 Thunderbird 使用的正是 `LIST "" "*"` 并且成功了。这说明 mail.seek.li 服务器**可能**能正确响应 `LIST "" "*"`，只是 MailKit 的缓存机制阻止了命令发出。

### MailKit 内部机制：`FolderCache` 是如何填充的

正常服务器的流程：认证后 MailKit 自动发送 `NAMESPACE` → 服务器返回 `(("" "/"))` → MailKit 在 `UpdateNamespaces` 中执行：

```csharp
// ImapEngine.UpdateNamespaces — 处理 NAMESPACE 响应
PersonalNamespaces.Add(new FolderNamespace(c, path));     // 1. 加入集合
if (!TryGetCachedFolder(text, out var folder))
{
    folder = CreateImapFolder(text, FolderAttributes.None, c);  // 2. 创建内部文件夹对象
    CacheFolder(folder);                                         // 3. 注册到 FolderCache
}
```

mail.seek.li 返回 NIL → 上述代码不执行 → `PersonalNamespaces` 空、`FolderCache` 中无 `""` 键 → 后续所有依赖 `GetFoldersAsync` 的调用全部失败。

## 修复方案：通过反射注入根文件夹缓存

### 原理

模拟 MailKit 在收到 `NAMESPACE (("" "/"))` 时的内部行为：通过反射访问 `ImapClient` 的 internal `engine` 字段，调用 `CreateImapFolder` 和 `CacheFolder` 在 `FolderCache` 中注册 `""` 根文件夹节点，然后往 `PersonalNamespaces` 添加对应的命名空间。

这等效于 Thunderbird 的"预设默认命名空间"策略 — 让 MailKit 认为服务器返回了 `(("" "/"))`。

### 实现步骤

在 `DiscoverFoldersAsync` 中，当检测到 `PersonalNamespaces.Count == 0` 时，在现有策略之前插入反射注入：

```csharp
if (client.PersonalNamespaces.Count == 0)
{
    // 通过反射模拟 NAMESPACE (("" "/")) 响应
    var engineField = typeof(ImapClient).GetField("engine",
        BindingFlags.NonPublic | BindingFlags.Instance);
    var engine = engineField.GetValue(client);
    var engineType = engine.GetType();  // ImapEngine (internal class)

    // 获取 Inbox 分隔符，或默认 '/'
    var separator = client.Inbox.DirectorySeparator != '\0'
        ? client.Inbox.DirectorySeparator : '/';

    // 检查 FolderCache 中是否已有 "" 键
    var cacheField = engineType.GetField("FolderCache",
        BindingFlags.Public | BindingFlags.Instance);  // internal readonly Dictionary<string, ImapFolder>
    var cache = (System.Collections.IDictionary)cacheField.GetValue(engine);

    if (!cache.Contains(""))
    {
        // 创建根文件夹对象: CreateImapFolder("", FolderAttributes.None, separator)
        var createMethod = engineType.GetMethod("CreateImapFolder",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var rootFolder = createMethod.Invoke(engine,
            new object[] { "", FolderAttributes.None, separator });

        // 注册到缓存: CacheFolder(rootFolder)
        var cacheMethod = engineType.GetMethod("CacheFolder",
            BindingFlags.Public | BindingFlags.Instance);
        cacheMethod.Invoke(engine, new object[] { rootFolder });
    }

    // 注入命名空间（公开 API）
    client.PersonalNamespaces.Add(new FolderNamespace(separator, ""));
}
```

注入后，现有策略1的代码 `GetFoldersAsync(PersonalNamespaces[0])` 将：
1. `TryGetCachedFolder("")` → **成功**（刚注入的）
2. 构造并发送 `LIST "" "*"` → 服务器响应（Thunderbird 验证过此命令可用）
3. 解析文件夹列表 → 发现所有文件夹

### 风险与注意事项

| 风险 | 说明 | 缓解措施 |
|------|------|---------|
| 反射依赖 MailKit 内部结构 | `engine` 字段名、`CreateImapFolder` 方法签名可能在版本更新时变化 | 整段注入代码用 try/catch 包裹，失败时静默回退到现有的策略2-4级联 |
| `LIST "" "*"` 仍可能失败 | 虽然 Thunderbird 验证过，但 MailKit 的 LIST 解析逻辑可能与 Thunderbird 不同 | 注入只影响缓存检查，LIST 响应解析失败仍会被现有 try/catch 捕获并回退 |
| 仅影响 `PersonalNamespaces` 为空的服务器 | 正常服务器不受影响 | 条件检查 `Count == 0` 确保只在必要时触发 |
| DB 缓存锁死 | 首次发现仅 INBOX 后，SyncManager 不再重试 IMAP 发现 | 这是独立问题，需要在 SyncManager 中添加定期重新发现机制（不在本方案范围内） |

### 预期结果

- **成功场景**：反射注入成功 + `LIST "" "*"` 返回文件夹 → 所有文件夹被发现，用户体验与 Thunderbird 一致
- **失败场景**：反射注入失败或 `LIST "" "*"` 仍无响应 → 自动回退到现有的策略4（INBOX only），行为与当前一致，无退化

## 给服务器管理员的修复建议

1. **NAMESPACE 支持**：确保 `NAMESPACE` 命令返回有效的 Personal Namespace，至少为 `(("" "/"))`（空前缀，`/` 分隔符）
2. **LIST "" "\*"**：确保该命令能正常返回所有用户可访问的文件夹
3. **LIST "" ""**：确保该命令能返回根目录的层级分隔符信息

## 环境信息

| 项目 | 值 |
|------|------|
| IMAP 服务器 | `mail.seek.li:993`（SSL/TLS） |
| 客户端库 | MailKit 4.x（.NET 8） |
| 认证方式 | 密码认证 — 正常通过 |
| `SELECT INBOX` | 正常工作 |
| 文件夹枚举 | 失败（如上所述） |

## 参考标准

- [RFC 3501 — IMAP4rev1](https://datatracker.ietf.org/doc/html/rfc3501) §5.1（INBOX）、§6.3.8（LIST）
- [RFC 2342 — IMAP4 Namespace](https://datatracker.ietf.org/doc/html/rfc2342)
