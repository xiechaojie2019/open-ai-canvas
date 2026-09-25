# 待确认清单（Go → .NET 8 翻译遗留项）

> 用途：历轮翻译中**有意推迟、无法当场验证、或与 Go 行为存在已记录差异**的条目汇总。
> 由维护者统一处置；处置后在条目上标注结论并同步对应模块文档。
>
> 分级：`[缺陷]` 与 Go 行为不一致或可能产生错误结果 ｜ `[取舍]` 已选择的实现决策，需追认
> ｜ `[待移植]` 排队中的模块依赖 ｜ `[待验证]` 需要双跑/实测确认

---

## 一、安全与加密

### 1. `[缺陷]` 设置加密为占位实现
- 位置：`src/OpenAICanvas.Auth/AuthService.cs` 的 `EncryptSecret` / `DecryptSecret`
- 现状：直接返回原值。Go 端是 AES-GCM，密钥来自 `.settings-key`。
- 影响：渠道 `apiKey`/`secretKey`、OAuth 客户端密钥等**明文落库**；与 Go 版数据库互读时，
  Go 写入的密文 .NET 解不开（反之亦然）。
- 处置建议：实现 AES-GCM（密钥文件路径与格式对齐 Go），并确定存量明文数据的迁移策略。

### 2. `[取舍]` SSRF 放行走进程环境变量
- 位置：`src/OpenAICanvas.Outbound/OutboundGuard.cs`
- 现状：`CANVAS_ALLOW_PRIVATE_UPSTREAMS`、`CANVAS_ALLOWED_PRIVATE_UPSTREAM_HOSTS` 直接读
  `Environment.GetEnvironmentVariable`（与 Go `os.Getenv` 一致），未纳入
  `CanvasEnvironment` 强类型封装；测试通过直接改进程环境变量控制。
- 影响：无配置热更新能力；并行测试类共享进程环境（已去掉 Dispose 清理避免竞态）。
- 处置建议：追认；或纳入 CanvasEnvironment 统一管理。

## 二、分布式与多实例

### 3. `[待移植]` Redis 协调器缺失
- 位置：`src/OpenAICanvas.Application/LogicalModelService.cs`
- 现状：路由目录版本（`routeCatalogVersion`）与线路健康阻断（`routeHealthBlocked`）
  只落进程内存。Go 经 Redis 协调（`BumpRouteCatalogVersion` / `RouteCatalogVersion` /
  `RouteBlockedUntil` / `BlockRoute`），并在 `invalidateRouteCatalog` 时跨实例传播。
- 影响：单实例部署行为一致；**多实例部署时**目录失效与健康阻断不跨实例传播，
  可能出现实例间目录短暂不一致。
- 处置建议：接入 StackExchange.Redis 协调器（PLAN 技术选型已列），接口形状已预留。

### 4. `[待验证]` 登录/验证码限流的存储
- 位置：`src/OpenAICanvas.Web/Security/RateLimiter.cs`（InMemoryRateLimiter）
- 现状：进程内存限流。Go 端 `enforceRateLimit` 是否走 Redis 协调器未逐行核对。
- 处置建议：核对 Go `handler/security.go`；若 Go 也为本地限流则追认。

## 三、排序与确定性

### 5. `[取舍]` 列表查询追加了 `id ASC` tiebreaker
- 位置：`Repository.SystemChannelsAsync` / `LogicalModelsAsync` / `ChannelModelsAsync` /
  价格档附着、`SaveChannelOrderAsync` 等
- 现状：Go 的 `ORDER BY sort_order asc, created_at asc`（无第三排序键，同键顺序依实现）；
  C# 统一追加 `, id ASC` 保证确定性。
- 影响：同一 `(sort_order, created_at)` 的记录在两版中的**数组顺序**可能不同
  （响应体不逐字节一致，但集合内容一致）。
- 处置建议：追认（确定性排序对前端更友好）；或移除 tiebreaker 逐字节对齐。

### 6. `[待验证]` 价格档复用行的 CreatedAt 语义
- 位置：`Repository.SaveChannelModelWithPriceTiersAsync`
- 现状：档位规格不变再次保存时，Go 走 `tx.Save(tier)`（GORM 全字段更新，struct 的
  CreatedAt 为零值——**是否会把 created_at 写成零值未实测**）；C# 保留原行 CreatedAt。
- 影响：仅当同一 selector_key 存在多行且依赖 created_at 排序时可见。
- 处置建议：用 Go 实例实测一次再定；若 Go 确实写零值，评估是否跟随。

### 7. `[取舍]` 插件包损坏的容错策略与 Go 不同
- 位置：`src/OpenAICanvas.Protocol/ProtocolRegistry.cs`（LoadPluginMetadata）
- 现状：Go 的 fallback 加载遇任一 `.yingce-plugin` 损坏会**整体失败**
  （registry 为 nil → 包括内置在内所有协议都不可解析）；C# 跳过坏包继续加载其余元数据。
- 影响：极端部署（坏包存在）下 Go 全部拒绝保存渠道模型，C# 部分可用。生产无坏包时一致。
- 处置建议：追认 C# 的容错更合理；或改为与 Go 一致的整目录失败。

## 四、JSON 与数值的逐字节差异

### 8. `[取舍]` defaults/spec 存储字节可能不同
- 现状：Go 解析 JSON 后数字统一 float64，再序列化时最短化（`5.0` → `5`）；
  C# JsonElement 保留原始文本（`5.0` 原样入库）。
- 影响：写库的 `capability_spec_json` / `default_options_json` 与 Go 不逐字节一致；
  语义与读写互认不受影响（双方解析器兼容）。
- 处置建议：追认；或在写入前做数字规范化。

### 9. `[取舍]` GoFormatFloat 的量级边界
- 位置：`CapabilitySpecOps.GoFormatFloat`
- 现状：以 330 位定点格式化模拟 Go `strconv.FormatFloat(v,'f',-1,64)`；秒数/数量/价格等
  领域取值安全，超过 double 常规量级（≈1e21 以上）与 Go 输出可能有别。
- 处置建议：追认（已注释边界）。

### 10. `[待验证]` 能力匹配 reasons 的顺序
- 现状：Go 遍历 map 生成 `CapabilityMatch.Reasons`，顺序**每次运行随机**；
  C# 确定性（Ordinal 序）。双方 `matched` 结果一致。
- 影响：做双跑逐字节对比时 reason 数组顺序需要对排序后比较。
- 处置建议：双跑验收脚本（阶段 12.9）按集合比较该字段。

### 11. `[取舍]` 排序接口超限请求的 400 文案
- 现状：PUT order 请求体超 1MB 时 Go 返回原始 `http: request body too large` 文案；
  C# 统一返回 `Bad Request`（与既有认证路由的绑定失败约定一致）。
- 处置建议：追认或单独对齐文案。

## 五、未移植（按 PLAN 排队，非缺陷）

### 12. `[待移植]` NoRoute 短代理
- Go：`/api/{channelId}/{providerPath}` 系统代理转发（含 24 个保留前缀黑名单），阶段 10.8。
- 现状：`app.MapFallback` 只回 404。

