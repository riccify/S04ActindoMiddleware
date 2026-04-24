# Dynamics NAV / Actindo Middleware - Objektuebersicht

Diese README ist die Arbeitsgrundlage fuer frischen Kontext zu den NAV-Objekten der Actindo-Middleware. Sie beschreibt alle Objekte in `NAVObjects`, deren Rollen, zentrale Funktionen, Tabellen und Zusammenspiel.

Die aktuellen Dateien liegen als `.fob`-Exports vor. Wo die Logik im Binary nur teilweise lesbar ist, wurde sie gegen die historisch versionierten `.cal`-Exporte und die im FOB enthaltenen Symbolnamen/Dokumentationsstrings abgeglichen.

## Gesamtbild

Die NAV-Objekte bilden die NAV-Seite der Integration zwischen Dynamics NAV 2016, einer eigenen Middleware/AllInOneAPI und Actindo POS.

```text
NAV Stammdaten / POS-Import
        |
        v
S04 Actindo Events / S04 Actindo Sync Collect
        |
        v
S04 Actindo Synch. Buffer
        |
        v
S04 Actindo Jobs (Job Queue)
        |
        v
S04 Actindo Service
        |
        v
S04 Actindo Core -> Middleware / Actindo API
```

Die wichtigsten Verantwortlichkeiten sind:

- Stammdaten-Export: Artikel, Varianten, Kunden, Preise, Lagerbestaende und Produktbilder werden als JSON erzeugt und an die Middleware gesendet.
- ID-Rueckschreibung: Actindo-IDs werden nach Neuanlagen wieder in NAV gespeichert.
- POS-Transaktionsimport: Actindo-Transaktionen werden abgeholt, in Puffertabellen geschrieben und daraus NAV-Verkaufsbelege erzeugt.
- Gutscheinlogik: Gutscheinpruefung, Aufladung, Einloesung, Storno und NAV-interne Verbuchung laufen ueber die Voucher Services.
- Aenderungserkennung: Sync Collect vergleicht relevante NAV-Daten gegen gespeicherte Sync-Staende und legt nur notwendige Updates in den Buffer.

## Objektliste

| Datei                                | Objekt                                 | ID    | Kurzrolle                                            |
| ------------------------------------ | --------------------------------------:| -----:| ---------------------------------------------------- |
| `Codeunits/ActindoCore.fob`          | Codeunit `S04 Actindo Core`            | 50090 | Technische HTTP-Kommunikation                        |
| `Codeunits/ActindoEvents.fob`        | Codeunit `S04 Actindo Events`          | 80093 | Eventsubscriber/Hooks fuer NAV-Aenderungen           |
| `Codeunits/ActindoImageHandler.fob`  | Codeunit `S04 Actindo Image Handler`   | 81000 | Produktbilder als Base64-Payload senden              |
| `Codeunits/ActindoJobs.fob`          | Codeunit `S04 Actindo Jobs`            | 66088 | Job-Queue Dispatcher                                 |
| `Codeunits/ActindoMiddleware.fob`    | Codeunit `S04 Actindo Middleware`      | 81122 | NAV-API-Methoden fuer ID-Sync                        |
| `Codeunits/ActindoService.fob`       | Codeunit `S04 Actindo Service`         | 80092 | Hauptlogik fuer Sync, Payloads und Transaktionen     |
| `Codeunits/ActindoSynchCollect.fob`  | Codeunit `S04 Actindo Sync Collect`    | 81123 | Aenderungen sammeln und Buffer fuellen               |
| `Codeunits/VoucherServices.fob`      | Codeunit `S04 Voucher Services`        | 60077 | Gutschein-Webservices und NAV-Gutscheinlogik         |
| `Tables/ActindoSetup.fob`            | Table `S04 Actindo Setup`              | 80092 | Einrichtung fuer Sync, Steuern, Varianten, Zahlarten |
| `Tables/ActindoSynchBuffer.fob`      | Table `S04 Actindo Synch. Buffer`      | 80091 | Request-/Response-Queue                              |
| `Tables/ActindoSynchState.fob`       | Table `S04 Actindo Sync State`         | 81123 | Letzter bekannter Sync-Zustand je Entitaet           |
| `Tables/TransactionBufferHeader.fob` | Table `S04 Actindo Trans.Buff. Header` | 81164 | Header-Puffer fuer POS-Transaktionen                 |
| `Tables/TransactionBufferLines.fob`  | Table `S04 Actindo Trans.Buff. Line`   | 81165 | Zeilen-Puffer fuer POS-Transaktionen                 |

## Zentrale Tabellen und externe NAV-Records

Neben den Objekten in diesem Ordner verwendet die Logik mehrere bestehende NAV-/S04-Tabellen:

