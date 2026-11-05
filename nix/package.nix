{
  lib,
  buildDotnetModule,
  buildGoModule,
  dotnetCorePackages,
  copyDesktopItems,
  makeDesktopItem,
  protobuf,
  grpc,
  # Avalonia / SkiaSharp / libsecret are loaded with dlopen at runtime.
  fontconfig,
  freetype,
  libGL,
  libICE,
  libSM,
  libX11,
  libXcursor,
  libXext,
  libXi,
  libXrandr,
  libXrender,
  libxkbcommon,
  libsecret,
  glib,
  libnotify,
  # Engines found through PATH when no bundled or BYDLO-managed copy exists.
  sing-box,
  xray,
  openvpn,
  wireguard-tools,
  withEngines ? true,
}:

let
  version = lib.removeSuffix "\n" (
    builtins.head (
      builtins.match ".*<Version>([^<]+)</Version>.*" (builtins.readFile ../Directory.Build.props)
    )
  );

  src = lib.fileset.toSource {
    root = ../.;
    fileset = lib.fileset.unions [
      ../BYDLO.slnx
      ../Directory.Build.props
      ../Directory.Packages.props
      ../NuGet.config
      ../global.json
      ../engines
      ../licenses
      ../src
      ../tools/amneziawg
      ../themes
      ../LICENSE
      ../THIRD_PARTY_NOTICES.md
    ];
  };

  # Same pinned sources (go.mod / go.sum) that the release bundle builds.
  engineLock = builtins.fromJSON (builtins.readFile ../engines/engines.lock.json);
  amneziawgVersion = (lib.findFirst (e: e.id == "amneziawg") null engineLock.engines).version;
  amneziawg = buildGoModule {
    pname = "bydlo-amneziawg";
    version = amneziawgVersion;
    src = lib.fileset.toSource {
      root = ../tools/amneziawg;
      fileset = lib.fileset.fileFilter (
        f: f.hasExt "go" || f.name == "go.mod" || f.name == "go.sum"
      ) ../tools/amneziawg;
    };
    vendorHash = "sha256-7BXACIWxUAubFG+LeUQvdsZHogPpW4sk3LDW/V/gKq4=";
    env.CGO_ENABLED = 0;
    ldflags = [
      "-s"
      "-w"
    ];
    postInstall = ''
      mv $out/bin/amneziawg $out/bin/bydlo-amneziawg
    '';
    meta.mainProgram = "bydlo-amneziawg";
  };

  runtimeLibraries = [
    fontconfig
    freetype
    libGL
    libICE
    libSM
    libX11
    libXcursor
    libXext
    libXi
    libXrandr
    libXrender
    libxkbcommon
    libsecret
    glib
  ];

  # Grpc.Tools ships glibc binaries that cannot run in the sandbox; use the matching nixpkgs ones.
  protocEnv = {
    PROTOBUF_PROTOC = lib.getExe' protobuf "protoc";
    GRPC_PROTOC_PLUGIN = lib.getExe' grpc "grpc_csharp_plugin";
  };

  dotnet-sdk = dotnetCorePackages.sdk_10_0;
  dotnet-runtime = dotnetCorePackages.runtime_10_0;
in
buildDotnetModule {
  pname = "bydlo";
  __structuredAttrs = true;
  inherit
    version
    src
    dotnet-sdk
    dotnet-runtime
    ;

  projectFile = "src/Bydlo.Desktop/Bydlo.Desktop.csproj";
  nugetDeps = ./deps.json;
  executables = [ "BYDLO" ];

  # Framework-dependent build against the nixpkgs runtime: trimming and ReadyToRun only
  # apply to self-contained release bundles (see docs/BUILD.md).
  dotnetFlags = [
    "-p:PublishTrimmed=false"
    "-p:PublishReadyToRun=false"
    "-p:ContinuousIntegrationBuild=true"
  ];

  env = protocEnv;

  nativeBuildInputs = [ copyDesktopItems ];

  runtimeDeps = runtimeLibraries;

  postInstall = ''
    install -Dm644 src/Bydlo.App/Assets/bydlo-256.png $out/share/icons/hicolor/256x256/apps/bydlo.png
    install -Dm644 LICENSE THIRD_PARTY_NOTICES.md -t $out/share/doc/bydlo
  ''
  + lib.optionalString withEngines ''
    # Bundled layout from engines.lock.json: engines/<id>/<version>/<executable>.
    install -Dm755 ${lib.getExe amneziawg} $out/lib/bydlo/engines/amneziawg/${amneziawgVersion}/bydlo-amneziawg
  '';

  # Autostart and import associations must launch this wrapper, not the unwrapped apphost
  # (it cannot find its native libraries on its own). The invoked path is usually a stable
  # profile link (/run/current-system/sw/bin/BYDLO) that survives upgrades and garbage collection.
  makeWrapperArgs = [
    "--run"
    "case \"$0\" in /*) : \"\${BYDLO_LAUNCHER:=$0}\" ;; *) : \"\${BYDLO_LAUNCHER:=$PWD/$0}\" ;; esac; export BYDLO_LAUNCHER"
    "--suffix"
    "PATH"
    ":"
    (lib.makeBinPath (
      [
        libnotify
        glib.bin
      ]
      ++ lib.optionals withEngines [
        sing-box
        xray
        openvpn
        wireguard-tools
      ]
    ))
  ];

  postFixup = ''
    ln -s BYDLO $out/bin/bydlo
  '';

  desktopItems = [
    (makeDesktopItem {
      name = "bydlo";
      exec = "BYDLO";
      icon = "bydlo";
      desktopName = "BYDLO";
      genericName = "Bypass Your Digital Limitations Online";
      comment = "One app. Every protocol.";
      categories = [
        "Network"
        "Utility"
      ];
      startupWMClass = "BYDLO";
    })
  ];

  passthru = {
    inherit
      amneziawg
      runtimeLibraries
      protocEnv
      dotnet-sdk
      ;
  };

  meta = {
    description = "Bypass Your Digital Limitations Online — one app for every protocol";
    homepage = "https://github.com/endercodezz/BYDLO";
    license = lib.licenses.gpl3Plus;
    mainProgram = "BYDLO";
    platforms = [
      "x86_64-linux"
      "aarch64-linux"
    ];
  };
}
