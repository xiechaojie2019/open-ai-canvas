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
| 3.1 | 渠道管理 | `app/channel*.go` | 🟡 列表/创建/复制/更新/删除/排序已通；models 子资源路由已通（见 3.2） |
| 3.2 | 渠道模型 + 价格档 | `app/channel_models.go` | 🟡 列表/排序/价格档附着已通；保存/删除/fetch/import/test 待做 |
| 3.3 | 逻辑模型与版本 | `app/logical_models.go` | 🟡 公开目录/管理端 CRUD/模拟/报价已通；工作流选路待做 |
| 3.4 | 模型目录发现 | `provider/registry.go` | 🟡 元数据注册表已接（内置 13 协议+插件包）；声明式 Protocol 层完成（表达式引擎/manifest wire 类型/校验归一化/ManifestAdapter/适配器注册表）；声明式 Providers 执行接入完成（`ProviderProtocolTask` create→poll→download 编排，图片/视频入口按 ctx 注册表路由）；剩余：运行时注册表动态注入（随 10.1/10.2） |
| 3.5 | 模型能力矩阵 | `app/model_capability.go` | ✅ 读路径完成（解码/归一化/投影/校验） |
| 3.6 | 路由目录快照与健康度 | `app/model_router.go` | ✅ 快照/匹配/选路/模拟完成（Redis 协调待接） |
| 3.7 | 模型 SKU 选择器 | `model/model_sku.go` | ✅ |
| 3.8 | 系统模型种子 | `EnsureSystemChannelModels` | ✅ |

### 阶段 4 · 任务与生成（14 + 12 条路由）

