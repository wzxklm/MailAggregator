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
| 语言 | C# / .NET 8+ | 跨平台开发，Windows 原生体验 |
| UI 框架 | WPF | Windows 原生 UI |
| 邮件协议 | IMAP + SMTP | 统一协议，一套代码覆盖所有邮箱 |
| 邮件库 | MailKit | 支持 XOAUTH2、SOCKS5 代理、IMAP IDLE |
| OAuth | PKCE 流程 | 通过系统浏览器完成 Google/Microsoft 授权 |
| 本地数据库 | SQLite | 缓存邮件、存储 Token、账户配置 |
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
| 授权码 / 应用专用密码 | 用户在邮箱后台生成的专用凭据，非账户主密码 | 用户手动填入凭据，应用加密存储至本地 |

### 主流邮箱接入信息

各邮箱服务商的服务器地址、端口、认证方式均不相同，应用内置预设模板，用户也可手动配置。

| 邮箱 | IMAP 服务器 | IMAP 端口 | SMTP 服务器 | SMTP 端口 | 加密 | 认证方式 |
|---|---|---|---|---|---|---|
| Gmail | imap.gmail.com | 993 | smtp.gmail.com | 465(SSL) / 587(TLS) | SSL/TLS | OAuth2 |
| Outlook/365 企业 | outlook.office365.com | 993 | smtp.office365.com | 587 | STARTTLS | OAuth2 |
| Outlook.com 个人 | outlook.office365.com | 993 | smtp-mail.outlook.com | 587 | STARTTLS | OAuth2 |
| Yahoo | imap.mail.yahoo.com | 993 | smtp.mail.yahoo.com | 465 / 587 | SSL/TLS | 应用专用密码 |
| iCloud | imap.mail.me.com | 993 | smtp.mail.me.com | 587 / 465 | STARTTLS/SSL | 应用专用密码 |
| 163 网易 | imap.163.com | 993 | smtp.163.com | 465 / 994 | SSL | 授权码 |
| QQ 邮箱 | imap.qq.com | 993 | smtp.qq.com | 465 / 587 | SSL | 授权码 |

IMAP 端口统一为 993 + SSL。SMTP 端口因厂商而异（465/SSL 或 587/STARTTLS），需按预设模板配置。

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
MailAggregator/
├── src/
│   ├── MailAggregator.Core/          # 核心逻辑 (.NET 类库)
│   │   ├── Models/                   # 数据模型
│   │   ├── Services/                 # 邮件同步、OAuth、代理
│   │   └── Data/                     # SQLite 数据访问
│   │
│   ├── MailAggregator.Desktop/       # WPF 桌面应用
│   │   ├── Views/                    # XAML 视图
│   │   ├── ViewModels/               # MVVM ViewModel
│   │   └── App.xaml
│   │
│   └── MailAggregator.Tests/         # 单元测试
│
├── docs/                             # 文档
├── .github/workflows/                # CI/CD
└── README.md
```

Core 与 Desktop 分离，核心逻辑在 Ubuntu 上开发测试，UI 层在 CI 中编译。
