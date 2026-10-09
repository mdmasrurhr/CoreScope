# Contributing

Thanks for helping. Bug reports with a `selftest.json` from a different PC are especially useful (see the bug template).

## Build and test

You need the .NET 10 SDK (`winget install Microsoft.DotNet.SDK.10`).

```
dotnet build src/CoreScope/CoreScope.csproj
dotnet test tests/CoreScope.Tests
tools\package.ps1        # zip and Setup.exe; needs Inno Setup 6 (winget install JRSoftware.InnoSetup)
```

Project layout is in [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md).

## Adding an insight

1. Add the measurement to `HealthCollector` if it is not collected yet.
2. Add the finding in `InsightEngine*.cs`. Report a pass as well as a problem, and show the measurement and rule.
3. Give every warning or critical finding a way forward in `FixesFor`: a fix CoreScope performs, a Windows settings page, a
   CoreScope page, or a step-by-step guide. A unit test fails if one has none.
4. A fix that changes something needs a confirmation and a test (use the fake tool runner in `FixAndHealthTests`).

## Pull requests

Keep them focused, run the tests, and describe what you changed and how you checked it. UI changes: include a screenshot in light and dark.

## Releasing (maintainers)

1. Set `<Version>` in `src/CoreScope/CoreScope.csproj` and update `CHANGELOG.md`.
2. Commit, then tag and push: `git tag v1.2.0` and `git push origin v1.2.0`.
3. The **Release** workflow tests, packages and publishes the release. If the repository secret `WINGET_TOKEN` is set, it also opens the winget-pkgs pull request.
