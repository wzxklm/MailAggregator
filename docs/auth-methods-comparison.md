# 认证方式完整对比报告：MailAggregator vs Thunderbird

> 调研日期：2026-03-09
> 对比对象：[mozilla/releases-comm-central](https://github.com/mozilla/releases-comm-central)
> 核心文件：`OAuth2Providers.sys.mjs`、`OAuth2.sys.mjs`、`OAuth2Module.sys.mjs`

---

## 一、OAuth2 提供商逐一对比

### 1. Google (Gmail)

| 字段 | Thunderbird | 我们 | 状态 |
|------|-------------|------|------|
| clientId | `406964657835-...` | `406964657835-...` | 相同 |
| clientSecret | `kSmqreRr0qwBWJgbf5Y-PjSU` | `kSmqreRr0qwBWJgbf5Y-PjSU` | 相同 |
| authorizationEndpoint | `/o/oauth2/auth` (v1) | `/o/oauth2/v2/auth` (v2) | 均可用 |
| tokenEndpoint | `oauth2.googleapis.com/token` | `oauth2.googleapis.com/token` | 相同 |
| redirectionEndpoint | `http://localhost` | 未配置（代码硬编码 `http://localhost:{port}/`） | 功能等效 |
| usePKCE | **false** | **强制 true（无此字段）** | 不一致，Google 不需要 PKCE |
| scope | `mail + carddav + calendar` | `mail.google.com/` | 合理（我们不需要联系人/日历） |
| additionalAuthParams | `login_hint` | `access_type=offline, prompt=consent` | 我们多了确保获取 refresh_token 的参数 |
| serverHosts | 6 个（含 googlemail.com 变体） | 2 个 | 缺少 `pop/imap/smtp.googlemail.com` |

**评估**：基本正确，可以正常工作。PKCE 多发不会导致失败（Google 会忽略），但不规范。

### 2. Microsoft (Outlook / Office 365)

| 字段 | Thunderbird | 我们 | 状态 |
|------|-------------|------|------|
| clientId | `9e5f94bc-...` | `9e5f94bc-...` | 相同 |
| clientSecret | 无 | 无 | 相同 |
| authorizationEndpoint | 相同 | 相同 | 相同 |
| tokenEndpoint | 相同 | 相同 | 相同 |
| redirectionEndpoint | **`https://localhost`** | 代码硬编码 `http://localhost:{port}/` | **协议不匹配（https vs http），可能导致失败** |
| usePKCE | **true** | 强制 true | 恰好正确 |
| scope | IMAP + SMTP + POP + offline_access | IMAP + SMTP + offline_access | 基本一致 |
| serverHosts | **9 个** | **3 个** | **缺少 6 个主机名** |

**缺少的 Microsoft 主机名**：
- `imap-mail.outlook.com`（个人 Outlook.com IMAP）
- `imap.outlook.com`
- `smtp.outlook.com`
- `pop-mail.outlook.com`
- `pop.outlook.com`
- `smtp-mail.outlook.com`（已有）

**评估**：redirectionEndpoint 协议不匹配是潜在风险。主机名覆盖不全会导致部分 Outlook 用户无法匹配到 OAuth 提供商。

### 3. Yahoo — 严重问题

| 字段 | Thunderbird | 我们 | 状态 |
|------|-------------|------|------|
| clientId | `dj0yJmk9NUtCTWFM...` | `dj0yJmk9MGNoTTQ0...` | **不同！我们用的不是 Thunderbird 的凭据** |
| clientSecret | **`f2de6a30ae123cdbc258c15e0812799010d589cc`** | **缺失** | **将导致 token exchange 直接失败** |
| authorizationEndpoint | 相同 | 相同 | 相同 |
| tokenEndpoint | 相同 | 相同 | 相同 |
| redirectionEndpoint | **`http://localhost`** | 配置了 `https://127.0.0.1` 但**代码未使用** | **双重错误：值错误 + 字段未读取** |
| usePKCE | **false** | 强制 true | **Yahoo 不需要 PKCE，可能被拒绝** |
| scope | `mail-w` | `mail-w` | 相同 |
| serverHosts | 含 `yahoo.co.jp` 变体 | 仅 2 个 | 缺少日本区域主机名 |

**评估**：Yahoo OAuth 认证**必定失败**，原因：缺少 clientSecret + redirectionEndpoint 未被代码使用。

### 4. AOL — 严重问题

| 字段 | Thunderbird | 我们 | 状态 |
|------|-------------|------|------|
| clientId | **与 Yahoo 相同** | `dj0yJmk9MGNoTTQ0...`（独立值） | **Thunderbird 中 AOL 与 Yahoo 共享凭据** |
| clientSecret | **与 Yahoo 相同** | **缺失** | **将导致 token exchange 直接失败** |
| authorizationEndpoint | 相同 | 相同 | 相同 |
| tokenEndpoint | 相同 | 相同 | 相同 |
| redirectionEndpoint | **`http://localhost`** | 配置了 `https://127.0.0.1` 但**代码未使用** | **同 Yahoo** |
| usePKCE | **false** | 强制 true | **同 Yahoo** |

**评估**：AOL OAuth 认证**必定失败**，原因同 Yahoo。

### 5. Fastmail — 严重问题

| 字段 | Thunderbird | 我们 | 状态 |
|------|-------------|------|------|
| clientId | `35f0d5bc` | `35f141ae` | **不同** |
| clientSecret | 无 | 无 | 相同 |
| authorizationEndpoint | 相同 | 相同 | 相同 |
| tokenEndpoint | `/oauth/refresh` | `/oauth/refresh` | 相同 |
| redirectionEndpoint | `http://localhost` | 未配置 | 代码硬编码 localhost 恰好正确 |
| usePKCE | true | 强制 true | 恰好正确 |
| scope | **`protocol-imap + protocol-smtp + protocol-pop + carddav + caldav`** | **`urn:ietf:params:jmap:mail`** | **错误！JMAP scope 无法用于 IMAP/SMTP 认证** |

**Thunderbird 的 Fastmail scope 完整值**：
```
https://www.fastmail.com/dev/protocol-imap
https://www.fastmail.com/dev/protocol-smtp
https://www.fastmail.com/dev/protocol-pop
https://www.fastmail.com/dev/protocol-carddav
https://www.fastmail.com/dev/protocol-caldav
```

**评估**：Fastmail OAuth 认证**会因 scope 权限不足而失败**，`urn:ietf:params:jmap:mail` 是 JMAP 协议的 scope，不授予 IMAP/SMTP 访问权限。

### 6. Comcast/Xfinity — 完全缺失

Thunderbird 支持此提供商，我们未配置。

| 字段 | Thunderbird 值 |
|------|---------------|
| issuer | `login.comcast.net` |
| clientId | `thunderbird-login` |
| clientSecret | 无 |
| authorizationEndpoint | `https://oauth.xfinity.com/oauth/authorize` |
| tokenEndpoint | `https://oauth.xfinity.com/oauth/token` |
| redirectionEndpoint | `http://localhost` |
| usePKCE | true |
| scope | `openid profile email` |
| serverHosts | `imap.comcast.net`, `smtp.comcast.net` |

---

## 二、Thunderbird 完整 OAuth2 提供商字段规范

每个 issuer 条目的标准字段：

| 字段 | 类型 | 说明 | 必填 |
|------|------|------|------|
| `clientId` | string | OAuth2 Client ID | 是 |
| `clientSecret` | string | OAuth2 Client Secret（无则留空） | 否 |
| `authorizationEndpoint` | string | 授权端点 URL | 是 |
| `tokenEndpoint` | string | Token 端点 URL | 是 |
| `redirectionEndpoint` | string | 重定向 URI（每个提供商独立配置） | 是 |
| `usePKCE` | boolean | 是否使用 PKCE S256 | 否（默认 false） |

我们的 `OAuthProviderConfig` 模型**缺少 `usePKCE` 字段**，且 `redirectionEndpoint` 虽然在模型中定义，但在 `OAuthService.PrepareAuthorization()` 中**从未被读取**。

---

## 三、核心代码缺陷

### 缺陷 1：`RedirectionEndpoint` 字段形同虚设

**文件**：`src/MailAggregator.Core/Services/Auth/OAuthService.cs`，第 61 行

```csharp
var redirectUri = $"http://localhost:{listenerPort}/";  // 始终硬编码，忽略 provider.RedirectionEndpoint
```

`OAuthProviderConfig` 模型定义了 `RedirectionEndpoint` 属性，Yahoo 和 AOL 配置中也设置了值，但代码从不读取此字段。影响所有需要非标准 redirect_uri 的提供商。

### 缺陷 2：缺少 `usePKCE` 字段

**文件**：`src/MailAggregator.Core/Models/OAuthProviderConfig.cs`

模型中无 `usePKCE` 字段。`OAuthService.PrepareAuthorization()` 对所有提供商强制发送 `code_challenge` 和 `code_challenge_method=S256` 参数。

Thunderbird 中各提供商的 PKCE 配置：
- Google: **false**
- Microsoft: **true**
- Yahoo: **false**
- AOL: **false**
- Fastmail: **true**
- Comcast: **true**

### 缺陷 3：Yahoo/AOL 缺少 `clientSecret`

**文件**：`src/MailAggregator.Core/oauth-providers.json`

Yahoo 和 AOL 都需要 `clientSecret` 才能完成 token exchange。`OAuthService.cs` 第 153 行在 `clientSecret` 为空时会跳过发送，导致 token exchange 直接被服务器拒绝。

### 缺陷 4：Yahoo/AOL `redirectionEndpoint` 值错误

配置中为 `https://127.0.0.1`，Thunderbird 中为 `http://localhost`。即使修复缺陷 1 使代码读取此字段，值本身也是错误的。

### 缺陷 5：Fastmail scope 使用 JMAP 而非 IMAP/SMTP

配置中 scope 为 `urn:ietf:params:jmap:mail`（JMAP 协议），应为：
```
https://www.fastmail.com/dev/protocol-imap
https://www.fastmail.com/dev/protocol-smtp
```

---

## 四、SASL 认证方式对比

### Thunderbird 支持的全部 SASL 机制

| SASL 机制 | 安全级别 | 密码传输方式 | IMAP | SMTP | POP3 |
|-----------|---------|-------------|------|------|------|
| GSSAPI (Kerberos) | 最高 | 无密码（Kerberos ticket） | 是 | 是 | 是 |
| XOAUTH2 | 高 | 无密码（OAuth2 token） | 是 | 是 | 是 |
| SCRAM-SHA-256 | 高 | 基于 salt 的挑战-响应 | 是 | 是 | 否 |
| SCRAM-SHA-1 | 高 | 基于 salt 的挑战-响应 | 是 | 是 | 否 |
| CRAM-MD5 | 中 | 挑战-响应（MD5 哈希） | 是 | 是 | 是 |
| NTLM | 中 | Windows 集成认证 | 是 | 是 | 是 |
| EXTERNAL | 特殊 | TLS 客户端证书 | 是 | 是 | 否 |
| PLAIN | 低* | 明文（Base64 编码） | 是 | 是 | 是 |
| LOGIN | 低* | 明文（Base64，旧式） | 是 | 是 | 是 |

*PLAIN/LOGIN 在 TLS 加密连接下是安全的。

### 我们的 SASL 支持（通过 MailKit 自动协商）

MailKit 的 `client.AuthenticateAsync(email, password)` 自动按安全级别从高到低协商：

1. SCRAM-SHA-256
2. SCRAM-SHA-1
3. NTLM
4. DIGEST-MD5
5. CRAM-MD5
6. PLAIN
7. LOGIN

**结论**：密码认证方面，MailKit 自动协商覆盖了 Thunderbird 支持的所有主要 SASL 机制，无需手动实现。这是我们的优势。

### Thunderbird 认证协商优先级

**IMAP**：GSSAPI → XOAUTH2 → SCRAM-SHA-256 → SCRAM-SHA-1 → CRAM-MD5 → NTLM → EXTERNAL → PLAIN → LOGIN → IMAP LOGIN 命令

**SMTP**：GSSAPI → XOAUTH2 → SCRAM-SHA-256 → SCRAM-SHA-1 → CRAM-MD5 → NTLM → PLAIN → LOGIN

### Thunderbird 安全降级策略

1. **自动检测模式**：通过 IMAP `CAPABILITY`/SMTP `EHLO`/POP3 `CAPA` 获取服务器支持的机制列表，按优先级选最安全的
2. **安全硬限制**：非加密连接上绝不使用 PLAIN/LOGIN，会弹出安全警告
3. **OAuth2 不降级**：OAuth2 认证失败不会自动降级到密码认证（需用户手动切换）
4. **STARTTLS 降级攻击防护**：如果服务器之前声明支持 STARTTLS 但突然不支持，会警告用户

---

## 五、问题汇总（按严重性排序）

### 直接导致认证失败（5 项）

| # | 提供商 | 问题 | 影响文件 | 修复方案 |
|---|--------|------|---------|---------|
| 1 | Yahoo | 缺少 `clientSecret` | `oauth-providers.json` | 添加 clientSecret |
| 2 | AOL | 缺少 `clientSecret` | `oauth-providers.json` | 添加 clientSecret（与 Yahoo 相同） |
| 3 | Fastmail | scope 是 JMAP 的，不是 IMAP/SMTP 的 | `oauth-providers.json` | 改为 `protocol-imap` + `protocol-smtp` |
| 4 | Yahoo/AOL | `RedirectionEndpoint` 配置了但代码不读取 | `OAuthService.cs:61` | 修复代码使用该字段 |
| 5 | Yahoo/AOL | `redirectionEndpoint` 值也错了 | `oauth-providers.json` | 改为 `http://localhost` |

### 可能导致部分场景失败（5 项）

| # | 提供商 | 问题 | 修复方案 |
|---|--------|------|---------|
| 6 | Microsoft | redirect_uri 协议不匹配（http vs https） | 配置 `redirectionEndpoint: "https://localhost"` 并修复代码使用它 |
| 7 | Microsoft | 缺少 6 个常见 hostname | 补充 `imap-mail.outlook.com` 等 |
| 8 | 全部 | 无 `usePKCE` 字段，对不需要 PKCE 的提供商强制发送 | 在模型中添加 `usePKCE` 字段 |
| 9 | 全部 | Token 过期无 grace time（Thunderbird 提前 30 秒） | 添加提前刷新缓冲 |
| 10 | Google | 缺少 `googlemail.com` 变体的 hostname 映射 | 补充 3 个 hostname |

---

## 六、修复优先级建议

```
P0（立即 — 3 个提供商完全不可用）:
  1. 修复 OAuthService.PrepareAuthorization() 读取 provider.RedirectionEndpoint
  2. 在 OAuthProviderConfig 模型中添加 usePKCE 字段
  3. 修正 Yahoo: 添加 clientSecret、修正 redirectionEndpoint 为 http://localhost、设置 usePKCE=false
  4. 修正 AOL: 同 Yahoo（共享凭据）
  5. 修正 Fastmail scope: 改为 protocol-imap + protocol-smtp

P1（尽快 — 部分用户受影响）:
  6. Microsoft: 配置 redirectionEndpoint 为 https://localhost
  7. Microsoft: 补充缺失的 hostname
  8. 添加 token 刷新 grace time（提前 30-60 秒）

P2（计划）:
  9. Google: 补充 googlemail.com 变体 hostname
  10. Yahoo: 补充 yahoo.co.jp 变体 hostname
```

> 注：Comcast/Xfinity 支持 IMAP 密码认证，不强制 OAuth2，无需添加 OAuth 提供商。
> 动态注册 OAuth 提供商属于低优先级，非核心需求。

---

## 七、总结

| 提供商 | 当前状态 | 主要问题 |
|--------|---------|---------|
| **Google** | 可工作 | PKCE 多发但不影响；缺 googlemail.com 主机名 |
| **Microsoft** | 可能工作 | redirect_uri 协议不匹配是风险点；缺部分主机名 |
| **Yahoo** | **不可用** | 缺 clientSecret + redirectionEndpoint 未使用且值错误 |
| **AOL** | **不可用** | 同 Yahoo |
| **Fastmail** | **不可用** | scope 错误（JMAP vs IMAP/SMTP） |

> 注意：Google（2022 年起）和 Microsoft 365（2022 年起）已禁用第三方应用的密码认证，必须使用 OAuth2。
> Yahoo/AOL 也在逐步推进 OAuth2。因此 OAuth2 配置的正确性至关重要。

**5 个已配置的 OAuth 提供商中，只有 Google 能可靠工作，Microsoft 有风险，Yahoo/AOL/Fastmail 全部不可用。**

密码认证方面，MailKit 自动 SASL 协商完整覆盖了 Thunderbird 支持的所有机制，对仍支持密码认证的邮件服务无问题。
