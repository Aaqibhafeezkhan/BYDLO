using Bydlo.Core.Import;

namespace Bydlo.Tests;

/// <summary>
/// Issue #221: parsers for untrusted input (links, base64 subscriptions, Clash YAML,
/// sing-box/Xray JSON, .ovpn/.conf files, QR payloads) must never crash the import:
/// every adversarial input either imports or is reported, and only the documented
/// <see cref="FormatException"/> / <see cref="ArgumentException"/> contract may escape.
/// Inputs are small and fixed (no randomness, no network, no timing asserts).
/// </summary>
public class ParserRobustnessTests
{
    public static TheoryData<string> AdversarialInputs()
    {
        var data = new TheoryData<string>
        {
            "",
            " ",
            "\n",
            "# just a comment",
            "://",
            "vless://",
            "vless://not-a-uuid",
            "vless://u@h:99999?encryption=none",
            "vmess://",
            "vmess://!!!not-base64!!!",
            "vmess://e30=",
            "trojan://",
            "trojan://pass@",
            "ss://",
            "ss://@@@",
            "hysteria2://",
            "hysteria2://a@h:1,2,3,abc",
            "tuic://",
            "tuic://uuid-only@h:443",
            "socks5://",
            "socks5://u@h",
            "http://",
            "wireguard://",
            "[Interface]\nPrivateKey = \n[Peer]\nEndpoint = [",
            "client\ndev tun\nremote [:::1",
            "client\ndev tun\nremote h 0 udp extra1 extra2 extra3",
            "proxies:",
            "proxies: [",
            "port: not-a-port",
            "{",
            "{}",
            """{"outbounds": []}""",
            new string('{', 150),
            "e30=",
            "!!!",
            "aGVsbG8=",
            "0:0:0:0:0:0:0:1",
            "\0vless://x",
            "vless://u@h:443\n[Interface]\nPrivateKey = x\n[Peer]\nEndpoint = [",
        };
        data.Add(new string('a', 4000));
        data.Add("vless://" + new string('a', 2000));
        return data;
    }

    [Theory]
    [MemberData(nameof(AdversarialInputs))]
    public void Adversarial_input_never_escapes_unexpected_exception(string input)
    {
        ImportResult? result = null;
        try
        {
            result = new ProfileImporter().ImportText(input);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            // Documented contract: malformed input may surface as a parse error.
        }

        if (result is not null)
        {
            var hasContent = input
                .Split('\n')
                .Select(l => l.Trim())
                .Any(l => l.Length > 0 && !l.StartsWith('#'));
            if (hasContent)
            {
                Assert.True(result.Profiles.Count > 0 || result.Errors.Count > 0);
            }
            else
            {
                Assert.Empty(result.Profiles);
            }
        }
    }
}
