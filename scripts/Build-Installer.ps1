param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$AppVersion = "1.0.0"
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$projectPath = Join-Path $repoRoot "src\ScreenMemory.AI.App\ScreenMemory.AI.App.csproj"
$installerScript = Join-Path $repoRoot "Installer\ScreenMemoryAI.iss"
$publishDir = Join-Path $repoRoot "src\ScreenMemory.AI.App\bin\$Configuration\net10.0-windows10.0.19041.0\$Runtime\publish"

Write-Host "Publishing ScreenMemory AI ($Configuration, $Runtime, self-contained)..."
dotnet publish $projectPath `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:DebugType=None `
    -p:DebugSymbols=false

$requiredFiles = @(
    "ScreenMemory.AI.App.exe",
    "AIModels\semantic\model_quantized.onnx",
    "AIModels\semantic\vocab.txt",
    "onnxruntime.dll"
)

foreach ($relativePath in $requiredFiles) {
    $path = Join-Path $publishDir $relativePath
    if (-not (Test-Path $path)) {
        throw "Required publish artifact is missing: $path"
    }
}

$iscc = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
$isccPath = if ($iscc) { $iscc.Source } else { $null }
if (-not $iscc) {
    $commonPaths = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
        "${env:LocalAppData}\Programs\Inno Setup 6\ISCC.exe"
    )

    foreach ($path in $commonPaths) {
        if (Test-Path $path) {
            $isccPath = (Get-Item $path).FullName
            break
        }
    }
}

if (-not $isccPath) {
    throw "Inno Setup compiler was not found. Install Inno Setup 6, then rerun this script."
}

Write-Host "Compiling installer with Inno Setup..."
& $isccPath `
    "/DRepoRoot=$repoRoot" `
    "/DPublishDir=$publishDir" `
    "/DAppVersion=$AppVersion" `
    $installerScript

$installerPath = Join-Path $repoRoot "Installer\ScreenMemoryAI_Setup_v$AppVersion.exe"
if (-not (Test-Path $installerPath)) {
    throw "Installer build finished, but output was not found: $installerPath"
}

Write-Host "Installer created: $installerPath"
