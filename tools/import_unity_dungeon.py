"""
Converts the original Unity dungeon prefabs (ProBuilder meshes + nested torches) into Godot assets.

Reads   <unity>/Assets/Resources/Prefabs/Dungeon/{Hall,Corner,T-Junction,X-Junction,C-Junction}.prefab
Writes  assets/dungeon_pieces/<Piece>_<material>.obj   exact ProBuilder geometry and UVs, one file per material
        scenes/dungeon/<Piece>.tscn                    meshes + materials + collision + torch instances

Usage (from the project folder; no third-party packages needed):
    python -I tools/import_unity_dungeon.py E:/claude/_reference/ExploreTogether

Coordinate notes (see docs/PLAN.md):
- Positions are copied 1:1 (Unity x,y,z -> Godot x,y,z), like the maze grid, so maze rotations stay valid.
- Unity triangles are clockwise-from-outside in its left-handed space. The same numbers in a
  right-handed space read counter-clockwise from outside, which is exactly the OBJ convention.
  So the triangle order is kept as-is; the script verifies every face of every (convex) part points outward.
- Child transforms are baked into the vertices, relative to the prefab root (the root's own transform
  is replaced by the maze placement at runtime, as in the original InstantiateModified).
"""
import math
import os
import re
import struct
import sys

PIECES = {  # Unity prefab -> Godot scene/mesh name (matches the PieceKind enum)
    "Hall": "Hall",
    "Corner": "Corner",
    "T-Junction": "TJunction",
    "X-Junction": "XJunction",
    "C-Junction": "CJunction",
}
MATERIALS = {  # Unity material guid -> Godot material
    "3a631b7a1ac72984b91711e2bb69218f": "brick",     # completethewall.mat
    "21cd6d06debad9d4aaa79af3d03c4a05": "hardwood",  # hardwood.mat
    "68da5ae1b4a179744b14325818dc0ae9": "chess",     # chess.mat
    "bf093496a1392a947a0631020d4414e5": "rock",      # grass_rock/Materials/Rock.mat
}
# Look changes made in Godot (not in the Unity prefabs), per piece: {piece: {from material: to material}}.
# 2026-11-15: straight halls keep brick walls; every other piece's walls use rock. Floors/ceilings unchanged.
MATERIAL_OVERRIDES = {
    "Corner": {"brick": "rock"},
    "TJunction": {"brick": "rock"},
    "XJunction": {"brick": "rock"},
    "CJunction": {"brick": "rock"},
}
PROBUILDER_MESH_SCRIPT ="8233d90336aea43098adf6dbabd606a2"
TORCH_PREFAB = "d9daea13b96f9354ebcd4e7715a7ea51"

DOC_RE = re.compile(r"^--- !u!(\d+) &(-?\d+)( stripped)?\s*$")
VEC_RE = re.compile(r"\{x: ([-\deE.+]+), y: ([-\deE.+]+)(?:, z: ([-\deE.+]+))?(?:, w: ([-\deE.+]+))?\}")


# ----------------------------------------------------------------------------- Unity YAML (minimal)

def split_documents(text):
    docs, cur = [], None
    for line in text.splitlines():
        m = DOC_RE.match(line)
        if m:
            cur = {"class": int(m.group(1)), "id": m.group(2), "stripped": bool(m.group(3)), "lines": []}
            docs.append(cur)
        elif cur is not None:
            cur["lines"].append(line)
    return docs


def field(doc, name):
    for line in doc["lines"]:
        s = line.strip()
        if s.startswith(name + ":"):
            return s[len(name) + 1:].strip()
    return None


def vec(s):
    m = VEC_RE.search(s)
    return tuple(float(g) for g in m.groups() if g is not None)


def file_id(s):
    return re.search(r"fileID: (-?\d+)", s).group(1)


def list_block(doc, name):
    """Lines of a YAML list under `name:` (items start with '- ')."""
    out, inside, indent = [], False, None
    for line in doc["lines"]:
        if not inside:
            if line.strip() == name + ":":
                inside, indent = True, len(line) - len(line.lstrip())
            continue
        cur = len(line) - len(line.lstrip())
        if line.strip() and cur <= indent and not line.lstrip().startswith("- "):
            break
        if line.strip() and cur < indent:
            break
        out.append(line)
    return out