### 13. `[待移植]` 渠道模型 fetch / import / test
- Go：`FetchAdminChannelModels` / `ImportAdminChannelModels` / `TestAdminChannelModel`，
  依赖出站 HTTP 客户端（SSRF 受控 Dialer）。阶段 5/10。

### 14. `[待移植]` Go main.go 启动序列的其余种子
- `EnsureDefaultPromptTemplates`、`EnsureBuiltinProjectWorkflowTemplate`、
  `EnsureBuiltinSkills`、`EnsureSkillPackages`、`MigrateLegacyStorage`（阶段 6/10）。
- 注：`EnsureSystemChannelModels` 已于本轮接入（Program.cs）。

### 15. `[待移植]` 默认能力配置与任务能力校验
- `DefaultModelCapabilityConfig` / `DefaultImageCapabilityConfig`（未移植）；
  `ValidateTaskCapability`、`switchTaskToNextRoute`、`resolveArchivedTaskRoute` 等
  任务选路逻辑（阶段 4 主体）。

### 16. `[待移植]` 阶段 0.16 环境变量补全
- 已确认缺失：`CANVAS_REGISTRATION_ENABLED`、`CANVAS_PUBLIC_BASE_URL`、
  `ENABLE_PROVIDER_PLUGINS`、`HTTP_PROXY`/`HTTPS_PROXY`/`NO_PROXY`、
  `CANVAS_UPDATER_*`（13 个）、`SQLITE_SOURCE_PATH`。
- 部分已用但未进 `CanvasEnvironment`：`CANVAS_OFFICIAL_PLUGIN_DIR`（ProtocolRegistry）、
  `CANVAS_ALLOW_PRIVATE_UPSTREAMS` / `CANVAS_ALLOWED_PRIVATE_UPSTREAM_HOSTS`（OutboundGuard）。
- 已实现：`CANVAS_CHANNEL_CIRCUIT_*`（RuntimePolicy）。

### 17. `[部分完成·待部署双跑]` 阶段 12 部署与验收
- 已补齐 `OpenAICanvas.Tools` 的 `migrate-schema`、`migrate-sqlite-postgres`、
  `migrate-logical-model-families`、`migrate-channel-model-price-tiers`、
  `reseed-logical-model-sources`；已补齐 `backend-dotnet/Dockerfile` 与独立 Compose。
- 已实现最小 `host-updater` 协议宿主（Linux Unix socket + Bearer token + Release 检查），
  但 .NET Docker update/rollback 明确返回 409；完整主机备份、镜像切换、数据库回滚仍使用 Go updater。
- 已验证：Tools 独立构建、全量 solution build、SQLite schema 15 生命周期、Compose config、
  22 条迁移/插件回归测试。未验证：真实 PostgreSQL SQLite→PG 双跑、真实 Docker build/up、329 条路由全量比对。

## 六、协作与工程卫生

### 18. `[待确认]` 是否存在并行编辑会话
- 本轮在 `ChannelAdminService` / `CanvasService` 发现另一会话写入的
  `EnsureSystemChannelModelsAsync` 实现（语义正确、注释更完整，已保留并去重）。
- 若确有多个会话同时编辑 `backend-dotnet/`，请明确先后顺序，避免互相覆盖。

### 19. `[缺陷·轻微]` 测试告警清理
- 位置：`tests/OpenAICanvas.Tests/`
- 现状：**代码警告已清零**（`--no-incremental` 全量构建从 6 处清到 0）。
  剩余 8 条为 xUnit 分析器风格建议（`xUnit1030`: 测试方法里调用 `ConfigureAwait(false)`）
  与 `MSB3061` 文件锁（`--no-incremental` 重建时的 obj 目录竞争，环境问题非代码缺陷）。
- 影响：无功能影响。
- 处置建议：可在下次触碰相关测试文件时顺手去掉 `ConfigureAwait(false)`；不必专门跑一轮。


### 20. `[已解决]` 用户每日活跃记录（RecordActivity）
- 位置：`CanvasService.RecordActivity` + `Repository.RecordUserActivityAsync`
- 现状：**已实现**（本轮随认证宿主桥接一并补齐）。按天 upsert，支持
  login / task / agent_message / canvas / asset / resource 六类事件；
  未知事件静默忽略，与 Go 的 `default: return nil` 一致。
- 关联修复：`CanvasAuthHost` 之前未注入，导致 `EnsureSignupBonus` 与
  `RecordActivity` 都走 `NullAuthHost` 空实现——注册用户既没有积分账户也没有注册奖励。
  现已按 Go 的 `authHost{svc: service}` 方式延迟绑定。

### 21. `[待验证]` 结构化配额的字节口径
- 现状：C# `UserStorageUsageAsync` 与 Go `repo.UserStorageUsage` 均按 payload 长度求和；
  但 Go 用 `len([]byte(...))`（UTF-8 字节），C# 混用 `string.Length`（UTF-16 代码单元）的
  路径已在画布节点统一为 `Encoding.UTF8.GetByteCount`。
- 处置建议：双跑对比同一数据集的配额扣减结果；检查其余 3 处 `UserStorageUsage` 调用方
  （素材/任务节点移植时）沿用字节口径。

### 22. `[取舍]` 存储锁为进程内信号量
- 现状：Go 的 `storageMu` 也是进程内互斥锁（非分布式锁），C# 用 `SemaphoreSlim` 等价。
- 处置建议：追认；多实例部署下与 Go 有一致的并发边界。

### 23. `[待验证]` 素材移动时 payload.updatedAt 的小数位格式
- 现状：Go 用 RFC3339Nano（尾零去除），C# 固定 7 位小数。
  影响素材移动后 payload 内 `updatedAt` 的字节表示（语义一致）。
- 处置建议：统一改为尾零去除格式（`ResolveRfc3339Nano`），或双跑时按解析后时间比较。

### 24. `[取舍]` DELETE /assets/:id 暂未接路由
- 现状：资源级联判定链（`resource_delete.go`，含 AssetVersion/AssetReference/业务引用检查
  与软删除决策）未移植；为避免"半翻译"造成误删，删除路由整体推迟到资源节点。
- 处置建议：阶段 5 资源节点移植后一并接线并补"删除被引用素材返回来源"的测试。

### 25. `[待移植]` 云存储物理删除 worker
- 位置：`ResourceDeleteService.DrainResourceDeletionJobsAsync`
- 现状：删除任务（Outbox）已同事务落库；本地 provider 内联清理 ✅。
  云 provider（aliyun-oss / tencent-cos / qiniu-kodo / s3）的物理对象删除
  依赖云 SDK（Go 用 aliyun-oss-sdk / COS XML SDK / qiniu-go-sdk / aws-sdk），
  任务保留 pending 由后续 worker 推进。
- 影响：本地部署完全等价；云存储部署时删除的物理对象暂不清理（记录仍在，无泄漏引用）。
- 处置建议：引入 AWSSDK.S3（已引用）实现 S3 分支；国产云按 REST 签名实现或引入官方 SDK。

