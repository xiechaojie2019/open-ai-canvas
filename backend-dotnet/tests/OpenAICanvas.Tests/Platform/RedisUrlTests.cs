#nullable enable
using OpenAICanvas.Platform;
using Xunit;

namespace OpenAICanvas.Tests.Platform;

public sealed class RedisUrlTests
{
    [Theory]
    [InlineData("redis://redis:6379/0", "redis:6379", false, 0)]
    [InlineData("redis://redis:6379", "redis:6379", false, 0)]
    [InlineData("redis://localhost", "localhost:6379", false, 0)]
    [InlineData("rediss://user:secret@example.com:6380/2", "example.com:6380", true, 2)]
    public void 解析_redis_scheme(string url, string endpoint, bool ssl, int db)
    {
        var options = Coordinator.BuildConnectionOptions(url);
        Assert.Equal(endpoint, options.EndPoints[0].ToString().Split('/')[^1]);
        Assert.Equal(ssl, options.Ssl);
        Assert.Equal(db, options.DefaultDatabase);
    }

    [Fact]
    public void 解析_userinfo_密码()
    {
        var options = Coordinator.BuildConnectionOptions("redis://:mypwd@host:6379/1");
        Assert.Equal("default", options.User);
        Assert.Equal("mypwd", options.Password);
    }

    [Fact]
    public void 无_scheme_仍走原生解析()
    {
        var options = Coordinator.BuildConnectionOptions("redis:6379,abortConnect=false");
        Assert.Equal("redis:6379", options.EndPoints[0].ToString().Split('/')[^1]);
    }

    [Theory]
    [InlineData("redis:///0")]
    [InlineData("redis://host/abc")]
    public void 非法_url_报错(string url)
    {
        Assert.ThrowsAny<Exception>(() => Coordinator.BuildConnectionOptions(url));
    }
}
