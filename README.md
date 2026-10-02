# GCalendarSync

Sync **Google Contacts** to a **Windows Phone 8.1** device over **CardDAV**, using nothing but a
Google App Password. No Google Cloud project, no OAuth client, no People API.

The same package also installs and runs on **Windows 10 Mobile** - it targets WP8.1, and W10M runs
WP8.1 packages. Everything below is written for WP8.1 because that is what it is built against; the
contact API it uses (`Windows.Phone.PersonalInformation`) is still present on W10M.

This is the WP8.1 companion to
[GCalendarSyncCardDAVW10M](https://github.com/infodimus/GCalendarSyncCardDAVW10M), which does the
same job on Windows 10 Mobile. It is a separate application rather than a shared codebase, because
the two platforms' contact APIs have almost nothing in common - see
[How it differs from the W10M version](#how-it-differs-from-the-w10m-version).

---

## What it does

* **Download (Google → phone)** — manually from the app, and automatically on a timer. The
  interval is a setting; Windows Phone will not run a background task more often than every
  **15 minutes**, so that is the floor.
* **Upload (phone → Google)** — **manual only**, from the button on the main page. Same decision
  the W10M version makes: a background run cannot ask the user anything, and an accidental edit on
  the phone should not reach Google unattended.
* The first download fetches everything. Every one after that compares stored ETags against the
  server's and touches only what changed.
* Contacts land in the app's own contact store, which the People hub shows alongside every other
  account, and which the user can edit in place.

There are **no notifications** of any kind.

---

## Getting started

### 1. A Google App Password

<https://myaccount.google.com/apppasswords> — 16 characters, and it needs 2-Step Verification
switched on for the account. Your normal Google password will not work; Google rejects Basic auth
with it.

### 2. Build

```
build.bat                  ARM, Release, version bumped   (what you deploy to a phone)
build.bat x86 Release      the WP8.1 emulator
build.bat both Release     both, at the same version
build.bat ARM Release nobump
build.bat clean            wipe build output (AppPackages is kept)
```

Needs **Visual Studio 2015** (MSBuild 14) *and* **Visual Studio 2013's** WP8.1 C++ tooling, for the
reasons in [`src/GCalendarSync.Tls/build-tls.bat`](src/GCalendarSync.Tls/build-tls.bat). The
signing certificate is generated on the first build if it is missing.

The version is bumped on every build on purpose: WP8.1 will **not** replace an installed app with a
package of the same version. The deployment reports success and the phone quietly keeps running the
old binary.

### 3. Install on the phone

> Upgrading from a build named GCalendarSync? The package identity and the signing certificate
> are deliberately unchanged, so the `.appx` alone upgrades it in place - your synced contacts, saved
> account and ETags all carry over, and there is no new `.cer` to install. Only the names changed.

From `src/GCalendarSync/AppPackages/GCalendarSync_<version>_ARM_Test/`, in this
order:

1. `GCalendarSync_<version>_ARM.cer` — the signing certificate, once per phone
2. `Microsoft.VCLibs.ARM.12.00.Phone.appx` — the C++ runtime the TLS component needs, once
3. `GCalendarSync_<version>_ARM.appx`

`AppPackages/` itself is not tracked — it is build output, and every build writes another few
megabytes of it. Run `build.bat` and it is there.

#### About the signing certificate

`src/GCalendarSync/GCalendarSync_TemporaryKey.pfx` **is** committed, which is not the usual advice.
The reasoning: it is a self-signed sideloading certificate for a test app that will never be
submitted to the Store, it carries no authority anywhere except on a phone that has been told to
trust it, and having it in the repo is what lets any clone produce a package the phone accepts as an
*upgrade* of an installed copy rather than as a different app — which is the point of keeping the
package identity fixed. The Windows 10 Mobile version this was ported from does the same.

The cost, stated plainly: anyone with this repo can build a package that a phone already trusting
this certificate will install over the app. For a personal test build that is a fair trade; for
anything that mattered it would not be. If it ever needs rotating, `make-cert.ps1` writes a new pair
— but a package signed by a different key cannot upgrade one signed by this one, so the app has to be
uninstalled first, and that takes its synced contacts and sync state with it.

### 4. Sync

Enter the Gmail address and the App Password, tap **sync from Google**. The first sync is the long
one. After that, automatic download is scheduled by itself, and **upload phone changes** sends back
whatever you have edited in the People hub.

### Typing the App Password only once

A 16-character App Password is no fun on a phone keyboard, so **load credentials from file** in the
app bar reads it from a two-line text file:

```
login: someone@gmail.com
password: abcdefghijklmnop
```

The app reads `Documents\caldav.cfg` **on startup**, with no tapping at all, and a file put there
outranks whatever account was saved before - that is what makes swapping accounts for a test one
file copy. **load credentials from file** re-reads it, and opens a file picker if it is not there,
so the file can live anywhere.

The separator can be `:` or `=`, spacing and key case do not matter, `#` and `;` start a comment,
and the spaces Google puts in an App Password are stripped. Template:
[`docs/caldav.cfg.sample`](docs/caldav.cfg.sample).

It only ever reads. There is no "save credentials" on purpose - writing an App Password into
Documents, where any app holding the `documentsLibrary` capability can read it, would be a worse deal
than typing it. The app's own copy goes in the Credential Locker.

---

## Why it ships its own OpenSSL

Windows Phone 8.1's certificate store was frozen in 2014. `www.google.com` chains to a **Google
Trust Services** root (GTS Root R1, in use since 2018) that simply is not in it, so the platform's
own TLS stack cannot validate the CardDAV endpoint at all.

So the app carries its own TLS:

* [`src/GCalendarSync.Tls`](src/GCalendarSync.Tls) — a small C++/CX Windows Runtime component
  wrapping OpenSSL 1.0.2r. It exposes a *stream* (connect / write / read / close), not an HTTP
  client. A WP8.1 C# app cannot P/Invoke a plain DLL, so a WinRT component is the only route to
  native code.
* [`src/GCalendarSync.Core/Net`](src/GCalendarSync.Core/Net) — HTTP/1.1 on top of that
  stream, as an `HttpMessageHandler`, so `HttpClient` and everything above it is ordinary C#.
* [`certs/mozilla-cacert.pem`](certs) — the trust anchors, shipped inside the package and handed to
  the native code as bytes. Nothing depends on a root store nobody can update.

The vendored OpenSSL build is in [`lib/openssl`](lib/openssl), with
[`PROVENANCE.md`](lib/openssl/PROVENANCE.md) recording exactly how to rebuild it. The same
arrangement as [OKLumessenger](https://github.com/infodimus/OKLumessenger), which is where this
layer comes from.

**Why v120 and not v140:** forced by the platform. `Microsoft.Cpp\v4.0\V140` has no
`Application Type\Windows Phone`, and the only VCLibs Extension SDK for WindowsPhoneApp 8.1 is
12.0. So the native component is `v120_wp81`, the OpenSSL it links matches, and the package depends
on VCLibs 12.00.

---

## How it differs from the W10M version

Same protocol, same sync model, same ETag bookkeeping. What changed is everything that touches the
phone, because WP8.1's contact API is a different shape.

### Contacts are slots, not lists

W10M's `Windows.ApplicationModel.Contacts.Contact` holds unbounded lists of phone numbers and email
addresses, each with a free-text label. WP8.1's `Windows.Phone.PersonalInformation.StoredContact`
holds a **fixed set of named slots** — `MobileTelephone`, `Telephone`, `WorkTelephone`,
`HomeFax`, `WorkEmail`, `OtherAddress` and so on. The slot *is* the label.

That is better in one way: labels round-trip exactly, where the W10M version has to keep a separate
`LabelStorage` map because the People app does not preserve its `Description` strings. But it caps a
contact at **9 phone numbers (7 voice, 2 fax), 3 email addresses, 3 addresses and 1 website**, and a
Google contact can hold more.

The slot names are strings rather than an enumeration, so an invented one compiles and is rejected
only by the phone, at run time, with `ArgumentException: The parameter is incorrect.` — which is how
a plausible-looking `HomeTelephone` survived in these tables until a test account full of home
numbers found it. The home phone is `Telephone`; there is no `HomeTelephone`, and no slot at all
for "other". A test now checks every name in the tables against the real `KnownContactProperties`.

See [`ContactSlots.cs`](src/GCalendarSync.Core/Services/ContactSlots.cs) for the mapping.

### Uploads patch, they do not rebuild

A CardDAV `PUT` replaces the whole card. Building a fresh card from what the phone holds would
delete the contact photo, categories, IMPP handles, every `X-` property Google keeps — and any value
past the slot capacity above. (The W10M version does rebuild, and does lose them.)

So the card Google last sent is kept on disk, and an upload rewrites **only** the properties the
People hub can express, leaving every other line exactly as it came. Values that never reached the
phone for want of a slot are recorded when the contact is written and put back on upload, so absence
from the phone means "the user deleted it" and nothing else.

See [`VCardWriter.cs`](src/GCalendarSync.Core/Services/VCardWriter.cs) and
[`CardCache.cs`](src/GCalendarSync.Core/Storage/CardCache.cs).

### Change detection fingerprints what the phone actually kept

Both versions decide what to upload by comparing a hash. The W10M version hashes the contact it
*wrote*; this one re-reads the contact after writing it and hashes **that**, because slot capacity,
ordering and the store's own normalisation all mean the two differ. Hashing the written form makes
every contact look edited on the next upload.

See [`ContactHash.cs`](src/GCalendarSync.Core/Services/ContactHash.cs).

### One connection per request, so fewer requests

The OpenSSL handler opens a fresh connection per request, which makes request count matter far more
than it does on W10M: 500 contacts fetched one `GET` at a time would be 500 TLS handshakes. The
client batches with an `addressbook-multiget` `REPORT` (RFC 6352 §8.7), 25 cards at a time, and
falls back to per-contact `GET`s if a server refuses the REPORT.

See [`CardDavClient.cs`](src/GCalendarSync.Core/Services/CardDavClient.cs).

### Smaller things

| | W10M version | here |
|---|---|---|
| Sync logic | duplicated in the app and the background component | one `SyncEngine` both call |
| Sync bookkeeping | one `LocalSettings` entry per contact | one tab-separated file per map |
| Websites | known not to sync | `KnownContactProperties.Url`, so they do |
| Contact photos | parsed, never applied | set as the contact's display picture |
| Middle names | dropped (display problems) | `AdditionalName` slot |
| Upload conflicts | last writer wins | `If-Match`, so a contact changed on Google is reported, not overwritten |
| Full re-sync | empties the contact list and rewrites | updates in place, removes only strays |

---

## Layout

```
src/GCalendarSync.Tls/                 C++/CX WinRT component over OpenSSL (per-architecture)
src/GCalendarSync.Core/                the sync engine - everything except the UI
    Models/                            VCardContact, the one in-memory shape
    Net/                               HTTP/1.1 over the TLS stream + the trust bundle
    Services/                          vCard parse/write/patch, CardDAV, phone contacts, hashing
    Storage/                           credentials, ETags, cached cards, settings
    Sync/                              SyncEngine, background registration
src/GCalendarSync.BackgroundTasks/     the periodic download (winmdobj)
src/GCalendarSync/                     the app: main page and settings
tests/                                 desktop tests for the platform-neutral half
lib/openssl/                           vendored OpenSSL 1.0.2r for WP8.1, ARM and x86
certs/                                 the CA bundle that ships in the package
docs/TESTING.md                        what the tests cover and what needs a device
docs/caldav.cfg.sample                 template for the account file
```

---

## Tests

```
tests\run-tests.bat
```

238 checks over the vCard parser, the writer/patcher, the slot mapping, the change fingerprint and
the credentials file — compiled from the **real** `Core` sources, not copies, so they cannot drift
from what ships. The interesting one is `NothingIsLostThroughThePhone`: a crowded Google card goes
onto the phone, past the slot capacity, and comes back with every value and the photo intact.

What still needs a device is listed in [`docs/TESTING.md`](docs/TESTING.md).

---

## Known limitations

* A birthday with no year (`BDAY:--0409`) does not reach the phone — WP8.1 stores a birthday as a
  `DateTimeOffset`, which needs one. Google keeps its copy, because uploads patch.
* The website that does fit on the phone loses its `work`/`home` label, as the WP8.1 `Url` slot
  carries no type. Extra websites keep theirs.
* A phone number Google labels **Other** arrives as a second *home* number, because the phone has
  no slot for that label. Uploading a contact you have edited relabels it on Google. The number
  itself is never lost; hiding it from the phone to keep the label would be the worse trade.
* Only the first organisation unit survives on the phone. A department (`ORG:Acme;Sales`) is kept
  in the card and restored on upload as long as the company name is unchanged.
* New contacts created on the phone are supported by the code, but the People hub does not normally
  offer an app's own store as a destination for a new contact, so it is not a path you can expect
  to use.
* Automatic sync only runs while Windows Phone is willing to run background tasks at all, and it
  schedules them within another 15 minutes of the interval elapsing.

---

## Credits

The CardDAV sync model is a port of
[GCalendarSyncCardDAVW10M](https://github.com/infodimus/GCalendarSyncCardDAVW10M), whose People API
lineage traces to [@Computershik73](https://github.com/Computershik73)'s
[WPGContacts](https://github.com/Computershik73/WPGContacts) and
[SymGContacts](https://github.com/Computershik73/SymGContacts).

The OpenSSL/TLS layer comes from [OKLumessenger](https://github.com/infodimus/OKLumessenger).

This code was written with the assistance of Claude Code, Anthropic's agentic coding tool.

## Licence

GPL-2.0-or-later. See [LICENSE](LICENSE).
