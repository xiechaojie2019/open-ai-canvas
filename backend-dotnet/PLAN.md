# 影策后端 Go → .NET 8 迁移计划清单

> 目标：用 .NET 8 完整重写 `backend/`（Go 1.25 / Gin / GORM），**保持 HTTP 出入参、业务错误码、Cookie 会话、数据库表结构与数据完全一致**，前端 `web/` 零改动即可对接。
>
> 源端事实基线（2026-09-16 实测，由 `backend/cmd/schema-dump` 用 GORM 自身解析器导出）：
> - Go 文件 **490** 个，非测试代码约 **10.5 万行**
> - HTTP 路由 **329** 条（GET 135 / POST 117 / PATCH 34 / DELETE 28 / PUT 13 / Any 2）
> - 持久化表 **80** 张（`Models()` 79 张 + `schema_migrations` 1 张）
> - 数据库列 **958** 列、索引 **421** 个（其中 1 个 EF Core 无法表达，见下）
> - 分层：`cmd/` → `internal/handler`(9.0k) → `internal/service`(别名层) → `internal/app`(68.3k 组合根) → 域包 → `internal/repository`(9.8k) → `internal/database`(1.7k)
>
> **基线工件**：`schema-dump.json`（GORM 权威导出）、`suppressed-indexes.json`（EF 无法表达的索引），
> 由 `tests/OpenAICanvas.Tests/Persistence/` 的结构一致性测试直接消费。

---

## 〇、首轮核对修正（相对初版计划的偏差）

初版计划基于静态阅读，用 GORM 解析器复核后修正如下：

| # | 初版说法 | 复核结论 | 影响 |
| --- | --- | --- | --- |
| 1 | 82 张表 | **80 张**（`Models()` 79 + `schema_migrations` 1） | 阶段 1 工作量修正 |
| 2 | — | `schema_migrations` **不在** `Models()` 内，由 `database/migrations.go` 单独 `AutoMigrate` | 必须计入，否则结构不一致 |
| 3 | 路由只在 `/api` 下 | 存在根级路由 `GET /oauth/linuxdo/callback` | 阶段 2 必须挂在根路由 |
| 4 | `NoRoute` = 404 兜底 | **是短代理** `/api/{channelId}/{providerPath}`，含 24 个保留前缀黑名单 | 阶段 10.8 是真实模块，不是空壳 |
| 5 | 329 条 GET/POST 等 | 另有 **2 条 `.Any()`**：`/api/ai/system/:channelId/*path`、`/api/ai/custom` | 必须支持全方法 |
| 6 | 4 处 `go:embed` 只列了 1 处 | 共 **4 处**：openapi.yaml、agent 策略 md ×2、protocol docs ×21、skills seed json | 需全部转为 `EmbeddedResource` |
| 7 | 环境变量已齐 | 缺 `CANVAS_REGISTRATION_ENABLED`、`CANVAS_PUBLIC_BASE_URL`、`CANVAS_OFFICIAL_PLUGIN_DIR`、`ENABLE_PROVIDER_PLUGINS`、`CANVAS_CHANNEL_CIRCUIT_FAILURES/SECONDS`、`HTTP(S)_PROXY`/`NO_PROXY`、`CANVAS_UPDATER_*` 13 个、`SQLITE_SOURCE_PATH` | 阶段 0 补全 |
| 8 | 支付插件进程内调用 | 是**独立进程 + HTTP RPC**（协议版本 `yingce.payment/v1`） | 阶段 8.3 架构修正 |
| 9 | — | `LogicalModelPriceSKU` 是非持久化辅助类型，不建表 | 阶段 1.4 说明 |

### 生成期新发现的两个真实结构差异（已修正）

| # | 问题 | 结论 |
| --- | --- | --- |
| 10 | Go 的 `int` 该映射成 C# `int` 还是 `long`？ | **必须 `long`**。Go 的 `int` 在 amd64/arm64 上是 64 位，GORM 的 PostgreSQL dialector 对 `schema.Int` 一律输出 `bigint`；映射成 C# `int` 会让 EF 生成 `integer`，产生 53 列的**真实结构差异**。JSON 契约不受影响（两边都是数字）。 |
| 11 | 同一列上的多个索引 | EF Core 以"列集合"标识索引，同一组列只能有一个。全库 421 个索引中有 **1 处**冲突（`prompt_templates.enabled` 上并存 `idx_prompt_templates_enabled` 与 `idx_prompt_template_active`），EF 模型无法表达，记入 `suppressed-indexes.json`，改由 Schema 对齐阶段用原始 SQL 补建。 |

