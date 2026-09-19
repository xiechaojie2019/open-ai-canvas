# 迁移进度清单

图例：✅ 完成并验证 ｜ 🟡 部分完成 ｜ ☐ 未开始

---

## 环境修复（阻塞项，已解决）

本机 WorkBuddy 的 bash / PowerShell 会话会剥掉 `PROGRAMFILES(X86)`、`PROGRAMFILES`、`PROGRAMDATA`、
`APPDATA` 等 Windows 环境变量。NuGet 的
`NuGet.Common/PathUtil/NuGetEnvironment.cs` 在计算 `MachineWideSettingsBaseDirectory` 时读取
`PROGRAMFILES(X86)`，为空时执行 `Path.Combine(null, "NuGet")`，导致**任何** `dotnet restore`
都以 `Value cannot be null. (Parameter 'path1')` 失败（与本仓库代码无关，新建空白项目同样复现）。

**所有 dotnet 命令必须经由 `scripts/dotnet.sh` 转发**：

```bash
./scripts/dotnet.sh build OpenAICanvas.sln
./scripts/dotnet.sh test tests/OpenAICanvas.Tests/OpenAICanvas.Tests.csproj
./scripts/dotnet.sh run --project src/OpenAICanvas.Tools/OpenAICanvas.Tools.csproj -- schema-ddl sqlite out.sql
```

---

## 契约基线工件

| 文件 | 来源 | 用途 |
| --- | --- | --- |
| `schema-dump.json` | `backend/cmd/schema-dump`（GORM 自身解析器） | 80 表 / 958 列 / 421 索引的权威基线 |
| `scripts/generate-entities.py` | — | 从基线生成实体层、枚举常量、SQLite/PostgreSQL 建表脚本 |

重新生成：

```bash
cd backend && go run ./cmd/schema-dump > ../backend-dotnet/schema-dump.json
cd ../backend-dotnet && python scripts/generate-entities.py schema-dump.json
```

---

## 阶段 0 · 工程骨架与契约基座 — 🟡 基本完成

| # | 模块 | 对应 Go | 状态 |
| --- | --- | --- | --- |
| 0.1 | 解决方案与项目分层（15 + 1 测试） | — | ✅ |
| 0.2 | 错误码常量 | `kernel/error_codes.go` | ✅ `Domain/Kernel/ErrorCodes.cs` |
| 0.3 | 错误原因常量与映射 | `kernel/error_codes.go` | ✅ `Domain/Kernel/ErrorReasons.cs` |
| 0.4 | `AppError` 结构化错误 | `kernel/errors.go` | ✅ `Domain/Kernel/AppError.cs` |
| 0.5 | 验证码冷却异常 | `auth/email_cooldown.go` | ✅ |
| 0.6 | 响应信封 | `handler/response.go` | ✅ `Web/Contracts/ApiEnvelope.cs` |
| 0.7 | `ok`/`fail`/`failService` 投影 | `handler/response.go` | ✅ `Web/Http/ApiResults.cs` |
| 0.8 | Go `omitempty` 等价语义 | 791 处 struct tag | ✅ `Domain/Serialization/GoOmitEmpty*` |
| 0.9 | Go `time.Time` RFC3339Nano | 全量时间字段 | ✅ `Domain/Serialization/GoTimeConverter.cs` |
| 0.10 | Go JSON 转义等价（小写 `\u`） | `encoding/json` | ✅ `Domain/Serialization/GoJsonEncoder.cs` |
| 0.11 | 全局 JSON 契约 | — | ✅ `Web/Serialization/CanvasJson.cs` |
| 0.12 | 请求关联中间件 | `handler/request-context.go` | ✅ |
| 0.13 | CORS 中间件（非法 Origin 403） | `main.go:211-318` | ✅ |
| 0.14 | 异常兜底中间件 | `gin.Recovery()` | ✅ |
| 0.15 | 访问日志（分享 token 脱敏） | `main.go:100-102` | ✅ |
| 0.16 | 环境变量契约 | `main.go: env/envBool/envDuration` | 🟡 核心变量已实现，**缺 20+ 个**（见下方补全项） |
| 0.17 | 构建信息 | `internal/buildinfo` | ✅ |
| 0.18 | 启动编排 | `cmd/server/main.go` | ✅ |
| 0.19 | 健康检查与版本路由（5 条） | `cmd/server/system_status.go` | ✅ |
| 0.20 | OpenAPI 静态输出 | `handler/openapi_embed.go` | ✅ 16558 字节，与原文件一致 |
| 0.21 | 未匹配路由兜底 | `main.go: r.NoRoute` | 🟡 已挂 404，**短代理逻辑待阶段 10.8** |

**环境变量补全项（阶段 0.16 待补）**：`CANVAS_REGISTRATION_ENABLED`、`CANVAS_PUBLIC_BASE_URL`、
`CANVAS_OFFICIAL_PLUGIN_DIR`、`ENABLE_PROVIDER_PLUGINS`、`CANVAS_CHANNEL_CIRCUIT_FAILURES`、
`CANVAS_CHANNEL_CIRCUIT_SECONDS`、`HTTP_PROXY`/`HTTPS_PROXY`/`NO_PROXY`、
`CANVAS_UPDATER_*`（13 个）、`SQLITE_SOURCE_PATH`。

---

## 阶段 1 · 领域模型与持久化层 — 🟡 实体层已完成，仓储层未开始

| # | 模块 | 对应 Go | 状态 |
| --- | --- | --- | --- |
| 1.1 | 25 个字符串枚举类型与 114 个常量 | `model/*.go` | ✅ `Domain/Entities/Enumerations.cs` |
| 1.2 | 身份与账号实体（5 张表） | `model/models_identity.go` | ✅ |
| 1.3 | 渠道与模型实体（4 张表） | `model/models_channel.go` | ✅ |
| 1.4 | 逻辑模型实体（6 张表，另含非持久化 `LogicalModelPriceSKU`） | `model/models_logical_model.go` | ✅ |
| 1.5 | 计费与财务实体（6 张表） | `model/models_finance.go` | ✅ |
| 1.6 | 支付实体（6 张表） | `model/models_payment.go` | ✅ |
| 1.7 | 平台与设置实体（10 张表） | `model/models_platform.go` | ✅ |
| 1.8 | 项目/短剧实体（31 张表） | `model/models_project.go` | ✅ |
| 1.9 | 任务与创作实体（4 + 2 张表） | `model/models_task.go` `models_creation.go` | ✅ |
| 1.10 | 其余实体（CloudAgent / AgentProfile / Plugin / AdminAuditEvent / schema_migrations） | `model/*.go`、`database/migrations.go` | ✅ |
| 1.11 | 建表脚本（**80** 表 + **425** 索引，SQLite 与 PostgreSQL 两份） | `database/schema.go: Models()` | ✅ 由基线生成 |
| 1.12 | 连接工厂与连接池（sqlite 8/4、postgres 30/10） | `database/database.go` | ✅ `CanvasDatabase.cs` |
| 1.13 | 方言抽象（行锁/upsert/分页/迁移锁） | GORM dialector 行为 | ✅ `SqlDialect.cs` |
| 1.14 | 软删除强制过滤 | `gorm.DeletedAt` 自动行为 | ✅ `SoftDelete.cs` |
| 1.15 | 迁移运行器（15 个版本 + 校验和 + 历史 6/7 顺序） | `database/migrations.go` | ✅ `Schema/SchemaMigrator.cs` |
| 1.16 | 仓储层（**30 个文件 / 498 个方法 / 9756 行 Go**） | `repository/*.go` | 🟡 核心子集已完成（用户/会话/验证码/序列号 13 个方法 + 通用入口） |
| 1.17 | 实体列映射与 SQL 构造器 | — | ✅ `EntityMetadata.cs` + `SqlBuilder.cs` |

### 阶段 1 交付物

**实体层（与 ORM 无关，纯 POCO）**

- `src/OpenAICanvas.Domain/Entities/` — 15 个文件，**80 个实体类**（958 列 + 13 个 `gorm:"-"` 瞬态字段）
- `src/OpenAICanvas.Domain/Entities/Enumerations.cs` — 25 个常量类 / 114 个常量
- `src/OpenAICanvas.Domain/Model/ModelText.cs` — 名称归一与字数统计

**持久化层（Dapper）**

- `Persistence/Schema/SqliteSchema.sql`、`PostgresSchema.sql` — 建表脚本，内嵌为程序集资源
- `Persistence/CanvasDatabase.cs` — 连接工厂、事务、连接池
- `Persistence/SqlDialect.cs` — SQLite / PostgreSQL 方言
- `Persistence/SoftDelete.cs` — 软删除强制过滤
- `Persistence/SchemaMigrationCatalog.cs` — 15 个迁移版本定义
- `Persistence/Schema/SchemaMigrator.cs` — 迁移运行器与版本校验
- `Persistence/Schema/Baseline.cs` — 各版本迁移步骤实现
- `src/OpenAICanvas.Tools` — `schema-ddl [sqlite|postgres] [路径]` 输出建表脚本

### 实体层验证结果

| 测试 | 断言 | 结果 |
| --- | --- | --- |
| 真实 SQLite 库表数量与 Go 一致 | 80 = 80 | ✅ |
| 真实 SQLite 库列名与列序一致 | 958 列逐列比对 | ✅ |
| 真实 SQLite 库索引全部创建 | 425 个 | ✅ |
| 原始 SQL 索引也已创建 | 4 个（基线导出抓不到的） | ✅ |
| 软删除表带 `deleted_at` 列 | 3 张表 | ✅ |
| PostgreSQL 建表脚本列类型与 Go 一致 | 958 列逐列比对 | ✅ |
| 全新库迁移写入 15 条记录且名称/校验和与 Go 一致 | v1..v15 | ✅ |
| 重复迁移幂等 | 3 次后仍 15 条 | ✅ |
| 校验和不匹配 / 名称不匹配 / 版本过高 均拒绝启动 | 3 条错误路径 | ✅ |
| 软删除过滤语义 | 默认注入、显式包含、非软删除表不加 | ✅ |
| 响应信封 / omitempty / 时间 / 转义契约 | 17 项 | ✅ |

**合计 40/40 通过，`dotnet build` 0 警告 0 错误。**

**端到端实测**：启动服务 → 自动建库 → `GET /api/health/ready` 返回
`{"code":0,"data":{"status":"ok","ready":true,...,"schema":{"current":15,"expected":15,"ready":true},...},"msg":"ok"}`；
实际库文件 80 表 / 958 列 / 425 索引 / 15 条迁移记录，与 GORM 基线完全一致。

### 生成期修正的真实结构差异

1. **Go `int` → C# `long`**：Go 的 `int` 在 amd64/arm64 上是 64 位，GORM 的 PostgreSQL dialector
   对 `schema.Int` 输出 `bigint`。若映射成 C# `int`，会生成 `integer`，影响 **53 列**。
2. **4 个原始 SQL 索引不在模型导出里**：`idx_schema_migrations_applied_at`、
   `idx_project_asset_candidates_pending_identity`、`idx_logical_model_source_active`、
   `idx_users_email_nonempty` 由 `tx.Exec` 创建，基线只覆盖模型声明的 421 个，
   已补入建表脚本，合计 **425**。
3. **`prompt_templates.enabled` 上有两个索引**：改用 Dapper 后不再受 EF
   "同一列集合只能一个索引" 的限制，两个索引都正常创建。

---

## 阶段 2–12 · 业务模块 — ☐ 未开始

逐模块清单见 `PLAN.md` 第三节。汇总：

| 阶段 | 模块 | 路由数 | 状态 |
| --- | --- | --- | --- |
| 2 | 认证与用户（含根级 `GET /oauth/linuxdo/callback`） | 22 | ✅ 22/22 已通 |
| 3 | 渠道、逻辑模型与路由调度 | — | 🟡 fetch/import 已通（含出站受控 HTTP 客户端）；仅剩 test（依赖阶段 4 provider 执行引擎） |
| 4 | 任务与生成 | 14 + 12 | 🟡 任务读取 + 文本回放 7 条已通；创建/重试/取消/上游查询/SSE 待做（依赖 provider 执行引擎） |
| 5 | 资源、素材与存储 | 31 + 3 | 🟡 画布 CRUD + 素材库全量（含 DELETE 资源级联删除）+ 资源分片上传 + 文件下发（ETag/Range/匿名签名）已通；OSS/Eagle/导出 待做 |
| 6 | 项目与短剧工作流 | 47 | 🟡 项目 CRUD + 单元/画布链接 + 素材文件夹树已通；素材关联/角色/镜头/工作流/工作台待做 |
| 7 | 画布与分享 | 5 + 6 | 🟢 分享 CRUD + 公开投影脱敏 + 公开资源本地投递（含 Range）+ 外观配置全量已通；云重定向待接 |
| 8 | 财务与支付（含独立进程 RPC 插件） | 29 + 21 | 🟢 33 条已通（钱包/签到/兑换码/调账/账单核对 + 支付渠道/商品/订单/回调/对账）；仅剩内置适配器实现（按用户指示暂缓） |
| 9 | 管理后台 | 8 + 4 + 4 + 2 + 2 + 9 | 🟡 功能开放配置 + 平台设置 + 公告 + API 日志/导出 + 存储统计 + 资源分页/批量删除/文件下发已通；分析总览/系统性能/更新待做 |
| 10 | 插件、技能与提示词（含 `NoRoute` 短代理、2 条 `.Any()`） | 18 + 17 + 4 + 1 + 2 + 2 | 🟡 风格档案 6 条 + 技能库读取/状态 8 条 + 提示词模板 4 条已通；技能写入与安装、插件、代理待做 |
| 11 | 云 Agent | 10 | ☐ |
| 12 | 迁移工具、部署与验收 | — | ☐ |

### 内嵌资源待转 `EmbeddedResource`（4 处）

| Go 位置 | 资源 | 目标 |
| --- | --- | --- |
| `handler/openapi_embed.go` | `openapi.yaml` | ✅ 已内嵌 |
| `prompts/agent_policy.go` | `agent-system-policy.md`、`agent-media-policy.md` | ☐ 阶段 11 |
| `protocol/documentation.go` | `docs/*.md`（21 个） | ☐ 阶段 4 |
| `skills/skills_seed.go` | `seed/skills.json` | ☐ 阶段 10.3 |

**路由总数进度：170 / 293 ｜ 仓储方法进度：245 / 498**

> 历轮翻译遗留的取舍、待验证行为与未移植依赖已汇总至
> [`PENDING-CONFIRMATIONS.md`](PENDING-CONFIRMATIONS.md)，由维护者统一处置。

---

## 平台设置模块（功能开放配置，已端到端打通）

| # | 路由 | 状态 |
| --- | --- | --- |
| 9.1 | `GET /public/welcome` | ✅ 无需登录 + `Cache-Control: no-store` |
| 9.2 | `GET /features` | ✅ 需登录 |
| 9.3 | `GET /admin/settings/features` | ✅ 需管理员 |
| 9.4 | `PATCH /admin/settings/features` | ✅ 局部更新语义 |

### 交付物

- `Platform/FeatureAvailability.cs` — 功能开关读写 + `RequireFeature` 守卫
- `Platform/RuntimePolicy.cs` — 完整策略（资源/任务/请求）+ `PublicRuntimeLimits`
- `Application/DrawingEngineService.cs` — 绘图工具设置
- `Web/Endpoints/FeatureEndpoints.cs` — 4 条路由

### 两个关键实现细节

1. **PATCH 的局部更新语义**。Go 是 `req := current; ShouldBindJSON(&req)`——
   只有请求里出现的字段被覆盖。C# 直接反序列化到新对象会**把缺失字段写成默认值**，
   从而把没提交的开关全部改掉。必须按 JSON 实际字段逐个合并。
2. **老配置的向前兼容**。反序列化以默认值为基底，
   这样历史配置缺少新增字段时不会把功能意外关掉（Go 用 `DefaultFeatureAvailability()` 打底）。

### 验证结果（75/75 通过）

覆盖：欢迎开关免登录 + 禁缓存、未登录 401、默认配置（前台模型目录默认关闭）、
非管理员读写被拒、**局部更新只覆盖出现的字段**（连关两次验证互不干扰）、
配置持久化、非法 JSON 400、**老配置缺字段时回落默认值**。

---

## 阶段 2 · 认证（22 条全部已通）

| # | 路由 | 状态 |
| --- | --- | --- |
| 2.1 | `GET /auth/settings` | ✅ |
| 2.2 | `POST /auth/register` | ✅ |
| 2.3 | `POST /auth/email-code` | ✅ |
| 2.4 | `POST /auth/login` | ✅ |
| 2.5 | `POST /auth/logout` | ✅ |
| 2.6 | `GET /auth/session` | ✅ 逻辑模型子系统已实现（见「逻辑模型专项」节） |
| 2.7 | `POST /auth/password-reset-code` | ✅ |
| 2.7b | `POST /auth/password-reset` | ✅ |
| 2.8 | LinuxDO OAuth（`/auth/linuxdo/*` + 根级回调） | ✅ |
| 2.9 | `GET /admin/users`、`POST /admin/users`、`GET /admin/references` | ✅ |
| 2.10 | `POST /admin/users/bulk-disable`、`PATCH /admin/users/:id`、`DELETE /admin/users/:id` | ✅ |
| 2.11 | `GET /admin/users/:id/ledger`、`/tasks`、`/audit-events` | ✅ |
| 2.11b | `GET /admin/users/:id/detail` | ✅ |
| 2.12 | `GET /channels/system` | ✅ 能力配置解码/归一化已补齐 |

### 交付物

- `Auth/AuthContracts.cs` — 注册/登录/公开设置/认证用户/会话结果
- `Auth/AuthService.cs` + `AuthService.Settings.cs` — 登录、注册、会话、口令、验证码
- `Auth/IMailSender.cs` — 邮件发送接口（SMTP 实现属后续模块）
- `Web/Endpoints/AuthEndpoints.cs` — 5 条路由
- `Web/Security/RateLimiter.cs` — 限流 + 会话 Cookie（Set-Cookie 逐字节对齐 Go）
- `Platform/RuntimePolicy.cs` — 频控默认值（与 Go 一致）
- `Application/CanvasService.cs` — 业务组合根

### 验证结果（67/67 通过）

| 测试 | 断言 |
| --- | --- |
| `GET /auth/settings` 首用户分支 | 响应体逐字节比对（含两个零值字段） |
| 首用户注册 | 成为 admin、下发 Set-Cookie、`passwordHash` 不出现、空邮箱被 omitempty 省略 |
| 登录成功 / 失败 | 200 + Cookie / 401 + `reason:unauthorized` + 统一文案 |
| 账号不存在与口令错误 | 返回同一文案（防账号枚举） |
| 被禁用账号 | 403 + `该账号已被禁用` |
| 用户名/口令校验 | 400 + 精确文案 |
| 非首用户注册 | 403 未开放注册 / 400 必须提供邮箱 |
| 登出 | `Max-Age=0` 清 Cookie，无效 Cookie 也返回成功 |
| 未启用邮件时取验证码 | 403 + `平台尚未启用注册邮件` |
| 登录限流 | 第 11 次 429 + `Retry-After` + `code:42901` + `reason:rate_limited` |
| 非法 JSON | 400 |

---

## 剩余工作量（据实清点）

| 部分 | 规模 | 状态 |
| --- | --- | --- |
| 阶段 0 契约基座 | — | ✅ 完成 |
| 阶段 1 实体层 + 建表脚本 + 迁移器 | 80 表 / 958 列 / 425 索引 | ✅ 完成 |
| 阶段 1 仓储层 | 30 文件 / **498 方法** / 9756 行 | 🟡 13 个方法（认证链路子集） |
| 阶段 2–11 路由 | **324 条**（已做 5 条） | ☐ |
| 阶段 2–11 业务逻辑 | `internal/app` 约 **6.8 万行** | ☐ |
| 阶段 2–11 HTTP 层 | `internal/handler` 约 **9.0 千行** | ☐ |

仓储层与业务层合计约 **7.8 万行 Go** 需要逐条翻译。这是**数周量级**的工作，
需要多轮持续推进，无法在单轮内完成。

**推进建议**：按「仓储方法 → 领域服务 → HTTP 路由 → 契约回归测试」的纵向顺序，
每次打通一个完整模块（认证 / 渠道 / 任务 / 项目 …），而不是横向铺开。


---

## 阶段 2 明细（22 条，13 条已通）

