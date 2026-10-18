# Updates the Scoop manifest (bucket/krupno.json) to a published release: its version, the ZIP's link and the ZIP's SHA-256
# from the release's SHA256SUMS.txt. The model's link and hash stay, and so does the formatting. Run by
# .github/workflows/packages.yml; by hand:
#   powershell -File installer\update-scoop.ps1 -Tag v0.2.5
# Prints the new version, or nothing if the manifest already has it.
param(
    [Parameter(Mandatory)][string]$Tag,
    [string]$Path = "$PSScriptRoot\..\bucket\krupno.json",
    [string]$Repository = "FLABER87/krupno"
)
$ErrorActionPreference = "Stop"

$version = $Tag.TrimStart("v")
$text = [IO.File]::ReadAllText((Resolve-Path $Path))
$manifest = $text | ConvertFrom-Json
$old = $manifest.version
if ($old -eq $version) { return }

$oldZip = "Krupno-$old-win-x64.zip"
$zip = "Krupno-$version-win-x64.zip"
$sums = (Invoke-WebRequest -UseBasicParsing "https://github.com/$Repository/releases/download/$Tag/SHA256SUMS.txt").Content
if ($sums -is [byte[]]) { $sums = [Text.Encoding]::UTF8.GetString($sums) }
$line = $sums -split "`n" | Where-Object { $_ -match "^([0-9a-fA-F]{64}) \*?$([regex]::Escape($zip))\s*$" } | Select-Object -First 1
if (-not $line) { throw "$zip is not in the SHA256SUMS.txt of $Tag" }
$hash = ($line -split " ")[0].ToLowerInvariant()

$oldHash = $manifest.architecture.'64bit'.hash[0]
$text = $text.Replace("`"version`": `"$old`"", "`"version`": `"$version`"")
$text = $text.Replace("/v$old/$oldZip", "/v$version/$zip")
$text = $text.Replace($oldHash, $hash)
$updated = $text | ConvertFrom-Json
if ($updated.version -ne $version -or $updated.architecture.'64bit'.hash[0] -ne $hash -or
    $updated.architecture.'64bit'.url[0] -notlike "*/$Tag/$zip") {
    throw "The manifest was not updated as expected"
}
[IO.File]::WriteAllText((Resolve-Path $Path), $text, (New-Object Text.UTF8Encoding $false))
$version
