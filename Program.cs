using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace QwenTerminal
{
    /*
        vskit terminal - a small violet console client for any
        OpenAI-compatible chat API (/chat/completions, streaming SSE).

        Everything you type goes to the model. Lines starting with '/'
        are local commands. Config is saved next to the exe in api.conf.

        Self-contained: C# on .NET Framework 4.0, no external assets.
        The hex mark is rasterised at runtime from polygon geometry.
    */

    internal static class Program
    {
        // ---------------- win32 ----------------

        private const int STD_OUTPUT_HANDLE = -11;
        private const int ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int which);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetConsoleMode(IntPtr handle, out int mode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleMode(IntPtr handle, int mode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleTextAttribute(IntPtr handle, short attribute);

        [DllImport("kernel32.dll")]
        private static extern uint GetOEMCP();

        private static bool IsWindows()
        {
            return Environment.OSVersion.Platform == PlatformID.Win32NT;
        }

        private static readonly Encoding OemEncoding = GetOem();

        private static Encoding GetOem()
        {
            if (!IsWindows()) return new UTF8Encoding(false);
            try { return Encoding.GetEncoding((int)GetOEMCP()); }
            catch { return Encoding.Default; }
        }

        // ---------------- style ----------------

        private class Style
        {
            public readonly string Ansi;
            public readonly short Attr;
            public Style(string ansi, short attr) { Ansi = ansi; Attr = attr; }
        }

        private static readonly Style P_BRIGHT = new Style("\x1b[37m", 7);       // bright gray
        private static readonly Style P_DEEP = new Style("\x1b[90m", 8);         // dark gray
        private static readonly Style P_SOFT = new Style("\x1b[97m", 15);        // light gray, replies
        private static readonly Style P_DIM = new Style("\x1b[90m", 8);          // dim gray, meta
        private static readonly Style P_PINK = new Style("\x1b[37m", 7);         // gray errors
        private static readonly Style TEXT = new Style("\x1b[37m", 7);           // input echo

        private static IntPtr hOut;
        private static bool vt;
        private static bool colour;
        private static bool asciiOnly;

        // ---------------- glyphs ----------------

        private const string G_FULL = "\u2588";
        private const string G_UP = "\u2580";
        private const string G_DN = "\u2584";
        private const string G_DH = "\u2550";
        private const string G_SPARK = "\u2726";
        private const string G_DOT = "\u00B7";
        private const string G_TRI_RIGHT = "\u25B6";

        private static string Rule(int n)
        {
            StringBuilder b = new StringBuilder(n);
            for (int i = 0; i < n; i++) b.Append(G_DH);
            return b.ToString();
        }

        // ---------------- 5x7 pixel font ----------------

        private static readonly string[] FONT_KEYS = new string[]
        {
            " ", "V", "S", "K", "I", "T", "E", "R", "M", "N", "A", "L"
        };

        private static readonly string[][] FONT_GLYPHS = new string[][]
        {
            new string[] { ".....", ".....", ".....", ".....", ".....", ".....", "....." },
            new string[] { "#...#", "#...#", "#...#", "#...#", ".#.#.", ".#.#.", "..#.." }, // V
            new string[] { ".####", "#....", "#....", ".###.", "....#", "....#", "####." }, // S
            new string[] { "#...#", "#..#.", "#.#..", "##...", "#.#..", "#..#.", "#...#" }, // K
            new string[] { ".###.", "..#..", "..#..", "..#..", "..#..", "..#..", ".###." }, // I
            new string[] { "#####", "..#..", "..#..", "..#..", "..#..", "..#..", "..#.." }, // T
            new string[] { "#####", "#....", "#....", "####.", "#....", "#....", "#####" }, // E
            new string[] { "####.", "#...#", "#...#", "####.", "#.#..", "#..#.", "#...#" }, // R
            new string[] { "#...#", "##.##", "#.#.#", "#.#.#", "#...#", "#...#", "#...#" }, // M
            new string[] { "#...#", "##..#", "#.#.#", "#.#.#", "#..##", "#...#", "#...#" }, // N
            new string[] { ".###.", "#...#", "#...#", "#####", "#...#", "#...#", "#...#" }, // A
            new string[] { "#....", "#....", "#....", "#....", "#....", "#....", "#####" }, // L
        };

        private static string[] Glyph(char c)
        {
            char key = char.ToUpperInvariant(c);
            for (int i = 0; i < FONT_KEYS.Length; i++)
                if (FONT_KEYS[i].Length > 0 && FONT_KEYS[i][0] == key) return FONT_GLYPHS[i];
            return FONT_GLYPHS[0];
        }

        private static List<string> Pixels(string text)
        {
            const int H = 7;
            List<string> rows = new List<string>(H);
            for (int r = 0; r < H; r++) rows.Add("");
            for (int i = 0; i < text.Length; i++)
            {
                string[] g = Glyph(text[i]);
                for (int r = 0; r < H; r++) rows[r] += g[r];
                if (i != text.Length - 1)
                    for (int r = 0; r < H; r++) rows[r] += ".";
            }
            return rows;
        }

        private static string Pack(List<string> pixels)
        {
            StringBuilder outSb = new StringBuilder();
            for (int r = 0; r < pixels.Count; r += 2)
            {
                string up = pixels[r];
                string dn = r + 1 < pixels.Count ? pixels[r + 1] : new string('.', up.Length);
                StringBuilder line = new StringBuilder();
                for (int c = 0; c < up.Length; c++)
                {
                    bool u = up[c] == '#';
                    bool d = c < dn.Length && dn[c] == '#';
                    if (u && d) line.Append(G_FULL);
                    else if (u) line.Append(G_UP);
                    else if (d) line.Append(G_DN);
                    else line.Append(' ');
                }
                outSb.Append(line.ToString()).Append('\n');
            }
            return outSb.ToString();
        }

        private static string Plain(List<string> pixels)
        {
            StringBuilder outSb = new StringBuilder();
            foreach (string p in pixels)
                outSb.Append(p.Replace('.', ' ')).Append('\n');
            return outSb.ToString();
        }

        // ---------------- vskit hex mark ----------------

        private struct Pt
        {
            public double X;
            public double Y;
            public Pt(double x, double y) { X = x; Y = y; }
        }

        private static Pt HexVert(double deg, double r)
        {
            double a = deg * Math.PI / 180.0;
            return new Pt(r * Math.Cos(a), -r * Math.Sin(a));
        }

        private static Pt Lerp(Pt a, Pt b, double f)
        {
            return new Pt(a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f);
        }

        private static Pt Rot(Pt p, double deg)
        {
            double a = deg * Math.PI / 180.0;
            double c = Math.Cos(a), s = Math.Sin(a);
            return new Pt(p.X * c - p.Y * s, p.X * s + p.Y * c);
        }

        private static Pt Unit(Pt p)
        {
            double d = Math.Sqrt(p.X * p.X + p.Y * p.Y);
            return new Pt(p.X / d, p.Y / d);
        }

        private static Pt[] Arm(double rot)
        {
            const double R = 1.0;
            double apothem = R * Math.Sqrt(3.0) / 2.0;
            const double t = 0.36;
            const double trimLead = 0.02;
            const double trimTail = 0.26;

            Pt v0 = HexVert(90, R);
            Pt v1 = HexVert(30, R);
            Pt v2 = HexVert(-30, R);

            Pt d1 = Unit(new Pt(v1.X - v0.X, v1.Y - v0.Y));
            Pt d2 = Unit(new Pt(v2.X - v1.X, v2.Y - v1.Y));

            Pt n1 = new Pt(-d1.Y, d1.X);
            if (n1.X * v0.X + n1.Y * v0.Y > 0) n1 = new Pt(d1.Y, -d1.X);
            Pt n2 = new Pt(-d2.Y, d2.X);
            if (n2.X * v2.X + n2.Y * v2.Y > 0) n2 = new Pt(d2.Y, -d2.X);

            Pt aOut = Lerp(v0, v1, trimLead);
            Pt cOut = Lerp(v2, v1, trimTail);
            Pt aIn = new Pt(aOut.X + n1.X * t, aOut.Y + n1.Y * t);
            Pt cIn = new Pt(cOut.X + n2.X * t, cOut.Y + n2.Y * t);
            double k = 1.0 - t / apothem;
            Pt v1In = new Pt(v1.X * k, v1.Y * k);

            Pt[] poly = new Pt[] { aOut, v1, cOut, cIn, v1In, aIn };
            for (int i = 0; i < poly.Length; i++) poly[i] = Rot(poly[i], rot);
            return poly;
        }

        private static bool InPoly(Pt[] poly, double x, double y)
        {
            bool inside = false;
            int n = poly.Length;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                if ((poly[i].Y > y) != (poly[j].Y > y) &&
                    x < (poly[j].X - poly[i].X) * (y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)
                {
                    inside = !inside;
                }
            }
            return inside;
        }

        private static int[] Violet(double f)
        {
            if (f < 0) f = 0;
            if (f > 1) f = 1;
            int gray = (int)(245 - 95 * f);
            return new int[] { gray, gray, gray };
        }

        private const int MARK_W = 32;
        private const int MARK_PH = 36;

        private static int[,] MarkPixels()
        {
            Pt[] a0 = Arm(0);
            Pt[] a1 = Arm(120);
            Pt[] a2 = Arm(240);

            int[,] grid = new int[MARK_PH, MARK_W];
            double scale = (MARK_PH - 2) / 2.0;
            double cx = MARK_W / 2.0;
            double cy = MARK_PH / 2.0;

            for (int py = 0; py < MARK_PH; py++)
            {
                for (int px = 0; px < MARK_W; px++)
                {
                    double x = (px + 0.5 - cx) / scale;
                    double y = (py + 0.5 - cy) / scale;
                    if (InPoly(a2, x, y)) grid[py, px] = 3;
                    else if (InPoly(a1, x, y)) grid[py, px] = 2;
                    else if (InPoly(a0, x, y)) grid[py, px] = 1;
                }
            }
            return grid;
        }

        private static void PrintMark()
        {
            string[] logo = new string[]
            {
                "\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2804\u28c0\u2822\u2880\u28e4\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28f7\u28c4\u2800\u2854\u2880\u2802\u285c\u28ad\u28bb\u28cd\u28af\u287b\u28dd\u28ff\u28ff\u287f\u28df\u2802",
                "\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2804\u2800\u28e6\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28f7\u2854\u2840\u2882\u281c\u28ea\u2897\u287e\u28f6\u287d\u28fe\u28df\u28ef\u281b\u2800\u2800",
                "\u2800\u2800\u2800\u2800\u2800\u2804\u2800\u2820\u28f6\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28d4\u2828\u2878\u285d\u28ef\u28f3\u288f\u28ff\u2833\u2809\u2800\u28a0\u28ec\u2876",
                "\u2820\u28d3\u28a4\u28c2\u28c4\u28c0\u2880\u28fe\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u2846\u2801\u28de\u2871\u28dd\u280e\u2800\u2880\u2820\u28e5\u2833\u285e\u2879",
                "\u2800\u2844\u2889\u2832\u28bf\u28fc\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u2854\u28e7\u287d\u280b\u2800\u28f0\u28f6\u28fb\u28f6\u28ff\u28be\u28f7",
                "\u28a4\u2848\u2809\u2832\u28a4\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u2801\u2880\u2874\u288f\u2873\u28ae\u287f\u28fd\u28de\u283b\u285c",
                "\u2812\u28ed\u2833\u28b6\u28fc\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28bf\u2859\u282e\u28dc\u28ef\u287d\u28f3\u288c\u2853\u2808",
                "\u2878\u28f0\u288b\u28f7\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28f7\u28fb\u28bf\u28fb\u28ff\u287d\u28d7\u280b\u2804\u2800",
                "\u2823\u2887\u289f\u28f8\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28e7\u289f\u287f\u28a3\u28df\u287b\u2818\u2800\u2800\u2800",
                "\u2831\u284a\u2824\u28f8\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u283f\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u2828\u2817\u280b\u28c1\u28e4\u2816\u280a\u2881\u28c0",
                "\u2800\u2801\u2802\u28b9\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u284f\u2800\u2800\u2800\u2800\u28ff\u2842\u2839\u28ff\u28ff\u28ff\u28ff\u28ff\u2819\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u287f\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u2804\u2812\u288b\u28c9\u2864\u28d4\u28ee\u28fd\u28fe",
                "\u28a2\u2823\u28cc\u28bc\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u2800\u2800\u2800\u2800\u28b0\u28ff\u2845\u2800\u28ff\u28ff\u28ff\u28ff\u28ff\u2800\u2838\u28bf\u28f9\u28ff\u28ff\u28ff\u28ff\u28ff\u2847\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28f6\u28fb\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff",
                "\u2883\u2849\u2820\u28b8\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u285f\u28fc\u28b9\u2800\u2800\u2800\u2800\u28fe\u283f\u2847\u2800\u28ff\u28ff\u28ff\u28ff\u284f\u2800\u2800\u28de\u28e7\u28fb\u281f\u28bf\u28ff\u28ff\u28a0\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u287f\u28e7\u2831\u28cc\u28f3\u28fd\u28fb\u28ff\u28ff\u28fb",
                "\u2800\u2892\u2855\u28fa\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u2801\u2847\u2808\u28c7\u2800\u2800\u2800\u2808\u2846\u28b3\u2800\u2807\u285f\u280b\u2809\u2800\u2800\u2800\u2803\u2899\u28e0\u28e4\u28e4\u28fc\u28ef\u28da\u28df\u28b8\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u2800\u280c\u2811\u280c\u28b3\u281b\u285b\u280f\u281b\u2809",
                "\u2858\u28b7\u28cc\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u285f\u2809\u28bb\u28c0\u28e7\u28e4\u28fd\u28e6\u28e4\u28c4\u2800\u2830\u2840\u2803\u2800\u2800\u2800\u2800\u2800\u2800\u2874\u281f\u281b\u28c9\u28c9\u2849\u2809\u2808\u2809\u2809\u2809\u280b\u28bb\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u2800\u2888\u2808\u2808\u2801\u281b\u2800\u2800\u2800\u28d2",
                "\u2809\u28a3\u285b\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u2867\u2816\u281b\u2809\u2809\u2809\u2800\u2800\u2810\u2812\u28a2\u2844\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u287e\u28e0\u28f2\u28fe\u28ff\u28bf\u28f7\u28b6\u2844\u2800\u2800\u28fd\u28ff\u28ff\u28ff\u28ff\u287f\u281f\u28ff\u28ff\u28ff\u28ff\u28ff\u281b\u2881\u28e4\u2876\u283f\u281b\u280b",
                "\u2800\u2800\u280c\u28bd\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u2877\u2800\u2800\u2800\u28e0\u28f6\u28f6\u28ff\u28df\u28ff\u28f6\u2845\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2803\u28bf\u28ff\u28ff\u28ff\u28ff\u2800\u28ff\u2840\u2800\u28bb\u28ec\u28d9\u287b\u287f\u28e1\u28fe\u28ff\u28ff\u284d\u2808\u28c0\u28e4\u28ec\u28e4\u28f6\u28f2\u28f6\u28ff",
                "\u2800\u2888\u2810\u2840\u28bb\u28eb\u28bf\u28ff\u28ff\u28ff\u28ff\u2818\u28a7\u2801\u2800\u28fb\u284f\u2838\u28ff\u28ff\u28ff\u28ff\u280f\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2811\u2884\u28c9\u28db\u28cb\u28e1\u2874\u2803\u2800\u2800\u28ff\u28ff\u28ff\u281f\u28e0\u285b\u28bf\u28ff\u28ff\u28f7\u28f2\u28fd\u28ff\u28ff\u28f7\u28fe\u28f7\u28ff\u28ff",
                "\u2800\u2800\u2880\u2810\u2840\u2883\u2848\u28ff\u28bf\u28ff\u28ff\u28df\u2846\u2800\u2800\u2809\u283f\u28e6\u28c8\u28c9\u28c9\u2824\u281a\u2801\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u28ff\u285f\u28e1\u28f6\u28ff\u28ff\u28fe\u28ff\u28ff\u28ff\u28bf\u287f\u28ff\u28ff\u287f\u283f\u281b\u28cb\u28e1",
                "\u2820\u2810\u2840\u28a2\u28f6\u28ff\u28a7\u283b\u28ef\u28ff\u28ef\u285b\u28bf\u2844\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u2818\u2810\u2802\u2841\u2824\u2814\u2882\u28c9\u28e4\u2874",
                "\u28c0\u2825\u280c\u28f3\u28af\u28df\u28ee\u28d7\u28fe\u28df\u28ff\u28ff\u28e6\u28ed\u2840\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u2802\u28c8\u2825\u2854\u2864\u28cd\u2823\u28dd\u28be\u2879",
                "\u2800\u2800\u2800\u2820\u2808\u2809\u2808\u2809\u2809\u2809\u28e8\u28ff\u28ff\u28ff\u28ef\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2880\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u285f\u283b\u289e\u28ff\u28dd\u28f3\u288e\u28b3\u28bb\u286e\u28d5",
                "\u2800\u2800\u2880\u2800\u2840\u2800\u2800\u28c0\u28f4\u28fe\u28ff\u28ff\u28ff\u28ff\u28ff\u28e7\u2840\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u28f0\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u2857\u28a0\u2818\u287c\u28fd\u28db\u285e\u2826\u28e7\u28bb\u28fd",
                "\u2800\u2888\u2800\u2840\u2840\u28a4\u281e\u2849\u28ad\u28f9\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28c4\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2808\u2808\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2880\u28f4\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28df\u28ff\u28cd\u28e3\u28be\u28f5\u28ef\u28f7\u28fd\u28e6\u28d1\u28ef\u28bf",
                "\u2800\u2802\u28f4\u28fe\u285f\u28e7\u280a\u2854\u28a2\u281b\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28f7\u28c0\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2810\u2812\u2802\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u28a0\u28fe\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u285f\u2809\u28ef\u28b9\u28fd\u28bb\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff",
                "\u28f6\u28df\u2833\u28cf\u287f\u28ce\u2833\u28c8\u285c\u28fa\u28ff\u283f\u28bf\u28dd\u287f\u28eb\u289f\u28fd\u28ff\u28ff\u283b\u28e6\u28c4\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2880\u2814\u281b\u28ff\u283f\u28df\u28a9\u28be\u28ff\u28ff\u28ff\u28ff\u28c7\u283e\u28dc\u2867\u28ef\u28df\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff",
                "\u280b\u2880\u28b1\u28eb\u28df\u28be\u2879\u28b4\u2878\u28f5\u284f\u28c2\u28be\u287f\u28fd\u28f9\u28df\u28fe\u28ff\u285f\u28a0\u2847\u2800\u28f9\u2802\u2804\u28c0\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u28f7\u28e3\u289f\u287f\u28fe\u28ff\u28ff\u28ff\u28ff\u288c\u282b\u289d\u287b\u28f5\u28bb\u285f\u28ff\u28bf\u28ff\u28bf\u287f\u28ff\u283f",
                "\u2800\u28a2\u281e\u28f4\u28af\u28af\u28dd\u28e6\u28b3\u285d\u2876\u28ed\u28ff\u28fd\u28f3\u28df\u287e\u28fd\u285f\u2880\u285f\u2800\u2880\u287f\u2800\u2800\u2800\u2801\u2820\u2824\u2800\u2800\u2800\u2824\u2810\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u28b8\u2857\u2808\u282d\u28ff\u28ff\u28ff\u28ff\u287f\u288c\u2823\u2840\u2850\u2888\u2803\u281a\u2826\u28c9\u2802\u2823\u281c\u2844\u288b",
                "\u28dc\u28f7\u28bb\u285c\u28ef\u28fe\u285e\u28e5\u28d3\u28be\u287d\u288e\u2877\u28af\u2877\u28ef\u289f\u28fd\u2803\u28f8\u2801\u2800\u287c\u2803\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u28bb\u2844\u28b9\u28ff\u28ff\u28ff\u28ff\u2883\u286e\u2851\u28b0\u28a0\u28c2\u285c\u28e6\u2874\u28f1\u28ce\u28f4\u28e9\u285c\u28e6",
                "\u28ff\u28ef\u28b7\u287b\u28cf\u28f7\u28df\u2836\u28d9\u282e\u2859\u28aa\u281c\u28ef\u28bd\u28ef\u28ff\u2803\u2804\u2883\u28e0\u281e\u2801\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2839\u28fe\u28ff\u28ff\u28ff\u2847\u2822\u28a1\u2859\u28a6\u2853\u287c\u28fd\u28fe\u28ff\u28ff\u28ff\u28ff\u28f7\u28ff\u28ff",
                "\u28ff\u2879\u2887\u2873\u2879\u28de\u2818\u2848\u2885\u2822\u2881\u2802\u2858\u2824\u28cb\u28f6\u28e1\u2834\u281a\u2809\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u28ff\u28ff\u28ff\u28ff\u2830\u2841\u2886\u2818\u28e1\u283b\u28fd\u28f3\u28ff\u28ff\u28ff\u28ff\u28bf\u28ff\u28ff\u28ff",
                "\u28a3\u281d\u2862\u288d\u2831\u2888\u28c2\u28cc\u2864\u2826\u2836\u2836\u281e\u281b\u280b\u2801\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u28b0\u28ff\u28ff\u28ff\u281b\u2837\u28ed\u28c2\u280c\u28a0\u2813\u2874\u28fb\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ef\u28ff",
                "\u28c7\u28be\u2871\u281e\u2808\u2809\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u28f8\u28ff\u28ff\u2847\u2800\u2800\u2800\u2809\u281b\u2833\u283f\u28f6\u28fd\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff\u28ff"
            };

            for (int i = 0; i < logo.Length; i++)
            {
                Push(P_BRIGHT);
                Say(logo[i]);
                Pop();
            }
        }

        // ---------------- config ----------------

        // Поздравляю ты залез в код и увидел этот коментарий но к сожалению мой апи ключ ты не увидишь <3.
        private static string apiUrl = "https://tooken.club/v1";
        private static string apiKey = "";
        private static string apiModel = "qwen3.8-flash";
        private static bool apiEnabled = true;      // allow sending messages to the API
        private static string reasoningEffort = ""; // "", none, minimal, low, medium, high
        private static bool toolsEnabled = true;    // model may act on this machine
        private static string toolApprove = "ask";  // ask | auto
        private static string workDir = "";         // where tools run and resolve paths
        private static string systemPrompt =
            "You are a concise assistant answering inside a small terminal. " +
            "Plain text only, no markdown, no emoji. Keep answers short unless asked for detail.";

        private static readonly List<Msg> History = new List<Msg>();
        private const int MAX_HISTORY_MESSAGES = 30;
        private const int MAX_TOOL_ROUNDS = 12;

        private class ToolCall
        {
            public string Id;
            public string Name;
            public string ArgsJson;
        }

        private class Msg
        {
            public string Role;
            public string Content;
            public string Reasoning;
            public string ToolCallId;      // for role=tool
            public List<ToolCall> Calls;   // for role=assistant with tool_calls
        }

        private static string ConfPath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "api.conf");
        }

        private static void LoadConf()
        {
            try
            {
                if (!File.Exists(ConfPath())) return;
                foreach (string raw in File.ReadAllLines(ConfPath()))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq < 1) continue;
                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string val = line.Substring(eq + 1).Trim();
                    switch (key)
                    {
                        case "url": apiUrl = val; break;
                        case "key": apiKey = val; break;
                        case "model": apiModel = val; break;
                        case "api": apiEnabled = val == "1" || val.ToLowerInvariant() == "true"; break;
                        case "system": systemPrompt = val; break;
                        case "effort": reasoningEffort = val; break;
                        case "tools": toolsEnabled = val == "1" || val.ToLowerInvariant() == "true"; break;
                        case "approve": toolApprove = val.ToLowerInvariant(); break;
                        case "workdir": workDir = val; break;
                    }
                }
            }
            catch { }
        }

        private static void SaveConf()
        {
            try
            {
                string[] lines = new string[]
                {
                    "# vskit terminal config",
                    "api=" + (apiEnabled ? "1" : "0"),
                    "url=" + apiUrl,
                    "key=" + apiKey,
                    "model=" + apiModel,
                    "effort=" + reasoningEffort,
                    "tools=" + (toolsEnabled ? "1" : "0"),
                    "approve=" + toolApprove,
                    "workdir=" + workDir,
                    "system=" + systemPrompt,
                };
                File.WriteAllLines(ConfPath(), lines);
            }
            catch (Exception e)
            {
                Push(P_PINK);
                Say("   could not write api.conf: " + e.Message);
                Pop();
            }
        }

        // ---------------- json helpers ----------------

        private static string JsonEscape(string s)
        {
            StringBuilder b = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': b.Append("\\\""); break;
                    case '\\': b.Append("\\\\"); break;
                    case '\n': b.Append("\\n"); break;
                    case '\r': b.Append("\\r"); break;
                    case '\t': b.Append("\\t"); break;
                    default:
                        if (c < 0x20) b.Append("\\u").Append(((int)c).ToString("x4"));
                        else b.Append(c);
                        break;
                }
            }
            return b.ToString();
        }

        // find the start index of the value for a JSON key (handles whitespace around ':')
        private static int FindJsonKeyValue(string json, string key, int startFrom)
        {
            if (json == null) return -1;
            for (int i = startFrom; i < json.Length; i++)
            {
                if (json[i] != '"') continue;
                int keyStart = i + 1;
                int j = keyStart;
                bool match = true;
                int k = 0;
                for (; j < json.Length; j++)
                {
                    char c = json[j];
                    if (c == '\\')
                    {
                        match = false;
                        j++;
                        continue;
                    }
                    if (c == '"') break;
                    if (k >= key.Length || c != key[k]) match = false;
                    k++;
                }
                if (j >= json.Length) break;
                if (match && k == key.Length)
                {
                    int p = j + 1;
                    while (p < json.Length && char.IsWhiteSpace(json[p])) p++;
                    if (p < json.Length && json[p] == ':')
                    {
                        p++;
                        while (p < json.Length && char.IsWhiteSpace(json[p])) p++;
                        if (p < json.Length) return p;
                    }
                }
                i = j;
            }
            return -1;
        }

        private static string ReadJsonStringAt(string json, int quoteIndex, out int endQuote)
        {
            endQuote = quoteIndex;
            if (json == null || quoteIndex < 0 || quoteIndex >= json.Length || json[quoteIndex] != '"') return null;
            int i = quoteIndex + 1;
            StringBuilder b = new StringBuilder();
            for (; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '"')
                {
                    endQuote = i;
                    break;
                }
                if (c == '\\' && i + 1 < json.Length)
                {
                    char n = json[++i];
                    switch (n)
                    {
                        case 'n': b.Append('\n'); break;
                        case 'r': b.Append('\r'); break;
                        case 't': b.Append('\t'); break;
                        case 'b': b.Append('\b'); break;
                        case 'f': b.Append('\f'); break;
                        case '"': b.Append('"'); break;
                        case '\\': b.Append('\\'); break;
                        case '/': b.Append('/'); break;
                        case 'u':
                            if (i + 4 < json.Length)
                            {
                                int code;
                                if (int.TryParse(json.Substring(i + 1, 4),
                                    System.Globalization.NumberStyles.HexNumber, null, out code))
                                {
                                    b.Append((char)code);
                                    i += 4;
                                }
                            }
                            break;
                        default: b.Append(n); break;
                    }
                }
                else b.Append(c);
            }
            return b.ToString();
        }

        private static string ReadJsonStringAt(string json, int quoteIndex)
        {
            int endQuote;
            return ReadJsonStringAt(json, quoteIndex, out endQuote);
        }

        private static string JsonString(string json, string key)
        {
            int idx = 0;
            while (true)
            {
                int valIdx = FindJsonKeyValue(json, key, idx);
                if (valIdx < 0) return null;
                if (json[valIdx] == '"') return ReadJsonStringAt(json, valIdx);
                idx = valIdx + 1;
            }
        }

        // every value of a string key, e.g. all "id":"..." in a /models response
        private static List<string> JsonStrings(string json, string key)
        {
            List<string> found = new List<string>();
            int from = 0;
            while (true)
            {
                int valIdx = FindJsonKeyValue(json, key, from);
                if (valIdx < 0) break;
                if (json[valIdx] == '"')
                {
                    int endQuote;
                    string v = ReadJsonStringAt(json, valIdx, out endQuote);
                    if (v != null) found.Add(v);
                    from = endQuote + 1;
                }
                else
                {
                    from = valIdx + 1;
                }
            }
            return found;
        }

        private static string SerializeMsg(Msg m)
        {
            if (m.Role == "tool")
            {
                return "{\"role\":\"tool\",\"tool_call_id\":\"" + JsonEscape(m.ToolCallId)
                    + "\",\"content\":\"" + JsonEscape(m.Content) + "\"}";
            }
            if (m.Role == "assistant" && m.Calls != null && m.Calls.Count > 0)
            {
                StringBuilder b = new StringBuilder();
                b.Append("{\"role\":\"assistant\",\"content\":");
                if (m.Content == null || m.Content.Length == 0) b.Append("null");
                else b.Append("\"").Append(JsonEscape(m.Content)).Append("\"");
                b.Append(",\"tool_calls\":[");
                for (int i = 0; i < m.Calls.Count; i++)
                {
                    if (i > 0) b.Append(",");
                    ToolCall tc = m.Calls[i];
                    b.Append("{\"id\":\"").Append(JsonEscape(tc.Id))
                        .Append("\",\"type\":\"function\",\"function\":{\"name\":\"")
                        .Append(JsonEscape(tc.Name))
                        .Append("\",\"arguments\":\"")
                        .Append(JsonEscape(tc.ArgsJson)).Append("\"}}");
                }
                b.Append("]}");
                return b.ToString();
            }
            return "{\"role\":\"" + m.Role + "\",\"content\":\"" + JsonEscape(m.Content) + "\"}";
        }

        private const string TOOLS_SCHEMA =
            "[{\"type\":\"function\",\"function\":{\"name\":\"run_command\"," +
            "\"description\":\"Run any Windows cmd or PowerShell command on the user's PC (full system access, any path/app/process). For GUI apps that should not block, use 'start \\\"\\\" <app>'.\"," +
            "\"parameters\":{\"type\":\"object\",\"properties\":{\"command\":{\"type\":\"string\"," +
            "\"description\":\"the command line to execute via cmd.exe /c\"}},\"required\":[\"command\"]}}}," +
            "{\"type\":\"function\",\"function\":{\"name\":\"read_file\"," +
            "\"description\":\"Read any text file on the user's PC (supports absolute paths anywhere on disk).\"," +
            "\"parameters\":{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}},\"required\":[\"path\"]}}}," +
            "{\"type\":\"function\",\"function\":{\"name\":\"write_file\"," +
            "\"description\":\"Write (create or overwrite) a text file anywhere on the user's PC.\"," +
            "\"parameters\":{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"},\"content\":{\"type\":\"string\"}},\"required\":[\"path\",\"content\"]}}}," +
            "{\"type\":\"function\",\"function\":{\"name\":\"list_dir\"," +
            "\"description\":\"List files and folders in any directory on the user's PC.\"," +
            "\"parameters\":{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}},\"required\":[\"path\"]}}}]";

        private const string TOOLS_PREAMBLE =
            " You have full access to the user's Windows PC via tools (run_command, read_file, write_file, list_dir). " +
            "You are NOT restricted to the terminal chat folder: you can use absolute paths anywhere on the PC " +
            "(e.g. C:\\Users\\..., Desktop, Documents), run cmd or PowerShell commands, launch or close apps " +
            "(use 'start \"\" <app>' for GUI apps), and manage files or system settings. " +
            "Default working directory for relative paths: ";

        private static string BuildBody(string userText, bool stream)
        {
            StringBuilder msgs = new StringBuilder();
            string sys = systemPrompt;
            if (toolsEnabled) sys += TOOLS_PREAMBLE + EffectiveWorkDir();
            msgs.Append("{\"role\":\"system\",\"content\":\"").Append(JsonEscape(sys)).Append("\"}");
            foreach (Msg m in History)
            {
                msgs.Append(",").Append(SerializeMsg(m));
            }
            msgs.Append(",{\"role\":\"user\",\"content\":\"")
                .Append(JsonEscape(userText)).Append("\"}");

            string effort = "";
            if (reasoningEffort.Length > 0 && reasoningEffort != "default")
            {
                effort = ",\"reasoning_effort\":\"" + JsonEscape(reasoningEffort) + "\"";
            }

            string tools = toolsEnabled ? ",\"tools\":" + TOOLS_SCHEMA : "";

            return "{\"model\":\"" + JsonEscape(apiModel) + "\",\"stream\":" + (stream ? "true" : "false")
                + effort + tools + ",\"messages\":[" + msgs.ToString() + "]}";
        }

        // ---------------- tiny json walker ----------------

        // returns the balanced substring starting at open (which must be '[' or '{')
        private static string Balanced(string s, int open)
        {
            char close = s[open] == '[' ? ']' : '}';
            int depth = 0;
            bool inStr = false;
            for (int i = open; i < s.Length; i++)
            {
                char c = s[i];
                if (inStr)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '"') inStr = false;
                    continue;
                }
                if (c == '"') inStr = true;
                else if (c == '[' || c == '{') depth++;
                else if (c == ']' || c == '}')
                {
                    depth--;
                    if (depth == 0) return s.Substring(open, i - open + 1);
                }
            }
            return null;
        }

        // split a JSON array string into its top-level object substrings
        private static List<string> SplitArray(string arr)
        {
            List<string> items = new List<string>();
            int depth = 0;
            bool inStr = false;
            int start = -1;
            for (int i = 0; i < arr.Length; i++)
            {
                char c = arr[i];
                if (inStr)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '"') inStr = false;
                    continue;
                }
                if (c == '"') inStr = true;
                else if (c == '{')
                {
                    if (depth == 0) start = i;
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0 && start >= 0)
                    {
                        items.Add(arr.Substring(start, i - start + 1));
                        start = -1;
                    }
                }
            }
            return items;
        }

        // parse assistant message from a non-streaming completion response
        private static Msg ParseCompletion(string json)
        {
            Msg m = new Msg();
            m.Role = "assistant";

            int tc = FindJsonKeyValue(json, "tool_calls", 0);
            if (tc >= 0 && json[tc] == '[')
            {
                string arr = Balanced(json, tc);
                if (arr != null)
                {
                    m.Calls = new List<ToolCall>();
                    foreach (string obj in SplitArray(arr))
                    {
                        ToolCall call = new ToolCall();
                        call.Id = JsonString(obj, "id");
                        call.Name = JsonString(obj, "name");
                        int argIdx = FindJsonKeyValue(obj, "arguments", 0);
                        if (argIdx >= 0)
                        {
                            if (obj[argIdx] == '"') call.ArgsJson = ReadJsonStringAt(obj, argIdx);
                            else if (obj[argIdx] == '{') call.ArgsJson = Balanced(obj, argIdx);
                        }
                        if (call.Name != null)
                        {
                            if (call.Id == null) call.Id = "call_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                            if (call.ArgsJson == null) call.ArgsJson = "{}";
                            m.Calls.Add(call);
                        }
                    }
                    if (m.Calls.Count == 0) m.Calls = null;
                }
            }

            // content: take "content" inside "message" if present
            int msgIdx = FindJsonKeyValue(json, "message", 0);
            string scope = msgIdx >= 0 ? json.Substring(msgIdx) : json;
            m.Reasoning = JsonString(scope, "reasoning_content");
            if (m.Reasoning == null || m.Reasoning.Length == 0) m.Reasoning = JsonString(scope, "reasoning");
            m.Content = JsonString(scope, "content");
            return m;
        }

        // ---------------- tool execution ----------------

        private static string EffectiveWorkDir()
        {
            if (workDir.Length > 0 && Directory.Exists(workDir)) return workDir;
            return Environment.CurrentDirectory;
        }

        private static string ResolvePath(string p)
        {
            if (Path.IsPathRooted(p)) return p;
            return Path.Combine(EffectiveWorkDir(), p);
        }

        private static string Trunc(string s, int max)
        {
            if (s == null) return "";
            s = s.Replace("\0", "");
            if (s.Length <= max) return s;
            return s.Substring(0, max) + "\n...[truncated " + (s.Length - max) + " chars]";
        }

        private static bool Approve(string preview)
        {
            if (toolApprove == "auto") return true;
            Push(P_PINK);
            Console.Out.Write("   " + G_SPARK + " run? [y/N] ");
            Pop();
            Console.Out.Flush();
            string ans;
            try { ans = Console.ReadLine(); }
            catch { ans = ""; }
            if (ans == null) return false;
            ans = ans.Trim().ToLowerInvariant();
            return ans == "y" || ans == "yes" || ans == "д" || ans == "да";
        }

        private static string ExecTool(ToolCall call)
        {
            string args = call.ArgsJson;

            if (call.Name == "run_command")
            {
                string command = JsonString(args, "command");
                if (command == null || command.Trim().Length == 0) return "error: empty command";

                Push(P_DIM);
                Say("   " + G_TRI_RIGHT + " run_command: " + Trunc(command.Replace("\n", " "), 200));
                Pop();

                if (!Approve(command)) return "user denied this command";

                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo();
                    if (IsWindows())
                    {
                        psi.FileName = "cmd.exe";
                        psi.Arguments = "/c " + command;
                        psi.StandardOutputEncoding = OemEncoding;
                        psi.StandardErrorEncoding = OemEncoding;
                    }
                    else
                    {
                        psi.FileName = "/bin/sh";
                        psi.Arguments = "-c \"" + command.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
                        psi.StandardOutputEncoding = Encoding.UTF8;
                        psi.StandardErrorEncoding = Encoding.UTF8;
                    }
                    psi.WorkingDirectory = EffectiveWorkDir();
                    psi.UseShellExecute = false;
                    psi.RedirectStandardInput = true;
                    psi.RedirectStandardOutput = true;
                    psi.RedirectStandardError = true;
                    psi.CreateNoWindow = true;

                    StringBuilder outBuf = new StringBuilder();
                    StringBuilder errBuf = new StringBuilder();
                    object outLock = new object();
                    object errLock = new object();

                    using (Process p = Process.Start(psi))
                    {
                        try { p.StandardInput.Close(); } catch { }

                        p.OutputDataReceived += delegate(object sender, DataReceivedEventArgs ev)
                        {
                            if (ev.Data != null)
                            {
                                lock (outLock) { if (outBuf.Length < 16000) outBuf.AppendLine(ev.Data); }
                            }
                        };
                        p.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs ev)
                        {
                            if (ev.Data != null)
                            {
                                lock (errLock) { if (errBuf.Length < 8000) errBuf.AppendLine(ev.Data); }
                            }
                        };

                        p.BeginOutputReadLine();
                        p.BeginErrorReadLine();

                        if (!p.WaitForExit(120000))
                        {
                            try { p.Kill(); } catch { }
                            return "error: command timed out after 120s";
                        }

                        // Give async readers a brief moment to flush remaining lines without
                        // blocking forever if a detached child (e.g. `start "" ...`) holds pipes.
                        Thread.Sleep(50);

                        string stdout;
                        string stderr;
                        lock (outLock) { stdout = outBuf.ToString(); }
                        lock (errLock) { stderr = errBuf.ToString(); }

                        StringBuilder b = new StringBuilder();
                        b.Append("exit code: ").Append(p.ExitCode).Append("\n");
                        if (stdout.Length > 0) b.Append("stdout:\n").Append(Trunc(stdout, 6000));
                        if (stderr.Length > 0) b.Append("\nstderr:\n").Append(Trunc(stderr, 3000));
                        return b.ToString();
                    }
                }
                catch (Exception e)
                {
                    return "error: " + e.Message;
                }
            }

            if (call.Name == "read_file")
            {
                string path = JsonString(args, "path");
                if (path == null) return "error: no path";
                try
                {
                    string full = ResolvePath(path);
                    Push(P_DIM);
                    Say("   " + G_TRI_RIGHT + " read_file: " + full);
                    Pop();
                    return Trunc(File.ReadAllText(full, Encoding.UTF8), 8000);
                }
                catch (Exception e)
                {
                    return "error: " + e.Message;
                }
            }

            if (call.Name == "write_file")
            {
                string path = JsonString(args, "path");
                string content = JsonString(args, "content");
                if (path == null) return "error: no path";
                if (content == null) return "error: no content";
                try
                {
                    string full = ResolvePath(path);
                    Push(P_DIM);
                    Say("   " + G_TRI_RIGHT + " write_file: " + full + " (" + content.Length + " chars)");
                    Pop();
                    if (!Approve("write " + full)) return "user denied this write";
                    string dir = Path.GetDirectoryName(full);
                    if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(full, content, new UTF8Encoding(false));
                    return "ok, wrote " + content.Length + " chars to " + full;
                }
                catch (Exception e)
                {
                    return "error: " + e.Message;
                }
            }

            if (call.Name == "list_dir")
            {
                string path = JsonString(args, "path");
                try
                {
                    string full = path == null || path.Trim().Length == 0 ? EffectiveWorkDir() : ResolvePath(path);
                    Push(P_DIM);
                    Say("   " + G_TRI_RIGHT + " list_dir: " + full);
                    Pop();
                    StringBuilder b = new StringBuilder();
                    b.Append("dir: ").Append(full).Append("\n");
                    foreach (string d in Directory.GetDirectories(full))
                        b.Append("[D] ").Append(Path.GetFileName(d)).Append("\n");
                    foreach (string f in Directory.GetFiles(full))
                        b.Append("    ").Append(Path.GetFileName(f)).Append("\n");
                    return Trunc(b.ToString(), 6000);
                }
                catch (Exception e)
                {
                    return "error: " + e.Message;
                }
            }

            return "error: unknown tool " + call.Name;
        }

        // ---------------- agent loop (tools mode) ----------------

        private static void ChatWithTools(string userText)
        {
            History.Add(new Msg { Role = "user", Content = userText });

            for (int round = 0; round < MAX_TOOL_ROUNDS; round++)
            {
                HttpWebRequest req = NewRequest("/chat/completions", "POST");
                req.ContentType = "application/json";

                // messages are already in History; build body without the extra user text
                string body = BuildBodyFromHistory(false);

                byte[] payload = Encoding.UTF8.GetBytes(body);
                req.ContentLength = payload.Length;

                string json;
                try
                {
                    using (Stream up = req.GetRequestStream()) up.Write(payload, 0, payload.Length);
                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    using (StreamReader rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    {
                        json = rd.ReadToEnd();
                    }
                }
                catch (WebException we)
                {
                    Fail(we);
                    break;
                }
                catch (Exception e)
                {
                    Push(P_PINK);
                    Say("   error: " + e.Message);
                    Pop();
                    break;
                }

                Msg m = ParseCompletion(json);

                if (m.Reasoning != null && m.Reasoning.Length > 0)
                {
                    Push(P_DIM);
                    Console.Out.WriteLine(m.Reasoning);
                    Pop();
                }

                if (m.Content != null && m.Content.Length > 0)
                {
                    Push(P_SOFT);
                    Console.Out.WriteLine(m.Content);
                    Pop();
                }

                if (m.Calls == null)
                {
                    History.Add(m);
                    TrimHistory();
                    Console.Out.WriteLine();
                    return;
                }

                History.Add(m);

                foreach (ToolCall tc in m.Calls)
                {
                    string result = ExecTool(tc);
                    History.Add(new Msg { Role = "tool", ToolCallId = tc.Id, Content = result });
                }
                Console.Out.WriteLine();
            }

            TrimHistory();
        }

        private static string BuildBodyFromHistory(bool stream)
        {
            StringBuilder msgs = new StringBuilder();
            string sys = systemPrompt + TOOLS_PREAMBLE + EffectiveWorkDir();
            msgs.Append("{\"role\":\"system\",\"content\":\"").Append(JsonEscape(sys)).Append("\"}");
            foreach (Msg m in History)
            {
                msgs.Append(",").Append(SerializeMsg(m));
            }

            string effort = "";
            if (reasoningEffort.Length > 0 && reasoningEffort != "default")
            {
                effort = ",\"reasoning_effort\":\"" + JsonEscape(reasoningEffort) + "\"";
            }

            return "{\"model\":\"" + JsonEscape(apiModel) + "\",\"stream\":" + (stream ? "true" : "false")
                + effort + ",\"tools\":" + TOOLS_SCHEMA + ",\"messages\":[" + msgs.ToString() + "]}";
        }

        private static void TrimHistory()
        {
            while (History.Count > MAX_HISTORY_MESSAGES) History.RemoveAt(0);
        }

        // ---------------- http ----------------

        private static HttpWebRequest NewRequest(string path, string method)
        {
            try
            {
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
            }
            catch { }

            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(apiUrl.TrimEnd('/') + path);
            req.Method = method;
            req.Timeout = 15000;
            req.ReadWriteTimeout = 300000;
            if (apiKey.Length > 0) req.Headers["Authorization"] = "Bearer " + apiKey;
            return req;
        }

        private static string ReadError(WebException we)
        {
            try
            {
                HttpWebResponse err = we.Response as HttpWebResponse;
                if (err != null)
                {
                    using (StreamReader rd = new StreamReader(err.GetResponseStream(), Encoding.UTF8))
                    {
                        string body = rd.ReadToEnd();
                        string msg = JsonString(body, "message");
                        if (msg != null) return msg;
                        return body.Length > 200 ? body.Substring(0, 200) : body;
                    }
                }
            }
            catch { }
            return we.Message;
        }

        // ---------------- chat ----------------

        private static void Chat(string userText)
        {
            HttpWebRequest req = NewRequest("/chat/completions", "POST");
            req.ContentType = "application/json";

            byte[] payload = Encoding.UTF8.GetBytes(BuildBody(userText, true));
            req.ContentLength = payload.Length;
            try
            {
                using (Stream up = req.GetRequestStream()) up.Write(payload, 0, payload.Length);
            }
            catch (WebException we)
            {
                Fail(we);
                return;
            }

            StringBuilder reply = new StringBuilder();
            StringBuilder raw = new StringBuilder();
            bool streamed = false;
            bool inThink = false;
            Stopwatch watch = Stopwatch.StartNew();

            try
            {
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string line;
                    while ((line = rd.ReadLine()) != null)
                    {
                        if (line.Length == 0) continue;

                        if (line.StartsWith("data:"))
                        {
                            string chunk = line.Substring(5).Trim();
                            if (chunk == "[DONE]") break;

                            string think = JsonString(chunk, "reasoning_content");
                            if (think == null || think.Length == 0) think = JsonString(chunk, "reasoning");
                            if (think != null && think.Length > 0)
                            {
                                streamed = true;
                                inThink = true;
                                Push(P_DIM);
                                Console.Out.Write(think);
                                Console.Out.Flush();
                                Pop();
                            }

                            string piece = JsonString(chunk, "content");
                            if (piece != null && piece.Length > 0)
                            {
                                if (inThink)
                                {
                                    inThink = false;
                                    Console.Out.WriteLine();
                                }
                                streamed = true;
                                reply.Append(piece);
                                Push(P_SOFT);
                                Console.Out.Write(piece);
                                Console.Out.Flush();
                                Pop();
                            }
                        }
                        else
                        {
                            // non-SSE host: collect the body, parse at the end
                            raw.Append(line);
                        }
                    }
                }

                if (!streamed)
                {
                    string rawStr = raw.ToString();
                    string think = raw.Length > 0 ? JsonString(rawStr, "reasoning_content") : null;
                    if (think == null || think.Length == 0) think = raw.Length > 0 ? JsonString(rawStr, "reasoning") : null;
                    if (think != null && think.Length > 0)
                    {
                        Push(P_DIM);
                        Console.Out.WriteLine(think);
                        Pop();
                    }
                    string content = raw.Length > 0 ? JsonString(rawStr, "content") : null;
                    if (content != null)
                    {
                        reply.Append(content);
                        Push(P_SOFT);
                        Console.Out.WriteLine(content);
                        Pop();
                        streamed = true;
                    }
                }

                watch.Stop();

                if (reply.Length == 0 && !streamed)
                {
                    Push(P_PINK);
                    Say("   empty response from " + apiModel);
                    Pop();
                    return;
                }

                Console.Out.WriteLine();
                Push(P_DIM);
                Console.Out.WriteLine("   " + G_DOT + " " + apiModel + " " + G_DOT + " " +
                    watch.Elapsed.TotalSeconds.ToString("0.0") + "s " + G_DOT + " " +
                    reply.Length + " chars " + G_DOT + " history " + (History.Count / 2 + 1) +
                    " " + G_DOT + " /new to reset");
                Pop();
                Console.Out.WriteLine();

                History.Add(new Msg { Role = "user", Content = userText });
                History.Add(new Msg { Role = "assistant", Content = reply.ToString() });
                TrimHistory();
            }
            catch (WebException we)
            {
                Console.Out.WriteLine();
                Fail(we);
            }
            catch (Exception e)
            {
                Console.Out.WriteLine();
                Push(P_PINK);
                Say("   error: " + e.Message);
                Pop();
            }
        }

        private static void Fail(WebException we)
        {
            Push(P_PINK);
            Say("   api error: " + ReadError(we));
            Pop();
            Push(P_DIM);
            Say("   endpoint: " + apiUrl + "  model: " + apiModel);
            Pop();
        }

        // ---------------- models list ----------------

        private static void ListModels()
        {
            HttpWebRequest req = NewRequest("/models", "GET");
            try
            {
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string body = rd.ReadToEnd();
                    List<string> ids = JsonStrings(body, "id");
                    if (ids.Count == 0)
                    {
                        Push(P_PINK);
                        Say("   got a response but found no \"id\" fields in it");
                        Pop();
                        return;
                    }

                    int sel = ids.IndexOf(apiModel);
                    if (sel < 0) sel = 0;

                    int winH = 24;
                    int winW = 80;
                    try { winH = Console.WindowHeight; winW = Console.WindowWidth; } catch { }
                    int visCount = Math.Max(1, Math.Min(ids.Count, Math.Max(5, winH - 6)));
                    int scroll = 0;
                    if (sel >= visCount) scroll = sel - visCount + 1;

                    Push(P_BRIGHT);
                    Say("   models at " + apiUrl + " (" + ids.Count + ")");
                    Pop();
                    Push(P_DIM);
                    Say("   use Up/Down arrows, Enter to apply, Esc to cancel");
                    Pop();

                    int startTop = -1;
                    bool interactive = true;
                    try
                    {
                        // Check if stdin is interactive before entering redraw loop
                        bool dummy = Console.KeyAvailable;
                        startTop = Console.CursorTop;
                    }
                    catch
                    {
                        interactive = false;
                    }

                    if (!interactive)
                    {
                        foreach (string id in ids)
                        {
                            bool current = id == apiModel;
                            Push(current ? P_SOFT : TEXT);
                            Say("     " + (current ? G_TRI_RIGHT + " " : "   ") + id);
                            Pop();
                        }
                        Push(P_DIM);
                        Say("   switch with: /model <name>");
                        Pop();
                        return;
                    }

                    bool oldCursor = true;
                    try { oldCursor = Console.CursorVisible; Console.CursorVisible = false; } catch { }

                    try
                    {
                        // Reserve lines once so Console.CursorTop doesn't scroll unexpectedly on redraw
                        for (int i = 0; i < visCount; i++) Console.Out.WriteLine();
                        startTop = Math.Max(0, Console.CursorTop - visCount);

                        while (true)
                        {
                            if (sel < scroll) scroll = sel;
                            if (sel >= scroll + visCount) scroll = sel - visCount + 1;

                            try { Console.SetCursorPosition(0, startTop); } catch { }

                            for (int i = 0; i < visCount; i++)
                            {
                                int idx = scroll + i;
                                string id = ids[idx];
                                bool isSelected = idx == sel;
                                bool isCurrent = id == apiModel;

                                string prefix = isSelected ? "   " + G_TRI_RIGHT + " " : "     ";
                                string suffix = isCurrent ? " (current)" : "";
                                if (i == 0 && scroll > 0) suffix += "  ^";
                                else if (i == visCount - 1 && scroll + visCount < ids.Count) suffix += "  v";

                                string line = prefix + id + suffix;
                                int maxW = Math.Max(20, winW - 1);
                                if (line.Length > maxW) line = line.Substring(0, maxW);
                                else line = line + Spaces(maxW - line.Length);

                                Push(isSelected ? P_BRIGHT : (isCurrent ? P_SOFT : TEXT));
                                Console.Out.WriteLine(line);
                                Pop();
                            }

                            ConsoleKeyInfo key = Console.ReadKey(true);
                            if (key.Key == ConsoleKey.UpArrow || key.Key == ConsoleKey.K)
                            {
                                sel = (sel - 1 + ids.Count) % ids.Count;
                            }
                            else if (key.Key == ConsoleKey.DownArrow || key.Key == ConsoleKey.J)
                            {
                                sel = (sel + 1) % ids.Count;
                            }
                            else if (key.Key == ConsoleKey.PageUp)
                            {
                                sel = Math.Max(0, sel - visCount);
                            }
                            else if (key.Key == ConsoleKey.PageDown)
                            {
                                sel = Math.Min(ids.Count - 1, sel + visCount);
                            }
                            else if (key.Key == ConsoleKey.Home)
                            {
                                sel = 0;
                            }
                            else if (key.Key == ConsoleKey.End)
                            {
                                sel = ids.Count - 1;
                            }
                            else if (key.Key == ConsoleKey.Enter)
                            {
                                try { Console.SetCursorPosition(0, startTop + visCount); } catch { }
                                apiModel = ids[sel];
                                SaveConf();
                                Push(P_DIM);
                                Say("   model -> " + apiModel);
                                Pop();
                                break;
                            }
                            else if (key.Key == ConsoleKey.Escape || key.Key == ConsoleKey.Q)
                            {
                                try { Console.SetCursorPosition(0, startTop + visCount); } catch { }
                                Push(P_DIM);
                                Say("   kept model: " + apiModel);
                                Pop();
                                break;
                            }
                        }
                    }
                    finally
                    {
                        try { Console.CursorVisible = oldCursor; } catch { }
                    }
                }
            }
            catch (WebException we)
            {
                Fail(we);
            }
            catch (Exception e)
            {
                Push(P_PINK);
                Say("   error: " + e.Message);
                Pop();
            }
        }

        // ---------------- banner ----------------

        private const int INNER = 78;

        private static void Push(Style s)
        {
            if (!colour) return;
            if (vt) Console.Out.Write(s.Ansi);
            else if (IsWindows()) SetConsoleTextAttribute(hOut, s.Attr);
        }

        private static void Pop()
        {
            if (!colour) return;
            if (vt) Console.Out.Write("\x1b[0m");
            else if (IsWindows()) SetConsoleTextAttribute(hOut, 7);
        }

        private static void Say(string text)
        {
            Console.Out.WriteLine(text);
        }

        private static void Banner()
        {
            Console.Out.WriteLine();
            Push(P_DEEP);
            Say(Rule(INNER));
            Pop();

            PrintMark();

            List<string> word = Pixels("VSKIT");
            string art = asciiOnly ? Plain(word) : Pack(word);
            string[] rows = art.Split('\n');
            int indent = (INNER - word[0].Length) / 2;
            for (int i = 0; i < rows.Length; i++)
            {
                if (rows[i].Length == 0) continue;
                Push(P_BRIGHT);
                Say(new string(' ', indent) + rows[i]);
                Pop();
            }

            Push(P_DEEP);
            Say(Rule(INNER));
            Pop();

            Push(P_DIM);
            Say("   model " + G_DOT + " " + apiModel + "      endpoint " + G_DOT + " " + apiUrl);
            Say("   " + G_DOT + " type /help for commands, everything else goes to the model");
            Pop();
            Console.Out.WriteLine();
        }

        // ---------------- repl ----------------

        private static void Prompt()
        {
            Push(P_BRIGHT);
            Console.Out.Write(G_FULL + G_FULL);
            Pop();
            Push(P_DEEP);
            Console.Out.Write("vskit");
            Pop();
            Push(P_BRIGHT);
            Console.Out.Write(" " + G_TRI_RIGHT + " ");
            Pop();
            Console.Out.Flush();
        }

        private static void OnCtrlC(object sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            Console.Out.WriteLine();
            Push(P_DIM);
            Say("   " + G_DOT + " ctrl+c " + G_DOT + " (press again or type /exit to quit)");
            Pop();
        }

        private static void Repl()
        {
            Console.CancelKeyPress += OnCtrlC;

            while (true)
            {
                Prompt();

                string line;
                try { line = Console.ReadLine(); }
                catch { break; }

                if (line == null)
                {
                    Console.Out.WriteLine();
                    break;
                }

                string input = line.Trim();
                if (input.Length == 0) continue;
                if (!Handle(input)) break;
            }

            Push(P_DIM);
            Say("   " + G_DOT + " bye " + G_DOT + " config saved in api.conf");
            Pop();
        }

        private static bool Handle(string input)
        {
            string[] parts = input.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string cmd = parts[0].ToLowerInvariant();
            if (cmd.StartsWith("/")) cmd = cmd.Substring(1);

            switch (cmd)
            {
                case "help":
                case "?":
                    if (parts.Length > 1 && parts[1].ToLowerInvariant() == "api") HelpApi();
                    else Help();
                    break;

                case "infovs":
                    InfoVs();
                    break;

                case "lain":
                    {
                        string lainUrl = "https://tenor.com/ru/view/lain-serial-experiments-lain-gif-26108112";
                        try
                        {
                            if (IsWindows())
                            {
                                Process.Start(new ProcessStartInfo(lainUrl) { UseShellExecute = true });
                            }
                            else if (File.Exists("/usr/bin/open"))
                            {
                                Process.Start("open", lainUrl);
                            }
                            else
                            {
                                Process.Start("xdg-open", lainUrl);
                            }
                            Push(P_SOFT);
                            Say("   opening " + lainUrl);
                            Pop();
                        }
                        catch (Exception e)
                        {
                            Push(P_PINK);
                            Say("   could not open browser: " + e.Message);
                            Pop();
                        }
                    }
                    break;

                case "api":
                    if (parts.Length > 1)
                    {
                        string arg = parts[1].ToLowerInvariant();
                        if (arg == "on")
                        {
                            apiEnabled = true;
                            SaveConf();
                            Push(P_DIM);
                            Say("   api enabled. messages will be sent to " + apiUrl);
                            Pop();
                        }
                        else if (arg == "off")
                        {
                            apiEnabled = false;
                            SaveConf();
                            Push(P_DIM);
                            Say("   api disabled. local /commands still work; messages will not be sent.");
                            Pop();
                        }
                        else if (arg == "help")
                        {
                            HelpApi();
                        }
                        else
                        {
                            Push(P_PINK);
                            Say("   usage: /api on | off | help");
                            Pop();
                        }
                    }
                    else
                    {
                        Push(P_DIM);
                        Say("   api: " + (apiEnabled ? "on" : "off") + "   url: " + apiUrl);
                        Say("   model: " + apiModel + "   key: " + (apiKey.Length == 0 ? "(none)" : MaskKey(apiKey)));
                        Say("   use /api on | off, or /help api for full details.");
                        Pop();
                    }
                    break;

                case "exit":
                case "quit":
                case "q":
                    return false;

                case "new":
                case "reset":
                    History.Clear();
                    Push(P_DIM);
                    Say("   context cleared.");
                    Pop();
                    break;

                case "model":
                    if (parts.Length > 1)
                    {
                        apiModel = string.Join(" ", parts, 1, parts.Length - 1);
                        SaveConf();
                        Push(P_DIM);
                        Say("   model -> " + apiModel);
                        Pop();
                    }
                    else
                    {
                        Push(P_DIM);
                        Say("   current model: " + apiModel);
                        Say("   switch with: /model <name> or /models");
                        Pop();
                    }
                    break;

                case "models":
                    ListModels();
                    break;

                case "url":
                    if (parts.Length > 1)
                    {
                        apiUrl = parts[1];
                        SaveConf();
                        Push(P_DIM);
                        Say("   url -> " + apiUrl);
                        Pop();
                    }
                    else
                    {
                        Push(P_DIM);
                        Say("   url is " + apiUrl);
                        Pop();
                    }
                    break;

                case "key":
                    if (parts.Length > 1)
                    {
                        apiKey = parts[1];
                        SaveConf();
                        Push(P_DIM);
                        Say("   key saved (masked: " + MaskKey(apiKey) + ")");
                        Pop();
                    }
                    else
                    {
                        Push(P_DIM);
                        Say("   key is " + (apiKey.Length == 0 ? "empty" : "set: " + MaskKey(apiKey)));
                        Pop();
                    }
                    break;

                case "tools":
                    if (parts.Length > 1)
                    {
                        string lvl = parts[1].ToLowerInvariant();
                        if (lvl == "on" || lvl == "1" || lvl == "true" || lvl == "enable")
                        {
                            toolsEnabled = true;
                            SaveConf();
                            Say("   tools on. the model now has full PC access (commands & files).");
                        }
                        else if (lvl == "off" || lvl == "0" || lvl == "false" || lvl == "disable")
                        {
                            toolsEnabled = false;
                            SaveConf();
                            Say("   tools off. plain chat only.");
                        }
                        else Say("   usage: /tools on | off");
                        if (toolsEnabled)
                        {
                            Push(P_PINK);
                            Say("   " + G_SPARK + " safety: every run_command/write_file asks you first (/approve auto to skip).");
                            Pop();
                        }
                    }
                    else
                    {
                        Say("   tools: " + (toolsEnabled ? "on" : "off") + "   approval: " + toolApprove);
                        Say("   workdir: " + EffectiveWorkDir());
                        Say("   model tools: run_command, read_file, write_file, list_dir");
                    }
                    break;

                case "approve":
                    if (parts.Length > 1)
                    {
                        string lvl = parts[1].ToLowerInvariant();
                        if (lvl == "ask" || lvl == "auto") { toolApprove = lvl; SaveConf(); Say("   approval -> " + lvl); }
                        else Say("   usage: /approve ask | auto");
                    }
                    else Say("   approval: " + toolApprove);
                    break;

                case "workdir":
                    if (parts.Length > 1)
                    {
                        string d = string.Join(" ", parts, 1, parts.Length - 1);
                        string full = ResolvePath(d);
                        if (Directory.Exists(full)) { workDir = full; SaveConf(); Say("   workdir -> " + full); }
                        else Say("   no such directory: " + full);
                    }
                    else Say("   workdir: " + EffectiveWorkDir());
                    break;

                case "think":
                case "thinking":
                case "effort":
                case "reasoning":
                    if (parts.Length > 1)
                    {
                        string lvl = parts[1].ToLowerInvariant();
                        if (lvl == "on") lvl = "medium";
                        else if (lvl == "off") lvl = "none";
                        switch (lvl)
                        {
                            case "none":
                            case "minimal":
                            case "low":
                            case "medium":
                            case "high":
                            case "default":
                                reasoningEffort = lvl;
                                SaveConf();
                                Push(P_DIM);
                                Say("   thinking level -> " + lvl);
                                Pop();
                                break;
                            default:
                                Push(P_PINK);
                                Say("   levels: on | off | none | minimal | low | medium | high | default");
                                Pop();
                                break;
                        }
                    }
                    else
                    {
                        Push(P_DIM);
                        Say("   thinking level: " + (reasoningEffort.Length == 0 ? "default (server decides)" : reasoningEffort));
                        Say("   set with: /think on | off | low | medium | high");
                        Pop();
                    }
                    break;

                case "system":
                    if (parts.Length > 1)
                    {
                        systemPrompt = string.Join(" ", parts, 1, parts.Length - 1);
                        SaveConf();
                        Push(P_DIM);
                        Say("   system prompt updated.");
                        Pop();
                    }
                    else
                    {
                        Push(P_DIM);
                        Say("   system: " + systemPrompt);
                        Pop();
                    }
                    break;

                case "status":
                    Say("   api ....... " + (apiEnabled ? "on" : "off"));
                    Say("   url ....... " + apiUrl);
                    Say("   model ..... " + apiModel);
                    Say("   thinking .. " + (reasoningEffort.Length == 0 ? "default" : reasoningEffort));
                    Say("   tools ..... " + (toolsEnabled ? "on (" + toolApprove + ")" : "off") + "   workdir: " + EffectiveWorkDir());
                    Say("   key ....... " + (apiKey.Length == 0 ? "(none)" : MaskKey(apiKey)));
                    Say("   history ... " + History.Count + " messages");
                    Say("   config .... " + ConfPath());
                    break;

                case "clear":
                    if (vt) Console.Out.Write("\x1b[2J\x1b[H");
                    else { try { Console.Clear(); } catch { } }
                    break;

                case "clear-cont":
                    History.Clear();
                    if (vt) Console.Out.Write("\x1b[3J\x1b[2J\x1b[H");
                    else { try { Console.Clear(); } catch { } }
                    Banner();
                    break;

                default:
                    if (input.StartsWith("/"))
                    {
                        Push(P_DIM);
                        Say("   unknown command " + cmd + ". /help lists them.");
                        Pop();
                    }
                    else
                    {
                        SendToModel(input);
                    }
                    break;
            }
            return true;
        }

        private static void SendToModel(string text)
        {
            if (!apiEnabled)
            {
                Push(P_PINK);
                Say("   api is off. enable with: /api on (see /help api)");
                Pop();
                return;
            }
            if (toolsEnabled) ChatWithTools(text);
            else Chat(text);
        }

        private static string MaskKey(string key)
        {
            if (key.Length <= 8) return "****";
            return key.Substring(0, 4) + "..." + key.Substring(key.Length - 4);
        }

        private static void Help()
        {
            string[][] rows = new string[][]
            {
                new string[] { "/model [name]", "show current model, or switch to <name>" },
                new string[] { "/models", "interactive model picker (arrows + Enter)" },
                new string[] { "/api <on|off>", "enable or disable API requests" },
                new string[] { "/url <url>", "set api base url, e.g. https://host/v1" },
                new string[] { "/key <key>", "set api key (stored in api.conf)" },
                new string[] { "/think <lvl>", "thinking level: none minimal low medium high" },
                new string[] { "/tools <on|off>", "let the model run commands and edit files" },
                new string[] { "/approve <ask|auto>", "confirm each command, or trust the model" },
                new string[] { "/workdir <path>", "folder where tools operate" },
                new string[] { "/system <text>", "set the system prompt" },
                new string[] { "/new", "clear conversation context" },
                new string[] { "/status", "show current config" },
                new string[] { "/clear", "wipe the screen" },
                new string[] { "/clear-cont", "wipe screen + context and redraw banner" },
                new string[] { "/help", "this list" },
                new string[] { "/help api", "full api guide, key & toggle" },
                new string[] { "/infovs", "about vskit terminal, purpose & art" },
                new string[] { "/lain", "present day, present time" },
                new string[] { "/exit", "quit" },
            };
            Push(P_BRIGHT);
            Say("   " + G_SPARK + " commands");
            Pop();
            foreach (string[] r in rows)
            {
                Push(P_SOFT);
                Console.Out.Write("   " + r[0] + Spaces(Math.Max(2, 16 - r[0].Length)));
                Pop();
                Push(P_DIM);
                Console.Out.WriteLine(r[1]);
                Pop();
            }
            Push(P_DIM);
            Say("   anything else you type is sent to " + apiModel + " as a message.");
            Pop();
        }

        private static void HelpApi()
        {
            Push(P_BRIGHT);
            Say("   " + G_SPARK + " api help");
            Pop();

            Push(P_DIM);
            Say("   OpenAI-compatible client (/chat/completions, streaming SSE, /models).");
            Say("");
            Pop();

            Push(P_BRIGHT);
            Say("   current state");
            Pop();
            Push(P_DIM);
            Say("   api ....... " + (apiEnabled ? "on" : "off"));
            Say("   url ....... " + apiUrl);
            Say("   model ..... " + apiModel);
            Say("   key ....... " + (apiKey.Length == 0 ? "(none)" : MaskKey(apiKey)));
            Say("   config .... " + ConfPath());
            Say("");
            Pop();

            string[][] rows = new string[][]
            {
                new string[] { "/api on", "enable sending messages to the API" },
                new string[] { "/api off", "disable API requests (local /commands still work)" },
                new string[] { "/api", "show current API state (on/off, url, model, key)" },
                new string[] { "/url <url>", "set base url (e.g. https://api.openai.com/v1)" },
                new string[] { "/key <key>", "set Bearer API key (saved in api.conf, masked here)" },
                new string[] { "/model <name>", "select model to use for chat" },
                new string[] { "/models", "fetch available models from <url>/models" },
                new string[] { "/think <lvl>", "reasoning effort: none minimal low medium high default" },
                new string[] { "/status", "show full terminal and API configuration" },
            };

            Push(P_BRIGHT);
            Say("   api commands");
            Pop();
            foreach (string[] r in rows)
            {
                Push(P_SOFT);
                Console.Out.Write("   " + r[0] + Spaces(Math.Max(2, 16 - r[0].Length)));
                Pop();
                Push(P_DIM);
                Console.Out.WriteLine(r[1]);
                Pop();
            }

            Push(P_DIM);
            Say("");
            Say("   key & auth:");
            Say("   - sent as HTTP header: Authorization: Bearer <key>");
            Say("   - stored as plain text in api.conf (key=...) next to the exe");
            Say("   - if key is empty, requests are sent without Authorization (for local APIs)");
            Say("   - startup flags: --url <url> --key <key> --model <name>");
            Pop();
        }

        private static void InfoVs()
        {
            Push(P_BRIGHT);
            Say("   " + G_SPARK + " vskit terminal — info");
            Pop();

            Push(P_SOFT);
            Say("   Цель:");
            Pop();
            Push(P_DIM);
            Say("   Создан для более удобной работы, так как обычный PowerShell не по нраву.");
            Say("   Главная цель терминала — в удобстве, как минимум личном.");
            Say("");
            Pop();

            Push(P_SOFT);
            Say("   Смысл:");
            Pop();
            Push(P_DIM);
            Say("   По фану.");
            Say("");
            Pop();

            Push(P_SOFT);
            Say("   Как написан и какой бэкенд:");
            Pop();
            Push(P_DIM);
            Say("   - Язык и сборка: самодостаточный C# (.NET Framework 4.0, csc.exe), без внешних библиотек.");
            Say("   - Интерфейс: Win32 API (kernel32.dll) + ANSI VT-цвета и встроенный шрифт/лого.");
            Say("   - Бэкенд ИИ: OpenAI-compatible HTTP REST API (HttpWebRequest, HTTPS)");
            Say("     • /chat/completions (streaming SSE для чата + tool-calling для управления ПК)");
            Say("     • /models (получение списка моделей), конфиг хранится в api.conf.");
            Say("");
            Pop();

            string[] art = new string[]
            {
                "⠠⡐⢠⠂⠥⠒⡌⠰⡈⢆⡑⢢⠘⡐⢢⠑⢢⠁⠦⢡⢂⠣⢌⠒⡄⢃⠆⡱⢌⠒⠌⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠄⠀⠀⡀⢀⠀⠠⠀⠠⠀⠀⠀⠀⠀⠀⠀⠣⢘⡐⢢⢡⠒⡌⠒⠤⢃⠜⡰⢈⠔⢢⠑⢢⠑⡌⠒⡌⠰⢌⠒⡰⢈⠒⢌⠢⡑⢢⠁⠎⠤⡑⢂⠆⡑⠢⢌",
                "⠠⠑⣂⢉⠒⡥⠘⡡⢑⠢⡘⠤⡉⠔⡡⠊⡅⠚⡌⠢⠜⡰⢈⡒⠌⡆⡍⠐⠀⠀⠀⠀⠀⠂⠄⡐⠀⠀⠀⠐⠀⠀⠂⠈⠐⠀⠄⠂⠀⠂⠁⢀⠀⠠⢀⠀⠀⠀⡀⠀⠈⠢⢡⢊⠔⣉⠦⡁⢎⠰⡉⠆⡑⢊⠔⢃⠌⡱⢈⠣⡘⢄⠃⡡⠋⡄⢓⡈⢆⡉⠎⡰⢉⠆⡘⠡⢃⠌",
                "⠠⠓⡄⢊⠔⢢⠑⡐⠣⡑⢌⠢⠱⡘⢄⠓⡌⠱⢠⡉⠆⡅⢣⠘⠈⠀⠀⠀⠀⠀⠀⠀⠄⠠⠀⠠⠀⠁⠌⠀⠀⠈⠀⠈⠀⠐⠀⡀⠂⠀⠐⠀⠂⠁⡀⠠⠁⠀⠀⠀⠀⠀⠀⠈⠘⡄⢢⠑⡌⢢⠑⡌⠱⡈⠜⡐⣊⠔⡡⢒⠡⢊⠔⡡⠓⡈⠦⠘⠤⡘⢢⠑⡌⢢⠑⡃⢎⡘",
                "⠐⡅⢊⠤⡉⢆⠱⣈⠱⡈⢆⠡⡃⠜⡠⢃⠌⣑⠢⢌⡱⠈⠁⠀⠀⠀⠠⠈⠀⠀⡐⠈⢀⠠⠀⢀⠐⠀⠈⠀⠐⠀⢁⠀⠂⡀⠀⢀⠐⠠⠁⠈⠀⠀⠀⠀⠀⠡⠐⠀⠂⠀⠀⠀⠀⠀⠁⠊⠴⡁⢎⠰⢡⠘⢢⠑⡄⢊⠔⡡⢊⠔⡨⢐⠡⠜⡰⠉⢆⡑⠢⡑⣈⠆⡱⢈⠆⡘",
                "⠐⡌⢂⠒⣡⠊⡔⢠⠃⡜⢠⠃⡜⢠⠱⣈⠒⡌⢒⠢⠁⠀⠀⠀⠀⠄⠡⢀⠀⠀⠀⠂⠄⠀⠄⠀⢀⠀⠂⠈⠀⠡⠀⠐⠠⠀⠈⠀⠄⠀⠂⠀⠠⠀⠀⠐⠈⠐⠀⠡⢀⠈⠀⠄⠀⠀⠀⠀⠐⡁⢎⡘⠤⡉⢆⠡⡘⠤⢃⠔⡡⢎⠰⢉⠢⠱⣀⠋⠤⢌⠱⡐⠄⢎⠰⡁⢎⠰",
                "⠐⢌⠢⡑⢄⠣⢌⠢⡑⢌⠢⡑⢌⠢⡑⢄⠣⡘⠂⠀⠀⠀⠀⠁⠀⠀⢀⠀⡈⠄⠐⠠⠀⢀⠀⠄⠂⡀⠀⠄⠈⡀⠀⠂⠀⠐⠀⢁⠀⠁⠠⠈⠀⠀⡁⠀⠁⠀⠀⠀⠄⠀⠂⡀⠂⠌⡀⠁⠀⠈⠢⡘⠤⡑⢌⠢⠑⡌⢢⠘⡐⢢⠑⡌⢢⠑⠤⣉⠒⡌⢢⠡⡉⢆⠱⡐⢌⠱",
                "⡈⢆⠱⡈⢆⠱⡈⢔⡈⢆⠱⣈⢂⠆⡱⢈⢆⠁⠀⠀⠀⠐⠈⠀⠌⠐⡀⠀⠐⢀⠀⠂⠁⠄⠈⠀⡐⠀⠂⠈⠄⠐⠠⠀⠁⠄⡈⠠⠀⠂⢀⠠⠁⠄⠀⢈⠀⠀⡀⠠⢀⠀⠄⢀⠈⠄⠀⡀⠂⠀⠀⠁⠆⢍⠢⣉⠒⡌⢄⠣⡘⢄⠣⡐⢡⠊⡔⢠⠃⠜⣀⠣⡘⢄⠣⡘⢠⢃",
                "⠐⡌⠰⡁⢎⠰⡁⢆⡘⢄⠣⡐⢌⠢⡑⢌⠂⠀⠀⠀⠀⠁⢀⠈⠀⢀⠀⠌⠐⠀⠈⠐⠀⠂⠌⠀⡀⠀⠀⠠⠈⠀⠄⠈⠀⠂⠀⠐⠀⠈⡀⠠⠀⠈⢀⠀⠂⠀⡀⠀⢀⠀⠈⠀⠀⡀⠀⠄⠀⡁⠂⠀⠘⡄⠣⢄⠣⡘⢄⠊⡔⠌⢢⠉⢆⠱⣈⠤⣉⠒⡄⢣⠘⡄⢣⠘⡄⣊",
                "⠂⡌⠱⡈⠆⠥⡘⠤⡈⢆⠱⡈⢆⠱⡈⠎⠀⠀⠀⠀⠈⠄⠀⠀⠂⡀⠀⠠⠀⠂⠐⠈⠀⡁⠀⠀⠀⠀⠄⠁⠀⠀⠀⠀⠀⢀⠀⠄⡀⠠⠀⠀⠠⠁⠀⠄⠀⠄⠠⠐⠀⠀⠀⠄⠀⠄⡁⠠⠐⠀⠂⠀⠀⠨⡑⢌⢂⠱⣈⠒⡌⡘⠤⣉⢂⠒⡄⡒⢄⠣⡘⠄⢣⠘⡄⠣⠔⢢",
                "⠐⡨⠑⡌⣘⠢⡑⢢⠑⣈⠆⡱⢈⠦⡁⠀⠀⠄⠠⠐⠀⠀⠂⠀⡐⠀⠈⠀⠀⡁⠂⠐⠀⠀⠀⠀⢂⠀⠀⠠⠁⠀⠀⠀⠈⠀⠀⠐⠀⠀⠠⠀⠐⠀⠈⠀⠀⠀⠄⠐⠀⠌⠠⠀⠄⠀⡀⠀⠂⠐⡀⠁⠀⠀⠑⡌⢢⠑⡄⢣⠘⡄⢣⠐⡌⢒⡰⢁⠎⣐⠡⢊⠅⡒⢌⠱⡈⢆",
                "⠁⢆⠱⡐⢢⠑⡌⢢⠑⡂⠜⣀⠣⠂⠀⠀⠀⠀⠀⠀⠈⠀⢀⠀⠄⠀⠂⠁⠀⠄⠠⠀⠀⠀⠌⠀⠀⢠⡀⠀⠀⠀⠄⠀⠀⠠⠀⠂⡀⠄⠀⠀⠄⠈⠀⠀⠄⠀⠀⠀⠂⠠⠀⠀⡐⠠⠀⠁⠐⠀⠀⠐⠀⡀⠀⠘⡄⢣⠘⡄⢣⠘⡄⢣⠐⡡⢂⠥⢊⢄⠣⢌⢂⠱⡈⢆⠱⣈",
                "⢉⠢⢡⠘⣄⠊⡔⢡⠊⡜⢠⣁⠃⠀⠀⠀⠂⠁⡀⠀⠐⠀⡀⠠⠀⠂⠐⠠⠈⠀⠀⠀⢀⠁⠀⠀⠀⢰⣧⡟⠀⠀⢀⠀⠠⠀⠁⠀⠀⠀⠂⠁⠈⠀⠀⠄⠀⠀⠀⠀⠀⠠⢀⠁⠀⠀⠂⠈⠀⠠⠁⠀⠀⠀⠀⠀⠘⡄⢣⠘⡄⢣⠘⡄⢃⠆⡡⠘⣄⠊⡔⡈⢆⠡⢒⡈⢒⠤",
                "⢂⡑⢢⠑⡄⡊⠔⡡⢊⠔⡡⢂⠄⠀⠀⠡⠀⠐⠀⠀⠁⠐⢀⠁⠄⠀⢂⠀⠄⡀⠁⠈⠀⠀⠀⠀⠀⣸⣿⣿⡄⠈⠀⢈⠀⠀⠀⡀⠀⠀⢀⠈⠀⠀⠀⠀⡀⠄⠀⠀⠀⠐⡀⠈⠀⠄⠁⡐⠈⠀⠄⠠⠀⠀⠀⠀⠀⡜⢠⢃⠜⡠⠑⡌⢢⠘⡄⠣⢄⠣⡐⢡⠊⡔⢡⠘⡌⠒",
                "⠂⡌⢢⠉⡔⢡⠊⡔⢡⠊⡔⡁⠀⠀⡀⠀⠂⠀⢀⠂⠌⠀⠀⡀⠈⠐⠀⠄⠀⠀⠀⠀⠀⠂⠀⠀⠀⣾⣿⣿⡆⠀⠀⠀⡀⠀⠐⠀⢠⠀⠂⢀⠀⠀⠀⠀⠄⠐⠀⡁⢀⠀⠀⠁⠀⠀⠂⢀⠐⠈⡀⠐⠀⠈⠀⠀⠀⡜⢠⠊⡔⢡⠃⡜⠠⢃⠌⡑⢢⠡⡘⢄⠣⠌⢢⠡⠌⢣",
                "⠐⡌⢆⠱⣈⠢⡑⢌⠢⡑⡰⠁⠀⠁⠀⠐⢀⠀⠂⠀⠄⠐⠀⠀⠀⠂⢀⠀⠀⠁⡀⢀⠀⡀⠀⠀⠀⣿⣿⣿⣧⠀⠀⠀⠀⠀⠁⠀⠠⡇⠀⠀⠀⠀⣇⠀⠂⠀⠀⠀⠈⡄⠀⢀⠂⠀⠐⠀⠠⠀⡀⠀⠌⠀⠄⠀⠀⢈⠆⡱⢈⠆⡱⢈⠱⡈⠜⡠⠃⢆⠱⡈⢆⡉⢆⠱⡘⠤",
                "⠒⡨⢐⠢⡄⠣⢌⠢⡑⢢⠑⠀⠀⠀⠀⠐⠀⢈⠀⡀⠀⠁⠈⠠⢈⠐⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⣿⣿⣿⣿⡄⠀⠀⠐⠀⠀⠀⠀⣿⠀⠠⠀⠀⣯⠀⠀⠀⠀⠀⠀⡇⠀⠀⠄⠈⢀⠐⠀⠀⠄⠀⠀⠀⠀⠈⠀⠀⡎⠰⡁⢎⠰⣁⠲⡁⢎⠰⡁⢎⠰⣁⠢⡘⢄⠣⡘⡰",
                "⢂⠱⣈⠒⡌⠱⡈⢆⡑⠢⠍⠀⠀⠀⠀⠈⠐⠀⠂⠠⠀⠠⠐⠀⠀⠈⠀⠄⠀⠀⠀⠀⠀⠀⢰⠀⠀⣿⣿⣿⣿⣇⠀⢤⠀⠀⠀⠀⠀⢸⣟⡀⠀⠀⣿⣆⠀⠈⠀⠀⠀⢟⡀⠀⠠⠀⠀⡀⠀⠂⠀⠂⠀⠀⢂⠀⠀⠀⡜⢡⠘⠤⡁⢆⠡⡘⢄⠣⡘⢄⠣⢄⠱⡈⢆⠱⢠⠑",
                "⠄⡃⢄⠣⢌⠱⡈⠆⡌⢡⠃⠀⠀⠀⠀⠀⠈⠀⠌⠀⠈⠀⡐⠀⠀⠀⠀⠀⡀⠀⠀⡀⠀⠄⢸⠀⠀⣿⣿⣿⣿⣿⢂⢸⡀⠀⠀⠀⠀⠘⣿⣜⡄⠀⣿⣯⡄⣀⠀⠀⠀⠺⠅⠀⠐⠀⠀⠀⠁⠀⠠⠀⠁⠄⠀⠀⠀⠀⡜⢠⠋⡔⢡⠊⡔⢡⠊⡔⠡⢊⠔⢊⠰⡁⢎⠰⠁⢎",
                "⢄⠱⣈⠒⡌⢢⠑⡘⡄⣃⠆⠀⠀⠀⠀⠀⠀⠀⠠⠀⠄⠀⠀⢀⠀⠄⠀⠀⡁⠀⢀⠀⣤⠀⠘⡇⠀⢹⣿⣿⣿⣿⣯⣸⡴⠀⠀⠀⠀⢀⣻⣿⣬⣂⡋⢁⣤⢤⢶⣶⣤⣰⣶⠀⠀⠄⢀⠐⠀⠄⠁⡀⠠⠀⠀⠌⠀⠐⡘⡄⢣⠘⡄⢣⠘⡄⢃⠌⡱⢈⠜⡠⢃⠜⡠⢃⠍⢢",
                "⣀⠒⡄⢣⠘⣄⢃⡒⡌⣐⠂⠀⠀⠀⠀⠀⠀⠀⠀⠀⠌⠀⠈⡀⠀⠀⠀⢰⡆⠁⠀⠘⠒⠁⣀⣉⠀⢀⣀⣉⣩⣿⡟⢿⣿⣽⣯⣿⣼⣿⣿⣿⠿⢀⡿⡹⠊⠋⠉⠁⠀⠈⠛⠄⢀⠀⠂⢀⠀⠂⠀⠀⠐⠀⠀⡀⠂⠠⡑⢌⠢⡑⢌⠢⡑⢌⠢⡘⢄⠃⣆⠱⡈⠆⡱⢈⡌⡡",
                "⢀⠣⠌⡄⠓⡄⣂⠒⡰⢈⠆⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⡀⠀⠀⠀⠀⠂⢨⠄⠀⣔⣾⣿⡿⠿⠼⠆⠸⠿⣞⣱⡞⣿⣠⣹⣿⣿⣿⣿⣿⣿⡟⠰⢫⠗⡐⠀⠀⠀⠀⢄⠀⣶⣤⡀⠀⠀⠂⠀⠀⠀⠀⠐⠀⠀⠀⠀⠀⡱⢈⡔⠡⢊⠤⡑⢌⠢⡑⠌⡒⢠⢃⡘⠤⡑⢌⠰⢡",
                "⢀⠣⡘⠠⢍⠰⣀⢃⠒⡩⠄⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠠⢀⢸⠀⠀⢸⡃⠘⢊⠉⠀⠀⠀⠀⠀⢀⡀⠀⢉⡙⠻⣿⣿⣿⣿⣿⣿⣿⣯⣀⣷⣏⡌⠀⠠⠀⠀⠀⢈⠀⣸⣿⣿⠄⠀⠀⠀⠀⡀⠄⠀⠀⠀⠀⠀⠀⣑⠢⣐⠡⢊⠔⢌⠢⡑⢄⠣⡘⢄⠢⡘⠤⡑⢌⡑⢢",
                "⠠⡑⢌⠱⣈⠒⡄⢣⠘⡔⢡⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠂⠸⠆⠀⢸⠷⠊⢁⠀⠀⠄⠀⠀⠉⡀⢹⣷⡄⠻⣾⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣧⣿⡀⠁⠀⠄⢁⣴⣿⡿⢻⠀⠀⠀⠀⠀⠀⠀⠀⠄⠀⠀⠀⠀⢢⠑⡄⠣⢌⡘⢄⠣⡘⢄⠃⡜⠠⢃⠜⡠⢑⠢⡘⠤",
                "⢄⠱⡈⢆⢡⠊⡔⠡⢃⠜⠤⡀⠀⠀⠀⠀⠀⠀⠀⠄⠀⠀⠀⠀⠘⣇⠀⢸⠀⠘⣿⣇⠈⠆⠀⠀⢐⠀⣼⣿⣷⣄⣹⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⠿⠶⣾⡿⠿⠟⣡⣾⡀⠀⠀⠀⠠⠀⢀⠀⠀⠀⠀⢀⠠⢅⠪⡐⢅⠢⡘⢄⠣⡘⢄⠣⢌⠱⡈⢆⠱⡈⢆⠱⢌",
                "⠄⡃⠜⡠⢂⠣⢌⠱⡈⠜⡰⢁⠆⠀⠀⠀⠀⠀⠈⡄⢳⡄⠀⠀⠀⠿⡄⢾⣿⣦⣘⠿⣷⣤⣁⣈⣴⣾⣿⢿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣷⣶⣶⣶⣷⣾⣿⣿⠀⠀⠀⠠⢀⠀⠀⠀⠀⠀⠀⠤⢃⡌⢢⠑⡌⢢⠑⡌⢢⠑⡌⠒⡌⢢⠑⡌⢂⠅⡊⢔⠨",
                "⠤⠑⢌⡐⠣⡘⠄⢣⠘⡌⠔⡩⠘⡄⠀⠀⠀⠀⠀⢃⢻⣆⠈⠀⠀⣹⣡⢸⣿⣿⣿⣷⣬⣉⣙⣋⣩⣥⣴⣾⣿⣿⣿⣿⣿⣿⣿⡟⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⠀⠀⠀⠀⠀⠈⠀⠀⠀⢀⡘⢢⢡⠘⡄⢣⠐⢢⠑⡈⢆⠒⢌⡑⢌⠢⡑⡈⠆⡌⠱⣈⠒",
                "⠠⢉⠆⡌⠱⡠⢉⠆⡱⢈⠆⡱⢉⠔⡀⠀⠀⠀⠀⠈⢆⣻⡇⣆⠈⠷⣜⣆⢻⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⢳⣻⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⠀⠀⠀⠀⠀⢀⠀⠀⠀⡄⣊⠔⣂⠣⠘⠤⡉⢆⢡⠱⡈⠜⡠⠒⡌⠒⠤⡑⢌⡐⠣⢄⠩",
                "⣀⠣⡘⢠⠃⡔⣉⠢⡑⢌⡘⢄⠣⡘⡁⠀⠀⠀⠀⠀⠈⠻⣷⡘⠆⠈⢳⠺⡄⢻⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣣⢗⣻⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⡟⠀⠀⠔⠀⠀⠀⠀⠀⠰⢐⠡⢊⢄⠣⡉⢆⠱⡈⢆⠢⡑⠬⡐⡡⠌⡑⢢⠁⠆⡌⠱⣈⠱",
                "⡀⢆⡑⢢⠑⡰⢄⠱⡈⢆⡘⢄⠣⢔⡁⠀⠀⡄⠀⠀⠀⠀⠘⢻⣷⣄⠈⢫⡽⡄⢻⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣤⣿⣷⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⠗⠀⠀⠀⠀⠀⠀⠀⠀⡱⢈⡒⠩⢄⠱⡈⢆⠡⡘⠤⡑⠌⢢⠑⡰⢡⠑⢢⠉⡜⢠⠃⡄⢣",
                "⠐⡂⠜⡠⢃⠒⡌⡰⢁⠆⡸⢀⠇⢢⠄⠀⠰⡀⠀⠀⠀⠀⠀⠀⠉⠛⠳⣄⠹⣹⢆⠹⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣆⠀⠀⠀⠀⠀⠁⠀⠀⡔⠡⢌⠱⡈⢆⠱⡈⢆⠑⡢⢡⢉⠆⡱⢀⠣⡘⢄⠣⢌⠢⡑⢌⠢",
                "⠡⡘⠤⠑⡌⠒⠤⡑⠌⣂⠱⡈⢎⢢⠁⢀⡱⠰⠀⠀⠀⠀⠀⠀⠀⠀⠀⠈⠀⠑⢯⠶⡘⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣏⣡⣴⣶⣶⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⠈⠀⠀⠀⠀⠀⠀⠀⠀⡰⢉⠆⡱⢈⠆⠱⡐⢌⠢⡑⠢⠌⡆⠱⡈⢆⠱⣈⠒⡄⢣⠘⡠⢃",
                "⠐⡌⢢⢉⡔⡉⢆⠱⡈⢄⢃⠜⡠⢆⠁⢠⢂⡱⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠈⠻⣵⣈⡙⢿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⡿⠃⠀⠀⠀⠀⠀⠀⠀⠀⠀⡑⢢⠘⡄⢣⢈⠱⡈⢆⠱⣈⠱⡘⢄⠣⡑⢌⠒⡠⠑⡌⢢⠑⡄⢣",
                "⠐⡌⢂⠦⡐⢡⠊⡔⢡⠊⡔⢨⡐⢌⠒⠤⢒⡰⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠛⢼⣢⡙⠻⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⡿⠋⠀⠀⠀⠀⠈⠀⠀⠀⡀⢄⠀⢑⡂⢣⠘⠤⡈⢆⠱⡈⠔⡠⢃⠜⡠⢃⠜⡠⢊⠅⠣⢌⠡⢊⠔⡡",
                "⠈⡔⢡⢂⡑⠆⡱⢈⠆⡱⢈⠆⡘⡠⢉⠜⡐⢢⠁⠀⠀⠀⠄⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠙⠧⢌⡙⢿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⠟⠉⠀⠀⠀⠀⠀⠀⠀⢀⠀⠤⡑⢊⠔⢢⡘⢄⠣⢌⠱⣀⠣⡘⠰⣁⠣⣈⠱⠈⢆⠱⡈⢌⠱⡈⢆⠣⡘⠔",
                "⠐⡌⢂⠆⡱⢈⠔⡡⢊⠔⡡⢊⠔⡑⢌⠢⠱⣈⠒⡰⣀⠒⠤⣀⠀⡀⠀⠀⠀⠀⣈⠀⠀⠀⠀⠀⠀⠀⢤⡈⠐⠪⣙⢿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⠿⠛⣠⠂⠀⠀⠀⠀⠀⡄⠀⠀⠀⠀⢆⡱⢨⠘⡄⠲⣈⠒⡌⠒⡄⠣⢌⠱⠠⡑⡄⢣⠉⢆⠢⡑⢌⢂⠱⡈⢆⠱⣈",
                "⠐⡌⢢⠘⡄⢣⢘⠰⡈⢆⠑⠢⢌⡑⢌⠒⡡⢂⡱⠐⢤⢉⠒⡌⢢⢡⠩⢌⠓⡌⢄⠣⢢⡐⠤⠠⠀⠀⢸⣚⡳⢧⡤⣌⡈⠛⠛⠿⢻⢟⠿⠿⠟⢋⣡⢴⡛⢶⠀⠀⠐⠂⠥⡉⠄⠀⠀⠀⠘⢠⠢⡑⡌⠰⢃⠄⠣⢌⠱⣈⠒⡌⢒⡡⡘⠤⡁⠎⡄⢃⠜⡠⢊⠔⡡⢊⠔⢢",
                "⢂⠌⡄⢣⠘⡄⢎⠰⡁⠎⡌⡑⠢⠌⡄⠣⠔⡃⢔⠩⡐⢊⠔⡌⣡⠢⡑⢌⠒⡌⢌⡒⠁⠈⠀⠀⠀⠀⠸⣴⢫⡗⡾⣡⢏⡷⢲⠖⡦⣴⠲⣖⣺⠹⣖⡣⣟⠾⠀⠀⠀⠀⢂⠵⡁⠀⠀⠀⡘⢄⠣⡐⢌⠱⡈⢌⠣⢌⠒⡄⢣⠘⡄⢢⠑⠤⡑⢌⠰⡁⢆⠱⣈⠢⡑⢌⠚⠤",
                "⠂⡜⢠⠃⡜⠰⢈⠆⡱⢈⠔⡨⠑⠬⡐⠱⡈⡔⣈⠒⡡⢊⠔⡨⢐⠢⡑⢌⠒⡌⠢⠜⡀⠀⠀⠀⠀⠀⠀⠞⣧⢻⠵⣋⢾⡱⣏⢿⡱⣎⡳⣝⢮⡻⠵⠋⠈⠀⠀⠀⠀⠀⢉⡒⡀⠀⠀⠀⠱⡈⢆⠱⡈⢆⡑⠢⡑⠢⡑⠌⢢⠑⡌⢢⠑⢢⠑⡌⡑⢌⢂⠒⡄⢃⠜⡠⣉⠒",
                "⠐⡄⢣⠘⡄⠓⡌⢢⠑⡌⢢⠡⡉⢆⠡⢃⠴⠐⡄⢣⠐⢣⠘⡄⢃⠆⡱⢈⡒⠌⣅⠃⠀⠀⠀⠀⠀⠀⠀⠀⠈⠋⠿⣱⢧⡝⣮⢧⡻⠜⠓⠉⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠒⡄⠀⠀⢠⠓⡘⡄⢣⠘⠤⣈⠱⡈⣑⠨⡘⢄⠣⠘⠤⣉⠢⡑⠤⡑⢌⠢⡑⢌⡂⢎⡐⠤⣉",
                "⠐⡌⢢⠑⡌⠱⡈⠤⠃⡜⣀⠣⣘⠠⢃⠌⡂⢇⠸⢠⠉⢆⠱⡈⢆⠱⣀⠣⡘⠬⠄⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠈⠁⠈⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠁⠂⠉⠔⣈⠆⣉⠒⡄⠣⠔⡠⢃⠜⡠⢃⠍⡔⠄⢣⠘⠤⡑⢌⠢⡁⢆⡘⠤⡘⢰⠠",
                "⠐⡌⢂⠱⣈⠱⣈⠒⡡⢒⠠⢃⠄⠣⢌⠢⣉⠢⣁⠣⡘⢄⠣⡘⢄⠣⡄⠓⠈⠁⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠊⠔⠣⢌⡑⡊⠔⣡⠊⡔⢡⠊⠤⡙⠠⢍⠒⢌⠢⠑⡌⢢⠘⠤⡑⢢⠑",
                "⠐⢌⠡⠒⡄⠣⢄⠣⡐⢡⠊⡔⢊⠱⣈⠒⣄⠃⢆⠱⣈⠦⠱⠘⠈⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠁⠘⠸⢠⠑⡌⢢⠉⣆⠩⡑⠬⡘⢄⠣⡑⢄⠣⡘⠤⡑⢢⢉",
                "⠈⢆⠡⢃⠌⡑⢢⠑⡌⠡⢎⠰⡁⠎⡄⡓⠤⠙⠈⠂⠁⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠈⠐⠁⠚⠤⡑⡌⠱⡈⢆⠱⡈⢆⠱⡈⢆⠱⡈⢆",
                "⢁⠊⡔⡁⢎⠰⡁⢎⠰⡉⢆⠣⠘⠁⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠈⠑⢌⢢⡁⠇⣌⠂⡅⢊⠤⡑⢌",
                "⠌⡒⠤⡑⢌⠢⡑⢌⠒⠁⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠘⡌⢄⠣⠜⡠⢆⠱⣈",
                "⠒⢌⠰⢡⠊⡔⠡⠎⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠈⢆⡑⢊⠔⢢⠑⠤",
                "⡈⢆⡘⢂⠱⠨⠅⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⢌⡡⢊⠆⣉⠒",
                "⠐⢢⠘⠤⡉⡕⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠢⢅⡊⠤⣉",
                "⢈⠢⢉⠆⡱⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠒⡌⠱⡠"
            };
            Push(P_SOFT);
            foreach (string line in art)
            {
                Say("   " + line);
            }
            Pop();
        }

        private static string Spaces(int n)
        {
            if (n < 0) n = 0;
            return new string(' ', n);
        }

        // ---------------- entry ----------------

        private static void Main(string[] args)
        {
            hOut = IsWindows() ? GetStdHandle(STD_OUTPUT_HANDLE) : IntPtr.Zero;

            List<string> flags = new List<string>();
            foreach (string a in args) flags.Add(a.ToLowerInvariant());

            asciiOnly = flags.Contains("--ascii");
            bool plain = flags.Contains("--no-color");

            try { Console.OutputEncoding = new UTF8Encoding(false); }
            catch { asciiOnly = true; plain = true; }

            vt = false;
            colour = true;
            if (!Console.IsOutputRedirected && !plain)
            {
                if (IsWindows())
                {
                    int mode;
                    if (GetConsoleMode(hOut, out mode) && SetConsoleMode(hOut, mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING))
                    {
                        int back;
                        if (GetConsoleMode(hOut, out back)) vt = (back & ENABLE_VIRTUAL_TERMINAL_PROCESSING) != 0;
                    }
                }
                else
                {
                    vt = true;
                }
            }
            if (plain || Console.IsOutputRedirected) colour = false;

            if (flags.Contains("--help") || flags.Contains("-h") || flags.Contains("/?"))
            {
                Usage();
                return;
            }

            LoadConf();
            ParseArgOverrides(args);

            if (flags.Contains("--once"))
            {
                // read one prompt from stdin, answer, exit (for pipes and scripts)
                string text = Console.In.ReadToEnd().Trim();
                if (text.Length > 0) SendToModel(text);
                return;
            }

            Banner();
            Repl();
        }

        private static void ParseArgOverrides(string[] args)
        {
            for (int i = 0; i + 1 < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--url": apiUrl = args[++i]; break;
                    case "--key": apiKey = args[++i]; break;
                    case "--model": apiModel = args[++i]; break;
                }
            }
        }

        private static void Usage()
        {
            Say("qwen-terminal.exe - violet console client for OpenAI-compatible APIs");
            Say("");
            Say("  (no args)        banner, then a chat prompt");
            Say("  --url <url>      api base url, e.g. https://api.openai.com/v1");
            Say("  --key <key>      api key");
            Say("  --model <name>   model id to chat with");
            Say("  --once           read one prompt from stdin, answer, exit");
            Say("  --ascii          plain-character logo");
            Say("  --no-color       no colour / escape sequences");
            Say("  --help           this text");
            Say("");
            Say("  config is saved to api.conf next to the exe; /help lists in-app commands.");
        }
    }
}

