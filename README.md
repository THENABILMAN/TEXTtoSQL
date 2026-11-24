# TEXTtoSQL

Offline Text-to-SQL assistant for a university MySQL lab. Runs from a pendrive on Windows 11:
open a `.sql` file, ask questions, and the SQL is run on a throwaway copy of the database before it is shown.

- `src/` C# source and `build.bat` (uses the compiler that ships with Windows)
- `pendrive/TextToSQL/knowledge/` worked lab examples shown to the model
- `eval/` held-out questions, gold answers and the scoring script (`run.py`)

Not in this repo because of size: `model/model.gguf` (qwen2.5-coder 1.5B), `engine/` (llama.cpp
`llama-server`), and `mariadb/` (portable MariaDB 10.11), all inside `pendrive/TextToSQL/`.
