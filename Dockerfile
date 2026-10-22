# Scheduler web app: Blazor UI, MCP endpoint and background workers in one image, plus the EF Core migration
# bundle (/migrate/efbundle) that the pre-rollout Job runs. See docs/deployment.md.
# The SDK stage runs on the building machine's own architecture and cross-compiles for the image's (TARGETARCH, from
# docker build --platform), so k8s/build.sh makes the cluster's amd64 image on an ARM machine without emulation.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

# Restore first, from project files only, so the layer is reused until a dependency changes.
COPY global.json Directory.Build.props dotnet-tools.json ./
COPY src/Scheduler.Core/Scheduler.Core.csproj src/Scheduler.Core/
COPY src/Scheduler.Data/Scheduler.Data.csproj src/Scheduler.Data/
COPY src/Scheduler.ServiceDefaults/Scheduler.ServiceDefaults.csproj src/Scheduler.ServiceDefaults/
COPY src/Scheduler.Web/Scheduler.Web.csproj src/Scheduler.Web/
RUN dotnet restore src/Scheduler.Web/Scheduler.Web.csproj -a $TARGETARCH && dotnet tool restore

COPY src/ src/
RUN dotnet publish src/Scheduler.Web/Scheduler.Web.csproj -c Release -a $TARGETARCH -o /app --no-restore

# Applies pending migrations to the database given with --connection; does nothing if there are none. Kept out of
# /app: next to the web app's assemblies, the bundle would start the web app instead. The bundle is a native
# executable, so it takes a runtime identifier (x64, not Docker's amd64).
RUN RID=linux-$([ "$TARGETARCH" = amd64 ] && echo x64 || echo "$TARGETARCH") \
    && dotnet ef migrations bundle --project src/Scheduler.Data --startup-project src/Scheduler.Data \
    --configuration Release --target-runtime $RID --output /migrate/efbundle

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app .
COPY --from=build /migrate /migrate
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Scheduler.Web.dll"]
