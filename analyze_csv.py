import csv, collections, sys
sys.stdout.reconfigure(encoding='utf-8')

with open('file-list.csv', encoding='utf-8-sig', newline='') as f:
    rows = list(csv.reader(f))

files = [(r[5].strip(), r[6].strip()) for r in rows if len(r) >= 7 and r[5].strip() and r[5].strip() != 'FileName' and r[6].strip()]
dirs = [r[6].strip().rstrip('\\') for r in rows if len(r) >= 7 and not r[5].strip() and r[6].strip()]

# .db 文件都是什么名字
dbs = [p for n, p in files if p.lower().endswith('.db')]
dbnames = collections.Counter(p.rsplit('\\', 1)[-1].lower() for p in dbs)
print('.db 文件名分布:', dict(dbnames.most_common(5)))

# 1 SQ+IQC 下第4级目录（业务分区）
lvl4 = collections.Counter()
for p in dirs:
    parts = p.split('\\')
    if len(parts) >= 5:
        lvl4[parts[4]] += 1
print('--- 第4级目录(业务分区) ---')
for k, v in lvl4.most_common(20):
    print('%s: %d' % (k, v))

# 第6级目录（疑似供应商层）抽样
lvl6 = collections.Counter()
for p in dirs:
    parts = p.split('\\')
    if len(parts) >= 7:
        lvl6[parts[6]] += 1
print('--- 第6级目录 TOP25(疑似供应商/分类层) ---')
for k, v in lvl6.most_common(25):
    print('%s: %d' % (k, v))

# 文件名前后空格异常
ws = sum(1 for n, p in files if n != n.strip() or p.rstrip() != p or ' \\' in p or '\\ ' in p)
print('文件名/路径含多余空格的行:', ws)

# 同名不同目录（潜在重复）粗查：同一文件名出现次数
namec = collections.Counter(n.lower() for n, p in files)
dupnames = sum(1 for k, v in namec.items() if v > 1)
print('出现≥2次的文件名个数:', dupnames)

# 一级业务分区下的支持/不支持统计
sup = {'.pdf', '.jpg', '.jpeg', '.png', '.bmp'}
tif = 0
for n, p in files:
    e = p.rsplit('.', 1)[-1].lower() if '.' in p.rsplit('\\', 1)[-1] else ''
    if e in ('tif', 'tiff'):
        tif += 1
n_sup = sum(1 for n, p in files if ('.' in p.rsplit('\\', 1)[-1]) and ('.' + p.rsplit('.', 1)[-1].lower()) in sup)
print('支持格式: %d / %d (%.1f%%), 其中 TIFF 另有 %d 个' % (n_sup, len(files), n_sup * 100.0 / len(files), tif))

# 抽样看几条典型路径
print('--- 典型路径抽样 ---')
import random
random.seed(1)
for n, p in random.sample(files, 6):
    print(p)
