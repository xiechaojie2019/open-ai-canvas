#!/usr/bin/env python3
"""从 Go schema 导出（cmd/schema-dump 的 JSON）生成 .NET 8 实体层与 EF Core 配置。

用法：
    go run ./cmd/schema-dump > schema-dump.json        # 在 backend/ 下执行
    python scripts/generate-entities.py schema-dump.json

设计原则：
- 表名/列名/索引名一律取自 GORM 自身的解析结果，不重新实现命名策略。
- 每个 Go 字段名原样保留为 C# 属性名（`ID` 不改成 `Id`），便于 Go↔C# 逐字段对照。
- 显式输出 [JsonPropertyName]，不依赖命名策略，避免契约漂移。
- `json:",omitempty"` 输出 [GoOmitEmpty]；`json:"-"` 输出 [JsonIgnore]；
  `gorm:"-"` 在 EF 配置里 Ignore，保持 Domain 层不依赖 EF Core。
"""

from __future__ import annotations

import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
REPO = ROOT.parent
GO_MODEL_DIR = REPO / "backend" / "internal" / "model"

DOMAIN_ENTITIES = ROOT / "src" / "OpenAICanvas.Domain" / "Entities"

# Go 内建/外部类型 → C# 类型
#
# 注意 int：Go 的 int 在 amd64/arm64 上是 64 位，GORM 的 PostgreSQL dialector
# 对 schema.Int 一律输出 bigint（见 gorm.io/driver/postgres 的 DataTypeOf）。
# 若映射成 C# int（32 位），EF 会生成 integer，与 Go 侧的 bigint 不一致，
# 因此这里统一映射为 long。JSON 契约不受影响（两边都是数字）。
SCALAR_MAP = {
    "string": "string",
    "int": "long",
    "int32": "int",
    "int64": "long",
    "uint": "uint",
    "uint32": "uint",
    "uint64": "ulong",
    "bool": "bool",
    "float32": "float",
    "float64": "double",
    "time.Time": "DateTime",
    "gorm.DeletedAt": "DateTime?",
    "[]byte": "byte[]",
    "json.RawMessage": "string",
    "map[string]string": "Dictionary<string, string>",
    "map[string]interface {}": "Dictionary<string, JsonElement>",
}

# 值类型：加 ? 需要 Nullable<T>
VALUE_TYPES = {
    "int", "long", "uint", "ulong", "bool", "float", "double", "DateTime",
}


def go_model_file_map() -> dict[str, str]:
    """扫描 internal/model/*.go，得到 struct 名 → 源文件名。"""
    mapping: dict[str, str] = {}
    pattern = re.compile(r"^type ([A-Za-z0-9_]+) struct", re.M)
    for path in sorted(GO_MODEL_DIR.glob("*.go")):
        if path.name.endswith("_test.go"):
            continue
        text = path.read_text(encoding="utf-8")
        for name in pattern.findall(text):
            mapping[name] = path.name
    return mapping


def cs_type(go_type: str) -> str:
    pointer = go_type.startswith("*")
    base = go_type.lstrip("*")

    if base in SCALAR_MAP:
        result = SCALAR_MAP[base]
    elif base.startswith("[]model."):
        result = f"List<{base[len('[]model.'):]}>"
    elif base.startswith("model."):
        # Go 的自定义字符串枚举类型：C# 侧用 string 精确对应，常量集中放在 Enumerations.cs。
        result = "string"
    else:
        raise ValueError(f"未知 Go 类型：{go_type}")

    if pointer:
        if result.endswith("?"):
            return result
        if result in VALUE_TYPES:
            return result + "?"
        return result + "?"
    return result


def group_name(struct_name: str, file_map: dict[str, str]) -> str:
    """按 Go 源文件分组，产出 C# 文件名。"""
    if struct_name in STRUCT_RENAMES:
        return "SchemaMigrations"
    file_name = file_map.get(struct_name)
    if file_name is None:
        raise ValueError(f"找不到 {struct_name} 的源文件")
    stem = file_name[:-3]  # 去掉 .go
    return "".join(part.capitalize() for part in stem.split("_"))


# Go 里是包私有结构（internal/database/migrations.go），C# 侧提升为公开实体。
STRUCT_RENAMES = {
    "schemaMigration": "SchemaMigration",
}


def cs_struct_name(struct_name: str) -> str:
    return STRUCT_RENAMES.get(struct_name, struct_name)


