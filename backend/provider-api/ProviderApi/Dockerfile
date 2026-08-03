# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore first for layer caching
COPY global.json ./
COPY backend/shared/BuildingBlocks/BuildingBlocks.csproj backend/shared/BuildingBlocks/
COPY backend/shared/Domain/Domain.csproj backend/shared/Domain/
COPY backend/shared/Application/Application.csproj backend/shared/Application/
COPY backend/shared/Infrastructure/Infrastructure.csproj backend/shared/Infrastructure/
COPY backend/provider-api/ProviderApi/ProviderApi.csproj backend/provider-api/ProviderApi/
RUN dotnet restore backend/provider-api/ProviderApi/ProviderApi.csproj

COPY backend/ backend/
RUN dotnet publish backend/provider-api/ProviderApi/ProviderApi.csproj -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# Run as non-root (user provided by the base image)
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "ProviderApi.dll"]
