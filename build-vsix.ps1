# Build script for Claude Code VS 2022 extension
param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"

Write-Host "=== Building Claude Code for VS 2022 ===" -ForegroundColor Cyan

$vssdk = "$env:USERPROFILE\.nuget\packages\microsoft.vssdk.buildtools\17.0.5232\tools\vssdk"
$outputDir = "ClaudeCode\bin\$Configuration\net472"
$vsixPath = "ClaudeCode.vsix"

# Build
dotnet build ClaudeCode\ClaudeCode.csproj -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

# Compile VSCT
& "$vssdk\bin\VSCT.exe" "ClaudeCode\ClaudeCodePackage.vsct" "$outputDir\ClaudeCode.ctmenu" "-I$vssdk\inc" -nologo 2>&1

# Copy pkgdef
Copy-Item "ClaudeCode\ClaudeCode.pkgdef" "$outputDir\ClaudeCode.pkgdef" -Force

Write-Host "Packaging VSIX..." -ForegroundColor Yellow
$tempDir = Join-Path $env:TEMP "claude-vsix-$(Get-Random)"
New-Item -ItemType Directory -Path $tempDir -Force | Out-Null

try {
    Copy-Item "ClaudeCode\source.extension.vsixmanifest" "$tempDir\extension.vsixmanifest"

    # Minimal Content_Types - NO duplicates
    $ct = '<?xml version="1.0" encoding="utf-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="vsixmanifest" ContentType="text/xml"/><Default Extension="dll" ContentType="application/octet-stream"/><Default Extension="pkgdef" ContentType="text/plain"/><Default Extension="ctmenu" ContentType="application/octet-stream"/><Default Extension="html" ContentType="text/html"/><Default Extension="css" ContentType="text/css"/><Default Extension="js" ContentType="application/javascript"/></Types>'
    [System.IO.File]::WriteAllText("$tempDir\[Content_Types].xml", $ct, [System.Text.Encoding]::UTF8)

    # Copy all DLLs
    foreach ($dll in @("ClaudeCode.dll","Microsoft.Web.WebView2.Core.dll","Microsoft.Web.WebView2.WinForms.dll","Microsoft.Web.WebView2.Wpf.dll","Newtonsoft.Json.dll")) {
        $src = Join-Path $outputDir $dll
        if (Test-Path $src) { Copy-Item $src $tempDir }
    }

    # Copy pkgdef and ctmenu
    foreach ($f in @("ClaudeCode.pkgdef","ClaudeCode.ctmenu")) {
        $src = Join-Path $outputDir $f
        if (Test-Path $src) { Copy-Item $src $tempDir }
    }

    # Copy runtimes
    $rt = Join-Path $outputDir "runtimes"
    if (Test-Path $rt) { Copy-Item $rt "$tempDir\runtimes" -Recurse }

    # Copy webview resources
    $res = Join-Path $outputDir "Resources"
    if (Test-Path $res) { Copy-Item $res "$tempDir\Resources" -Recurse }

    # Create VSIX
    if (Test-Path $vsixPath) { Remove-Item $vsixPath }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($tempDir, (Join-Path (Get-Location) $vsixPath))

    $size = [math]::Round((Get-Item $vsixPath).Length / 1MB, 2)
    Write-Host "=== VSIX: $vsixPath ($size MB) ===" -ForegroundColor Green
}
finally {
    Remove-Item $tempDir -Recurse -Force -ErrorAction SilentlyContinue
}