### 26. `[待移植]` 公开分享资源的 Range 与云 provider 重定向
- 位置：`MapCanvasShareRoutes` 的 `/public/canvas-shares/:token/resources/:resourceId/file`
- 现状：本地 provider 用 `SendFileAsync` 全量投递（支持 If-Modified 等基础语义）。
  Go 侧还有 Range 分段请求（音视频拖动）与云 provider 307 重定向分支未移植。
- 处置建议：音视频拖动成为需求时补 Range 解析；云 provider 随 #25 一并接入。

### 27. `[已解决]` 创建项目默认工作流
- 位置：`ProjectService.CreateProjectAsync` 与 `ProjectWorkflowService`。
- 现状：**已落地** Go 的默认项目级工作流模板初始化、实例+步骤事务写入、项目
  revision 递增与失败回滚；项目详情现在可返回项目级与单元级 workflows。
- 任务产物补偿使用部署数据目录解密任务输入，避免读取错误的默认路径。

### 28. `[待移植]` 公告配图上传与 OSS/响应拦截设置
- 位置：`MapAnnouncementRoutes` 的 `/admin/announcement-images`（POST）、
  `/admin/settings/{oss,response-interception,ark-private-assets}`
- 现状：公告配图的**草稿消费/丢弃/下发**已完整移植；上传（multipart + 图片嗅探）
  依赖资源上传链路待接；OSS/响应拦截/ARK 设置依赖云 SDK。
- **已补齐（2026-09-18）**：`/admin/settings/appearance` 全量（阶段 7.4），
  含公开读取、管理端读写、皮肤主题校验、外观资源上传与匿名下发。
- 处置建议：随阶段 5 资源上传节点一并补齐。

### 29. `[待移植]` 任务创建与执行引擎（阶段 4 主体）
- 位置：`handler/routes.go` 的 `POST /tasks`、`POST /tasks/:id/retry`、
  `POST /tasks/:id/cancel`、`POST /tasks/:id/query-provider`
- 现状：任务的**读取与文本回放**（列表 / 详情 / 日志 / 增量 / 收尾 / 统计）已完整移植；
  写入路径依赖 provider 执行引擎（`provider_text.go`、`provider_image.go`、
  `provider_video.go`、`provider_audio.go`、`provider_http_client.go`、`provider_protocol.go`）
  与 worker 调度（`task_worker.go`）、计费协调（`billing.go`）、生命周期
  （`task_lifecycle.go`）。
- 影响：无法通过 API 创建任务；任务列表/详情在空库下返回空数组，行为正确。
- 处置建议：作为阶段 4 主体专项推进，建议按「计费协调 → 文本协议 → 图片协议 →
  worker 调度 → 视频/音频 → HTTP/声明式」顺序拆分。

### 30. `[待移植]` 文本事件 SSE 流（`GET /tasks/:id/text-events`）
- 位置：`handler/routes.go: streamTaskTextEvents` + `app/text_replay_cache.go`
- 现状：**非流式**的文本回放（`GET /tasks/:id/text-deltas`）已移植，
  含 `after` / `Last-Event-ID` 游标语义；SSE 长连接未做。
- 阻塞：需要 `platform.NewBoundedReadCache`（有界读缓存，750ms TTL、按字节数限容）
  与 `ErrReadCacheBusy → 503` 的降级路径；两者都依赖 `internal/platform` 的缓存子系统。
- 处置建议：随阶段 4 provider 执行引擎一并补齐；缓存子系统可先行独立移植。

### 31. `[取舍]` 文本增量请求体上限
- 位置：`src/OpenAICanvas.Web/Endpoints/TaskEndpoints.cs`
- 现状：`POST /tasks/:id/text-deltas` 加了 1MB 的 `ContentLength` 前置上限；
  Go 侧该路由**未设置** `MaxBytesReader`（仅靠 64KB 的单条增量校验兜底）。
- 影响：超过 1MB 的请求体会在路由层直接 400，而 Go 会在服务层报「单条文本增量不能超过 64KB」。
  两者都拒绝，但错误文案与状态码相同（均 400），仅拒绝位置不同。
- 处置建议：追认（防御性上限），或去掉以与 Go 完全一致。

### 32. `[已解决]` 计费结算与退款（资金核心）
- 位置：`Persistence/Repositories/Repository.Billing.cs`
- 现状：**已移植**。含 `SettleBillingOrder`（token 用量结算 + 预授权差额 +
  硬上限截断 + 账本双分录）、`RestoreRefundedBillingOrder`、`RefundBillingOrder`、
  `MarkBillingRunning` / `MarkBillingUncertain`。
- 验证：12 个端到端用例，每个同时校验**订单状态 × 账户余额 × 账本分录**三者一致。
- 遗留：`app/task_billing.go` 的协调层（`CheckRetryEligibility` /
  `BillingFailureRequiresReview`）尚未接线——它供任务重试与失败补偿使用，
  随阶段 4 的 `task_lifecycle` 一并完成。

### 33. `[基本解决]` 支付渠道（支付宝 / 微信）
- **宿主侧已就绪**：`Payment/RpcProvider.cs` 完整实现 `yingce.payment/v1` stdio JSON 协议。
- **路由与订单状态机已就绪**：18 条路由（渠道 / 商品 / 订单 / 回调 / 收银台 / 管理端），
  入账走三重保护（行锁 + 账本唯一键 + 条件更新）。
- **待做 1（按用户指示暂缓）**：内置适配器实现（支付宝 / 微信协议翻译）。
  Go 宿主的 `paymentRegistry` 实际也是空的，所以行为一致。
- **已完成**：对账 3 条路由 + `app/payment_reconciliation.go`（355 行）。
  含七种比对结果与「账单必须经签名查单确认才能补发」的安全防线。
- 阶段 8 的 33 条可做路由**全部完成**。

### 36. `[待移植]` 插件启用状态（`pluginStateForUser`）
- 位置：`Application/PaymentPluginManifests.cs` 的 `IPluginAvailability`
- 现状：支付渠道的 `pluginEnabled` 本应来自插件系统（阶段 10），
  当前退化为「读内置清单的 `Enabled`」（false）。
- 影响：未接入真实插件系统前，内置渠道永远显示为「未启用」。
- 处置建议：随阶段 10 插件系统一并接线；接口形状已预留，调用方无需改动。

### 37. `[取舍]` `ErrOrderNotFound` 的跨进程识别
- 位置：`Payment/PaymentTypes.cs` 的 `PaymentOrderNotFoundException`
- 现状：Go 宿主用 `errors.Is(err, payment.ErrOrderNotFound)` 判定「上游查不到单」；
  但走 RPC 时插件把哨兵错误包成了普通消息（`"payment provider order not found: xxx"`），
  `errors.Is` **匹配不到**——这是 Go 侧的既有行为。
  C# 改用专门异常类型，由 `RpcPaymentProvider` 按消息前缀识别。
