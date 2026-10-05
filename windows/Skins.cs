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
        public Color Emerald, EmeraldDark, Jade, Lapis, LapisHi, LapisDark, Gold, GoldDeep, Ivory, Ink;
    }

    // Скин подменяет цвета Palette; всё, что рисуется после Apply, берёт новые цвета
    static class Skin
    {
        static Color C(int r, int g, int b) { return Color.FromArgb(r, g, b); }

        public static readonly List<SkinDef> All = new List<SkinDef> {
            new SkinDef { Id = "emerald", Title = new[] { "Zumrad (yashil)", "Зумрад (яшил)", "Изумруд (зелёный)", "Emerald (green)" },
                Emerald = C(18, 94, 68), EmeraldDark = C(10, 52, 40), Jade = C(70, 184, 140), Lapis = C(16, 82, 90), LapisHi = C(72, 178, 168), LapisDark = C(8, 38, 46),
                Gold = C(222, 186, 98), GoldDeep = C(160, 118, 34), Ivory = C(246, 238, 218), Ink = C(40, 32, 20) },
            new SkinDef { Id = "ruby", Title = new[] { "Yoqut (qizil)", "Ёқут (қизил)", "Рубин (красный)", "Ruby (red)" },
                Emerald = C(150, 28, 44), EmeraldDark = C(68, 12, 22), Jade = C(240, 106, 112), Lapis = C(96, 30, 56), LapisHi = C(206, 96, 120), LapisDark = C(40, 12, 26),
                Gold = C(232, 190, 108), GoldDeep = C(168, 118, 40), Ivory = C(250, 238, 230), Ink = C(44, 24, 22) },
            new SkinDef { Id = "sapphire", Title = new[] { "Safir (ko'k)", "Сафир (кўк)", "Сапфир (синий)", "Sapphire (blue)" },
                Emerald = C(24, 78, 150), EmeraldDark = C(10, 30, 72), Jade = C(92, 170, 240), Lapis = C(40, 52, 130), LapisHi = C(100, 126, 214), LapisDark = C(10, 16, 52),
                Gold = C(226, 196, 112), GoldDeep = C(160, 124, 44), Ivory = C(238, 244, 252), Ink = C(24, 30, 44) },
            new SkinDef { Id = "sunset", Title = new[] { "Quyosh botishi (to'q sariq, sariq)", "Қуёш ботиши (тўқ сариқ, сариқ)", "Закат (оранжевый, жёлтый)", "Sunset (orange, yellow)" },
                Emerald = C(190, 86, 18), EmeraldDark = C(92, 36, 8), Jade = C(255, 168, 60), Lapis = C(150, 60, 24), LapisHi = C(240, 150, 70), LapisDark = C(60, 22, 8),
                Gold = C(255, 214, 72), GoldDeep = C(196, 140, 20), Ivory = C(255, 244, 222), Ink = C(52, 30, 12) },
            new SkinDef { Id = "onyx", Title = new[] { "Oniks (qora)", "Оникс (қора)", "Оникс (чёрный)", "Onyx (black)" },
                Emerald = C(36, 36, 40), EmeraldDark = C(12, 12, 14), Jade = C(200, 200, 208), Lapis = C(52, 52, 60), LapisHi = C(138, 138, 150), LapisDark = C(6, 6, 8),
                Gold = C(214, 176, 92), GoldDeep = C(150, 112, 36), Ivory = C(240, 238, 232), Ink = C(30, 30, 32) },
            new SkinDef { Id = "pearl", Title = new[] { "Injui (oq, kulrang)", "Инжуи (оқ, кулранг)", "Жемчуг (белый, серый)", "Pearl (white, grey)" },
                Emerald = C(84, 92, 106), EmeraldDark = C(44, 50, 60), Jade = C(190, 200, 214), Lapis = C(110, 118, 132), LapisHi = C(178, 186, 200), LapisDark = C(30, 34, 42),
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
            Palette.Lapis = s.Lapis; Palette.LapisHi = s.LapisHi; Palette.LapisDark = s.LapisDark; Palette.Gold = s.Gold;
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
            m.Padding = new Padding((int)(6 * k), (int)(8 * k), (int)(6 * k), (int)(8 * k));
        }

        // Подрезает текст многоточием, чтобы длинное имя не растягивало меню и не обрезалось по краю
        public static string Fit(string text, Font f, int maxPx)
        {
            if (string.IsNullOrEmpty(text)) return "";
            TextFormatFlags fl = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            if (TextRenderer.MeasureText(text, f, new Size(5000, 100), fl).Width <= maxPx) return text;
            int n = text.Length;
            while (n > 1 && TextRenderer.MeasureText(text.Substring(0, n).TrimEnd() + "\u2026", f, new Size(5000, 100), fl).Width > maxPx) n--;
            return text.Substring(0, n).TrimEnd() + "\u2026";
        }
        public static int MaxTextW { get { return (int)(300 * Dpi()); } }

        // Заголовок группы в меню чата: акцентный цвет, без выделения при наведении
        public static ToolStripMenuItem Head(string text) { ToolStripMenuItem it = Make(text, null); it.Tag = "head"; return it; }
        // Участник группы: аватар с точкой «в сети»; не в сети — приглушён
        public static ToolStripMenuItem Person(string nick, ChatMember m)
        {
            float k = Dpi(); int px = (int)(24 * k);
            ToolStripMenuItem it = Make(Fit(nick, Fonts.Get("NB Sans", 10.5f, "Segoe UI", FontStyle.Regular), MaxTextW), null);
            Bitmap b = new Bitmap(px, px);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Avatars.Draw(g, new Rectangle(0, 0, px, px), m.UserId, m.Nick, m.Avatar);
                if (!m.Online) using (SolidBrush dim = new SolidBrush(Color.FromArgb(130, Surface))) g.FillEllipse(dim, 0, 0, px, px);
                int d = (int)(9 * k);
                using (SolidBrush ring = new SolidBrush(Surface)) g.FillEllipse(ring, px - d - 1, px - d - 1, d + 2, d + 2);
                using (SolidBrush dot = new SolidBrush(m.Online ? Ui.Online : Ui.Offline)) g.FillEllipse(dot, px - d, px - d, d, d);
            }
            it.Image = b; it.ImageScaling = ToolStripItemImageScaling.None;
            if (!m.Online) it.Tag = "off";
            return it;
        }

        static ToolStripMenuItem Make(string text, string glyph)
        {
            ToolStripMenuItem it = new ToolStripMenuItem(text);
            it.Padding = new Padding(2, 6, 2, 6);
            if (glyph != null && HasIcons) it.Image = Glyph(glyph, Ui.Dim, (int)(20 * Dpi()));
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

        public static Color Surface { get { return Ui.Panel; } }   // тот же нейтральный фон, что у окна чата
        public static Color Hover { get { return View.Mix(Ui.Panel, Ui.Text, 0.10); } }

        class SkinColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return MenuUi.Surface; } }
            public override Color ImageMarginGradientBegin { get { return MenuUi.Surface; } }
            public override Color ImageMarginGradientMiddle { get { return MenuUi.Surface; } }
            public override Color ImageMarginGradientEnd { get { return MenuUi.Surface; } }
            public override Color MenuBorder { get { return MenuUi.Surface; } }
        }

        class SkinRenderer : ToolStripProfessionalRenderer
        {
            public SkinRenderer() : base(new SkinColors()) { RoundedEdges = false; }

            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                // выпадающее окно со скруглёнными углами (без рамки)
                ToolStripDropDown dd = e.ToolStrip as ToolStripDropDown;
                if (dd != null && dd.Width > 0 && dd.Height > 0 && (dd.Region == null || dd.Tag as string != dd.Width + "x" + dd.Height))
                {
                    try { using (GraphicsPath rp = Layered.Round(new Rectangle(0, 0, dd.Width, dd.Height), 8)) dd.Region = new System.Drawing.Region(rp); dd.Tag = dd.Width + "x" + dd.Height; } catch { }
                }
                using (SolidBrush b = new SolidBrush(MenuUi.Surface)) e.Graphics.FillRectangle(b, e.AffectedBounds);
            }
            protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }
            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { }   // без рамки
            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                if (!e.Item.Selected || !e.Item.Enabled || e.Item.Tag as string == "head") return;
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle r = new Rectangle(4, 1, e.Item.Width - 8, e.Item.Height - 2);
                using (GraphicsPath p = Layered.Round(r, 8))
                using (SolidBrush b = new SolidBrush(MenuUi.Hover)) g.FillPath(b, p);
            }
            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                int y = e.Item.Height / 2;
                using (Pen p = new Pen(Ui.Border)) e.Graphics.DrawLine(p, 12, y, e.Item.Width - 12, y);
            }
            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                string tag = e.Item.Tag as string;
                e.TextColor = !e.Item.Enabled || tag == "off" ? Ui.Dim : tag == "head" ? Ui.Accent : Ui.Text;
                if (tag == "head") e.TextFont = Ui.Medium;
                base.OnRenderItemText(e);
            }
            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
            {
                e.ArrowColor = Ui.Dim;
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
                using (SolidBrush b = new SolidBrush(Ui.Accent)) g.FillEllipse(b, box);
                using (Pen p = new Pen(Ui.OnAccent, Math.Max(1.4f, s / 6f)))
                {
                    p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                    g.DrawLines(p, new PointF[] {
                        new PointF(box.X + s * 0.24f, box.Y + s * 0.54f), new PointF(box.X + s * 0.44f, box.Y + s * 0.72f), new PointF(box.X + s * 0.78f, box.Y + s * 0.30f) });
                }
            }
        }
    }
}
