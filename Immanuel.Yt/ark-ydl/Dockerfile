# ark-ydl — ASP.NET Core (.NET 10) + yt-dlp + ffmpeg + deno, in one image.
# Build context: this folder (ark-ydl/). .dockerignore keeps bin/obj/tests/App_Data out of the upload.
#
#   docker build -t ark-ydl .
#   docker run --rm -p 8080:8080 ark-ydl            → http://localhost:8080
#
# yt-dlp is baked in (checksum-verified) and then kept current by the app itself ("yt-dlp -U" on
# start and every 24 h), because YouTube changes often and a stale extractor is what breaks downloaders.

ARG DOTNET_VERSION=10.0

# ── build ───────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src
COPY src/Ark.Ydl/Ark.Ydl.csproj src/Ark.Ydl/
RUN dotnet restore src/Ark.Ydl/Ark.Ydl.csproj
COPY src/Ark.Ydl/ src/Ark.Ydl/
RUN dotnet publish src/Ark.Ydl/Ark.Ydl.csproj -c Release -o /app --no-restore /p:UseAppHost=false

# ── deno: the JavaScript runtime yt-dlp uses to solve YouTube's player challenges ──
FROM denoland/deno:bin-2.9.8 AS deno

# ── runtime ─────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}-noble AS final

RUN apt-get update \
 && apt-get install -y --no-install-recommends ffmpeg ca-certificates curl \
 && rm -rf /var/lib/apt/lists/*

COPY --from=deno /deno /usr/local/bin/deno

# Official standalone yt-dlp (bundles the EJS challenge solver). "latest" or a tag such as 2026.10.26.
ARG YTDLP_VERSION=latest
ARG TARGETARCH
RUN set -eux; \
    case "${TARGETARCH:-amd64}" in \
      amd64) asset=yt-dlp_linux ;; \
      arm64) asset=yt-dlp_linux_aarch64 ;; \
      *) echo "unsupported architecture: ${TARGETARCH}"; exit 1 ;; \
    esac; \
    if [ "${YTDLP_VERSION}" = "latest" ]; then base=https://github.com/yt-dlp/yt-dlp/releases/latest/download; \
    else base=https://github.com/yt-dlp/yt-dlp/releases/download/${YTDLP_VERSION}; fi; \
    mkdir -p /opt/ark-ydl/tools; \
    cd /opt/ark-ydl/tools; \
    curl -fsSL -o yt-dlp "${base}/${asset}"; \
    curl -fsSL -o SHA2-256SUMS "${base}/SHA2-256SUMS"; \
    echo "$(grep " ${asset}\$" SHA2-256SUMS | cut -d' ' -f1)  yt-dlp" | sha256sum -c -; \
    rm SHA2-256SUMS; \
    chmod 755 yt-dlp; \
    chown -R "${APP_UID}" /opt/ark-ydl

WORKDIR /app
COPY --from=build /app .

# Downloads go to fast local disk (not /home, which App Service may mount as slow shared storage).
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_FORWARDEDHEADERS_ENABLED=true \
    DENO_DIR=/tmp/deno \
    Ydl__DataRoot=/tmp/ark-ydl \
    Ydl__Tools__Directory=/opt/ark-ydl/tools \
    Ydl__Tools__JsRuntime=deno

EXPOSE 8080
USER $APP_UID

HEALTHCHECK --interval=30s --timeout=5s --start-period=40s --retries=3 \
  CMD curl -fsS http://localhost:8080/healthz || exit 1

ENTRYPOINT ["dotnet", "Ark.Ydl.dll"]
