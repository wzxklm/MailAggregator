# 邮件聚合系统 - 任务分解

本文档为 AI（Claude Code）自动化开发专用，任务按依赖关系和可并行性编排。

## 约定

- **[顺序]**：必须按顺序执行，前置任务完成后才能开始
- **[并行]**：可启用多个 subagent 同时开发，任务之间无代码交叉
- **[验证]**：阶段完成后的检查点，确认代码可编译、测试通过
- 每个任务完成后，更新 CLAUDE.md 中的「开发进度」章节
- 每个阶段完成后，执行 git commit，提交信息简述本阶段内容

---

## 阶段 0：环境搭建 [顺序]

> 目标：在 DevContainer 中构建完整的 .NET 开发环境

### 0.1 安装 .NET 8 SDK

```bash
# 安装 .NET 8 SDK（参考 Microsoft 官方脚本）
wget https://dot.net/v1/dotnet-install.sh -O dotnet-install.sh
chmod +x dotnet-install.sh
./dotnet-install.sh --channel 8.0
# 配置环境变量（写入 ~/.bashrc）
export DOTNET_ROOT=$HOME/.dotnet
export PATH=$PATH:$DOTNET_ROOT:$DOTNET_ROOT/tools
```

### 0.2 创建解决方案与项目骨架

```bash
dotnet new sln -n MailAggregator -o /workspace
dotnet new classlib -n MailAggregator.Core -o src/MailAggregator.Core
dotnet new wpf -n MailAggregator.Desktop -o src/MailAggregator.Desktop
dotnet new xunit -n MailAggregator.Tests -o src/MailAggregator.Tests

dotnet sln add src/MailAggregator.Core
dotnet sln add src/MailAggregator.Desktop
dotnet sln add src/MailAggregator.Tests

dotnet add src/MailAggregator.Desktop reference src/MailAggregator.Core
dotnet add src/MailAggregator.Tests reference src/MailAggregator.Core
```

### 0.3 安装 NuGet 依赖

Core 项目：
```bash
cd src/MailAggregator.Core
dotnet add package MailKit
dotnet add package Microsoft.EntityFrameworkCore.Sqlite
dotnet add package Microsoft.EntityFrameworkCore.Design
dotnet add package Serilog
dotnet add package Serilog.Sinks.File
dotnet add package Microsoft.Extensions.DependencyInjection.Abstractions
dotnet add package System.Security.Cryptography.ProtectedData
```

Desktop 项目：
```bash
cd src/MailAggregator.Desktop
dotnet add package CommunityToolkit.Mvvm
dotnet add package Microsoft.Extensions.DependencyInjection
dotnet add package Microsoft.Web.WebView2
dotnet add package Serilog.Extensions.Logging
```

Tests 项目：
```bash
cd src/MailAggregator.Tests
dotnet add package Moq
dotnet add package FluentAssertions
```

### 0.4 创建目录结构

```
src/MailAggregator.Core/
├── Models/
├── Services/
│   ├── Auth/
│   ├── Mail/
│   ├── Sync/
│   └── Discovery/
└── Data/

src/MailAggregator.Desktop/
├── Views/
├── ViewModels/
├── Converters/
└── Resources/
```

### 0.5 验证环境

```bash
dotnet build MailAggregator.sln  # Core 和 Tests 应能编译（Desktop 在 Linux 上会跳过或报 WPF 警告，这是预期行为）
dotnet test src/MailAggregator.Tests
```

### 0.6 更新 CLAUDE.md

将环境状态、已安装组件、编译验证结果写入 CLAUDE.md。

### 0.7 Git 提交

```
提交信息：搭建项目骨架，配置 .NET 8 开发环境和 NuGet 依赖
```

---

## 阶段 1：数据基础层 [顺序]

> 目标：建立数据模型、数据库访问、凭据加密，这是所有后续功能的基础

### 1.1 数据模型 (Models/)

定义核心实体类，字段和类型由 AI 根据需求文档和架构文档自行设计：

- `Account` — 邮箱账户配置（地址、服务器信息、认证类型、代理配置等）
- `EmailMessage` — 邮件（UID、发件人、收件人、主题、时间、正文引用、已读状态、所属账户与文件夹）
- `EmailAttachment` — 附件信息
- `MailFolder` — IMAP 文件夹（名称、UIDVALIDITY、最大 UID、特殊用途标记）

### 1.2 数据库访问 (Data/)

- EF Core `DbContext`，配置 SQLite 连接
- 数据库迁移初始化

### 1.3 凭据加密服务

