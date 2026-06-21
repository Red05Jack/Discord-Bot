# Discord XP Bot (C#)

Der Bot verwaltet ein eigenes, dauerhaftes XP- und Levelsystem für:

- Textnachrichten;
- Voice-Zeit;
- Einladungen.

Alle XP-Werte, Zeiträume, Kanäle und Schalter stehen in `appsettings.json`. Die Daten
werden in SQLite gespeichert und bleiben nach einem Neustart erhalten.

## XP-Konten und Level

Jeder Benutzer besitzt drei getrennte XP-Werte:

- `MessageXp`
- `VoiceXp`
- `InviteXp`

`TotalXp` ist immer die Summe dieser drei Werte. Ein Benutzer startet mit 0 XP auf Level 0.
Die benötigten XP für das nächste Level werden so berechnet:

```text
round(20.0 * (currentLevel + 1)^1.9)
```

Große Gutschriften können mehrere Level auf einmal auslösen. Abzüge können das Level wieder
senken, XP werden aber nie kleiner als 0. Jede tatsächliche XP-Bewegung wird mit Benutzer,
Betrag, Grund, Zeitpunkt, alten/neuen XP und altem/neuem Level in SQLite protokolliert.

Bei einem Level-Up sendet der Bot eine Nachricht in den unter `Levels` konfigurierten Kanal.

## Nachrichten-XP

Jede normale neue User-Nachricht erhält deterministisch 15 bis 25 XP. Die XP werden aus der
Discord-Nachrichten-ID berechnet. Dieselbe ID ergibt daher immer denselben Wert und wird durch
den eindeutigen Datenbankschlüssel nur einmal verbucht.

Im normalen Betrieb verarbeitet der Bot nur neue Nachrichten live. Wird eine gespeicherte
Nachricht live gelöscht, werden deren XP sofort wieder abgezogen.

Ein vollständiger Scan startet niemals automatisch beim Botstart. Er wird ausschließlich
durch einen Administratorbefehl ausgelöst.

### `!recalculate`

Ein Mitglied mit der Berechtigung **Server verwalten** kann den gesamten Nachrichtenbestand
neu berechnen:

```text
!recalculate
```

Der Bot merkt sich den exakten Startzeitpunkt und arbeitet anschließend in zwei Phasen:

1. Alle erreichbaren Nachrichten vor dem Startzeitpunkt werden historisch eingelesen.
2. Danach werden Nachrichten ab dem Startzeitpunkt in einem schnellen Catch-up-Durchlauf
   nachgezogen.

Währenddessen werden neue und gelöschte Nachrichten gepuffert und beim Abschluss in den
neuen Nachrichten-Snapshot übernommen. Danach läuft die Live-Verarbeitung normal weiter.
Voice- und Invite-Verarbeitung bleiben während des gesamten Scans aktiv. `MessageXp` wird
vollständig ersetzt. Zusätzlich verwendet der Bot Discord Member Search v2, um für aktuelle
Servermitglieder den verwendeten Invite-Code, den Einlader und das echte Beitrittsdatum
abzurufen. Noch nicht gespeicherte Zuordnungen werden in `invite_rewards` übernommen.

Mitglieder, die bereits mindestens sieben Tage auf dem Server sind, lösen sofort die
konfigurierte Invite-Belohnung aus. Jüngere Zuordnungen bleiben bis zum Erreichen der Frist
offen. Weil dabei die konkrete Member-ID gespeichert wird, kann die Belohnung beim späteren
Verlassen wieder zurückgebucht werden. Bereits gespeicherte Mitglieder werden nicht doppelt
vergütet.

Fortschritt und Ergebnisliste erscheinen im unter `BotChannel` konfigurierten Textkanal.
Die Konsole zeigt zusätzlich Kanalstarts, gelesene Chargen, Threads, Catch-up und SQL-Phasen.

### `!myrank`

```text
!myrank
```

Sendet ohne Begleittext eine PNG-Rangkarte auf Basis der bereitgestellten `rank.html`.
Die Karte zeigt Discord-Avatar, Anzeigename, Rang unter den aktuellen Servermitgliedern,
Level und Fortschritt bis zum nächsten Level.

