# Launcher and docs cleanup

The repo now has a .NET 11 launcher at `src/Kare.CopilotLauncher`. The user-facing README stays focused on setup and configuration. The deeper runtime, security, and model details remain in `plan.md`, `.github/copilot-instructions.md`, and the `findings/` folder.

This keeps the public entry point short while preserving the device-specific constraints and launch boundaries in the repo guidance.

The launcher supports the requested flow:

- explicit device IP or host name, protected config, or a remembered last working address
- protected config in `~/.config/kare/device.env`
- SSH tunnel to the device
- health check before launch
- Copilot CLI BYOK env setup
- small self-contained publish targets for Linux, Windows, and macOS on x64 and arm64

The first launcher draft treated any first argument as the device address and let protected config override an explicit address. That broke `copilot-kare -i "Review this repository"` and made command-line overrides unreliable. The parser now treats a leading option as a Copilot argument, and address precedence is explicit argument, environment, protected config, then the last working address.

The launcher is too small to justify the cross-OS limits of Native AOT. The project uses full trimming, single-file compression, and RID-specific self-contained publishing instead. This keeps startup and distribution simple while allowing one Linux build machine to produce the Linux, Windows, and macOS release binaries.
