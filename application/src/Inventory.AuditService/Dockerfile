# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/Inventory.Shared/Inventory.Shared.csproj src/Inventory.Shared/
COPY src/Inventory.AuditService/Inventory.AuditService.csproj src/Inventory.AuditService/
RUN dotnet restore src/Inventory.AuditService/Inventory.AuditService.csproj
COPY src/Inventory.Shared/ src/Inventory.Shared/
COPY src/Inventory.AuditService/ src/Inventory.AuditService/
RUN dotnet publish src/Inventory.AuditService/Inventory.AuditService.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
EXPOSE 8080
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "Inventory.AuditService.dll"]