# 与 BCL 类型同名的实体需要文件级别名消歧：Go 的 Task vs System.Threading.Tasks.Task。
ALIASED_STRUCTS = {
    "Task": "TaskEntity",
}


def cs_ref(struct_name: str) -> str:
    """在类型引用位置使用的名字。"""
    return ALIASED_STRUCTS.get(struct_name, struct_name)


def literal(value: str, cs: str) -> str:
    """把 GORM 的默认值字符串转成 C# 字面量。"""
    if cs == "string":
        return '"' + value.replace("\\", "\\\\").replace('"', '\\"') + '"'
    if cs == "bool":
        return "true" if value == "true" else "false"
    if cs in ("int", "long", "uint", "ulong"):
        return value
    if cs in ("float", "double"):
        return value
    return '"' + value + '"'


def gorm_explicit_type(gorm_tag: str) -> str | None:
    """从 gorm tag 中提取显式的 type:xxx 声明。"""
    if not gorm_tag:
        return None
    for part in gorm_tag.split(";"):
        part = part.strip()
        if part.startswith("type:"):
            value = part[len("type:"):].strip()
            return value or None
    return None


def default_initializer(cs: str, go_type: str) -> str:
    """给出与 Go 零值一致的属性初始化。

    Go 零值：string=""、数值=0、bool=false、nil 切片/映射=null（JSON 输出 null 或被 omitempty 省略）。
    """
    if go_type == "gorm.DeletedAt":
        return ""  # 可空，默认 null
    if go_type.startswith("*"):
        return ""
    if cs == "string":
        return " = string.Empty;"
    if cs.startswith("List<") or cs.startswith("Dictionary<"):
        # Go 的 nil 切片/映射序列化为 null，不能用空集合初始化。
        return " = null!;"
    return ""


def build_entity_block(table: dict, file_map: dict[str, str]) -> tuple[str, str, str]:
    """返回 (实体类代码, EF 配置代码, 组名)。实体类代码不含文件头。"""
    struct = cs_struct_name(table["goName"])
    group = group_name(table["goName"], file_map)
    table_name = table["table"]
    source_file = "internal/database/migrations.go" if struct == "SchemaMigration" \
        else f"internal/model/{file_map[table['goName']]}"

    entity_lines: list[str] = []

    columns = [c for c in table["columns"] if c["isColumn"]]
    transient = [c for c in table["columns"] if not c["isColumn"]]

    for column in columns:
        cs = cs_type(column["goType"])
        if column["goType"] == "gorm.DeletedAt":
            has_soft_delete = True

        doc = f'数据库列 <c>{column["name"]}</c>'
        details = []
        if column["size"]:
            details.append(f"maxLength={column['size']}")
        if column["primaryKey"]:
            details.append("主键")
        if column["notNull"]:
            details.append("not null")
        if details:
            doc += "（" + "，".join(details) + "）"

        entity_lines.append(f"    /// <summary>{doc}</summary>")
        if column["jsonIgnored"]:
            entity_lines.append("    [JsonIgnore]")
        else:
            entity_lines.append(f'    [JsonPropertyName("{column["jsonName"]}")]')
            if column["jsonOmitEmpty"]:
                entity_lines.append("    [GoOmitEmpty]")
        initializer = default_initializer(cs, column["goType"])
        entity_lines.append(f'    public {cs} {column["goName"]} {{ get; set; }}{initializer}')
        entity_lines.append("")

        # EF 配置

    for field in transient:
        cs = cs_type(field["goType"])
        entity_lines.append('    /// <summary>瞬态字段（Go <c>gorm:"-"</c>）：不建列，但参与 JSON 序列化。</summary>')
        if field["jsonIgnored"]:
            entity_lines.append("    [JsonIgnore]")
        else:
            entity_lines.append(f'    [JsonPropertyName("{field["jsonName"]}")]')
            if field["jsonOmitEmpty"]:
                entity_lines.append("    [GoOmitEmpty]")
        initializer = default_initializer(cs, field["goType"])
        entity_lines.append(f'    public {cs} {field["goName"]} {{ get; set; }}{initializer}')
        entity_lines.append("")

    entity = f"""/// <summary>
/// 对应 Go <c>{table['goName']}</c>，数据库表 <c>{table_name}</c>。
/// 源文件：{source_file}
/// </summary>
public class {struct}
{{
{chr(10).join(entity_lines).rstrip()}
}}
"""

    ref = cs_ref(struct)
    return entity, group


