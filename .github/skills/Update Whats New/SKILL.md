---
name: Update Whats New
description: Curates the in-app What's new for the next Lumi release (highlights since the last release, a spotlight, all ten languages) and checks it. Run it before every release; Publish New Version does.
---

# Update What's New

After an installed Lumi updates, it opens **What's new** once: a glass card with the release's spotlight and its
highlights, each with an action that opens where the feature lives. It shows what came out since the version the user
last saw, however many versions they skipped. Use this skill before publishing any version (Publish New Version runs
it at its version step), or when the user asks to change what an update shows.

## How it works

- **The catalog** — `src/Lumi/Services/WhatsNew/WhatsNewCatalog.cs`, `WhatsNewCatalog.Releases`, newest first. Each
  `WhatsNewRelease` is a version (`major.minor.patch`) and its `WhatsNewItem`s, most important first. Text is
  `nameof(Loc.…)` keys, so a string missing from `en.json` fails the build. An icon is the name of one of Strata's line
  icons, `nameof(StrataIcons.…)` (`Strata/src/StrataTheme/Controls/StrataLineIcon.cs`: phone, bell, image, mic,
  globe, plug, wand, folder, shield, clock, sliders and more), the same family the rest of Lumi draws with.
- **Who sees what** — `WhatsNewSchedule`: the profile remembers `LastSeenWhatsNewVersion`; an update shows the releases
  after it, up to the running version. Profiles from before What's new count from 1.0. New users never see it (the
  welcome is their introduction, and finishing it starts them at the current version), development builds neither
  show nor record it, and a version whose entry is empty is marked seen silently. One showing holds one spotlight (the
  newest release's) and at most `WhatsNewSchedule.MaxHighlights` other items, newest release first.
- **Spotlight** — an item with `IsSpotlight = true`, at most one per release, shown large with its `EyebrowKey`,
  `Points` and `NoteKey`. A spotlight whose `Destination` is `PhoneSettings` is pictured as a phone receiving Lumi's
  notifications and adapts when a phone is already paired ("Your phone is connected", **Phone settings**); any other
  spotlight wears its icon large.
- **Actions** — `ActionKey` + `Destination`. `MainViewModel.OpenWhatsNewDestination` maps destinations to places; for
  one Settings row it calls `SettingsVM.ShowSetting(pageIndex, "<control x:Name>")`, which scrolls the row to the top.
- **Platforms** — `Platforms` keeps an item off platforms that don't have the feature (Windows-only computer use, say).
- **Reopening** — Settings → About → **What's new** shows the latest highlights again without recording anything.

## Steps

1. **Pick the version** being released: the one Publish New Version is about to set in `src/Lumi/Lumi.csproj` (usually
   the next patch), or the one the user gives.
2. **Read what changed** since the last release, bodies included:
   `git log vLAST..HEAD --format="=== %h %s%n%b"`. Check the draft release notes too when they exist.
3. **Choose the highlights** — two to six things a user would notice and want to try. Prefer new abilities and visible
   changes over fixes, performance and refactors (those stay in the release notes). Order them by how much they
   matter to the user. Merge related commits into one item.
4. **Choose a spotlight**, or none. Use it for the release's headline. When the release improves Lumi on the phone,
   make the phone the spotlight (`Destination = PhoneSettings`): it is how people discover the companion app, and it
   adapts for users who already paired one.
5. **Write the strings** with keys `WhatsNew_<Feature>_Title`, `_Body`, and `_Action` (when there is a place to go),
   in all ten languages, then add them to every catalog in one step. Put them in a JSON file outside the repository
   (the session folder or `$env:TEMP`), each key with its text by catalog name:
   ```json
   {
     "WhatsNew_Voice_Title": { "en": "…", "he": "…", "es": "…", "pt-BR": "…", "zh-Hans": "…", "de": "…", "fr": "…", "ja": "…", "ko": "…", "ar": "…" },
     "WhatsNew_Voice_Body": { "en": "…", "he": "…", "…": "…" }
   }
   ```
   ```powershell
   pwsh -File '.github/skills/Update Whats New/Add-WhatsNewStrings.ps1' -Path "$env:TEMP/whats-new-1.0.3.json"
   ```
   It adds the keys after the existing `WhatsNew_` keys of `src/Lumi/Resources/Strings/*.json`, and writes nothing if a
   key is malformed, already exists, or misses a language. To change a string that exists, edit the catalogs.
   - Titles are short and concrete (about 40 characters). Bodies are one or two sentences, about 130 characters at
     most (tiles sit two to a row), and say what the user can now do, not how it was built.
   - Use Lumi's user-facing names (agents, connectors, AI accounts, Jobs), as the release notes do. Write shortcuts as
     `Ctrl+…` (macOS shows `⌘` automatically) and keep `Ctrl+Enter`, product names and model names untranslated.
   - Translate with each catalog's existing terms for the feature (search the catalog for its Settings label) and its
     voice (Hebrew uses לומי in sentences and masculine forms, as the rest of `he.json`). No version numbers in text.
6. **Add the release** at the top of `WhatsNewCatalog.Releases`; when the version already has an entry, written ahead
   of the release, bring that one up to date instead. The entries already there show each kind of item:
   ```csharp
   new("1.0.3",
   [
       new(nameof(StrataIcons.Mic), nameof(Loc.WhatsNew_Voice_Title), nameof(Loc.WhatsNew_Voice_Body))
       {
           ActionKey = nameof(Loc.WhatsNew_Voice_Action),
           Destination = WhatsNewDestination.VoiceSettings, // a new destination: see below
       },
       new(nameof(StrataIcons.Globe), nameof(Loc.WhatsNew_Browser_Title), nameof(Loc.WhatsNew_Browser_Body)),
   ]),
   ```
   - Nothing worth showing (a hotfix): add `new("1.0.3", [])`. Every released version needs an entry;
     `WhatsNewScheduleTests.TheCatalogDescribesTheVersionBeingBuilt` fails when `Lumi.csproj` names a version the
     catalog doesn't reach.
   - An action leads to a `WhatsNewDestination`. A new place to go is a new value there and its case in
     `MainViewModel.OpenWhatsNewDestination` (to open one Settings row, name the row in its view if it has no
     `x:Name` yet); `WhatsNewShellTests.EveryDestinationLeadsSomewhere` fails until it has one.
   - No Strata icon fits: add one to `StrataIcons` in the Strata submodule (a stroke path on the 24-unit grid, with a
     summary like its neighbours'), and commit and push Strata before Lumi.
   - Never change or remove the entries of released versions: users who skipped them still see them.
7. **Test**:
   ```powershell
   dotnet test tests/Lumi.Tests/Lumi.Tests.csproj --filter "FullyQualifiedName~WhatsNew|FullyQualifiedName~LocalizationCoverageTests"
   ```
8. **Look at it** the way users updating from the last release will, in an isolated profile:
   ```powershell
   $env:LUMI_APPDATA_DIR = "$env:TEMP\lumi-whats-new"
   dotnet run --project src\Lumi\Lumi.csproj -- --skip-onboarding --whats-new-from <last released version>
   ```
   `--whats-new` alone shows everything since 1.0. Neither flag records anything. Check the card with avalonia-mcp
   (`#WhatsNewOverlay`, `#WhatsNewSpotlightAction`, `#WhatsNewDismissButton`) in dark and light themes and in Hebrew
   (right to left), that every action opens the right place, and that `get_binding_errors` is empty.
9. **Commit** everything these steps changed, before the version bump or with it, so the release build carries it:
   the catalog and strings, and for a new destination `MainViewModel.WhatsNew.cs` and the view that names its row
   (`git status` lists them). A new Strata icon is committed and pushed in Strata first, then Lumi's submodule pointer
   with the rest.
