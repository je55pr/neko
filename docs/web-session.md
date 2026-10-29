# Browser-session adapter (experimental)

This is an optional local integration for a signed-in website. The application uses a **separate visible WebView2 window** and a user-selected local profile. It never reads cookies or obtains API keys from the browser profile. The main Neko UI remains restricted to its internal local origin.

Copy `web-session.example.json` to an ignored `secrets.json` and supply your own HTTPS home URL, existing (or new) profile directory, route prefix, and CSS selectors. The profile should not be open simultaneously in another application. The adapter only permits top-level navigation on the configured HTTPS host.

**Sending is disabled by default.** `experimentalBrowserSend: true` deliberately enables an unstable browser-composer path. It is not a verified provider-specific adapter and does not guarantee delivery, model selection, streaming finality, or persisted history. Do not use it for consequential messages. No automatic retries are performed after a click when delivery cannot be confirmed.

A result is accepted only when the website visibly renders a new matching user message on a conversation route. Even then, this is UI evidence rather than a server receipt. Assistant completion is reported only when an explicit configured activity selector disappears and the displayed text has stabilized. Missing activity detection never implies completion.

A production provider adapter should replace this with authenticated, correlated acknowledgements and authoritative history reconciliation. Keep site-specific URLs, selectors, and all credentials out of the repository. This example contains no account-specific data.