def index_property(table: dict, column_name: str) -> str | None:
    for column in table["columns"]:
        if column["isColumn"] and column["name"] == column_name:
            return column["goName"]
    return None


def generate_enumerations() -> tuple[int, list[str]]:
    """从 Go 的 `type X string` + const 块生成 C# 常量类。

    Go 的自定义字符串类型在 JSON 里就是普通字符串，因此 C# 属性用 string，
    常量集中放在静态类里，保持值集合与 Go 完全一致。
    """
    type_pattern = re.compile(r"^type ([A-Za-z0-9_]+) string$", re.M)
    # 形如：TaskStatusQueued TaskStatus = "queued"
    const_pattern = re.compile(
        r"^\s*([A-Za-z0-9_]+)\s+([A-Za-z0-9_]+)\s*=\s*\"([^\"]*)\"\s*$", re.M)

    types: set[str] = set()
    constants: dict[str, list[tuple[str, str]]] = {}
    missing: list[str] = []

    for path in sorted(GO_MODEL_DIR.glob("*.go")):
        if path.name.endswith("_test.go"):
            continue
        text = path.read_text(encoding="utf-8")
        declared = type_pattern.findall(text)
        types.update(declared)
        declared_set = set(declared)
        for name, owner, value in const_pattern.findall(text):
            if owner in declared_set:
                constants.setdefault(owner, []).append((name, value))

    # 未解析到字面量常量的类型（例如在别处用表达式定义）需要人工确认。
    for name in sorted(types):
        if name not in constants:
            missing.append(name)

    lines: list[str] = []
    for name in sorted(types):
        entries = constants.get(name, [])
        lines.append(f"/// <summary>对应 Go <c>{name}</c>。JSON 中即为字符串字面量。</summary>")
        lines.append(f"public static class {name}")
        lines.append("{")
        for const_name, value in entries:
            escaped = value.replace("\\", "\\\\").replace('"', '\\"')
            lines.append(f'    public const string {const_name} = "{escaped}";')
        if not entries:
            lines.append("    // 该类型未在 model 包内以字符串字面量定义常量，请人工核对。")
        lines.append("}")
        lines.append("")

    header = """// <auto-generated />
// 由 scripts/generate-entities.py 生成，请勿手工编辑。
// 对应 Go: internal/model/*.go 的 `type X string` 与 const 块。
namespace OpenAICanvas.Domain.Entities;

"""
    target = DOMAIN_ENTITIES / "Enumerations.cs"
    target.write_text(header + "\n".join(lines).rstrip() + "\n", encoding="utf-8")
    return len(types), missing



def generate_entity_metadata(tables: list[dict]) -> int:
    """生成实体属性 → 数据库列的映射表。

    Dapper 不做命名推断，而实体属性名保留了 Go 字段名（如 ID / MetadataJSON），
    与列名（id / metadata_json）不是简单的大小写关系，因此必须显式给出映射。
    由基线生成可以保证与建表脚本永远一致。
    """
    blocks: list[str] = []
    for table in sorted(tables, key=lambda t: t["table"]):
        struct = cs_ref(cs_struct_name(table["goName"]))
        columns = [c for c in table["columns"] if c["isColumn"]]
        keys = [c for c in columns if c["primaryKey"]]
        key = keys[0]["goName"] if len(keys) == 1 else None

        rows = ",\n".join(
            f'            new ColumnMap("{c["goName"]}", "{c["name"]}")' for c in columns)
        blocks.append(
            f'        [typeof({struct})] = new EntityMap(\n'
            f'            "{table["table"]}",\n'
            f'            {("\"" + key + "\"") if key else "null"},\n'
            f'            new ColumnMap[]\n'
            f'            {{\n{rows},\n'
            f'            }}),')

    text = f"""// <auto-generated />
// 由 scripts/generate-entities.py 从 schema-dump.json 生成，请勿手工编辑。
#nullable enable
using System.Collections.Generic;
using OpenAICanvas.Domain.Entities;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;

namespace OpenAICanvas.Persistence;

/// <summary>属性名 → 数据库列名的映射。</summary>
public sealed record ColumnMap(string Property, string Column);

/// <summary>单个实体的持久化元数据。</summary>
public sealed record EntityMap(string Table, string? KeyProperty, IReadOnlyList<ColumnMap> Columns)
{{
    /// <summary>按属性名取列名；不存在时返回 null。</summary>
    public string? ColumnOf(string property)
    {{
        foreach (ColumnMap map in Columns)
        {{
            if (map.Property == property)
            {{
                return map.Column;
            }}
        }}

        return null;
    }}
}}

/// <summary>
/// 全部实体的列映射。供 INSERT / UPDATE 语句构造使用，保证 SQL 列名与建表脚本一致。
/// </summary>
public static class EntityMetadata
{{
    private static readonly Dictionary<System.Type, EntityMap> Maps = new()
    {{
{chr(10).join(blocks)}
    }};

    public static EntityMap For(System.Type entityType) =>
        Maps.TryGetValue(entityType, out EntityMap? map)
            ? map
            : throw new System.InvalidOperationException($"没有实体元数据：{{entityType.Name}}");

    public static EntityMap For<T>() => For(typeof(T));

    public static IReadOnlyCollection<System.Type> KnownTypes => Maps.Keys;
}}
"""
    target = ROOT / "src" / "OpenAICanvas.Persistence" / "EntityMetadata.cs"
    target.write_text(text, encoding="utf-8")
    return len(tables)


