using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace HashDoc
{
    public class HarvestItem
    {
        public string Name;
        public int FileCount;
        public string SamplePath = "";
    }

    /// <summary>
    /// 基于路径的辅助识别：只保留“文档类型规则”和“从文件清单抽取供应商候选”两项。
    /// 曾经的“在路径任意层级匹配供应商”已删除——BMG / ELV / PPAP 各层级不一致（ELV 下一层混着分类目录、
    /// PPAP 下一层是 PPAP(完整）/Supplier PPAP Report），靠路径猜供应商会命中分类目录，不可靠。
    /// 供应商改为由文件清单一一次性播种、人工勾选确认。
    /// </summary>
    public static class PathRules
    {
        // 路径段落 → 文档类型（数组顺序即优先级）
        static readonly string[][] DocTypeRules =
        {
            new[] { "02 BMG", "BMG" },
            new[] { "03 ELV", "ELV" },
            new[] { "04 PPAP", "PPAP" },
        };

        /// <summary>按路径段落推断文档类型（02 BMG / 03 ELV / 04 PPAP），未命中返回 null。</summary>
        public static string InferDocType(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return null;
            foreach (var seg in fullPath.Replace('/', '\\').Split('\\'))
                foreach (var rule in DocTypeRules)
                    if (string.Equals(seg, rule[0], StringComparison.OrdinalIgnoreCase))
                        return rule[1];
            return null;
        }

        /// <summary>
        /// 从文件清单（Excel 另存的 CSV，或每行一个路径的 TXT）抽取供应商候选：
        /// 取 <paramref name="anchor"/> 目录（默认 "02 BMG"）紧邻的下一层<b>文件夹</b>名。
        /// 直接散落在该目录下的文件（Thumbs.db、x模板.xlsx、new folder 1.zip）不算候选——它们下面没有分隔符。
        /// 只认这一条固定规则，不做“任意层级匹配”，也不做语义过滤；名字是否真是供应商由人工勾选确认。
        /// </summary>
        public static List<HarvestItem> ReadSupplierSeeds(string listFile, string anchor, CancellationToken ct)
        {
            var result = new Dictionary<string, HarvestItem>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(anchor)) anchor = "02 BMG";
            string key = "\\" + anchor.Trim().Trim('\\') + "\\";

            foreach (var path in ReadPathLines(listFile, ct))
            {
                int i = path.IndexOf(key, StringComparison.OrdinalIgnoreCase);
                if (i < 0) continue;
                string rest = path.Substring(i + key.Length);
                if (rest.Length == 0) continue;
                int sep = rest.IndexOf('\\');
                bool bare = sep < 0;   // 标记下面直接就是这一段，没有更深的层级
                string name = (bare ? rest : rest.Substring(0, sep)).Trim();
                if (name.Length == 0) continue;
                // 孤零零带扩展名的多半是文件本身（02 BMG\Thumbs.db、02 BMG\new folder 3.zip），不算供应商；
                // 不带扩展名的当目录行留下（清单里偶尔会导出目录行，如 ...\02 BMG\WUERTH-SH）
                if (bare && LooksLikeFile(name)) continue;

                HarvestItem it;
                if (!result.TryGetValue(name, out it))
                    result[name] = it = new HarvestItem { Name = name, SamplePath = path };
                else if (path.Length < it.SamplePath.Length) it.SamplePath = path;
                it.FileCount++;
            }

            var list = new List<HarvestItem>(result.Values);
            list.Sort((a, b) => b.FileCount.CompareTo(a.FileCount)); // 文件多的排前面，通常就是主供应商
            return list;
        }

        /// <summary>
        /// 只对 02 BMG 做一层供应商推断：取 02 BMG 紧邻的下一段，且仅当它就在供应商字典里才返回。
        /// 字典里没有的名字（分类目录、临时文件夹）一律返回 null，绝不把没确认过的名字写进档案。
        /// 其它类型目录层级不一致，不推断。
        /// </summary>
        public static string InferSupplierFromBmg(string fullPath, IEnumerable<string> knownSuppliers)
        {
            if (string.IsNullOrEmpty(fullPath) || knownSuppliers == null) return null;
            int i = fullPath.IndexOf("\\02 BMG\\", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            string rest = fullPath.Substring(i + "\\02 BMG\\".Length);
            int sep = rest.IndexOf('\\');
            string name = (sep >= 0 ? rest.Substring(0, sep) : rest).Trim();
            if (name.Length == 0) return null;
            foreach (var s in knownSuppliers)
                if (string.Equals(s, name, StringComparison.OrdinalIgnoreCase)) return s;
            return null;
        }

        /// <summary>孤立的这一段名字像不像文件：末段是 1~5 位字母数字扩展名就算（.db / .zip / .xlsx）。</summary>
        static bool LooksLikeFile(string name)
        {
            int dot = name.LastIndexOf('.');
            if (dot <= 0 || dot == name.Length - 1) return false;
            if (name.Length - dot - 1 > 5) return false;
            for (int i = dot + 1; i < name.Length; i++)
                if (!char.IsLetterOrDigit(name[i])) return false;
            return true;
        }

        /// <summary>逐行读出“像完整路径”的字段：CSV 取含盘符的列，纯文本则整行即路径。</summary>
        static IEnumerable<string> ReadPathLines(string listFile, CancellationToken ct)
        {
            using (var reader = new StreamReader(listFile, Encoding.UTF8, true))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    ct.ThrowIfCancellationRequested();
                    if (line.Length == 0) continue;
                    string hit = null;
                    foreach (var field in SplitCsvLine(line))
                    {
                        string v = field.Trim().Trim('"').Trim();
                        if (v.Length >= 3 && v[1] == ':' && (v[2] == '\\' || v[2] == '/'))
                            hit = v; // 取本行最后一个像路径的字段（通常就是 FullPath）
                    }
                    if (hit != null) yield return hit.Replace('/', '\\');
                }
            }
        }

        /// <summary>最小 CSV 拆分：支持双引号包裹的逗号与 "" 转义（Excel 导出的清单够用）。</summary>
        static List<string> SplitCsvLine(string line)
        {
            var fields = new List<string>();
            var sb = new StringBuilder();
            bool inQuote = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inQuote)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                        else inQuote = false;
                    }
                    else sb.Append(c);
                }
                else if (c == '"') inQuote = true;
                else if (c == ',') { fields.Add(sb.ToString()); sb.Length = 0; }
                else sb.Append(c);
            }
            fields.Add(sb.ToString());
            return fields;
        }
    }
}
