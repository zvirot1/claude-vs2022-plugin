Add-Type -AssemblyName System.IO.Compression.FileSystem
$extDir = (Get-ChildItem "$env:LOCALAPPDATA\Microsoft\VisualStudio\17.0_*\Extensions" -Recurse -Filter "ClaudeCode.dll" | Select-Object -First 1).DirectoryName
Write-Host "Extension dir: $extDir"

$zip = [System.IO.Compression.ZipFile]::OpenRead("C:\dev\vs\claude-vs2022\ClaudeCode.vsix")
foreach ($entry in $zip.Entries) {
    if ($entry.FullName -match "^(Resources|runtimes)" -and $entry.FullName -notmatch "/$") {
        $dest = Join-Path $extDir $entry.FullName
        $dir = Split-Path $dest
        if (!(Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $dest, $true)
        Write-Host "  Extracted: $($entry.FullName)"
    }
}
$zip.Dispose()
Write-Host "Done!"