---

## 〇之二、持久化层改用 Dapper（2026-09-17 决策）

**决策**：不使用 EF Core，改用 **Dapper + 手写 SQL**。实体层不受影响——
生成时已刻意让 `OpenAICanvas.Domain` 不引用任何 ORM 包，80 个实体是纯 POCO，
没有 `[Key]`/`[Column]`/`[NotMapped]` 特性，两种方案都能直接复用。

**依据（本项目实测的 GORM 调用分布）**

| 指标 | 数值 | 说明 |
| --- | --- | --- |
| `.Create()` / `.First()` / `.Find()` / `.Count()` | 378 / 232 / 157 / 135 | ORM 形状的批量操作 |
| `db.Raw()` + `db.Exec()` | 仅 9 | Go 侧几乎没有手写 SQL |
| `.Preload()` / `.Association()` | 0 | 无 eager loading |
| `clause.OnConflict`（upsert） | 27 | EF Core 8 无原生 upsert |
| `clause.Locking`（行锁） | 16 | EF Core 需 `FromSqlRaw` 绕过 |
| 软删除表引用 | 76 处 | 3 张表，Dapper 下需强制注入过滤 |

**Dapper 方案下的结构来源**

表结构不再由 ORM 推断，而是由生成器从 `schema-dump.json` 产出静态 DDL：

- `Persistence/Schema/SqliteSchema.sql` — 80 表 + 425 索引
- `Persistence/Schema/PostgresSchema.sql` — 同上，列类型取 GORM postgres dialector 输出

这比 EF 生成的 DDL 更准：EF 会把带长度的字符串映射成 `TEXT`，而 GORM 在 PostgreSQL 下
生成 `varchar(n)`——改用静态 DDL 后与 Go 逐字一致。

**关键实现**

| 关注点 | 实现 | 依据 |
| --- | --- | --- |
| 软删除 | `SoftDelete.Apply()` 默认注入 `deleted_at IS NULL`，需显式 `includeDeleted: true` 才包含已删除记录 | 对应 GORM 的自动过滤与 `Unscoped()` |
| 行锁 | `SqlDialect.ForUpdate()` 在 SQLite 下返回空串 | `gorm.io/driver/sqlite` 的 "FOR" 构建器显式跳过，注释为 "SQLite3 does not support row-level locking" |
| upsert | `SqlDialect.OnConflictDoNothing()` | 对应 27 处 `clause.OnConflict` |
| 迁移版本 | `SchemaMigrationCatalog` 的 15 个版本，名称与校验和与 Go 逐字一致 | 保证 .NET 版能接管 Go 版已迁移的数据库 |
| 历史 6/7 顺序 | `PlanFor(version6Record)` | 对应 Go 的 `migrationsForDatabase` |
| 增量升级 | 列定义从 DDL 脚本解析后构造 `ALTER TABLE ADD COLUMN` | 避免在代码里重复写一份类型导致漂移 |

**已验证**：全新 SQLite 库迁移后 80 表 / 958 列 / 425 索引 / 15 条迁移记录，
表名与列数与 GORM 基线完全一致，`health/ready` 返回 `schema.current=15, ready=true`。

---

## 一、契约红线（任何模块都不得违反）

这 8 条是"接口一模一样"的定义，逐条对应源码位置，翻译时作为验收标准。

| # | 契约 | Go 源 | .NET 8 实现方式 |
| --- | --- | --- | --- |
| C1 | 成功响应 `{code:0, data:T, msg:"ok"}`；失败 `{code, data:null, msg, reason?}` | `handler/response.go` | 统一 `ApiEnvelope` + `IResultFilter` / 异常中间件 |
| C2 | `reason` 仅在有值时出现；`reason` 取值集合固定 12 个 | `kernel/error_codes.go` | `ErrorReason` 静态类 + `JsonIgnore(WhenWritingNull)` |
| C3 | 失败时 HTTP status 与业务 code 双表达，code 默认 = status | `response.go: writeFailure` | 同上 |
| C4 | `code=42901` 限流带 `Retry-After` 头；`40301` 配额 | `response.go: failService` | 自定义 `EmailCodeCooldownException` |
| C5 | 5xx 绝不回显原始错误，只回固定文案 | `response.go: safeInternalErrorMessage` | 4 条固定文案表 |
| C6 | 会话 Cookie `open_ai_canvas_session`，Path=`/`，HttpOnly，SameSite=Lax，Secure 跟随 `X-Forwarded-Proto` | `auth/auth.go:21`、`handler/auth.go:1174` | 手写 `Set-Cookie`，不用 `CookieAuthenticationHandler` 默认行为 |
| C7 | 所有 `/api/**` 路径逐字一致，含 `NoRoute` 兜底转发 | `handler/api.go`、`main.go:115` | 显式路由表 + `MapFallback` |
| C8 | CORS：允许头/方法/暴露头固定，非法 Origin 直接 403 | `main.go:211-318` | 自定义中间件（非 `UseCors` 默认行为） |

