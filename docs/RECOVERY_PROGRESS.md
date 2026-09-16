# Recovery progress

- Checkpoint: `paid-staging` HEAD `5fddef4068b45677d7aa209203d9d22790d94c14`; preserved dirty P15 verifier/tests; installed EXE is still pre-fix `CDADCC46FB51939F74DF4FD503A2AFCF61B25FCBBB2A3C2BFBAE3C3A5BDCEB44`.
- Current problem: P15 ordered-content Browser Automation completion.
- Proven root cause: source extraction crossed a sentence boundary and invented a publisher, so correct NASA visible completion could not satisfy terminal verification and navigation repeated.
- Fix: accept publication-attribution wording and reject cross-sentence source captures inside the existing verifier; retain collection and final-publisher checks.
- Verification: preserved pre-fix failures; dirty-source 1,173/1,173 desktop and 547/547 Browser tests; fresh verifier review 38/38. Packaging, install, and exact Store/Chrome replay pending.
- Remaining: P40; P14/P34 installed matrix; broader P10-P16 web/direct behavior; P21/P38; long real microphone P04/P08/P09/P20/P32/P37; P23/P43; P41/P42; final P25-P30/P44 audit, frozen candidate, and human acceptance.