| 路由 | 状态 |
| --- | --- |
| `GET /auth/settings` | ✅ |
| `POST /auth/register` | ✅ |
| `POST /auth/email-code` | ✅ |
| `POST /auth/login` | ✅ |
| `POST /auth/logout` | ✅ |
| `POST /auth/password-reset-code` | ✅ |
| `POST /auth/password-reset` | ✅ |
| `GET /admin/users` | ✅ |
| `POST /admin/users` | ✅ |
| `GET /admin/references` | ✅ |
| `POST /admin/users/bulk-disable` | ✅ |
| `PATCH /admin/users/{id}` | ✅ |
| `DELETE /admin/users/{id}` | ✅ |
| `GET /auth/session` | ✅ 逻辑模型子系统已实现 |
| `GET /auth/linuxdo/start` | ✅ |
| `GET /auth/linuxdo/callback` | ✅ |
| 根级 `GET /oauth/linuxdo/callback` | ✅ |
| `GET /channels/system` | ✅ |
| `GET /admin/users/{id}/ledger` | ✅ |
| `GET /admin/users/{id}/tasks` | ✅ |
| `GET /admin/users/{id}/audit-events` | ✅ |
| `GET /admin/users/{id}/detail` | ✅ |

### 本轮新增（8 条）

- `Auth/AuthService.PasswordReset.cs` — 口令重置（**所有失败路径统一文案**，防账号枚举）
- `Application/AdminUserService.cs` — 管理员用户 CRUD + 批量停用
- `Persistence/Repositories/Repository.Admin.cs` — 积分账户、审计、批量停用事务
- `Persistence/Repositories/Repository.Channels.cs` — 渠道查询（**强制软删除过滤**）
- `Web/Endpoints/AdminUserEndpoints.cs` — 6 条路由

### 关键实现点

1. **`DELETE /admin/users/:id` 不是物理删除**。有资金流水后必须保留用户主体，
   实际行为是"停用 + 清除全部登录态"，同时删除文本增量存档。
2. **批量停用是一个事务**。保留校验（含当前管理员、最后一个可用管理员）、
   会话清理、状态更新、审计写入必须原子完成，否则会出现"部分停用"的中间态。
3. **口令重置的账号枚举防护**：账号不存在 / 已禁用 / 无口令 / 验证码过期 / 验证码错误
   ——**全部返回同一文案**"验证码无效或已过期"。取验证码接口同样对不存在的账号静默返回成功。
4. **改口令必须先失效旧会话**，否则旧 Cookie 仍能继续用。
5. **渠道表是软删除表**，所有查询经 `SoftDelete.Apply` 注入 `deleted_at IS NULL`。


---

## 阶段 2 剩余 6 条及其阻塞原因

| 路由 | 阻塞 |
| --- | --- |
| `GET /auth/session` | **逻辑模型**（route catalog + 能力匹配 + 价格展示，约 3700 行） |
| `GET /channels/system` | **能力配置**（`DecodeModelCapabilityConfig` + `NormalizeModelCapabilityConfigForModel`） |
| `GET /auth/linuxdo/start`、`/auth/linuxdo/callback`、根级 `/oauth/linuxdo/callback` | 无依赖，独立模块（`internal/auth/linuxdo.go` 504 行） |
| `GET /admin/users/{id}/detail` | 用户关联数据聚合 |

### 能力子系统规模（共享阻塞）

`model_capability.go`（1056 行）+ `model_router.go`（1413 行）+ `logical_models.go`（1201 行）
= **约 3700 行、85 个函数、33 个类型**。这是 `/auth/session` 与 `/channels/system` 的共同前置，
建议作为下一个专项模块推进。

### 本轮新增（3 条 + 基础设施）

- `Application/CreditPolicyService.cs` — 积分策略与公开投影（`CreditScale = 1_000_000`）
- `Persistence/Repositories/Repository.Finance.cs` — 账本分页、引用键查询、用户任务分页
- `Web/Endpoints/AdminUserEndpoints.cs` — 新增 ledger / tasks / audit-events 三条

### 关键契约细节

1. **账本永远排除 `reserve` 类型**。预扣记录不是用户可见的收支，
   即使不带 `type` 过滤也要排除。测试验证了 3 条记录里只返回 2 条。
2. **limit 的钳制发生在 Count 之后**，所以 `total` 不受 limit 影响。
3. **`checkedInToday` 靠账本引用键判断**：`checkin:<userID>:<UTC 日期>` 是否存在。
4. **分页字段名是 `pageSize`** 而不是 `limit`（Go 的 json tag），已出现多次，注意别写错。


---

## 阶段 2 剩余 2 条及其阻塞原因

| 路由 | 阻塞 |
| --- | --- |
| `GET /auth/session` | **逻辑模型**（route catalog + 能力匹配 + 价格展示，约 3700 行） |
| `GET /channels/system` | **能力配置**（`DecodeModelCapabilityConfig` + `NormalizeModelCapabilityConfigForModel`） |

### 本轮新增（8 条路由 + 基础设施）

- `Auth/AuthService.LinuxDO.cs` — LinuxDO OAuth 完整服务（504 行 Go → C#）
- `Persistence/Repositories/Repository.OAuth.cs` — OAuthState 消费（乐观并发）、UserIdentity、CreateOAuthUser 事务
- `Persistence/Repositories/Repository.Admin.cs` — UserStorageUsage、UserStoredFileBytes、DailyUploadBytes
- `Web/Endpoints/AuthEndpoints.cs` — `GET /auth/linuxdo/start` + `GET /auth/linuxdo/callback`
- `Web/Endpoints/AdminUserEndpoints.cs` — `GET /admin/users/{id}/detail`
- `Program.cs` — 根级 `/oauth/linuxdo/callback` 兼容路由

### 关键实现点

1. **OAuth state 消费用乐观并发**：`UPDATE … WHERE used_at IS NULL` + 检查 affected rows，
   防止并发下同一个 state 被消费两次。
2. **LinuxDO 限流与 Go 端硬编码一致**：start 20/10min、callback 30/10min，
   不是从 `RuntimeRequestPolicy.LoginIpPerTenMinutes`（50）读取。
3. **口令重置的账号枚举防护**：账号不存在 / 已禁用 / 无口令 / 验证码过期 / 验证码错误
   ——全部返回同一文案。
4. **设置加密占位**：`EncryptSecret`/`DecryptSecret` 当前直接返回原值（Go 端是 AES-GCM）。
   这是临时方案，后续需要补全真实加密。
5. **SQLite 表名 `task_text_delta`**（单数），不是 `task_text_deltas`。


---

## 阶段 2 剩余 1 条

| 路由 | 阻塞 |
| --- | --- |
| `GET /auth/session` | **逻辑模型**（route catalog + 能力匹配 + 价格展示，约 3700 行） |

### 本轮新增（1 条路由 + DTO 基础设施）

- `Application/Capabilities/ModelCapabilityConfig.cs` — 完整能力配置 DTO 树（15 个类型）
- `Application/Capabilities/PublicModelChannelDto.cs` — 渠道公开投影 + 出站头 DTO
- `Application/ChannelService.cs` — `PublicSystemChannelsAsync`
- `Web/Endpoints/AdminUserEndpoints.cs` — `GET /channels/system`

### 关键决策

`ModelCapabilityConfig` 的解码与归一化（`DecodeModelCapabilityConfig` + `NormalizeModelCapabilityConfigForModel`）
本轮故意**不实现**——空数据库场景下 `PriceConfigured=false`，不会触发。
等逻辑模型专项时再补齐，避免一次性吞下 3700 行。

---

## 逻辑模型专项（能力子系统第一轮，已端到端打通）

`GET /auth/session` 与 `GET /channels/system` 的共同阻塞是能力子系统
（`model_capability.go` 1056 行 + `model_router.go` 1413 行 + `logical_models.go` 1201 行）。
本轮打通其**读路径全部 + 目录快照 + 公开投影**，并顺带接通 `GET /models`、`POST /models/available`。
管理端 CRUD（`/admin/logical-models` 系列）、报价 `/models/:id/quote`、路由模拟属阶段 3 剩余部分。

### 本轮交付物

- `Application/Capabilities/CapabilitySpec.cs` — 能力规格类型（CapabilitySpec / OptionConstraint /
  InputConstraint / ModelRequestIntent / CapabilityMatch）+ 解码、归一化、匹配、
  供应线路覆盖校验（`validateProductSpecWithinRoutes` 等）+ 默认参数归一化 + 指纹去重
- `Application/Capabilities/ModelCapabilityConfigOps.cs` — 渠道能力 JSON 解码 / 三类能力校验 /
  `CapabilitySpecFromModelCapabilityConfig` 投影 / `supportsTokenBilling` /
  `normalizeChannelModelTierResolution`（此前 `.cs` 里已有 DTO 树，本轮补齐全部行为）
- `Application/Capabilities/ModelSku.cs` — SKU 选择器规范化 / 意图选择器 / 价格档匹配
  （精确规格优先、通配兜底、按命中键数打分）
- `Application/LogicalModelService.cs` — 路由目录快照（30s TTL、2s 失败冷却、5min 旧快照续服务）、
  本地健康阻断表、`PublicLogicalModelsAsync`、价格档与价格展示计算、
  `capabilitySpecWithRoutePresets` / `capabilitySpecWithPriceTiers`
- `Persistence/Repositories/Repository.LogicalModels.cs` — LogicalModels / LogicalModel /
  LogicalModelRevision / LogicalModelGraphs（批量 IN 加载防 N+1）/ ChannelModelsByIDs /
  SystemChannelsByIDs / 价格档附着（含旧库默认档兜底，软删除表强制过滤）
- `Web/Endpoints/ModelEndpoints.cs` — `GET /models`、`POST /models/available` 两条路由
- `SessionDto` 按 gin.H 字典序重排（drawingEngine → features → logicalModels → runtimeLimits → user）

### 关键实现点

1. **map 键的字典序契约**。Go 的 `encoding/json` 对 map 按 key 字典序输出；
   C# 字典保插入序。所有 `inputs` / `options` / `selector` / `defaultOptions`
   构造时必须按 `StringComparer.Ordinal` 排序插入，否则响应体与 Go 不逐字节一致。
2. **空 map / nil 切片的 omitempty**。Go 对 `*int64` 的 omitempty 只在 nil 时省略
   （`displayPrice` 多档时整个字段消失，不是输出 null）；空 map（如音频能力的
   `inputs`/`options`）同样整体省略。C# 侧统一以 null 表达。
3. **能力值比较经 normalizedScalar**。JSON 数字统一走 float64 最短十进制格式
   （Go `FormatFloat(v,'f',-1,64)` 等价实现），`vquality` 比较前先过别名归一
   （`"480"/"low" → "480p"`）再去掉 `p` 后缀，与 Go 逐字一致。
4. **目录快照的多实例协调先落本地**。Go 用 Redis 协调目录版本与线路健康；
   .NET 侧当前无协调器，版本号与健康阻断先放进程内存，接口形状不变，
   接入协调器时调用方零改动。
5. **产品规格的 inputs 必须显式声明**。意图带 `image:1` 而产品规格未声明 inputs 时
   判定为「不支持 参考图片输入」并被目录排除——这是 Go 的真实行为，测试种子已按此建模。
6. **存量红测试修复**：`AuthEndpointTests.登录后会话恢复返回完整用户信息` 的 Cookie
   从未真正附加到请求（CookieContainer 未接入工厂客户端），且断言了不存在的
   `runtimeLimits.maxFileBytes` 字段、假设首注册用户不是管理员；已按 Go 契约修正。

### 验证结果（163/163 通过，`dotnet build` 0 警告 0 错误）

- 能力规格单元测试 24 项：归一化报错文案、别名归一、vquality 互换比较、
  数值步长匹配、通配语义、供应线路拼合覆盖、默认参数通配回落、
  SKU 精确优先、价格档收窄、指纹顺序无关、Agnes/解析度归一
- 端到端 9 项：未登录 401、空目录 `[]`、完整投影逐字段断言（含字段顺序）、
  意图匹配过滤、非法 JSON 400、未登录 session `{"user":null}` 逐字节、
  登录 session 键序 + 模型目录、系统渠道能力配置归一化、损坏配置省略字段
- 端到端实测：启动服务 → 建库 → 注册 → `/auth/session` 返回
  `{"drawingEngine":…,"features":…,"logicalModels":[],"runtimeLimits":…,"user":…}`（字典序），
  未登录 `/models` 返回 401 + `reason:unauthorized`

### 阶段 3 剩余（下一轮建议）

- `GET /channels`、`POST /channels` 等渠道管理 CRUD（`channel*.go`）
- ~~`GET /admin/logical-models`、`POST/PATCH/DELETE /admin/logical-models`~~ ✅（见下节）
- ~~`POST /models/:id/quote`、`POST /admin/logical-models/:id/simulate`~~ ✅（见下节）
- `ResolveLogicalModel` 已实现；`switchTaskToNextRoute` / `beginTaskRouteAttempt` 属任务阶段（阶段 4）

---

## 逻辑模型管理端与报价（能力子系统第二轮，已端到端打通）

本轮打通逻辑模型模块的**管理端 CRUD、路由模拟、任务选路核心与报价**，
新增 6 条路由：`GET/POST /admin/logical-models`、`PATCH/DELETE /admin/logical-models/:id`、
`POST /admin/logical-models/:id/simulate`、`POST /models/:id/quote`。

### 本轮交付物

- `Application/LogicalModelService.Admin.cs` — `AdminLogicalModels`（管理投影含线路明细与
  configurationError/availabilityError 归因）、`SaveAdminLogicalModel`（`logicalModelBundle` 全套校验：
  code 正则、能力一致性、价格策略/计费方式、Token 计费协议约束、启用前结构/结算校验）、
  `DeleteAdminLogicalModel`（归档 + 审计 + 活动任务拒绝）
- `Application/LogicalModelService.Routing.cs` — `ResolveLogicalModel`（能力合同 + 启用状态 +
  价格档 + 健康阻断四重约束）、`eligibleLogicalRoutes`（高优先级清空选路池）、
  `weightedRoute`（加密随机数拒绝采样）、`sortedRouteDiagnostics`（模拟诊断）、`QuoteLogicalModel`
- `Application/BillingCalc.cs` — 计费估算纯函数：`billingQuantity`、方舟视频 token 像素帧公式
  （`estimateArkVideoTokens` + Seedance 分辨率/比例归一）、`creditAmount`（整数向上取整）、
  `tokenEstimateAmount`（每百万单价换算）、溢出保护逐条对齐 Go
- `Persistence/Repositories/Repository.LogicalModelWrites.cs` — `ChannelModelAsync` /
  `ChannelModelByKeyAsync`（软删除过滤 + 价格档附着）、`SaveLogicalModelBundleAsync`
  （事务：模型行 `revision_sequence` 原子递增 → 版本 → 线路 → 回填 active_revision_id）、
  `ArchiveLogicalModelAsync`（PostgreSQL 行锁 + 活动任务检查 + 审计同事务）
- `Web/Endpoints/ModelEndpoints.cs` — 新增 6 条路由（错误文案逐字对齐：
  「前台模型参数格式错误」「路由模拟请求格式错误」「模型报价请求格式错误」）

### 关键实现点

1. **`AdminLogicalModelDto` 继承 `PublicLogicalModelDto`** 复刻 Go 匿名内嵌：
   JSON 序列化基类字段在前（id…available），派生字段在后（enabled、activeRevisionId、
   revisionVersion、configurationError、availabilityError、routes），与 Go 逐字节一致。
2. **版本号必须由模型行原子递增**。`MAX(version)+1` 会让并发事务分配同一版本；
   C# 与 Go 一样在事务内 `UPDATE … revision_sequence = revision_sequence + 1` 并要求命中一行。
3. **裸 error 与 AppError 的投影差异**。Go 的「供应线路引用的渠道模型不存在」、
   `gorm.ErrRecordNotFound` 是裸 error → failService 投影为 **500 固定文案**；
   C# 对应 `InvalidOperationException`，不能误包装成 4xx AppError。
4. **报价的默认参数会先合并进意图**。`ResolveLogicalModel` 先改写 `intent.Options`，
   所以 per_second 报价在请求不带时长时也能被默认 `videoSeconds` 填充成功；
   超出产品规格的时长则被能力匹配以「所选模型不支持当前请求」拒绝——两条都是 Go 真实行为。
5. **渠道策略报价的档位选择不带意图**。Go 报价调用 `newBillingOrderWithPriceTier` 时不传 intent，
   档位按空选择器匹配（只有通配档能命中，多条精确档取命中键数最高者），计价后组装
   BillingOrder 对象但不落库。

### 验证结果（179/179 通过，`dotnet build` 0 警告 0 错误）

新增 16 项端到端测试：未登录 401、创建（管理投影逐字段 + 审计写入）、
非法 code / 缺失能力配置 / 启用无线路 / 按秒计费限视频 4 类 400 校验、
更新创建新版本（revisionVersion=2 且 activeRevisionId 变化）、
归档后从列表消失且重复删除报错、路由模拟（inPool 候选 + 未发布模型 400）、
报价三类计费（渠道档位 1_500_000、统一按次 2_500_000、统一按秒默认时长填充 5×2_000_000）
与异常路径（空意图能力拒绝、不存在模型「所选模型不可用」）。
端到端实测：登录后 `/admin/logical-models` 空列表、报价/模拟未登录 401、非法 body 400 中文文案。

### 阶段 3 剩余（下一轮建议）

- ~~渠道模型写路径（保存/价格档归一化/删除）~~ ✅
- ~~渠道/模型排序（`/admin/channels(/:id/models)/order` GET+PUT，乐观并发 409）~~ ✅（见下节）
- ~~`EnsureSystemChannelModels` 启动种子~~ ✅（Program.cs 迁移后调用）
- ~~渠道模型 fetch/import（上游模型目录拉取与导入）~~ ✅（见下节）
- 渠道模型 test（`TestAdminChannelModel` 依赖阶段 4 provider 执行引擎，随任务模块一并移植）
- Go main.go 启动序列的其余种子：`EnsureDefaultPromptTemplates` /
  `EnsureBuiltinProjectWorkflowTemplate` / `EnsureBuiltinSkills` / `EnsureSkillPackages` /
  `MigrateLegacyStorage`（阶段 6/10）
- 系统中转与代理（阶段 10 提前依赖评估）

---

## 渠道排序与启动种子（能力子系统第五轮，已端到端打通）

本轮打通 **4 条排序路由**（`GET/PUT /admin/channels/order` 与
`GET/PUT /admin/channels/:id/models/order`，Go 以两条路径循环注册）并接入
`EnsureSystemChannelModels` 启动种子。

### 本轮交付物

- `Repository.SaveChannelOrderAsync` — 事务内乐观并发：读当前顺序与 `expectedIds` 严格相等、
  长度一致、`ids` 无重复且全部已知，任一不满足回 `ErrChannelOrderChanged`；
  逐项写 `sort_order = index`；模型排序附加刷新渠道 ModelsJSON；
  PostgreSQL 先按稳定序 `FOR UPDATE` 锁再读快照（对齐 Go 注释的行为）
- `ChannelAdminService.AdminChannelOrderAsync / SaveAdminChannelOrderAsync` —
  渠道列表项 `{id,name,enabled}`（模型项名称取 displayName 优先）；
  快照过期回 **409** +「列表已发生变化，请重新打开排序后再保存」；
  `ids`/`expectedIds` 缺失或超 10000 → 400「请重新加载完整排序列表」；
  保存成功失效路由目录
- `EnsureSystemChannelModelsAsync` — 系统渠道无任何渠道模型记录时按 ModelsJSON 补占位
  （已停用但存在的记录不重建，保持管理员手动清理结果）；`Program.cs` 在迁移后、监听前调用
- 路由体上限：排序 PUT 请求体 1MB（对齐 Go `MaxBytesReader`）

### 验证结果（202/202 通过，`dotnet build` 0 警告 0 错误）

新增 4 项端到端测试：未登录 401、渠道排序读/交换保存/旧快照 409/长度与未知 ID 409、
`ids` 缺失 400、模型排序保存后 ModelsJSON 顺序刷新。
端到端实测：渠道与模型排序 GET → 正确保存 → 过期快照 409（含中文文案）→
交换保存后 ModelsJSON 按启用模型刷新。

---

## 上游模型目录拉取与导入（能力子系统第六轮，已端到端打通）

本轮落地**出站受控 HTTP 客户端**并打通 **2 条路由**：
`POST /admin/channels/:id/models/fetch`（只读目录预览，限流 10 次/分钟/用户+渠道）与
`POST /admin/channels/:id/models/import`（导入所选模型，请求体上限 64KB）。

### 本轮交付物

- `OutboundHttpClient`（Outbound）— 受控出站客户端：`SocketsHttpHandler.ConnectCallback`
  内完成「解析 → SSRF 校验 → 拨号到已验证 IP」，TLS SNI/Host 保留原主机名；
  按序尝试全部解析地址（对齐 Go net.Dialer）；代理主机直连旁路（`configuredProxyHost`）；
  代理本身读 HTTP(S)_PROXY 环境变量（.NET DefaultProxy 同源）
