FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
ARG TARGETARCH
WORKDIR /Source

# copy csproj and restore as distinct layers
#
# THIS LIST MUST COVER EVERY PROJECT IN Source/ACE.sln. `dotnet restore` below runs against the
# solution, so it resolves EVERY referenced .csproj - a project present in the .sln but missing
# here fails the build with a bare "exit code: 1" from `dotnet restore`, which does not name the
# missing project in the deploy log. This has broken the image four times now (ACE.Content.Tools
# + ACE.Database.LoadTest, then ACE.Dashboard, then ACE.Database.LoadTest.Tests, then
# ACE.Content.Tools.Tests), each time because a commit added a project to the .sln and did not
# touch this file. The build-test workflow now runs a "Dockerfile covers every ACE.sln project"
# step that fails the PR instead of the deploy - if you change this list, keep that check green.
COPY ./Source/*.sln ./
COPY ./Source/ACE.Adapter/*.csproj ./ACE.Adapter/
COPY ./Source/ACE.Common/*.csproj ./ACE.Common/
COPY ./Source/ACE.Content.Tools/*.csproj ./ACE.Content.Tools/
COPY ./Source/ACE.Content.Tools.Tests/*.csproj ./ACE.Content.Tools.Tests/
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
#
# aspnet, not runtime: ACE.Server hosts the Market's Kestrel API in-process
# (Docs/Market/DESIGN.md 4.1) and takes a FrameworkReference on Microsoft.AspNetCore.App, which
# the plain runtime image does not carry. The aspnet image is a strict superset. Same tag the
# dashboard already uses (Source/ACE.Dashboard/Dockerfile).
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble
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

# add the healthcheck script AFTER the app copy, so it cannot be clobbered by /ace's contents.
COPY deploy/healthcheck.sh /ace/healthcheck.sh

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
# Both the world-watchdog thread (the writer, in C#) and healthcheck.sh (the reader, below)
# read this same variable for the heartbeat file path, so they cannot drift out of sync.
ENV ACE_HEARTBEAT_FILE=/tmp/ace-world-heartbeat
# Invoked via `sh`, not as a bare path: core.filemode is false on Windows checkouts, so a
# newly added .sh lands mode 100644 in the image and a bare `CMD /ace/healthcheck.sh` fails
# with "permission denied" rather than running.
# --start-period raised 10m -> 20m: the check now requires an actual world tick (not just a
# bound port), and boot must run schema migrations, apply Content/**.sql and precache before
# the world thread even starts ticking - 10m was sized for the old port-only check and is not
# generous enough for that startup sequence.
HEALTHCHECK --start-period=20m --interval=15s --timeout=5s --retries=3 \
  CMD sh /ace/healthcheck.sh
