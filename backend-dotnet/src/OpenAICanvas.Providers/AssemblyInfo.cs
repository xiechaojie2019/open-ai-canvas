#nullable enable
using System.Runtime.CompilerServices;

// 工作流协议的纯函数（字段推断 / nodeInfo 组装）保持 internal，
// 契约测试直接断言这些函数的返回结构，端到端流程走 public RunAsync。
[assembly: InternalsVisibleTo("OpenAICanvas.Tests")]
