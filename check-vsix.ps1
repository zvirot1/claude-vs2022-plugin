Add-Type -AssemblyName System.IO.Compression.FileSystem
$z = [System.IO.Compression.ZipFile]::OpenRead("C:\dev\vs\claude-vs2022\ClaudeCode.vsix")
$e = $z.GetEntry("[Content_Types].xml")
$r = New-Object System.IO.StreamReader($e.Open())
Write-Host "=== [Content_Types].xml ==="
Write-Host $r.ReadToEnd()
$r.Close()
Write-Host ""
Write-Host "=== All entries ==="
$z.Entries | ForEach-Object { Write-Host $_.FullName }
$z.Dispose()
