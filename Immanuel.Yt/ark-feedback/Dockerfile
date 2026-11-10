# ark-feedback — ASP.NET Core (.NET 10) + yt-dlp, models downloaded on first start into /data.
#
#   docker build -t ark-feedback .
#   docker run --rm -p 8080:8080 -v ark-feedback-data:/data ark-feedback     → http://localhost:8080
#
# The models (about 850 MB for every profile) are fetched from Hugging Face on first start and kept in the /data
# volume; yt-dlp is baked in and then self-updated daily by the app.

ARG DOTNET_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src
COPY src/Ark.Feedback/Ark.Feedback.csproj src/Ark.Feedback/
RUN dotnet restore src/Ark.Feedback/Ark.Feedback.csproj
COPY src/Ark.Feedback/ src/Ark.Feedback/
RUN dotnet publish src/Ark.Feedback/Ark.Feedback.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}-noble AS final
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl \
 && rm -rf /var/lib/apt/lists/*

ARG YTDLP_VERSION=latest
ARG TARGETARCH
RUN set -eux; \
    case "${TARGETARCH:-amd64}" in amd64) asset=yt-dlp_linux ;; arm64) asset=yt-dlp_linux_aarch64 ;; *) echo "unsupported architecture: ${TARGETARCH}"; exit 1 ;; esac; \
    if [ "${YTDLP_VERSION}" = "latest" ]; then base=https://github.com/yt-dlp/yt-dlp/releases/latest/download; else base=https://github.com/yt-dlp/yt-dlp/releases/download/${YTDLP_VERSION}; fi; \
    mkdir -p /opt/ark-feedback/tools; cd /opt/ark-feedback/tools; \
    curl -fsSL -o yt-dlp "${base}/${asset}"; curl -fsSL -o SHA2-256SUMS "${base}/SHA2-256SUMS"; \
    echo "$(grep " ${asset}\$" SHA2-256SUMS | cut -d' ' -f1)  yt-dlp" | sha256sum -c -; rm SHA2-256SUMS; chmod 755 yt-dlp; \
    mkdir -p /data; chown -R "${APP_UID}" /opt/ark-feedback /data

WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_FORWARDEDHEADERS_ENABLED=true \
    Feedback__DataRoot=/data \
    Feedback__Tools__Directory=/opt/ark-feedback/tools

VOLUME ["/data"]
EXPOSE 8080
USER $APP_UID

HEALTHCHECK --interval=30s --timeout=5s --start-period=120s --retries=3 CMD curl -fsS http://localhost:8080/healthz || exit 1

ENTRYPOINT ["dotnet", "Ark.Feedback.dll"]
