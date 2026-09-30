using FolderRewind.History.Graph;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System;
using Windows.Foundation;
using Windows.UI;

namespace FolderRewind.Views.Graph
{
    /// <summary>
    /// 备份记录左侧的泳道图，一行一条检查点。
    /// <para>
    /// 每行只画自己那一段（<c>y = 0</c> 到 <c>y = RowHeight</c>）：跨行的线在行边界处接起来，
    /// 所以列位置必须严格等于 <c>lane * LaneWidth + LaneWidth / 2</c>，行高也必须处处相同 ——
    /// 差一点就会在行与行之间看见折角或断缝。
    /// </para>
    /// <para>
    /// 泳道号与配色的对应关系（取模循环）由 <see cref="CheckpointGraphLayoutBuilder"/> 定，
    /// 这里只负责把算好的三组线画出来，不重新推导布局。
    /// </para>
    /// </summary>
    public sealed partial class CheckpointRail : UserControl
    {
        private const double DotDiameter = 10;
        private const double LineWidth = 2;

        // 前六色就是 ThemeService.GetAccentColor 那一组（用户能选的主题色），后两色补齐到 8 ——
        // 原本想用「品红 + 灰紫」，但它们跟已有的粉、紫在人眼尺度上分不出来，换成了红与金。
        // 这里刻意不复用 ThemeService：那是用户设置的系统强调色，挂上去会随设置撞色；
        // 也不跟 UseHistoryStatusColors 耦合 —— 那个管历史状态色，语义不同。
        // 都是中明度色，深浅主题下都读得清，因此不做主题分支。
        private static readonly Color[] Palette =
        [
            Color.FromArgb(255, 0, 120, 212),
            Color.FromArgb(255, 0, 153, 188),
            Color.FromArgb(255, 16, 124, 16),
            Color.FromArgb(255, 227, 0, 140),
            Color.FromArgb(255, 202, 80, 16),
            Color.FromArgb(255, 116, 77, 169),
            Color.FromArgb(255, 216, 59, 58),
            Color.FromArgb(255, 146, 108, 0)
        ];

        private static readonly SolidColorBrush[] Brushes = CreateBrushes();

        /// <summary>
        /// 调色板长度必须与布局用的色数一致，否则同一个泳道号在圆点和线条上会取到两个颜色。
        /// 这种不一致编译器不会说话，只能自己喊出来。
        /// </summary>
        static CheckpointRail()
        {
            if (Palette.Length != CheckpointGraphLayout.PaletteSize)
            {
                throw new InvalidOperationException(
                    "泳道调色板的颜色数与 CheckpointGraphLayout.PaletteSize 对不上：改一处必须同时改另一处。");
            }
        }

        public static readonly DependencyProperty RowProperty = DependencyProperty.Register(
            nameof(Row),
            typeof(CheckpointGraphRow),
            typeof(CheckpointRail),
            new PropertyMetadata(null, static (sender, _) => ((CheckpointRail)sender).Redraw()));

        public CheckpointRail() => InitializeComponent();

        /// <summary>本行要画的布局。为空时画布清空（虚拟化刚复用到空实例的那一瞬）。</summary>
        public CheckpointGraphRow? Row
        {
            get => (CheckpointGraphRow?)GetValue(RowProperty);
            set => SetValue(RowProperty, value);
        }

        private void Redraw()
        {
            // 列表虚拟化会复用控件实例：不清空就会留下上一行画过的线。
            Surface.Children.Clear();
            if (Row is not { } row)
            {
                return;
            }

            var centerY = CheckpointGraphLayout.RowHeight / 2;
            var laneX = LaneX(row.Lane);

            foreach (var lane in row.PassThroughLanes)
            {
                Surface.Children.Add(Vertical(LaneX(lane), CheckpointGraphLayout.RowHeight, BrushFor(lane)));
            }

            // 同一段曲线在 fromX == toX 时正好退化成竖线，所以直入线／直出线不用单独一条分支。
            foreach (var lane in row.IncomingLanes)
            {
                Surface.Children.Add(Curve(LaneX(lane), 0, laneX, centerY, BrushFor(lane)));
            }

            foreach (var lane in row.OutgoingLanes)
            {
                Surface.Children.Add(Curve(laneX, centerY, LaneX(lane), CheckpointGraphLayout.RowHeight, BrushFor(lane)));
            }

            var dot = new Ellipse
            {
                Width = DotDiameter,
                Height = DotDiameter,
                Fill = BrushFor(row.ColorIndex)
            };
            Canvas.SetLeft(dot, laneX - (DotDiameter / 2));
            Canvas.SetTop(dot, centerY - (DotDiameter / 2));
            Surface.Children.Add(dot);
        }

        private static double LaneX(int lane) => (lane * CheckpointGraphLayout.LaneWidth) + (CheckpointGraphLayout.LaneWidth / 2);

        private static Rectangle Vertical(double x, double height, SolidColorBrush brush)
        {
            var line = new Rectangle { Width = LineWidth, Height = height, Fill = brush };
            Canvas.SetLeft(line, x - (LineWidth / 2));
            Canvas.SetTop(line, 0);
            return line;
        }

        /// <summary>
        /// 一段跨半行的连线。两个控制点各在本端正上／正下方，于是两端切向都是竖直的，
        /// 相邻两行画出来的线在行边界上不会有折角。
        /// </summary>
        private static Path Curve(double fromX, double fromY, double toX, double toY, SolidColorBrush brush)
        {
            var middle = (fromY + toY) / 2;
            var figure = new PathFigure
            {
                StartPoint = new Point(fromX, fromY),
                IsClosed = false,
                IsFilled = false
            };
            figure.Segments.Add(new BezierSegment
            {
                Point1 = new Point(fromX, middle),
                Point2 = new Point(toX, middle),
                Point3 = new Point(toX, toY)
            });

            return new Path
            {
                Data = new PathGeometry { Figures = new PathFigureCollection { figure } },
                Stroke = brush,
                StrokeThickness = LineWidth
            };
        }

        private static SolidColorBrush BrushFor(int laneIndex) => Brushes[laneIndex % Brushes.Length];

        private static SolidColorBrush[] CreateBrushes()
        {
            var brushes = new SolidColorBrush[Palette.Length];
            for (var i = 0; i < brushes.Length; i++)
            {
                brushes[i] = new SolidColorBrush(Palette[i]);
            }

            return brushes;
        }
    }
}
