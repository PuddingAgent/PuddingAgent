from pathlib import Path
from PIL import Image

def convert_all(src="images", dst="converted"):
    src_dir = Path(src)
    dst_dir = Path(dst)
    dst_dir.mkdir(exist_ok=True)
    outputs = []
    for png in src_dir.glob("*.png"):
        out = dst_dir / png.with_suffix(".jpg").name
        img = Image.open(png)
        img.save(out, "JPEG")
        outputs.append(out)
    return outputs

if __name__ == "__main__":
    print("converted", len(convert_all()))