- 影响：进程内与 RPC 两条路径的「订单不存在」语义一致；比 Go 的 RPC 路径更正确。
- 处置建议：追认。若要与 Go 完全一致，可在 RPC 路径不识别该前缀。

### 34. `[取舍]` 兑换码批次明文存档未加密
- 位置：`FinanceService.EncryptBatchCodes` / `DecryptBatchCodes`
- 现状：批次明文码以 JSON 明文存入 `redeem_batches.codes_cipher`；
  Go 用 `encryptSettingSecret`（AES-GCM）加密。
- 影响：与第 1 条同源（设置加密为占位实现）。管理员导出明文码的接口行为一致，
  但**静态存储不加密**；与 Go 版数据库互读时解不开对方的数据。
- 处置建议：随第 1 条一并实现 AES-GCM。

### 35. `[已解决]` 基线漏导出 `gorm:"-:migration"` 只读计算字段
- 位置：`backend/cmd/schema-dump/main.go`
- 现状：**已修复**。原实现遇到 `field.IgnoreMigration` 直接 `continue`，
  导致 `redeem_batches` 的 `available_count` / `redeemed_count` / `disabled_count` /
  `expired_count` 四个子查询字段完全没进基线，实体层缺字段、API 响应少字段。
- 修法：改为导出为**瞬态字段**（`isColumn: false`），并跳过方言类型推导。
  重新生成后：列数仍 958，瞬态字段 13 → 17，**建表脚本逐字节不变**。
- 备注：这是「生成器已支持瞬态字段、但导出端丢弃」的盲区，其余 76 张表无同类字段。

### 38. `[待移植]` 技能写入与安装（依赖文件系统 / 网络）
- 位置：`internal/skills/skill_packages.go`（1011 行）+ `handler/skills.go` 的
  `POST /skills`、`PUT /skills/{id}`、`/skills/install`、`/skills/install/github`、
  `/skills/{id}/sync` 与文件读取（`files`/`file`/`file/raw`/`bundle`/`search`）
- 现状：**技能读取与状态已完整移植**（8 条路由）；写入与安装未做。
- 阻塞：技能包要落盘到 `<dataDir>/skill-packages/<skillId>/`，并维护
  `skill_versions` / `skill_files` 两级记录；GitHub 安装还需出站 HTTP + 解压 + SSRF 防护。
- 处置建议：作为阶段 10 的后续专项；技能包存储可复用阶段 5 的资源存储约定。

---

### 47. `[取舍]` 角色设定 JSON 数字保留原文（Go float64 最短化）
- 位置：`Application/ProjectCharacterService.cs`（NormalizedDefinition / SortedElement）
- 现状：Go 把请求里的 definition 绑定成 `map[string]any`，数字全部变 float64，
  再 `json.Marshal` 时输出最短浮点形式（`1.0` → `1`，超 2^53 精度丢失）。
  C# 沿用仓库既有 DOM 模式（与 AssetLibrary/ProjectService 一致）：JsonElement 保留原文，
  仅递归按 Ordinal 排序键。常规前端数值（整数、短小数）输出一致；
  `1.0` 这类写法会保留 `1.0`。
- 处置建议：若要逐字节对齐需实现 Go 的 shortest-float 格式化（ragon/strconv APP.e-1x 语义），
  建议统一在一个 JSON 工具层做，供全部 DOM 化节点复用。

### 48. `[已解决]` `/voice-profiles` 曾返回原始实体且未播种内置声音
- 位置：`Web/Endpoints/UserDataEndpoints.cs` 的 voice-profiles 处理器
- 现状：**已修正**。Go 的 `ListVoiceProfiles` 会幂等播种 13 个内置
  OpenAI 兼容声音并返回 `VoiceProfileSummary` 投影（compatibleModels 数组、
  sampleResourceId omitempty、仅启用档案）；旧实现直接回原始实体（compatibleModelsJson 透出）。
- 修法：改走 `ProjectCharacterService.ListVoiceProfilesAsync`；
  `Repository.VoiceProfilesAsync` 补 `status='active'` 过滤（对齐 Go）。
  旧测试 `声音档案列表_空数组` 的假设被推翻，改为断言 13 条内置 + 幂等。

### 49. `[取舍]` 角色/项目缺失走 500 信封（原样对齐 Go）
- 位置：`Application/ProjectCharacterService.cs`（CharacterAssetAsync / RequireProjectAsync）
- 现状：Go 的角色路由不用 `IsProjectNotFound`，`ProjectForUser` / `ProjectCharacterAsset`
  的 gorm not-found 原样进 `failService` → `failInternal` → **HTTP 500 +「系统处理失败」**。
  .NET 用 `InvalidOperationException("record not found")` 复现同一投影（其余项目路由
  如 style-profiles 用 404，角色路由刻意保持 500）。
- 处置建议：维持现状即可；若日后 Go 侧统一 not-found 语义，这里一并改。

### 50. `[待移植]` 三视图任务收尾与项目读时的 reconcile
- 位置：`app/project_character.go` 的 `finalizeCharacterTurnaroundTask` /
  `characterTurnaroundTaskBound` / `reconcileCharacterTurnaroundTasks`
- 现状：角色 CRUD/形象/声音已移植（6 条路由 + voice-profiles）；
  三视图生成任务的「任务成功 → 绑定新角色版本」收尾依赖任务域（input 解密、
  canvas_image 任务类型、幂等绑定），与「任务与创作」节点同批接入。
- 阻塞：任务创建/SSE/worker 链路（阶段 8）。

### 51. `[已解决]` ProjectDetail 全量聚合与工作流读视图
- 位置：`app/project.go` 的 `ProjectDetail`（GET /projects/:id）与
  `project_workbench_read.go` 的 workspace/画布分页读视图。
- 现状：**已落地** `GET /projects/{id}`、`GET /projects/{id}/canvases`、
  `GET /projects/{id}/units/{unitId}/workspace`，并补齐工作流详情、任务摘要、
  镜头/版本/产物/素材引用、候选与项目素材摘要聚合。项目详情读取会对成功任务
  执行工作流产物补偿（资源、项目素材版本、镜头产物与任务关联），单个历史任务
  补偿失败不阻断项目展示。
- 备注：角色三视图专用 `reconcileCharacterTurnaroundTasks` 仍由角色任务域后续
  接入；本批已完成 `RegisterTaskOutputFromTask` 对工作流任务的读侧补偿。

### 52. `[取舍]` 分页查询参数解析错误文案
- 位置：`handler/project.go` 的 `parsePositiveQueryInt`（asset-candidates 等）
- 现状：Go 对 `page=abc` 返回 400 + Go strconv 原始报错文案
  （如 `strconv.ParseInt: parsing "abc": invalid syntax`）；
  C# 返回 400 + "Bad Request"。状态码与 code 一致，仅 msg 文案不同。
  请求体 JSON 绑定错误同理（Go 输出 encoding/json 报错原文）。
- 处置建议：保持现状（前端按状态码处理）；如需逐字节对齐可复制 Go 的错误文案。

