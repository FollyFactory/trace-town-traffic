# Build
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

# Restore against the manifests alone, so a source-only change does not
# invalidate the package layer.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/TraceTown.Traffic/TraceTown.Traffic.csproj src/TraceTown.Traffic/
RUN dotnet restore src/TraceTown.Traffic/TraceTown.Traffic.csproj

COPY src/ src/
COPY examples/ examples/
RUN dotnet publish src/TraceTown.Traffic/TraceTown.Traffic.csproj \
      -c Release \
      -o /app \
      --no-restore

# Run
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app

# Nothing here needs root.
RUN addgroup -S traffic && adduser -S traffic -G traffic
COPY --from=build --chown=traffic:traffic /app .
USER traffic

# The control API. Bound to loopback by default — set TRAFFIC_CONTROL_HOST to
# 0.0.0.0 to reach it from outside the container.
EXPOSE 8080

ENTRYPOINT ["./trace-town-traffic"]
CMD ["examples/ecommerce.json"]
