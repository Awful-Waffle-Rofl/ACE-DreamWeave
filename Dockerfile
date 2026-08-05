FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
ARG TARGETARCH
WORKDIR /Source

# copy csproj and restore as distinct layers
#
# THIS LIST MUST COVER EVERY PROJECT IN Source/ACE.sln. `dotnet restore` below runs against the
# solution, so it resolves EVERY referenced .csproj - a project present in the .sln but missing
# here fails the build with a bare "exit code: 1" from `dotnet restore`, which does not name the
# missing project in the deploy log. This has broken the image three times now (ACE.Content.Tools
# + ACE.Database.LoadTest, then ACE.Dashboard, then ACE.Database.LoadTest.Tests), each time
# because a commit added a project to the .sln and did not touch this file.
# Check with:  git show HEAD:Source/ACE.sln | grep -c '\.csproj'   against the COPY count below.
COPY ./Source/*.sln ./
COPY ./Source/ACE.Adapter/*.csproj ./ACE.Adapter/
COPY ./Source/ACE.Common/*.csproj ./ACE.Common/
COPY ./Source/ACE.Content.Tools/*.csproj ./ACE.Content.Tools/
COPY ./Source/ACE.Dashboard/*.csproj ./ACE.Dashboard/
COPY ./Source/ACE.Database/*.csproj ./ACE.Database/
COPY ./Source/ACE.Database.LoadTest/*.csproj ./ACE.Database.LoadTest/
COPY ./Source/ACE.Database.LoadTest.Tests/*.csproj ./ACE.Database.LoadTest.Tests/
COPY ./Source/ACE.Database.Tests/*.csproj ./ACE.Database.Tests/
COPY ./Source/ACE.DatLoader/*.csproj ./ACE.DatLoader/
COPY ./Source/ACE.DatLoader.Tests/*.csproj ./ACE.DatLoader.Tests/
COPY ./Source/ACE.Entity/*.csproj ./ACE.Entity/
COPY ./Source/ACE.Server/*.csproj ./ACE.Server/
COPY ./Source/ACE.Server.Tests/*.csproj ./ACE.Server.Tests/

RUN dotnet restore -a $TARGETARCH

# copy and publish app and libraries
COPY . ../.
RUN dotnet publish ./ACE.Server/ACE.Server.csproj -a $TARGETARCH -c release -o /ace --no-restore

# final stage/image
FROM mcr.microsoft.com/dotnet/runtime:10.0-noble
ARG DEBIAN_FRONTEND="noninteractive"
WORKDIR /ace

# install net-tools (netstat for health check) & cleanup
RUN apt-get update && \
    apt-get install --no-install-recommends -y \
    net-tools && \
    apt-get clean && \
    rm -rf \
    /tmp/* \
    /var/lib/apt/lists/* \
    /var/tmp/*

# add app from build
COPY --from=build /ace .

# run app
ENTRYPOINT ["dotnet", "ACE.Server.dll"]

# ports and volumes
EXPOSE 9000-9001/udp
VOLUME /ace/Config /ace/Content /ace/Dats /ace/Logs /ace/Mods

# health check
# The port the server binds is Config.js's Server.Network.Port, which is not 9000 in every
# environment (stage runs on 9002 so it can share a host with prod). Each environment sets
# ACE_HEALTHCHECK_PORT in its docker.env; the default matches Config.js.example.
ENV ACE_HEALTHCHECK_PORT=9000
HEALTHCHECK --start-period=10m --interval=15s --timeout=5s --retries=3 \
  CMD netstat -an | grep -q ":${ACE_HEALTHCHECK_PORT}\b" || exit 1
