import csv, collections, sys
sys.stdout.reconfigure(encoding='utf-8')

SUP = {'.pdf', '.jpg', '.jpeg', '.png', '.bmp', '.tif', '.tiff'}

with open('file-list.csv', encoding='utf-8-sig', newline='') as f:
    rows = list(csv.reader(f))

files = [r[6].strip() for r in rows if len(r) >= 7 and r[5].strip() and r[5].strip() != 'FileName' and r[6].strip()]
files = [p for p in files if ('.' in p.rsplit('\\', 1)[-1]) and ('.' + p.rsplit('.', 1)[-1].lower()) in SUP]
print('支持格式的文件:', len(files))

# 目录树：每个目录的直接文件数（按支持格式）
direct = {}
for p in files:
    d = p.rsplit('\\', 1)[0]
    # 累加给所有祖先
    while d and d not in ('S:', 'S:\\'):
        direct[d] = direct.get(d, 0) + 1
        d = d.rsplit('\\', 1)[0]

# 所有目录
alldirs = set(direct.keys())
def dirname(p):
    return p.rstrip('\\').rsplit('\\', 1)[-1]

# 候选：非数字开头的目录名，聚合子树文件数
cand = collections.Counter()
sample = {}
for d, c in direct.items():
    n = dirname(d)
    if not n or n[0].isdigit():
        continue
    cand[n] += c
    if n not in sample or len(d) < len(sample[n]):
        sample[n] = d

cands_sorted = cand.most_common()
print('候选目录名总数:', len(cands_sorted))

# 模拟用户确认：只把“子树文件数 >= 5”的候选加入字典（合理的筛选线）
dictnames = {n for n, c in cands_sorted if c >= 5}
print('按“子树档案数>=5”模拟确认后的字典大小:', len(dictnames))
print('字典 TOP20:', [n for n, c in cands_sorted if n in dictnames][:20])

# 逐文件匹配：取最深的、与字典名完全相等的路径段
hit = 0
miss_samples = []
for p in files:
    segs = p.split('\\')
    m = None
    for seg in segs[:-1]:
        if seg in dictnames:
            m = seg
    if m:
        hit += 1
    elif len(miss_samples) < 8:
        miss_samples.append(p)

print('供应商可自动预填: %d / %d (%.1f%%)' % (hit, len(files), hit * 100.0 / len(files)))
print('--- 未命中示例 ---')
for p in miss_samples:
    print(p)
