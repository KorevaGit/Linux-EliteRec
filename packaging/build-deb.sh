#!/usr/bin/env bash
#
# Пакет .deb: самодостаточная публикация с ReadyToRun, раскладка по системе, сборка пакета.
# Устроено как tools/build-deb.sh у EliteSIP для Linux — те же пути и правила.
#
#   packaging/build-deb.sh --version 0.1.0
#
# Что где ляжет:
#
#   /opt/eliterec/                         программа и рантайм .NET (root, менеджер не пишет)
#     EliteRec                             приложение — его зовут ярлык и автозапуск
#   /usr/share/applications/eliterec.desktop
#   /etc/xdg/autostart/eliterec.desktop    автозапуск в трей (--tray), выключается у себя
#   /usr/share/icons/hicolor/*/apps/eliterec.png
#   /usr/share/doc/eliterec/{copyright,changelog.gz}
#
# Зависимости (packaging/debian/control) — с альтернативами под Debian 13 и Mint 22:
# у EliteSIP одна жёсткая libicu74 сломала установку на Debian 13 (docs/PLAN.md, R6).

set -euo pipefail

root=$(cd "$(dirname "$0")/.." && pwd)
version=
output="$root/artifacts"
maintainer="EliteSIP"
# Две параллельные сборки: ReadyToRun на всех ядрах съедает память (урок EliteSIP).
jobs=${ELITEREC_BUILD_JOBS:-2}

usage() {
    sed -n '2,18p' "$0" | sed 's/^# \{0,1\}//'
    echo "ключи: --version X.Y.Z [--output КАТАЛОГ] [--maintainer ИМЯ]"
    exit 2
}

fail() {
    echo "пакет не собран: $*" >&2
    exit 1
}

while [ $# -gt 0 ]; do
    case "$1" in
        --version) version=$2; shift 2 ;;
        --output) output=$2; shift 2 ;;
        --maintainer) maintainer=$2; shift 2 ;;
        *) usage ;;
    esac
done

[[ $version =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || usage
command -v dpkg-deb >/dev/null || fail "нет dpkg-deb"

stage="$root/artifacts/package/eliterec_$version"
program="$stage/opt/eliterec"
rm -rf "$stage"
mkdir -p "$program" "$output"

# --- Публикация -------------------------------------------------------------
#
# Самодостаточная: ни в EliteLinux, ни в Mint .NET не стоит, а ставить его на каждую
# машину — ещё один источник пакетов мимо нашей линии. ReadyToRun — меньше JIT при
# запуске: на HDD и A6 это секунды до появления окна.

echo "== публикация $version"
dotnet publish "$root/src/EliteRec.App/EliteRec.App.csproj" \
    --configuration Release \
    --runtime linux-x64 \
    --self-contained true \
    --output "$program" \
    --nologo \
    --disable-build-servers \
    -maxcpucount:"$jobs" \
    -p:Version="$version" \
    -p:PublishReadyToRun=true \
    -p:DisableTransitiveFrameworkReferenceDownloads=true \
    -p:DebugType=none \
    || fail "не опубликовался EliteRec.App"

# Короткое имя: по нему ищут процесс и зовут ярлык.
mv "$program/EliteRec.App" "$program/EliteRec"

# --- Раскладка по системе ---------------------------------------------------

echo "== раскладка"
install -D -m 0644 "$root/packaging/applications/eliterec.desktop" "$stage/usr/share/applications/eliterec.desktop"
install -D -m 0644 "$root/packaging/autostart/eliterec.desktop" "$stage/etc/xdg/autostart/eliterec.desktop"
for size in 16 32 48 64 128 256 512; do
    install -D -m 0644 "$root/docs/brand/eliterec-$size.png" "$stage/usr/share/icons/hicolor/${size}x${size}/apps/eliterec.png"
done

doc="$stage/usr/share/doc/eliterec"
install -D -m 0644 "$root/packaging/debian/copyright" "$doc/copyright"
{
    echo "eliterec ($version) stable; urgency=medium"
    echo
    echo "  * Выпуск EliteRec $version для Linux Mint 22 / Ubuntu 24.04 и Debian 13."
    echo "  * Коммит: $(git -C "$root" describe --tags --always --dirty 2>/dev/null || echo неизвестен)."
    echo
    echo " -- $maintainer  $(LC_ALL=C date -R)"
} | gzip -9n > "$doc/changelog.gz"
chmod 0644 "$doc/changelog.gz"

# Права явно: umask сборщика не должен попасть в /opt на машине менеджера.
find "$stage" -type d -exec chmod 0755 {} +
find "$program" -type f -exec chmod 0644 {} +
chmod 0755 "$program/EliteRec" "$program/createdump"

# --- Пакет ------------------------------------------------------------------

echo "== пакет"
mkdir -p "$stage/DEBIAN"
size=$(du -sk --exclude=DEBIAN "$stage" | cut -f1)
sed -e "s/@VERSION@/$version/" -e "s/@MAINTAINER@/$maintainer/" -e "s/@SIZE@/$size/" \
    "$root/packaging/debian/control" > "$stage/DEBIAN/control"

package="$output/EliteRec-$version.deb"
# --root-owner-group: всё в пакете — root, без fakeroot. xz: меньше по каналу на каждую машину.
dpkg-deb --root-owner-group -Zxz --build "$stage" "$package" >/dev/null || fail "dpkg-deb отказал"

echo "  пакет:   $package"
echo "  размер:  $(du -h "$package" | cut -f1) (распакованный — $((size / 1024)) МБ)"
