namespace Bydlo.Core.Security;

/// <summary>
/// Last line of defence against starting real network engines or privileged network changes
/// in a development / CI environment. When <c>BYDLO_SAFE_DEV=1</c> is set, every real launch is refused.
/// </summary>
public static class NetworkSafetyGuard
{
    public const string SafeDevVariable = "BYDLO_SAFE_DEV";

    private static readonly AsyncLocal<bool?> TestLock = new();

    /// <summary>True when the development safety lock is active.</summary>
    public static bool IsLocked => IsTruthy(Environment.GetEnvironmentVariable(SafeDevVariable)) || TestLock.Value == true;

    /// <summary>Tests may add a lock in their async flow, but cannot disable the environment safety lock.</summary>
    internal static void OverrideForTests(bool? locked) => TestLock.Value = locked;

    /// <summary>Throws <see cref="NetworkSafetyException"/> if real network actions are locked.</summary>
    public static void EnsureAllowed(string action)
    {
        if (IsLocked)
        {
            throw new NetworkSafetyException(
                $"Refused to {action}: {SafeDevVariable}=1 (development safety lock). Use Mock Mode for testing.");
        }
    }

    private static bool IsTruthy(string? value) =>
        value is not null && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("yes", StringComparison.OrdinalIgnoreCase));
}

public sealed class NetworkSafetyException(string message) : InvalidOperationException(message);
