# Scheduler web app: Blazor UI, MCP endpoint and background workers in one image.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, from project files only, so the layer is reused until a dependency changes.
COPY global.json Directory.Build.props ./
COPY src/Scheduler.Core/Scheduler.Core.csproj src/Scheduler.Core/
COPY src/Scheduler.Data/Scheduler.Data.csproj src/Scheduler.Data/
COPY src/Scheduler.ServiceDefaults/Scheduler.ServiceDefaults.csproj src/Scheduler.ServiceDefaults/
COPY src/Scheduler.Web/Scheduler.Web.csproj src/Scheduler.Web/
RUN dotnet restore src/Scheduler.Web/Scheduler.Web.csproj

COPY src/ src/
RUN dotnet publish src/Scheduler.Web/Scheduler.Web.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Scheduler.Web.dll"]