# ----------------------------------------------------------------------------- math

def quat_to_matrix(x, y, z, w):
    return [
        [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
        [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
        [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)],
    ]


def trs(pos, rot, scale):
    r = quat_to_matrix(*rot)
    m = [[r[i][j] * scale[j] for j in range(3)] + [pos[i]] for i in range(3)]
    return m + [[0, 0, 0, 1]]


def mul(a, b):
    return [[sum(a[i][k] * b[k][j] for k in range(4)) for j in range(4)] for i in range(4)]


def apply(m, p):
    return tuple(m[i][0] * p[0] + m[i][1] * p[1] + m[i][2] * p[2] + m[i][3] for i in range(3))


IDENTITY = [[1, 0, 0, 0], [0, 1, 0, 0], [0, 0, 1, 0], [0, 0, 0, 1]]


def sub(a, b): return (a[0] - b[0], a[1] - b[1], a[2] - b[2])
def cross(a, b): return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])
def dot(a, b): return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def norm(a):
    n = math.sqrt(dot(a, a))
    return (a[0] / n, a[1] / n, a[2] / n) if n > 1e-12 else (0.0, 0.0, 0.0)


def fmt(v):
    v = 0.0 if abs(v) < 1e-5 else v
    return f"{v:.5f}".rstrip("0").rstrip(".") if "." in f"{v:.5f}" else f"{v:.5f}"


# ----------------------------------------------------------------------------- conversion

def convert_prefab(path):
    docs = split_documents(open(path, encoding="utf-8").read())
    by_id = {d["id"]: d for d in docs}

    gameobjects = {d["id"]: field(d, "m_Name") for d in docs if d["class"] == 1}
    transforms = {}
    for d in docs:
        if d["class"] == 4 and not d["stripped"]:
            transforms[d["id"]] = {
                "go": file_id(field(d, "m_GameObject")),
                "pos": vec(field(d, "m_LocalPosition")),
                "rot": vec(field(d, "m_LocalRotation")),
                "scale": vec(field(d, "m_LocalScale")),
                "father": file_id(field(d, "m_Father")),
            }
    root = next(t for t, v in transforms.items() if v["father"] == "0")

    def world(tid):
        """Matrix of a transform relative to the prefab root (root's own transform excluded)."""
        if tid == root or tid == "0":
            return IDENTITY
        t = transforms[tid]
        return mul(world(t["father"]), trs(t["pos"], t["rot"], t["scale"]))

    transform_of_go = {v["go"]: k for k, v in transforms.items()}
    renderers = {}
    for d in docs:
        if d["class"] == 23:
            mats = [re.search(r"guid: ([0-9a-f]{32})", l).group(1) for l in list_block(d, "m_Materials") if "guid:" in l]
            renderers[file_id(field(d, "m_GameObject"))] = mats

    parts = []  # (name, material, [triangles of ((pos, uv) x3)], [points])
    for d in docs:
        if d["class"] != 114 or PROBUILDER_MESH_SCRIPT not in (field(d, "m_Script") or ""):
            continue
        go = file_id(field(d, "m_GameObject"))
        name = gameobjects[go]
        m = world(transform_of_go[go])
        positions = [apply(m, vec(l)) for l in list_block(d, "m_Positions") if "{x:" in l]
        uvs = [vec(l)[:2] for l in list_block(d, "m_Textures0") if "{x:" in l]
        faces, submesh = [], 0
        for line in list_block(d, "m_Faces"):
            s = line.strip()
            if s.startswith("- m_Indexes:"):
                raw = bytes.fromhex(s.split(":", 1)[1].strip())
                faces.append([list(struct.unpack(f"<{len(raw) // 4}i", raw)), 0])
            elif s.startswith("m_SubmeshIndex:"):
                faces[-1][1] = int(s.split(":")[1])
        mats = renderers.get(go, [])
        by_material = {}
        for indexes, sub_i in faces:
            guid = mats[min(sub_i, len(mats) - 1)]
            mat = MATERIALS.get(guid)
            if mat is None:
                raise SystemExit(f"{path}: {name} uses unknown material guid {guid}")
            tris = [tuple((positions[i], uvs[i]) for i in indexes[k:k + 3]) for k in range(0, len(indexes), 3)]
            by_material.setdefault(mat, []).extend(tris)
        parts.append((name, by_material, positions))

    torches = []
    for d in docs:
        if d["class"] != 1001 or TORCH_PREFAB not in (field(d, "m_SourcePrefab") or ""):
            continue
        parent = file_id(field(d, "m_TransformParent"))
        mods = {}
        for line in d["lines"]:
            s = line.strip()
            if s.startswith("propertyPath:"):
                key = s.split(":", 1)[1].strip()
            elif s.startswith("value:"):
                mods[key] = s.split(":", 1)[1].strip()
        g = lambda k, default: float(mods.get(k, default) or default)
        pos = (g("m_LocalPosition.x", 0), g("m_LocalPosition.y", 0), g("m_LocalPosition.z", 0))
        rot = (g("m_LocalRotation.x", 0), g("m_LocalRotation.y", 0), g("m_LocalRotation.z", 0), g("m_LocalRotation.w", 1))
        scale = (g("m_LocalScale.x", 1), g("m_LocalScale.y", 1), g("m_LocalScale.z", 1))
        torches.append(mul(world(parent), trs(pos, rot, scale)))

    return parts, torches


