# -*- coding: utf-8 -*-
"""按实际完成情况勾选 PLAN.md 的阶段状态。
一次性脚本：以行号定位阶段 3 的表格行（PLAN.md 中唯一），逐行重写。"""
import io

path = "PLAN.md"
with io.open(path, encoding="utf-8") as f:
    lines = f.readlines()

# 阶段 0/1/2 行：把行尾 "| ☐ |" 替换为 "| ✅ |"
phase012_ids = [
    "0.1", "0.2", "0.3", "0.4", "0.5", "0.6", "0.7", "0.8", "0.10", "0.11", "0.12",
    "1.1", "1.2", "1.3", "1.4", "1.5", "1.6", "1.7", "1.8", "1.9", "1.10", "1.13", "1.14",
    "2.1", "2.2", "2.3", "2.4", "2.5", "2.6", "2.7", "2.8",
]

# 整行重写映射（阶段 3/5/6/7/9/10/12 的差异化状态）
line_rewrites = {
    # 阶段 0 特殊
    "0.9": "| 0.9 | 环境变量契约 | 全量 `os.Getenv` | `CanvasEnvironment` 强类型封装（核心变量已实现，缺 20+ 见待确认 #16） | 🟡 |",
    "1.11": "| 1.11 | `AppDbContext` | `database/schema.go: Models()` | ✅ 80 实体列映射（`EntityMetadata` + `SqlBuilder`，Dapper 方案） | ✅ |",
    "1.15": "| 1.15 | 仓储层 | `repository/*.go` (40 文件) | 🟡 按需逐个迁移（当前 162/498 方法，随业务节点推进） | 🟡 |",
    # 阶段 3
    "3.1": "| 3.1 | 渠道管理 | `app/channel*.go` | 🟡 列表/创建/复制/更新/删除/排序已通；test 路由待做 |",
    "3.2": "| 3.2 | 渠道模型 + 价格档 | `app/channel_models.go` | 🟡 列表/排序/价格档附着已通；保存/删除/fetch/import/test 待做 |",
    "3.3": "| 3.3 | 逻辑模型与版本 | `app/logical_models.go` | 🟡 公开目录/管理端 CRUD/模拟/报价已通；工作流选路待做 |",
    "3.4": "| 3.4 | 模型目录发现 | `provider/registry.go` | 🟡 元数据注册表已接（内置 13 协议+插件包）；声明式执行引擎待做 |",
    "3.5": "| 3.5 | 模型能力矩阵 | `app/model_capability.go` | ✅ 读路径完成（解码/归一化/投影/校验） |",
    "3.6": "| 3.6 | 路由目录快照与健康度 | `app/model_router.go` | ✅ 快照/匹配/选路/模拟完成（Redis 协调待接） |",
    "3.7": "| 3.7 | 模型 SKU 选择器 | `model/model_sku.go` | ✅ |",
    # 阶段 5
    "5.2": "| 5.2 | 资源引用解析 | `internal/assets` | ✅ 引用收集/校验/ID 解析完成（UserDataService 内） |",
    "5.3": "| 5.3 | 资源删除与引用检查 | `app/resource_delete.go` | ✅ 判定链/Outbox/本地物理删除完成（云删除 worker 待接，#25） |",
    "5.5": "| 5.5 | 素材库 | `app/asset*.go` `repository/asset_library.go` | 🟡 全量 CRUD/分页/facets/分类/移动完成；资源上传/下发待做 |",
    "5.8": "| 5.8 | 用户数据导出/分页 | `handler/user_data.go` (31 条) | 🟡 画布 CRUD/素材/快照/分享已通（约 18 条）；上传/文件下发/OSS 待做 |",
    # 阶段 6
    "6.1": "| 6.1 | 项目 CRUD | `app/project.go` | ✅ 列表/分页/创建/更新/删除级联（默认工作流待接，#27） |",
    "6.2": "| 6.2 | 项目单元（章节/剧集） | `app/project_workflow.go` | ✅ CRUD/导入/重排（workspace 读视图待接） |",
    "6.5": "| 6.5 | 项目素材关联 | `app/project_asset.go` | 🟡 文件夹树完成；素材关联/版本/角色待做 |",
    "6.8": "| 6.8 | 风格档案 | `handler/style_profile.go` | ✅ CRUD/收藏/最近使用/归一化校验（voice-profiles 列表已通） |",
    "6.9": "| 6.9 | 功能开关门禁 | `handler/feature_availability.go` | ✅ |",
    # 阶段 7
    "7.1": "| 7.1 | 画布工程与单元 | `internal/canvas` | ✅ CRUD/同步校验/媒体守卫/配额（快照 replace 待接） |",
    "7.2": "| 7.2 | 画布能力校验 | `canvas/capability` | ✅ 媒体资产守卫（assetId+resourceID 配对校验） |",
    "7.3": "| 7.3 | 画布分享（公开 token） | `handler/canvas_share.go` + 日志脱敏 | ✅ CRUD/公开投影脱敏/资源代理（Range 待接，#26） |",
    # 阶段 9
    "9.1": "| 9.1 | 管理员审计事件 | `model.AdminAuditEvent` | ✅ 写入/分页/按目标查询 |",
    "9.2": "| 9.2 | 分析统计 | `app/analytics.go` `handler/admin_analytics.go` | 🟡 API 日志/导出/存储统计完成；overview 待活跃表写入（#20） |",
    "9.3": "| 9.3 | 存储管理 | `handler/admin_storage.go` | 🟡 存储统计完成；资源列表/删除/文件下发待做 |",
    "9.6": "| 9.6 | 系统设置 | `app/settings.go` | 🟡 注册/邮件/LinuxDO/积分/公告完成；OSS/外观/响应拦截/ARK/LibTV 待做 |",
    "9.7": "| 9.7 | 公告与已读 | `app/announcement.go` | 🟡 feed/已读/CRUD/关闭/配图草稿消费与丢弃完成；配图上传待资源链路（#28） |",
    # 阶段 10
    "10.2": "| 10.2 | 声明式协议插件 | `app/protocol_plugins.go` `protocol_registry.go` | 🟡 元数据注册表已接（内置 13 协议+插件包）；执行引擎待做 |",
    "10.5": "| 10.5 | 提示词模板与用户定制 | `internal/prompts` | 🟡 结构化画风校验/用户风格归一化完成；模板渲染与偏好待做 |",
    # 阶段 12
    "12.8": "| 12.8 | 契约回归测试 | `*_test.go` | 🟡 266 项端到端契约测试通过（随节点持续补充） |",
}

changed = 0
for index, line in enumerate(lines):
    stripped = line.strip()
    if not stripped.startswith("|"):
        continue
    parts = [part.strip() for part in stripped.split("|")]
    if len(parts) < 3:
        continue
    row_id = parts[1]
    if row_id in phase012_ids and line.rstrip().endswith("| ☐ |"):
        lines[index] = line.replace("| ☐ |", "| ✅ |")
        changed += 1
    elif row_id in line_rewrites and "| ☐ |" in line:
        prefix = f"| {row_id} |"
        lines[index] = line_rewrites[row_id] + "\n"
        changed += 1

with io.open(path, "w", encoding="utf-8", newline="") as f:
    f.writelines(lines)
print(f"updated {changed} rows")
print(f"✅ count: {sum(1 for l in lines if '✅' in l)}")
print(f"🟡 count: {sum(1 for l in lines if '🟡' in l)}")