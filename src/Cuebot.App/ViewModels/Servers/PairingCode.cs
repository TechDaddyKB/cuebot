using Avalonia.Media.Imaging;
using QRCoder;

namespace Cuebot.App.ViewModels.Servers;

/// <summary>The phone pairing link as a QR code image.</summary>
internal static class PairingCode
{
    /// <summary>A black-on-white QR code, 8 pixels per module (with the standard quiet zone).</summary>
    public static Bitmap Render(string link) => new(new MemoryStream(Png(link)));

    internal static byte[] Png(string link)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(link, QRCodeGenerator.ECCLevel.M);
        return new PngByteQRCode(data).GetGraphic(8);
    }
}
