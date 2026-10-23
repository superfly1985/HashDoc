using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
        public int FailedHashes;   // 被占用/无权限等原因读不了的文件数
    }

    /// <summary>磁盘上一个文件的关键信息。枚举时一次拿全，避免对每个文件再单独取大小/时间。</summary>
    public class FileEntry
    {
        public string Path;
        public long Size;
        public DateTime MTime;
    }

    public class DirEntry
    {
        public string Path;
        public DateTime MTime;
    }

    /// <summary>
    /// 归档目录扫描引擎。性能关键：
    /// 1) 路径+大小+修改时间三项与库内一致时跳过哈希；
    /// 2) 目录树一遍枚举拿全元数据（不再对每个文件单独 FileInfo）；
    /// 3) 需要哈希的文件并行计算；
    /// 4) 进度上报节流（每 200ms 一条，不轰炸 UI 线程）；
    /// 5) 目录快照（DirState）做增量剪枝——启动检查只进"变过的目录"。
    /// </summary>
    public static class ScanCore
    {
        /// <summary>单目录扫描（自检与旧调用用）。</summary>
        public static Task<ScanOutput> ScanAsync(string root, bool autoApply,
            IProgress<ScanProgress> progress, CancellationToken ct)
        {
            return ScanAsync(new[] { root }, autoApply, progress, ct);
        }

        /// <summary>多目录扫描：一次核对该清单里的全部归档目录（全量，结束时刷新目录快照）。</summary>
        public static Task<ScanOutput> ScanAsync(IEnumerable<string> roots, bool autoApply,
            IProgress<ScanProgress> progress, CancellationToken ct)
        {
            var list = new List<string>();
            foreach (var r in roots)
                if (!string.IsNullOrWhiteSpace(r)) list.Add(r);
            return Task.Run(delegate { return Scan(list, autoApply, progress, ct); }, ct);
        }

        /// <summary>全量扫描（手动【扫描目录】/【刷新】用）。结束时把目录快照写回库里。</summary>
        public static ScanOutput Scan(IList<string> roots, bool autoApply, IProgress<ScanProgress> progress, CancellationToken ct)
        {
            Dictionary<string, string> states;
            var output = ScanFull(roots, progress, ct, out states);
            try { DirStateRepo.SaveAll(states); } catch { }    // 快照写失败不影响本次结果
            if (autoApply) output.AutoApplied = Apply(output.Rows);
            return output;
        }

        /// <summary>
        /// 启动时的增量"新文件"检查：只枚举目录树，与上次快照比对，只进入变过的目录。
        /// 稳态（没人动过）时几乎不碰文件；有人拷了新文件进去时只摸所在目录。
        /// 结果只含"未入库"行——路径变更/丢失/重复仍然交给【扫描目录】。
        /// </summary>
        public static ScanOutput ScanForNewFiles(IList<string> roots, IProgress<ScanProgress> progress, CancellationToken ct)
        {
            var prev = DirStateRepo.Load();
            Dictionary<string, string> now;
            var output = ScanForNewFiles(roots, prev, progress, ct, out now);
            try { DirStateRepo.SaveAll(now); } catch { }
            return output;
        }

        /// <summary>纯计算版（自检用）：传入上次快照，输出本次快照，不读写 DirState 表。</summary>
        public static ScanOutput ScanForNewFiles(IList<string> roots, Dictionary<string, string> prevStates,
            IProgress<ScanProgress> progress, CancellationToken ct, out Dictionary<string, string> newStates)
        {
            newStates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var throttle = new ProgressThrottle(progress);
            var norm = NormalizeRoots(roots);
            var output = new ScanOutput();
            if (norm.Count == 0) return output;

            // 1) 只枚举目录树：几万文件的归档区通常只有几百~几千个目录
            var allDirs = new List<DirEntry>();
            var dirSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in norm)
                foreach (var d in WalkDirs(root, ct))
                    if (dirSeen.Add(d.Path)) allDirs.Add(d);

            foreach (var d in allDirs)
                newStates[d.Path] = AppSettings.MTimeStr(d.MTime);

            // 2) 剪枝：只保留"快照里没有"或"修改时间和快照对不上"的目录
            var changed = new List<DirEntry>();
            foreach (var d in allDirs)
            {
                string stored;
                if (prevStates != null && prevStates.TryGetValue(d.Path, out stored) &&
                    MTimeEqual(d.MTime, stored))
                    continue;
                changed.Add(d);
            }
            if (changed.Count == 0) return output;

            // 3) 变过的目录里枚举文件，与库内路径比对
            var all = FileRepo.LoadAll();
            var byPath = new Dictionary<string, FileRecord>(StringComparer.OrdinalIgnoreCase);
            var byHash = new Dictionary<string, FileRecord>(StringComparer.Ordinal);
            foreach (var rec in all)
            {
                if (!string.IsNullOrEmpty(rec.CurrentPath)) byPath[rec.CurrentPath] = rec;
                byHash[rec.Hash] = rec;
            }

            var unknown = new List<FileEntry>();
            var fileSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int seenFiles = 0;
            foreach (var d in changed)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var f in ListFiles(d.Path, ct))
                {
                    if (!fileSeen.Add(f.Path)) continue;
                    seenFiles++;
                    if (!byPath.ContainsKey(f.Path)) unknown.Add(f);
                }
            }
            output.TotalFiles = seenFiles;

            // 4) 未知路径算哈希定身份：命中库内哈希 = 移动/重复（不是新文件），未命中 = 未入库
            var hashes = HashMany(unknown, progress, "正在核对新文件", ct, output);
            foreach (var f in unknown)
            {
                string hash;
                if (!hashes.TryGetValue(f.Path, out hash)) continue;   // 读取失败，跳过
                if (byHash.ContainsKey(hash)) continue;                // 移动或重复，不算新文件
                output.Rows.Add(new ScanRow
                {
                    Category = ScanCategory.Unregistered,
                    FilePath = f.Path,
                    Hash = hash,
                    NewSize = f.Size,
                    NewMTime = AppSettings.MTimeStr(f.MTime),
                    Detail = "归档目录内存在未入库文件，可补入库",
                });
            }
            throttle.Flush();
            return output;
        }

        // ================= 全量扫描 =================

        public static ScanOutput ScanFull(IList<string> roots, IProgress<ScanProgress> progress, CancellationToken ct,
            out Dictionary<string, string> dirStates)
        {
            dirStates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var throttle = new ProgressThrottle(progress);
            var norm = NormalizeRoots(roots);

            var all = FileRepo.LoadAll();
            var byPath = new Dictionary<string, FileRecord>(StringComparer.OrdinalIgnoreCase);
            var byHash = new Dictionary<string, FileRecord>(StringComparer.Ordinal);
            foreach (var rec in all)
            {
                if (!string.IsNullOrEmpty(rec.CurrentPath)) byPath[rec.CurrentPath] = rec;
                byHash[rec.Hash] = rec;
            }

            // 1) 目录树 + 全部文件元数据一遍枚举（跨根去重，避免包含关系的根扫两遍）
            var entries = new List<FileEntry>();
            var dirEntries = new List<DirEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int pass = 0;
            foreach (var root in norm)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var d in WalkDirs(root, ct))
                {
                    if (dirStates.ContainsKey(d.Path)) continue;   // 多根包含时只记一次
                    dirStates[d.Path] = AppSettings.MTimeStr(d.MTime);
                    dirEntries.Add(d);
                    foreach (var f in ListFiles(d.Path, ct))
                        if (seen.Add(f.Path)) entries.Add(f);
                }
                throttle.Report(++pass, norm.Count, "正在枚举 " + root);
            }

            var output = new ScanOutput { TotalFiles = entries.Count };

            // 2) 找出需要算哈希的：未知路径、或大小/时间与库内不一致的
            var needHash = new List<FileEntry>();
            foreach (var f in entries)
            {
                FileRecord rec;
                if (!byPath.TryGetValue(f.Path, out rec)) { needHash.Add(f); continue; }
                if (f.Size == rec.FileSize && MTimeEqual(f.MTime, rec.FileMTime)) continue;
                needHash.Add(f);
            }

            // 3) 并行哈希
            var hashes = HashMany(needHash, progress, "正在核对内容", ct, output);

            // 4) 按磁盘顺序分类
            var matched = new HashSet<string>(StringComparer.Ordinal);     // 本次扫描确认仍在磁盘上的档案哈希
            var claimedMove = new HashSet<string>(StringComparer.Ordinal); // 同一哈希只允许认领一次"移动"
            var silentFix = new List<object[]>();                          // hash / size / mtime / status
            int done = 0;
            foreach (var f in entries)
            {
                ct.ThrowIfCancellationRequested();
                done++;
                throttle.Report(done, entries.Count, f.Path);

                FileRecord rec;
                byPath.TryGetValue(f.Path, out rec);

                if (rec != null)
                {
                    matched.Add(rec.Hash);
                    if (f.Size == rec.FileSize && MTimeEqual(f.MTime, rec.FileMTime))
                        continue; // 三要素一致 → 跳过哈希

                    string h;
                    if (!hashes.TryGetValue(f.Path, out h)) continue;  // 读取失败，留给下次扫描

                    if (h == rec.Hash)
                    {
                        // 内容没变，只是时间戳被动过（或丢失档案回归）→ 静默修正属性
                        silentFix.Add(new object[] { rec.Hash, f.Size, AppSettings.MTimeStr(f.MTime), 0 });
                        continue;
                    }
                    output.Rows.Add(new ScanRow
                    {
                        Category = ScanCategory.ContentChanged,
                        FilePath = f.Path,
                        Hash = h,
                        Detail = "位置未变但内容与库内记录不一致（库内 " + HashService.Short(rec.Hash) + "），请人工核对",
                    });
                    continue;
                }

                // 未知路径 → 算哈希判定身份：改名/移动？重复？未入库？
                string hash;
                if (!hashes.TryGetValue(f.Path, out hash)) continue;
                FileRecord orig;
                if (byHash.TryGetValue(hash, out orig))
                {
                    if (claimedMove.Contains(hash) || File.Exists(orig.CurrentPath))
                    {
                        output.Rows.Add(new ScanRow
                        {
                            Category = ScanCategory.Duplicate,
                            FilePath = f.Path,
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
                            FilePath = f.Path,
                            OldPath = orig.CurrentPath,
                            Hash = hash,
                            NewSize = f.Size,
                            NewMTime = AppSettings.MTimeStr(f.MTime),
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
                        FilePath = f.Path,
                        Hash = hash,
                        NewSize = f.Size,
                        NewMTime = AppSettings.MTimeStr(f.MTime),
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
            foreach (var x in silentFix)
                FileRepo.UpdateSilent((string)x[0], (long)x[1], (string)x[2], (int)x[3]);

            throttle.Flush();
            return output;
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

        // ================= 基础设施 =================

        static List<string> NormalizeRoots(IList<string> roots)
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
            return norm;
        }

        /// <summary>目录树（含根目录本身），只摸目录不摸文件。目录数通常远小于文件数。</summary>
        static List<DirEntry> WalkDirs(string root, CancellationToken ct)
        {
            var dirs = new List<DirEntry>();
            var stack = new Stack<DirectoryInfo>();
            stack.Push(new DirectoryInfo(root));
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var d = stack.Pop();
                DateTime mtime;
                try { mtime = d.LastWriteTime; }
                catch { continue; }
                dirs.Add(new DirEntry { Path = d.FullName, MTime = mtime });
                try
                {
                    foreach (var sub in d.EnumerateDirectories())
                    {
                        if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        stack.Push(sub);
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (DirectoryNotFoundException) { }
                catch (IOException) { }
            }
            return dirs;
        }

        /// <summary>单目录里的支持文件。EnumerateFileSystemInfos 的元数据随枚举填充，不再逐文件取大小/时间。</summary>
        static List<FileEntry> ListFiles(string dir, CancellationToken ct)
        {
            var list = new List<FileEntry>();
            try
            {
                var di = new DirectoryInfo(dir);
                foreach (var f in di.EnumerateFiles())
                {
                    ct.ThrowIfCancellationRequested();
                    if (!FileTypes.IsSupported(f.Name)) continue;
                    list.Add(new FileEntry { Path = f.FullName, Size = f.Length, MTime = f.LastWriteTime });
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (DirectoryNotFoundException) { }
            catch (IOException) { }
            return list;
        }

        /// <summary>并行算一批文件的 SHA-256（哈希对象每任务一个，不可共享）。单个文件失败只跳过不中断。</summary>
        static Dictionary<string, string> HashMany(List<FileEntry> items, IProgress<ScanProgress> progress,
            string label, CancellationToken ct, ScanOutput output)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (items.Count == 0) return map;

            var results = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int done = 0;
            var throttle = new ProgressThrottle(progress);
            int degree = Math.Max(2, Math.Min(8, Environment.ProcessorCount));

            try
            {
                Parallel.ForEach(items,
                    new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = degree },
                    delegate (FileEntry f)
                    {
                        try
                        {
                            results[f.Path] = HashService.Sha256(f.Path, null, ct);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (IOException) { Interlocked.Increment(ref output.FailedHashes); }
                        catch (UnauthorizedAccessException) { Interlocked.Increment(ref output.FailedHashes); }
                        var n = Interlocked.Increment(ref done);
                        throttle.Report(n, items.Count, label + "：" + f.Path);
                    });
            }
            catch (AggregateException ex)
            {
                // 取消或意外错误：保留已算出的部分，剩余条目按"没哈希"处理
                foreach (var e in ex.InnerExceptions)
                    if (!(e is OperationCanceledException)) throw e;
            }

            foreach (var kv in results) map[kv.Key] = kv.Value;
            output.HashedFiles = map.Count;
            return map;
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

        /// <summary>修改时间是否一致（±2 秒容差，兼容 FAT/网络盘的时间戳精度）。</summary>
        public static bool MTimeEqual(DateTime disk, string stored)
        {
            DateTime t;
            if (!DateTime.TryParseExact(stored, AppSettings.MTimeFmt,
                null, System.Globalization.DateTimeStyles.None, out t))
                return false;
            return Math.Abs((disk - t).TotalSeconds) <= 2.0;
        }

        /// <summary>进度节流：UI 每至多 200ms 收一条，扫描几万个文件不再轰炸消息队列。</summary>
        class ProgressThrottle
        {
            readonly IProgress<ScanProgress> _inner;
            readonly Stopwatch _sw = Stopwatch.StartNew();
            bool _force;   // Flush 后强制放行下一条
            public ProgressThrottle(IProgress<ScanProgress> inner) { _inner = inner; }

            public void Report(int done, int total, string current)
            {
                if (_inner == null) return;
                if (!_force && done < total && _sw.ElapsedMilliseconds < 200) return;
                _force = false;
                _sw.Restart();
                _inner.Report(new ScanProgress { Done = done, Total = total, Current = current ?? "" });
            }

            public void Flush() { _force = true; }
        }
    }
}
