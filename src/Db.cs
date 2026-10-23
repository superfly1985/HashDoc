using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.IO;

namespace HashDoc
{
    /// <summary>SQLite 访问入口：连接、建库建表、参数化执行。所有语句经全局锁串行化，后台扫描与界面查询互不冲突。</summary>
    public static class Db
    {
        /// <summary>当前程序支持的库结构版本。新版本改表时 +1，并在 InitSchema 里补一段迁移。</summary>
        public const int SchemaVersion = 4;

        static readonly object _lock = new object();
        static SQLiteConnection _conn;

        public static string DbPath { get; private set; }

        /// <summary>本次启动如果做了结构升级，这里是自动备份出来的旧库路径（供启动提示用）。</summary>
        public static string LastUpgradeBackup { get; private set; }

        public static SQLiteConnection Conn { get { return _conn; } }

        public static void Open(string explicitPath = null)
        {
            LastUpgradeBackup = null;
            DbPath = explicitPath ?? ResolveDbPath();
            string dir = Path.GetDirectoryName(Path.GetFullPath(DbPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            _conn = new SQLiteConnection("Data Source=" + DbPath + ";Version=3;");
            _conn.Open();
            Exec("PRAGMA busy_timeout=3000");
            // 几万档案规模下的常规调优：WAL 减少写放大，加大页缓存，临时结果放内存
            try
            {
                Exec("PRAGMA journal_mode=WAL");
                Exec("PRAGMA synchronous=NORMAL");
                Exec("PRAGMA cache_size=-16000");   // ~16MB
                Exec("PRAGMA temp_store=MEMORY");
            }
            catch { }   // 老文件系统不支持 WAL 时静默退回默认日志模式
            try
            {
                InitSchema();
            }
            catch
            {
                // 结构检查/迁移失败时干净断开，避免留下半开的连接与锁
                try { _conn.Dispose(); } catch { }
                _conn = null;
                throw;
            }
        }

        /// <summary>
        /// 数据库固定放在 程序目录\data\HashDoc.db —— 程序文件可以整个覆盖升级，data 目录不受影响。
        /// 程序目录不可写（如放进 Program Files）时退回 %APPDATA%\HashDoc。
        /// </summary>
        static string ResolveDbPath()
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            try
            {
                string dataDir = Path.Combine(exeDir, "data");
                Directory.CreateDirectory(dataDir);
                string probe = Path.Combine(dataDir, ".write_probe");
                File.WriteAllText(probe, "1");
                File.Delete(probe);

                string target = Path.Combine(dataDir, "HashDoc.db");
                if (!File.Exists(target)) MigrateLegacyDb(exeDir, target);
                return target;
            }
            catch
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HashDoc");
                Directory.CreateDirectory(dir);
                string target = Path.Combine(dir, "HashDoc.db");
                if (!File.Exists(target))
                {
                    string legacy = Path.Combine(exeDir, "HashDoc.db");
                    if (File.Exists(legacy)) TryCopy(legacy, target);
                }
                return target;
            }
        }

        /// <summary>
        /// 老版本把 HashDoc.db 直接放在 exe 同级；首次运行新版时搬进 data\，
        /// 原文件改名留档（不直接删，出问题还能救回来）。
        /// </summary>
        static void MigrateLegacyDb(string exeDir, string target)
        {
            string legacy = Path.Combine(exeDir, "HashDoc.db");
            if (!File.Exists(legacy))
            {
                // 曾经因为程序目录只读而退到 %APPDATA% 的库，也一并搬过来
                string appData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HashDoc", "HashDoc.db");
                if (!File.Exists(appData)) return;
                legacy = appData;
            }
            if (!TryCopy(legacy, target)) return;
            try
            {
                string keep = legacy + ".old-" + DateTime.Now.ToString("yyyyMMdd");
                if (File.Exists(keep)) File.Delete(keep);
                File.Move(legacy, keep);
            }
            catch { } // 文件被占用就留着，反正 data\ 里已经有一份了
        }

        static bool TryCopy(string src, string dst)
        {
            try { File.Copy(src, dst, false); return true; }
            catch { return false; }
        }

