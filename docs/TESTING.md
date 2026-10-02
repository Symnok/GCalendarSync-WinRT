# Testing

## What runs on the desktop

```
tests\run-tests.bat
```

238 checks, no phone and no Google account involved. `run-tests.bat` compiles the **real** sources
out of `src\GCalendarSync.Core` with the desktop C# compiler and runs them, so nothing here is a
copy that can drift from what the app ships.

That is possible because a `StoredContact`'s properties are an `IDictionary<string, object>` holding
plain strings and `DateTimeOffset`s. Everything except opening the store, saving a contact and the
display picture is therefore ordinary C#, and lives in `ContactMapping` / `ContactSlots` rather than
in the WinRT wrapper.

Covered:

| Area | What is checked |
|---|---|
| `VCardParser` | a realistic Google card: `itemN.` groups, `X-ABLabel`, folded base64 photos, vCard 2.1 bare type parameters (`TEL;WORK;VOICE`), `ORG` with a department, escaping |
| `VCardText` | line unfolding, escape/unescape round trips, structured values split on unescaped semicolons only |
| `VCardWriter.Patch` | photo / `CATEGORIES` / `X-` properties survive; managed properties are rewritten once, not duplicated; deleted values go; unplaced values come back; orphaned `X-ABLabel` lines go with their value; `ORG` departments; patching a patch changes nothing |
| `VCardWriter.BuildNew` | a self-contained card for a phone-only contact |
| `ContactSlots` | slots fill in preference order; a voice number never falls into a fax slot and vice versa; overflow is reported rather than dropped |
| `ContactMapping` | every slot round-trips its label; scalars round-trip; `ManagedKeys` covers every slot that is read (a slot missing from it would keep a stale value) |
| `ContactHash` | the same card gives the same fingerprint; phone-number formatting and name case are not edits; a changed number, note, label or count is |
| `ClearedKeys` | the keys a rewrite clears out of the property map cover every slot, and contain **no** name property - see the regression note below |
| `CredentialsFile` | the two-line account file: `:` or `=`, any spacing and key case, comments, CRLF or LF, spaces stripped from the App Password, and a useful complaint for each way it can be wrong |
| end to end | `NothingIsLostThroughThePhone`: a card with 10 numbers, 4 email addresses, 2 websites and a photo goes onto the phone, past the slot capacity, and comes back with every value intact |

### A regression worth knowing about

`StoredContact.GivenName` and the property map's `"GivenName"` entry are the **same storage**. The
first build listed the five name properties alongside the slots in the keys a rewrite clears, and
cleared them *after* assigning the name - so every contact arrived on the phone with no first or last
name. `ClearedKeysLeaveTypedPropertiesAlone` fails if any of the five is ever put back in that list,
and `ContactWithANameKeepsIt` keeps the card that found it.

Anything added to `ContactSlots.TypedProperties` in future is covered by the same check.

## What needs a device

The rest cannot be exercised off a phone. None of it is logic this app owns - it is the platform
contract - but it is what to check on a real device, in this order.

### 1. It starts

The splash screen, then the main page. A crash on startup paints the exception and how far it got
(see `App.xaml.cs`), so a black screen means something else - usually a package that failed to
deploy, or a missing VCLibs 12.00.

### 2. TLS reaches Google

Tap **sync from Google** with a bad password. The right failure is Google's 401 with the App
Password explanation. The wrong failures, and what they mean:

| Message | Cause |
|---|---|
| `could not reach www.google.com (DNS lookup failed ... WSAGetLastError=10093)` | Winsock was never started - `WSAStartup` in `TlsClient.cpp` |
| `certificate for www.google.com was rejected` | the trust bundle did not load, or does not contain the GTS roots |
| `no CA certificates could be loaded from the trust bundle` | `Assets\google-cacert.pem` is missing from the package |
| a `TypeLoadException` or a missing-class failure | `GCalendarSync.Tls.winmd` is not in the package, or the `activatableClass` registration was not emitted |

`internetClient` is what allows the raw sockets; without it the connect fails.

### 3. The account file

Put a two-line file in Documents as `caldav.cfg` (see
`docs\caldav.cfg.sample`). Both boxes should fill on startup, with no tap at all
in and the banner should name the file.

Then rename it and tap again: the file picker should open instead. Picking the file should fill the
boxes in the same way - the app is suspended while the picker is up and the answer comes back through
`App.OnActivated`, so a pick that appears to do nothing means that continuation is not arriving.

If the Documents path is refused rather than simply empty, the app falls through to the picker
silently, which is the intended behaviour but does hide a missing `documentsLibrary` grant. The
association on `.cfg` in the manifest is what makes the capability effective.

### 4. The store, and the People hub

First successful sync should show the contacts in the People hub under this app's account. Check:

* a contact with several numbers shows them under the right labels (mobile / home / work / fax)
* a contact with a photo on Google has it here
* a contact with no name but an organisation still reads sensibly

The `contacts` capability has to be in the manifest from the 2014 namespace
(`<m3:Capability Name="contacts" />`); the base schema has no such capability and the package is
rejected with APPX3024.

### 5. Incremental download

Change one contact on Google, sync again. Exactly that contact should be reported as updated. Then
sync a third time with nothing changed: the result should be "Already up to date".

Delete a contact on Google and sync: it should disappear from the phone.

### 6. Upload

Edit a contact in the People hub, then **upload phone changes**. Expect:

* only the edited contact is uploaded ("1 of 1 changed contact(s)")
* uploading again straight away reports no changes - if it re-uploads everything, the read-back
  fingerprint is not stable, which is the failure `ContactHash` is written to avoid
* on Google, the edit is there **and** the photo, categories and anything else the phone cannot hold
  are still there

To check the conflict path: edit the same contact on both sides without syncing in between, then
upload. It should be reported as changed on Google, not overwritten.

### 7. Background download

Settings → automatic download on, interval 15 minutes. Then leave the app. The main page's status
line shows what the last background run did and when.

Windows Phone schedules a `TimeTrigger` within another 15 minutes of the interval elapsing, and only
while it is willing to run background tasks at all, so "nothing yet after 20 minutes" is not
necessarily a bug. What *is* a bug: a status line that never changes at all, which usually means the
`EntryPoint` in `Package.appxmanifest` does not match the runtime class name exactly.

The first sync must have been run from the app; the task gives up otherwise, on purpose.

### 8. A large address book

Several hundred contacts is where the differences from W10M start to matter: the multiget batching,
the file-backed state maps, and reading the whole store back for an upload. Watch for the sync
finishing at all on a 512 MB device, and for how long the first download takes.

Contacts are written a batch of 25 at a time, as each batch arrives, rather than the whole address
book being downloaded first and written second. So the progress line counts contacts **saved**, not
paths attempted, and a run that is interrupted leaves behind everything it had already written.

#### Reading a download that wrote nothing

"No contacts were written to the phone" is followed by which of three different things happened,
because from the outside they look identical:

| What it says | What it means |
|---|---|
| *Google listed no contacts for this account* | The account really is empty, or the address is not the one the contacts are in. |
| *N were asked for but did not arrive from Google* | The listing worked and the cards did not: a fetch problem, not a phone problem. |
| *N could not be written to the phone: `<error>`* | The cards arrived and the contact store refused them. The error is the first one verbatim. |

If nothing at all is going right, a download gives up after 50 failures rather than grinding through
the rest — the remaining contacts would fail the same way, and the next sync starts over from the
beginning regardless.
