namespace OpenAICanvas.Tools;

using OpenAICanvas.Tools.Commands;

/// <summary>
/// 一次性迁移 / 运维命令入口。对应 Go 的 <c>cmd/migrate-*</c>、<c>cmd/payment-*</c>、
/// <c>cmd/host-updater</c>，在阶段 12 逐个补齐。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        try
        {
            switch (args[0])
            {
                case "schema-ddl":
                    return SchemaDdlCommand.Run(args);
                default:
                    Console.Error.WriteLine($"尚未实现的命令：{args[0]}");
                    PrintUsage();
                    return 1;
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"{args[0]} 执行失败：{error.Message}");
            return 1;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("用法：OpenAICanvas.Tools <command> [options]");
        Console.WriteLine();
        Console.WriteLine("已实现：");
        Console.WriteLine("  schema-ddl [sqlite|postgres] [输出路径]   输出建表脚本");
        Console.WriteLine();
        Console.WriteLine("计划支持（对应 Go 版 cmd/）：");
        Console.WriteLine("  migrate-schema                     数据库结构迁移");
        Console.WriteLine("  migrate-sqlite-postgres            SQLite → PostgreSQL 数据迁移");
        Console.WriteLine("  migrate-logical-model-families     逻辑模型族迁移");
        Console.WriteLine("  migrate-channel-model-price-tiers  渠道模型价格档迁移");
        Console.WriteLine("  reseed-logical-model-sources       逻辑模型来源重播种");
        Console.WriteLine("  payment-alipay                     支付宝支付回调处理");
        Console.WriteLine("  payment-wechat                     微信支付回调处理");
        Console.WriteLine("  host-updater                       宿主更新器");
    }
}
