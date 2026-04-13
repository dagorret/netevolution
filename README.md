# Nevolution

[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Avalonia](https://img.shields.io/badge/Avalonia-12.0-7C3AED?logo=avaloniaui&logoColor=white)](https://avaloniaui.net/)
[![SQLite](https://img.shields.io/badge/SQLite-Local%20Storage-003B57?logo=sqlite&logoColor=white)](https://www.sqlite.org/)
[![MailKit](https://img.shields.io/badge/MailKit-IMAP%20Client-2C7A7B)](https://github.com/jstedfast/MailKit)
[![Architecture](https://img.shields.io/badge/Architecture-Layered-0F766E)](#architecture)
[![Status](https://img.shields.io/badge/Status-Active%20Prototype-F59E0B)](#current-phase)
[![License](https://img.shields.io/badge/License-Private-lightgrey)](#)

A mail client prototype built in C# with a layered architecture, local SQLite persistence, IMAP synchronization through MailKit, secure password storage through the host OS keyring, and two frontends: CLI and Desktop with Avalonia.

## What It Is

Nevolution is a mail client designed around a simple and extensible architecture:

- synchronizes message headers from IMAP into SQLite
- persists accounts and the active account locally
- stores account passwords in the OS secure store instead of SQLite
- downloads message bodies on demand and in the background
- supports real IMAP folders (`INBOX`, `Sent`, `Drafts`, `Trash`, `Archive`)
- exposes the same data source to both CLI and Desktop
- lets users add accounts directly from the Desktop UI

The current focus is reading experience, incremental sync, and UI consistency when the user switches accounts, folders, or emails quickly.

## Architecture

The solution is split into layers:

- `Nevolution.Core`
  - contracts (`IMailClient`, `IEmailRepository`)
  - domain models (`MailAccount`, `EmailMessage`, `FolderState`, `MailFolderInfo`)
  - application services (`SyncService`, `BackgroundBodySyncService`)
- `Nevolution.Infrastructure`
  - IMAP implementation with `MailKit`
  - SQLite persistence with `Microsoft.Data.Sqlite`
  - platform-specific secret stores (`libsecret`, Windows Credential Manager, macOS Keychain)
- `Nevolution.App.Cli`
  - operational utilities for account setup, account selection, and manual sync
- `Nevolution.App.Desktop`
  - Desktop UI with Avalonia
  - MVVM pattern
  - immediate local rendering plus background IMAP work
  - account creation flow from the UI

## Technology Stack

- `C# / .NET 10`
- `Avalonia 12` for cross-platform Desktop UI
- `MailKit 4.15.1` for IMAP
- `Microsoft.Data.Sqlite 10.0.5` for local persistence
- `Ace4896.DBus.Services.Secrets 1.4.0` for Linux Secret Service integration
- `MVVM` in Desktop
- `SQLite` for accounts metadata, headers, folder state, and message bodies
- native secret storage per OS for credentials

## Current Features

- persisted IMAP accounts in SQLite
- secure password persistence through `ISecretStore`
- active account shared by CLI and Desktop
- incremental header synchronization per folder
- body download:
  - on demand when an email is selected
  - in the background for missing bodies
- `TextBody` and `HtmlBody` support
- HTML fallback to readable text in Desktop
- optimized email selection:
  - immediate UI update from local data
  - placeholder while downloading a missing body
  - mark-as-read without blocking navigation
- optimized folder switching:
  - SQLite first
  - IMAP sync after
  - cancellation and obsolete request discard
- Desktop account creation:
  - main menu
  - add account dialog
  - validation and immediate activation

## Current Phase

Status: **functional prototype / active prototype**

The project already covers the core reading and synchronization flow, but it is still under active construction. In particular:

- it is optimized for reading, not yet for composition or SMTP sending
- passwords are resolved through a single abstraction and persisted in the OS keyring when available
- console instrumentation and diagnostics are already in place
- cancellation, concurrency, and UI responsiveness are actively being hardened

## Repository Structure

```text
src/
  Nevolution.Core/
  Nevolution.Infrastructure/
  Nevolution.App.Cli/
  Nevolution.App.Desktop/
data/
  mail.db
Nevolution.slnx
```

## How To Use

### 1. Requirements

- `.NET 10` SDK
- valid IMAP access
- for Gmail:
  - IMAP enabled
  - an app password if the account uses 2FA

### 2. Restore dependencies

```bash
dotnet restore Nevolution.slnx
```

### 3. Create or register an account

Accounts metadata is stored in SQLite. Passwords are stored through `ISecretStore`:

- Linux: `LibSecretStore` via Secret Service / libsecret
- Windows: Credential Manager
- macOS: Keychain
- fallback: `EnvironmentSecretStore` via `NEVOLUTION_PASSWORD`

You can create an account either from the Desktop UI or through the CLI.

CLI example:

```bash
dotnet run --project src/Nevolution.App.Cli -- account add \
  --email your_mail@gmail.com \
  --password YOUR_APP_PASSWORD \
  --display-name "Your Name" \
  --imap-host imap.gmail.com \
  --imap-port 993 \
  --username your_mail@gmail.com \
  --active
```

Desktop flow:

1. Launch `Nevolution.App.Desktop`
2. Open `Cuenta -> Añadir cuenta`
3. Fill in display name, email, IMAP host, IMAP port, username, and password
4. Save the account and optionally use it immediately

List configured accounts:

```bash
dotnet run --project src/Nevolution.App.Cli -- account list
```

Set the active account:

```bash
dotnet run --project src/Nevolution.App.Cli -- account use --id your_mail@gmail.com
```

### 4. Run synchronization from the CLI

By default, it uses the active account persisted in SQLite:

```bash
dotnet run --project src/Nevolution.App.Cli -- --folder inbox
```

Accepted logical folders include:

- `inbox`
- `sent`
- `drafts`
- `trash`
- `archive`

### 5. Run the Desktop app

```bash
dotnet run --project src/Nevolution.App.Desktop
```

The app:

- opens the Desktop window
- loads the active account from SQLite
- loads the password from the configured secret store
- resolves known IMAP folders
- shows local data first
- runs sync and body download in the background

## Data Location

By default, the local database is stored at:

```text
data/mail.db
```

This can be changed with:

```bash
export NEVOLUTION_DATA_PATH=/path/to/your/directory
```

## Main Data Model

### Accounts

Persisted accounts use this logical schema:

- `Id`
- `Email`
- `ImapHost`
- `ImapPort`
- `Username`
- `DisplayName`
- `IsActive`

`Password` remains part of `MailAccount` only as the in-memory carrier. It is not persisted in SQLite in current versions.

### Emails

Each message persists, among other fields:

- `AccountId`
- `Folder`
- `ImapUid`
- `Subject`
- `From`
- `Date`
- `IsRead`
- `HasBody`
- `TextBody`
- `HtmlBody`

## Technical Flow Summary

### Header sync

1. Connect to IMAP with the active account.
2. Read `UidValidity` and the last local UID for the folder.
3. If `UidValidity` changed, clear the local folder state.
4. Download only new headers.
5. Persist them into SQLite.

### Body download

1. If the body already exists in SQLite, use it immediately.
2. If it is missing, download it on demand or in the background.
3. `BackgroundBodySyncService` avoids duplicate downloads with in-flight coordination.
4. The body is persisted and the UI refreshes only if the result is still valid for the current selection.

### Folder switching

1. The UI loads local messages from SQLite first.
2. It then starts IMAP header sync in the background.
3. If the user switches folders again, the previous work is cancelled or discarded.

## Logging and Diagnostics

The project already includes console logs for:

- Desktop startup
- IMAP authentication
- email selection
- folder switching
- header sync
- body downloads
- cancellation and obsolete requests
- secret store resolution and fallback
- account creation and activation

This makes it easier to diagnose credential issues, host resolution, SSL/TLS problems, latency, and UI race conditions.

## Current Limitations

- no SMTP sending yet
- no message composition editor
- pagination and virtualization are still evolving
- HTML rendering in Desktop currently uses a readable-text fallback, not a full HTML engine
- Desktop account management currently covers add/use flow only; edit/delete/test-connection are still pending

## Short-Term Roadmap

- edit and delete accounts from Desktop
- connection test / validation before save
- improved pagination / cursor-based paging
- SMTP composition and sending
- better HTML rendering
- broader test coverage
- Desktop packaging and distribution

## Quick Build

```bash
dotnet build src/Nevolution.Core/Nevolution.Core.csproj
dotnet build src/Nevolution.Infrastructure/Nevolution.Infrastructure.csproj
dotnet build src/Nevolution.App.Cli/Nevolution.App.Cli.csproj
dotnet build src/Nevolution.App.Desktop/Nevolution.App.Desktop.csproj
```

## Notes

- CLI and Desktop share the same active account and the same SQLite database.
- Existing databases with legacy plaintext passwords are migrated on load into the configured secret store when possible.
- `NEVOLUTION_DATA_PATH` changes the data directory used by both apps.
- `NEVOLUTION_SECRET_STORE` can override secret store selection with `auto`, `environment`, `linux`, `windows`, or `macos`.
- `NEVOLUTION_PASSWORD` can be used as a fallback for CLI/testing environments without a keyring session.
- If IMAP login fails, the app tries to produce explicit diagnostics without logging the password.
- The project is optimized for fast iteration and intentionally keeps the design simple.
