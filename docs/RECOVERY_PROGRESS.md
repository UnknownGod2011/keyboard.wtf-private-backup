# Recovery progress

- Checkpoint: `paid-staging` product source `b4308aa03840d131693df2da024764e593883ffb`; installer `D3D984C972DA11C384F02CEF19576DFD181DDEAE89F93522A20717E6F5524BD3`; installed/package EXE `7BE04C1B875CB120C7C339FF13BB424F657EA7438C5F1FBAD3273B3BEB1A4655`.
- Current problem: P12/P15 omitted form-field completion coverage.
- Proven root cause: terminal form verification accepted every planner-declared check but did not prove the planner declared each explicit user mutation clause; one of two requested Selenium fields was omitted and false `Completed` passed.
- Fix made: dirty source requires distinct final-field checks to cover the conservative count of explicit form-mutation clauses; clause boundaries exclude navigation, quoted payloads and action words inside labels.
- Verification: Exact installed `b4308aa` failure retained with screenshot/task/log. Regression fails before and passes after; 44 verifier, 580 Browser/ownership/lifecycle and 1,180 desktop tests pass. Rebuild/install/replay is next. P40 is physically verified. P14 installed Planning/Reading/Executing/Waiting/NeedsUserAction cancellation passes; Partial awaits the corrected candidate.
- Remaining: finish P12/P15 installed replay; P14 Partial and P34 installed matrix; broader P10-P16 web/direct behavior; P21/P38; long real microphone P04/P08/P09/P20/P32/P37; P23/P43; P41/P42; final P25-P30/P44 audit, frozen candidate, and human acceptance.