| # | 模块 | 对应 Go | 状态 |
| --- | --- | --- | --- |
| 4.1 | 任务 CRUD | `handler/routes.go` | ✅ 列表/详情/日志/文本增量/回放收尾/POST /tasks 全量（批 1+2） |
| 4.2 | 任务文本增量与 SSE 流 | `app/text_replay.go` | ✅ text-events SSE（游标/心跳/轮询）+ AdminTextReplayStats |
| 4.3 | 任务重试 / 取消 / 上游查询 | `app/task_*.go` (12 文件) | ✅ retry/cancel/query-provider 三路由全通（#61/#62 上游部分待 provider 引擎） |
| 4.4 | Worker 调度与租约 | `app/task_worker.go` | 🟡 已通：`Repository.TaskLease`（领取/续期/释放/让渡/持租约进度/终态/完成落库，PG SKIP LOCKED + SQLite 条件更新）、`TaskTerminalService`（失败/取消/成功收尾与计费协调）、`TaskWorkerService`（2s 调度循环 + 全局并发槽 + 45s 任务租约 + 15s 续租循环 + 超时策略 + 输入解密 + 系统渠道 channel_models 授权解析 + 存储配额核算落库）、`TaskDispatchWorker` 宿主（`CANVAS_DISABLE_BACKGROUND_WORKERS` 可关）；文本/图片/视频/音频执行分支已按任务类型接入（4.6–4.9 的"挂到 Worker"同步完成）；简化项：无 RouteAttempt 路由状态机、无 persistGeneratedMediaResult 媒体落盘、无 defer/newapi-channel-2 回查、无 canvas_ops 结果行与结构化配额、无 registerActiveTask 主动取消挂点、timeline 两类型直接报未实现（随 4.14） |
| 4.5 | 计费协调（预扣/结算/退款） | `app/billing.go` `billing_review.go` | ✅ 仓储层 MarkRunning/Settle/Restore/Refund/Uncertain 全通；TaskTerminalService 收尾协调（失败退款/取消/成功结算/不确定待核对/结果落库失败补偿）与 TaskWorkerService 接入（领取即 MarkRunning、成功 Settle、失败按复核判定走 Uncertain 或退款）随 4.4 完成；CheckRetryEligibility 与 BillingFailureRequiresReview 对齐 Go；账单巡检 StaleBillingReviewStats + TaskBillingReviewService.AuditAsync + BillingReviewWorker 宿主（每小时一次，只读提醒人工核对，不代资金动作）；SQLite 巡检测试 2 条 |
| 4.6 | 文本协议 | `app/provider_text.go` | ✅ 具备端到端执行能力：错误体系（失败识别/错误码归一化/HTTP 与正文归类/四类异常）、`ProviderHelpers`、`ProviderRequestTypes`、`ProviderMedia`、`ContentTypeSniffer`、`ParseRetryAfter`、`ProtocolRequestBuilder`（四类请求体 + URL/OriginPath + AWS SigV4/腾讯 TC3 签名）、`StreamingAgentParser`（chat/responses/claude 三协议 SSE 流式解析，含工具调用累积与 `[DONE]`）、`AgentToolPayload`（非流式统一解析）、`ProviderTextOrchestration`（协议归一/思考模式/工具选择归一/输出上限/`stream_options` 用量/结果整形/空正文校验/Responses 回落判定/历史过滤）、`ProviderTextRequestBuilder`（三协议请求体与多模态内容块）、**`ProviderTransport`（出站安全边界：大小上限/非 2xx + Retry-After/分片观测/网络错误映射/鉴权装配，共 42 条线级测试）+ `ProviderTextTask`（`requestTextProvider` 端到端：非流式 postJSON、流式 SSE、非 event-stream 退化、legacy 回落；**`RunTextTaskAsync` = `runTextTask` 按 interfaceType 分发，未识别类型走 legacy**）**；剩余：挂到任务 Worker（依赖 4.4/4.5） |
| 4.7 | 图片协议 | `app/provider_image.go` | ✅ `ProviderImageTask` 已通：OpenAI Images（生成/蒙版编辑 + multipart 手写构造 + 按能力裁剪参数）、Gemini Images（`/v1beta` + inlineData + 双向 MIME 签名校验）、Grok Images（`aspect_ratio`/`resolution` 归一化）、火山方舟（尺寸像素区间夹取 + 外链下载内联，跨源不带鉴权）；即梦手写协议已通（`RunJiMengAsync`：火山 V4 签名（Service=cv）CVSync2AsyncSubmitTask 提交 + CVSync2AsyncGetResult 轮询，`image_urls` 外链下载内联与 `binary_data_base64` 解码，蒙版/参考图 14 张上限/像素面积 [1MP,16MP] 门禁，code≠10000 报错带 request_id，not_found/expired 报失效，超时报错）；`ProviderImageOptions` 提供尺寸/质量归一化与能力裁剪；声明式接入完成：入口只查 ctx 注入注册表（裸 ctx 走手写协议，与 Go 一致）；剩余：挂到任务 Worker（依赖 4.4/4.5，与 4.6 相同） |
| 4.8 | 视频协议（含遗留） | `app/provider_video.go` | ✅ 全通：`ProviderVideoPolling`（`runVideoPollLoop`：初始延迟/间隔、**两个独立计数器**（未找到/畸形响应）、Retry-After 拉长等待、重试态与恢复播报、可取消等待；`runVideoDownload`：有限次重试 + `VideoDownloadException`；`retryableVideoPollError` 全分类；`isProviderTaskNotReadyError` 固定短语判定）；`ProviderVideoTask`（OpenAI 风格 multipart+轮询+content 回落、Seedance `/videos`、Agent Plan `/contents/generations/tasks`、xAI `/videos` JSON；恢复任务只查询不重建；下载跨源不带鉴权）；`ProviderVideoOptions`（分辨率名匹配与固定分辨率、Seedance 时长/比例/分辨率归一化、首尾帧排序、素材 URL 策略）；声明式接入完成：未注入注册表补官方包（ensureOfficialProtocolAdapter）、显式空表报"插件未安装"、官方映射未安装时报错，均与 Go 路由边界一致 |
| 4.9 | 音频协议 | `app/provider_audio.go` | ✅ `ProviderAudioTask` 已通：同步 `/audio/speech` 二进制（body 含 model/input/voice/response_format/speed，AudioSpeed 覆盖默认 1、AudioInstructions 映射 instructions）+ 异步 `/audio/tasks`（`data`/`result`/`output` 包装展开、id/task_id/request_id 任务 ID 严格取字符串、成功态 done/completed/succeeded/success/done、失败态 failed/cancelled/canceled/expired/error 带上游错误文案、2.5s 轮询间隔 1h 超时）；结果下载三分支（data URL 解码 + 尺寸上限校验、公网 URL 外链下载、渠道 `/content` 回落）与 `validateGeneratedAudio` 魔数校验（pcm/mpeg/wav/ogg/flac/aac 签名、非音频内容与声明不符均报错、空 octet-stream 按格式回退）；中文错误文案逐字对齐 Go；声明式接入与图片一致（只查 ctx 注入注册表，无 official-fallback 报错路径）；剩余：挂到任务 Worker（依赖 4.4/4.5，与 4.6 相同） |
| 4.10 | HTTP 客户端与声明式协议 | `app/provider_http_client.go` `provider_protocol.go` | 🟡 出站安全边界已通（`ProviderTransport`：响应大小上限两道检查/非 2xx + Retry-After/分片观测/网络错误映射/鉴权装配/渠道 URL 版本前缀归一）；**协调层已通（`Platform/Coordinator`：固定窗口限流、并发租约与退避等待、渠道熔断、路由目录版本与路由屏蔽、`Application/ProviderRequestContext` 适配器）**，并已接入 `ProviderTextTask`（熔断前置短路 → 占槽 → 请求 → 记结果 → 释放）；**声明式协议执行已通（`ProviderProtocolExecutor`：白名单 method 校验、body/URL/头装配、11 类鉴权驱动含 AWS SigV4/TC3/火山 V4、multipart 媒体加载；`ProviderProtocolPayload`：`protocolRequestFromInput` 投影、素材角色判定、`finishProtocolResult` 结果整形；`ProviderProtocolTask`：create→poll→download 三阶段编排、幂等键、ExtractProviderTaskID 回落、结果下载与 media 归一）**；剩余：Redis Lua 脚本的集成测试 |
| 4.11 | 工作流 Provider | `app/workflow_provider.go` (2155 行) | ✅ `ProviderWorkflowTask` 全链路已通：JSON→节点表解析（含槽位计数与列表展开）、字段角色推断/覆盖安全性、分辨率默认值与槽位文案归一（与 Go 完全一致，无默认值返回原值）、`runninghub-workflow-{image,video,audio}` 三类 interfaceType 提交、轮询统一走 `ProviderVideoPolling`（声明式策略可注入，image 遗留分支固定 2.5s 间隔 1h 预算）、结果下载与 media 归一、协议信封解析；已挂接 Worker 执行分支与创建准入（`workflowPluginIDForInterface` 对齐 Go，仅认三类后缀）；28 条契约测试覆盖解析/归一/提交/轮询/下载/信封/SSRF 前置；剩余：插件启用校验留在 admission 层（与 Go 相同），插件注册表仍视为未启用（4.12 的 plugin runtime） |
| 4.12 | RunningHub 集成 | `app/runninghub_management.go` | ✅ `WorkflowPluginGate` 插件门控（平台/用户两级状态，创建准入与 Worker 执行双闸，默认禁用）、RunningHub 管理代理（workflow-info / app-info 拉取上游参数模板，SSRF 前置 + 128KB 上限）、`GET /plugins/status` 状态聚合；平台开关由 `plugin_platform_states` 数据行控制，插件中心安装/启停 UI 留 10.1 |
| 4.13 | 视频转码与播放副本 | `app/video_transcode.go` | ☐ |
| 4.14 | 时间轴转录 / 渲染 | `app/transcription*.go` `timeline*.go` | 🟡 transcription 创建已通（whisper 执行待）；render 创建待做 |
| 4.15 | 创作运行与提交 | `app/creation*.go` | ✅ 运行生命周期/报价/批准/执行 13 条（批 1）+ 画布提交 3 条（批 2，#63 关闭）；agentRequests 占位符水合待（#64） |

