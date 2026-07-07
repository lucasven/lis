# Build Image
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy csproj files and restore as distinct layers
COPY Directory.Build.props .
COPY Lis.Core/*.csproj Lis.Core/
COPY Lis.Persistence/*.csproj Lis.Persistence/
COPY Lis.Tools/*.csproj Lis.Tools/
COPY Lis.Agent/*.csproj Lis.Agent/
COPY Lis.Channels/*.csproj Lis.Channels/
COPY Lis.Providers/*.csproj Lis.Providers/
COPY Lis.Api/*.csproj Lis.Api/
RUN dotnet restore "Lis.Api/Lis.Api.csproj"

# Copy .editorconfig for analyzer rules
COPY .editorconfig .

# Copy and publish app
COPY Lis.Core/ Lis.Core/
COPY Lis.Persistence/ Lis.Persistence/
COPY Lis.Tools/ Lis.Tools/
COPY Lis.Agent/ Lis.Agent/
COPY Lis.Channels/ Lis.Channels/
COPY Lis.Providers/ Lis.Providers/
COPY Lis.Api/ Lis.Api/
RUN dotnet publish "Lis.Api/Lis.Api.csproj" -c Release --no-restore -o /app

# Runtime Image
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
EXPOSE 3010
WORKDIR /app

# Globalization, timezones, and Chromium for pinchtab browser automation
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    LC_ALL=pt_BR.UTF-8 \
    LANG=pt_BR.UTF-8
RUN apt-get update && apt-get install -y --no-install-recommends \
    locales tzdata bash chromium fonts-liberation \
    && sed -i '/pt_BR.UTF-8/s/^# //g' /etc/locale.gen \
    && locale-gen \
    && rm -rf /var/lib/apt/lists/*

# PinchTab binary (pinned) — skips runtime download
ARG TARGETARCH
ADD --chmod=755 https://github.com/pinchtab/pinchtab/releases/download/v0.14.0/pinchtab-linux-${TARGETARCH} /opt/pinchtab/bin/pinchtab
RUN printf 'v0.14.0' > /opt/pinchtab/bin/VERSION
ENV PINCHTAB_DATA_DIR=/opt/pinchtab \
    PINCHTAB_CHROME_PATH=/usr/bin/chromium

COPY --from=build /app .
ENTRYPOINT ["dotnet", "Lis.Api.dll"]
