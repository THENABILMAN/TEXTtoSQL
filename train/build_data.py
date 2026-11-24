"""Builds train.jsonl / val.jsonl. Every SQL answer is executed on a private MariaDB first.
Prompts are laid out exactly like src/TextToSQL.cs. eval/ questions are never used."""
import json, random, re, subprocess, time, os, shutil, glob, math, collections
import pymysql, pandas as pd
R = random.Random(11)
ROOT = r'C:\TEXTtoSQL'; PEN = ROOT + r'\pendrive\TextToSQL'
N_SYN = int(os.environ.get('N_SYN', 4200)); N_PUB = int(os.environ.get('N_PUB', 4200)); BUDGET = 3200

# ---- prompt pieces taken from the app's source so they cannot drift apart
cs = open(ROOT + r'\src\TextToSQL.cs', encoding='utf-8').read()
block = re.search(r'const string Rules =(.*?);\s*\n\s*\n', cs, re.S).group(1)
RULES = ''.join(json.loads(m) for m in re.findall(r'"(?:[^"\\]|\\.)*"', block))
STOP = set(re.search(r'HashSet<string>\("([^"]+)"\.Split', cs).group(1).split())

def tokens(s):
    out = []
    for w in re.findall('[a-z]+', re.sub('([a-z])([A-Z])', r'\1 \2', s).lower()):
        if w in STOP: continue
        if len(w) > 5 and w.endswith('ies'): w = w[:-3] + 'y'
        elif len(w) > 5 and w.endswith('ing'): w = w[:-3]
        elif len(w) > 4 and w.endswith('ed'): w = w[:-2]
        elif len(w) > 3 and w.endswith('s') and not w.endswith('ss'): w = w[:-1]
        if len(w) > 4 and w.endswith('e'): w = w[:-1]
        out.append(w)
    return out

EX = []
for b in re.split(r'\n\s*\n', open(PEN + r'\knowledge\manual_examples.txt', encoding='utf-8').read()):
    L = [l for l in b.splitlines() if not l.startswith('//')]
    if not L or not L[0].startswith('Q: '): continue
    k = ' '.join(l[3:] for l in L if l.startswith('K: '))
    sql = '\n'.join(l for l in L[1:] if not l.startswith('K: ')).strip()
    if sql: EX.append((L[0][3:].strip(), sql, set(tokens(L[0][3:] + ' ' + k))))
df_ = collections.Counter(w for e in EX for w in e[2]); idf = {w: math.log(1 + len(EX) / c) for w, c in df_.items()}
def best(q, n):
    qt = set(tokens(q))
    s = sorted(((sum(idf[w] for w in qt & e[2]) / math.sqrt(4 + len(e[2])), i) for i, e in enumerate(EX)), reverse=True)[:n]
    return [EX[i] for sc, i in s if sc > 0][::-1]

# ---- private database
WORK = ROOT + r'\work\traindb'; PORT = 33911
shutil.rmtree(WORK, ignore_errors=True); shutil.copytree(PEN + r'\mariadb\template', WORK + r'\data')
srv = subprocess.Popen([PEN + r'\mariadb\bin\mysqld.exe', '--no-defaults', '--basedir=' + PEN + r'\mariadb', '--datadir=' + WORK + r'\data',
    '--bind-address=127.0.0.1', f'--port={PORT}', '--skip-name-resolve', '--innodb-flush-log-at-trx-commit=0', '--innodb-doublewrite=0',
    '--lower-case-table-names=1', '--secure-file-priv=' + WORK, '--character-set-server=utf8mb4',
    '--sql-mode=NO_ZERO_IN_DATE,NO_ZERO_DATE,NO_ENGINE_SUBSTITUTION', '--performance-schema=OFF', '--log-error=' + WORK + r'\db.log'],
    creationflags=0x08000000)
con = None
for _ in range(120):
    try:
        con = pymysql.connect(host='127.0.0.1', port=PORT, user='root', autocommit=True,
                              client_flag=pymysql.constants.CLIENT.MULTI_STATEMENTS, read_timeout=30)
        break
    except Exception: time.sleep(.5)
if con is None: srv.kill(); raise SystemExit('database did not start')

def run(sql):
    with con.cursor() as cur:
        cur.execute(sql); rows = list(cur.fetchall() or [])
        while cur.nextset(): rows += list(cur.fetchall() or [])
        return rows
def statements(sql):
    """splits DELIMITER scripts the way the mysql client does"""
    if 'DELIMITER' not in sql.upper(): return [sql]
    out, d, buf = [], ';', ''
    for l in sql.splitlines():
        m = re.match(r'\s*DELIMITER\s+(\S+)', l, re.I)
        if m: d = m.group(1); continue
        buf += l + '\n'
        if buf.rstrip().endswith(d): out.append(buf.rstrip()[:-len(d)]); buf = ''
    return out
def execute(sql):
    rows = []
    for s in statements(sql): rows = run(s)
    return rows
run("SET GLOBAL max_statement_time=5; SET SESSION max_statement_time=5")

def reset(ddl):
    run("DROP DATABASE IF EXISTS t2s; CREATE DATABASE t2s CHARACTER SET utf8mb4; USE t2s; SET FOREIGN_KEY_CHECKS=0")
    run(ddl); run("SET FOREIGN_KEY_CHECKS=1")

