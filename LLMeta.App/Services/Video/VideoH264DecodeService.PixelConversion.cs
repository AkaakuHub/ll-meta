namespace LLMeta.App.Services;

public sealed partial class VideoH264DecodeService
{
    private static byte[] ConvertRgb32ToBgra(byte[] rgb32, int width, int height)
    {
        var requiredLength = checked(width * height * 4);
        if (rgb32.Length < requiredLength)
        {
            throw new InvalidOperationException(
                $"RGB32 buffer too small. got={rgb32.Length} required={requiredLength}"
            );
        }

        if (rgb32.Length == requiredLength)
        {
            return rgb32;
        }

        var sourceStride = rgb32.Length / height;
        if (sourceStride < width * 4)
        {
            throw new InvalidOperationException(
                $"RGB32 stride too small. stride={sourceStride} width={width}"
            );
        }

        var trimmed = new byte[requiredLength];
        for (var y = 0; y < height; y++)
        {
            var sourceOffset = y * sourceStride;
            var targetOffset = y * width * 4;
            Buffer.BlockCopy(rgb32, sourceOffset, trimmed, targetOffset, width * 4);
        }

        return trimmed;
    }

    private static byte[] ConvertNv12ToBgra(byte[] nv12, int width, int height)
    {
        var minimumRequired = checked(width * height * 3 / 2);
        if (nv12.Length < minimumRequired)
        {
            throw new InvalidOperationException(
                $"NV12 buffer too small. got={nv12.Length} required={minimumRequired}"
            );
        }

        var sourceStride = width;
        var sourceHeight = height;
        var guessedHeight = (nv12.Length * 2) / (width * 3);
        if (guessedHeight >= height && (width * guessedHeight * 3) / 2 == nv12.Length)
        {
            sourceHeight = guessedHeight;
        }

        var yPlaneSize = sourceStride * sourceHeight;
        var uvPlaneStart = yPlaneSize;
        var uvStride = sourceStride;
        if (uvPlaneStart + (uvStride * (sourceHeight / 2)) > nv12.Length)
        {
            throw new InvalidOperationException(
                $"NV12 layout invalid. length={nv12.Length} stride={sourceStride} sourceHeight={sourceHeight}"
            );
        }

        var bgra = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var uvRow = (y / 2) * uvStride;
            for (var x = 0; x < width; x++)
            {
                var yValue = nv12[y * sourceStride + x];
                var uvIndex = uvPlaneStart + uvRow + (x & ~1);
                var uValue = nv12[uvIndex];
                var vValue = nv12[uvIndex + 1];

                var c = yValue - 16;
                var d = uValue - 128;
                var e = vValue - 128;

                var r = ClampToByte((298 * c + 409 * e + 128) >> 8);
                var g = ClampToByte((298 * c - 100 * d - 208 * e + 128) >> 8);
                var b = ClampToByte((298 * c + 516 * d + 128) >> 8);

                var dst = (y * width + x) * 4;
                bgra[dst] = (byte)b;
                bgra[dst + 1] = (byte)g;
                bgra[dst + 2] = (byte)r;
                bgra[dst + 3] = 255;
            }
        }

        return bgra;
    }

    private static int ClampToByte(int value)
    {
        if (value < 0)
        {
            return 0;
        }

        if (value > 255)
        {
            return 255;
        }

        return value;
    }
}
