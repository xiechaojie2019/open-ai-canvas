#nullable enable
using System.Runtime.CompilerServices;

// 工作流协议的纯函数（字段推断 / nodeInfo 组装）保持 internal，
// 契约测试直接断言这些函数的返回结构，端到端流程走 public RunAsync。
[assembly: InternalsVisibleTo("OpenAICanvas.Tests")]
// RunningHub 管理代理（4.12）复用字段收集/归一的 internal 纯函数。
[assembly: InternalsVisibleTo("OpenAICanvas.Application")]
