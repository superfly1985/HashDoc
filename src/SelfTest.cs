using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace HashDoc
{
    /// <summary>无界面功能自检：HashDoc.exe --selftest。覆盖导入/查重/扫描/丢失/检索/补入库/备份/改名联动全链路。</summary>
    public static class SelfTest
    {
        public static int Run()
        {
            var lines = new List<string>();
            int fails = 0;
            string baseDir = Path.Combine(Path.GetTempPath(),
                "HashDocSelfTest_" + Guid.NewGuid().ToString("N").Substring(0, 8));

            void Check(string name, bool ok, string detail = null)
            {
                lines.Add((ok ? "[PASS] " : "[FAIL] ") + name +
                          (string.IsNullOrEmpty(detail) ? "" : "  -- " + detail));
                if (!ok) fails++;
            }

            try
            {
                Db.Open(Path.Combine(baseDir, "test.db"));
                string root = Path.Combine(baseDir, "archive");
                Directory.CreateDirectory(root);
                AppSettings.RootDir(root);
                DictRepo.SupplierAdd("测试供应商", "");

                string inbox = Path.Combine(baseDir, "inbox");
                Directory.CreateDirectory(inbox);
                string src = Path.Combine(inbox, "test.pdf");
                File.WriteAllBytes(src, MakeBytes(64 * 1024));
                string expectHash = HashService.Sha256(src);

                // 1. 入库
                var opt = new ImportOptions
                {
                    Supplier = "测试供应商",
                    DocType = "PPAP",
                    Codes = new List<CodeEntry>
                    {
                        new CodeEntry(CodeTypes.PartNo, "P001"),
                        new CodeEntry(CodeTypes.VwNo, "V001"),
                        new CodeEntry(CodeTypes.IdentNo, "I001"),
                    },
                };
                var r1 = ImportCore.ImportAsync(new List<string> { src }, opt, null, CancellationToken.None)
                                   .GetAwaiter().GetResult();
                string dest = Path.Combine(root, "测试供应商", "test.pdf");
                Check("入库成功", r1.Count == 1 && r1[0].Success, r1.Count == 1 ? r1[0].Message : "无结果");
                Check("归档文件已复制到 供应商 目录", File.Exists(dest));
                Check("SHA-256 计算一致", r1.Count == 1 && r1[0].Hash == expectHash);
                Check("库内档案记录存在", FileRepo.Exists(expectHash));
                Check("关联编号 3 条", FileRepo.GetByHash(expectHash).Codes.Count == 3);

                // 2. 同内容不同名再导入 → 重复跳过
                string src2 = Path.Combine(inbox, "test_副本.pdf");
                File.WriteAllBytes(src2, MakeBytes(64 * 1024));
                var r2 = ImportCore.ImportAsync(new List<string> { src2 }, opt, null, CancellationToken.None)
                                   .GetAwaiter().GetResult();
                Check("重复导入被跳过（复用原属性）", r2.Count == 1 && r2[0].Duplicate, r2.Count == 1 ? r2[0].Message : "");

                // 3. 扫描识别重复文件
                string dup = Path.Combine(root, "测试供应商", "test_copy.pdf");
                File.Copy(dest, dup, true);
                var s1 = ScanCore.ScanAsync(root, false, null, CancellationToken.None).GetAwaiter().GetResult();
                Check("扫描发现重复文件", s1.Rows.Exists(x => x.Category == ScanCategory.Duplicate &&
                    string.Equals(x.FilePath, dup, StringComparison.OrdinalIgnoreCase)));

                // 4. 改名 → 路径自动更新
                File.Delete(dup);
                string renamed = Path.Combine(root, "测试供应商", "renamed.pdf");
                File.Move(dest, renamed);
                var s2 = ScanCore.ScanAsync(root, true, null, CancellationToken.None).GetAwaiter().GetResult();
                Check("扫描识别路径变更", s2.Rows.Exists(x => x.Category == ScanCategory.PathChanged));
                Check("库内路径已自动更新", FileRepo.GetByHash(expectHash).CurrentPath == renamed);

                // 5. 删除文件 → 标记丢失，记录保留
                File.Delete(renamed);
                var s3 = ScanCore.ScanAsync(root, true, null, CancellationToken.None).GetAwaiter().GetResult();
                Check("扫描标记源文件丢失", s3.Rows.Exists(x => x.Category == ScanCategory.Lost));
                Check("库内状态=丢失且记录保留", FileRepo.GetByHash(expectHash).Status == 1);

                // 6. 检索
                var hit = FileRepo.Search(new SearchFilter { IdentNo = "I001" });
                Check("编号检索命中", hit.Count == 1 && hit[0].Hash == expectHash);
                var hitPart = FileRepo.Search(new SearchFilter { PartNo = "P0" });
                Check("零件号模糊检索命中", hitPart.Count == 1);
                var miss = FileRepo.Search(new SearchFilter { Name = "不存在" });
                Check("文件名检索无结果", miss.Count == 0);

                // 7. 未入库文件 + 补入库
                string adoptPath = Path.Combine(root, "adopt.pdf");
                File.WriteAllBytes(adoptPath, MakeBytes(128 * 1024));
                var s4 = ScanCore.ScanAsync(root, false, null, CancellationToken.None).GetAwaiter().GetResult();
                var unreg = s4.Rows.Find(x => x.Category == ScanCategory.Unregistered);
                Check("扫描发现未入库文件", unreg != null &&
                    string.Equals(unreg.FilePath, adoptPath, StringComparison.OrdinalIgnoreCase));
                if (unreg != null)
                {
                    AdoptCore.Import(unreg, "测试供应商", "BMG");
                    Check("补入库成功", FileRepo.Exists(unreg.Hash) &&
                        FileRepo.GetByHash(unreg.Hash).Supplier == "测试供应商");
                    Check("补入库重复登记被跳过", !AdoptCore.Import(unreg, "测试供应商", "BMG"));
                }

                // 8. 备份
                string bak = Path.Combine(baseDir, "bak.db");
                BackupTo(bak);
                Check("数据库备份", File.Exists(bak) && new FileInfo(bak).Length > 0);

                // 9. 供应商改名联动
                DictRepo.SupplierRename("测试供应商", "新供应商", "备注");
                Check("供应商改名联动档案", FileRepo.GetByHash(expectHash).Supplier == "新供应商");

                // 10. TIFF 白名单 + 文档类型规则 + 供应商种子抽取
                Check("TIFF 已加入白名单", FileTypes.IsSupported("x.tif") && FileTypes.IsSupported("x.TIFF"));
                Check("DocType规则 BMG", PathRules.InferDocType(@"S:\x\02 BMG\A\a.pdf") == "BMG");
                Check("DocType规则 PPAP", PathRules.InferDocType(@"S:\x\04 PPAP\b\a.pdf") == "PPAP");
                Check("DocType规则不误判(成都PPAP)", PathRules.InferDocType(@"S:\x\成都PPAP\a.pdf") == null);

                // 供应商种子：只认「目录标记」紧邻的下一层文件夹名
                string seedCsv = Path.Combine(root, "seed.csv");
                File.WriteAllLines(seedCsv, new[]
                {
                    "FileName,FullPath",
                    "\"a.pdf\",\"S:\\QA\\x\\02 BMG\\MD\\VDO\\BLBMG\\a.pdf\"",
                    "\"b.pdf\",\"S:\\QA\\x\\02 BMG\\MD\\VDO\\b.pdf\"",
                    "\"c.pdf\",\"S:\\QA\\x\\02 BMG\\GATES\\c.pdf\"",
                    "\"d.pdf\",\"S:\\QA\\x\\04 PPAP\\国内\\泰科\\d.pdf\"",
                    "\"e.pdf\",\"S:\\QA\\x\\02 BMG\\\"",
                    "\"f.pdf\",\"S:\\QA\\x\\02 BMG\\Thumbs.db\"",
                    "\"g.pdf\",\"S:\\QA\\x\\02 BMG\\模板.xlsx\"",
                    "\"h.pdf\",\"S:\\QA\\x\\02 BMG\\WUERTH-SH\"",
                }, new UTF8Encoding(true));
                var seeds = PathRules.ReadSupplierSeeds(seedCsv, "02 BMG", CancellationToken.None);
                Check("供应商种子只取紧邻下一层", seeds.Count == 3 &&
                    seeds.Exists(s => s.Name == "MD") && seeds.Exists(s => s.Name == "GATES"),
                    "count=" + seeds.Count);
                Check("供应商种子不漏到深层目录",
                    !seeds.Exists(s => s.Name == "VDO" || s.Name == "BLBMG" || s.Name == "泰科"));
                Check("供应商种子忽略散落文件",
                    !seeds.Exists(s => s.Name == "Thumbs.db" || s.Name == "模板.xlsx"));
                Check("供应商种子保留无扩展名的目录行", seeds.Exists(s => s.Name == "WUERTH-SH"));
                var seedMd = seeds.Find(s => s.Name == "MD");
                Check("供应商种子统计文件数与最短样例",
                    seedMd != null && seedMd.FileCount == 2 &&
                    seedMd.SamplePath.EndsWith(@"\b.pdf", StringComparison.OrdinalIgnoreCase),
                    seedMd == null ? "(null)" : seedMd.FileCount + " / " + seedMd.SamplePath);

                // 随动检索的候选来源：字典里已有的编码，按使用次数倒序
                var partCodes = FileRepo.DistinctCodes(CodeTypes.PartNo);
                Check("编号候选可用于随动检索", partCodes.Contains("P001"),
                    "count=" + partCodes.Count);
                Check("未知编码类型返回空候选", FileRepo.DistinctCodes("NotARealType").Count == 0);

                // 11. 随动检索的挂载不许抛：DropDownList 只能配 ListItems，且必须先设 Source 再设 Mode
                //     （顺序反了照样抛 NotSupportedException，补入库窗口曾因此在 Load 里中断、表格空白）
                try
                {
                    var cbList = new System.Windows.Forms.ComboBox
                    { DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList };
                    cbList.Items.AddRange(new object[] { "TE", "MD" });
                    UiUtil.AutoComplete(cbList, new[] { "TE" });
                    bool okList = cbList.AutoCompleteSource == System.Windows.Forms.AutoCompleteSource.ListItems &&
                                  cbList.AutoCompleteMode == System.Windows.Forms.AutoCompleteMode.Suggest;

                    var cbEdit = new System.Windows.Forms.ComboBox
                    { DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDown };
                    UiUtil.AutoComplete(cbEdit, new[] { "TE", "MD", "TE" });
                    bool okEdit = cbEdit.AutoCompleteSource == System.Windows.Forms.AutoCompleteSource.CustomSource &&
                                  cbEdit.AutoCompleteCustomSource.Count == 2;   // 重复值被去掉

                    Check("联想挂载不抛异常且模式正确", okList && okEdit,
                        "list=" + cbList.AutoCompleteSource + "/" + cbList.AutoCompleteMode +
                        " edit=" + cbEdit.AutoCompleteSource + "/" + cbEdit.AutoCompleteCustomSource.Count);
                    cbList.Dispose();
                    cbEdit.Dispose();
                }
                catch (Exception ex)
                {
                    Check("联想挂载不抛异常且模式正确", false, ex.GetType().Name + ": " + ex.Message);
                }

                // 12. 先入库后补信息：供应商留空也要能建档，并能在列表里被识别为「待补」
                string loosePath = Path.Combine(root, "空供应商入库.pdf");
                File.WriteAllBytes(loosePath, MakeBytes(2048));
                var loose = new ScanRow
                {
                    FilePath = loosePath,
                    Hash = HashService.Sha256(loosePath),
                    NewSize = 2048,
                    Category = ScanCategory.Unregistered,
                };
                AdoptCore.Import(loose, "", "BMG");
                var looseRec = FileRepo.GetByHash(loose.Hash);
                Check("空供应商也能入库", looseRec != null && looseRec.Supplier == "",
                    looseRec == null ? "(未写入)" : "supplier=[" + looseRec.Supplier + "]");
                Check("空供应商的档案判定为待补", looseRec != null && looseRec.NeedsInfo);
                Check("类型空也算待补", new FileRecord { Supplier = "TE", DocType = "" }.NeedsInfo);
                Check("缺BMG号也算待补", new FileRecord { Supplier = "TE", DocType = "BMG" }.NeedsInfo);
                Check("三项填全后不再算待补",
                    !new FileRecord { Supplier = "TE", DocType = "BMG", BmgNo = "B-1" }.NeedsInfo);

                // 13. BMG 目录默认供应商：下一层且在字典里才填，其它一律不猜
                var dictNames = new[] { "MD", "TE" };
                Check("BMG下一层且在字典内→默认供应商",
                    PathRules.InferSupplierFromBmg(@"S:\x\02 BMG\MD\VDO\BLBMG\a.pdf", dictNames) == "MD");
                Check("BMG下一层但字典外→不预填",
                    PathRules.InferSupplierFromBmg(@"S:\x\02 BMG\临时文件夹\a.pdf", dictNames) == null);
                Check("非BMG目录→不预填",
                    PathRules.InferSupplierFromBmg(@"S:\x\03 ELV\MD\a.pdf", dictNames) == null);

                // 14. BMG 号：可维护字段，随档案读写、进候选、参与待补判定
                var afterBmg = FileRepo.GetByHash(expectHash);
                FileRepo.UpdateAttrs(expectHash, afterBmg.Supplier, afterBmg.DocType, "BMG-TEST-001");
                Check("BMG号可保存并读回", FileRepo.GetByHash(expectHash).BmgNo == "BMG-TEST-001");
                Check("BMG号进入随动候选项", FileRepo.DistinctBmgNos().Contains("BMG-TEST-001"));

                // 15. 直接把文件拷进归档目录后，再走【添加文件】应"直接登记"，不能复制出 _1 副本
                string inside = Path.Combine(root, "测试供应商", "手工拷入.pdf");
                File.WriteAllBytes(inside, MakeBytes(4096));
                var rIn = ImportCore.ImportAsync(new List<string> { inside },
                    new ImportOptions { Supplier = "测试供应商", DocType = "BMG" }, null, CancellationToken.None)
                    .GetAwaiter().GetResult();
                Check("归档目录内的文件直接登记", rIn.Count == 1 && rIn[0].Success, rIn.Count == 1 ? rIn[0].Message : "");
                Check("不再复制出 _1 副本",
                    !File.Exists(Path.Combine(root, "测试供应商", "手工拷入_1.pdf")));
                Check("直接登记后路径保持原样",
                    FileRepo.GetByHash(HashService.Sha256(inside)).CurrentPath == inside);
            }
            catch (Exception ex)
            {
                lines.Add("[FATAL] " + ex);
                fails++;
            }
            finally
            {
                try { Directory.Delete(baseDir, true); } catch { }
            }

            lines.Add(fails == 0 ? "== ALL PASS ==" : "== FAILED: " + fails + " ==");
            string resultFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selftest_result.txt");
            File.WriteAllLines(resultFile, lines);
            Console.WriteLine(string.Join(Environment.NewLine, lines));
            return fails == 0 ? 0 : 1;
        }

        static void BackupTo(string file)
        {
            using (var dest = new SQLiteConnection("Data Source=" + file + ";Version=3;"))
            {
                dest.Open();
                Db.Conn.BackupDatabase(dest, "main", "main", -1, null, 0);
            }
        }

        static byte[] MakeBytes(int n)
        {
            var rnd = new Random(2026);
            var b = new byte[n];
            rnd.NextBytes(b);
            return b;
        }
    }
}
