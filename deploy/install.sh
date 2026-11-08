#!/usr/bin/env bash
# Install or upgrade the prebuilt Bubble Fighter Fantasy server on Linux (x64 or arm64).
#
#   curl -fsSL https://wasabi-fantasytaledev.github.io/install.sh | bash
#   curl -fsSL https://wasabi-fantasytaledev.github.io/install.sh | bash -s -- --docker
#
# Layout under the install directory (native mode):
#   releases/<tag>/   one unpacked package per installed release
#   current           symlink to the active release
#   state/            the setup assistant's saved installation; kept across upgrades
#   dotnet/           the ASP.NET Core Runtime 10, only when the system has none
#   bff               launcher; with no arguments it starts the setup assistant
# Docker mode builds a local image from the same package into docker/; --with-mysql adds a MySQL 8.4 container.
set -euo pipefail

REPOSITORY="Wasabi-FantasyTaleDev/BubbleFighter.Fantasy"
VERSION="latest"
if [ "$(id -u)" -eq 0 ]; then DIRECTORY="/opt/bubblefighter-fantasy"; else DIRECTORY="$HOME/bubblefighter-fantasy"; fi
INSTALL_RUNTIME=1
SERVICE=0
START=0
DOCKER=0
WITH_MYSQL=0
ADMIN_PORT=""
MYSQL_PORT=3306
FROM=""

usage() {
    cat <<'EOF'
Usage: install.sh [options]

  --version <tag>   release tag to install, e.g. v0.1.0 (default: the latest release)
  --dir <path>      install directory (default: /opt/bubblefighter-fantasy as root, ~/bubblefighter-fantasy otherwise)
  --no-runtime      fail instead of downloading the ASP.NET Core Runtime 10 when it is missing
  --service         as root: install and enable a systemd unit (bubblefighter-fantasy.service)
  --start           start the server in the foreground when the installation finishes
  --docker          run the server with Docker Compose instead
  --with-mysql      with --docker: also run a MySQL 8.4 container for the server's data
  --admin-port <n>  port of the setup assistant and administration WebUI (default 8080)
  --mysql-port <n>  with --with-mysql: loopback port of the bundled MySQL (default 3306)
  --from <dir>      install from a directory holding the release assets instead of downloading them
  -h, --help        show this help

Upgrading: run the script again. The state directory and earlier releases are kept.
EOF
}

while [ $# -gt 0 ]; do
    case "$1" in
        --version) VERSION="${2:?--version needs a tag}"; shift 2 ;;
        --dir) DIRECTORY="${2:?--dir needs a path}"; shift 2 ;;
        --no-runtime) INSTALL_RUNTIME=0; shift ;;
        --service) SERVICE=1; shift ;;
        --start) START=1; shift ;;
        --docker) DOCKER=1; shift ;;
        --with-mysql) WITH_MYSQL=1; shift ;;
        --admin-port) ADMIN_PORT="${2:?--admin-port needs a port}"; shift 2 ;;
        --mysql-port) MYSQL_PORT="${2:?--mysql-port needs a port}"; shift 2 ;;
        --from) FROM="${2:?--from needs a directory}"; shift 2 ;;
        -h|--help) usage; exit 0 ;;
        *) echo "error: unknown option '$1' (see --help)" >&2; exit 2 ;;
    esac
done

fail() { echo "error: $*" >&2; exit 1; }
step() { printf '==> %s\n' "$*"; }

[ "$(uname -s)" = "Linux" ] || fail "this installer supports Linux only; on Windows download the zip package."
case "$(uname -m)" in
    x86_64|amd64) RUNTIME_ID="linux-x64" ;;
    aarch64|arm64) RUNTIME_ID="linux-arm64" ;;
    *) fail "unsupported processor '$(uname -m)'; packages exist for x86_64 and aarch64." ;;
esac
for tool in curl tar sha256sum; do
    command -v "$tool" >/dev/null 2>&1 || fail "'$tool' is required."