### 高风险点（必须专项验证）

1. **`omitempty` 语义差异（最高风险）**
   Go 的 `json:"x,omitempty"` 对 `""` / `0` / `false` 也会省略；`System.Text.Json` 默认只省略 `null`。
   → 出参会多出字段。方案：为实体统一挂自定义 `JsonIgnoreCondition` 策略，或对每个 tag 生成显式 `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]`。**逐字段核对，不可全局一刀切**（`false` 有时必须输出）。

2. **`time.Time` 序列化**
   Go 输出 RFC3339 纳秒（`2026-09-16T23:22:25.123456789+08:00`）；STJ 默认只到 7 位小数且可能省略时区。需自定义 `JsonConverter<DateTime>`。

3. **`int64` 精度**
   Go 的 `int64` 直接输出数字；STJ 对 `long` 也输出数字，安全。但若误用 `double` 会变科学计数法 → 金额、微积分字段必须 `long`。

4. **GORM 软删除**
   所有带 `gorm.DeletedAt` 的模型需 EF Core 全局查询过滤器 `HasQueryFilter(e => e.DeletedAt == null)`，并保留 `Unscoped()` 等价入口。

5. **GORM `AutoMigrate` ≠ EF Core Migrations**
   `AutoMigrate` 是运行时增量对齐；EF Core 需生成迁移。方案：首版用 EF Core `EnsureCreated` 对齐空库 + 手写 `SchemaAlignment` 脚本处理历史升级路径（含 `widenPostgresAssetIDColumns` 等 6 段特殊迁移）。

6. **蛇形命名**
   GORM 默认表名复数蛇形、列名蛇形。EF Core 需全局 `UseSnakeCaseNamingConvention()`，并逐个显式指定 `ToTable()` 防止复数化差异。

7. **`gin.H` 的键顺序是字典序（易踩）**
   Go 版大量使用 `gin.H{...}`（即 `map[string]any`）构造 `data` 或响应体，而 `encoding/json`
   **对 map 键按字典序输出**。因此 C# DTO 的属性声明顺序必须刻意排成字典序，否则字段顺序对不上。
   例：`gin.H{"status":"ok","build":...}` 实际输出 `{"build":...,"status":"ok"}`。
   已在 `SystemStatusDtos.cs` 的 `HealthLiveDataDto` / `SystemVersionDataDto` 落实。
   注意区分：Go **struct** 按字段声明顺序输出，**map** 按字典序输出。

### 本机构建环境注意

本机 WorkBuddy 的 bash / PowerShell 会话会剥掉 `PROGRAMFILES(X86)` / `PROGRAMFILES` /
`PROGRAMDATA` / `APPDATA` 等 Windows 环境变量。NuGet 在
`NuGet.Common/PathUtil/NuGetEnvironment.cs` 的 `MachineWideSettingsBaseDirectory` 分支读取
`PROGRAMFILES(X86)`，为空时 `Path.Combine(null, "NuGet")` 抛
`Value cannot be null. (Parameter 'path1')`，导致**任何** `dotnet restore` 失败
（新建空白项目同样复现，与本仓库代码无关）。

→ **所有 dotnet 命令必须经由 `scripts/dotnet.sh` 转发。**

---

## 二、目标解决方案结构

