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

### 17. `[待移植]` 阶段 12 部署与验收
- `migrate-schema` / `migrate-sqlite-postgres` 等工具、Dockerfile / Compose 对齐、
  SQLite 与 PostgreSQL 双跑互认、329 条路由全量比对（当前 54/329）。

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

### 27. `[待移植]` 创建项目不生成默认工作流
- 位置：`ProjectService.CreateProjectAsync`
- 现状：Go 创建项目后调用 `createProjectWorkflow(project.ID, "", "project")` 生成默认工作流，
  失败则回滚项目记录。C# 因工作流引擎（阶段 6.7）未移植，创建时不生成；revision 仍 +1 保持递增语义。
- 影响：`/projects/:id` 的 workflows 数组在两版间不同（C# 恒为空），详情聚合接口暂未开放，
  影响面暂不可见；但阶段 6 工作台读视图接入前必须补齐。
- 处置建议：阶段 6.7（工作流模板与实例）移植时补默认工作流生成与回滚逻辑。

### 28. `[待移植]` 公告配图上传与 OSS/外观/响应拦截设置
- 位置：`MapAnnouncementRoutes` 的 `/admin/announcement-images`（POST）、
  `/admin/settings/{oss,appearance,response-interception,ark-private-assets}`
- 现状：公告配图的**草稿消费/丢弃/下发**已完整移植；上传（multipart + 图片嗅探）
  依赖资源上传链路；OSS/外观/响应拦截/ARK 设置依赖云 SDK 与外观资源存储。
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

## 处置记录

| 日期 | 条目 | 结论 |
| --- | --- | --- |
| （待维护者填写） | | |
