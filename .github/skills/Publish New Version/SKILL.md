---
name: Publish New Version
description: Bumps the version and publishes a Lumi release for every platform through GitHub Actions, with release notes and a risk assessment
---

# Publish New Version

Use this when the user asks to publish or release a new version of Lumi. For unpublished test packages, run the same workflow as a dry run (`gh workflow run release.yml --ref <branch> -f version=X.Y.Z -f dry_run=true`): it builds every package as workflow artifacts without signing or publishing anything.

## How a release runs

`release.yml` builds the commit `main` points to when it's dispatched, and refuses production releases from any other ref. [docs/testing.md](../../../docs/testing.md) describes its QA gate.

1. `validate_release_inputs` checks the version.
2. In parallel: **Extended UI QA** on Windows x64/ARM64, Linux x64/ARM64 and both kinds of Mac, `build-pwa`, and `build-android`. `build-android` uses the `android-production` environment, which waits for a maintainer's approval. The user asking for the release is that approval (step 8).
3. Once those pass, `create-release` creates one **draft** GitHub release. Both `release-windows` architectures merge their Velopack packages onto it; then `release-linux` and `release-macos` merge theirs, and `publish-android` attaches the APK. `publish-android` also appends an `## Android` installation note to the release notes, unless they already contain `## Android`.
4. `publish-release` makes the draft public after everything else has passed. Until then, nothing is public.

A full run takes about 35 to 50 minutes.

## Steps

1. **Sync and check main.** Run `git pull --ff-only origin main` and `git fetch --tags`. Check that PR validation passed for the commit `main` points to:
   ```powershell
   gh run list --workflow=pr-validation.yml --branch main -L 3 --json headSha,conclusion
   ```
   If it hasn't run for that commit, or failed, run it (`gh workflow run pr-validation.yml --ref main`), wait for it, and fix or report failures before releasing.
2. **Choose the version.** Read `<Version>` in `src/Lumi/Lumi.csproj` and bump the patch number, unless the user gives a version. It must be numeric `major.minor.patch` and higher than the last release, because the Android version code is derived from it.
3. **Bump only `src/Lumi/Lumi.csproj`.** The workflow stamps the version into every package, including Android's version code.
4. **Write the release notes** to a markdown file in the session's files folder.
   - Read every commit since the last tag, bodies included: `git log vLAST..HEAD --format="=== %h %s%n%b"`. Save it to a temp file when it's long.
   - Write the user-facing changes in these sections, omitting empty ones:
     - **✨ New Features**: new capabilities and UX
     - **🐛 Bug Fixes**: resolved issues
     - **🔧 Improvements**: polish, performance and refactors users notice
   - Write one concise, user-friendly sentence per entry, grouping related commits. Use Lumi's user-facing names (connectors, agents, AI accounts), not code names. Check claims against commit bodies or the code.
   - End with an `## Android` section: the APK name (`Lumi-X.Y.Z-android-arm64.apk`), and the note that early preview builds signed with the Android debug certificate can't update in place (revoke the paired phone in the preview app, uninstall it, install the APK and pair again). With that heading present, the pipeline doesn't append its own note.
5. **Update What's new** for X.Y.Z with the [Update Whats New](../Update%20Whats%20New/SKILL.md) skill, from the notes you just wrote: the highlights users see once when Lumi updates, in all ten languages, or an empty entry when the release has nothing worth showing. Every released version needs its entry in `WhatsNewCatalog`: `WhatsNewScheduleTests.TheCatalogDescribesTheVersionBeingBuilt` fails without it. Run that skill's tests before committing.
6. **Commit and push the bump** with everything What's new changed. Check `git status`: a new destination also changes `src/Lumi/ViewModels/MainViewModel.WhatsNew.cs` (and the view that names the row it opens), and a new icon changes `Strata`, which is committed and pushed in its own repository first.
   ```powershell
   git add src/Lumi/Lumi.csproj src/Lumi/Services/WhatsNew src/Lumi/Resources/Strings src/Lumi/ViewModels/MainViewModel.WhatsNew.cs
   git status --short   # stage anything else What's new changed, such as a view or Strata
   git commit -m "bump version to X.Y.Z"
   git push origin main
   ```
