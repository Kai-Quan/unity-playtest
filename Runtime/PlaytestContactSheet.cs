#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Collections.Generic;
using UnityEngine;

namespace KaiQuan.Playtest
{
    /// <summary>
    /// Tiles a run of game frames into ONE numbered picture.
    ///
    /// WHY, since N frames tiled cost the same tokens as N frames sent loose: a
    /// model compares two cells of the same image far better than two separate
    /// images. Motion is a relationship between frames, and a relationship is
    /// easiest to see when both halves of it are in one picture. "The cup turned"
    /// is obvious across two cells and genuinely hard across two attachments.
    ///
    /// It also lets the two jobs a screenshot does be priced separately. A frame
    /// from the MIDDLE of a gesture only has to show that something moved, so it
    /// can be small; the frame at the END is the one whose text gets read and
    /// whose positions get clicked, so it stays full size. Before this, every
    /// frame paid the full-size price to answer the cheap question.
    ///
    /// Cells run left to right then down, numbered from 1, on a dark ground with
    /// gutters — so a cell edge is never mistaken for the edge of the screen.
    /// </summary>
    public static class PlaytestContactSheet
    {
        /// <summary>Width of ONE cell. The sheet grows to fit rather than the cells
        /// shrinking to fill, so a two-frame action costs two frames' worth of
        /// tokens instead of the same as a nine-frame one. It also means cells are
        /// always the same size, so an agent learns one visual scale instead of
        /// re-guessing it every action.
        ///
        /// A quarter width is plenty to see a camera move, a panel open or an
        /// object turn. It is NOT enough to read UI text — that is what the
        /// full-size final picture is for, and splitting those two jobs is the
        /// whole point of sending two pictures.</summary>
        public const int CellWidth = 426;

        private const int Gutter = 5;
        private static readonly Color32 Ground = new Color32(24, 24, 28, 255);
        private static readonly Color32 Ink = new Color32(255, 255, 255, 255);
        private static readonly Color32 Plate = new Color32(0, 0, 0, 255);

        public static byte[] Compose(IReadOnlyList<Texture2D> frames) => Compose(frames, CellWidth);

        /// <summary>Same sheet at a chosen cell size. A playtest sequence answers
        /// "did it move", which survives being small; an inspection sheet answers
        /// "is that gap real", which does not.</summary>
        public static byte[] Compose(IReadOnlyList<Texture2D> frames, int cellWidth)
        {
            int n = frames.Count;
            int cols = n <= 3 ? n : Mathf.CeilToInt(Mathf.Sqrt(n));
            int rows = Mathf.CeilToInt(n / (float)cols);

            int cellW = Mathf.Max(64, cellWidth);
            int cellH = Mathf.Max(1, Mathf.RoundToInt(cellW * frames[0].height / (float)frames[0].width));
            int sheetW = cellW * cols + Gutter * (cols + 1);
            int sheetH = cellH * rows + Gutter * (rows + 1);

            var sheet = new Texture2D(sheetW, sheetH, TextureFormat.RGB24, false);
            var ground = new Color32[sheetW * sheetH];
            for (int i = 0; i < ground.Length; i++) ground[i] = Ground;
            sheet.SetPixels32(ground);

            int scale = Mathf.Max(2, cellH / 40);   // number size tracks the cell

            for (int i = 0; i < n; i++)
            {
                int c = i % cols, r = i / cols;
                int x = Gutter + c * (cellW + Gutter);
                int y = sheetH - (r + 1) * (cellH + Gutter);   // row 0 on top

                var small = PlaytestBridge.Resize(frames[i], cellW, cellH);
                sheet.SetPixels32(x, y, cellW, cellH, small.GetPixels32());
                Object.Destroy(small);

                DrawNumber(sheet, i + 1, x + scale * 2, y + cellH - scale * 2, scale);
            }

            sheet.Apply();
            byte[] png = sheet.EncodeToPNG();
            Object.Destroy(sheet);
            return png;
        }

        // ── the smallest possible font ──────────────────────────────────
        // Three pixels wide, five tall, one row of three bits per line, top row
        // first. Numbering the cells is worth this much code and no more: without
        // it, reading order is an assumption, and an agent that guesses the order
        // of a sequence backwards reports the opposite of what happened.

        private static readonly ushort[] Digits =
        {
            0b111_101_101_101_111, // 0
            0b010_110_010_010_111, // 1
            0b111_001_111_100_111, // 2
            0b111_001_111_001_111, // 3
            0b101_101_111_001_001, // 4
            0b111_100_111_001_111, // 5
            0b111_100_111_101_111, // 6
            0b111_001_001_001_001, // 7
            0b111_101_111_101_111, // 8
            0b111_101_111_001_111, // 9
        };

        /// <summary>Draw a number with its top-left corner at (left, top), on a
        /// black plate so it reads over a bright frame as well as a dark one.</summary>
        private static void DrawNumber(Texture2D tex, int value, int left, int top, int scale)
        {
            string s = value.ToString();
            int gw = 3 * scale, gh = 5 * scale, gap = scale;
            int w = s.Length * gw + (s.Length - 1) * gap;

            Fill(tex, left - scale, top - gh - scale, w + scale * 2, gh + scale * 2, Plate);

            for (int d = 0; d < s.Length; d++)
            {
                ushort glyph = Digits[s[d] - '0'];
                int ox = left + d * (gw + gap);
                for (int r = 0; r < 5; r++)
                    for (int c = 0; c < 3; c++)
                        if (((glyph >> (14 - (r * 3 + c))) & 1) != 0)
                            Fill(tex, ox + c * scale, top - (r + 1) * scale, scale, scale, Ink);
            }
        }

        private static void Fill(Texture2D tex, int x, int y, int w, int h, Color32 color)
        {
            int x0 = Mathf.Clamp(x, 0, tex.width);
            int y0 = Mathf.Clamp(y, 0, tex.height);
            int x1 = Mathf.Clamp(x + w, 0, tex.width);
            int y1 = Mathf.Clamp(y + h, 0, tex.height);
            if (x1 <= x0 || y1 <= y0) return;

            var block = new Color32[(x1 - x0) * (y1 - y0)];
            for (int i = 0; i < block.Length; i++) block[i] = color;
            tex.SetPixels32(x0, y0, x1 - x0, y1 - y0, block);
        }
    }
}
#endif
