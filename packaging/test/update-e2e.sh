#!/usr/bin/env bash
#
# Сквозная проверка обновления EliteRec в чистом Debian 13 (docker), без настоящего канала и ключей:
#
#   packaging/test/update-e2e.sh
#
# Тестовая пара ключей и тестовый канал (HTTPS со своим сертификатом, Basic tester:test-pass,
# раскладка как у update.elitesip.vip). Ставится 0.1.0, на канал кладётся 0.1.1 и проверяется:
# чужая подпись — отказ; идёт запись — установка отложена, EliteRec не тронут; запись кончилась —
# EliteRec закрыт, 0.1.1 поставлен, поднят; повторный такт — «актуально»; подменённый пакет — отказ.
set -euo pipefail
root=$(cd "$(dirname "$0")/../.." && pwd)
work="$root/artifacts/update-test"
rm -rf "$work"; mkdir -p "$work/channel/releases/eliterec/linux"
dotnet build "$root/tools/EliteRec.ReleaseKit" -c Release --nologo >/dev/null
rk() { dotnet "$root/tools/EliteRec.ReleaseKit/bin/Release/net10.0/releasekit.dll" "$@" >/dev/null; }
rk keygen "$work/keys"; rk keygen "$work/other"
python3 -I -c 'import json,sys; json.dump({"updates":{"baseURL":"https://channel.test:8443","user":"tester","password":"test-pass"},
    "releasesPublicKey":open(sys.argv[1]).read().strip()}, open(sys.argv[2],"w"))' "$work/keys/releases.pub" "$work/provisioning.json"
for v in 0.1.0 0.1.1; do "$root/packaging/build-deb.sh" --version "$v" --provisioning "$work/provisioning.json" --output "$work" >/dev/null; done
cp "$work/EliteRec-0.1.1.deb" "$work/channel/releases/eliterec/linux/"
manifest() { python3 -I -c 'import json,hashlib,sys
d=open(sys.argv[2],"rb").read()
json.dump({"format":1,"version":sys.argv[1],"url":"https://channel.test:8443/releases/eliterec/linux/EliteRec-%s.deb"%sys.argv[1],
    "sha256":sys.argv[3] or hashlib.sha256(d).hexdigest(),"size":len(d),"published_at":"2026-10-28T12:00:00Z","notes":""}, open(sys.argv[4],"w"))' "$@"; }
manifest 0.1.1 "$work/EliteRec-0.1.1.deb" "" "$work/manifest.json"
manifest 0.1.2 "$work/EliteRec-0.1.1.deb" "$(printf '0%.0s' {1..64})" "$work/manifest.tampered.json"
rk sign "$work/manifest.json" "$work/keys/releases.key" "$work/current.good.json"
rk sign "$work/manifest.json" "$work/other/releases.key" "$work/current.foreign.json"
rk sign "$work/manifest.tampered.json" "$work/keys/releases.key" "$work/current.tampered.json"
cp "$root/packaging/test/channel.py" "$root/packaging/test/in-container.sh" "$work/"
docker run --rm -v "$work:/work" debian:trixie bash /work/in-container.sh 2>&1 | grep -vE "^(debconf|dpkg-preconfigure)|Reading|Building|Need to get|After this|Get:|Preparing|Unpacking|Setting up|The following|^  eliterec|upgraded"
