#!/bin/bash
# Сборка NamazBar для macOS без Xcode-проекта: swiftc → NamazBar.app → NamazBar.dmg.
# Запускается на Mac или на сервере GitHub Actions (macos-*). Результат — в mac/out/.
# Подпись ad-hoc (без аккаунта Apple Developer): при первом запуске macOS спросит разрешение
# (Системные настройки → Конфиденциальность и безопасность → «Всё равно открыть»).
set -euo pipefail
cd "$(dirname "$0")"
VERSION="${VERSION:-1.0.0}"
OUT=out
APP="$OUT/NamazBar.app"
rm -rf "$OUT"; mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources/fonts" "$OUT/preview"

# универсальный бинарник: Apple Silicon + Intel, macOS 13+
for arch in arm64 x86_64; do
  swiftc -O -parse-as-library -target "$arch-apple-macos13.0" \
    -framework AppKit -framework SwiftUI -framework ServiceManagement \
    Sources/*.swift -o "$OUT/NamazBar-$arch"
done
lipo -create "$OUT/NamazBar-arm64" "$OUT/NamazBar-x86_64" -output "$APP/Contents/MacOS/NamazBar"
rm "$OUT"/NamazBar-arm64 "$OUT"/NamazBar-x86_64

cp ../fonts/*.ttf "$APP/Contents/Resources/fonts/"
sed "s/__VERSION__/$VERSION/g" Info.plist > "$APP/Contents/Info.plist"

# иконка: приложение само рисует её в 1024×1024, iconutil собирает .icns
ICONSET="$OUT/AppIcon.iconset"; mkdir -p "$ICONSET"
"$APP/Contents/MacOS/NamazBar" --icon "$OUT/icon1024.png"
for s in 16 32 128 256 512; do
  sips -z $s $s "$OUT/icon1024.png" --out "$ICONSET/icon_${s}x${s}.png" >/dev/null
  sips -z $((s*2)) $((s*2)) "$OUT/icon1024.png" --out "$ICONSET/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns"
rm -rf "$ICONSET"

# подпись ad-hoc — без неё macOS на Apple Silicon не запустит приложение вообще
codesign --force --deep --sign - "$APP"
codesign --verify --verbose "$APP"

# картинки состояний для проверки дизайна (строка меню, карточка, перерыв)
"$APP/Contents/MacOS/NamazBar" --preview "$OUT/preview"
cp "$OUT/icon1024.png" "$OUT/preview/icon.png"

# установочный образ: приложение + ярлык «Программы» (перетащить)
STAGE="$OUT/dmg"; mkdir -p "$STAGE"
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"
hdiutil create -volname "NamazBar" -srcfolder "$STAGE" -fs HFS+ -format UDZO -ov "$OUT/NamazBar-$VERSION.dmg"
rm -rf "$STAGE"
echo "OK: $OUT/NamazBar-$VERSION.dmg"
