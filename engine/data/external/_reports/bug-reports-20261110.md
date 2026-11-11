# Bug reports and official known issues — raw research table (2026-11-10)

Reported cases, not team measurements. Summary and usage: `docs/security-software-survey.md` section 6.
Microsoft, Firefox, Chromium, EA and LOLDrivers pages were read raw; GitHub, Bugzilla, Riot, Corsair and bank pages were read through a summariser, so re-check exact strings before hard-coding them in rules.
Grade: A vendor/Microsoft official, C maintainer-confirmed issue or professional media, D community. All rows were opened unless marked "snippet".

Source keys
- RH = https://learn.microsoft.com/en-us/windows/release-health/resolved-issues-windows-11-24h2 (live, dated 2026-11-05)
- WBa / WBb = web.archive.org/web/20241216080616/ and /20250415093732/ + https://learn.microsoft.com/en-us/windows/release-health/status-windows-11-24h2
- WBc / WBd = web.archive.org/web/20250530013221/ and /20251201/ + the RH URL
- WB22 = web.archive.org/web/20230901/https://learn.microsoft.com/en-us/windows/release-health/resolved-issues-windows-11-22h2
- FF = https://raw.githubusercontent.com/mozilla-firefox/firefox/main/toolkit/xre/dllservices/mozglue/WindowsDllBlocklistDefs.in
- CR = https://raw.githubusercontent.com/chromium/chromium/main/chrome/chrome_elf/third_party_dlls/hardcoded_blocklist.cc

