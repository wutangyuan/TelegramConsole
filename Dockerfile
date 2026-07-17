FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY TelegramConsole.Core/TelegramConsole.Core.csproj TelegramConsole.Core/
COPY TelegramConsole.Infrastructure/TelegramConsole.Infrastructure.csproj TelegramConsole.Infrastructure/
COPY TelegramConsole.Runtime/TelegramConsole.Runtime.csproj TelegramConsole.Runtime/
COPY TelegramConsole.Web/TelegramConsole.Web.csproj TelegramConsole.Web/
RUN dotnet restore TelegramConsole.Web/TelegramConsole.Web.csproj
COPY TelegramConsole.Core/ TelegramConsole.Core/
COPY TelegramConsole.Infrastructure/ TelegramConsole.Infrastructure/
COPY TelegramConsole.Runtime/ TelegramConsole.Runtime/
COPY TelegramConsole.Web/ TelegramConsole.Web/
RUN dotnet publish TelegramConsole.Web/TelegramConsole.Web.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
USER root
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ca-certificates tzdata \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build --chown=app:app /app/publish .
RUN mkdir -p /app/data && chown app:app /app/data
USER app
ENV ASPNETCORE_URLS=http://+:8080 \
    TELEGRAMCONSOLE_DATA_DIR=/app/data \
    TZ=Asia/Shanghai \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
VOLUME ["/app/data"]
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
  CMD curl --fail --silent http://127.0.0.1:8080/health/live || exit 1
ENTRYPOINT ["dotnet", "TelegramConsole.Web.dll"]

