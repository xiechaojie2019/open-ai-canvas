// Command schema-dump 用 GORM 自身的 schema 解析器导出权威的表名、列名、类型与索引。
//
// 用途：Go → .NET 8 迁移的契约基线。GORM 的表名/列名依赖 naming strategy
// （CamelCase→snake_case，且含 commonInitialisms 特例），手写复刻极易出错，
// 因此直接调用 GORM 解析器输出真值，供 C# 侧生成与核对。
//
// 输出 JSON 到 stdout，不连接任何数据库。
package main

import (
	"encoding/json"
	"fmt"
	"os"
	"sort"
	"strings"
	"time"

	"infinite-canvas/backend/internal/database"

	"gorm.io/driver/postgres"
	"gorm.io/driver/sqlite"
	"gorm.io/gorm"
	"gorm.io/gorm/clause"
	"gorm.io/gorm/schema"
)

// noConnDialector 提供一个不建立连接的 GORM dialector：
// 只为 schema 解析服务，数据类型映射委托给真实 sqlite dialector。
type noConnDialector struct {
	inner sqlite.Dialector
}

func (d noConnDialector) Name() string { return "sqlite" }

func (d noConnDialector) Initialize(db *gorm.DB) error { return nil }

func (d noConnDialector) Migrator(*gorm.DB) gorm.Migrator { return nil }

func (d noConnDialector) DataTypeOf(field *schema.Field) string { return d.inner.DataTypeOf(field) }

func (d noConnDialector) DefaultValueOf(field *schema.Field) clause.Expression {
	return d.inner.DefaultValueOf(field)
}

func (d noConnDialector) BindVarTo(writer clause.Writer, stmt *gorm.Statement, v interface{}) {}

func (d noConnDialector) QuoteTo(writer clause.Writer, str string) {}

func (d noConnDialector) Explain(sql string, vars ...interface{}) string { return sql }

type columnInfo struct {
	Name          string   `json:"name"`
	GoName        string   `json:"goName"`
	GoType        string   `json:"goType"`
	SQLType       string   `json:"sqlType"`
	Size          int      `json:"size"`
	PrimaryKey    bool     `json:"primaryKey"`
	AutoIncrement bool     `json:"autoIncrement"`
	NotNull       bool     `json:"notNull"`
	Unique        bool     `json:"unique"`
	HasDefault    bool     `json:"hasDefault"`
	DefaultValue  string   `json:"defaultValue"`
	Ignored       bool     `json:"ignored"`
	IndexNames    []string `json:"indexNames"`

	// GORM 各 dialector 实际会写出的 DDL 列类型。
	// EF Core 的默认类型映射与 GORM 不同（例如 EF 把带长度的 string 映射为 TEXT，
	// 而 GORM 生成 varchar(n)），必须显式对齐才能保证物理结构一致。
	SQLiteType   string `json:"sqliteType"`
	PostgresType string `json:"postgresType"`

	// JSON 序列化契约：来自 struct tag，决定 C# 侧的属性名与 omitempty 语义。
	JsonName      string `json:"jsonName"`
	JsonOmitEmpty bool   `json:"jsonOmitEmpty"`
	JsonIgnored   bool   `json:"jsonIgnored"`

	// 原始 gorm tag，用于区分 gorm:"-" 瞬态字段与关联字段（两者 DBName 都为空）。
	GormTag string `json:"gormTag"`

	// 该字段是否是数据库列。false 表示瞬态字段或关联字段，不参与建表。
	IsColumn bool `json:"isColumn"`
}

type indexInfo struct {
	Name    string   `json:"name"`
	Class   string   `json:"class"`
	Columns []string `json:"columns"`
	Unique  bool     `json:"unique"`
	Where   string   `json:"where"`
}

type tableInfo struct {
	GoName  string       `json:"goName"`
	Table   string       `json:"table"`
	Columns []columnInfo `json:"columns"`
	Indexes []indexInfo  `json:"indexes"`
}

// schemaMigration 对应 internal/database/migrations.go 里的私有结构，
// 它同样通过 AutoMigrate 建表，因此必须计入契约基线。
type schemaMigration struct {
	Version   int64     `gorm:"primaryKey"`
	Name      string    `gorm:"size:160;not null"`
	Checksum  string    `gorm:"size:96;not null"`
	AppliedAt time.Time `gorm:"not null"`
}

