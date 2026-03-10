# 2FA 双因素认证管理功能（TOTP）

## 背景

在 MailAggregator 桌面邮件客户端中集成 TOTP 验证码管理器（类似 Google Authenticator）。纯本地计算，不需要联网。密钥使用现有的 AES-256-GCM 加密服务加密存储。

---

## 技术方案

使用 **OtpNet 1.4.0** NuGet 包实现 TOTP 验证码生成（RFC 6238）。遵循现有项目模式：Model → DbContext → Service（接口 + 实现）→ ViewModel → View。密钥通过现有 `ICredentialEncryptionService` 加密。

---

## 简化设计原则

- **仅支持 TOTP**，不支持 HOTP（现代服务几乎都用 TOTP）
- **按创建时间排序**，无需拖拽排序
- **编辑时不允许修改密钥**，只能改 Issuer/Label
- **不做剪贴板自动清除**

---

## 新增文件

### Core — 数据模型（2 个文件）
| 文件 | 用途 |
|------|------|
| `Core/Models/OtpAlgorithm.cs` | 枚举：`Sha1`, `Sha256`, `Sha512` |
| `Core/Models/TwoFactorAccount.cs` | 实体：Id, Issuer, Label, EncryptedSecret, Algorithm, Digits, Period, CreatedAt, UpdatedAt |

### Core — 服务层（4 个文件）
| 文件 | 用途 |
|------|------|
| `Core/Services/TwoFactor/ITwoFactorCodeService.cs` | 接口：GenerateCode, GetRemainingSeconds, ParseOtpAuthUri |
| `Core/Services/TwoFactor/TwoFactorCodeService.cs` | 实现：封装 OtpNet；otpauth:// URI 解析 |
| `Core/Services/TwoFactor/ITwoFactorAccountService.cs` | 接口：Add, AddFromUri, Update, Delete, GetAll |
| `Core/Services/TwoFactor/TwoFactorAccountService.cs` | CRUD 实现；加密密钥 |

### Desktop — ViewModel（3 个文件）
| 文件 | 用途 |
|------|------|
| `Desktop/ViewModels/TwoFactorDisplayItem.cs` | ObservableObject：CurrentCode, RemainingSeconds, ProgressPercentage |
| `Desktop/ViewModels/TwoFactorViewModel.cs` | 主列表 VM：DispatcherTimer（1 秒）更新验证码，复制到剪贴板 |
| `Desktop/ViewModels/AddTwoFactorViewModel.cs` | 添加/编辑对话框 VM：手动输入 + otpauth:// URI 导入 |

### Desktop — 视图（4 个文件）
| 文件 | 用途 |
|------|------|
| `Desktop/Views/TwoFactorWindow.xaml` | 2FA 主窗口：账户列表 + 实时验证码 + 进度条 |
| `Desktop/Views/TwoFactorWindow.xaml.cs` | Loaded → InitializeAsync，Closed → Dispose |
| `Desktop/Views/AddTwoFactorWindow.xaml` | 添加/编辑对话框 |
| `Desktop/Views/AddTwoFactorWindow.xaml.cs` | DialogResult 关闭 |

### 测试（2 个文件）
| 文件 | 用途 |
|------|------|
| `Tests/Services/TwoFactor/TwoFactorCodeServiceTests.cs` | TOTP RFC 测试向量，URI 解析 |
| `Tests/Services/TwoFactor/TwoFactorAccountServiceTests.cs` | CRUD、加密往返 |

---

## 修改文件

| 文件 | 变更内容 |
|------|----------|
| `Core/MailAggregator.Core.csproj` | 添加 OtpNet 1.4.0 |
| `Core/Data/MailAggregatorDbContext.cs` | 添加 DbSet、实体配置、时间戳 |
| `Core/Data/DatabaseInitializer.cs` | `CREATE TABLE IF NOT EXISTS TwoFactorAccounts` |
| `Desktop/App.xaml.cs` | DI 注册新服务和 VM |
| `Desktop/MainWindow.xaml` | 工具栏添加「2FA」按钮 |
| `Desktop/ViewModels/MainViewModel.cs` | 添加 OpenTwoFactorCommand |

---

## 关键设计决策

1. **密钥加密存储** — 复用 `ICredentialEncryptionService`（AES-256-GCM），Dispose 时 `ZeroMemory` 清零
2. **数据库兼容** — `CREATE TABLE IF NOT EXISTS`（项目不使用 EF 迁移）
3. **DispatcherTimer** — 1 秒间隔 UI 线程，周期边界时重新计算 OTP
4. **独立于邮件** — TwoFactorAccount 与 Account 无外键关联

## 实现注意事项

1. **DatabaseInitializer 必须手动建表** — `EnsureCreatedAsync()` 仅在数据库不存在时创建全部表，对已有数据库新增 `DbSet` 不会自动建表。必须在 `InitializeAsync` 中追加 `ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS TwoFactorAccounts (...)")` 手动建表
2. **StampTimestamps 需要扩展** — `MailAggregatorDbContext.StampTimestamps()` 目前只处理 `Account` 和 `EmailMessage`，须为 `TwoFactorAccount` 添加同样的 `CreatedAt`/`UpdatedAt` 自动时间戳逻辑
3. **服务生命周期** — `TwoFactorCodeService` → **Singleton**（无状态，纯 OTP 计算）；`TwoFactorAccountService` → **Scoped**（依赖 DbContext，同 `AccountService` 模式）

---

## UI 布局

```
+--------------------------------------------------+
|  双因素认证管理                                     |
+--------------------------------------------------+
|  +--------------------------------------------+  |
|  | Google                                      |  |
|  | user@gmail.com                              |  |
|  |        123 456              [复制]           |  |
|  |        ████████░░░  剩余 12 秒               |  |
|  +--------------------------------------------+  |
|  | GitHub                                      |  |
|  | myuser                                      |  |
|  |        789 012              [复制]           |  |
|  |        ██████░░░░  剩余 18 秒                |  |
|  +--------------------------------------------+  |
+--------------------------------------------------+
|  状态文本                    [添加] [编辑] [删除]   |
+--------------------------------------------------+
```

---

## 实施顺序

**阶段 1：Core 数据层** — 枚举 → 实体 → DbContext → DatabaseInitializer → OtpNet 包
**阶段 2：Core 服务层** — TwoFactorCodeService → TwoFactorAccountService → 测试
**阶段 3：Desktop UI** — DisplayItem → ViewModel → 窗口 → MainWindow 按钮 → DI 注册
**阶段 4：验证** — `dotnet test` 全量测试
