using Bydlo.Core.Import;
using Bydlo.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;

namespace Bydlo.Tests;

public sealed class DependencyCompatibilityTests
{
    [Fact]
    public void Mvvm_generator_keeps_partial_properties_and_command_notifications_working()
    {
        var model = new DependencySmokeViewModel();
        var changes = new List<string?>();
        var commandChanges = 0;
        model.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        model.PingCommand.CanExecuteChanged += (_, _) => commandChanges++;
        Assert.False(model.PingCommand.CanExecute(null));
        model.Enabled = true;
        Assert.Equal([nameof(model.Enabled)], changes);
        Assert.Equal(1, commandChanges);
        Assert.True(model.PingCommand.CanExecute(null));
        model.PingCommand.Execute(null);
        Assert.Equal(1, model.CallCount);
    }

    [Theory]
    [InlineData("socks5://user:password@127.0.0.1:1080#test")]
    [InlineData("socks5://user:password@127.0.0.1:1080#%D0%A2%D0%B5%D1%81%D1%82")]
    public void Zxing_pixel_round_trip_preserves_importable_profile_links(string link)
    {
        var writer = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.QR_CODE,
            Options = new QrCodeEncodingOptions { Width = 320, Height = 320, Margin = 1 },
        };
        var pixels = writer.Write(link);
        var source = new RGBLuminanceSource(pixels.Pixels, pixels.Width, pixels.Height, RGBLuminanceSource.BitmapFormat.BGRA32);
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions { TryHarder = true, PossibleFormats = [BarcodeFormat.QR_CODE] },
        };
        var result = reader.Decode(source);
        Assert.NotNull(result);
        Assert.Equal(link, result.Text);
        var imported = new ProfileImporter().ImportText(result.Text);
        Assert.Empty(imported.Errors);
        Assert.Equal(ProtocolKind.Socks5, Assert.Single(imported.Profiles).Profile.Protocol);
    }
}

internal sealed partial class DependencySmokeViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PingCommand))]
    public partial bool Enabled { get; set; }

    public int CallCount { get; private set; }

    [RelayCommand(CanExecute = nameof(Enabled))]
    private void Ping() => CallCount++;
}