```
backend-dotnet/
├── OpenAICanvas.sln
├── Directory.Build.props            # net8.0 / Nullable / LangVersion
├── PLAN.md                          # 本文件
├── CHECKLIST.md                     # 进度勾选表
├── src/
│   ├── OpenAICanvas.Domain/         # ← internal/model + internal/kernel（80 实体 + 枚举 + AppError）
│   ├── OpenAICanvas.Persistence/    # ← internal/database + internal/repository（EF Core 8）
│   ├── OpenAICanvas.Auth/           # ← internal/auth（会话、密码、OAuth、邮箱验证码）
│   ├── OpenAICanvas.Canvas/         # ← internal/canvas + canvas/capability
│   ├── OpenAICanvas.Prompts/        # ← internal/prompts
│   ├── OpenAICanvas.Skills/         # ← internal/skills
│   ├── OpenAICanvas.Payment/        # ← internal/payment（支付宝/微信/对账）
│   ├── OpenAICanvas.Platform/       # ← internal/platform（限流、worker、运行时策略、缓存）
│   ├── OpenAICanvas.Assets/         # ← internal/assets（资源引用解析）
│   ├── OpenAICanvas.Outbound/       # ← internal/outbound（SSRF 防护 HTTP 客户端）
│   ├── OpenAICanvas.Protocol/       # ← internal/protocol（模型协议适配）
│   ├── OpenAICanvas.Providers/      # ← internal/provider + internal/generation
│   ├── OpenAICanvas.Application/    # ← internal/app（业务组合根，最大块）
│   ├── OpenAICanvas.Web/            # ← internal/handler + cmd/server（ASP.NET Core 8）
│   └── OpenAICanvas.Tools/          # ← cmd/migrate-*、payment-*、host-updater
└── tests/
    └── OpenAICanvas.Tests/          # ← 对应 Go *_test.go 的契约回归测试
```

**技术选型**

| 用途 | Go | .NET 8 |
| --- | --- | --- |
| Web 框架 | Gin 1.12 | ASP.NET Core 8（Minimal API 显式路由） |
| 数据访问 | GORM 1.31 | **Dapper 2.1 + 手写 SQL**（建表脚本由 GORM 权威导出生成） |
| 缓存/协调 | go-redis v9 | StackExchange.Redis |
| 对象存储 | AWS SDK / 七牛 / 腾讯 COS / 火山 | `AWSSDK.S3` / `Qiniu.SDK` / `Tencent.QCloud.Cos.Sdk` / 火山 REST |
| 密码哈希 | `golang.org/x/crypto` | `Konscious.Security.Cryptography`（Argon2）或 `BCrypt.Net`（按源码实际算法定） |
| JSON | `encoding/json` | `System.Text.Json` + 自定义 Converter |
| UUID | `google/uuid` | `Guid`（注意 Go 是无连字符小写十六进制） |

---

## 三、分阶段模块清单（12 个阶段）

### 阶段 0 · 工程骨架与契约基座  ← 先做，其余全部依赖它

| # | 模块 | 对应 Go | 交付物 | 状态 |
| --- | --- | --- | --- | --- |
| 0.1 | 解决方案与项目分层 | — | `OpenAICanvas.sln` + 15 个项目 | ✅ |
| 0.2 | 错误码与原因常量 | `kernel/error_codes.go` | `ErrorCodes` / `ErrorReason` | ✅ |
| 0.3 | `AppError` 结构化错误 | `kernel/errors.go` | `AppError` + 工厂方法 6 个 | ✅ |
| 0.4 | 响应信封 | `handler/response.go` | `ApiEnvelope` + `EnvelopeResultFilter` | ✅ |
| 0.5 | 全局异常投影 | `handler/response.go: failService` | `ExceptionHandlingMiddleware` | ✅ |
| 0.6 | JSON 序列化契约 | 全量 tag | `CanvasJsonOptions`（camelCase + omitempty 等价 + time 格式） | ✅ |
| 0.7 | 请求关联中间件 | `handler/request-context.go` | `RequestCorrelationMiddleware`（`X-Request-ID` / `X-Canvas-Trace-ID`） | ✅ |
| 0.8 | CORS 中间件 | `main.go:211-318` | `CanvasCorsMiddleware`（逐字照搬白名单与 403 行为） | ✅ |
| 0.9 | 环境变量契约 | 全量 `os.Getenv` | `CanvasEnvironment` 强类型封装（核心变量已实现，缺 20+ 见待确认 #16） | 🟡 |
| 0.10 | 启动编排 | `cmd/server/main.go` | `Program.cs`（迁移 → 种子 → 监听 → 优雅退出） | ✅ |
| 0.11 | 健康/系统状态路由 | `cmd/server/system_status.go` | 5 条路由 | ✅ |
| 0.12 | OpenAPI 静态资源 | `handler/openapi_embed.go` + `openapi.yaml` | 原样内嵌输出 | ✅ |

### 阶段 1 · 领域模型与持久化层

