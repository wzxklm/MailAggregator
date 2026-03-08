# 邮件聚合系统 - 技术方案

## 项目概述

个人使用的 Windows 桌面邮件聚合客户端，将 Gmail、Outlook、Yahoo 等多家邮箱统一管理。

## 架构

纯客户端架构，无后端服务器。应用直连各邮件服务商，本地缓存邮件数据。

```
┌──────────────────────────────────────┐
│          Windows 桌面应用 (WPF)       │
│                                      │
│  UI 层 (WPF / XAML)                  │
│  ├─ 统一收件箱                        │
│  ├─ 写邮件                           │
│  └─ 账户管理                          │
│                                      │
│  核心逻辑层 (.NET 类库)               │
│  ├─ AutoDiscovery (自动发现服务器)    │
│  ├─ OAuth 认证 (PKCE 流程)            │
│  ├─ IMAP 收邮件 (MailKit)            │
│  ├─ SMTP 发邮件 (MailKit)            │
│  ├─ 代理分流 (per-connection SOCKS5) │
│  └─ 本地缓存 (SQLite)                │
└──────────────────────────────────────┘
       ↕              ↕            ↕
     Gmail          Microsoft    其他 IMAP 邮箱
  (XOAUTH2)       (XOAUTH2)    (授权码/应用专用密码)
```

## 技术栈

| 组件 | 技术选型 | 说明 |
|------|---------|------|
| 语言 | C# / .NET 8+ | Core 层跨平台开发与测试，UI 层仅限 Windows |
| UI 框架 | WPF | Windows 原生 UI |
| MVVM 框架 | CommunityToolkit.Mvvm | 轻量级 MVVM 工具包，提供 ObservableObject、RelayCommand 等 |
| 依赖注入 | Microsoft.Extensions.DependencyInjection | 管理服务生命周期，Core 与 Desktop 解耦 |
| 邮件协议 | IMAP + SMTP | 统一协议，一套代码覆盖所有邮箱 |
| 邮件库 | MailKit | 支持 XOAUTH2、SOCKS5 代理、IMAP IDLE |
| OAuth | PKCE 流程 | 通过系统浏览器完成 Google/Microsoft 授权 |
| 本地数据库 | SQLite (EF Core) | 缓存邮件、存储 Token、账户配置 |
| HTML 渲染 | WebView2 | WPF 中嵌入 WebView2 控件显示 HTML 邮件 |
| 日志 | Serilog | 结构化日志，输出到文件，便于排查问题 |
| 代理 | MailKit 内建 SOCKS5 | 每个邮箱账户可配置独立代理 |

## 邮件协议方案

所有邮箱统一使用 IMAP/SMTP 协议，不使用各厂商专属 REST API。

- 收邮件：IMAP，支持 IMAP IDLE 实时通知
- 发邮件：SMTP
- 多设备状态同步：依赖 IMAP 服务端状态（已读/星标/文件夹）

### IMAP/SMTP 认证方式

| 认证方式 | 说明 | 用户操作 |
|---|---|---|
| OAuth2 (XOAUTH2) | 通过系统浏览器完成 OAuth 授权，使用 token 认证 IMAP/SMTP | 跳转浏览器授权，应用自动获取并管理 token |
| 明文密码 (PLAIN) | 通过 TLS 加密通道传输密码（含授权码/应用专用密码） | 用户手动填入凭据，应用加密存储至本地 |

### 邮箱服务器自动发现 (AutoDiscovery)

参考 Thunderbird 的设计，用户添加账户时只需输入邮箱地址，应用自动查找 IMAP/SMTP 服务器配置，无需手动填写。

查找顺序（逐级回退）：

1. 邮箱域名自带的 autoconfig：请求 `https://autoconfig.{domain}/mail/config-v1.1.xml`
2. 域名 well-known 路径：请求 `https://{domain}/.well-known/autoconfig/mail/config-v1.1.xml`
3. Thunderbird 中央数据库 (ISPDB)：请求 `https://autoconfig.thunderbird.net/v1.1/{domain}`（MPL 2.0 许可，免费供任何客户端使用）
4. MX 记录反查：DNS 查询 MX 记录，根据 MX 域名再尝试以上查找
5. 手动配置：以上均未命中时，用户手动填写服务器信息