# Go 侧由原始 SQL 创建、因而不在模型导出里的索引。
# 来源：internal/database/schema.go 与 migrations.go 的 tx.Exec("CREATE ... INDEX ...")。
RAW_SQL_INDEXES = [
    {
        "table": "schema_migrations",
        "name": "idx_schema_migrations_applied_at",
        "unique": False,
        "columns": ["applied_at"],
        "expression": None,
        "where": "",
    },
    {
        "table": "project_asset_candidates",
        "name": "idx_project_asset_candidates_pending_identity",
        "unique": True,
        "columns": ["project_id", "category", "name_key"],
        "expression": None,
        "where": "status = 'pending_confirmation' AND name_key <> ''",
    },
    {
        "table": "logical_models",
        "name": "idx_logical_model_source_active",
        "unique": True,
        "columns": [],
        "expression": "source_channel_model_id",
        "where": "source_channel_model_id <> '' AND archived_at IS NULL",
    },
    {
        "table": "users",
        "name": "idx_users_email_nonempty",
        "unique": True,
        "columns": [],
        "expression": "lower(email)",
        "where": "email <> ''",
    },
]


def sql_literal(value: str, go_type: str) -> str:
    """把 GORM 的默认值渲染成 SQL 字面量。"""
    base = go_type.lstrip("*")
    if base == "bool":
        return value
    if base in ("int", "int32", "int64", "uint", "uint32", "uint64", "float32", "float64"):
        return value
    return "'" + value.replace("'", "''") + "'"


def generate_schema_ddl(tables: list[dict], provider: str) -> str:
    """生成建表与建索引脚本，列类型取 GORM 各 dialector 的真实输出。"""
    type_key = "sqliteType" if provider == "sqlite" else "postgresType"
    lines: list[str] = []
    lines.append("-- 由 scripts/generate-entities.py 从 schema-dump.json 生成，请勿手工编辑。")
    lines.append(f"-- 目标数据库：{provider}")
    lines.append(f"-- 列类型取自 GORM 的 {provider} dialector DataTypeOf，")
    lines.append("-- 保证与 Go 侧 AutoMigrate 产出的物理结构一致。")
    lines.append("")

    index_statements: list[str] = []

    for table in sorted(tables, key=lambda t: t["table"]):
        table_name = table["table"]
        columns = [c for c in table["columns"] if c["isColumn"]]
        lines.append(f'CREATE TABLE IF NOT EXISTS "{table_name}" (')

        body: list[str] = []
        inline_primary: list[str] = []
        for column in columns:
            sql_type = column[type_key]
            piece = f'    "{column["name"]}" {sql_type}'

            # SQLite 的自增主键类型里已经带了 PRIMARY KEY，不能再补 NOT NULL 或表级主键。
            if "PRIMARY KEY" in sql_type.upper():
                inline_primary.append(column["name"])
            elif column["notNull"] or column["primaryKey"]:
                piece += " NOT NULL"

            if column["hasDefault"] and column["defaultValue"] != "<nil>":
                piece += f' DEFAULT {sql_literal(column["defaultValue"], column["goType"])}'

            body.append(piece)

        table_keys = [c["name"] for c in columns if c["primaryKey"] and c["name"] not in inline_primary]
        if table_keys:
            joined = ", ".join(f'"{k}"' for k in table_keys)
            body.append(f"    PRIMARY KEY ({joined})")

        lines.append(",\n".join(body))
        lines.append(");")
        lines.append("")

        for index in table["indexes"]:
            target = ", ".join(f'"{c}"' for c in index["columns"])
            unique = "UNIQUE " if index["unique"] else ""
            statement = f'CREATE {unique}INDEX IF NOT EXISTS "{index["name"]}" ON "{table_name}" ({target})'
            if index.get("where"):
                statement += f' WHERE {index["where"]}'
            index_statements.append(statement + ";")

    for index in RAW_SQL_INDEXES:
        target = index["expression"] if index["expression"] else ", ".join(f'"{c}"' for c in index["columns"])
        unique = "UNIQUE " if index["unique"] else ""
        statement = f'CREATE {unique}INDEX IF NOT EXISTS "{index["name"]}" ON "{index["table"]}" ({target})'
        if index["where"]:
            statement += f' WHERE {index["where"]}'
        index_statements.append(statement + ";")

    lines.append("-- 索引")
    lines.extend(index_statements)
    lines.append("")
    return "\n".join(lines)