| # | 模块 | 对应 Go | 内容 | 状态 |
| --- | --- | --- | --- | --- |
| 1.1 | 枚举与常量 | `model/models.go` | 20 个字符串枚举类型 + 全部常量 | ✅ |
| 1.2 | 身份与账号实体 | `model/models_identity.go` | `User` `AuthSession` `UserIdentity` `OAuthState` `EmailVerificationCode` | ✅ |
| 1.3 | 渠道与模型实体 | `model/models_channel.go` | `ModelChannel` `ChannelModel` `ChannelModelPriceTier` `IDSequence` | ✅ |
| 1.4 | 逻辑模型实体 | `model/models_logical_model.go` | `LogicalModel` `LogicalModelRevision` `LogicalModelRoute` `RouteAttempt` `ApiCallLog` `ModelPricing` | ✅ |
| 1.5 | 计费与财务实体 | `model/models_finance.go` | `CreditAccount` `CreditLedgerEntry` `BillingOrder` `TopupProduct` | ✅ |
| 1.6 | 支付实体 | `model/models_payment.go` | `PaymentProviderConfig` `PaymentOrder` `PaymentNotification` `PaymentReconciliationRun/Item` `RedeemBatch` `RedeemCode` | ✅ |
| 1.7 | 平台与设置实体 | `model/models_platform.go` | `SystemSetting` `PluginPlatformState` `UserPluginState` `StorageLocation` `UserOSSSetting` `UserDailyUploadUsage` `ArkPrivateAssetBinding` | ✅ |
| 1.8 | 项目/短剧实体 | `model/models_project.go` | `Project` `ProjectUnit` `Shot` `ShotRevision` `ShotArtifact` `Asset` 系列 10 张 + `Workflow*` 5 张 | ✅ |
| 1.9 | 任务与创作实体 | `model/models_task.go` `models_creation.go` | `Task` `TaskTextDelta` `TaskLog` `Result` `CreationRun` `CreationSubmission` | ✅ |
| 1.10 | 其余实体 | `model/*.go` | `CloudAgent*` `AgentProfile` `Skill*` `Resource*` `Announcement*` `CanvasShare` `StyleProfile` `VoiceProfile` | ✅ |
| 1.11 | `AppDbContext` | `database/schema.go: Models()` | ✅ 80 实体列映射（`EntityMetadata` + `SqlBuilder`，Dapper 方案） | ✅ |
| 1.12 | 实体配置 | GORM tag | 逐表 `IEntityTypeConfiguration`（主键/长度/索引/唯一约束） | ☐ |
| 1.13 | 连接与连接池 | `database/database.go` | sqlite / postgres 双驱动 + 池配置 | ✅ |
| 1.14 | Schema 对齐与迁移 | `database/migrations.go` `schema.go` | 6 段特殊迁移等价实现 | ✅ |
| 1.15 | 仓储层 | `repository/*.go` (40 文件) | 🟡 按需逐个迁移（当前 162/498 方法，随业务节点推进） | 🟡 |

### 阶段 2 · 认证与用户（47 条路由）

| # | 模块 | 对应 Go | 状态 |
| --- | --- | --- | --- |
| 2.1 | 会话签发与校验 | `auth/` (16 文件) | ✅ |
| 2.2 | 注册 / 登录 / 登出 | `handler/auth.go` | ✅ |
| 2.3 | 邮箱验证码 | `handler/auth.go` + 冷却异常 | ✅ |
| 2.4 | OAuth 回调 | `handler/auth.go:208` | ✅ |
| 2.5 | 公开认证设置 | `auth/settings` | ✅ |
| 2.6 | 管理员用户 CRUD + 批量禁用 | `handler/auth.go:227+` | ✅ |
| 2.7 | 用户详情 / 账本 / 任务查询 | `handler/auth.go` | ✅ |
| 2.8 | 渠道订单 | `handler/channel_order.go` | ✅ |

### 阶段 3 · 渠道、逻辑模型与路由调度

