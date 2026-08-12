FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:e1fc6e423f543119c406d24e2e687d67c569f18f04a37a8b0005d80ad0dcee80 AS build
ARG VERSION=0.1.0
ARG REVISION=local
ARG CREATED=unknown
WORKDIR /src
COPY NuGet.config Directory.Build.props KeyWars.slnx ./
COPY src/KeyWars/KeyWars.csproj src/KeyWars/
COPY src/KeyWars/packages.lock.json src/KeyWars/
COPY tests/KeyWars.UnitTests/KeyWars.UnitTests.csproj tests/KeyWars.UnitTests/
COPY tests/KeyWars.UnitTests/packages.lock.json tests/KeyWars.UnitTests/
COPY tests/KeyWars.IntegrationTests/KeyWars.IntegrationTests.csproj tests/KeyWars.IntegrationTests/
COPY tests/KeyWars.IntegrationTests/packages.lock.json tests/KeyWars.IntegrationTests/
COPY tests/KeyWars.ConcurrencyTests/KeyWars.ConcurrencyTests.csproj tests/KeyWars.ConcurrencyTests/
COPY tests/KeyWars.ConcurrencyTests/packages.lock.json tests/KeyWars.ConcurrencyTests/
COPY tests/KeyWars.E2ETests/KeyWars.E2ETests.csproj tests/KeyWars.E2ETests/
COPY tests/KeyWars.E2ETests/packages.lock.json tests/KeyWars.E2ETests/
COPY tests/KeyWars.WindowsUiTests/KeyWars.WindowsUiTests.csproj tests/KeyWars.WindowsUiTests/
COPY tests/KeyWars.WindowsUiTests/packages.lock.json tests/KeyWars.WindowsUiTests/
COPY tools/KeyWars.LoadTest/KeyWars.LoadTest.csproj tools/KeyWars.LoadTest/
COPY tools/KeyWars.LoadTest/packages.lock.json tools/KeyWars.LoadTest/
RUN dotnet restore --locked-mode
COPY . .
RUN dotnet publish src/KeyWars/KeyWars.csproj -c Release -o /app/publish --no-restore \
    -p:UseAppHost=false \
    -p:Version="${VERSION}" \
    -p:InformationalVersion="${VERSION}+${REVISION}" \
    -p:SourceRevisionId="${REVISION}" \
    -p:IncludeSourceRevisionInInformationalVersion=false \
    -p:ContinuousIntegrationBuild=true

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:207cc51496778557731c81ff670333d8ade4a4fec22768fd1be8e78474a84ecf AS runtime
ARG VERSION=0.1.0
ARG REVISION=local
ARG CREATED=unknown
LABEL org.opencontainers.image.title="KeyWars" \
      org.opencontainers.image.description="Deutschsprachige selbst gehostete Tipptraining- und Mehrspieler-Webanwendung" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${REVISION}" \
      org.opencontainers.image.created="${CREATED}" \
      org.opencontainers.image.source="https://github.com/adrianweidig/KeyWars" \
      org.opencontainers.image.licenses="MIT"
WORKDIR /app
# hadolint ignore=DL3008
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates libgssapi-krb5-2 libldap2 libsasl2-2 \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /data/dataprotection-keys /data/backups \
    && chown -R app:app /data
COPY --from=build /app/publish .
USER 1654
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080 \
    KEYWARS__DATA__DIRECTORY=/data
ENTRYPOINT ["dotnet", "KeyWars.dll"]
