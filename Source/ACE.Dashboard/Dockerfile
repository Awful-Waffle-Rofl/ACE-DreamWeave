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
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "ACE.Dashboard.dll"]
