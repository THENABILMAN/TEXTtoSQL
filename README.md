# TEXTtoSQL

An **experimental** offline Text-to-SQL assistant for a university MySQL lab. Runs from a pendrive on a 4 GB RAM
Windows 11 PC (tested): no internet, no installation, no GPU.

Open a `.sql` file, ask in English, and a small local AI model writes the SQL. The app tests it on a throwaway copy of
the database, so your file is never changed.

## How it works

1. Loads your file into a private MariaDB copy and reads the tables.
2. Finds up to 3 similar examples among 81 from the lab manuals.
3. Qwen2.5-Coder 1.5B (a transformer neural network) writes the SQL.
4. The app runs and checks it, and retries up to 3 times in total.

## How to use

Open `TextToSQL.exe`, click **Open database (.sql)**, type a question or paste a numbered list, and press **Ask**.
Answers can be wrong even when the SQL runs, so check them.

## Fine-tuning experiment

**Question:** does training on lab-style SQL beat using examples alone?

**Method:** LoRA on an RTX 4050 (6 GB), 8,319 examples whose SQL all ran on MariaDB, 1 epoch, about an hour.
Both models were tested in the same app on the same 80 questions.

| Test set | Original | Fine-tuned |
|---|---|---|
| lab6 | 13/14 | 13/14 |
| company | 11/16 | 12/16 |
| hospital | 18/18 | 16/18 |
| library | 10/12 | 11/12 |
| restaurant (new questions) | 14/20 | 14/20 |
| **Total** | **66/80** | **66/80** |

**Finding:** 9 questions became right and 9 became wrong, and answers came faster (4.9 s against 6.6 s on an i7).
The total did not improve, so the app uses the original model. Earlier failures on the first 60 questions guided
development, so the restaurant score is the fairest estimate.

**Next:** blend the two models and train on more varied data.

## Folders

- `src/` app source and `build.bat`
- `pendrive/` compiled app and lab examples
- `eval/` tests and results
- `train/` training scripts

Not included: the model, llama.cpp and MariaDB (too large), and the course documents. To rebuild, put the first three in
`model/`, `engine/` and `mariadb/` inside `pendrive/TextToSQL/`.

Public training data: [gretelai/synthetic_text_to_sql](https://huggingface.co/datasets/gretelai/synthetic_text_to_sql)
(Apache-2.0).

## Author

Mohammad Ali Nabil