| # | Victim | Culprit | Module / version | Symptom + codes | Fix / status | Gr | Src | Category |
|---|---|---|---|---|---|---|---|---|
| 1 | Win11 24H2 | Voicemeeter | vbvoicemeetervaio64_win10.sys < 3.3.1.9 | BSOD MEMORY_MANAGEMENT; safeguard 53794850 | App 1.1.1.8/2.1.1.8/3.1.1.8; hold lifted 2024-11-05 | A | WBc | other |
| 2 | Win11 24H2 | Intel SST (11th Gen) | IntcAudioBus.sys 10.29.0.5152 / 10.30.0.5152 | BSOD; 51876952 | Driver >= 10.29.00.5714 / 10.30.00.5714 | A | WBa | OS-update |
| 3 | Safe Exam Browser <= 3.7 | Win11 24H2 | - | App won't open; 49562592 | SEB >= 3.8 | A | WBc | OS-update |
| 4 | Audio (speakers, Bluetooth) | Dirac Audio | cridspapo.dll | No audio, devices not recognised; 54283088 | Hold, Dec 2024 | A | WBb | audio-injector |
| 5 | Win11 24H2 | SenseShield | sprotect.sys 1.0.2.372, 1.0.3.48903 | Hang, blue/black screen; 56318982 | Update host app; hold lifted 2025-10-15 | A | RH | DRM-DLP |
| 6 | AutoCAD 2022 | Win11 24H2 | - | Launch error; 56211213 | AutoCAD 2022.1.4 (S182.0.0) | A | WBc | OS-update |
| 7 | Outlook | Google Workspace Sync | - | "Cannot Start Microsoft Outlook... MAPI was unable to load the information service"; 54318776 | GWSMO 4.3.68.0 | A | WBc | other |
| 8 | Ubisoft games (AC Origins/Odyssey/Valhalla, Star Wars Outlaws, Avatar) | Win11 24H2 | - | Hang, black screen; 54437462, 54580160 | Game patches; second hold lifted 2024-12-05 | A | WBa/WBc | OS-update |
| 9 | Asphalt 8 | Win11 24H2 | - | Exception hang; 52796746 | Resolved 2025-03-19 | A | WBc | OS-update |
| 10 | Desktop/wallpaper | "Wallpaper customization apps" (unnamed) | - | Icons vanish, virtual desktop errors; 52754008 | Hold lifted 2025-10-15 | A | RH | shell-ext |
| 11 | Win11 24H2 upgrade | Chinese tax/gov apps' "integrated software" | unnamed | BSOD during upgrade, rollback; 54653508 | New software version | A | WBc | DRM-DLP |
| 12 | Win11 24H2 upgrade | ASUS X415KA/X515KA | BIOS < 311 | BSOD in setup; 54157480 | BIOS 311 | A | WBc | other |
| 13 | ARC Raiders, MARVEL Tokon, THE FINALS | RGB tools | inpoutx64 | EXCEPTION_ACCESS_VIOLATION, hang, reboot after KB5121003 (2026-08-11) | Driver block from 2026-08-26; workaround Services\inpoutx64\Start=4 | A | RH | RGB-monitor |
| 14 | Files, Outlook, browsers | Samsung Galaxy Connect / Continuity Service | - | "C:\ is not accessible - Access denied" on Galaxy Book 4 (Mar 2026) | App pulled from Store; MS recovery KB | A | RH | other |
| 15 | Dell PCs | Intel IPF Processor Participant driver x KB5095093 | - | Device Manager yellow bang, performance/power change | KB5121767 (2026-07-18) | A | RH | OS-update |
| 16 | OBS, NDI Tools | KB5063878 (Aug 2025) | - | Severe NDI stutter, worse with Display Capture | KB5065426 | A | WBd | OS-update |
| 17 | Blu-ray/DVD/DTV apps | KB5064081 | EVR + HDCP / audio DRM | Copy-protection errors, black screen | KB5067036 | A | RH | OS-update |
| 18 | Start menu / boot | ExplorerPatcher, StartAllBack | - | Start won't open (KB5028254); Windows won't start (KB5022913) | App updates | A | WB22 | shell-ext |
| 19 | Outlook/Office (32-bit apps) | Trellix Endpoint Security < 35.31.25 x KB5027231 | - | Apps won't open; event log access violation "module is unknown" | Trellix HX 35.31.25 | A | WB22 | AV-filter |
| 20 | DirectX enterprise apps | Intel graphics 26.20.100.7463-30.0.101.1190 x KB5019980 | apphelp.dll | apphelp.dll error | Driver > 30.0.101.1190; KB5025305 | A | WB22 | OS-update |
| 21 | Start, Search, UWP | ClickShare (Office-API integrators) | - | Won't open; damaged registry keys | KB5027303 | A | WB22 | other |
| 22 | Windows boot | CrowdStrike Falcon | C-00000291*.sys in drivers\CrowdStrike | BSOD 0x50 / 0x7E loop | Delete file in Safe Mode | A | https://support.microsoft.com/en-us/help/5042421 | AV-filter |
| 23 | Firefox | AhnLab (file comment: "AhnLab Internet Security") | nzbrcom.dll (all versions) | a11y crash, bug 1311969 | Still blocked in file, though bug closed WORKSFORME and AhnLab asked for removal | A | FF (re-checked locally 11/10) | banking-module |
| 24 | Firefox | Hancom Office shell extension | hncshellext64.dll <= 1.0.0.3 | Crash when the file picker opens, mostly ko locale; bug 1581092 | Blocked since Fx77 | A | FF (re-checked) | shell-ext |
| 25 | Firefox content process | Teruten (T-Cube) | T_Prevent64.dll, TDCommonLib64.dll | EXCEPTION_ACCESS_VIOLATION_EXEC; bug 1904087 | Blocked Fx130 / 128.1esr; bug still open | A | FF (re-checked) | DRM-DLP |
| 26 | Firefox | MWStory Print Guard | printguard64.dll <= 25.11.21.1 | EXCEPTION_ACCESS_VIOLATION_READ; bug 2011909 | Blocked Fx147-149 | A | FF (re-checked) | DRM-DLP |
| 27 | Firefox | Nahimic / Sonic Studio 3 | nahimicosd.dll <= 2.2.25.0 (GPU process), nahimic2osd.dll, nahimicmsiosd.dll, audiodevprops2.dll <= 2.6.14.0, ss3devprops.dll | Crashes; bugs 1804023, 1540883, 1233556, 1360029 | Blocked | A | FF | audio-injector |
| 28 | Firefox | ExplorerPatcher; Windhawk | explorerpatcher.amd64.dll; windhawk.dll <= 1.6.1.0 | Crashes; bugs 1798707, 2007362 | Blocked | A | FF | shell-ext |
| 29 | Firefox content/RDD | Trend Micro; McAfee/Musarubra DLP | tmmon64.dll, TmUmEvt64.dll; fcagff64.dll, mfehcinj.dll, epclient64.dll | Crashes; bugs 1872261, 1990299, 1634090, 1994743 | Blocked | A | FF | AV-filter |
| 30 | Firefox | Webroot | WRusr.dll <= 9.0.32.49; WRDll.x64.dll | Deadlocks (1752466), crashes (1700281) | Blocked | A | FF | AV-filter |
| 31 | Firefox | 360 Total Security; Kingsoft; Esafenet; Cylance | safemon64.dll, chromesafe64.dll <= 2.0.0.42; kisfdpro64.dll, dghmpg64.dll; dynamicdll64.dll; CylanceMemDef64.dll | Crashes; bugs 1536227, 1958112, 1837242, 2019298, 1756190 | Blocked | A | FF | AV-filter |
| 32 | Firefox | Meta Quest Link; ASUS GamerOSD; IDM; KeyScrambler | magicdsfilterQuest3.dll; atkdx11disp.dll; idmcchandler7_64.dll; KeyScramblerIE.dll <= 3.17.0.4 | Crashes; bugs 1986922, 812683, 1333486, 1994657 | Blocked | A | FF | overlay-hook |
| 33 | Chrome | TortoiseSVN; ASUS VirtualCamera; CyberLink YouCam; Lenovo OneKey Theater | libsvn_tsvn32.dll; virtualcamera.ax; ycwebcamerasource.ax; activedetect64.dll, windowsapihookdll64.dll | Browser crashes | Hard-coded block | A | CR | shell-ext |
| 34 | Chrome/Edge >= 78 | Symantec Endpoint Protection <= 14.2 RU2 | Application Control | "Aw, Snap!", STATUS_INVALID_IMAGE_HASH | SEP 14.2 RU2 MP1; RendererCodeIntegrityEnabled=0 | A | https://knowledge.broadcom.com/external/article/175842 | AV-filter |
| 35 | Godot 4.2 editor | Nahimic audio driver | not named | Process hangs, no window | Stop Nahimic service; closed by PR #99388 (4.4) | C | https://github.com/godotengine/godot/issues/85608 | audio-injector |
| 36 | VS Code | Nahimic service; Badlion Client | - | 20-50 s startup | Stop service / uninstall | C | https://github.com/microsoft/vscode/issues/56644 | audio-injector |
| 37 | Dear ImGui OpenGL multi-viewport apps | Nahimic | NahimicOSD.dll | 0xC0000005 on viewport resize, DLL in swap-buffers stack | Uninstall Nahimic | C | https://github.com/ocornut/imgui/issues/3321 | audio-injector |
| 38 | SignalRGB | Nahimic family | NahimicOSD.dll, SSAudioOSD.dll, SS2OSD.dll, Nahimic2OSD.dll, NahimicMSIOSD.dll, nhAsusPhoebusOSD.dll | UI locks on secondary monitor | Uninstall | A | https://docs.signalrgb.com/troubleshooting/software-conflicts/nahimic/ | audio-injector |
| 39 | LibreHardwareMonitor 0.9.4, FanControl, OCCT, Razer Synapse | Microsoft Defender | WinRing0 | HackTool:Win32/Winring0 (2025-03-11); VulnerableDriver:WinNT/Winring0.G (Aug 2025) | PawnIO swap, PR #1857 merged 2025-09-16 | C | https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/issues/1660 | RGB-monitor |
| 40 | PowerToys 0.74.1 | Malwarebytes | - | Explorer hangs for minutes, taskbar vanishes | Malwarebytes package 1.0.75991+ | D | https://github.com/microsoft/PowerToys/issues/28988 | AV-filter |
| 41 | Git for Windows (MSYS2 fork) | Exploit Protection Mandatory ASLR | MitigationOptions bit 0x100 | "Could not fork child process... DLL rebasing may be required" | Exempt usr\bin EXEs; #5557 (24H2) open, cause unconfirmed | C | https://github.com/git-for-windows/git/issues/1412 | other |
| 42 | DOOM Eternal | OBS 29.1.3 + Sonic Radar 3 | - | Crash before first screen | Closed "not planned"; cause unconfirmed | D | https://github.com/obsproject/obs-studio/issues/9792 | overlay-hook |
| 43 | EA games (Javelin) | Monitoring/RGB tools | winring0x64.sys, glckio2.sys, gpcidrv64.sys, atillk64.sys, ntiolib_x64.sys, winio64.sys, msio64.sys, eneio64.sys, cpuz141_x64.sys, hwinfo64a.sys, procexp152.sys, rzpnk.sys, magdrvamd64.sys, dbutil_2_3.sys, klhk.sys, zam64.sys, alsysio64.sys, asrdrv101.sys | Error #119 (denylist) | Disable or uninstall the tool | A | https://help.ea.com/en/articles/platforms/pc-ea-anticheat/ (page timed out on re-check 11/10) | anti-cheat |
| 44 | VALORANT | Storage / Wi-Fi drivers | iastorvd.sys, secnvme.sys, solidnvm.sys, asstahci64.sys, mtkwl6ex.sys < 3.5 | "VAN: INCOMPATIBLE OEM DRIVER" | Switch to Microsoft inbox driver or update (2026-02-04) | A | https://support.riotgames.com/de-de/riot/performance/van-incompatible-oem-driver | anti-cheat |
| 45 | PUBG, Valorant, Bloodhunt, Destiny 2, Genshin, PSO2 (GameGuard), DayZ | Kernel-mode Hardware-enforced Stack Protection | no files named | Game won't run or Windows crashes | No fix given (2023-04-19) | C | https://www.bleepingcomputer.com/news/microsoft/microsoft-defender-update-causes-windows-hardware-stack-protection-mess/ | anti-cheat |
| 46 | Corsair iCUE | VMware, VirtualBox, Citrix, Armoury Crate, Aura Sync, MSI Center, RGB Fusion, Synapse 3, G HUB, NZXT CAM, SteelSeries GG, SignalRGB, OpenRGB | - | Unspecified resource conflicts | Uninstall unused ones | A | https://www.corsair.com/us/ko/explorer/gamer/accessories/list-of-programs-known-to-conflict-with-corsair-icue/ | RGB-monitor |
| 47 | IPinside LWS Agent (하나은행) | Chrome sandbox; TAP-Windows Adapter V9 / TAP-Win32 Adapter OAS | I3GProc.exe, I3Gmgr | Endless "install" prompt; slow install check | --disable-features=NetworkServiceSandbox; delete TAP adapter | A | https://biz.kebhana.com/cont/customer/customer04/customer047/customer0471/index.jsp | banking-module |
| 48 | Games | NVIDIA app Game Filters | - | Up to 15% fps loss (Dec 2024) | Turn off Game Filters and Photo Mode | C | https://tomshardware.com/pc-components/gpus/we-tested-nvidia-app-performance-problems-games-run-up-to-15-percent-slower-with-the-app (snippet; only the headline confirmed on re-check) | overlay-hook |

