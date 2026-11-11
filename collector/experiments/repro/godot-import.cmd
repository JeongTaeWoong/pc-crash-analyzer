@echo off
if exist "C:\Users\wjdxo\AppData\Local\Temp\claude\1dc3a2c70db991fb\71c95dcb-57e6-4e3f-8957-2f4554e43c02\scratchpad\repro\godot-sample\proj\svg-crash-example\.godot" rmdir /s /q "C:\Users\wjdxo\AppData\Local\Temp\claude\1dc3a2c70db991fb\71c95dcb-57e6-4e3f-8957-2f4554e43c02\scratchpad\repro\godot-sample\proj\svg-crash-example\.godot"
"C:\Users\wjdxo\AppData\Local\Temp\claude\1dc3a2c70db991fb\71c95dcb-57e6-4e3f-8957-2f4554e43c02\scratchpad\repro\godot-4.3\app\Godot_v4.3-stable_win64.exe" --headless --disable-crash-handler --path "C:\Users\wjdxo\AppData\Local\Temp\claude\1dc3a2c70db991fb\71c95dcb-57e6-4e3f-8957-2f4554e43c02\scratchpad\repro\godot-sample\proj\svg-crash-example" --import
exit /b %errorlevel%
