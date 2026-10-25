#!/usr/bin/env bash
# Create, list, and verify local Kare restore points. Issue #6, local half.
#
# A restore point is a directory with a manifest.json and a files/ tree.
# The manifest records a schema version, a device sequence number, and a
# SHA-256 plus byte count for every object. Verify means the checksums in
# the manifest still match the files on disk. Nothing in a restore point
# is a credential: protected configuration, Copilot auth state, models,
# and runtimes are excluded by design. Logs are excluded unless
# --include-logs is passed.
#
# Usage:
#   build/kare-backup.sh [--include-logs] [--keep-days N]
#   build/kare-backup.sh --list
#   build/kare-backup.sh --verify RESTORE_POINT_DIR
#
# Locations can be overridden for tests and dry runs:
#   KARE_BACKUP_ROOT, KARE_STATE_DIRECTORY, KARE_CACHE_DIRECTORY,
#   KARE_DATA_DIRECTORY (dashboard registry and intake state)

set -euo pipefail
umask 077

BACKUP_ROOT="${KARE_BACKUP_ROOT:-/var/lib/kare/backups}"
STATE_DIR="${KARE_STATE_DIRECTORY:-/var/lib/kare/state}"
CACHE_DIR="${KARE_CACHE_DIRECTORY:-/var/lib/kare/cache}"
DATA_DIR="${KARE_DATA_DIRECTORY:-$HOME/.local/share/kare}"
LOG_DIR="${KARE_LOG_DIRECTORY:-/var/lib/kare/logs}"
KEEP_DAYS=14
INCLUDE_LOGS=false
MODE="create"
VERIFY_DIR=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --include-logs) INCLUDE_LOGS=true; shift ;;
        --keep-days) KEEP_DAYS="$2"; shift 2 ;;
        --list) MODE="list"; shift ;;
        --verify) MODE="verify"; VERIFY_DIR="$2"; shift 2 ;;
        *) echo "Unknown argument: $1" >&2; exit 2 ;;
    esac
done

POINTS_DIR="$BACKUP_ROOT/restore-points"

list_points() {
    mkdir -p "$POINTS_DIR"
    find "$POINTS_DIR" -mindepth 1 -maxdepth 1 -type d -printf '%f\n' | sort
}

verify_point() {
    local point="$1"
    [[ -f "$point/manifest.json" ]] || { echo "No manifest.json in $point" >&2; exit 1; }
    python3 - "$point" <<'PYEOF'
import hashlib, json, pathlib, sys
point = pathlib.Path(sys.argv[1])
manifest = json.loads((point / "manifest.json").read_text())
bad = 0
for obj in manifest["objects"]:
    target = point / "files" / obj["path"]
    if not target.is_file():
        print(f"missing: {obj['path']}")
        bad += 1
        continue
    data = target.read_bytes()
    if len(data) != obj["bytes"] or hashlib.sha256(data).hexdigest() != obj["sha256"]:
        print(f"mismatch: {obj['path']}")
        bad += 1
print(f"verified {len(manifest['objects'])} objects, {bad} bad")
sys.exit(1 if bad else 0)
PYEOF
}

if [[ "$MODE" == "list" ]]; then
    list_points
    exit 0
fi

if [[ "$MODE" == "verify" ]]; then
    verify_point "$VERIFY_DIR"
    exit 0
fi

mkdir -p "$POINTS_DIR"
chmod 700 "$BACKUP_ROOT" "$POINTS_DIR"

SEQUENCE_FILE="$BACKUP_ROOT/.sequence"
SEQUENCE=1
if [[ -f "$SEQUENCE_FILE" ]]; then
    SEQUENCE=$(( $(cat "$SEQUENCE_FILE") + 1 ))
fi
printf '%s\n' "$SEQUENCE" > "$SEQUENCE_FILE"
chmod 600 "$SEQUENCE_FILE"

STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
COMMIT="unknown"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if git -C "$REPO_ROOT" rev-parse --short HEAD >/dev/null 2>&1; then
    COMMIT="$(git -C "$REPO_ROOT" rev-parse --short HEAD)"
    if [[ -n "$(git -C "$REPO_ROOT" status --porcelain)" ]]; then
        COMMIT="$COMMIT-dirty"
    fi
fi

POINT_NAME="$(printf '%s-%06d-%s' "$STAMP" "$SEQUENCE" "$COMMIT")"
TMP_POINT="$POINTS_DIR/.tmp-$POINT_NAME"
POINT="$POINTS_DIR/$POINT_NAME"
rm -rf "$TMP_POINT"
mkdir -p "$TMP_POINT/files"

copy_tree() {
    local source="$1" label="$2"
    [[ -d "$source" ]] || return 0
    mkdir -p "$TMP_POINT/files/$label"
    cp -a "$source"/. "$TMP_POINT/files/$label"/
}

copy_tree "$STATE_DIR" "state"
copy_tree "$CACHE_DIR" "cache"
copy_tree "$DATA_DIR" "data"
if [[ "$INCLUDE_LOGS" == true ]]; then
    copy_tree "$LOG_DIR" "logs"
fi

# Copilot session state is auth-adjacent and device-local. It never ships.
rm -rf "$TMP_POINT/files/data/copilot" "$TMP_POINT/files/state/copilot"

# The manifest is written by python for correct JSON. Sequence and commit
# arrive by environment so no value is ever interpolated into code.
POINT_DIR="$TMP_POINT" KARE_SEQ="$SEQUENCE" KARE_COMMIT="$COMMIT" KARE_STAMP="$STAMP" python3 - <<'PYEOF'
import hashlib, json, os, pathlib
point = pathlib.Path(os.environ["POINT_DIR"])
files = point / "files"
objects = []
for target in sorted(files.rglob("*")):
    if target.is_file():
        data = target.read_bytes()
        objects.append({
            "path": str(target.relative_to(files)),
            "sha256": hashlib.sha256(data).hexdigest(),
            "bytes": len(data),
        })
manifest = {
    "schema": 1,
    "created_at_utc": os.environ["KARE_STAMP"],
    "sequence": int(os.environ["KARE_SEQ"]),
    "kare_commit": os.environ["KARE_COMMIT"],
    "objects": objects,
}
(point / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
print(f"manifest: {len(objects)} objects")
PYEOF

chmod 600 "$TMP_POINT/manifest.json"
mv "$TMP_POINT" "$POINT"
verify_point "$POINT"

# Prune by directory age. The manifest stamp, not the wall clock of the
# machine reading it later, is what makes a point old.
find "$POINTS_DIR" -mindepth 1 -maxdepth 1 -type d -mtime "+$KEEP_DAYS" -print -exec rm -rf {} + 2>/dev/null | \
    sed 's/^/pruned: /' || true

printf 'Restore point: %s\n' "$POINT"
