#!/bin/bash
# Sufni.Bridge — automatische Screenshot-Aufnahme aller Diagramm-Tabs
# Ausführen: bash take_screenshots.sh
# Output: ./screenshots/
#
# Voraussetzungen:
#   - cliclick installiert: brew install cliclick
#   - Accessibility für Claude.app (oder Terminal) in
#     Systemeinstellungen → Datenschutz & Sicherheit → Bedienungshilfen
#
# Koordinaten-Kalibrierung (iPhone 17 Pro Simulator, Xcode 26):
#   Simulator-Fenster: ca. 456x972pt. Kalibriert am 2026-03-28.
#   Fensterposition wird dynamisch ermittelt.
#   x: screen_x = win_x + 27 + device_x  (kein Skalierungsfaktor)
#   y: Session-Zeile 1 bei win_y+267 (empirisch), Tab-Leiste bei win_y+187

set -e

UDID="0F342F84-BDAD-43E9-972B-86FC38E901D3"
BUNDLE="de.nielsbarth.sufnibridge-dev"
OUT="./screenshots"
mkdir -p "$OUT"

echo "▶ Simulator booten..."
xcrun simctl boot "$UDID" 2>/dev/null || true
open -a Simulator
sleep 3

echo "▶ App starten..."
xcrun simctl terminate "$UDID" "$BUNDLE" 2>/dev/null || true
xcrun simctl launch "$UDID" "$BUNDLE"
sleep 4

echo "▶ Simulator aktivieren und Fensterposition ermitteln..."
osascript -e 'tell application "Simulator" to activate'
sleep 1
WIN=$(osascript -e 'tell application "System Events" to tell process "Simulator" to get {position, size} of front window')
WX=$(echo "$WIN" | cut -d, -f1 | tr -d ' ')
WY=$(echo "$WIN" | cut -d, -f2 | tr -d ' ')
echo "  Fenster: ($WX, $WY)"

# Hilfsfunktionen
tap() {
    local sx=$((WX + 27 + $1))
    local sy=$((WY + 187 + $2))
    cliclick c:$sx,$sy
}
tap_abs() {
    cliclick c:$1,$2
}
swipe_up() {
    local sx=$((WX + 27 + $1))
    cliclick dd:$sx,$((WY + $3)) w:300 du:$sx,$((WY + $4))
}

# Tab x-Positionen (device coords, 6 Tabs auf 402pt Breite)
TAB_Y=0          # relativ zu WY+187 (= 0 Offset, tap() addiert 187)
X_SUM=33         # Summary
X_SPR=100        # Spring
X_DAM=167        # Damper
X_BAL=234        # Balance
X_MIS=301        # Misc (Position vs Velocity Plots)
X_NOT=368        # Notes

echo "▶ Screenshot: Sessions-Liste..."
xcrun simctl io "$UDID" screenshot "$OUT/01_sessions_list.png"

# Session öffnen: erste Zeile bei WY+267
echo "▶ Session antippen (00100.SST)..."
tap_abs $((WX + 27 + 150)) $((WY + 267))
sleep 4
xcrun simctl io "$UDID" screenshot "$OUT/02_session_summary.png"

# Spring-Tab
echo "▶ Spring Tab (warte auf Plot-Generierung)..."
tap $X_SPR $TAB_Y
sleep 8
xcrun simctl io "$UDID" screenshot "$OUT/03_spring_travel_histograms.png"

# Damper-Tab
echo "▶ Damper Tab..."
tap $X_DAM $TAB_Y
sleep 8
xcrun simctl io "$UDID" screenshot "$OUT/04_damper_velocity_histograms.png"

# Balance-Tab
echo "▶ Balance Tab..."
tap $X_BAL $TAB_Y
sleep 8
xcrun simctl io "$UDID" screenshot "$OUT/05_balance.png"

# Misc-Tab (Position vs Velocity Plots)
echo "▶ Misc Tab (Position vs Velocity)..."
tap $X_MIS $TAB_Y
sleep 8
xcrun simctl io "$UDID" screenshot "$OUT/06_byb_velocity_distribution_comparison.png"

# Misc nach unten scrollen
echo "▶ Misc scrollen..."
swipe_up 150 0 537 137   # von WY+537 nach WY+137 (400pt Swipe)
sleep 2
xcrun simctl io "$UDID" screenshot "$OUT/07_byb_position_velocity_comparison.png"

swipe_up 150 0 537 137
sleep 2
xcrun simctl io "$UDID" screenshot "$OUT/08_byb_front_position_velocity.png"

swipe_up 150 0 537 137
sleep 2
xcrun simctl io "$UDID" screenshot "$OUT/09_byb_rear_position_velocity.png"

# Notes-Tab
echo "▶ Notes Tab..."
tap $X_NOT $TAB_Y
sleep 2
xcrun simctl io "$UDID" screenshot "$OUT/10_misc.png"

echo ""
echo "✅ Screenshots gespeichert in: $OUT"
ls -1 "$OUT"
echo ""
echo "Nächster Schritt: Bilder in pics/ Ordner des Repos kopieren:"
echo "  cp $OUT/*.png /Users/niels/Telemetry/Sufni.Bridge/pics/"
