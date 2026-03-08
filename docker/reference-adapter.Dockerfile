FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
WORKDIR /src

# Copy project files for restore
COPY BattleScribeSpec.slnx .
COPY src/BattleScribeSpec.TestKit/BattleScribeSpec.TestKit.csproj src/BattleScribeSpec.TestKit/
COPY src/BattleScribeSpec.Oracle/BattleScribeSpec.Oracle.csproj src/BattleScribeSpec.Oracle/
COPY src/BattleScribeSpec.ReferenceAdapter/BattleScribeSpec.ReferenceAdapter.csproj src/BattleScribeSpec.ReferenceAdapter/

# Copy IKVM JARs needed at restore/build time
COPY lib/*.jar lib/
RUN dotnet restore src/BattleScribeSpec.ReferenceAdapter/BattleScribeSpec.ReferenceAdapter.csproj

# Copy all source
COPY src/ src/
COPY specs/ specs/

# Publish
RUN dotnet publish src/BattleScribeSpec.ReferenceAdapter/BattleScribeSpec.ReferenceAdapter.csproj \
    -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0-preview
WORKDIR /app
COPY --from=build /app .

# Adapter reads from stdin, writes to stdout
ENTRYPOINT ["dotnet", "bs-reference-adapter.dll"]
