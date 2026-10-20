# Multi-stage build: a Node stage compiles the web interface, the SDK image builds and publishes the host plus
# every plugin into per-plugin directories, and the ASP.NET runtime image carries the final headless server.
FROM node:22-bookworm-slim AS webui
WORKDIR /src/src/Plugins/BubbleFighter.Fantasy.WebUi/client
COPY src/Plugins/BubbleFighter.Fantasy.WebUi/client/package.json src/Plugins/BubbleFighter.Fantasy.WebUi/client/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY src/Plugins/BubbleFighter.Fantasy.WebUi/client/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
COPY --from=webui /src/src/Plugins/BubbleFighter.Fantasy.WebUi/wwwroot ./src/Plugins/BubbleFighter.Fantasy.WebUi/wwwroot
RUN dotnet publish src/BubbleFighter.Fantasy.Host/BubbleFighter.Fantasy.Host.csproj -c Release -o /out/host --no-self-contained -p:SkipWebUiClientBuild=true

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
RUN useradd --system --uid 1001 bff && mkdir /app/state && chown bff /app/state
COPY --from=build /out/host ./
COPY profiles ./profiles
USER bff
EXPOSE 7100 7101 8080
# Without arguments the host runs first-run setup, then the installation it saved under /app/state.
ENTRYPOINT ["dotnet", "BubbleFighter.Fantasy.Host.dll"]
CMD ["start", "--state-dir", "/app/state"]
