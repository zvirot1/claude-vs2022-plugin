# Creates VSIX using the same method that worked on first install
# (ZipFile.CreateFromDirectory + Out-File for Content_Types)
param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.IO.Compression.FileSystem

$outputDir = "ClaudeCode\bin\$Configuration\net472"
$vsixPath = Join-Path (Get-Location) "ClaudeCode.vsix"

$tempDir = Join-Path $env:TEMP "claude-vsix-$(Get-Random)"
New-Item -ItemType Directory -Path $tempDir -Force | Out-Null

try {
    # extension.vsixmanifest
    Copy-Item "ClaudeCode\source.extension.vsixmanifest" "$tempDir\extension.vsixmanifest"

    # [Content_Types].xml - use Out-File same as first successful build
    # NO duplicate entries
    @"
<?xml version="1.0" encoding="utf-8"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
  <Default Extension="vsixmanifest" ContentType="text/xml" />
  <Default Extension="dll" ContentType="application/octet-stream" />
  <Default Extension="pkgdef" ContentType="text/plain" />
  <Default Extension="ctmenu" ContentType="application/octet-stream" />
  <Default Extension="html" ContentType="text/html" />
  <Default Extension="css" ContentType="text/css" />
  <Default Extension="js" ContentType="application/javascript" />
</Types>
"@ | Out-File -LiteralPath "$tempDir\[Content_Types].xml" -Encoding utf8

    # DLLs
    foreach ($dll in @("ClaudeCode.dll","Microsoft.Web.WebView2.Core.dll","Microsoft.Web.WebView2.WinForms.dll","Microsoft.Web.WebView2.Wpf.dll","Newtonsoft.Json.dll")) {
        $src = Join-Path $outputDir $dll
        if (Test-Path $src) { Copy-Item $src $tempDir }
    }

    # pkgdef and ctmenu
    foreach ($f in @("ClaudeCode.pkgdef","ClaudeCode.ctmenu")) {
        $src = Join-Path $outputDir $f
        if (Test-Path $src) { Copy-Item $src $tempDir }
    }

    # Runtimes
    $rt = Join-Path $outputDir "runtimes"
    if (Test-Path $rt) { Copy-Item $rt "$tempDir\runtimes" -Recurse }

    # Resources
    $res = Join-Path $outputDir "Resources"
    if (Test-Path $res) { Copy-Item $res "$tempDir\Resources" -Recurse }

    # Create ZIP
    if (Test-Path $vsixPath) { Remove-Item $vsixPath }
    [System.IO.Compression.ZipFile]::CreateFromDirectory($tempDir, $vsixPath)

    $size = [math]::Round((Get-Item $vsixPath).Length / 1MB, 2)
    Write-Host "=== VSIX: ClaudeCode.vsix ($size MB) ===" -ForegroundColor Green
}
finally {
    Remove-Item $tempDir -Recurse -Force -ErrorAction SilentlyContinue
}
