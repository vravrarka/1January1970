# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/Inventory.Gateway/Inventory.Gateway.csproj src/Inventory.Gateway/
RUN dotnet restore src/Inventory.Gateway/Inventory.Gateway.csproj
COPY src/Inventory.Gateway/ src/Inventory.Gateway/
RUN dotnet publish src/Inventory.Gateway/Inventory.Gateway.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
# Каталог для TLS-сертификата шлюза; владелец — непривилегированный пользователь приложения.
RUN mkdir -p /https && chown $APP_UID /https
EXPOSE 8080 8443
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "Inventory.Gateway.dll"]