| Record                          | Rolle                                                                  |
| ------------------------------- | ---------------------------------------------------------------------- |
| `Item` (27)                     | Artikelstamm, inkl. `Actindo POS ID`, Sync-Flags, Preis-/Bestandsdaten |
| `Item Variant` (5401)           | Varianten mit eigener Actindo POS ID                                   |
| `Customer` (18)                 | Debitorenstamm                                                         |
| `S04 Customer Extended` (60018) | Erweiterung fuer `Actindo Customer ID`, `Actindo Primary Address ID`   |
| `Webservice Setup` (60073)      | Basis-URL, Clientdaten und Bearer Token                                |
| `Webservice Endpoints` (80090)  | Relative Endpoint-URLs fuer Code `ACTINDO`                             |
| `Sales Header` / `Sales Line`   | Zielbelege fuer importierte POS-Transaktionen                          |
| `SP - Voucher` (5091710)        | Gutschein-Stammdatensatz                                               |
| `SP - Voucher Entry` (5091712)  | Gutscheinposten                                                        |
| weitere S04-Tabellen            | Voucher Sales, Shop Sales Lines, Vivenu/Gutschein-Hilfslogik           |

## Codeunits

### S04 Actindo Core (50090)

Technische Kommunikationsschicht. Diese Codeunit kapselt HTTP-Requests gegen die Middleware/AllInOneAPI und wird vor allem von `S04 Actindo Service` und `S04 Actindo Image Handler` genutzt.

Wichtige Aufgaben:

- Baut Request-URLs aus `Webservice Setup` und `Webservice Endpoints`.
- Fuehrt HTTP-Methoden `GET`, `POST`, `PUT`, `PATCH`, `DELETE` aus.
- Nutzt `RestSharp` und `Schalke04.RestClient`.
- Sendet JSON (`application/json`) aus `Newtonsoft.Json.Linq.JObject`.
- Setzt Header wie `Authorization: Bearer ...` und `Content-Type`.
- Erzwingt TLS 1.2 ueber `System.Net.ServicePointManager.SecurityProtocol`.
- Liest und schreibt Bearer Token im `Webservice Setup`.
- Schreibt sprechende Kommunikationsfehler mit HTTP-Status, URI und Response-Text.

Zentrale Funktionen:

- `SendRequest(method, WebserviceEndpoint, Request, Response, TransactionId)`: Einstieg fuer andere Objekte.
- `CreateRequestURL(...)`: Kombiniert Base URL und Endpoint URL.
- `ExecutePayloadRequestWebservice(...)`: Sendet Request mit Payload.
- `SetupRequestInternal(...)`: Bereitet RestSharp-Request vor.
- `ExecuteRequestInternal(...)`: Dispatch je HTTP-Methode.
- `HandleResponseError(...)`: Fehlerformatierung.
- `GetWebserviceSetup()`, `GetBearerToken()`, `SetBearerToken()`: Setup-/Tokenzugriff.

Besonderheit: Im Objekt sind Hinweise auf `ALLINONEAPI`/`ACTINDO` enthalten. Falls kein gueltiger Bearer Token vorhanden ist, wird ein Fehler erzeugt.

### S04 Actindo Events (80093)

Event-Hook-Codeunit. Sie verbindet NAV-Ereignisse mit der Actindo-Synchronisation.

Erkannte Bereiche:

- `### CUSTOMER HOOKS ###`
- `OnAfterDeleteCustomer`
- Eventsubscriber auf `OnAfterDeleteEvent`
- `### SALES PRICE HOOKS ###`

Aufgabe:

- Reagiert auf relevante NAV-Aenderungen, insbesondere Kundenloeschungen bzw. kundenbezogene Events.
- Nutzt `S04 Actindo Setup`, um zu entscheiden, ob Actindo-Sync aktiv ist.
- Stoesst bei relevanten Aenderungen die Buffer-Befuellung ueber Service/Sync-Collect an.

Hinweis: Die FOB-Symbole zeigen die Hooks, aber nicht die komplette C/AL-Logik als Klartext. Funktional gehoert diese Codeunit zur eventbasierten Frontdoor der Synchronisation.

### S04 Actindo Image Handler (81000)

Sendet Produktbilder an Actindo.

Wichtige Aufgaben:

- Ermittelt Bilder zu einem Artikel (`Item` / `recImage`).
- Liest Bild-BLOBs per Stream.
- Wandelt Bilder in Base64 um (`System.Convert.ToBase64String`).
- Erkennt MIME Types, z.B. `image/jpeg`.
- Baut JSON mit `images` und `paths`.
- Sendet an Endpoint `SET_IMAGES` bzw. Pfadbestandteile wie `products/img/...`.

Zentrale Funktionen:

- `SetImages(Item)`: Oeffentlicher Einstieg fuer einen Artikel.
- `SetImagesBatch()`: Batch-Verarbeitung aller relevanten Artikel mit Actindo-ID.
- `CreateJsonObject(Item, payload)`: Baut Bildpayload.
- `SendToMiddleware(payload, endpoint)`: Sendet ueber `S04 Actindo Core`.
- `GetMimeTypeByFileType` / `S04Utilities.GetWebItemImageFileType`: Dateityp-/MIME-Ermittlung.

Payload-Idee:

```json
{
  "images": [
    {
      "path": "products/img/...",
      "content": "<base64>",
      "type": "image/jpeg"
    }
  ],
  "paths": [...]
}
```

### S04 Actindo Jobs (66088)

