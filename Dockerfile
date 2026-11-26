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
# PLAYWRIGHT
# ============================================================

# Install Playwright CLI in SDK image
RUN dotnet tool install --global Microsoft.Playwright.CLI

ENV PATH="$PATH:/root/.dotnet/tools"

# Install Chromium
RUN playwright install chromium


# ============================================================
# RUNTIME
# RegInsights targets .NET 8
# ============================================================

FROM mcr.microsoft.com/dotnet/aspnet:8.0-noble AS runtime

WORKDIR /app

COPY --from=build /app/publish .

# Copy environment-specific appsettings downloaded by GitHub Actions
COPY appsettings.json /app/appsettings.json

# ============================================================
# Playwright / Chromium runtime dependencies
# ============================================================

# The public Ubuntu mirrors (archive/security.ubuntu.com) are slow or time out
# from GitHub-hosted runners on some days - on 2026-11-23 this step ran for 20+
# minutes and the build had to be cancelled (normally ~5 s). GitHub runners live
# in Azure, so use Azure's Ubuntu mirror with retries and per-request timeouts,
# fall back to the stock mirrors if the Azure mirror is unreachable, and restore
# the original sources afterwards so the image's apt config is unchanged.
# Package list is unchanged.
RUN set -eux; \
    SRC=/etc/apt/sources.list.d/ubuntu.sources; \
    APT_OPTS="-o Acquire::Retries=5 -o Acquire::http::Timeout=30"; \
    cp "$SRC" /tmp/ubuntu.sources.orig; \
    sed -i -e 's|http://archive.ubuntu.com|http://azure.archive.ubuntu.com|g' \
           -e 's|http://security.ubuntu.com|http://azure.archive.ubuntu.com|g' "$SRC"; \
    if ! apt-get $APT_OPTS update --error-on=any; then \
        echo "Azure Ubuntu mirror failed - falling back to the default mirrors"; \
        cp /tmp/ubuntu.sources.orig "$SRC"; \
        apt-get $APT_OPTS update; \
    fi; \
    apt-get $APT_OPTS install -y --no-install-recommends \
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
    ; \
    cp /tmp/ubuntu.sources.orig "$SRC"; \
    rm -f /tmp/ubuntu.sources.orig; \
    rm -rf /var/lib/apt/lists/*

# Copy Playwright browsers from build image
# Playwright's Chromium lives outside /root so the non-root runtime user can
# reach it (/root is 0700); PLAYWRIGHT_BROWSERS_PATH tells Microsoft.Playwright
# where to look.
COPY --from=build /root/.cache/ms-playwright /ms-playwright
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright

# SECURITY (CodeAnt IaC CKV_DOCKER_3 / CKV_K8S_23): run as the aspnet image's
# built-in unprivileged `app` user (moved to UID/GID 10001 below). /app is handed to it because
# the worker's optional outputs (FreeDigest preview / capture dirs, tile-QA
# screenshots) default to paths relative to the working directory.
# The `app` user is moved to uid/gid 10001 so it cannot collide with a host account on the node
# (CodeAnt CKV_K8S_40); the k8s manifests pin runAsUser/runAsGroup 10001 to match.
RUN groupmod -g 10001 app \
    && usermod -u 10001 -g 10001 app \
    && chown -R app:app /app /home/app
USER app

# Container-level health check for `docker run` (CodeAnt CKV_DOCKER_2). Kubernetes ignores it and
# uses the probes in k8s/deployment.*.yaml. No curl in the image, and /health/* may require
# X-Health-Token, so this only checks that Kestrel accepts connections on :8080 (bash /dev/tcp).
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=3 \
  CMD bash -c 'exec 3<>/dev/tcp/127.0.0.1/8080' || exit 1


# ============================================================
# START APPLICATION
# ============================================================

ENTRYPOINT ["dotnet", "RegtrackInsights.dll"]
