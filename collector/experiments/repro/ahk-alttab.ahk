#Requires AutoHotkey v2.0
#NoTrayIcon
; AutoHotkey changelog 2.0.30: "Fixed a & b::AltTabMenu and similar crashing the program when activated [broken by v2.0.20]."
a & b::AltTabMenu
Sleep 15000
SendLevel 1
SendEvent "{a down}"
Sleep 200
SendEvent "{b down}{b up}"
Sleep 200
SendEvent "{a up}"
Sleep 6000
Send "{Esc}"
Sleep 500
ExitApp 0