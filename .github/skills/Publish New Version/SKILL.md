---
name: Publish New Version
description: Bumps the version and publishes a Lumi release for every platform through GitHub Actions, with release notes
---

# Publish New Version

Use this when the user asks to publish or release a new version of Lumi. For unpublished test packages, run the same workflow as a dry run (`gh workflow run release.yml --ref <branch> -f version=X.Y.Z -f dry_run=true`): it builds every package as workflow artifacts without signing or publishing anything.

## How a release runs

`release.yml` builds the commit `main` points to when it's dispatched, and refuses production releases from any other ref. [docs/testing.md](../../../docs/testing.md) describes its QA gate.

1. `validate_release_inputs` checks the version.
2. In parallel: **Extended UI QA** on Windows x64/ARM64, Linux x64/ARM64 and both kinds of Mac, `build-pwa`, and `build-android`. `build-android` uses the `android-production` environment, which waits for a maintainer's approval. The user asking for the release is that approval (step 6).
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
5. **Commit and push the bump.**
   ```powershell
   git add src/Lumi/Lumi.csproj
   git commit -m "bump version to X.Y.Z"
   git push origin main
   ```
6. **Start the release and approve Android.**
   ```powershell
   gh workflow run release.yml --ref main -f version=X.Y.Z
   gh run list --workflow=release.yml -L 1 --json databaseId,headSha,status
   # When build-android is waiting:
   gh api repos/adirh3/Lumi/actions/runs/<run-id>/pending_deployments --jq '.[] | {env: .environment.name, id: .environment.id}'
   '{"environment_ids":[<id>],"state":"approved","comment":"Release X.Y.Z"}' |
     gh api --method POST repos/adirh3/Lumi/actions/runs/<run-id>/pending_deployments --input -
   ```
7. **Attach the notes while the release is a draft**, after `create-release` creates it and before `publish-release` runs. Windows, Linux and macOS packaging leaves several minutes.
   ```powershell
   gh api repos/adirh3/Lumi/releases --jq '.[] | select(.tag_name == "vX.Y.Z") | {id, draft}'
   gh release edit vX.Y.Z --notes-file <notes.md>
   ```
   No output means the draft doesn't exist yet. (Indexing an empty list, as in `[...] | .[0] | {id}`, returns an object of nulls, not "nothing".) Don't replace the notes after publishing: the release would be public without them, and replacing them would drop any note the pipeline added.
8. **Monitor.** Poll `gh run view <run-id> --json status,conclusion,jobs` instead of `gh run watch`, whose output is huge. If a job fails:
   - Nothing is public until `publish-release`. Find the failing test in the job's log: `gh api repos/adirh3/Lumi/actions/jobs/<job-id>/logs`. (`gh run view --log-failed` can return nothing for the Extended UI QA jobs.)
   - The headed QA step deliberately runs a control with autosave turned off, so `[qa] FAIL persist-checkpoint` and `LUMI_QA_FAIL` lines in its log are expected. Judge the step by its conclusion.
   - If the failure matches a known flaky test, re-run the failed jobs once the run has finished (`gh run rerun <run-id> --failed`). Jobs that passed, including the approved Android build, are kept. Tell the user it happened. If it fails again, or isn't a known flake, stop and report it. Never weaken or skip a check to get a release out.
9. **Verify.**
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