7. **Assess the risk** before starting the release, and present it with the release notes so the user decides with both. Base it on the change itself (`git diff vLAST..HEAD` and each commit's `git show --numstat`), not only the commit messages:
   - **What changed where**: per commit, its product and test lines; the Strata range (`git -C Strata log OLD..NEW`, from `git ls-tree vLAST Strata` and `git ls-tree HEAD Strata`); and whether dependencies (`*.csproj`, `*.props`, `build/`), the chat database schema (`ChatDatabase.SchemaVersion`), persisted settings and models (new public properties in `Models.cs`), the phone protocol (`src/Lumi.Remote.Protocol`), the Android manifest or the release workflow changed. Changes there widen what can break and what's hard to undo.
   - **Risks by impact radius**: a risk is a change that could break something, rated by what breaks with it. Start with what everyone does: opening Lumi, starting or resuming a chat, and sending a message and getting a reply. Then flows many people use (editing a message, the phone, approvals), then features that can only break themselves (a new page, an opt-in feature). For each, name the changes that touch it, what could break and how likely it is. An intended change in behavior, such as a new default, isn't a risk.
   - **The evidence, by kind**: tests added; the full suite (`-f full_suite=true`) on the exact commit, both Windows architectures included; trim warnings (release builds are published trimmed, and nothing in CI launches one: the validation build log covers Lumi's own code, and only a publish shows warnings from Strata and other libraries, so compare them with the previous version's code); how long each change has been on main and in the user's daily Lumi (the `ProductVersion` of `src/Lumi/bin/Release/**/Lumi.exe` names the commit it was built from, and `git reflog` shows when main was pulled); and real use in the user's profile when it matters, read only.
   - **A confidence for each risk**: high only for what you verified directly (a diff, a test run, a log, a launched build); medium when you infer it from tests or similar code; low when it rests on behavior nobody has measured or run. Don't inflate or downplay. Keep pre-existing issues apart from new ones, and list what you couldn't verify.
   - When a high-impact risk sits on an untested path that's cheap to check, check it. For example, publish the release build to a temp folder and launch it with an isolated `LUMI_APPDATA_DIR`: `dotnet publish src/Lumi/Lumi.csproj -c Release -r win-x64 -o <temp>/out --artifacts-path <temp>/art /p:Version=X.Y.Z`. Never publish into `src/Lumi/bin`, which the user's daily Lumi runs from. If downloading the Copilot CLI fails, add `/p:CopilotCliBinaryPath=<the cached copilot.exe in src/Lumi/obj/copilot-full/<version>/<rid>>`.
   - End with an overall verdict and the options: ship, ship with a small change, or wait.
8. **Start the release and approve Android.**
   ```powershell
   gh workflow run release.yml --ref main -f version=X.Y.Z
   gh run list --workflow=release.yml -L 1 --json databaseId,headSha,status
   # When build-android is waiting:
   gh api repos/adirh3/Lumi/actions/runs/<run-id>/pending_deployments --jq '.[] | {env: .environment.name, id: .environment.id}'
   '{"environment_ids":[<id>],"state":"approved","comment":"Release X.Y.Z"}' |
     gh api --method POST repos/adirh3/Lumi/actions/runs/<run-id>/pending_deployments --input -
   ```
9. **Attach the notes while the release is a draft**, after `create-release` creates it and before `publish-release` runs. Windows, Linux and macOS packaging leaves several minutes.
   ```powershell
   gh api repos/adirh3/Lumi/releases --jq '.[] | select(.tag_name == "vX.Y.Z") | {id, draft}'
   gh release edit vX.Y.Z --notes-file <notes.md>
   ```
   No output means the draft doesn't exist yet. (Indexing an empty list, as in `[...] | .[0] | {id}`, returns an object of nulls, not "nothing".) Don't replace the notes after publishing: the release would be public without them, and replacing them would drop any note the pipeline added.
10. **Monitor.** Poll `gh run view <run-id> --json status,conclusion,jobs` instead of `gh run watch`, whose output is huge. If a job fails:
   - Nothing is public until `publish-release`. Find the failing test in the job's log: `gh api repos/adirh3/Lumi/actions/jobs/<job-id>/logs`. (`gh run view --log-failed` can return nothing for the Extended UI QA jobs.)
   - The headed QA step deliberately runs a control with autosave turned off, so `[qa] FAIL persist-checkpoint` and `LUMI_QA_FAIL` lines in its log are expected. Judge the step by its conclusion.
   - If the failure matches a known flaky test, re-run the failed jobs once the run has finished (`gh run rerun <run-id> --failed`). Jobs that passed, including the approved Android build, are kept. Tell the user it happened. If it fails again, or isn't a known flake, stop and report it. Never weaken or skip a check to get a release out.
11. **Verify.**
   ```powershell
   gh api repos/adirh3/Lumi/releases/tags/vX.Y.Z --jq '{draft, prerelease, assets: (.assets | length)}'
   gh api repos/adirh3/Lumi/releases/latest --jq .tag_name
   ```
   - The release is public, not a prerelease, and the latest release.
   - Its notes match the notes file, with exactly one `## Android` section.
   - All assets are there: Windows x64/ARM64 `Setup.exe`, `Portable.zip` and nupkgs; Linux x64/ARM64 `AppImage`, portable `tar.gz` and nupkgs; macOS arm64 and x64 `Setup.pkg`, `Portable.zip` and nupkgs; the APK and its `.txt`; `RELEASES` and `releases.{win,win-arm64,linux,linux-arm64,osx-arm64,osx-x64}.json`. Delta nupkgs only exist when a previous package is available for that channel.
   - The update feeds offer the new version: `https://github.com/adirh3/Lumi/releases/latest/download/releases.<channel>.json` for `win`, `win-arm64`, `linux`, `linux-arm64`, `osx-arm64` and `osx-x64`.
   - The APK's `SIGNER_SHA256` (in its `.txt`) matches the previous release's, so Android users can install it over their current app.

## If a published release has a problem

- **Stop it spreading:** `gh release edit vX.Y.Z --draft=true` hides it from update checks and makes its downloads unavailable. Anyone who already downloaded it still installs it at their next restart.
- **Fix forward with a patch release.** Lumi never downgrades. Since 1.0, chats live in SQLite, so 0.9.x would show an empty chat list.
- Lumi checks for updates at launch and every 4 hours, and the user chooses when to download and restart.

## Important Notes

- The `gh` CLI must be authenticated (`gh auth status`).
- Release notes describe changes from the user's perspective, not implementation details.
