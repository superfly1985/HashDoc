using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace HashDoc
{
    public enum ScanCategory
    {
        PathChanged,    // 档案文件被改名/移动 → 更新库内路径
        Lost,           // 库内有、磁盘上没有 → 标记源文件丢失
        Duplicate,      // 同内容文件多份 → 重复档案，供人工清理
        Unregistered,   // 归档目录内存在但未入库 → 可补入库
        ContentChanged, // 位置没变但内容变了 → 人工核对
    }

    public class ScanRow
    {
        public ScanCategory Category;
        public string FilePath = "";   // 磁盘路径（丢失行 = 库内原路径）
        public string OldPath = "";
        public string Hash = "";
        public long NewSize;
        public string NewMTime = "";
        public string Detail = "";
        public bool CanApply;
        public bool Apply;
        public bool Applied;

        public string CategoryText
        {
            get
            {
                switch (Category)
                {
                    case ScanCategory.PathChanged: return "路径变更";
                    case ScanCategory.Lost: return "源文件丢失";
                    case ScanCategory.Duplicate: return "重复文件";
                    case ScanCategory.Unregistered: return "未入库";
                    default: return "内容已变化";
                }
            }
        }
    }

    public class ScanProgress
    {
        public int Done, Total;
        public string Current = "";
    }

    public class ScanOutput
    {
        public List<ScanRow> Rows = new List<ScanRow>();
        public int TotalFiles;
        public int HashedFiles;
        public int AutoApplied;
    }

    /// <summary>
    /// 归档目录扫描引擎。性能关键：路径+大小+修改时间三项与库内一致时跳过哈希，
    /// 只对“未知路径”和“大小/时间变了”的文件计算 SHA-256。
    /// </summary>
    public static class ScanCore
    {
        /// <summary>单目录扫描（自检与旧调用用）。</summary>
        public static Task<ScanOutput> ScanAsync(string root, bool autoApply,
            IProgress<ScanProgress> progress, CancellationToken ct)
        {
            return ScanAsync(new[] { root }, autoApply, progress, ct);
        }

        /// <summary>多目录扫描：一次核对该清单里的全部归档目录。</summary>
        public static Task<ScanOutput> ScanAsync(IEnumerable<string> roots, bool autoApply,
            IProgress<ScanProgress> progress, CancellationToken ct)
        {
            var list = new List<string>();
            foreach (var r in roots)
                if (!string.IsNullOrWhiteSpace(r)) list.Add(r);
            return Task.Run(delegate { return Scan(list, autoApply, progress, ct); }, ct);
        }

        public static ScanOutput Scan(IList<string> roots, bool autoApply, IProgress<ScanProgress> progress, CancellationToken ct)
        {
            var norm = new List<string>();
            foreach (var r in roots)
            {
                string p = Path.GetFullPath(r);
                if (p.Length > 3) p = p.TrimEnd('\\');
                bool dup = false;
                foreach (var x in norm) if (string.Equals(x, p, StringComparison.OrdinalIgnoreCase)) dup = true;
                if (!dup) norm.Add(p);
            }

            var all = FileRepo.LoadAll();
            var byPath = new Dictionary<string, FileRecord>(StringComparer.OrdinalIgnoreCase);
            var byHash = new Dictionary<string, FileRecord>(StringComparer.Ordinal);
            foreach (var rec in all)
            {
                if (!string.IsNullOrEmpty(rec.CurrentPath)) byPath[rec.CurrentPath] = rec;
                byHash[rec.Hash] = rec;
            }

            // 多个根目录之间可能有包含关系，按路径去重，避免同一文件扫两遍
            var diskFiles = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in norm)
                foreach (var f in Enumerate(root, ct))
                    if (seen.Add(f)) diskFiles.Add(f);

            var output = new ScanOutput { TotalFiles = diskFiles.Count };
            var matched = new HashSet<string>(StringComparer.Ordinal);     // 本次扫描确认仍在磁盘上的档案哈希
            var claimedMove = new HashSet<string>(StringComparer.Ordinal); // 同一哈希只允许认领一次“移动”，其余副本按重复处理
            var silentFix = new List<object[]>();                          // hash / size / mtime / status（内容未变仅属性变化）
            int done = 0;

            foreach (string path in diskFiles)
            {
                ct.ThrowIfCancellationRequested();
                done++;
                if (progress != null) progress.Report(new ScanProgress { Done = done, Total = diskFiles.Count, Current = path });

                FileRecord rec;
                byPath.TryGetValue(path, out rec);
                var fi = new FileInfo(path);

                if (rec != null)
                {
                    // 库内记录就在此路径
                    matched.Add(rec.Hash);
                    if (fi.Length == rec.FileSize && MTimeEqual(fi.LastWriteTime, rec.FileMTime))
                        continue; // 三要素一致 → 跳过哈希

                    string h = HashFile(output, path, ct);
                    if (h == rec.Hash)
                    {
                        // 内容没变，只是时间戳被动过（或丢失档案回归）→ 静默修正属性
                        silentFix.Add(new object[] { rec.Hash, fi.Length, AppSettings.MTimeStr(fi.LastWriteTime), 0 });
                        continue;
                    }
                    output.Rows.Add(new ScanRow
                    {
                        Category = ScanCategory.ContentChanged,
                        FilePath = path,
                        Hash = h,
                        Detail = "位置未变但内容与库内记录不一致（库内 " + HashService.Short(rec.Hash) + "），请人工核对",
                    });
                    continue;
                }

                // 未知路径 → 算哈希判定身份：改名/移动？重复？未入库？
                string hash = HashFile(output, path, ct);
                FileRecord orig;
                if (byHash.TryGetValue(hash, out orig))
                {
                    if (claimedMove.Contains(hash) || File.Exists(orig.CurrentPath))
                    {
                        output.Rows.Add(new ScanRow
                        {
                            Category = ScanCategory.Duplicate,
                            FilePath = path,
                            Hash = hash,
                            Detail = "与已归档档案内容相同，可人工清理：" + orig.CurrentPath,
                        });
                    }
                    else
                    {
                        claimedMove.Add(hash);
                        matched.Add(hash);
                        output.Rows.Add(new ScanRow
                        {
                            Category = ScanCategory.PathChanged,
                            FilePath = path,
                            OldPath = orig.CurrentPath,
                            Hash = hash,
                            NewSize = fi.Length,
                            NewMTime = AppSettings.MTimeStr(fi.LastWriteTime),
                            CanApply = true,
                            Apply = true,
                            Detail = "档案从【" + orig.CurrentPath + "】被移动/改名到当前位置",
                        });
                    }
                }
                else
                {
                    output.Rows.Add(new ScanRow
                    {
                        Category = ScanCategory.Unregistered,
                        FilePath = path,
                        Hash = hash,
                        NewSize = fi.Length,
                        NewMTime = AppSettings.MTimeStr(fi.LastWriteTime),
                        Detail = "归档目录内存在未入库文件，可补入库",
                    });
                }
            }

            // 库里有、磁盘上找不到 → 标记源文件丢失（只判定本次扫描范围内的目录，
            // 否则只扫 A 目录时 B 目录的档案会被误标成丢失）
            foreach (var rec in all)
            {
                if (matched.Contains(rec.Hash)) continue;
                if (rec.Status == 1) continue;
                if (!InScope(rec.CurrentPath, norm)) continue;
                output.Rows.Add(new ScanRow
                {
                    Category = ScanCategory.Lost,
                    FilePath = rec.CurrentPath,
                    Hash = rec.Hash,
                    CanApply = true,
                    Apply = true,
                    Detail = "库内档案在磁盘上未找到（记录保留，不删除档案）",
                });
            }

            // 属性静默修正始终生效（内容没变，只是时间戳/状态回归）
            foreach (var f in silentFix)
                FileRepo.UpdateSilent((string)f[0], (long)f[1], (string)f[2], (int)f[3]);

            if (autoApply) output.AutoApplied = Apply(output.Rows);
            return output;
        }

        static string HashFile(ScanOutput output, string path, CancellationToken ct)
        {
            output.HashedFiles++;
            return HashService.Sha256(path, null, ct);
        }

        /// <summary>把勾选的差异写入数据库（路径更新 / 丢失标记）。返回处理条数。</summary>
        public static int Apply(IEnumerable<ScanRow> rows)
        {
            int n = 0;
            foreach (var row in rows)
            {
                if (!row.CanApply || !row.Apply || row.Applied) continue;
                if (row.Category == ScanCategory.PathChanged)
                    FileRepo.UpdatePath(row.Hash, row.FilePath, row.NewSize, row.NewMTime);
                else if (row.Category == ScanCategory.Lost)
                    FileRepo.SetStatus(row.Hash, 1);
                row.Applied = true;
                n++;
            }
            return n;
        }

        /// <summary>文件是否落在本次扫描的某个根目录下。</summary>
        static bool InScope(string filePath, List<string> roots)
        {
            if (string.IsNullOrEmpty(filePath)) return false;
            string dir;
            try { dir = Path.GetDirectoryName(Path.GetFullPath(filePath)); }
            catch { return false; }
            if (string.IsNullOrEmpty(dir)) return false;
            foreach (var r in roots)
                if (ArchiveRoots.Under(dir, r)) return true;
            return false;
        }

        static List<string> Enumerate(string root, CancellationToken ct)
        {
            var files = new List<string>();
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                string dir = stack.Pop();
                try
                {
                    foreach (var f in Directory.GetFiles(dir))
                        if (FileTypes.IsSupported(f)) files.Add(f);
                    foreach (var d in Directory.GetDirectories(dir))
                    {
                        var attr = File.GetAttributes(d);
                        if ((attr & FileAttributes.ReparsePoint) == 0) stack.Push(d);
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (DirectoryNotFoundException) { }
                catch (IOException) { }
            }
            return files;
        }

        /// <summary>修改时间是否一致（±2 秒容差，兼容 FAT/网络盘的时间戳精度）。</summary>
        public static bool MTimeEqual(DateTime disk, string stored)
        {
            DateTime t;
            if (!DateTime.TryParseExact(stored, AppSettings.MTimeFmt,
                null, System.Globalization.DateTimeStyles.None, out t))
                return false;
            return Math.Abs((disk - t).TotalSeconds) <= 2.0;
        }
    }
}
