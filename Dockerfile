# syntax=docker/dockerfile:1

# ---------- Build ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restaurar primero solo con los .csproj para aprovechar la caché de capas.
COPY src/VortexApi/VortexApi.csproj src/VortexApi/
RUN dotnet restore src/VortexApi/VortexApi.csproj

COPY src/ src/
RUN dotnet publish src/VortexApi/VortexApi.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

# ---------- Runtime ----------
# Imagen "chiseled": sin shell ni gestor de paquetes y con usuario no root.
# La variante "extra" incluye ICU y tzdata, que necesita Microsoft.Data.SqlClient.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra AS runtime
WORKDIR /app

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    # El ingress de Azure Container Apps termina TLS y envía X-Forwarded-For / X-Forwarded-Proto.
    ForwardedHeaders__Enabled=true

COPY --from=build /app/publish .

EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "VortexApi.dll"]
