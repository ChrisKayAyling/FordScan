# Third-party material

FordDiag is licensed under the GNU General Public License v3.0 or later (see `LICENSE`).

| Material | Where | Source / licence |
|---|---|---|
| ELM327 / STN transport, ISO-TP, vehicle simulator | `src/FordDiag.Comms` | Originally written by the same author for a separate project; GPL-3.0-or-later |
| APIM SYNC 3 / SYNC 4 option definitions | `src/FordDiag.Core/Coding/Definitions/apim-*.json` (converted by `tools/convert_apim_definitions.py`) | [consp/apim-asbuilt-decode](https://github.com/consp/apim-asbuilt-decode), GPL-3.0 |
| Option definitions for BCM, IPC, ABS, PSCM, SCCM, HVAC, ACM, DSP, PAM, TCU, FCIM, APIM (SYNC 1, 2, 3, 4) and others | `src/FordDiag.Core/Coding/Definitions/cyan-*.json` (imported by `tools/import_cyanlabs.py`) | [CyanLabs As-Built Database](https://cyanlabs.net/asbuilt-db/), free to use; credit to the CyanLabs community. Each definition keeps its source link and attribution text |
| Module data (mode 22) signal sets, 28 Ford/Lincoln model sets, 4,800+ signals | `src/FordDiag.Core/Live/Definitions/obdb-*.json` (imported by `tools/import_obdb.py`) | [OBDb](https://github.com/OBDb) Ford repositories, CC-BY-SA-4.0 (attribution kept in each file; one-way compatible with GPL-3.0). Decoding rules follow OBDb's reference implementation |
| VIN model/engine lookup (optional, on request) | `NhtsaVinClient` | NHTSA vPIC public API (free, no key) |

Ford, FORScan, OBDLink, vLinker and other names belong to their owners. This project is not affiliated with or endorsed by them.