自动发现返回的信息包括：IMAP/SMTP 服务器地址、端口、加密方式、支持的认证类型。

应用根据发现的服务器地址自动匹配是否支持 OAuth2。若匹配到内置 OAuth 配置则优先使用 OAuth2，否则使用密码认证。

### 内置 OAuth2 配置

参考 Thunderbird Android 的设计，按 IMAP/SMTP 服务器地址匹配 OAuth 配置。每项配置包含：clientId、scopes、authorizationEndpoint、tokenEndpoint、redirectUri。

OAuth Client ID 需要开发者在各平台的开发者控制台注册获取。配置以 JSON 文件（`oauth-providers.json`）形式存放在应用目录下，不硬编码在源码中。用户也可自行替换为自己注册的 Client ID。

Redirect URI 使用 `http://localhost:{随机端口}` 本地回调。流程：应用临时启动 HTTP listener 监听随机端口 → 打开系统浏览器进行 OAuth 授权 → 浏览器回调到 localhost → 应用获取 authorization code 并交换 token → 关闭 listener。此方案为 Google 和 Microsoft 官方推荐的桌面应用 PKCE 方案。

| 厂商 | 匹配服务器地址 | OAuth 授权端点 |
|---|---|---|
| Gmail | imap.gmail.com, smtp.gmail.com | accounts.google.com |
| Microsoft | outlook.office365.com, smtp.office365.com, smtp-mail.outlook.com | login.microsoftonline.com |
| Yahoo | imap.mail.yahoo.com, smtp.mail.yahoo.com | api.login.yahoo.com |
| AOL | imap.aol.com, smtp.aol.com | api.login.aol.com |
| Fastmail | imap.fastmail.com, smtp.fastmail.com | api.fastmail.com |

## 凭据加密方案

密码、OAuth Token 等敏感信息加密后存储至 SQLite，不以明文落盘。

- 加密算法：AES-256-GCM（认证加密，防篡改）
- 密钥管理：Windows 平台使用 DPAPI（`System.Security.Cryptography.ProtectedData`）保护加密密钥，密钥绑定当前 Windows 用户，其他用户或其他机器无法解密
- 存储格式：`Base64(nonce + ciphertext + tag)`，存入 SQLite 的 TEXT 字段
- 加密范围：密码 / 授权码、OAuth access_token 与 refresh_token

## HTML 邮件渲染方案

使用 WebView2 在 WPF 中渲染 HTML 邮件。

- 依赖：Microsoft.Web.WebView2（NuGet），运行时需要 Edge WebView2 Runtime（Windows 10+ 已内置）
- 安全策略：
  - 默认拦截外部图片加载（防邮件追踪），用户可手动点击加载
  - 禁用 JavaScript 执行（`CoreWebView2Settings.IsScriptEnabled = false`）
  - 禁止页面导航到外部链接，外部链接用系统默认浏览器打开
- 纯文本邮件以 `<pre>` 标签包裹后通过 WebView2 显示，保持统一渲染路径

## 邮件操作

### 删除邮件

将邮件移动到 Trash 文件夹（而非 IMAP `\Deleted` 标记 + EXPUNGE），用户可在垃圾箱中找回。通过 IMAP `LIST` 命令的 `SPECIAL-USE` 扩展（`\Trash` 属性）自动识别各邮箱的垃圾箱文件夹，不硬编码文件夹名称。

### 回复与转发

回复和转发邮件时，使用块引用风格引用原文：

```
--- Original Message ---
From: sender@example.com
Date: 2026-03-08
Subject: Hello

原文内容...
```

HTML 邮件中使用 `<blockquote>` 标签包裹原文。回复时自动填充 `In-Reply-To` 和 `References` 邮件头，维护邮件会话线索。

### 统一收件箱排序

多账户邮件混合后按邮件 `Date` 头字段时间倒序排列（最新在上）。

## 邮件同步策略

### 增量同步

基于 IMAP UID 机制实现增量同步，避免每次全量拉取。

