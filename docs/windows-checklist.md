# Windows check

Use a real Windows 10 22H2 desktop with the installed Spotify client. Build checks and the startup smoke check do not replace these checks.

Record the Windows build, display scaling, graphics adapter, Spotify version and Lilt commit when testing.

## First launch

- [ ] Extract the portable zip to a clean folder and launch `Lilt.exe`.
- [ ] Confirm the tray icon appears and the card has no taskbar button or Alt+Tab entry.
- [ ] With Spotify closed, confirm the empty state is readable and controls are unavailable.
- [ ] Open Spotify and start a track. Confirm title, artists, art and play state update.
- [ ] Restart Spotify while Lilt stays open. Confirm the session reconnects.

## Window and input

- [ ] Keep another app active and use play/pause, previous and next. Confirm keyboard focus remains in the other app.
- [ ] Cover the card with ordinary apps. Confirm always-on-top works when enabled and can be turned off.
- [ ] Move and resize the card from every edge and corner.
- [ ] Resize down to the minimum. Confirm all essential controls remain reachable and no text overlaps buttons.
- [ ] Expand the card. Confirm added details and controls fit without scrollbars.
- [ ] Enable click-through from the tray. Confirm buttons, dragging and resizing all pass through to the app below.
- [ ] Use the tray to restore interaction, then repeat using the recovery shortcut.
- [ ] Hide and restore the card using both the tray and shortcut.
- [ ] Open settings from the tray, change values and close them. Confirm normal focus works in settings.

## Displays and appearance

- [ ] Try 100%, 150% and 200% display scaling.
- [ ] Move between monitors with different scale factors.
- [ ] Restart and confirm the saved size and position are restored.
- [ ] Disconnect the monitor that held the card. Confirm it can be recovered on a visible display.
- [ ] Check dark, light and moving backgrounds for readable text and clean edges.
- [ ] Disable Windows transparency effects. Confirm the fallback remains readable.
- [ ] Check drag and resize smoothness with blur enabled.
- [ ] Try a borderless game. Record its behaviour; do not mark exclusive fullscreen as supported.

## Playback

- [ ] In local mode, play/pause and switch tracks while Spotify is minimised.
- [ ] Play a video in another app. Confirm Lilt's controls still target Spotify.
- [ ] Check pause, buffering/no active playback, advertisements and local files where available.
- [ ] If testing Free, record which transport actions Spotify permits.
- [ ] In account mode, confirm the displayed track and device agree with Spotify.
- [ ] Like and unlike a track. Verify both changes in Spotify.
- [ ] Switch tracks quickly and use like. Confirm a late response does not change the next track's heart.
- [ ] Transfer playback to a phone. Confirm the displayed state and command target stay consistent.
- [ ] Confirm seek and volume appear only when the layout and device allow them.

## Recovery

- [ ] Disconnect the network during playback, then reconnect it.
- [ ] Let the computer sleep and wake it again.
- [ ] Disconnect the Spotify account. Confirm saved credentials are removed and local playback remains usable.
- [ ] Reconnect and confirm the sign-in flow completes once without leaving a callback listener running.
- [ ] Enable startup, sign out of Windows and sign back in. Disable startup afterward.
- [ ] Leave the card idle, paused and hidden; inspect CPU and memory use for sustained activity or growth.
- [ ] Quit from the tray. Confirm no Lilt process or tray icon remains.

## Automated startup check

`Lilt.exe --smoke-test` opens the native window and exits automatically. It checks startup and resource loading. It does not prove that glass, input routing, Spotify playback or OAuth works on a real desktop.
