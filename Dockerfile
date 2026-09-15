# ============================================================
# BUILD
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
# RUNTIME
# RegInsights targets .NET 8
# ============================================================

FROM mcr.microsoft.com/dotnet/aspnet:8.0-noble AS runtime

WORKDIR /app

COPY --from=build /app/publish .

# Playwright / Chromium runtime dependencies
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
        ca-certificates \
        libnss3 \
        libatk1.0-0 \
        libatk-bridge2.0-0 \
        libcups2 \
        libdrm2 \
        libdbus-1-3 \
        libxkbcommon0 \
        libatspi2.0-0 \
        libxcomposite1 \
        libxdamage1 \
        libxfixes3 \
        libxrandr2 \
        libgbm1 \
        libasound2t64 \
        libpango-1.0-0 \
        libcairo2 \
        libgtk-3-0 \
        libglib2.0-0 \
        fonts-liberation \
    && rm -rf /var/lib/apt/lists/*

ENTRYPOINT ["dotnet", "RegtrackInsights.dll"]
