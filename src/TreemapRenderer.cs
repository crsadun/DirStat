using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;

namespace DirStat
{
    public sealed class TreemapItem
    {
        public DirNode Node;
        public RectangleF Rect;
    }

    public sealed class TreemapRenderer
    {
        // Light direction (toward the surface, upper-left). Will be normalized.
        private static readonly double LX = -1.0;
        private static readonly double LY = -1.0;
        private static readonly double LZ = 4.0;
        private static readonly double LMag = Math.Sqrt(LX * LX + LY * LY + LZ * LZ);

        // Cushion height parameter (0..1). Larger = more pronounced 3D look.
        private const double CushionHeight = 0.6;
        // Ambient light (so shadows don't go fully black).
        private const double Ambient = 0.18;

        private List<TreemapItem> _items;
        public IReadOnlyList<TreemapItem> Items
        {
            get { return _items; }
        }

        public void Render(Bitmap bmp, DirNode root, Func<DirNode, Color> colorOf, Color background)
        {
            if (bmp == null) return;
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(background);
            }
            _items = new List<TreemapItem>(1024);
            if (root == null) return;
            var bounds = new RectangleF(0, 0, bmp.Width, bmp.Height);
            Layout(root, bounds, _items);

            if (_items.Count == 0) return;

            // Direct pixel access.
            var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int stride = data.Stride;
                int width = bmp.Width;
                int height = bmp.Height;
                IntPtr scan0 = data.Scan0;
                unsafe
                {
                    byte* basePtr = (byte*)scan0.ToPointer();
                    // Initialize background bytes (BGRA premultiplied? Format32bppArgb is non-premul.)
                    int bgB = background.B, bgG = background.G, bgR = background.R;
                    for (int y = 0; y < height; y++)
                    {
                        byte* row = basePtr + y * stride;
                        for (int x = 0; x < width; x++)
                        {
                            row[0] = (byte)bgB;
                            row[1] = (byte)bgG;
                            row[2] = (byte)bgR;
                            row[3] = 255;
                            row += 4;
                        }
                    }

                    foreach (var it in _items)
                    {
                        if (it.Rect.Width < 1 || it.Rect.Height < 1) continue;
                        Color c = colorOf(it.Node);
                        DrawCushion(basePtr, stride, width, height, it.Rect, c);
                    }
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }

        public TreemapItem ItemAt(PointF p)
        {
            if (_items == null) return null;
            // Iterate; rectangles do not overlap so order doesn't matter for hit-testing.
            for (int i = 0; i < _items.Count; i++)
            {
                if (_items[i].Rect.Contains(p)) return _items[i];
            }
            return null;
        }

        // ---- Layout ----

        private static void Layout(DirNode node, RectangleF rect, List<TreemapItem> outItems)
        {
            if (rect.Width < 1f || rect.Height < 1f) return;
            if (!node.IsDirectory || node.Children == null || node.Children.Count == 0)
            {
                if (!node.IsDirectory && node.Size > 0)
                    outItems.Add(new TreemapItem { Node = node, Rect = rect });
                return;
            }

            // Sum sizes of children that contribute area.
            long total = 0;
            int countContrib = 0;
            for (int i = 0; i < node.Children.Count; i++)
            {
                long s = node.Children[i].Size;
                if (s > 0) { total += s; countContrib++; }
            }
            if (total <= 0 || countContrib == 0) return;

            double areaScale = (double)rect.Width * (double)rect.Height / total;

            // Build areas array. Children are pre-sorted descending by Finalize_PostScan.
            var children = new List<DirNode>(countContrib);
            var areas = new List<double>(countContrib);
            for (int i = 0; i < node.Children.Count; i++)
            {
                long s = node.Children[i].Size;
                if (s <= 0) continue;
                children.Add(node.Children[i]);
                areas.Add(s * areaScale);
            }

            Squarify(children, areas, rect, outItems);
        }

