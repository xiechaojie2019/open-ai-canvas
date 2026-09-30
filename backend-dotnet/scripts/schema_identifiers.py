#!/usr/bin/env python3
"""物理标识符命名规则的唯一来源：snake_case 表名/列名折算为 camelCase，以及例外表。

为什么单独成模块：`generate-entities.py`（产出 EntityMetadata 与实体注释）和
`rename-schema-identifiers.py`（产出 v16 改名计划）必须对"哪个名字改成什么"给出
完全一致的答案。规则一旦分开维护，就会出现生成器把某表写成 camelCase、改名计划
却把它排除在外的静默错配——运行时表现为实体元数据指向不存在的表/列。

例外：迁移账本 `schema_migrations` 与其列 `applied_at` 保持原名。迁移链自身在改名
前后都要读写它，改名会引出"账本在迁移执行到一半时改名"的顺序问题，收益不足。
"""

from __future__ import annotations

LEDGER_TABLE = "schema_migrations"
LEDGER_COLUMNS = {"applied_at"}


def camel(name: str) -> str:
    """snake_case 转 camelCase；没有下划线时原样返回。"""
    parts = name.split("_")
    return parts[0] + "".join(part[:1].upper() + part[1:] for part in parts[1:])


def physical_table(table: str) -> str:
    """表在迁移 v16 之后的物理名。"""
    if table == LEDGER_TABLE:
        return table
    return camel(table)


def physical_column(table: str, column: str) -> str:
    """列在迁移 v16 之后的物理名。"""
    if table == LEDGER_TABLE or column in LEDGER_COLUMNS:
        return column
    return camel(column)
