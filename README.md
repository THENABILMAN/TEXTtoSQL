# TEXTtoSQL

Offline Text-to-SQL assistant for a university MySQL lab. Runs from a pendrive on Windows 11:
open a `.sql` file, ask questions, and the SQL is run on a throwaway copy of the database before it is shown.

- `src/` C# source and `build.bat` (uses the compiler that ships with Windows)
- `pendrive/TextToSQL/knowledge/` worked lab examples shown to the model
- `eval/` held-out questions, gold answers and the scoring script (`run.py`)

Not in this repo because of size: `model/model.gguf` (qwen2.5-coder 1.5B), `engine/` (llama.cpp
`llama-server`), and `mariadb/` (portable MariaDB 10.11), all inside `pendrive/TextToSQL/`.

## Fine-tuning result

`train/` holds the data builder (8,319 examples, every SQL answer executed on MariaDB; half from
gretelai/synthetic_text_to_sql, Apache-2.0) and the LoRA script (Qwen2.5-Coder-1.5B, RTX 4050 6 GB, 1 epoch).
Same app build, 80 graded questions:

| Set | Original | Fine-tuned |
|---|---|---|
| lab6 | 13/14 | 13/14 |
| company | 11/16 | 12/16 |
| hospital | 18/18 | 16/18 |
| library | 10/12 | 11/12 |
| restaurant (new questions) | 14/20 | 14/20 |
| **Total** | **66/80** | **66/80** |

Validation loss fell from 0.578 to 0.123, but that set shares question templates with the training data and the
graded score did not improve, so the app still ships the original model.
