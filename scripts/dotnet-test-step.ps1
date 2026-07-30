#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Runs one CI `dotnet test` step and FAILS IT WHEN THE STEP EXECUTED NO TESTS.

.DESCRIPTION
    A green `dotnet test` step is supposed to mean "the tests this step names ran and passed".
    By default it can also mean two other things, and nothing distinguishes them from the outside:

      1. THE FILTER MATCHED NOTHING. VSTest prints "No test matches the given testcase filter",
         exits 0, and the step is green. This is what `--filter "Engine=FrozenNrUiRoster&
         DisplayName~kitchen-sink"` did on every PR: that class is a single `[Fact] AllSpecs()`,
         so no test's display name contains a spec id and the DisplayName clause can never match.
         Total: 0. Green.

      2. THE FILTER MATCHED ONLY TESTS THAT SKIP. Worse, because it looks like a real run — a
         non-zero test count, no failures. `--filter "Engine=FrozenNrRoster&DisplayName~kitchen-
         sink"` selected exactly one test: the `Mode=Sequential` variant of the class, which is
         gated behind NR_SEQUENTIAL and therefore self-skips in CI. Measured:
         `Skipped! - Failed: 0, Passed: 0, Skipped: 1, Total: 1`. Green.

    Between them, both frozen NR roster suites had ZERO per-PR coverage — which is how a HAR bump
    merged green and then broke two suites that the smoke job claimed to be guarding.

    So the invariant this script enforces is not "the selection was non-empty" (case 2 satisfies
    that) but the one that actually means something:

        THE STEP EXECUTED AT LEAST ONE TEST  —  passed + failed >= 1.

    It is measured from the TRX `<Counters>` element, not scraped from console text.
    `RunConfiguration.TreatNoTestsAsError=true` is passed as well, purely so that case 1 fails with
    VSTest's own precise message ("No test matches the given testcase filter `…`") instead of this
    script's more general one.

.PARAMETER TestProfile
    Test profile name, forwarded as `-p:TestProfile=<name>` (tests/test-profiles/<name>.runsettings).
    A real parameter rather than a pass-through argument because PowerShell splits a bare
    `-p:TestProfile=core` at the colon into `-p` and `TestProfile=core`.

.PARAMETER DotnetTestArgs
    Everything else, forwarded to `dotnet test` verbatim (project path, --filter, --logger, …).
    Do NOT pass `--` RunSettings arguments here; this script appends its own.

.EXAMPLE
    pwsh scripts/dotnet-test-step.ps1 tests/BattleScribeSpec.Tests.csproj --no-build --filter "Engine=FrozenNrUiRoster"

.EXAMPLE
    pwsh scripts/dotnet-test-step.ps1 -TestProfile core tests/BattleScribeSpec.Tests.csproj --no-build
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [string]$TestProfile,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$DotnetTestArgs
)

$ErrorActionPreference = 'Stop'
# `dotnet test` exiting non-zero is DATA here, not a terminating error: the accounting below must
# still run and report before that exit code is propagated. (PowerShell 7.4+ would otherwise throw.)
$PSNativeCommandUseErrorActionPreference = $false

if (-not $DotnetTestArgs -or $DotnetTestArgs.Count -eq 0) {
    Write-Error "No arguments given. Usage: dotnet-test-step.ps1 [-TestProfile <name>] <dotnet test args...>"
    exit 2
}

$resultsDir = Join-Path ([System.IO.Path]::GetTempPath()) "bsspec-test-step-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $resultsDir -Force | Out-Null

try {
    $argv = @('test')
    if ($TestProfile) { $argv += "-p:TestProfile=$TestProfile" }
    $argv += $DotnetTestArgs
    $argv += @(
        '--logger', 'trx;LogFileName=step.trx',
        '--results-directory', $resultsDir,
        # VSTest's empty-selection gate. Not the guard — just a better error message for case 1.
        '--', 'RunConfiguration.TreatNoTestsAsError=true'
    )

    & dotnet @argv
    $testExit = $LASTEXITCODE

    $total = 0
    $executed = 0
    $passed = 0
    $failed = 0

    # One TRX per test assembly. A run that never got as far as writing one (VSTest aborting on an
    # empty selection, for instance) simply contributes nothing, and the guard below fires.
    foreach ($file in Get-ChildItem -LiteralPath $resultsDir -Filter '*.trx' -Recurse -File) {
        $counters = ([xml](Get-Content -LiteralPath $file.FullName -Raw)).TestRun.ResultSummary.Counters
        $total += [int]$counters.total
        $executed += [int]$counters.executed
        $passed += [int]$counters.passed
        $failed += [int]$counters.failed
    }

    $skipped = $total - $executed
    Write-Host "[test-step] selected $total | executed $executed (passed $passed, failed $failed) | skipped $skipped"

    if ($executed -lt 1) {
        $why = if ($total -eq 0) {
            "its filter selected NO TESTS AT ALL"
        } else {
            "all $total selected test(s) SKIPPED — nothing was actually executed"
        }

        $invocation = "$(if ($TestProfile) { "-p:TestProfile=$TestProfile " })$($DotnetTestArgs -join ' ')"

        Write-Host "::error::This test step verified nothing: $why."
        @(
            '',
            "  This step executed 0 tests: $why.",
            '',
            '  A step that runs no test is indistinguishable, from its exit code, from a step whose',
            '  tests all passed — which is exactly the defect this guard exists for. Two ways in:',
            '',
            '    * a DisplayName clause against a [Fact] aggregate. Classes shaped as one',
            '      `[Fact] AllSpecs()` collapse every spec into a single test whose name carries no',
            '      spec id, so `--filter "...&DisplayName~kitchen-sink"` can never match. Narrow such',
            '      a suite from the engine side (see NR_FROZEN_SMOKE / NR_UI_SMOKE) and filter on the',
            '      Engine trait alone.',
            '',
            '    * selecting a self-skipping test. The Mode=Sequential conformance classes are gated',
            '      behind NR_SEQUENTIAL and skip in CI, and a filter that reaches only those looks',
            '      like a real run. Add `Mode!=Sequential`, and check that the fixture this suite',
            '      depends on is actually available.',
            '',
            "  Invocation: $invocation",
            ''
        ) | ForEach-Object { Write-Host $_ }

        exit 1
    }

    exit $testExit
}
finally {
    Remove-Item -LiteralPath $resultsDir -Recurse -Force -ErrorAction SilentlyContinue
}