- 首次同步：拉取最近 30 天的邮件（可配置），缓存至本地 SQLite
- 后续同步：记录每个文件夹的 `UIDVALIDITY` 和最大 `UID`，仅拉取 UID 大于本地最大值的新邮件
- `UIDVALIDITY` 变化时（服务器重置），清除该文件夹本地缓存并重新同步
- 已删除邮件检测：对比本地 UID 集合与服务器 UID 集合，移除本地多余记录

### 并发模型

每个邮箱账户独立运行同步任务，互不阻塞。

- 使用 `Task` + `async/await` 管理并发，不手动创建线程
- 每个账户维护一个 IMAP 长连接用于 IDLE 监听，收到新邮件通知后触发增量同步
- IDLE 连接断开时自动重连（指数退避策略：1s → 2s → 4s → ... → 最大 60s）
- 同步操作（拉取邮件列表、正文等）使用独立的短连接，操作完成后断开
- 全局 `CancellationToken` 控制应用退出时优雅关闭所有连接

### 新邮件通知

- IMAP IDLE 收到新邮件时，通过 Windows 系统通知（Toast Notification）提示用户
- 通知内容：发件人、主题
- 点击通知跳转到对应邮件

## 错误处理与日志

- 网络超时 / 连接失败：自动重试（最多 3 次，指数退避），重试失败后在 UI 上标记账户状态为"连接失败"，不阻塞其他账户
- OAuth Token 过期：自动使用 refresh_token 刷新；refresh_token 失效时提示用户重新授权
- IMAP 认证失败：提示用户检查密码 / 授权码，标记账户状态为"认证失败"
- 日志框架：Serilog，输出到本地文件（按天滚动，保留 7 天），记录连接、同步、错误等关键事件

## 代理方案

应用级代理，不影响系统网络。MailKit 原生支持 per-connection SOCKS5 代理，每个邮箱账户可独立配置代理地址，也可设为直连。

## 开发环境

| 项目 | 详情 |
|------|------|
| 系统 | Ubuntu 22.04 (DevContainer, CUDA 12.4) |
| 容器 | Docker DevContainer |
| .NET SDK | 待安装 (.NET 8+) |
| IDE | VSCode |
| 版本控制 | Git + GitHub |

在 Ubuntu 上可完成的工作：
- 核心逻辑开发（MailKit 邮件收发、OAuth、代理、SQLite）
- 单元测试与集成测试
- 非 UI 部分的调试

在 Ubuntu 上无法完成的工作：
- WPF UI 编译与调试（WPF 仅限 Windows）

## 构建与发布 (GitHub Actions CI/CD)

本地开发推送代码后，由 GitHub Actions 在 Windows Runner 上完成编译和打包。

### CI/CD 流程

```
git push
    ↓
GitHub Actions (windows-latest)
    ↓
1. 还原依赖 (dotnet restore)
2. 编译项目 (dotnet build)
3. 运行测试 (dotnet test)
4. 发布为独立 .exe (dotnet publish, win-x64, self-contained)
5. 打包安装程序 (可选, MSIX / Inno Setup)
6. 上传至 GitHub Release
```

推送带版本号的 tag（如 v1.0.0）时触发构建，产物自动上传至 GitHub Release。

## 项目结构

```
/workspace/                           # 仓库根目录
├── src/
│   ├── MailAggregator.Core/          # 核心逻辑 (.NET 类库)
│   │   ├── Models/                   # 数据模型
│   │   ├── Services/                 # 邮件同步、OAuth、代理、AutoDiscovery
│   │   └── Data/                     # SQLite 数据访问、加密存储
│   │
│   ├── MailAggregator.Desktop/       # WPF 桌面应用
│   │   ├── Views/                    # XAML 视图
│   │   ├── ViewModels/               # MVVM ViewModel
│   │   └── App.xaml
│   │
│   └── MailAggregator.Tests/         # 单元测试
│
├── MailAggregator.sln                # 解决方案文件
├── docs/                             # 文档
├── .github/workflows/                # CI/CD
└── README.md
```

Core 与 Desktop 分离，核心逻辑在 Ubuntu 上开发测试，UI 层在 CI 中编译。
