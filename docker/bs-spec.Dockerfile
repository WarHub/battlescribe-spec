# THE SDK TAG HERE IS NOT FREE TO DRIFT FROM global.json. The repo pins its SDK feature band
# (rollForward: latestPatch) so `AnalysisLevel=latest-recommended` + `TreatWarningsAsErrors` cannot be
# widened by a toolchain nobody chose. global.json is COPYed below, so an image whose SDK sits outside
# that band does not merely build differently — it fails outright with "A compatible .NET SDK was not
# found". `ToolchainPinDriftTests.DockerImagesUseTheSdkBandPinnedInGlobalJson` holds the two together,
# so bumping one without the other is a red Lint test rather than a broken image nobody builds.
#
# It was `10.0-preview` until 2026-08-14 — for the whole of .NET 10's GA life.
FROM mcr.microsoft.com/dotnet/sdk:10.0.302 AS build
WORKDIR /src

# Copy solution + build props for restore
COPY BattleScribeSpec.slnx .
COPY Directory.Build.props .
COPY Directory.Build.targets .
COPY Directory.Packages.props .
COPY global.json .
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
