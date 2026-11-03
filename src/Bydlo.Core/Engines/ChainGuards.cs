using Bydlo.Core.Models;

namespace Bydlo.Core.Engines;

/// <summary>Relay-chain support boundary (issue #75). Only sing-box and Xray carry chains.</summary>
public static class ChainGuards
{
    /// <summary>Throws an honest <c>error.chain.engine</c> when a chain reaches an engine without relay support.</summary>
    public static void ThrowIfChained(EngineKind kind, EngineStartRequest request)
    {
        if (request.Chain is not null)
        {
            throw new EngineException($"{kind} cannot carry a relay chain. Use sing-box or Xray.", localizationKey: "error.chain.engine");
        }
    }
}
