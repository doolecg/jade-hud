"""Jade HUD's logo, drawn by the voxel-icons skill: python <skill>/scripts/draw.py docs/images/logo.py
A dirt block with a jade cube on top and a little tooltip card hovering beside it."""
from voxel import GREEN, PAPER, WATER, Box, by_depth, faces, voxel

DIRT = faces((138, 98, 62))

def scene():
    ground = [voxel(x, 0, z, DIRT) for x in range(2) for z in range(2)]
    jade = [voxel(0, 1, 0, GREEN)]
    card = [Box(2.1, 0.9, 0.0, 1.9, 1.3, 0.25, PAPER),
            Box(2.3, 1.75, 0.25, 1.1, 0.14, 0.05, WATER),
            Box(2.3, 1.4, 0.25, 1.5, 0.14, 0.05, GREEN)]
    return by_depth(ground + jade) + card

ICONS = [
    {"name": "logo", "drawing": scene(),
     "png": {"logo.png": 160, "logo-1024.png": 1024}},
]
