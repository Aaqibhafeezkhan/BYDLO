using System.Text;
using Bydlo.Core.Import;

namespace Bydlo.Tests;

/// <summary>
/// Issue #221: parsers for untrusted input (links, base64 subscriptions, Clash YAML,
/// sing-box/Xray JSON, .ovpn/.conf files, QR payloads) must never crash the import:
/// every adversarial input either imports or is reported in <see cref="ImportResult.Errors"/>.
/// Inputs are fixed and small (no randomness, no network, no timing asserts); a seeded
/// generative check below covers the space between the fixed cases.
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
    public void Adversarial_input_is_reported_never_thrown(string input)
    {
        // No try/catch here by design: any exception escaping ImportText fails the test.
        var result = new ProfileImporter().ImportText(input);
        Assert.NotNull(result);
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

    [Fact]
    public void Base64_subscription_body_with_mixed_lines_imports_good_and_reports_bad()
    {
        const string good = "vless://11111111-2222-3333-4444-555555555555@example.com:443?encryption=none&security=reality&sni=a.com&fp=chrome&pbk=PUBKEY123&sid=ab12&type=tcp&flow=xtls-rprx-vision#Good";
        var body = good + "\nnot-a-link\ntrojan://pass@";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(body));

        var result = new ProfileImporter().ImportText(encoded);

        var profile = Assert.Single(result.Profiles);
        Assert.Equal("Good", profile.Profile.Name);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Seeded_mutations_never_escape_the_importer()
    {
        // Generative check with a fixed seed: reproducible, bounded (<= 512 chars each),
        // fast (200 inputs), no network. Any exception escaping ImportText fails the test.
        var random = new Random(221);
        const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789://@[]{}.,;?&=#%+ \t\"'\\\n-_";
        string[] schemes = ["vless://", "vmess://", "trojan://", "ss://", "hysteria2://", "tuic://", "socks5://", "wireguard://", "", "", ""];
        var valid = new ProfileImporter().ImportText("vless://11111111-2222-3333-4444-555555555555@example.com:443?encryption=none#Seed");
        Assert.Single(valid.Profiles);

        for (var i = 0; i < 200; i++)
        {
            var input = Mutate(random, alphabet, schemes);
            var result = new ProfileImporter().ImportText(input);
            Assert.NotNull(result);
        }
    }

    private static string Mutate(Random random, string alphabet, string[] schemes)
    {
        var length = random.Next(0, 512);
        var builder = new StringBuilder(schemes[random.Next(schemes.Length)]);
        for (var i = 0; i < length; i++)
        {
            var c = alphabet[random.Next(alphabet.Length)];
            // Random case flips and bracket/colon injections hit the bracket-parsing branches.
            builder.Append(random.Next(8) == 0 ? char.ToUpperInvariant(c) : c);
        }
        return builder.ToString();
    }
}
