# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first for layer caching
COPY global.json ./
COPY backend/shared/BuildingBlocks/BuildingBlocks.csproj backend/shared/BuildingBlocks/
COPY backend/shared/Domain/Domain.csproj backend/shared/Domain/
COPY backend/shared/Application/Application.csproj backend/shared/Application/
COPY backend/shared/Infrastructure/Infrastructure.csproj backend/shared/Infrastructure/
COPY backend/consumer-api/ConsumerApi/ConsumerApi.csproj backend/consumer-api/ConsumerApi/
RUN dotnet restore backend/consumer-api/ConsumerApi/ConsumerApi.csproj

COPY backend/ backend/
RUN dotnet publish backend/consumer-api/ConsumerApi/ConsumerApi.csproj -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build --chown=app:app /app/publish .

# LocalDiskFileStorageService (the local-disk fallback used when Supabase
# Storage isn't configured) creates this directory eagerly on startup - the
# base image's WORKDIR is root-owned, so without this the non-root `app`
# user below gets UnauthorizedAccessException before the process ever binds
# a port. Pre-create and own it so `Directory.CreateDirectory` is a no-op.
RUN mkdir -p /app/App_Data/uploads && chown -R app:app /app/App_Data

# Run as non-root (user provided by the base image)
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "ConsumerApi.dll"]