### `!recalculate-invites`

```text
!recalculate-invites
```

Berechnet ausschließlich die verfügbaren Invite-Zuordnungen neu. Nachrichten- und Voice-XP
werden dabei nicht verändert. Der Befehl benötigt **Server verwalten**.

## Voice-XP

Voice-Zeit wird in vollständigen Blöcken vergütet. Standardmäßig gilt:

- Blocklänge: 5 Minuten;
- Belohnung je vollständigem Block: zufällig 5 bis 15 XP;
- Prüfung laufender Sitzungen: standardmäßig jede Minute;
- unvollständige Restzeit verfällt beim Verlassen.

Bei 13 Minuten werden somit zwei 5-Minuten-Blöcke ausgezahlt; die restlichen 3 Minuten
verfallen. Die laufende Sitzung wird in SQLite gespeichert.
Beim Start werden vorhandene Sitzungen mit Discord abgeglichen. Falls Discord ein
Voice-Join-Event nicht geliefert hat, legt der nächste Checkpoint die fehlende Sitzung
automatisch an.

Eine leere Liste unter `Voice.EligibleChannelIds` erlaubt alle Voice-Kanäle.
`Voice.ExcludedChannelIds` schließt einzelne Kanäle aus.

## Einladungen

Neu erkannte Einladungen werden gespeichert und nach der konfigurierten Haltezeit vergütet.
Bereits vergebene Invite-XP werden dauerhaft protokolliert und nicht doppelt ausgezahlt.
Verlässt das eingeladene Mitglied später den Server, wird die zugehörige Belohnung
zurückgebucht.

Beim Start liest der Bot aktuelle Invite-Zähler nur als Ausgangsstand ein. Historische,
bereits gespeicherte Einladungen werden ausschließlich durch diesen Slash-Command aktiviert:

```text
/einladungen-nachbearbeiten
```

Discord liefert keine vollständige historische Zuordnung von Mitgliedern zu Invite-Codes.
Vanity-URLs und Beitritte während einer Offline-Zeit können deshalb nicht immer zugeordnet
werden.

## Discord-App einrichten

Im Discord Developer Portal müssen unter **Bot → Privileged Gateway Intents** aktiviert sein:

- Server Members Intent
- Message Content Intent

Empfohlene Bot-Berechtigungen:

- View Channels
- Send Messages
- Read Message History
- Use Application Commands
- Manage Server, damit Einladungen gelesen werden können
- Manage Channels, falls Ausgabekanäle automatisch erstellt werden sollen

## Konfiguration und Token

Ist der Bot nur auf einem Server, darf `Discord.GuildId` auf `0` bleiben. Bei mehreren
Servern muss die gewünschte Guild-ID gesetzt werden.

Der Token sollte über eine Umgebungsvariable gesetzt und nicht in Git gespeichert werden:

```powershell
$env:DISCORD_BOT_TOKEN = "DEIN_BOT_TOKEN"
dotnet run
```

Weitere unterstützte Umgebungsvariablen:

- `DISCORD_GUILD_ID`
- `BOT_DATABASE_PATH`
- `BOT_CONFIG_PATH`

Der Bot-Ausgabekanal wird über `BotChannel.ChannelId` oder `BotChannel.ChannelName`
ausgewählt. Mit `BotChannel.CreateChannelIfMissing: true` wird er bei Bedarf erstellt.

Mit `"Debug": { "Enabled": true }` zeigt die Konsole erkannte Voice-Bewegungen und jede
tatsächlich angewendete positive oder negative XP-Bewegung.

## Start und Test

```powershell
dotnet restore
dotnet run
```

Lokaler Selbsttest:

```powershell
dotnet run --project tests/DiscordXpBot.SelfTest.csproj
```

## Befehle

- `!myrank` – sendet die eigene Rank-Karte als Bild
- `!recalculate` – berechnet Nachrichten und verfügbare Invite-Zuordnungen neu
- `!recalculate-invites` – berechnet ausschließlich Invite-Zuordnungen neu
- `/xp-liste` – sendet die aktuelle interne XP-Liste in den Bot-Textkanal
- `/einladungen-nachbearbeiten` – aktiviert gespeicherte historische Einladungen
