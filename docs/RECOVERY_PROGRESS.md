# Recovery progress

- Checkpoint: installed `d444221ad25ac6250b1bfb227b7c7d2123abab13`; installer `A1E27181713C0DF0BA663F65471487D4E31785B14DA1F746CFB202D2CA132A13`; installed/package EXE `87711C523744EDC506ED077071C94B8B2BCA8E036CB97272D8C225BE9EC681AB`; final P15 delayed-link correction is dirty.
- Current problem: P15 Devpost distinct-site action identity, then P34 installed guidance matrix.
- Proven root cause: after identity fixes selected the correct RevenueCat card, its real same-tab link took over five seconds to commit; the immediate no-change receipt caused replanning from the old list and a guessed wrong slug.
- Fix made: retain both identity guards; additionally reconcile only a successful no-change link click against an observed URL change on the same exact owned tab, with an eight-second bound.
- Verification: exact installed `d444221` proves all wrong-control mutations are gone; Playwright proves the correct live href and delayed commit; delayed-link tests, 671 Browser tests and all 1,188 desktop tests pass. Rebuild/install/exact replay is next. P12/P14/P40 are physically verified.
- Remaining: P15 distinct-site acceptance; P34 installed matrix; broader P10-P16 web/direct behavior; P21/P38; long real microphone P04/P08/P09/P20/P32/P37; P23/P43; bounded Screen Analysis Gemini 3.5/3.8 latency-cost benchmark; P41/P42; final P25-P30/P44 audit, frozen candidate, and human acceptance.
