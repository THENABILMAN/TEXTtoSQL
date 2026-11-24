import json, torch
from transformers import AutoTokenizer, AutoModelForCausalLM
d=r'C:\TEXTtoSQL\train\out\merged'; t=AutoTokenizer.from_pretrained(d); m=AutoModelForCausalLM.from_pretrained(d,dtype=torch.bfloat16).cuda().eval()
ok=0; V=[json.loads(l) for l in open(r'C:\TEXTtoSQL\train\val.jsonl',encoding='utf-8')][:12]
for o in V:
    i=t(o['prompt'],add_special_tokens=False,return_tensors='pt').to('cuda')
    g=m.generate(**i,max_new_tokens=250,do_sample=False,eos_token_id=t.convert_tokens_to_ids('<|im_end|>'),pad_token_id=t.pad_token_id)
    s=t.decode(g[0][i['input_ids'].shape[1]:],skip_special_tokens=True).split('```')[0].strip(); e=o['answer'].split('```')[0].strip(); ok+=s==e
    print('---',s==e); print(s[:260])
print('exact',ok,'of',len(V))