        static void InitSchema()
        {
            // 先看库版本再动任何 DDL：库比程序新时直接拒绝，一行都不写
            long ver = Convert.ToInt64(Scalar("PRAGMA user_version") ?? 0L);
            if (ver > SchemaVersion) throw new DbTooNewException(ver);

            // 需要升级结构时，先把旧库整份备份到 data\backup，再动结构
            if (ver > 0 && ver < SchemaVersion) BackupBeforeUpgrade(ver);

            Exec(@"CREATE TABLE IF NOT EXISTS FileInfo(
    FileHash     TEXT PRIMARY KEY,
    CurrentPath  TEXT,
    FileName     TEXT NOT NULL,
    FileSize     INTEGER,
    FileMTime    TEXT,
    Supplier     TEXT,
    DocType      TEXT,
    Status       INTEGER DEFAULT 0,
    ImportTime   TEXT)");

            Exec(@"CREATE TABLE IF NOT EXISTS FileCode(
    FileHash   TEXT NOT NULL,
    CodeType   TEXT NOT NULL,
    CodeValue  TEXT NOT NULL,
    PRIMARY KEY(FileHash, CodeType, CodeValue))");

            Exec("CREATE INDEX IF NOT EXISTS IX_FileCode_Value ON FileCode(CodeValue)");
            // 录入时的随动检索：按类型取已出现过的编号值
            Exec("CREATE INDEX IF NOT EXISTS IX_FileCode_Type ON FileCode(CodeType, CodeValue)");
            Exec("CREATE INDEX IF NOT EXISTS IX_FileInfo_Supplier ON FileInfo(Supplier)");
            Exec("CREATE INDEX IF NOT EXISTS IX_FileInfo_DocType ON FileInfo(DocType)");

            Exec(@"CREATE TABLE IF NOT EXISTS SupplierDict(
    SupplierName TEXT PRIMARY KEY,
    Remark       TEXT)");

            Exec(@"CREATE TABLE IF NOT EXISTS DocTypeDict(
    DocTypeName TEXT PRIMARY KEY,
    SortOrder   INTEGER,
    Remark      TEXT)");

            Exec(@"CREATE TABLE IF NOT EXISTS Config(
    Key   TEXT PRIMARY KEY,
    Value TEXT)");

            // 归档目录清单（多位置归档）：第一行为默认入库位置
            Exec(@"CREATE TABLE IF NOT EXISTS ArchiveRoot(
    RootPath   TEXT PRIMARY KEY,
    SortOrder  INTEGER,
    Remark     TEXT)");

            // 目录快照（v4）：目录路径 → 修改时间，用于启动时的增量检查剪枝
            Exec(@"CREATE TABLE IF NOT EXISTS DirState(
    DirPath TEXT PRIMARY KEY,
    MTime   TEXT NOT NULL)");

            if (ver < 1) { Exec("PRAGMA user_version=1"); ver = 1; }
            if (ver < 2)
            {
                MigrateSingleRoot();
                Exec("PRAGMA user_version=2");
                ver = 2;
            }
            if (ver < 3)
            {
                // v2 → v3：档案加 BMG 号（单值可维护字段，允许为空）
                MigrateAddBmgNo();
                Exec("PRAGMA user_version=3");
            }
            if (ver < 4)
            {
                // v3 → v4：目录快照表（无存量数据要搬，建表即可）
                Exec(@"CREATE TABLE IF NOT EXISTS DirState(
    DirPath TEXT PRIMARY KEY,
    MTime   TEXT NOT NULL)");
                Exec("PRAGMA user_version=4");
            }

            DictRepo.SeedDocTypes();
        }

        /// <summary>v2 → v3：FileInfo 增加 BmgNo 列。旧库没有这列（SQLite 的 ADD COLUMN 没有 IF NOT EXISTS，先查一下）。</summary>
        static void MigrateAddBmgNo()
        {
            if (HasColumn("FileInfo", "BmgNo")) return;
            Exec("ALTER TABLE FileInfo ADD COLUMN BmgNo TEXT");
            Exec("CREATE INDEX IF NOT EXISTS IX_FileInfo_BmgNo ON FileInfo(BmgNo)");
        }

