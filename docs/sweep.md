# Privacy and secrets sweep

Run on 11 November 2026, before the first publish, over every tracked file in the working tree (15 files plus this
one) and every blob in the git history (all commits on all refs), binary files included (PNG metadata was searched
for ASCII secrets, addresses and user paths).

## Rules

Each rule was first run against a planted sample to prove it matches (a positive control); all eleven matched.

| Rule | Pattern (in words) |
| --- | --- |
| Keys and tokens | OpenAI, Anthropic, GitHub, AWS, Google, Slack and Hugging Face key shapes; PEM private-key headers; JWTs |
| Key or token assignments | `api_key`, `secret`, `token`, `password` or `bearer` followed by `=` or `:` and a quoted value of 8+ characters |
| Private addresses | `192.168.x.x` and `100.x.x.x` (the Tailscale range) |
| E-mail addresses | anything shaped like `name` at `domain.tld` |
| Hostnames | the owner's first name, a hyphen, then letters or digits, any case |
| Singapore bus stop codes | any standalone five-digit number, plus the words *bus stop*, *BusStopCode*, *arrivelah* and *DataMall* |
| The live-wallpaper app this project replaced | its name, any case |
| Personal names | the owner's first name and surname, his GitHub handle, and the other names used in his other projects |
| User paths | `C:\Users\<name>` in any spelling |

## Result: clean

| Rule | Hits | Verdict |
| --- | --- | --- |
| Keys and tokens | 0 | clean |
| Key or token assignments | 1: `publicKeyToken="6595b64144ccf1df"` in `src/app.manifest` | not a secret: the public Microsoft identifier of the Common Controls v6 assembly, the same in every Windows app manifest |
| Private addresses | 0 | clean |
| E-mail addresses | 0 in files | clean; every commit's author and committer is GitHub's no-reply address for the account |
| Hostnames | 0 | clean |
| Five-digit numbers | 2: a 10,000 ms web timeout and the cursor ID `IDC_ARROW` (32,512), both in `src/DesignYourWindows.cs` | not bus stop codes; no bus stop words anywhere |
| The replaced app's name | 0 | clean |
| Personal names | the copyright line in `LICENSE`, the same line in the exe's `AssemblyCopyright` attribute, and the GitHub handle in two repository URLs in `README.md` | allowed: the licence notice, and the links to this repository and to designyourtv |
| User paths | 0 | clean |

This file matches its own rules only where it names them (the bus stop words in the table above, and the quoted
manifest token). Nothing in the history differs from the working tree in these respects: the same three kinds of expected hit appear in
the older blobs and nothing else.

The images in `docs/screens/` show only procedurally drawn art, Windows' own icons, the date, the time, a battery
reading and the weather for Greenwich (the example place in `config.json`).
