# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/Inventory.Shared/Inventory.Shared.csproj src/Inventory.Shared/
COPY src/Inventory.DeviceService/Inventory.DeviceService.csproj src/Inventory.DeviceService/
RUN dotnet restore src/Inventory.DeviceService/Inventory.DeviceService.csproj
COPY src/Inventory.Shared/ src/Inventory.Shared/
COPY src/Inventory.DeviceService/ src/Inventory.DeviceService/
RUN dotnet publish src/Inventory.DeviceService/Inventory.DeviceService.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
EXPOSE 8080
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "Inventory.DeviceService.dll"]
