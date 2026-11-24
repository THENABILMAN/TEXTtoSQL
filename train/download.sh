#!/bin/bash
# Resumable downloads: this network resets connections at random.
cd "$(dirname "$0")/hf" || exit 1
H=https://huggingface.co
get() { # url file
  local i
  for i in $(seq 1 80); do
    [ -f "$2.ok" ] && return 0
    curl -sSfL -C - --connect-timeout 20 --speed-limit 2000 --speed-time 40 -o "$2" "$1" && { touch "$2.ok"; echo "OK $2 (try $i)"; return 0; }
    sleep 3
  done
  echo "FAILED $2"; return 1
}
mkdir -p model gretel; bad=0
for f in config.json generation_config.json tokenizer.json tokenizer_config.json vocab.json merges.txt LICENSE model.safetensors; do
  get $H/Qwen/Qwen2.5-Coder-1.5B-Instruct/resolve/main/$f model/$f 2>&1 | tail -2; [ -f model/$f.ok ] || bad=1
done
get $H/datasets/gretelai/synthetic_text_to_sql/resolve/main/synthetic_text_to_sql_train.snappy.parquet gretel/train.parquet 2>&1 | tail -2
[ -f gretel/train.parquet.ok ] || bad=1
ls -la model gretel; exit $bad
