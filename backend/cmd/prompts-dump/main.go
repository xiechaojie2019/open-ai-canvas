// prompts-dump 导出内置提示词模板定义，供 .NET 侧生成等价数据。
// 这是一次性工具，生成完成后连同 internal/prompts/export_tmp.go 一起删除。
package main

import (
	"encoding/json"
	"fmt"
	"os"

	"infinite-canvas/backend/internal/prompts"
)

type variableOut struct {
	Label       string `json:"label"`
	Placeholder string `json:"placeholder"`
}

type definitionOut struct {
	Operation      string        `json:"operation"`
	Label          string        `json:"label"`
	Category       string        `json:"category"`
	Description    string        `json:"description"`
	OutputType     string        `json:"outputType"`
	SchemaKey      string        `json:"schemaKey"`
	Variables      []variableOut `json:"variables"`
	DefaultContent string        `json:"defaultContent"`
	OutputContract string        `json:"outputContract"`
}

func main() {
	definitions := prompts.ExportDefaultDefinitions()
	out := make([]definitionOut, 0, len(definitions))
	for _, definition := range definitions {
		variables := make([]variableOut, 0, len(definition.Variables))
		for _, variable := range definition.Variables {
			variables = append(variables, variableOut{Label: variable.Label, Placeholder: variable.Placeholder})
		}
		out = append(out, definitionOut{
			Operation:      definition.Operation,
			Label:          definition.Label,
			Category:       definition.Category,
			Description:    definition.Description,
			OutputType:     definition.OutputType,
			SchemaKey:      definition.SchemaKey,
			Variables:      variables,
			DefaultContent: definition.DefaultContent,
			OutputContract: prompts.ExportPromptOutputContract(definition.Operation),
		})
	}

	payload := map[string]any{
		"definitions": out,
		"legacyPreamble": prompts.ExportLegacyStoryboardVideoPreamble(),
	}

	encoder := json.NewEncoder(os.Stdout)
	encoder.SetEscapeHTML(false)
	encoder.SetIndent("", "  ")
	if err := encoder.Encode(payload); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
}
