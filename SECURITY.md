# Security policy

Please use the GitHub repository's **Security** tab and **Report a vulnerability** private-advisory form. If private reporting is not enabled, contact the maintainer through their GitHub profile to request a private channel without including exploit details. Do not open a public issue containing a vulnerability or sensitive diagnostics.

Include the affected version, Windows and Codex versions, reproduction steps, and the smallest useful diagnostic excerpt. Remove usernames, local media names, conversations, cookies, tokens, API keys, and complete browser-profile paths.

Security-sensitive areas include:

- accepting or exposing a non-loopback CDP endpoint;
- connecting to the wrong browser or renderer target;
- executing wallpaper-supplied script or an Application wallpaper;
- bypassing `scene.pkg`, texture, shader, dimension, or WebGL resource limits;
- escaping the selected media or Wallpaper Engine project directory;
- reading Codex authentication, browser storage, chats, or network traffic;
- cleanup that removes nodes, files, or processes not owned by this project.

Do not publish a proof of concept until a fix and coordinated disclosure date are agreed.
