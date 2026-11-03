using System.Globalization;
using Bydlo.Core.Engines;

namespace Bydlo.Core.Models;

public sealed record DpiSettings
{
    public const string FragmentKey = "bydlo.tlsFragment";
    public const string SizeKey = "bydlo.fragmentSize";
    public const string IntervalKey = "bydlo.fragmentInterval";
    public const string MuxKey = "bydlo.mux";
    public const string PaddingKey = "bydlo.padding";

    public string Fragment { get; init; } = "off";
    public string Size { get; init; } = "100-200";
    public string Interval { get; init; } = "10-20";
    public string Mux { get; init; } = "off";
    public bool Padding { get; init; }

    public DpiSettings For(Profile profile) => this with
    {
        Fragment = profile.Option(FragmentKey) ?? Fragment,
        Size = profile.Option(SizeKey) ?? Size,
        Interval = profile.Option(IntervalKey) ?? Interval,
        Mux = profile.Option(MuxKey) ?? Mux,
        Padding = bool.TryParse(profile.Option(PaddingKey), out var padding) ? padding : Padding,
    };

    public int ValidateFormat()
    {
        if (Fragment is not ("off" or "tls" or "record") ||
            Mux is not ("off" or "smux" or "yamux" or "h2mux" or "xray" or "xudp") ||
            !TryRange(Size, 1, 16384, out _) || !TryRange(Interval, 0, 5000, out var interval))
        {
            throw new EngineException("profile.dpi.invalid", localizationKey: "profile.dpi.invalid");
        }
        return interval;
    }

    public int Validate(Profile profile, EngineKind engine)
    {
        var interval = ValidateFormat();
        var singBox = engine == EngineKind.SingBox;
        if (Mux != "off" && profile.Option(Keys.Flow)?.Contains("vision", StringComparison.OrdinalIgnoreCase) == true)
        {
            throw new EngineException("profile.dpi.vision", localizationKey: "profile.dpi.vision");
        }
        if ((singBox && Mux is "xray" or "xudp") || (!singBox && Mux is "smux" or "yamux" or "h2mux")
            || (!singBox && Fragment == "record"))
        {
            throw new EngineException("profile.dpi.unsupported", localizationKey: "profile.dpi.unsupported");
        }
        if (Mux != "off" && profile.Protocol is not (ProtocolKind.Vless or ProtocolKind.Vmess or ProtocolKind.Trojan or ProtocolKind.Shadowsocks))
        {
            throw new EngineException("profile.dpi.unsupported", localizationKey: "profile.dpi.unsupported");
        }
        if (Padding && (singBox ? Mux == "off" : profile.Option(Keys.Transport) is not ("xhttp" or "splithttp")))
        {
            throw new EngineException("profile.dpi.unsupported", localizationKey: "profile.dpi.unsupported");
        }
        return interval;
    }

    private static bool TryRange(string value, int min, int max, out int end)
    {
        end = 0;
        var parts = value.Split('-', 2);
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var start)
            || start < min || start > max)
        {
            return false;
        }
        end = start;
        return parts.Length == 1 ||
            (int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out end) && end >= start && end <= max);
    }
}
