# Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY octo/octo.csproj octo/

# Tests run separately in CI; the image only needs the application dependencies.
RUN dotnet restore octo/octo.csproj

COPY octo/ octo/

RUN dotnet publish octo/octo.csproj -c Release --no-restore -p:UseAppHost=false -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app

# Continuous Subsonic Radio normalizes mixed FLAC/M4A sources into one stable
# MP3 response inside the core Octo process. This is a runtime dependency, not a
# Radio sidecar or service boundary.
# fpcalc (libchromaprint-tools) is the other half of download verification: it turns a
# finished download into the Chromaprint fingerprint AcoustID is asked about. Absent, the
# feature degrades to a no-op and logs once; it never fails a download.
# Generated list covers set names in Inter (inside the app); Noto CJK and Symbola draw the
# Chinese, Japanese, Korean and emoji names Inter has no letters for, DejaVu Arabic and Hebrew.
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg fonts-dejavu-core fonts-noto-cjk fonts-symbola libchromaprint-tools \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /app/downloads

COPY --from=build /app/publish .

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "octo.dll"]