- `OutboundGuard.ResolveOutboundHostAsync` — 解析+校验单一入口，避免连接期二次解析
- `ChannelModelCatalogService` — `/models` 目录拉取：openai/gemini 双格式
  （gemini 默认 `/v1beta` + `x-goog-api-key`）、`apiURL` 版本前缀收敛
  （`channelAPIPrefixes` 七个前缀、请求路径显式版本优先）、64MB 响应上限、
  上游错误文案映射（401/403→鉴权失败、404→未提供 /models、429→频繁、
  default→providerHTTPError.Error() 等价文案）
- `ChannelModelAdminService` — `PreviewAdminChannelModels`（只读）、
  `FetchAdminChannelModels`（服务面保留，路由未用，与 Go 一致）、
  `ImportAdminChannelModels`（仅导入仍在上游目录中的模型、≤500、
  按 ProviderModelKey 去重、退役 SKU 跳过、创建停用未定价占位）
- `Repository.CreateMissingChannelModelsAsync` — 批量 INSERT ON CONFLICT DO NOTHING，
  返回受影响行数

### 关键实现点

1. **`localhost` 双栈陷阱**。`localhost` 同时解析 `::1` 与 `127.0.0.1`，测试上游只绑
   IPv4 时连接被拒——Go dialer 逐地址回退，C# 初版只试第一个地址。已改为按序尝试全部
   地址；测试监听改 `IPv6Any + DualMode`。
2. **ConnectCallback 与代理**。设置了代理时回调收到的是**代理**端点，需要
   `configuredProxyHost` 旁路直连；.NET DefaultProxy 在 Windows 读系统代理设置。
3. **目录服务无 apiKey 必 400**（「请填写 API Key」）——Go 同样强校验，测试渠道必须带密钥。

### 验证结果（206/206 通过，`dotnet build` 0 警告 0 错误）

新增 4 项端到端测试（本地 TcpListener 假上游）：目录去重/剥前缀/字典序 + Bearer 鉴权头、
导入创建停用占位且重复导入 added=0、空选择与未知模型 400、上游 401 → 502「鉴权失败」。

### 阶段 3 收尾状态

阶段 3 全部路由仅剩 `POST /admin/channels/:id/models/test`（依赖阶段 4 provider 执行引擎）。
下一轮建议进入**阶段 5（资源/存储）**或直接攻坚阶段 4（任务与生成）。

---

## 画布工程 CRUD（阶段 5 节点 A，已端到端打通）

本轮打通 **4 条画布路由**：`GET /canvas-projects`（摘要/分页双形态）、
`GET/PUT/DELETE /canvas-projects/:id`。对应 Go `handler/user_data.go` 与 `internal/canvas` 域。

### 本轮交付物

- `UserDataService`（Application）— 画布 upsert 全链：`ValidateSyncedPayload`（4MB 上限 +
  递归禁内嵌 `data:image|video|audio`）、`canvasProjectFromJSON`、
  **画布媒体守卫**（`ValidateCanvasMediaAssets`：media 节点/timeline.clips 的
  storageKey/content/url/dataUrl 只能通过本用户素材指向 ready 资源，
  `resource:` 前缀与 `/api/resources/` 文件 URL 两种定位）、
  结构化配额（`StructuredDataMB`/`CanvasCount` + `UserStorageUsage` 实测）、
  进程内存储锁（对齐 Go Service 级互斥）、画布库分页
  （`projectId=independent` 过滤、标题搜索、name/nodes 双排序——
  nodes 排序按方言用 `json_array_length` / `jsonb_array_length`）
- `Repository.CanvasProjects.cs` — 8 个方法：列表/摘要/单读/upsert（未命中回退插入）/删除
  （连带清理 canvas_shares、canvas_unit_links，解绑 tasks.project_id）/分页/素材与资源批量取
- `UserDataEndpoints` — 4 条路由；PUT 请求体 5MB、`canvas-write:{uid}` 限流
  （`CanvasWritePerMinute`）、画布 ID 与路径一致性校验、单读不存在回 **404**
  （Go 用 `fail(404, err)` 而非 failService）

### 关键实现点

1. **`JsonSerializer.SerializeToElement(string)` 是再编码不是解析**——payload 会变成
   JSON 字符串元素；必须 `JsonDocument.Parse(...).RootElement.Clone()`（本轮实测踩坑）。
2. **单读 404 与全局 500 的分叉**：Go 的 `fail(c, 404, err)` 会回显错误原文，
   与 `failService` 的 5xx 固定文案路径不同；C# 对 `InvalidOperationException` 特判 404。
3. **分页 `sort=nodes` 依赖 JSON 函数**：SQLite `json_array_length` / PG `jsonb_array_length`，
   已按方言分支。

### 验证结果（213/213 通过，`dotnet build` 0 警告 0 错误）

新增 7 项端到端测试：未登录 401、空列表、upsert→单读→摘要→分页→覆盖保存全流程、
ID 不一致 400、内嵌媒体拒绝、未入库媒体守卫拒绝、删除后 404、标题搜索与独立画布过滤。

### 阶段 5 剩余（下一轮建议）

- ~~画布工程 CRUD~~ ✅（节点 A）
- ~~素材库：batch/分页/facets/单读/upsert/快照/分类/移动~~ ✅（节点 B）
- ~~`DELETE /assets/:id`（资源级联判定链）~~ ✅（见下节）
- ~~资源上传三件套（`/resources/uploads` 会话/分片/complete）~~ ✅（阶段 5.1）
- ~~文件下发（`/resources/:id/file`、`/public/resources/:id/file`，含 ETag/Range/匿名签名）~~ ✅（阶段 5.8）
- 存储位置与 OSS 设置（`/settings/oss`，test 需云 SDK）、Eagle 集成
- `ReplaceUserCanvasProjects` / `ReplaceUserAssets`（同步接口服务面）
- 提示词偏好（`/settings/prompt-templates`，阶段 10.5）

---

## 素材库全量（阶段 5 节点 B，已端到端打通）

本轮打通 **8 条素材路由**：`POST /assets/batch`（体上限 16KB、≤100 个）、
`GET /assets`（未分页→摘要，带过滤参数→分页+facets）、`GET/PUT /assets/:id`
（PUT 体上限 5MB、`assets-write:{uid}` 限流 `AssetWritePerMinute`、ID 一致性校验）、
`GET/POST/PATCH/DELETE /asset-folders`、`PATCH /assets/folder`（≤200 个）、
`GET /user-data/snapshot`。

### 本轮交付物

- `AssetLibraryDomain` — `AssetFromJSON`（4MB+内嵌媒体校验、ID ≤80、主版本 ID ≤36、
  `validateUserAssetDocument` 六类素材 per-kind data 校验、类别归一化、status 缺省 confirmed）
- `UserDataService` 扩展 — 素材 upsert（storage lock + 分类存在性 + **`ValidateAssetCanvasReferences`**
  画布反向引用守卫 + 结构化配额）、`ClientAssetPayload`（补齐 coverUrl/tags/时间戳/
  image·video 正数尺寸，cover 从 dataUrl/url/storageKey 推导）、分页与三组分面、
  分类 CRUD（重名拒绝、删除时素材先移出并回写 payload.folderId/updatedAt）、批量移动
- `Repository.AssetLibrary.cs` — 12 个方法（分页/facets/摘要/单读/批量/upsert/
  分类全套/move 事务含 payload 回写）
- `UserDataEndpoints` — 8 条路由

### 关键实现点

1. **素材移动要同步回写 payload**：Go `assetPayloadWithFolder` 更新 payload 内
   `folderId`（空则删除键）与 `updatedAt`（RFC3339Nano），并**按键字典序输出**（Go map 序列化）；
   C# 对齐（updatedAt 固定 7 位小数与 Go 的去尾零存在字节差异，见待确认清单 #23）。
2. **画布反向引用守卫**：素材 payload 变更时若该素材仍被画布媒体节点引用、
   且新 payload 不再包含原 resourceID → 400「素材仍被画布引用，不能替换为其他云端资源」。
3. **facets 的 status 过滤**：active（≠archived）/archived/原样三种分支；
   分页含 title 与 payload_json 双列 LIKE。

### 验证结果（219/219 通过，`dotnet build` 0 警告 0 错误）

新增 6 项端到端测试：upsert 补全契约（status 归一 confirmed）与批量读取、
缺 coverUrl/未知类型 400、分页 facets 搜索与 folderId 过滤、分类 CRUD（重名/删除移出/missing 400）、
批量移动（payload.folderId 回写）、快照双集合。

### 阶段 5 剩余（下一轮建议）

- ~~`DELETE /assets/:id`（资源级联判定链）~~ ✅（见下节）
- ~~资源上传三件套（`/resources/uploads` 会话/分片/complete）~~ ✅（阶段 5.1）
- ~~文件下发（`/resources/:id/file`、`/public/resources/:id/file`，含 ETag/Range/匿名签名）~~ ✅（阶段 5.8）
- 存储位置与 OSS 设置（`/settings/oss`，test 需云 SDK）、Eagle 集成
- `ReplaceUserCanvasProjects` / `ReplaceUserAssets`（同步接口服务面）
- 提示词偏好（`/settings/prompt-templates`，阶段 10.5）

---

## 素材删除与资源级联（阶段 5 节点 C，已端到端打通）

本轮打通 `DELETE /assets/:id`——翻译 Go `resource_delete.go` 的完整判定链与级联事务。

### 本轮交付物

- `Repository.ResourceReferences.cs` — **全业务面资源引用快照**（素材/画布/任务/创作会话/
  创作执行项/任务日志/任务结果/项目（含主图直引）/风格/素材版本/项目候选/工作流步骤/
  镜头产物/声音/公告/公告草稿共 16 类文档与直引）、素材业务引用（项目链接/镜头引用/候选）、
  素材版本+表现记录、同物理对象共享计数、`DeleteAssetAndResources` 级联事务
  （shot_asset_references → character_voice_bindings → asset_representations →
  project_asset_links → project_asset_candidates → asset_versions → asset →
  resource_deletion_jobs → ark_private_asset_bindings → resources）
- `ResourceDeleteService` — 删除判定链：payload/版本/表现的 owned 资源收集 →
  **被其他素材共享的物理对象剔除** → 占用文案（去重、标题截 32、最多 3 条 +「等 N 处」）→
  删除任务（Outbox）与业务删除同事务 → 本地物理文件 drain 清理（含符号链接逃逸校验）
- `DELETE /assets/:id` 路由接线

### 关键实现点

1. **两道守卫缺一不可**：asset payload 的 `data.storageKey`/`coverUrl` 收集 owned 资源；
   快照里「其他素材文档命中同资源」时该资源标记 shared 不删除——
   防止误删仍被别的素材使用的物理文件。
2. **Outbox 模式**：业务删除与 deletion_jobs 同事务；失败物理文件完全不动，
   成功后同步 drain 本地任务（Go 为异步 goroutine，C# 内联等价）。
   **云 provider（OSS/COS/Kodo/S3）的任务保留 pending**，等待云 SDK worker（待确认 #25）。
3. 测试踩坑：种子数据的 UserID 必须用真实用户 hex ID 而非用户名——
   快照/资源查询全部按 user_id 过滤。

### 验证结果（222/222 通过，`dotnet build` 0 警告 0 错误）

新增 3 项端到端测试：删除无引用素材（资源同删 + 本地文件清理 + 任务 done）、
被画布引用返回 400 占用提示且素材保留、删除不存在素材 500（Go 裸 ErrRecordNotFound 语义）。

### 阶段 5 剩余（下一轮建议）

- 资源上传三件套（`/resources/uploads` 会话/分片/complete）与文件下发（`/resources/:id/file`）
- 存储位置与 OSS 设置（`/settings/oss`）、Eagle 集成
- `ReplaceUserCanvasProjects` / `ReplaceUserAssets`（同步接口服务面）
- 提示词偏好（`/settings/prompt-templates`，阶段 10.5）

---

## 画布分享（阶段 7 节点，已端到端打通）

本轮打通 **6 条分享路由**：`GET/POST/DELETE /canvas-projects/:id/share`、
`GET /public/canvas-shares/:token`、`GET /public/canvas-shares/:token/resources/:resourceId/file`。

### 本轮交付物

- `Repository.CanvasShares.cs` — 按项目/令牌哈希查分享、upsert、按项目删除、`ResourceForUser`
- `CanvasShareService` — 令牌签发（32 字节 base64url，仅存 SHA-256 哈希，明文经设置加密
  通道回显）、有效期 0–365 天、轮换（rotate）、撤销；
  **公开投影脱敏**（metadata 白名单 60+ 键、禁用键 apiKey/storageKey/task* 递归剥离、
  media 节点 content 重写为带令牌代理 URL 并登记放行资源）、
  公开资源投递（令牌 → 放行清单 → 本地文件流，安全响应头全套）
- `MapCanvasShareRoutes` — 6 条路由；公开端限流 120/min 与 300/min（按 IP）

### 关键实现点

1. **脱敏是双层的**：metadata 只留白名单键，其余对象递归剥离禁用键——
   即使白名单键的值是对象，内部的 apiKey/storageKey 仍会被剥掉。
2. **media content 重写**：storageKey/content 指向的资源登记进放行清单后，
   前端统一用 `/api/public/canvas-shares/{token}/resources/{id}/file` 访问；
   不在清单的资源一律 404。
3. **令牌明文只在创建者会话可见**：公开接口只收令牌本身，数据库只有哈希。

### 验证结果（227/227 通过，`dotnet build` 0 警告 0 错误）

新增 5 项端到端测试：生命周期（创建/回读/撤销）、有效期越界 400、公开投影脱敏
（taskId 剥离、prompt 保留、apiKey 剥离、storageKey 不出现、content 重写为代理 URL）、
公开资源令牌放行 + 非放行 404 + 无效令牌 404、撤销后公开链接失效。

---

## 创作画布提交（阶段 4.15 收尾，已端到端打通）

本轮打通 **3 条路由**：`POST /creation-runs/:id/canvas`（创建/关联画布）、
`GET /creation-runs/:id/canvas-snapshot`、`POST /creation-runs/:id/canvas-commit`
（获批范围 diff 校验 + 乐观提交）。PENDING #63 关闭。

### 本轮交付物

- canvas：守卫/状态/已批准校验 → 新建默认画布（结构化配额 + 事务内落库 +
  绑定 run.CanvasID + revision+1 + waiting_canvas）或幂等返回已关联画布
- snapshot：画布文档 + `creationHash` 快照哈希
- commit：`ValidateSyncedPayload` + 媒体资产守卫 + 快照哈希乐观锁 +
  **获批范围 diff**：顶层字段（nodes/connections/updatedAt 外）禁改禁增、
  节点禁删、更新节点按 ops 逐字段重放比对（含 baseline 手工编辑三态检测）、
  新增节点按 `creationAddedNode` 默认参数比对（8 类节点默认尺寸/标题）、
  连线禁改禁删/新增需批准且端点存在、结果回写须绑定本运行提交的真实成功任务
  （taskId/nodeId/资源指纹/成功态四重校验）→ CompareSave 乐观提交
- `CreationRunMutationContext` 补事务内用户存储用量 / 画布读取与写入 /
  任务读取；`SameJson` 改为**语义相等**（键序与转义无关，字符串按解码值、
  数字按值）替代 raw-text 比较

### 关键实现点

1. **客户端回传的 JSON 转义形式不可信**（HttpClient 默认编码会把非 ASCII 转成
   unicode 转义），raw-text 比较会误报「方案外内容」；改为递归语义比较。
2. `MutateCreationRun` 事务内**所有读写必须走同一连接**——回调里经
   `_repository.*` 的辅助查询会开新连接，SQLite 下死锁/超时；
   统一改走 `CreationRunMutationContext` 的事务内方法（含 UserStorageUsage）。
3. `MutateCreationRun` 的 UPDATE 需覆盖 `canvas_id`（CreateRunCanvas 会改它）。

### 验证结果（488/488 通过）

新增 2 项测试（未批准 409 → 批准后创建画布 → 快照 → 原样提交 → 过期哈希 409；401）。

---

## 创作运行（阶段 4.15 主体，已端到端打通）

本轮打通 **13 条路由**：`GET/POST /creation-runs`、`GET/PATCH /creation-runs/:id`、
`POST /creation-runs/:id/{claim,heartbeat,release,proposal-approve,proposal-invalidate}`、
`POST /creation-runs/:id/submissions/{prepare,approve,refresh}`、
`POST /creation-runs/:id/execute`。

### 本轮交付物

- `Repository.CreationRuns` — 运行 CRUD（clientKey 幂等创建：同键同内容幂等返回、
  不同内容冲突）、`MutateCreationRunAsync`（条件写入拿行锁 → 回调读写 → 统一保存，)
  + `CreationRunMutationContext`（回调内读写同事务：提交读写/撤销/资源读取/
  价格签名/任务+积分预留原子创建）、存储用量聚合
- `CreationRunService` — 创建（state JSON 校验：≤1MB、密钥字段/内嵌媒体/
  临时签名链接拒绝）、claim（代次 + 45s 租约）、save（revision 乐观锁 + 状态白名单 +
  取消后不可恢复）、proposal-approve（操作校验 1-100、资源归属、版本/哈希幂等、
  撤销旧报价）、proposal-invalidate、submissions/prepare（admission 不落库 + 
  后置规格一致性与文本图片理解能力检查 + 报价 5 分钟过期 + 签名哈希）、
  approve（重算报价哈希一致 + 价格签名复核 + 批准）、refresh（requote: 前缀继任项 + 
  原报价撤销）、execute（事务内创建任务 + 提交绑定 TaskID 幂等）
- 准入复用：`TaskCreationService.AdmitQueuedAsync`（admission 不落库变体）+ 
  `TaskBillingOrderAsync`（credits 门控/channel+unified 计价）

### 已知取舍（PENDING #63）

1. 画布提交三路由（canvas / canvas-snapshot / canvas-commit）属 creation_canvas.go
   （571 行，含 approvalBaseline / ops 应用 / 快照哈希），随下一批接入。
2. prepareCreationTask 的 agentRequests 占位符水合（validateAgentResourcePlaceholders）
   未移植——创作仅支持 text 模式携带 agentRequests 且本期默认不带。
3. 报价/请求哈希采用 Ordinal 键序规范化 JSON 的 SHA-256；与 Go 的语义一致
   （Go json.Marshal 也按键排序），但跨实现字节级一致性未验证（同 PENDING #47 家族）。

### 验证结果（486/486 通过）

新增 3 项测试（创建/claim/保存/心跳/release/幂等/冲突、密钥与内嵌媒体拒绝、401）。

---

## 任务生命周期（阶段 4.3，已端到端打通）

本轮打通 **3 条路由**：`POST /tasks/:id/retry`、`POST /tasks/:id/cancel`、
`POST /tasks/:id/query-provider`（准入门槛 + 归属校验）。

### 本轮交付物

- `TaskLifecycleService` — retry：creation 冲突 409、cloud_agent 拒绝、
  状态白名单（failed/cancelled）、上游取消待确认拒绝、计费核对
  （uncertain → 费用核对文案）、内容审核拒绝、归档模型快照重试
  （revision/route/channelModel/渠道四级失效文案 + 能力合同重匹配 + 价格配置检查）、
  `RetryTaskWithBilling` 事务（限额 + 积分预留 + 字段重置 + route_run+1 +
  文本增量清空，task_not_retryable 幂等冲突）
- cancel：条件更新（CancelTaskIfStatus，用户/状态双守卫）、幂等返回、
  状态漂移报错、ProviderRequestID 补齐（账单 + 最近请求日志）、
  文本回放草稿保留（7 天）、排队取消直接退款 / 运行取消冻结待核对
- query-provider：失败态/视频类型/上游 ID/账单归属四级门槛，
  上游查询本身依赖 provider 引擎（PENDING #60，返回 Go 非声明式协议同文案）
- `Repository.TaskWrites.RetryTaskWithBillingAsync` +
  `Repository.TaskAdmission.LogicalModelRouteAsync`/`SystemChannelByIDAsync`/
  `CreateTaskLogAsync`

### 已知取舍（PENDING #60）

取消时带 ProviderRequestID 的任务，Go 会后台请求上游取消；本批跳过该 HTTP 调用
（费用留给人工核对/retry 对账，与 Go 的最终账务路径一致），记录 warn 日志。

### 验证结果（483/483 通过）

新增 6 项测试（取消排队任务退款+幂等、取消完成任务 400、
重试失败任务重新入队+幂等冲突、重试运行中 400、查询非失败任务 400、401）。

---

## 任务创建批 2（阶段 4.2，已端到端打通）

本轮打通队列任务 admission：`POST /tasks` 队列路径全语义（与文本回放路径合计完成
POST /tasks 全量），并新增 6 项队列端到端测试。

### 本轮交付物

- `TaskCreationService.Admission` — 队列三分支选路：
  前台模型（ResolveLogicalModel + applyRoutedProviderSelection：供应链字段剔除、
  能力默认值回填、capabilityOptions 写回执行配置、渠道模型/价格档 ID 写入）、
  系统渠道（渠道/模型/协议/能力合同逐级校验 → 能力参数提取 → intent 匹配 →
  图片定价规格归一（quality/size 缺省 1k）→ 价格档选择 → apiFormat/interfaceType 写入）、
  自定义渠道（CustomChannels 门控 + 直通）
