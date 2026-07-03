# BrowserOS spike

BrowserOS is AGPL-3.0. Do not copy or vendor BrowserOS source into keyboard.wtf.
This folder contains only local spike notes/scripts for evaluating BrowserOS as an external backend candidate.

Allowed integration shape:
- external installed BrowserOS application/runtime
- external browseros-cli/MCP server process
- keyboard.wtf invokes it as an optional backend through JSON/CLI/MCP

Not allowed:
- copying BrowserOS source into src/
- vendoring BrowserOS code
- linking AGPL code into keyboard.wtf
- shipping BrowserOS as embedded code without separate licensing review
