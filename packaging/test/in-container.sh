#!/bin/bash
# Сквозная проверка обновления в чистом Debian 13 (запускается в контейнере, от root).
set -u
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq >/dev/null && apt-get install -y -qq python3 openssl ca-certificates >/dev/null 2>&1
openssl req -x509 -newkey rsa:2048 -nodes -days 1 -subj "/CN=channel.test" -addext "subjectAltName=DNS:channel.test" \
    -keyout /tmp/channel.key -out /tmp/channel.crt 2>/dev/null
cp /tmp/channel.crt /usr/local/share/ca-certificates/channel.crt && update-ca-certificates >/dev/null 2>&1
echo "127.0.0.1 channel.test" >> /etc/hosts
C=/work/channel/releases/eliterec/linux
python3 /work/channel.py /work/channel & sleep 1

apt-get install -y -qq /work/EliteRec-0.1.0.deb >/dev/null 2>&1; echo "установлено: $(dpkg-query -W -f='${Version}' eliterec)"
ls -ld /var/lib/eliterec/updates | awk '{print "общий каталог:", $1}'
mkdir -p /run/eliterec-updater

echo "--- 1. чужая подпись"
cp /work/current.foreign.json $C/current.json
/opt/eliterec/eliterec-updater; echo "код $? | версия $(dpkg-query -W -f='${Version}' eliterec)"

echo "--- 2. идёт запись (живой процесс EliteRec отметил «пишу»)"
cp /work/current.good.json $C/current.json
/opt/eliterec/EliteRec --watch-devices >/dev/null 2>&1 & app=$!; sleep 2
touch /var/lib/eliterec/updates/recording-$app
/opt/eliterec/eliterec-updater; echo "код $? | версия $(dpkg-query -W -f='${Version}' eliterec) | отметка «ставлю» снята: $([ -e /run/eliterec-updater/installing ] && echo нет || echo да) | EliteRec жив: $(kill -0 $app 2>/dev/null && echo да || echo нет)"

echo "--- 3. запись кончилась — обновление ставится, EliteRec закрыт штатно"
rm /var/lib/eliterec/updates/recording-$app
/opt/eliterec/eliterec-updater; echo "код $? | версия $(dpkg-query -W -f='${Version}' eliterec) | EliteRec закрыт: $(kill -0 $app 2>/dev/null && echo нет || echo да)"

echo "--- 4. повторный такт — актуально, ничего не качается"
/opt/eliterec/eliterec-updater; echo "код $?"

echo "--- 5. подменённый на канале пакет 0.1.2 (отпечаток в подписанном манифесте другой)"
cp $C/EliteRec-0.1.1.deb $C/EliteRec-0.1.2.deb
cp /work/current.tampered.json $C/current.json
/opt/eliterec/eliterec-updater; echo "код $? | версия $(dpkg-query -W -f='${Version}' eliterec) | скачанное удалено: $(ls /var/lib/eliterec/updater/ | grep -c 0.1.2 | sed 's/^0$/да/')"