        static bool HasColumn(string table, string column)
        {
            var t = Query("PRAGMA table_info(" + table + ")");
            foreach (System.Data.DataRow r in t.Rows)
                if (string.Equals(Convert.ToString(r["name"]), column, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /// <summary>把 WAL 日志合回主库。在线备份前调用，保证备份出的单文件是完整的。</summary>
        public static void Checkpoint()
        {
            try { Exec("PRAGMA wal_checkpoint(TRUNCATE)"); } catch { }
        }

        /// <summary>结构升级前整份备份旧库到 data\backup\（用 SQLite 在线备份，程序正开着也安全）。</summary>
        static void BackupBeforeUpgrade(long fromVer)
        {
            try
            {
                Checkpoint();
                string dir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(DbPath)), "backup");
                Directory.CreateDirectory(dir);
                string dest = Path.Combine(dir,
                    "HashDoc_db" + fromVer + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".db");
                using (var target = new SQLiteConnection("Data Source=" + dest + ";Version=3;"))
                {
                    target.Open();
                    _conn.BackupDatabase(target, "main", "main", -1, null, 0);
                }
                LastUpgradeBackup = dest;
            }
            catch { } // 备份失败不阻断启动：迁移本身是幂等加表，风险很低
        }

        /// <summary>v1 → v2：把旧的单根配置（Config.RootDir）搬进 ArchiveRoot 清单。</summary>
        static void MigrateSingleRoot()
        {
            object n = Scalar("SELECT COUNT(*) FROM ArchiveRoot");
            if (n != null && n != DBNull.Value && Convert.ToInt32(n) > 0) return;

            string legacy = Config.Get("RootDir");
            if (string.IsNullOrWhiteSpace(legacy)) return;
            try
            {
                string p = Path.GetFullPath(legacy.Trim());
                if (p.Length > 3) p = p.TrimEnd('\\');
                Exec("INSERT INTO ArchiveRoot(RootPath,SortOrder,Remark) VALUES(@p,1,'迁移自旧版归档根目录')",
                    SqlArgs.P("@p", p));
            }
            catch { }
        }

        public static int Exec(string sql, Dictionary<string, object> ps = null)
        {
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = sql;
                    Fill(cmd, ps);
                    return cmd.ExecuteNonQuery();
                }
            }
        }

        public static object Scalar(string sql, Dictionary<string, object> ps = null)
        {
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = sql;
                    Fill(cmd, ps);
                    return cmd.ExecuteScalar();
                }
            }
        }

        public static DataTable Query(string sql, Dictionary<string, object> ps = null)
        {
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = sql;
                    Fill(cmd, ps);
                    using (var reader = cmd.ExecuteReader())
                    {
                        var table = new DataTable();
                        table.Load(reader);
                        return table;
                    }
                }
            }
        }

        public static void InTx(Action action)
        {
            lock (_lock)
            {
                using (var tx = _conn.BeginTransaction())
                {
                    action();
                    tx.Commit();
                }
            }
        }

        static void Fill(SQLiteCommand cmd, Dictionary<string, object> ps)
        {
            if (ps == null) return;
            foreach (var kv in ps)
                cmd.Parameters.AddWithValue(kv.Key, kv.Value ?? DBNull.Value);
        }
    }

    /// <summary>SQL 参数字典的简写构造：SqlArgs.P("@h", hash, "@p", path)。</summary>
    public static class SqlArgs
    {
        public static Dictionary<string, object> P(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i + 1 < kv.Length; i += 2)
                d[(string)kv[i]] = kv[i + 1];
            return d;
        }
    }

    /// <summary>库结构版本比程序支持的还新（用户把程序换成了旧版）。这种情况只提示、不写库。</summary>
    public class DbTooNewException : Exception
    {
        public readonly long FoundVersion;

        public DbTooNewException(long found)
            : base("这个档案库由更高版本的「识档」创建（库版本 " + found +
                   "，本程序只支持到 " + Db.SchemaVersion + "）。\n\n" +
                   "请把程序升级到最新发布包后再打开；数据库本身没有被修改，数据安全。\n\n" +
                   "数据库位置：" + (Db.DbPath ?? "(未知)"))
        {
            FoundVersion = found;
        }
    }

    /// <summary>应用配置（Config 表），拷贝数据库即拷贝全部配置。</summary>
    public static class Config
    {
        public static string Get(string key)
        {
            return Db.Scalar("SELECT Value FROM Config WHERE Key=@k", SqlArgs.P("@k", key)) as string;
        }

        public static void Set(string key, string value)
        {
            Db.Exec("INSERT INTO Config(Key,Value) VALUES(@k,@v) " +
                    "ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value",
                    SqlArgs.P("@k", key, "@v", value));
        }
    }
}
