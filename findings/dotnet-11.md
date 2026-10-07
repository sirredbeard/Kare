# .NET 11 notes

Checked 2026-10-01.

.NET 11 is at RC 1. General availability is expected in November 2026. Research can use RC 1. Production should pin GA.

Useful runtime work:

Runtime-native async reduces async overhead and improves stack traces.

Native AOT interface dispatch is faster and smaller.

Arm64 has hardware FP16 instructions for `Half`.

New memory stream wrappers can expose memory and sequences without a copy.

Process APIs add signaling, exit status, safer lookup, and tighter handle control. These are useful if GenieX runs as a supervised sidecar.

In-process crash reports can capture managed state before exit on Linux.

System.Text.Json source generation and polymorphism continue to improve. Kare still needs compile-time metadata for Native AOT.

MemoryCache has built-in OpenTelemetry metrics.

Async validation is available for DataAnnotations and options.

EF Core 11 is still in development. It requires .NET 11.

EF Core 11 removes unnecessary joins and ordering in common include queries, strips no-op casts that can block indexes, and adds query translations.

Do not assume Npgsql or pgvector support EF Core 11 until matching packages are published. Providers generally do not work across EF Core major versions.

Use the latest mutually compatible EF Core, Npgsql, and pgvector versions, even if that means a .NET 11 app uses EF Core 10 initially.

Sources:

https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/overview

https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-11.0/whatsnew

https://learn.microsoft.com/en-us/ef/core/providers/

## Measured 2026-10-02, scaffolding the solution

Built the real projects. These are the things that actually bit, recorded so nobody re-learns them.

Dev box: x86_64 Azure Linux 4.0, kernel 6.18.31, 4 cores, ~19 GB RAM. SDK 11.0.100-rc.1.26425.128. podman 5.8.4, no docker. `qemu-aarch64-static` is registered in `/proc/sys/fs/binfmt_misc/qemu-aarch64`, which is what makes emulated aarch64 testing possible at all here.

`NETSDK1057` preview warnings appear on every project. Informational, ignore.

### Native AOT cannot be cross linked from x86_64

ILCompiler happily produces aarch64 objects. The link then fails:

`/usr/bin/ld.bfd: unrecognised emulation mode: aarch64linux`

Stock binutils on this host has no aarch64 emulation built in. The fix is to link inside an aarch64 image, not to fight the linker. `build/Containerfile.arm64` pins Ubuntu 24.04 with clang, lld, zlib1g-dev, libkrb5-dev, libssl-dev, libicu-dev. `build/publish-arm64.sh` drives it and auto-detects podman or docker. This settles the open question in the plan: the pinned ARM64 build environment is required, not a preference.

### AOT analyzer catches real hazards

`[Range(typeof(TimeSpan), "...", "...")]` trips `IL2026`. It routes through a reflection-based `TypeConverter`, which Native AOT cannot keep. Replaced with integer seconds properties plus derived `TimeSpan` getters. Worth keeping `TreatWarningsAsErrors` on for exactly this reason, because it caught the problem at scaffold time instead of at first AOT publish on the board.

### Packages already in the .NET 11 shared framework

`NU1510` fires if you reference these explicitly. They ship in the framework now:

- `Microsoft.Extensions.Options`
- `Microsoft.Extensions.Diagnostics.Abstractions`
- `Microsoft.Extensions.Hosting.Abstractions`

These are NOT in the framework and still need a package reference:

- `Microsoft.Extensions.Options.DataAnnotations`
- `Microsoft.Extensions.Logging.Console`

### NativeLibrary.TryLoad has two very different overloads

`NativeLibrary.TryLoad(string)` does not apply the `lib` prefix, does not append `.so`, and does not probe the assembly directory. It is not the same thing `DllImport` does. Using it made the device probe report "native did not load" for a library that loads fine.

Use `TryLoad(name, assembly, DllImportSearchPath, out handle)` anchored on the owning assembly to match `DllImport` semantics. This was a false negative on a gate check, which is the worst kind of bug in a tool whose entire job is reporting truth.

### Package versions as of this check

- `Microsoft.Extensions.AI` 10.10.0, `.Abstractions` 10.10.1
- `Microsoft.Extensions.Hosting` 11.0.0-rc.1.26425.128
- `Microsoft.Extensions.Http.Resilience` 10.10.0
- `Microsoft.Extensions.Caching.Hybrid` 10.10.0
- `xunit.v3` 3.0.0, `xunit.runner.visualstudio` 3.1.0, `Microsoft.NET.Test.Sdk` 17.14.1

xUnit v3 adds analyzer `xUnit1051`: any call taking a `CancellationToken` inside a test must pass `TestContext.Current.CancellationToken`. With `TreatWarningsAsErrors` that is a build break, not a suggestion.

