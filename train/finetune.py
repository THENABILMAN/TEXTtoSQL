"""LoRA fine-tune of Qwen2.5-Coder-1.5B on a 6 GB GPU, then merge to train/out/merged.
Usage: python finetune.py [--steps N]   (--steps is a quick smoke test: no merge)"""
import json, math, os, random, sys, time, argparse
import torch
from transformers import AutoTokenizer, AutoModelForCausalLM, get_cosine_schedule_with_warmup
from peft import LoraConfig, get_peft_model
ap = argparse.ArgumentParser(); ap.add_argument('--steps', type=int, default=0); ap.add_argument('--epochs', type=int, default=2)
ap.add_argument('--lr', type=float, default=1e-4); ap.add_argument('--bs', type=int, default=2); ap.add_argument('--acc', type=int, default=8); A = ap.parse_args()
assert torch.cuda.is_available(), 'GPU PyTorch is required'
HERE = os.path.dirname(os.path.abspath(__file__)); BASE = HERE + r'\hf\model'; OUT = HERE + r'\out'; os.makedirs(OUT, exist_ok=True)
random.seed(5); torch.manual_seed(5); torch.backends.cuda.matmul.allow_tf32 = True
tok = AutoTokenizer.from_pretrained(BASE); END = tok.convert_tokens_to_ids('<|im_end|>')
def load(f):
    out = []
    for l in open(f'{HERE}\\{f}.jsonl', encoding='utf-8'):
        o = json.loads(l); p = tok(o['prompt'], add_special_tokens=False)['input_ids']; a = tok(o['answer'], add_special_tokens=False)['input_ids'] + [END]
        if len(p) + len(a) <= 1800: out.append((p, a))
    return out
train, val = load('train'), load('val'); n = [len(p) + len(a) for p, a in train]
print('train', len(train), 'val', len(val), 'tokens mean', sum(n) // len(n), 'max', max(n), flush=True)
model = AutoModelForCausalLM.from_pretrained(BASE, dtype=torch.bfloat16, attn_implementation='sdpa').cuda()
model.config.use_cache = False; model.gradient_checkpointing_enable(gradient_checkpointing_kwargs={'use_reentrant': False}); model.enable_input_require_grads()
model = get_peft_model(model, LoraConfig(r=16, lora_alpha=32, lora_dropout=0.05, task_type='CAUSAL_LM',
    target_modules=['q_proj', 'k_proj', 'v_proj', 'o_proj', 'gate_proj', 'up_proj', 'down_proj']))
for p in model.parameters():
    if p.requires_grad: p.data = p.data.float()
model.print_trainable_parameters(); inner = model.get_base_model()

def loss_of(batch):
    """cross entropy on answer tokens only; the head runs on those positions only, to save memory"""
    L = max(len(p) + len(a) for p, a in batch); ids = torch.full((len(batch), L), tok.pad_token_id); att = torch.zeros_like(ids); lab = torch.full_like(ids, -100)
    for i, (p, a) in enumerate(batch):
        k = len(p) + len(a); ids[i, :k] = torch.tensor(p + a); att[i, :k] = 1; lab[i, len(p):k] = torch.tensor(a)
    ids, att, lab = ids.cuda(), att.cuda(), lab.cuda()
    with torch.autocast('cuda', dtype=torch.bfloat16):
        h = inner.model(input_ids=ids, attention_mask=att).last_hidden_state[:, :-1]
        m = lab[:, 1:] != -100; logits = inner.lm_head(h[m])
    return torch.nn.functional.cross_entropy(logits.float(), lab[:, 1:][m])
@torch.no_grad()
def evaluate():
    model.eval(); v = [loss_of(val[i:i + 2]).item() for i in range(0, len(val), 2)]; model.train(); return sum(v) / len(v)

opt = torch.optim.AdamW([p for p in model.parameters() if p.requires_grad], lr=A.lr, weight_decay=0.01)
total = A.steps or A.epochs * (len(train) // (A.bs * A.acc)); sch = get_cosine_schedule_with_warmup(opt, max(1, int(total * .04)), total)
v0 = evaluate(); print(f'val loss before {v0:.4f}  optimizer steps {total}', flush=True)
def batches():
    while True:
        o = train[:]; random.shuffle(o)
        chunks = [sorted(o[i:i + 256], key=lambda z: len(z[0]) + len(z[1])) for i in range(0, len(o), 256)]   # similar lengths together
        bs = [c[i:i + A.bs] for c in chunks for i in range(0, len(c), A.bs)]; random.shuffle(bs); yield from bs
it = batches(); t0 = time.time(); run = 0.; best = v0; hist = []; model.train()
for s in range(1, total + 1):
    for _ in range(A.acc): l = loss_of(next(it)) / A.acc; l.backward(); run += l.item()
    g = torch.nn.utils.clip_grad_norm_([p for p in model.parameters() if p.requires_grad], 1.0); opt.step(); sch.step(); opt.zero_grad(set_to_none=True)
    if not math.isfinite(float(g)): sys.exit('gradient is not finite, stopping')
    if s % 10 == 0 or s == total:
        print(f'{s}/{total} loss {run / (10 if s % 10 == 0 else s % 10):.4f}  {(time.time() - t0) / s:.1f} s/step  left {(total - s) * (time.time() - t0) / s / 60:.0f} min  gpu {torch.cuda.max_memory_allocated() / 1e9:.1f} GB', flush=True); run = 0.
    if not A.steps and (s % 150 == 0 or s == total):
        v = evaluate(); hist.append((s, v)); print(f'   val loss {v:.4f}', flush=True)
        if v < best: best = v; model.save_pretrained(OUT + r'\lora')
json.dump(dict(before=v0, best=best, history=hist), open(OUT + r'\history.json', 'w'))
if A.steps: sys.exit()
del opt; torch.cuda.empty_cache()
from peft import PeftModel
m = PeftModel.from_pretrained(AutoModelForCausalLM.from_pretrained(BASE, dtype=torch.bfloat16), OUT + r'\lora').merge_and_unload()
m.save_pretrained(OUT + r'\merged', safe_serialization=True); tok.save_pretrained(OUT + r'\merged'); print('merged', flush=True)
