# syntax=docker/dockerfile:1.7

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY backend-dotnet/ ./backend-dotnet/
COPY backend/internal/handler/openapi.yaml ./backend/internal/handler/openapi.yaml
RUN dotnet restore backend-dotnet/OpenAICanvas.sln
RUN dotnet publish backend-dotnet/src/OpenAICanvas.Web/OpenAICanvas.Web.csproj \
    -c Release -r linux-x64 --self-contained true \
    -o /out/web /p:DebugType=None /p:DebugSymbols=false
RUN dotnet publish backend-dotnet/src/OpenAICanvas.Tools/OpenAICanvas.Tools.csproj \
    -c Release -r linux-x64 --self-contained true \
    -o /out/tools /p:DebugType=None /p:DebugSymbols=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl tzdata \
    && rm -rf /var/lib/apt/lists/* \
    && groupadd --system app \
    && useradd --system --gid app --home-dir /app --shell /usr/sbin/nologin app \
    && mkdir -p /app /data /run/open-ai-canvas-updater \
    && chown -R app:app /app /data /run/open-ai-canvas-updater

WORKDIR /app
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
ENV CANVAS_BACKEND_ADDR=:8080
ENV CANVAS_BACKEND_DATA_DIR=/data

COPY --from=build /out/web/ /app/web/
COPY --from=build /out/tools/ /app/tools/
RUN chmod +x /app/web/OpenAICanvas.Web /app/tools/OpenAICanvas.Tools

USER app
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=5 \
    CMD curl -fsS http://127.0.0.1:8080/api/health/ready >/dev/null || exit 1

CMD ["/app/web/OpenAICanvas.Web"]
