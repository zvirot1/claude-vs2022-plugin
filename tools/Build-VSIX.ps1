$src = 'C:\Users\Administrator\AppData\Local\Temp\vsix-final'
$dst = 'C:\dev\vs\claude-vs2022\ClaudeCode-1.0.5.vsix'
if (Test-Path $dst) { Remove-Item $dst }

# Read existing manifest to extract id/version
[xml]$xml = Get-Content (Join-Path $src 'extension.vsixmanifest')
$ns = New-Object System.Xml.XmlNamespaceManager($xml.NameTable)
$ns.AddNamespace('v','http://schemas.microsoft.com/developer/vsx-schema/2011')
$identity = $xml.SelectSingleNode('//v:Identity', $ns)
$displayName = $xml.SelectSingleNode('//v:DisplayName', $ns).InnerText
$description = $xml.SelectSingleNode('//v:Description', $ns).InnerText
$id = $identity.Id
$ver = $identity.Version
$prereqId = $xml.SelectSingleNode('//v:Prerequisite', $ns).Id
$prereqVer = $xml.SelectSingleNode('//v:Prerequisite', $ns).Version

# Compute file list + SHA256
$files = @()
$packageRoot = $src
foreach ($f in (Get-ChildItem -Path $src -Recurse -File | Where-Object { $_.Name -ne '[Content_Types].xml' -and $_.FullName -notlike "*\_rels\*" -and $_.Name -ne 'manifest.json' -and $_.Name -ne 'catalog.json' })) {
    $rel = '/' + $f.FullName.Substring($src.Length + 1).Replace([char]92, '/')
    $hash = (Get-FileHash -Algorithm SHA256 -Path $f.FullName).Hash
    $files += [pscustomobject]@{ fileName = $rel; sha256 = $hash }
}

# Build manifest.json
$installDir = '[installdir]\Common7\IDE\Extensions\Anthropic\ClaudeCode\' + $ver
$manifest = [pscustomobject]@{
    id = $id
    version = $ver
    type = 'Vsix'
    vsixId = $id
    extensionDir = $installDir
    files = $files
    installSizes = @{ targetDrive = (Get-ChildItem -Path $src -Recurse -File | Measure-Object -Property Length -Sum).Sum }
    dependencies = @{ $prereqId = $prereqVer }
}
$manifestJson = $manifest | ConvertTo-Json -Depth 10 -Compress
[System.IO.File]::WriteAllText((Join-Path $src 'manifest.json'), $manifestJson, [System.Text.Encoding]::UTF8)

# Build catalog.json
$catalog = [pscustomobject]@{
    manifestVersion = '1.1'
    info = @{
        id = "$id,version=$ver"
        manifestType = 'Extension'
    }
    packages = @(
        [pscustomobject]@{
            id = "Component.$id"
            version = $ver
            type = 'Component'
            extension = $true
            dependencies = @{
                $id = $ver
                $prereqId = $prereqVer
            }
            localizedResources = @(
                @{ language = 'en-US'; title = $displayName; description = $description }
            )
        },
        [pscustomobject]@{
            id = $id
            version = $ver
            type = 'Vsix'
            payloads = @(@{ fileName = "ClaudeCode.vsix"; size = 0 })
            vsixId = $id
            extensionDir = $installDir
            installSizes = @{ targetDrive = (Get-ChildItem -Path $src -Recurse -File | Measure-Object -Property Length -Sum).Sum }
        }
    )
}
$catalogJson = $catalog | ConvertTo-Json -Depth 10 -Compress
[System.IO.File]::WriteAllText((Join-Path $src 'catalog.json'), $catalogJson, [System.Text.Encoding]::UTF8)

# Build [Content_Types].xml
$ctxml = '<?xml version="1.0" encoding="utf-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="vsixmanifest" ContentType="text/xml" /><Default Extension="dll" ContentType="application/octet-stream" /><Default Extension="pkgdef" ContentType="text/plain" /><Default Extension="ctmenu" ContentType="application/octet-stream" /><Default Extension="json" ContentType="application/json" /><Default Extension="html" ContentType="text/html" /><Default Extension="css" ContentType="text/css" /><Default Extension="js" ContentType="application/javascript" /><Default Extension="txt" ContentType="text/plain" /></Types>'
[System.IO.File]::WriteAllText((Join-Path $src '[Content_Types].xml'), $ctxml, [System.Text.Encoding]::UTF8)

# Plain ZIP (no OPC) with forward-slash entries
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::Open($dst, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem -Path $src -Recurse -File | Where-Object { $_.FullName -notlike "*\_rels\*" } | ForEach-Object {
        $rel = $_.FullName.Substring($src.Length + 1).Replace([char]92, '/')
        $entry = $zip.CreateEntry($rel, [System.IO.Compression.CompressionLevel]::Optimal)
        $es = $entry.Open()
        $fs = [System.IO.File]::OpenRead($_.FullName)
        $fs.CopyTo($es)
        $fs.Close()
        $es.Close()
        Write-Output $rel
    }
} finally { $zip.Dispose() }
'SIZE=' + (Get-Item $dst).Length