done
if [ "$DOCKER" -eq 1 ]; then
    command -v docker >/dev/null 2>&1 || fail "--docker needs Docker Engine."
    docker compose version >/dev/null 2>&1 || fail "--docker needs the Docker Compose plugin."
    [ "$SERVICE" -eq 0 ] || fail "--service and --docker cannot be combined; Compose restarts the containers."
elif [ "$WITH_MYSQL" -eq 1 ]; then
    fail "--with-mysql needs --docker."
fi
for port in "$ADMIN_PORT" "$MYSQL_PORT"; do
    [ -z "$port" ] || { [ "$port" -ge 1 ] 2>/dev/null && [ "$port" -le 65535 ]; } || fail "'$port' is not a port number."
done
if [ "$SERVICE" -eq 1 ]; then
    [ "$(id -u)" -eq 0 ] || fail "--service needs root."
    command -v systemctl >/dev/null 2>&1 || fail "--service needs systemd."
fi

if [ -n "$FROM" ]; then
    [ -d "$FROM" ] || fail "'$FROM' is not a directory."
    [ "$VERSION" != "latest" ] || fail "--from needs --version to name the release folder."
    TAG="$VERSION"
elif [ "$VERSION" = "latest" ]; then
    BASE="https://github.com/$REPOSITORY/releases/latest/download"
    # The latest-release address redirects to the tag; resolve it so the release folder carries its name.
    TAG="$(curl -fsSLI -o /dev/null -w '%{url_effective}' "https://github.com/$REPOSITORY/releases/latest")"
    TAG="${TAG##*/}"
    [ -n "$TAG" ] && [ "$TAG" != "latest" ] || fail "no published release was found for $REPOSITORY."
else
    TAG="$VERSION"
    BASE="https://github.com/$REPOSITORY/releases/download/$TAG"
fi
ASSET="bubblefighter-fantasy-$RUNTIME_ID.tar.gz"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

fetch() {
    if [ -n "$FROM" ]; then cp "$FROM/$1" "$WORK/$1" || fail "'$FROM/$1' is missing."
    else curl -fSL --retry 3 -o "$WORK/$1" "$BASE/$1"; fi
}
step "Fetching $ASSET ($TAG)"
fetch "$ASSET"
fetch SHA256SUMS
step "Checking the download"
(cd "$WORK" && grep "  $ASSET\$" SHA256SUMS | sha256sum -c --quiet -) || fail "the checksum of $ASSET does not match SHA256SUMS."

if [ "$DOCKER" -eq 1 ]; then
    STACK="$DIRECTORY/docker"
    step "Preparing the Docker stack in $STACK"
    mkdir -p "$STACK"
    rm -rf "$STACK/server.partial"
    mkdir "$STACK/server.partial"
    tar -xzf "$WORK/$ASSET" -C "$STACK/server.partial" --strip-components=1
    rm -rf "$STACK/server"
    mv "$STACK/server.partial" "$STACK/server"
    cat > "$STACK/Dockerfile" <<'EOF'
