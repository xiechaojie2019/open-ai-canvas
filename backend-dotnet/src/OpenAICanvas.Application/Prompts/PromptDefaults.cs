#nullable enable

namespace OpenAICanvas.Application.Prompts;

/// <summary>
/// 内置提示词模板定义。
/// </summary>
/// <remarks>
/// <b>本文件由 scripts/generate-prompt-defaults.py 生成，请勿手工编辑。</b>
/// 模板正文逐字来自 Go 的 <c>defaultPromptDefinitions()</c>，
/// 由 Go 自身的 JSON 编码器导出，避免手工复制产生偏差。
/// </remarks>
public static class PromptDefaults
{
    /// <summary>
    /// 分镜视频模板的历史前缀。用于识别并剥离旧版本遗留的引导语。
    /// 对应 Go: <c>legacyStoryboardVideoPromptPreamble</c>。
    /// </summary>
    public const string LegacyStoryboardVideoPromptPreamble = """
        生成单一连续镜头的视频执行提示词。一个镜头只保留一个叙事目标、一个主运镜和一条主要动作链；摄影机运动必须有起点、动机和停止点。优先保证角色身份、表演、关键动作和连续性，次要环境效果可以简化。


        """;

    /// <summary>全部内置操作定义。对应 Go: <c>defaultPromptDefinitions()</c>。</summary>
    public static List<PromptOperationDefinition> Definitions() =>
    [
        new PromptOperationDefinition
        {
            Operation = "chapter_assets_extract",
            Label = "章节角色、场景与道具提取",
            Category = "角色",
            Description = "从章节正文提取角色、实际发生剧情的场景和影响剧情的道具，分别进入资产待确认列表。",
            OutputType = "json",
            SchemaKey = "chapter-assets/v1",
            Variables =
            [
                new PromptTemplateVariable { Label = "项目名称", Placeholder = "{{项目名称}}" },
                new PromptTemplateVariable { Label = "章节名称", Placeholder = "{{章节名称}}" },
                new PromptTemplateVariable { Label = "项目画风", Placeholder = "{{项目画风}}" },
            ],
            OutputContract = """
        服务端固定 JSON Schema chapter-assets/v1（不可由运营模板或用户定制覆盖）：
        {
          "type": "object", "additionalProperties": false,
          "required": ["characters", "scenes", "props"],
          "properties": {
            "characters": {"$ref": "#/$defs/characterBreakdown/properties/characters"},
            "scenes": {"type": "array", "items": {"$ref": "#/$defs/asset"}},
            "props": {"type": "array", "items": {"$ref": "#/$defs/asset"}}
          },
          "$defs": {
            "asset": {
              "type": "object", "additionalProperties": false,
              "required": ["name", "description", "prompt"],
              "properties": {"name": {"type": "string"}, "description": {"type": "string"}, "prompt": {"type": "string"}}
            },
            "characterBreakdown": {
          "type": "object",
          "additionalProperties": false,
          "required": ["characters"],
          "properties": {
            "characters": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["name", "aliases", "role", "appearance", "clothing", "physique", "personality", "props", "consistencyPrompt", "multiViewPrompt", "voiceLanguage", "voiceAge", "voiceTimbre"],
                "properties": {
                  "name": {"type": "string"},
                  "aliases": {"type": "array", "items": {"type": "string"}},
                  "role": {"type": "string"},
                  "appearance": {"type": "string"},
                  "clothing": {"type": "string"},
                  "physique": {"type": "string"},
                  "personality": {"type": "string"},
                  "props": {"type": "string"},
                  "consistencyPrompt": {"type": "string"},
                  "multiViewPrompt": {"type": "string"},
                  "voiceLanguage": {"type": "string"},
                  "voiceAge": {"type": "string"},
                  "voiceTimbre": {"type": "string"}
                }
              }
            }
          }
        }
          }
        }
        """,
            DefaultContent = "你是短剧资产导演。按章节事实提取角色、场景和道具。同一身份的别名合并，不编造正文未出现的资产；未明确的视觉信息写“正文未明确”。角色包含剧情定位、稳定外貌服装体态、性格、一致性提示、三视图提示及语言、声音年龄、音色。场景描述空间布局、环境和光照；道具描述材质、形状及剧情用途。无对应资产时返回空数组。",
        },
        new PromptOperationDefinition
        {
            Operation = "storyboard_plan",
            Label = "分镜规划",
            Category = "分镜",
            Description = "把剧情、项目画风和当前角色版本规划为可执行镜头。",
            OutputType = "json",
            SchemaKey = "storyboard-plan/v3",
            Variables =
            [
                new PromptTemplateVariable { Label = "项目名称", Placeholder = "{{项目名称}}" },
                new PromptTemplateVariable { Label = "项目画风", Placeholder = "{{项目画风}}" },
                new PromptTemplateVariable { Label = "用户要求", Placeholder = "{{用户要求}}" },
            ],
            OutputContract = """
        服务端固定 JSON Schema storyboard-plan/v3（不可由运营模板或用户定制覆盖）：
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["title", "logline", "styleGuide", "characters", "locations", "shots"],
          "properties": {
            "title": {"type": "string"},
            "logline": {"type": "string"},
            "styleGuide": {"type": "string", "maxLength": 120},
            "characters": {"type": "array", "items": {"type": "string"}},
            "locations": {"type": "array", "items": {"type": "string"}},
            "shots": {
              "type": "array",
              "minItems": 1,
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["title", "description", "durationSeconds", "dialogue", "characterIds", "narrativeIntent", "viewerPOV", "performanceBlocking", "shotSize", "emotion", "lightingAndAtmosphere", "audioEffects", "visualPrompt", "videoPrompt", "camera", "motion", "timeBeats", "mustHave", "optionalDetails", "continuityOut", "negativePrompt", "assetRefs"],
                "properties": {
                  "title": {"type": "string"},
                  "description": {"type": "string"},
                  "durationSeconds": {"type": "integer", "minimum": 1, "maximum": 60},
                  "dialogue": {"type": "string"},
                  "characterIds": {"type": "array", "description": "优先填写当前角色资产 ID；尚未确认资产的角色填写角色名称", "items": {"type": "string"}},
                  "narrativeIntent": {"type": "string"},
                  "viewerPOV": {"type": "string"},
                  "performanceBlocking": {"type": "string"},
                  "shotSize": {"type": "string"},
                  "emotion": {"type": "string"},
                  "lightingAndAtmosphere": {"type": "string"},
                  "audioEffects": {"type": "string"},
                  "visualPrompt": {"type": "string"},
                  "videoPrompt": {"type": "string"},
                  "camera": {"type": "string"},
                  "motion": {"type": "string"},
                  "timeBeats": {"type": "string"},
                  "mustHave": {"type": "array", "maxItems": 3, "items": {"type": "string"}},
                  "optionalDetails": {"type": "array", "items": {"type": "string"}},
                  "continuityOut": {"type": "string"},
                  "negativePrompt": {"type": "string"},
                  "assetRefs": {
                    "type": "array",
                    "maxItems": 6,
                    "items": {
                      "type": "object",
                      "additionalProperties": false,
                      "required": ["nodeId", "role", "priority"],
                      "properties": {
                        "nodeId": {"type": "string"},
                        "role": {"type": "string", "enum": ["character", "environment", "wardrobe", "prop", "weapon", "style", "motion", "audio"]},
                        "priority": {"type": "integer", "minimum": 0, "maximum": 100}
                      }
                    }
                  }
                }
              }
            }
          }
        }
        """,
            DefaultContent = """
        你是影视分镜导演和 AI 视频提示词专家。先理解故事目标、人物动机、冲突、情绪曲线和结尾，再把剧情转译为可执行、可拍摄的连续镜头，不要把剧情段落直接改写成镜头摘要。

        每个镜头先确定观众此刻跟随谁，再明确 narrativeIntent、viewerPOV 和 performanceBlocking，然后选择景别、机位、焦段、构图和运镜。摄影机设计必须说明叙事动机，不得用术语数量代替导演判断；一个镜头只保留一个主运镜，必须有起点、动机和停止点，并为人物表演让出注意力。

        大远景只承担空间、规模和处境建立；人物对白与喜剧反应优先使用中景、近景、过肩或独立反应镜头。按叙事需要使用反应、停顿、空镜、插入和匹配剪辑，避免每镜都推进、环绕、航拍或慢动作。

        description 只写可见画面与动作；把“意识到、回忆起、感到”等不可见信息转译成眼神、停顿、手部动作、走位、道具反应或环境变化。visualPrompt 明确人物左右位置、视线、前中后景、遮挡、视觉焦点、可信光源、材质和色彩；videoPrompt 只补充主体运动、环境运动和结尾状态。

        保持角色五官、年龄、发型、服装、道具、损伤位置、空间关系和项目媒介一致。视觉媒介、光线和材质严格服从项目画风，不得默认改写为真人摄影，也不得把 3D、动画或卡通项目强制改成写实。负面要求必须针对当前镜头的换脸、服装变化、手部错误、乱码、闪烁、穿模、风格突变和动作僵硬风险。
        """,
        },
        new PromptOperationDefinition
        {
            Operation = "storyboard_repair",
            Label = "分镜修复",
            Category = "分镜",
            Description = "修复模型返回的分镜结构、字段和镜头复杂度。",
            OutputType = "json",
            SchemaKey = "storyboard-plan/v3",
            Variables =
            [
                new PromptTemplateVariable { Label = "校验错误", Placeholder = "{{校验错误}}" },
                new PromptTemplateVariable { Label = "项目画风", Placeholder = "{{项目画风}}" },
            ],
            OutputContract = """
        服务端固定 JSON Schema storyboard-plan/v3（不可由运营模板或用户定制覆盖）：
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["title", "logline", "styleGuide", "characters", "locations", "shots"],
          "properties": {
            "title": {"type": "string"},
            "logline": {"type": "string"},
            "styleGuide": {"type": "string", "maxLength": 120},
            "characters": {"type": "array", "items": {"type": "string"}},
            "locations": {"type": "array", "items": {"type": "string"}},
            "shots": {
              "type": "array",
              "minItems": 1,
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["title", "description", "durationSeconds", "dialogue", "characterIds", "narrativeIntent", "viewerPOV", "performanceBlocking", "shotSize", "emotion", "lightingAndAtmosphere", "audioEffects", "visualPrompt", "videoPrompt", "camera", "motion", "timeBeats", "mustHave", "optionalDetails", "continuityOut", "negativePrompt", "assetRefs"],
                "properties": {
                  "title": {"type": "string"},
                  "description": {"type": "string"},
                  "durationSeconds": {"type": "integer", "minimum": 1, "maximum": 60},
                  "dialogue": {"type": "string"},
                  "characterIds": {"type": "array", "description": "优先填写当前角色资产 ID；尚未确认资产的角色填写角色名称", "items": {"type": "string"}},
                  "narrativeIntent": {"type": "string"},
                  "viewerPOV": {"type": "string"},
                  "performanceBlocking": {"type": "string"},
                  "shotSize": {"type": "string"},
                  "emotion": {"type": "string"},
                  "lightingAndAtmosphere": {"type": "string"},
                  "audioEffects": {"type": "string"},
                  "visualPrompt": {"type": "string"},
                  "videoPrompt": {"type": "string"},
                  "camera": {"type": "string"},
                  "motion": {"type": "string"},
                  "timeBeats": {"type": "string"},
                  "mustHave": {"type": "array", "maxItems": 3, "items": {"type": "string"}},
                  "optionalDetails": {"type": "array", "items": {"type": "string"}},
                  "continuityOut": {"type": "string"},
                  "negativePrompt": {"type": "string"},
                  "assetRefs": {
                    "type": "array",
                    "maxItems": 6,
                    "items": {
                      "type": "object",
                      "additionalProperties": false,
                      "required": ["nodeId", "role", "priority"],
                      "properties": {
                        "nodeId": {"type": "string"},
                        "role": {"type": "string", "enum": ["character", "environment", "wardrobe", "prop", "weapon", "style", "motion", "audio"]},
                        "priority": {"type": "integer", "minimum": 0, "maximum": 100}
                      }
                    }
                  }
                }
              }
            }
          }
        }
        """,
            DefaultContent = "你是影视分镜 JSON 修复导演。修复结构时必须保留剧情信息，通过拆镜或重新分配内容解决复杂度超限，不得用删除关键剧情掩盖错误。若校验错误是台词/旁白超长，必须拆镜或精简为单镜头可念完的台词，保留关键情节，不要把超长 dialogue 原样放回。保持原项目的视觉媒介、角色身份、服装、道具和连续性，不要擅自改写画风。只修复校验错误和由此引发的镜头组织问题。",
        },
        new PromptOperationDefinition
        {
            Operation = "storyboard_first_frame",
            Label = "分镜首帧",
            Category = "生成",
            Description = "把单镜头结构转换为图片模型使用的首帧提示词。",
            OutputType = "text",
            SchemaKey = "",
            Variables =
            [
                new PromptTemplateVariable { Label = "项目视觉", Placeholder = "{{项目视觉}}" },
                new PromptTemplateVariable { Label = "首帧构图", Placeholder = "{{首帧构图}}" },
                new PromptTemplateVariable { Label = "表演起始状态", Placeholder = "{{表演起始状态}}" },
                new PromptTemplateVariable { Label = "负面要求", Placeholder = "{{负面要求}}" },
            ],
            OutputContract = "当前操作输出普通文本提示词，没有 JSON Schema。",
            DefaultContent = """
        生成单一、可执行的分镜首帧。严格继承项目视觉媒介和角色资产；画面明确主体左右位置、视线、前中后景、遮挡、视觉焦点、可信光源与材质。只描述静止首帧，不提前写后续运动，不添加画外人物、无来源光线、文字或水印。

        【项目视觉】
        {{项目视觉}}

        【首帧构图】
        {{首帧构图}}

        【表演起始状态】
        {{表演起始状态}}

        【负面要求】
        {{负面要求}}
        """,
        },
        new PromptOperationDefinition
        {
            Operation = "storyboard_video",
            Label = "分镜视频",
            Category = "生成",
            Description = "把单镜头结构转换为视频模型使用的紧凑执行提示词。",
            OutputType = "text",
            SchemaKey = "",
            Variables =
            [
                new PromptTemplateVariable { Label = "项目视觉", Placeholder = "{{项目视觉}}" },
                new PromptTemplateVariable { Label = "镜头意图", Placeholder = "{{镜头意图}}" },
                new PromptTemplateVariable { Label = "首帧构图", Placeholder = "{{首帧构图}}" },
                new PromptTemplateVariable { Label = "表演与调度", Placeholder = "{{表演与调度}}" },
                new PromptTemplateVariable { Label = "摄影机", Placeholder = "{{摄影机}}" },
                new PromptTemplateVariable { Label = "时间节拍", Placeholder = "{{时间节拍}}" },
                new PromptTemplateVariable { Label = "运动与结尾", Placeholder = "{{运动与结尾}}" },
                new PromptTemplateVariable { Label = "声音", Placeholder = "{{声音}}" },
                new PromptTemplateVariable { Label = "执行优先级", Placeholder = "{{执行优先级}}" },
                new PromptTemplateVariable { Label = "负面要求", Placeholder = "{{负面要求}}" },
            ],
            OutputContract = "当前操作输出普通文本提示词，没有 JSON Schema。",
            DefaultContent = """
        【项目视觉】
        {{项目视觉}}

        【镜头意图】
        {{镜头意图}}

        【首帧构图】
        {{首帧构图}}

        【表演与调度】
        {{表演与调度}}

        【摄影机】
        {{摄影机}}

        【时间节拍】
        {{时间节拍}}

        【运动与结尾】
        {{运动与结尾}}

        【台词与声音】
        {{声音}}

        【执行优先级】
        {{执行优先级}}

        【负面要求】
        {{负面要求}}
        """,
        },
        new PromptOperationDefinition
        {
            Operation = "character_extract",
            Label = "角色卡提取",
            Category = "角色",
            Description = "从章节正文提取需要跨镜头保持一致的角色资产。",
            OutputType = "json",
            SchemaKey = "character-breakdown/v1",
            Variables =
            [
                new PromptTemplateVariable { Label = "项目名称", Placeholder = "{{项目名称}}" },
                new PromptTemplateVariable { Label = "章节名称", Placeholder = "{{章节名称}}" },
                new PromptTemplateVariable { Label = "项目画风", Placeholder = "{{项目画风}}" },
            ],
            OutputContract = """
        服务端固定 JSON Schema character-breakdown/v1（不可由运营模板或用户定制覆盖）：
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["characters"],
          "properties": {
            "characters": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["name", "aliases", "role", "appearance", "clothing", "physique", "personality", "props", "consistencyPrompt", "multiViewPrompt", "voiceLanguage", "voiceAge", "voiceTimbre"],
                "properties": {
                  "name": {"type": "string"},
                  "aliases": {"type": "array", "items": {"type": "string"}},
                  "role": {"type": "string"},
                  "appearance": {"type": "string"},
                  "clothing": {"type": "string"},
                  "physique": {"type": "string"},
                  "personality": {"type": "string"},
                  "props": {"type": "string"},
                  "consistencyPrompt": {"type": "string"},
                  "multiViewPrompt": {"type": "string"},
                  "voiceLanguage": {"type": "string"},
                  "voiceAge": {"type": "string"},
                  "voiceTimbre": {"type": "string"}
                }
              }
            }
          }
        }
        """,
            DefaultContent = "你是短剧角色资产导演。只提取章节中实际出场、发言或对剧情产生明确作用，并且后续制作需要保持视觉或声音一致的角色。忽略系统播报、纯物件、无身份群众和没有持续角色价值的一次性路人；合并同一角色的姓名、专属称谓和别名。正文未明确的信息必须写“正文未明确”，不得自行改变人物关系、时代背景或编造编号式姓名。角色设定要具体、稳定、可用于后续三视图和视频一致性控制。",
        },
        new PromptOperationDefinition
        {
            Operation = "character_turnaround",
            Label = "角色三视图",
            Category = "角色",
            Description = "按当前角色版本和项目画风生成正、侧、背三视图。",
            OutputType = "text",
            SchemaKey = "",
            Variables =
            [
                new PromptTemplateVariable { Label = "角色名称", Placeholder = "{{角色名称}}" },
                new PromptTemplateVariable { Label = "项目画风", Placeholder = "{{项目画风}}" },
                new PromptTemplateVariable { Label = "角色设定", Placeholder = "{{角色设定}}" },
            ],
            OutputContract = "当前操作输出普通文本提示词，没有 JSON Schema。",
            DefaultContent = "制作专业人物三视图设定表。画面严格分成三个等宽竖向区域，从左到右依次为正面全身、右侧面全身、背面全身。三个视角必须是同一角色、同一服装、同一发型、同一体型和同一比例，采用站立中性姿势，完整显示头顶到脚底。背景使用纯净中性浅色和均匀设定稿光线，只负责分离轮廓，不得改变项目画风的绘画或渲染媒介。禁止文字、边框、道具说明、表情变化和额外人物。",
        },
        new PromptOperationDefinition
        {
            Operation = "short_drama_outline",
            Label = "短剧大纲",
            Category = "项目",
            Description = "根据一句话故事生成短剧标题、简介和章节正文，供项目创建导入。",
            OutputType = "json",
            SchemaKey = "short-drama-outline/v1",
            Variables =
            [
                new PromptTemplateVariable { Label = "章节数量", Placeholder = "{{章节数量}}" },
                new PromptTemplateVariable { Label = "叙事结构", Placeholder = "{{叙事结构}}" },
                new PromptTemplateVariable { Label = "每章字数", Placeholder = "{{每章字数}}" },
                new PromptTemplateVariable { Label = "叙事视角", Placeholder = "{{叙事视角}}" },
                new PromptTemplateVariable { Label = "整体基调", Placeholder = "{{整体基调}}" },
                new PromptTemplateVariable { Label = "角色规模", Placeholder = "{{角色规模}}" },
                new PromptTemplateVariable { Label = "章节篇幅", Placeholder = "{{章节篇幅}}" },
            ],
            OutputContract = """
        服务端固定 JSON Schema short-drama-outline/v1（不可由运营模板或用户定制覆盖）：
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["title", "synopsis", "chapters"],
          "properties": {
            "title": {"type": "string"},
            "synopsis": {"type": "string"},
            "chapters": {
              "type": "array",
              "minItems": 1,
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["title", "content"],
                "properties": {
                  "title": {"type": "string"},
                  "content": {"type": "string"}
                }
              }
            }
          }
        }
        只输出一个 JSON 对象，不要输出 markdown 代码块或其他文字。
        """,
            DefaultContent = "你是短剧编剧。根据用户的一句话故事，生成一部短剧的标题、一句话简介和 {{章节数量}} 个章节。生成要求：叙事采用{{叙事结构}}结构，每章约 {{每章字数}} 字，使用{{叙事视角}}视角，整体基调{{整体基调}}，主要角色约 {{角色规模}}，章节篇幅{{章节篇幅}}。",
        },
        new PromptOperationDefinition
        {
            Operation = "skill_draft",
            Label = "技能草稿",
            Category = "技能",
            Description = "根据用户想法生成可复用创作技能的名称、分类、简介和指令草稿。",
            OutputType = "json",
            SchemaKey = "skill-draft/v1",
            Variables = [],
            OutputContract = """
        服务端固定 JSON Schema skill-draft/v1（不可由运营模板或用户定制覆盖）：
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["skillName", "tag", "description", "instruction"],
          "properties": {
            "skillName": {"type": "string"},
            "tag": {"type": "string", "enum": ["drama", "ecommerce", "creative", "social", "others"]},
            "description": {"type": "string"},
            "instruction": {"type": "string"}
          }
        }
        只输出一个 JSON 对象，不要输出 markdown 代码块或其他文字。
        """,
            DefaultContent = "你是一位技能编写助手。根据用户的想法，为一个「可复用的创作技能」生成一份草稿。技能名称简短，不超过 20 个字。分类 tag 必须是 drama、ecommerce、creative、social、others 之一。简介不超过 120 字，说明适用场景、输入条件和最终产出。指令使用 Markdown，至少 300 字，写给后续在画布中使用该技能的模型阅读，必须包含角色设定、输入与约束、分步执行流程、检查清单和输出格式。工具步骤只描述所需能力、输入、输出和确认点，不虚构具体工具名，不把工具、节点、权限、预算或审批写成技能授予的能力；执行时始终以运行环境实际暴露的能力清单为准。",
        },
    ];
}