| # | 模块 | 对应 Go | 状态 |
| --- | --- | --- | --- |
| 3.1 | 渠道管理 | `app/channel*.go` | 🟡 列表/创建/复制/更新/删除/排序已通；test 路由待做 |
| 3.2 | 渠道模型 + 价格档 | `app/channel_models.go` | 🟡 列表/排序/价格档附着已通；保存/删除/fetch/import/test 待做 |
| 3.3 | 逻辑模型与版本 | `app/logical_models.go` | 🟡 公开目录/管理端 CRUD/模拟/报价已通；工作流选路待做 |
| 3.4 | 模型目录发现 | `provider/registry.go` | 🟡 元数据注册表已接（内置 13 协议+插件包）；声明式执行引擎待做 |
| 3.5 | 模型能力矩阵 | `app/model_capability.go` | ✅ 读路径完成（解码/归一化/投影/校验） |
| 3.6 | 路由目录快照与健康度 | `app/model_router.go` | ✅ 快照/匹配/选路/模拟完成（Redis 协调待接） |
| 3.7 | 模型 SKU 选择器 | `model/model_sku.go` | ✅ |
| 3.8 | 系统模型种子 | `EnsureSystemChannelModels` | ✅ |

### 阶段 4 · 任务与生成（14 + 12 条路由）

| # | 模块 | 对应 Go | 状态 |
| --- | --- | --- | --- |
| 4.1 | 任务 CRUD | `handler/routes.go` | ☐ |
| 4.2 | 任务文本增量与 SSE 流 | `app/text_replay.go` | ☐ |
| 4.3 | 任务重试 / 取消 / 上游查询 | `app/task_*.go` (12 文件) | ☐ |
| 4.4 | Worker 调度与租约 | `app/task_worker.go` | ☐ |
| 4.5 | 计费协调（预扣/结算/退款） | `app/billing.go` | ☐ |
| 4.6 | 文本协议 | `app/provider_text.go` | ☐ |
| 4.7 | 图片协议 | `app/provider_image.go` | ☐ |
| 4.8 | 视频协议（含遗留） | `app/provider_video.go` | ☐ |
| 4.9 | 音频协议 | `app/provider_audio.go` | ☐ |
| 4.10 | HTTP 客户端与声明式协议 | `app/provider_http_client.go` `provider_protocol.go` | ☐ |
| 4.11 | 工作流 Provider | `app/workflow_provider.go` (2155 行) | ☐ |
| 4.12 | RunningHub 集成 | `app/runninghub_management.go` | ☐ |
| 4.13 | 视频转码与播放副本 | `app/video_transcode.go` | ☐ |
| 4.14 | 时间轴转录 / 渲染 | `app/transcription*.go` `timeline*.go` | ☐ |
| 4.15 | 创作运行与提交 | `app/creation*.go` | ☐ |

### 阶段 5 · 资源、素材与存储

| # | 模块 | 对应 Go | 状态 |
| --- | --- | --- | --- |
| 5.1 | 资源上传（含分片） | `handler/resource_upload_session.go` | ✅ 三件套路由 + 幂等/配额/本地落盘完成（对象存储通道待 #25） |
| 5.2 | 资源引用解析 | `internal/assets` | ✅ 引用收集/校验/ID 解析完成（UserDataService 内） |
| 5.3 | 资源删除与引用检查 | `app/resource_delete.go` | ✅ 判定链/Outbox/本地物理删除完成（云删除 worker 待接，#25） |
| 5.4 | 资源清理作业 | `repository/resource_cleanup.go` | ☐ |
| 5.5 | 素材库 | `app/asset*.go` `repository/asset_library.go` | 🟡 全量 CRUD/分页/facets/分类/移动完成；资源上传已通，素材库侧的资源下发仍待接线 |
| 5.6 | 存储位置与 OSS 设置 | `app/storage*.go` | ☐ |
| 5.7 | Eagle 集成 | `app/eagle.go` | ☐ |
| 5.8 | 用户数据导出/分页 | `handler/user_data.go` (31 条) | 🟡 画布 CRUD/素材/快照/分享、资源上传（5.1）与文件下发（`/resources/:id/file`、`/public/resources/:id/file`，含 ETag/Range）已通（约 22 条）；OSS 相关与导出待做 |

### 阶段 6 · 项目与短剧工作流（47 条路由）

| # | 模块 | 对应 Go | 状态 |
| --- | --- | --- | --- |
| 6.1 | 项目 CRUD | `app/project.go` | ✅ 列表/分页/创建/更新/删除级联（默认工作流待接，#27） |
| 6.2 | 项目单元（章节/剧集） | `app/project_workflow.go` | ✅ CRUD/导入/重排（workspace 读视图待接） |
| 6.3 | 角色与配音绑定 | `app/project_character.go` | ☐ |
| 6.4 | 分镜与镜头版本 | `app/project_shot.go` | ☐ |
| 6.5 | 项目素材关联 | `app/project_asset.go` | 🟡 文件夹树完成；素材关联/版本/角色待做 |
| 6.6 | 工作台读取视图 | `app/project_workbench_read.go` | ☐ |
| 6.7 | 工作流模板与实例 | `app/project_workflow_v2*` | ☐ |
| 6.8 | 风格档案 | `handler/style_profile.go` | ✅ CRUD/收藏/最近使用/归一化校验（voice-profiles 列表已通） |
| 6.9 | 功能开关门禁 | `handler/feature_availability.go` | ✅ |