# Generated by install.sh from the prebuilt Linux package in server/.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --chown=1654:1654 server/ ./
RUN mkdir /app/state && chown 1654:1654 /app/state
USER 1654:1654
ENV BFF_STATE_DIR=/app/state
ENTRYPOINT ["dotnet", "BubbleFighter.Fantasy.Host.dll"]
CMD ["start"]
EOF
    # A stack that was created with MySQL keeps it on upgrade, so its database is never orphaned.
    if [ -f "$STACK/.env" ] && grep -q '^BFF_MYSQL_PASSWORD=' "$STACK/.env"; then WITH_MYSQL=1; fi
    # Host networking publishes whichever game ports the chosen clients use; the bundled MySQL stays on loopback.
    {
        cat <<'EOF'
# Generated by install.sh. Settings live in .env; the installation state lives in the bff-state volume.
name: bubblefighter-fantasy
services:
  bff:
    build: .
    image: bubblefighter-fantasy:${BFF_RELEASE}
    network_mode: host
    env_file: .env
    volumes:
      - bff-state:/app/state
    restart: unless-stopped
EOF
        if [ "$WITH_MYSQL" -eq 1 ]; then
            cat <<'EOF'
    depends_on:
      mysql:
        condition: service_healthy
  mysql:
    image: mysql:8.4
    environment:
      MYSQL_ROOT_PASSWORD: ${BFF_MYSQL_ROOT_PASSWORD}
      MYSQL_DATABASE: bf_game_db
      MYSQL_USER: bff
      MYSQL_PASSWORD: ${BFF_MYSQL_PASSWORD}
    command: ["--character-set-server=utf8mb4", "--collation-server=utf8mb4_unicode_ci"]
    ports:
      - "127.0.0.1:${BFF_MYSQL_PORT}:3306"
    volumes:
      - mysql-data:/var/lib/mysql
    healthcheck:
      test: ["CMD", "mysqladmin", "ping", "-h", "127.0.0.1"]
      interval: 5s
      timeout: 3s
      retries: 30
    restart: unless-stopped
EOF
        fi
        printf 'volumes:\n  bff-state:\n'
        [ "$WITH_MYSQL" -eq 0 ] || printf '  mysql-data:\n'
    } > "$STACK/compose.yaml"
    secret() { head -c 24 /dev/urandom | od -An -tx1 | tr -d ' \n'; }
    ( umask 077; touch "$STACK/.env" )
    set_env() { sed -i "/^$1=/d" "$STACK/.env"; echo "$1=$2" >> "$STACK/.env"; }
    grep -q '^BFF_ADMIN_PORT=' "$STACK/.env" || set_env BFF_ADMIN_PORT "${ADMIN_PORT:-8080}"
    [ -z "$ADMIN_PORT" ] || set_env BFF_ADMIN_PORT "$ADMIN_PORT"
    if [ "$WITH_MYSQL" -eq 1 ] && ! grep -q '^BFF_MYSQL_PASSWORD=' "$STACK/.env"; then
        # Generated once; an upgrade keeps the passwords the database volume was created with.
        set_env BFF_MYSQL_PASSWORD "$(secret)"
        set_env BFF_MYSQL_ROOT_PASSWORD "$(secret)"
        set_env BFF_MYSQL_PORT "$MYSQL_PORT"
    fi
    set_env BFF_RELEASE "$TAG"
    step "Building and starting the containers"
    (cd "$STACK" && docker compose up -d --build --remove-orphans)
    PORT="$(sed -n 's/^BFF_ADMIN_PORT=//p' "$STACK/.env")"
    echo
    echo "Bubble Fighter Fantasy $TAG is running in Docker ($STACK)."
    echo "  Open http://127.0.0.1:$PORT/ to finish setup."
    echo "  On a remote machine, forward the port first: ssh -L $PORT:127.0.0.1:$PORT <user>@<server>"
    if [ "$WITH_MYSQL" -eq 1 ]; then
        echo "  Database for the assistant: MySQL, host 127.0.0.1, port $(sed -n 's/^BFF_MYSQL_PORT=//p' "$STACK/.env"),"
        echo "  database bf_game_db, user bff, password: the BFF_MYSQL_PASSWORD line of $STACK/.env"
    else
        echo "  Database: choose SQLite in the assistant (stored in the bff-state volume), or an existing MySQL."
        echo "  Run the installer with --docker --with-mysql to add a bundled MySQL 8.4 instead."
    fi
    echo "  Logs:    cd $STACK && docker compose logs -f bff"
    echo "  Upgrade: run this installer again with --docker; the volumes and .env are kept."
    exit 0
fi

has_runtime() { "$1" --list-runtimes 2>/dev/null | grep -q '^Microsoft\.AspNetCore\.App 10\.'; }
DOTNET=""
if [ -x "$DIRECTORY/dotnet/dotnet" ] && has_runtime "$DIRECTORY/dotnet/dotnet"; then
    DOTNET="$DIRECTORY/dotnet/dotnet"