## Filename → product (seen in opened sources)

Kernel drivers: vbvoicemeetervaio64_win10.sys → Voicemeeter · IntcAudioBus.sys → Intel SST · sprotect.sys → SenseShield · inpoutx64 → RGB tools · C-00000291*.sys → CrowdStrike · winring0x64.sys → EVGA Precision XOC / LED Sync (also LibreHardwareMonitor, FanControl) · glckio2.sys → Gigabyte RGB Fusion · gpcidrv64.sys → Gigabyte OC Guru II / Xtreme Gaming Engine · atillk64.sys → Gigabyte Aorus Engine · ntiolib_x64.sys → MSI Command Center / Super Charger / OC Kit · winio64.sys → MSI Creator Center · msio64.sys → Galaxy Gamer RGB · eneio64.sys → ENE · magdrvamd64.sys → Samsung Magician · monitor_win10_x64.sys → IObit Advanced SystemCare · hwos2ec10x64.sys → Huawei PCManager · cpuz141_x64.sys → CPU-Z · dbutil_2_3.sys → Dell DBUtil · hwinfo64a.sys → HWiNFO · procexp152.sys → Process Explorer · rzpnk.sys → Razer Synapse · iastorvd.sys → Intel VMD/RST · mtkwl6ex.sys → MediaTek RZ616 · vgk.sys, vgc.exe → Riot Vanguard

