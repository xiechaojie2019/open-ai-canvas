package prompts

// 供 cmd/prompts-dump 导出内置模板定义的桥接。
//
// Go 的 defaultPromptDefinitions / promptOutputContract 是包内私有的，
// 外部工具无法直接调用；这里加一层导出，让 .NET 侧能用 Go 自身的 JSON 编码器
// 生成等价数据，避免手工复制 21KB 中文模板正文时产生不可见偏差。
//
// 这些函数不参与任何业务路径，只为生成工具存在。

// ExportDefaultDefinitions 导出全部内置操作定义。
func ExportDefaultDefinitions() []PromptOperationDefinition { return defaultPromptDefinitions() }

// ExportPromptOutputContract 导出某操作的输出契约（含固定 JSON Schema）。
func ExportPromptOutputContract(operation string) string { return promptOutputContract(operation) }

// ExportLegacyStoryboardVideoPreamble 导出分镜视频模板的历史前缀。
func ExportLegacyStoryboardVideoPreamble() string { return legacyStoryboardVideoPromptPreamble }
