# Code signing with SignPath Foundation

SignPath Foundation signs open source projects with its own certificate free of charge.
The `.github/workflows/release.yml` workflow can already submit files for signing, but this only turns on after the setup below.
Until then, releases are built unsigned, and SmartScreen warns on the first launch of the installer.

## Terms that matter for Krupno

The full list is at [signpath.org/terms](https://signpath.org/terms). The key points:

- **The project must already be released.** You apply for a project that already has a release in the form that will be signed. So an unsigned release comes first.
- **All components under OSI licenses, with no proprietary parts.** This is why the models are not bundled with the installer but downloaded during installation from the [models-v2](https://github.com/FLABER87/krupno/releases/tag/models-v2) release. `DirectML.dll` is distributed under a Microsoft license, not an open source one. If SignPath asks about it: it is a Windows system component that we ship next to the app so that an outdated copy from System32 is not picked up.
- **Only our own files are signed:** `Krupno.exe`, `Krupno.dll`, `Krupno.Core.dll`, `krupno-cli.exe`, `krupno-cli.dll` and the installer. The .NET, ONNX Runtime and DirectML files are already signed by Microsoft.
- **Two-factor authentication** on GitHub and SignPath for everyone with access.
- **Every release is approved manually** in SignPath (the Approver role).
- **A "Code signing policy" section** on the project's home page and on the download page. The text is below.
- **Privacy.** Apart from the update check, the app sends nothing over the network unless the user asks it to. ONNX Runtime telemetry is turned off before anything uses it (`OnnxEnvironment`). The installer downloads only the models selected during installation. The update check (`UpdateCheck`) can be turned off during installation (the "Check for new versions" task) and in the window ("Check for updates" under "Settings"), and it is described in the privacy policy below.

## Setup

1. Publish an unsigned release: tag `v<version>` → draft release → publish.
2. Apply at [signpath.org/apply](https://signpath.org/apply).
3. Once approved, in SignPath:
   - connect the **GitHub.com** trusted build system and link it to the `krupno` project;
   - install the SignPath GitHub App on the repository;
   - create the `app` and `setup` artifact configurations from [app.xml](app.xml) and [setup.xml](setup.xml);
   - create the `release-signing` signing policy with manual approval;
   - issue a user API token with the Submitter role.
4. On GitHub, in Settings → Secrets and variables → Actions:
   - the `SIGNPATH_API_TOKEN` secret: the token from step 3;
   - the `SIGNPATH_ORGANIZATION_ID` variable: the organization ID in SignPath. As soon as it is set, the workflow starts signing.
5. Add the section below to the root README.

The project and policy slugs are set at the top of `release.yml` (`SIGNPATH_PROJECT`, `SIGNPATH_POLICY`).

Inno Setup creates the uninstaller (`unins000.exe`) from the installer on the user's machine, so it stays unsigned.
It could only be signed by ISCC during the build (the `SignTool` directive), while the SignPath request is sent from the workflow.

## Text for the README

```markdown
## Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).

- Committers and reviewers: [FLABER87](https://github.com/FLABER87)
- Approvers: [FLABER87](https://github.com/FLABER87)

Privacy policy: this program will not transfer any information to other networked systems unless specifically
requested by the user or the person installing or operating it. The installer downloads only the models selected
during installation, from this repository's [models-v2](https://github.com/FLABER87/krupno/releases/tag/models-v2) release.
At startup the app asks github.com which of this repository's releases is the latest (one request to
`github.com/FLABER87/krupno/releases/latest`, carrying only the app's version), to say when a newer version is
available. Nothing else is sent, and nothing is downloaded. The check can be turned off during installation and under
"Settings" in the app.
```