### 53. `[已解决]` 资源 CRUD/导入路由与存储用量契约
- 位置：`Web/Endpoints/ResourceCrudEndpoints.cs` + `Application/ResourceUploadService.cs`
- 现状：**已落地 7 条路由**（列表/详情/整传 multipart/URL 导入/存储用量/OSS 直链/ARK 同步）。
  URL 导入完整移植（SSRF 校验 → 90s 限长下载 → 复用上传的幂等/探测/配额/落盘），
  并补 PNG/GIF/JPEG 尺寸头探测（对齐 Go `image.DecodeConfig` 注册的三种格式）。
- 修正：`AccountFileStorageUsage` 原为 `{UsedBytes, LimitBytes, OverQuota}`，
  与 Go 契约 `{usedBytes, totalBytes}` 不符，已改齐。
- 遗留：ARK 私有资产同步仍为桩（云 SDK 未移植，#25）→ 调用即 500 信封。

### 54. `[取舍]` Cache-Control 响应头的合成顺序
- 位置：`GET /resources/:id/oss-url`
- 现状：Go 原样输出 `private, no-store`；ASP.NET 对已知响应头经类型化模型
  重新合成，输出 `no-store, private`。指令集合与语义一致，仅顺序不同。
- 处置建议：保持现状；除非发现前端/代理对顺序敏感，不值得绕过框架管道。

### 55. `[待移植]` 剩余大节点（任务执行 / 创作运行 / 云 Agent / 技能写入）
- 位置：`app/task_creation.go`（POST /tasks 前置校验 + 计费预留）、
  `task_execution.go`/`task_worker.go`/`task_route_executor.go`（worker 执行引擎）、
  `provider_task_recovery.go`（query-task/retry/cancel 的供应线路查询）、
  `creation.go`（creation-runs 15 条，908 行）+ `creation_canvas.go`（571 行）、
  `handler/agent.go` + `app/agent*`（云 Agent 10 条）、
  `skills/skill_packages.go` 写入与安装（5 条 + 文件 5 条）、
  `ai/models` 系统中转、`model-catalog` 3 条、`diagnostics` 2 条、`runninghub` 2 条、
  settings 余量（runtime-policy/oss/drawing-engine/libtv/response-interception/
  ark-private-assets/prompt-templates/system-performance/system-update）
- 现状：以上路由未挂；依赖任务创建-计费-Worker 执行链与供应线路协议引擎，
  建议按「任务创建 → Worker 执行 → SSE → retry/cancel/query-provider →
  timeline → creation-runs → agent → 技能/其余零散」顺序分批移植。
- 处置建议：每批落地后在 CHECKLIST 记录路由数与测试，并回填本清单。

### 56. `[已解决]` 管理端分析总览与模型价格
- 位置：`Application/AdminAnalyticsService.Analytics.cs` + `Repository.AnalyticsQueries.cs`
- 现状：**已移植**（8 条路由）。分析聚合与 Go 逐语义对齐（滚动窗 DAU/WAU/MAU、
  趋势点、模型/用户/失败聚合、分位数取整 `index = (n-1)q + 0.5`）。
- 备注：api_call_logs 表**没有** updated_at 列（GORM 实体字段是装饰结果），
  分析投影按 schema-dump 的真实列清单。

### 57. `[待移植]` 剩余路由盘点（任务引擎及其下游，截至本批后余 ~70 条）
- **已知分布**：任务引擎 7（POST /tasks、retry、cancel、query-provider、
  text-events SSE、timeline 2）；creation-runs 13；云 Agent 7；技能 9
  （files 5 + 写入 4）；OSS 设置 6（test 依赖云 SDK）；libtv 3 + canvas 导入 2；
  system-performance 2；system-update 4（host-updater 进程，.NET 部署形态待定）；
  diagnostics 2；runninghub 2；POST /ai/models 系统中转 1；
  api-logs media/query-task 2；admin/channels/{id}/models/test 1
- 移植顺序建议不变：任务引擎 → creation-runs → agent → 技能 → 其余零散
  （oss/libtv/runninghub/ai/models 均依赖出站或云 SDK，可与任务引擎解耦并行）。

### 58. `[待移植]` POST /tasks 队列路径 admission（模型路由 + 计费预留）
- 位置：`app/task_creation.go` 的 `resolveTaskModelSelection`（前台模型/系统渠道/
  自定义渠道三分支）、`applyRoutedProviderSelection`、`applyChannelCapabilityDefaults`、
  `capabilityOptionsFromConfig`、`channelModelPriceTierForIntent`、
  `taskBillingOrder`/`newLogicalModelBillingOrder`（finance.go:427）与
  `RequireWorkflowPluginForUser`（RunningHub 插件门控）
- 现状：文本回放路径已完整（含密钥加密、投影脱敏、存储配额）；
  非回放任务返回 Go 维护模式信封（HTTP 400 +「服务正在维护，暂不接受新的生成任务」，
  与 Go handler 对 CreateTask 统一 fail(c,400,err) 的投影一致）。
- 下批：retry/cancel/query-provider 与队列 admission 同批（共享路由/计费解析）。
  `ModelRequestIntentFromTaskInput`、`hasExecutableProviderVideoConfig`、
  `taskInputUses*`、`compactPersistedValue` 等前置件已随本批移植或已在
  CapabilitySpec/ModelCatalog 中。

### 59. `[取舍]` ValidateTaskCapability 逐值校验未随队列 admission 移植
- 位置：Go `model_capability.go:700`（validateImageTask / validateVideoTask /
  validateWorkflowProvider*）
- 现状：队列 admission 已移植（批 2）；OPTION 级参数约束由逻辑模型
  MatchCapability 与系统渠道能力合同匹配覆盖。图片/视频的逐值校验
  （尺寸枚举、时长范围、参考图数量与大小等）留待 worker 执行批次补齐。
- 影响：客户端提交越界参数的任务会成功入队，执行阶段才会失败；
  Go 会在 admission 阶段拒绝。无安全影响（能力合同仍由
  ResolveLogicalModel / 渠道匹配强制）。

### 60. `[已解决]` POST /tasks 队列路径 admission
- 现状：**已移植**（本批）。三分支选路、能力默认值回填、capabilityOptions
  归一、计费预留（channel/unified 双策略）、工作流插件门控、
  项目归档守卫、内嵌媒体检查全部落地；471→477 项测试。
- 剩余（下一批）：retry/cancel/query-provider 与 worker 执行引擎。

### 61. `[取舍]` 取消任务的上游 HTTP 取消请求跳过
- 位置：Go `task_lifecycle.go` cancelTask 的 requestProviderCancellation 后台调用
- 现状：本批 cancel 已移植条件取消、幂等、账单退款/待核对、文本回放收尾；
  带 ProviderRequestID 的任务在 Go 会后台请求上游取消（30s 超时，失败仅记日志）。
  C# 无 provider 引擎，跳过该调用并记录 warn 日志；费用仍留给
  人工核对/retry 对账（与 Go 的最终账务路径一致），任务侧状态无差异。
- 处置建议：随 provider 引擎批次补齐。