elif command -v dotnet >/dev/null 2>&1 && has_runtime "$(command -v dotnet)"; then
    DOTNET="$(command -v dotnet)"
elif [ "$INSTALL_RUNTIME" -eq 1 ]; then
    step "Installing the ASP.NET Core Runtime 10 into $DIRECTORY/dotnet"
    curl -fsSL --retry 3 -o "$WORK/dotnet-install.sh" https://dot.net/v1/dotnet-install.sh
    bash "$WORK/dotnet-install.sh" --channel 10.0 --runtime aspnetcore --install-dir "$DIRECTORY/dotnet" --no-path
    DOTNET="$DIRECTORY/dotnet/dotnet"
    has_runtime "$DOTNET" || fail "the ASP.NET Core Runtime 10 did not install."
else
    fail "the ASP.NET Core Runtime 10 is missing; install it or run without --no-runtime."
fi

RELEASE="$DIRECTORY/releases/$TAG"
step "Unpacking into $RELEASE"
mkdir -p "$DIRECTORY/releases" "$DIRECTORY/state"
rm -rf "$RELEASE.partial"
mkdir "$RELEASE.partial"
tar -xzf "$WORK/$ASSET" -C "$RELEASE.partial" --strip-components=1
"$DOTNET" "$RELEASE.partial/BubbleFighter.Fantasy.Host.dll" profiles list --profiles-dir "$RELEASE.partial/profiles" >/dev/null \
    || fail "the unpacked server does not start; the previous installation is unchanged."
rm -rf "$RELEASE"
mv "$RELEASE.partial" "$RELEASE"
ln -sfn "releases/$TAG" "$DIRECTORY/current"

PORT_LINE=""
[ -z "$ADMIN_PORT" ] || PORT_LINE="export BFF_ADMIN_PORT=\"\${BFF_ADMIN_PORT:-$ADMIN_PORT}\""
cat > "$DIRECTORY/bff" <<EOF
#!/bin/sh
# Generated by install.sh. With no arguments the setup assistant starts; see 'bff --help'.
export BFF_STATE_DIR="\${BFF_STATE_DIR:-$DIRECTORY/state}"
$PORT_LINE
export DOTNET_ROOT="$(dirname "$(readlink -f "$DOTNET")")"
exec "$DOTNET" "$DIRECTORY/current/BubbleFighter.Fantasy.Host.dll" "\$@"
EOF
chmod 755 "$DIRECTORY/bff"

if [ "$SERVICE" -eq 1 ]; then
    step "Installing bubblefighter-fantasy.service"
    id bff >/dev/null 2>&1 || useradd --system --home-dir "$DIRECTORY" --shell /usr/sbin/nologin bff
    chown -R bff:bff "$DIRECTORY/state"
    cat > /etc/systemd/system/bubblefighter-fantasy.service <<EOF
[Unit]
Description=Bubble Fighter Fantasy server
After=network-online.target
Wants=network-online.target

[Service]
User=bff
ExecStart=$DIRECTORY/bff
Restart=on-failure
KillSignal=SIGTERM
TimeoutStopSec=30

[Install]
WantedBy=multi-user.target
EOF
    systemctl daemon-reload
    systemctl enable --now bubblefighter-fantasy.service
fi

echo
echo "Bubble Fighter Fantasy $TAG is installed in $DIRECTORY."
echo "  Start:   $DIRECTORY/bff"
echo "  Then open http://127.0.0.1:${ADMIN_PORT:-8080}/ to finish setup."
echo "  On a remote machine, forward the port first: ssh -L ${ADMIN_PORT:-8080}:127.0.0.1:${ADMIN_PORT:-8080} <user>@<server>"
echo "  Upgrade: run this installer again; $DIRECTORY/state is kept."
if [ "$START" -eq 1 ] && [ "$SERVICE" -eq 0 ]; then
    echo
    exec "$DIRECTORY/bff"
fi
