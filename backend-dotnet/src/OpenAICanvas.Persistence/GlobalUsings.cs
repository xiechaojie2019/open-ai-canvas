// 全局别名：Go 的 Task 实体（表 tasks）与 BCL 的 System.Threading.Tasks.Task 同名。
//
// 规则：
//   - 裸写 Task        → System.Threading.Tasks.Task（异步方法返回值）
//   - 实体引用写 TaskEntity → OpenAICanvas.Domain.Entities.Task
//
// 生成器产出的实体文件与 EntityMetadata 已统一使用 TaskEntity 别名，
// 这里把裸 Task 固定到 BCL，避免每个文件都要重复声明。
global using Task = System.Threading.Tasks.Task;
