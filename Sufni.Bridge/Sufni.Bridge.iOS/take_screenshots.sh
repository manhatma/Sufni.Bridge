#!/bin/bash
# Sufni.Bridge — automatische Screenshot-Aufnahme
# Ausführen: bash take_screenshots.sh  (aus dem Verzeichnis Sufni.Bridge.iOS/)
# Output: ./screenshots/
#
# Voraussetzungen:
#   - cliclick installiert: brew install cliclick
#   - Accessibility für Claude.app in:
#     Systemeinstellungen → Datenschutz & Sicherheit → Bedienungshilfen
#   - App deployt und DB aus iPhone kopiert (sst.db → Documents/Sufni.Bridge/)

set -e

UDID="9418F204-D26E-4C72-BF1D-C24EAA601001"  # iPhone 15 Pro, iOS 26.3.1
BUNDLE="de.nielsbarth.sufnibridge-dev"
OUT="./screenshots"
mkdir -p "$OUT"

# ── Kalibrierungswerte (iPhone 15 Pro, kalibriert 2026-04-14) ─────────────
# screen_x = win_x + 27 + device_x
# screen_y = win_y + offset  (offset = 77 + device_y)

TAB_Y=195        # Analyse-Tabs in Session-View (Summary/Spring/…)
TOPBAR_Y=152     # Nav-Buttons oben (Filter/Delete/Combine/Compare)
ROW1_Y=300       # Erste Session-Zeile (SpätzLE)
ROW2_Y=340       # Zweite Session-Zeile
BOTTOM_Y=887     # Untere Button-Leiste in Session-View
MAINTAB_Y=877    # Haupt-Tab-Leiste (Sessions/Linkages/Calibrations/Setups)

COMBINE_X=275    # Combine-Button (= Cancel in Combine-Modus)
COMPARE_X=345    # Compare-Button (= Cancel in Compare-Modus)
BACK_X=33        # ← Zurück (in Session-Bottom-Bar)
CROP_X=229       # Crop-Toggle (in Session-Bottom-Bar)

# Analyse-Tab x (6 Tabs, 393pt Breite):
X_SUM=33; X_SPR=100; X_DAM=167; X_BAL=234; X_MIS=301; X_NOT=368

# Haupt-Tab x (4 Tabs, 393pt Breite):
X_SESSIONS=49; X_LINKAGES=147; X_CALS=245; X_SETUPS=344

# ── Hilfsfunktionen ────────────────────────────────────────────────────────
tap()      { cliclick c:$((WX+27+$1)),$((WY+$2)); }
tap_tab()  { cliclick c:$((WX+27+$1)),$((WY+TAB_Y)); }
tap_main() { cliclick c:$((WX+27+$1)),$((WY+MAINTAB_Y)); }
swipe_up() { local sx=$((WX+27+$1)); cliclick dd:$sx,$((WY+600)) w:300 du:$sx,$((WY+250)); }
shot()     { xcrun simctl io "$UDID" screenshot "$OUT/$1"; echo "  → $1"; }

restart_app() {
    echo "  (App-Neustart...)"
    xcrun simctl terminate "$UDID" "$BUNDLE" 2>/dev/null || true
    xcrun simctl launch   "$UDID" "$BUNDLE"
    sleep 6
    osascript -e 'tell application "Simulator" to activate'
    sleep 1
    WIN=$(osascript -e 'tell application "System Events" to tell process "Simulator" to get {position, size} of front window')
    WX=$(echo "$WIN" | cut -d, -f1 | tr -d ' ')
    WY=$(echo "$WIN" | cut -d, -f2 | tr -d ' ')
}

# ── Simulator sicherstellen ────────────────────────────────────────────────
xcrun simctl boot "$UDID" 2>/dev/null || true
open -a Simulator
sleep 3

# ── ABSCHNITT 1: Sessions-Liste & Combine-Modus ───────────────────────────
echo "▶ Abschnitt 1: Sessions-Liste & Combine"
restart_app
echo "  Fenster: ($WX, $WY)"

echo "▶ 01 Sessions-Liste"
sleep 1
shot "01_sessions_list.png"

