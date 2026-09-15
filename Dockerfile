
# ============================================================
# Build
# ============================================================

FROM mcr.microsoft.com/dotnet/sdk:10.0.400-noble AS build

WORKDIR /src

COPY . .

RUN dotnet restore src/RegtrackInsights/RegtrackInsights.csproj

RUN dotnet publish src/RegtrackInsights/RegtrackInsights.csproj \
    -c Release \
    -o /app/publish \
    --no-restore


# ============================================================
# Runtime
# ============================================================

FROM mcr.microsoft.com/playwright/dotnet:v1.62.0-noble AS runtime

WORKDIR /app

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "RegtrackInsights.dll"]