Job-Queue Dispatcher. Diese Codeunit ist klein, aber zentral fuer die regelmaessige Verarbeitung.

Zentrale Funktion:

- `TriggerFunctionFromJobQueueEntry(FunctionParameter)`: Wird aus der Job Queue mit Parameter String aufgerufen.

Unterstuetzte Parameter:

| Parameter             | Aktion                                                                    |
| --------------------- | ------------------------------------------------------------------------- |
| `PROCESSSYNCHBUFFER`  | Ruft `S04 Actindo Service.ProcessSynchBufferEntries()` auf                |
| `GETTRANSACTIONS`     | Legt/uebergibt einen Transaktionsabruf an den Service                     |
| `PROCESSTRANSACTIONS` | Ruft `S04 Actindo Service.ProcessTransactionsEntries()` auf               |
| `COLLECTDATA`         | Ruft `S04 Actindo Sync Collect.CollectRelevantEntriesForSyncBuffer()` auf |

Wenn ein unbekannter Parameter uebergeben wird, erzeugt die Codeunit einen Fehler: Der Parameter kann keinem Funktionsaufruf zugeordnet werden.

### S04 Actindo Middleware (81122)

Diese Codeunit stellt die NAV-seitigen API-Funktionen bereit, die von der Middleware aufgerufen werden, um Actindo-IDs in NAV zu setzen oder aus NAV auszulesen.

Hauptaufgaben:

- Actindo-Produkt-IDs auf Artikel und Varianten zurueckschreiben.
- Actindo-Customer-IDs und Primary-Address-IDs auf Kunden-/S04-Erweiterungen schreiben.
- Bestehende ID-Zuordnungen aus NAV an die Middleware liefern.
- Administrative Loesch-/Reset-Funktionen fuer Actindo-IDs.
- Nach ID-Rueckschreibung weitere Verarbeitung anstossen, z.B. Bilder senden oder Produkt erneut speichern.

Zentrale Funktionen:

- `SetProductIDs(request, response)`: Liest Produkt-ID-Zuordnungen aus JSON und schreibt `Actindo POS ID` auf `Item` bzw. `Item Variant`.
- `GetProductIDs(...)` / `GetProductID(...)`: Liefert bekannte Produkt-/Varianten-IDs an die Middleware.
- `ClearProductIDs()`: Entfernt Actindo POS IDs von Artikeln und Varianten nach Benutzerbestaetigung.
- `SetCustomerID(request, response)`: Schreibt `Actindo Customer ID` und ggf. `Actindo Primary Address ID`.
- `GetCustomerIDs(response)`: Liefert alle Kunden mit Actindo-ID.
- `ClearCustomerActindoID(...)`, `ClearProductActindoID(...)`: Detail-Reset-Funktionen.

Wichtige JSON-Felder:

- `nav_id`
- `actindo_id`
- `customer.nav_id`
- `customer.actindo_id`
- `customerId`
- `productId`
- `masterProductId`
- `masterSku`
- `bufferId`

Wichtige NAV-Felder:

- `Item."Actindo POS ID"`
- `Item Variant."Actindo POS ID"`
- `Item."Actindo POS Last Update On"`
- `S04 Customer Extended."Actindo Customer ID"`
- `S04 Customer Extended."Actindo Primary Address ID"`
- `S04 Customer Extended."Actindo Last Update On"`

Besonderheiten:

- Varianten koennen ueber Master-/Variantenzuordnung verarbeitet werden.
- Fortschrittsdialoge werden bei Massenoperationen genutzt.
- Bei fehlenden Produkt-IDs oder nicht vorhandenen NAV-Datensaetzen werden klare Fehlermeldungen erzeugt.

### S04 Actindo Service (80092)

Haupt-Codeunit der Integration. Sie steuert Buffer-Verarbeitung, Payload-Erstellung, API-Aufrufe und POS-Transaktionsverarbeitung.

#### Middleware-Kommunikation

- `SendToMiddleware(payload, endpoint)`: Holt `Webservice Setup`/`Webservice Endpoints` fuer `ACTINDO`, ruft `S04 Actindo Core.SendRequest()` und gibt Response-Text zurueck.

#### Synch-Buffer-Verarbeitung

- `TransferToSynchBuffer(pEntityType, pEntityId)`: Frontdoor fuer `customer`, `product`, `inventory`, `price`, `transactions`, `full` usw.
- `CreateActindoSynchBufferEntry(request, referenceNo, endpoint)`: Legt offenen Buffer-Eintrag mit Request-BLOB an, sofern kein passender offener Eintrag existiert.
- `ProcessSynchBufferEntries()`: Verarbeitet alle offenen Buffer-Eintraege.
- `HandleSingleActindoBufferEntry(VAR ActindoSynchBuffer)`: Markiert Eintrag als `In Progress`, ruft endpoint-spezifische TryFunction auf, schreibt Response, Fehler, Dauer und Status.

Typische Endpoints:

