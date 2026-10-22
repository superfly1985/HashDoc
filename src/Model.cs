using System;
using System.Collections.Generic;
using System.IO;

namespace HashDoc
{
    /// <summary>文件档案记录。FileHash（SHA-256）是文件唯一身份，改名、移动不丢失业务属性，其余为附属信息。</summary>
    public class FileRecord
    {
        public string Hash;
        public string CurrentPath;
        public string FileName;
        public long FileSize;
        public string FileMTime;     // yyyy-MM-dd HH:mm:ss
        public string Supplier;
        public string DocType;
        public string BmgNo;         // BMG 号（单值，可空）
        public int Status;           // 0=正常 1=源文件丢失
        public string ImportTime;
        public List<CodeEntry> Codes = new List<CodeEntry>();

        public bool Lost { get { return Status == 1; } }

        /// <summary>
        /// 供应商 / 文档类型 / BMG 号还空着 = 这条档案的信息没填完。补入库允许先登记、事后再补，
        /// 主列表用「待补」标出来，勾「只看待补」就能挑出来逐条填。
        /// </summary>
        public bool NeedsInfo
        {
            get
            {
                return string.IsNullOrWhiteSpace(Supplier)
                    || string.IsNullOrWhiteSpace(DocType)
                    || string.IsNullOrWhiteSpace(BmgNo);
            }
        }

        /// <summary>缺哪些信息（列表提示、状态栏统计用）。</summary>
        public string MissingInfo
        {
            get
            {
                var names = new List<string>();
                if (string.IsNullOrWhiteSpace(Supplier)) names.Add("供应商");
                if (string.IsNullOrWhiteSpace(DocType)) names.Add("类型");
                if (string.IsNullOrWhiteSpace(BmgNo)) names.Add("BMG号");
                return string.Join("、", names);
            }
        }
    }

    public class CodeEntry
    {
        public string Type;   // CodeTypes.*
        public string Value;

        public CodeEntry() { }

        public CodeEntry(string type, string value)
        {
            Type = type;
            Value = value;
        }
    }

    /// <summary>关联编号的三种固定类型。编码存 TEXT，后续如需扩展类型只需加常量。</summary>
    public static class CodeTypes
    {
        public const string PartNo = "SupplierPartNo";
        public const string VwNo = "VWNo";
        public const string IdentNo = "IdentNo";

        public static readonly string[][] All = new[]
        {
            new[] { PartNo, "供应商零件号" },
            new[] { VwNo, "VW客户号" },
            new[] { IdentNo, "ident内部件号" },
        };

        public static string Label(string type)
        {
            foreach (var x in All)
                if (x[0] == type) return x[1];
            return type;
        }
    }

    public class DictItem
    {
        public string Name;
        public string Remark;
    }

    public class SearchFilter
    {
        public string Name;
        public string Supplier;
        public string DocType;
        public string BmgNo;
        public string PartNo;
        public string VwNo;
        public string IdentNo;
    }

    /// <summary>只处理 PDF/JPG/PNG/BMP/TIFF，其余后缀直接跳过。</summary>
    public static class FileTypes
    {
        static readonly HashSet<string> _ext = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".pdf", ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff"
        };

        public static bool IsSupported(string path)
        {
            return _ext.Contains(Path.GetExtension(path) ?? "");
        }

