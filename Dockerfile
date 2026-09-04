FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

ARG BUILD_CONFIGURATION=Release

WORKDIR /source

COPY Directory.Build.props ./

COPY src/PhantomBot.Core/PhantomBot.Core.csproj \
    src/PhantomBot.Core/

COPY src/PhantomBot.Infrastructure/PhantomBot.Infrastructure.csproj \
    src/PhantomBot.Infrastructure/

COPY src/PhantomBot.Worker/PhantomBot.Worker.csproj \
    src/PhantomBot.Worker/

COPY src/PhantomBot.Baseline/PhantomBot.Baseline.csproj \
    src/PhantomBot.Baseline/

RUN dotnet restore \
    src/PhantomBot.Worker/PhantomBot.Worker.csproj

RUN dotnet restore \
    src/PhantomBot.Baseline/PhantomBot.Baseline.csproj

COPY . .

FROM build AS publish-worker

ARG BUILD_CONFIGURATION=Release

RUN dotnet publish \
    src/PhantomBot.Worker/PhantomBot.Worker.csproj \
    --configuration "$BUILD_CONFIGURATION" \
    --no-restore \
    --output /app/worker \
    /p:UseAppHost=false

FROM build AS publish-baseline

ARG BUILD_CONFIGURATION=Release

RUN dotnet publish \
    src/PhantomBot.Baseline/PhantomBot.Baseline.csproj \
    --configuration "$BUILD_CONFIGURATION" \
    --no-restore \
    --output /app/baseline \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final

WORKDIR /app

RUN mkdir -p /app/data && \
    chown "$APP_UID:$APP_UID" /app/data

COPY --from=publish-worker /app/worker ./worker
COPY --from=publish-baseline /app/baseline ./baseline

ENV DOTNET_ENVIRONMENT=Production

USER $APP_UID

ENTRYPOINT ["dotnet", "/app/worker/PhantomBot.Worker.dll"]