echo "▶ 02 Combine-Modus (2 Sessions auswählen)"
tap $COMBINE_X $TOPBAR_Y          # Combine-Modus aktivieren
sleep 0.8
tap 196 $ROW1_Y                   # Zeile 1 anwählen
sleep 0.3
tap 196 $ROW2_Y                   # Zeile 2 anwählen
sleep 0.5
shot "02_sessions_combine.png"
tap $COMBINE_X $TOPBAR_Y          # Cancel (gleiche Position)
sleep 1.5

# ── ABSCHNITT 2: Session-Analyse-Tabs ─────────────────────────────────────
echo "▶ Abschnitt 2: Session-Analyse-Tabs"
restart_app

echo "▶ 03 Summary"
tap 196 $ROW1_Y                   # Session öffnen
sleep 6
shot "03_summary.png"

echo "▶ 04 Spring"
tap_tab $X_SPR; sleep 8; shot "04_spring.png"
cliclick dd:$((WX+27+196)),$((WY+780)) w:400 du:$((WX+27+196)),$((WY+200)); sleep 5
shot "04b_spring_scroll.png"
# Scroll zurück nach oben für nächsten Tab
cliclick dd:$((WX+27+196)),$((WY+200)) w:400 du:$((WX+27+196)),$((WY+780)); sleep 1

echo "▶ 05 Damper"
tap_tab $X_DAM; sleep 8; shot "05_damper.png"
cliclick dd:$((WX+27+196)),$((WY+780)) w:400 du:$((WX+27+196)),$((WY+200)); sleep 5
shot "05b_damper_scroll.png"
cliclick dd:$((WX+27+196)),$((WY+200)) w:400 du:$((WX+27+196)),$((WY+780)); sleep 1

echo "▶ 06 Balance"
tap_tab $X_BAL; sleep 8; shot "06_balance.png"

echo "▶ 07 Position vs Velocity"
tap_tab $X_MIS; sleep 8; shot "07_position_velocity.png"

echo "▶ 08 Notes"
tap_tab $X_NOT; sleep 3; shot "08_notes.png"

echo "▶ 09 Crop-Overlay"
tap $CROP_X $BOTTOM_Y             # Crop öffnen
sleep 2
shot "09_crop.png"
tap $CROP_X $BOTTOM_Y             # Crop schließen
sleep 1

tap $BACK_X $BOTTOM_Y             # Zurück zur Sessions-Liste
sleep 2

# ── ABSCHNITT 3: Compare ──────────────────────────────────────────────────
echo "▶ Abschnitt 3: Compare"
restart_app

echo "▶ 10-12 Compare"
tap $COMPARE_X $TOPBAR_Y          # Compare-Modus aktivieren
sleep 0.8
tap 196 $ROW1_Y                   # Session 1 wählen
sleep 0.3
tap 196 $ROW2_Y                   # Session 2 wählen
sleep 0.5
tap 196 835                       # "Compare (2)"-Button am unteren Rand bestätigen
sleep 9                           # Charts laden
shot "10_compare_travel.png"
# 3 große Swipes für Balance-Sektion
for i in 1 2 3; do cliclick dd:$((WX+27+196)),$((WY+780)) w:400 du:$((WX+27+196)),$((WY+200)); sleep 3; done
shot "11_compare_balance.png"
# Nochmals 3 Swipes für Summary-Tabelle
for i in 1 2 3; do cliclick dd:$((WX+27+196)),$((WY+780)) w:400 du:$((WX+27+196)),$((WY+200)); sleep 3; done
shot "12_compare_summary.png"

# ── ABSCHNITT 4: Linkage & Calibration ───────────────────────────────────
echo "▶ Abschnitt 4: Linkage & Calibration"
restart_app

echo "▶ 13 Linkage"
tap_main $X_LINKAGES
sleep 2
tap 196 $ROW1_Y
sleep 1
shot "13_linkage.png"
tap $BACK_X $BOTTOM_Y
sleep 1

echo "▶ 14 Calibration"
tap_main $X_CALS
sleep 2
tap 196 $ROW1_Y
sleep 1
shot "14_calibration.png"

echo ""
echo "✅ Screenshots in: $OUT"
ls -1 "$OUT"
echo ""
echo "Zum Kopieren nach pics/:"
echo "  cp $OUT/*.png /Users/niels/Telemetry/Sufni.Bridge/pics/"
