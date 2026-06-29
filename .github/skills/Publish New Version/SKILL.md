---
name: Publish New Version
description: Bumps version and triggers a new Lumi release via GitHub Actions
---

# Publish New Version

When the user asks to publish or release a new version of Lumi:

1. **Check current version**: Read the `<Version>` tag in `src/Lumi/Lumi.csproj`
2. **Determine new version**: Bump the patch version (e.g., 0.1.0 → 0.2.0), or ask the user if they want a specific version
3. **Update csproj**: Edit the `<Version>` tag in `src/Lumi/Lumi.csproj` to the new version
4. **Generate changelog**:
   - Get the commit diff since the last release tag: `git log <last-tag>..HEAD --oneline`
   - Read the raw commit messages and infer user-facing changes
   - Write a human-readable changelog in markdown with these sections (omit empty sections):
     - **✨ New Features** — new capabilities, UX additions
     - **🐛 Bug Fixes** — resolved issues
     - **🔧 Improvements** — polish, performance, refactors that affect UX
   - Each entry should be a concise, user-friendly sentence (not a commit message). Group related commits into a single entry when appropriate.
   - Save the changelog text — it will be used as the GitHub Release body.
5. **Commit and push**:
   ```
   git add src/Lumi/Lumi.csproj
   git commit -m "bump version to X.Y.Z"
   git push origin main
   ```
6. **Trigger release workflow**:
   ```powershell
   gh workflow run release.yml -f version=X.Y.Z
   ```
7. **Monitor the build**:
   ```powershell
   gh run list --workflow="release.yml" -L 1
   gh run watch <run-id> --exit-status
   ```
8. **Update the release with changelog**: Once the workflow completes, update the release body:
   ```powershell
   gh release edit vX.Y.Z --notes "<changelog markdown>"
   ```
9. **Verify**: Confirm the release is published with the changelog:
   ```powershell
   gh release view vX.Y.Z
   ```

## Important Notes

- The version must be valid SemVer (e.g., 1.0.0, 0.2.0)
- The release workflow builds for Windows x64, signs with Azure Trusted Signing (sign CLI), and publishes to GitHub Releases
- The `gh` CLI must be authenticated (`gh auth status`)
- Always bump version in csproj BEFORE triggering the workflow
- The changelog should describe what changed from the user's perspective, not implementation details
