using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HashDoc
{
    public class ImportOptions
    {
        public bool MoveSource;
        public string RootDir = "";     // 归档到哪个目录（空 = 默认目录）
        public string Supplier = "";
        public string DocType = "";
        public string BmgNo = "";
        public List<CodeEntry> Codes = new List<CodeEntry>();
    }

    public class ImportProgress
    {
        public int Done, Total;
        public string Current = "";
        public string Phase = "";
    }

    public class ImportResult
    {
        public string Source;
        public bool Success;      // 成功入库
        public bool Duplicate;    // 已归档过，跳过
        public string Message = "";
        public string Hash = "";
    }

    /// <summary>文件入库：算哈希 → 查重 → 复制/移动到 归档根目录\供应商\ → 写库。逐文件独立，单个失败不影响其余。</summary>
    public static class ImportCore
    {
        public static Task<List<ImportResult>> ImportAsync(List<string> files, ImportOptions opt,
            IProgress<ImportProgress> progress, CancellationToken ct)
        {
            return Task.Run(delegate
            {
                var results = new List<ImportResult>();
                int i = 0;
                foreach (var src in files)
                {
                    ct.ThrowIfCancellationRequested();
                    i++;
                    results.Add(ImportOne(src, opt, progress, i, files.Count, ct));
                }
                return results;
            }, ct);
        }

        static ImportResult ImportOne(string src, ImportOptions opt, IProgress<ImportProgress> progress,
            int idx, int total, CancellationToken ct)
        {
            var r = new ImportResult { Source = src };
            try
            {
                if (!FileTypes.IsSupported(src))
                {
                    r.Message = "不支持的文件类型，已跳过";
                    return r;
                }
                if (!File.Exists(src))
                {
                    r.Message = "源文件不存在";
                    return r;
                }

                // 1. 后台计算 SHA-256（文件唯一身份）
                Report(progress, idx, total, Path.GetFileName(src), "计算SHA-256");
                string hash = HashService.Sha256(src, null, ct);
                r.Hash = hash;

                // 2. 已归档 → 提示已归档，复用原有属性，不做任何改动
                if (FileRepo.Exists(hash))
                {
                    var old = FileRepo.GetByHash(hash);
                    r.Duplicate = true;
                    r.Message = "该文件已归档过（复用原有属性）：" + old.CurrentPath;
                    return r;
                }

                // 3. 源文件本来就在归档目录里（有人手工拷进去的）→ 直接登记建档，不再复制出一份 xxx_1 副本
                if (ArchiveRoots.IsUnderAny(src))
                {
                    var self = new FileInfo(src);
                    FileRepo.Insert(new FileRecord
                    {
                        Hash = hash,
                        CurrentPath = src,
                        FileName = self.Name,
                        FileSize = self.Length,
                        FileMTime = AppSettings.MTimeStr(self.LastWriteTime),
                        Supplier = opt.Supplier,
                        DocType = opt.DocType,
                        BmgNo = opt.BmgNo,
                        Status = 0,
                        ImportTime = AppSettings.NowStr(),
                        Codes = opt.Codes,
                    });
                    r.Success = true;
                    r.Message = "已在归档目录内，直接登记：" + src;
                    return r;
                }

                // 4. 复制/移动到 归档目录\供应商名称\，同名冲突自动加后缀，绝不覆盖
                string rootCfg = !string.IsNullOrEmpty(opt.RootDir) ? opt.RootDir : AppSettings.RootDir();
                if (string.IsNullOrWhiteSpace(rootCfg))
                {
                    r.Message = "请先在【设置】中添加归档目录";
                    return r;
                }
                string root = Path.GetFullPath(rootCfg);
                string dir = Path.Combine(root, SanitizeFolder(opt.Supplier));
                Directory.CreateDirectory(dir);
                string dest = UniqueDest(dir, Path.GetFileName(src));

                Report(progress, idx, total, Path.GetFileName(dest), opt.MoveSource ? "移动文件" : "复制文件");
                if (opt.MoveSource) File.Move(src, dest);
                else File.Copy(src, dest, false);
                var fi = new FileInfo(dest);

                // 5. 写库（档案 + 编号，同一事务）
                var rec = new FileRecord
                {
                    Hash = hash,
                    CurrentPath = dest,
                    FileName = Path.GetFileName(dest),
                    FileSize = fi.Length,
                    FileMTime = AppSettings.MTimeStr(fi.LastWriteTime),
                    Supplier = opt.Supplier,
                    DocType = opt.DocType,
                    BmgNo = opt.BmgNo,
                    Status = 0,
                    ImportTime = AppSettings.NowStr(),
                    Codes = opt.Codes,
                };
                FileRepo.Insert(rec);
                r.Success = true;
                r.Message = "已归档：" + dest;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { r.Message = "失败：" + ex.Message; }
            return r;
        }

        static string UniqueDest(string dir, string fileName)
        {
            string stem = Path.GetFileNameWithoutExtension(fileName);
            string ext = Path.GetExtension(fileName);
            string dest = Path.Combine(dir, fileName);
            int i = 1;
            while (File.Exists(dest))
            {
                dest = Path.Combine(dir, stem + "_" + i + ext);
                i++;
            }
            return dest;
        }

        internal static string SanitizeFolder(string name)
        {
            var sb = new StringBuilder(name);
            foreach (char c in Path.GetInvalidFileNameChars())
                sb.Replace(c, '_');
            string s = sb.ToString().TrimEnd('.', ' ');
            return string.IsNullOrEmpty(s) ? "_" : s;
        }

        static void Report(IProgress<ImportProgress> p, int done, int total, string current, string phase)
        {
            if (p != null) p.Report(new ImportProgress { Done = done, Total = total, Current = current, Phase = phase });
        }
    }

    /// <summary>扫描发现的“未入库文件”补入库：文件已在归档目录内，直接登记建档，不复制不移动。</summary>
    public static class AdoptCore
    {
        /// <summary>
        /// 补入库单条登记。返回 false = 库里已有同哈希档案（内容相同即同一份），跳过不写。
        /// 归档目录里常有一式多份的拷贝，不查重会撞 FileInfo.FileHash 主键直接弹“入库失败”。
        /// </summary>
        public static bool Import(ScanRow row, string supplier, string docType, string bmgNo = "")
        {
            // ScanRow.Hash 默认是空字符串而不是 null，用 ?? 会把空主键写进库，这里按“空就现算”处理
            string hash = string.IsNullOrEmpty(row.Hash) ? HashService.Sha256(row.FilePath) : row.Hash;
            if (FileRepo.Exists(hash)) return false;

            var fi = new FileInfo(row.FilePath);
            FileRepo.Insert(new FileRecord
            {
                Hash = hash,
                CurrentPath = row.FilePath,
                FileName = Path.GetFileName(row.FilePath),
                FileSize = fi.Exists ? fi.Length : row.NewSize,
                FileMTime = fi.Exists ? AppSettings.MTimeStr(fi.LastWriteTime) : (row.NewMTime ?? ""),
                Supplier = supplier,
                DocType = docType,
                BmgNo = bmgNo ?? "",
                Status = 0,
                ImportTime = AppSettings.NowStr(),
            });
            return true;
        }
    }
}
