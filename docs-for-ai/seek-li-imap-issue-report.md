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

## 我们的临时解决方案

当检测到 Personal Namespaces 为空时，客户端跳过文件夹枚举，仅同步 INBOX。这保证了基本功能可用，但降低了用户体验。

## 修复建议

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