| Endpoint           | Bedeutung                                                 |
| ------------------ | --------------------------------------------------------- |
| `CREATE_PRODUCT`   | Produkt in Actindo neu anlegen                            |
| `SAVE_PRODUCT`     | Produkt in Actindo aktualisieren                          |
| `FULL_PRODUCT`     | Vollstaendiges Produkt inkl. Varianten/Inventories senden |
| `CREATE_CUSTOMER`  | Kunde neu anlegen                                         |
| `SAVE_CUSTOMER`    | Kunde aktualisieren                                       |
| `SAVE_INVENTORY`   | Bestand aktualisieren                                     |
| `SAVE_PRICE`       | Preis aktualisieren                                       |
| `GET_TRANSACTIONS` | POS-Transaktionen abholen                                 |

#### API-Methoden / TryFunctions

- `TryCreateActindoProduct(...)`
- `TrySaveActindoProduct(...)`
- `TryCreateFullActindoProduct(...)`
- `TrySaveActindoInventory(...)`
- `TrySaveActindoPrice(...)`
- `TryCreateActindoCustomer(...)`
- `TrySaveActindoCustomer(...)`
- `TryGetTransactions(...)`

TryFunctions verhindern, dass ein Fehler die gesamte Batch-Verarbeitung abbricht. Fehler werden stattdessen am Buffer-Eintrag protokolliert.

#### JSON-Erstellung fuer Stammdaten

- `CreateProductJsonObject(payloadObj, itemNo, isSave)`: Standardprodukt.
- `CreateFullProductJsonObject(...)`: Vollprodukt mit Varianten und Inventories.
- `CreateCustomerJsonObject(debObject, debNo, create)`: Kunde inkl. Adresse; bei Save mit Actindo-ID.
- `CreateInventoryJsonObject(payloadObj, itemNo)`: Bestandsupdate.
- `CreatePriceJsonObject(payloadObj, itemNo)`: Preisupdate.
- `CreateTransactionJsonObject(payloadObj)`: Request fuer Transaktionsabruf, im Kern mit Datumsfilter.

Wichtige Payload-Helfer:

- `AssignEAN(...)`: Barcodes/Cross References.
- `AssignCatalog(...)`: Katalogmapping.
- `AssignPrice(...)`: Basispreis, Angebots-/Bulkpreise, Mitarbeiterpreis, Memberpreis.
- `AssignInventory(...)`: Lagerbestand je Artikel/Variante.
- `AssignVariants(...)`: Varianten, Flock-/Logo-Logik, EANs, Preise.
- `AssignTranslations(...)`: DE/EN Bezeichnungen, Keywords, Meta Title, Meta Description.
- `AssignTimestamps(...)`: created/modified Zeitstempel.
- `BuildPriceObject(...)`: Einheitlicher Preisbaustein fuer `_pim_price`, `_pim_price_employee`, `_pim_price_member`.

#### POS-Transaktionshandling

Actindo-Transaktionen werden nicht direkt zu Sales Orders verarbeitet, sondern zuerst in Tabellen 81164/81165 geschrieben.

Zentrale Funktionen:

- `TryGetTransactions(...)`: Holt neue Transaktionen seit `Actindo Setup."Last Transaction Date"`.
- `CreateTransactionsFromResponse(response)`: Zerlegt Response und erzeugt Header-/Line-Puffer.
- `ProcessTransactionsEntries()`: Verarbeitet offene Transaktionspuffer.
- `ProcessSingleTransactionEntry(VAR Header)`: Verarbeitet einen einzelnen Header.
- `TryCreateSalesOrderFromBuffer(VAR BufferHeader, VAR SalesOrderNo)`: Legt NAV-Verkaufsbeleg an.
- `ExplodePotentialBOMComponents(SalesLine, VAR LineNo)`: Fuegt Komponenten fuer Flock-/Assembly-BOM-Artikel hinzu.
- `FindLocationBasedOnTransaction(...)`: Mappt POS Location auf Location/Responsibility Center.
- `FindCustomerBasedOnTransaction(...)`: Findet NAV-Kunden anhand Actindo Customer ID.
- `GetItemNoFromBufferLine(...)`: Splittet Artikelnummer und Variantencode aus Buffer-Zeile.
- `ApplyActindoDiscountFromBufferLine(...)`: Uebernimmt Actindo-Rabatte in Belegzeilen.

Gutscheinbezug:

- Positionen mit Gutschein-/Restgutscheinlogik werden erkannt.
- `TryRedeemVoucherWithSalesHeader` aus `S04 Voucher Services` wird genutzt, um Gutscheine gegen den erzeugten Sales Header einzuloesen.
- Doppelte Verarbeitung wird gegen offene und gebuchte Verkaufsbelege geprueft.

#### Setup- und Hilfsfunktionen

- `CheckActindoSetup()`: Prueft Pflichtfelder in `S04 Actindo Setup`.
- `DateTime2UnixTimestamp(dt)`: NAV-DateTime zu Unix-Zeit.
- `EvaluateDate(Value)`: JSON-Datum zu NAV-Date.
- `GetLastTransactionDateString()`: Formatiert das letzte Transaktionsdatum fuer den Request.
- `GetItemStock(...)`: Ermittelt Lagerbestand.

Wichtige Rueckgabewerte/Status-Texte:

