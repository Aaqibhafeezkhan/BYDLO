# NixOS module: `imports = [ bydlo.nixosModules.default ]; programs.bydlo.enable = true;`
self:
{
  config,
  lib,
  pkgs,
  ...
}:
let
  cfg = config.programs.bydlo;
in
{
  options.programs.bydlo = {
    enable = lib.mkEnableOption "BYDLO desktop client";
    package = lib.mkOption {
      type = lib.types.package;
      default = self.packages.${pkgs.stdenv.hostPlatform.system}.bydlo;
      defaultText = lib.literalExpression "bydlo.packages.\${pkgs.stdenv.hostPlatform.system}.bydlo";
      description = "BYDLO package to install.";
    };
  };

  config = lib.mkIf cfg.enable {
    environment.systemPackages = [ cfg.package ];
  };
}