### 阶段 5 · 资源、素材与存储

| # | 模块 | 对应 Go | 状态 |
| --- | --- | --- | --- |
| 5.1 | 资源上传（含分片） | `handler/resource_upload_session.go` | ✅ 三件套路由 + 幂等/配额/本地落盘完成（对象存储通道待 #25） |
| 5.2 | 资源引用解析 | `internal/assets` | ✅ 引用收集/校验/ID 解析完成（UserDataService 内） |
| 5.3 | 资源删除与引用检查 | `app/resource_delete.go` | ✅ 判定链/Outbox/本地物理删除完成（云删除 worker 待接，#25） |
| 5.4 | 资源清理作业 | `repository/resource_cleanup.go` | ✅ 候选筛选（未完成 1h / 就绪 24h）+ 引用快照与外观检查 + 事务删除 + Outbox 物理清理；后台作业含公告草稿超期清理与回收站过期素材清理（保留天数取运行时策略），顺序与 Go 一致 |
| 5.5 | 素材库 | `app/asset*.go` `repository/asset_library.go` | ✅ CRUD/分页/facets/分类/移动 + 摘要内嵌角色卡（阶段 6.5） |
| 5.6 | 存储位置与 OSS 设置 | `app/storage*.go` | ☐ |
| 5.7 | Eagle 集成 | `app/eagle.go` | ☐ |
| 5.8 | 用户数据导出/分页 | `handler/user_data.go` (31 条) | 🟡 画布/素材/分享/资源 CRUD/导入/用量/OSS 直链/提示词偏好已通（约 27 条）；OSS 用户设置 3 条与导出待做 |

