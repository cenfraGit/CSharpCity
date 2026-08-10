using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Silk.NET.OpenGL;
using GdiPixelFormat = System.Drawing.Imaging.PixelFormat;
using GlPixelFormat = Silk.NET.OpenGL.PixelFormat;

namespace CSharpCity.Render;

/// <summary>
/// Rasterizes printable ASCII into a single GL texture at startup, so labels cost one texture bind
/// and one instanced draw regardless of how much text the city carries.
/// </summary>
[SupportedOSPlatform("windows6.1")]
public sealed class FontAtlas : IDisposable
{
    public const char FirstChar = ' ';   // 32
    public const char LastChar = '~';    // 126
    const int Columns = 16;
    const int CellPixels = 64;
    const float FontPixels = 42f;

    /// <summary>Index of the cell filled with solid white — used to draw label backing plaques.</summary>
    const int SolidCellIndex = 95;

    readonly GL _gl;
    readonly float[] _advances = new float[LastChar - FirstChar + 1];

    public uint Texture { get; }
    /// <summary>Quad edge length in em units. Cells are padded, so this is slightly over 1.</summary>
    public float CellEm => CellPixels / FontPixels;

    public FontAtlas(GL gl)
    {
        _gl = gl;

        int glyphCount = LastChar - FirstChar + 1;
        int rows = (glyphCount + 1 + Columns - 1) / Columns;
        int width = Columns * CellPixels;
        int height = rows * CellPixels;

        using var bitmap = new Bitmap(width, height, GdiPixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var font = new Font("Segoe UI", FontPixels, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var brush = new SolidBrush(Color.White))
        {
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;

            // GenericTypographic reports the true advance; the default format pads it.
            var format = (StringFormat)StringFormat.GenericTypographic.Clone();
            format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;

            for (int i = 0; i < glyphCount; i++)
            {
                char c = (char)(FirstChar + i);
                var (x, y) = CellOrigin(i);
                graphics.DrawString(c.ToString(), font, brush, x + 2, y + 2, format);
                _advances[i] = graphics.MeasureString(c.ToString(), font, PointF.Empty, format).Width
                               / FontPixels;
            }

            var (sx, sy) = CellOrigin(SolidCellIndex);
            graphics.FillRectangle(brush, sx, sy, CellPixels, CellPixels);
        }

        Texture = Upload(bitmap);
    }

    static (int X, int Y) CellOrigin(int index) =>
        (index % Columns * CellPixels, index / Columns * CellPixels);

    uint Upload(Bitmap bitmap)
    {
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly, GdiPixelFormat.Format32bppArgb);
        try
        {
            var texture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, texture);
            unsafe
            {
                _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8,
                    (uint)bitmap.Width, (uint)bitmap.Height, 0,
                    GlPixelFormat.Bgra, PixelType.UnsignedByte, (void*)data.Scan0);
            }
            _gl.GenerateMipmap(TextureTarget.Texture2D);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
                (int)GLEnum.LinearMipmapLinear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
                (int)GLEnum.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
            return texture;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    public float Advance(char c)
    {
        int index = c - FirstChar;
        return index >= 0 && index < _advances.Length ? _advances[index] : _advances['?' - FirstChar];
    }

    /// <summary>Total width of a string in em units.</summary>
    public float Measure(string text)
    {
        float width = 0f;
        foreach (char c in text) width += Advance(c);
        return width;
    }

    /// <summary>UV rectangle (u0, v0, u1, v1) of a glyph's cell.</summary>
    public (float U0, float V0, float U1, float V1) Uv(char c)
    {
        int index = c - FirstChar;
        if (index < 0 || index >= _advances.Length) index = '?' - FirstChar;
        return CellUv(index);
    }

    public (float U0, float V0, float U1, float V1) SolidUv()
    {
        // Sample the middle of the solid cell so mipmapping never bleeds in a neighbour.
        var (u0, v0, u1, v1) = CellUv(SolidCellIndex);
        float cx = (u0 + u1) * 0.5f, cy = (v0 + v1) * 0.5f;
        return (cx, cy, cx, cy);
    }

    (float, float, float, float) CellUv(int index)
    {
        int rows = (_advances.Length + 1 + Columns - 1) / Columns;
        float cellU = 1f / Columns, cellV = 1f / rows;
        float u = index % Columns * cellU, v = index / Columns * cellV;
        return (u, v, u + cellU, v + cellV);
    }

    public void Dispose() => _gl.DeleteTexture(Texture);
}
