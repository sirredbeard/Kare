# Kare benchmark

The harness for issue #8. It runs one frozen task set against two arms:

- `kare` - a Kare `/v1` endpoint, streamed, with time to first token and total time read off the stream.
- `auto` - `copilot -p TASK --model auto` on the same machine.

Quality, latency, and cost are reported as three separate numbers. There is no combined score. A cheap wrong answer is still wrong.

## Run it

```bash
python3 bench/benchmark/benchmark.py --tasks bench/benchmark/tasks.example.json --dry-run
python3 bench/benchmark/benchmark.py --tasks bench/benchmark/tasks.json --arms kare,auto --out benchmarks/results
```

Dry run stubs both arms. It checks the harness, not a model.

A real run needs Kare up on the device and Copilot CLI signed in on the client machine. Set `KARE_BENCH_API_KEY` in the shell if the endpoint needs a key. Do not put a key in a task file or a result file.

## Rules

- Freeze the task set before a scored run. `tasks.example.json` is a 4-task starter. Issue #8 calls for 24 tasks, four per category across six categories.
- Run both arms from the same client machine, the same repository commit, and the same account.
- Record unknown values as unknown. The `auto` arm reports credits only when the CLI prints them. The harness does not guess.
- Results land in `benchmark-results/<UTC stamp>/` as `results.jsonl` plus `summary.json`. The `benchmarks/results/` path is already in `.gitignore`. Point `--out` there for scored runs.
