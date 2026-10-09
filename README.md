# Begleitcode: E-Rechnungen automatisch verarbeiten mit .NET

Beispielprojekt zum Buch von Ali Önder Celebi. .NET 10, ohne NuGet-Pakete.

## Projekte

| Projekt | Inhalt |
|---|---|
| `src/ERechnung.Core` | Formaterkennung, Extraktion, Zusatzangaben, Validierung (KoSIT, Mustang), Plausibilität, Kurzansicht, Kreditorzuordnung |
| `src/ERechnung.Api` | ASP.NET Core Minimal API (`/api/v1/invoices/...`, `/health`) |
| `src/ERechnung.Worker` | Eingangsordner, Postfach (Microsoft Graph), Routing, KoSIT-Daemon-Host |
| `tests/ERechnung.SelfTest` | Einheiten- und Regressionstests ohne Test-Framework |
| `tools/ERechnung.Stats` | Fehlerstatistik über abgelehnte Rechnungen |
| `samples` | Beispielrechnung RE-2026-0815 als UBL, CII und ZUGFeRD-PDF |

## Voraussetzungen

- .NET 10 SDK
- Java ab Version 11
- KoSIT-Validator (`validator-<version>-standalone.jar`) und Prüfkonfiguration XRechnung
- Mustang-CLI (`Mustang-CLI-<version>.jar`)

## Schnellstart

```bash
# 1. KoSIT-Daemon starten
java -jar validator-1.6.3-standalone.jar -s xrechnung-config/scenarios.xml -r xrechnung-config -D -P 8081

# 2. API starten
export Api__ApiKey=geheim Mustang__JarPath=/pfad/Mustang-CLI-2.26.0.jar
dotnet run --project src/ERechnung.Api --urls http://localhost:5080

# 3. Rechnung prüfen
curl -H "X-Api-Key: geheim" -F file=@samples/RE-2026-0815_ubl.xml http://localhost:5080/api/v1/invoices/process
```

Getestet mit KoSIT-Validator 1.6.3, Prüfkonfiguration XRechnung 3.0.2 (Release 2026-08-31) und Mustang 2.26.0.
