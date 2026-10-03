# Research notes: talking to Ford CAN systems

Status of each claim is marked **[sourced]**, **[sourced, secondary]** or **[UNVERIFIED]** (must be checked on real hardware before trusting).

## Buses
- Ford OBD-II port carries two CAN networks. HS-CAN on pins 6/14 (500 kbit/s), MS-CAN on pins 3/11 (125 kbit/s). Most vehicles since ~2003-04 have both; many body/infotainment modules (BCM/SJB, IPC on older cars, HVAC, audio) are on MS-CAN. [sourced: FORScan forum "How to access MS CAN bus using FORScan and modified ELM327"; OBDLink FRPM]
- Newer vehicles route more modules onto HS-CAN behind a gateway, so the bus a module lives on varies by model/year; the scanner therefore probes every catalogued module on every bus. [sourced, secondary]

## Adapters
- ELM327 has a user-programmable "protocol B" that defaults to 11-bit CAN at 125 kbit/s (PP 2C=E0, PP 2D=04; baud = 500/divisor). A **modified** ELM327 adds a DPDT switch that wires the chip to pins 3/11 instead of 6/14; FORScan asks the user to flip it. [sourced: ELM327 datasheet; FORScan forum]
- STN-based adapters (OBDLink EX = STN2120) have a real MS-CAN transceiver. Protocol preset **53** = ISO 15765, 11-bit, 125 kbit/s on the MS-CAN pins; `STP 53` selects it. [sourced: OBDLink FRPM section "Protocol presets"]
- vLinker FS: marketed as automatic HS/MS-CAN switching, 3 Mbps UART, FTDI VCP, 115200 baud in FORScan. **How** it switches (which command) is not documented publicly; we assume selecting protocol B (`ATPB E004` + `ATSP B`) routes to MS-CAN. [UNVERIFIED - needs a real vLinker FS and a car]
- Clone ELM327s: many corrupt data above 38400 baud; genuine/STN units run 115200+. FORScan wants ELM327 v1.4b or later. [sourced, secondary]

## Protocol
- Diagnostics are UDS (ISO 14229) over ISO-TP (ISO 15765-2) on 11-bit CAN ids; response id = request id + 8 (PCM 7E0/7E8, BCM 726/72E, ABS 760/768, IPC 720/728, APIM 7D0/7D8, AWD 703/70B). [sourced for PCM/BCM/AWD (openRS_ project), ABS/IPC/BCM (Ford as-built docs); APIM 7D0 secondary]
- Standard services used: 0x10 session, 0x22 read DID, 0x2E write DID, 0x19 DTC, 0x14 clear, 0x3E tester present, 0x27 security access. Ford identification DIDs: F190 VIN, F188 strategy, F113 assembly, F124 calibration. [sourced, secondary: Ford IDS/FDRS docs]
- (Superseded by the Coding section below.) **As-Built** data is Ford's per-module configuration, addressed as `MMM-BB-LL` (module request address, block, line), e.g. `726-01-01` = BCM block 1 line 1. [sourced: FORScan forum]


## Sources
- FORScan forum: https://forum.forscan.org/viewtopic.php?t=4 and https://forscan.org/forum/viewtopic.php?t=17208
- OBDLink Family Reference and Programming Manual: https://www.scantool.net/scantool/downloads/682/obdlink_frpm_f.pdf
- ELM327 datasheet: https://www.elmelectronics.com/wp-content/uploads/2016/07/ELM327DSF.pdf
- openRS_ (Ford Focus RS UDS/CAN ids and PIDs): https://github.com/klexical/openRS_
- vLinker FS: https://vgatemall.com/products-detail/i-19/ ; setup notes https://community.cyanlabs.net/t/updating-firmware-and-initial-setup-vgate-vlinker-fs-in-forscan/5539
- Ford As-Built reference: https://www.vehicleservicepros.com/service-repair/diagnostics-and-drivability/article/21175699/using-as-built-data


