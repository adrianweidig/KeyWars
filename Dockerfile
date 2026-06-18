FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY NuGet.config Directory.Build.props KeyWars.slnx ./
COPY src/KeyWars/KeyWars.csproj src/KeyWars/
COPY tests/KeyWars.UnitTests/KeyWars.UnitTests.csproj tests/KeyWars.UnitTests/
COPY tests/KeyWars.IntegrationTests/KeyWars.IntegrationTests.csproj tests/KeyWars.IntegrationTests/
COPY tests/KeyWars.ConcurrencyTests/KeyWars.ConcurrencyTests.csproj tests/KeyWars.ConcurrencyTests/
COPY tests/KeyWars.E2ETests/KeyWars.E2ETests.csproj tests/KeyWars.E2ETests/
COPY tools/KeyWars.LoadTest/KeyWars.LoadTest.csproj tools/KeyWars.LoadTest/
RUN dotnet restore
COPY . .
RUN dotnet publish src/KeyWars/KeyWars.csproj -c Release -o /app/publish --no-restore -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ARG VERSION=0.1.0
ARG REVISION=local
ARG CREATED=unknown
LABEL org.opencontainers.image.title="KeyWars" \
      org.opencontainers.image.description="Deutschsprachige selbst gehostete Tipptraining- und Mehrspieler-Webanwendung" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${REVISION}" \
      org.opencontainers.image.created="${CREATED}" \
      org.opencontainers.image.source="https://github.com/theheadless/keywars" \
      org.opencontainers.image.licenses="MIT"
WORKDIR /app
RUN mkdir -p /data/dataprotection-keys /data/backups && chown -R app:app /data
COPY --from=build /app/publish .
USER app
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080 \
    KEYWARS__DATA__DIRECTORY=/data
ENTRYPOINT ["dotnet", "KeyWars.dll"]
