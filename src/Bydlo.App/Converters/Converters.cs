using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Bydlo.App.Services;

public sealed class ByteCountConverter : IValueConverter
{
    public static ByteCountConverter Instance { get; } = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is long bytes ? Format.Bytes(bytes) : value;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class ProtocolLabelConverter : IValueConverter
{
    public static ProtocolLabelConverter Instance { get; } = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Bydlo.Core.Models.ProtocolKind protocol ? Bydlo.App.ViewModels.Labels.Protocol(protocol) : value;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Resource key → icon geometry (used by navigation items).</summary>
public sealed class IconConverter : IValueConverter
{
    public static IconConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string key && Application.Current?.TryFindResource(key, out var res) == true ? res as Geometry : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Localization key → text (for keys that come from view models). Re-evaluated when items are re-rendered.</summary>
public sealed class LocConverter : IValueConverter
{
    public static LocConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string key ? Loc.Instance[key] : value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class RoutingRuleLabelConverter : IValueConverter
{
    public static RoutingRuleLabelConverter Instance { get; } = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        Bydlo.Core.Routing.RoutingCondition condition => Loc.Instance["routing.rules.condition." + condition],
        Bydlo.Core.Routing.RoutingAction action => Loc.Instance["routing.rules.action." + action],
        _ => value,
    };
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Enum / string value → localized label using a key prefix given as parameter, e.g. "game.".</summary>
public sealed class PrefixLocConverter : IValueConverter
{
    public static PrefixLocConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? null : Loc.Instance[$"{parameter}{value.ToString()!.ToLowerInvariant()}"];

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>bool → opacity (1 / 0) so visibility changes can be animated with a DoubleTransition.</summary>
public sealed class BoolToOpacityConverter : IValueConverter
{
    public static BoolToOpacityConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? 1.0 : 0.0;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