func main() {
	db, err := gorm.Open(noConnDialector{inner: sqlite.Dialector{DSN: ":memory:"}}, &gorm.Config{
		DisableForeignKeyConstraintWhenMigrating: false,
	})
	if err != nil {
		fail(err)
	}

	models := database.Models()
	// schema_migrations 不在 Models() 中，但确实建表，单独追加。
	models = append(models, &schemaMigration{})

	sqliteDialector := sqlite.Dialector{DSN: ":memory:"}
	postgresDialector := postgres.Dialector{}

	result := make([]tableInfo, 0, len(models))
	for _, item := range models {
		info, err := parseModel(db, sqliteDialector, postgresDialector, item)
		if err != nil {
			fail(fmt.Errorf("解析 %T：%w", item, err))
		}
		result = append(result, info)
	}

	sort.Slice(result, func(i, j int) bool { return result[i].Table < result[j].Table })

	encoder := json.NewEncoder(os.Stdout)
	encoder.SetIndent("", "  ")
	encoder.SetEscapeHTML(false)
	if err := encoder.Encode(result); err != nil {
		fail(err)
	}
}

func parseModel(db *gorm.DB, sqliteDialector sqlite.Dialector, postgresDialector postgres.Dialector, value any) (tableInfo, error) {
	stmt := &gorm.Statement{DB: db}
	if err := stmt.Parse(value); err != nil {
		return tableInfo{}, err
	}

	s := stmt.Schema
	indexes := s.ParseIndexes()

	// 每个字段参与的索引名，供 C# 侧还原复合索引。
	indexByField := map[string][]string{}
	for _, index := range indexes {
		for _, option := range index.Fields {
			if option.Field == nil {
				continue
			}
			indexByField[option.Field.DBName] = append(indexByField[option.Field.DBName], index.Name)
		}
	}

	info := tableInfo{
		GoName:  s.Name,
		Table:   s.Table,
		Columns: make([]columnInfo, 0, len(s.Fields)),
		Indexes: make([]indexInfo, 0, len(indexes)),
	}

	for _, field := range s.Fields {
		// gorm:"-:migration" 的字段不建列，但仍参与 JSON 序列化（典型是只读计算字段，
		// 如 redeem_batches 的 available_count 等子查询别名）。这类字段必须作为
		// 瞬态字段导出，否则实体层会丢掉它们，API 契约缺字段。
		isColumn := field.DBName != "" && !field.IgnoreMigration
		names := indexByField[field.DBName]
		sort.Strings(names)
		if !isColumn {
			// 瞬态字段不参与索引；同时避免对无列字段调用方言推导。
			names = nil
		}
		sqliteType := ""
		postgresType := ""
		if isColumn {
			sqliteType = sqliteDialector.DataTypeOf(field)
			postgresType = postgresDialector.DataTypeOf(field)
		}
		jsonName, jsonOmitEmpty, jsonIgnored := parseJSONTag(field.Tag.Get("json"))
		info.Columns = append(info.Columns, columnInfo{
			Name:          field.DBName,
			GoName:        field.Name,
			GoType:        field.FieldType.String(),
			SQLType:       string(field.DataType),
			Size:          field.Size,
			PrimaryKey:    field.PrimaryKey,
			AutoIncrement: field.AutoIncrement,
			NotNull:       field.NotNull,
			Unique:        field.Unique,
			HasDefault:    field.HasDefaultValue,
			DefaultValue:  fmt.Sprint(field.DefaultValueInterface),
			IndexNames:    names,
			SQLiteType:    sqliteType,
			PostgresType:  postgresType,
			JsonName:      jsonName,
			JsonOmitEmpty: jsonOmitEmpty,
			JsonIgnored:   jsonIgnored,
			GormTag:       field.Tag.Get("gorm"),
			IsColumn:      isColumn,
		})
	}

	for _, index := range indexes {
		columns := make([]string, 0, len(index.Fields))
		for _, option := range index.Fields {
			if option.Field != nil {
				columns = append(columns, option.Field.DBName)
			}
		}
		info.Indexes = append(info.Indexes, indexInfo{
			Name:    index.Name,
			Class:   index.Class,
			Columns: columns,
			Unique:  index.Class == "UNIQUE",
			Where:   index.Where,
		})
	}

	return info, nil
}

func fail(err error) {
	fmt.Fprintln(os.Stderr, "schema-dump:", err)
	os.Exit(1)
}

// parseJSONTag 拆解 encoding/json 的 struct tag，返回字段名、是否 omitempty、是否忽略。
func parseJSONTag(tag string) (name string, omitEmpty bool, ignored bool) {
	if tag == "" {
		return "", false, false
	}

	parts := strings.Split(tag, ",")
	name = parts[0]
	if name == "-" {
		// json:"-" 表示完全不参与序列化；json:"-," 表示字段名就是 "-"。
		if len(parts) > 1 {
			return "-", false, false
		}
		return "", false, true
	}

	for _, option := range parts[1:] {
		if option == "omitempty" {
			omitEmpty = true
		}
	}
	return name, omitEmpty, false
}
