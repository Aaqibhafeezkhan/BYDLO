# Development shell: `nix develop`, then follow docs/BUILD.md (agents keep `source scripts/env.sh` and `--mock`).
{ pkgs, bydlo }:
pkgs.mkShell (
  {
    packages = [
      bydlo.dotnet-sdk
      pkgs.go
      pkgs.powershell
      pkgs.squashfsTools
      pkgs.dpkg
      pkgs.fakeroot
    ];

    # NixOS has no global /usr/lib: native libraries that Avalonia, SkiaSharp and libsecret
    # load at runtime must be visible to `dotnet run`.
    LD_LIBRARY_PATH = pkgs.lib.makeLibraryPath bydlo.runtimeLibraries;
    DOTNET_ROOT = "${bydlo.dotnet-sdk}/share/dotnet";
  }
  // bydlo.protocEnv
)
