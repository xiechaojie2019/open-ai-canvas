using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// 支付插件测试桩：实现 yingce.payment/v1 的 stdio 单行 JSON 协议。
//
// 行为由请求里的 config["scenario"] 控制，用于让宿主侧的测试覆盖各条错误分支。
// 默认场景返回成功响应。

const string ProtocolVersion = "yingce.payment/v1";

string raw = Console.In.ReadToEnd();
JsonNode? request;
try
{
    request = JsonNode.Parse(raw);
}
catch (JsonException)
{
    Write(new JsonObject { ["ok"] = false, ["code"] = "invalid_request", ["message"] = "请求不是有效 JSON" });
    return 0;
}

if (request is null)
{
    Write(new JsonObject { ["ok"] = false, ["code"] = "invalid_request", ["message"] = "请求不是有效 JSON" });
    return 0;
}

string version = request["version"]?.GetValue<string>() ?? "";
string operation = request["operation"]?.GetValue<string>() ?? "";
string scenario = request["config"]?["scenario"]?.GetValue<string>() ?? "ok";

// ---- 版本与操作校验（与 Go 的 rpc_runner 一致） ----
if (version != ProtocolVersion)
{
    Write(new JsonObject { ["ok"] = false, ["code"] = "unsupported_version", ["message"] = "不支持的支付插件协议版本" });
    return 0;
}

// ---- 错误场景 ----
switch (scenario)
{
    case "error":
        Write(new JsonObject { ["ok"] = false, ["code"] = "provider_error", ["message"] = "渠道拒绝该请求" });
        return 0;

    case "error-custom-code":
        Write(new JsonObject { ["ok"] = false, ["code"] = "trade_not_exist", ["message"] = "交易不存在" });
        return 0;

    case "error-no-message":
        Write(new JsonObject { ["ok"] = false, ["code"] = "silent_failure" });
        return 0;

    case "bad-json":
        Console.Out.Write("this is not json\n");
        Console.Out.Flush();
        return 0;

    case "empty":
        // 不输出任何内容，进程正常退出。
        return 0;

    case "exit-nonzero":
        Console.Error.Write("boom\n");
        Console.Error.Flush();
        return 3;

    case "huge":
    {
        // 输出超过宿主 2MB 上限的内容。
        StringBuilder builder = new();
        builder.Append("{\"ok\":true,\"data\":\"");
        while (builder.Length < (3 << 20))
        {
            builder.Append('x');
        }
        builder.Append("\"}");
        Console.Out.Write(builder.ToString());
        Console.Out.Flush();
        return 0;
    }

    case "echo-request":
    {
        // 把 request 子对象的字段名排序后拼成字符串回传，
        // 供宿主侧断言「发出的字段名与 Go 的 json tag 一致」。
        JsonNode? requestNode = request["request"];
        List<string> names = requestNode is JsonObject obj
            ? obj.Select(pair => pair.Key).OrderBy(key => key, StringComparer.Ordinal).ToList()
            : [];
        Write(new JsonObject
        {
            ["ok"] = true,
            ["data"] = new JsonObject { ["merchantOrderNo"] = string.Join(",", names) },
        });
        return 0;
    }

    case "sleep":
    {
        // 用于验证超时：睡到超过宿主 30 秒上限。
        Thread.Sleep(TimeSpan.FromSeconds(40));
        Write(new JsonObject { ["ok"] = true, ["data"] = new JsonObject() });
        return 0;
    }
}

// ---- 正常路径 ----
switch (operation)
{
    case "validate_config":
        Write(new JsonObject { ["ok"] = true });
        return 0;

    case "create_order":
        Write(new JsonObject
        {
            ["ok"] = true,
            // Go 的 Checkout 无 json tag → PascalCase。
            ["data"] = new JsonObject
            {
                ["Mode"] = "redirect",
                ["Value"] = "https://pay.example.com/checkout?id=abc",
                ["ExpiresAt"] = "2026-09-18T10:00:00Z",
            },
        });
        return 0;

    case "query_order":
    case "close_order":
        Write(new JsonObject
        {
            ["ok"] = true,
            // Go 的 Result 带 tag → camelCase。
            ["data"] = new JsonObject
            {
                ["merchantOrderNo"] = "M-1001",
                ["providerTradeNo"] = "T-2002",
                ["providerStatus"] = "TRADE_SUCCESS",
                ["amountFen"] = 1990,
                ["currency"] = "CNY",
                ["paid"] = true,
                ["closed"] = false,
                ["paidAt"] = "2026-09-18T09:30:00Z",
            },
        });
        return 0;

    case "verify_notification":
        Write(new JsonObject
        {
            ["ok"] = true,
            ["data"] = new JsonObject
            {
                ["eventId"] = "evt-1",
                // 嵌入的 Result 字段平铺到同一层。
                ["merchantOrderNo"] = "M-1001",
                ["providerTradeNo"] = "T-2002",
                ["providerStatus"] = "TRADE_SUCCESS",
                ["amountFen"] = 1990,
                ["currency"] = "CNY",
                ["paid"] = true,
                ["closed"] = false,
            },
        });
        return 0;

    case "download_trade_bill":
        Write(new JsonObject
        {
            ["ok"] = true,
            // Go 的 BillRecord 无 tag → PascalCase。
            ["data"] = new JsonArray
            {
                new JsonObject
                {
                    ["MerchantOrderNo"] = "M-1001",
                    ["ProviderTradeNo"] = "T-2002",
                    ["ProviderStatus"] = "TRADE_SUCCESS",
                    ["AmountFen"] = 1990,
                    ["Currency"] = "CNY",
                    ["PaidAt"] = "2026-09-18T09:30:00Z",
                },
            },
        });
        return 0;

    default:
        Write(new JsonObject
        {
            ["ok"] = false,
            ["code"] = "unknown_operation",
            ["message"] = $"unknown payment operation \"{operation}\"",
        });
        return 0;
}

static void Write(JsonObject payload)
{
    Console.Out.Write(payload.ToJsonString());
    Console.Out.Write('\n');
    Console.Out.Flush();
}
