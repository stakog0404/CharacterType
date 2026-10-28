#!/bin/bash
set -e

# 이동: 스크립트가 위치한 폴더로 이동
cd "$(dirname "$0")"

echo "=========================================================="
echo "      CharacterType for macOS 원클릭 앱 생성기            "
echo "=========================================================="
echo ""

# 1. swiftc 컴파일러 확인
if ! command -v swiftc &> /dev/null; then
    echo "[!] Swift 컴파일러가 감지되지 않았습니다."
    echo "    macOS 개발자 도구를 설치합니다. 잠시 후 나타나는 팝업에서 [설치]를 눌러주세요."
    xcode-select --install
    echo "    설치 완료 후 이 스크립트를 다시 더블 클릭해 주세요."
    exit 1
fi

echo "[1/4] 초경량 네이티브 바이너리 컴파일 중..."
swiftc -O -framework Cocoa -framework CoreGraphics main.swift -o CharacterType_bin

echo "[2/4] CharacterType.app 번들 패키징 중..."
APP="CharacterType.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS"
mkdir -p "$APP/Contents/Resources"

mv CharacterType_bin "$APP/Contents/MacOS/CharacterType"
chmod +x "$APP/Contents/MacOS/CharacterType"

# 리소스 이미지 복사
if [ -d "resources" ]; then
    cp -R resources/* "$APP/Contents/Resources/"
fi

# Info.plist 생성 (Dock 아이콘 없는 데스크톱 상주 위젯 설정)
cat << 'EOF' > "$APP/Contents/Info.plist"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key>
    <string>CharacterType</string>
    <key>CFBundleIdentifier</key>
    <string>com.antigravity.CharacterType</string>
    <key>CFBundleName</key>
    <string>CharacterType</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>1.0</string>
    <key>LSUIElement</key>
    <true/>
    <key>NSHighResolutionCapable</key>
    <true/>
</dict>
</plist>
EOF

echo "[3/4] 앱 번들 격리 속성(Quarantine) 정리 중..."
xattr -cr "$APP" 2>/dev/null || true

echo "[4/4] 완료!"
echo ""
echo "=========================================================="
echo "  성공적으로 [CharacterType.app] 생성이 완료되었습니다!  "
echo "=========================================================="
echo ""
echo " 앞으로는 생성된 'CharacterType.app'을 그냥 더블 클릭해서"
echo " 평범한 맥 앱처럼 바로 실행하시면 됩니다."
echo ""
echo " [중요 안내]"
echo " 최초 1회 실행 시 다른 창에서 타이핑을 감지하기 위한"
echo " '손쉬운 사용(Accessibility)' 권한 허용 창이 나타납니다."
echo " [시스템 설정 열기]를 눌러 스위치를 켜주시면 즉시 반응합니다."
echo ""
echo "지금 CharacterType.app을 실행합니다..."
open "$APP"

echo "이 창은 닫으셔도 됩니다."
