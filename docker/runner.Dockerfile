FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
WORKDIR /src

# Copy project files for restore
COPY BattleScribeSpec.slnx .
COPY src/BattleScribeSpec.TestKit/BattleScribeSpec.TestKit.csproj src/BattleScribeSpec.TestKit/
COPY src/BattleScribeSpec.Runner/BattleScribeSpec.Runner.csproj src/BattleScribeSpec.Runner/
RUN dotnet restore src/BattleScribeSpec.Runner/BattleScribeSpec.Runner.csproj

# Copy source and specs
COPY src/BattleScribeSpec.TestKit/ src/BattleScribeSpec.TestKit/
COPY src/BattleScribeSpec.Runner/ src/BattleScribeSpec.Runner/
COPY specs/ specs/

# Publish
RUN dotnet publish src/BattleScribeSpec.Runner/BattleScribeSpec.Runner.csproj \
    -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0-preview
WORKDIR /app
COPY --from=build /app .
COPY --from=build /src/specs /specs

ENTRYPOINT ["dotnet", "bs-spec-runner.dll"]
CMD ["--specs", "/specs", "--output", "summary"]
