namespace GewCoordinator;

/// <summary>
/// Decoder for the "EAST" sprite container (*.spr / *.spr.NCS).
///
/// Header : "EAST", u16 version, u16 frameCount, u16 0, u32 offsets[frameCount + 1]
///          (offsets are relative to the end of the table; the last one is the end of data).
/// Frame  : u32 0, u16 height, 15 byte row bitmap (bit y set => row y is stored),
///          then per stored row: u8 n, n * (u16 skip, u16 len, len palette indices).
///          skip + len of one row always sum to <see cref="Width"/>.
/// Transparency is expressed only by skipped pixels; palette index 0 is an opaque black.
/// </summary>
public sealed class Sprite
{
    public const int Width = 136;
    public const int Height = 120;

    /// <summary>Value of a pixel that is not drawn (a palette index is 0..255).</summary>
    public const ushort Transparent = 0xFFFF;

    /// <summary>Each frame is Width*Height palette indices, or <see cref="Transparent"/>.</summary>
    public ushort[][] Frames { get; }

    private Sprite(ushort[][] frames) => Frames = frames;

    public static Sprite Load(string path) => Parse(File.ReadAllBytes(path));

    public static Sprite Parse(byte[] b)
    {
        if (b.Length < 10 || b[0] != 'E' || b[1] != 'A' || b[2] != 'S' || b[3] != 'T')
            throw new InvalidDataException("EAST sprite ではありません");

        int count = BitConverter.ToUInt16(b, 6);
        const int table = 10;
        int dataBase = table + 4 * (count + 1);
        var frames = new ushort[count][];
        for (int i = 0; i < count; i++)
        {
            int start = dataBase + (int)BitConverter.ToUInt32(b, table + 4 * i);
            int end = dataBase + (int)BitConverter.ToUInt32(b, table + 4 * (i + 1));
            frames[i] = DecodeFrame(b, start, end);
        }
        return new Sprite(frames);
    }

    private static ushort[] DecodeFrame(byte[] b, int start, int end)
    {
        var img = new ushort[Width * Height];
        Array.Fill(img, Transparent);
        int height = Math.Min((int)BitConverter.ToUInt16(b, start + 4), Height);
        int p = start + 21;
        for (int y = 0; y < height; y++)
        {
            if (((b[start + 6 + (y >> 3)] >> (y & 7)) & 1) == 0) continue;
            int segments = b[p++];
            int x = 0;
            for (int k = 0; k < segments; k++)
            {
                int skip = BitConverter.ToUInt16(b, p);
                int len = BitConverter.ToUInt16(b, p + 2);
                p += 4;
                x += skip;
                if (x + len > Width || p + len > end)
                    throw new InvalidDataException("スプライトデータが不正です");
                for (int i = 0; i < len; i++) img[y * Width + x + i] = b[p + i];
                p += len;
                x += len;
            }
        }
        return img;
    }
}

/// <summary>256 colour palette (Data\Default.pal: 256 x B,G,R,X).</summary>
public sealed class Palette
{
    /// <summary>BGRA colours, all opaque (index 0 is black).</summary>
    public uint[] Colors { get; } = new uint[256];

    public static Palette Load(string path)
    {
        var b = File.ReadAllBytes(path);
        if (b.Length < 1024) throw new InvalidDataException("パレットファイルが小さすぎます");
        var pal = new Palette();
        for (int i = 0; i < 256; i++)
            pal.Colors[i] = 0xFF000000u | (uint)(b[i * 4 + 2] << 16) | (uint)(b[i * 4 + 1] << 8) | b[i * 4];
        return pal;
    }
}
