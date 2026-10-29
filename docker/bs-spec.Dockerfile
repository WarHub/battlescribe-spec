# THE SDK THAT BUILDS THIS IMAGE IS THE ONE global.json NAMES, AND THE BUILD READS IT FROM THERE. The
# repo pins its SDK (rollForward: latestPatch) so `AnalysisLevel=latest-recommended` +
# `TreatWarningsAsErrors` cannot be widened by a toolchain nobody chose, and Dependabot's dotnet-sdk
# updater moves that pin by rewriting global.json. So the build stage starts from runtime-deps — the
# native libraries .NET needs, and no SDK — and installs global.json's `sdk.version` itself, before
# anything else runs: looked up in Microsoft's release metadata, checked against the SHA-512 published
# there, and confirmed by asking the installed `dotnet` which SDK it resolves. An SDK bump, a patch, a
# feature band or a major, is a one-line PR to global.json, and this file follows it with no edit.
#
# IT USED TO NAME THE SDK TWICE. The build stage was `FROM` an SDK image tag that had to move by hand in
# every bump PR — Dependabot does not know this file exists — and the drift test holding the two
# together compared feature bands only. Under latestPatch that is not enough: an image in the right band
# but below the pinned patch cannot satisfy the pin either, and #559 (10.0.400 -> 10.0.402) passed that
# test and failed here with "A compatible .NET SDK was not found". Nor were the tags a complete source:
# MCR publishes SDK images for the newest feature band only, so a repo holding an older band would have
# had no tag for its later patches at all. The release metadata lists every SDK, and it is what
# Dependabot reads to offer the bump. `ToolchainPinDriftTests.DockerImages_TakeTheSdkFromGlobalJson_AndTheRuntimeFromTheTargetFramework`
# now refuses any `dotnet/sdk` image here, whatever its tag, and fails if this stage stops reading
# global.json.
#
# The .NET images below are a different decision: the runtime the app targets. They name the channel
# of the TargetFramework in Directory.Build.props (net10.0 -> 10.0), not global.json's — an SDK of a
# later major still builds net10.0, and the app then needs a 10.0 runtime to start — and the same test
# holds them to it. The floating channel tag is what keeps the runtime's patches coming without a bot.
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS build
SHELL ["/bin/bash", "-euo", "pipefail", "-c"]

# What the official SDK image sets for a build, so this one behaves as it did there: no first-run
# banner, no HTTPS development certificate nobody uses, and no XML docs unpacked from packages.
ENV DOTNET_GENERATE_ASPNET_CERTIFICATE=false \
    DOTNET_NOLOGO=true \
    NUGET_XMLDOC_MODE=skip

# curl and jq exist to install the SDK below. They stay in this stage: the published one starts from
# aspnet and copies only the app out of here.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl jq \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /src

# The SDK, from global.json and nothing else. This layer is keyed on global.json, so it is rebuilt when
# the pin moves and reused otherwise.
#
# - The version is global.json's `sdk.version`, exactly. latestPatch would also accept a later patch in
#   the band — CI's setup-dotnet installs the newest one — but an image built twice from one commit
#   should get one SDK, and the pinned version is the only one the commit names.
# - The archive's URL and SHA-512 come from the channel's releases.json, the record Microsoft publishes
#   for every SDK, older feature bands included. Nothing here runs a downloaded installer script, and
#   nothing is unpacked before its hash matches.
# - The last check asks the installed `dotnet`, from beside global.json, which SDK it resolves. It is
#   the question `dotnet restore` would otherwise answer first, with "A compatible .NET SDK was not
#   found".
COPY global.json .
RUN version="$(jq -er '.sdk.version' global.json)"; \
    channel="$(cut -d. -f1,2 <<<"$version")"; \
    case "$(uname -m)" in \
        x86_64) rid=linux-x64 ;; \
        aarch64) rid=linux-arm64 ;; \
        armv7l) rid=linux-arm ;; \
        *) echo "No .NET SDK build is mapped for $(uname -m): add its RID beside the others in docker/bs-spec.Dockerfile." >&2; exit 1 ;; \
    esac; \
    metadata="https://builds.dotnet.microsoft.com/dotnet/release-metadata/$channel/releases.json"; \
    if ! curl -fsSL --retry 3 -o /tmp/releases.json "$metadata"; then \
        echo "global.json pins .NET SDK $version, and its channel's release metadata could not be read: $metadata" >&2; \
        exit 1; \
    fi; \
    if ! sdk="$(jq -ec --arg v "$version" --arg name "dotnet-sdk-$rid.tar.gz" \
            'first(.releases[].sdks[]? | select(.version == $v) | .files[] | select(.name == $name))' /tmp/releases.json)"; then \
        echo "global.json pins .NET SDK $version, and $metadata lists no $rid build of it." >&2; \
        exit 1; \
    fi; \
    curl -fsSL --retry 3 -o /tmp/dotnet-sdk.tar.gz "$(jq -r .url <<<"$sdk")"; \
    if ! sha512sum --check --strict --quiet <<<"$(jq -r .hash <<<"$sdk")  /tmp/dotnet-sdk.tar.gz"; then \
        echo "The .NET SDK $version archive does not match the SHA-512 $metadata publishes for it; nothing was installed." >&2; \
        exit 1; \
    fi; \
    mkdir -p /usr/share/dotnet; \
    tar -oxzf /tmp/dotnet-sdk.tar.gz -C /usr/share/dotnet; \
    ln -s /usr/share/dotnet/dotnet /usr/bin/dotnet; \
    rm /tmp/dotnet-sdk.tar.gz /tmp/releases.json; \
    resolved="$(dotnet --version)"; \
    if [ "$resolved" != "$version" ]; then \
        echo "global.json pins .NET SDK $version, and the SDK installed for it resolves as $resolved." >&2; \
        exit 1; \
    fi; \
    echo "Installed .NET SDK $resolved ($rid), as global.json pins."

