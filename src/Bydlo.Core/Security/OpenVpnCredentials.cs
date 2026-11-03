namespace Bydlo.Core.Security;

/// <summary>OpenVPN auth files contain exactly two nonempty lines.</summary>
public static class OpenVpnCredentials
{
    public static void Validate(string? username, string? password)
    {
        if (!Valid(username) || !Valid(password))
        { throw new FormatException("OpenVPN requires a username and password without line breaks."); }
    }

    private static bool Valid(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.IndexOfAny(['\r', '\n', '\0']) < 0;
}
