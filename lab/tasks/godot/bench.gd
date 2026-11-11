# Fixed Godot script workload for the lab. Run headless:
#   Godot_console.exe --headless --path <project> --script res://bench.gd -- <io-dir>
# Times a compute loop and a write/read/delete pass over 3000 small files, prints one LABRESULT line, then quits.
# (500 files took 0.19 s in the 11/10 spike and varied by 5%; 3000 gives a longer, steadier interval.)
extends SceneTree

const FILES := 3000

func _init() -> void:
	var args := OS.get_cmdline_user_args()
	var io_dir := args[0] if args.size() > 0 else OS.get_user_data_dir()
	DirAccess.make_dir_recursive_absolute(io_dir)

	var t0 := Time.get_ticks_usec()
	var acc := 0
	for i in range(30000000):
		acc += (i * 7) % 13
	var t1 := Time.get_ticks_usec()

	var payload := PackedByteArray()
	payload.resize(4096)
	for i in range(FILES):
		var f := FileAccess.open(io_dir.path_join("f%d.bin" % i), FileAccess.WRITE)
		f.store_buffer(payload)
		f.close()
	var read_bytes := 0
	for i in range(FILES):
		read_bytes += FileAccess.get_file_as_bytes(io_dir.path_join("f%d.bin" % i)).size()
	for i in range(FILES):
		DirAccess.remove_absolute(io_dir.path_join("f%d.bin" % i))
	var t2 := Time.get_ticks_usec()

	print("LABRESULT ", JSON.stringify({"compute_ms": (t1 - t0) / 1000.0, "io_ms": (t2 - t1) / 1000.0, "acc": acc, "read_bytes": read_bytes}))
	quit()
