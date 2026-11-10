#Requires AutoHotkey v2.0
#NoTrayIcon
; AutoHotkey changelog 2.0.21: "Fixed StrGet crashing when given an address and not a length. (Broken by v2.0.20)"
; Fix commit 0460a7e touches the MultiByteToWideChar path, so this passes an encoding that needs conversion.
; The wait lets the collector's 10-second module snapshot see this process before it dies.
Sleep 15000
buf := Buffer(64, 0)
StrPut("hello", buf, "UTF-8")
s := StrGet(buf.Ptr, "UTF-8")
ExitApp 0
