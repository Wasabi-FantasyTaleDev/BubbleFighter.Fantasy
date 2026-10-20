#!/usr/bin/env bash
# Starts the Bubble Fighter Fantasy host with the kr-1256 profile.
# Loads repo-root .env when present. Values are never printed.
set -eu

show_usage() {
  cat <<'EOF'
Start the Bubble Fighter Fantasy host (profile kr-1256).

Usage:
  tools/start-server.sh [options] [host-args...]

Options consumed by this script:
  --skip-build   skip dotnet build (fails if the Release host is missing)
  --rebuild      force a non-incremental Release build
  -h, --help     show this text

Other arguments are forwarded to the host after:
  run --profile kr-1256

Environment:
  If .env exists at the repository root, KEY=VALUE lines are loaded into this
  process. Non-empty variables already set in the process are left unchanged.
  File contents and values are not printed.

No-database login test:
  Copy profiles/kr-1256/local.patch.yaml.example to
  profiles/kr-1256/local.patch.yaml (git-ignored) to disable persistence.mysql.
EOF
}

load_dotenv() {
  local file="$1"
  if [[ ! -f "$file" ]]; then
    return 1
  fi

  local line name value
  while IFS= read -r line || [[ -n "$line" ]]; do
    line="${line%$'\r'}"
    case "$line" in
      ''|\#*) continue ;;
    esac
    if [[ "$line" == export\ * ]]; then
      line="${line#export }"
    fi
    case "$line" in
      *=*) ;;
      *) continue ;;
    esac
    name="${line%%=*}"
    value="${line#*=}"
    name="${name%"${name##*[![:space:]]}"}"
    name="${name#"${name%%[![:space:]]*}"}"
    value="${value#"${value%%[![:space:]]*}"}"
    value="${value%"${value##*[![:space:]]}"}"
    if [[ ! "$name" =~ ^[A-Za-z_][A-Za-z0-9_]*$ ]]; then
      continue
    fi
    if [[ ${#value} -ge 2 ]]; then
      local first="${value:0:1}"
      local last="${value: -1}"
      if [[ "$first" == "$last" && ( "$first" == '"' || "$first" == "'" ) ]]; then
        value="${value:1:${#value}-2}"
      fi
    fi
    if [[ -n "${!name:-}" ]]; then
      continue
    fi
    printf -v "$name" '%s' "$value"
    export "$name"
  done < "$file"
  return 0
}

skip_build=0
rebuild=0
host_args=()

for arg in "$@"; do
  case "$arg" in
    --skip-build) skip_build=1 ;;
    --rebuild) rebuild=1 ;;
    -h|--help) show_usage; exit 0 ;;
    *) host_args+=("$arg") ;;
  esac
done

if [[ "$skip_build" -eq 1 && "$rebuild" -eq 1 ]]; then
  echo "error: use only one of --skip-build and --rebuild" >&2
  exit 1
fi

script_dir="$(cd "$(dirname "$0")" && pwd)"
repo_root="$(cd "$script_dir/.." && pwd)"
solution="$repo_root/BubbleFighter.Fantasy.sln"
host_project="$repo_root/src/BubbleFighter.Fantasy.Host/BubbleFighter.Fantasy.Host.csproj"
host_dll="$repo_root/src/BubbleFighter.Fantasy.Host/bin/Release/net10.0/BubbleFighter.Fantasy.Host.dll"
env_file="$repo_root/.env"
local_patch="$repo_root/profiles/kr-1256/local.patch.yaml"
local_patch_example="$repo_root/profiles/kr-1256/local.patch.yaml.example"

if [[ ! -f "$solution" ]]; then
  echo "error: solution not found: $solution" >&2
  exit 1
fi

if ! command -v dotnet >/dev/null 2>&1; then
  echo "error: dotnet is not on PATH. Install .NET SDK 10.0+." >&2
  exit 1
fi

cd "$repo_root"

if load_dotenv "$env_file"; then
  echo "Loaded environment from $env_file"
else
  echo "No .env file at the repository root; using the current process environment."
fi

if [[ -z "${BFF_MYSQL_PASSWORD:-}" && ! -f "$local_patch" ]]; then
  echo "warning: BFF_MYSQL_PASSWORD is empty and profiles/kr-1256/local.patch.yaml is missing." >&2
  echo "warning: the persistence.mysql row will fail to load. For a no-DB login test, copy" >&2
  echo "warning: $local_patch_example" >&2
  echo "warning: to $local_patch" >&2
fi

if [[ "$skip_build" -eq 1 ]]; then
  if [[ ! -f "$host_dll" ]]; then
    echo "error: Release host is missing at $host_dll. Run without --skip-build." >&2
    exit 1
  fi
  echo "Skipping build (--skip-build)."
else
  if [[ "$rebuild" -eq 1 ]]; then
    echo "Building BubbleFighter.Fantasy.sln (Release, --no-incremental)..."
    dotnet build "$solution" -c Release --no-incremental
  else
    echo "Building BubbleFighter.Fantasy.sln (Release, incremental if up to date)..."
    dotnet build "$solution" -c Release
  fi
fi

for host_arg in "${host_args[@]+"${host_args[@]}"}"; do
  if [[ "$host_arg" == "--dump-config" ]]; then
    echo "warning: host --dump-config prints interpolated config and may include secrets. Do not share that output." >&2
    break
  fi
done

echo "Starting host: run --profile kr-1256"
if [[ ${#host_args[@]} -gt 0 ]]; then
  exec dotnet run --project "$host_project" -c Release --no-build -- run --profile kr-1256 "${host_args[@]}"
fi
exec dotnet run --project "$host_project" -c Release --no-build -- run --profile kr-1256
