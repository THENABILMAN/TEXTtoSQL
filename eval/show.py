import json,sys
d=json.load(open(sys.argv[1],encoding='utf8'));print(d['warnings'],len(d['schema']));
if len(sys.argv)>2: print(d['schema'])
for a in d['answers']:print('\n#',a['question'][:110],a['ok'],a['tries'],a['seconds'],a['notes'],a['error']);print(a['sql'] or a['text']);print(a['output'][:200])
