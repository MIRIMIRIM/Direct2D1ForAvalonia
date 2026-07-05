param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [switch]$SkipTextParityCli,
    [switch]$RunWindowSmoke,
    [switch]$RunRenderParity,
    [switch]$RunBenchmarks,
    [string]$ArtifactsPath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
if ([string]::IsNullOrWhiteSpace($ArtifactsPath)) {
    $ArtifactsPath = "artifacts\validate"
}

$artifacts = if ([System.IO.Path]::IsPathRooted($ArtifactsPath)) {
    $ArtifactsPath
} else {
    Join-Path $repoRoot $ArtifactsPath
}
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

function Invoke-ValidationStep {
    param(
        [string]$Name,
        [scriptblock]$Action
    )

    Write-Host ""
    Write-Host "==> $Name"
    & $Action
    if ($LASTEXITCODE -ne 0) {
        throw "Validation step failed: $Name"
    }
}

Push-Location $repoRoot
try {
    Invoke-ValidationStep "Build solution" {
        dotnet build src\Direct2D1ForAvalonia.slnx -c $Configuration
    }

    Invoke-ValidationStep "TextParity tests" {
        dotnet test src\TextParity.Tests\TextParity.Tests.csproj -c $Configuration --no-build -v minimal
    }

    Invoke-ValidationStep "AotSmoke offscreen tests" {
        dotnet test src\AotSmoke.Tests\AotSmoke.Tests.csproj -c $Configuration --no-build -v minimal
    }

    if (-not $SkipTextParityCli) {
        Invoke-ValidationStep "TextParity CLI" {
            dotnet run --project src\ParityTools\ParityTools.csproj -c $Configuration --no-build -- `
                text `
                --report (Join-Path $artifacts "textparity-report.md") `
                --out-dir (Join-Path $artifacts "textparity")
        }
    }

    if ($RunWindowSmoke) {
        Invoke-ValidationStep "AotSmoke window smoke" {
            dotnet run --project src\AotSmoke\AotSmoke.csproj -c $Configuration --no-build -- `
                --auto-exit `
                --out (Join-Path $artifacts "aotsmoke") `
                --timeout-ms 10000
        }
    }

    if ($RunRenderParity) {
        Invoke-ValidationStep "Render parity smoke" {
            dotnet run --project src\ParityTools\ParityTools.csproj -c $Configuration --no-build -- `
                render `
                --out-dir (Join-Path $artifacts "renderparity") `
                --report-json (Join-Path $artifacts "renderparity-report.json")
        }
    }

    if ($RunBenchmarks) {
        Invoke-ValidationStep "Benchmark smoke" {
            dotnet run --project src\Benchmarks\Benchmarks.csproj -c Release -- `
                --case "TextShaper L=32" `
                --json (Join-Path $artifacts "benchmarks-textshaper-l32.json")
        }
    }
}
finally {
    Pop-Location
}

Write-Host ""
Write-Host "Validation passed. Artifacts: $artifacts"
