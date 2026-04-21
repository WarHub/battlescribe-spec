#!/usr/bin/env pwsh
#
# migrate-specs.ps1 — Migrates spec YAML files from index-based to ID-based addressing.
# Uses dotnet run to execute a C# migration tool that parses specs properly via SpecLoader.
#
# Usage: pwsh -File tools/migrate-specs.ps1 [-DryRun] [-Filter <pattern>]
#

param(
    [switch]$DryRun,
    [string]$Filter = ""
)

$ErrorActionPreference = 'Stop'

$projectDir = Join-Path $PSScriptRoot ".." "tools" "SpecMigrator"
$args_ = @()
if ($DryRun) { $args_ += "--dry-run" }
if ($Filter) { $args_ += "--filter"; $args_ += $Filter }

dotnet run --project $projectDir -- @args_