- `ICredentialEncryptionService` — AES-256-GCM 加密/解密
- 密钥管理通过 DPAPI（Windows）保护；Linux 开发环境下提供简单的 fallback 实现（如固定密钥），仅用于测试
- 单元测试覆盖加密/解密流程

### 1.4 验证 + 提交

```bash
dotnet build src/MailAggregator.Core
dotnet test src/MailAggregator.Tests
git commit  # 提交信息：实现数据模型、SQLite 数据访问层和凭据加密服务
```

---

## 阶段 2：认证与发现 [并行]

> 目标：实现账户添加所需的三个独立服务。三个任务无代码交叉，可启用 3 个 subagent 并行开发。

### 2.1 AutoDiscovery 服务 ← subagent A

实现 `IAutoDiscoveryService`，按架构文档中的 5 级回退顺序查找 IMAP/SMTP 服务器配置：

1. `autoconfig.{domain}` XML
2. `{domain}/.well-known/autoconfig` XML
3. Thunderbird ISPDB
4. MX 记录反查
5. 返回空结果（由 UI 层引导用户手动配置）

解析 XML 配置格式，提取服务器地址、端口、加密方式、认证类型。
编写单元测试（mock HTTP 响应和 DNS 查询）。

### 2.2 OAuth PKCE 服务 ← subagent B

实现 `IOAuthService`：

- 加载 `oauth-providers.json` 配置文件
- 按 IMAP/SMTP 服务器地址匹配 OAuth 提供商
- PKCE 流程：生成 code_verifier/code_challenge → 启动本地 HTTP listener（随机端口）→ 构建授权 URL → 等待回调获取 authorization code → 交换 token
- Token 刷新逻辑
- Token 加密存储（调用阶段 1 的加密服务）
- 创建 `oauth-providers.json` 模板文件（包含 Gmail、Microsoft、Yahoo、AOL、Fastmail 的配置结构，Client ID 留空）
- 编写单元测试

### 2.3 密码认证服务 ← subagent C

实现 `IPasswordAuthService`：

- 接收用户输入的密码/授权码
- 加密存储（调用阶段 1 的加密服务）
- 提供解密后的凭据用于 IMAP/SMTP 认证
- 编写单元测试

### 2.4 验证 + 提交

等待 3 个 subagent 全部完成后：

```bash
dotnet build src/MailAggregator.Core
dotnet test src/MailAggregator.Tests
git commit  # 提交信息：实现 AutoDiscovery、OAuth PKCE 和密码认证服务
```

---

## 阶段 3：邮件协议层 [顺序]

> 目标：基于 MailKit 实现 IMAP 收邮件和 SMTP 发邮件。此阶段各任务有依赖关系（连接管理 → 收发邮件），需顺序执行。

### 3.1 IMAP 连接管理

- 封装 MailKit `ImapClient` 的连接、认证、断开
- 支持 XOAUTH2 和 PLAIN 两种认证方式（根据账户配置自动选择）
- 支持 per-connection SOCKS5 代理（读取账户的代理配置）
- 连接失败重试（最多 3 次，指数退避）

### 3.2 SMTP 连接管理

- 封装 MailKit `SmtpClient` 的连接、认证、断开
- 认证方式和代理支持同 IMAP

### 3.3 邮件同步服务

实现 `IEmailSyncService`：

- 获取 IMAP 文件夹列表（识别 SPECIAL-USE 标记）
- 首次同步：拉取最近 30 天邮件的信封信息（发件人、主题、时间、摘要）和正文
- 增量同步：基于 UIDVALIDITY + 最大 UID
- UIDVALIDITY 变化时重置文件夹缓存
- 已删除邮件检测
- 标记已读/未读（IMAP `\Seen` 标志）
- 下载附件
- 移动邮件到指定文件夹
- 删除邮件（移动到 Trash 文件夹，通过 `\Trash` SPECIAL-USE 识别）
- 缓存邮件到本地 SQLite

### 3.4 邮件发送服务

实现 `IEmailSendService`：

- 写新邮件（收件人、抄送、密抄、主题、正文、附件）
- 回复（填充 In-Reply-To、References 头，块引用原文）
- 回复全部
- 转发（携带原始附件）
- 选择发件账户

### 3.5 验证 + 提交

```bash
dotnet build src/MailAggregator.Core
dotnet test src/MailAggregator.Tests
git commit  # 提交信息：实现 IMAP 同步和 SMTP 发送服务
```

---

## 阶段 4：账户管理与后台服务 [并行]

> 目标：整合前面的服务，实现账户生命周期管理和后台同步。两个任务可并行。