### 62. `[待移植]` query-provider 的上游状态查询
- 位置：Go `provider_task_recovery.go` queryFailedVideoTask 后半段
- 现状：四级准入门槛（失败态/视频类型/上游 ID/账单归属）与租约语义已移植；
  实际上游查询依赖 declarative protocol adapter（provider 引擎）。
  当前通过门槛后返回 Go 对非声明式协议的同文案
  「该任务的请求协议不支持安全查询上游状态」。

### 63. `[已解决]` 创作画布提交三路由
- 现状：**已移植**（canvas / canvas-snapshot / canvas-commit）。
  获批范围 diff 覆盖：顶层字段、节点增删改、连线增删改、
  结果回写（taskId+nodeId+资源指纹+成功态）。
  与 Go 的差异：手工编辑三态检测按 baseline 元数据逐键实现（一致）；
  `SameJson` 采用递归语义比较（键序/转义无关）——Go 的 reflect.DeepEqual
  对解析后 map 语义相同。
- 备注：`creationAddedNode` 的 8 类节点默认尺寸/标题已内置；
  `batch-table`/`script` 的 CreateMetadata 默认结构未逐字复制
  （新增节点 metadata 默认 content/status，其余由 ops.Metadata 提供）。

### 64. `[取舍]` 创作报价 prepare 的 agentRequests 占位符水合
- 位置：Go `creation_agent_references.go`（resolveAgentResourcePlaceholders）
- 现状：创作仅允许 text 模式携带 agentRequests；本期 C# 未实现占位符水合，
  携带 agentRequests + resource: 引用的请求会在 text 参考图检查处失败
  （媒体模式直接拒绝 agentRequests，与 Go 一致）。
- 处置建议：随云 Agent 批次补齐。

### 65. `[部分解决]` 技能写入与同步（3 条）
- 现状更新：**create/update 已移植**（POST /skills、PUT /skills/:id，单 Markdown 技能，
  含 ZIP 落盘/内容哈希/版本链/四写事务，492 项测试）；file 5 条读路由此前已通。
- 剩余：`POST /skills/install`（multipart zip/markdown 解析 +
  normalizeSkillArchiveRoot）、`POST /skills/install/github`、`POST /skills/:id/sync`
  （依赖出站 GitHub 客户端与 StartSyncWorker）。
- 位置：Go `skills/skill_packages.go` InstallSkillUpload（multipart zip/markdown
  解析 + normalizeSkillArchiveRoot + persistSkillArchive）/ InstallGitHubSkill /
  SyncGitHubSkill（出站 GitHub 客户端 + StartSyncWorker）
- 现状：5 条文件读路由已通（与 Go 落盘布局一致）；create/update（单 Markdown
  技能，走 createSingleMarkdownSkill）随下批；install/sync 依赖出站与
  zip/markdown 解析器（archiveFromZip/archiveFromMarkdown/normalizeSkillArchiveRoot）。

### 66. `[部分解决]` 系统性能与缓存清理
- 现状：**2 条路由已移植**（GET /admin/system-performance、
  POST /admin/system-performance/cache/clear）。
  指标为 .NET 等价实现：goroutines→ThreadPool.ThreadCount、
  gomaxprocs→ProcessorCount、GC 堆统计→GC API、磁盘→DriveInfo+写探针。
- 剩余：loadAverage（Linux 可读 /proc/loadavg 增强）、
  Redis 协调器统计（未移植，恒为本地模式）、
  ActiveWorkerTasks（待 worker 批次）。
- 备注：cache clear 的 Redis 分组删除未移植（无 Redis），本地限流窗口清零
  与路由目录失效已接。

## 处置记录

| 日期 | 条目 | 结论 |
| --- | --- | --- |
| （待维护者填写） | | |

### 66. `[已解决]` 用户诊断包 preview/export
- 现状：**已移植**（POST /diagnostics/preview、POST /diagnostics/export）。
  采集上限（任务 100/日志 2000/上游 1000）、时间窗（默认近 30 分钟、≤24h）、
  客户端事件截取最后 500 条、逐记录脱敏（14 类标记 [REDACTED]、URL 剥离
  query/fragment、标识符白名单、rune 截断、数值上下界）、
  ZIP 结构（manifest.json/README.txt/3 个 jsonl/runtime.json）、
  10MB 上限、品牌名来自外观设置均与 Go 一致。
- 备注：JSON 数字/转义与 Go 有细微差异（同 #47 家族）。

### 67. `[已解决]` 资源删除后台作业的两项附带职责
- 位置：`src/OpenAICanvas.Web/Workers/ResourceCleanupWorker.cs`
- 内容：Go 的 `startResourceDeletionWorker`（`app/resource_deletion_worker.go`）除
  **孤儿资源清理**与 **drain 删除任务**外，还兼任两项清理：
  1. `cleanupStaleAnnouncementImageDrafts` —— 清理超期（24h）的公告配图草稿；
  2. `cleanupExpiredArchivedAssets` —— 按 `RecycleBinRetentionDays` 清理回收站过期素材。
- **已于 2026-09-20 补齐**（阶段 5.4 收尾）：
  - `ResourceCleanupService` 新增 `CleanupStaleAnnouncementImageDraftsAsync`（分批循环，
    每批 50，直到无剩余或无可清理项）与 `CleanupExpiredArchivedAssetsAsync`
    （保留天数取自运行时策略，≤0 时不回收）。
  - 为让后台作业可复用草稿丢弃逻辑，把 `AnnouncementService.DiscardAnnouncementImageAsync`
    的核心抽出为 `DiscardAnnouncementImageDraftCoreAsync`（不含角色校验，与 Go 的
    `discardAnnouncementImageDraft` 对齐）。
  - worker 的启动轮与每小时轮现已按 Go 顺序执行：**草稿 → 回收站 → 孤儿**，三者互不影响。
  - 仓储新增 `ExpiredArchivedAssetsAsync`（`status = archived AND updated_at <= cutoff`）。
### 68. `[进行中]` 阶段 11（云 Agent）分批移植：契约基座已通，运行时与路由未动
- 现状：阶段 11 按依赖切片推进。本批已落「契约基座」并保证与 Go 逐字节
  一致（测试锁定，防漂移）：
  - 画布能力注册表 `Domain/Canvas/Capability/CanvasCapabilities.cs`
    （descriptor/registry/builtin；能力集哈希 `9f4199f9d89d5ce4ba08142f78c17cdd6c8f888c46069ea562786e0bfd8cf980`）。
    关键还原点：Go `cloneDescriptor` 的空切片经 `append(nil…)` 变 nil → JSON `null`；
    `normalizeConnectionKinds` 对连线输入类型做字典序排序。
  - Agent 策略文档 `OpenAICanvas.Prompts/AgentPolicies/*.md`（嵌入资源）+
    `AgentPolicyDocuments.cs`（解析/哈希与 Go `prompts.LoadAgentPolicies` 一致）。
  - 执行记录/画布变更仓储 `Repository.CloudAgents.cs`（Ensure/查询/修订 CAS
    互斥 `MutateCloudAgent`/终态 CAS/undo 标记）+ `CloudAgentMutationContext`
    事务上下文（画布/任务/资源/配额 InTx）。
  - 运行契约 `Application/CloudAgent/CloudAgentContracts.cs`：请求/状态/运行时/
    审批/事件 DTO（字段顺序=Go 结构体声明顺序）、请求校验、确定性 ID/指纹、
    画布/媒体内容哈希（键排序 + Go 转义 + 数字最短表示）、检查点异常。
  - 全局 JSON 配置下沉 `Domain/Serialization/GoJson.cs`（Web `CanvasJson`
    变为别名），Application 层不再依赖 Web。
  - 任务 admission 扩展：`TaskAdmission`（确定性任务 ID + MaxCharge 报价上限
    + token 计费 ChargeLimit 固化），挂在 CreateQueued/AdmitQueued。
