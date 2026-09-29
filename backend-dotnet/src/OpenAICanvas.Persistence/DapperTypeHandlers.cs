using System.Data;
using System.Text.Json;
using Dapper;
using OpenAICanvas.Domain.Entities;

namespace OpenAICanvas.Persistence;

/// <summary>为 GORM serializer:json 列注册 Dapper 的实体类型转换。</summary>
internal static class DapperTypeHandlers
{
    private static int _registered;

    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) != 0)
        {
            return;
        }

        SqlMapper.AddTypeHandler(new ChannelModelTagsHandler());
    }

    private sealed class ChannelModelTagsHandler : SqlMapper.TypeHandler<List<ChannelModelTag>>
    {
        public override List<ChannelModelTag> Parse(object value)
        {
            if (value is null || value is DBNull)
            {
                return [];
            }

            string json = value is string text ? text : Convert.ToString(value) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(json))
            {
                return [];
            }

            return JsonSerializer.Deserialize<List<ChannelModelTag>>(json) ?? [];
        }

        public override void SetValue(IDbDataParameter parameter, List<ChannelModelTag>? value)
        {
            parameter.Value = JsonSerializer.Serialize(value ?? []);
            parameter.DbType = DbType.String;
        }
    }
}
