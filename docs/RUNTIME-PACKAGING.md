# Windows runtime packaging

Windows releases are framework-dependent single-file applications. Do not
bundle .NET, including indirectly inside an embedded helper. Backspace and
Backspace.config remain in the user's chosen folder. The Microsoft .NET 10
x64 runtime is a separate Windows prerequisite; Avalonia does not require
the Windows Desktop runtime.

## Startup and missing runtime

The small native host is built from `src/Backspace.Host/host.c`. It uses the
SDK's normal apphost and single-file bundle markers, then calls the matching
hostfxr entry point in the same process. This preserves Backspace.exe as the
process identity, which matters for portable paths, updates, single-instance
handling and Windows services. It does not extract a second managed program
into a data folder or wrap the service in a child process.

Runtime discovery uses the machine-wide x64 registration or the standard
Program Files location. It does not accept DOTNET_ROOT or the current
directory as a runtime source. In particular, a privileged service must not
load a runtime selected by an unprivileged client's environment.

When a normal interactive launch needs the runtime, Backspace offers setup.
It downloads the pinned official Microsoft installer, checks its SHA-512
against Microsoft's release metadata and verifies its Windows signature.
The verified file remains locked against modification or replacement until
the installer exits; verification and elevation cannot use different bytes.
The installer runs with `/install /passive /norestart`; Windows may request
administrator approval. After success, Backspace starts again without
moving its executable or changing its data. A retry marker prevents repeated
automatic setup if installation completes but the runtime still cannot load.
Downloads and installers are disposable and removed after the attempt.
Removing the runtime makes this setup available again on a later launch.
The application itself runs as the signed-in user. Normal use and manual
backups do not require administrator access when .NET 10 is available. The
missing-runtime setup may request administrator approval; optional VSS
snapshots and backups before sign-in also need approval and remain unavailable
without it.

Scheduled operations, service startup, update recovery and other noninteractive
commands must return failure promptly when the runtime is missing. They must
never wait for invisible installation prompts. The missing-executable fallback
is also native, so it can record its warning without .NET.

## Build and maintenance

`scripts/build-native-host.ps1` builds the native host and fallback using the
pinned portable Zig C compiler. The compiler archive's SHA-256 is checked
before extraction. The default development cache is inside the repository's
ignored obj directory; set BACKSPACE_NATIVE_COMPILER to an existing verified
zig.exe to reuse it. No developer tool installation is required.

`src/Backspace.Host/runtime.json` pins the x64 runtime installer URL, version,
release date and SHA-512 from Microsoft's official release metadata. Review
that pin when preparing a release. Download URLs cannot be supplied by the
user or by an update server response at runtime. Runtime updates remain
Microsoft's responsibility once installed; Backspace does not replace an
existing compatible runtime merely to match its installer pin.

The Windows release target rejects SelfContained=true. Release scripts and
GitHub Actions must use `--self-contained false`. The unsupported macOS/Linux
publish examples do not use this Windows host.

## Required acceptance

Use an isolated Windows VM, never uninstall a runtime on the developer's
laptop. Test a machine without .NET, a cancelled download, declined UAC,
failed/offline download, an existing compatible runtime, removal followed by
reinstallation, and a damaged download that must not execute. Check that
unattended launches fail without dialogs. Also repeat the actual packaged
update/recovery tests and service startup with this host. A working build or
an existing-runtime launch is not proof of the missing-runtime setup flow.

Run `scripts/test-native-host.ps1 -OutputDirectory <scratch-folder>` for
the native verification checks. It downloads the pinned installer but never
executes it. The actual production verification function must accept the
authentic file, prevent writes and deletion while its lock is held, release
that lock, and reject the deliberately damaged copy. The fixture is deleted
after the test. Use a repository-local scratch folder for review work.

The bundle marker and apphost binding contract are described in Microsoft's
[apphost implementation](https://github.com/dotnet/runtime/blob/v10.0.0/src/native/corehost/corehost.cpp)
and [bundle marker](https://github.com/dotnet/runtime/blob/v10.0.0/src/native/corehost/apphost/bundle_marker.cpp).
Their MIT notice is retained in src/Backspace.Host/THIRD-PARTY-NOTICES.