Injected DLLs: cridspapo.dll → Dirac Audio · nzbrcom.dll → AhnLab · hncshellext64.dll → Hancom Office · T_Prevent64.dll, TDCommonLib64.dll → Teruten · printguard64.dll → MWStory · NahimicOSD.dll, Nahimic2OSD.dll, NahimicMSIOSD.dll, audiodevprops2.dll → Nahimic · ss3devprops.dll, SS2OSD.dll → ASUS Sonic Studio · explorerpatcher.amd64.dll → ExplorerPatcher · windhawk.dll → Windhawk · tmmon64.dll, TmUmEvt64.dll → Trend Micro · fcagff64.dll, mfehcinj.dll → McAfee DLP · WRusr.dll → Webroot · safemon64.dll, chromesafe64.dll → 360 · kisfdpro64.dll → Kingsoft · CylanceMemDef64.dll → Cylance · dgapi64.dll → Digital Guardian · hmpalert.dll (product not named in the report), pghook.dll (Avecto), guard64.dll (COMODO), aswJsFlt.dll (Avast), k7pswsen.dll (K7) · idmcchandler7_64.dll → IDM · magicdsfilterQuest3.dll → Meta Quest Link · libsvn_tsvn32.dll → TortoiseSVN · ycwebcamerasource.ax → CyberLink YouCam

Processes: I3GProc.exe, I3Gmgr → IPinside LWS Agent
