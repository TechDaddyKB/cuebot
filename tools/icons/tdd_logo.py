"""Converts the Tech Daddy Digital logo (tools/icons/tdd-logo.svg, white shapes) into geometry resources in
src/Cuebot.App/Styles/Brand.axaml: Brand.TddHead (the head, for small sizes) and Brand.TddLogo (head and wordmark).
The SVG is an auto-traced vector: every path is absolute M/C/Z with a translate, which is baked into the coordinates.

Usage: python3 tools/icons/tdd_logo.py
"""
import re
import xml.etree.ElementTree as ET
from pathlib import Path

HERE = Path(__file__).resolve().parent
OUT = HERE.parents[1] / "src/Cuebot.App/Styles/Brand.axaml"
WORDMARK_TOP = 700   # paths starting below this are the "TECH DADDY DIGITAL" wordmark


def bake(d, tx, ty):
    out, pending = [], []
    for token in re.findall(r"[MCZ]|-?\d*\.?\d+(?:e-?\d+)?", d):
        if token in "MCZ":
            out.append(token)
            continue
        pending.append(float(token))
        if len(pending) == 2:
            out.append(f"{pending[0] + tx:.2f},{pending[1] + ty:.2f}")
            pending = []
    return " ".join(out)


def main():
    root = ET.parse(HERE / "tdd-logo.svg").getroot()
    head, all_paths, top = [], [], []
    for el in root:
        if not el.tag.endswith("path"):
            continue
        tx, ty = map(float, re.match(r"translate\(([-\d.]+),([-\d.]+)\)", el.get("transform", "translate(0,0)")).groups())
        data = bake(el.get("d"), tx, ty)
        all_paths.append(data)
        ys = [float(p.split(",")[1]) for p in re.findall(r"-?[\d.]+,-?[\d.]+", data)]
        (head if min(ys) < WORDMARK_TOP else top).append(data)
    OUT.write_text(f"""<ResourceDictionary xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <!--
    Tech Daddy Digital logo, generated from tools/icons/tdd-logo.svg by tools/icons/tdd_logo.py: do not edit by hand.
    Filled shapes (nonzero, as in the SVG); paint them white on dark or the text colour on light.
  -->
  <StreamGeometry x:Key="Brand.TddHead">F1 {" ".join(head)}</StreamGeometry>
  <StreamGeometry x:Key="Brand.TddLogo">F1 {" ".join(all_paths)}</StreamGeometry>
</ResourceDictionary>
""", encoding="utf-8")
    print(f"head paths {len(head)}, wordmark paths {len(top)} -> {OUT}")


if __name__ == "__main__":
    main()