def schema_text():
    T = [t[0] for t in run("SELECT table_name FROM information_schema.tables WHERE table_schema='t2s' ORDER BY create_time, table_name")]
    C = {t: [] for t in T}
    for t, c, ty, k in run("SELECT table_name,column_name,column_type,column_key FROM information_schema.columns WHERE table_schema='t2s' ORDER BY table_name,ordinal_position"):
        if not ty.startswith('enum'): ty = re.sub(r'^(int|bigint|smallint|tinyint)\(\d+\)', r'\1', ty).upper()
        C[t].append(dict(n=c, ty=ty, pk=k == 'PRI', ref=None, s=None))
    for t, c, rt, rc in run("SELECT table_name,column_name,referenced_table_name,referenced_column_name FROM information_schema.key_column_usage WHERE table_schema='t2s' AND referenced_table_name IS NOT NULL"):
        for x in C[t]:
            if x['n'] == c: x['ref'] = rt + '.' + rc
    for t in T:
        for x in C[t]:
            if x['ref'] is None:
                for o in T:
                    pk = [y['n'] for y in C[o] if y['pk']]
                    if o == t or len(pk) != 1 or len(pk[0]) < 4: continue
                    a, b = x['n'].lower(), pk[0].lower()
                    if a == b and x['pk'] and sum(y['pk'] for y in C[t]) == 1: continue
                    if a == b or a.endswith('_' + b): x['guess'] = o + '.' + pk[0]
            if x['pk'] or x['ref'] or not re.search('CHAR|TEXT', x['ty'], re.I) or re.search('email|phone|address|comment|password|descr', x['n'], re.I): continue
            v = [str(r[0]) for r in run(f"SELECT DISTINCT `{x['n']}` FROM `{t}` WHERE `{x['n']}` IS NOT NULL LIMIT 9")]
            if v and all(len(z) <= 24 for z in v): x['s'] = v if len(v) <= 6 else v[:1]
    def col(x):
        ref = x['ref'] or x.get('guess')
        smp = ' [' + ', '.join("'" + z + "'" for z in x['s']) + (', ...' if len(x['s']) == 1 else '') + ']' if x['s'] else ''
        return x['n'] + ' ' + x['ty'] + (' PK' if x['pk'] else '') + (' -> ' + ref if ref else '') + smp
    return '\n'.join(t + '(' + ', '.join(col(x) for x in C[t]) + ')' for t in T), T, {x['n'].lower() for t in T for x in C[t]}

def disguise(sql, T, known):
    ids = re.findall(r'\b[A-Za-z]+_[A-Za-z]+\b', re.sub("'[^']*'", '', sql))
    if all(i.lower() in known for i in ids) and not re.search(r'\b(Fname|Lname|Dname|Dno|Essn)\b', sql): return sql
    for t in T: sql = re.sub(r"(?<![\w.'])" + re.escape(t) + r"(?![\w'])", 'sample_' + t.lower(), sql, flags=re.I)
    return sql
def prompt(db, schema, T, known, q):
    s = ("<|im_start|>system\n" + RULES + f"\nSchema of database {db} (PK = primary key, -> = foreign key, [..] = values in the data):\n"
         + schema + "<|im_end|>\n<|im_start|>user\n")
    ex = best(q, 3) if R.random() < .6 else []
    if ex:
        s += "Examples from the lab manual. They use a DIFFERENT database, so copy only the technique:\n"
        s += ''.join(f"Q: {a}\n{disguise(b, T, known)}\n\n" for a, b, _ in ex) + "Now answer this question using only the schema above:\n" + q
    else: s += "Answer this question using only the schema above:\n" + q
    return s + "<|im_end|>\n<|im_start|>assistant\n```sql\n"

# ---- held-out guard
heldout = [set(tokens(l)) for f in glob.glob(ROOT + r'\eval\*.txt') if f.endswith(('_q.txt', 'q10.txt'))
           for l in open(f, encoding='utf-8') if l.strip()]
def leaks(q):
    a = set(tokens(q)); return any(len(a & h) / max(1, len(a | h)) > .6 for h in heldout)

