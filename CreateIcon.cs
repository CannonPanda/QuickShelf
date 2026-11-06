// Build-time only: generates a multiresolution icon without third-party tools.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

static class CreateIcon {
    static Bitmap Draw(int size) {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap)) {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.ScaleTransform(size / 16f, size / 16f);
            using (var folder = new GraphicsPath())
            using (var back = new SolidBrush(Color.FromArgb(210, 137, 26)))
            using (var front = new SolidBrush(Color.FromArgb(255, 199, 65)))
            using (var bolt = new SolidBrush(Color.FromArgb(30, 52, 77))) {
                folder.AddLines(new PointF[] { new PointF(1,5), new PointF(1,3), new PointF(2,2), new PointF(6,2), new PointF(8,4), new PointF(14,4), new PointF(15,5), new PointF(15,13), new PointF(14,14), new PointF(2,14), new PointF(1,13) });
                folder.CloseFigure(); g.FillPath(back, folder);
                folder.Reset();
                folder.AddLines(new PointF[] { new PointF(1,6), new PointF(15,6), new PointF(15,13), new PointF(14,14), new PointF(2,14), new PointF(1,13) });
                folder.CloseFigure(); g.FillPath(front, folder);
                g.FillPolygon(bolt, new PointF[] { new PointF(9,5), new PointF(5.5f,10), new PointF(8,10), new PointF(7,13.5f), new PointF(12,8), new PointF(9,8), new PointF(10,5) });
            }
        }
        return bitmap;
    }
    static void Main(string[] args) {
        int[] sizes = {16,20,24,32,48,64,256};
        using (var writer = new BinaryWriter(File.Create(args[0]))) {
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            foreach (int size in sizes) {
                int length = 40 + size * size * 4 + ((size + 31) / 32) * 4 * size;
                writer.Write((byte)(size == 256 ? 0 : size)); writer.Write((byte)(size == 256 ? 0 : size));
                writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32);
                writer.Write(length); writer.Write(offset); offset += length;
            }
            foreach (int size in sizes) using (var bitmap = Draw(size)) {
                int maskRow = ((size + 31) / 32) * 4;
                writer.Write(40); writer.Write(size); writer.Write(size * 2); writer.Write((ushort)1); writer.Write((ushort)32);
                writer.Write(0); writer.Write(size * size * 4); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
                for (int y = size - 1; y >= 0; y--) for (int x = 0; x < size; x++) {
                    Color c = bitmap.GetPixel(x,y); writer.Write(c.B); writer.Write(c.G); writer.Write(c.R); writer.Write(c.A);
                }
                for (int y = size - 1; y >= 0; y--) {
                    var row = new byte[maskRow];
                    for (int x = 0; x < size; x++) if (bitmap.GetPixel(x,y).A == 0) row[x / 8] |= (byte)(128 >> (x % 8));
                    writer.Write(row);
                }
            }
        }
        if (args.Length > 1) using (var preview = Draw(256)) preview.Save(args[1], ImageFormat.Png);
    }
}