### 阶段 7 · 画布与分享

| # | 模块 | 对应 Go | 状态 |
| --- | --- | --- | --- |
| 7.1 | 画布工程与单元 | `internal/canvas` | ✅ CRUD/同步校验/媒体守卫/配额（快照 replace 待接） |
| 7.2 | 画布能力校验 | `canvas/capability` | ✅ 媒体资产守卫（assetId+resourceID 配对校验） |
| 7.3 | 画布分享（公开 token） | `handler/canvas_share.go` + 日志脱敏 | ✅ CRUD/公开投影脱敏/资源代理（Range 待接，#26） |
| 7.4 | 画布状态与外观 | `app/appearance*.go` | ✅ 品牌/登录页素材/皮肤主题（4 套内置+校验）/SEO 全量；公开读取 + 管理端读写 + 资源上传下发 |

### 阶段 8 · 财务与支付（29 + 21 条路由）

| # | 模块 | 对应 Go | 状态 |
| --- | --- | --- | --- |
| 8.1 | 积分账户与账本 | `app/finance.go` | ☐ |
| 8.2 | 充值商品 | `app/payment.go` | ☐ |
| 8.3 | 支付订单与通知 | `internal/payment` + `cmd/payment-*` | ☐ |
| 8.4 | 对账 | `app/payment_reconciliation.go` | ☐ |
| 8.5 | 兑换码批次 | `app/redeem*.go` | ☐ |
| 8.6 | 支付宝 / 微信支付适配 | `payment-sdk/` `payment-plugins/` | ☐ |

### 阶段 9 · 管理后台

| # | 模块 | 对应 Go | 状态 |
| --- | --- | --- | --- |
| 9.1 | 管理员审计事件 | `model.AdminAuditEvent` | ✅ 写入/分页/按目标查询 |
| 9.2 | 分析统计 | `app/analytics.go` `handler/admin_analytics.go` | 🟡 API 日志/导出/存储统计完成；overview 待活跃表写入（#20） |
| 9.3 | 存储管理 | `handler/admin_storage.go` | ✅ 存储统计/资源分页（筛选校验）/批量删除（引用阻塞+Outbox，**含外观引用检查**）/管理员直连下发（Range/download）完成 |
| 9.4 | 系统更新（host-updater） | `internal/hostupdate` `updaterclient` | ☐ |
| 9.5 | 系统性能 | `handler/admin_system_performance.go` | ☐ |
| 9.6 | 系统设置 | `app/settings.go` | 🟡 注册/邮件/LinuxDO/积分/公告完成；OSS/外观/响应拦截/ARK/LibTV 待做 |
| 9.7 | 公告与已读 | `app/announcement.go` | 🟡 feed/已读/CRUD/关闭/配图草稿消费与丢弃完成；配图上传待资源链路（#28） |

### 阶段 10 · 插件、技能与提示词

| # | 模块 | 对应 Go | 状态 |
| --- | --- | --- | --- |
| 10.1 | 插件运行时与状态 | `app/plugin_runtime*` `app/plugin_management.go` | ☐ |
| 10.2 | 声明式协议插件 | `app/protocol_plugins.go` `protocol_registry.go` | 🟡 元数据注册表已接（内置 13 协议+插件包）；执行引擎待做 |
| 10.3 | 技能库 | `internal/skills` + `app/skills.go` | ☐ |
| 10.4 | 技能包管理 | `repository/skill_packages.go` | ☐ |
| 10.5 | 提示词模板与用户定制 | `internal/prompts` | 🟡 结构化画风校验/用户风格归一化完成；模板渲染与偏好待做 |
| 10.6 | LibTV / TapNow 集成 | `handler/libtv.go` `handler/tapnow.go` | ☐ |
| 10.7 | 自定义渠道中转 | `handler/custom_proxy.go` | ☐ |
| 10.8 | 系统代理与路径转发 | `handler/system_proxy_stream.go` | ☐ |

### 阶段 11 · 云 Agent（20 个 app 文件）

