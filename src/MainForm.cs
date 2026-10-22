using System;
using System.Drawing;
using System.Windows.Forms;

namespace HashDoc
{
    /// <summary>主窗体：档案浏览（资源浏览器式主页面）+ 设置（辅助功能集中页），两个页签。</summary>
    public class MainForm : Form
    {
        readonly BrowseTab _browse;
        readonly SettingsPage _settings;

        public MainForm()
        {
            Text = "识档 HashDoc v" + UiUtil.AppVersion() + " — 图纸档案管理";
            Font = UiUtil.Font9;
            StartPosition = FormStartPosition.CenterScreen;

            // 窗口尺寸不超过屏幕可用区域：高缩放比（125%/150%）的笔记本上也能完整显示
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int w = Math.Min(1280, wa.Width - 60);
            int h = Math.Min(780, wa.Height - 60);
            ClientSize = new Size(Math.Max(900, w), Math.Max(620, h));
            MinimumSize = new Size(Math.Min(1024, wa.Width - 20), Math.Min(660, wa.Height - 20));

            _browse = new BrowseTab(this);
            _settings = new SettingsPage(RefreshAll);

            var tabs = new TabControl { Dock = DockStyle.Fill };
            var p1 = new TabPage("档案浏览");
            var p2 = new TabPage("设置");
            p1.Controls.Add(_browse);
            p2.Controls.Add(_settings);
            tabs.TabPages.AddRange(new[] { p1, p2 });
            Controls.Add(tabs);
        }

        /// <summary>数据/字典/路径变化后整页刷新（浏览页重建树与列表）。</summary>
        public void RefreshAll()
        {
            _settings.Reload();
            _browse.ReloadAll();
        }
    }
}