        private static void Squarify(List<DirNode> nodes, List<double> areas, RectangleF rect,
                                     List<TreemapItem> outItems)
        {
            int idx = 0;
            int n = nodes.Count;
            while (idx < n)
            {
                if (rect.Width < 1f || rect.Height < 1f) return;
                double w = Math.Min(rect.Width, rect.Height);
                if (w <= 0) return;

                int rowStart = idx;
                double rowSum = areas[idx];
                double rowMin = areas[idx];
                double rowMax = areas[idx];
                idx++;

                while (idx < n)
                {
                    double a = areas[idx];
                    double newSum = rowSum + a;
                    double newMin = Math.Min(rowMin, a);
                    double newMax = Math.Max(rowMax, a);
                    double oldWorst = Worst(rowSum, rowMin, rowMax, w);
                    double newWorst = Worst(newSum, newMin, newMax, w);
                    if (newWorst <= oldWorst)
                    {
                        rowSum = newSum; rowMin = newMin; rowMax = newMax;
                        idx++;
                    }
                    else break;
                }

                // Lay out [rowStart, idx) into a strip on the short side.
                if (rect.Width >= rect.Height)
                {
                    // Vertical strip on the left.
                    float stripW = (float)(rowSum / rect.Height);
                    if (stripW > rect.Width) stripW = rect.Width;
                    float y = rect.Y;
                    float yEnd = rect.Y + rect.Height;
                    for (int i = rowStart; i < idx; i++)
                    {
                        float h = (i == idx - 1) ? (yEnd - y) : (float)(areas[i] / stripW);
                        var r = new RectangleF(rect.X, y, stripW, h);
                        if (nodes[i].IsDirectory)
                            Layout(nodes[i], r, outItems);
                        else
                            outItems.Add(new TreemapItem { Node = nodes[i], Rect = r });
                        y += h;
                    }
                    rect = new RectangleF(rect.X + stripW, rect.Y, rect.Width - stripW, rect.Height);
                }
                else
                {
                    // Horizontal strip on the top.
                    float stripH = (float)(rowSum / rect.Width);
                    if (stripH > rect.Height) stripH = rect.Height;
                    float x = rect.X;
                    float xEnd = rect.X + rect.Width;
                    for (int i = rowStart; i < idx; i++)
                    {
                        float ww = (i == idx - 1) ? (xEnd - x) : (float)(areas[i] / stripH);
                        var r = new RectangleF(x, rect.Y, ww, stripH);
                        if (nodes[i].IsDirectory)
                            Layout(nodes[i], r, outItems);
                        else
                            outItems.Add(new TreemapItem { Node = nodes[i], Rect = r });
                        x += ww;
                    }
                    rect = new RectangleF(rect.X, rect.Y + stripH, rect.Width, rect.Height - stripH);
                }
            }
        }

        private static double Worst(double sum, double min, double max, double w)
        {
            double s2 = sum * sum;
            double w2 = w * w;
            return Math.Max(w2 * max / s2, s2 / (w2 * min));
        }

        // ---- Cushion shading ----

