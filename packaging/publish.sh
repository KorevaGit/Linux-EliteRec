#!/usr/bin/env bash
#
# Выкладка выпуска EliteRec на канал обновлений и проверка того, что канал отдаёт (как tools/publish.sh
# EliteSIP для Linux):
#
#   packaging/publish.sh --version 0.1.1 [--dry-run | --verify-only]
#
#   1. манифест сверяется с пакетом до всякой сети: версия, адрес в каталоге EliteRec, sha256, размер;
#   2. кладётся пакет, ПОТОМ манифест (PUT /internal/objects/<ключ>, Bearer): наоборот — машины пошли бы
#      за файлом, которого ещё нет;
#   3. с канала забирается то, что он отдаёт, с парой Basic: манифест байт в байт, пакет по sha256,
#      и 401 без пары.
#
# Доступы — ~/.config/EliteSIP-release/publish.local.json (тот же сервер, что у EliteSIP: update.baseURL,
# basicUser, basicPassword, adminToken). Секреты уходят в curl через стандартный ввод (-K -), а не
# аргументами: аргументы процесса видны всем.
set -euo pipefail

root=$(cd "$(dirname "$0")/.." && pwd)
config="$HOME/.config/EliteSIP-release/publish.local.json"
version=
dry_run=false
verify_only=false

fail() { echo; echo "выкладка остановлена: $*" >&2; exit 1; }
ok() { echo "  ок: $*"; }

while [ $# -gt 0 ]; do
    case "$1" in
        --version) version=$2; shift 2 ;;
        --config) config=$2; shift 2 ;;
        --dry-run) dry_run=true; shift ;;
        --verify-only) verify_only=true; shift ;;
        *) sed -n '2,18p' "$0" | sed 's/^# \{0,1\}//'; exit 2 ;;
    esac
done
[[ $version =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || fail "версия — три числа: --version X.Y.Z"
[ -f "$config" ] || fail "нет $config"
out="$root/artifacts/release/$version"
package="$out/EliteRec-$version.deb"
envelope="$out/current.json"
[ -f "$package" ] && [ -f "$envelope" ] || fail "нет выпуска в $out — его собирает packaging/release.sh"

json() { python3 -I -c 'import json,sys
d=json.load(open(sys.argv[1], encoding="utf-8-sig"))
for p in sys.argv[2].split("."): d=d.get(p) if isinstance(d,dict) else None
print("" if d is None else d)' "$1" "$2"; }
base_url=$(json "$config" update.baseURL); base_url=${base_url%/}
basic="$(json "$config" update.basicUser):$(json "$config" update.basicPassword)"
token=$(json "$config" update.adminToken)
[ -n "$base_url" ] && [ "$basic" != ":" ] || fail "в $config нет update.baseURL или пары"
quote() { local v=${1//\\/\\\\}; printf '"%s"' "${v//\"/\\\"}"; }
request() { local secret=$1; shift; printf '%s\n' "$secret" | curl -sS -K - -w '%{http_code}' "$@"; }

echo "== 1. выпуск $version → $base_url"
payload=$(python3 -I -c 'import base64,json,sys; print(base64.b64decode(json.load(open(sys.argv[1]))["payload"]).decode())' "$envelope")
field() { python3 -I -c 'import json,sys; print(json.loads(sys.argv[1]).get(sys.argv[2],""))' "$payload" "$1"; }
[ "$(field version)" = "$version" ] || fail "в манифесте версия $(field version)"
[ "$(field url)" = "$base_url/releases/eliterec/linux/EliteRec-$version.deb" ] || fail "адрес в манифесте: $(field url)"
[ "$(field sha256)" = "$(sha256sum "$package" | cut -d' ' -f1)" ] || fail "отпечаток в манифесте не совпал с пакетом"
[ "$(field size)" = "$(stat -c %s "$package")" ] || fail "размер в манифесте не совпал с пакетом"
ok "манифест сходится с пакетом"
$dry_run && { echo "проверка без выкладки — готово"; exit 0; }

if ! $verify_only; then
    [ -n "$token" ] || fail "в $config нет update.adminToken"
    echo "== 2. выкладка"
    for pair in "$package:releases/eliterec/linux/EliteRec-$version.deb" "$envelope:releases/eliterec/linux/current.json"; do
        file=${pair%%:*}; key=${pair#*:}
        code=$(request "header = $(quote "Authorization: Bearer $token")" -o /dev/null -X PUT --data-binary "@$file" "$base_url/internal/objects/$key")
        [ "$code" = 204 ] || [ "$code" = 200 ] || fail "PUT $key: $code"
        ok "$key"
    done
fi

echo "== 3. что отдаёт канал"
tmp=$(mktemp -d); trap 'rm -rf "$tmp"' EXIT
[ "$(curl -sS -o /dev/null -w '%{http_code}' "$base_url/releases/eliterec/linux/current.json")" = 401 ] || fail "манифест отдаётся без пары"
code=$(request "user = $(quote "$basic")" -o "$tmp/current.json" "$base_url/releases/eliterec/linux/current.json")
[ "$code" = 200 ] && cmp -s "$tmp/current.json" "$envelope" || fail "манифест на канале не тот ($code)"
code=$(request "user = $(quote "$basic")" -o "$tmp/package.deb" "$base_url/releases/eliterec/linux/EliteRec-$version.deb")
[ "$code" = 200 ] && cmp -s "$tmp/package.deb" "$package" || fail "пакет на канале не тот ($code)"
ok "канал отдаёт выпуск $version; машины поставят его в течение 10 минут (кроме тех, где идёт запись)"
