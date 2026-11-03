# Contributing to Amberline

Amberline does not take pull requests; pull requests are closed automatically. There are two ways to help:

- **Open an issue.** Report a bug or ask for a feature in the repository's Issues, with the issue template. The maintainer fixes
  it in Amberline itself.
- **Fork it.** Amberline is MIT-licensed: change your fork as you like and run it yourself.

Amberline only works if it stays free of other people's code and files. The rules below apply to everything in this repository,
to what you attach to an issue, and to any fork that wants to stay clean.

## The clean-room rule

- **Write everything yourself, from observed behaviour.** What the client sends and accepts on the wire, what it shows, and
  what the player's own data files say are fair inputs. Describe the behaviour in your own words and implement it from that
  description.
- **No code from the game or AIR client.** Do not contribute code, pseudo-code, structure layouts, function names or addresses
  recovered from either client's binaries with reverse-engineering tools, and do not paraphrase such output line by line. If
  you have studied that output for a feature, let someone who has not write the code from your written behaviour description.
- **No client files and no data copied from them.** No executables, libraries, SWFs, images, sounds, fonts, maps, scripts,
  game data files or extracts of them; no tables of values copied out of them. Code reads such data from the player's own
  installation at run time.
- **No captures or logs of real sessions.** No packet captures, client logs, crash dumps or screenshots, and no recorded test
  corpora built from them. Checks that need recorded behaviour record it from your own build and keep it out of the repository.
- **No personal data.** No names, e-mail addresses, IP addresses, host names, account or summoner names, or local paths
  (`C:\Users\...`, home folders) in code, comments, checks or commit messages.
- **Nominative naming only.** Protocol names the client needs (the AMF class aliases, RTMP destinations, XMPP domains, data
  paths and script names) stay as the client expects them, kept in their protocol files. Everything else uses Amberline's own
  names; do not use the game's or its publisher's trademarks as branding.

## Code markers

- `NOT RETAIL:` marks a deliberate departure from the original game's behaviour, or an addition it never had, with the reason.
- `Reconstruction:` marks behaviour inferred where the original cannot be observed.
- `DIAGNOSTICS:` marks test and development aids that change no gameplay.

## What to put in an issue

1. What you did, what you expected, and what happened instead.
2. The Amberline version: the `commit` value of `BUILD.json` in your server folder.
3. Your Windows version, and whether the server runs on your own PC or on a rented server.
4. The error text, copied as text. Before you paste a log, remove names, e-mail addresses, IP addresses and host names.
5. Never attach game or AIR client files, packet captures or crash dumps.