| # | 模块 | 对应 Go | 状态 |
| --- | --- | --- | --- |
| 11.1 | Agent 运行时 | `app/cloud_agent_runtime.go` (1263 行) | ☐ |
| 11.2 | Agent 会话与调度 | `app/cloud_agent.go` | ☐ |
| 11.3 | 工具调用 | `app/cloud_agent_tools.go` | ☐ |
| 11.4 | 媒体处理 | `app/cloud_agent_media.go` | ☐ |
| 11.5 | 画布状态同步 | `app/cloud_agent_canvas_state.go` | ☐ |
| 11.6 | 分镜生成 | `app/cloud_agent_storyboard.go` | ☐ |
| 11.7 | 批量表格 | `app/cloud_agent_batch_table.go` | ☐ |
| 11.8 | 审批与预览 | `app/cloud_agent_approval_preview.go` | ☐ |
| 11.9 | Agent 档案 | `repository/agent_profile.go` | ☐ |

### 阶段 12 · 迁移工具、部署与验收

| # | 模块 | 对应 Go | 状态 |
| --- | --- | --- | --- |
| 12.1 | `migrate-schema` | `cmd/migrate-schema` | ☐ |
| 12.2 | `migrate-sqlite-postgres` | `cmd/migrate-sqlite-postgres` | ☐ |
| 12.3 | `migrate-logical-model-families` | 同名 cmd | ☐ |
| 12.4 | `migrate-channel-model-price-tiers` | 同名 cmd | ☐ |
| 12.5 | `reseed-logical-model-sources` | 同名 cmd | ☐ |
| 12.6 | `host-updater` | `cmd/host-updater` | ☐ |
| 12.7 | Dockerfile / Compose 对齐 | `backend/Dockerfile` | ☐ |
| 12.8 | 契约回归测试 | `*_test.go` | 🟡 266 项端到端契约测试通过（随节点持续补充） |
| 12.9 | 双跑对比验收 | — | ☐ |

---

## 四、逐模块翻译的标准作业流程（SOP）

每个模块严格按这 6 步走，避免"看起来翻译完了但契约不对"：

1. **读源**：完整读 Go 源文件（含 `_test.go`，测试就是契约说明）。
2. **抄契约**：先写 DTO（请求/响应），逐字段核对 `json` tag 与 `omitempty`，确认指针/可空。
3. **写路由**：`[HttpPost("/api/xxx")]`，路径、方法、参数绑定位置逐字对齐。
4. **写逻辑**：业务逻辑 1:1 平移，保持分支与错误抛出点一致。
5. **对齐错误**：每个错误分支映射到相同 `code` / `reason` / `status` / 文案。
6. **验契约**：用同一条请求打 Go 与 .NET 两个实例，**响应体逐字节 diff**，含 HTTP 状态、响应头、Set-Cookie。

---

## 五、工作量与交付顺序建议

按"依赖少、可独立验证、被依赖多"排序，推荐落地顺序：

```
阶段 0（基座）
  → 阶段 1（模型+持久化）
    → 阶段 2（认证：所有业务路由的前置）
      → 阶段 5（资源/存储：任务产物的依赖）
        → 阶段 3（渠道/模型：任务执行的前置）
          → 阶段 4（任务与生成：系统核心）
            → 阶段 6（项目/短剧）
              → 阶段 7（画布）
                → 阶段 8（财务支付）
                  → 阶段 9（后台）
                    → 阶段 10（插件技能提示词）
                      → 阶段 11（云 Agent）
                        → 阶段 12（工具/部署/验收）
```

规模提示：阶段 0 + 1 约占总量 12%，阶段 4 与阶段 6 各约占 20%，阶段 11 约占 15%。

---

## 六、验收清单

- [ ] 329 条路由全部注册，路径/方法逐条比对一致
- [ ] 全部成功响应体与 Go 版逐字节一致（含字段缺失行为）
- [ ] 全部失败响应 `code` / `reason` / `msg` / HTTP status 一致
- [ ] Cookie 名称、属性、`MaxAge` 一致
- [ ] CORS 头与非法 Origin 403 行为一致
- [ ] SSE `text-events` 事件名（`progress` / `delta` / `terminal` / `error`）、`id` 游标、心跳格式一致
- [ ] 同一 SQLite 文件与 PostgreSQL 库，两种实现读写互认
- [ ] 环境变量名全部一致，无新增必填项
- [ ] 全量 `openapi.yaml` 输出一致