### 阶段 6 · 项目与短剧工作流（47 条路由）

| # | 模块 | 对应 Go | 状态 |
| --- | --- | --- | --- |
| 6.1 | 项目 CRUD | `app/project.go` | ✅ 列表/分页/创建/更新/删除级联（默认工作流待接，#27） |
| 6.2 | 项目单元（章节/剧集） | `app/project_workflow.go` | ✅ CRUD/导入/重排（workspace 读视图待接） |
| 6.3 | 角色与配音绑定 | `app/project_character.go` | ✅ 角色 CRUD/形象/声音 + voice-profiles 播种（三视图任务收尾待任务域，#47） |
| 6.4 | 分镜与镜头版本 | `app/project_shot.go` | ✅ 创建/版本/删除/章节替换/资产引用/候选与确认 |
| 6.5 | 项目素材关联 | `app/project_asset.go` | ✅ 链接/解绑/更新/版本/分页过滤 + 摘要内嵌角色卡 |
| 6.6 | 工作台读取视图 | `app/project_workbench_read.go` | 🟡 core/overview 已通（ProjectDetail 聚合待任务域+工作流 v2，#51；候选/素材/画布分页已随 6.4/6.5 落地） |
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
| 8.1 | 积分账户与账本 | `app/finance.go` | ✅ wallet/redeem/checkin/adjust + 管理端批次/账单/人工核对 |
| 8.2 | 充值商品 | `app/payment.go` | ✅ products/providers/checkout/refresh |
| 8.3 | 支付订单与通知 | `internal/payment` + `cmd/payment-*` | ✅ orders/notify/return/query/close |
| 8.4 | 对账 | `app/payment_reconciliation.go` | ✅ reconciliations + 手动核对/批量解决 |
| 8.5 | 兑换码批次 | `app/redeem*.go` | ✅ 批次/码/禁用/兑换 |
| 8.6 | 支付宝 / 微信支付适配 | `payment-sdk/` `payment-plugins/` | 🟡 注册表/清单/插件宿主已通；内置 SDK 适配器未移植（#33） |

### 阶段 9 · 管理后台

| # | 模块 | 对应 Go | 状态 |
| --- | --- | --- | --- |
| 9.1 | 管理员审计事件 | `model.AdminAuditEvent` | ✅ 写入/分页/按目标查询 |
| 9.2 | 分析统计 | `app/analytics.go` `handler/admin_analytics.go` | ✅ overview/models/users/export.csv + API 日志/导出/存储统计 + 模型价格 CRUD |
| 9.3 | 存储管理 | `handler/admin_storage.go` | ✅ 存储统计/资源分页（筛选校验）/批量删除（引用阻塞+Outbox，**含外观引用检查**）/管理员直连下发（Range/download）完成 |
| 9.4 | 系统更新（host-updater） | `internal/hostupdate` `updaterclient` | ☐ |
| 9.5 | 系统性能 | `handler/admin_system_performance.go` | 🟡 总览与缓存清理已通；`Platform/Coordinator` 已提供多实例协调能力（限流/并发/熔断/路由版本），Redis 维度可据此接入 |
| 9.6 | 系统设置 | `app/settings.go` | 🟡 注册/邮件/LinuxDO/积分/运行时策略/绘图工具/响应拦截/方舟素材库完成；OSS 6 条/LibTV 3 条待做（test 依赖云 SDK/外部服务） |
| 9.7 | 公告与已读 | `app/announcement.go` | ✅ feed/已读/CRUD/关闭/配图草稿消费与丢弃 + 配图上传（阶段 9.5） |

