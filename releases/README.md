# Releases

This folder contains every signed-off VSIX build. Files are named
`ClaudeCode-<version>-<YYYYMMDDHHmm>.vsix` so the timestamp is preserved
inside Git history (in addition to the GitHub Releases page).

Latest builds are mirrored to GitHub Releases: <https://github.com/zvirot1/claude-vs2022-plugin/releases>

## Build history

| Date (UTC)        | File                                       | Notes                                                         |
|-------------------|--------------------------------------------|---------------------------------------------------------------|
| 2026-05-04 06:15  | `ClaudeCode-1.0.5-202605040615.vsix`       | Active File chip in blue, in-frame, X-to-dismiss              |
| 2026-05-03 10:26  | `ClaudeCode-1.0.5-202605031026.vsix`       | IntelliJ Round 7 — Active File chip + hide XML in bubble      |
| 2026-05-03 05:35  | `ClaudeCode-1.0.5-202605030535.vsix`       | Eclipse Round 6 — inline images in user bubble                |
| 2026-04-29 11:24  | `ClaudeCode-1.0.5-202604291124.vsix`       | First v3-format VSIX (manifest.json + catalog.json)           |
| 2026-04-29 11:22  | `ClaudeCode-1.0.5-202604291122.vsix`       | Manifest reverted to minimal community-only target            |
| 2026-04-29 11:17  | `ClaudeCode-1.0.5-202604291117.vsix`       | Allow Pro + Enterprise install targets                        |
| 2026-04-29 10:07  | `ClaudeCode-1.0.5-202604291007.vsix`       | OPC + `_rels/.rels` (still rejected by VSIXInstaller 18.x)    |
| 2026-04-29 09:07  | `ClaudeCode-1.0.5-202604290907.vsix`       | OPC via System.IO.Packaging                                   |
| 2026-04-29 08:43  | `ClaudeCode-1.0.5-202604290843.vsix`       | First public ZIP (Compress-Archive — invalid VSIX)            |

## Building a new release

```powershell
# 1. Build the DLL (Release)
dotnet build ClaudeCode/ClaudeCode.csproj -c Release

# 2. Stage the new bits into the VSIX scratch dir
cp ClaudeCode/bin/Release/net472/ClaudeCode.dll  $TEMP/vsix-final/
cp -r ClaudeCode/Resources/webview/.            $TEMP/vsix-final/Resources/webview/

# 3. Run the v3 packager (produces a VSIXInstaller-18.x compatible package)
powershell -ExecutionPolicy Bypass -File tools/Build-VSIX.ps1

# 4. Stamp + archive
$ts = Get-Date -Format yyyyMMddHHmm
Copy-Item ClaudeCode-1.0.5.vsix "releases/ClaudeCode-1.0.5-$ts.vsix"
```

The `tools/Build-VSIX.ps1` script wraps the bits in a plain ZIP (forward
slashes, no OPC `_rels`) plus the `manifest.json` + `catalog.json` files
that VSIXInstaller 18.x requires.
