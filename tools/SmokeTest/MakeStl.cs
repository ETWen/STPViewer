using System.IO;

namespace SmokeTest;

/// <summary>Generate a binary STL grid with two triangles per cell and noncoplanar vertices.</summary>
internal static class MakeStl
{
    public static void Run(string path, int triCount)
    {
        // Two triangles per cell; use a square grid large enough for the requested count.
        int cells = System.Math.Max(1, triCount / 2);
        int grid = (int)System.Math.Ceiling(System.Math.Sqrt(cells));
        int tris = grid * grid * 2;

        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        bw.Write(new byte[80]);          // header
        bw.Write((uint)tris);            // triangle count

        static void WriteTri(BinaryWriter w, float ax, float ay, float az,
            float bx, float by, float bz, float cx, float cy, float cz)
        {
            for (int i = 0; i < 3; i++) w.Write(0f);   // normal (0,0,0)
            w.Write(ax); w.Write(ay); w.Write(az);
            w.Write(bx); w.Write(by); w.Write(bz);
            w.Write(cx); w.Write(cy); w.Write(cz);
            w.Write((ushort)0);                        // attribute byte count
        }

        float Z(int x, int y) => (float)(System.Math.Sin(x * 0.1) * System.Math.Cos(y * 0.1) * 5.0);
        for (int y = 0; y < grid; y++)
            for (int x = 0; x < grid; x++)
            {
                float x0 = x, x1 = x + 1, y0 = y, y1 = y + 1;
                WriteTri(bw, x0, y0, Z(x, y), x1, y0, Z(x + 1, y), x1, y1, Z(x + 1, y + 1));
                WriteTri(bw, x0, y0, Z(x, y), x1, y1, Z(x + 1, y + 1), x0, y1, Z(x, y + 1));
            }

        Console.WriteLine($"Wrote {tris:N0} triangles to {path} ({new FileInfo(path).Length:N0} bytes)");
    }
}
