# Builds the installer and the portable ZIP. Requires .NET SDK 10 and Inno Setup 6.5+ (ISCC.exe).
# The version comes from <Version> in Directory.Build.props. Output goes to artifacts:
#   Krupno-<version>-setup-x64.exe, Krupno-<version>-win-x64.zip and SHA256SUMS.txt.
#   powershell -ExecutionPolicy Bypass -File installer\build.ps1                    # everything at once
#   powershell -ExecutionPolicy Bypass -File installer\build.ps1 -Step publish      # only publish to artifacts\app
#   powershell -ExecutionPolicy Bypass -File installer\build.ps1 -Step package -AppDir <folder>
#   powershell -ExecutionPolicy Bypass -File installer\build.ps1 -Step checksums   # recompute SHA256SUMS.txt
# CI needs the separate steps: SignPath signs the exe and dll files between publishing and packaging,
# and the installer after packaging, so the checksums are computed over the signed files.
param(
    [ValidateSet("all", "publish", "package", "checksums")]
    [string]$Step = "all",
    [string]$AppDir,
    [string]$OutDir,
    [string]$Iscc
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
if (-not $AppDir) { $AppDir = Join-Path $root "artifacts\app" }
if (-not $OutDir) { $OutDir = Join-Path $root "artifacts" }
$AppDir = [IO.Path]::GetFullPath($AppDir)
$OutDir = [IO.Path]::GetFullPath($OutDir)
# A separate bin/obj folder: the self-contained build does not mix with the regular one in src\*\bin.
$buildDir = Join-Path $root "artifacts\build"

$version = ([xml](Get-Content -Raw -Encoding UTF8 "$root\Directory.Build.props")).SelectSingleNode("//Version").InnerText
# The installer's VERSIONINFO takes numbers only: 0.2.0-beta → 0.2.0.0.
$numbers = @(($version -split "[-+]")[0].Split("."))
while ($numbers.Count -lt 4) { $numbers += "0" }
$fileVersion = $numbers -join "."

function Invoke-Native([string]$What, [scriptblock]$Command) {
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$What exited with code $LASTEXITCODE" }
}

function Publish-App {
    if (Test-Path -LiteralPath $AppDir) { Remove-Item -LiteralPath $AppDir -Recurse -Force }
    foreach ($project in "src\Krupno.App\Krupno.App.csproj", "src\Krupno.Cli\Krupno.Cli.csproj") {
        # Models are not copied (KrupnoCopyModels=false): the installer downloads them, and the ZIP has none.
        Invoke-Native "dotnet publish $project" {
            dotnet publish "$root\$project" -c Release -o $AppDir --artifacts-path $buildDir -nologo `
                -p:SelfContained=true -p:DebugType=none -p:KrupnoCopyModels=false
        }
    }
    # The app needs neither the debug DirectML, package symbols, nor the onnxruntime import library.
    # Not -Include: PowerShell 5.1 ignores it together with -LiteralPath, and everything would be deleted.
    Get-ChildItem -LiteralPath $AppDir -Recurse -File |
        Where-Object { $_.Extension -in ".pdb", ".lib" -or $_.Name -eq "DirectML.Debug.dll" } | Remove-Item
    Copy-RuntimeLicenses

    foreach ($exe in "Krupno.exe", "krupno-cli.exe") {
        if (-not (Test-Path -LiteralPath (Join-Path $AppDir $exe))) { throw "$exe is missing from $AppDir" }
    }
    if (Get-ChildItem -LiteralPath $AppDir -Recurse -File -Filter *.onnx) { throw "$AppDir contains models (*.onnx)" }
    Write-Host "Published to $AppDir"
}

# .NET, ONNX Runtime and DirectML licenses, taken from the exact NuGet packages that went into the build.
function Copy-RuntimeLicenses {
    $assets = Get-Content -Raw -Encoding UTF8 "$buildDir\obj\Krupno.App\project.assets.json" | ConvertFrom-Json
    $packages = $assets.project.restore.packagesPath
    $versions = @{}
    foreach ($library in $assets.libraries.PSObject.Properties.Name) {
        $id, $libraryVersion = $library -split "/"
        $versions[$id] = $libraryVersion
    }
    # Runtime packs are not listed in libraries; their versions are in downloadDependencies: "[10.0.13, 10.0.13]".
    foreach ($framework in $assets.project.frameworks.PSObject.Properties.Value) {
        foreach ($dependency in $framework.downloadDependencies) {
            $versions[$dependency.name] = $dependency.version.Trim("[]").Split(",")[0].Trim()
        }
    }
    $licenses = @(
        @("Microsoft.NETCore.App.Runtime.win-x64", "LICENSE.TXT", "dotnet-LICENSE.txt"),
        @("Microsoft.NETCore.App.Runtime.win-x64", "THIRD-PARTY-NOTICES.TXT", "dotnet-THIRD-PARTY-NOTICES.txt"),
        @("Microsoft.WindowsDesktop.App.Runtime.win-x64", "LICENSE", "windowsdesktop-LICENSE.txt"),
        @("Microsoft.ML.OnnxRuntime.DirectML", "LICENSE", "onnxruntime-LICENSE.txt"),
        @("Microsoft.ML.OnnxRuntime.DirectML", "ThirdPartyNotices.txt", "onnxruntime-ThirdPartyNotices.txt"),
        @("Microsoft.AI.DirectML", "LICENSE.txt", "DirectML-LICENSE.txt"),
        @("Microsoft.AI.DirectML", "ThirdPartyNotices.txt", "DirectML-ThirdPartyNotices.txt")
    )
    $target = New-Item -ItemType Directory -Force (Join-Path $AppDir "licenses")
    foreach ($license in $licenses) {
        $id, $file, $name = $license
        if (-not $versions.ContainsKey($id)) { throw "Package $id is not in project.assets.json" }
        $source = Join-Path $packages "$($id.ToLowerInvariant())\$($versions[$id])\$file"
        Copy-Item -LiteralPath $source -Destination (Join-Path $target $name)
    }
}

function Find-Iscc {
    if ($Iscc) { return $Iscc }
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    foreach ($dir in "${env:ProgramFiles(x86)}\Inno Setup 6", "$env:ProgramFiles\Inno Setup 6", "$env:LOCALAPPDATA\Programs\Inno Setup 6") {
        if (Test-Path -LiteralPath "$dir\ISCC.exe") { return "$dir\ISCC.exe" }
    }
    throw "ISCC.exe not found. Install Inno Setup 6.5+ (winget install JRSoftware.InnoSetup) or pass -Iscc <path>"
}

function New-Packages {
    if (-not (Test-Path -LiteralPath (Join-Path $AppDir "Krupno.exe"))) { throw "Krupno.exe is missing from $AppDir; run -Step publish first" }
    New-Item -ItemType Directory -Force $OutDir | Out-Null

    # Portable ZIP: a Krupno folder with the app, the licenses and an empty models folder with a hint.
    # Entries are added one by one: CreateFromDirectory in PowerShell 5.1 writes paths with "\", while the ZIP
    # spec uses "/", so unzip or 7-Zip outside Windows would create files with "\" in the name instead of folders.
    $zip = Join-Path $OutDir "Krupno-$version-win-x64.zip"
    # Path inside Krupno → source file. Keys are case-insensitive: our LICENSE.txt and the others
    # replace published files of the same name, just like copying into one folder would.
    $entries = [ordered]@{}
    foreach ($file in Get-ChildItem -LiteralPath $AppDir -Recurse -File) {
        $entries[$file.FullName.Substring($AppDir.Length).TrimStart("\")] = $file.FullName
    }
    $entries["LICENSE.txt"] = "$root\LICENSE"
    $entries["THIRD-PARTY-NOTICES.txt"] = "$root\THIRD-PARTY-NOTICES.txt"
    $entries["models\README.txt"] = "$PSScriptRoot\models-README.txt"
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip }
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($entry in $entries.GetEnumerator()) {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $entry.Value,
                "Krupno/" + $entry.Key.Replace("\", "/"), [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally { $archive.Dispose() }

    $compiler = Find-Iscc
    Invoke-Native "ISCC" {
        & $compiler /Qp "/DAppVersion=$version" "/DFileVersion=$fileVersion" "/DAppDir=$AppDir" "/DOutputDir=$OutDir" "$PSScriptRoot\Krupno.iss"
    }
    Write-Checksums
}

function Write-Checksums {
    $files = "Krupno-$version-setup-x64.exe", "Krupno-$version-win-x64.zip" | ForEach-Object { Join-Path $OutDir $_ }
    $sums = foreach ($file in $files) {
        "{0} *{1}" -f (Get-FileHash -Algorithm SHA256 -LiteralPath $file).Hash.ToLowerInvariant(), (Split-Path $file -Leaf)
    }
    [IO.File]::WriteAllLines((Join-Path $OutDir "SHA256SUMS.txt"), [string[]]$sums)
    foreach ($file in $files) { "{0,8:N1} MB  {1}" -f ((Get-Item -LiteralPath $file).Length / 1MB), $file }
}

Write-Host "Krupno $version"
if ($Step -in "all", "publish") { Publish-App }
if ($Step -in "all", "package") { New-Packages }
if ($Step -eq "checksums") { Write-Checksums }
