
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;   // [NEW] for click/edit
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using System.Xml.Linq;
using Autodesk.Revit.DB;
using Microsoft.Win32;
using static System.Net.Mime.MediaTypeNames;
using Ellipse = System.Windows.Shapes.Ellipse;
using Line = System.Windows.Shapes.Line;
using Panel = System.Windows.Controls.Panel;
using Point = System.Windows.Point;


// [KEEP] tránh va chạm với System.IO.Path
using WpfPath = System.Windows.Shapes.Path;

namespace RevitProjectDataAddin
{
    public partial class 梁配筋施工図 : Window
    {
        // ===== Config =====
        const double CanvasPadding = 0.70;     // khung vẽ chiếm 70% Canvas
        const double AxisStrokeThickness = 1.5;
        const double LineHitPx = 18; // kích thước vùng click theo pixel (tùy bạn 14–24)          

        // ===== Anchor enums cho text =====
        enum HAnchor { Left, Center, Right }
        enum VAnchor { Top, Middle, Bottom }

        // ===== World(mm) -> Canvas(px) transform =====
        struct WCTransform
        {
            public double Ox, Oy, Scale;
            public bool YDown; // true = Canvas Y đi xuống

            public WCTransform(double ox, double oy, double scale, bool yDown = true)
            { Ox = ox; Oy = oy; Scale = scale; YDown = yDown; }

            public Point P(double wx, double wy)
                => new Point(Ox + wx * Scale, YDown ? Oy + wy * Scale : Oy - wy * Scale);

            public double S(double lenMm) => lenMm * Scale;
        }

        // ===== Helpers: primitives theo tọa độ world (mm) =====
        static Line DrawLineW(Canvas c, WCTransform t,
            double x1, double y1, double x2, double y2,
            Brush stroke = null, double thickness = 1, DoubleCollection dash = null)
        {
            var p1 = t.P(x1, y1);
            var p2 = t.P(x2, y2);
            var line = new Line
            {
                X1 = p1.X,
                Y1 = p1.Y,
                X2 = p2.X,
                Y2 = p2.Y,
                Stroke = stroke ?? Brushes.Black,
                StrokeThickness = thickness
            };
            if (dash != null) line.StrokeDashArray = dash;
            c.Children.Add(line);
            return line;
        }

        static Polyline DrawPolylineW(Canvas c, WCTransform t,
            IEnumerable<Point> worldPts,
            Brush stroke = null, double thickness = 1, Brush fill = null)
        {
            var pl = new Polyline
            {
                Stroke = stroke ?? Brushes.Black,
                StrokeThickness = thickness,
                Fill = fill
            };
            foreach (var wp in worldPts) pl.Points.Add(t.P(wp.X, wp.Y));
            c.Children.Add(pl);
            return pl;
        }

        // Cung tròn theo tâm + bán kính + góc (độ)
        static WpfPath DrawArcByAnglesCenterW(Canvas c, WCTransform t,
            double cx, double cy, double radiusMm,
            double startDeg, double endDeg,
            Brush stroke = null, double thickness = 1, bool asClockwise = true)
        {
            double toRad = Math.PI / 180.0;
            var a0 = startDeg * toRad;
            var a1 = endDeg * toRad;

            var sx = cx + radiusMm * Math.Cos(a0);
            var sy = cy + radiusMm * Math.Sin(a0);
            var ex = cx + radiusMm * Math.Cos(a1);
            var ey = cy + radiusMm * Math.Sin(a1);

            var rx = t.S(radiusMm);
            var ry = t.S(radiusMm);

            var fig = new PathFigure { StartPoint = t.P(sx, sy), IsFilled = false, IsClosed = false };
            var seg = new ArcSegment
            {
                Point = t.P(ex, ey),
                Size = new Size(rx, ry),
                IsLargeArc = Math.Abs(endDeg - startDeg) % 360.0 > 180.0,
                SweepDirection = asClockwise ? SweepDirection.Clockwise : SweepDirection.Counterclockwise
            };
            fig.Segments.Add(seg);
            var geo = new PathGeometry(); geo.Figures.Add(fig);

            var path = new WpfPath { Stroke = stroke ?? Brushes.Black, StrokeThickness = thickness, Data = geo };
            c.Children.Add(path);
            return path;
        }

        static Ellipse DrawCircleW(Canvas c, WCTransform t,
            double cx, double cy, double radiusMm,
            Brush stroke = null, double thickness = 1, Brush fill = null)
        {
            double d = t.S(radiusMm * 2);
            var cp = t.P(cx, cy);
            var el = new Ellipse
            {
                Width = d,
                Height = d,
                Stroke = stroke ?? Brushes.Black,
                StrokeThickness = thickness,
                Fill = fill
            };
            Canvas.SetLeft(el, cp.X - d / 2.0);
            Canvas.SetTop(el, cp.Y - d / 2.0);
            c.Children.Add(el);
            return el;
        }

        // Chấm tròn kích thước theo pixel (không scale theo mm)
        static Ellipse DrawDotPx(Canvas c, WCTransform t,
            double wx, double wy, double sizePx = 4,
            Brush fill = null, Brush stroke = null, double strokePx = 0.8)
        {
            var p = t.P(wx, wy);
            var el = new Ellipse
            {
                Width = sizePx,
                Height = sizePx,
                Fill = fill ?? Brushes.Black,
                Stroke = stroke,
                StrokeThickness = strokePx
            };
            Canvas.SetLeft(el, p.X - sizePx / 2.0);
            Canvas.SetTop(el, p.Y - sizePx / 2.0);
            c.Children.Add(el);
            return el;
        }

        static TextBlock DrawTextW(Canvas c, WCTransform t,
            string text, double wx, double wy,
            double fontPx = 12, Brush color = null,
            HAnchor ha = HAnchor.Center, VAnchor va = VAnchor.Middle)
        {
            var tb = new TextBlock { Text = text ?? "", FontSize = fontPx, Foreground = color ?? Brushes.Black };
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var sz = tb.DesiredSize;

            var p = t.P(wx, wy);
            double left = p.X, top = p.Y;

            switch (ha)
            {
                case HAnchor.Center: left -= sz.Width / 2.0; break;
                case HAnchor.Right: left -= sz.Width; break;
            }
            switch (va)
            {
                case VAnchor.Middle: top -= sz.Height / 2.0; break;
                case VAnchor.Bottom: top -= sz.Height; break;
            }

            Canvas.SetLeft(tb, left);
            Canvas.SetTop(tb, top);
            c.Children.Add(tb);
            return tb;
        }
        // ===== [SCENE] Wrappers: vẽ ra Canvas đồng thời ghi vào Scene =====
        private Line DrawLine_Rec(Canvas c, WCTransform T, GridBotsecozu owner,
                                  double x1, double y1, double x2, double y2,
                                  Brush stroke = null, double thickness = 1,
                                  DoubleCollection dash = null, string layer = "LINE")
        {
            SceneFor(owner).Add(new SceneLine(x1, y1, x2, y2, layer));
            return DrawLineW(c, T, x1, y1, x2, y2, stroke, thickness, dash);
        }

        private TextBlock DrawText_Rec(Canvas c, WCTransform T, GridBotsecozu owner,
                                       string text, double wx, double wy,
                                       double fontPx = 12, Brush color = null,
                                       HAnchor ha = HAnchor.Center, VAnchor va = VAnchor.Bottom,
                                       double heightMm = 200, string layer = "TEXT")
        {
            var (h, v) = ToDxfAlign(ha, va);
            SceneFor(owner).Add(new DxfText(text ?? "", wx, wy, heightMm, hAlign: h, vAlign: v, rotDeg: 0, layer: layer));
            return DrawTextW(c, T, text, wx, wy, fontPx, color, ha, va);
        }
        private Ellipse DrawDotMm_Rec(Canvas c, WCTransform T, GridBotsecozu owner,
                                 double wx, double wy, double rMm = 60,
                                 Brush fill = null, Brush stroke = null, double strokePx = 0.8,
                                 string layer = "MARK")
        {
            // (1) Viền tròn cho đẹp (CIRCLE – tùy thích, có thể giữ)
            SceneFor(owner).Add(new DxfCircle(wx, wy, rMm, layer));

            // (2) Ruột đặc bằng SOLID hình “kim cương” (4 đỉnh), rất bền với mọi CAD
            // 4 đỉnh: trên – phải – dưới – trái (xoay 45° trông như chấm tròn)
            double r = rMm;
            SceneFor(owner).Add(new DxfSolid(
                wx, wy + r,   // v1
                wx + r, wy,   // v2
                wx, wy - r,   // v3
                wx - r, wy,   // v4
                layer
            ));

            // UI: vẫn vẽ ellipse nhỏ theo pixel để nhìn sắc nét trên WPF
            double sizePx = Math.Max(4.0, 2.0 * rMm * T.Scale);
            return DrawDotPx(c, T, wx, wy, sizePx, fill ?? Brushes.Gray, stroke, strokePx);
        }



