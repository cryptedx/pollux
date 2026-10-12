# Network browsing only when the user goes there

A Tab can show `pollux:network` (the computers Windows discovered) and a bare `\\server` (its disk shares). This is consistent with ADR 0001: Pollux still opens no connection of its own. Listing them asks Windows (the shell's Network folder, `NetShareEnum`), and Windows' own SMB client and network discovery do the talking with the user's credentials, exactly as when the user opens the same place in Explorer. It happens only on an explicit navigation by the user (clicking, typing, Back/Forward to it, or reopening a Tab the user left there, as UNC share Tabs already are); nothing lists the network on its own (not at startup or in the background, not for Home, the sidebar or a prefetch), and neither Network nor a server appears in Recent or as a Home tile. When access is denied, Pollux offers Windows' own credential prompt and never handles a password.

## Consequences

- Network discovery being off in Windows leaves `pollux:network` empty; Pollux does not work around it.
- A slow or unreachable server is shown like a slow folder (loading bar, "still loading" note, Esc cancels); listing it runs off the UI thread.
- Browsing shares of a UNC path (`\\server\share\…`) was already possible and is unchanged.