## Measured 2026-10-02, ASP.NET Core under the AOT analyzers

Building the service with `TreatWarningsAsErrors` and the AOT analyzers on produced nine errors immediately. All were real, and all have supported fixes. Worth writing down because the fixes are not obvious from the error text.

`MapGet` and `MapPost` taking a `Delegate` raise IL2026 and IL3050. The supported answer is the Request Delegate Generator: set `<EnableRequestDelegateGenerator>true</EnableRequestDelegateGenerator>`. That generates the request delegates at compile time and the warnings disappear. It is turned on automatically by `PublishAot`, but not during an ordinary Debug build, which is where the analyzer runs.

`ValidateDataAnnotations()` raises IL2026 because it walks the options type by reflection. The supported answer is the options validation source generator:

```csharp
[OptionsValidator]
public sealed partial class InferenceLimitsValidator : IValidateOptions<InferenceLimits>;
```

registered with `AddSingleton<IValidateOptions<InferenceLimits>, InferenceLimitsValidator>()` and kept with `.ValidateOnStart()`. Same checks, same attributes on the options type, no reflection. It works across assemblies as long as the options properties are public.

Also set `<EnableConfigurationBindingGenerator>true</EnableConfigurationBindingGenerator>` so `Bind` and `Get<T>` use generated binders.

`NU1510` list differs for the Web SDK. `Microsoft.Extensions.Options.DataAnnotations` is in the ASP.NET Core shared framework and must not be referenced from a `Microsoft.NET.Sdk.Web` project, even though it does need a reference from a plain `Microsoft.NET.Sdk` library. Easy to get wrong when moving code between projects.

`WebApplication.CreateSlimBuilder` plus `ConfigureHttpJsonOptions` inserting a `JsonSerializerContext` into `TypeInfoResolverChain` is the pattern that keeps the whole JSON path source generated.

## Measured 2026-10-02 later, Native AOT ARM64 actually works

Ran `build/publish-arm64.sh bench/Kare.DeviceProbe artifacts/kare-probe --aot`. It worked. The container approach is correct and this is no longer theoretical.

Output: `kare-probe`, 4.1 MB, ELF 64-bit LSB pie executable, ARM aarch64, dynamically linked, stripped. Plus a separate 7.4 MB `.dbg`. The three ONNX native libraries sit beside it.

Ran that binary in `ubuntu:24.04` arm64 under qemu. It started, loaded `libonnxruntime-genai.so`, inspected the system, and printed the full report. Gate 4 is now closed for both the JIT self-contained path and the Native AOT path.

One practical trap. The first attempt spent 45 minutes failing to restore because NuGet downloads kept dying with "The response ended prematurely" while a large model download was running at the same time. An emulated aarch64 network stack plus a saturated link is not a combination restore survives. Fix was to bind mount the host package cache into the container:

```
-v "${NUGET_PACKAGES:-$HOME/.nuget/packages}":/nuget
```

Restore then took 32 seconds instead of timing out forever. The packages are architecture tagged, so sharing an x86_64 host cache with an aarch64 container is safe. This is now in `publish-arm64.sh`.

Build time with a warm cache under emulation was roughly 6 minutes, most of it in "Generating native code". On a real aarch64 build machine it would be a fraction of that.

## Measured 2026-10-02, the ASP.NET Core service under Native AOT on ARM64

`build/publish-arm64.sh src/Kare.Service artifacts/kare --aot` produced a 12 MB stripped aarch64 PIE executable. Restore 23 seconds from the mounted host cache, then "Generating native code", then done. No trimming warnings, no AOT warnings.

Ran it in `ubuntu:24.04` aarch64 with the stock `appsettings.json`, which has an empty `ModelPath`. It failed at startup with exactly the right error:

```
Microsoft.Extensions.Options.OptionsValidationException: ModelPath: The ModelPath field is required.
   at Kare.Inference.OnnxGenAI.OnnxGenAiBackend..ctor(IOptions`1 options, ILogger`1 logger)
```

That is the result I wanted. It proves three things actually work at runtime and not just at compile time under AOT: the `[OptionsValidator]` source generated validator, the configuration binding source generator reading `appsettings.json`, and dependency injection constructing the backend. A misconfigured service refuses to open a listener instead of starting and failing on the first request.

One detail in the stack worth noting. `System.Reflection.DynamicInvokeInfo` appears in the DI activation path. Microsoft.Extensions.DependencyInjection still uses reflection based constructor invocation under AOT, and it works because the types are rooted by the annotations. It is not a violation, but it does mean DI activation is not free. For the hot path that does not matter, because everything is resolved once at startup.

Running an aarch64 ASP.NET Core process under qemu is slow to start. Measure startup on the real board, not here.