def main() -> int:
    if len(sys.argv) < 2:
        print("用法：generate-entities.py <schema-dump.json>", file=sys.stderr)
        return 1

    tables = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
    file_map = go_model_file_map()

    entities_by_group: dict[str, list[str]] = {}
    order: list[str] = []

    for table in tables:
        entity, group = build_entity_block(table, file_map)
        if group not in entities_by_group:
            entities_by_group[group] = []
            order.append(group)
        entities_by_group[group].append(entity)

    DOMAIN_ENTITIES.mkdir(parents=True, exist_ok=True)

    entity_usings = """using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Serialization;
"""

    for group in order:
        entity_text = (
            "// <auto-generated />\n"
            "// 由 scripts/generate-entities.py 从 cmd/schema-dump 的权威导出生成，请勿手工编辑。\n"
            "// 重新生成：cd backend && go run ./cmd/schema-dump > ../backend-dotnet/schema-dump.json\n"
            "//           cd ../backend-dotnet && python scripts/generate-entities.py schema-dump.json\n"
            "#nullable enable\n"
            + entity_usings
            + "\nnamespace OpenAICanvas.Domain.Entities;\n\n"
            + "\n".join(entities_by_group[group])
        )
        (DOMAIN_ENTITIES / f"{group}.cs").write_text(entity_text, encoding="utf-8")



    total_columns = sum(1 for t in tables for c in t["columns"] if c["isColumn"])
    total_transient = sum(1 for t in tables for c in t["columns"] if not c["isColumn"])
    total_indexes = sum(len(t["indexes"]) for t in tables)
    print(f"已生成 {len(tables)} 个实体，分 {len(order)} 组")
    print(f"  列 {total_columns}，瞬态字段 {total_transient}，索引 {total_indexes}")
    print("  分组：" + ", ".join(order))

    enum_count, missing = generate_enumerations()
    print(f"已生成 {enum_count} 个常量类 → Entities/Enumerations.cs")
    if missing:
        print("  以下类型未解析到字符串常量，需人工核对：" + ", ".join(missing))

    schema_dir = ROOT / "src" / "OpenAICanvas.Persistence" / "Schema"
    schema_dir.mkdir(parents=True, exist_ok=True)
    for provider, file_name in (("sqlite", "SqliteSchema.sql"), ("postgres", "PostgresSchema.sql")):
        (schema_dir / file_name).write_text(
            generate_schema_ddl(tables, provider), encoding="utf-8")

    metadata_count = generate_entity_metadata(tables)
    print(f"已生成实体列映射 → Persistence/EntityMetadata.cs（{metadata_count} 个实体）")

    model_indexes = sum(len(t["indexes"]) for t in tables)
    print(f"已生成建表脚本 → Persistence/Schema/SqliteSchema.sql、PostgresSchema.sql")
    print(f"  表 {len(tables)}，模型索引 {model_indexes}，原始 SQL 索引 {len(RAW_SQL_INDEXES)}，"
          f"索引合计 {model_indexes + len(RAW_SQL_INDEXES)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
