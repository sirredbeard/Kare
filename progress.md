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
- The focused test project builds and runs on the device: 157 passed.
- Routing modes default to `Personal`; `Work` requires explicit selection.
- Cloud routes can be limited to `Personal`, `Work`, or `Both`.
- The launcher now resolves `explicit_work`, protected repository mappings,
  configured organizations, and the built-in `neverenginsupport` and `dotnet`
  organization matches.
- Work mode is sent to Kare with a fixed provider header and personal routes are
  filtered before dispatch.
- The pricing test helper enables cloud configuration so configured average prices are available to local savings estimates.

## Validation notes

- `dotnet test tests/Kare.Tests/Kare.Tests.csproj --no-restore --nologo`: 157 passed.
- `dotnet build src/Kare.Service/Kare.Service.csproj --no-restore --nologo`: succeeded with 0 warnings and 0 errors.
- The test project build completed all referenced Kare projects successfully.
- Routing mode and reason metadata are included in route records and cache
  fingerprints. Invalid mode or reason combinations are rejected.

## Remaining

- Set explicit Personal and Work route permissions in protected configuration
  after the approved Work provider is confirmed. Do not commit that configuration.
- Work-provider mapping remains an explicit route boundary, not repository ownership or authentication.

## Issue #15

- Reproduced the plain-terminal failure with the published launcher artifact.
- The launcher now waits for SSH authentication and tunnel establishment before
  checking `/health` or starting Copilot.
- SSH uses a private control socket and backgrounds only after authentication.
- Added a regression test for the control-master arguments.
- Current validation: 158 tests passed. The service build passed with one
  transient file-lock warning caused by parallel test and build execution; rerun
  serially before the fix PR.
