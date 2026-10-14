#nullable disable

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Button = System.Windows.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using FontStyle = System.Windows.FontStyle;
using FontStyles = System.Windows.FontStyles;
using FontStretch = System.Windows.FontStretch;
using FontStretches = System.Windows.FontStretches;
using FontWeight = System.Windows.FontWeight;
using FontWeights = System.Windows.FontWeights;

namespace BroadcastSystemNet.Desktop
{
    /// <summary>页面基类：统一的标题、卡片容器与常用控件工厂。</summary>
    public abstract class PageBase
    {
        protected readonly MainWindow Host;
        protected readonly StackPanel Root = new StackPanel();
        protected readonly TextBlock StatusLine = new TextBlock
        {
            FontSize = 12,
            Foreground = Theme.TextGray,
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };

        private readonly StackPanel _head = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };

        /// <summary>
        /// fillHeight 页面专用的"撑满视口"容器。子类应把内容加到这里，而不是 Root。
        /// 原因：Root 是 StackPanel，会把子元素按无限高度测量，里面的星号行会退化成 Auto，
        /// 结果是列表按内容自然高度撑开、把底部按钮顶出可视区（被 ScrollViewer 裁掉）。
        /// 这个容器是 Auto/星号/Auto 三行的 Grid，中间那行真正吃满剩余高度。
        /// </summary>
        protected System.Windows.Controls.Panel FillHost { get; private set; }

        protected PageBase(MainWindow host, string title, string subtitle = null, string subtitleTip = null)
        {
            Host = host;
            Root.Margin = new Thickness(2);
            var t = Theme.Title(title, 17);
            t.Margin = new Thickness(0, 0, 0, 2);
            _head.Children.Add(t);
            if (!string.IsNullOrEmpty(subtitle))
            {
                var sl = Theme.Label(subtitle, 12, Theme.TextGray);
                if (!string.IsNullOrEmpty(subtitleTip)) sl.ToolTip = subtitleTip;
                _head.Children.Add(sl);
            }
        }

        /// <summary>构建页面内容（子类实现）。</summary>
        protected abstract void Build();

        /// <summary>
        /// 返回可放入内容区的控件。
        /// fillHeight=false（默认）：整页可滚动，内容加到 Root；
        /// fillHeight=true：页面撑满内容区、内部自己滚动（"顶部工具栏/底部按钮固定"的页面），内容加到 FillHost。
        /// </summary>
        public UIElement RenderPage(bool fillHeight = false)
        {
            if (_built) return _view;
            _built = true;

            UIElement pageRoot;
            if (fillHeight)
            {
                var outer = new Grid { Margin = new Thickness(2) };
                outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // 标题
                outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 内容
                outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // 状态行
                Grid.SetRow(_head, 0);
                outer.Children.Add(_head);
                var content = new Grid();
                Grid.SetRow(content, 1);
                outer.Children.Add(content);
                StatusLine.Margin = new Thickness(0, 8, 0, 0);
                Grid.SetRow(StatusLine, 2);
                outer.Children.Add(StatusLine);
                FillHost = content;
                pageRoot = outer;
            }
            else
            {
                Root.Children.Add(_head);
                pageRoot = Root;
            }

            Build();

            if (!fillHeight) Root.Children.Add(StatusLine);

            _view = new ScrollViewer
            {
                Content = pageRoot,
                VerticalScrollBarVisibility = fillHeight ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            return _view;
        }

        private bool _built;
        private UIElement _view;

        protected void SetStatus(string text, Brush color = null)
        {
            StatusLine.Text = text;
            StatusLine.Foreground = color ?? Theme.TextGray;
        }

        protected static Border Card(UIElement content, double bottom = 12) => Theme.Card2(content, bottom);

        protected static TextBlock KeyValue(string key, string value, double keyWidth = 120)
        {
            var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(keyWidth) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var k = Theme.Label(key, 12.5, Theme.TextGray);
            var v = Theme.Label(value, 12.5);
            v.TextWrapping = TextWrapping.Wrap;
            Grid.SetColumn(k, 0); Grid.SetColumn(v, 1);
            grid.Children.Add(k); grid.Children.Add(v);
            return new TextBlock { Text = key + "：" + value, FontSize = 12.5, Foreground = Theme.TextDark, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) };
        }
    }
}
