using Bydlo.Core.Engines;
using Bydlo.Core.Engines.Mock;
using Bydlo.Core.Platform;
using Bydlo.Core.Processes;
using Bydlo.Core.Security;
using Bydlo.Engines.OpenVpn;
using Bydlo.Engines.SingBox;
using Bydlo.Engines.TgWsProxy;
using Bydlo.Engines.WireGuard;
using Bydlo.Engines.Xray;
using Bydlo.Engines.Zapret;

namespace Bydlo.Engines;

/// <summary>Registers every real adapter. Lower priority number wins when engines overlap.</summary>
public static class EngineRegistryFactory
{
    public static EngineRegistry Create(
        IProcessRunner runner,
        LogRedactor redactor,
        EngineLocator locator,
        IPrivilegeService privileges,
        TgWsProxyRuntime tgRuntime,
        Func<IEngine>? mockFactory = null)
    {
        var registry = new EngineRegistry(mockFactory ?? (() => new MockEngine()));
        registry
            .Register(SingBoxEngine.Info, () => new SingBoxEngine(runner, redactor, locator, privileges), priority: 10)
            .Register(XrayEngine.Info, () => new XrayEngine(runner, redactor, locator), priority: 20)
            .Register(TunnelServiceEngine.WireGuardInfo, () => new TunnelServiceEngine(false, runner, redactor, locator, privileges), priority: 30)
            .Register(TunnelServiceEngine.AmneziaInfo, () => new TunnelServiceEngine(true, runner, redactor, locator, privileges), priority: 10)
            .Register(OpenVpnEngine.Info, () => new OpenVpnEngine(runner, redactor, locator, privileges), priority: 10)
            .Register(ZapretEngine.Info, () => new ZapretEngine(runner, redactor, locator, privileges))
            .Register(TgWsProxyEngine.Info, () => new TgWsProxyEngine(runner, redactor, locator, tgRuntime));
        return registry;
    }
}
