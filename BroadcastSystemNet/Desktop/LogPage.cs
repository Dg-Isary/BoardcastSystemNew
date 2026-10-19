#nullable disable

using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Button = System.Windows.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using CheckBox = System.Windows.Controls.CheckBox;
using DataGrid = System.Windows.Controls.DataGrid;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace BroadcastSystemNet.Desktop
{
    /// <summary>执行日志：打铃/播放/抢占/引用失效/配置变更都在这里，可按级别筛选。</summary>
    public class LogPage : PageBase
    {
        private readonly DataGrid _grid = new DataGrid();
        private readonly CheckBox _onlyProblem = new CheckBox { Content = "只看异常与警告", VerticalAlignment = VerticalAlignment.Center };
        private JsonArray _logs = new JsonArray();
        private System.Windows.Threading.DispatcherTimer _timer;

        public LogPage(MainWindow host) : base(host, "执行日志", "记录每次触发、播放、抢占、引用失效与配置变更") { }

        protected override void Build()
        {
            _grid.AutoGenerateColumns = false;
            _grid.IsReadOnly = true;
            _grid.Height = 420;
            _grid.Columns.Add(new DataGridTextColumn { Header = "时间", Binding = new System.Windows.Data.Binding("time"), Width = 150 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "类别", Binding = new System.Windows.Data.Binding("category"), Width = 70 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "动作", Binding = new System.Windows.Data.Binding("action"), Width = 90 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "级别", Binding = new System.Windows.Data.Binding("level"), Width = 60 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "任务", Binding = new System.Windows.Data.Binding("task"), Width = 70 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "内容", Binding = new System.Windows.Data.Binding("detail"), Width = 420 });

            var card = new StackPanel();
            card.Children.Add(Theme.Title("最近执行记录"));
            card.Children.Add(Theme.Row(
                Theme.Button("刷新", Theme.Primary, Brushes.White, "\uf021", () => _ = ReloadAsync()),
                Theme.Button("打开日志目录", null, null, "\uf07b", () => Host.OpenPath(Program.LogDir)),
                _onlyProblem));
            _onlyProblem.Checked += (s, e) => RenderGrid();
            _onlyProblem.Unchecked += (s, e) => RenderGrid();
            card.Children.Add(_grid);
            Root.Children.Add(Card(card));

            SetStatus("日志异步落盘，不会拖慢播放；每天一个文件，保留 30 天。");
            _ = ReloadAsync();

            // 页面打开期间每 3 秒自动刷新
            _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _timer.Tick += (s, e) => _ = ReloadAsync();
            _timer.Start();
        }

        private async Task ReloadAsync()
        {
            try
            {
                _logs = await DesktopApi.GetLog(300);
                RenderGrid();
            }
            catch (Exception ex) { SetStatus("读取日志失败：" + ex.Message, Theme.Danger); }
        }

        private void RenderGrid()
        {
            bool onlyProblem = _onlyProblem.IsChecked == true;
            var rows = new List<object>();
            foreach (var n in _logs)
            {
                if (n is not JsonObject o) continue;
                string level = o["level"]?.ToString() ?? "info";
                if (onlyProblem && level != "error" && level != "warn") continue;
                rows.Add(new
                {
                    time = o["time"]?.ToString(),
                    category = o["category"]?.ToString(),
                    action = o["action"]?.ToString(),
                    level = level,
                    task = o["task"]?.ToString(),
                    detail = o["detail"]?.ToString() ?? o["name"]?.ToString()
                });
            }
            rows.Reverse();
            _grid.ItemsSource = rows;
            SetStatus($"已加载 {_logs.Count} 条（显示 {rows.Count} 条）　文件：{Program.LogDir}");
        }
    }
}
