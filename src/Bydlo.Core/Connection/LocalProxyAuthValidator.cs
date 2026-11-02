using Bydlo.Core.Models;

namespace Bydlo.Core.Connection;

/// <summary>Pure validation for the "Allow LAN" proxy option (issue #80).</summary>
public static class LocalProxyAuthValidator
{
    public const string LoginRequiredKey = "lan.auth.login.required";
    public const string PasswordRequiredKey = "lan.auth.password.required";

    /// <summary>Returns the localization key of the problem, or null when the settings are consistent.</summary>
    public static string? Validate(AppSettings settings, string? password) =>
        !settings.AllowLan ? null
        : string.IsNullOrWhiteSpace(settings.LocalProxyUsername) ? LoginRequiredKey
        : string.IsNullOrWhiteSpace(password) ? PasswordRequiredKey
        : null;
}
