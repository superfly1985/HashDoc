using System;
using System.Collections.Generic;
using System.Data;
using System.Text;

namespace HashDoc
{
    /// <summary>供应商 / 文档类型字典的维护。改名会同步更新档案表中的引用，磁盘文件夹不动。</summary>
    public static class DictRepo
    {
        public static List<DictItem> Suppliers()
        {
            return ToItems(Db.Query("SELECT SupplierName, Remark FROM SupplierDict ORDER BY SupplierName COLLATE NOCASE"),
                "SupplierName");
        }

        public static List<DictItem> DocTypes()
        {
            return ToItems(Db.Query("SELECT DocTypeName, Remark FROM DocTypeDict ORDER BY SortOrder, DocTypeName COLLATE NOCASE"),
                "DocTypeName");
        }

        static List<DictItem> ToItems(DataTable t, string nameCol)
        {
            var list = new List<DictItem>();
            foreach (DataRow r in t.Rows)
                list.Add(new DictItem
                {
                    Name = r[nameCol] as string ?? "",
                    Remark = r["Remark"] as string ?? ""
                });
            return list;
        }

        public static bool SupplierExists(string name)
        {
            return Convert.ToInt64(Db.Scalar("SELECT COUNT(*) FROM SupplierDict WHERE SupplierName=@n", SqlArgs.P("@n", name))) > 0;
        }

        public static void SupplierAdd(string name, string remark)
        {
            Db.Exec("INSERT INTO SupplierDict(SupplierName, Remark) VALUES(@n, @r)", SqlArgs.P("@n", name, "@r", remark));
        }

        public static void SupplierRename(string oldName, string newName, string remark)
        {
            Db.InTx(delegate
            {
                Db.Exec("UPDATE SupplierDict SET SupplierName=@n, Remark=@r WHERE SupplierName=@o",
                    SqlArgs.P("@n", newName, "@r", remark, "@o", oldName));
                Db.Exec("UPDATE FileInfo SET Supplier=@n WHERE Supplier=@o",
                    SqlArgs.P("@n", newName, "@o", oldName));
            });
        }

        public static void SupplierDelete(string name)
        {
            Db.Exec("DELETE FROM SupplierDict WHERE SupplierName=@n", SqlArgs.P("@n", name));
        }

        public static int SupplierUsage(string name)
        {
            return Convert.ToInt32(Db.Scalar("SELECT COUNT(*) FROM FileInfo WHERE Supplier=@n", SqlArgs.P("@n", name)));
        }

        public static bool DocTypeExists(string name)
        {
            return Convert.ToInt64(Db.Scalar("SELECT COUNT(*) FROM DocTypeDict WHERE DocTypeName=@n", SqlArgs.P("@n", name))) > 0;
        }

        public static void DocTypeAdd(string name, string remark)
        {
            int max = Convert.ToInt32(Db.Scalar("SELECT IFNULL(MAX(SortOrder),0) FROM DocTypeDict"));
            Db.Exec("INSERT INTO DocTypeDict(DocTypeName, SortOrder, Remark) VALUES(@n, @s, @r)",
                SqlArgs.P("@n", name, "@s", max + 1, "@r", remark));
        }

        public static void DocTypeRename(string oldName, string newName, string remark)
        {
            Db.InTx(delegate
            {
                Db.Exec("UPDATE DocTypeDict SET DocTypeName=@n, Remark=@r WHERE DocTypeName=@o",
                    SqlArgs.P("@n", newName, "@r", remark, "@o", oldName));
                Db.Exec("UPDATE FileInfo SET DocType=@n WHERE DocType=@o",
                    SqlArgs.P("@n", newName, "@o", oldName));
            });
        }

        public static void DocTypeDelete(string name)
        {
            Db.Exec("DELETE FROM DocTypeDict WHERE DocTypeName=@n", SqlArgs.P("@n", name));
        }

        public static int DocTypeUsage(string name)
        {
            return Convert.ToInt32(Db.Scalar("SELECT COUNT(*) FROM FileInfo WHERE DocType=@n", SqlArgs.P("@n", name)));
        }

        /// <summary>首版内置三类；细分型号（如 PPAP-首页、PPAP-FMEA）后续在此字典里自行添加即可。</summary>
        public static void SeedDocTypes()
        {
            long n = Convert.ToInt64(Db.Scalar("SELECT COUNT(*) FROM DocTypeDict"));
            if (n > 0) return;
            DocTypeAdd("BMG", "");
            DocTypeAdd("PPAP", "");
            DocTypeAdd("ELV", "");
        }
    }

    /// <summary>档案与关联编号的增查改。</summary>
    public static class FileRepo
    {
        public static bool Exists(string hash)
        {
            return Convert.ToInt64(Db.Scalar("SELECT COUNT(*) FROM FileInfo WHERE FileHash=@h", SqlArgs.P("@h", hash))) > 0;
        }