- `ModelRequestIntentFromTaskInput` 全量移植（模式/类型推导、参考输入计数、
  mask、capabilityOptions 优先、config 兜底、quality auto/any 剔除）
- `taskBillingOrder` + `newLogicalModelBillingOrder` — credits 门控、
  channel 策略走 BuildBillingOrderWithPriceTier（order.Model=前台 code）、
  unified 策略按 fixed/per_second/token 三模式计价（PriceVersion 取 revision.Version）
- 工作流协议门控（RunningHub 插件未启用 → 与 Go 一致 400 信封）、
  视频模式检查、内嵌媒体检查、ensureTaskProjectActive 归档守卫、
  protectTaskSecrets + 存储配额 + CreateTaskWith* 事务（批 1 已备）、任务日志
- `Repository.TaskAdmission` — LogicalModelRoute/SystemChannelByID/CreateTaskLog

### 已知取舍（PENDING #59）

Go 的 `ValidateTaskCapability`（图片/视频参数逐值校验）未移植——
OPTION 级约束已由 MatchCapability 与渠道能力合同匹配覆盖；图片/视频逐值校验
随 worker 执行批次补齐。

### 验证结果（477/477 通过）

新增 6 项队列测试（前台路径入队+脱敏+日志、缺 logicalModelId 400、
系统渠道缺配置 400、视频模式 400、RunningHub 400、内嵌媒体 400）。

---

## 任务创建批 1（阶段 4.1，已端到端打通）

本轮打通 **3 条路由**：`POST /tasks`（文本回放路径全语义 + 队列路径守卫）、
`GET /tasks/:id/text-events`（SSE）、`POST /timeline/transcriptions`。

### 本轮交付物

- `Repository.TaskWrites` — 活动任务计数、`CreateTaskWithActiveLimit` /
  `CreateTaskWithCreditReservation`（逻辑模型有效性 + 活跃限额 + 积分预留同事务，
  ErrActiveTaskLimit/ErrInsufficientCredits/ErrLogicalModelUnavailable 三态投影）、
  `CancelTaskIfStatus`（条件取消，供下一批 retry/cancel）
- `TaskCreationService` — POST /tasks 准入：类型白名单、prompt 必填（Go 英文原文）、
  输入归一（canvasSnapshot data: URL 压缩）、文本回放全路径
  （status=text_replay、不排队不计费、密钥字段 AES-GCM 加密、
  taskForOutput 白名单投影 inputJSON + 清空供应线路内部字段）、
  存储配额（任务条数 + 任务数据 GB）
- SSE `GET /tasks/:id/text-events` — after/Last-Event-ID 游标（负数/非法 → 400 中文文案）、
  connected/progress/delta/terminal/heartbeat(15s)/poll(750ms) 事件序列与 Go 一致
- `POST /timeline/transcriptions` — 功能门控 + 资源归属 + 音视频 MIME 白名单 + 排队入库

### 关键取舍（PENDING #58）

1. **队列路径 admission（前台模型路由/系统渠道/自定义渠道 + 计费预留 + 工作流插件门控）
   未移植**：非回放任务当前返回 Go 维护模式信封——HTTP 400 +「服务正在维护，
   暂不接受新的生成任务」（Go handler 对 CreateTask 所有错误统一 fail(c,400,err)，
   维护提示也走 400）。不会产生未路由未计费的半准入任务。
2. SSE 的 Content-Type 头手工设置；Results.Stream 的委托重载在此托管模型下不可用，
   改为直写 Response.Body + 心跳/轮询调度。

### 验证结果（471/471 通过）

新增 3 项测试（回放任务创建→增量→SSE 头/事件 + 脱敏断言、队列路径维护信封、401）。

---

## 模型目录（阶段 2.7 补全，已端到端打通）

本轮打通 **2 条路由**：`GET /model-catalog`、`POST /model-catalog/available`。

### 本轮交付物

- `ModelCatalogService` — 按前台模型开关返回互斥形状（source=frontend → models；
  source=system → 脱敏渠道目录）。系统目录逐渠道展开启用模型：
  意图过滤复用与任务 admission 相同的能力合同（CapabilitySpec 匹配、
  audio 仅能力类型）、单模型损坏隔离告警不拖垮目录、
  价格档发布校验（ValidatePriceTierPrice 逐语义移植，ark video 协议常量
  `volcengine-ark-video`）、展示价格/Available 从同一批有效档位派生、
  capabilityConfig 经归一化合同投影
- `ModelCatalogResponseDto` — 两集合恒为数组（空目录发 []，不发 null）
- 2 条路由接线（ModelEndpoints）

### 验证结果（468/468 通过）

新增 2 项测试（system 形状恒数组、available 意图过滤；401）。

---

## 用户提示词偏好（阶段 10.5，已端到端打通）

本轮打通 **3 条路由**：`GET /settings/prompt-templates`、
`PATCH /settings/prompt-templates/:operation`、`DELETE /settings/prompt-templates/:operation`。

### 本轮交付物

- `Repository.UserPromptCustomizations` — 用户定制的列表/单查/Save（update-or-insert）/删除
- `PromptTemplateService` 补偏好域 — `UserPromptPreferences`（定义 × 启用模板 × 定制，
  rewrite 且模板已更新 → outdated）、`UpdateUserPromptCustomization`
  （inherit/append/rewrite 三模式、inherit 清空正文、12000 字 rune 上限、
  占位符白名单校验、需启用模板）、`ResetUserPromptCustomization`
- `UserPromptPreferenceDto` — `template` 无 omitempty（缺启用版本输出 null）、
  `customization` 是 omitempty 指针（无定制时整字段省略，与 Go 一致）

### 验证结果（466/466 通过）

新增 2 项测试（列表/定制/inherit 清空/重置/未知操作 400；401）。

---

## 平台设置批次（阶段 9.5，已端到端打通）

本轮打通 **11 条路由**：运行时策略 4 条（`GET/PUT/DELETE /admin/settings/runtime-policy`、
`GET .../self-use`）、绘图工具 2 条（`GET/PATCH /admin/settings/drawing-engine`）、
模型响应拦截 2 条（`GET/PATCH /admin/settings/response-interception`）、
方舟素材库 2 条（`GET/PATCH /admin/settings/ark-private-assets`）、
公告配图上传 1 条（`POST /admin/announcement-images`）。

### 本轮交付物

- `SettingsCrypto` — AES-GCM 设置密钥加密，与 Go **跨实现互解**：
  同一 `.settings-key`（32 字节）+ "enc:v1:" + Base64(RawStd)(nonce(12)+ct+tag(16))，
  Go 写入的密文 .NET 可解（待确认 #1 的正解前置）
- `PlatformSettingsService` — 运行时策略读写/校验（错误文案逐字对齐 Go，
  含自用部署上限模板 self-use）/重置；响应拦截（规则归一 + rune 上限校验 +
  归一化回读）；方舟素材库（密钥加密落库、hasAccessKeySecret 脱敏、
  空密钥保槽位、启用四要素校验、明文迁移自动加密）
- `AnnouncementService.UploadAnnouncementImageAsync` — 10MB 上限 +
  魔数嗅探（无文件名回退，对齐 `http.DetectContentType`）+ 草稿登记 +
  失败清理资源记录；上传器由路由层按 DI 注入
- 11 条路由接线（MapAdminPlatformSettingsRoutes）

### 关键实现点

1. **运行时策略读取语义**：设置记录存在且合法则生效，否则回落内置默认；
   `configured` 标记是否有持久化记录。全局 `IRuntimePolicyProvider` 的
   设置化读取随任务引擎批次接入（本批先落路由与读写面）。
2. **方舟密钥回填规则**：请求密钥为空且 AccessKeyID 未变化时保留旧密钥；
   Region/ProjectName/AccessKeyID 任一变化会清空 DefaultGroupID（组按项目重建）。
3. **配图嗅探不走文件名回退**：`ResourceUploadService.DetectUploadedMimeType`
   带扩展名回退（上传语义），公告校验用纯魔数嗅探（Go 行为）。

### 验证结果（464/464 通过）

新增 5 项测试（运行时策略读写/校验/自用模板/重置、绘图工具与响应拦截、
方舟校验与脱敏、配图上传与内容校验、401）。

---

## 资源 CRUD 与管理端分析（阶段 5.9 / 9.4，已端到端打通）

本轮打通 **15 条路由**：资源 7 条（`GET /resources`、`GET /resources/:id`、
`POST /resources` multipart 整传、`POST /resources/import` URL 导入、
`GET /resources/storage-usage`、`GET /resources/:id/oss-url`、
`POST /resources/:id/ark-private-asset`）+ 分析 4 条（overview/models/users/export.csv）+
模型价格 4 条（GET/POST/PATCH/DELETE）。

### 本轮交付物

- `ResourceCrudEndpoints` — 资源 CRUD 路由（限流/体限/幂等键与 Go 对齐）
- `ResourceUploadService.ImportResourceUrlAsync` — URL 导入完整移植：
  `OutboundGuard.ValidateOutboundUrlAsync`（SSRF）→ 90s 限长流式下载 →
  复用幂等/类型探测/配额/落盘；补 PNG/GIF/JPEG 尺寸头探测
- 存储用量契约修正：`{usedBytes,totalBytes}`（原实现多了 limitBytes/overQuota）
- `Repository.AnalyticsQueries` — 分析三查询（tasks/logs/activities，日志列裁剪报文）、
  排队计数、模型价格 CRUD
- `AdminAnalyticsService.Analytics` — `buildAnalyticsOverview` 全量移植
  （KPI/DAU/WAU/MAU 滚动窗、趋势、模型聚合、用户聚合、失败分类）、
  `AdminAnalyticsCSV`（usage CSV + BOM）、价格归一化校验（币种大写、非负、≤12 字符）
- 8 条路由接线（MapAdminInsightRoutes）

### 关键实现点

1. **api_call_logs 无 updated_at 列**（GORM 实体上的字段是装饰结果非表列），
   分析投影按 schema-dump 精确列清单。
2. **管理分析 URL 查询到时间窗的归一**：RFC3339 / yyyy-MM-dd 双格式；
   日粒度 To 自动 +1 天；窗口上限 366 天。
3. **非管理员 403 由既有 admin 测试覆盖**；注册第二用户需邮件验证码链路，不在本批展开。

### 验证结果（459/459 通过）

新增 3 项测试（资源列表/详情/整传/用量/OSS 直链 + URL 导入校验 + 401；
分析空库窗口/CSV/模型视图；价格 CRUD/校验/401）。

---

## 分镜候选与工作台读视图（阶段 6 节点 6.4/6.6，已端到端打通）

本轮打通 **12 条路由**：
`POST /projects/:id/shots`、`PUT /projects/:id/units/:unitId/shots`（2MB 体限）、
`POST /projects/:id/shots/:shotId/revisions`、`DELETE .../shots/:shotId`、
`POST/DELETE .../shots/:shotId/assets(/)…`、`POST/GET /projects/:id/asset-candidates`、
`POST .../asset-candidates/:candidateId/confirm`、`DELETE /projects/:id/canvases/:canvasId`、
`GET /projects/:id/core`、`GET /projects/:id/overview`。

### 本轮交付物

- `Repository.Shots.cs` — 镜头版本链（SaveShotWithRevision：MAX(version)+1、
  下游产物 stale、工作流失效、项目 revision）、章节整体替换（expectedShotIds 乐观锁 →
  「本章分镜已发生变化」400）、删除级联 + 顺序压紧、引用 upsert/解绑失效、
  候选创建（ON CONFLICT DO NOTHING）与分页过滤、候选确认双事务（普通/角色）
- `Repository.WorkbenchRead.cs` — 14 项总览指标子查询（布尔列按方言参数化）与单元行
- `ProjectShotService` — 镜头创建（幂等 ID 走更新分支、draft 默认、状态白名单）、
  章节替换（200 上限、单镜 6 版本上限）、候选身份去重（名称键 = 字母数字小写化，
  含 aliases 与角色素材载荷别名）、角色候选画像校验、确认（角色并入 PrepareNextVersion，
  普通类建 text/entity 资产）
- `ProjectUnitService.UnlinkCanvasProjectAsync` — 画布解绑（载荷剥 projectId、
  关系列/快照/revision 原子更新）
- `ProjectWorkbenchService` — core/overview 读视图（ProjectDetail 聚合待任务域，#51）

### 关键实现点

1. **镜头写路径全部经工作流失效**：storyboard 起的步骤重置（本步 running、后续 pending）、
   实例 revision+1——工作流 v2 未移植但失效逻辑已按 SQL 语义落地。
2. **候选身份键**：Go `strings.Map` 只保留 unicode 字母数字并小写；别名参与身份，
   角色素材的 payload.data.definition.aliases 也算已知身份。
3. **候选画像校验先于身份去重**（Go 顺序）：重复候选也要先过完整画像校验。
4. **分页 LIMIT/OFFSET 顺序**：SQLite/PostgreSQL 均为 `LIMIT n OFFSET m`。
5. **Application 层出现同名 `ProjectStatus` 内部类**：补 `ProjectStatusArchived` 常量
   （内层解析优先，避开 Domain 层歧义）。

### 验证结果（453/453 通过）

新增 6 项测试（分镜 401、镜头生命周期/章节替换/乐观锁、引用 upsert/解绑 404、
候选创建/去重/分页/确认（含角色并档）、core/overview 指标与 404、读视图 401）。

---

## 项目角色与配音（阶段 6 节点 6.3，已端到端打通）

本轮打通 **6 条角色路由 + voice-profiles 修正**：
`POST /projects/:id/characters`（256KB 体限）、`GET/PATCH /projects/:id/characters/:assetId`、
`PUT .../representations`（128KB）、`PUT/DELETE .../voice`（64KB），
以及 `GET /voice-profiles`（补 13 个内置声音播种 + VoiceProfileSummary 投影）。

### 本轮交付物

- `Repository.ProjectCharacters.cs` — 角色 JOIN 查询（assets × project_asset_links，
  category=character）、版本/表现/声音绑定读取、`EnsureVoiceProfiles`
  （ON CONFLICT (user_id, provider, voice_key) DO NOTHING）、
  `CreateProjectCharacter`（资产+首版本+链接+项目 revision 同事务）、
  `SaveCharacterVersion`（版本链整体替换 + 资产域字段更新 + revision；命中 0 行抛 not-found）
- `ProjectCharacterService` — 角色不可变版本链（复制当前设定/表现/声音，整体替换）：
  创建（名称修剪、definition 归一 `{}`、RFC3339Nano 载荷）、更新、形象整体替换
  （1–8 个、视角白名单、视角去重、资源 image+ready 校验、共享 operationId）、
  声音绑定（样本资源校验 + MIME 白名单、user_upload 档案自动建档、builtin 档案校验）、解绑
- `ICharacterCardProvider` 实装：`ProjectAssetService.AttachCharacterCardProvider`
  （与 CanvasAuthHost.Attach 同款解环），素材摘要现在内嵌角色卡
  （visualStatus：turnaround_sheet 或三视图齐备 → ready / partial / missing）
- voice-profiles：旧实现直接回原始实体（漏内置播种、漏 status=active 过滤）已修正
- 6 条路由接线 + 补挂此前未注册的 `MapProjectAssetLinkRoutes`

### 关键实现点

1. **角色路由的 not-found 是 500 不是 404**：Go 这组路由不用 `IsProjectNotFound`，
   gorm not-found 原样进 failInternal（500 信封）。C# 用非 AppError 异常复现（见待确认 #49）。
2. **表现替换按输入顺序逐项校验**：第一项的资源检查先于第二项的视角去重（Go 顺序）。
3. **definition/payload 键序递归 Ordinal 排序**对齐 Go map 序列化；
   数字保留原文（已知取舍，见待确认 #47）。
4. **旧测试 `声音档案列表_空数组` 的假设被推翻**：首次调用播种 13 个内置声音（待确认 #48）。

### 验证结果（447/447 通过）

新增 4 项角色端到端测试（401、创建/读取/更新版本链与角色卡内嵌 + 500 信封、
形象替换校验链 + partial/ready、声音播种 + 绑定/解绑），修正 1 项旧声音列表测试。
全量 447 项测试通过，构建 0 错误（测试工程遗留 10 个平行会话警告）。

---

## 项目基础 CRUD（阶段 6 节点 A，已端到端打通）

本轮打通 **5 条项目路由**：`GET /projects`（摘要/分页双形态）、`POST /projects`、
`PATCH/DELETE /projects/:id`、含创建校验与删除级联解绑。

### 本轮交付物

- `Repository.Projects.cs` — 11 个方法：列表/分页/单读/创建/更新/删除级联
  （活动任务拒绝、画布解绑并回写 payload.projectId、单元/链接/分享清理、任务解绑）、
  画布文档/单元摘要/画布摘要/素材计数聚合
- `ProjectService` — 摘要聚合（canvasCount/assetCount/unitCount/completedUnitCount）、
  CreateProject（名称必填、type/aspectRatio/sourceType 默认值、画风资产配置校验
  `ValidateStyleProfileJSON`（256KB、schemaVersion/presetId/title/prompt/revision 必填、
  assets ≤20、执行策略枚举）、预设一致性 `ValidateStyleProfilePreset`、默认模型 ≤500）、
  UpdateProject（局部提交语义 + 主图资源归属/就绪/类型校验）
- 路由 4+1 条（list 双形态算 GET）

### 关键实现点

1. **删除项目先检查活动任务**（queued/running），且把项目下画布的 payload.projectId
   从 JSON 里移除并回写（键字典序）——与 Go 的解绑语义一致。
2. **画风配置是结构化合同**：Go `prompts.ValidateStyleProfileJSON` 校验
   schemaVersion=1 / revision ≥1 / assets 数组 ≤20 / executionPolicy 枚举；
   presetID 必须与快照内 presetId 一致。
3. **创建项目暂不生成默认工作流**（Go `EnsureBuiltinProjectWorkflowTemplate` +
   `createProjectWorkflow`），差异记入待确认 #27。

### 验证结果（233/233 通过，`dotnet build` 0 警告 0 错误）

新增 13 项测试：未登录 401、创建默认值与空名 400、摘要列表+分页 hasMore、
局部更新保留未提交字段、删除后 404、画风预设不一致 400。

### 阶段 6 剩余（下一轮建议）

- `GET /projects/:id`（ProjectDetail 全量聚合：工作流/镜头/角色 reconcile——依赖阶段 6 中后续节点）
- `GET /projects/:id/core`、`/overview`（工作台读视图）
- 单元管理 8 条（创建/列表/重排/更新/删除/导入/workspace）
- 画布链接 3 条、项目素材与分类 8 条、角色与配音 5 条、镜头与候选 9 条
- `EnsureBuiltinProjectWorkflowTemplate`（内置工作流模板种子，创建项目时依赖）

---

## 平台设置与公告（阶段 9 节点 A/B，已端到端打通）

本轮打通 **14 条路由**：平台设置 8 条（`/admin/settings/{registration,email,linuxdo,credits}` GET+PATCH）
与公告 6 条（`/announcements`、`/announcements/read`、`/announcements/:id/image`、
`/admin/announcements` GET+POST、`/admin/announcements/:id` PATCH、`.../close`、
`/admin/announcement-images/:id` DELETE）。

### 本轮交付物

- `AuthService.AdminSettings.cs` — 注册开关与邮件配置的管理读写：
  `normalizeEmailSetting`（端口默认 587、加密默认 starttls、域名归一化去 @ 与尾点）、
  `validateEmailSetting`（启用时主机/端口/发件邮箱必填、用户名需配密码、发件人名禁换行）、
  默认域名白名单 9 项（gmail/163/126/qq/outlook/hotmail/icloud/yahoo/foxmail）
- `CreditPolicyService` — `AdminAsync` / `UpdateAsync`（校验 + 审计事件
  `credit_policy.update`），倍率范围 0.0001–100
- `AnnouncementService` + `Repository.Announcements.cs` — 公告 feed（含未读数）、
  已读标记（幂等 ON CONFLICT）、管理分页（关键字/状态）、创建/更新（配图草稿消费）、
  关闭、配图草稿丢弃（含资源引用检查与删除任务）、配图下发（本地文件流 + 安全响应头）
- 14 条路由接线

### 关键实现点

1. **邮件默认域名白名单在无配置时生效**：Go `readEmailSetting` 无记录时返回
   `normalizeEmailSetting(零值)`，因此默认白名单会拦截白名单外域名——
   这修正了既有测试的错误假设（`new@example.com` 应先被 400 拒绝而非 403）。
2. **公告配图是草稿机制**：上传先落 `announcement_image_drafts`，创建/更新公告时
   同事务消费草稿；丢弃草稿前必须检查资源是否被其他业务引用（防误删）。