### 阶段 10 · 插件、技能与提示词

| # | 模块 | 对应 Go | 状态 |
| --- | --- | --- | --- |
| 10.1 | 插件运行时与状态 | `app/plugin_runtime*` `app/plugin_management.go` | ☐ |
| 10.2 | 声明式协议插件 | `app/protocol_plugins.go` `protocol_registry.go` | 🟡 元数据注册表已接（内置 13 协议+插件包）；声明式 Protocol 层完成（表达式引擎/manifest 线格式/校验归一化/适配器注册表 + 官方 fallback 加载）；Providers 执行层完成（`ProviderProtocolTask` 三阶段编排 + 图片/视频入口注册表路由，ctx 注入语义与 Go 一致）；运行时管理（安装/启停/删除）待做 |
| 10.3 | 技能库 | `internal/skills` + `app/skills.go` | 🟡 列表/详情/删除/加入/点赞 + 包文件读取 5 条 + 创建/更新完成（15 条路由）；install/sync 待做 |
| 10.4 | 技能包管理 | `repository/skill_packages.go` | ☐ 包目录布局与 manifest 解析待移植（install/sync/file 读的前置） |
| 10.5 | 提示词模板与用户定制 | `internal/prompts` | ✅ 管理端模板 CRUD/启停 + 用户偏好列表/定制三模式/重置（模板渲染 CompilePrompt 待做） |
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
| 12.8 | 契约回归测试 | `*_test.go` | 🟡 483 项端到端契约测试通过（随节点持续补充） |
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


---

## 九、部署与生产修复日志

### 2026-09-21 · 4.12 RunningHub 集成完成

插件门控与 RunningHub 管理代理已通（Go `workflow_plugins.go` + `runninghub_management.go` 主路径）：

- `Repository.PluginStates`：`plugin_platform_states` / `user_plugin_states` 读写与 upsert（UPDATE 0 行转 INSERT，插入时补主键），SQLite/PG 同源 SQL。
- `WorkflowPluginGate`：白名单 `runninghub-workflow-{image,video,audio}` 归一到插件 ID `runninghub-workflow-provider`；平台状态行是唯一真源，无行 = 平台禁用（对齐 Go 内置清单 Enabled:false 的默认部署行为）；用户无记录时跟随平台状态，显式停用优先。创建准入 `RequireForUser`（EffectiveEnabled）与 Worker 执行 `RequireForInterface`（仅平台维度）双闸，未知 interfaceType 统一 403 文案。
- `RunningHubManagementService` + `POST /runninghub/workflow-info`、`POST /runninghub/app-info`：服务端代理拉取上游参数模板（getJsonApiFormat / apiCallDemo），出站 URL 先过 SSRF 校验，API Key 缺失 400，上游异常统一 502；端点固定按 `runninghub-workflow-image` 门控（对齐 Go），请求体 128KB 上限。
- `GET /plugins/status`：返回 statuses + states 聚合（平台可用、用户开关、生效状态、禁用原因），画布与插件中心的禁用提示直接消费。

测试：新增 9 条（仓储 upsert 2、门控 4、端点 3），全量 1456/1456 通过（`ChannelOrderTests` 一轮并行噪音失败，单独复跑即过，与本次改动无关）。

已知限制：插件中心安装/卸载/启停 UI 与管理端点属 10.1；平台开关当前仅能通过 `plugin_platform_states` 数据行开启。

### 2026-09-21 · 4.11 部署 211

发布物由框架依赖改为 self-contained linux-x64：服务器 Dockerfile 入口是原生 apphost（`./OpenAICanvas.Web`），不带运行时探测路径，框架依赖发布会导致容器缺框架重启循环（exit 150）。重新 publish 后 docker compose build 恢复 healthy。

