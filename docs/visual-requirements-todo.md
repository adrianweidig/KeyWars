# Visual Requirements To-Do

Ausgangsreferenz: `C:\Users\adrian.TOP\Downloads\WhatsApp Image 2026-06-28 at 09.56.08.jpeg`

## Bildanalyse

Der Screenshot zeigt KeyWars als dunkles, kompaktes Trainings-Cockpit mit
Desktop-Sidebar und Mobile-Bottom-Navigation. Die wichtigsten visuellen Muster
sind:

- ein einheitliches Icon-System für Navigation, Moduswahl, Quests und Ergebnisse;
- cyan/grüne Fortschrittsbalken mit goldenen Rang- und Reward-Akzenten;
- runde Icon-Badges für Quests, Quickstart-Karten und Statuswerte;
- ein zentraler Tippbereich mit klarer Sofortrunde und sekundärer Rangliste;
- mobile Verdichtung ohne Informationsverlust, mit identischen visuellen Motiven;
- dezente Glow-, Panel- und Hintergrundeffekte statt externer Bild-CDNs.

## Abgeleitete Requirements

- [x] Lokale, frei lizenzierte SVG-Icons einführen und im Repo mit Notice dokumentieren.
- [x] Keine CDN-Abhängigkeit hinzufügen; alle neuen Assets müssen unter `wwwroot` liegen.
- [x] Navigation, Mobile-Navigation und Topbar auf dieselbe Icon-Familie umstellen.
- [x] Quests, letzte Ergebnisse und Quickstart-Karten von Textglyphen auf SVG-Icons umstellen.
- [x] Rang-/Reward-Flächen mit Badge- und Trophy-Symbolik anreichern.
- [x] Wettbewerbs- und Arena-Flächen mit scanbaren Aktionsicons ergänzen.
- [x] Erfolge um echte Medal-/Achievement-Icons erweitern.
- [x] Ein dezentes KeyWars-Hintergrund-SVG als atmosphärischen Effekt einbinden.
- [x] Responsive Layoutregeln prüfen, damit die neuen Icons keine Mobile-Flächen verdecken.
- [x] Drittanbieterquellen und Lizenzbedingungen in `THIRD-PARTY-NOTICES.md` erfassen.
- [x] Offline-vendorbare Komplettpakete für Icons und direkt verfügbare Illustrationen herunterladen.
- [x] Asset-Manifest mit SHA256, Lizenzdateien und Runtime-Derivaten erzeugen.
- [x] Erfolge als vollständigen motivierenden Katalog mit gesperrten und freigeschalteten Badges anzeigen.
- [x] Reward-/Motivation-Events mit lokalen Visual-Keys und lokalen Icons ausgeben.
- [x] Empty States, App-Icons und Favicons aus lokalen Assets ergänzen.

## Umgesetzte Assets

- `third_party/visual-assets/asset-manifest.json`: vollständige Quellen-, Hash- und Lizenzübersicht.
- `third_party/visual-assets/originals/`: gepinnte Originalpakete und offizielle Seitenassets.
- `third_party/visual-assets/licenses/`: lokale Lizenztexte und Lizenznotizen.
- `src/KeyWars/wwwroot/vendor/keywars-assets/keywars-icons.svg`: kuratierte Runtime-Sprite mit stabilen KeyWars-Alias-IDs.
- `src/KeyWars/wwwroot/vendor/keywars-assets/illustrations/`: lokale Motivations- und Empty-State-Illustrationen.
- `src/KeyWars/wwwroot/img/keywars-circuit.svg`: projektspezifisches Hintergrund-SVG.
- `src/KeyWars/wwwroot/img/favicon.svg`, `apple-touch-icon.png`, `site.webmanifest`: lokale App-Branding-Assets.
- `src/KeyWars/Infrastructure/UiIcons.cs`: Razor-Helper für kontrollierte SVG-Nutzung.
- `src/KeyWars/Infrastructure/MotivationVisuals.cs`: stabile Zuordnung von Missionen, Erfolgen, Trainingsmodi und Events zu Visual-Keys.

## Optional Später

- [x] Weitere leere Zustände mit passenden lokalen Illustrationen versehen.
- [x] Eigene KeyWars-App-Icons/Favicons aus dem neuen Badge-System ableiten.
- [x] Einen visuellen Regressionstest für das externe SVG-Sprite ergänzen, falls die Browser-Suite instabil auf externe `<use>`-Referenzen reagiert.
- [ ] Bei Bedarf einzelne Open-Peeps-/Humaaans-Assets manuell kuratieren, wenn eine konkrete Seite eine figürliche Illustration statt der neutralen KeyWars-Fallback-Illustration braucht.
