# Build from the repository root:
# docker build -f src/PocketBridge.Relay/Dockerfile -t pocketbridge-relay .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY Directory.Build.props ./
COPY src/PocketBridge.Core/ src/PocketBridge.Core/
COPY src/PocketBridge.Relay/ src/PocketBridge.Relay/
RUN dotnet publish src/PocketBridge.Relay/PocketBridge.Relay.csproj -c Release -o /app --nologo

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "PocketBridge.Relay.dll"]