        public const string DialogFilter =
            "支持的档案文件(PDF/图片)|*.pdf;*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff|PDF 文档|*.pdf|图片|*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff|全部文件|*.*";
    }

    /// <summary>应用级配置（存 Config 表）与通用格式。</summary>
    public static class AppSettings
    {
        public const string MTimeFmt = "yyyy-MM-dd HH:mm:ss";

        /// <summary>默认归档目录（清单第一行）。多目录请用 ArchiveRoots。</summary>
        public static string RootDir() { return ArchiveRoots.Default(); }

        /// <summary>兼容旧调用：把该目录加入清单并设为默认。</summary>
        public static void RootDir(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return;
            ArchiveRoots.Add(v);
            ArchiveRoots.SetDefault(ArchiveRoots.Normalize(v));
        }

        /// <summary>auto=自动静默更新 confirm=列出差异确认后更新</summary>
        public static string ScanMode() { return Config.Get("ScanMode") ?? "confirm"; }
        public static void ScanMode(string v) { Config.Set("ScanMode", v); }

        public static bool MoveOnImport() { return Config.Get("ImportMove") == "1"; }
        public static void MoveOnImport(bool v) { Config.Set("ImportMove", v ? "1" : "0"); }

        public static string NowStr() { return DateTime.Now.ToString(MTimeFmt); }
        public static string MTimeStr(DateTime t) { return t.ToString(MTimeFmt); }
    }

    public class ArchiveRoot
    {
        public string Path;
        public string Remark = "";
        public int SortOrder;
    }

    /// <summary>
    /// 归档目录清单（ArchiveRoot 表）：可以有多个不同位置的归档根目录，
    /// 第一行为默认入库位置。档案归属由路径判断，FileInfo 不存根目录字段。
    /// </summary>
    public static class ArchiveRoots
    {
        public static List<ArchiveRoot> All()
        {
            var list = new List<ArchiveRoot>();
            var t = Db.Query("SELECT RootPath, SortOrder, Remark FROM ArchiveRoot ORDER BY SortOrder, RootPath");
            foreach (System.Data.DataRow r in t.Rows)
                list.Add(new ArchiveRoot
                {
                    Path = Convert.ToString(r["RootPath"]),
                    SortOrder = r["SortOrder"] == DBNull.Value ? 0 : Convert.ToInt32(r["SortOrder"]),
                    Remark = Convert.ToString(r["Remark"]) ?? "",
                });
            return list;
        }

        public static List<string> Paths()
        {
            var list = new List<string>();
            foreach (var r in All()) if (!string.IsNullOrEmpty(r.Path)) list.Add(r.Path);
            return list;
        }

        /// <summary>默认归档目录：清单第一行。</summary>
        public static string Default()
        {
            var p = Paths();
            return p.Count > 0 ? p[0] : "";
        }

        /// <summary>统一成绝对路径、去掉末尾反斜杠；非法路径返回空串。</summary>
        public static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            try
            {
                string full = Path.GetFullPath(path.Trim());
                return full.Length > 3 ? full.TrimEnd('\\') : full;
            }
            catch { return ""; }
        }

        public static bool Contains(string path)
        {
            string p = Normalize(path);
            if (p.Length == 0) return false;
            foreach (var x in Paths())
                if (string.Equals(x, p, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static void Add(string path, string remark = "")
        {
            string p = Normalize(path);
            if (p.Length == 0) return;
            object max = Db.Scalar("SELECT MAX(SortOrder) FROM ArchiveRoot");
            int order = (max == null || max == DBNull.Value) ? 1 : Convert.ToInt32(max) + 1;
            Db.Exec("INSERT INTO ArchiveRoot(RootPath,SortOrder,Remark) VALUES(@p,@o,@r) " +
                    "ON CONFLICT(RootPath) DO UPDATE SET Remark=excluded.Remark",
                    SqlArgs.P("@p", p, "@o", order, "@r", remark ?? ""));
        }

        /// <summary>只从清单移除，磁盘文件不动。</summary>
        public static void Remove(string path)
        {
            string p = Normalize(path);
            if (p.Length == 0) return;
            Db.Exec("DELETE FROM ArchiveRoot WHERE RootPath=@p", SqlArgs.P("@p", p));
        }

        /// <summary>设为默认：排到清单第一位，其余保持原有相对顺序。</summary>
        public static void SetDefault(string path)
        {
            string p = Normalize(path);
            var paths = Paths();
            int idx = paths.FindIndex(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) return;
            if (idx > 0) { paths.RemoveAt(idx); paths.Insert(0, p); }
            for (int i = 0; i < paths.Count; i++)
                Db.Exec("UPDATE ArchiveRoot SET SortOrder=@o WHERE RootPath=@p",
                    SqlArgs.P("@o", i + 1, "@p", paths[i]));
        }

        /// <summary>档案属于哪个归档目录（取最具体的匹配）；都不在则返回 null。</summary>
        public static string RootOf(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return null;
            string dir;
            try { dir = Path.GetDirectoryName(Path.GetFullPath(filePath)); }
            catch { return null; }
            if (string.IsNullOrEmpty(dir)) return null;

            string best = null;
            foreach (var r in Paths())
                if (Under(dir, r) && (best == null || r.Length > best.Length)) best = r;
            return best;
        }

        public static bool IsUnderAny(string filePath) { return RootOf(filePath) != null; }

        public static bool Under(string dir, string root)
        {
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(root)) return false;
            return dir.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                   dir.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>树上显示的名字：取最后一级目录名；盘符根目录补回反斜杠。</summary>
        public static string DisplayName(string root)
        {
            string name = Path.GetFileName(root ?? "");
            if (string.IsNullOrEmpty(name)) return root ?? "";
            return name;
        }
    }
}
