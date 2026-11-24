# TEXTtoSQL

An **experimental** offline Text-to-SQL assistant for a university MySQL lab (CSE311L / CSE 331L).
Open a `.sql` database file, ask a question in English, and a small language model on your own PC writes the SQL.
The app runs that SQL on a throwaway copy of the database before showing it, so your file is never changed.

> **Status:** working prototype. About 82% correct on 80 graded questions. Not yet tested on a real 4 GB PC.
> Treat every answer as a draft and check it.

## Contents

- [Goal](#goal)
- [How it works](#how-it-works)
- [Using the app](#using-the-app)
- [What was done, and why](#what-was-done-and-why)
- [Fine-tuning experiment](#fine-tuning-experiment)
- [Results](#results)
- [What went wrong along the way](#what-went-wrong-along-the-way)
- [Limitations](#limitations)
- [Repository layout](#repository-layout)
- [Rebuilding it](#rebuilding-it)
- [Next steps](#next-steps)

## Goal

Plug a pendrive into a Windows 11 PC with 4 GB of RAM, open the program, give it any `.sql` database file, and ask
lab-style questions, with no internet, no installation and no GPU.

Those limits shaped the main choices:

| Choice | Reason |
|---|---|
| Qwen2.5-Coder 1.5B, 4-bit (986 MB) | A coding model small enough to aim for 4 GB of RAM |
| llama.cpp on the CPU | Runs without a GPU or an installer |
| Portable MariaDB 10.11 | Real MySQL-style syntax and errors, so answers can be tested |
| C# compiled with the compiler that ships with Windows | A 79 KB program with no runtime to install |
| Examples retrieved from the lab manuals | Adapts the model to the course without training |

## How it works

```
 .sql file ──> private MariaDB copy ──> schema (tables, keys, sample values)
                                                │
 question ──> up to 3 similar lab examples ─────┤
                                                v
                                     1.5B language model
                                                │ SQL
                                                v
                         run on the copy + rule checks ──fail──> retry (3 tries in total)
                                                │ ok
                                                v
                            second opinion (SELECT questions) ──> answer + result table
```

1. **Load.** The script is loaded into a temporary MariaDB server reached through a named pipe, not the network.
   Simple conversions are attempted for SQLite, Oracle and SQL Server scripts.
2. **Describe.** Tables, columns, primary and foreign keys, likely undeclared links and sample values become a compact
   text schema. For large databases, only the tables that match the question are included.
3. **Retrieve.** Up to 3 of the 81 worked examples in `manual_examples.txt` are added as a guide.
4. **Generate.** The model (Qwen2.5-Coder, a transformer neural network) writes the SQL.
5. **Check.** The SQL is executed, and simple rules look for problems such as a missing alias, the wrong comparison
   operator, or a `SELECT` when the question asks to change data.
6. **Retry.** Errors go back to the model as hints.
7. **Second opinion.** For queries that only read data, the model writes another draft without examples. If the results
   differ, a further draft can settle it; otherwise the app shows a warning.
8. **Clean up.** The temporary server and its files are removed on exit.

Theory questions such as "What is the difference between WHERE and HAVING?" get a short text answer.

## Using the app

1. Copy the complete `pendrive/TextToSQL` folder to the pendrive (about 1.1 GB, including the parts not in this repo).
2. Run `TextToSQL.exe` and wait until it says it is ready.
3. Click **Open database (.sql)** or drag the file into the window.
4. Type a question, or paste a numbered list, and press **Ask** or **Ctrl+Enter**.

| Control | What it does |
|---|---|
| Reset database | Reloads the original data |
| Copy last SQL | Copies the latest query |
| Save all answers | Saves every question and answer to a `.sql` file |
| Keep changes between questions | Lets an `UPDATE` or `ALTER` affect later questions |

"Runs without errors" means only that: the SQL ran. It does not mean the answer is correct.

To teach it new patterns without training, add to `pendrive/TextToSQL/knowledge/manual_examples.txt` and restart:

```
Q: Show employees whose salary is above 50000
K: salary greater than where filter
SELECT First_Name, Salary_Amount
FROM employee
WHERE Salary_Amount > 50000;
```

`K:` holds optional search words. A blank line ends an example, so leave none inside the SQL.

## What was done, and why

1. **Collected the lab material:** 14 documents (6 lab manuals and 8 weekly sheets) were condensed into 81 worked
   examples. The manuals themselves are not in this repo.
2. **Built the app:** database loader, schema reader, example search, model runner, checks and window.
3. **Built a test set:** graded questions on several databases, because a query that runs can still be wrong. Each
   answer's results are compared with a reference answer; for changes, the resulting database state is compared.
4. **Improved it using the failures:** 48/60 at first, then 52/60 after fixing a broken check and adding the second
   opinion, among other changes.
5. **Fine-tuned the model on a GPU** to see whether real training would beat retrieved examples.
6. **Compared both models** on the same app build and questions, plus 20 questions written before any results were seen.
7. **Kept the original model,** because the fine-tuned one did not score higher.

## Fine-tuning experiment

**Question:** does training on lab-style SQL improve a 1.5B model that already gets examples and checks from the app?

| Setting | Value |
|---|---|
| Base model | Qwen2.5-Coder-1.5B-Instruct |
| Method | LoRA, rank 16, alpha 32, dropout 0.05 |
| Trained weights | 18.5 million (1.2% of the model) |
| Hardware | NVIDIA RTX 4050 Laptop, 6 GB (about 4.1 GB used by PyTorch) |
| Precision | bfloat16 with gradient checkpointing |
| Batch | 2, accumulated 8 times (effective 16) |
| Learning rate | 1e-4, cosine schedule, AdamW |
| Length | 1 epoch, 507 steps, just over an hour |
| Main tools | Python 3.11, PyTorch 2.14 (CUDA 12.6), Transformers 5.24, PEFT 0.22 |

**Data: 8,319 examples** (8,119 for training, 200 for validation), in the same prompt layout the app uses:

- **4,203 generated** from randomised databases in 10 themes, with lab-style questions on joins, grouping, subqueries,
  set operations, updates, `ALTER`, views, procedures, functions and triggers.
- **4,116 public** from [gretelai/synthetic_text_to_sql](https://huggingface.co/datasets/gretelai/synthetic_text_to_sql)
  (Apache-2.0).

Every SQL answer was run on MariaDB before it was kept, and candidate questions too similar to the test questions that
existed then were dropped. Running without errors does not prove that a public example's answer is right.

| Step | Validation loss |
|---|---|
| Before training | 0.578 |
| 150 | 0.145 |
| 300 | 0.127 |
| 450 | 0.124 |
| 507 | 0.123 |

The validation examples share patterns with the training data, so this drop alone does not show better SQL.

## Results

Same app build, 80 graded questions, on an i7-13700HX using the CPU only:

| Test set | Original | Fine-tuned |
|---|---|---|
| lab6 | 13/14 | 13/14 |
| company | 11/16 | 12/16 |
| hospital | **18/18** | 16/18 |
| library | 10/12 | **11/12** |
| restaurant (new questions) | 14/20 | 14/20 |
| **Total** | **66/80 (82.5%)** | **66/80 (82.5%)** |
| Mean time per question* | 6.6 s | **4.9 s** |

\* Includes the 10 ungraded sample lab questions.

- **Fine-tuned was better at** finding missing rows, supervisor names, `EXCEPT`, and "reduce by 50" updates.
- **Fine-tuned was worse at** some longer joins, totals per item and `IS NULL`. It also tended to copy patterns from its
  training data.
- **Sample lab questions (10):** both models' SQL ran on all 10, but the fine-tuned model got the `RIGHT JOIN` one wrong.

**Conclusion:** one round of fine-tuning changed *which* questions were right, but not *how many*. The app ships the
original model.

How far to trust these numbers:

- 80 questions is a small sample; a gap of one or two would mean little.
- Failures on the first 60 questions guided the app's fixes and the training topics, so those scores may flatter.
  The restaurant set (14/20) is the fairest estimate.
- Scoring is strict: the right data with columns in a different order counts as wrong.

## What went wrong along the way

- **Invisible characters.** An edit script turned the regex `\b` into a backspace character, so the check for "change
  the data" questions never fired until it was fixed.
- **Unstable network.** Downloads kept resetting; `train/download.sh` retries and resumes.
- **Broken conversion.** The first converted fine-tuned model produced gibberish. The newer Transformers library saved
  `rope_theta` in a new place, the converter missed it, and it was written as 0. Restoring the original config and
  tokenizer files fixed it without retraining.
- **Misleading validation loss.** It fell by 79% while the graded score did not move.

## Limitations

- **Not tested on a 4 GB PC.** In development the model process used about 1.2 GB and the database about 60 MB. Expect
  slower answers than in the table above.
- It cannot answer every question. Multi-step joins and loosely worded questions are the weakest areas.
- It targets MySQL/MariaDB. Other dialects are only partly converted.
- The model has no memory of earlier questions.

## Repository layout

| Path | Contents |
|---|---|
| `src/` | App source (`TextToSQL.cs`) and `build.bat` |
| `pendrive/TextToSQL/` | Compiled program and the lab examples |
| `eval/` | Test databases, questions, reference answers, `run.py`, and results in `old/` (original) and `new/` (fine-tuned) |
| `train/build_data.py` | Generates and checks the training data |
| `train/finetune.py` | LoRA training and merging |
| `train/download.sh` | Resumable model and dataset download |
| `train/compare.sh` | Tests the original and fine-tuned models |
| `train/out_check.py` | Checks the merged model on the GPU |
| `train/train.log` | Training log |

Not included: the model files and bundled programs (size), the course documents, the sample lab database, and the
generated training data.

## Rebuilding it

**App.** Put these in `pendrive/TextToSQL/`, then run `src/build.bat`:

| Folder | Needed |
|---|---|
| `model/` | One `.gguf` file, such as Qwen2.5-Coder 1.5B Instruct in Q4_K_M |
| `engine/` | `llama-server.exe` and its DLLs from a Windows CPU build of llama.cpp |
| `mariadb/` | `bin/` and `share/` from MariaDB 10.11, plus an initialised, cleanly shut down data folder named `template/` |

**Tests.** Run `python eval/run.py`, or name sets, for example `python eval/run.py hospital restaurant`.
Paths in the scripts assume `C:\TEXTtoSQL`; change them if yours differ. The `lab6` and `q10` sets also need the lab
database, which is not included.

**Training.** Needs an NVIDIA GPU with about 6 GB, and the app files above for MariaDB:

```
pip install torch --index-url https://download.pytorch.org/whl/cu126
pip install transformers peft accelerate pandas pyarrow pymysql
bash train/download.sh
python train/build_data.py
python train/finetune.py --epochs 1
```

To convert the result, copy the JSON files and `merges.txt` from `train/hf/model` over those in `train/out/merged`
(see the conversion problem above) and delete `chat_template.jinja`. Put `FROM ./merged` in `train/out/Modelfile`, then
run this in `train/out`:

```
ollama create -q q4_K_M t2s-ft -f Modelfile
```

## Next steps

- Test on a real 4 GB PC from the pendrive.
- Blend the two models by applying the LoRA at half strength, then test on fresh questions.
- Use more varied training questions, so the model copies patterns less.

## Author

Mohammad Ali Nabil