拓扑确认：backend 8081:8080、web 5173:3000（compose 既有配置）；宿主机 8080 属于服务器上其他服务，与本栈无关。冒烟：health/live 与 health/ready 200（schema 15/15）；未登录 401；deploycheck 管理账号经 5173 代理登录 code:0；plugins/catalog（admin scope）200 正常返回 providers。
### 2026-09-21 · 4.11 工作流 Provider 迁移完成

`ProviderWorkflowTask` 全链路已通（Go `workflow_provider.go` 主路径）：JSON→节点表解析（槽位计数/列表展开）、字段角色推断与覆盖安全、分辨率默认值与槽位文案归一（无默认值返回原值，与 Go 一致）、`runninghub-workflow-{image,video,audio}` 三类 interfaceType 提交、轮询复用 `ProviderVideoPolling`（声明式策略可注入；image 遗留分支固定 2.5s 间隔 1h 预算）、结果下载与 media 归一、协议信封解析。Worker 执行分支与创建准入同步挂接：`workflowPluginIDForInterface` 仅认三类后缀（对齐 Go，裸 `runninghub` 不走工作流插件）。

顺带修复两个迁移 bug：`ProviderHelpers.AtoiOrInvalid`（非数字状态串如 `QUEUED` 不再被误归一为 0=成功）与 `ProviderVideoPolling` 预算在休眠/查询中途耗尽时 OperationCanceledException 泄漏（统一归为轮询超时，与 Go 子上下文超时语义一致）。

测试侧新增 `InternalsVisibleTo`（`AssemblyInfo.cs`），28 条工作流契约测试覆盖解析/归一/提交/轮询/下载/信封/SSRF 前置；全量 1447/1447 通过（一轮 `ChannelModelCatalogTests` 并行噪音失败，单独复跑即过）。

已知限制：插件启用校验留在 admission 层（与 Go 相同）；插件注册表仍视为未启用，留待 4.12 的 plugin runtime。

### 2026-09-21 · 4.5 计费协调与账单巡检收尾完成

补齐 Go billing_review.go 缺口：Repository.StaleBillingReviewStatsAsync（只读统计 15 分钟未更新且仍处 reserved/running/uncertain 的订单）、TaskBillingReviewService.AuditAsync（总数为零时静默，异常时打警告日志）与 BillingReviewWorker 宿主（立即巡检一次后每小时一次，只提醒人工核对，不代任何资金动作）。
RestoreRefundedBillingOrderAsync 仓储路径此前已通，协调层调用点随 provider_task_recovery 迁移时接入。全量 1417/1417 通过（含新增巡检 SQLite 路径测试 2 条）。

### 2026-09-21 · 4.4 Worker 调度与租约迁移完成

`Repository.TaskLease`（12 条 SQLite 路径测试）+ `TaskTerminalService`（对应
Go `task_terminal.go` 全部收尾分支）+ `TaskWorkerService`（调度循环/租约
维护/执行编排/超时策略/存储配额落库）+ `TaskDispatchWorker` 宿主接线
全通，全量 1415/1415。文本/图片/视频/音频协议自此接入任务全链路
（4.6–4.9 的"挂到 Worker"同步关闭）。第一批简化项已如实记录在 4.4 清单：
RouteAttempt 状态机、媒体落盘、newapi-channel-2 回查、canvas_ops 结果行、
registerActiveTask 主动取消挂点、timeline 执行器均待后续阶段。

### 2026-09-21 · 4.9 音频协议迁移完成

`ProviderAudioTask`（OpenAI 风格同步语音 `/audio/speech` + 异步
`/audio/tasks` create→poll→download 全链路）连同 25 条契约测试全通，
全量 1403/1403。声明式分支（只查 ctx 注入注册表）、结果下载三分支
（data URL / 外链 / content 回落）、`validateGeneratedAudio` 魔数校验
（pcm/mpeg/wav/ogg/flac/aac）与中文错误文案均与 Go `provider_audio.go` 对齐。

### 2026-09-21 · 协议目录 404 修复（bf43d74e）

**现象**：管理后台「渠道模型」编辑弹窗报"协议目录读取失败 / Not Found"。
前端 `fetchPluginProviderCatalog("admin.system-channel")` 调用
`GET /api/plugins/catalog`，.NET 侧没有 /plugins 路由，MapFallback 直接回 404。