        public static void Insert(FileRecord r)
        {
            Db.InTx(delegate
            {
                Db.Exec(@"INSERT INTO FileInfo(FileHash, CurrentPath, FileName, FileSize, FileMTime, Supplier, DocType, BmgNo, Status, ImportTime)
                          VALUES(@h, @p, @n, @s, @m, @sup, @dt, @bmg, @st, @it)",
                    SqlArgs.P("@h", r.Hash, "@p", r.CurrentPath, "@n", r.FileName, "@s", r.FileSize,
                              "@m", r.FileMTime, "@sup", r.Supplier, "@dt", r.DocType, "@bmg", r.BmgNo,
                              "@st", r.Status, "@it", r.ImportTime));
                foreach (var c in r.Codes)
                    InsertCode(r.Hash, c);
            });
        }

        static void InsertCode(string hash, CodeEntry c)
        {
            Db.Exec("INSERT OR IGNORE INTO FileCode(FileHash, CodeType, CodeValue) VALUES(@h, @t, @v)",
                SqlArgs.P("@h", hash, "@t", c.Type, "@v", c.Value));
        }

        public static FileRecord GetByHash(string hash)
        {
            var t = Db.Query("SELECT * FROM FileInfo WHERE FileHash=@h", SqlArgs.P("@h", hash));
            if (t.Rows.Count == 0) return null;
            var rec = FromRow(t.Rows[0]);
            rec.Codes = LoadCodes(hash);
            return rec;
        }

        public static List<FileRecord> LoadAll()
        {
            var list = new List<FileRecord>();
            var t = Db.Query("SELECT * FROM FileInfo");
            foreach (DataRow r in t.Rows)
                list.Add(FromRow(r));
            return list;
        }

        /// <summary>全量加载并附带编号（档案浏览页建树/列表用）。</summary>
        public static List<FileRecord> LoadAllWithCodes()
        {
            var list = LoadAll();
            AttachCodes(list);
            return list;
        }

        /// <summary>统计某个归档目录子树下的档案数（设置页清单用，不遍历磁盘）。</summary>
        public static int CountUnder(string root)
        {
            if (string.IsNullOrEmpty(root)) return 0;
            string prefix = root + "\\";
            object n = Db.Scalar(
                "SELECT COUNT(*) FROM FileInfo WHERE lower(substr(CurrentPath,1,@n))=lower(@q)",
                SqlArgs.P("@n", prefix.Length, "@q", prefix));
            return n == null || n == DBNull.Value ? 0 : Convert.ToInt32(n);
        }

