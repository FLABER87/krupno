# Installer and releases

The installer is built with Inno Setup (`installer/Krupno.iss`). It installs the app together with .NET (self-contained publish), so .NET does not need to be installed separately.
The wizard is light whatever the Windows theme: Inno Setup's dark style draws the checkboxes of its lists cut on both sides at 300% DPI.

## Install modes and tasks

By default the app is installed for the current user, to `%LOCALAPPDATA%\Programs\Krupno`, without administrator rights. The "for all users" option installs it to Program Files.
Optional tasks:
- a desktop shortcut;
- the install folder in PATH, so `krupno-cli` can be run from any console;
- an "Upscale with Krupno" item in the Explorer context menu for images and folders, for a selection of up to 15 items (Explorer's limit for such items). Explorer starts Krupno.exe for each selected item; every one after the first hands its file to the window already open, so the whole selection lands in one queue.

The update check is not a task: it is on by default and turned off in the app's "Settings" (see [Updates](../README.md#updates)). The installer of Krupno 0.2.4 and earlier had a task for it, which wrote its choice to `HKCU\Software\Krupno` (`HKLM` for all users); later installers leave that value as it is, so a check turned off there stays off until it is turned on in "Settings".

## Models

The models are not bundled with the installer. You can tick them in the component list, and the installer downloads them from the [models-v4](https://github.com/FLABER87/krupno/releases/tag/models-v4) release and verifies their SHA-256.
When the app is updated, a model with the same SHA-256 is not downloaded again.

## A running Krupno

Setup and the uninstaller don't replace or remove the files of a Krupno that is running: every window and `krupno-cli` holds a named mutex (`RunningMarker`, the installer's `AppMutex`; anyone may wait on it, so Setup sees it when run as an administrator or elevated), and while one does, Setup asks for Krupno to be closed instead of having the Restart Manager close it in the middle of processing. Closing the window during a run cancels it and finishes the result being written first. Once past that check, Setup holds a mutex of its own (`SetupMutex`) until it ends, and no processing starts meanwhile, in the window (a message says why) or in `krupno-cli` (it exits with an error).

The names depend on the install mode. A copy installed for the current user (or not installed at all: the ZIP, a build) uses its Windows session's `Krupno-Running` and `Krupno-Setup`. A copy installed for all users (its folder is in the `HKLM` uninstall entry) uses the global `Global\Krupno-Running-AllUsers` and `Global\Krupno-Setup-AllUsers`, as it may also be running in another user's session (Fast User Switching, Remote Desktop), out of reach of a session's names; creating them needs no privilege, so a standard user's Krupno holds them too. Setup takes the names of the mode it installs in (`{code:}` constants, which it expands once the mode is known), and for all users it also checks and holds the session's names, which Krupno 0.2.2 and earlier hold whatever the mode (a copy that old running in another session goes unseen). Global names for every installation would have one user's Setup wait for another user's own copy. The uninstaller checks the names Setup checked when it installed (Inno Setup keeps them in the uninstall log). "Update" in a copy installed for all users doesn't start while another user's Krupno is running either, and says so.

## Uninstalling

Uninstalling removes the app, everything the tasks above added and the accelerators' compiled models (`%LOCALAPPDATA%\Krupno\cache`), and leaves the rest of `%LOCALAPPDATA%\Krupno` (settings, logs and your own models) alone. That folder belongs to the account that runs the uninstaller: after a "for all users" installation, the other accounts' compiled models stay in their own `%LOCALAPPDATA%\Krupno\cache` (up to 2 GB each) until they delete it.

## Languages

The installer speaks the same languages as the window and picks the one Windows uses; with none of them, it asks. Hong Kong and Macau get Traditional Chinese (Setup matches them by their own language IDs; otherwise it would take the first Chinese one, Simplified). If the wizard runs in a language the app wouldn't pick by itself (one chosen in that dialog, or Brazilian Portuguese on a Portuguese Windows), a first installation writes it to the app's `settings.json`, so the app starts in it too; "Language" in "Settings" changes it later. The Explorer item is named in the language chosen during installation.

The wizard's own text comes from Inno Setup's messages files: twelve come with Inno Setup, and the Indonesian, Vietnamese and both Chinese ones, which it doesn't install, are kept in `installer/languages` (taken from the `is-6_7_1` tag of [jrsoftware/issrc](https://github.com/jrsoftware/issrc), to match the Inno Setup version the release uses). Two of those lacked the download and verification messages of Inno Setup 6.5+ that the model download shows; Krupno adds them at the end of the file's `[Messages]`. A missing message would fall back to English with a compiler warning, and CI fails on any warning of the installer script.
Krupno's own text (installation types, components, tasks, the Explorer item) is in `installer/messages.iss`; the model names there are the ones the window shows.
CI compiles the script without output on every push (`ISCC /O-`), so a broken language or message shows up before a release, and `LocalizationTests` checks that the installer has the window's languages and every message in each of them.

## Updating from the window

The update check and "Update" are described for users in [Updates](../README.md#updates); the details are in [how-it-works.md](how-it-works.md#update-check). "Update" (an installed copy) downloads the release's installer from github.com by its direct link (`releases/download/v0.3.0/Krupno-0.3.0-setup-x64.exe`; GitHub sends it on to its file storage) together with the release's `SHA256SUMS.txt`, into `%LOCALAPPDATA%\Krupno\updates`. The line shows the percentage, and ✕ cancels. The files aren't signed, so the installer runs only if its SHA-256 is the one `SHA256SUMS.txt` gives; otherwise the line says so and offers to try again.

Then Setup runs without its wizard (`/SILENT`, a progress window only) in the mode of the installation the window belongs to (for the current user or for all users, which asks for administrator rights), in the window's language, and with its log in `%LOCALAPPDATA%\Krupno\logs\setup-<version>.log`. The window closes once Setup has taken over (it holds its `SetupMutex`, past the UAC prompt and the check for a running Krupno); if Setup ends before that (the UAC prompt was declined), the window stays and says the update wasn't installed. Setup keeps the components and tasks chosen before, skips the models it already has, and starts Krupno again when it ends; if it failed or was canceled, the old version starts and says the update wasn't installed, with a link to Setup's log. The next start deletes the downloaded installer.

The window never closes in the middle of processing: "Update" waits while a run is going on, and no run starts while the installer downloads. Another Krupno window or `krupno-cli` would hold the files Setup replaces, so the line asks to close them first. "What's new" opens the release page. The portable ZIP has no installation to update: its line links to the release page, which has the installer and the ZIP.

## Building the installer

To build the installer and the portable ZIP, you need the .NET SDK 10 and Inno Setup 6.5+ (`winget install JRSoftware.InnoSetup`):

```bash
powershell -ExecutionPolicy Bypass -File installer\build.ps1
```

The output goes to `artifacts/`: `Krupno-<version>-setup-x64.exe`, `Krupno-<version>-win-x64.zip` and `SHA256SUMS.txt`. The version comes from `<Version>` in `Directory.Build.props`.
The script publishes `Krupno.exe` and `krupno-cli.exe` into one folder. Models from `models/` are not copied there, the DirectML debug files and `*.pdb` are removed, and the .NET and Windows ML licenses go to `licenses/`.

## Model releases

The installer lists the models in `installer/Krupno.iss` (file name, size, SHA-256). A new set of models is published as a separate release (`models-v5` and so on), and the link in `Krupno.iss` is updated. Old model releases are never overwritten: already built installers point to them. A model release is published without "Set as the latest release" (`gh release create models-v5 --latest=false`): the window finds a new version of Krupno through the latest release, and a model release in its place would hide one until the next release of the app.

## Releasing a version

Releases are built by the `.github/workflows/release.yml` workflow. To release a version:
1. Bump `<Version>` in `Directory.Build.props`.
2. Push a `v<version>` tag.
3. The workflow builds the installer and the ZIP and creates a draft release. Publish it manually on GitHub.
4. Once the release is published, `.github/workflows/packages.yml` updates the package managers (see below).

Code signing through SignPath Foundation is already built into the workflow. It is turned on by the setup described in [installer/signpath/README.md](../installer/signpath/README.md).

## Package managers

**Scoop.** The repository is a Scoop bucket of its own: `bucket/krupno.json` installs the portable ZIP and the default model, which goes into the `models` folder; that folder is persisted, so the models a user adds stay through updates. The official Extras bucket takes apps with at least 100 stars on GitHub. After a release is published, the `Packages` workflow runs `installer/update-scoop.ps1`, which takes the new version and the ZIP's SHA-256 from the release's `SHA256SUMS.txt`, and opens a pull request with the new manifest (GitHub Actions may open it only with "Allow GitHub Actions to create and approve pull requests" in the repository's settings; otherwise the log has the link to open it by hand). A pull request from the workflow runs no checks, so it is merged by an administrator. When the default model moves to a new model release, its link and hash in the manifest (in `url`, `hash` and `autoupdate`) change by hand, together with `Krupno.iss`.

**WinGet.** The package is `FLABER87.Krupno` in [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs): the installer, for the current user (`/CURRENTUSER`) or for all users (`/ALLUSERS`). Its first version was submitted by hand; after that, the `Packages` workflow submits each release through [WinGet Releaser](https://github.com/vedantmgoyal9/winget-releaser) once the `WINGET_TOKEN` secret is set: a classic token with the `public_repo` scope of an account with a fork of winget-pkgs. Without the secret, the job only says so. Microsoft's checks and review take from hours to days.
