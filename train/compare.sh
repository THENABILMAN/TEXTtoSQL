#!/bin/bash
# Runs the whole evaluation with the old and then the fine-tuned model. Leaves the OLD model in place.
M=/c/TEXTtoSQL/pendrive/TextToSQL/model; S=/c/TEXTtoSQL/work/models; E=/c/TEXTtoSQL/eval; mkdir -p $S
NEW="$1"
[ -f $S/old.gguf ] || cp $M/model.gguf $S/old.gguf
[ "$(head -c 4 "$NEW")" = GGUF ] || { echo "not a GGUF"; exit 1; }
cp "$NEW" $S/new.gguf || exit 1
cmp -s $S/old.gguf $S/new.gguf && { echo "same file"; exit 1; }
cd $E
for v in ${VARIANTS:-old new}; do
  cp $S/$v.gguf $M/model.gguf
  for n in lab6 company hospital library restaurant q10; do rm -f $n.json; done
  PYTHONIOENCODING=utf-8 python run.py > results_$v.log 2>&1
  mkdir -p $v; cp lab6.json company.json hospital.json library.json restaurant.json q10.json $v/
  echo "== $v"; tail -1 results_$v.log
done
cp $S/old.gguf $M/model.gguf