- `no_actindo_id`
- `no_stock`
- `item_not_found`
- Fehlertexte wie `... does not exist`, `... not active`, `... konnte nicht gefunden werden`.

### S04 Actindo Sync Collect (81123)

Sammelt relevante Aenderungen und legt gezielt Buffer-Eintraege an. Diese Codeunit verhindert, dass bei jedem Lauf pauschal alles gesendet wird.

Grundidee:

1. Relevante Artikel, Varianten, Kunden, Preise, Bestaende und Uebersetzungen werden durchlaufen.
2. Aus den aktuellen NAV-Daten wird ein Vergleichszustand/JSON abgeleitet.
3. Dieser wird gegen `S04 Actindo Sync State` verglichen.
4. Nur bei echten Aenderungen wird `TransferToSynchBuffer(...)` aufgerufen.
5. Der Sync State wird aktualisiert.

Zentrale Funktionen:

- `CollectRelevantEntriesForSyncBuffer()`: Haupteinstieg fuer Job `COLLECTDATA`.
- `CheckIfItemMustBeUpdated(...)`: Prueft Artikel-/Variantenrelevanz.
- `CheckIfCustomerMustBeUpdated(...)`: Prueft Kundenrelevanz.
- `ProcessUpdate(...)`: Stoesst Buffer-Eintrag an und aktualisiert Zustand.
- `CheckTranslations(...)`: Prueft Uebersetzungen/SEO-Felder.
- `CheckInventory(...)`: Prueft Lagerbestand.
- `CheckPrice(...)`: Prueft Preisstruktur.
- `CheckCustomer(...)`: Prueft Kundendaten.
- `BuildPriceObject(...)`: Baut vergleichbare Preisobjekte.
- `GetItemStock(...)`: Bestandsermittlung.
- `CheckActindoSetup()`: Setup-Pflichtpruefung.

Relevante Datenbereiche:

- `ItemDescription` / `Description 2`
- `ItemTranslation`
- `Item Inventory`
- `Item Prices`
- `Customer`
- `Customer Price Group`
- `Cross-Reference No.`
- `Actindo POS String Value`
- `Actindo POS Stock Synch`
- `Actindo POS Synch.`

### S04 Voucher Services (60077)

Gutschein-Codeunit fuer Webservice-Aufrufe und NAV-interne Gutscheinverarbeitung. Sie wird direkt von Actindo bzw. von der Transaktionsverarbeitung genutzt.

#### Webservice-Funktionen

| Funktion                                        | Aufgabe                                                  |
| ----------------------------------------------- | -------------------------------------------------------- |
| `ChargeGiftCard(request, response)`             | Gutschein aktivieren/aufladen oder Restgutschein anlegen |
| `CheckGiftCard(request, response)`              | Status und Guthaben pruefen                              |
| `PayUsingGiftCard(request, response)`           | Gutschein einloesen/bezahlen                             |
| `ReverseGiftCardTransaction(request, response)` | Gutscheintransaktion stornieren                          |

Typische Request-/Response-Felder:

- `card_number` / `cardNumber`
- `amount` / `amount_val`
- `currency_code`
- `external_reference` / `externalReferenceId`
- `transactionId`
- `balance`
- `expiration_date`
- `error`

#### Status- und Fehlerlogik

Statuscodes:

- `ACTIVE`
- `INACTIVE`
- `BLOCKED`
- `EXPIRED`

Fehlercodes:

| Code | Bedeutung                                  |
| ----:| ------------------------------------------ |
| 1    | Invalid Card / Gutschein nicht gefunden    |
| 2    | Invalid Amount / Betrag ungueltig          |
| 3    | Not enough Balance / Guthaben reicht nicht |
| 4    | Not charged / nicht aufgeladen             |
| 5    | Not redeemed / nicht eingeloest            |

Zentrale Hilfsfunktionen:

- `GetVoucherStatusCode(...)`
- `GetVoucherErrorCode(...)`
- `GetVoucherErrorMessage(...)`
- `PostVoucherByRequest(...)`
- `UpdateOutstandingAmount(...)`
- `ReverseGiftCardTransaction(...)`

#### NAV-Beleg-/Gutscheinlogik

Weitere Funktionen:

- `DeleteVoucherEntriesInEinloesung()`
- `DeleteCreateVoucherLine(VAR RecSSL)`
- `ErrorByVoucherPriceTypeLines(...)`
- `SetVoucherEntryToCancelledByDelete(VoucherNo)`
- `CreateIndiVoucher(SalesInvHdrNo)`
- `DeleteExpiredVoucher(SPVoucher)`
- `TryRedeemVoucherWithSalesHeader(...)`
- `TryLinkVoucherToSalesHeader(...)`
- `CreateGenJnlLineFromTransaction(...)`
- `ExecuteMultipleVouchers(...)`
- `TryIndiVoucherProcessing(...)`
- `SetRestVoucherParameters(...)`

Besonderheiten:

- Doppelte Einloesung wird ueber bestehende Voucher-/Sales-Tabellen und externe Referenzen verhindert.
- Restgutscheine bei Retouren werden erzeugt.
- Fuer Vivenu-/Actindo-Gutscheinzahlungen koennen General-Journal-Zeilen erzeugt werden.
- Gutscheinzeilen auf Verkaufsbelegen werden erkannt und bei Bedarf neu aufgebaut.