3. **公告更新会清空已读记录**（未读恢复），与 Go 的 `user_announcement_reads` 删除一致。

### 验证结果（244/244 通过，`dotnet build` 0 警告 0 错误）

新增 11 项测试：设置类 6 项（未登录 401、注册开关持久化、邮件默认值与启用校验、
域名归一化、积分策略校验与审计、LinuxDO 局部更新保留密钥）+ 公告 5 项
（未登录 401、生命周期创建→feed→已读→关闭→重复关闭 400、标题/级别校验、
更新重置已读、配图草稿校验与丢弃+配图下发）。

### 阶段 9 剩余（下一轮建议）

- 分析统计 4 条（overview/users/models/export.csv）
- API 日志 5 条（列表/详情/媒体/导出 CSV/查询任务）
- 存储管理、系统性能、系统更新 7 条（更新依赖 host-updater 独立进程）
- 外观设置 4 条、响应拦截 2 条、ARK 私有素材 2 条、OSS 设置 3 条（依赖云 SDK）

---

## API 日志与存储统计（阶段 9 节点 C，已端到端打通）

本轮打通 **4 条路由**：`GET /admin/api-logs`（分页+过滤）、`GET /admin/api-logs/:id`（详情含报文）、
`GET /admin/api-logs-export.csv`、`GET /admin/storage/stats`。

### 本轮交付物

- `Repository.AdminAnalytics.cs` — 日志过滤查询（时间范围/用户/模型/渠道/能力/记录类型/状态/关键字）、
  导出查询（≤5000）、按 ID 查日志、日志关联任务、账单批量、历史渠道引用（含已删除）、
  用户批量、存储汇总/按 kind/按 provider 分组统计
- `AdminAnalyticsService` — `normalizeAnalyticsFilter`（默认近 30 天、区间上限 366 天、
  单日 to 自动 +1 天）、`decorateAPICallLogs`（渠道名三态：自定义/实名/已删除渠道、
  用户账号、账单状态与金额、任务状态与媒体预览 URL）、列表剥离原始报文、
  CSV 导出（BOM + 12 列 + 转义）、存储统计投影
- 4 条路由接线

### 关键实现点

1. **列表与详情的报文边界**：列表强制清空 `requestBody`/`responseBody`（omitempty 使空串字段整体省略），
   原始报文只允许按单条读详情。
2. **渠道名三态**：`channelId` 为空 →「自定义渠道」；历史渠道表命中 → 真实名称；
   未命中 →「已删除渠道」——历史日志允许读已删除渠道名称，但不返回密钥。
3. **媒体预览双路径**：任务结果里的资源 ID 走代理 URL（`/api/admin/api-logs/:id/media`），
   外链直接透传。

### 验证结果（250/250 通过，`dotnet build` 0 警告 0 错误）

新增 6 项测试：未登录 401、列表装饰与报文剥离、详情与不存在 404、CSV 导出表头与数据行、
recordType 校验 400、存储统计分组（空 provider 归一 local、ready 与物理字节口径）。

### 阶段 9 剩余（下一轮建议）

- 分析总览 3 条（overview/users/models）——依赖 `user_daily_activities` 写入（待确认 #20）
- 存储资源列表 1 条、系统性能 2 条、系统更新 4 条（依赖 host-updater 独立进程）
- 外观设置 4 条、响应拦截 2 条、ARK 私有素材 2 条、OSS 设置 3 条（依赖云 SDK/资源上传）

---

## 项目单元与画布链接（阶段 6 节点 B，已端到端打通）

本轮打通 **10 条路由**：`POST/GET /projects/:id/units`、`GET/PATCH/DELETE /projects/:id/units/:unitId`、
`POST /projects/:id/units/import`、`PATCH /projects/:id/units/reorder`、
`POST /projects/:id/canvas-links`、`DELETE /projects/:id/canvas-links/:canvasId/units/:unitId`。

### 本轮交付物

- `Repository.ProjectUnits.cs` — 14 个方法：单元全量/单读/创建/批量导入（同事务）/
  条件更新（sourceChanged 决定是否写正文与字数）/删除/重排（逐条 position）/版本号递增/
  画布归属/链接 CRUD
- `ProjectUnitService` — `newProjectUnit`（kind 默认 chapter、白名单校验、标题必填、
  position 负值归零、状态 draft）、`ProjectUnitWordCount`（去 HTML 标签 + 反转义 + 字符数）、
  重排全量校验（数量一致、ID 有效、无重复）、导入 1–2500 上限、画布链接归属校验
- 10 条路由接线（2MB/32MB/256KB/64KB 分级体上限）

### 关键实现点

1. **字数统计口径**：Go `ProjectUnitWordCount` 先去 HTML 标签（正则）再 `html.UnescapeString`
   （`&amp;` → `&`）再取 rune 数——C# 用 `WebUtility.HtmlDecode` + `Trim().Length` 等价。
2. **导入响应剥离正文**：2500 章的长篇正文不回传，只返回标识与摘要。
3. **更新时条件写正文**：`sourceChanged` 为假时 UPDATE 不写 `source_text`/`word_count`，
   避免无谓的正文重写。
4. **重排是"全量提交"语义**：提交的 ID 列表必须与库中数量一致、全部有效且不重复，
   否则 400——防止并发下部分重排造成顺序错乱。

### 验证结果（256/256 通过，`dotnet build` 0 警告 0 错误）

新增 6 项测试：单元 CRUD（字数统计 4 字符、状态流转、删除）、类型/标题/状态校验 400、
批量导入（数量校验 + 正文剥离 + position 续排）、重排（不完整/无效 ID 400 + 正常交换）、
画布链接（归属 + canvasCounts + 空参 400 + 解绑）、项目不存在 404。

### 阶段 6 剩余（下一轮建议）

- 项目素材与分类 8 条、角色与配音 5 条、镜头与候选 9 条
- `GET /projects/:id`（ProjectDetail 全量聚合）、`/core`、`/overview`（工作台读视图）
- 工作流模板与实例 3 条（依赖内置模板种子，待确认 #27）
- 画布删除 2 条

---

## 项目素材文件夹树（阶段 6 节点 C，已端到端打通）

本轮打通 **4 条路由**：`GET/POST /projects/:id/asset-folders`、
`PATCH/DELETE /projects/:id/asset-folders/:folderId`。

### 本轮交付物

- `Repository.ProjectAssetFolders.cs` — 文件夹全量/单读/创建/更新/删除（均递增项目版本号）、
  素材链接读取与更新、按 ID 批量取素材