- 第二批（11.2/11.3/11.5 会话与读取面）已通：`CloudAgentSessionService.cs`、
  `CloudAgentPolicyCompiler.cs`（含偏好快照校验、锚点构建）、`CloudAgentTools.cs`
  （工具 schema，清单与 Go 一致）、`CloudAgentCanvasState.cs`（分页投影/精读/
  分镜与批量表结构化投影/媒体参考解析）。测试 1505/1505。
- 已知取舍（收口批 2026-09-25 已闭环）：CreateAsync 续聊分支的 advance 调用、
  工具执行事务、步进入队、审批决策/取消/撤销/清理交接全部落地；10 条路由 +
  SSE events + worker 调度钩子（CloudAgentSchedulerWorker，2s tick + keyset
  游标 + 根任务恢复）已接线并开放。
- 剩余：Docker 部署验证；Go `advanceCloudAgents` 的 Redis 多实例协调未移植
  （调度器为单实例内存游标，与 PENDING #66 同族）。
- 剩余（下批顺序）：运行时 advance 循环与审批决策 → 工具执行（读工具 + 写事务）
  → 媒体（引用/草稿/完成回写）→ 分镜与批量表变更 → undo/recovery →
  `handler/agent.go` 10 条路由 + SSE events → worker 调度钩子（advanceCloudAgents）。
- 取舍：分批期间 `POST /agent/runs` 等路由整体未接线（避免「可建不可跑」的
  悬挂运行）；10 条路由待运行时闭环后一次性开放。


