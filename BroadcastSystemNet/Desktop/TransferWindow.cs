#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using FontFamily = System.Windows.Media.FontFamily;
using FontWeights = System.Windows.FontWeights;

namespace BroadcastSystemNet.Desktop
{
    /// <summary>
    /// 「导入 / 导出」窗口：一个窗口、两种模式，7 项中文复选。
    ///
    /// 与后端的关系：本窗口**不含任何校验与写盘逻辑**，全部交给
    /// <c>POST /api/transfer/{preview,import,export}</c>（即 <see cref="ConfigTransfer"/>）。
    /// 这样做的好处是导入窗口、拖入导入、整包还原、以后的网页/手机端共用同一份校验规则
    /// （规格文档要求"四个入口共用同一批导入器，务必只保留一份校验规则"）。
    ///
    /// 规格对应：
    ///   · 复选项顺序与文案见 <see cref="ConfigTransfer.Order"/> / <see cref="ConfigTransfer.Label"/>；
    ///   · 「完全覆盖」只在**来源主机名与本机一致**时出现（先预览拿到 same_host 再显示）；
    ///   · 导出默认**不勾**隐私数据，勾选后弹 AppDialog 醒目二次确认（不用系统 MessageBox）。
    /// </summary>
    public class TransferWindow : Window
    {
        // ---------- 模式 ----------
        private readonly SegmentedToggle _mode = new SegmentedToggle(new[] { "导入", "导出" }, 200,
            "导入：把别的电脑（或本机旧备份）的配置按项合并进来；导出：把本机配置按项打包出去", 0);

        // ---------- 复选 ----------
        private readonly Dictionary<string, CheckBox> _checks = new Dictionary<string, CheckBox>();

        // ---------- 导入 ----------
        private readonly StackPanel _importPanel = new StackPanel();
        private readonly TextBox _srcPath = Theme.TextBox("");
        private readonly Button _previewBtn = Theme.Button("预览（不写盘）", Theme.Neutral, Theme.TextDark, "\uf002", null);
        private readonly Button _importBtn = Theme.Button("开始导入", Theme.Primary, Brushes.White, "\uf0ed", null);
        private readonly CheckBox _overwrite = new CheckBox
        {
            Content = "完全覆盖（先清空本机该类配置，再写入包里的内容）",
            FontSize = 12.5,
            Foreground = Theme.TextDark,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0)
        };
        private readonly TextBlock _overwriteHint = Theme.Label("", 11.5, Theme.TextGray);
        private bool _overwriteAllowed;          // 仅当预览报告"来源主机与本机一致"时为 true

