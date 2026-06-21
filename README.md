# Discord XP Bot (C#)

Der Bot verwaltet:

- interne XP-Konten mit einem eindeutigen Ledger für Nachrichten, Voice und Einladungen;
- 15–25 deterministische XP pro Nachricht;
- zufällige Invite-XP, sobald das eingeladene Mitglied die konfigurierte Haltezeit erreicht;
- eine vollständige Rückbuchung, wenn das eingeladene Mitglied später den Server verlässt;
- zufällige XP pro vollständiger Voice-Minute nach mindestens fünf Minuten im selben Voice-Chat;
- persistente Invite-Deduplizierung, MEE6-Versandaufträge und Voice-Sitzungen in SQLite;
- einen eigenen Discord-Kanal für konfigurierbare `!give-xp`- und `!remove-xp`-Nachrichten.

Alle XP-Werte, Zeiträume, Voice-Kanäle, Befehlsvorlagen und weitere Schalter stehen in
`appsettings.json`.

## Wichtiger Hinweis zu MEE6

MEE6 stellt keine öffentliche Schreib-API bereit, mit der ein fremder Bot XP vergeben oder
entfernen kann. Dieses Projekt sendet wie gewünscht konfigurierbare `!`-Nachrichten in einen
eigenen Kanal. Der offizielle MEE6-Bot ignoriert jedoch normalerweise Nachrichten anderer
Bots. Ob diese Nachrichten tatsächlich XP bei MEE6 verändern, hängt daher von deiner
MEE6-/Server-Konfiguration ab. Ein User-Token oder Self-Bot wird bewusst nicht verwendet.

Jede geplante XP-Änderung wird vor dem Senden eindeutig in `xp_dispatches` gespeichert.
Invite-Auszahlungen und Rückbuchungen bleiben zusätzlich dauerhaft in
`invite_xp_ledger`. Derselbe Invite-Reward kann dadurch nicht ein zweites Mal erzeugt werden.

Das interne XP-System führt alle Quellen zusammen. MEE6-Befehle für Voice und Einladungen
bleiben zusätzlich bestehen, sind aber nicht die Datenquelle der internen XP-Liste.

## Nachrichten-XP

Jede normale User-Nachricht erhält zwischen 15 und 25 XP. Der Wert wird ausschließlich aus
der Discord-Nachrichten-ID mit einer festen SplitMix64-Formel berechnet. Deshalb liefert
dieselbe Message-ID immer denselben XP-Wert und kann durch den eindeutigen SQL-Schlüssel nur
einmal verbucht werden.

Beim Start liest der Bot standardmäßig alle erreichbaren Nachrichten in Textkanälen,
öffentlichen archivierten Threads und aktiven Threads ein. Danach werden neue Nachrichten
live verbucht. Wird eine Nachricht während der Bot läuft gelöscht, entfernt er auch deren XP.
Nach dem Scan erscheint die interne XP-Liste im vorhandenen Bot-/MEE6-Textkanal.

## Discord-App einrichten

