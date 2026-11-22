import re,pathlib,collections,sys
T=r'[A-Za-z_][\w<>,\[\]\. ]*?\?'
cats={
 'return':re.compile(r'^\s*(?:public|internal|private|protected)(?:\s+(?:static|async|override|virtual|abstract|sealed|new))*\s+(?:Task<|ValueTask<)?'+T+r'>?\s+[A-Z]\w*\s*[(<]'),
 'optional-param':re.compile(T+r'\s+\w+\s*=\s*(?:null|default)\b'),
 'record-param':re.compile(r'\brecord\b.*\('),
 'field':re.compile(r'^\s*(?:private|internal|public|protected)(?:\s+(?:readonly|static|volatile))*\s+'+T+r'\s+_?\w+\s*(?:;|=)'),
 'property':re.compile(r'^\s*(?:public|internal)\s+(?:required\s+)?'+T+r'\s+[A-Z]\w*\s*\{'),
}
rows=collections.defaultdict(list)
for p in sorted(pathlib.Path('src').rglob('*.cs')):
  if '/obj/' in str(p) or '/bin/' in str(p): continue
  for i,l in enumerate(p.read_text().splitlines(),1):
    if l.strip().startswith('//'): continue
    for c,r in cats.items():
      if c=='record-param':
        if r.search(l) and re.search(r'\w\?\s+\w',l): rows[c].append((p,i,l.strip()))
      elif r.search(l): rows[c].append((p,i,l.strip())); break
proj=lambda p:p.parts[1]
print('| category | '+' | '.join(sorted({proj(p) for v in rows.values() for p,_,_ in v}))+' | total |')
ps=sorted({proj(p) for v in rows.values() for p,_,_ in v})
for c,v in rows.items():
  cnt=collections.Counter(proj(p) for p,_,_ in v)
  print(f'| {c} | '+' | '.join(str(cnt[x]) for x in ps)+f' | {len(v)} |')
with open(sys.argv[1],'w') as f:
  for c,v in rows.items():
    f.write(f'\n## {c} ({len(v)})\n\n')
    for p,i,l in v: f.write(f'- `{p}:{i}` `{l[:140]}`\n')
