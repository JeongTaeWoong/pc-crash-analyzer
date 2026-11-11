# Fixed Blender workload for the lab. Run headless:
#   blender -b --factory-startup --python bench.py -- <result.json> <work-dir>
# Builds the same scene from a fixed seed every time (no external files), then times four stages:
#   build (scene construction), render (Cycles on CPU), physics (rigid-body steps), save (40 .blend writes).
# A failed stage is recorded with its error instead of aborting the run.
import json
import os
import random
import sys
import time

import bmesh
import bpy

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
result_path, work = argv[0], argv[1]
os.makedirs(work, exist_ok=True)
res = {"blender": bpy.app.version_string, "stages": {}}


def stage(name, fn):
    t = time.perf_counter()
    try:
        fn()
        res["stages"][name] = {"ok": True, "sec": round(time.perf_counter() - t, 4)}
    except Exception as e:  # recorded, not raised: "what failed" is data too
        res["stages"][name] = {"ok": False, "sec": round(time.perf_counter() - t, 4), "error": repr(e)[:300]}


def build():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    rnd = random.Random(42)
    cube = bpy.data.meshes.new("cube")
    bm = bmesh.new(); bmesh.ops.create_cube(bm, size=0.5); bm.to_mesh(cube); bm.free()
    ground_mesh = bpy.data.meshes.new("ground")
    bm = bmesh.new(); bmesh.ops.create_grid(bm, x_segments=1, y_segments=1, size=20); bm.to_mesh(ground_mesh); bm.free()
    scene.collection.objects.link(bpy.data.objects.new("ground", ground_mesh))
    for i in range(400):
        o = bpy.data.objects.new("c%03d" % i, cube)
        o.location = (rnd.uniform(-4, 4), rnd.uniform(-4, 4), rnd.uniform(1, 12))
        o.rotation_euler = (rnd.random() * 3, rnd.random() * 3, rnd.random() * 3)
        scene.collection.objects.link(o)
    sun = bpy.data.objects.new("sun", bpy.data.lights.new("sun", "SUN"))
    sun.data.energy = 3.0
    sun.rotation_euler = (0.6, 0.2, 0.4)
    scene.collection.objects.link(sun)
    cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam"))
    cam.location = (14, -14, 10)
    cam.rotation_euler = (1.1, 0, 0.785)
    scene.collection.objects.link(cam)
    scene.camera = cam
    scene.world = bpy.data.worlds.new("world")


def render():
    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.device = "CPU"          # security modules load the CPU, file and input paths, not the GPU
    scene.cycles.samples = 96
    scene.cycles.seed = 7
    scene.cycles.use_denoising = False
    scene.render.resolution_x, scene.render.resolution_y = 1280, 720
    scene.render.resolution_percentage = 100
    scene.render.filepath = os.path.join(work, "render.png")
    bpy.ops.render.render(write_still=True)


def physics():
    scene = bpy.context.scene
    bpy.ops.rigidbody.world_add()
    world = scene.rigidbody_world
    if world.collection is None:
        world.collection = bpy.data.collections.new("rigid")
    for o in list(scene.collection.objects):
        if o.type == "MESH":
            world.collection.objects.link(o)
    ground = bpy.data.objects["ground"]
    if ground.rigid_body is None:
        raise RuntimeError("linking to the rigid body collection did not create rigid_body settings")
    ground.rigid_body.type = "PASSIVE"
    scene.frame_start, scene.frame_end = 1, 120
    world.point_cache.frame_start, world.point_cache.frame_end = 1, 120
    for f in range(1, 121):
        scene.frame_set(f)
    res["physics_lowest_z"] = round(min(o.matrix_world.translation.z for o in scene.collection.objects if o.type == "MESH" and o.name != "ground"), 3)


def save():
    for i in range(40):  # 40 x ~1.2 MB: 5 writes took 0.05 s in the 11/10 spike, too short to measure
        bpy.ops.wm.save_as_mainfile(filepath=os.path.join(work, "save%d.blend" % i), copy=True)
    res["save_bytes"] = os.path.getsize(os.path.join(work, "save0.blend"))


for name, fn in (("build", build), ("render", render), ("physics", physics), ("save", save)):
    stage(name, fn)

with open(result_path, "w", encoding="utf-8") as f:
    json.dump(res, f)
