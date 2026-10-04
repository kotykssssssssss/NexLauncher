using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NexLauncher.Models;
using SkiaSharp;

namespace NexLauncher.Services.Skins;

public sealed class SkinValidator
{
    public const int MaxBytes = 256 * 1024;
    public Task<SkinImage> ImportAsync(string path, CancellationToken token) => Task.Run(async () =>
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);
        return Validate(await ReadBoundedAsync(stream, MaxBytes, token), token);
    }, token);

    public static async Task<byte[]> ReadBoundedAsync(Stream stream, int limit, CancellationToken token)
    {
        using var result = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (result.Length + count > limit) throw new SkinException(limit == MaxBytes
                ? "Файл скина слишком велик. Максимум — 256 КБ."
                : "Ответ сервиса скинов слишком велик. Попробуй позже.");
            result.Write(buffer, 0, count);
        }
        return result.ToArray();
    }

    public SkinImage Validate(byte[] bytes, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (bytes.Length > MaxBytes) throw new SkinException("Файл скина слишком велик. Максимум — 256 КБ.");
        if (bytes.Length < 33 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new SkinException("Нужен настоящий PNG-файл скина.");
        var width = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
        if (width != 64 || height is not (32 or 64)) throw new SkinException("Скин Java Edition должен иметь размер 64×64 или 64×32 пикселя.");
        CheckChunks(bytes, token);
        using var stream = new MemoryStream(bytes, false);
        using var codec = SKCodec.Create(stream);
        if (codec is null || codec.EncodedFormat != SKEncodedImageFormat.Png || codec.Info.Width != 64 || codec.Info.Height != height || codec.FrameCount > 1)
            throw new SkinException("Не удалось прочитать PNG. Анимированные PNG не поддерживаются.");
        using var bitmap = new SKBitmap(new SKImageInfo(64, (int)height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        if (codec.GetPixels(bitmap.Info, bitmap.GetPixels()) != SKCodecResult.Success)
            throw new SkinException("PNG повреждён или содержит неполные данные.");
        var pixels = new byte[64 * 64 * 4];
        System.Runtime.InteropServices.Marshal.Copy(bitmap.GetPixels(), pixels, 0, 64 * (int)height * 4);
        if (!Enumerable.Range(0, 64 * (int)height).Any(i => pixels[i * 4 + 3] != 0))
            throw new SkinException("Скин полностью прозрачный. Выбери изображение с видимой основной текстурой.");
        if (height == 32) ConvertLegacy(pixels);
        var normalized = false;
        foreach (var area in new[] { (0, 0, 32, 16), (0, 16, 64, 32), (16, 48, 48, 64) })
            for (var y = area.Item2; y < area.Item4; y++) for (var x = area.Item1; x < area.Item3; x++)
            { var index = (y * 64 + x) * 4 + 3; normalized |= pixels[index] != 255; pixels[index] = 255; }
        using var clean = new SKBitmap(new SKImageInfo(64, 64, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, clean.GetPixels(), pixels.Length);
        using var image = SKImage.FromBitmap(clean); using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return new SkinImage(png.ToArray(), pixels, height == 32, normalized);
    }

    public static void ValidateModel(SkinImage image, SkinModel model)
    {
        if (!Enum.IsDefined(model)) throw new SkinException("Выбери Classic или Slim.");
        if (image.Legacy && model == SkinModel.Slim) throw new SkinException("Скины 64×32 рассчитаны на Classic. Для Slim выбери скин 64×64.");
    }
    private static void CheckChunks(byte[] bytes, CancellationToken token)
    {
        var offset = 8; var first = true; var ended = false;
        while (offset < bytes.Length)
        {
            token.ThrowIfCancellationRequested();
            if (bytes.Length - offset < 12) throw new SkinException("PNG повреждён: неполный блок.");
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
            if (length > bytes.Length - offset - 12) throw new SkinException("PNG повреждён: неверная длина блока.");
            var type = System.Text.Encoding.ASCII.GetString(bytes, offset + 4, 4);
            if (first && (type != "IHDR" || length != 13) || !first && type == "IHDR" || type == "acTL")
                throw new SkinException("Некорректный или анимированный PNG.");
            var crc = uint.MaxValue;
            for (var i = offset + 4; i < offset + 8 + length; i++)
            { crc ^= bytes[i]; for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u); }
            if (~crc != BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 8 + (int)length, 4)))
                throw new SkinException("PNG повреждён: контрольная сумма не совпала.");
            offset += (int)length + 12; first = false;
            if (type == "IEND") { ended = length == 0 && offset == bytes.Length; break; }
        }
        if (!ended) throw new SkinException("PNG повреждён: отсутствует корректное завершение.");
    }
    private static void ConvertLegacy(byte[] pixels)
    {
        // Same mirrored limb layout used by Minecraft's legacy skin conversion.
        foreach (var copy in new[] { (4,16,16,32,4,4), (8,16,16,32,4,4), (0,20,24,32,4,12), (4,20,16,32,4,12),
            (8,20,8,32,4,12), (12,20,16,32,4,12), (44,16,-8,32,4,4), (48,16,-8,32,4,4),
            (40,20,0,32,4,12), (44,20,-8,32,4,12), (48,20,-16,32,4,12), (52,20,-8,32,4,12) })
            for (var y = 0; y < copy.Item6; y++) for (var x = 0; x < copy.Item5; x++)
                Array.Copy(pixels, ((copy.Item2 + y) * 64 + copy.Item1 + copy.Item5 - 1 - x) * 4,
                    pixels, ((copy.Item2 + copy.Item4 + y) * 64 + copy.Item1 + copy.Item3 + x) * 4, 4);
        var opaqueHat = true;
        for (var y = 0; y < 16; y++) for (var x = 32; x < 64; x++) opaqueHat &= pixels[(y * 64 + x) * 4 + 3] >= 128;
        if (opaqueHat) for (var y = 0; y < 16; y++) for (var x = 32; x < 64; x++) pixels[(y * 64 + x) * 4 + 3] = 0;
    }
}