### 69. `[进行中]` 全盘扫描后的最终剩余清单（2026-09-25 复核）
- 扫描口径：Go 路由 319 条 vs .NET 端点；分组前缀还原后逐条核实。
- **确认为假阳性（已实现）**：plugins 全部 10 条（PluginEndpoints.cs）、
  payments 旧前缀 11 条（/admin/payments/*）、openapi.yaml（Program.cs 内嵌
  backend openapi.yaml 原样输出）、/oauth/linuxdo/callback 根级别名
  （Program.cs:406）、creation-runs 循环注册伪影。
- **本批已补**：工作流 v2 6 条：GET /projects/{id}（ProjectDetail 全量聚合与成功任务产物补偿）、GET canvases 分页、GET workspace、POST workflows、PATCH workflow-steps/{stepId}、POST task-output。新增 `Repository.ProjectWorkflow.cs` 的 Dapper 查询/事务方法与 `ProjectWorkflowService`，覆盖模板初始化、工作流实例/步骤、状态门禁、项目/单元/镜头归属校验、产物幂等回填。
- **真实剩余**：工作流 v2、Eagle、skills 安装/同步、LibTV/TapNow 导入均已完成；以下只剩最后的系统渠道流式中转：
  1. ~~timeline renders 1 条~~ ✅ 2026-09-25 已补：CreateTimelineRenderAsync
     （HasMedia 校验 + timeline_render 任务）+ POST /timeline/renders 端点。
  2. ~~channels models/test 1 条~~ ✅ 2026-09-25 已补：
     ChannelModelAdminService.TestAdminChannelModelAsync + TaskWorkerService.
     RunProviderProbeAsync + POST /admin/channels/{id}/models/test 端点。
  3. ~~Eagle 6 条 HTTP 路由~~ ✅ 2026-09-25 已补：EagleService + loopback/41595
     禁代理客户端 + 六条 PluginEagleEndpoints 路由（library/items/file/thumbnail/
     items POST/folders POST）。PENDING 原按“能力族”计 5 条，实际 HTTP 数为 6。
  4. ~~system-update 4 条~~ ✅ 2026-09-25 已补（用户决策：.NET 走 Docker，
     不移植 hostupdate）：GET/check 返回固定 supported=false 状态，
     start/rollback 返回 409「请通过 docker compose build 完成升级」。
  5. ~~画布导入 2 条~~ ✅ 2026-09-25 已补：CanvasImportService +
     CanvasImportEndpoints（LibTV token 读取/外部详情归一与 TapNow 分享解析，
     16KB body 限制、项目归属与受控出站）。
  6. ~~skills 安装 3 条~~ ✅ 2026-09-25 已补：SkillsService.Install/GitHub
     + SkillsInstallEndpoints（multipart Markdown/ZIP、GitHub URL/ref/subdir、
     commit 固定、sync 状态更新与归档清理）。后台 6 小时自动同步 worker 仍属
     可选后续增强，手动三条路由契约已通。
  7. ai 中转 3 条：ANY /ai/custom、ANY /ai/system/{channelId}/*path（流式）、
     POST /ai/models（Go custom_proxy.go 308 行 + system_proxy_stream.go 70 行）。
     **依赖栈实测（2026-09-25，最难的部分）**：除 handler 外还需移植——
     ValidateCustomRelayURL（SSRF 出站校验）、DecodeRelayOutboundHeaders/
     ApplyOutboundHeaders/ApplyDefaultOutboundHeaders、CustomRelayHTTPClient、
     AcquireCustomRelaySlot（Redis 并发槽，platform_bridge.go 226 行）、
     InterceptResponseText（response_interception.go 157 行）、outbound_alias.go
     81 行；/ai/system 系统渠道流式另有渠道授权、authorizeSystemProxy、
     ChannelAPIURLForProtocol、AcquireChannelSlot、代理计费/退款、API 调用日志
     ——合计约 1200-1700 行 Go（出站 SSRF + Redis 槽 + 代理计费三大基础设施），
     需独立完整轮次。建议顺序：先 /ai/custom + /ai/models（约 1200 行），
     /ai/system 流式（依赖渠道计费栈）单独一批。
     **进展（2026-09-25 第二次复核）**：.NET 已有 `OpenAICanvas.Outbound`
     项目（OutboundGuard.ValidateOutboundUrlAsync / ParseOutboundHeadersJson /
     NormalizeOutboundHeaders / EncodeOutboundHeadersJson +
     OutboundHttpClient.Create / ApplyHeaders）——出站栈大部分已移植。
     /ai/custom + /ai/models 剩余缺口缩小为约 600 行 C#：
     ValidateCustomRelayURL（自定义中转专用 SSRF 规则）、
     DecodeRelayOutboundHeaders（请求头内编码头解码）、
     ApplyDefaultOutboundHeaders、CustomRelayHTTPClient（无重定向工厂）、
     authorizeCustomRelay（方法/路径白名单）、AcquireCustomRelaySlot
     （内存槽位，PENDING #66 同族）、InterceptResponseText（接既有
     response-interception 设置服务）、流式密钥 REDACTED 滑动窗口。
     一个标准轮次可完成；/ai/system 流式随后单独一批。

---
**交接补充（2026-09-25，转交其他开发）**：

- 已再完成 1 条：`POST /admin/channels/{id}/models/test`（提交 0585381e /
  6231eaa2 / 35320c71：含 TaskWorkerService.RunProviderProbeAsync 公开探测入口、
  PlatformSettingsService 单例注册修复、SystemUpdateEndpoints Docker 固定状态 4 条）。
- **工作流 v2 6 条的基础设施缺口（做之前必读）**：.NET 仓储层缺整套 workflow
  方法——WorkflowTemplateVersion 读写、WorkflowInstanceForScope、WorkflowSteps、
  CreateWorkflowInstance（实例+步骤同事务）、NextWorkflowStep、UpdateWorkflowProgress、
  RegisterWorkflowTaskOutput（幂等回填：成功任务+步骤+表示唯一键）、
  ProjectWorkflowInstances、WorkflowStepForProject；实体
  WorkflowInstance/WorkflowStepInstance/WorkflowTemplateVersion/WorkflowStepTask/
  ProductionTaskLink 已在 ModelsProject.cs。Go 侧方法集中在
  `repository/project_workflow.go`（需新建同名 .NET 文件），服务层在
  `app/project_workflow.go` 679 行 + `app/project_workbench_read.go` 422 行。
  GET /projects/{id}（ProjectDetail）还依赖
  reconcileCharacterTurnaroundTasks 补偿与 TasksWithOptions(limit 100)，
  可先降级为不含补偿（旧任务产物不回填，主功能可用）。
- Eagle 5 条：服务层 app/eagle.go 410 行 + eagle_thumbnail.go 59 行
  （出站到用户 Eagle 服务器，走 OutboundGuard SSRF；file/thumbnail 为
  流式二进制转发）。
- 画布导入 2 条：handler libtv.go 86 行 + tapnow.go 36 行；服务侧入口在
  auth_bridge.go（service 域转 canvas 域），需先评估 canvas 域移植量。
- skills 安装 3 条：handler/skills.go 16/52/197 行；依赖 zip multipart 解析
  与 GitHub 出站下载（archiveFromZip/archiveFromMarkdown/normalizeSkillArchiveRoot，
  见 #65 部分解决）。
- /ai/system 流式 1 条（最难，最后做）：还需 SystemChannel/SystemChannelModel
  授权读、authorizeSystemProxy 白名单（handler/security.go 171-235 行可照移）、
  ChannelAPIURLForProtocol、ValidateChannelOutboundURL、AcquireChannelSlot
  （渠道并发，Redis→本地槽）、ReserveProxyBillingWithBody/MarkBillingRunning/
  退款三件套、EnsureChatCompletionStreamUsageRequest、API 调用日志。
  建议拆两步：先 GET /models 与非流式 POST，再补 SSE 转发与计费联动。
- 部署：全部完成后统一发布 192.168.0.211（publish linux-x64 →
  /opt/open-ai-canvas-dotnet/linux → compose build backend → up -d；
  index.html 已带 no-cache，注意本地直推用 ssh.github.com:443）。


### 70. `[部分解决]` 2026-09-25 清单切片完成状态
- 工作流 v2 六条、Eagle 六路由、skills install/github/sync 三路由均已实现并接线。
- 工作流 focused 17/17；Eagle/skills security focused 12/12；全量测试本轮 1516 通过，
  3 个既有顺序污染用例单跑均通过。
- 剩余真实能力族：画布导入 libtv/tapnow 2 条、/ai/system 流式中转（系统渠道授权/
  渠道计费/API 日志联动）。skills 自动同步 worker 作为后续增强，不阻塞手动 sync 路由。

### 71. `[部分解决]` 2026-09-25 清单批次继续
- 工作流 v2 六条、Eagle 六路由、skills install/github/sync 三路由、LibTV/TapNow 两路由均已实现、接线并通过 focused tests。
- 本轮全量 .NET 测试 **1519/1519** 通过，解决方案 build 0 errors（16 existing warnings）。
- 最后剩余能力族：`ANY /ai/system/{channelId}/*path` 系统渠道流式中转（渠道模型授权、AcquireChannelSlot、代理计费/退款、API 日志联动）；该批独立实施。

### 72. `[部分解决]` 系统渠道流式中转最小闭环（2026-09-25）
- `SystemProxyEndpoints.cs` 已接线 `ANY /ai/system/{channelId}/*path`：
  - GET `/models` 与 POST OpenAI/Claude/Gemini/MiniMax/Agnes 白名单；路径归一、模型/协议授权、query 密钥剔除、固定渠道 URL + OutboundGuard SSRF。
  - 请求/响应大小上限、渠道并发 lease、ResponseHeadersRead SSE 逐块 flush、`X-Accel-Buffering: no`、二进制/JSON 分支、上游状态与 Retry-After 透传。
  - API 调用日志已落库，敏感 URL/Key 不写入；取消/超时/上游失败均记失败状态。
- 取舍：完整 Go `ReserveProxyBillingWithBody/MarkBillingRunning/usage settlement/refund` 尚未强行伪造；本批日志 `billing_pending` 明示待核账，避免误扣或假成功。多实例渠道槽使用现有 Coordinator lease；单实例退化已记录。

### 73. `[部分解决]` PLAN 残余审计与迁移/支付批（2026-09-25）
- 真实代码批已补：Tools `migrate-schema`、`migrate-sqlite-postgres`、
  `migrate-logical-model-families`、`migrate-channel-model-price-tiers`、
  `reseed-logical-model-sources`、Docker self-contained Dockerfile/Compose、
  host-updater Docker 409 兼容；PaymentRegistry 官方 RPC 包发现/摘要校验/
  运行时注册/启停回滚。
- 专项验证：迁移/插件 22/22、支付 55/55；全量当前 1528 测试中并行运行偶发
  2 个既有 ChannelModelCatalog/ChannelOrder 顺序污染，单跑均通过。
- 仍需外部验收/决策：真实 PostgreSQL 双跑、Linux ELF 支付 provider 与商户凭证、
  云存储 Aliyun/Tencent/Qiniu SDK 或 REST 签名、Worker timeline ffmpeg/whisper
  执行器与媒体落盘/RouteAttempt、Redis 实集成、Go/.NET 双跑比较口径、系统代理
  ReserveProxyBilling/token usage settlement（当前 billing_pending，不伪造账单）。
- 文档过期项：阶段 1.12 EF 配置、5.8 后端数据导出、9.4 host-updater、
  10.3/10.4/10.6 install/sync/import、10.7/10.8 “未实现”描述已按实际代码更新。