### 4.1 账户管理服务 ← subagent A

实现 `IAccountService`：

- 添加账户完整流程：输入邮箱 → AutoDiscovery → 匹配 OAuth 或密码认证 → 验证连接 → 保存账户
- 编辑账户配置（服务器、代理等）
- 删除账户（清除本地缓存和凭据）
- 账户列表查询

### 4.2 后台同步管理器 ← subagent B

实现 `ISyncManager`：

- 每个账户独立运行同步任务
- IMAP IDLE 长连接监听新邮件
- IDLE 断开自动重连（指数退避：1s → 2s → 4s → ... → 60s）
- 新邮件触发增量同步
- 全局 CancellationToken 控制退出
- Serilog 日志记录

### 4.3 验证 + 提交

```bash
dotnet build src/MailAggregator.Core
dotnet test src/MailAggregator.Tests
git commit  # 提交信息：实现账户管理和后台同步管理器
```

---

## 阶段 5：WPF UI 层 [顺序]

> 目标：实现桌面 UI。WPF 代码在 Linux 上无法编译运行，但可以编写 XAML 和 ViewModel 代码，由 CI 在 Windows 上编译验证。

### 5.1 应用框架搭建

- `App.xaml.cs`：DI 容器配置，注册所有 Core 层服务
- 主窗口布局：左侧账户/文件夹树 + 右上邮件列表 + 右下邮件预览（经典三栏布局）
- 导航框架

### 5.2 账户管理 UI

- 添加账户向导（输入邮箱 → 自动发现 → 选择认证方式 → 验证 → 完成）
- 账户列表与编辑界面
- 代理配置界面

### 5.3 收件箱 UI

- 统一收件箱视图（多账户邮件混合，按时间倒序）
- 按账户筛选
- 邮件列表项（发件人、主题、时间、摘要、未读标记）
- 邮件预览（WebView2 渲染 HTML，安全策略：禁 JS、拦截外部图片）
- 文件夹切换
- 附件查看/下载

### 5.4 写邮件 UI

- 新邮件窗口（收件人、抄送、密抄、主题、正文编辑器）
- 回复/回复全部/转发（预填充引用原文）
- 选择发件账户
- 添加附件

### 5.5 系统通知

- 新邮件 Toast Notification（发件人、主题）
- 点击通知跳转到对应邮件

### 5.6 提交

```
git commit  # 提交信息：实现 WPF UI 层（账户管理、收件箱、写邮件、通知）
```

---

## 阶段 6：CI/CD [顺序]

> 目标：配置 GitHub Actions，实现自动编译、测试、发布

### 6.1 GitHub Actions 工作流

创建 `.github/workflows/build.yml`：

- 触发条件：push tag `v*`
- Runner：`windows-latest`
- 步骤：restore → build → test → publish（win-x64, self-contained）→ 上传至 GitHub Release

### 6.2 提交

```
git commit  # 提交信息：配置 GitHub Actions CI/CD 工作流
```

---

## Claude Code 工作流规范

### CLAUDE.md 维护规则

每个阶段完成后，更新 CLAUDE.md 中的以下内容：

```markdown
# 3. Development Progress（开发进度）
- 当前阶段：阶段 X
- 已完成：阶段 0 ~ X-1 的简要说明
- 进行中：当前任务描述

# 4. Environment Status（环境状态）
- .NET SDK：8.x 已安装
- NuGet 包：已配置
- 项目可编译：是/否
- 测试通过：X/Y

# 5. Coding Conventions（编码规范）
- 在开发过程中发现的项目特定规则，逐步积累
```

### Subagent 使用规则

- 仅在标记为 **[并行]** 的阶段使用 subagent
- 每个 subagent 负责一个完整的独立服务（包含接口定义、实现、单元测试）
- subagent 不得修改其他 subagent 负责的文件
- 所有 subagent 完成后，主 agent 负责集成验证（编译 + 测试）

### 质量保障流程

1. 每个任务完成后 → 执行 `/simplify` 进行代码质量检查和简化
2. 每次 git commit 后 → 执行安全审查
3. 每个阶段完成后 → `dotnet build` + `dotnet test` 全量验证

### 开发环境限制

- **Linux 上可做**：Core 层所有代码开发、单元测试、集成测试
- **Linux 上不可做**：WPF UI 编译（阶段 5 的代码只能写，不能本地验证编译）
- Core 项目的 TargetFramework 使用 `net8.0`（跨平台），Desktop 项目使用 `net8.0-windows`（仅 Windows）