## Coding (as-built) - added in v0.2
- **Line format.** `MMM-BB-LL d1 d2 d3 d4 d5 cs`: module request address, block, line, five data bytes and a checksum byte. [sourced: FORScan forum worked example `720-01-01 044A 3464 202F`; Ford service-site printout; consp/apim-asbuilt-decode `asbuilt.py`]
- **Checksum.** `cs = low byte of (address high byte + address low byte + block + line + sum of the data bytes)`. Derived from the forum example, **verified on all 34 complete lines of a real Ford As-Built printout** (e.g. `724-01-01 1892 0799 77`, `7D0-01-01 0800 4000 0021`) and identical to the formula in consp's decoder. FORScan itself recalculates it when the field is written as 00. [verified]
- **Blocks and DIDs.** A block is the concatenation of its lines' data bytes without checksums; block N is data identifier `DE00 + N - 1` (APIM: "Block 1 maps to 7D0-01 or DE00"; UCDS XML labels blocks `DE00, DE01...`). [sourced for the APIM and UCDS; **assumed for other modules**]. The app never trusts the layout blindly: it reads the module's actual blocks first, refuses a write when a block length differs, and always diffs against a fresh read before writing.
- **File formats.** FORScan `.abt`/text lines (dash or `7D0G5G1...` form), UCDS XML (`DExx` elements), Ford service-site `.ab` XML (best effort: elements with a `LABEL` and `CODE` children; the schema is not documented). [sourced from consp's parser]
- **Option definitions (byte, bit, size, value names).** Bundled: Ford APIM SYNC 3 (312 options) and SYNC 4 (248 options), converted by `tools/convert_apim_definitions.py` from consp/apim-asbuilt-decode (GPL-3.0, compatible with this project's licence), which took them from QNX debug information and community findings. Recognised by block sizes (SYNC 3: 10,12,5,7,6,1,16,10,20,20; SYNC 4: 20,15,15,5,15,6,16,10,25,25). [sourced; accuracy is the upstream project's]
- **CyanLabs As-Built Database** (https://cyanlabs.net/asbuilt-db/, confirmed free to use by the project owner): 55 pages for BCM, IPC, ABS, PSCM, SCCM, HVAC, ACM, DSP, PAM, TCU, FCIM, RCM, SECM, IPMA/IPMB, APIM SYNC 1-4 and more, imported with `tools/import_cyanlabs.py` (4,805 options). Each option has a title, a location mask such as `726-01-01: x*xx-xxxx` and `value=label` lines. How the masks were decoded:
  - The mask is the hex digits of the **whole line, including the 2-digit checksum at the end**; `*` marks the nibbles the option occupies (`x*xx-xxxx` = 3 data bytes + checksum, option in the low nibble of byte 0). So lines are **not always 5 data bytes** (BCM 3-4, SCCM 4, APIM 5); saved files now follow each definition's line widths.
  - Nibble 0 is the high nibble. Verified against the independent consp APIM data: CyanLabs' first nibble contains exactly consp's Smart DSP/AAM/SDARS/RSEM flags and the second nibble PDC HMI/Rear Camera/Illumination, which also showed that consp's `bit` counts from the **most significant bit** (corrected in the converter).
  - Values may be `0=...` lists, ranges (`60 thru FF=Reserved`), `Hex2Dec(**)` numbers with units, or `HEX=DECx0.01+0=Value (cm)` scalings; all are imported. Items with unusual masks (352 of 5,157) are skipped.
  - Line widths are inferred from the masks that exist on each line; lines without any documented option are guessed, so such layouts are flagged "approximate" in the app. Several generations share a module address, so the app lists every definition that fits and preselects the one whose part-number prefix (e.g. `BC3T`) matches the module's F188/F113.
- Forum threads were not copied (per-vehicle, incomplete). Users can add `*.json` definitions to `<config dir>/definitions`, and Compare diffs two vehicles' files to find unknown bits, which is how the community finds them.
- Sources: https://cyanlabs.net/asbuilt-db/ · https://github.com/consp/apim-asbuilt-decode · https://forum.forscan.org/viewtopic.php?t=7353 · https://forscan.org/forum/viewtopic.php?t=17208 · https://cyanlabs.net/asbuilt-db/ (reference only)


## Vehicle identification, module PIDs, service functions (v0.3)
- **VIN.** ISO 3779: 17 characters without I/O/Q; position 9 is a check digit (weights 8,7,6,5,4,3,2,10,0,9,8,7,6,5,4,3,2; X = 10), mandatory in North America only; position 10 is the model year (30-year cycle; in North America a letter in position 7 means 2010-2039); position 11 the plant. Ford world manufacturer identifiers are a short built-in table. Model/trim/engine come from NHTSA's vPIC (`DecodeVinValues`, free, no key) on request. [sourced: NHTSA vPIC docs; ISO 3779]
- **Module PIDs.** The only open, structured Ford mode 22 data found is [OBDb](https://github.com/OBDb) (CC-BY-SA-4.0): per-model signal sets with module header, DID, bit position, scaling, unit and model-year filters. The decoding rules (bit index MSB first after `62 DID`, `raw*mul/div+add`, clamp to min/max, null bounds, byte swap) were taken from OBDb's own reference code. 28 Ford/Lincoln sets, 4,800+ signals, mostly PCM (7E0), BCM (726), ABS (760), IPC (720). The FORScan PID list shipped by openRS_ (MIT) has names and units only (no DIDs or formulas), so it was not used. Forum threads that carry PID lists (focusrs.org, ford-trucks.com) redirect automated access to a third-party gateway and were not used. Signals the source marks `dbg` are shown as "experimental" and hidden by default.
- **Service functions.** Only two kinds of entries could be sourced: (a) ISO 14229 ECUReset (service 0x11) and (b) the battery monitoring system reset, described in community guides as BCM 0x726, extended session, RoutineControl 0x31 routine `201A`. (b) is a single community description that I could not trace to a primary source, so it is labelled "community-reported, not verified" and gated by expert mode. Oil-life reset, steering angle calibration, crash flag reset, DPF regeneration and others: no open source for their identifiers was found (Ford's owner manual pages only describe manual button procedures).
- **Output tests (0x2F).** No Ford-specific input/output control identifiers are published openly. Ford's service content describes outputs but not DIDs; FORScan and IDS keep theirs private. The bundled test is the standard SAE J1979 mode 08 support probe; the engine supports user-supplied 0x2F tests with a hold time, tester present and guaranteed cleanup.


## Capture (v0.4)
- **Why.** The Ford-specific routine ids, actuator tests and PID scaling that are missing from open sources are visible on the wire when a tool such as FORScan runs against your own car. FORScan's licence (clause 6.D) forbids reverse engineering, decompiling or disassembling the program, so its executable and data files were deliberately not examined; observing the ELM327 conversation between a program and an adapter does not touch the program.
- **How.** A TCP proxy (the program connects as to a WiFi ELM327) bridges to the real serial/TCP adapter and follows UART speed changes (`ATBRD`, `STSBR`). The decoder tracks `ATSH/ATCP/ATCRA/ATCAF/ATH` and reads both manual ISO-TP (raw frames, flow control, multi-frame requests and answers) and automatic formatting (payload lines, `0:`/`1:` continuation lines); the trailing "expected responses" digit of data commands and "response pending" (7F xx 78) are handled.
- **Drafts.** Tester present, reads and failed requests are dropped; a closing session request and output-control hand-backs become cleanup; gaps over 1.5 s become hold times; security access (0x27) and programming requests produce warnings because seeds change and flashing must never be replayed. Captured procedures are labelled "captured" and still need expert mode and confirmation to run.
