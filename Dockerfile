# syntax=docker/dockerfile:1

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json ./
COPY src/YtDownloader/YtDownloader.csproj src/YtDownloader/
RUN dotnet restore src/YtDownloader/YtDownloader.csproj
COPY src/ src/
RUN dotnet publish src/YtDownloader/YtDownloader.csproj -c Release -o /app --no-restore

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENV TEMP_DIR=/tmp/ytdl \
    DOTNET_gcServer=0
# Railway sets PORT at runtime; 8080 is the image default for local runs.
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "YtDownloader.dll"]