def part_triangles(by_material):
    return [(a, b, c) for tris in by_material.values() for (a, _), (b, _), (c, _) in tris]


def check_outward(piece, parts):
    """
    Each part is a closed solid. With right-handed CCW-outward triangles its signed volume
    (sum of a . (b x c) / 6) is positive; inward-facing winding would make it negative.
    Works for non-convex parts too (e.g. an L-shaped wall).
    """
    total = 0
    for name, by_material, _ in parts:
        tris = part_triangles(by_material)
        volume = sum(dot(a, cross(b, c)) for a, b, c in tris) / 6
        total += len(tris)
        if volume <= 0:
            raise SystemExit(f"{piece}/{name}: signed volume {volume:.3f} <= 0; winding assumption is wrong")
    return total


def is_convex(tris, points):
    for a, b, c in tris:
        n = cross(sub(b, a), sub(c, a))
        if dot(n, n) < 1e-12:
            continue
        if any(dot(n, sub(p, a)) > 1e-4 for p in points):
            return False
    return True


def write_obj(path, tris):
    lines = ["# Converted from the Unity ProBuilder dungeon prefab by tools/import_unity_dungeon.py"]
    vi = 0
    for (a, ua), (b, ub), (c, uc) in tris:
        n = norm(cross(sub(b, a), sub(c, a)))
        for p in (a, b, c):
            lines.append(f"v {fmt(p[0])} {fmt(p[1])} {fmt(p[2])}")
        for uv in (ua, ub, uc):
            lines.append(f"vt {fmt(uv[0])} {fmt(uv[1])}")
        lines.append(f"vn {fmt(n[0])} {fmt(n[1])} {fmt(n[2])}")
        k = vi // 3 + 1
        lines.append(f"f {vi + 1}/{vi + 1}/{k} {vi + 2}/{vi + 2}/{k} {vi + 3}/{vi + 3}/{k}")
        vi += 3
    open(path, "w", newline="\n").write("\n".join(lines) + "\n")


def transform3d(m):
    # Godot's text format for Transform3D is row-major: Transform3D(row0, row1, row2, origin).
    # (Verified in-engine: "Transform3D(0, 0, 1, 0, 1, 0, -1, 0, 0, ...)" maps local +Z to +X.)
    vals = [m[r][c] for r in range(3) for c in range(3)] + [m[0][3], m[1][3], m[2][3]]
    return "Transform3D(" + ", ".join(fmt(v) for v in vals) + ")"