## Tabellen

### S04 Actindo Setup (80092)

Einrichtungstabelle fuer die gesamte Actindo-Integration.

Wichtige Felder:

| Feld                            | Bedeutung                                     |
| ------------------------------- | --------------------------------------------- |
| `PK`                            | Primaerschluessel                             |
| `Default Taxclass ID`           | Standard-Steuerklasse in Actindo              |
| `Reduced Taxclass ID`           | Reduzierte Steuerklasse                       |
| `Free Taxclass ID`              | Steuerfrei                                    |
| `Voucher Variantset ID`         | Variantenset fuer Gutscheine                  |
| `Merchandise Variantset ID`     | Variantenset fuer Merchandise                 |
| `Flock Variantset ID`           | Variantenset fuer Flock                       |
| `Merchandise Attributeset ID`   | Attributset fuer Merchandise                  |
| `Flock Attributeset ID`         | Attributset fuer Flock                        |
| `Last Transaction Date`         | Letzter erfolgreicher Transaktionsabruf       |
| `Standard Customer Account`     | Standarddebitor fuer Transaktionsverbuchung   |
| `Voucher Product Posting Group` | Produktbuchungsgruppe fuer Gutscheine         |
| `Customer Synch. Active`        | Aktiviert Kundensynchronisation               |
| `Product Synch. Active`         | Aktiviert Artikelsynchronisation              |
| `POS Transaction Synch. Active` | Aktiviert POS-Transaktionsimport              |
| `String Value Employee Price`   | Attributwert fuer Mitarbeiterpreise           |
| `Payment Method Code BAR`       | Zahlungsmethode Barzahlung                    |
| `Payment Method Code EC`        | Zahlungsmethode EC-/Kartenzahlung             |
| `Salesperson Transactions`      | Verkaeufercode fuer importierte Transaktionen |

Diese Tabelle wird von `S04 Actindo Service`, `S04 Actindo Events` und `S04 Actindo Sync Collect` als Schalter- und Mappingzentrale verwendet.

### S04 Actindo Synch. Buffer (80091)

Queue-/Logtabelle fuer ausgehende und eingehende Synchronisationsrequests.

Felder:

| Feld                     | Typ/Rolle                 | Bedeutung                                   |
| ------------------------ | ------------------------- | ------------------------------------------- |
| `No.`                    | BigInteger, AutoIncrement | Laufende Nummer                             |
| `Request Type`           | Text50                    | Art der Anfrage                             |
| `Status`                 | Option                    | `Open`, `In Progress`, `Processed`, `Error` |
| `Request Timestamp`      | DateTime                  | Zeitpunkt der Anlage                        |
| `Processing Timestamp`   | DateTime                  | Zeitpunkt der Verarbeitung                  |
| `Processing Duration`    | Duration                  | Laufzeit                                    |
| `Request Document`       | BLOB                      | JSON Request                                |
| `Response Document`      | BLOB                      | JSON Response                               |
| `Error Message`          | Text250                   | Kurzfehler                                  |
| `Detailed Error Message` | BLOB                      | Detailfehler                                |
| `User ID`                | Code50                    | Benutzer                                    |
| `Reference No.`          | Code50                    | Artikel-/Kunden-/Transaktionsreferenz       |
| `Direction`              | Option                    | `Inbound`, `Outbound`                       |
| `Endpoint`               | Text30                    | Logischer Endpoint, z.B. `CREATE_PRODUCT`   |

OnLookup-Funktionen exportieren JSON-BLOBs als temporaere Datei (`JSON_Blob.json`) und oeffnen sie per Hyperlink.

### S04 Actindo Sync State (81123)

Speichert den zuletzt bekannten Synchronisationszustand. Wird von `S04 Actindo Sync Collect` genutzt, um echte Aenderungen zu erkennen.

Wichtige Felder:

| Feld                         | Bedeutung                                                          |
| ---------------------------- | ------------------------------------------------------------------ |
| `Entry Type`                 | Typ des Zustands, z.B. Item, Customer, Item Inventory, Item Prices |
| `Item No.`                   | Artikelnummer                                                      |
| `Variant Code`               | Variantencode                                                      |
| `Cross-Reference No.`        | Barcode/EAN                                                        |
| `Last Update`                | Zeitpunkt der letzten Zustandsaktualisierung                       |
| `Update Necessary`           | Kennzeichen, dass erneute Synchronisierung noetig ist              |
| `ItemDescription2`           | Zweite Artikelbeschreibung                                         |
| `Transl. Description DE/EN`  | Uebersetzte Beschreibung                                           |
| `Transl. WebKeywords DE/EN`  | SEO Keywords                                                       |
| `Transl. WebMetaDesc DE/EN`  | SEO Meta Description                                               |
| `Transl. WebMetaTitle DE/EN` | SEO Meta Title                                                     |

### S04 Actindo Trans.Buff. Header (81164)