        // ---------- 导出 ----------
        private readonly StackPanel _exportPanel = new StackPanel();
        private readonly TextBox _dstPath = Theme.TextBox("");
        private readonly Button _exportBtn = Theme.Button("开始导出", Theme.Primary, Brushes.White, "\uf0c7", null);
        private readonly CheckBox _privacy = new CheckBox
        {
            Content = "同时导出隐私数据（网页口令、组网令牌、数据密码、凭证密钥）",
            FontSize = 12.5,
            Foreground = Theme.Danger,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0)
        };
        private bool _privacyConfirmed;

        // ---------- 结果 ----------
        private readonly TextBlock _status = Theme.Label("", 12.5, Theme.TextGray);
        private readonly StackPanel _detail = new StackPanel();

        /// <summary>导入或导出成功后回调（MainWindow 用它重载当前页，避免列表还是旧数据）。</summary>
        public Action OnFinished;

        public TransferWindow()
        {
            Title = "导入 / 导出";
            Width = 780;
            Height = Math.Min(720, Math.Max(440, System.Windows.SystemParameters.WorkArea.Height - 120));   // 高缩放：限高按工作区动态算
            ResizeMode = ResizeMode.CanResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(0xF4, 0xF6, 0xF8));
            FontFamily = new FontFamily("Microsoft YaHei UI");

            var body = new StackPanel { Margin = new Thickness(18) };

            // ---------- 标题 ----------
            var title = Theme.Title("导入 / 导出", 16);
            body.Children.Add(title);
            var sub = Theme.Label("按项导入导出：勾哪些就导哪些。导入会先做合法性校验与旧格式迁移，非法条目跳过并报告，不会静默失败。",
                12, Theme.TextGray);
            sub.TextWrapping = TextWrapping.Wrap;
            sub.Margin = new Thickness(0, 4, 0, 12);
            body.Children.Add(sub);

            // ---------- 模式切换 ----------
            var modeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            modeRow.Children.Add(Theme.Label("模式：", 12.5, Theme.TextGray));
            _mode.Changed += () => { SyncMode(); };
            modeRow.Children.Add(_mode);
            body.Children.Add(modeRow);

            // ---------- 复选清单 ----------
            body.Children.Add(BuildItemList());

            // ---------- 导入面板 ----------
            BuildImportPanel();
            body.Children.Add(Theme.Card2(_importPanel, 12));

            // ---------- 导出面板 ----------
            BuildExportPanel();
            body.Children.Add(Theme.Card2(_exportPanel, 12));

            // ---------- 结果区 ----------
            _status.TextWrapping = TextWrapping.Wrap;
            body.Children.Add(_status);
            _detail.Margin = new Thickness(0, 8, 0, 0);
            body.Children.Add(_detail);

            // ---------- 底部 ----------
            var footer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0)
            };
            var close = Theme.Button("关闭", null, null, "\uf00d", () => Close());
            close.Margin = new Thickness(0);
            footer.Children.Add(close);
            body.Children.Add(footer);

            Content = new Border
            {
                Background = Brushes.White,
                BorderBrush = Theme.Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Margin = new Thickness(14),
                Padding = new Thickness(4, 2, 4, 2),
                Child = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content = body
                }
            };

            SyncMode();
        }

        // =====================================================================
        //  界面搭建
        // =====================================================================
        private UIElement BuildItemList()
        {
            var panel = new StackPanel();
            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            head.Children.Add(Theme.Label("导入 / 导出的项目：", 12.5, Theme.TextDark));
            var all = Theme.Button("全选", null, null, "\uf00c", () => SetAll(true));
            all.Margin = new Thickness(8, 0, 0, 0);
            head.Children.Add(all);
            var none = Theme.Button("全不选", null, null, null, () => SetAll(false));
            none.Margin = new Thickness(6, 0, 0, 0);
            head.Children.Add(none);
            panel.Children.Add(head);

            foreach (var key in ConfigTransfer.Order)
            {
                var cb = new CheckBox
                {
                    Content = ConfigTransfer.Label(key),
                    FontSize = 12.5,
                    Foreground = Theme.TextDark,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 3, 0, 3),
                    Tag = key
                };
                _checks[key] = cb;
                panel.Children.Add(cb);
            }

            // 默认全选：跨机同步排程是最常见的用法
            SetAll(true);
            return panel;
        }

        private void BuildImportPanel()
        {
            _importPanel.Children.Add(Theme.Label("来源文件", 12.5, Theme.TextDark));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            _srcPath.Width = 520;
            _srcPath.IsReadOnly = true;
            _srcPath.ToolTip = "支持本程序导出的 .zip 备份包，也支持单个 .jsonl 配置文件（拖入页面更快捷）";
            row.Children.Add(_srcPath);
            var pick = Theme.Button("选择文件…", null, null, "\uf15b", () => PickSource());
            pick.Margin = new Thickness(8, 0, 0, 0);
            row.Children.Add(pick);
            _importPanel.Children.Add(row);

            var hint = Theme.Label("支持 .zip 备份包（新格式带清单，旧格式整包备份也能读）与单个 .jsonl（此时只处理该文件对应的那一类）。",
                11.5, Theme.TextGray);
            hint.TextWrapping = TextWrapping.Wrap;
            hint.Margin = new Thickness(0, 6, 0, 0);
            _importPanel.Children.Add(hint);

            var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            _previewBtn.Margin = new Thickness(0, 0, 8, 0);
            _previewBtn.Click += async (s, e) => await DoPreviewAsync();
            btns.Children.Add(_previewBtn);
            _importBtn.Click += async (s, e) => await DoImportAsync();
            btns.Children.Add(_importBtn);
            _importPanel.Children.Add(btns);

            _overwrite.Visibility = Visibility.Collapsed;
            _overwriteHint.Visibility = Visibility.Collapsed;
            _importPanel.Children.Add(_overwrite);
            _importPanel.Children.Add(_overwriteHint);
        }

        private void BuildExportPanel()
        {
            _exportPanel.Children.Add(Theme.Label("导出到", 12.5, Theme.TextDark));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            _dstPath.Width = 520;
            _dstPath.IsReadOnly = true;
            _dstPath.ToolTip = "导出为 .zip 包（新格式：老格式内容 + 根目录清单 transfer.json）";
            row.Children.Add(_dstPath);
            var pick = Theme.Button("选择保存位置…", null, null, "\uf07c", () => PickTarget());
            pick.Margin = new Thickness(8, 0, 0, 0);
            row.Children.Add(pick);
            _exportPanel.Children.Add(row);

            var hint = Theme.Label("默认不导出隐私数据；此时 vnt 段只带服务器地址，跨机导入时令牌与密码一律以本机现有为准。",
                11.5, Theme.TextGray);
            hint.TextWrapping = TextWrapping.Wrap;
            hint.Margin = new Thickness(0, 6, 0, 0);
            _exportPanel.Children.Add(hint);

            _privacy.Checked += (s, e) => ConfirmPrivacy();
            _privacy.Unchecked += (s, e) => { _privacyConfirmed = false; };
            _exportPanel.Children.Add(_privacy);

            var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            _exportBtn.Click += async (s, e) => await DoExportAsync();
            btns.Children.Add(_exportBtn);
            _exportPanel.Children.Add(btns);
        }

        // =====================================================================
        //  交互
        // =====================================================================
        private void SyncMode()
        {
            bool isImport = _mode.SelectedIndex == 0;
            _importPanel.Visibility = isImport ? Visibility.Visible : Visibility.Collapsed;
            _exportPanel.Visibility = isImport ? Visibility.Collapsed : Visibility.Visible;
            _status.Text = "";
            _detail.Children.Clear();
        }

        private void SetAll(bool on)
        {
            foreach (var cb in _checks.Values) cb.IsChecked = on;
        }

        private List<string> CheckedItems()
        {
            var list = new List<string>();
            foreach (var key in ConfigTransfer.Order)
                if (_checks.TryGetValue(key, out var cb) && cb.IsChecked == true) list.Add(key);
            return list;
        }

        private void PickSource()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择要导入的备份包或配置文件",
                Filter = "备份包或配置 (*.zip;*.jsonl)|*.zip;*.jsonl|ZIP 备份包 (*.zip)|*.zip|配置文件 (*.jsonl)|*.jsonl|所有文件 (*.*)|*.*"
            };
            if (dlg.ShowDialog(this) == true)
            {
                _srcPath.Text = dlg.FileName;
                _overwriteAllowed = false;
                _overwrite.Visibility = Visibility.Collapsed;
                _overwriteHint.Visibility = Visibility.Collapsed;
                _status.Text = "已选择来源文件，建议先点「预览（不写盘）」确认将要导入的内容。";
                _status.Foreground = Theme.TextGray;
                _detail.Children.Clear();
            }
        }

        private void PickTarget()
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "选择导出位置",
                Filter = "ZIP 压缩包 (*.zip)|*.zip",
                FileName = $"BroadcastTransfer_{DateTime.Now:yyyyMMdd_HHmmss}.zip"
            };
            if (dlg.ShowDialog(this) == true) _dstPath.Text = dlg.FileName;
        }

        /// <summary>勾选隐私数据 → 醒目二次确认（AppDialog 风格，不用系统 MessageBox）。</summary>
        private void ConfirmPrivacy()
        {
            if (_privacyConfirmed) return;
            bool ok = AppDialog.Confirm(this, "导出选项 · 隐私数据",
                "勾选后，导出的压缩包里会以明文包含：\n\n"
                + "· 网页看板口令\n"
                + "· 组网令牌（token）\n"
                + "· 数据密码（data_password）\n"
                + "· 凭证密钥（cred_secret）\n\n"
                + "这些内容能让他人冒充本机接入组网与话筒通道。除非确实要做整机迁移，"
                + "否则建议保持不勾选——排程、调休、预警配置、音乐库、铃声**不勾也能正常导出与还原**。",
                "我确认要导出", "取消（推荐）", AppDialogKind.Warning);
            if (ok) _privacyConfirmed = true;
            else { _privacy.IsChecked = false; _privacyConfirmed = false; }
        }

        // =====================================================================
        //  执行
        // =====================================================================
        private async Task DoPreviewAsync()
        {
            if (string.IsNullOrWhiteSpace(_srcPath.Text)) { Warn("请先选择来源文件。"); return; }
            var items = CheckedItems();
            if (items.Count == 0) { Warn("请至少勾选一项。"); return; }
            await RunAsync("/api/transfer/preview", new
            {
                path = _srcPath.Text,
                items,
                overwrite = _overwrite.IsChecked == true
            }, true);
        }

        private async Task DoImportAsync()
        {
            if (string.IsNullOrWhiteSpace(_srcPath.Text)) { Warn("请先选择来源文件。"); return; }
            var items = CheckedItems();
            if (items.Count == 0) { Warn("请至少勾选一项。"); return; }

            bool ow = _overwriteAllowed && _overwrite.IsChecked == true;
            if (ow && !AppDialog.Confirm(this, "确认完全覆盖",
                    "「完全覆盖」会先清空本机被勾选项的现有配置，再写入包里的内容。\n\n"
                    + "被勾选的类别中，本机原有的记录会丢失（写盘前有滚动备份，但界面不提供一键回滚）。\n\n确定继续吗？",
                    "继续覆盖", "取消", AppDialogKind.Warning)) return;

            var r = await RunAsync("/api/transfer/import", new { path = _srcPath.Text, items, overwrite = ow }, false);
            if (r != null && r["status"]?.ToString() != "error") { try { OnFinished?.Invoke(); } catch { } }
        }

        private async Task DoExportAsync()
        {
            if (string.IsNullOrWhiteSpace(_dstPath.Text)) { Warn("请先选择导出位置。"); return; }
            var items = CheckedItems();
            if (items.Count == 0) { Warn("请至少勾选一项。"); return; }

            var r = await RunAsync("/api/transfer/export", new
            {
                path = _dstPath.Text,
                items,
                privacy = _privacyConfirmed && _privacy.IsChecked == true
            }, false);
            if (r != null && r["status"]?.ToString() != "error")
            {
                string msg = r["message"]?.ToString() ?? "导出完成。";
                if (_privacyConfirmed && _privacy.IsChecked == true)
                    AppDialog.Warn(this, "导出完成（含隐私数据）", msg + "\n\n该压缩包内含明文口令与令牌，请妥善保管，不要通过不安全的渠道传递。");
                else
                    AppDialog.Success(this, "导出完成", msg);
            }
        }

        /// <summary>调用后端接口并把结果渲染到结果区。返回原始响应（失败返回 null）。</summary>
        private async Task<JsonObject> RunAsync(string path, object payload, bool isPreview)
        {
            SetBusy(true);
            _status.Text = isPreview ? "正在解析并校验…" : "正在处理…";
            _status.Foreground = Theme.TextGray;
            _detail.Children.Clear();
            try
            {
                var node = await DesktopApi.PostJson(path, payload);
                var r = node as JsonObject;
                if (r == null)
                {
                    _status.Text = "操作失败：本机服务没有响应（请确认程序仍在运行）。";
                    _status.Foreground = Theme.Danger;
                    return null;
                }
                Render(r, isPreview);

                // 预览时若报告"来源主机与本机一致"，才显示「完全覆盖」选项（规格 §1.1）
                if (isPreview)
                {
                    bool same = r["same_host"]?.GetValue<bool>() ?? false;
                    bool hasManifest = r["has_manifest"]?.GetValue<bool>() ?? false;
                    _overwriteAllowed = same;
                    _overwrite.Visibility = same ? Visibility.Visible : Visibility.Collapsed;
                    _overwriteHint.Visibility = same ? Visibility.Visible : Visibility.Collapsed;
                    _overwrite.IsChecked = false;
                    _overwriteHint.Text = same
                        ? "来源主机与本机一致（" + (r["source_host"]?.ToString() ?? "") + "），可以逐项覆盖为本包内容。"
                        : (hasManifest
                            ? "来源主机与本机不同，已按跨机规则处理：vnt 只取服务器地址，其余以本机现有为准。"
                            : "这是旧格式备份包（没有清单，来源主机未知），已按跨机规则从严处理。");
                    _overwriteHint.Foreground = same ? Theme.Warn : Theme.TextGray;
                }
                return r;
            }
            catch (Exception ex)
            {
                _status.Text = "操作失败：" + ex.Message;
                _status.Foreground = Theme.Danger;
                return null;
            }
            finally { SetBusy(false); }
        }

        private void SetBusy(bool busy)
        {
            _previewBtn.IsEnabled = !busy;
            _importBtn.IsEnabled = !busy;
            _exportBtn.IsEnabled = !busy;
        }

        private void Render(JsonObject r, bool isPreview)
        {
            string st = r["status"]?.ToString() ?? "";
            string msg = r["message"]?.ToString() ?? "";
            _status.Text = msg;
            _status.Foreground = st == "error" ? Theme.Danger : (st == "partial" ? Theme.Warn : Theme.Success);

            if (r["items"] is not JsonArray items) return;
            foreach (var n in items)
            {
                if (n is not JsonObject it) continue;
                string label = it["label"]?.ToString() ?? it["key"]?.ToString() ?? "";
                int imported = it["imported"]?.GetValue<int>() ?? 0;
                int skipped = it["skipped"]?.GetValue<int>() ?? 0;
                int invalid = it["invalid"]?.GetValue<int>() ?? 0;
                int exported = it["exported"]?.GetValue<int>() ?? 0;
                bool ok = it["ok"]?.GetValue<bool>() ?? true;

                string line = label + "：";
                if (exported > 0 || it["exported"] != null) line += "导出 " + exported + " 条";
                else
                {
                    line += (isPreview ? "将写入 " : "已写入 ") + imported + " 条";
                    if (skipped > 0) line += "，跳过 " + skipped + " 条";
                    if (invalid > 0) line += "，非法 " + invalid + " 条";
                }

                var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
                head.Children.Add(Theme.Icon(ok ? "\uf058" : "\uf057", 12.5, ok ? Theme.Success : Theme.Warn));
                var t = Theme.Label(line, 12.5, ok ? Theme.TextDark : Theme.Warn);
                t.Margin = new Thickness(6, 0, 0, 0);
                head.Children.Add(t);
                _detail.Children.Add(head);

                if (it["messages"] is JsonArray msgs)
                {
                    foreach (var m in msgs)
                    {
                        var mt = Theme.Label("　· " + (m?.ToString() ?? ""), 11.5, Theme.TextGray);
                        mt.TextWrapping = TextWrapping.Wrap;
                        _detail.Children.Add(mt);
                    }
                }
            }

            if (r["source_host"] != null && r["has_manifest"]?.GetValue<bool>() == true)
            {
                var ht = Theme.Label("来源主机：" + (r["source_host"]?.ToString() ?? "未记录")
                    + (r["same_host"]?.GetValue<bool>() == true ? "（与本机一致）" : "（与本机不同）"), 11.5, Theme.TextGray);
                ht.Margin = new Thickness(0, 8, 0, 0);
                _detail.Children.Add(ht);
            }
        }

        private void Warn(string message)
        {
            _status.Text = message;
            _status.Foreground = Theme.Warn;
        }
    }
}
