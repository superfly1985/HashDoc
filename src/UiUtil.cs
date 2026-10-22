using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace HashDoc
{
    /// <summary>界面公共小件：字体、控件工厂、打开文件/文件夹、大小格式化。</summary>
    public static class UiUtil
    {
        public static readonly Font Font9 = new Font("Microsoft YaHei UI", 9F);
        public static readonly Font Font9B = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        public static readonly Color LostRed = Color.Firebrick;
        public static readonly Color DupBrown = Color.DarkGoldenrod;
        public static readonly Color UnregBlue = Color.RoyalBlue;

        public static Label Lbl(string text, bool bold = false)
        {
            return new Label { Text = text, AutoSize = true, Font = bold ? Font9B : Font9 };
        }

        public static Button Btn(string text, int width = 90)
        {
            // 文字优先：内容比 width 长时自动加宽，避免中文按钮文字被截断
            return new Button
            {
                Text = text,
                Font = Font9,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(width, 26),
            };
        }

        public static TextBox Txt(int width = 160)
        {
            return new TextBox { Width = width, Font = Font9 };
        }

        public static ComboBox Combo(int width = 160, bool listOnly = true)
        {
            return new ComboBox
            {
                Width = width,
                Font = Font9,
                DropDownStyle = listOnly ? ComboBoxStyle.DropDownList : ComboBoxStyle.DropDown,
            };
        }

        /// <summary>
        /// 单行控件条：不换行、随内容收缩、控件垂直居中。
        /// 对话框里的工具条/按钮行统一用它，避免再用绝对坐标摆位。
        /// </summary>
        public static FlowLayoutPanel Row(params Control[] items)
        {
            var f = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                Anchor = AnchorStyles.Left,
            };
            foreach (var c in items)
            {
                c.Margin = c is Label ? new Padding(0, 6, 8, 4) : new Padding(0, 3, 8, 3);
                c.Anchor = AnchorStyles.Left;
                f.Controls.Add(c);
            }
            return f;
        }

        /// <summary>靠右的按钮行（内部顺序仍是先加的在左）。</summary>
        public static FlowLayoutPanel RowRight(params Control[] items)
        {
            var f = Row(items);
            f.Anchor = AnchorStyles.Right;
            return f;
        }

        /// <summary>占满一格的单行说明文字（左右都撑开，超长自动省略号）。</summary>
        public static Label LblFill(string text, Color? color = null)
        {
            return new Label
            {
                Text = text,
                AutoSize = false,
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
                Height = 20,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = Font9,
                ForeColor = color ?? SystemColors.ControlText,
                Margin = new Padding(0, 2, 0, 2),
            };
        }

        /// <summary>
        /// 给下拉框挂上随动检索：打字即按候选过滤，选中即定位。
        /// 只提示不自动补全（Suggest），避免把用户想新建的值替换成历史值。
        /// </summary>
        public static void AutoComplete(ComboBox cb, IEnumerable<string> candidates)
        {
            // DropDownList 只能配 ListItems：配 CustomSource 会直接抛 NotSupportedException
            // （“当 DropDownStyle 是 ComboBoxStyle.DropDownList 并且 AutoCompleteSource 不是 ListItems 时…”）。
            // 顺序也不能反：默认 Source 是 None，先把 Mode 设成 Suggest 一样会抛，必须先设 Source。
            if (cb.DropDownStyle == ComboBoxStyle.DropDownList)
            {
                cb.AutoCompleteSource = AutoCompleteSource.ListItems;
                cb.AutoCompleteMode = AutoCompleteMode.Suggest;
                return;
            }
            cb.AutoCompleteSource = AutoCompleteSource.CustomSource;
            cb.AutoCompleteCustomSource = MakeSource(candidates);
            cb.AutoCompleteMode = AutoCompleteMode.Suggest;
        }

        /// <summary>
        /// 给表格的文本列挂随动检索。表格单元格没有 AutoComplete 属性，
        /// 只能挂到正在编辑的 TextBox 上；非目标列进来时清空候选，免得串味。
        /// </summary>
        public static void GridAutoComplete(DataGridView grid, string columnName, IEnumerable<string> candidates)
        {
            var src = MakeSource(candidates);
            var empty = new AutoCompleteStringCollection();
            grid.EditingControlShowing += delegate (object s, DataGridViewEditingControlShowingEventArgs e)
            {
                var tb = e.Control as TextBox;
                if (tb == null) return;
                bool mine = grid.CurrentCell != null && grid.CurrentCell.OwningColumn != null &&
                            grid.CurrentCell.OwningColumn.Name == columnName;
                tb.AutoCompleteMode = AutoCompleteMode.Suggest;
                tb.AutoCompleteSource = AutoCompleteSource.CustomSource;
                tb.AutoCompleteCustomSource = mine ? src : empty;
            };
        }

        static AutoCompleteStringCollection MakeSource(IEnumerable<string> candidates)
        {
            var src = new AutoCompleteStringCollection();
            if (candidates == null) return src;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in candidates)
            {
                if (string.IsNullOrWhiteSpace(v)) continue;
                if (!seen.Add(v.Trim())) continue;
                src.Add(v.Trim());
            }
            return src;
        }

        /// <summary>可编辑下拉框（能选历史值，也能直接敲新的）。</summary>
        public static ComboBox ComboEdit(int width = 160)
        {
            return new ComboBox
            {
                Width = width,
                Font = Font9,
                DropDownStyle = ComboBoxStyle.DropDown,
            };
        }

        /// <summary>程序版本（取自程序集，发布时改 csproj 的 Version 即可）。</summary>
        public static string AppVersion()
        {
            try
            {
                var asm = typeof(UiUtil).Assembly;
                var attr = (System.Reflection.AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(
                    asm, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
                if (attr != null && !string.IsNullOrEmpty(attr.InformationalVersion))
                {
                    // 有 git 仓库时 SDK 会把提交哈希拼在版本号后（如 1.0.5+f6c717b），界面只显示纯版本号
                    var v = attr.InformationalVersion;
                    int i = v.IndexOf('+');
                    return i > 0 ? v.Substring(0, i) : v;
                }
                return asm.GetName().Version.ToString(3);
            }
            catch { return "?"; }
        }

        /// <summary>提交哈希短码（前 8 位）；没有 git 信息时返回空串。--dbinfo 排障用。</summary>
        public static string CommitInfo()
        {
            try
            {
                var asm = typeof(UiUtil).Assembly;
                var attr = (System.Reflection.AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(
                    asm, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
                var v = attr?.InformationalVersion ?? "";
                int i = v.IndexOf('+');
                if (i <= 0) return "";
                var sha = v.Substring(i + 1);
                return sha.Length == 0 ? "" : "（commit " + sha.Substring(0, Math.Min(8, sha.Length)) + "）";
            }
            catch { return ""; }
        }

        public static void ShowError(IWin32Window owner, string title, Exception ex)
        {
            MessageBox.Show(owner, ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        public static string FormatSize(long bytes)
        {
            double v = bytes;
            if (v < 1024) return bytes + " B";
            if (v < 1024 * 1024) return (v / 1024).ToString("F1") + " KB";
            if (v < 1024.0 * 1024 * 1024) return (v / 1024 / 1024).ToString("F1") + " MB";
            return (v / 1024 / 1024 / 1024).ToString("F2") + " GB";
        }

        public static void OpenFile(IWin32Window owner, string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    MessageBox.Show(owner, "文件不存在：" + path +
                        "\n\n可执行一次目录扫描，让档案状态与磁盘保持一致。", "提示",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex) { ShowError(owner, "打开失败", ex); }
        }

        public static void OpenFolder(IWin32Window owner, string path)
        {
            try
            {
                string dir = File.Exists(path) ? Path.GetDirectoryName(path) : path;
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                {
                    MessageBox.Show(owner, "文件夹不存在：" + dir, "提示",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                if (File.Exists(path))
                    System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\"");
                else
                    System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch (Exception ex) { ShowError(owner, "打开失败", ex); }
        }

        public static void CopyClipboard(string s)
        {
            try { Clipboard.SetText(s ?? ""); } catch { }
        }

        /// <summary>全部关联编号按类型分行展示（预览面板使用）。</summary>
        public static string CodesText(FileRecord r)
        {
            if (r == null || r.Codes.Count == 0) return "";
            var sb = new StringBuilder();
            foreach (var ct in CodeTypes.All)
            {
                var vals = r.Codes.Where(c => c.Type == ct[0]).Select(c => c.Value).ToList();
                if (vals.Count > 0)
                {
                    if (sb.Length > 0) sb.Append("\n");
                    sb.Append(ct[1] + "：" + string.Join("、", vals));
                }
            }
            return sb.ToString();
        }

        /// <summary>取某一类编号的全部值（多个用顿号连接），列表按类型分列时使用。</summary>
        public static string CodeValue(FileRecord r, string type)
        {
            if (r == null || r.Codes.Count == 0) return "";
            var sb = new StringBuilder();
            foreach (var c in r.Codes.Where(x => x.Type == type))
            {
                if (sb.Length > 0) sb.Append("、");
                sb.Append(c.Value);
            }
            return sb.ToString();
        }
    }

    /// <summary>双缓冲、只读的公共表格样式。</summary>
    public class SmoothGrid : DataGridView
    {
        public SmoothGrid()
        {
            DoubleBuffered = true;
            AllowUserToAddRows = false;
            AllowUserToDeleteRows = false;
            AllowUserToResizeRows = false;
            ReadOnly = true;
            RowHeadersVisible = false;
            SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            MultiSelect = false;
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            BackgroundColor = Color.White;
            BorderStyle = BorderStyle.FixedSingle;
        }
    }
}
