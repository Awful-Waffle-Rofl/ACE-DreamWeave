# ACE.Dashboard image. Standalone (no ACE project references), so the build context is just
# this directory. Built + pushed to GHCR by .github/workflows/publish-dashboard.yml.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
ARG TARGETARCH
WORKDIR /src

COPY *.csproj ./
RUN dotnet restore -a $TARGETARCH

COPY . ./
RUN dotnet publish ACE.Dashboard.csproj -a $TARGETARCH -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble
WORKDIR /app
COPY --from=build /app ./

# Bind inside the container; the compose maps a host port. Config (connection string, Basic
# auth creds) comes from environment variables (Analytics__ConnectionString, Dashboard__*).
#
# This MUST override the Kestrel config key, not ASPNETCORE_URLS. appsettings.json pins
# Kestrel:Endpoints:Http:Url to http://127.0.0.1:5080 for running the app directly on a host, and
# an explicit Kestrel endpoint BEATS ASPNETCORE_URLS - the app logs "Overriding address(es) ...
# Binding to endpoints defined via IConfiguration" and binds container-loopback:5080 instead.
# Nothing then listens on 8080, so the compose port mapping resolves to a closed port and the
# container answers nothing while looking perfectly healthy. Setting the same config key via an
# environment variable wins, because env config is applied after appsettings.json.
ENV Kestrel__Endpoints__Http__Url=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "ACE.Dashboard.dll"]
