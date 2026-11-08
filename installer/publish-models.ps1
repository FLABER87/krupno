# Publishes the models in the models folder as the next model release (models-vN, not marked as the latest release) and
# points the installer, the Scoop manifest and the docs to it. A published model release is never changed, since built
# installers download from it (docs/installer.md#model-releases), so every change to the set is a new release.
#   powershell -ExecutionPolicy Bypass -File installer\publish-models.ps1 -DryRun   # what would be published and changed
#   powershell -ExecutionPolicy Bypass -File installer\publish-models.ps1           # publish, after a confirmation
# Only a set that differs from the latest model release is published: a model with another SHA-256, or a new file.
# Models missing from the folder are taken from the latest release, so the new one is complete. -Exclude leaves files of
# the folder out (the latest release's copy is taken if it has one).
# The licenses (MODEL-LICENSES.txt) and the release notes start from the latest release's, with the version replaced, and
# are kept as drafts in artifacts\models-vN, which later runs reuse. The last paragraph of the notes, the list of changes,
# is generated on every run (-Summary adds a sentence to it); -NotesFile and -LicensesFile replace the drafts. Every
# model must be named in both: for a new one, add its author, license and source to the drafts and run again.
# A new model's installer component, its title in the window and its README row are added by hand: the script lists
# them. The edited files are left uncommitted.
param(
    [string]$ModelsDir,
    [string[]]$Exclude = @(),
    [string]$LicensesFile,
    [string]$NotesFile,
    [string]$Summary,
    [string]$Repository = "FLABER87/krupno",
    [switch]$DryRun,
    # Publish without asking.
    [switch]$Yes
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
if (-not $ModelsDir) { $ModelsDir = Join-Path $root "models" }
if (-not (Test-Path -LiteralPath $ModelsDir -PathType Container)) { throw "No folder $ModelsDir" }
$ModelsDir = (Resolve-Path -LiteralPath $ModelsDir).Path

# Files that link to the current model release. docs/models.md is left out: it names old releases on purpose.
$referenceFiles = "installer\Krupno.iss", "bucket\krupno.json", "installer\models-README.txt",
    "installer\signpath\README.md", "README.md", "docs\installer.md", "THIRD-PARTY-NOTICES.txt", "CLAUDE.md"
$licensesName = "MODEL-LICENSES.txt"
# The list of changes at the end of the release notes ("New since [models-v3](...)", "Changes since [models-v4](...)").
$changesParagraph = '(?s)\s*\n(New|Updated|Changed|Changes)\b[^\n]*?\bsince \[models-v\d+\].*\z'

function Invoke-Gh {
    $output = & gh @args
    if ($LASTEXITCODE -ne 0) { throw "gh $($args -join ' ') exited with code $LASTEXITCODE" }
    $output
}

function Get-GhJson { (Invoke-Gh @args) -join "`n" | ConvertFrom-Json }

function Read-Text([string]$Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    $bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $offset = if ($bom) { 3 } else { 0 }
    [pscustomobject]@{ Text = [Text.Encoding]::UTF8.GetString($bytes, $offset, $bytes.Length - $offset); Bom = $bom }
}

function Write-Text([string]$Path, [string]$Text, [bool]$Bom) {
    [IO.File]::WriteAllText($Path, $Text, (New-Object Text.UTF8Encoding $Bom))
}

function Get-Sha256([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

# A file of the latest model release, downloaded once into the temporary folder.
function Get-PreviousFile([string]$Name) {
    $path = Join-Path $temp $Name
    if (-not (Test-Path -LiteralPath $path)) {
        Invoke-Gh release download $oldTag --repo $Repository --pattern $Name --dir $temp | Out-Null
    }
    $path
}

# Whether a text names a file as a whole: "real.onnx" is not named by "4xBHI_dat2_real.onnx".
function Test-Named([string]$Text, [string]$Name) {
    $Text -match ('(?<![\w.-])' + [regex]::Escape($Name) + '(?![\w.-])')
}

function Format-Names($Entries) { ($Entries | ForEach-Object { '`' + $_.Name + '`' }) -join ", " }

function Format-MB([long]$Size) { "{0,8:N1} MB" -f ($Size / 1MB) }

# The Scoop manifest downloads the default model: its hash follows the model's.
function Update-Bucket([string]$Text) {
    $arch = ($Text | ConvertFrom-Json).architecture.'64bit'
    $urls = @($arch.url)
    $hashes = @($arch.hash)
    for ($i = 0; $i -lt $urls.Count; $i++) {
        if ($urls[$i] -match '/releases/download/models-v\d+/([^/]+)$') {
            $entry = $byName[$Matches[1]]
            if (-not $entry) { throw "bucket\krupno.json downloads $($Matches[1]), which is neither in $ModelsDir nor in $oldTag" }
            $Text = $Text.Replace($hashes[$i], $entry.Hash)
        }
    }
    $Text
}

function Show-Diff($Edit) {
    $oldLines = $Edit.Old -split "\r?\n"
    $newLines = $Edit.New -split "\r?\n"
    for ($i = 0; $i -lt [Math]::Min($oldLines.Count, $newLines.Count); $i++) {
        if ($oldLines[$i] -ceq $newLines[$i]) { continue }
        if ($oldLines[$i].Length -gt 160) {
            Write-Host "      line $($i + 1): models-v$old -> $newTag"
        } else {
            Write-Host "      - $($oldLines[$i].Trim())" -ForegroundColor Red
            Write-Host "      + $($newLines[$i].Trim())" -ForegroundColor Green
        }
    }
}

$savedEncoding = [Console]::OutputEncoding
# gh writes UTF-8; Windows PowerShell would decode it with the console's code page and garble the release notes.
[Console]::OutputEncoding = New-Object Text.UTF8Encoding $false
$temp = Join-Path ([IO.Path]::GetTempPath()) ("krupno-models-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    # The latest model release and the next one.
    $releases = Get-GhJson release list --repo $Repository --limit 1000 --json "tagName,isDraft,isLatest"
    $drafts = @($releases | Where-Object { $_.isDraft -and $_.tagName -match '^models-v\d+$' })
    if ($drafts) {
        throw ("A draft release $($drafts[0].tagName) is on GitHub, probably left by an interrupted run. Delete it and " +
            "run again: gh release delete $($drafts[0].tagName) --repo $Repository --yes")
    }
    $numbers = @($releases | Where-Object { -not $_.isDraft -and $_.tagName -match '^models-v\d+$' } |
        ForEach-Object { [int]$_.tagName.Substring("models-v".Length) })
    if (-not $numbers) { throw "$Repository has no model release (models-vN)" }
    $old = [int]($numbers | Measure-Object -Maximum).Maximum
    $oldTag = "models-v$old"
    $new = $old + 1
    $newTag = "models-v$new"
    $latestApp = @($releases | Where-Object { $_.isLatest })[0].tagName

    $issPath = Join-Path $root "installer\Krupno.iss"
    $iss = Read-Text $issPath
    if (-not $iss.Text.Contains("/releases/download/$oldTag`"")) {
        throw "installer\Krupno.iss does not download from $oldTag, the latest model release: pull the latest changes first"
    }

    # What changed: the folder's models against the latest release's files.
    $previous = Get-GhJson release view $oldTag --repo $Repository --json "assets,body"
    $previousAssets = @{}
    foreach ($asset in $previous.assets) { $previousAssets[$asset.name] = $asset }
    function Get-PreviousHash($Asset) {
        if ($Asset.digest) { return $Asset.digest -replace '^sha256:', '' }
        Get-Sha256 (Get-PreviousFile $Asset.name)
    }

    Write-Host "Hashing the models in $ModelsDir..."
    $entries = New-Object Collections.Generic.List[object]
    $byName = @{}
    $files = @(Get-ChildItem -LiteralPath $ModelsDir -File |
        Where-Object { $_.Extension -eq ".onnx" -and $Exclude -notcontains $_.Name })
    foreach ($file in $files) {
        $hash = Get-Sha256 $file.FullName
        $asset = $previousAssets[$file.Name]
        $state = if (-not $asset) { "new" } elseif ((Get-PreviousHash $asset) -eq $hash) { "same" } else { "updated" }
        $entry = [pscustomobject]@{ Name = $file.Name; Size = $file.Length; Hash = $hash; Path = $file.FullName; State = $state }
        $entries.Add($entry)
        $byName[$entry.Name] = $entry
    }
    foreach ($asset in $previous.assets) {
        if ($asset.name -eq $licensesName -or $byName.ContainsKey($asset.name)) { continue }
        $entry = [pscustomobject]@{
            Name = $asset.name; Size = [long]$asset.size; Hash = (Get-PreviousHash $asset); Path = $null; State = "carried"
        }
        $entries.Add($entry)
        $byName[$entry.Name] = $entry
    }
    $entries = @($entries | Sort-Object Name)
    $newModels = @($entries | Where-Object State -eq "new")
    $updatedModels = @($entries | Where-Object State -eq "updated")
    if (-not $newModels -and -not $updatedModels) {
        Write-Host "The models are the same as in $($oldTag): nothing to publish."
        return
    }
    $models = @($entries | Where-Object { $_.Name -like "*.onnx" })
    $problems = @()
    $warnings = @()

    # Licenses and release notes, kept as drafts in artifacts\models-vN.
    $draftDir = Join-Path $root "artifacts\$newTag"
    New-Item -ItemType Directory -Force -Path $draftDir | Out-Null
    $licensesPath = Join-Path $draftDir $licensesName
    $notesPath = Join-Path $draftDir "notes.md"

    if ($LicensesFile) { $licensesSource = (Resolve-Path -LiteralPath $LicensesFile).Path; $licensesFrom = $LicensesFile }
    elseif (Test-Path -LiteralPath $licensesPath) { $licensesSource = $licensesPath; $licensesFrom = "the draft" }
    else { $licensesSource = Get-PreviousFile $licensesName; $licensesFrom = $oldTag }
    $licenses = (Read-Text $licensesSource).Text -replace "models-v$old(?!\d)", $newTag
    Write-Text $licensesPath $licenses $false

    $changes = @()
    if ($newModels) { $changes += "new " + (Format-Names $newModels) }
    if ($updatedModels) { $changes += "updated " + (Format-Names $updatedModels) }
    $changes = "Changes since [$oldTag](https://github.com/$Repository/releases/tag/$oldTag): " + ($changes -join "; ") + "."
    if ($Summary) { $changes += " " + $Summary.Trim() }
    if (@($entries | Where-Object { $_.State -eq "same" -or $_.State -eq "carried" })) {
        $changes += " The other files are unchanged."
    }
    if ($NotesFile) {
        $notes = (Read-Text (Resolve-Path -LiteralPath $NotesFile).Path).Text
        $notesFrom = $NotesFile
    } else {
        if (Test-Path -LiteralPath $notesPath) { $base = (Read-Text $notesPath).Text; $notesFrom = "the draft" }
        else { $base = $previous.body; $notesFrom = $oldTag }
        $notes = ($base -replace $changesParagraph, "").TrimEnd() + "`n`n" + $changes + "`n"
    }
    Write-Text $notesPath $notes $false
    # A model must have its own row, not only a mention in the list of changes.
    $notesBody = $notes -replace $changesParagraph, ""

    foreach ($model in $models) {
        if (-not (Test-Named $licenses $model.Name)) {
            $problems += "$($model.Name) is not in artifacts\$newTag\$($licensesName): add its author, license and source"
        }
        if (-not (Test-Named $notesBody $model.Name)) {
            $problems += "$($model.Name) is not in the table of artifacts\$newTag\notes.md: add a row for it"
        }
    }

    # The edits: sizes and hashes in the installer, the default model's hash in the Scoop manifest, and the release's
    # links everywhere.
    $modelLine = '\{#Model\("(?<component>[^"]+)", "(?<file>[^"]+)", "[\d_]+", "[0-9a-fA-F]{64}"\)\}'
    $issText = $iss.Text
    $installerFiles = @()
    foreach ($match in [regex]::Matches($iss.Text, $modelLine)) {
        $file = $match.Groups["file"].Value
        $installerFiles += $file
        $entry = $byName[$file]
        if (-not $entry) { $problems += "installer\Krupno.iss downloads $file, which is neither in the folder nor in $oldTag"; continue }
        $size = ([long]$entry.Size).ToString("N0", [Globalization.CultureInfo]::InvariantCulture).Replace(",", "_")
        $line = '{#Model("' + $match.Groups["component"].Value + '", "' + $file + '", "' + $size + '", "' + $entry.Hash + '")}'
        $issText = $issText.Replace($match.Value, $line)
    }
    if (-not $installerFiles) { throw "installer\Krupno.iss has no {#Model(...)} lines" }

    $edits = [ordered]@{}
    foreach ($relative in $referenceFiles) {
        $path = Join-Path $root $relative
        if (-not (Test-Path -LiteralPath $path)) { $warnings += "$relative is missing"; continue }
        $current = if ($relative -eq "installer\Krupno.iss") { $iss } else { Read-Text $path }
        $text = $current.Text
        if ($relative -eq "installer\Krupno.iss") { $text = $issText }
        if ($relative -eq "bucket\krupno.json") { $text = Update-Bucket $text }
        $text = $text -replace "models-v$old(?!\d)", $newTag
        if ($relative -eq "bucket\krupno.json") {
            $arch = ($text | ConvertFrom-Json).architecture.'64bit'
            if (@($arch.url | Where-Object { $_ -match '/models-v\d+/' -and $_ -notmatch "/$newTag/" })) {
                throw "bucket\krupno.json was not updated as expected"
            }
        }
        if ($text -cne $current.Text) {
            $edits[$relative] = [pscustomobject]@{ Path = $path; Old = $current.Text; New = $text; Bom = $current.Bom }
        }
    }

    foreach ($model in $newModels) {
        if ($model.Name -notlike "*.onnx") { continue }
        $missing = @()
        if ($installerFiles -notcontains $model.Name) {
            $missing += "a component and a {#Model} line in installer\Krupno.iss with its title in installer\messages.iss"
        }
        $missing += "its title in src\Krupno.App\ViewModels\ModelItem.cs (if it is not there yet)"
        foreach ($doc in "README.md", "THIRD-PARTY-NOTICES.txt") {
            if (-not (Test-Named (Read-Text (Join-Path $root $doc)).Text $model.Name)) { $missing += "an entry in $doc" }
        }
        $warnings += "$($model.Name) is new; by hand: " + ($missing -join "; ")
    }

    # The plan.
    Write-Host ""
    Write-Host "$Repository`: $oldTag -> $newTag (not marked as the latest release)"
    foreach ($entry in $entries) {
        $label = switch ($entry.State) { "new" { "new    " } "updated" { "updated" } "same" { "same   " } "carried" { "kept   " } }
        $color = switch ($entry.State) { "new" { "Green" } "updated" { "Yellow" } default { "Gray" } }
        $note = if ($entry.State -eq "carried") { "  (not in the folder: taken from $oldTag)" } else { "" }
        Write-Host "  $label $(Format-MB $entry.Size)  $($entry.Name)$note" -ForegroundColor $color
    }
    Write-Host "  $licensesName from $licensesFrom"
    $total = ($entries | Measure-Object Size -Sum).Sum
    Write-Host "  $($entries.Count + 1) files, $((Format-MB $total).Trim())"
    Write-Host ""
    Write-Host "Release notes (artifacts\$newTag\notes.md, from $notesFrom):"
    foreach ($line in $notes.TrimEnd() -split "\r?\n") { Write-Host "  | $line" -ForegroundColor DarkGray }
    Write-Host ""
    Write-Host "Files to update once the release is published:"
    foreach ($relative in $edits.Keys) {
        Write-Host "  $relative"
        if ($DryRun) { Show-Diff $edits[$relative] }
    }
    if (Test-Path -LiteralPath (Join-Path $root ".git")) {
        $paths = $referenceFiles | ForEach-Object { $_.Replace("\", "/") }
        $left = @(& git -C $root grep -n -w -e "models-v$old" -- . | Where-Object { $paths -notcontains ($_ -split ":")[0] })
        if ($left) {
            Write-Host "Other mentions of $oldTag, left as they are:"
            foreach ($line in $left) { Write-Host "  $line" -ForegroundColor DarkGray }
        }
    }
    foreach ($warning in $warnings) { Write-Host "Warning: $warning" -ForegroundColor Yellow }
    foreach ($problem in $problems) { Write-Host "Error: $problem" -ForegroundColor Red }
    if ($problems) {
        Write-Host "Not published: fix the errors above (the drafts are in artifacts\$newTag) and run the script again." -ForegroundColor Red
        exit 1
    }
    if ($DryRun) {
        Write-Host ""
        Write-Host "Dry run: nothing was published or changed."
        return
    }

    if (-not $Yes) {
        Write-Host ""
        $answer = Read-Host "Publish $newTag to $Repository? A published model release is never changed afterwards [y/N]"
        if ($answer -notmatch '^(y|yes)$') { Write-Host "Cancelled: nothing was published or changed."; return }
    }

    # Publishing: a draft first, made public only once every file is on GitHub with the right size and hash.
    $uploads = @()
    foreach ($entry in $entries) {
        if (-not $entry.Path) {
            Write-Host "Downloading $($entry.Name) from $oldTag..."
            $entry.Path = Get-PreviousFile $entry.Name
            if ((Get-Sha256 $entry.Path) -ne $entry.Hash) { throw "$($entry.Name) from $oldTag does not match its SHA-256" }
        }
        $uploads += $entry.Path
    }
    $uploads += $licensesPath
    $expected = @($entries) + [pscustomobject]@{
        Name = $licensesName; Size = (Get-Item -LiteralPath $licensesPath).Length; Hash = (Get-Sha256 $licensesPath)
    }
    try {
        Write-Host "Uploading $($uploads.Count) files to the draft release $newTag..."
        Invoke-Gh release create $newTag --repo $Repository --draft "--latest=false" --title "Models v$new" `
            --notes-file $notesPath @uploads | Out-Null
        $created = Get-GhJson release view $newTag --repo $Repository --json "assets"
        $assets = @{}
        foreach ($asset in $created.assets) { $assets[$asset.name] = $asset }
        foreach ($file in $expected) {
            $asset = $assets[$file.Name]
            if (-not $asset -or [long]$asset.size -ne [long]$file.Size -or
                ($asset.digest -and $asset.digest -ne "sha256:$($file.Hash)")) {
                throw "$($file.Name) did not upload correctly"
            }
        }
        if ($assets.Count -ne $expected.Count) { throw "The draft has $($assets.Count) files instead of $($expected.Count)" }
        Invoke-Gh release edit $newTag --repo $Repository "--draft=false" "--latest=false" | Out-Null
    } catch {
        Write-Host ("Publishing failed. A draft release $newTag may be left on GitHub (drafts are not public): delete it " +
            "before running again: gh release delete $newTag --repo $Repository --yes") -ForegroundColor Red
        throw
    }
    # The window finds new versions of Krupno through the latest release, which must stay the app's.
    if ($latestApp) {
        $latestNow = (Invoke-Gh api "repos/$Repository/releases/latest" --jq ".tag_name") -join ""
        if ($latestNow -ne $latestApp) {
            Invoke-Gh release edit $latestApp --repo $Repository --latest | Out-Null
            Write-Host "$newTag had become the latest release; $latestApp is the latest again." -ForegroundColor Yellow
        }
    }
    Write-Host "Published https://github.com/$Repository/releases/tag/$newTag" -ForegroundColor Green

    foreach ($edit in $edits.Values) { Write-Text $edit.Path $edit.New $edit.Bom }
    Write-Host "Updated $($edits.Count) files: $($edits.Keys -join ', ')."
    Write-Host ("Next: check git diff, do what the warnings above ask for, then commit and open a pull request. Scoop " +
        "takes $newTag once it is merged, the installer with the next release of Krupno.")
} finally {
    [Console]::OutputEncoding = $savedEncoding
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
