# Backup and restore points

Status on 2026-10-25: the local half of issue #6 is built. The Azure half is not provisioned yet, and this file does not pretend otherwise.

## Field inventory

Every persisted field I found on the device, sorted by what a backup may do with it:

| Data | Location | Class | In a restore point |
|---|---|---|---|
| Dashboard registry snapshot (sources, skills, MCP servers) | `~/.local/share/kare/` | portable | yes |
| Dashboard intake state | `~/.local/share/kare/dashboard-intake.json` | portable | yes |
| Service state directory | `/var/lib/kare/state` (`KARE_STATE_DIRECTORY`) | portable | yes |
| Response cache snapshot and it's `.bak1` to `.bak3` rollbacks | `/var/lib/kare/cache/responses.json` | portable, with conditions | yes |
| Route policy and model catalog | protected config file outside the repo | portable only without secrets | no, by hand |
| Service logs | `/var/lib/kare/logs` | policy | only with `--include-logs` |
| Protected configuration and API keys | `~/.config/kare/` | secret | never |
| Copilot auth and session state | `~/.config/kare/copilot-home/`, `~/.local/state/kare/copilot/` | secret, device-bound | never |
| GenieX models and runtime | `/var/lib/kare/models`, `/var/lib/kare/runtimes` | re-downloadable | never |
| Published service releases | `~/kare/service/releases/` | reproducible from git | never |

The response cache has one condition from `plan.md` that still applies: a restored cache entry may serve traffic only after the parser, index, route policy, tokenizer, and model versions check out against the restoring build. A checksum alone does not prove the entry is safe to serve.

## Local restore points

`build/kare-backup.sh` writes one restore point per run:

```text
/var/lib/kare/backups/restore-points/<UTC stamp>-<sequence>-<commit>/
  manifest.json
  files/state/...
  files/cache/...
  files/data/...
```

- `manifest.json` is schema 1. It carries the UTC stamp, a device sequence number that only moves forward, the Kare commit (with a `-dirty` suffix when the tree had uncommitted changes, so a point never claims a commit it did not come from), and one entry per object with a path, a SHA-256, and a byte count.
- Points are pruned at 14 days. That matches the restore window in issue #6.
- `--verify <point>` re-reads every object and re-checks the size and the hash. A tampered or truncated file fails the point.
- Everything is written with mode 600 files and 700 directories, in a staging directory that is renamed into place only when the manifest is done.

## What is left for the Azure half

These steps from issue #6 are not done, and each one needs something I do not have in the repo:

1. Provision the Azure Database for PostgreSQL Flexible Server with 14-day point-in-time restore and cost alerts. That is a paid resource with a connection string. The string goes in protected configuration only.
2. Add the uploader: read new restore points, send objects in idempotent batches keyed by sequence plus path plus hash, flush deltas on a 15-minute cadence, and write a full manifest every six hours.
3. Add restore into a staging directory with atomic activation and a rollback path, plus clone mode with a new device identity and path remapping.
4. Run the restore tests the issue lists: latest point, a three-hour-old point, a 13-day-old point, a database point-in-time restore, and a second-device clone. Measure bytes per day, database size, restore time, and monthly cost.

Until step 1 happens, restore points stay on the NVMe in `/var/lib/kare`. A dead board takes it's local points with it. That is the gap the Azure half exists to close.
