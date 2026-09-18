using System.Text.Json;
using Microsoft.AspNetCore.Http;
using OpenAICanvas.Web.Contracts;
using OpenAICanvas.Web.Serialization;
using OpenAICanvas.Domain.Serialization;

namespace OpenAICanvas.Web.Http;

/// <summary>直接写出统一信封的 <see cref="IResult"/>，避免 MVC 的 JSON 格式化器介入。</summary>
internal sealed class EnvelopeResult : IResult
{
    private readonly int _status;
    private readonly ApiEnvelope _body;

    internal EnvelopeResult(int status, ApiEnvelope body)
    {
        _status = status;
        _body = body;
    }

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        httpContext.Response.StatusCode = _status;
        httpContext.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(
            httpContext.Response.Body,
            _body,
            CanvasJson.WriteOptions,
            httpContext.RequestAborted);
    }
}
