#!/usr/bin/env bash
#
# Выпуск EliteRec для Linux — по образцу tools/release.sh EliteSIP для Linux:
#
#   packaging/release.sh --version 0.1.1 [--notes "что нового"] [--dry-run]
#
#   1. дерево чистое, версия растёт относительно последней метки v*;
#   2. сборка и все тесты; провалился хоть один — выпуска нет;
#   3. пакет .deb с заводской настройкой (packaging/build-deb.sh);
#   4. манифест releases/eliterec/linux/current.json, подпись Ed25519 ключом линии EliteRec;
#   5. проверка манифеста кодом обновляльщика (releasekit verify);
#   6. метка v<версия> — локально; выкладывает packaging/publish.sh, отправку метки делает человек.
#
# Ключи — в ~/.config/EliteRec-release (вне Git): releases.key (закрытый, 0600), provisioning.json
# (канал и пара — те же, что у EliteSIP; открытый ключ — свой).
set -euo pipefail

root=$(cd "$(dirname "$0")/.." && pwd)
keys="$HOME/.config/EliteRec-release"
version=
notes=
dry_run=false
base_url=

fail() { echo; echo "выпуск остановлен: $*" >&2; exit 1; }
step() { echo; echo "== $*"; }

while [ $# -gt 0 ]; do
    case "$1" in
        --version) version=$2; shift 2 ;;
        --notes) notes=$2; shift 2 ;;
        --dry-run) dry_run=true; shift ;;
        *) sed -n '2,16p' "$0" | sed 's/^# \{0,1\}//'; exit 2 ;;
    esac
done
[[ $version =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || fail "версия — три числа: --version X.Y.Z"
[ -f "$keys/releases.key" ] || fail "нет $keys/releases.key (releasekit keygen $keys)"
[ -f "$keys/provisioning.json" ] || fail "нет $keys/provisioning.json"
base_url=$(python3 -I -c 'import json,sys; print(json.load(open(sys.argv[1]))["updates"]["baseURL"].rstrip("/"))' "$keys/provisioning.json")

step "дерево и версия"
cd "$root"
if [ -n "$(git status --porcelain)" ]; then
    $dry_run && echo "  ВНИМАНИЕ: в дереве несохранённые правки" || fail "в дереве несохранённые правки — выпуск невоспроизводим"
fi
last=$(git tag --list 'v*' --sort=-v:refname | head -1)
if [ -n "$last" ]; then
    newest=$(printf '%s\n%s\n' "${last#v}" "$version" | sort -V | tail -1)
    [ "$newest" = "$version" ] && [ "${last#v}" != "$version" ] || fail "версия $version не новее $last"
fi

step "сборка и тесты"
dotnet test "$root/EliteRec.Linux.slnx" -c Release --nologo || fail "тесты не прошли"
dotnet build "$root/tools/EliteRec.ReleaseKit" -c Release --nologo >/dev/null || fail "releasekit не собрался"
releasekit() { dotnet "$root/tools/EliteRec.ReleaseKit/bin/Release/net10.0/releasekit.dll" "$@"; }

# Открытый ключ в настройке — пара к закрытому: иначе машины отвергнут каждый выпуск.
[ "$(releasekit public "$keys/releases.key")" = "$(python3 -I -c 'import json,sys; print(json.load(open(sys.argv[1]))["releasesPublicKey"])' "$keys/provisioning.json")" ] \
    || fail "releasesPublicKey в provisioning.json — не пара к releases.key"

step "пакет"
out="$root/artifacts/release/$version"
rm -rf "$out"; mkdir -p "$out"
"$root/packaging/build-deb.sh" --version "$version" --provisioning "$keys/provisioning.json" --output "$out" || fail "пакет не собран"
package="$out/EliteRec-$version.deb"

step "манифест и подпись"
python3 -I - "$version" "$base_url/releases/eliterec/linux/EliteRec-$version.deb" "$(releasekit hash "$package")" \
    "$(stat -c %s "$package")" "$notes" > "$out/manifest.json" <<'PY'
import json, sys, datetime
version, url, sha256, size, notes = sys.argv[1:6]
print(json.dumps({"format": 1, "version": version, "url": url, "sha256": sha256, "size": int(size),
    "published_at": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"), "notes": notes},
    ensure_ascii=False), end="")
PY
releasekit sign "$out/manifest.json" "$keys/releases.key" "$out/current.json"
echo -n "$(python3 -I -c 'import json,sys; print(json.load(open(sys.argv[1]))["releasesPublicKey"])' "$keys/provisioning.json")" > "$out/releases.pub"
releasekit verify "$out/current.json" "$out/releases.pub" "$base_url/releases/eliterec/linux/current.json" || fail "манифест не прошёл проверку кодом обновляльщика"

if ! $dry_run; then
    step "метка"
    git tag -a "v$version" -m "EliteRec $version" && echo "  метка v$version (не отправлена: git push origin v$version)"
fi

step "готово"
echo "  $out/EliteRec-$version.deb"
echo "  $out/current.json"
echo "  выкладка: packaging/publish.sh --version $version"
