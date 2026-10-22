using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace HashDoc
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            // 无界面自检：HashDoc.exe --selftest，结果写入 EXE 目录 selftest_result.txt
            if (args != null && args.Contains("--selftest"))
                return SelfTest.Run();
            // 支持排查：打印程序版本、数据库位置、库版本、档案条数（覆盖升级后核对用）
            if (args != null && args.Contains("--dbinfo"))
                return DbInfo();

            bool createdNew;
            using (var mux = new Mutex(true, "HashDoc_SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("识档（HashDoc）已在运行中。", "提示",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                try
                {
                    Db.Open();
                }
                catch (DbTooNewException ex)
                {
                    MessageBox.Show(ex.Message, "档案库版本过高 — 识档 HashDoc",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return 2;
                }
                catch (Exception ex)
                {
                    MessageBox.Show("数据库初始化失败：" + ex.Message +
                        "\n\n数据库位置：" + (Db.DbPath ?? "(未知)"), "识档 HashDoc",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }

                // 结构升级只发生一次，升级前已自动备份，这里告知用户备份位置
                if (!string.IsNullOrEmpty(Db.LastUpgradeBackup))
                    MessageBox.Show(
                        "档案库已自动升级到新版结构，原有档案、字典、配置全部保留。\n\n" +
                        "升级前的旧库已备份到：\n" + Db.LastUpgradeBackup,
                        "档案库已升级 — 识档 HashDoc", MessageBoxButtons.OK, MessageBoxIcon.Information);

                Application.Run(new MainForm());
            }
            return 0;
        }

        /// <summary>HashDoc.exe --dbinfo：打印版本 / 库位置 / 库版本 / 档案条数。覆盖升级后用它核对。</summary>
        static int DbInfo()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("程序版本：" + UiUtil.AppVersion());
            sb.AppendLine("程序目录：" + AppDomain.CurrentDomain.BaseDirectory);
            try
            {
                Db.Open();
                sb.AppendLine("数据库文件：" + Db.DbPath);
                sb.AppendLine("库结构版本：" + Convert.ToInt64(Db.Scalar("PRAGMA user_version")) +
                              "（本程序支持 " + Db.SchemaVersion + "）");
                sb.AppendLine("档案条数：" + Convert.ToInt64(Db.Scalar("SELECT COUNT(*) FROM FileInfo")));
                sb.AppendLine("归档目录：" + ArchiveRoots.Paths().Count + " 个");
            }
            catch (Exception ex)
            {
                sb.AppendLine("打开数据库失败：" + ex.Message);
                Console.WriteLine(sb.ToString());
                return 1;
            }
            Console.WriteLine(sb.ToString());
            return 0;
        }
    }
}