def write_scene(path, piece, parts, torches):
    materials = sorted({mat for _, by_mat, _ in parts for mat in by_mat})
    ext, sub_res, nodes = [], [], []
    for i, mat in enumerate(materials):
        ext.append(f'[ext_resource type="Mesh" path="res://assets/dungeon_pieces/{piece}_{mat}.obj" id="mesh_{mat}"]')
        ext.append(f'[ext_resource type="Material" path="res://assets/materials/{mat}.tres" id="mat_{mat}"]')
    if torches:
        ext.append('[ext_resource type="PackedScene" path="res://scenes/props/Torch.tscn" id="torch"]')

    nodes.append(f'[node name="{piece}" type="Node3D"]')
    for mat in materials:
        nodes.append(f'\n[node name="{mat.capitalize()}" type="MeshInstance3D" parent="."]\n'
                     f'mesh = ExtResource("mesh_{mat}")\nmaterial_override = ExtResource("mat_{mat}")')
    nodes.append('\n[node name="Body" type="StaticBody3D" parent="."]')
    for i, (name, by_material, positions) in enumerate(parts):
        tris = part_triangles(by_material)
        if is_convex(tris, positions):
            unique = sorted({tuple(round(c, 4) for c in p) for p in positions})
            pts = ", ".join(f"{fmt(x)}, {fmt(y)}, {fmt(z)}" for x, y, z in unique)
            sub_res.append(f'[sub_resource type="ConvexPolygonShape3D" id="shape_{i}"]\npoints = PackedVector3Array({pts})')
        else:
            # Non-convex part (e.g. an L-shaped wall): exact triangle mesh, solid from both sides.
            data = ", ".join(f"{fmt(p[0])}, {fmt(p[1])}, {fmt(p[2])}" for t in tris for p in t)
            sub_res.append(f'[sub_resource type="ConcavePolygonShape3D" id="shape_{i}"]\n'
                           f'data = PackedVector3Array({data})\nbackface_collision = true')
        safe = re.sub(r"[^A-Za-z0-9_]", "_", name)
        nodes.append(f'\n[node name="{safe}_{i}" type="CollisionShape3D" parent="Body"]\nshape = SubResource("shape_{i}")')
    for i, m in enumerate(torches):
        nodes.append(f'\n[node name="Torch{i + 1}" parent="." instance=ExtResource("torch")]\ntransform = {transform3d(m)}')

    text = "[gd_scene format=3]\n\n" + "\n".join(ext) + "\n\n" + "\n\n".join(sub_res) + "\n\n" + "\n".join(nodes) + "\n"
    open(path, "w", newline="\n").write(text)


def main():
    if len(sys.argv) != 2:
        raise SystemExit(__doc__)
    unity = sys.argv[1]
    src = os.path.join(unity, "Assets", "Resources", "Prefabs", "Dungeon")
    os.makedirs("assets/dungeon_pieces", exist_ok=True)
    os.makedirs("scenes/dungeon", exist_ok=True)
    for unity_name, piece in PIECES.items():
        parts, torches = convert_prefab(os.path.join(src, unity_name + ".prefab"))
        swap = MATERIAL_OVERRIDES.get(piece, {})
        for _, by_material, _ in parts:
            for old, new in swap.items():
                if old in by_material:
                    by_material.setdefault(new, []).extend(by_material.pop(old))
        total = check_outward(piece, parts)
        for mat in {m for _, by_mat, _ in parts for m in by_mat}:
            write_obj(f"assets/dungeon_pieces/{piece}_{mat}.obj",
                      [t for _, by_mat, _ in parts for t in by_mat.get(mat, [])])
        write_scene(f"scenes/dungeon/{piece}.tscn", piece, parts, torches)
        allp = [p for _, _, ps in parts for p in ps]
        lo = [min(p[i] for p in allp) for i in range(3)]
        hi = [max(p[i] for p in allp) for i in range(3)]
        shapes = ["convex" if is_convex(part_triangles(bm), ps) else "concave" for _, bm, ps in parts]
        print(f"{piece:10s} parts={[f'{n}:{k}' for (n, _, _), k in zip(parts, shapes)]} tris={total} (outward) torches={len(torches)} "
              f"bounds=({fmt(lo[0])},{fmt(lo[1])},{fmt(lo[2])})..({fmt(hi[0])},{fmt(hi[1])},{fmt(hi[2])})")


if __name__ == "__main__":
    main()