1. Im [Discord Developer Portal](https://discord.com/developers/applications) eine App und
   einen Bot erstellen.
2. Unter **Bot → Privileged Gateway Intents** den **Server Members Intent** aktivieren.
3. Den Bot mit diesen Berechtigungen einladen:
   - View Channels
   - Send Messages
   - Read Message History
   - Use Application Commands
   - Manage Server (zum Lesen der Discord-Einladungen)
   - Manage Channels (zum automatischen Erstellen des MEE6-Befehlskanals)
4. In Discord den Entwicklermodus aktivieren und per Rechtsklick die Server- und Kanal-IDs
   kopieren.

## Konfiguration

Ist der Bot nur auf einem Discord-Server, darf `Discord.GuildId` auf `0` bleiben und wird
automatisch erkannt. Bei mehreren Servern muss die gewünschte Server-ID gesetzt werden.
Der Token sollte nicht eingecheckt werden:

```powershell
$env:DISCORD_BOT_TOKEN = "DEIN_BOT_TOKEN"
dotnet run
```

Alternativ kann der Token lokal in `Discord.Token` stehen. Weitere unterstützte
Umgebungsvariablen:

- `DISCORD_GUILD_ID`
- `BOT_DATABASE_PATH`
- `BOT_CONFIG_PATH`

Bei `Voice.EligibleChannelIds` bedeutet eine leere Liste: alle Voice-Kanäle. IDs in
`Voice.ExcludedChannelIds` werden immer ausgeschlossen.

`Voice.MinimumRewardableMinutes` steht standardmäßig auf `5`: Wer vorher geht, erhält keine
Voice-XP. Beim Erreichen der fünften Minute werden alle fünf Minuten vergütet. Danach zählt
jede weitere vollständige Minute derselben Sitzung. Bei einem Wechsel in einen anderen
Voice-Chat beginnt die Mindestzeit erneut.

Mit `"Debug": { "Enabled": true }` protokolliert die Konsole jeden erkannten Voice-Beitritt,
Voice-Austritt und Kanalwechsel mit Benutzer- und Kanal-ID. Bei `false` werden diese
Bewegungsmeldungen ausgeblendet.

Die Nachrichtenwerte stehen unter `Messages`. Für den historischen Scan ist kein
**Message Content Intent** notwendig, weil der Bot nur Message-ID und Autor auswertet.

Mit `"ScanOnly": true` läuft der Bot vorerst ausschließlich als Historien-Scanner:
Voice-, Invite-, MEE6-, Live-Nachrichten- und Slash-Command-Verarbeitung sind pausiert.
Nach Abschluss schreibt er eine Scan-Zusammenfassung und die vollständige User-XP-Liste in
den bestehenden Bot-Textkanal.

Direkt beim Start des Scans wird dort eine Statusnachricht angelegt. Sie zeigt den aktuell
gelesenen Kanal, Nachrichtenzahlen und Laufzeit und wird während des Scans fortlaufend
aktualisiert. Nach Abschluss wird diese Nachricht zur ersten Ergebnisseite.

Die Konsole protokolliert zusätzlich jeden Arbeitsschritt mit `[SCAN]`: Kanal-Start und
-Ende, jede angefragte 100er-Nachrichtencharge, gefundene Threads, SQL-Zwischenstände sowie
jede an Discord gesendete Ergebnisseite.

Der Bot sucht beim Start den Kanal aus `Mee6Commands.ChannelId` oder
`Mee6Commands.ChannelName`. Mit `CreateChannelIfMissing: true` wird
`#mee6-xp-befehle` automatisch erstellt. Die Standardnachrichten sind:

```text
!give-xp @Benutzer 123
!remove-xp @Benutzer 123
```

Die Vorlagen können über `GiveXpCommand` und `RemoveXpCommand` verändert werden. Verfügbare
Platzhalter sind `{user}`, `{userId}` und `{xp}`.

## Start

```powershell
dotnet restore
dotnet run
```

Optionaler lokaler Funktionstest:

```powershell
dotnet run --project tests/DiscordXpBot.SelfTest.csproj
```

## Slash-Commands

- `/xp-admin benutzer betrag [grund]` – passt interne XP an und stellt den MEE6-Befehl bereit
- `/einladungen-nachbearbeiten` (benötigt „Server verwalten“)
- `/xp-liste` – gibt die aktuelle interne XP-Liste erneut im Bot-Textkanal aus

## Neue und bisherige Einladungen

Beim Start liest der Bot die aktuellen Invite-Zähler nur als Ausgangsstand ein. Daraus werden
keine rückwirkenden XP erzeugt. Anschließend erkannte neue Joins werden automatisch gespeichert
und nach der Haltezeit verarbeitet.

Bereits vor diesem Update in SQLite vorhandene, noch offene Invite-Datensätze sind standardmäßig
gesperrt. Erst `/einladungen-nachbearbeiten` schaltet sie frei. Der Befehl kann nur Datensätze
verarbeiten, die bereits in SQLite stehen; Discord liefert keine vollständige historische
Zuordnung von Mitgliedern zu Invite-Codes.

## Grenzen des Invite-Trackings

Discord sendet beim Beitritt nicht direkt den verwendeten Invite-Code. Der Bot vergleicht
daher die Nutzungszähler aller Einladungen. Dafür muss er beim Start bereits laufen und
„Server verwalten“ besitzen. Vanity-URLs und Beitritte während einer Offline-Zeit können
nicht immer eindeutig einem Einladenden zugeordnet werden.