- `ProjectAssetFolderService` — 文件夹树全套校验：
  名称（非空、≤60 字符）、样式枚举（glass/stacked/midnight/paper/cinema/compact，默认 glass）、
  主题枚举（aurora/obsidian/ember/pearl，默认 aurora）、
  **父级校验**（自引用/环检测/父存在性/**子树高度 + 祖先深度 ≤ 8 层**）、
  同级重名（忽略大小写 + 唯一约束冲突兜底）、删除非空拒绝
- 4 条路由接线（32KB 体上限）

### 关键实现点

1. **层级限制是"祖先深度 + 子树高度"**：不是简单看父链长度——移动一个带子树的文件夹时，
   必须保证整棵子树落位后总深度 ≤ 8，否则深层子树会被截断。
2. **环检测与自引用分开报错**：`currentID == folderID` → 「不能移动到自身或其子目录」；
   父链出现重复 → 「文件夹目录关系存在循环」。
3. **样式/主题有默认值**：空串回落 `glass`/`aurora`，非法值 400——前端可直接省略。

### 验证结果（261/261 通过，`dotnet build` 0 警告 0 错误）

新增 5 项测试：文件夹 CRUD（默认样式主题、同级重名 400、局部更新保留未提交字段、删除）、
名称/样式/主题校验 400、层级（自引用、移入子目录、父不存在）、删除非空拒绝、项目不存在 404。
另统一加固了全部 16 个测试类的 Dispose 清理（连接池延迟导致的句柄占用重试）。

### 阶段 6 剩余（下一轮建议）

- 项目素材关联 4 条（`/projects/:id/assets` 列表/新增/更新/删除，含首版本事务）
- 角色与配音 5 条、镜头与候选 9 条
- `GET /projects/:id`、`/core`、`/overview`（工作台读视图）
- 工作流模板与实例 3 条（依赖内置模板种子，待确认 #27）
- 画布删除 2 条

---

## 风格档案与声音档案（阶段 10 节点 A，已端到端打通）

本轮打通 **7 条路由**：`GET/POST /style-profiles`、`PATCH/DELETE /style-profiles/:id`、
`PATCH /style-profiles/:id/favorite`、`POST /style-profiles/:id/use`、`GET /voice-profiles`。

### 本轮交付物

- `Repository.StyleProfiles.cs` — 风格列表（收藏优先 + 更新时间倒序）/计数/单读/创建/更新/
  收藏/最近使用/删除（命中 0 行视为不存在）、声音档案列表
- `StyleProfileService` — 用户风格 CRUD 全套校验：
  `ValidateStyleProfileJSON`（256KB、schemaVersion=1、presetId/title/prompt/revision 必填、
  assets ≤20、执行策略枚举、**来源枚举 builtin/user/external**）、
  `normalizeUserStyleProfileJSON`（名称 ≤80、简介 ≤500、标签 ≤20、封面 ≤4096B 且限
  http(s)/站内路径/图片 Data URL、负面 Prompt ≤64KB、source 归一为 user、revision 强制、
  标签去空去重、序列化后 ≤256KB）、上限 200 个
- `StyleProfileDocument` — 字段顺序与 Go 结构体一致（写库 JSON 逐字节对齐）
- 7 条路由接线（320KB 体上限）

### 关键实现点

1. **创建时两次归一化**：Go 先用空 presetID 归一生成 ID，再用真实 ID 二次归一——
   这样 `sourceProfileId` 落库就是自身 ID，`revision` 从 1 开始。
2. **更新时 revision +1 由服务层控制**，客户端提交的 revision 被忽略（防止回退版本）。
3. **封面 URL 白名单**：只允许 `http://`、`https://`、站内路径（`/` 开头）与
   `data:image/`，其他协议（如 ftp）一律 400。

### 验证结果（266/266 通过，`dotnet build` 0 警告 0 错误）

新增 5 项测试：未登录 401、CRUD（归一化 source=user/revision=1、标签 JSON 存储、
更新 revision=2、收藏、使用、删除）、校验（缺 presetId、非法来源、非法封面 400）、
不存在 404、声音档案空列表。

### 阶段 10 剩余（下一轮建议）

- 技能库 13 条（列表/详情/文件/搜索/安装/同步——依赖 GitHub 与文件系统）
- 提示词模板 7 条（`/settings/prompt-templates` 用户定制 + `/admin/prompt-templates` 管理）
- 插件运行时与声明式协议（阶段 10.1/10.2）
- LibTV/TapNow 集成、自定义渠道中转、系统代理短代理

### 协作提示

本轮发现 `ChannelAdminService` / `CanvasService` 中出现了另一会话写入的
`EnsureSystemChannelModelsAsync` 同名实现（语义正确、注释更完整），已按仓库约定
保留既有版本并移除本轮重复插入；如确有并行会话在编辑本目录，请协调先后（见待确认清单 #14）。

---

## 渠道模型写路径（能力子系统第四轮，已端到端打通）

本轮打通渠道模型 **4 条路由**：`POST /admin/channels/:id/models`、
`PATCH /admin/channels/:id/models/:modelId`、`DELETE .../models/:modelId`、
`POST .../models/batch-delete`。逻辑模型与渠道两个模块至此全部闭环。

### 本轮交付物

- `OpenAICanvas.Protocol/ProtocolRegistry.cs` — 协议**元数据**注册表：内置 13 协议 +
  官方插件包（`.yingce-plugin` zip 内 manifest.json 的 providers）元数据加载，
  供渠道模型保存的合同校验（协议存在 / Enabled / UnavailableReason / 能力类别匹配）。
  声明式协议的执行引擎属阶段 4/10，另行移植。
- `Application/ChannelModelAdminService.cs` — `SaveAdminChannelModel`（合同归一化、
  重复键冲突、能力配置归一化与版本计数、价格档归一化 `normalizeChannelModelPriceTiers`
  （旧 API 折叠默认档 / SKU 选择器规范化 / 计费方式与价格上限校验）、
  摘要档选择、`validateChannelModelTierCapabilities` 视频档规格校验）+
  单个/批量删除（归属校验、活动引用拒绝、≤100 上限）
- `Repository.Channels.cs` — `SaveChannelModelWithPriceTiers`（档位按规范键复用行并
  递增价格版本、移除档软删除、**GORM Save 的 UpdateOrCreate 语义**）、
  `DeleteChannelModels`（锁主体 → 归属 → 前台模型线路/进行中任务引用 → 停用 bump 版本 →
  软删除 → 刷新 ModelsJSON）、`SyncChannelModelNames`、`ChannelModelByID` /
  `ChannelModelByKeyIncludingDisabled`

### 关键实现点

1. **GORM `Save` 是 UpdateOrCreate**。Go 的 `tx.Save(item)` 对新模型 UPDATE 命中 0 行后
   自动回退 INSERT；C# 初版只做 UPDATE，创建静默失败（HTTP 响应仍用内存对象返回成功），
   直到后续更新才暴露"record not found"。已改为 UPDATE 失败即 INSERT。
2. **文本/图片/视频模型的 capabilityConfig 必填**。`NormalizeModelCapabilityConfigForModel`
   对三类能力都强校验——测试初版没带配置，行为与 Go 一致地被 400 拒绝。
3. **重复删除命中服务层归属校验**（400「请刷新后重试」），先于仓储 not-found，与 Go 一致。
4. **协议元数据来自插件包**。`grok-image` 等图片协议不在内置注册表，由仓库根
   `plugin-packages/*.yingce-plugin` 声明；C# 按 `CANVAS_OFFICIAL_PLUGIN_DIR` 或向上
   8 级查找目录加载元数据（与 Go `OfficialPluginPackageDir` 一致）。
5. **测试环境注意**：SSRF 放行（`CANVAS_ALLOWED_PRIVATE_UPSTREAM_HOSTS`）是进程环境变量，
   并行测试类共享——Dispose 清理会与仍在运行的类竞态，故只设置不清理。

### 验证结果（197/197 通过，`dotnet build` 0 警告 0 错误）

新增 9 项端到端测试：文本模型旧 API 折叠默认档 + ModelsJSON 刷新、视频双档与规格选择器、
档位规格超出能力配置报错、协议有效性/能力匹配双校验、重复模型键冲突、
插件协议（grok-image）元数据解析、更新价格版本递增且档位复用（模型 2 / 档位 2）、
单个删除与重复删除 400、批量删除空选择与前台模型引用拒绝。
端到端实测：保存（含中文 displayName）→ 列表含 selector → PATCH 版本递增 →
批量删除 `deleted:1`。

### 阶段 3 剩余（下一轮建议）

- ~~渠道模型 fetch/import（上游模型目录拉取与导入）~~ ✅（见下节）
- 渠道模型 test（`TestAdminChannelModel` 依赖阶段 4 provider 执行引擎，随任务模块一并移植）
- 系统中转与代理（阶段 10 提前依赖评估）
- 建议下一轮进入**阶段 5（资源/素材/存储）**或补齐 `EnsureSystemChannelModels` 启动种子，
  然后按 PLAN 顺序推进阶段 4（任务与生成，系统核心）

### 最新推进：系统渠道模型启动种子（3.8）

- ✅ `ChannelAdminService.EnsureSystemChannelModelsAsync`：启动时扫描系统渠道；仅在渠道模型记录为空时，按 `ModelsJSON` 补齐禁用占位。
- ✅ 与 Go 对齐：去除 `models/` 前缀、过滤空白与重复名称、跳过 `RetiredModelsJSON`，已有（含停用）记录保持不变。
- ✅ `CanvasService` 与 `Program.cs` 已接入，迁移后服务开始监听前执行。
- ✅ 新增端到端测试：旧渠道占位补齐、退役模型过滤、重复启动幂等。
- ✅ 验证：`启动种子_为旧系统渠道补齐模型占位并尊重退役清单` 1/1 通过。

下一项按计划为渠道模型 fetch/import/test（需补齐上游 HTTP 客户端），随后进入阶段 4 任务与生成。

---

## 渠道管理 CRUD（能力子系统第三轮，已端到端打通）

本轮打通系统渠道管理 **7 条路由**：`GET/POST /admin/channels`、
`POST /admin/channels/:id/duplicate`、`PATCH/DELETE /admin/channels/:id`、
`GET /admin/channels/:id/models`、`PATCH /admin/channels/:id/models/:modelId/sort`。

### 本轮交付物

- `OpenAICanvas.Outbound/OutboundGuard.cs` — 出站 SSRF 校验（`ValidateOutboundURL`：
  scheme/认证信息/DNS 解析 + 私网回环阻断 + `CANVAS_ALLOWED_PRIVATE_UPSTREAM_HOSTS`
  精确放行）与自定义请求头编解码（规范大小写、系统头黑名单、16KB 总量），首次落地 Outbound 工程
- `Application/ChannelAdminService.cs` — 渠道分页/创建/复制/更新（含 presentation-only
  短路径与 `mergeChannelRequest` 旧值合并）/删除（软删除 + 清密钥保留主体供账本关联）、
  `syncInitialChannelModels`（占位模型同步 + 退役清单）、`ensureChannelModels` 兜底、
  模型排序（事务内刷新渠道 ModelsJSON）
- `Repository.Channels.cs` 扩展 — 分页查询、复制事务、全量更新、删除事务、
  排序事务（锁主体 → 更新 → 刷新 ModelsJSON）、价格档附着
- `Web/Endpoints/ChannelEndpoints.cs` — 7 条路由

### 关键实现点

1. **apiKey 对任何视角都不回显**。Go 的 `publicChannel` 只给非 admin 系统渠道填
   `"system"` 占位，admin 拿到的 `apiKey` 是空串，凭证存在性只通过 `hasApiKey` 表达；
   模型列表、创建/更新响应同理。测试初版按“admin 可见”断言是错的。
2. **GORM 时间戳自动填充语义**。复制/创建时 Go 不设置 CreatedAt/UpdatedAt 由 GORM 补 now，
   C# 需显式赋值（否则落库 0001-01-01）；更新路径 Save 自动 bump UpdatedAt。
3. **`repo.ChannelModels` 附着价格档**。Go 读取渠道模型即附带 tiers（空为 `[]`，无 omitempty）；
   C# 漏掉会导致复制流程 `NullReferenceException`（Go 的 nil range 合法而 C# 不是）。
4. **`ensureChannelModels` 的 includeDisabled 语义**。Go 传 `true`（含停用占位）；
   C# 初版误传 false 会把禁用占位重复插入——这是行为等价性测试抓出来的第 4 个翻译偏差。
5. **SSRF 放行读取环境变量而非配置**。`CANVAS_ALLOWED_PRIVATE_UPSTREAM_HOSTS`
   必须是进程环境变量（对应 Go `os.Getenv`），测试里 `UseSetting` 不生效。

### 验证结果（188/188 通过，`dotnet build` 0 警告 0 错误）

新增 9 项端到端测试：未登录 401、创建（契约断言含初始模型同步与 `models/` 前缀保留）、
并发数双校验、presentation 短路径（别名/排序更新不影响凭证）、完整更新（无 apiKey 保留旧值）、
复制（副本含模型占位）、删除后列表消失且重复删除 400、模型列表兜底同步、排序校验与生效。
端到端实测：创建 → 模型列表 → 复制 → 关键字过滤 → 删除 → 重复删除 400 全链路通过。

### 阶段 3 剩余（下一轮建议）

- ~~渠道模型写路径（保存/价格档归一化/删除）~~ ✅
- ~~渠道/模型排序（`/admin/channels(/:id/models)/order` GET+PUT，乐观并发 409）~~ ✅（见下节）
- ~~`EnsureSystemChannelModels` 启动种子~~ ✅（Program.cs 迁移后调用）
- ~~渠道模型 fetch/import（上游模型目录拉取与导入）~~ ✅（见下节）
- 渠道模型 test（`TestAdminChannelModel` 依赖阶段 4 provider 执行引擎，随任务模块一并移植）
- Go main.go 启动序列的其余种子：`EnsureDefaultPromptTemplates` /
  `EnsureBuiltinProjectWorkflowTemplate` / `EnsureBuiltinSkills` / `EnsureSkillPackages` /
  `MigrateLegacyStorage`（阶段 6/10）
- 系统中转与代理（阶段 10 提前依赖评估）

---

## 渠道排序与启动种子（能力子系统第五轮，已端到端打通）

本轮打通 **4 条排序路由**（`GET/PUT /admin/channels/order` 与
`GET/PUT /admin/channels/:id/models/order`，Go 以两条路径循环注册）并接入
`EnsureSystemChannelModels` 启动种子。

### 本轮交付物

- `Repository.SaveChannelOrderAsync` — 事务内乐观并发：读当前顺序与 `expectedIds` 严格相等、
  长度一致、`ids` 无重复且全部已知，任一不满足回 `ErrChannelOrderChanged`；
  逐项写 `sort_order = index`；模型排序附加刷新渠道 ModelsJSON；
  PostgreSQL 先按稳定序 `FOR UPDATE` 锁再读快照（对齐 Go 注释的行为）
- `ChannelAdminService.AdminChannelOrderAsync / SaveAdminChannelOrderAsync` —
  渠道列表项 `{id,name,enabled}`（模型项名称取 displayName 优先）；
  快照过期回 **409** +「列表已发生变化，请重新打开排序后再保存」；
  `ids`/`expectedIds` 缺失或超 10000 → 400「请重新加载完整排序列表」；
  保存成功失效路由目录
- `EnsureSystemChannelModelsAsync` — 系统渠道无任何渠道模型记录时按 ModelsJSON 补占位
  （已停用但存在的记录不重建，保持管理员手动清理结果）；`Program.cs` 在迁移后、监听前调用
- 路由体上限：排序 PUT 请求体 1MB（对齐 Go `MaxBytesReader`）

### 验证结果（202/202 通过，`dotnet build` 0 警告 0 错误）

新增 4 项端到端测试：未登录 401、渠道排序读/交换保存/旧快照 409/长度与未知 ID 409、
`ids` 缺失 400、模型排序保存后 ModelsJSON 顺序刷新。
端到端实测：渠道与模型排序 GET → 正确保存 → 过期快照 409（含中文文案）→
交换保存后 ModelsJSON 按启用模型刷新。

---

## 上游模型目录拉取与导入（能力子系统第六轮，已端到端打通）

本轮落地**出站受控 HTTP 客户端**并打通 **2 条路由**：
`POST /admin/channels/:id/models/fetch`（只读目录预览，限流 10 次/分钟/用户+渠道）与
`POST /admin/channels/:id/models/import`（导入所选模型，请求体上限 64KB）。

### 本轮交付物

- `OutboundHttpClient`（Outbound）— 受控出站客户端：`SocketsHttpHandler.ConnectCallback`
  内完成「解析 → SSRF 校验 → 拨号到已验证 IP」，TLS SNI/Host 保留原主机名；
  按序尝试全部解析地址（对齐 Go net.Dialer）；代理主机直连旁路（`configuredProxyHost`）；
  代理本身读 HTTP(S)_PROXY 环境变量（.NET DefaultProxy 同源）
- `OutboundGuard.ResolveOutboundHostAsync` — 解析+校验单一入口，避免连接期二次解析
- `ChannelModelCatalogService` — `/models` 目录拉取：openai/gemini 双格式
  （gemini 默认 `/v1beta` + `x-goog-api-key`）、`apiURL` 版本前缀收敛
  （`channelAPIPrefixes` 七个前缀、请求路径显式版本优先）、64MB 响应上限、
  上游错误文案映射（401/403→鉴权失败、404→未提供 /models、429→频繁、
  default→providerHTTPError.Error() 等价文案）
- `ChannelModelAdminService` — `PreviewAdminChannelModels`（只读）、
  `FetchAdminChannelModels`（服务面保留，路由未用，与 Go 一致）、
  `ImportAdminChannelModels`（仅导入仍在上游目录中的模型、≤500、
  按 ProviderModelKey 去重、退役 SKU 跳过、创建停用未定价占位）
- `Repository.CreateMissingChannelModelsAsync` — 批量 INSERT ON CONFLICT DO NOTHING，
  返回受影响行数

### 关键实现点

1. **`localhost` 双栈陷阱**。`localhost` 同时解析 `::1` 与 `127.0.0.1`，测试上游只绑
   IPv4 时连接被拒——Go dialer 逐地址回退，C# 初版只试第一个地址。已改为按序尝试全部
   地址；测试监听改 `IPv6Any + DualMode`。
2. **ConnectCallback 与代理**。设置了代理时回调收到的是**代理**端点，需要
   `configuredProxyHost` 旁路直连；.NET DefaultProxy 在 Windows 读系统代理设置。
3. **目录服务无 apiKey 必 400**（「请填写 API Key」）——Go 同样强校验，测试渠道必须带密钥。

### 验证结果（206/206 通过，`dotnet build` 0 警告 0 错误）

新增 4 项端到端测试（本地 TcpListener 假上游）：目录去重/剥前缀/字典序 + Bearer 鉴权头、
导入创建停用占位且重复导入 added=0、空选择与未知模型 400、上游 401 → 502「鉴权失败」。

### 阶段 3 收尾状态

阶段 3 全部路由仅剩 `POST /admin/channels/:id/models/test`（依赖阶段 4 provider 执行引擎）。
下一轮建议进入**阶段 5（资源/存储）**或直接攻坚阶段 4（任务与生成）。

---

## 画布工程 CRUD（阶段 5 节点 A，已端到端打通）

本轮打通 **4 条画布路由**：`GET /canvas-projects`（摘要/分页双形态）、
`GET/PUT/DELETE /canvas-projects/:id`。对应 Go `handler/user_data.go` 与 `internal/canvas` 域。

### 本轮交付物

- `UserDataService`（Application）— 画布 upsert 全链：`ValidateSyncedPayload`（4MB 上限 +
  递归禁内嵌 `data:image|video|audio`）、`canvasProjectFromJSON`、
  **画布媒体守卫**（`ValidateCanvasMediaAssets`：media 节点/timeline.clips 的
  storageKey/content/url/dataUrl 只能通过本用户素材指向 ready 资源，
  `resource:` 前缀与 `/api/resources/` 文件 URL 两种定位）、
  结构化配额（`StructuredDataMB`/`CanvasCount` + `UserStorageUsage` 实测）、
  进程内存储锁（对齐 Go Service 级互斥）、画布库分页
  （`projectId=independent` 过滤、标题搜索、name/nodes 双排序——
  nodes 排序按方言用 `json_array_length` / `jsonb_array_length`）
- `Repository.CanvasProjects.cs` — 8 个方法：列表/摘要/单读/upsert（未命中回退插入）/删除
  （连带清理 canvas_shares、canvas_unit_links，解绑 tasks.project_id）/分页/素材与资源批量取
- `UserDataEndpoints` — 4 条路由；PUT 请求体 5MB、`canvas-write:{uid}` 限流
  （`CanvasWritePerMinute`）、画布 ID 与路径一致性校验、单读不存在回 **404**
  （Go 用 `fail(404, err)` 而非 failService）

### 关键实现点

1. **`JsonSerializer.SerializeToElement(string)` 是再编码不是解析**——payload 会变成
   JSON 字符串元素；必须 `JsonDocument.Parse(...).RootElement.Clone()`（本轮实测踩坑）。
2. **单读 404 与全局 500 的分叉**：Go 的 `fail(c, 404, err)` 会回显错误原文，
   与 `failService` 的 5xx 固定文案路径不同；C# 对 `InvalidOperationException` 特判 404。
3. **分页 `sort=nodes` 依赖 JSON 函数**：SQLite `json_array_length` / PG `jsonb_array_length`，
   已按方言分支。

### 验证结果（213/213 通过，`dotnet build` 0 警告 0 错误）

新增 7 项端到端测试：未登录 401、空列表、upsert→单读→摘要→分页→覆盖保存全流程、
ID 不一致 400、内嵌媒体拒绝、未入库媒体守卫拒绝、删除后 404、标题搜索与独立画布过滤。

### 阶段 5 剩余（下一轮建议）

- ~~画布工程 CRUD~~ ✅（节点 A）
- ~~素材库：batch/分页/facets/单读/upsert/快照/分类/移动~~ ✅（节点 B）
- ~~`DELETE /assets/:id`（资源级联判定链）~~ ✅（见下节）
- ~~资源上传三件套（`/resources/uploads` 会话/分片/complete）~~ ✅（阶段 5.1）
- ~~文件下发（`/resources/:id/file`、`/public/resources/:id/file`，含 ETag/Range/匿名签名）~~ ✅（阶段 5.8）
- 存储位置与 OSS 设置（`/settings/oss`，test 需云 SDK）、Eagle 集成
- `ReplaceUserCanvasProjects` / `ReplaceUserAssets`（同步接口服务面）
- 提示词偏好（`/settings/prompt-templates`，阶段 10.5）

---

## 素材库全量（阶段 5 节点 B，已端到端打通）

本轮打通 **8 条素材路由**：`POST /assets/batch`（体上限 16KB、≤100 个）、
`GET /assets`（未分页→摘要，带过滤参数→分页+facets）、`GET/PUT /assets/:id`
（PUT 体上限 5MB、`assets-write:{uid}` 限流 `AssetWritePerMinute`、ID 一致性校验）、
`GET/POST/PATCH/DELETE /asset-folders`、`PATCH /assets/folder`（≤200 个）、
`GET /user-data/snapshot`。

### 本轮交付物

- `AssetLibraryDomain` — `AssetFromJSON`（4MB+内嵌媒体校验、ID ≤80、主版本 ID ≤36、
  `validateUserAssetDocument` 六类素材 per-kind data 校验、类别归一化、status 缺省 confirmed）
- `UserDataService` 扩展 — 素材 upsert（storage lock + 分类存在性 + **`ValidateAssetCanvasReferences`**
  画布反向引用守卫 + 结构化配额）、`ClientAssetPayload`（补齐 coverUrl/tags/时间戳/
  image·video 正数尺寸，cover 从 dataUrl/url/storageKey 推导）、分页与三组分面、
  分类 CRUD（重名拒绝、删除时素材先移出并回写 payload.folderId/updatedAt）、批量移动
- `Repository.AssetLibrary.cs` — 12 个方法（分页/facets/摘要/单读/批量/upsert/
  分类全套/move 事务含 payload 回写）
- `UserDataEndpoints` — 8 条路由

### 关键实现点

1. **素材移动要同步回写 payload**：Go `assetPayloadWithFolder` 更新 payload 内
   `folderId`（空则删除键）与 `updatedAt`（RFC3339Nano），并**按键字典序输出**（Go map 序列化）；
   C# 对齐（updatedAt 固定 7 位小数与 Go 的去尾零存在字节差异，见待确认清单 #23）。
2. **画布反向引用守卫**：素材 payload 变更时若该素材仍被画布媒体节点引用、
   且新 payload 不再包含原 resourceID → 400「素材仍被画布引用，不能替换为其他云端资源」。
3. **facets 的 status 过滤**：active（≠archived）/archived/原样三种分支；
   分页含 title 与 payload_json 双列 LIKE。

### 验证结果（219/219 通过，`dotnet build` 0 警告 0 错误）

新增 6 项端到端测试：upsert 补全契约（status 归一 confirmed）与批量读取、
缺 coverUrl/未知类型 400、分页 facets 搜索与 folderId 过滤、分类 CRUD（重名/删除移出/missing 400）、
批量移动（payload.folderId 回写）、快照双集合。

### 阶段 5 剩余（下一轮建议）

- ~~`DELETE /assets/:id`（资源级联判定链）~~ ✅（见下节）
- ~~资源上传三件套（`/resources/uploads` 会话/分片/complete）~~ ✅（阶段 5.1）
- ~~文件下发（`/resources/:id/file`、`/public/resources/:id/file`，含 ETag/Range/匿名签名）~~ ✅（阶段 5.8）
- 存储位置与 OSS 设置（`/settings/oss`，test 需云 SDK）、Eagle 集成
- `ReplaceUserCanvasProjects` / `ReplaceUserAssets`（同步接口服务面）
- 提示词偏好（`/settings/prompt-templates`，阶段 10.5）

---

## 素材删除与资源级联（阶段 5 节点 C，已端到端打通）

本轮打通 `DELETE /assets/:id`——翻译 Go `resource_delete.go` 的完整判定链与级联事务。

### 本轮交付物

- `Repository.ResourceReferences.cs` — **全业务面资源引用快照**（素材/画布/任务/创作会话/
  创作执行项/任务日志/任务结果/项目（含主图直引）/风格/素材版本/项目候选/工作流步骤/
  镜头产物/声音/公告/公告草稿共 16 类文档与直引）、素材业务引用（项目链接/镜头引用/候选）、
  素材版本+表现记录、同物理对象共享计数、`DeleteAssetAndResources` 级联事务
  （shot_asset_references → character_voice_bindings → asset_representations →
  project_asset_links → project_asset_candidates → asset_versions → asset →
  resource_deletion_jobs → ark_private_asset_bindings → resources）
- `ResourceDeleteService` — 删除判定链：payload/版本/表现的 owned 资源收集 →
  **被其他素材共享的物理对象剔除** → 占用文案（去重、标题截 32、最多 3 条 +「等 N 处」）→
  删除任务（Outbox）与业务删除同事务 → 本地物理文件 drain 清理（含符号链接逃逸校验）
- `DELETE /assets/:id` 路由接线

### 关键实现点

1. **两道守卫缺一不可**：asset payload 的 `data.storageKey`/`coverUrl` 收集 owned 资源；
   快照里「其他素材文档命中同资源」时该资源标记 shared 不删除——
   防止误删仍被别的素材使用的物理文件。
2. **Outbox 模式**：业务删除与 deletion_jobs 同事务；失败物理文件完全不动，
   成功后同步 drain 本地任务（Go 为异步 goroutine，C# 内联等价）。
   **云 provider（OSS/COS/Kodo/S3）的任务保留 pending**，等待云 SDK worker（待确认 #25）。
3. 测试踩坑：种子数据的 UserID 必须用真实用户 hex ID 而非用户名——
   快照/资源查询全部按 user_id 过滤。

### 验证结果（222/222 通过，`dotnet build` 0 警告 0 错误）

新增 3 项端到端测试：删除无引用素材（资源同删 + 本地文件清理 + 任务 done）、
被画布引用返回 400 占用提示且素材保留、删除不存在素材 500（Go 裸 ErrRecordNotFound 语义）。

### 阶段 5 剩余（下一轮建议）

- 资源上传三件套（`/resources/uploads` 会话/分片/complete）与文件下发（`/resources/:id/file`）
- 存储位置与 OSS 设置（`/settings/oss`）、Eagle 集成
- `ReplaceUserCanvasProjects` / `ReplaceUserAssets`（同步接口服务面）
- 提示词偏好（`/settings/prompt-templates`，阶段 10.5）

---

## 画布分享（阶段 7 节点，已端到端打通）

本轮打通 **6 条分享路由**：`GET/POST/DELETE /canvas-projects/:id/share`、
`GET /public/canvas-shares/:token`、`GET /public/canvas-shares/:token/resources/:resourceId/file`。

### 本轮交付物

- `Repository.CanvasShares.cs` — 按项目/令牌哈希查分享、upsert、按项目删除、`ResourceForUser`
- `CanvasShareService` — 令牌签发（32 字节 base64url，仅存 SHA-256 哈希，明文经设置加密
  通道回显）、有效期 0–365 天、轮换（rotate）、撤销；
  **公开投影脱敏**（metadata 白名单 60+ 键、禁用键 apiKey/storageKey/task* 递归剥离、
  media 节点 content 重写为带令牌代理 URL 并登记放行资源）、
  公开资源投递（令牌 → 放行清单 → 本地文件流，安全响应头全套）
- `MapCanvasShareRoutes` — 6 条路由；公开端限流 120/min 与 300/min（按 IP）

### 关键实现点

1. **脱敏是双层的**：metadata 只留白名单键，其余对象递归剥离禁用键——
   即使白名单键的值是对象，内部的 apiKey/storageKey 仍会被剥掉。
2. **media content 重写**：storageKey/content 指向的资源登记进放行清单后，
   前端统一用 `/api/public/canvas-shares/{token}/resources/{id}/file` 访问；
   不在清单的资源一律 404。
3. **令牌明文只在创建者会话可见**：公开接口只收令牌本身，数据库只有哈希。

### 验证结果（227/227 通过，`dotnet build` 0 警告 0 错误）

新增 5 项端到端测试：生命周期（创建/回读/撤销）、有效期越界 400、公开投影脱敏
（taskId 剥离、prompt 保留、apiKey 剥离、storageKey 不出现、content 重写为代理 URL）、
公开资源令牌放行 + 非放行 404 + 无效令牌 404、撤销后公开链接失效。

---

## 项目基础 CRUD（阶段 6 节点 A，已端到端打通）

本轮打通 **5 条项目路由**：`GET /projects`（摘要/分页双形态）、`POST /projects`、
`PATCH/DELETE /projects/:id`、含创建校验与删除级联解绑。

### 本轮交付物

- `Repository.Projects.cs` — 11 个方法：列表/分页/单读/创建/更新/删除级联
  （活动任务拒绝、画布解绑并回写 payload.projectId、单元/链接/分享清理、任务解绑）、
  画布文档/单元摘要/画布摘要/素材计数聚合
- `ProjectService` — 摘要聚合（canvasCount/assetCount/unitCount/completedUnitCount）、
  CreateProject（名称必填、type/aspectRatio/sourceType 默认值、画风资产配置校验
  `ValidateStyleProfileJSON`（256KB、schemaVersion/presetId/title/prompt/revision 必填、
  assets ≤20、执行策略枚举）、预设一致性 `ValidateStyleProfilePreset`、默认模型 ≤500）、
  UpdateProject（局部提交语义 + 主图资源归属/就绪/类型校验）
- 路由 4+1 条（list 双形态算 GET）

### 关键实现点

1. **删除项目先检查活动任务**（queued/running），且把项目下画布的 payload.projectId
   从 JSON 里移除并回写（键字典序）——与 Go 的解绑语义一致。
2. **画风配置是结构化合同**：Go `prompts.ValidateStyleProfileJSON` 校验
   schemaVersion=1 / revision ≥1 / assets 数组 ≤20 / executionPolicy 枚举；
   presetID 必须与快照内 presetId 一致。
3. **创建项目暂不生成默认工作流**（Go `EnsureBuiltinProjectWorkflowTemplate` +
   `createProjectWorkflow`），差异记入待确认 #27。

### 验证结果（233/233 通过，`dotnet build` 0 警告 0 错误）

新增 13 项测试：未登录 401、创建默认值与空名 400、摘要列表+分页 hasMore、
局部更新保留未提交字段、删除后 404、画风预设不一致 400。

### 阶段 6 剩余（下一轮建议）

- `GET /projects/:id`（ProjectDetail 全量聚合：工作流/镜头/角色 reconcile——依赖阶段 6 中后续节点）
- `GET /projects/:id/core`、`/overview`（工作台读视图）
- 单元管理 8 条（创建/列表/重排/更新/删除/导入/workspace）
- 画布链接 3 条、项目素材与分类 8 条、角色与配音 5 条、镜头与候选 9 条
- `EnsureBuiltinProjectWorkflowTemplate`（内置工作流模板种子，创建项目时依赖）

---

## 平台设置与公告（阶段 9 节点 A/B，已端到端打通）

本轮打通 **14 条路由**：平台设置 8 条（`/admin/settings/{registration,email,linuxdo,credits}` GET+PATCH）
与公告 6 条（`/announcements`、`/announcements/read`、`/announcements/:id/image`、
`/admin/announcements` GET+POST、`/admin/announcements/:id` PATCH、`.../close`、
`/admin/announcement-images/:id` DELETE）。

### 本轮交付物

- `AuthService.AdminSettings.cs` — 注册开关与邮件配置的管理读写：
  `normalizeEmailSetting`（端口默认 587、加密默认 starttls、域名归一化去 @ 与尾点）、
  `validateEmailSetting`（启用时主机/端口/发件邮箱必填、用户名需配密码、发件人名禁换行）、
  默认域名白名单 9 项（gmail/163/126/qq/outlook/hotmail/icloud/yahoo/foxmail）
- `CreditPolicyService` — `AdminAsync` / `UpdateAsync`（校验 + 审计事件
  `credit_policy.update`），倍率范围 0.0001–100
- `AnnouncementService` + `Repository.Announcements.cs` — 公告 feed（含未读数）、
  已读标记（幂等 ON CONFLICT）、管理分页（关键字/状态）、创建/更新（配图草稿消费）、
  关闭、配图草稿丢弃（含资源引用检查与删除任务）、配图下发（本地文件流 + 安全响应头）
- 14 条路由接线

### 关键实现点

1. **邮件默认域名白名单在无配置时生效**：Go `readEmailSetting` 无记录时返回
   `normalizeEmailSetting(零值)`，因此默认白名单会拦截白名单外域名——
   这修正了既有测试的错误假设（`new@example.com` 应先被 400 拒绝而非 403）。
2. **公告配图是草稿机制**：上传先落 `announcement_image_drafts`，创建/更新公告时
   同事务消费草稿；丢弃草稿前必须检查资源是否被其他业务引用（防误删）。
3. **公告更新会清空已读记录**（未读恢复），与 Go 的 `user_announcement_reads` 删除一致。

### 验证结果（244/244 通过，`dotnet build` 0 警告 0 错误）

新增 11 项测试：设置类 6 项（未登录 401、注册开关持久化、邮件默认值与启用校验、
域名归一化、积分策略校验与审计、LinuxDO 局部更新保留密钥）+ 公告 5 项
（未登录 401、生命周期创建→feed→已读→关闭→重复关闭 400、标题/级别校验、
更新重置已读、配图草稿校验与丢弃+配图下发）。

### 阶段 9 剩余（下一轮建议）

- 分析统计 4 条（overview/users/models/export.csv）
- API 日志 5 条（列表/详情/媒体/导出 CSV/查询任务）
- 存储管理、系统性能、系统更新 7 条（更新依赖 host-updater 独立进程）
- 外观设置 4 条、响应拦截 2 条、ARK 私有素材 2 条、OSS 设置 3 条（依赖云 SDK）

---

## API 日志与存储统计（阶段 9 节点 C，已端到端打通）

本轮打通 **4 条路由**：`GET /admin/api-logs`（分页+过滤）、`GET /admin/api-logs/:id`（详情含报文）、
`GET /admin/api-logs-export.csv`、`GET /admin/storage/stats`。

### 本轮交付物

- `Repository.AdminAnalytics.cs` — 日志过滤查询（时间范围/用户/模型/渠道/能力/记录类型/状态/关键字）、
  导出查询（≤5000）、按 ID 查日志、日志关联任务、账单批量、历史渠道引用（含已删除）、
  用户批量、存储汇总/按 kind/按 provider 分组统计
- `AdminAnalyticsService` — `normalizeAnalyticsFilter`（默认近 30 天、区间上限 366 天、
  单日 to 自动 +1 天）、`decorateAPICallLogs`（渠道名三态：自定义/实名/已删除渠道、
  用户账号、账单状态与金额、任务状态与媒体预览 URL）、列表剥离原始报文、
  CSV 导出（BOM + 12 列 + 转义）、存储统计投影
- 4 条路由接线

### 关键实现点

1. **列表与详情的报文边界**：列表强制清空 `requestBody`/`responseBody`（omitempty 使空串字段整体省略），
   原始报文只允许按单条读详情。
2. **渠道名三态**：`channelId` 为空 →「自定义渠道」；历史渠道表命中 → 真实名称；
   未命中 →「已删除渠道」——历史日志允许读已删除渠道名称，但不返回密钥。
3. **媒体预览双路径**：任务结果里的资源 ID 走代理 URL（`/api/admin/api-logs/:id/media`），
   外链直接透传。

### 验证结果（250/250 通过，`dotnet build` 0 警告 0 错误）

新增 6 项测试：未登录 401、列表装饰与报文剥离、详情与不存在 404、CSV 导出表头与数据行、
recordType 校验 400、存储统计分组（空 provider 归一 local、ready 与物理字节口径）。

### 阶段 9 剩余（下一轮建议）

- 分析总览 3 条（overview/users/models）——依赖 `user_daily_activities` 写入（待确认 #20）
- 存储资源列表 1 条、系统性能 2 条、系统更新 4 条（依赖 host-updater 独立进程）
- 外观设置 4 条、响应拦截 2 条、ARK 私有素材 2 条、OSS 设置 3 条（依赖云 SDK/资源上传）

---

## 项目单元与画布链接（阶段 6 节点 B，已端到端打通）

本轮打通 **10 条路由**：`POST/GET /projects/:id/units`、`GET/PATCH/DELETE /projects/:id/units/:unitId`、
`POST /projects/:id/units/import`、`PATCH /projects/:id/units/reorder`、
`POST /projects/:id/canvas-links`、`DELETE /projects/:id/canvas-links/:canvasId/units/:unitId`。

### 本轮交付物

- `Repository.ProjectUnits.cs` — 14 个方法：单元全量/单读/创建/批量导入（同事务）/
  条件更新（sourceChanged 决定是否写正文与字数）/删除/重排（逐条 position）/版本号递增/
  画布归属/链接 CRUD
- `ProjectUnitService` — `newProjectUnit`（kind 默认 chapter、白名单校验、标题必填、
  position 负值归零、状态 draft）、`ProjectUnitWordCount`（去 HTML 标签 + 反转义 + 字符数）、
  重排全量校验（数量一致、ID 有效、无重复）、导入 1–2500 上限、画布链接归属校验
- 10 条路由接线（2MB/32MB/256KB/64KB 分级体上限）

### 关键实现点

1. **字数统计口径**：Go `ProjectUnitWordCount` 先去 HTML 标签（正则）再 `html.UnescapeString`
   （`&amp;` → `&`）再取 rune 数——C# 用 `WebUtility.HtmlDecode` + `Trim().Length` 等价。
2. **导入响应剥离正文**：2500 章的长篇正文不回传，只返回标识与摘要。
3. **更新时条件写正文**：`sourceChanged` 为假时 UPDATE 不写 `source_text`/`word_count`，
   避免无谓的正文重写。
4. **重排是"全量提交"语义**：提交的 ID 列表必须与库中数量一致、全部有效且不重复，
   否则 400——防止并发下部分重排造成顺序错乱。

### 验证结果（256/256 通过，`dotnet build` 0 警告 0 错误）

新增 6 项测试：单元 CRUD（字数统计 4 字符、状态流转、删除）、类型/标题/状态校验 400、
批量导入（数量校验 + 正文剥离 + position 续排）、重排（不完整/无效 ID 400 + 正常交换）、
画布链接（归属 + canvasCounts + 空参 400 + 解绑）、项目不存在 404。

### 阶段 6 剩余（下一轮建议）

- 项目素材与分类 8 条、角色与配音 5 条、镜头与候选 9 条
- `GET /projects/:id`（ProjectDetail 全量聚合）、`/core`、`/overview`（工作台读视图）
- 工作流模板与实例 3 条（依赖内置模板种子，待确认 #27）
- 画布删除 2 条

---

## 项目素材文件夹树（阶段 6 节点 C，已端到端打通）

本轮打通 **4 条路由**：`GET/POST /projects/:id/asset-folders`、
`PATCH/DELETE /projects/:id/asset-folders/:folderId`。

### 本轮交付物

- `Repository.ProjectAssetFolders.cs` — 文件夹全量/单读/创建/更新/删除（均递增项目版本号）、
  素材链接读取与更新、按 ID 批量取素材
- `ProjectAssetFolderService` — 文件夹树全套校验：
  名称（非空、≤60 字符）、样式枚举（glass/stacked/midnight/paper/cinema/compact，默认 glass）、
  主题枚举（aurora/obsidian/ember/pearl，默认 aurora）、
  **父级校验**（自引用/环检测/父存在性/**子树高度 + 祖先深度 ≤ 8 层**）、
  同级重名（忽略大小写 + 唯一约束冲突兜底）、删除非空拒绝
- 4 条路由接线（32KB 体上限）

### 关键实现点

1. **层级限制是"祖先深度 + 子树高度"**：不是简单看父链长度——移动一个带子树的文件夹时，
   必须保证整棵子树落位后总深度 ≤ 8，否则深层子树会被截断。
2. **环检测与自引用分开报错**：`currentID == folderID` → 「不能移动到自身或其子目录」；
   父链出现重复 → 「文件夹目录关系存在循环」。
3. **样式/主题有默认值**：空串回落 `glass`/`aurora`，非法值 400——前端可直接省略。

### 验证结果（261/261 通过，`dotnet build` 0 警告 0 错误）

新增 5 项测试：文件夹 CRUD（默认样式主题、同级重名 400、局部更新保留未提交字段、删除）、
名称/样式/主题校验 400、层级（自引用、移入子目录、父不存在）、删除非空拒绝、项目不存在 404。
另统一加固了全部 16 个测试类的 Dispose 清理（连接池延迟导致的句柄占用重试）。

### 阶段 6 剩余（下一轮建议）

- 项目素材关联 4 条（`/projects/:id/assets` 列表/新增/更新/删除，含首版本事务）
- 角色与配音 5 条、镜头与候选 9 条
- `GET /projects/:id`、`/core`、`/overview`（工作台读视图）
- 工作流模板与实例 3 条（依赖内置模板种子，待确认 #27）
- 画布删除 2 条

---

## 风格档案与声音档案（阶段 10 节点 A，已端到端打通）

本轮打通 **7 条路由**：`GET/POST /style-profiles`、`PATCH/DELETE /style-profiles/:id`、
`PATCH /style-profiles/:id/favorite`、`POST /style-profiles/:id/use`、`GET /voice-profiles`。

### 本轮交付物

- `Repository.StyleProfiles.cs` — 风格列表（收藏优先 + 更新时间倒序）/计数/单读/创建/更新/
  收藏/最近使用/删除（命中 0 行视为不存在）、声音档案列表
- `StyleProfileService` — 用户风格 CRUD 全套校验：
  `ValidateStyleProfileJSON`（256KB、schemaVersion=1、presetId/title/prompt/revision 必填、
  assets ≤20、执行策略枚举、**来源枚举 builtin/user/external**）、
  `normalizeUserStyleProfileJSON`（名称 ≤80、简介 ≤500、标签 ≤20、封面 ≤4096B 且限
  http(s)/站内路径/图片 Data URL、负面 Prompt ≤64KB、source 归一为 user、revision 强制、
  标签去空去重、序列化后 ≤256KB）、上限 200 个
- `StyleProfileDocument` — 字段顺序与 Go 结构体一致（写库 JSON 逐字节对齐）
- 7 条路由接线（320KB 体上限）

### 关键实现点

1. **创建时两次归一化**：Go 先用空 presetID 归一生成 ID，再用真实 ID 二次归一——
   这样 `sourceProfileId` 落库就是自身 ID，`revision` 从 1 开始。
2. **更新时 revision +1 由服务层控制**，客户端提交的 revision 被忽略（防止回退版本）。
3. **封面 URL 白名单**：只允许 `http://`、`https://`、站内路径（`/` 开头）与
   `data:image/`，其他协议（如 ftp）一律 400。

### 验证结果（266/266 通过，`dotnet build` 0 警告 0 错误）

新增 5 项测试：未登录 401、CRUD（归一化 source=user/revision=1、标签 JSON 存储、
更新 revision=2、收藏、使用、删除）、校验（缺 presetId、非法来源、非法封面 400）、
不存在 404、声音档案空列表。

### 阶段 10 剩余（下一轮建议）

- 技能库 13 条（列表/详情/文件/搜索/安装/同步——依赖 GitHub 与文件系统）
- 提示词模板 7 条（`/settings/prompt-templates` 用户定制 + `/admin/prompt-templates` 管理）
- 插件运行时与声明式协议（阶段 10.1/10.2）
- LibTV/TapNow 集成、自定义渠道中转、系统代理短代理

### 协作提示

本轮发现 `ChannelAdminService` / `CanvasService` 中出现了另一会话写入的
`EnsureSystemChannelModelsAsync` 同名实现（语义正确、注释更完整），已按仓库约定
保留既有版本并移除本轮重复插入；如确有并行会话在编辑本目录，请协调先后（见待确认清单 #14）。

---

## 渠道模型写路径（能力子系统第四轮，已端到端打通）

本轮打通渠道模型 **4 条路由**：`POST /admin/channels/:id/models`、
`PATCH /admin/channels/:id/models/:modelId`、`DELETE .../models/:modelId`、
`POST .../models/batch-delete`。逻辑模型与渠道两个模块至此全部闭环。

### 本轮交付物

- `OpenAICanvas.Protocol/ProtocolRegistry.cs` — 协议**元数据**注册表：内置 13 协议 +
  官方插件包（`.yingce-plugin` zip 内 manifest.json 的 providers）元数据加载，
  供渠道模型保存的合同校验（协议存在 / Enabled / UnavailableReason / 能力类别匹配）。
  声明式协议的执行引擎属阶段 4/10，另行移植。
- `Application/ChannelModelAdminService.cs` — `SaveAdminChannelModel`（合同归一化、
  重复键冲突、能力配置归一化与版本计数、价格档归一化 `normalizeChannelModelPriceTiers`
  （旧 API 折叠默认档 / SKU 选择器规范化 / 计费方式与价格上限校验）、
  摘要档选择、`validateChannelModelTierCapabilities` 视频档规格校验）+
  单个/批量删除（归属校验、活动引用拒绝、≤100 上限）
- `Repository.Channels.cs` — `SaveChannelModelWithPriceTiers`（档位按规范键复用行并
  递增价格版本、移除档软删除、**GORM Save 的 UpdateOrCreate 语义**）、
  `DeleteChannelModels`（锁主体 → 归属 → 前台模型线路/进行中任务引用 → 停用 bump 版本 →
  软删除 → 刷新 ModelsJSON）、`SyncChannelModelNames`、`ChannelModelByID` /
  `ChannelModelByKeyIncludingDisabled`

### 关键实现点

1. **GORM `Save` 是 UpdateOrCreate**。Go 的 `tx.Save(item)` 对新模型 UPDATE 命中 0 行后
   自动回退 INSERT；C# 初版只做 UPDATE，创建静默失败（HTTP 响应仍用内存对象返回成功），
   直到后续更新才暴露"record not found"。已改为 UPDATE 失败即 INSERT。
2. **文本/图片/视频模型的 capabilityConfig 必填**。`NormalizeModelCapabilityConfigForModel`
   对三类能力都强校验——测试初版没带配置，行为与 Go 一致地被 400 拒绝。
3. **重复删除命中服务层归属校验**（400「请刷新后重试」），先于仓储 not-found，与 Go 一致。
4. **协议元数据来自插件包**。`grok-image` 等图片协议不在内置注册表，由仓库根
   `plugin-packages/*.yingce-plugin` 声明；C# 按 `CANVAS_OFFICIAL_PLUGIN_DIR` 或向上
   8 级查找目录加载元数据（与 Go `OfficialPluginPackageDir` 一致）。
5. **测试环境注意**：SSRF 放行（`CANVAS_ALLOWED_PRIVATE_UPSTREAM_HOSTS`）是进程环境变量，
   并行测试类共享——Dispose 清理会与仍在运行的类竞态，故只设置不清理。

### 验证结果（197/197 通过，`dotnet build` 0 警告 0 错误）

新增 9 项端到端测试：文本模型旧 API 折叠默认档 + ModelsJSON 刷新、视频双档与规格选择器、
档位规格超出能力配置报错、协议有效性/能力匹配双校验、重复模型键冲突、
插件协议（grok-image）元数据解析、更新价格版本递增且档位复用（模型 2 / 档位 2）、
单个删除与重复删除 400、批量删除空选择与前台模型引用拒绝。
端到端实测：保存（含中文 displayName）→ 列表含 selector → PATCH 版本递增 →
批量删除 `deleted:1`。

### 阶段 3 剩余（下一轮建议）

- ~~渠道模型 fetch/import（上游模型目录拉取与导入）~~ ✅（见下节）
- 渠道模型 test（`TestAdminChannelModel` 依赖阶段 4 provider 执行引擎，随任务模块一并移植）
- 系统中转与代理（阶段 10 提前依赖评估）
- 建议下一轮进入**阶段 5（资源/素材/存储）**或补齐 `EnsureSystemChannelModels` 启动种子，
  然后按 PLAN 顺序推进阶段 4（任务与生成，系统核心）


---

## 阶段 4 · 任务与生成 — 读取与文本回放（已端到端打通）

本轮交付 **7 条路由**（阶段 4 中不依赖 provider 执行引擎的部分）。

| # | 路由 | 状态 |
| --- | --- | --- |
| 4.1 | `GET /tasks` | ✅ 分页 + 项目过滤 + activeOnly |
| 4.2 | `GET /tasks/{id}` | ✅ 输出投影（抹掉渠道模型/供应线路） |
| 4.3 | `GET /tasks/{id}/logs` | ✅ |
| 4.4 | `POST /tasks/{id}/text-deltas` | ✅ 配额校验 + 行锁 |
| 4.5 | `GET /tasks/{id}/text-deltas` | ✅ `after` / `Last-Event-ID` 游标 |
| 4.6 | `POST /tasks/{id}/text-replay-complete` | ✅ 条件更新收尾 |
| 4.7 | `GET /admin/text-replay-stats` | ✅ 需管理员 |

### 交付物

- `Persistence/Repositories/Repository.Tasks.cs` — 13 个仓储方法（任务/日志/账单/文本增量）
- `Application/TaskOutput.cs` — 任务读模型投影（纯函数，316 行 Go 的完整翻译）
- `Application/TaskService.cs` — 任务读取与文本回放服务
- `Domain/Kernel/KernelUtil.cs` — `TruncateRunes`（**截断时追加 `...`**）
- `Web/Endpoints/TaskEndpoints.cs` — 7 条路由

### 关键实现点

1. **`kernel.TruncateRunes` 截断时追加 `...`**。这是一个容易漏的细节：
   `runes[:limit] + "..."`。未超限时原样返回。任务列表的 `prompt` 截到 500 rune，
   实际长度 503。顺带修正了 LinuxDO 里我自己写的同名函数（漏了 `...`）。
2. **任务列表/详情必须抹掉渠道字段**。`logicalModelRevisionId`、`routeId`、`channelModelId`
   是管理员内部信息，普通用户接口一律清空；`input_json` 只保留 8 个非敏感键白名单。
3. **文本增量的配额判定在事务内**：先对任务行加锁并校验状态（已结束直接拒绝），
   再算任务/用户字节与条数配额，最后写入。SQLite 无行锁，靠事务串行化。
4. **`task_text_delta` 是普通表**（不是软删除表），清理时用
   `expires_at <= ? OR NOT EXISTS (...)` 同时回收孤儿行。
5. **`TaskStatus` 命名冲突**：`OpenAICanvas.Domain.Entities.TaskStatus` 与
   `System.Threading.Tasks.TaskStatus` 同名，仓储文件需要显式 `using TaskStatus = ...`。

### 未做（见 PENDING-CONFIRMATIONS 第 29-31 条）

`POST /tasks`、`retry`、`cancel`、`query-provider`、`GET /tasks/{id}/text-events`（SSE）
依赖 provider 执行引擎与有界读缓存，作为阶段 4 主体专项推进。


---

## 阶段 8 · 财务与支付 — 钱包与兑换码（已端到端打通）

本轮交付 **10 条路由**（阶段 8 中不依赖支付 SDK 与结算引擎的部分）。

| # | 路由 | 状态 |
| --- | --- | --- |
| 8.1 | `GET /wallet` | ✅ 账本分页 + 积分策略 |
| 8.2 | `POST /wallet/redeem` | ✅ 限流 10/小时 |
| 8.3 | `POST /wallet/checkin` | ✅ 已签到 409 |
| 8.4 | `GET /admin/redeem-batches` | ✅ 含 4 个计数 |
| 8.5 | `POST /admin/redeem-batches` | ✅ 返回明文码 + `no-store` |
| 8.6 | `GET /admin/redeem-batches/{id}/codes` | ✅ 明文反查 + 兑换人 |
| 8.7 | `POST /admin/redeem-batches/{id}/disable` | ✅ |
| 8.8 | `POST /admin/redeem-batches/{id}/codes/{codeId}/disable` | ✅ |
| 8.9 | `POST /admin/users/{id}/credits/adjust` | ✅ 扣减余额下限保护 |
| 8.10 | `GET /admin/billing-orders` | ✅ `status` 默认 `review` |

### 交付物

- `Persistence/Repositories/Repository.Redeem.cs` — 10 个仓储方法（积分发放/调账/兑换码/账单）
- `Persistence/Repositories/Repository.Activity.cs` — 用户每日活跃 upsert
- `Application/FinanceService.cs` — 财务服务
- `Application/CanvasAuthHost.cs` — **认证宿主桥接（修复注册奖励缺失）**
- `Web/Endpoints/FinanceEndpoints.cs` — 10 条路由

### 三个关键修复

1. **认证宿主桥接缺失（真实缺陷）**。`CanvasService` 构造时没传 `authHost`，
   一直回落到 `NullAuthHost`，导致 `EnsureSignupBonus` 与 `RecordActivity` 都是空操作——
   **注册用户既没有积分账户也没有注册奖励**。按 Go 的 `authHost{svc: service}`
   方式延迟绑定修复（`CanvasAuthHost.Attach`）。
2. **基线漏导出只读计算字段**。`cmd/schema-dump` 遇到 `gorm:"-:migration"` 直接跳过，
   导致 `redeem_batches` 的 4 个子查询计数字段完全缺失。改为导出为瞬态字段后，
   列数仍 958、瞬态字段 13 → 17、**建表脚本逐字节不变**。
3. **手写 SQL 必须带列别名**。`SELECT t.*` 在 Dapper 下会让 `batch_id`、`code_hash`
   这类下划线列**静默读成空值**。新增 `SqlBuilder.Projection<T>(alias)` 统一生成
   `"t"."col" AS "Prop"`，本轮 4 处手写 SQL 全部改用它。

### 未做（见 PENDING-CONFIRMATIONS 第 32-34 条）

`POST /admin/billing-orders/:id/resolve` 与 `batch-resolve` 依赖 `SettleBillingOrder`
（155 行，token 计费 + 预授权差额 + 账本双分录）；`/payments/**` 依赖支付 SDK
与独立 RPC 插件进程。两者作为阶段 8 后续专项。


---

## 阶段 8 · 计费结算与退款（资金核心，已打通）

本轮交付 **2 条路由** + 结算/退款的完整仓储层。

| # | 路由 | 状态 |
| --- | --- | --- |
| 8.11 | `POST /admin/billing-orders/{id}/resolve` | ✅ 结算 / 退款 |
| 8.12 | `POST /admin/billing-orders/batch-resolve` | ✅ 逐单提交 + 失败项列表 |

### 交付物

- `Persistence/Repositories/Repository.Billing.cs` — 9 个方法：
  `MarkBillingRunning` / `MarkBillingUncertain` / `SettleBillingOrder` /
  `RestoreRefundedBillingOrder` / `RefundBillingOrder` / `BillingUsage` /
  `TaskHasSuccessfulBillableCall` / `RecordBillingResolution` / `UpdateBillingProviderRequestID`
- `Application/FinanceService.cs` — `ResolveBillingOrderAsync` / `ResolveBillingOrdersAsync`

### 资金不变量（每个用例同时校验三者一致）

1. **订单状态 × 账户余额 × 账本分录** 必须同时正确。
   只改状态没动钱、或动了钱没分录，都是缺陷。
2. **整笔结算**：`reserved -= amount`，可用余额不变，账本一条 `consume`
   （`amount = -amount`，`reservedDelta = -amount`）。
3. **token 结算**：按上游真实用量算实际金额，`refund = max(reserved - actual, 0)`，
   `supplement = max(actual - reserved, 0)`；账本写 `consume`
   （`availableDelta = -supplement`，`reservedDelta = -reserved`）+
   差额 `refund` 分录。
4. **硬上限截断**：`charge_limit_microcredits > 0 && actual > limit` → `actual = limit`，
   账本备注带「硬上限」文案。
5. **结算失败保留上游用量**：即使事务回滚，已观测到的 usage 仍写回订单
   （上游事实不能因账户异常丢失）。
6. **幂等**：已结算订单重复结算直接成功返回；已退款订单结算报错。

### 一个容易看错的语义

`zeroPricedTokenOrder`（三个单价全为 0）**只在 `RestoreRefundedBillingOrder` 里
把 actual 置 0**；在 `SettleBillingOrder` 里，token 分支的条件是
`token && !zeroPriced`，所以**零价 token 订单会落到「整笔预留转消费」分支**，
`actual = AmountMicrocredits` 而不是 0。测试专门覆盖了这个反直觉点。

### 错误映射

- `BillingUsageUnavailable`（无成功调用日志 / 视频缺输出 token）→ 500
- `BillingStateConflict`（状态与期望不符）→ 500
- 订单不存在 → 404；`action` 非法 / 缺 `note` → 400

### 剩余（PENDING-CONFIRMATIONS 第 33 条）

`/payments/**`（21 条）依赖支付 SDK 与独立 RPC 插件进程，需先确认插件部署形态。


---

## 阶段 8 · 支付插件 RPC 宿主（基础设施已打通）

### 结论：不需要任何第三方 RPC 库

Go 侧的「支付插件」**不是** gRPC，也**不是** `net/rpc`，而是一个极简的
**stdio 单行 JSON 协议**（`yingce.payment/v1`）：

```
宿主启动插件进程（工作目录=插件包目录，环境变量清空到 PATH/HOME）
  → stdin 写一行 JSON 请求（写完关闭管道）
  → stdout 读一行 JSON 响应（上限 2MB）
  → 进程退出（超时 30 秒）
```

每次调用起一个新进程，无长连接、无序列化框架。因此 .NET 8 用标准库的
`System.Diagnostics.Process` 即可完整实现，**无需引入 gRPC / SignalR / 任何 RPC 包**。

### 交付物

- `Payment/PaymentTypes.cs` — 协议 DTO + `PaymentJson` 配置
- `Payment/PaymentProvider.cs` — `IPaymentProvider` 接口 + `PaymentRegistry`
- `Payment/RpcProvider.cs` — `RpcPaymentProvider`（进程宿主，含 Linux ELF 架构校验）
- `tests/OpenAICanvas.PaymentTestPlugin/` — **真实的测试插件进程**（.NET 8 控制台）
- `tests/OpenAICanvas.Tests/Payment/RpcPaymentProviderTests.cs` — 24 个端到端用例

### 协议里的三个坑

1. **JSON 命名混合了两种风格**。Go 侧一部分 struct 带 json tag（camelCase），
   另一部分**没有 tag**（`encoding/json` 原样用字段名 → PascalCase）。
   例如 `Checkout` / `CreateRequest` / `BillRecord` / `NotificationResponse` 是 PascalCase，
   而 `Result` / `Notification` / `Descriptor` 是 camelCase。**必须逐个核对，不能统一套命名策略。**
2. **写完请求必须关闭 stdin**。插件侧是「读到 EOF 才算拿到完整请求」
   （Go 的 `cmd.Stdin = strings.NewReader(...)` 也是这个语义）。
   不关管道，插件会一直阻塞在读取上直到超时——这个 bug 让首轮 24 个用例全部超时 30 秒。
3. **判定顺序必须与 Go 一致**：先读完 stdout → 检查长度超限 → 最后才看是否超时。
   若先等进程退出，插件写满管道会被阻塞、进程不退出，于是「响应超限」被误报成「超时」。

### 安全约束（与 Go 一致）

- 入口路径必须相对且以 `backend/` 开头（拒绝绝对路径、`..` 逃逸）
- 进程环境变量清空到只剩 `PATH=/usr/bin:/bin`、`HOME=/nonexistent`
- stdout 上限 2MB、执行超时 30 秒
- Linux 下校验 ELF：拒绝 Windows PE / Mach-O / 异架构二进制（提前报错，而非落到 `execve` 的含糊 ENOEXEC）

### 错误码映射

| 情况 | 错误码 |
| --- | --- |
| 可执行文件不存在 | `plugin_executable_missing` |
| 无执行权限 | `plugin_permission_denied` |
| 格式/架构不兼容 | `plugin_exec_format_error` |
| 启动失败 | `plugin_start_failed` |
| 执行超时 | `plugin_timeout` |
| 进程非零退出 | `plugin_process_failed` |
| 响应非法 JSON | `plugin_invalid_response` |
| 读取失败 | `plugin_read_failed` |
| 插件业务失败 | 插件给的 `code` 原样透传 |

### 未做（按用户指示）

- **支付 SDK（插件本身的实现）**：支付宝 / 微信适配器先不实现。
  插件是独立进程，可继续使用 Go 编译的二进制（`plugin-packages/` 已打包）。
- **`/payments/**`（21 条路由）**：宿主已就绪，路由待接。


---

## 阶段 8 · 支付渠道、商品与订单（已端到端打通）

本轮交付 **18 条路由**（用户侧 10 + 管理端 8；对账 3 条留下一轮）。

| # | 路由 | 状态 |
| --- | --- | --- |
| 8.13 | `GET /payments/providers` | ✅ 只回「已启用且已配置」 |
| 8.14 | `GET /payments/products` | ✅ 需 `credits` 功能开关 |
| 8.15 | `POST /payments/orders` | ✅ 用户级幂等键 + 未支付订单数上限 |
| 8.16 | `GET /payments/orders/{id}` | ✅ |
| 8.17 | `POST /payments/orders/{id}/query` | ✅ 2 秒节流 |
| 8.18 | `POST /payments/orders/{id}/close` | ✅ 关单前必查单 |
| 8.19 | `GET /payments/orders/{id}/checkout` | ✅ 302 跳渠道收银台 |
| 8.20 | `POST /payments/orders/{id}/checkout/refresh` | ✅ 限流 5/小时 |
| 8.21 | `POST /payments/notify/{providerId}/{configId}` | ✅ 免鉴权 + 验签 + 1MB 上限 |
| 8.22 | `GET /payments/return/{providerId}` | ✅ 订单号形态校验后 302 |
| 8.23 | `GET /admin/payments/providers` | ✅ 敏感字段只回 `secretConfigured` |
| 8.24 | `PUT /admin/payments/providers/{id}/config` | ✅ 版本化配置 |
| 8.25-8.27 | `GET/POST/PUT /admin/payments/products` | ✅ |
| 8.28 | `GET /admin/payments/orders` | ✅ 关键词跨表匹配 + 用户信息 |
| 8.29-8.30 | `POST /admin/payments/orders/{id}/query\|close` | ✅ |

### 交付物

- `Persistence/Repositories/Repository.Payment.cs` — 24 个仓储方法
- `Application/PaymentService.cs` — 服务（订单状态机、回调收件箱、入账）
- `Application/PaymentContracts.cs` — DTO
- `Application/PaymentPluginManifests.cs` — 内置渠道清单 + `IPluginAvailability`
- `Protocol/ManifestPaymentTypes.cs` — 清单字段类型
- `Web/Endpoints/PaymentEndpoints.cs` — 18 条路由
- `tests/.../Payment/FakePaymentProvider.cs` — 进程内测试适配器

### 资金不变量

1. **入账三重保护**：行锁订单 → 账本 `reference_key` 唯一约束（回调/查单竞态的最终防线）
   → 条件更新 `status <> credited`。任一处不满足都回滚。
2. **证据必须匹配**：金额、币种、渠道交易号与下单快照一致才入账；
   同一渠道的交易号不得被两个订单使用。
3. **余额上限**：`available + credits <= 2^53-1`，超出拒绝（避免前端精度丢失）。
4. **回调收件箱**：按 `(provider_id, provider_event_id)` 去重；先落库再尝试入账，
   失败留给 worker 重试——回调路径必须快，但不能丢事件。
5. **关单前必查单**；关单失败后**再查一次**，否则已成功的支付会卡在 closing 重试循环。

### 一个重要的架构发现

内置的微信/支付宝适配器 manifest 里 `Runtime.Backend = "host:wechatpay-v3-native"`，
即**宿主内置实现**（与 `plugin-packages/` 里 `runtime.backend = "rpc"` 的独立进程插件不同）。
但 Go 宿主的 `paymentRegistry` 实际是**空的**——适配器实现并未接入。
所以按用户指示暂缓适配器移植后：
- `GET /payments/providers` 返回空数组、下单报「未知支付渠道」——与 Go 行为一致
- 其余逻辑（商品、订单状态机、回调、入账）完整可用，测试注入进程内适配器即可全流程验证

### 两处需要留意的差异

- **插件启用状态**：Go 用 `pluginStateForUser`（插件系统，阶段 10 未移植）。
  C# 抽成 `IPluginAvailability`，默认实现读内置清单的 `Enabled`（false），测试注入「全可用」。
- **`ErrOrderNotFound` 的识别**：Go 用 `errors.Is`，但走 RPC 时插件把哨兵错误包成了
  普通消息，`errors.Is` 匹配不到。C# 改用专门的 `PaymentOrderNotFoundException`，
  由 `RpcPaymentProvider` 按消息前缀识别，让进程内与 RPC 两条路径行为一致。

### 未做

- **对账 3 条**（`POST/GET /admin/payments/reconciliations`、`GET .../{id}/items`）——下一轮
- **内置适配器实现**（按用户指示暂缓；插件可复用 Go 编译的二进制）


---

## 阶段 8 · 支付对账（收尾，阶段 8 已完整）

本轮交付 **3 条路由**，阶段 8 的 33 条可做路由全部完成。

| # | 路由 | 状态 |
| --- | --- | --- |
| 8.31 | `POST /admin/payments/reconciliations` | ✅ 手动触发对账 |
| 8.32 | `GET /admin/payments/reconciliations` | ✅ 运行分页（pageSize 默认 30） |
| 8.33 | `GET /admin/payments/reconciliations/{id}/items` | ✅ 明细分页（pageSize 默认 50） |

### 交付物

- `Persistence/Repositories/Repository.Reconciliation.cs` — 9 个方法
- `Application/PaymentService.Reconciliation.cs` — 对账服务（partial）

### 对账的核心安全约束

**下载到的账单只是「线索」，不是付款授权。** 每一笔补发都必须先经**签名查单**确认：

```
账单说已支付 → 查单确认（MerchantOrderNo 一致 && Paid && 金额一致 && 交易号一致）
             → 才调 CompletePaymentOrder 补发积分
```

否则伪造一份账单文件就能凭空充值。测试专门构造了「账单说已支付、查单返回未支付」的用例，
断言记为 `credit_failed` 且**不产生任何账本记录**。

### 七种比对结果

| 结果 | 含义 |
| --- | --- |
| `matched` | 本地已入账 + 账单有记录，一致 |
| `recovered` | 账单有记录 + 本地未入账 + 查单确认 → 自动补发成功 |
| `local_order_not_found` | 账单有成功交易，本地没有对应订单 |
| `provider_record_missing` | 本地已入账，账单里找不到对应记录 |
| `amount_mismatch` | 金额或币种不一致 |
| `trade_no_mismatch` | 交易号不一致 |
| `credit_failed` | 账单确认已支付，但查单未确认或补发失败 |

### 两个实现要点

1. **每天每渠道只能有一个对账运行**：`(provider_id, bill_date)` 唯一键 + 行锁，
   配合 30 分钟冷却，保证两个 worker 不会同时补发同一笔积分。
   并发抢锁失败时**重读并套用同样的冷却规则**，而不是报冲突。
2. **账单日按渠道时区（+08:00）计算**。用固定偏移而非
   `TimeZoneInfo.FindSystemTimeZoneById`——后者的 ID 在 Windows 与 Linux 不同，
   而 Go 侧就是硬编码 +08:00。窗口是 `[账单日 00:00, 次日 00:00)`（上海时区）。

### 一个踩到的坑

Dapper 的 SQL 里 **`WHERE` 子句用到的条件值也必须传参**。
我在 `CompletePaymentReconciliation` 的 `WHERE status = @running` 里漏传了 `@running`，
Dapper 直接报 `Must add values for the following parameters`——好在它报错而不是静默匹配。

### 阶段 8 收尾状态

钱包 / 签到 / 兑换码 / 调账 / 账单核对 / 支付渠道 / 商品 / 订单 / 回调 / 对账 **全部完成**。
唯一剩下的是内置适配器实现（支付宝 / 微信协议翻译），按用户指示暂缓——
Go 宿主的 `paymentRegistry` 实际也是空的，所以当前行为与 Go 一致。


---

## 阶段 10 · 技能库（读取与状态，已端到端打通）

本轮交付 **8 条路由**（技能库中不依赖文件写入与网络的部分）。

| # | 路由 | 状态 |
| --- | --- | --- |
| 10.9 | `GET /skills` | ✅ 四种 scope + 搜索 + 分类 + 三种排序 |
| 10.10 | `GET /skills/added` | ✅ 我加入的 |
| 10.11 | `GET /skills/{id}` | ✅ 含正文 |
| 10.12 | `DELETE /skills/{id}` | ✅ 仅作者，级联清理状态/版本/文件 |
| 10.13-14 | `POST\|DELETE /skills/{id}/add` | ✅ 加入 / 移出 |
| 10.15-16 | `POST\|DELETE /skills/{id}/like` | ✅ 收藏 / 取消收藏 |

### 交付物

- `Persistence/Repositories/Repository.Skills.cs` — 10 个方法
- `Application/SkillsService.cs` — 技能服务
- `Web/Endpoints/SkillsEndpoints.cs` — 8 条路由

### 三个容易做错的点

1. **可见性边界必须统一**。所有详情与关系写入（加入 / 收藏）都先过 `VisibleSkillAsync`，
   否则私有技能可以通过「加入」或「收藏」侧信道泄露正文。删除还要再过一层
   `OwnedSkillAsync`（仅作者）。
2. **`scope=public` 对所有人（含作者）都只返回公开技能**。
   作者要看自己的私有技能必须用 `scope=created` 或 `scope=mine`。
   我第一版测试按「作者能看到自己的全部」写，被打脸了。
3. **计数 = 内置初始值 + 实时用户行为**。`initial_like_count` / `initial_added_count`
   是迁移历史数据用的，必须在输出时叠加实时统计，否则老技能的数字会「变小」。

### 其他细节

- **列表不带 `instruction`**（正文可能很长），只有详情带。靠 `omitempty` 实现。
- **列表查询刻意不加 `DISTINCT`**：用户状态表按 `(user_id, skill_id)` 唯一，
  JOIN 不会重复；加了 DISTINCT 会被带进 PostgreSQL 的热门排序查询里。
- **展示媒体兼容历史 snake_case**（`showcase_uri` / `showcase_url`），
  只在持久化边界转换，对外一律 camelCase。
- **未知分类静默忽略**（等价于不过滤），不报错——与 Go 一致。

### 未做

技能写入（`POST /skills`、`PUT /skills/{id}`）依赖技能包文件落盘；
安装 / 同步 / 文件读取（`files`/`file`/`file/raw`/`bundle`/`search`/`sync`）同样需要
文件系统与网络（GitHub 安装还要过 SSRF 防护）。


---

## 阶段 10 · 提示词模板（4 条路由）

本轮交付 **4 条路由** + 9 个内置操作定义的完整数据。

| # | 路由 | 状态 |
| --- | --- | --- |
| 10.17 | `GET /admin/prompt-templates` | ✅ 模板列表 + 操作定义 |
| 10.18 | `POST /admin/prompt-templates` | ✅ 新建版本（body 上限 64KB） |
| 10.19 | `PATCH /admin/prompt-templates/{id}` | ✅ 更新版本 |
| 10.20 | `DELETE /admin/prompt-templates/{id}` | ✅ 删除版本 |

### 交付物

- `Persistence/Repositories/Repository.PromptTemplates.cs` — 7 个方法
- `Application/Prompts/PromptTemplateService.cs` — 服务（含启动种子）
- `Application/Prompts/PromptContracts.cs` — DTO
- `Application/Prompts/PromptDefaults.cs` — **生成文件**（9 个操作定义）
- `Web/Endpoints/PromptTemplateEndpoints.cs` — 4 条路由
- `scripts/generate-prompt-defaults.py` — 生成脚本

### 内置模板数据用「Go 导出 → 脚本生成」而不是手工翻译

21KB 中文模板正文，手工复制极易出现不可见偏差。做法是：

```bash
cd backend && go run ./cmd/prompts-dump > ../backend-dotnet/prompts-dump.json
cd ../backend-dotnet && python scripts/generate-prompt-defaults.py
```

`cmd/prompts-dump` 通过 `internal/prompts/export_for_tools.go` 的桥接函数调用包内私有的
`defaultPromptDefinitions()`，用 **Go 自身的 JSON 编码器**导出，保证逐字一致。

### 版本化模板的四条硬约束

1. **每次创建产生新版本**（`MAX(version) + 1`），历史生成结果永远可追溯。
2. **启用中的版本不可修改内容或名称**——要改必须新建版本。
3. **启用中的版本不能直接停用**——必须先启用同类型的其他版本。
4. **启用中的版本不能删除**——同上。

配合「每个操作最多一个启用版本」的不变量（`SavePromptTemplate` 在启用时先停用其他版本）。

### 占位符白名单

模板里的 `{{变量}}` 必须都在该操作的 `Variables` 里声明过。未知变量在保存时就拒绝
（报错时按字典序排列去重后列出）——否则渲染时会静默留下未替换的占位符，
产生错误的提示词却很难发现。

### 其他细节

- **`DefaultContent` 绝不下发**（Go 的 `json:"-"`）。测试专门断言响应体里没有 `defaultContent`。
- **种子幂等**：已存在任何版本的操作用户不动；分镜视频模板还会剥离历史遗留的引导语
  （仅限 `created_by` 为空且仍以旧前缀开头的启用版本）。
- **`SavePromptTemplate` 必须是 upsert**：Go 用 `tx.Save`（有主键则 UPDATE，无则 INSERT）。
  为此新增了 `SqlDialect.OnConflictDoUpdate` 辅助。

### 顺带清理的编译警告

把 `--no-incremental` 全量构建的代码警告从 6 处清到 0：
- `LinuxDO` 头像/用户名字段可空化（上游可能缺失）
- `ChannelModelAdmin` 的 `null!` 显式忽略
- `ModelCapabilityConfigOps` 的 `ApplyModelSpecificVideoCapability` 参数与返回类型可空化
- 两处「同步实现包装成 Task」的方法加 `await Task.CompletedTask`
- 测试里的 `GetString() ?? ""`

剩余 8 个警告是 xUnit 风格建议（测试方法里用 `ConfigureAwait`）与 `MSB3061` 文件锁
（环境问题，`--no-incremental` 特有），均非代码缺陷，记在 PENDING-CONFIRMATIONS 第 19 条。
