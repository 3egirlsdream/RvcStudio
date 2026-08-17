param(
    [Parameter(Mandatory = $true)]
    [string] $InputPath,

    [Parameter(Mandatory = $true)]
    [string] $OutputPng,

    [Parameter(Mandatory = $true)]
    [string[]] $OutputIco
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

if (-not ('RvcStudioIconBuilder' -as [type])) {
    $drawingAssembly = [System.Drawing.Bitmap].Assembly.Location
    $drawingDirectory = Split-Path $drawingAssembly
    $gdiPlusAssembly = Join-Path $drawingDirectory 'System.Private.Windows.GdiPlus.dll'
    $windowsCoreAssembly = Join-Path $drawingDirectory 'System.Private.Windows.Core.dll'
    $drawingPrimitivesAssembly = Join-Path $drawingDirectory 'System.Drawing.Primitives.dll'
    Add-Type -ReferencedAssemblies @($drawingAssembly, $drawingPrimitivesAssembly, $gdiPlusAssembly, $windowsCoreAssembly) -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

public static class RvcStudioIconBuilder
{
    private static bool IsCheckerboardPixel(byte b, byte g, byte r)
    {
        int min = Math.Min(r, Math.Min(g, b));
        int max = Math.Max(r, Math.Max(g, b));
        return min >= 232 && max - min <= 16;
    }

    public static Bitmap ExtractTransparentForeground(string inputPath)
    {
        using (var input = new Bitmap(inputPath))
        {
            var bitmap = new Bitmap(input.Width, input.Height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.DrawImageUnscaled(input, 0, 0);
            }

            var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            var data = bitmap.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                int stride = Math.Abs(data.Stride);
                byte[] pixels = new byte[stride * bitmap.Height];
                Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);

                int width = bitmap.Width;
                int height = bitmap.Height;
                bool[] background = new bool[width * height];
                int[] queue = new int[width * height];
                int head = 0;
                int tail = 0;

                Action<int, int> enqueueIfBackground = (x, y) =>
                {
                    int index = y * width + x;
                    if (background[index]) return;
                    int offset = y * stride + x * 4;
                    if (!IsCheckerboardPixel(pixels[offset], pixels[offset + 1], pixels[offset + 2])) return;
                    background[index] = true;
                    queue[tail++] = index;
                };

                for (int x = 0; x < width; x++)
                {
                    enqueueIfBackground(x, 0);
                    enqueueIfBackground(x, height - 1);
                }
                for (int y = 1; y < height - 1; y++)
                {
                    enqueueIfBackground(0, y);
                    enqueueIfBackground(width - 1, y);
                }

                while (head < tail)
                {
                    int index = queue[head++];
                    int x = index % width;
                    int y = index / width;
                    if (x > 0) enqueueIfBackground(x - 1, y);
                    if (x + 1 < width) enqueueIfBackground(x + 1, y);
                    if (y > 0) enqueueIfBackground(x, y - 1);
                    if (y + 1 < height) enqueueIfBackground(x, y + 1);
                }

                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int index = y * width + x;
                        int offset = y * stride + x * 4;
                        if (background[index])
                        {
                            pixels[offset] = 0;
                            pixels[offset + 1] = 0;
                            pixels[offset + 2] = 0;
                            pixels[offset + 3] = 0;
                        }
                        else
                        {
                            pixels[offset + 3] = 255;
                        }
                    }
                }

                Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            return bitmap;
        }
    }

    public static Bitmap Resize(Bitmap source, int size)
    {
        var result = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        result.SetResolution(96, 96);
        using (var graphics = Graphics.FromImage(result))
        {
            graphics.Clear(Color.Transparent);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.DrawImage(source, new Rectangle(0, 0, size, size));
        }
        return result;
    }

    public static void SavePng(Bitmap source, string outputPath, int size)
    {
        using (var resized = Resize(source, size))
        {
            resized.Save(outputPath, ImageFormat.Png);
        }
    }

    public static void SaveIco(Bitmap source, string outputPath, int[] sizes)
    {
        var frames = new byte[sizes.Length][];
        for (int i = 0; i < sizes.Length; i++)
        {
            int size = sizes[i];
            using (var resized = Resize(source, size))
            using (var stream = new MemoryStream())
            {
                resized.Save(stream, ImageFormat.Png);
                frames[i] = stream.ToArray();
            }
        }

        using (var stream = File.Create(outputPath))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)sizes.Length);

            int offset = 6 + sizes.Length * 16;
            for (int i = 0; i < sizes.Length; i++)
            {
                int size = sizes[i];
                writer.Write((byte)(size == 256 ? 0 : size));
                writer.Write((byte)(size == 256 ? 0 : size));
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((ushort)1);
                writer.Write((ushort)32);
                writer.Write((uint)frames[i].Length);
                writer.Write((uint)offset);
                offset += frames[i].Length;
            }

            foreach (byte[] frame in frames)
            {
                writer.Write(frame);
            }
        }
    }
}
'@
}

$resolvedInput = (Resolve-Path -LiteralPath $InputPath).Path
$pngDirectory = Split-Path -Parent $OutputPng
if ($pngDirectory) {
    New-Item -ItemType Directory -Path $pngDirectory -Force | Out-Null
}

$foreground = [RvcStudioIconBuilder]::ExtractTransparentForeground($resolvedInput)
try {
    [RvcStudioIconBuilder]::SavePng($foreground, $OutputPng, 1024)
    foreach ($icoPath in $OutputIco) {
        $icoDirectory = Split-Path -Parent $icoPath
        if ($icoDirectory) {
            New-Item -ItemType Directory -Path $icoDirectory -Force | Out-Null
        }
        [RvcStudioIconBuilder]::SaveIco($foreground, $icoPath, @(16, 24, 32, 48, 64, 128, 256))
    }
}
finally {
    $foreground.Dispose()
}
