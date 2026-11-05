{
  description = "BYDLO — Bypass Your Digital Limitations Online";

  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";

  outputs =
    { self, nixpkgs }:
    let
      systems = [
        "x86_64-linux"
        "aarch64-linux"
      ];
      forAllSystems = f: nixpkgs.lib.genAttrs systems (system: f nixpkgs.legacyPackages.${system});
    in
    {
      packages = forAllSystems (pkgs: rec {
        bydlo = pkgs.callPackage ./nix/package.nix { };
        bydlo-minimal = bydlo.override { withEngines = false; };
        default = bydlo;
      });

      apps = forAllSystems (pkgs: {
        default = {
          type = "app";
          program = nixpkgs.lib.getExe self.packages.${pkgs.stdenv.hostPlatform.system}.bydlo;
        };
      });

      overlays.default = final: _prev: {
        bydlo = final.callPackage ./nix/package.nix { };
      };

      nixosModules.default = import ./nix/module.nix self;

      devShells = forAllSystems (pkgs: {
        default = import ./nix/shell.nix {
          inherit pkgs;
          bydlo = self.packages.${pkgs.stdenv.hostPlatform.system}.bydlo;
        };
      });

      formatter = forAllSystems (pkgs: pkgs.nixfmt);
    };
}