        private static unsafe void DrawCushion(byte* basePtr, int stride, int imgW, int imgH,
                                               RectangleF r, Color baseColor)
        {
            int x0 = (int)Math.Floor(r.X);
            int y0 = (int)Math.Floor(r.Y);
            int x1 = (int)Math.Ceiling(r.X + r.Width);
            int y1 = (int)Math.Ceiling(r.Y + r.Height);
            if (x0 < 0) x0 = 0;
            if (y0 < 0) y0 = 0;
            if (x1 > imgW) x1 = imgW;
            if (y1 > imgH) y1 = imgH;
            if (x1 - x0 < 1 || y1 - y0 < 1) return;

            double cx = r.X + r.Width * 0.5;
            double cy = r.Y + r.Height * 0.5;
            double hw = r.Width * 0.5;
            double hh = r.Height * 0.5;
            if (hw < 0.5) hw = 0.5;
            if (hh < 0.5) hh = 0.5;

            // Parabolic cushion: z(nx, ny) = (1 - nx^2) * (1 - ny^2),  nx in [-1,1], ny in [-1,1]
            // dz/dx = -2 * nx * (1 - ny^2) / hw   (gradient in pixel-space)
            // dz/dy = -2 * ny * (1 - nx^2) / hh
            // Surface normal (unnormalized): N = (-dz/dx, -dz/dy, 1)
            // We scale gradients by a "cushion height" factor to control depth.

            double k = CushionHeight;
            // Border darkening: rectangles with very small dimensions skip cushion math (single-pixel slivers).
            bool tinyW = (x1 - x0) < 3;
            bool tinyH = (y1 - y0) < 3;

            int bR = baseColor.R, bG = baseColor.G, bB = baseColor.B;

            for (int y = y0; y < y1; y++)
            {
                byte* row = basePtr + y * stride + x0 * 4;
                double ny = (y + 0.5 - cy) / hh;
                if (ny < -1.0) ny = -1.0; else if (ny > 1.0) ny = 1.0;
                double oneMinusNy2 = 1.0 - ny * ny;

                for (int x = x0; x < x1; x++)
                {
                    double intensity;
                    if (tinyW || tinyH)
                    {
                        intensity = 0.7;
                    }
                    else
                    {
                        double nx = (x + 0.5 - cx) / hw;
                        if (nx < -1.0) nx = -1.0; else if (nx > 1.0) nx = 1.0;
                        double oneMinusNx2 = 1.0 - nx * nx;
                        // gradients (scaled by k, divided by half-extent to get pixel-space slope)
                        double dzdx = -2.0 * k * nx * oneMinusNy2 / hw;
                        double dzdy = -2.0 * k * ny * oneMinusNx2 / hh;
                        // Normal = (-dzdx, -dzdy, 1)
                        double nxn = -dzdx;
                        double nyn = -dzdy;
                        double nzn = 1.0;
                        double nMag = Math.Sqrt(nxn * nxn + nyn * nyn + nzn * nzn);
                        // dot(L, N) / (|L| * |N|)
                        double dot = (LX * nxn + LY * nyn + LZ * nzn) / (LMag * nMag);
                        if (dot < 0) dot = 0;
                        intensity = Ambient + (1.0 - Ambient) * dot;
                        if (intensity > 1.0) intensity = 1.0;
                    }

                    int rr = (int)(bR * intensity);
                    int gg = (int)(bG * intensity);
                    int bb = (int)(bB * intensity);
                    if (rr > 255) rr = 255;
                    if (gg > 255) gg = 255;
                    if (bb > 255) bb = 255;

                    row[0] = (byte)bb;
                    row[1] = (byte)gg;
                    row[2] = (byte)rr;
                    row[3] = 255;
                    row += 4;
                }
            }
        }
    }

    // Picks a color per file based on its extension.
    public sealed class ExtensionColorMap
    {
        // Top-N most-prominent extensions get a slot in this palette; others fall through to a hashed pastel.
        // Order matters — first slot to last.
        private static readonly Color[] Palette =
        {
            Color.FromArgb(  0, 102, 204), // blue
            Color.FromArgb( 51, 153,  51), // green
            Color.FromArgb(204,   0,   0), // red
            Color.FromArgb(255, 153,   0), // orange
            Color.FromArgb(153,  51, 204), // purple
            Color.FromArgb( 51, 153, 204), // teal
            Color.FromArgb(204, 153,  51), // mustard
            Color.FromArgb(204, 102, 153), // pink
            Color.FromArgb(102, 153,  51), // olive
            Color.FromArgb( 51,  51, 153), // navy
            Color.FromArgb(153, 102,  51), // brown
            Color.FromArgb(102,  51, 153), // violet
        };

        private static readonly Color FallbackColor = Color.FromArgb(128, 128, 128);

        private readonly Dictionary<string, Color> _map =
            new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, Color> Map { get { return _map; } }

        public Color Fallback { get { return FallbackColor; } }

        // Build a color map from extension stats: top extensions by size get palette colors,
        // the rest get gray.
        public void Build(ExtensionStats stats)
        {
            _map.Clear();
            if (stats == null) return;
            var list = stats.GetSortedBySize();
            for (int i = 0; i < list.Count && i < Palette.Length; i++)
            {
                _map[list[i].Extension] = Palette[i];
            }
        }

        public Color GetForNode(DirNode n)
        {
            if (n == null || n.IsDirectory) return FallbackColor;
            string ext = System.IO.Path.GetExtension(n.Name);
            if (string.IsNullOrEmpty(ext)) ext = "<no extension>";
            Color c;
            return _map.TryGetValue(ext, out c) ? c : FallbackColor;
        }
    }
}
