# Developing PlayerData

## Prerequisites

- .NET SDK 9.0+
- Unity 6000.0+ (only for `src/PlayerData.Unity`; optional)
- [NuGetForUnity CLI](https://github.com/GlitchEnzo/NuGetForUnity) (only for the Unity project)

Optional developer tools (used by CI and local hooks):

- [typos](https://github.com/crate-ci/typos) — spell check
- [lefthook](https://github.com/evilmartians/lefthook) — git hooks
- [Task](https://taskfile.dev/) — task runner

## Build, run, test

```sh
dotnet build src/PlayerData.Core/PlayerData.Core.csproj -c Release  # packs Core into ../.local-feed
dotnet build PlayerData.slnx -c Release
dotnet test PlayerData.slnx -c Release --no-build
```

`PlayerData.SourceGenerator.IntegrationTests` consumes the packed `PlayerData.Core`
nupkg from the local feed (`../.local-feed`, see `nuget.config`), so build Core once
before restoring the full solution. `task build` does this for you.

Or via [Task](https://taskfile.dev/):

```bash
task build
task test
task check   # spellcheck + commit-lint + dco-check + build + test
```

## Git hooks

Install local hooks (Conventional Commits + DCO sign-off):

```powershell
pwsh ./scripts/install-git-hooks.ps1
# or
task setup
```

- **pre-commit**: spellcheck staged markdown/yaml (via lefthook)
- **commit-msg**: enforce Conventional Commits + DCO `Signed-off-by`

## Commit & PR conventions

- Use [Conventional Commits](https://www.conventionalcommits.org/): `type: description`
- Sign off every commit: `git commit -s`
- Squash-merge only; the PR title becomes the commit on the default branch

See [CONTRIBUTING.md](../.github/CONTRIBUTING.md) for the full workflow.

## Releases

Releases are automated with [Release Please](https://github.com/googleapis/release-please):

1. Merge Conventional Commits to the default branch.
2. Release Please opens/updates a release PR (version bump + CHANGELOG).
3. Merge the release PR to create the tag and GitHub Release.
4. `release-assets.yml` then packs the NuGet packages (`PlayerData.Core`,
   `PlayerData.MessagePipe`, `PlayerData.R3`, `PlayerData.VitalRouter`) at the
   released version and attaches the `.nupkg` files to the GitHub Release.

To publish to NuGet.org as well, add a `NUGET_KEY` secret and uncomment the
push step in `.github/workflows/release-assets.yml`.
