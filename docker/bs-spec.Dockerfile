FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
WORKDIR /src

# Copy solution + build props for restore
COPY BattleScribeSpec.slnx .
COPY Directory.Build.props .
COPY Directory.Packages.props .
COPY .editorconfig .

# The CLI is engine-free but pulls XmlGen, which ProjectReferences the vendored
# wham submodule at .deps/wham — copy it before restore.
COPY .deps/ .deps/
COPY src/BattleScribeSpec.TestKit/BattleScribeSpec.TestKit.csproj src/BattleScribeSpec.TestKit/
COPY src/BattleScribeSpec.XmlGen/BattleScribeSpec.XmlGen.csproj src/BattleScribeSpec.XmlGen/
COPY src/BattleScribeSpec.Cli/BattleScribeSpec.Cli.csproj src/BattleScribeSpec.Cli/
RUN dotnet restore src/BattleScribeSpec.Cli/BattleScribeSpec.Cli.csproj

# Copy source and specs
COPY src/ src/
COPY specs/ specs/

# Publish (framework-dependent; PublishAot is blocked upstream — see README AOT note)
RUN dotnet publish src/BattleScribeSpec.Cli/BattleScribeSpec.Cli.csproj \
    -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0-preview
WORKDIR /app
COPY --from=build /app .
COPY --from=build /src/specs /specs

ENTRYPOINT ["dotnet", "bs-spec.dll"]
CMD ["run", "--all", "--specs", "/specs", "--output", "summary"]