        // Nối các trục liên tiếp tại cao độ yChain (world, mm).
        void DrawAxisChain(Canvas c, WCTransform T, IList<double> pos, IList<double> spans,
                           double yChain, Brush stroke, double thickness,
                           bool withTicks = true, double tickLen = 0,
                           double labelSpanDy = -150, Brush labelBrush = null, double labelFont = 12)
        {
            for (int i = 0; i < pos.Count - 1; i++)
                DrawLineW(c, T, pos[i], yChain, pos[i + 1], yChain, stroke, thickness);

            if (withTicks && tickLen > 0)
            {
                double half = tickLen / 2;
                for (int i = 0; i < pos.Count; i++)
                    DrawLineW(c, T, pos[i], yChain - half, pos[i], yChain + half, stroke, thickness);
            }

            // [CHANGE] KHÔNG vẽ text span ở đây nữa (để vẽ loại "editable" bên dưới)
        }

        // ===== Parse số mm =====
        static double ParseMm(string s)
            => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

        // [NEW] ===== đổi chuỗi/number thành double an toàn
        static double AsDouble(object v)
        {
            if (v == null) return 0;
            if (v is double d) return d;
            if (v is int i) return i;
            var s = v.ToString();
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) return x;
            if (double.TryParse(s, out x)) return x;
            return 0;
        }

        // [NEW] Lấy offsets cột (Up/Down/Left/Right) cho cặp (階,通) hiện tại để vẽ 4 line dọc khung bù
        // nhớ: using System.Text;
        private (double up, double down, double left, double right) GetOffsetsByPosition(string kai, string tsu, string name)
        {
            double up = 500, down = 500, left = 500, right = 500;
            // key và nhãn vị trí
            string key = $"{kai}::{tsu}";            // ví dụ: "1F::Y1"
            string posLabel = $"{kai} {tsu}-{name}"; // ví dụ: "1F Y1-X1"

            var haichiList = _projectData?.Haichi?.柱配置図;
            if (haichiList == null)
                return (up, down, left, right);

            foreach (var haichi in haichiList)
            {
                if (haichi?.BeamSegmentsMap == null) continue;
                if (!haichi.BeamSegmentsMap.TryGetValue(key, out var segs) || segs == null) continue;

                var seg = segs.FirstOrDefault(s => s.位置表示 == posLabel);
                if (seg != null)
                {
                    // parse string → double an toàn
                    double Parse(string s) =>
                        double.TryParse(s, System.Globalization.NumberStyles.Float,
                                        System.Globalization.CultureInfo.InvariantCulture,
                                        out var v) ? v : 0;

                    up = Parse(seg.上側のズレ);
                    down = Parse(seg.下側のズレ);
                    left = Parse(seg.左側のズレ);
                    right = Parse(seg.右側のズレ);
                    break; // đã tìm thấy thì thoát luôn
                }
            }

            return (up, down, left, right);
        }

        // [NEW] === cập nhật 1 giá trị Span (mm) trong ProjectData.Kihon ===
        // tsuIsY => đang vẽ dãy X nên dùng ListSpanX; tsuIsX => đang vẽ dãy Y nên dùng ListSpanY
        private bool UpdateSpanValue(int spanIndex, double newMm, bool tsuIsX, bool tsuIsY)
        {
            var k = _projectData?.Kihon;
            if (k == null) return false;

            object listObj = tsuIsY ? (object)k.ListSpanX : (object)k.ListSpanY;
            if (listObj == null) return false;

            if (listObj is IList list && spanIndex >= 0 && spanIndex < list.Count)
            {
                var item = list[spanIndex];
                if (item == null) return false;

                var p = item.GetType().GetProperty("Span");
                if (p == null) return false;

                // set "Span" = chuỗi mm invariant
                p.SetValue(item, newMm.ToString(CultureInfo.InvariantCulture));
                return true;
            }
            return false;
        }

