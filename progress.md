# Kare progress

Updated 2026-10-08 on the VENTUNO Q.

## Current branch

- Branch: `pr-14-rewrite`
- Pull request: #14, `Add estimated model costs and local SLM savings`
- Base: `copilot/close-issues-7-10-11-13`
- The branch adds a provider-boundary routing mode on top of the PR work.

## Verified

- The board identifies as VENTUNO Q (`arduino,monza`).
- The local .NET 11 RC SDK is available.
- The focused test project builds and runs on the device: 154 passed.
- Routing modes default to `Personal`; `Work` requires explicit selection.
- Cloud routes can be limited to `Personal`, `Work`, or `Both`.
- The pricing test helper enables cloud configuration so configured average prices are available to local savings estimates.

## Validation notes

- `dotnet test tests/Kare.Tests/Kare.Tests.csproj --no-restore --nologo`: 154 passed.
- `dotnet build src/Kare.Service/Kare.Service.csproj --no-restore --nologo`: succeeded with 0 warnings and 0 errors.
- The test project build completed all referenced Kare projects successfully.

## Remaining

- Review the branch diff before committing.
- Push the branch to PR #14 only after the tests pass.
- Work-provider mapping remains an explicit route boundary, not repository ownership or authentication.
