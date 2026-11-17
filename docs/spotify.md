# Spotify setup

You can use local playback controls without connecting an account. Connect when you want likes and account playback controls.

## Create your app

1. Open the [Spotify Developer Dashboard](https://developer.spotify.com/dashboard).
2. Create an app for your own use and select the Web API.
3. Add this redirect URI exactly:

   ```text
   http://127.0.0.1:43821/callback
   ```

4. Save the app and copy its **Client ID**.
5. Open Lilt's settings from the tray, paste the Client ID and choose **Connect Spotify**.
6. Finish signing in in your browser. Return to Lilt after the browser confirms the connection.

Use `127.0.0.1`, not `localhost`. Keep the port and path as shown. You do not need a client secret.

If you share your Spotify developer app with a tester, add their account under the dashboard's user access settings first.

## What the two modes do

| Mode | Source | Available controls |
| --- | --- | --- |
| Local | Spotify's Windows media session | Metadata and the transport commands exposed by the desktop client |
| Account | Spotify Web API | Current playback, transport, supported seek/volume actions and likes |

Local mode is intended for Free and Premium accounts. Spotify can restrict individual actions on Free accounts; Lilt follows what the client exposes.

Account mode follows the active Spotify device. If playback moves to your phone, the card follows that playback. Commands target the device associated with the displayed playback state. Likes use the track URI from that same account state.

## Spotify's access limits

For development-mode apps, Spotify currently requires the app owner to have Premium. New apps can allow up to five authenticated users, and each user must be on the app's allowlist. Playback-control API endpoints require Premium for the listening account.

Publishing Lilt's source does not grant everyone access to one shared Spotify app. Each person should configure their own Client ID. A public integration serving a wider audience needs Spotify's separate approval.

Lilt uses the current library endpoints for checking, saving and removing tracks: `GET /me/library/contains` and `PUT` / `DELETE /me/library`.

References: [quota modes](https://developer.spotify.com/documentation/web-api/concepts/quota-modes), [PKCE](https://developer.spotify.com/documentation/web-api/tutorials/code-pkce-flow), [redirect URIs](https://developer.spotify.com/documentation/web-api/concepts/redirect_uri), [library actions](https://developer.spotify.com/documentation/web-api/reference/save-library-items).

## Stored data

Sign-in uses the system browser and PKCE. Lilt never asks for your Spotify password. Access and refresh tokens are stored locally using Windows protection for the current user. Disconnecting removes the saved session.

Keep tokens, local settings and screenshots containing account details out of issues and pull requests.

## If connection fails

- Check the redirect URI character for character.
- Check that the developer app owner has an active Premium subscription.
- Check the account allowlist if Spotify returns an access error.
- Close another Lilt sign-in attempt if the callback port is already in use.
- If Spotify limits requests, let the connection cool down. Repeatedly reconnecting will not reset the quota.