# ---- synthetic databases. None of them is a company, hospital, library or lab-06 schema.
# (parent, child, third, link, quantity); each entity = (name, category, values, number, date)
DOM = [
 ('university', ('Faculty','Building',['North','South','Annex'],'Budget','Founded_Date'), ('Student','Major',['Physics','CSE','Law','Arts'],'Credits','Admit_Date'), ('Club','Kind',['Sports','Music','Debate'],'Fee','Start_Date'), 'Membership','Points'),
 ('hotel', ('Hotel','City',['Dhaka','Sylhet','Khulna'],'Stars','Opened_Date'), ('Room','Room_Type',['Single','Double','Suite'],'Rent','Renovated_Date'), ('Guest','Country',['India','Nepal','Japan'],'Age','Birth_Date'), 'Booking','Nights'),
 ('shop', ('Supplier','Region',['East','West','Central'],'Rating','Joined_Date'), ('Product','Category',['Toy','Food','Tools'],'Price','Launch_Date'), ('Client','Segment',['Retail','Online'],'Balance','Signup_Date'), 'Sale','Quantity'),
 ('airline', ('Airport','Zone',['Asia','Europe','Africa'],'Runways','Opened_Date'), ('Flight','Status',['Open','Delayed','Closed'],'Fare','Flight_Date'), ('Passenger','Gender',['M','F'],'Miles','Birth_Date'), 'Ticket','Seats'),
 ('bank', ('Branch','Division',['Dhaka','Rajshahi','Barishal'],'Assets','Opened_Date'), ('Account','Account_Type',['Savings','Current','Student'],'Balance','Open_Date'), ('Holder','Occupation',['Teacher','Farmer','Doctor'],'Income','Birth_Date'), 'Ownership','Share'),
 ('league', ('Team','Home_City',['Comilla','Bogura','Jessore'],'Wins','Founded_Date'), ('Player','Position',['Keeper','Forward','Defender'],'Goals','Signed_Date'), ('Tournament','Level',['Local','National','World'],'Prize','Start_Date'), 'Entry','Minutes'),
 ('cinema', ('Studio','Country',['USA','Korea','India'],'Revenue','Founded_Date'), ('Movie','Style',['Drama','Action','Comedy'],'Runtime','Release_Date'), ('Viewer','Plan',['Free','Basic','Gold'],'Age','Joined_Date'), 'Watch','Rating'),
 ('farm', ('Farm','District',['Bogura','Pabna','Feni'],'Acres','Registered_Date'), ('Worker','Job',['Driver','Picker','Guard'],'Wage','Hire_Date'), ('Crop','Season',['Summer','Winter','Rainy'],'Yield_Kg','Planted_Date'), 'Task','Hours'),
 ('software', ('Division','Site',['Remote','HQ','Lab'],'Budget','Started_Date'), ('Engineer','Skill',['Java','Python','Cloud'],'Pay','Hired_Date'), ('Product','Stage',['Beta','Live','Retired'],'Cost','Release_Date'), 'Assignment','Hours'),
 ('travel', ('Agency','City',['Dhaka','Delhi','Dubai'],'Turnover','Founded_Date'), ('Guide','Language',['Bangla','English','French'],'Daily_Fee','Joined_Date'), ('Tour','Difficulty',['Easy','Medium','Hard'],'Cost','Start_Date'), 'Trip','Days'),
]
NAMES = ['Amin','Bella','Chowdhury','Dipu','Esha','Farhan','Gita','Hasan','Ivy','Jamil','Kona','Liton','Mina','Nadia','Omar','Putul']
TITLES = ['Alpha','Beacon','Cedar','Delta','Echo','Falcon','Garnet','Harbor','Iris','Jade','Koral','Lotus','Meghna','Nova','Orion','Padma']
def style(n, st):
    p = n.split('_')
    return {'T': n, 's': n.lower(), 'C': ''.join(p), 'c': p[0].lower() + ''.join(p[1:])}[st]
