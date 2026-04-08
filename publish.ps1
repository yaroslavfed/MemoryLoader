$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$redistRoot = Join-Path $projectRoot "Redist"
$targets = @(
    @{ Rid = "win-x64"; Folder = "64" },
    @{ Rid = "win-x86"; Folder = "32" }
)

foreach ($target in $targets) {
    $outputDir = Join-Path $redistRoot $target.Folder

    if (Test-Path $outputDir) {
        Remove-Item -LiteralPath $outputDir -Recurse -Force
    }

    New-Item -ItemType Directory -Path $outputDir | Out-Null

    dotnet publish $projectRoot `
        -c Release `
        -r $target.Rid `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:PublishTrimmed=false `
        -p:DebugType=none `
        -p:DebugSymbols=false `
        -o $outputDir

    if ($LASTEXITCODE -ne 0) {
        throw "Publish failed for $($target.Rid)"
    }
}

Write-Host "Ready:"
Write-Host "  $redistRoot\\64"
Write-Host "  $redistRoot\\32"
