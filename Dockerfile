FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:548d93f8a18a1acbe6cc127bc4f47281430d34a9e35c18afa80a8d6741c2adc3 AS build
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
COPY tools/KeyWars.LoadTest/KeyWars.LoadTest.csproj tools/KeyWars.LoadTest/
COPY tools/KeyWars.LoadTest/packages.lock.json tools/KeyWars.LoadTest/
RUN dotnet restore --locked-mode
COPY . .
RUN dotnet publish src/KeyWars/KeyWars.csproj -c Release -o /app/publish --no-restore -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:7644f992230d35cf230017189d4038c0ae0f7388b13f4f7ae1900a155bafb597 AS runtime
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
    && apt-get install -y --no-install-recommends ca-certificates libldap2 libsasl2-2 \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /data/dataprotection-keys /data/backups \
    && chown -R app:app /data
COPY --from=build /app/publish .
USER app
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080 \
    KEYWARS__DATA__DIRECTORY=/data
ENTRYPOINT ["dotnet", "KeyWars.dll"]
