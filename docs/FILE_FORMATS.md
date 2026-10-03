# Adding your own data

Put `*.json` files in the folders shown on the Settings page (`<config dir>/definitions`, `pids`, `service`). A file whose `id` matches a built-in entry replaces it. Broken files are skipped and listed in the Terminal log.

## Service functions and output tests (`service/*.json`)
```json
{ "procedures": [ {
    "id": "fan-test", "name": "Cooling fan on", "kind": "outputTest",       // "service" or "outputTest"
    "category": "Engine", "module": "7E0",                                   // omit module to let the user pick one
    "description": "...", "conditions": ["Engine off"], "warnings": ["Keep hands clear of the fan."],
    "minVoltage": 12.0, "confidence": "user", "source": "where you got the bytes",
    "steps":   [ { "hex": "10 03", "desc": "Extended session" },
                 { "hex": "2F F0 01 03 64", "expect": "6F F0 01 03", "desc": "Fan 100 %", "hold": 5 } ],  // hold = seconds, tester present is sent meanwhile
    "cleanup": [ { "hex": "2F F0 01 00", "desc": "Return control to the module" }, { "hex": "10 01", "desc": "Default session" } ]
} ] }
```
`expect` is the start of the positive answer (default: service id + 0x40). Cleanup steps always run, also after a failure or when the user presses Stop. The identifiers above are an example, not real Ford values.

## Module data sets (`pids/*.json`)
Same shape as `src/FordDiag.Core/Live/Definitions/*.json`: `commands` with `hdr` (module request id), `did` (4 hex digits), `signals` with `len` bits, optional `bix` (bit index, MSB first, after `62 DID`), `mul`, `div`, `add`, `min`, `max`, `unit`, `signed`, `map`.

## Coding option names (`definitions/*.json`)
Same shape as `src/FordDiag.Core/Coding/Definitions/*.json`: blocks with fields (`byte`, `bit` = offset of the least significant bit in the field's last byte, `size` in bits, `kind`, `options`).
