import json, subprocess, sys, os
E = r'C:\TEXTtoSQL\pendrive\TextToSQL\TextToSQL.exe'; D = 'C:\\TEXTtoSQL\\eval\\'
LAB = r'C:\Users\Nabil\Downloads\cse311_lab_06_database.sql'
sets = {'q10': LAB, 'lab6': LAB, 'company': D+'company.sql', 'hospital': D+'hospital.sql', 'library': D+'library.sql'}
pick = sys.argv[1:] or list(sets)
t = p = 0; secs = []
for n in pick:
    if n not in sets or not os.path.exists(sets[n]): continue
    q = D + ('q10.txt' if n == 'q10' else n+'_q.txt')
    a = [E, '--cli', sets[n], q, D+n+'.json'] + ([] if n == 'q10' else [D+n+'_gold.txt'])
    subprocess.run(a)
    try: d = json.load(open(D+n+'.json', encoding='utf8'))
    except Exception: print(n, open(D+n+'.json').read()[:800]); continue
    if d['warnings']: print('WARN', n, d['warnings'][:300])
    for x in d['answers']:
        secs.append(x['seconds'])
        if n == 'q10':
            if not x['ok'] or x['notes'] or x['tries'] > 1: print('\n@@', n, x['question'][:90], x['ok'], x['tries'], x['notes'], x['error'], '\n' + x['sql'])
            continue
        t += 1; p += x['verdict'] == 'PASS'
        if x['verdict'] != 'PASS': print('\n@@', n, x['question'], '|', x['verdict'], 'tries', x['tries'], x['notes'], x['error'][:150], '\n' + (x['sql'] or x['text']))
print(p, '/', t, 'mean s', round(sum(secs)/max(1, len(secs)), 1))
