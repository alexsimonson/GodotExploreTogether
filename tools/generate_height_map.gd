extends SceneTree
## Generates a height map (and a matching normal map) from a color texture's brightness.
## Dark areas (seams, grain, cracks) become low; bright areas become high.
##
## Usage (from the project folder):
##   <godot>_console.exe --headless --path . --script res://tools/generate_height_map.gd -- \
##       <in.png> <out_height.png> <out_normal.png> [blur_radius=1] [normal_strength=4] [invert=0]
##
## The blur wraps around the edges so tileable textures stay tileable.

func _init() -> void:
	var args := OS.get_cmdline_user_args()
	if args.size() < 3:
		printerr("usage: -- <in.png> <out_height.png> <out_normal.png> [blur_radius] [normal_strength] [invert]")
		quit(1)
		return
	var in_path := args[0]
	var height_path := args[1]
	var normal_path := args[2]
	var radius := int(args[3]) if args.size() > 3 else 1
	var strength := float(args[4]) if args.size() > 4 else 4.0
	var invert := args.size() > 5 and args[5] == "1"

	var img := Image.load_from_file(in_path)
	if img == null:
		printerr("could not load ", in_path)
		quit(1)
		return
	img.convert(Image.FORMAT_RGB8)
	var w := img.get_width()
	var h := img.get_height()

	# 1. Brightness (perceptual luminance).
	var lum := PackedFloat32Array()
	lum.resize(w * h)
	for y in h:
		for x in w:
			var c := img.get_pixel(x, y)
			lum[y * w + x] = c.r * 0.299 + c.g * 0.587 + c.b * 0.114

	# 2. Soften painterly noise with a wrapping box blur (horizontal, then vertical).
	if radius > 0:
		lum = _blur(lum, w, h, radius, true)
		lum = _blur(lum, w, h, radius, false)

	# 3. Stretch contrast between the 1st and 99th percentile, so the full 0..1 range is used.
	var sorted := lum.duplicate()
	sorted.sort()
	var lo := sorted[int(sorted.size() * 0.01)]
	var hi := sorted[int(sorted.size() * 0.99)]
	var span := maxf(hi - lo, 0.0001)

	var height := Image.create(w, h, false, Image.FORMAT_RGBA8)
	for y in h:
		for x in w:
			var v := clampf((lum[y * w + x] - lo) / span, 0.0, 1.0)
			if invert:
				v = 1.0 - v
			height.set_pixel(x, y, Color(v, v, v, 1.0))

	var height_gray := height.duplicate()
	height_gray.convert(Image.FORMAT_L8)
	height_gray.save_png(height_path)

	# 4. Normal map from the height map.
	var normal := height.duplicate()
	normal.bump_map_to_normal_map(strength)
	normal.convert(Image.FORMAT_RGB8)
	normal.save_png(normal_path)

	print("wrote ", height_path, " and ", normal_path, " (", w, "x", h, ", blur ", radius, ", strength ", strength, ")")
	quit()


func _blur(src: PackedFloat32Array, w: int, h: int, r: int, horizontal: bool) -> PackedFloat32Array:
	var dst := PackedFloat32Array()
	dst.resize(w * h)
	var n := float(2 * r + 1)
	for y in h:
		for x in w:
			var sum := 0.0
			for k in range(-r, r + 1):
				if horizontal:
					sum += src[y * w + posmod(x + k, w)]
				else:
					sum += src[posmod(y + k, h) * w + x]
			dst[y * w + x] = sum / n
	return dst