        // [NEW] === Vẽ nhãn span có thể click-để-sửa ===
        private void DrawEditableSpanLabel(Canvas canvas, WCTransform T,
                                           double cx, double cy,
                                           int spanIndex,
                                           string initialText,
                                           bool tsuIsX, bool tsuIsY,
                                           GridBotsecozu item)
        {
            var tb = new TextBlock
            {
                Text = initialText,
                FontSize = 12,
                Foreground = Brushes.Black,
                Background = Brushes.Transparent,
                Padding = new Thickness(6, 2, 6, 2), // <-- click dễ hơn
                Cursor = Cursors.IBeam,
                IsHitTestVisible = true
            };


            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var sz = tb.DesiredSize;
            var p = T.P(cx, cy);
            // [SCENE] đảm bảo DXF có nhãn span hiện tại
            SceneFor(item).Add(new DxfText(initialText ?? "", cx, cy, 200, hAlign: 1, vAlign: 1, rotDeg: 0, layer: "TEXT"));


            Canvas.SetLeft(tb, p.X - sz.Width / 2);
            Canvas.SetTop(tb, p.Y - sz.Height);
            Panel.SetZIndex(tb, 1000);
            canvas.Children.Add(tb);

            tb.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;

                var editor = new TextBox
                {
                    Text = tb.Text,
                    FontSize = 12,
                    Width = Math.Max(60, sz.Width + 20),
                    Background = Brushes.White
                };

                Canvas.SetLeft(editor, p.X - editor.Width / 2);
                Canvas.SetTop(editor, p.Y - sz.Height - 2);
                Panel.SetZIndex(editor, 2000);
                canvas.Children.Add(editor);

                editor.Focus();
                editor.SelectAll();

                void CommitAndRedraw()
                {
                    if (double.TryParse(editor.Text, NumberStyles.Float,
                                        CultureInfo.InvariantCulture, out var mm)
                     || double.TryParse(editor.Text, out mm))
                    {
                        if (mm > 0)
                        {
                            if (UpdateSpanValue(spanIndex, mm, tsuIsX, tsuIsY))
                            {
                                Redraw(canvas, item);
                                return;
                            }
                        }
                    }
                    canvas.Children.Remove(editor);
                }

                editor.KeyDown += (ks, ke) =>
                {
                    if (ke.Key == Key.Enter) CommitAndRedraw();
                    else if (ke.Key == Key.Escape) { canvas.Children.Remove(editor); ke.Handled = true; }
                };

                editor.LostKeyboardFocus += (ls, le) => CommitAndRedraw();
            };
        }

        // ======== [NEW] Tương tác kiểu Excel cho LINE ========

        enum LineRole { Chain, AxisCenter, AxisOffset, Other }

        sealed class LineMeta
        {
            public LineRole Role;
            public int? Index;
            public double X1, Y1, X2, Y2; // world-mm
            public GridBotsecozu Owner;
            public Brush OriginalBrush;
        }

        private Line _selectedLine;
        private LineMeta _selectedMeta;
        private readonly Brush _highlight = Brushes.DodgerBlue;

        private void SelectVisual(Line visual, LineMeta meta)
        {
            if (_selectedLine != null && _selectedMeta != null)
                _selectedLine.Stroke = _selectedMeta.OriginalBrush;

            _selectedLine = visual;
            _selectedMeta = meta;
            visual.Stroke = _highlight;
        }
        private readonly Dictionary<GridBotsecozu, List<double>> _extraChainsByItem = new Dictionary<GridBotsecozu, List<double>>();

        private List<double> ExtraChainsFor(GridBotsecozu item)
        {
            if (!_extraChainsByItem.TryGetValue(item, out var list))
            {
                list = new List<double>();
                _extraChainsByItem[item] = list;
            }
            return list;
        }

        private Line AddLineInteractive(
     Canvas c, WCTransform T,
     double x1, double y1, double x2, double y2,
     Brush stroke, double thickness,
     LineRole role, GridBotsecozu owner, int? index = null)
        {
            var p1 = T.P(x1, y1);
            var p2 = T.P(x2, y2);

            var meta = new LineMeta
            {
                Role = role,
                Index = index,
                X1 = x1,
                Y1 = y1,
                X2 = x2,
                Y2 = y2,
                Owner = owner,
                OriginalBrush = stroke
            };

            // (A) ĐƯỜNG HIỂN THỊ: mảnh, đúng màu
            var visual = new Line
            {
                X1 = p1.X,
                Y1 = p1.Y,
                X2 = p2.X,
                Y2 = p2.Y,
                Stroke = stroke,
                StrokeThickness = thickness,
                SnapsToDevicePixels = true,
                IsHitTestVisible = false,       // <-- để đường đè (hit) bắt sự kiện
                Tag = meta
            };

            // (B) ĐƯỜNG ĐÈ (HITBOX): dày, trong suốt, chỉ để bắt click
            var hit = new Line
            {
                X1 = p1.X,
                Y1 = p1.Y,
                X2 = p2.X,
                Y2 = p2.Y,
                Stroke = Brushes.Transparent,   // <-- vô hình
                StrokeThickness = Math.Max(thickness, LineHitPx),
                SnapsToDevicePixels = true,
                Cursor = Cursors.Hand,
                IsHitTestVisible = true,
                Tag = meta
            };

            // Sự kiện click đổi màu/chọn
            hit.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                SelectVisual(visual, meta);
            };

            // Menu chuột phải (insert/delete chain ...)
            hit.MouseRightButtonUp += (s, e) =>
            {
                e.Handled = true;
                ShowContextMenuForLine(c, visual, meta); // truyền "visual" để đổi màu/refresh
            };

            // Thứ tự add: hit trước, visual sau (visual nằm trên, nhưng IsHitTestVisible=false)
            c.Children.Add(hit);
            c.Children.Add(visual);
            // [SCENE] Ghi thêm line tương tác vào Scene để export dùng chung
            string layer = meta.Role == LineRole.AxisCenter ? "AXIS"
                         : meta.Role == LineRole.Chain ? "CHAIN"
                         : meta.Role == LineRole.AxisOffset ? "OFFSET"
                         : "LINE";
            SceneFor(owner).Add(new SceneLine(x1, y1, x2, y2, layer));

            return visual;
        }

        private void ShowContextMenuForLine(Canvas canvas, Line line, LineMeta meta)
        {
            var cm = new ContextMenu();

            if (meta.Role == LineRole.Chain)
            {
                cm.Items.Add(new Separator());

                const double deltaY = 300; // mm
                var miInsBelow = new MenuItem { Header = "Chèn line mới phía trên" };
                var miInsAbove = new MenuItem { Header = "Chèn line mới phía dưới" };

                var miDelete = new MenuItem { Header = "Xóa line này" };

                miInsAbove.Click += (s, e) =>
                {
                    var list = ExtraChainsFor(meta.Owner);
                    list.Add(meta.Y1 + deltaY);
                    Redraw(canvas, meta.Owner);
                };
                miInsBelow.Click += (s, e) =>
                {
                    var list = ExtraChainsFor(meta.Owner);
                    var y = Math.Max(0, meta.Y1 - deltaY);
                    list.Add(y);
                    Redraw(canvas, meta.Owner);
                };
                miDelete.Click += (s, e) =>
                {
                    var list = ExtraChainsFor(meta.Owner);
                    const double eps = 1e-6;
                    var idx = list.FindIndex(v => Math.Abs(v - meta.Y1) < eps);
                    if (idx >= 0) list.RemoveAt(idx);
                    // nếu là chain mặc định (y=300), menu xoá sẽ không làm gì
                    Redraw(canvas, meta.Owner);
                };

                cm.Items.Add(miInsBelow);
                cm.Items.Add(miInsAbove);
                cm.Items.Add(miDelete);
            }

            line.ContextMenu = cm;
            cm.IsOpen = true;
        }

        // ===== [ZOOM] View state (per-item) =====
        sealed class ViewState
        {
            public double Zoom = 1.0;   // 1.0 = fit
            public double PanXmm = 0.0; // pan theo mm (world)
            public double PanYmm = 0.0;
        }

        private readonly Dictionary<GridBotsecozu, ViewState> _viewByItem = new Dictionary<GridBotsecozu, ViewState>();
        private ViewState VS(GridBotsecozu item)
        {
            if (!_viewByItem.TryGetValue(item, out var vs))
            {
                vs = new ViewState();
                _viewByItem[item] = vs;
            }
            return vs;
        }

        // Trả về transform hiện tại + fitScale (để tính zoom quanh con trỏ)
        private bool TryMakeTransform(Canvas canvas, GridBotsecozu item,
                                      out WCTransform T, out double fitScale,
                                      out double W, out double H)
        {
            T = default; fitScale = 1; W = H = 0;
            var k = _projectData?.Kihon;
            if (k == null || _currentSecoList == null) return false;

            bool tsuIsX = k.NameX.Any(n => n.Name == _currentSecoList.通を選択);
            bool tsuIsY = k.NameY.Any(n => n.Name == _currentSecoList.通を選択);

            var names = tsuIsY ? k.NameX.Select(n => n.Name).ToList()
                               : tsuIsX ? k.NameY.Select(n => n.Name).ToList()
                                        : new List<string>();
            var spans = tsuIsY ? k.ListSpanX.Select(s => ParseMm(s.Span)).ToList()
                               : tsuIsX ? k.ListSpanY.Select(s => ParseMm(s.Span)).ToList()
                                        : new List<double>();
            if (names.Count < 2 || spans.Count < names.Count - 1) return false;

            W = spans.Take(names.Count - 1).Sum();
            if (W <= 0) return false;
            H = Math.Max(1, W * 0.4);

            double maxW = Math.Max(1, canvas.ActualWidth * CanvasPadding);
            double maxH = Math.Max(1, canvas.ActualHeight * CanvasPadding);
            fitScale = Math.Min(maxW / W, maxH / H);

            var vs = VS(item);
            // Replace all usages of Math.Clamp with Clamp in the file
            double scale = fitScale * Clamp(vs.Zoom, 0.05, 20.0);

            double baseOx = canvas.ActualWidth / 2.0;
            double baseOy = (canvas.ActualHeight - 800) / 2.0; // [KEEP]
            double ox = baseOx + vs.PanXmm * scale;
            double oy = baseOy + vs.PanYmm * scale;

            T = new WCTransform(ox, oy, scale, yDown: true);
            return true;
        }

        private static Point ScreenToWorld(WCTransform T, Point sp)
        {
            double wx = (sp.X - T.Ox) / T.Scale;
            double wy = (sp.Y - T.Oy) / T.Scale; // YDown=true
            return new Point(wx, wy);
        }

        // ===== [ZOOM] Trạng thái pan tạm thời khi kéo chuột =====
        bool _isPanning = false;
        Point _panStartPx;         // điểm màn hình lúc bắt đầu pan
        Point _panStartPanMm;      // pan mm lúc bắt đầu pan


        private void ShowSingleSegmentByKey(string kai, string tsu, string name)
        {
            // key và nhãn vị trí
            string key = $"{kai}::{tsu}";        // ví dụ: "1F::Y1"
            string posLabel = $"{kai} {tsu}-{name}"; // ví dụ: "1F Y1-X1"

            var haichiList = _projectData?.Haichi?.柱配置図;
            if (haichiList == null)
            {
                MessageBox.Show("Chưa có dữ liệu haichi (柱配置図).");
                return;
            }

            foreach (var haichi in haichiList)
            {
                if (haichi?.BeamSegmentsMap == null) continue;
                if (!haichi.BeamSegmentsMap.TryGetValue(key, out var segs) || segs == null) continue;

                var seg = segs.FirstOrDefault(s => s.位置表示 == posLabel);
                if (seg != null)
                {
                    string info =
                        $"位置表示: {seg.位置表示}\n" +
                        $"柱の符号: {seg.柱の符号}\n" +
                        $"上側のズレ: {seg.上側のズレ}\n" +
                        $"下側のズレ: {seg.下側のズレ}\n" +
                        $"左側のズレ: {seg.左側のズレ}\n" +
                        $"右側のズレ: {seg.右側のズレ}";
                    MessageBox.Show(info, $"Segment: {posLabel}");
                    return;
                }
            }

            MessageBox.Show($"Không tìm thấy segment: {posLabel}");
        }
        // ======= PREVIEW: Redraw =======
        private void Redraw(Canvas canvas, GridBotsecozu item)
        {

            if (canvas == null || _projectData?.Kihon == null || _currentSecoList == null) return;
            canvas.Children.Clear();
            // [SCENE] mỗi lần vẽ lại thì reset Scene của item
            SceneBegin(item);


            var k = _projectData.Kihon;
            bool tsuIsX = k.NameX.Any(n => n.Name == _currentSecoList.通を選択); // chọn Xk → vẽ dãy Y
            bool tsuIsY = k.NameY.Any(n => n.Name == _currentSecoList.通を選択); // chọn Yk → vẽ dãy X

            var names = tsuIsY ? k.NameX.Select(n => n.Name).ToList()
                               : tsuIsX ? k.NameY.Select(n => n.Name).ToList()
                                        : new List<string>();

            var spans = tsuIsY ? k.ListSpanX.Select(s => ParseMm(s.Span)).ToList()
                               : tsuIsX ? k.ListSpanY.Select(s => ParseMm(s.Span)).ToList()
                                        : new List<double>();

            if (names.Count < 2 || spans.Count < names.Count - 1) return;

            // Kích thước world
            double W = spans.Take(names.Count - 1).Sum();
            if (W <= 0) return;
            double H = Math.Max(1, W * 0.4); // tỉ lệ tạm

            // Scale & origin (Canvas Y-Down)
            // ... sau khi có W, H
            double maxW = Math.Max(1, canvas.ActualWidth * CanvasPadding);
            double maxH = Math.Max(1, canvas.ActualHeight * CanvasPadding);
            double fitScale = Math.Min(maxW / W, maxH / H);

            var vs = VS(item);
            // Replace all usages of Math.Clamp with Clamp in the file

            // Example replacement in Redraw method:
            double scale = fitScale * Clamp(vs.Zoom, 0.05, 20.0);

            double baseOx = canvas.ActualWidth / 2.0;
            double baseOy = (canvas.ActualHeight - 800) / 2.0; // [KEEP]
            double ox = baseOx + vs.PanXmm * scale;
            double oy = baseOy + vs.PanYmm * scale;

            var T = new WCTransform(ox, oy, scale, yDown: true);
            var F = 階を選択ComboBox.SelectedItem;

            // Vị trí trục X (world)
            var pos = new List<double> { -W / 2.0 };
            for (int i = 0; i < names.Count - 1; i++) pos.Add(pos[i] + spans[i]);

            double y0 = 0, yH = H;

            // === Precompute per-axis offsets ===
            var up = new double[pos.Count];
            var down = new double[pos.Count];
            var left = new double[pos.Count];
            var right = new double[pos.Count];

            string selY = 通を選択ComboBox.SelectedItem?.ToString() ?? "";
            string selF = F?.ToString() ?? "";

            for (int i = 0; i < pos.Count; i++)
            {
                string axisName = (i < names.Count) ? names[i] : "";
                double ui, di, li, ri;
                if (tsuIsY)  // đang duyệt trục X → key (F, Y, X)
                    (ui, di, li, ri) = GetOffsetsByPosition(selF, selY, axisName);
                else         // đang duyệt trục Y → key (F, X, Y)
                    (ui, di, li, ri) = GetOffsetsByPosition(selF, axisName, selY);

                up[i] = ui; down[i] = di; left[i] = li; right[i] = ri;
            }


            double yChainMid = 600;        // mm: cao độ dim + chain hiệu dụng
            double yChainBot = 900;        // mm: cao độ chain 1/4–1/2–1/4
            double dimFont = 12;

            Brush dimBrush = Brushes.DimGray;
            // 1) Trục dọc (đỏ) + 2 line biên offset mỗi trục (tùy dữ liệu)
            for (int i = 0; i < pos.Count; i++)
            {

                // vẽ trục đỏ
                DrawLine_Rec(canvas, T, item, pos[i], y0, pos[i], yH, Brushes.Red, AxisStrokeThickness, null, "AXIS");

                // nhãn tên trục
                DrawText_Rec(canvas, T, item, i < names.Count ? names[i] : "", pos[i], y0, fontPx: 12, color: Brushes.Black,
                             ha: HAnchor.Center, va: VAnchor.Bottom,
                             heightMm: 200, layer: "TEXT");


                //MessageBox.Show($"{names[i]}");
                if (tsuIsY) // đang duyệt trục X → dùng up/down làm biên trái/phải
                {
                    double xLeft = pos[i] - up[i];
                    double xRight = pos[i] + down[i];

                    DrawLine_Rec(canvas, T, item, xLeft, y0, xLeft, yH, Brushes.DimGray, 1.2, null, "OFFSET");
                    DrawLine_Rec(canvas, T, item, xRight, y0, xRight, yH, Brushes.DimGray, 1.2, null, "OFFSET");

                    //DrawText_Rec(canvas, T, item, $"{up[i]:0}", xLeft, y0, 12, Brushes.Gray, HAnchor.Center, VAnchor.Bottom, 200, "TEXT");
                    //DrawText_Rec(canvas, T, item, $"{down[i]:0}", xRight, y0, 12, Brushes.Gray, HAnchor.Center, VAnchor.Bottom, 200, "TEXT");

                    // ==== DIM tâm→trái/phải tại yChainMid (dùng DOT, đi DXF) ====
                    if (pos[i] > xLeft)
                    {
                        DrawLine_Rec(canvas, T, item, xLeft, yChainMid, pos[i], yChainMid, dimBrush, 1.2, null, "DIM");
                        DrawDotMm_Rec(canvas, T, item, xLeft, yChainMid, rMm: 25, layer: "DIM", fill: dimBrush);
                        DrawDotMm_Rec(canvas, T, item, pos[i], yChainMid, rMm: 25, layer: "DIM", fill: dimBrush);

                        double cxL = (xLeft + pos[i]) / 2.0;
                        DrawText_Rec(canvas, T, item, $"{up[i]:0}", cxL, yChainMid - 0,
                                     dimFont, dimBrush, HAnchor.Center, VAnchor.Bottom, 160, "TEXT");
                    }
                    if (xRight > pos[i])
                    {
                        DrawLine_Rec(canvas, T, item, pos[i], yChainMid, xRight, yChainMid, dimBrush, 1.2, null, "DIM");
                        DrawDotMm_Rec(canvas, T, item, pos[i], yChainMid, rMm: 25, layer: "DIM", fill: dimBrush);
                        DrawDotMm_Rec(canvas, T, item, xRight, yChainMid, rMm: 25, layer: "DIM", fill: dimBrush);

                        double cxR = (pos[i] + xRight) / 2.0;
                        DrawText_Rec(canvas, T, item, $"{down[i]:0}", cxR, yChainMid - 0,
                                     dimFont, dimBrush, HAnchor.Center, VAnchor.Bottom, 160, "TEXT");
                    }

                }
                else if (tsuIsX) // đang duyệt trục Y → dùng left/right
                {
                    double xLeft = pos[i] - left[i];
                    double xRight = pos[i] + right[i];

                    DrawLine_Rec(canvas, T, item, xLeft, y0, xLeft, yH, Brushes.DimGray, 1.2, null, "OFFSET");
                    DrawLine_Rec(canvas, T, item, xRight, y0, xRight, yH, Brushes.DimGray, 1.2, null, "OFFSET");

                    //DrawText_Rec(canvas, T, item, $"{left[i]:0}", xLeft, y0, 12, Brushes.Gray, HAnchor.Center, VAnchor.Bottom, 200, "TEXT");
                    //DrawText_Rec(canvas, T, item, $"{right[i]:0}", xRight, y0, 12, Brushes.Gray, HAnchor.Center, VAnchor.Bottom, 200, "TEXT");

                    // ==== DIM tâm→trái/phải tại yChainMid (dùng DOT, đi DXF) ====
                    if (pos[i] > xLeft)
                    {
                        DrawLine_Rec(canvas, T, item, xLeft, yChainMid, pos[i], yChainMid, dimBrush, 1.2, null, "DIM");
                        DrawDotMm_Rec(canvas, T, item, xLeft, yChainMid, rMm: 25, layer: "DIM", fill: dimBrush);
                        DrawDotMm_Rec(canvas, T, item, pos[i], yChainMid, rMm: 25, layer: "DIM", fill: dimBrush);

                        double cxL = (xLeft + pos[i]) / 2.0;
                        DrawText_Rec(canvas, T, item, $"{left[i]:0}", cxL, yChainMid - 0,
                                     dimFont, dimBrush, HAnchor.Center, VAnchor.Bottom, 160, "TEXT");
                    }
                    if (xRight > pos[i])
                    {
                        DrawLine_Rec(canvas, T, item, pos[i], yChainMid, xRight, yChainMid, dimBrush, 1.2, null, "DIM");
                        DrawDotMm_Rec(canvas, T, item, pos[i], yChainMid, rMm: 25, layer: "DIM", fill: dimBrush);
                        DrawDotMm_Rec(canvas, T, item, xRight, yChainMid, rMm: 25, layer: "DIM", fill: dimBrush);

                        double cxR = (pos[i] + xRight) / 2.0;
                        DrawText_Rec(canvas, T, item, $"{right[i]:0}", cxR, yChainMid - 0,
                                     dimFont, dimBrush, HAnchor.Center, VAnchor.Bottom, 160, "TEXT");
                    }

                }
            }

            // ===== 2) Chuỗi nối hiệu dụng per-span @ yChainMid + nhãn tổng eff =====
            if (tsuIsY)
            {
                for (int j = 0; j < pos.Count - 1; j++)
                {
                    double xA = pos[j] + down[j];    // phải trục j
                    double xB = pos[j + 1] - up[j + 1];  // trái trục j+1
                    if (xB <= xA) continue;

                    double eff = (pos[j + 1] - pos[j]) - (down[j] + up[j + 1]);
                    double cx = (xA + xB) / 2.0;

                    DrawLine_Rec(canvas, T, item, xA, yChainMid, xB, yChainMid, Brushes.DimGray, 1.2, null, "CHAIN");
                    DrawText_Rec(canvas, T, item, $"{eff:0}", cx, yChainMid - 0,
                                 12, Brushes.Gray, HAnchor.Center, VAnchor.Bottom, 200, "TEXT");

                    // 3-đoạn (1/4–1/2–1/4) + dot @ 1/4, 3/4 @ yChainBot
                    double L = xB - xA;
                    double xQ1 = xA + 0.25 * L;
                    double xQ3 = xA + 0.75 * L;

                    DrawLine_Rec(canvas, T, item, xA, yChainBot, xQ1, yChainBot, Brushes.DimGray, 1.2, null, "CHAIN");
                    DrawLine_Rec(canvas, T, item, xQ1, yChainBot, xQ3, yChainBot, Brushes.DimGray, 1.2, null, "CHAIN");
                    DrawLine_Rec(canvas, T, item, xQ3, yChainBot, xB, yChainBot, Brushes.DimGray, 1.2, null, "CHAIN");

                    double v14 = eff * 0.25, v12 = eff * 0.50;
                    double ly = yChainBot; // nếu muốn nâng chữ lên: dùng yChainBot - 80

                    DrawText_Rec(canvas, T, item, $"{v14:0}", (xA + xQ1) / 2.0, ly, 12, Brushes.Gray, HAnchor.Center, VAnchor.Bottom, 160, "TEXT");
                    DrawText_Rec(canvas, T, item, $"{v12:0}", (xQ1 + xQ3) / 2.0, ly, 12, Brushes.Gray, HAnchor.Center, VAnchor.Bottom, 160, "TEXT");
                    DrawText_Rec(canvas, T, item, $"{v14:0}", (xQ3 + xB) / 2.0, ly, 12, Brushes.Gray, HAnchor.Center, VAnchor.Bottom, 160, "TEXT");

                    DrawDotMm_Rec(canvas, T, item, xQ1, yChainBot, rMm: 25, layer: "MARK", fill: Brushes.Gray);
                    DrawDotMm_Rec(canvas, T, item, xQ3, yChainBot, rMm: 25, layer: "MARK", fill: Brushes.Gray);
                }
            }
            else if (tsuIsX)
            {
                for (int j = 0; j < pos.Count - 1; j++)
                {
                    double xA = pos[j] + right[j];
                    double xB = pos[j + 1] - left[j + 1];
                    if (xB <= xA) continue;

                    double eff = (pos[j + 1] - pos[j]) - (right[j] + left[j + 1]);
                    double cx = (xA + xB) / 2.0;

                    DrawLine_Rec(canvas, T, item, xA, yChainMid, xB, yChainMid, Brushes.DimGray, 1.2, null, "CHAIN");
                    DrawText_Rec(canvas, T, item, $"{eff:0}", cx, yChainMid - 0,
                                 12, Brushes.Gray, HAnchor.Center, VAnchor.Bottom, 200, "TEXT");

                    double L = xB - xA;
                    double xQ1 = xA + 0.25 * L;
                    double xQ3 = xA + 0.75 * L;

                    DrawLine_Rec(canvas, T, item, xA, yChainBot, xQ1, yChainBot, Brushes.DimGray, 1.2, null, "CHAIN");
                    DrawLine_Rec(canvas, T, item, xQ1, yChainBot, xQ3, yChainBot, Brushes.DimGray, 1.2, null, "CHAIN");
                    DrawLine_Rec(canvas, T, item, xQ3, yChainBot, xB, yChainBot, Brushes.DimGray, 1.2, null, "CHAIN");

                    double v14 = eff * 0.25, v12 = eff * 0.50;
                    double ly = yChainBot;

                    DrawText_Rec(canvas, T, item, $"{v14:0}", (xA + xQ1) / 2.0, ly, 12, Brushes.Gray, HAnchor.Center, VAnchor.Bottom, 160, "TEXT");
                    DrawText_Rec(canvas, T, item, $"{v12:0}", (xQ1 + xQ3) / 2.0, ly, 12, Brushes.Gray, HAnchor.Center, VAnchor.Bottom, 160, "TEXT");
                    DrawText_Rec(canvas, T, item, $"{v14:0}", (xQ3 + xB) / 2.0, ly, 12, Brushes.Gray, HAnchor.Center, VAnchor.Bottom, 160, "TEXT");

                    DrawDotMm_Rec(canvas, T, item, xQ1, yChainBot, rMm: 25, layer: "MARK", fill: Brushes.Gray);
                    DrawDotMm_Rec(canvas, T, item, xQ3, yChainBot, rMm: 25, layer: "MARK", fill: Brushes.Gray);
                }
            }

            // 3) Chuỗi nối mặc định (y=300)
            double yChain = 300; // mm tính từ đáy (y=0)


            DrawLine_Rec(canvas, T, item, pos.First(), yChain, pos.Last(), yChain, Brushes.Black, 1.2, null, "CHAIN");


            // 3b) [NEW] Nhãn SPAN click-để-sửa (ngay trên chain mặc định)
            for (int i = 0; i < names.Count - 1 && i < spans.Count; i++)
            {
                double x0 = pos[i];
                double x1 = pos[i + 1];
                double L = x1 - x0;

                double mid = x0 + L / 2.0;    // trung điểm
                double qL = (x0 + L / 4.0) + 250;    // 1/4 bên trái
                double qR = (x1 - L / 4.0) - 250;    // 1/4 bên phải

                double qL1 = (x0 + L / 8.0) + 250 + 125;    // 1/4 bên trái
                double qR1 = (x1 - L / 8.0) - 250 - 125;    // 1/4 bên phải
                // (tuỳ chọn) giữ lại nhãn span như bạn đang có:
                double cx = (x0 + x1) / 2.0;
                DrawEditableSpanLabel(canvas, T, cx, yChain - 150,
                                      i, spans[i].ToString("0"),
                                      tsuIsX, tsuIsY, item);

                // Vạch đánh dấu 200mm cao, màu xanh
                double tick = 5000;

                //// 1/4L (trái)
                DrawLine_Rec(canvas, T, item, qL, yChain + 600, qL, yChain + 4900,
                             Brushes.Green, 1.2, null, "MARK");
                //DrawText_Rec(canvas, T, item, "1/4L", qL, yChain + 1.5 * tick,
                //             11, Brushes.Blue, HAnchor.Center, VAnchor.Bottom, 200, "TEXT");
                //// 1/8L (trái)
                //DrawLine_Rec(canvas, T, item, qL1, yChain - tick, qL1, yChain + tick,
                //             Brushes.Red, 1.2, null, "MARK");
                DrawText_Rec(canvas, T, item, "3",
                         qL1, yChain + 1500,
                         12, Brushes.Red, HAnchor.Center, VAnchor.Bottom, 200, "TEXT");
                DrawText_Rec(canvas, T, item, "2",
                         qL1, yChain + 1700,
                         12, Brushes.Red, HAnchor.Center, VAnchor.Bottom, 200, "TEXT");
                DrawText_Rec(canvas, T, item, "0",
                         qL1, yChain + 1900,
                         12, Brushes.Red, HAnchor.Center, VAnchor.Bottom, 200, "TEXT");
                DrawText_Rec(canvas, T, item, "0",
                         qL1, yChain + 2350,
                         12, Brushes.Red, HAnchor.Center, VAnchor.Bottom, 200, "TEXT");
                DrawText_Rec(canvas, T, item, "2",
                         qL1, yChain + 2550,
                         12, Brushes.Red, HAnchor.Center, VAnchor.Bottom, 200, "TEXT");
                DrawText_Rec(canvas, T, item, "3",
                         qL1, yChain + 2750,
                         12, Brushes.Red, HAnchor.Center, VAnchor.Bottom, 200, "TEXT");
                DrawLine_Rec(canvas, T, item, qL1 - 500, yChain + 1300, qL1 + 500, yChain + 1300,
                         Brushes.Blue, 1.2, null, "CHAIN");
                DrawLine_Rec(canvas, T, item,
                         qL1 - 500, yChain + 2800, qL1 + 500, yChain + 2800,
                         Brushes.Blue, 1.2, null, "CHAIN");
                DrawLine_Rec(canvas, T, item,
                         qL1 - 500, yChain + 1300, qL1 - 500, yChain + 2800,
                         Brushes.Blue, 1.2, null, "MARK");
                DrawLine_Rec(canvas, T, item,
                         qL1 + 500, yChain + 1300, qL1 + 500, yChain + 2800,
                         Brushes.Blue, 1.2, null, "MARK");

                // MID
                //DrawLine_Rec(canvas, T, item, 
                //        mid, yChain - tick, mid, yChain + tick,
                //        Brushes.Red, 1.2, null, "MARK");
                //DrawText_Rec(canvas, T, item, "MID", 
                //         mid, yChain + 1.5 * tick,
                //         12, Brushes.Red, HAnchor.Center, VAnchor.Bottom, 200, "TEXT");
                //
                string beamCode = "G0";
                var layout = _projectData?.Haichi?.梁配置図?.FirstOrDefault();
                if (layout?.BeamSegmentsMap != null)
                {
                    var key = $"{selF}::{selY}";
                    if (layout.BeamSegmentsMap.TryGetValue(key, out var segs) && segs != null && i < segs.Count)
                        beamCode = segs[i].梁の符号 ?? "G0";
                }
                DrawText_Rec(canvas, T, item, beamCode,
                         mid, yChain + 900,
                        12, Brushes.Red, HAnchor.Center, VAnchor.Bottom, 200, "TEXT");
                DrawText_Rec(canvas, T, item, "(-200)",
                         mid, yChain + 1200,
                        12, Brushes.Red, HAnchor.Center, VAnchor.Bottom, 200, "TEXT");
                DrawText_Rec(canvas, T, item, "3",
                         mid, yChain + 1500,
                         12, Brushes.Red, HAnchor.Center, VAnchor.Bottom, 200, "TEXT");
                DrawText_Rec(canvas, T, item, "2",
                         mid, yChain + 1700,
                         12, Brushes.Red, HAnchor.Center, VAnchor.Bottom, 200, "TEXT");
                DrawText_Rec(canvas, T, item, "0",
                         mid, yChain + 1900,
                         12, Brushes.Red, HAnchor.Center, VAnchor.Bottom, 200, "TEXT");
                DrawText_Rec(canvas, T, item, "0",
                         mid, yChain + 2350,
                         12, Brushes.Red, HAnchor.Center, VAnchor.Bottom, 200, "TEXT");
                DrawText_Rec(canvas, T, item, "2",
                         mid, yChain + 2550,
                         12, Brushes.Red, HAnchor.Center, VAnchor.Bottom, 200, "TEXT");
                DrawText_Rec(canvas, T, item, "3",
                         mid, yChain + 2750,
                         12, Brushes.Red, HAnchor.Center, VAnchor.Bottom, 200, "TEXT");
                DrawText_Rec(canvas, T, item, "600x900",
                         mid, yChain + 3050,
                         12, Brushes.Red, HAnchor.Center, VAnchor.Bottom, 200, "TEXT");
                DrawLine_Rec(canvas, T, item, mid - 500, yChain + 1300, mid + 500, yChain + 1300,
                         Brushes.Blue, 1.2, null, "CHAIN");
                DrawLine_Rec(canvas, T, item,
                         mid - 500, yChain + 2800, mid + 500, yChain + 2800,
                         Brushes.Blue, 1.2, null, "CHAIN");
                DrawLine_Rec(canvas, T, item,
                         mid - 500, yChain + 1300, mid - 500, yChain + 2800,
                         Brushes.Blue, 1.2, null, "MARK");
                DrawLine_Rec(canvas, T, item,
                         mid + 500, yChain + 1300, mid + 500, yChain + 2800,
                         Brushes.Blue, 1.2, null, "MARK");

                //// 3/4L (phải)
                //DrawLine_Rec(canvas, T, item, qR, yChain - tick, qR, yChain + tick,
                //             Brushes.Blue, 1.2, null, "MARK");
                //DrawText_Rec(canvas, T, item, "3/4L", qR, yChain + 1.5 * tick,
                //             12, Brushes.Blue, HAnchor.Center, VAnchor.Bottom, 200, "TEXT");
                //// 3/8L (phải)
                //DrawLine_Rec(canvas, T, item, qR1, yChain - tick, qR1, yChain + tick,
                //             Brushes.Red, 1.2, null, "MARK");
                //DrawText_Rec(canvas, T, item, "3/8L", qR1, yChain + 1.5 * tick,
                //             12, Brushes.Red, HAnchor.Center, VAnchor.Bottom, 200, "TEXT");
                DrawLine_Rec(canvas, T, item, qR, yChain + 600, qR, yChain + 4900,
                         Brushes.Green, 1.2, null, "MARK");
                DrawText_Rec(canvas, T, item, "3",
                         qR1, yChain + 1500,
                         12, Brushes.Red, HAnchor.Center, VAnchor.Bottom, 200, "TEXT");
                DrawText_Rec(canvas, T, item, "2",
                         qR1, yChain + 1700,
                         12, Brushes.Red, HAnchor.Center, VAnchor.Bottom, 200, "TEXT");
                DrawText_Rec(canvas, T, item, "0",
                         qR1, yChain + 1900,
                         12, Brushes.Red, HAnchor.Center, VAnchor.Bottom, 200, "TEXT");
                DrawText_Rec(canvas, T, item, "0",
                         qR1, yChain + 2350,
                         12, Brushes.Red, HAnchor.Center, VAnchor.Bottom, 200, "TEXT");
                DrawText_Rec(canvas, T, item, "2",
                         qR1, yChain + 2550,
                         12, Brushes.Red, HAnchor.Center, VAnchor.Bottom, 200, "TEXT");
                DrawText_Rec(canvas, T, item, "3",
                         qR1, yChain + 2750,
                         12, Brushes.Red, HAnchor.Center, VAnchor.Bottom, 200, "TEXT");
                DrawLine_Rec(canvas, T, item, qR1 - 500, yChain + 1300, qR1 + 500, yChain + 1300,
                         Brushes.Blue, 1.2, null, "CHAIN");
                DrawLine_Rec(canvas, T, item,
                         qR1 - 500, yChain + 2800, qR1 + 500, yChain + 2800,
                         Brushes.Blue, 1.2, null, "CHAIN");
                DrawLine_Rec(canvas, T, item,
                         qR1 - 500, yChain + 1300, qR1 - 500, yChain + 2800,
                         Brushes.Blue, 1.2, null, "MARK");
                DrawLine_Rec(canvas, T, item,
                         qR1 + 500, yChain + 1300, qR1 + 500, yChain + 2800,
                         Brushes.Blue, 1.2, null, "MARK");
            }

            // 3c) [NEW] Chuỗi nối bổ sung do user chèn (tương tác chuột phải)
            foreach (var y in ExtraChainsFor(item))
            {
                if (y >= 0 && y <= yH)
                {
                    AddLineInteractive(canvas, T, pos.First(), y, pos.Last(), y,
                                       stroke: Brushes.Black, thickness: 1.2,
                                       role: LineRole.Chain, owner: item);
                }
            }
        }
        // ===== DXF TEXT model =====
        struct DxfText
        {
            public string Value;       // nội dung
            public double X, Y;        // world(mm)
            public double Height;      // chiều cao chữ (mm)
            public int HAlign;         // 0=Left,1=Center,2=Right,3=Aligned,4=Middle,5=Fit
            public int VAlign;         // 0=Baseline,1=Bottom,2=Middle,3=Top
            public double RotationDeg; // góc
            public string Layer;       // layer

            public DxfText(string value, double x, double y, double heightMm,
                           int hAlign = 1, int vAlign = 1, double rotDeg = 0, string layer = "TEXT")
            { Value = value; X = x; Y = y; Height = heightMm; HAlign = hAlign; VAlign = vAlign; RotationDeg = rotDeg; Layer = layer; }
        }


        // ===== [SCENE] Build DXF từ Scene (fallback qua dữ liệu nếu Scene trống) =====
        private (List<DxfLine> lines, List<DxfText> texts,
              List<DxfCircle> circles, List<DxfSolid> solids, string fileKey)
     BuildDxfGeometry(GridBotsecozu item)
        {
            string key = $"{_currentSecoList.階を選択}_{_currentSecoList.通を選択}";
            if (_sceneByItem.TryGetValue(item, out var scene) && scene != null && scene.Count > 0)
            {
                var lines = new List<DxfLine>();
                var texts = new List<DxfText>();
                var circles = new List<DxfCircle>();
                var solids = new List<DxfSolid>();

                foreach (var sh in scene)
                {
                    if (sh is SceneLine ln) lines.Add(new DxfLine(ln.X1, ln.Y1, ln.X2, ln.Y2, ln.Layer));
                    else if (sh is DxfText tx) texts.Add(tx);
                    else if (sh is DxfCircle cc) circles.Add(cc);
                    else if (sh is DxfSolid sd) solids.Add(sd);
                    // (bỏ DxfHatchCircle)
                }
                return (lines, texts, circles, solids, key);
            }
            return (new List<DxfLine>(), new List<DxfText>(), new List<DxfCircle>(), new List<DxfSolid>(), key);
        }

        struct DxfLine
        {
            public double X1, Y1, X2, Y2;
            public string Layer;
            public DxfLine(double x1, double y1, double x2, double y2, string layer = "0")
            { X1 = x1; Y1 = y1; X2 = x2; Y2 = y2; Layer = string.IsNullOrWhiteSpace(layer) ? "0" : layer; }
        }
        struct DxfCircle
        {
            public double X, Y, R;
            public string Layer;
            public DxfCircle(double x, double y, double r, string layer = "0")
            { X = x; Y = y; R = r; Layer = string.IsNullOrWhiteSpace(layer) ? "0" : layer; }
        }
        struct DxfHatchCircle
        {
            public double X, Y, R;
            public string Layer;
            public DxfHatchCircle(double x, double y, double r, string layer = "0")
            { X = x; Y = y; R = r; Layer = string.IsNullOrWhiteSpace(layer) ? "0" : layer; }
        }
        struct DxfSolid
        {
            public double X1, Y1, X2, Y2, X3, Y3, X4, Y4;
            public string Layer;
            public DxfSolid(double x1, double y1, double x2, double y2,
                            double x3, double y3, double x4, double y4,
                            string layer = "0")
            { X1 = x1; Y1 = y1; X2 = x2; Y2 = y2; X3 = x3; Y3 = y3; X4 = x4; Y4 = y4; Layer = string.IsNullOrWhiteSpace(layer) ? "0" : layer; }
        }

        // ===== GHI DXF (LINE + TEXT), flipY để CAD nhìn cùng hướng Canvas =====



        private void WriteSimpleDxf(
    string path,
    IEnumerable<DxfLine> lines,
    IEnumerable<DxfText> texts,
    IEnumerable<DxfCircle> circles,
    IEnumerable<DxfSolid> solids,
    bool flipY = true)
        {
            using (var sw = new StreamWriter(path, false, Encoding.ASCII))
            {
                sw.WriteLine("0"); sw.WriteLine("SECTION");
                sw.WriteLine("2"); sw.WriteLine("ENTITIES");

                // LINE
                foreach (var ln in lines)
                {
                    var y1 = flipY ? -ln.Y1 : ln.Y1;
                    var y2 = flipY ? -ln.Y2 : ln.Y2;
                    sw.WriteLine("0"); sw.WriteLine("LINE");
                    sw.WriteLine("8"); sw.WriteLine(string.IsNullOrEmpty(ln.Layer) ? "0" : ln.Layer);
                    sw.WriteLine("10"); sw.WriteLine(ln.X1.ToString(CultureInfo.InvariantCulture));
                    sw.WriteLine("20"); sw.WriteLine(y1.ToString(CultureInfo.InvariantCulture));
                    sw.WriteLine("30"); sw.WriteLine("0");
                    sw.WriteLine("11"); sw.WriteLine(ln.X2.ToString(CultureInfo.InvariantCulture));
                    sw.WriteLine("21"); sw.WriteLine(y2.ToString(CultureInfo.InvariantCulture));
                    sw.WriteLine("31"); sw.WriteLine("0");
                }

                // TEXT (để đơn giản: căn trái–baseline: 72=0, 73=0)
                foreach (var t in texts)
                {
                    var y = flipY ? -t.Y : t.Y;
                    sw.WriteLine("0"); sw.WriteLine("TEXT");
                    sw.WriteLine("8"); sw.WriteLine(string.IsNullOrEmpty(t.Layer) ? "TEXT" : t.Layer);
                    sw.WriteLine("10"); sw.WriteLine(t.X.ToString(CultureInfo.InvariantCulture));
                    sw.WriteLine("20"); sw.WriteLine(y.ToString(CultureInfo.InvariantCulture));
                    sw.WriteLine("30"); sw.WriteLine("0");
                    sw.WriteLine("40"); sw.WriteLine(t.Height.ToString(CultureInfo.InvariantCulture));
                    sw.WriteLine("1"); sw.WriteLine(t.Value ?? "");
                    sw.WriteLine("50"); sw.WriteLine(t.RotationDeg.ToString(CultureInfo.InvariantCulture));
                    sw.WriteLine("72"); sw.WriteLine("0");
                    sw.WriteLine("73"); sw.WriteLine("0");
                }

                // CIRCLE (viền chấm)
                foreach (var c in circles)
                {
                    var y = flipY ? -c.Y : c.Y;
                    sw.WriteLine("0"); sw.WriteLine("CIRCLE");
                    sw.WriteLine("8"); sw.WriteLine(string.IsNullOrEmpty(c.Layer) ? "0" : c.Layer);
                    sw.WriteLine("10"); sw.WriteLine(c.X.ToString(CultureInfo.InvariantCulture));
                    sw.WriteLine("20"); sw.WriteLine(y.ToString(CultureInfo.InvariantCulture));
                    sw.WriteLine("30"); sw.WriteLine("0");
                    sw.WriteLine("40"); sw.WriteLine(c.R.ToString(CultureInfo.InvariantCulture));
                }

                // SOLID (ruột chấm)
                foreach (var s in solids)
                {
                    double y1 = flipY ? -s.Y1 : s.Y1;
                    double y2 = flipY ? -s.Y2 : s.Y2;
                    double y3 = flipY ? -s.Y3 : s.Y3;
                    double y4 = flipY ? -s.Y4 : s.Y4;
                    sw.WriteLine("0"); sw.WriteLine("SOLID");
                    sw.WriteLine("8"); sw.WriteLine(string.IsNullOrEmpty(s.Layer) ? "0" : s.Layer);
                    sw.WriteLine("10"); sw.WriteLine(s.X1.ToString(CultureInfo.InvariantCulture));
                    sw.WriteLine("20"); sw.WriteLine(y1.ToString(CultureInfo.InvariantCulture));
                    sw.WriteLine("30"); sw.WriteLine("0");
                    sw.WriteLine("11"); sw.WriteLine(s.X2.ToString(CultureInfo.InvariantCulture));
                    sw.WriteLine("21"); sw.WriteLine(y2.ToString(CultureInfo.InvariantCulture));
                    sw.WriteLine("31"); sw.WriteLine("0");
                    sw.WriteLine("12"); sw.WriteLine(s.X3.ToString(CultureInfo.InvariantCulture));
                    sw.WriteLine("22"); sw.WriteLine(y3.ToString(CultureInfo.InvariantCulture));
                    sw.WriteLine("32"); sw.WriteLine("0");
                    sw.WriteLine("13"); sw.WriteLine(s.X4.ToString(CultureInfo.InvariantCulture));
                    sw.WriteLine("23"); sw.WriteLine(y4.ToString(CultureInfo.InvariantCulture));
                    sw.WriteLine("33"); sw.WriteLine("0");
                }

                sw.WriteLine("0"); sw.WriteLine("ENDSEC");
                sw.WriteLine("0"); sw.WriteLine("EOF");
            }
        }



        // ===== Export handlers =====
        private void ExportDxf_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSecoList?.gridbotsecozu == null || _currentSecoList.gridbotsecozu.Count == 0)
            { MessageBox.Show("Không có gì để xuất."); return; }

            var dlg = new SaveFileDialog
            {
                Filter = "AutoCAD DXF (*.dxf)|*.dxf",
                FileName = $"{_currentSecoList.階を選択}_{_currentSecoList.通を選択}.dxf"
            };
            if (dlg.ShowDialog() != true) return;

            var allLines = new List<DxfLine>();
            var allTexts = new List<DxfText>();
            var allCircs = new List<DxfCircle>();
            var allHats = new List<DxfHatchCircle>();

            var allSolids = new List<DxfSolid>();
            foreach (var it in _currentSecoList.gridbotsecozu)
            {
                var (ls, ts, cs, ss, _) = BuildDxfGeometry(it);
                allLines.AddRange(ls);
                allTexts.AddRange(ts);
                allCircs.AddRange(cs);
                allSolids.AddRange(ss);
            }
            WriteSimpleDxf(dlg.FileName, allLines, allTexts, allCircs, allSolids, flipY: true);
            MessageBox.Show("gsfdhsdfhsdfh");
        }

        private void ExportItemDxf_Click(object sender, RoutedEventArgs e)
        {
            var fe = sender as FrameworkElement;
            var item = fe != null ? fe.DataContext as GridBotsecozu : null;
            if (item == null) return;

            var (lines, texts, circles, solids, key) = BuildDxfGeometry(item);
            var dlg = new SaveFileDialog
            {
                Filter = "AutoCAD DXF (*.dxf)|*.dxf",
                FileName = $"{key}.dxf"
            };
            if (dlg.ShowDialog() == true)
            {
                WriteSimpleDxf(dlg.FileName, lines, texts, circles, solids, flipY: true);
                MessageBox.Show("gsfdhsdfhsdfh");
            }
        }

        // Replace all usages of "is not" pattern with equivalent C# 7.3 compatible code

        // Example replacement in Canvas_MouseWheel:
        private void Canvas_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            var canvas = sender as Canvas;
            var item = canvas != null ? canvas.DataContext as GridBotsecozu : null;
            if (canvas == null || item == null) return;
            if (!TryMakeTransform(canvas, item, out var T, out var fitScale, out _, out _)) return;

            var vs = VS(item);
            var sp = e.GetPosition(canvas);
            var w = ScreenToWorld(T, sp);

            double step = e.Delta > 0 ? 1.1 : 0.9;                // ±10%
            if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
                step = Math.Pow(step, 2);                         // nhanh hơn khi giữ Ctrl

            double newZoom = Clamp(vs.Zoom * step, 0.05, 20.0);
            double baseOx = canvas.ActualWidth / 2.0;
            double baseOy = (canvas.ActualHeight - 800) / 2.0;
            double Sprime = fitScale * newZoom;

            vs.PanXmm = (sp.X - baseOx) / Sprime - w.X;
            vs.PanYmm = (sp.Y - baseOy) / Sprime - w.Y;
            vs.Zoom = newZoom;

            Redraw(canvas, item);
            e.Handled = true;
        }

        // Example replacement in Canvas_MouseDown:
        private void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            var canvas = sender as Canvas;
            var item = canvas != null ? canvas.DataContext as GridBotsecozu : null;
            if (canvas == null || item == null) return;
            bool startPan = e.ChangedButton == MouseButton.Middle ||
                            (e.LeftButton == MouseButtonState.Pressed && Keyboard.IsKeyDown(Key.Space));
            if (!startPan) return;

            var vs = VS(item);
            _isPanning = true;
            _panStartPx = e.GetPosition(canvas);
            _panStartPanMm = new Point(vs.PanXmm, vs.PanYmm);
            canvas.CaptureMouse();
            e.Handled = true;

            if (e.ChangedButton == MouseButton.Middle && e.ClickCount == 2)
            {
                vs.Zoom = 1.0; vs.PanXmm = 0; vs.PanYmm = 0;
                Redraw(canvas, item);
                _isPanning = false;
                canvas.ReleaseMouseCapture();
            }
        }

        // Example replacement in Canvas_MouseMove:
        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isPanning) return;
            var canvas = sender as Canvas;
            var item = canvas != null ? canvas.DataContext as GridBotsecozu : null;
            if (canvas == null || item == null) return;
            if (!TryMakeTransform(canvas, item, out var T, out _, out _, out _)) return;

            var vs = VS(item);
            var sp = e.GetPosition(canvas);
            double S = T.Scale;

            vs.PanXmm = _panStartPanMm.X + (sp.X - _panStartPx.X) / S;
            vs.PanYmm = _panStartPanMm.Y + (sp.Y - _panStartPx.Y) / S;

            Redraw(canvas, item);
        }

        private void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isPanning) return;
            _isPanning = false;
            if (sender is Canvas c) c.ReleaseMouseCapture();
        }

        // Example replacement in Canvas_KeyDown:
        private void Canvas_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.F) return;
            var canvas = sender as Canvas;
            var item = canvas != null ? canvas.DataContext as GridBotsecozu : null;
            if (canvas == null || item == null) return;
            var vs = VS(item);
            vs.Zoom = 1.0; vs.PanXmm = 0; vs.PanYmm = 0;
            Redraw(canvas, item);
            e.Handled = true;
        }
        // Add this helper method to the 梁配筋施工図 class (or as a static method in a suitable location)
        private static double Clamp(double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
        // ===== [SCENE] Một nguồn dữ liệu cho Preview + DXF =====

        // Line trong thế giới mm + Layer
        struct SceneLine
        {
            public double X1, Y1, X2, Y2;
            public string Layer;
            public SceneLine(double x1, double y1, double x2, double y2, string layer = "LINE")
            { X1 = x1; Y1 = y1; X2 = x2; Y2 = y2; Layer = layer ?? "LINE"; }
        }

        // Tái dùng DxfText làm shape text (đã có ở dưới) — không cần thêm type mới.

        // Kho scene theo mỗi GridBotsecozu
        private readonly Dictionary<GridBotsecozu, List<object>> _sceneByItem = new Dictionary<GridBotsecozu, List<object>>();
        private List<object> SceneFor(GridBotsecozu item)
        {
            if (!_sceneByItem.TryGetValue(item, out var list))
            {
                list = new List<object>();
                _sceneByItem[item] = list;
            }
            return list;
        }

        // Mỗi lần vẽ lại thì làm mới scene cho item đó
        private void SceneBegin(GridBotsecozu item) => _sceneByItem[item] = new List<object>();

        // Anchor → DXF align helper
        private static (int h, int v) ToDxfAlign(HAnchor ha, VAnchor va)
        {
            int h = ha == HAnchor.Left ? 0 : ha == HAnchor.Right ? 2 : 1;
            int v = va == VAnchor.Top ? 3 : va == VAnchor.Middle ? 2 : 1;
            return (h, v);
        }


    }
}