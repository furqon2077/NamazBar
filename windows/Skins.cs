// NamazBar — цветовые темы (скины) и оформление контекстного меню.
// Совместимо с C# 5 / .NET Framework 4.x.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace NamazBar
{
    class SkinDef
    {
        public string Id;
        public string[] Title;   // uz-lotin, uz-kirill, ru, en
        public Color Emerald, EmeraldDark, Jade, Lapis, LapisDark, Gold, GoldDeep, Ivory, Ink;
    }

    // Скин подменяет цвета Palette; всё, что рисуется после Apply, берёт новые цвета
    static class Skin
    {
        static Color C(int r, int g, int b) { return Color.FromArgb(r, g, b); }

        public static readonly List<SkinDef> All = new List<SkinDef> {
            new SkinDef { Id = "emerald", Title = new[] { "Zumrad (yashil)", "Зумрад (яшил)", "Изумруд (зелёный)", "Emerald (green)" },
                Emerald = C(18, 94, 68), EmeraldDark = C(10, 52, 40), Jade = C(70, 184, 140), Lapis = C(30, 56, 110), LapisDark = C(14, 26, 54),
                Gold = C(222, 186, 98), GoldDeep = C(160, 118, 34), Ivory = C(246, 238, 218), Ink = C(40, 32, 20) },
            new SkinDef { Id = "ruby", Title = new[] { "Yoqut (qizil)", "Ёқут (қизил)", "Рубин (красный)", "Ruby (red)" },
                Emerald = C(150, 28, 44), EmeraldDark = C(68, 12, 22), Jade = C(240, 106, 112), Lapis = C(96, 30, 56), LapisDark = C(40, 12, 26),
                Gold = C(232, 190, 108), GoldDeep = C(168, 118, 40), Ivory = C(250, 238, 230), Ink = C(44, 24, 22) },
            new SkinDef { Id = "sapphire", Title = new[] { "Safir (ko'k)", "Сафир (кўк)", "Сапфир (синий)", "Sapphire (blue)" },
                Emerald = C(24, 78, 150), EmeraldDark = C(10, 30, 72), Jade = C(92, 170, 240), Lapis = C(40, 52, 130), LapisDark = C(10, 16, 52),
                Gold = C(226, 196, 112), GoldDeep = C(160, 124, 44), Ivory = C(238, 244, 252), Ink = C(24, 30, 44) },
            new SkinDef { Id = "sunset", Title = new[] { "Quyosh botishi (to'q sariq, sariq)", "Қуёш ботиши (тўқ сариқ, сариқ)", "Закат (оранжевый, жёлтый)", "Sunset (orange, yellow)" },
                Emerald = C(190, 86, 18), EmeraldDark = C(92, 36, 8), Jade = C(255, 168, 60), Lapis = C(150, 60, 24), LapisDark = C(60, 22, 8),
                Gold = C(255, 214, 72), GoldDeep = C(196, 140, 20), Ivory = C(255, 244, 222), Ink = C(52, 30, 12) },
            new SkinDef { Id = "onyx", Title = new[] { "Oniks (qora)", "Оникс (қора)", "Оникс (чёрный)", "Onyx (black)" },
                Emerald = C(36, 36, 40), EmeraldDark = C(12, 12, 14), Jade = C(200, 200, 208), Lapis = C(52, 52, 60), LapisDark = C(6, 6, 8),
                Gold = C(214, 176, 92), GoldDeep = C(150, 112, 36), Ivory = C(240, 238, 232), Ink = C(30, 30, 32) },
            new SkinDef { Id = "pearl", Title = new[] { "Injui (oq, kulrang)", "Инжуи (оқ, кулранг)", "Жемчуг (белый, серый)", "Pearl (white, grey)" },
                Emerald = C(84, 92, 106), EmeraldDark = C(44, 50, 60), Jade = C(190, 200, 214), Lapis = C(110, 118, 132), LapisDark = C(30, 34, 42),
                Gold = C(240, 242, 246), GoldDeep = C(150, 156, 168), Ivory = C(250, 250, 252), Ink = C(34, 38, 46) },
        };

        public static string CurId = "emerald";

        public static SkinDef Find(string id)
        {
            foreach (SkinDef s in All) if (s.Id == id) return s;
            return All[0];
        }
        public static SkinDef Cur { get { return Find(CurId); } }

        public static void Apply(string id)
        {
            SkinDef s = Find(id);
            CurId = s.Id;
            Palette.Emerald = s.Emerald; Palette.EmeraldDark = s.EmeraldDark; Palette.Jade = s.Jade;
            Palette.Lapis = s.Lapis; Palette.LapisDark = s.LapisDark; Palette.Gold = s.Gold;
            Palette.GoldDeep = s.GoldDeep; Palette.Ivory = s.Ivory; Palette.Ink = s.Ink;
        }

        // Читает выбранный скин из settings.ini (Store.Load уже вызван)
        public static void Load() { Apply(Store.Get("skin", "emerald")); }
        public static string Name(SkinDef s) { return s.Title[Lang.Cur]; }
    }

    // ───────────────────────── Оформление меню ─────────────────────────
    static class MenuUi
    {
        // Segoe MDL2 Assets есть в Windows 10/11; если шрифта нет — пункты просто без значков
        static bool? iconFont;
        static bool HasIcons
        {
            get
            {
                if (iconFont == null)
                {
                    bool ok = false;
                    try { using (Font f = new Font("Segoe MDL2 Assets", 10f)) ok = f.Name == "Segoe MDL2 Assets"; } catch { }
                    iconFont = ok;
                }
                return iconFont.Value;
            }
        }

        public static Bitmap Glyph(string glyph, Color c, int px)
        {
            Bitmap b = new Bitmap(px, px);
            using (Graphics g = Graphics.FromImage(b))
            using (Font f = new Font("Segoe MDL2 Assets", px * 0.62f, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                TextRenderer.DrawText(g, glyph, f, new Rectangle(0, 0, px, px), c, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            return b;
        }

        // Кружок-образец цветов скина: основной цвет, внутри золотой акцент
        public static Bitmap Swatch(SkinDef s, int px)
        {
            Bitmap b = new Bitmap(px, px);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle r = new Rectangle(1, 1, px - 3, px - 3);
                using (SolidBrush br = new SolidBrush(s.Emerald)) g.FillEllipse(br, r);
                using (Pen pen = new Pen(s.Gold, Math.Max(1.5f, px / 9f))) g.DrawEllipse(pen, r);
                int d = px / 3;
                using (SolidBrush br = new SolidBrush(s.Jade)) g.FillEllipse(br, (px - d) / 2, (px - d) / 2, d, d);
            }
            return b;
        }

        static float Dpi()
        {
            using (Bitmap b = new Bitmap(1, 1)) using (Graphics g = Graphics.FromImage(b)) return g.DpiX / 96f;
        }

        public static void Style(ContextMenuStrip m)
        {
            float k = Dpi();
            m.Renderer = new SkinRenderer();
            m.Font = Fonts.Get("NB Sans", 10.5f, "Segoe UI", FontStyle.Regular);
            m.ShowImageMargin = true; m.ShowCheckMargin = false;
            m.ImageScalingSize = new Size((int)(20 * k), (int)(20 * k));
            m.Padding = new Padding((int)(4 * k));
        }

        static ToolStripMenuItem Make(string text, string glyph)
        {
            ToolStripMenuItem it = new ToolStripMenuItem(text);
            it.Padding = new Padding(2, 5, 2, 5);
            if (glyph != null && HasIcons) it.Image = Glyph(glyph, Palette.Gold, (int)(20 * Dpi()));
            return it;
        }
        public static ToolStripMenuItem Sub(string text, string glyph) { return Make(text, glyph); }
        public static ToolStripMenuItem Item(string text, string glyph, Action click)
        {
            ToolStripMenuItem it = Make(text, glyph);
            if (click != null) it.Click += delegate { click(); };
            return it;
        }

        public static ToolStripMenuItem Swatch(SkinDef s, bool selected, Action click)
        {
            ToolStripMenuItem it = Make(Skin.Name(s), null);
            it.Image = Swatch(s, (int)(20 * Dpi()));
            it.Checked = selected;
            it.Click += delegate { click(); };
            return it;
        }

        public static Color Surface { get { return View.Mix(Palette.EmeraldDark, Color.Black, 0.35); } }
        public static Color Hover { get { return View.Mix(Palette.Emerald, Palette.Gold, 0.12); } }

        class SkinColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return MenuUi.Surface; } }
            public override Color ImageMarginGradientBegin { get { return MenuUi.Surface; } }
            public override Color ImageMarginGradientMiddle { get { return MenuUi.Surface; } }
            public override Color ImageMarginGradientEnd { get { return MenuUi.Surface; } }
            public override Color MenuBorder { get { return Color.FromArgb(120, Palette.Gold); } }
        }

        class SkinRenderer : ToolStripProfessionalRenderer
        {
            public SkinRenderer() : base(new SkinColors()) { RoundedEdges = false; }

            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                using (SolidBrush b = new SolidBrush(MenuUi.Surface)) e.Graphics.FillRectangle(b, e.AffectedBounds);
            }
            protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }
            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                using (Pen p = new Pen(Color.FromArgb(150, Palette.Gold)))
                    e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            }
            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                if (!e.Item.Selected || !e.Item.Enabled) return;
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle r = new Rectangle(3, 1, e.Item.Width - 7, e.Item.Height - 3);
                using (GraphicsPath p = Layered.Round(r, 6))
                using (SolidBrush b = new SolidBrush(MenuUi.Hover)) g.FillPath(b, p);
            }
            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                int y = e.Item.Height / 2;
                using (Pen p = new Pen(Color.FromArgb(70, Palette.Gold))) e.Graphics.DrawLine(p, 14, y, e.Item.Width - 14, y);
            }
            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.Enabled ? Palette.Ivory : Color.FromArgb(120, Palette.Ivory);
                base.OnRenderItemText(e);
            }
            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
            {
                e.ArrowColor = Palette.Gold;
                base.OnRenderArrow(e);
            }
            // галочка выбранного пункта — золотая «птичка» поверх значка/образца (в уголке)
            protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle r = e.ImageRectangle;
                int s = Math.Max(8, r.Height * 2 / 3);
                Rectangle box = e.Item.Image == null ? new Rectangle(r.X + (r.Width - s) / 2, r.Y + (r.Height - s) / 2, s, s)
                                                     : new Rectangle(r.Right - s + 2, r.Bottom - s + 2, s, s);
                using (SolidBrush b = new SolidBrush(Palette.Gold)) g.FillEllipse(b, box);
                using (Pen p = new Pen(Palette.EmeraldDark, Math.Max(1.4f, s / 6f)))
                {
                    p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                    g.DrawLines(p, new PointF[] {
                        new PointF(box.X + s * 0.24f, box.Y + s * 0.54f), new PointF(box.X + s * 0.44f, box.Y + s * 0.72f), new PointF(box.X + s * 0.78f, box.Y + s * 0.30f) });
                }
            }
        }
    }
}