# Copy solution + build props for restore
COPY BattleScribeSpec.slnx .
COPY Directory.Build.props .
COPY Directory.Packages.props .
COPY .editorconfig .

# The CLI is engine-free but pulls XmlGen, which ProjectReferences the vendored
# wham submodule at .deps/wham — copy it before restore.
# NOTE: this needs the submodule initialized; a clone without --recurse-submodules leaves .deps/wham
# empty and restore fails on XmlGen's ProjectReferences.
COPY .deps/ .deps/
COPY src/BattleScribeSpec.TestKit/BattleScribeSpec.TestKit.csproj src/BattleScribeSpec.TestKit/
COPY src/BattleScribeSpec.Telemetry/BattleScribeSpec.Telemetry.csproj src/BattleScribeSpec.Telemetry/
COPY src/BattleScribeSpec.Telemetry.Collector/BattleScribeSpec.Telemetry.Collector.csproj src/BattleScribeSpec.Telemetry.Collector/
COPY src/BattleScribeSpec.XmlGen/BattleScribeSpec.XmlGen.csproj src/BattleScribeSpec.XmlGen/
COPY src/BattleScribeSpec.Cli/BattleScribeSpec.Cli.csproj src/BattleScribeSpec.Cli/
RUN dotnet restore src/BattleScribeSpec.Cli/BattleScribeSpec.Cli.csproj

# Copy source and specs
COPY src/ src/
COPY specs/ specs/

# Publish (framework-dependent; PublishAot is blocked upstream — see README AOT note)
RUN dotnet publish src/BattleScribeSpec.Cli/BattleScribeSpec.Cli.csproj \
    -c Release -o /app --no-restore

# aspnet, NOT runtime: BattleScribeSpec.Telemetry.Collector carries
# `<FrameworkReference Include="Microsoft.AspNetCore.App" />` (it hosts the OTLP receiver), and that
# flows to the CLI — bs-spec.runtimeconfig.json lists both Microsoft.NETCore.App and
# Microsoft.AspNetCore.App. On the `runtime` image the build succeeds and the binary then refuses to
# start, which is the worst place to find out.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
COPY --from=build /src/specs /specs

# NO DEFAULT ENGINE, ON PURPOSE. This image is the engine-free orchestrator: it carries the specs and
# the runner, not an engine. The built-in `battlescribe` engine needs bs-engine-host plus the
# IKVM-compiled BattleScribe jars, which are third-party binaries fetched from a token-gated archive
# and are not shipped here.
#
# The CMD used to be `run --all --roster --specs /specs --output summary`, which resolves to that
# built-in engine and therefore could not work. Printing usage is the honest default; supply your own
# adapter as a connectable, and declare where its traffic lands:
#
#   docker run --rm -v "$PWD/my-adapter:/adapter:ro" bs-spec:local \
#     run --all --roster --specs /specs --output summary \
#     --engine "myengine=dotnet:/adapter/my-adapter.dll" --engine-endpoint local
#
# --engine-endpoint local is a declaration, not a tuning knob: an undeclared endpoint fails safe to
# "a third party's live service" and is throttled to 2 concurrent sessions.
ENTRYPOINT ["dotnet", "bs-spec.dll"]
CMD ["--help"]