        /// <summary>
        /// 某一类编号在库内已出现过的所有值（按出现次数降序），用于录入时的随动检索候选。
        /// </summary>
        public static List<string> DistinctCodes(string codeType)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(codeType)) return list;
            var t = Db.Query(
                "SELECT CodeValue, COUNT(*) AS C FROM FileCode WHERE CodeType=@t " +
                "GROUP BY CodeValue ORDER BY C DESC, CodeValue",
                SqlArgs.P("@t", codeType));
            foreach (DataRow r in t.Rows)
            {
                string v = r["CodeValue"] as string;
                if (!string.IsNullOrWhiteSpace(v)) list.Add(v);
            }
            return list;
        }

        /// <summary>库内出现过的 BMG 号（按使用次数降序），用于录入时的随动检索候选。</summary>
        public static List<string> DistinctBmgNos()
        {
            var list = new List<string>();
            var t = Db.Query(
                "SELECT BmgNo, COUNT(*) AS C FROM FileInfo WHERE BmgNo IS NOT NULL AND BmgNo<>'' " +
                "GROUP BY BmgNo ORDER BY C DESC, BmgNo");
            foreach (DataRow r in t.Rows)
            {
                string v = r["BmgNo"] as string;
                if (!string.IsNullOrWhiteSpace(v)) list.Add(v);
            }
            return list;
        }

        static List<CodeEntry> LoadCodes(string hash)
        {
            var list = new List<CodeEntry>();
            var t = Db.Query("SELECT CodeType, CodeValue FROM FileCode WHERE FileHash=@h ORDER BY CodeType, CodeValue",
                SqlArgs.P("@h", hash));
            foreach (DataRow r in t.Rows)
                list.Add(new CodeEntry(r["CodeType"] as string, r["CodeValue"] as string));
            return list;
        }

        static FileRecord FromRow(DataRow r)
        {
            return new FileRecord
            {
                Hash = r["FileHash"] as string ?? "",
                CurrentPath = r["CurrentPath"] as string ?? "",
                FileName = r["FileName"] as string ?? "",
                FileSize = r["FileSize"] == DBNull.Value ? 0 : Convert.ToInt64(r["FileSize"]),
                FileMTime = r["FileMTime"] as string ?? "",
                Supplier = r["Supplier"] as string ?? "",
                DocType = r["DocType"] as string ?? "",
                BmgNo = r["BmgNo"] == DBNull.Value ? "" : (r["BmgNo"] as string ?? ""),
                Status = r["Status"] == DBNull.Value ? 0 : Convert.ToInt32(r["Status"]),
                ImportTime = r["ImportTime"] as string ?? "",
            };
        }

        public static void UpdatePath(string hash, string path, long size, string mtime)
        {
            Db.Exec("UPDATE FileInfo SET CurrentPath=@p, FileSize=@s, FileMTime=@m, Status=0 WHERE FileHash=@h",
                SqlArgs.P("@p", path, "@s", size, "@m", mtime, "@h", hash));
        }

        public static void SetStatus(string hash, int status)
        {
            Db.Exec("UPDATE FileInfo SET Status=@st WHERE FileHash=@h", SqlArgs.P("@st", status, "@h", hash));
        }

        /// <summary>内容未变、仅大小/时间戳变化（或丢失档案回归）时的静默修正。</summary>
        public static void UpdateSilent(string hash, long size, string mtime, int status)
        {
            Db.Exec("UPDATE FileInfo SET FileSize=@s, FileMTime=@m, Status=@st WHERE FileHash=@h",
                SqlArgs.P("@s", size, "@m", mtime, "@st", status, "@h", hash));
        }

        public static void UpdateAttrs(string hash, string supplier, string docType, string bmgNo)
        {
            Db.Exec("UPDATE FileInfo SET Supplier=@sup, DocType=@dt, BmgNo=@bmg WHERE FileHash=@h",
                SqlArgs.P("@sup", supplier, "@dt", docType, "@bmg", bmgNo ?? "", "@h", hash));
        }

        public static void ReplaceCodes(string hash, List<CodeEntry> codes)
        {
            Db.InTx(delegate
            {
                Db.Exec("DELETE FROM FileCode WHERE FileHash=@h", SqlArgs.P("@h", hash));
                foreach (var c in codes)
                    InsertCode(hash, c);
            });
        }

        /// <summary>数据库索引检索，不遍历磁盘。条件全部可组合，编号为包含匹配。</summary>
        public static List<FileRecord> Search(SearchFilter f)
        {
            var sql = new StringBuilder("SELECT * FROM FileInfo WHERE 1=1");
            var ps = new Dictionary<string, object>();

            if (!string.IsNullOrEmpty(f.Name))
            {
                sql.Append(" AND FileName LIKE @n ESCAPE '\\'");
                ps["@n"] = "%" + EscapeLike(f.Name) + "%";
            }
            if (!string.IsNullOrEmpty(f.Supplier))
            {
                sql.Append(" AND Supplier=@sup");
                ps["@sup"] = f.Supplier;
            }
            if (!string.IsNullOrEmpty(f.DocType))
            {
                sql.Append(" AND DocType=@dt");
                ps["@dt"] = f.DocType;
            }
            if (!string.IsNullOrEmpty(f.BmgNo))
            {
                sql.Append(" AND BmgNo LIKE @bmg ESCAPE '\\'");
                ps["@bmg"] = "%" + EscapeLike(f.BmgNo) + "%";
            }
            AddCodeCond(sql, ps, f.PartNo, CodeTypes.PartNo, "@pn");
            AddCodeCond(sql, ps, f.VwNo, CodeTypes.VwNo, "@vw");
            AddCodeCond(sql, ps, f.IdentNo, CodeTypes.IdentNo, "@id");

            sql.Append(" ORDER BY FileName COLLATE NOCASE LIMIT 1000");

            var list = new List<FileRecord>();
            foreach (DataRow r in Db.Query(sql.ToString(), ps).Rows)
                list.Add(FromRow(r));
            AttachCodes(list);
            return list;
        }

        static void AddCodeCond(StringBuilder sql, Dictionary<string, object> ps, string value, string type, string pname)
        {
            if (string.IsNullOrEmpty(value)) return;
            // type 是代码内常量，非用户输入，可直接拼接
            sql.Append(" AND EXISTS(SELECT 1 FROM FileCode c WHERE c.FileHash=FileInfo.FileHash" +
                       " AND c.CodeType='" + type + "' AND c.CodeValue LIKE " + pname + " ESCAPE '\\')");
            ps[pname] = "%" + EscapeLike(value) + "%";
        }

        static void AttachCodes(List<FileRecord> list)
        {
            if (list.Count == 0) return;
            var map = new Dictionary<string, FileRecord>();
            foreach (var r in list) map[r.Hash] = r;

            for (int start = 0; start < list.Count; start += 400)
            {
                int count = Math.Min(400, list.Count - start);
                var ps = new Dictionary<string, object>();
                var ins = new StringBuilder();
                for (int i = 0; i < count; i++)
                {
                    if (i > 0) ins.Append(",");
                    string pn = "@h" + i;
                    ins.Append(pn);
                    ps[pn] = list[start + i].Hash;
                }
                var t = Db.Query("SELECT FileHash, CodeType, CodeValue FROM FileCode WHERE FileHash IN (" + ins + ")", ps);
                foreach (DataRow row in t.Rows)
                {
                    string h = row["FileHash"] as string;
                    FileRecord rec;
                    if (h != null && map.TryGetValue(h, out rec))
                        rec.Codes.Add(new CodeEntry(row["CodeType"] as string, row["CodeValue"] as string));
                }
            }
        }

        public static string EscapeLike(string s)
        {
            return s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        }
    }
}
