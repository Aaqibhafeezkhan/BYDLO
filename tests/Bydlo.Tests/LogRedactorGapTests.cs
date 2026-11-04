using Bydlo.Core.Security;

namespace Bydlo.Tests;

/// <summary>Issue #213: LogRedactor gaps found by audit — a bug report built on the
/// redactor must not leak credentials, so these shapes have to be masked.</summary>
public class LogRedactorGapTests
{
    private static string Redact(string text) => new LogRedactor().Redact(text);

    [Fact]
    public void Ss_userinfo_with_slashes_is_masked_host_kept()
    {
        var redacted = Redact("ss://YWVzLTI1Ni1nY206cGFzcy9zZWNyZXQ/Pw==@1.2.3.4:8388#x");
        Assert.Equal("ss://***@1.2.3.4:8388#x", redacted);
    }

    [Fact]
    public void Ss_body_without_userinfo_is_masked_whole()
    {
        var redacted = Redact("ss://YWVzLTI1Ni1nY206c2VjcmV0cGFzcjAxLjIuMy40OjgzODg=#legacy");
        Assert.Equal("ss://***#legacy", redacted);
    }

    [Fact]
    public void Vmess_body_is_masked_whole()
    {
        var redacted = Redact("vmess://eyJ2IjoidjIiLCJwcyI6IngsImFkZCI6IjEuMi4zLjQiLCJwb3J0Ijo0NDN9");
        Assert.Equal("vmess://***", redacted);
    }

    [Fact]
    public void Trojan_userinfo_with_slash_is_masked_host_kept()
    {
        var redacted = Redact("trojan://pa/ss@host:443");
        Assert.Equal("trojan://***@host:443", redacted);
    }

    [Fact]
    public void Quoted_json_value_with_spaces_is_masked_whole()
    {
        var redacted = Redact("\"password\": \"correct horse battery staple\"");
        Assert.Equal("\"password\": ***", redacted);
    }

    [Fact]
    public void Bearer_token_is_masked()
    {
        var redacted = Redact("Authorization: Bearer abcdefghijklmnop");
        Assert.Equal("Authorization: Bearer ***", redacted);
    }

    [Fact]
    public void Auth_str_value_is_masked()
    {
        var redacted = Redact("auth_str=supersecretauth");
        Assert.Equal("auth_str=***", redacted);
    }

    [Fact]
    public void Previous_shapes_stay_masked()
    {
        Assert.Equal("vless://***@host:443", Redact("vless://11111111-2222-3333-4444-555555555555@host:443"));
        Assert.Equal("password=*** next", Redact("password=hunter2 next"));
        Assert.Contains(LogRedactor.Mask, Redact("id 11111111-2222-3333-4444-555555555555"), StringComparison.Ordinal);
    }
}
