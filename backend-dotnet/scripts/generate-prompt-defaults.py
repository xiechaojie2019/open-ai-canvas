"""从 Go 导出的 prompts-dump.json 生成 C# 内置提示词模板数据。

用法：
    cd backend && go run ./cmd/prompts-dump > ../backend-dotnet/prompts-dump.json
    cd ../backend-dotnet && python scripts/generate-prompt-defaults.py

为什么走「Go 导出 → 脚本生成」而不是手工翻译：
模板正文有 21KB 中文，手工复制极易出现不可见的偏差；用 Go 自己的 JSON
编码器导出可保证逐字一致。
"""

import io
import json

DATA_FILE = "prompts-dump.json"
OUT_FILE = "src/OpenAICanvas.Application/Prompts/PromptDefaults.cs"


def cs_str(value):
    """生成 C# 字符串字面量。多行内容用 raw string。"""
    if "\n" not in value and "\r" not in value:
        out = value.replace("\\", "\\\\").replace('"', '\\"')
        return '"%s"' % out
    assert '"""' not in value, "content contains triple quotes"
    assert not value.startswith("\n"), "content starts with newline"
    indented = "\n".join(("        " + line) if line else "" for line in value.split("\n"))
    return '"""\n' + indented + '\n        """'


def main():
    data = json.load(io.open(DATA_FILE, encoding="utf-8"))
    defs = data["definitions"]

    lines = [
        "#nullable enable",
        "",
        "namespace OpenAICanvas.Application.Prompts;",
        "",
        "/// <summary>",
        "/// 内置提示词模板定义。",
        "/// </summary>",
        "/// <remarks>",
        "/// <b>本文件由 scripts/generate-prompt-defaults.py 生成，请勿手工编辑。</b>",
        "/// 模板正文逐字来自 Go 的 <c>defaultPromptDefinitions()</c>，",
        "/// 由 Go 自身的 JSON 编码器导出，避免手工复制产生偏差。",
        "/// </remarks>",
        "public static class PromptDefaults",
        "{",
        "    /// <summary>",
        "    /// 分镜视频模板的历史前缀。用于识别并剥离旧版本遗留的引导语。",
        "    /// 对应 Go: <c>legacyStoryboardVideoPromptPreamble</c>。",
        "    /// </summary>",
        "    public const string LegacyStoryboardVideoPromptPreamble = %s;" % cs_str(data["legacyPreamble"]),
        "",
        "    /// <summary>全部内置操作定义。对应 Go: <c>defaultPromptDefinitions()</c>。</summary>",
        "    public static List<PromptOperationDefinition> Definitions() =>",
        "    [",
    ]

    for item in defs:
        lines.append("        new PromptOperationDefinition")
        lines.append("        {")
        lines.append("            Operation = %s," % cs_str(item["operation"]))
        lines.append("            Label = %s," % cs_str(item["label"]))
        lines.append("            Category = %s," % cs_str(item["category"]))
        lines.append("            Description = %s," % cs_str(item["description"]))
        lines.append("            OutputType = %s," % cs_str(item["outputType"]))
        lines.append("            SchemaKey = %s," % cs_str(item["schemaKey"]))
        if item["variables"]:
            lines.append("            Variables =")
            lines.append("            [")
            for var in item["variables"]:
                lines.append(
                    "                new PromptTemplateVariable { Label = %s, Placeholder = %s },"
                    % (cs_str(var["label"]), cs_str(var["placeholder"]))
                )
            lines.append("            ],")
        else:
            lines.append("            Variables = [],")
        lines.append("            OutputContract = %s," % cs_str(item["outputContract"]))
        lines.append("            DefaultContent = %s," % cs_str(item["defaultContent"]))
        lines.append("        },")

    lines.append("    ];")
    lines.append("}")

    io.open(OUT_FILE, "w", encoding="utf-8").write("\n".join(lines) + "\n")
    print("generated %s (%d definitions)" % (OUT_FILE, len(defs)))


if __name__ == "__main__":
    main()