**修复**（插件中心 10.1 仍保持未做，只补目录这一条路由）：

1. `OpenAICanvas.Application/ProtocolCatalogService.cs`：
   `ProtocolAdapterLookup.OfficialFallback.List(scope, capability)` 投影为
   Go `PluginProviderCatalogItem` 同形 JSON（create/poll/contentType 摘要取自
   清单归一化后的 Metadata；workflows 恒为空数组，插件运行时未移植）。
2. `OpenAICanvas.Web/Endpoints/PluginEndpoints.cs`：`GET /plugins/catalog`
   登录即放行（对应 Go `requirePluginCenterAccess`：admin 直接放行、普通用户
   只查登录不查插件中心开关）；scope 默认 user.custom-channel，Trim 后过滤。
3. Program.cs 在 MapFallback 之前挂载 MapPluginRoutes。

**验证**：新增 3 条端点契约测试（未登录 401 / 官方插件包目录字段与
scope+capability 过滤 / 空结果边界），全量 1378/1378 通过。
**部署 211（2026-09-21 完成）**：publish linux-x64 → tar SCP 上传
/opt/open-ai-canvas-dotnet/ → 替换 linux/（旧目录保留为 linux.bak）→
docker compose 重建 backend，容器 healthy。

关键补充：生产容器原本没有 plugin-packages，OfficialFallback 注册表为空，
接口虽不报 404 但 providers 会是空数组。本次将仓库 plugin-packages/（169 项）
上传到服务器并在 docker-compose.yml 给 backend 增加挂载
`./plugin-packages:/app/plugin-packages`（代码默认候选目录即 /app/plugin-packages）。

冒烟结果：未登录 8081 直连与 5173 前端代理均 401（`请先登录`）；
deploycheck 登录后 `GET /api/plugins/catalog?scope=admin.system-channel`
返回 code:0 且 providers 共 82 个（Adobe Firefly、Agnes Image 等官方声明式接口），
渠道模型弹窗协议下拉恢复正常。

### 2026-09-20 · 首次部署 192.168.0.211 与 Dapper 集合参数修复（e94b54cb）

**部署链路**：本地 publish linux-x64，tar 打包后 SCP 上传 /opt/open-ai-canvas-dotnet/，
docker compose 重建 backend。容器 healthy，/api/health/live 与 /api/health/ready 均 200。

**生产暴露并修复的问题**（PostgreSQL 日志取证 + 端点测试复现）：

1. Dapper 不展开匿名对象里的集合参数：生产 PG 实测产生 IN $1 语法错误，
   SQLite 下落入 IN ((?,?)) 行值误用。修复：RepositoryBase 五个查询入口统一过
   ExpandCollectionParameters——IN (@name) 与 gorm 风格 IN @name（无括号，必须补上）
   统一展开为逐元素占位符；空集合展开为恒假空子查询（IN (NULL) / NOT IN (NULL)
   会因 UNKNOWN 过滤掉所有行）；未被 SQL 以 IN 引用的集合参数直接丢弃；
   DynamicParameters 通道维持只传标量约定（配合 Placeholders 手写展开）。
2. 参数对象本身是实体集合（分批多行 INSERT）被误反射：Dapper 原生列表展开通道
   （每个元素才是参数模板）被顶层属性扫描整体替换为垃圾参数。修复：参数本身是
   IEnumerable 且非 Dictionary 时直接放行。
3. user_daily_activities 的 login_count 列歧义（PG 要求 DO UPDATE 右侧限定表名），
   且 VALUES 首插写 0 吞掉首次登录计数。修复：首插 @initialLogin + 限定表名。
4. admin_analytics 过滤列 record_type 不存在。修复：改用 request_kind 语义
   （download 仅 download；all 不过滤；默认排除 poll/download）。

**验证**：持久层 38/38，全量 1375/1375 通过；生产冒烟登录（code:0，login upsert 生效）
与管理端分析总览（KPI/趋势正常返回）通过。Endpoint 组失败定位的关键经验：
ApiResults.FailService 对未分类异常只回固定文案，error_type 进 CanvasLog，
测试侧直接调 service（DI 解析 CanvasService）可拿到原始异常栈。