Header-Puffer fuer POS-Transaktionen, die aus Actindo abgeholt wurden.

Wichtige Felder:

| Feld                                               | Bedeutung                                   |
| -------------------------------------------------- | ------------------------------------------- |
| `Entry No.`                                        | Laufende Puffernummer                       |
| `Status`                                           | `Open`, `In Progress`, `Processed`, `Error` |
| `Actindo Document ID`                              | Actindo-Beleg-ID                            |
| `Document Type`                                    | Belegart                                    |
| `Document Number`                                  | Belegnummer                                 |
| `Document Date`                                    | Belegdatum                                  |
| `Value Date`                                       | Valutadatum                                 |
| `Currency`                                         | Waehrung                                    |
| `Net Amount`                                       | Nettobetrag                                 |
| `Gross Amount`                                     | Bruttobetrag                                |
| `VAT Amount`                                       | Mehrwertsteuerbetrag                        |
| `VAT Percent`                                      | Mehrwertsteuer-Prozent                      |
| `Customer/Supplier ID`                             | Actindo-/Kundenreferenz                     |
| `Customer/Supplier Name`                           | Name                                        |
| `Customer/Supplier Address/Zip/City/Country/Email` | Rechnungsadresse                            |
| `Delivery Name/Address/Zip/City/Country/Email`     | Lieferadresse                               |
| `POS Location ID/Name`                             | POS Standort                                |
| `POS Cashdesk ID/Name`                             | POS Kasse                                   |
| `Payment Type` / `Payment Type Label`              | Zahlungsart                                 |
| `Project ID`                                       | Projekt-ID                                  |
| `Original JSON`                                    | BLOB mit Originalpayload                    |
| `Error Message`                                    | Fehlertext                                  |
| `Processed On`, `Processed By`                     | Verarbeitungsdaten                          |
| `Processed to Document No.`                        | Erzeugter NAV-Beleg                         |
| `Voucher Lines exist`                              | Gutscheinzeilen vorhanden                   |

Beim Loeschen eines Headers werden zugehoerige Lines ueber `Header Entry No.` mitgeloescht.

### S04 Actindo Trans.Buff. Line (81165)

Zeilen-Puffer fuer POS-Transaktionspositionen.

Wichtige Felder:

| Feld                   | Bedeutung                                   |
| ---------------------- | ------------------------------------------- |
| `Entry No.`            | Laufende Zeilennummer                       |
| `Header Entry No.`     | Bezug zum Header                            |
| `Line No.`             | Positionsnummer                             |
| `Status`               | `Open`, `In Progress`, `Processed`, `Error` |
| `Actindo Position ID`  | Actindo-Positions-ID                        |
| `Business Document ID` | Externe Dokument-/Positionsreferenz         |
| `Item No.`             | Artikelnummer                               |
| `Item Name`            | Artikelname                                 |
| `Description`          | Langtext/Beschreibung                       |
| `Quantity`             | Menge                                       |
| `Unit Of Measure`      | Einheit                                     |
| `Unit Price`           | Einzelpreis                                 |
| `Base Price`           | Grundpreis                                  |
| `Amount excl. VAT`     | Nettobetrag                                 |
| `Amount incl. VAT`     | Bruttobetrag                                |
| `VAT Identifier`       | Mehrwertsteuerschluessel                    |
| `VATPercent`           | Mehrwertsteuer-Prozent                      |
| `Bin Code`             | Lagerfach                                   |
| `Original JSON`        | BLOB mit Originalzeilenpayload              |
| `Error Message`        | Fehlertext                                  |

## Ablauf: Produkt-/Kundensynchronisation

1. Ein Artikel, eine Variante, ein Kunde, ein Preis oder ein Bestand aendert sich.
2. `S04 Actindo Events` oder `S04 Actindo Sync Collect` erkennt die Relevanz.
3. `S04 Actindo Service.TransferToSynchBuffer(...)` entscheidet anhand Entitaet und vorhandener Actindo-ID:
   - ohne Actindo-ID: `CREATE_PRODUCT` bzw. `CREATE_CUSTOMER`
   - mit Actindo-ID: `SAVE_PRODUCT` bzw. `SAVE_CUSTOMER`
   - Teilupdates: `SAVE_PRICE`, `SAVE_INVENTORY`
   - Komplettpayload: `FULL_PRODUCT`
4. Der Service erzeugt JSON und schreibt einen offenen Eintrag in `S04 Actindo Synch. Buffer`.
5. Job Queue ruft `S04 Actindo Jobs` mit `PROCESSSYNCHBUFFER` auf.
6. `ProcessSynchBufferEntries()` verarbeitet offene Eintraege.
7. Die passende TryFunction baut Payload, sendet ueber `S04 Actindo Core` und speichert Response/Fehler.
8. Bei Neuanlagen schreibt die Middleware ueber `S04 Actindo Middleware` Actindo-IDs zurueck.

## Ablauf: POS-Transaktionsimport