def make():
    name, A, B, C, L, qty = R.choice(DOM); st = R.choice('TTssCc'); S = lambda n: style(n, st)
    tn = (lambda n: n.lower()) if st in 'sc' else (lambda n: n)
    d = dict(db=name + R.choice(['_db', '', 'db', '_lab']), A=A[0], B=B[0], C=C[0], L=L, st=st, lo=R.choice([10, 100, 1000, 10000]))
    d.update(ak=S(A[0]+'_ID'), an=S(A[0]+'_Name'), ac=S(A[1]), av=A[2], anum=S(A[3]), ad=S(A[4]),
             bk=S(B[0]+'_ID'), bn=S(B[0]+'_Name'), bc=S(B[1]), bv=B[2], bnum=S(B[3]), bd=S(B[4]),
             ck=S(C[0]+'_ID'), cn=S(C[0]+'_Name'), cc=S(C[1]), cv=C[2], cnum=S(C[3]), cd=S(C[4]),
             q=S(qty), bend=S('End_Date'), addr=S('Address'), mail=S('Email'),
             fk=S(A[0][:1]+'no') if R.random() < .35 else S(A[0]+'_ID'),     # sometimes named unlike the key, as Dno -> Dnumber
             lead=S(R.choice(['Head_ID','Lead_ID','Manager_ID','Chief_ID'])), boss=S(R.choice(['Mentor_ID','Senior_ID','Leader_ID','Super_ID'])),
             lb=S(B[0]+'_ID'), lc=S(C[0]+'_ID'), TA=tn(A[0]), TB=tn(B[0]), TC=tn(C[0]), TL=tn(L))
    g = d; lo = d['lo']; na, nb, nc = R.randint(5, 7), R.randint(10, 16), R.randint(5, 8)
    ta, tb, tc = R.sample(TITLES, na), R.sample(NAMES, nb), R.sample(TITLES, nc); d.update(ta=ta, tb=tb, tc=tc)
    dt = lambda: f"'{R.randint(2018, 2025)}-{R.randint(1, 12):02}-{R.randint(1, 28):02}'"
    val = lambda: R.randint(1, 9) * lo + R.choice([0, 0, lo // 2])
    ddl = f"""CREATE TABLE {g['TA']} ({g['ak']} INT PRIMARY KEY, {g['an']} VARCHAR(50), {g['ac']} VARCHAR(30), {g['anum']} DECIMAL(12,2), {g['ad']} DATE, {g['lead']} INT);
CREATE TABLE {g['TB']} ({g['bk']} INT PRIMARY KEY, {g['bn']} VARCHAR(50), {g['bc']} VARCHAR(30), {g['bnum']} DECIMAL(12,2), {g['bd']} DATE, {g['bend']} DATE, {g['addr']} VARCHAR(80), {g['mail']} VARCHAR(80), {g['fk']} INT, {g['boss']} INT, FOREIGN KEY ({g['fk']}) REFERENCES {g['TA']}({g['ak']}), FOREIGN KEY ({g['boss']}) REFERENCES {g['TB']}({g['bk']}));
CREATE TABLE {g['TC']} ({g['ck']} INT PRIMARY KEY, {g['cn']} VARCHAR(50), {g['cc']} VARCHAR(30), {g['cnum']} DECIMAL(12,2), {g['cd']} DATE);
CREATE TABLE {g['TL']} ({g['lb']} INT, {g['lc']} INT, {g['q']} INT, PRIMARY KEY ({g['lb']},{g['lc']}), FOREIGN KEY ({g['lb']}) REFERENCES {g['TB']}({g['bk']}), FOREIGN KEY ({g['lc']}) REFERENCES {g['TC']}({g['ck']}));
ALTER TABLE {g['TA']} ADD FOREIGN KEY ({g['lead']}) REFERENCES {g['TB']}({g['bk']});
"""
    for i in range(na):
        ddl += f"INSERT INTO {g['TA']} VALUES ({i+1},'{ta[i]}','{R.choice(A[2])}',{val()},{dt()},{R.choice([R.randint(1,nb), R.randint(1,nb), 'NULL'])});\n"
    for i in range(nb):
        boss = R.choice([R.randint(1, 3), 'NULL']) if i > 2 else 'NULL'
        ddl += (f"INSERT INTO {g['TB']} VALUES ({i+1},'{tb[i]}','{R.choice(B[2])}',{val()},{dt()},{R.choice(['NULL', dt()])},'{R.randint(1,99)} Lake Road, "
                f"{R.choice(['Dhaka','Sylhet','Pabna'])}','{tb[i].lower()}@{R.choice(['gmail.com','yahoo.com'])}',{R.choice([R.randint(1,na-1)]*4+['NULL'])},{boss});\n")
    for i in range(nc):
        ddl += f"INSERT INTO {g['TC']} VALUES ({i+1},'{tc[i]}',{R.choice([repr(R.choice(C[2]))]*5+['NULL'])},{val()},{dt()});\n"
    for a, b in sorted({(R.randint(1, nb-2), R.randint(1, nc-1)) for _ in range(22)}):
        ddl += f"INSERT INTO {g['TL']} VALUES ({a},{b},{R.randint(1,20)});\n"
    d['ddl'] = ddl; return d

P = lambda *a: R.choice(a)
def hum(x): return re.sub(r'([a-z])([A-Z])', r'\1 \2', x).replace('_', ' ').lower()
def pl(w):
    w = w.lower()
    return w[:-1] + 'ies' if w.endswith('y') and w[-2] not in 'aeiou' else w + ('es' if w.endswith(('ch', 'sh', 's')) else 's')
def qs(g):
    N = lambda c: c if R.random() < .55 else hum(c)              # exact column name or plain English
    A, B, C = g['A'].lower(), g['B'].lower(), g['C'].lower(); As, Bs, Cs = pl(A), pl(B), pl(C); lo = g['lo']
    TA, TB, TC, TL = g['TA'], g['TB'], g['TC'], g['TL']; L = g['L'].lower()
    ak, an, bk, bn, bc, bnum, bd, bend, ck, cn, cc, cnum, qn, fk, lead, boss, lb, lc, addr, mail = (g[x] for x in 'ak an bk bn bc bnum bd bend ck cn cc cnum q fk lead boss lb lc addr mail'.split())
    k = R.randint(2, 7) * lo; ks = f"{k:,}" if R.random() < .5 else str(k); yr = R.randint(2018, 2025)
    v, cv, ttl, cm, nm = R.choice(g['bv']), R.choice(g['cv']), R.choice(g['ta']), R.choice(g['tc']), R.choice(g['tb'])
    pct = R.choice([5, 10, 15, 20, 25]); amt = R.choice([5, 10, 20, 50, 100]); up = f"{1+pct/100:.2f}"; dn = f"{1-pct/100:.2f}"
    op, w = R.choice([('>', 'more than'), ('>=', 'at least'), ('<', 'less than'), ('<=', 'at most')])
    al = P('Total', 'Grand_Total', 'Sum_Value'); cnt = P('Total_Count', 'Headcount', 'Num', 'How_Many'); n = R.randint(2, 5)
    W = lambda: P('Write a SQL query to display ', 'Show ', 'List ', 'Display ', 'Find ', 'Retrieve ')
    hd, bs = (hum(re.sub(r'_?ID$', '', z, flags=re.I)) for z in (lead, boss)); i1, i2 = R.sample(range(1, 5), 2)
    so = P('EXCEPT', 'INTERSECT', 'UNION'); ph = {'EXCEPT': 'but not', 'INTERSECT': 'and also', 'UNION': 'or'}[so]
    f1, f2, f3, f4 = (R.random() < .5 for _ in range(4)); x = []
    AB = f"FROM {TB} b\nJOIN {TA} a ON b.{fk} = a.{ak}"; BLC = f"FROM {TB} b\nJOIN {TL} l ON b.{bk} = l.{lb}\nJOIN {TC} c ON l.{lc} = c.{ck}"
    CL = f"FROM {TC} c\nJOIN {TL} l ON c.{ck} = l.{lc}\nGROUP BY c.{ck}, c.{cn}"
    def add(q, s, kind='r'): x.append((q, s.strip() if 'DELIMITER' in s else s.strip() + ';', kind))
    add(f"{W()}the {N(bn)} and {N(bnum)} of {Bs} whose {N(bc)} is '{v}'" + (f", sorted by {N(bnum)} descending." if f1 else '.'), f"SELECT {bn}, {bnum}\nFROM {TB}\nWHERE {bc} = '{v}'" + (f"\nORDER BY {bnum} DESC" if f1 else ''))
    add(f"{W()}{Bs} with a {N(bnum)} {w} {ks}. Show {N(bn)} only.", f"SELECT {bn}\nFROM {TB}\nWHERE {bnum} {op} {k}")
    add(f"{W()}all {Bs} whose {N(addr)} contains 'Sylhet'. Show the {N(bn)}.", f"SELECT {bn}\nFROM {TB}\nWHERE {addr} LIKE '%Sylhet%'")
    add(f"{W()}the {N(bn)} of {Bs} with a yahoo {N(mail)} whose {N(bd)} is in {yr}.", f"SELECT {bn}\nFROM {TB}\nWHERE {mail} LIKE '%yahoo%'\n  AND YEAR({bd}) = {yr}")
    add(f"{W()}{N(bn)}, {N(bd)} and {N(bend)} of {Bs} with no {N(bend)}, " + ('newest first.' if f1 else 'earliest first.'), f"SELECT {bn}, {bd}, {bend}\nFROM {TB}\nWHERE {bend} IS NULL\nORDER BY {bd} " + ('DESC' if f1 else 'ASC'))
    add(f"{W()}the {N(bn)} of every {B} and the name of its {A}, {P('latest', 'most recent')} {N(bd)} first.", f"SELECT b.{bn}, a.{an}\n{AB}\nORDER BY b.{bd} DESC")
    add(f"{W()}the {N(bn)} of all {Bs} that belong to the {A} '{ttl}'.", f"SELECT b.{bn}\n{AB}\nWHERE a.{an} = '{ttl}'")
    add(f"{W()}each {A}'s name and the name of its {hd}. " + P(f"Include {As} without a {hd}.", f"Keep {As} that have no {hd}."), f"SELECT a.{an}, b.{bn}\nFROM {TA} a\nLEFT JOIN {TB} b ON a.{lead} = b.{bk}")
    add(f"{W()}the name of each {B} and the name of their {bs} (aliased as Guide_Name). " + P(f"Include {Bs} without a {bs}.", f"{Bs.capitalize()} with no {bs} must also appear."), f"SELECT b.{bn}, m.{bn} AS Guide_Name\nFROM {TB} b\nLEFT JOIN {TB} m ON b.{boss} = m.{bk}")
    add(f"{W()}the {N(bn)} of {Bs} that have a higher {N(bnum)} than their {bs}.", f"SELECT b.{bn}\nFROM {TB} b\nJOIN {TB} m ON b.{boss} = m.{bk}\nWHERE b.{bnum} > m.{bnum}")
    add(f"Which {Bs} act as {bs} for at least 2 others? Show the {bs}'s {N(bn)} and the number of people under them.", f"SELECT m.{bn}, COUNT(*) AS Num\nFROM {TB} b\nJOIN {TB} m ON b.{boss} = m.{bk}\nGROUP BY m.{bk}, m.{bn}\nHAVING COUNT(*) >= 2")
    add(f"{W()}the names of {Bs} that are the {hd} of some {A} and have at least one {L}.", f"SELECT DISTINCT b.{bn}\nFROM {TB} b\nJOIN {TA} a ON a.{lead} = b.{bk}\nJOIN {TL} l ON l.{lb} = b.{bk}")
    add(f"{W()}{Bs} that have no {L}. Show the {N(bn)}.", f"SELECT b.{bn}\nFROM {TB} b\nWHERE NOT EXISTS (SELECT 1 FROM {TL} l WHERE l.{lb} = b.{bk})")
    add(f"Which {As} have no {Bs} at all? Show {N(an)}.", f"SELECT a.{an}\nFROM {TA} a\nLEFT JOIN {TB} b ON b.{fk} = a.{ak}\nWHERE b.{bk} IS NULL")
    add(f"Which {As} have no {Bs} with {N(bc)} '{v}'? Show {N(an)}.", f"SELECT a.{an}\nFROM {TA} a\nWHERE NOT EXISTS (\n  SELECT 1 FROM {TB} b\n  WHERE b.{fk} = a.{ak} AND b.{bc} = '{v}'\n)")
    add(f"{W()}the {N(bn)} of each {B} and the {N(cn)} of every {C} they are linked to" + (f" with a {N(qn)} above 5." if f2 else '.'), f"SELECT b.{bn}, c.{cn}\n{BLC}" + (f"\nWHERE l.{qn} > 5" if f2 else ''))
    add(f"{W()}the names of {Bs} linked to the {C} named '{cm}'.", f"SELECT DISTINCT b.{bn}\n{BLC}\nWHERE c.{cn} = '{cm}'")
    add(f"{W()}each {C}'s {N(cn)} with its total {N(qn)} (aliased as {al}). Keep only totals of {w} 15.", f"SELECT c.{cn}, SUM(l.{qn}) AS {al}\n{CL}\nHAVING SUM(l.{qn}) {op} 15")
    add(f"{W()}every {A} with the total {N(qn)} of its {Bs}, shown as {al}.", f"SELECT a.{an}, SUM(l.{qn}) AS {al}\nFROM {TA} a\nJOIN {TB} b ON b.{fk} = a.{ak}\nJOIN {TL} l ON l.{lb} = b.{bk}\nGROUP BY a.{ak}, a.{an}")
    add(f"{W()}every {A} with the number of {Bs} it has (aliased as {cnt}), including {As} with none.", f"SELECT a.{an}, COUNT(b.{bk}) AS {cnt}\nFROM {TA} a\nLEFT JOIN {TB} b ON b.{fk} = a.{ak}\nGROUP BY a.{ak}, a.{an}")
    add(f"{W()}each {N(bc)} with the number of {Bs} (aliased as {cnt}) and the average {N(bnum)} (aliased as Mean_Value). Keep groups with at least 3 {Bs} and sort by {cnt} descending.", f"SELECT {bc}, COUNT(*) AS {cnt}, AVG({bnum}) AS Mean_Value\nFROM {TB}\nGROUP BY {bc}\nHAVING COUNT(*) >= 3\nORDER BY {cnt} DESC")
    add(f"What are the smallest, largest, average and total {N(bnum)} of {Bs} in the {A} '{ttl}'?", f"SELECT MIN(b.{bnum}), MAX(b.{bnum}), AVG(b.{bnum}), SUM(b.{bnum})\n{AB}\nWHERE a.{an} = '{ttl}'")
    add(f"{W()}{Bs} whose {N(bnum)} is above the average of their own {A}. Show {N(bn)} and {N(bnum)}.", f"SELECT b.{bn}, b.{bnum}\nFROM {TB} b\nWHERE b.{bnum} > (SELECT AVG(x.{bnum}) FROM {TB} x WHERE x.{fk} = b.{fk})")
    add(f"{W()}{Bs} whose {N(bnum)} is greater than that of all {Bs} in {A} number 2. Show {N(bn)}.", f"SELECT {bn}\nFROM {TB}\nWHERE {bnum} > ALL (SELECT {bnum} FROM {TB} WHERE {fk} = 2)")
    add(f"{W()}the top {n} {Cs} by total {N(qn)}. Show {N(cn)} and the total as {al}.", f"SELECT c.{cn}, SUM(l.{qn}) AS {al}\n{CL}\nORDER BY {al} DESC\nLIMIT {n}")
    add(f"{W()}the {C} with the {P('second', '2nd')} largest {N(cnum)} value. Show {N(cn)} and {N(cnum)}.", f"SELECT {cn}, {cnum}\nFROM {TC}\nWHERE {cnum} = (\n  SELECT DISTINCT {cnum} FROM {TC} ORDER BY {cnum} DESC LIMIT 1 OFFSET 1\n)")
    add(f"For each {A}, show the {B} with the largest {N(bnum)} ({an}, {bn}, {bnum}).", f"SELECT a.{an}, b.{bn}, b.{bnum}\n{AB}\nWHERE b.{bnum} = (SELECT MAX(x.{bnum}) FROM {TB} x WHERE x.{fk} = b.{fk})")
    add(f"{W()}the {lb} values linked to {C} {i1} {ph} to {C} {i2}, using {so}.", f"SELECT {lb} FROM {TL} WHERE {lc} = {i1}\n{so}\nSELECT {lb} FROM {TL} WHERE {lc} = {i2}")
    add(f"Use UNION to list all {N(an)} values and all {N(cn)} values in one column named All_Names, sorted alphabetically.", f"SELECT {an} AS All_Names FROM {TA}\nUNION\nSELECT {cn} FROM {TC}\nORDER BY All_Names")
    add(f"Write a SQL query using a RIGHT JOIN between {TB} and {TA} ({TA} on the right side) to show {an} and {bn}. All {As} must appear. Sort by {an}.", f"SELECT a.{an}, b.{bn}\nFROM {TB} b\nRIGHT JOIN {TA} a ON b.{fk} = a.{ak}\nORDER BY a.{an} ASC")
    add(f"Emulate a FULL OUTER JOIN of {TB} and {TA} showing {bn} and {an}.", f"SELECT b.{bn}, a.{an}\nFROM {TB} b\nLEFT JOIN {TA} a ON b.{fk} = a.{ak}\nUNION\nSELECT b.{bn}, a.{an}\nFROM {TB} b\nRIGHT JOIN {TA} a ON b.{fk} = a.{ak}")
    add(f"{W()}{N(bn)} and a column Tier that is 'Small' when {N(bnum)} is under {k}, 'Middle' when it is from {k} to {k+2*lo} and 'Large' otherwise.", f"SELECT {bn},\n       CASE WHEN {bnum} < {k} THEN 'Small'\n            WHEN {bnum} BETWEEN {k} AND {k+2*lo} THEN 'Middle'\n            ELSE 'Large' END AS Tier\nFROM {TB}")
    add(f"Count the {Bs} per month of {N(bd)} in {yr}.", f"SELECT MONTH({bd}) AS Month_No, COUNT(*) AS Num\nFROM {TB}\nWHERE YEAR({bd}) = {yr}\nGROUP BY MONTH({bd})")
    add(f"{W()}all details of {Cs} whose {N(cc)} is {P('missing', 'unknown', 'empty (NULL)')}.", f"SELECT *\nFROM {TC}\nWHERE {cc} IS NULL")
    # changes to data and structure
    add(P(f"Give all {Bs} of the {A} '{ttl}' a {pct} percent increase in {N(bnum)}.", f"Raise the {N(bnum)} of {Bs} in the {A} '{ttl}' by {pct}%."), f"UPDATE {TB}\nSET {bnum} = {bnum} * {up}\nWHERE {fk} = (SELECT {ak} FROM {TA} WHERE {an} = '{ttl}')", 'w')
    add(f"{P('Increase','Raise')} the {N(bnum)} of {Bs} with {N(bc)} '{v}' by {pct}{P('%',' percent')}.", f"UPDATE {TB}\nSET {bnum} = {bnum} * {up}\nWHERE {bc} = '{v}'", 'w')
    add(f"{P('Reduce','Decrease','Lower','Cut')} the {N(cnum)} of all {Cs} with {N(cnum)} {w} {ks} by {amt}.", f"UPDATE {TC}\nSET {cnum} = {cnum} - {amt}\nWHERE {cnum} {op} {k}", 'w')
    add(f"{P('Reduce','Decrease','Cut')} the {N(cnum)} of every {C} by {pct}%.", f"UPDATE {TC}\nSET {cnum} = {cnum} * {dn}", 'w')
    add(f"{P('Increase','Raise')} the {N(cnum)} of {Cs} with {N(cc)} '{cv}' by {amt}.", f"UPDATE {TC}\nSET {cnum} = {cnum} + {amt}\nWHERE {cc} = '{cv}'", 'w')
    add(f"{P('Delete','Remove')} all {L} records whose {N(qn)} is {w} 8.", f"DELETE FROM {TL}\nWHERE {qn} {op} 8", 'w')
    add(f"{P('Delete','Remove')} every {L} of the {B} named '{nm}'.", f"DELETE FROM {TL}\nWHERE {lb} IN (SELECT {bk} FROM {TB} WHERE {bn} = '{nm}')", 'w')
    add(f"Insert a new {C} with id 90, name 'Sample Entry', {N(cc)} '{cv}', {N(cnum)} {k} and {N(g['cd'])} {yr}-05-04.", f"INSERT INTO {TC} ({ck}, {cn}, {cc}, {cnum}, {g['cd']})\nVALUES (90, 'Sample Entry', '{cv}', {k}, '{yr}-05-04')", 'w')
    nc = style(P('Backup_Phone', 'Nick_Name', 'Web_Link', 'Blood_Type'), g['st']); ln = P(15, 20, 40, 100)
    add(P(f"Add a column named {nc} of type VARCHAR({ln}) to the {TB} table.", f"Write a SQL command to modify {TB} by adding a new column {nc} with data type VARCHAR({ln})."), f"ALTER TABLE {TB}\nADD {nc} VARCHAR({ln})", 'w')
    add(f"Change the data type of {cn} in {TC} to VARCHAR({ln+60}).", f"ALTER TABLE {TC}\nMODIFY COLUMN {cn} VARCHAR({ln+60})", 'w')
    add(f"Remove the column {mail} from {TB}.", f"ALTER TABLE {TB}\nDROP COLUMN {mail}", 'w')
    add(f"Create {'a unique index' if f3 else 'an index'} named idx_{bn.lower()} on {bn} of {TB}.", f"CREATE {'UNIQUE ' if f3 else ''}INDEX idx_{bn.lower()}\nON {TB} ({bn})", 'w')
    add(f"Create a view named {g['A']}_Stats that shows each {A} name, its number of {Bs} and their average {N(bnum)}.", f"CREATE VIEW {g['A']}_Stats AS\nSELECT a.{an}, COUNT(b.{bk}) AS Total, AVG(b.{bnum}) AS Average\nFROM {TA} a\nLEFT JOIN {TB} b ON b.{fk} = a.{ak}\nGROUP BY a.{ak}, a.{an}", 'w')
    add(f"Create a table Log_Entry with Log_ID as auto increment primary key, {bk} as a foreign key to {TB} and Amount DECIMAL(10,2) not null.", f"CREATE TABLE Log_Entry (\n  Log_ID INT AUTO_INCREMENT PRIMARY KEY,\n  {bk} INT,\n  Amount DECIMAL(10,2) NOT NULL,\n  FOREIGN KEY ({bk}) REFERENCES {TB}({bk})\n)", 'w')
    pn = f"Get{g['B']}By{bc.replace('_', '')}"
    add(f"Create a stored procedure {pn} that takes a {N(bc)} as IN parameter and lists the matching {Bs}.", f"DELIMITER $$\nCREATE PROCEDURE {pn}(IN p_value VARCHAR(30))\nBEGIN\n  SELECT * FROM {TB} WHERE {bc} = p_value;\nEND $$\nDELIMITER ;", 'w')
    add(f"Write a stored function Yearly{g['B']} that takes a {B} id and returns its {N(bnum)} times 12.", f"DELIMITER $$\nCREATE FUNCTION Yearly{g['B']}(p_id INT)\nRETURNS DECIMAL(14,2)\nDETERMINISTIC\nREADS SQL DATA\nBEGIN\n  DECLARE v_total DECIMAL(14,2);\n  SELECT {bnum} * 12 INTO v_total FROM {TB} WHERE {bk} = p_id;\n  RETURN v_total;\nEND $$\nDELIMITER ;", 'w')
    add(f"Create a trigger trg_{TL.lower()} that adds the {N(qn)} to the {N(cnum)} of the {C} after a row is inserted into {TL}.", f"DELIMITER $$\nCREATE TRIGGER trg_{TL.lower()}\nAFTER INSERT ON {TL}\nFOR EACH ROW\nBEGIN\n  UPDATE {TC}\n  SET {cnum} = {cnum} + NEW.{qn}\n  WHERE {ck} = NEW.{lc};\nEND $$\nDELIMITER ;", 'w')
    add(f"Create a trigger that blocks inserting a {B} with a negative {N(bnum)}.", f"DELIMITER $$\nCREATE TRIGGER trg_check_{TB.lower()}\nBEFORE INSERT ON {TB}\nFOR EACH ROW\nBEGIN\n  IF NEW.{bnum} < 0 THEN\n    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Value cannot be negative';\n  END IF;\nEND $$\nDELIMITER ;", 'w')
    return x

out, seen, stat = [], set(), collections.Counter(); t0 = time.time()
while len(out) < N_SYN:
    g = make()
    try: reset(g['ddl'])
    except Exception as e:
        stat['ddl_fail'] += 1
        if stat['ddl_fail'] < 4: print('DDL', e)
        if stat['ddl_fail'] > 200: raise
        continue
    sch, T, known = schema_text(); items = qs(g); R.shuffle(items)
    for q, sql, kind in [i for i in items if i[2] == 'r'][:9] + [i for i in items if i[2] == 'w'][:5]:
        if leaks(q): stat['leak'] += 1; continue
        try: rows = execute(sql)
        except Exception as e:
            stat['fail'] += 1
            if stat['fail'] < 12: print('FAIL', q, '\n', sql, '\n', e)
            continue
        if kind == 'r' and not rows and R.random() < .6: stat['empty'] += 1; continue
        if (q, sql) in seen: continue
        seen.add((q, sql)); out.append(dict(src='syn', prompt=prompt(g['db'], sch, T, known, q), answer=sql + '\n```'))
        if kind == 'w': reset(g['ddl'])
print('synthetic', len(out), dict(stat), int(time.time() - t0), 's', flush=True)

# ---- public: gretelai/synthetic_text_to_sql (Apache-2.0)
df = pd.read_parquet(ROOT + r'\train\hf\gretel\train.parquet').sample(frac=1, random_state=3); st2 = collections.Counter(); n0 = len(out)
quota = {'basic SQL': .14, 'aggregation': .20, 'single join': .24, 'multiple_joins': .16, 'subqueries': .14, 'window functions': .05, 'set operations': .05, 'CTEs': .02}
used = collections.Counter()
for r in df.itertuples():
    if len(out) - n0 >= N_PUB: break
    c = r.sql_complexity
    if used[c] >= quota.get(c, 0) * N_PUB: continue
    q, sql = r.sql_prompt.strip(), r.sql.strip()
    if leaks(q) or not (25 < len(q) < 260) or len(sql) > 700 or len(r.sql_context) > 2500: st2['skip'] += 1; continue
    if re.search(r'company|hospital|library', r.domain, re.I): st2['domain'] += 1; continue
    if re.search(r'SHUTDOWN|\bDATABASE\b|OUTFILE|LOAD_FILE|LOAD\s+DATA|SLEEP|GRANT|GLOBAL|\bUSER\b|\bmysql\.', sql + r.sql_context, re.I): st2['unsafe'] += 1; continue
    try:
        reset(r.sql_context); sch, T, known = schema_text()
        if len(sch) > BUDGET: continue
        rows = run(sql)
    except Exception:
        st2['err'] += 1
        try: con.ping(True)
        except Exception: pass
        continue
    if sql.upper().startswith(('SELECT', 'WITH', '(')) and (not rows or all(z is None for z in rows[0])): st2['empty'] += 1; continue
    used[c] += 1; out.append(dict(src='pub', prompt=prompt('userdb', sch, T, known, q), answer=sql.rstrip(';').strip() + ';\n```'))
print('public', len(out) - n0, dict(st2), dict(used), int(time.time() - t0), 's')
R.shuffle(out); nv = min(200, len(out) // 10)
for f, part in (('val', out[:nv]), ('train', out[nv:])):
    with open(ROOT + rf'\train\{f}.jsonl', 'w', encoding='utf-8') as h:
        for o in part: h.write(json.dumps(o, ensure_ascii=False) + '\n')
Ls = [len(o['prompt']) + len(o['answer']) for o in out]; print('total', len(out), 'chars mean', sum(Ls) // len(Ls), 'max', max(Ls))
try: run('SHUTDOWN')
except Exception: pass
srv.wait(15)