1. Job Queue ruft `S04 Actindo Jobs` mit `GETTRANSACTIONS` auf.
2. `S04 Actindo Service.TryGetTransactions()` baut Request mit `Last Transaction Date`.
3. Response wird per `CreateTransactionsFromResponse()` in Header/Line-Puffer 81164/81165 geschrieben.
4. Job Queue ruft `PROCESSTRANSACTIONS` auf.
5. `ProcessTransactionsEntries()` verarbeitet offene Header.
6. `TryCreateSalesOrderFromBuffer()` legt NAV-Verkaufsbeleg an, setzt Kunde, Location, Zahlart, Zeilen und Rabatte.
7. Gutscheinzeilen werden ueber `S04 Voucher Services` behandelt.
8. Buffer-Header wird auf `Processed` oder `Error` gesetzt, inkl. erzeugter Belegnummer.

## Ablauf: Gutschein-Webservice

Die Voucher API wird direkt fuer Gutscheinoperationen verwendet und ist fachlich eng mit POS-Transaktionen gekoppelt.

| Actindo-Aktion        | NAV-Funktion                 | Ergebnis                                        |
| --------------------- | ---------------------------- | ----------------------------------------------- |
| Gutschein pruefen     | `CheckGiftCard`              | Status, Balance, Ablaufdatum oder Fehler        |
| Gutschein aufladen    | `ChargeGiftCard`             | Voucher Entry/Saldo wird erzeugt oder angepasst |
| Gutschein einloesen   | `PayUsingGiftCard`           | Einloesungsposten und Transaktions-ID           |
| Einloesung stornieren | `ReverseGiftCardTransaction` | Gegenposten/Storno                              |

## Wichtige Konfigurationsabhaengigkeiten

Vor Verarbeitung muessen insbesondere gepflegt sein:

- `Webservice Setup` fuer `ACTINDO`/`ALLINONEAPI`
- `Webservice Endpoints` fuer alle genutzten Endpoints
- Bearer Token bzw. Clientdaten
- `S04 Actindo Setup`:
  - Sync-Aktiv-Flags
  - Taxclass IDs
  - Variantset/Attributeset IDs
  - Standarddebitor
  - Voucher Product Posting Group
  - Payment Method Codes
  - Salesperson Code fuer Transaktionen

Wenn Setup-Felder fehlen, bricht `CheckActindoSetup()` bzw. die jeweilige TryFunction mit Fehler ab und schreibt den Fehler in den Buffer.

## Fehler- und Loggingmodell

- `S04 Actindo Synch. Buffer` ist das zentrale Log fuer Stammdaten- und Teilupdates.
- `S04 Actindo Trans.Buff. Header/Line` protokollieren POS-Importe.
- TryFunctions verhindern Batch-Abbruch.
- Statuswerte sind durchgaengig `Open`, `In Progress`, `Processed`, `Error`.
- Request und Response bleiben als BLOB erhalten.
- OnLookup exportiert BLOBs als `JSON_Blob.json` zur Analyse.
- `Processing Duration`, `Processing Timestamp`, `Processed On`, `Processed By` helfen bei Laufzeit- und Fehleranalyse.

## Erweiterungspunkte

Wenn neue Actindo-Entitaeten oder Endpoints ergaenzt werden:

1. Endpoint in `Webservice Endpoints` pflegen.
2. Neuen Request-Typ/Endpoint in `TransferToSynchBuffer(...)` beruecksichtigen.
3. Payload-Erstellung in eigener Funktion kapseln.
4. TryFunction fuer API-Aufruf anlegen.
5. `HandleSingleActindoBufferEntry(...)` um den Endpoint erweitern.
6. Bei Aenderungserkennung `S04 Actindo Sync Collect` und ggf. `S04 Actindo Sync State` erweitern.
7. Job Queue nur erweitern, wenn ein neuer periodischer Ablauf gebraucht wird.

## Schnelle Orientierung nach Problemfall

| Problem                           | Zuerst pruefen                                                                    |
| --------------------------------- | --------------------------------------------------------------------------------- |
| Produkt/Kunde wird nicht gesendet | `S04 Actindo Setup` Sync-Flags, `S04 Actindo Sync Collect`, Buffer 80091          |
| Buffer bleibt offen               | Job Queue Parameter `PROCESSSYNCHBUFFER`, Codeunit 66088                          |
| HTTP-Fehler                       | `S04 Actindo Core`, Webservice Setup, Bearer Token, Endpoint URL                  |
| Actindo-ID fehlt in NAV           | `S04 Actindo Middleware.SetProductIDs` / `SetCustomerID`, Response im Buffer      |
| Bestand/Preis fehlt               | `CheckInventory`, `CheckPrice`, `SAVE_INVENTORY`, `SAVE_PRICE`                    |
| Bilder fehlen                     | `S04 Actindo Image Handler.SetImages`, Artikel mit Actindo POS ID, Bild-BLOB/MIME |
| Transaktionen fehlen              | `Last Transaction Date`, `GET_TRANSACTIONS`, Tabellen 81164/81165                 |
| Verkaufsbeleg wird nicht erzeugt  | Header/Line Status, `TryCreateSalesOrderFromBuffer`, Kunde/Location/Zahlart       |
| Gutscheinfehler                   | `S04 Voucher Services`, Voucher Status, Balance, External Reference               |
